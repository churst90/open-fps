using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Common.Networking;
using OpenFPS.Common.Components;
using OpenFPS.Server.Repositories;
using Arch.Core;
using Serilog;

namespace OpenFPS.Server.Core;

/// <summary>
/// Executes the text commands (/scan, /move, /spawn, ...). It answers through a reply callback, never a
/// <see cref="LiteNetLib.NetPeer"/>, so a MUD session and a UDP session are the same to it. Every
/// command body runs on the tick thread via the server's command buffer: the MUD gateway dispatches
/// from its own TCP thread, and touching the Arch world from there races the simulation.
/// </summary>
public partial class CommandHandler
{
    private readonly SessionManager _sessions;
    private readonly MapManager _maps;
    private readonly GameServer _server;
    private readonly CompositeService? _composites;
    private readonly OccupancyService? _seats;
    private readonly HandsService? _hands;
    private readonly IUserRepository? _users;
    private readonly FriendRepository? _friends;
    private readonly CombatService _combat;

    /// <summary>Firing, reloading, wounds and death. The server ticks the same one.</summary>
    public CombatService Combat => _combat;

    public CommandHandler(SessionManager sessions, MapManager maps, GameServer server,
                          CompositeService? composites = null, OccupancyService? seats = null,
                          HandsService? hands = null, IUserRepository? users = null,
                          FriendRepository? friends = null, CombatService? combat = null)
    {
        _combat = combat ?? new CombatService(maps, server, sessions);
        _users = users;
        _friends = friends;
        _sessions = sessions;
        _maps = maps;
        _server = server;
        _composites = composites;
        _seats = seats;
        _hands = hands;
    }

    public void HandleTextCommand(int connectionId, TextCommand cmd, Action<IMessage> reply)
    {
        if (!_sessions.TryGetSession(connectionId, out var session))
        {
            Say(reply, "You are not logged in.");
            return;
        }

        string commandName = cmd.Command.TrimStart('/').ToLowerInvariant();
        string[] args = cmd.Args ?? Array.Empty<string>();

        // Typing anything is the player being here; /afk is the one thing that says otherwise.
        session.LastActivityUtc = DateTime.UtcNow;
        if (commandName != "afk") session.Away = false;

        _server.EnqueueCommand(() => {
            try { Execute(commandName, args, session, reply); }
            catch (Exception ex)
            {
                Log.Error(ex, "Error executing command '{Command}' for {User}", commandName, session.Username);
                Say(reply, $"Command '{commandName}' failed. The error has been logged.");
            }
            // After the command, so /afk is announced at once and anything else says they are back.
            _server.UpdatePresence(session, DateTime.UtcNow);
        });
    }

    private void Execute(string commandName, string[] args, UserSession session, Action<IMessage> reply)
    {
        // The one permission check for every gated command, so a new case cannot forget its own.
        // docs/SERVER_SECURITY.md has the table; keep it in step with Permissions.
        if (!MayHere(session, Permissions.Canonical(commandName)))
        {
            // Control+B asks for the build dialog: a player who may not edit hears nothing at all.
            if (commandName == Permissions.Edit && OpenFPS.Server.Editor.WorldEditor.AsksForBuildForm(args)) return;
            // F12 is answered in words of its own: who the editor is for.
            if (commandName == Permissions.Edit && OpenFPS.Server.Editor.WorldEditor.AsksForMenu(args))
                Say(reply, OpenFPS.Server.Editor.WorldEditor.Refusal);
            else DenyCommand(reply);
            return;
        }
        switch (commandName)
        {
            case "scan": HandleScan(session, reply); break;
            case "pm": HandlePrivateMessage(session, args, reply); break;
            case "all":
                if (args.Length == 0) { Say(reply, "Usage: /all [message] — says it to everyone on the server."); break; }
                _server.Chat(session, string.Join(" ", args), ChatChannel.All);
                break;
            case "motd":
                Say(reply, GameServer.ReadMotd() is { Length: > 0 } motd ? motd : "There is no message of the day.");
                break;
            case "setmotd":
                GameServer.WriteMotd(string.Join(" ", args));
                Say(reply, args.Length == 0 ? "Message of the day cleared." : "Message of the day set.");
                break;
            case "announce":
                if (args.Length == 0) { Say(reply, "Usage: /announce [message]"); break; }
                _server.Announce(string.Join(" ", args), fromStaff: true);
                break;
            // /move by coordinates is a building tool; /tp is the teleporter's, an item you carry.
            case "move":
                HandleMove(session, args, reply);
                break;
            case "tp":
            case "goto":
            case "teleport":
                HandleTeleport(session, args, reply);
                break;
            // ── Maps of your own ────────────────────────────────────────────────────────────
            case "map":
                HandleMap(session, args, reply);
                break;
            case "maps":
                HandleMaps(session, args, reply);
                break;
            case "spawn":
                HandleSpawn(session, args, reply);
                break;
            case "set_sound":
                HandleSetSound(session, args, reply);
                break;
            case "set_audio_mode":
                HandleSetAudioMode(session, args, reply);
                break;
            case "play_folder":
                HandlePlayFolder(session, args, reply);
                break;
            case "start_state":
                HandleStartState(session, args, reply);
                break;
            // ── Building ────────────────────────────────────────────────────────────────────
            case "group":
                HandleGroup(session, args, reply);
                break;
            case "ungroup":
                HandleUngroup(session, reply);
                break;
            case "saveas":
                HandleSaveAs(session, args, reply);
                break;
            case "place":
                HandlePlace(session, args, reply);
                break;
            case "composites":
                HandleListComposites(reply);
                break;
            // ── Placing things where you cannot point ───────────────────────────────────────
            case "origin":
                HandleOrigin(session, reply);
                break;
            case "at":
                HandleAt(session, args, reply);
                break;
            case "put":
                HandlePut(session, args, reply);
                break;
            case "undo":
                HandleUndo(session, reply);
                break;
            case "room":
                HandleRoom(session, args, reply);
                break;
            // The world editor, F12 and /edit (docs/WORLD_EDITOR.md).
            case "edit":
                Editor.Handle(session, args, reply);
                break;
            // The whole server's weather, for testing.
            case "weather":
                HandleWeather(session, args, reply);
                break;
            case "prefabs":
                HandleListPrefabs(reply);
                break;
            case "clap":
                HandleClap(session, reply);
                break;
            case "knock":
                HandleKnock(session, args, reply);
                break;
            // A gun in your hands is the permission to fire it; naming a weapon out of the air needs fire-any.
            case "fire":
            case "shoot":
                _combat.Fire(session, args, reply, session.Can(Permissions.FireAny));
                break;
            case "aimassist":
                // "quiet": the client restating its saved choice on entering the world, not a question.
                string assist = AimAssistCommand(session, args);
                if (!args.Any(a => a.Equals("quiet", StringComparison.OrdinalIgnoreCase))) Say(reply, assist);
                break;
            // The client's world detail setting, restated ("quiet") when it joins a map.
            case "detail":
                HandleDetail(session, args, reply);
                break;
            case "reload":
                _combat.Reload(session, reply);
                break;
            // The fire selector, X and Shift+X; on the admin gun, its mode.
            case "selector":
                _combat.Selector(session, args, reply);
                break;
            // The trigger let go (Enter up): automatic fire stops.
            case "cease":
                _combat.Cease(session);
                break;
            // The admin gun's calibre, Y and Shift+Y.
            case "calibre":
            case "caliber":
                _combat.Calibre(session, args, reply);
                break;
            case "admingun":
                _combat.AdminGunCommand(session, args, reply);
                break;
            case "ammo":
                Say(reply, _combat.AmmoReadout(session));
                break;
            // The game client answers these itself; one that arrives here came from a text client.
            case "scope":
            case "zoom":
            case "range":
            case "zero":
                Say(reply, "The scope works in the game client: hold a scoped rifle and press numpad star, or type /scope.");
                break;
            // ── Doors ───────────────────────────────────────────────────────────────────────
            case "open":
                HandleDoor(session, args, reply, open: true);
                break;
            case "close":
            case "shut":
                HandleDoor(session, args, reply, open: false);
                break;
            case "doors":
                HandleListDoors(session, reply);
                break;
            case "addseat":
                HandleAddSeat(session, args, reply);
                break;
            case "removeseat":
                HandleRemoveSeat(session, args, reply);
                break;
            case "drivable":
                HandleDrivable(session, args, reply);
                break;
            // ── Occupancy ───────────────────────────────────────────────────────────────────
            case "ignition":
            case "key":
                HandleIgnition(session, args, reply);
                break;
            case "siren":
                HandleSiren(session, args, reply);
                break;
            case "horn":
                HandleHorn(session, reply);
                break;
            case "window":
            case "windows":
                HandleWindow(session, args, reply);
                break;
            case "enter":
            case "board":
            case "getin":
                HandleEnter(session, args, reply);
                break;
            case "exit":
            case "getout":
                HandleExit(session, reply);
                break;
            case "seats":
                HandleSeats(session, reply);
                break;
            // ── Carrying things ─────────────────────────────────────────────────────────────
            case "take":
            case "get":
            case "grab":
            case "pickup":
                HandleTake(session, args, reply);
                break;
            case "drop":
            case "putdown":
                HandleDrop(session, args, reply);
                break;
            case "stow":
            case "sling":
                HandleStow(session, args, reply);
                break;
            case "draw":
            case "equip":
            case "wield":
            case "unsling":
                HandleDraw(session, args, reply);
                break;
            case "hands":
                HandleHands(session, reply);
                break;
            case "inv":
            case "i":
            case "inventory":
                HandleInventory(session, reply);
                break;
            case "hand":
            case "offer":
                HandleHand(session, args, reply);
                break;
            // ── People and places ───────────────────────────────────────────────────────────
            case "friend":
            case "unfriend":
                HandleFriend(session, commandName, args, reply);
                break;
            case "friends":
                if (_friends == null) { Say(reply, "Friends are not available on this server."); break; }
                reply(OpenFPS.Server.Services.SocialService.BuildFriendList(session.Username, _friends, _sessions));
                break;
            case "team":
                HandleTeam(session, args, reply);
                break;
            case "t":
                TeamChat(session, args, reply);
                break;
            case "profile":
            case "whois":
                HandleProfile(session, args, reply);
                break;
            case "where":
            case "locate":
                // Staff's: a profile says which map, and that is all.
                HandleWhere(session, args, reply);
                break;
            case "afk":
            case "away":
                session.Away = !session.Away;
                Say(reply, session.Away
                    ? "You are marked away. Anything you do clears it."
                    : "You are back.");
                break;
            case "realname":
                HandleRealName(session, args, reply);
                break;
            // ── Administration ──────────────────────────────────────────────────────────────
            case "sessions":
                HandleSessions(reply);
                break;
            case "user":
            case "account":
                HandleUser(args, reply);
                break;
            case "throttled":
            case "ratelimit":
                HandleThrottled(reply);
                break;
            case "unlock":
                HandleUnlock(args, reply);
                break;
            case "setrole":
                HandleSetRole(session, args, reply);
                break;
            case "grant": HandleGrant(session, args, reply, give: true); break;
            case "revoke": HandleGrant(session, args, reply, give: false); break;
            case "perms":
            case "permissions": HandlePerms(session, args, reply); break;
            case "role": HandleRole(session, args, reply); break;
            case "bring":
                if (args.Length < 1) { Say(reply, "Usage: /bring NAME — brings a player to you."); break; }
                if (OnlineSession(args[0]) is not { } brought) { Say(reply, $"{args[0]} is not online."); break; }
                if (brought == session) { Say(reply, "You cannot bring yourself."); break; }
                PlaceBeside(brought, session, session, reply);
                break;
            case "give":
                if (TryGiveVehicle(session, args, reply)) break;
                if (TryGiveAmmo(session, args, reply)) break;
                if (args.Length < 1 || _hands == null)
                {
                    Say(reply, "Usage: /give [NAME] ITEM [COUNT]. Items: " + string.Join(", ", _hands?.GivableItems() ?? Array.Empty<string>()) + ".");
                    break;
                }
                {
                    // /give ITEM, /give ITEM 3, /give NAME ITEM, /give NAME ITEM 3.
                    var rest = args.ToList();
                    int count = 1;
                    if (rest.Count > 1 && int.TryParse(rest[^1], out int n)) { count = n; rest.RemoveAt(rest.Count - 1); }
                    var receiver = session;
                    if (rest.Count >= 2)
                    {
                        if (OnlineSession(rest[0]) is not { } named) { Say(reply, $"{rest[0]} is not online."); break; }
                        receiver = named; rest.RemoveAt(0);
                    }
                    if (count < 1) { Say(reply, "Give at least one."); break; }
                    // A premium item (the teleporter) is the administrator's to give.
                    string prefab = _hands.ResolveItem(rest[0]) ?? rest[0].ToLowerInvariant();
                    if (_maps.Prefabs.TryGetValue(prefab, out var premium) && premium.Premium && !session.Can(Permissions.GivePremium))
                    { Say(reply, $"{premium.Name} is a premium item; giving one needs {Permissions.GivePremium}."); break; }
                    // Give makes at most MaxGive; say the number made, not the number asked for.
                    count = Math.Min(count, HandsService.MaxGive);
                    if (!_hands.Give(receiver, rest[0].ToLowerInvariant(), count, out string item, out string placed, out string why)) { Say(reply, why); break; }
                    string what = $"{count} {(count == 1 ? item : Plural(item))}";
                    // The receiver hears it handed over: the item's own sound, which the client makes.
                    SendGiveEvent(receiver, prefab);
                    if (receiver != session)
                    {
                        _server.SendToSession(receiver, new TextEvent { Text = $"{session.Username} gave you {what}. {Capital(placed)}." });
                        Say(reply, $"You gave {receiver.Username} {what}.");
                    }
                    else Say(reply, $"You now have {what}. {Capital(placed)}.");
                }
                break;
            case "kick":
                if (args.Length < 1) { Say(reply, "Usage: /kick NAME [reason]"); break; }
                if (OnlineSession(args[0]) is not { } kicked) { Say(reply, $"{args[0]} is not online."); break; }
                if (kicked == session) { Say(reply, "You cannot kick yourself."); break; }
                if (kicked.Can(Permissions.Protected) && !session.Can(Permissions.Protected)) { Say(reply, $"You cannot kick {kicked.Username}."); break; }
                {
                    string why = args.Length > 1 ? string.Join(" ", args.Skip(1)) : "";
                    Log.Information("Moderation: {By} kicked {User}. {Why}", session.Username, kicked.Username, why);
                    _server.Kick(kicked, $"You have been removed from the server by {session.Username}." + (why.Length > 0 ? $" Reason: {why}" : ""));
                    Say(reply, $"Kicked {kicked.Username}.");
                }
                break;
            case "mute":
                if (args.Length < 1) { Say(reply, "Usage: /mute NAME [minutes, 10 if not given]"); break; }
                if (OnlineSession(args[0]) is not { } muted) { Say(reply, $"{args[0]} is not online."); break; }
                if (muted.Can(Permissions.Protected) && !session.Can(Permissions.Protected)) { Say(reply, $"You cannot mute {muted.Username}."); break; }
                {
                    int minutes = args.Length > 1 && int.TryParse(args[1], out int m) ? Math.Clamp(m, 1, 24 * 60) : 10;
                    muted.MutedUntilUtc = DateTime.UtcNow.AddMinutes(minutes);
                    Log.Information("Moderation: {By} muted {User} for {Minutes} min.", session.Username, muted.Username, minutes);
                    _server.SendToSession(muted, new TextEvent { Text = $"You have been muted for {minutes} minute{(minutes == 1 ? "" : "s")} by {session.Username}." });
                    Say(reply, $"Muted {muted.Username} for {minutes} minute{(minutes == 1 ? "" : "s")}.");
                }
                break;
            case "unmute":
                if (args.Length < 1) { Say(reply, "Usage: /unmute NAME"); break; }
                if (OnlineSession(args[0]) is not { } unmuted) { Say(reply, $"{args[0]} is not online."); break; }
                unmuted.MutedUntilUtc = DateTime.MinValue;
                _server.SendToSession(unmuted, new TextEvent { Text = "You can chat again." });
                Say(reply, $"{unmuted.Username} can chat again.");
                break;
            case "join":
            case "travel":
                HandleJoin(session, args, reply);
                break;
            case "savemap":
                HandleSaveMap(session, reply);
                break;
            case "help":
            case "commands":
            case "?":
                Say(reply, args.Length == 0 ? CommandCatalog.Help(session) : CommandCatalog.HelpFor(session, args[0]));
                break;
            default:
                Say(reply, CommandCatalog.Unknown(session, commandName));
                break;
        }
    }

    private static void Say(Action<IMessage> reply, string text) => reply(new TextEvent { Text = text });

    /// <summary>/aimassist [on|off]: the session's aim assistance for shots from the hip. The client
    /// keeps the choice and sends it each time it enters the world; a text player sets it here.</summary>
    internal static string AimAssistCommand(UserSession session, string[] args)
    {
        if (args.Length > 0)
        {
            switch (args[0].ToLowerInvariant())
            {
                case "on": session.AimAssist = true; break;
                case "off": session.AimAssist = false; break;
                default: return "Say /aimassist on or /aimassist off.";
            }
        }
        return session.AimAssist
            ? "Aim assist on: a shot from the hip near somebody in plain view is turned onto them."
            : "Aim assist off: a shot from the hip goes where you point.";
    }

    /// <summary>/detail low|medium|high, or /detail FULL FAR in metres: see GameServer.SetStreamRadii.</summary>
    private void HandleDetail(UserSession session, string[] args, Action<IMessage> reply)
    {
        bool quiet = args.Any(a => a.Equals("quiet", StringComparison.OrdinalIgnoreCase));
        var words = args.Where(a => !a.Equals("quiet", StringComparison.OrdinalIgnoreCase)).ToArray();
        OpenFPS.Common.StreamRadii? asked = null;
        if (words.Length == 1) asked = OpenFPS.Common.StreamRadii.Named(words[0]);
        else if (words.Length >= 2
                 && float.TryParse(words[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float full)
                 && float.TryParse(words[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float far))
            asked = new OpenFPS.Common.StreamRadii(full, far);
        if (words.Length > 0 && asked == null)
        {
            Say(reply, "Say /detail low, /detail medium or /detail high.");
            return;
        }
        string said = _server.SetStreamRadii(session, asked);
        if (!quiet) Say(reply, said);
    }

    private static void DenyCommand(Action<IMessage> reply) =>
        Say(reply, "You do not have permission to execute this command.");

    /// <summary>
    /// The session's map and its body in it. A session that has logged in but not yet sent 'ready' has
    /// no entity, so a world command must not dereference it.
    /// </summary>
    private bool TryGetBody(UserSession session, Action<IMessage> reply,
                            out World world, out SpatialGrid<Entity> grid, out Vector3 position)
    {
        world = null!; grid = null!; position = default;

        if (!_maps.TryGetMap(session.CurrentMapId, out world, out _, out grid, out _))
        {
            Say(reply, $"Map '{session.CurrentMapId}' is not loaded.");
            return false;
        }

        if (session.Entity == Entity.Null || !world.IsAlive(session.Entity) || !world.Has<Transform>(session.Entity))
        {
            Say(reply, "You are not in the world yet. Type 'ready' to enter it first.");
            return false;
        }

        position = world.Get<Transform>(session.Entity).Position;
        return true;
    }

    private static string Plural(string noun)
    {
        string n = noun.ToLowerInvariant();
        if (n.EndsWith("s") || n.EndsWith("x") || n.EndsWith("ch") || n.EndsWith("sh")) return noun + "es";
        return noun + "s";
    }
    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>
    /// Where something is from the way you face, in words ("left in front"), not a clock face. Cody,
    /// 2026-10-04: a clock face made him work out that 5 o'clock was behind him.
    /// </summary>
    internal static string GetRelativeDirection(Quaternion rotation, Vector3 targetDir)
        => OpenFPS.Common.DirectionWords.Relative(rotation, targetDir);
}

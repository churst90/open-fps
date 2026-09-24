using System.Linq;
using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Common.Networking;
using OpenFPS.Common.Components;
using OpenFPS.Server.Repositories;
using OpenFPS.Server.Systems;
using Arch.Core;
using Serilog;

namespace OpenFPS.Server.Core;

/// <summary>
/// Executes the text command set (/scan, /move, /spawn, …).
///
/// Two things about the shape of this class matter. First, it never touches a <see cref="LiteNetLib.NetPeer"/>:
/// it answers through a reply callback, so a telnet session and a UDP session are the same thing to it.
/// Second, every command body runs on the tick thread via the server's command buffer. The MUD gateway
/// dispatches from its own TCP task thread, and a command that mutated the Arch world from there would
/// race the simulation — the old peer-null guard prevented that race only by dropping every MUD command
/// on the floor, silently.
/// </summary>
public class CommandHandler
{
    private readonly SessionManager _sessions;
    private readonly MapManager _maps;
    private readonly GameServer _server;
    private readonly CompositeService? _composites;
    private readonly OccupancyService? _seats;
    private readonly HandsService? _hands;
    private readonly IUserRepository? _users;
    private readonly FriendRepository? _friends;

    public CommandHandler(SessionManager sessions, MapManager maps, GameServer server,
                          CompositeService? composites = null, OccupancyService? seats = null,
                          HandsService? hands = null, IUserRepository? users = null,
                          FriendRepository? friends = null)
    {
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
        bool isElevated = session.Role == UserRole.Dev || session.Role == UserRole.Admin;

        _server.EnqueueCommand(() => {
            try { Execute(commandName, args, session, reply, isElevated); }
            catch (Exception ex)
            {
                Log.Error(ex, "Error executing command '{Command}' for {User}", commandName, session.Username);
                Say(reply, $"Command '{commandName}' failed. The error has been logged.");
            }
        });
    }

    private void Execute(string commandName, string[] args, UserSession session, Action<IMessage> reply, bool isElevated)
    {
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
                if (!isElevated) { DenyCommand(reply); return; }
                GameServer.WriteMotd(string.Join(" ", args));
                Say(reply, args.Length == 0 ? "Message of the day cleared." : "Message of the day set.");
                break;
            case "announce":
                if (!isElevated) { DenyCommand(reply); return; }
                if (args.Length == 0) { Say(reply, "Usage: /announce [message]"); break; }
                _server.Announce(string.Join(" ", args), fromStaff: true);
                break;
            case "move":
            case "tp":
                if (!isElevated) { DenyCommand(reply); return; }
                HandleMove(session, args, reply);
                break;
            case "spawn":
                if (!isElevated) { DenyCommand(reply); return; }
                HandleSpawn(session, args, reply);
                break;
            case "set_sound":
                if (!isElevated) { DenyCommand(reply); return; }
                HandleSetSound(session, args, reply);
                break;
            case "set_audio_mode":
                if (!isElevated) { DenyCommand(reply); return; }
                HandleSetAudioMode(session, args, reply);
                break;
            case "play_folder":
                if (!isElevated) { DenyCommand(reply); return; }
                HandlePlayFolder(session, args, reply);
                break;
            case "start_state":
                if (!isElevated) { DenyCommand(reply); return; }
                HandleStartState(session, args, reply);
                break;
            // ── Building ────────────────────────────────────────────────────────────────────
            //
            // The whole verb set for making something, and it is deliberately small: gather what is
            // around you into one thing, take it apart again, save it so it can be made again, put a
            // saved one down, and write the world to disk. A house, a market stall, a barricade and a
            // vehicle body are all the same five verbs.
            case "group":
                if (!isElevated) { DenyCommand(reply); return; }
                HandleGroup(session, args, reply);
                break;
            case "ungroup":
                if (!isElevated) { DenyCommand(reply); return; }
                HandleUngroup(session, args, reply);
                break;
            case "saveas":
                if (!isElevated) { DenyCommand(reply); return; }
                HandleSaveAs(session, args, reply);
                break;
            case "place":
                if (!isElevated) { DenyCommand(reply); return; }
                HandlePlace(session, args, reply);
                break;
            case "composites":
                HandleListComposites(reply);
                break;
            // ── Placing things where you cannot point ───────────────────────────────────────
            case "origin":
                if (!isElevated) { DenyCommand(reply); return; }
                HandleOrigin(session, reply);
                break;
            case "at":
                if (!isElevated) { DenyCommand(reply); return; }
                HandleAt(session, args, reply);
                break;
            case "put":
                if (!isElevated) { DenyCommand(reply); return; }
                HandlePut(session, args, reply);
                break;
            case "undo":
                if (!isElevated) { DenyCommand(reply); return; }
                HandleUndo(session, reply);
                break;
            case "room":
                HandleRoom(session, args, reply);
                break;
            case "prefabs":
                HandleListPrefabs(reply);
                break;
            // Firing is no longer elevated when you are HOLDING the thing: a gun in your hands is
            // the permission. Naming a weapon out of the air still is — that is the dev trigger.
            case "fire":
            case "shoot":
                HandleFire(session, args, reply, isElevated);
                break;
            // ── Doors ───────────────────────────────────────────────────────────────────────
            //
            // Not elevated. Building a door needs a role; going through one does not.
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
                if (!isElevated) { DenyCommand(reply); return; }
                HandleAddSeat(session, args, reply);
                break;
            case "removeseat":
                if (!isElevated) { DenyCommand(reply); return; }
                HandleRemoveSeat(session, args, reply);
                break;
            case "drivable":
                if (!isElevated) { DenyCommand(reply); return; }
                HandleDrivable(session, args, reply);
                break;
            // ── Occupancy ───────────────────────────────────────────────────────────────────
            //
            // Not elevated, any of it. Getting into things is what the world is FOR; building the
            // thing you get into is the part that needs a role.
            case "ignition":
            case "key":
                HandleIgnition(session, args, reply);
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
            //
            // Not elevated either, and for the same reason: picking a thing up is what the world is
            // for. Every one of these takes an optional NAME, because a player who cannot point has
            // to be able to say which one they meant, and every refusal says what is in the way
            // rather than merely no.
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
            // ── People and places ───────────────────────────────────────────────────────────
            case "friend":
            case "unfriend":
                HandleFriend(session, commandName, args, reply);
                break;
            case "friends":
                if (_friends == null) { Say(reply, "Friends are not available on this server."); break; }
                reply(OpenFPS.Server.Services.SocialService.BuildFriendList(session.Username, _friends, _sessions));
                break;
            case "profile":
            case "whois":
                HandleProfile(session, args, reply);
                break;
            case "where":
            case "locate":
                HandleWhere(session, args, reply);
                break;
            case "join":
            case "travel":
                HandleJoin(session, args, reply);
                break;
            case "savemap":
                if (!isElevated) { DenyCommand(reply); return; }
                HandleSaveMap(session, reply);
                break;
            default:
                Say(reply, $"Command '{commandName}' not recognized.");
                break;
        }
    }

    private static void Say(Action<IMessage> reply, string text) => reply(new TextEvent { Text = text });

    private static void DenyCommand(Action<IMessage> reply) =>
        Say(reply, "You do not have permission to execute this command.");

    /// <summary>
    /// Resolves the session's map and its body in it. A session that has authenticated but not yet sent
    /// 'ready' has no entity, and every world command used to dereference it regardless.
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

    private void HandleSetAudioMode(UserSession session, string[] args, Action<IMessage> reply)
    {
        if (args.Length < 1) { Say(reply, "Usage: /set_audio_mode [LoopOne|LoopFolder|OneShot|StateMachine]"); return; }
        if (!Enum.TryParse<PlaybackMode>(args[0], true, out var mode)) { Say(reply, $"Unknown playback mode '{args[0]}'."); return; }
        if (!TryGetBody(session, reply, out var world, out _, out var playerPos)) return;

        var nearest = FindNearestObject(world, session.Entity, playerPos);
        if (nearest == null) { Say(reply, "No object nearby."); return; }

        var emitter = world.Has<SoundEmitterComponent>(nearest.Value) ? world.Get<SoundEmitterComponent>(nearest.Value) : new SoundEmitterComponent();
        emitter.Mode = mode;
        SetOrAdd(world, nearest.Value, emitter);
        _server.SyncAudioComponent(nearest.Value.Id);

        Say(reply, $"Set audio mode of nearest object to {mode}.");
    }

    private void HandlePlayFolder(UserSession session, string[] args, Action<IMessage> reply)
    {
        if (args.Length < 1) { Say(reply, "Usage: /play_folder [FolderId]"); return; }
        if (!TryGetBody(session, reply, out var world, out _, out var playerPos)) return;

        var nearest = FindNearestObject(world, session.Entity, playerPos);
        if (nearest == null) { Say(reply, "No object nearby."); return; }

        var emitter = world.Has<SoundEmitterComponent>(nearest.Value) ? world.Get<SoundEmitterComponent>(nearest.Value) : new SoundEmitterComponent();
        emitter.SoundId = args[0];
        emitter.Mode = PlaybackMode.LoopFolder;
        SetOrAdd(world, nearest.Value, emitter);
        _server.SyncAudioComponent(nearest.Value.Id);

        Say(reply, $"Nearest object is now playing folder: {args[0]}.");
    }

    private void HandleStartState(UserSession session, string[] args, Action<IMessage> reply)
    {
        if (args.Length < 2) { Say(reply, "Usage: /start_state [StartSoundId] [LoopSoundId]"); return; }
        if (!TryGetBody(session, reply, out var world, out _, out var playerPos)) return;

        var nearest = FindNearestObject(world, session.Entity, playerPos);
        if (nearest == null) { Say(reply, "No object nearby."); return; }

        var emitter = world.Has<SoundEmitterComponent>(nearest.Value) ? world.Get<SoundEmitterComponent>(nearest.Value) : new SoundEmitterComponent();
        emitter.StartSoundId = args[0];
        emitter.SoundId = args[1];
        emitter.Mode = PlaybackMode.StateMachine;
        SetOrAdd(world, nearest.Value, emitter);
        _server.SyncAudioComponent(nearest.Value.Id);

        Say(reply, $"Started state machine on nearest object: {args[0]} -> {args[1]}.");
    }

    private static void SetOrAdd<T>(World world, Entity e, T component) where T : struct
    {
        if (world.Has<T>(e)) world.Set(e, component);
        else world.Add(e, component);
    }

    private static Entity? FindNearestObject(World world, Entity self, Vector3 playerPos)
    {
        Entity? nearest = null;
        float minDist = PhysicsConstants.InteractionRange;
        world.Query(new QueryDescription().WithAll<Transform, IdentityComponent>(), (Entity e, ref Transform t) => {
            if (e == self) return;
            float d = Vector3.Distance(playerPos, t.Position);
            if (d < minDist) { minDist = d; nearest = e; }
        });
        return nearest;
    }

    private void HandleSpawn(UserSession session, string[] args, Action<IMessage> reply)
    {
        if (args.Length < 5)
        {
            Say(reply, "Usage: /spawn [Box|Cylinder] [Material] [sizeX] [sizeY] [sizeZ]");
            return;
        }

        if (!TryGetBody(session, reply, out var world, out _, out var playerPos)) return;

        if (!Enum.TryParse<ColliderShape>(args[0], true, out var shape)) shape = ColliderShape.Box;
        string material = args[1];
        if (!float.TryParse(args[2], out float sx) || !float.TryParse(args[3], out float sy) || !float.TryParse(args[4], out float sz))
        {
            Say(reply, "Invalid sizes.");
            return;
        }

        var rot = world.Get<Transform>(session.Entity).Rotation;
        Vector3 forward = Vector3.Transform(new Vector3(0, 0, 1), rot);
        Vector3 spawnPos = playerPos + (forward * 3.0f);

        // Through the one spawn path: a bare world.Create left the object out of the map lookup and out
        // of the spatial grid, so nothing could see it, hear it or walk into it — while this command
        // cheerfully reported success.
        var e = _maps.SpawnEntity(session.CurrentMapId, w => w.Create(
            new Transform { Position = spawnPos, Rotation = Quaternion.Identity },
            new ColliderComponent { Shape = shape, Size = new Vector3(sx, sy, sz), IsSolid = true },
            new MaterialComponent { Material = material, Variant = "0" },
            new IdentityComponent { Name = $"Custom {shape}" },
            EntityType.StaticObject
        ));

        if (e == Entity.Null) { Say(reply, "Spawn failed: the map is not loaded."); return; }

        Say(reply, $"Spawned {material} {shape} (entity {e.Id}) at {PlayerCoordinates.Format(spawnPos)}");
    }

    private void HandleSetSound(UserSession session, string[] args, Action<IMessage> reply)
    {
        if (args.Length < 2)
        {
            Say(reply, "Usage: /set_sound [SoundId] [Volume]");
            return;
        }

        if (!float.TryParse(args[1], out float vol)) { Say(reply, $"'{args[1]}' is not a volume."); return; }
        if (!TryGetBody(session, reply, out var world, out _, out var playerPos)) return;

        var nearest = FindNearestObject(world, session.Entity, playerPos);
        if (nearest == null)
        {
            Say(reply, "No object nearby to attach sound.");
            return;
        }

        // SetOrAdd, not Add: Add throws on an entity that already carries the component, which is what
        // every sibling handler here already knew.
        SetOrAdd(world, nearest.Value, new SoundEmitterComponent { SoundId = args[0], Volume = vol, Range = 50.0f });
        SetOrAdd(world, nearest.Value, EntityType.Beacon);
        _server.SyncAudioComponent(nearest.Value.Id);

        Say(reply, $"Attached sound {args[0]} to nearest object.");
    }

    private void HandleScan(UserSession session, Action<IMessage> reply)
    {
        if (!TryGetBody(session, reply, out var world, out _, out var playerPos)) return;

        float scanRadius = 20.0f;
        var results = new List<(string Name, float Distance, Vector3 Direction)>();

        world.Query(new QueryDescription().WithAll<Transform>(), (Entity e, ref Transform t) =>
        {
            if (e.Id == session.Entity.Id) return;

            float dist = Vector3.Distance(playerPos, t.Position);
            if (dist <= scanRadius && dist > 0.001f)
            {
                string name = "Unknown Object";
                if (world.Has<IdentityComponent>(e)) name = world.Get<IdentityComponent>(e).Name;
                else if (world.Has<PlayerComponent>(e)) name = world.Get<PlayerComponent>(e).Username;
                else if (world.Has<BeaconComponent>(e)) name = "Beacon";

                results.Add((name, dist, Vector3.Normalize(t.Position - playerPos)));
            }
        });

        if (results.Count == 0)
        {
            Say(reply, "No objects detected nearby.");
            return;
        }

        var playerRotation = world.Get<Transform>(session.Entity).Rotation;
        foreach (var item in results.OrderBy(r => r.Distance).Take(5))
        {
            string direction = GetRelativeDirection(playerRotation, item.Direction);
            Say(reply, $"{item.Name} at {direction}, {item.Distance:F1} meters.");
        }
    }

    // ── Building ────────────────────────────────────────────────────────────────────────────────

    /// <summary>How far a grouping sweep reaches by default, metres. About a room.</summary>
    private const float DefaultGroupRadius = 12f;
    /// <summary>How far away a composite may be and still count as "this one".</summary>
    private const float CompositeReachRadius = 30f;

    private CompositeService? Composites(Action<IMessage> reply)
    {
        if (_composites == null) Say(reply, "Building is not available on this server.");
        return _composites;
    }

    /// <summary>
    /// /group name [radius] [free] — makes one thing out of everything standing near you.
    ///
    /// A radius rather than a selection, because a selection needs pointing at things and pointing is
    /// the one thing a player here cannot do. "Everything within twelve metres of me" is a selection
    /// anybody can make, and can widen or narrow until it is the right one.
    /// </summary>
    private void HandleGroup(UserSession session, string[] args, Action<IMessage> reply)
    {
        var svc = Composites(reply); if (svc == null) return;
        if (args.Length < 1) { Say(reply, "Usage: /group name [radius] [free]"); return; }
        if (!TryGetBody(session, reply, out _, out _, out var position)) return;

        string name = args[0];
        float radius = args.Length > 1 && float.TryParse(args[1], out float r) ? r : DefaultGroupRadius;
        // "free" is the word for a composite that is not fixed down: a caravan rather than a house.
        bool anchored = !args.Contains("free", StringComparer.OrdinalIgnoreCase);

        int root = svc.Group(session.CurrentMapId, position, radius, name, anchored, session.Username, out int parts);
        if (root < 0) { Say(reply, $"Nothing within {radius:F0} m could be grouped."); return; }
        Say(reply, $"Grouped {parts} part(s) within {radius:F0} m into '{name}', "
                 + $"{(anchored ? "fixed in place" : "free to be moved")}, yours. Save it with /saveas.");
    }

    /// <summary>/ungroup — takes the nearest composite apart, leaving its parts exactly where they are.</summary>
    private void HandleUngroup(UserSession session, string[] args, Action<IMessage> reply)
    {
        var svc = Composites(reply); if (svc == null) return;
        if (!TryGetBody(session, reply, out _, out _, out var position)) return;

        int root = svc.NearestRoot(session.CurrentMapId, position, CompositeReachRadius);
        if (root < 0) { Say(reply, $"No composite within {CompositeReachRadius:F0} m."); return; }
        if (!svc.Ungroup(session.CurrentMapId, root, session.Username, Elevated(session),
                         out string name, out int parts, out string error))
        { Say(reply, $"That could not be ungrouped: {error}."); return; }
        Say(reply, $"'{name}' is now {parts} loose part(s), all where they were.");
    }

    /// <summary>/saveas id — writes the nearest composite to disk so anyone can place it again.</summary>
    private void HandleSaveAs(UserSession session, string[] args, Action<IMessage> reply)
    {
        var svc = Composites(reply); if (svc == null) return;
        if (args.Length < 1) { Say(reply, "Usage: /saveas id"); return; }
        if (!TryGetBody(session, reply, out _, out _, out var position)) return;

        int root = svc.NearestRoot(session.CurrentMapId, position, CompositeReachRadius);
        if (root < 0) { Say(reply, $"No composite within {CompositeReachRadius:F0} m. Use /group first."); return; }
        if (!svc.SaveAsTemplate(session.CurrentMapId, root, args[0], session.Username, Elevated(session),
                                out int parts, out string error))
        { Say(reply, $"Could not save: {error}."); return; }
        Say(reply, $"Saved '{args[0]}' with {parts} part(s). Place another with /place {args[0]}.");
    }

    /// <summary>/place id [yaw] — puts a saved composite down at your feet, facing where you like.</summary>
    private void HandlePlace(UserSession session, string[] args, Action<IMessage> reply)
    {
        var svc = Composites(reply); if (svc == null) return;
        if (args.Length < 1) { Say(reply, "Usage: /place id [yaw degrees]"); return; }
        if (!TryGetBody(session, reply, out _, out _, out var position)) return;

        float yaw = args.Length > 1 && float.TryParse(args[1], out float y) ? y : 0f;
        var rotation = Quaternion.CreateFromYawPitchRoll(yaw * (MathF.PI / 180f), 0f, 0f);

        int root = svc.Place(session.CurrentMapId, args[0], position, rotation, session.Username,
                             out int parts, out string error);
        if (root < 0) { Say(reply, $"Could not place: {error}."); return; }
        Say(reply, $"Placed '{args[0]}' here, {parts} part(s), facing {yaw:F0} degrees. "
                 + "It will be gone after a restart until you /savemap.");
    }

    private static bool Elevated(UserSession session)
        => session.Role == UserRole.Dev || session.Role == UserRole.Admin;

    private OccupancyService? Seats(Action<IMessage> reply)
    {
        if (_seats == null) Say(reply, "Getting into things is not available on this server.");
        return _seats;
    }

    /// <summary>
    /// /addseat name [drive] — puts a seat where you are standing, facing the way you are facing.
    ///
    /// Authored by standing in the right place, for the same reason grouping is by radius: a player
    /// here cannot point at anything, but they can always walk to a spot and say "here".
    /// </summary>
    private void HandleAddSeat(UserSession session, string[] args, Action<IMessage> reply)
    {
        var svc = Composites(reply); if (svc == null) return;
        if (args.Length < 1) { Say(reply, "Usage: /addseat name [drive]"); return; }
        if (!TryGetBody(session, reply, out var world, out _, out var position)) return;

        int root = svc.NearestRoot(session.CurrentMapId, position, CompositeReachRadius);
        if (root < 0) { Say(reply, $"No composite within {CompositeReachRadius:F0} m. Use /group first."); return; }

        bool controls = args.Contains("drive", StringComparer.OrdinalIgnoreCase)
                     || args.Contains("driver", StringComparer.OrdinalIgnoreCase);
        float yaw = world.Has<PlayerComponent>(session.Entity) ? world.Get<PlayerComponent>(session.Entity).Yaw : 0f;

        if (!svc.AddSeat(session.CurrentMapId, root, args[0], controls, position, yaw,
                         session.Username, Elevated(session), out string error))
        { Say(reply, $"Could not add that seat: {error}."); return; }

        Say(reply, controls
            ? $"Seat '{args[0]}' added here, and it drives."
            : $"Seat '{args[0]}' added here.");
    }

    /// <summary>/removeseat name — forgets a seat, putting whoever is in it out first.</summary>
    private void HandleRemoveSeat(UserSession session, string[] args, Action<IMessage> reply)
    {
        var svc = Composites(reply); if (svc == null) return;
        if (args.Length < 1) { Say(reply, "Usage: /removeseat name"); return; }
        if (!TryGetBody(session, reply, out _, out _, out var position)) return;

        int root = svc.NearestRoot(session.CurrentMapId, position, CompositeReachRadius);
        if (root < 0) { Say(reply, $"No composite within {CompositeReachRadius:F0} m."); return; }
        if (!svc.RemoveSeat(session.CurrentMapId, root, args[0], session.Username, Elevated(session), out string error))
        { Say(reply, $"Could not remove that seat: {error}."); return; }
        Say(reply, $"Seat '{args[0]}' removed.");
    }

    /// <summary>
    /// /drivable preset — gives the nearest free composite an engine, tyres and a mass.
    ///
    /// The last step of turning a pile of walls into a car, and the shortest, because everything it
    /// needs already exists: the same vehicle profiles the map's own traffic runs on. From here the
    /// thing is a vehicle to every system that cares — the grid, the broadcast radius, the client's
    /// engine synthesis — none of which needs telling that a player built this one.
    /// </summary>
    private void HandleDrivable(UserSession session, string[] args, Action<IMessage> reply)
    {
        var svc = Composites(reply); if (svc == null) return;
        if (args.Length < 1)
        {
            Say(reply, $"Usage: /drivable preset. Known: {string.Join(", ", MachineRegistry.Ids)}");
            return;
        }
        if (!TryGetBody(session, reply, out _, out _, out var position)) return;

        int root = svc.NearestRoot(session.CurrentMapId, position, CompositeReachRadius);
        if (root < 0) { Say(reply, $"No composite within {CompositeReachRadius:F0} m."); return; }
        if (!svc.MakeDrivable(session.CurrentMapId, root, args[0], session.Username, Elevated(session), out string error))
        { Say(reply, $"Could not make that drivable: {error}."); return; }

        _server.SyncAudioComponent(root);
        var profile = MachineRegistry.VehicleFor(args[0]);
        Say(reply, $"It drives as a {profile.Name} now — {profile.Engine.Name}, {profile.MassKg:F0} kg. "
                 + "Add a seat that drives with /addseat driver drive, then get in with /enter.");
    }

    /// <summary>/enter [seat] — gets into the nearest thing with seats.</summary>
    private void HandleEnter(UserSession session, string[] args, Action<IMessage> reply)
    {
        var svc = Seats(reply); if (svc == null) return;
        if (!TryGetBody(session, reply, out _, out _, out var position)) return;

        int root = svc.NearestEnterable(session.CurrentMapId, position, OccupancyService.BoardingRange);
        if (root < 0) { Say(reply, "There is nothing to get into within reach."); return; }

        svc.Enter(session, root, args.Length > 0 ? string.Join(" ", args) : null, out string message);
        Say(reply, message);
    }

    /// <summary>/exit — gets out, onto a clear patch of ground beside it.</summary>
    private void HandleExit(UserSession session, Action<IMessage> reply)
    {
        var svc = Seats(reply); if (svc == null) return;
        svc.Exit(session, out string message);
        Say(reply, message);
    }

    /// <summary>
    /// /seats — reads out what is inside the nearest thing you could get into.
    ///
    /// The replacement for looking through a window, and it has to say which seats are TAKEN as well
    /// as which exist: walking round a car trying doors is how a sighted player finds that out.
    /// </summary>
    private void HandleSeats(UserSession session, Action<IMessage> reply)
    {
        var svc = Seats(reply); if (svc == null) return;
        if (!TryGetBody(session, reply, out var world, out _, out var position)) return;

        int root = world.Has<OccupantComponent>(session.Entity)
            ? world.Get<OccupantComponent>(session.Entity).RootEntityId
            : svc.NearestEnterable(session.CurrentMapId, position, CompositeReachRadius);
        if (root < 0) { Say(reply, "Nothing with seats nearby."); return; }

        var seats = svc.Seats(session.CurrentMapId, root, position);
        if (seats.Count == 0) { Say(reply, "It has no seats."); return; }

        foreach (var seat in seats)
            Say(reply, $"  {seat.Name}{(seat.Controls ? " (drives)" : "")}: "
                     + $"{(seat.Taken ? "taken" : "free")}, {seat.Distance:F1} metres.");
    }

    // ── Carrying things ─────────────────────────────────────────────────────────────────────────
    //
    // Four verbs and two readouts, and between them they are the whole inventory: take, drop, stow,
    // draw, hands, inv. There is no inventory SCREEN because there is nothing to put on one — a
    // thing you are carrying is the same entity it was on the floor, at a position on your body, and
    // the readout is a sentence rather than a grid because a sentence is what a screen reader takes
    // in at a go.

    private HandsService? Hands(Action<IMessage> reply)
    {
        if (_hands == null) Say(reply, "Picking things up is not available on this server.");
        return _hands;
    }

    /// <summary>/take [name] — picks up the nearest thing you can reach, or the nearest one called that.</summary>
    private void HandleTake(UserSession session, string[] args, Action<IMessage> reply)
    {
        var svc = Hands(reply); if (svc == null) return;
        svc.Take(session, string.Join(" ", args), out string message);
        Say(reply, message);
    }

    /// <summary>
    /// /drop [name|left|right|all] — puts something down, and it lands.
    ///
    /// The landing goes out to everyone in earshot rather than back to the person who dropped it: a
    /// dropped thing is a sound in a room, and the useful half of it is that the people who did NOT
    /// drop it can hear where it went.
    /// </summary>
    private void HandleDrop(UserSession session, string[] args, Action<IMessage> reply)
    {
        var svc = Hands(reply); if (svc == null) return;
        svc.Drop(session, string.Join(" ", args), out string message,
                 (id, label, sounds) => _server.EmitWorldAudio(session.CurrentMapId, id, label, sounds));
        Say(reply, message);
    }

    /// <summary>/stow [name|left|right|all] — slings what you are holding onto your back.</summary>
    private void HandleStow(UserSession session, string[] args, Action<IMessage> reply)
    {
        var svc = Hands(reply); if (svc == null) return;
        svc.Stow(session, string.Join(" ", args), out string message);
        Say(reply, message);
    }

    /// <summary>/draw [name] — takes something off your back and puts it in your hands.</summary>
    private void HandleDraw(UserSession session, string[] args, Action<IMessage> reply)
    {
        var svc = Hands(reply); if (svc == null) return;
        svc.Draw(session, string.Join(" ", args), out string message);
        Say(reply, message);
    }

    /// <summary>/hands — what is in your hands, which is the question with the hard limit behind it.</summary>
    private void HandleHands(UserSession session, Action<IMessage> reply)
    {
        var svc = Hands(reply); if (svc == null) return;
        if (!TryGetBody(session, reply, out var world, out _, out _)) return;
        if (!_maps.TryGetMap(session.CurrentMapId, out _, out _, out _, out var lookup)) return;
        var hands = world.Has<HandsComponent>(session.Entity)
            ? world.Get<HandsComponent>(session.Entity) : new HandsComponent();
        Say(reply, HandsService.Carrying(world, lookup, hands));
    }

    /// <summary>/inv — everything you have on you, hands first, with what it weighs.</summary>
    private void HandleInventory(UserSession session, Action<IMessage> reply)
    {
        var svc = Hands(reply); if (svc == null) return;
        Say(reply, svc.Readout(session));
    }

    // ── Building where you cannot point ─────────────────────────────────────────────────────────
    //
    // Walking to the spot works for a wall and not for a roof, and walking to twenty wall positions
    // in a row does not reliably produce a straight wall. So there is a CURSOR: moved in metres from
    // an origin the player chose, announcing itself and what is already there every time it moves.
    // That is a review cursor, which a screen-reader user has navigated documents with for years.

    /// <summary>/origin — puts the build origin at your feet, facing the way you face.</summary>
    private void HandleOrigin(UserSession session, Action<IMessage> reply)
    {
        if (!TryGetBody(session, reply, out var world, out _, out var position)) return;
        float yaw = world.Has<PlayerComponent>(session.Entity) ? world.Get<PlayerComponent>(session.Entity).Yaw : 0f;
        session.Build.SetOrigin(position, yaw);
        Say(reply, $"Build origin set at your feet, forward is {Compass(yaw)}. The cursor is at the origin.");
    }

    /// <summary>
    /// /at [right up forward] — moves the cursor there and says what is there, or just reads it out.
    ///
    /// Also takes a named direction and a distance (`/at forward 3`), which is the form you want when
    /// you are running a wall: it moves RELATIVE to where the cursor already is, and remembers the
    /// direction so a `/put ... run` can follow it.
    /// </summary>
    private void HandleAt(UserSession session, string[] args, Action<IMessage> reply)
    {
        if (!TryGetBody(session, reply, out var world, out var grid, out _)) return;
        var build = session.Build;
        if (!build.Placed) { Say(reply, "Set a build origin first with /origin."); return; }

        if (args.Length >= 2 && BuildSession.TryDirection(args[0], out var direction))
        {
            if (!float.TryParse(args[1], out float distance)) { Say(reply, $"'{args[1]}' is not a distance."); return; }
            build.Cursor += direction * distance;
            build.LastStep = direction;
        }
        else if (args.Length >= 3)
        {
            if (!float.TryParse(args[0], out float right) || !float.TryParse(args[1], out float up) || !float.TryParse(args[2], out float forward))
            { Say(reply, "Usage: /at right up forward, or /at <direction> <metres>"); return; }
            var moved = new Vector3(right, up, forward) - build.Cursor;
            if (moved.LengthSquared() > 0.0001f) build.LastStep = Vector3.Normalize(moved);
            build.Cursor = new Vector3(right, up, forward);
        }
        else if (args.Length != 0)
        {
            Say(reply, "Usage: /at right up forward, or /at <direction> <metres>, or /at on its own to read it out.");
            return;
        }

        Say(reply, $"Cursor {build.Describe()}. {WhatIsAt(world, grid, build.WorldCursor)}");
    }

    /// <summary>
    /// What is already where the cursor is.
    ///
    /// The half of the cursor that makes it usable. Moving to a spot and being told nothing is the
    /// same as not moving; being told "concrete wall" is how you find the wall you placed a minute
    /// ago and build the next one against it.
    /// </summary>
    private static string WhatIsAt(World world, SpatialGrid<Entity> grid, Vector3 at)
    {
        Entity? closest = null;
        float nearest = 1.5f;
        foreach (var e in grid.GetItemsInRadius(at, 3f))
        {
            if (!world.IsAlive(e) || !world.Has<Transform>(e) || world.Has<PlayerComponent>(e)) continue;
            float d = Vector3.Distance(world.Get<Transform>(e).Position, at);
            if (d < nearest) { nearest = d; closest = e; }
        }
        if (closest == null) return "Empty.";
        string name = world.Has<IdentityComponent>(closest.Value) ? world.Get<IdentityComponent>(closest.Value).Name
                    : world.Has<NameComponent>(closest.Value) ? world.Get<NameComponent>(closest.Value).Name
                    : "something";
        return nearest < 0.3f ? $"{name}, right here." : $"{name}, {nearest:F1} m off.";
    }

    /// <summary>
    /// /put prefab [turn degrees] [run n] — places a prefab at the cursor.
    ///
    /// The run is the important half. A wall is not one part, it is a line of them, and a line placed
    /// by hand from a cursor is only as straight as the arithmetic somebody did in their head. Asking
    /// for a run steps by the part's OWN footprint along the direction the cursor last travelled, so
    /// the panels touch, the wall is straight, and it is one command instead of eight.
    /// </summary>
    private void HandlePut(UserSession session, string[] args, Action<IMessage> reply)
    {
        var svc = Composites(reply); if (svc == null) return;
        if (args.Length < 1)
        { Say(reply, "Usage: /put prefab [turn degrees] [run count [direction]]. /prefabs lists them."); return; }
        if (!TryGetBody(session, reply, out var world, out var grid, out _)) return;

        var build = session.Build;
        if (!build.Placed) { Say(reply, "Set a build origin first with /origin."); return; }

        string prefabId = args[0];
        if (!svc.Prefabs.Prefabs.ContainsKey(prefabId.ToLowerInvariant()))
        { Say(reply, $"There is no prefab called '{prefabId}'. /prefabs lists them."); return; }

        float turn = 0f;
        int run = 1;
        var step = build.LastStep;
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i].Equals("turn", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                float.TryParse(args[i + 1], out turn);
            else if (args[i].Equals("run", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                int.TryParse(args[i + 1], out run);
                // A direction on the run itself — "run 4 right" — which is what anybody would say.
                // Without it the only way to aim a run is to move the cursor zero metres in the
                // direction you want first, which works and is a riddle.
                if (i + 2 < args.Length && BuildSession.TryDirection(args[i + 2], out var aimed))
                {
                    step = aimed;
                    build.LastStep = aimed;
                }
            }
        }
        run = Math.Clamp(run, 1, 64);

        var rotation = build.Facing(turn);
        float spacing = FootprintAlong(svc.Prefabs, prefabId, rotation, build.Yaw, step);

        int placed = 0, blocked = 0;
        for (int i = 0; i < run; i++)
        {
            var local = build.Cursor + step * (spacing * i);
            var at = build.ToWorld(local);
            if (Occupied(world, grid, at, 0.3f)) { blocked++; continue; }

            Entity e;
            try { e = _maps.SpawnEntity(session.CurrentMapId, w => svc.Prefabs.Spawn(w, prefabId, at, rotation, Vector3.One)); }
            catch (Exception ex) { Say(reply, $"Could not place it: {ex.Message}"); return; }
            if (e == Entity.Null) { Say(reply, "Could not place it: the map is not loaded."); return; }
            build.Placed_Entities.Add(e.Id);
            placed++;
        }

        if (placed == 0) { Say(reply, "There is already something there. Nothing placed."); return; }
        // Leave the cursor at the end of the run, where the next thing goes.
        if (placed > 1) build.Cursor += step * (spacing * placed);

        string what = svc.Prefabs.Prefabs[prefabId.ToLowerInvariant()].Name;
        Say(reply, placed == 1
            ? $"{what} placed {build.Describe()}.{(blocked > 0 ? " Something was already there." : "")}"
            : $"{placed} x {what} placed in a line, {spacing:0.##} m apart. Cursor now {build.Describe()}."
              + (blocked > 0 ? $" {blocked} skipped, something was already there." : ""));
    }

    /// <summary>How far along a direction one of these reaches, so a run of them touches.</summary>
    private static float FootprintAlong(PrefabRepository prefabs, string prefabId, Quaternion rotation,
                                        float buildYaw, Vector3 stepInBuildAxes)
    {
        // A prefab need not have a body at all (a region volume, a bare emitter). One metre is a
        // sane pitch for a run of those, and the alternative — a run that never advances — would
        // stack the whole lot in one place with nothing to say why.
        var size = prefabs.Prefabs[prefabId.ToLowerInvariant()].ColliderSize ?? Vector3.One;
        if (size == Vector3.Zero) return 1f;
        // The part is rotated into the world; the step is in the builder's axes. Measure the part's
        // extent along the step by taking both into the same frame.
        var half = CompositeAcoustics.AxisAlignedHalfExtents(size * 0.5f,
            Quaternion.Concatenate(rotation, Quaternion.Inverse(Quaternion.CreateFromYawPitchRoll(buildYaw, 0f, 0f))));
        float along = MathF.Abs(stepInBuildAxes.X) * half.X
                    + MathF.Abs(stepInBuildAxes.Y) * half.Y
                    + MathF.Abs(stepInBuildAxes.Z) * half.Z;
        return MathF.Max(0.1f, along * 2f);
    }

    /// <summary>Whether something solid is already sitting where a part is about to go.</summary>
    private static bool Occupied(World world, SpatialGrid<Entity> grid, Vector3 at, float radius)
    {
        foreach (var e in grid.GetItemsInRadius(at, MathF.Max(radius, 1f)))
        {
            if (!world.IsAlive(e) || !world.Has<Transform>(e) || world.Has<PlayerComponent>(e)) continue;
            if (Vector3.Distance(world.Get<Transform>(e).Position, at) < radius) return true;
        }
        return false;
    }

    /// <summary>
    /// /undo — takes back the last thing you placed.
    ///
    /// Not a convenience. A part in the wrong place is invisible to somebody who cannot see it, so
    /// the mistake is not merely unfixed, it is undetectable until they walk into it — and by then
    /// they have built three more things around it.
    /// </summary>
    private void HandleUndo(UserSession session, Action<IMessage> reply)
    {
        var build = session.Build;
        if (!TryGetBody(session, reply, out var world, out _, out _)) return;

        while (build.Placed_Entities.Count > 0)
        {
            int id = build.Placed_Entities[^1];
            build.Placed_Entities.RemoveAt(build.Placed_Entities.Count - 1);
            if (!_maps.TryGetMap(session.CurrentMapId, out _, out _, out _, out var lookup)) break;
            if (!lookup.TryGetValue(id, out var e) || !world.IsAlive(e)) continue;   // already gone

            string what = world.Has<IdentityComponent>(e) ? world.Get<IdentityComponent>(e).Name : "it";
            _maps.DestroyEntity(session.CurrentMapId, e);
            _server.BroadcastRemoval(session.CurrentMapId, id);
            Say(reply, $"Took back the {what}.");
            return;
        }
        Say(reply, "You have not placed anything.");
    }

    /// <summary>
    /// /room [radius] — says whether what is around you encloses a room, and if not, what is missing.
    ///
    /// A dry run of the rule `/group` will apply, so it can be asked BEFORE committing. This is the
    /// feedback loop the whole of building without sight hangs off: a sighted builder stands back and
    /// sees that the roof is missing, and this is the replacement for standing back.
    /// </summary>
    private void HandleRoom(UserSession session, string[] args, Action<IMessage> reply)
    {
        if (!TryGetBody(session, reply, out var world, out _, out var position)) return;
        float radius = args.Length > 0 && float.TryParse(args[0], out float r) ? r : DefaultGroupRadius;

        // Exactly what a sweep of this radius would take, measured about where it would put the origin.
        var parts = new List<Entity>();
        var q = new QueryDescription().WithAll<Transform>();
        float r2 = radius * radius;
        world.Query(in q, (Entity e, ref Transform t) =>
        {
            if (Vector3.DistanceSquared(t.Position, position) > r2) return;
            if (!CompositeService.CanBeGrouped(world, e)) return;
            if (CompositeService.IsBiggerThanTheSweep(world, e, radius)) return;
            parts.Add(e);
        });

        if (parts.Count == 0) { Say(reply, $"Nothing within {radius:F0} m that could be built with."); return; }

        var survey = CompositeAcoustics.SurveyLoose(world, parts);
        Say(reply, $"{parts.Count} part(s) within {radius:F0} m. {survey.Explain()}");
    }

    /// <summary>/prefabs — what there is to put down.</summary>
    private void HandleListPrefabs(Action<IMessage> reply)
    {
        var svc = Composites(reply); if (svc == null) return;
        var all = svc.Prefabs.Prefabs;
        if (all.Count == 0) { Say(reply, "No prefabs are loaded."); return; }
        Say(reply, $"{all.Count} prefab(s):");
        foreach (var kv in all)
        {
            var size = kv.Value.ColliderSize;
            Say(reply, $"  {kv.Key}: {kv.Value.Name}, {kv.Value.Material}"
                     + (size.HasValue ? $", {size.Value.X:0.##} by {size.Value.Y:0.##} by {size.Value.Z:0.##} m." : ", no body."));
        }
    }

    private static string Compass(float yaw)
    {
        float degrees = yaw * (180f / MathF.PI);
        while (degrees < 0) degrees += 360f;
        while (degrees >= 360f) degrees -= 360f;
        return degrees switch
        {
            < 22.5f or >= 337.5f => "north", < 67.5f => "north east", < 112.5f => "east",
            < 157.5f => "south east", < 202.5f => "south", < 247.5f => "south west",
            < 292.5f => "west", _ => "north west",
        };
    }

    /// <summary>
    /// /fire [weapon] — fires what is in your hands, at whatever is in front of you.
    ///
    /// This used to be a DEV TRIGGER and nothing else, because nothing connected a gun to a player:
    /// there was no equip, no held item and no trigger, so `WeaponSynth`, `ShotResolver`,
    /// `WeaponMechanics` and `GlassBreak` were four tested models nobody had ever heard. Hands supply
    /// the real join, and they supply it without an `EquippedWeaponComponent`: the weapon is the
    /// `ItemComponent.WeaponId` of the thing you are holding, so equipping a gun and picking one up
    /// are the same act, and putting it down disarms you with no bookkeeping anywhere.
    ///
    /// Naming a weapon out of the air stays elevated, and stays the trigger it was — useful for
    /// hearing a model without first building a world to find a gun in.
    ///
    /// It also happens to be the only thing in the game that can currently break a window, which is
    /// why the glass path hangs off it too.
    /// </summary>
    private void HandleFire(UserSession session, string[] args, Action<IMessage> reply, bool isElevated)
    {
        if (!TryGetBody(session, reply, out var world, out var grid, out var position)) return;
        if (!_maps.TryGetMap(session.CurrentMapId, out _, out _, out _, out var lookup)) return;

        bool armed = HandsService.TryGetHeldWeapon(world, session.Entity, lookup, out var weapon, out _);

        // Naming one overrides what you are holding, and only a dev may do that. Everyone else fires
        // the thing in their hands or nothing, which is the rule the world should have had all along.
        //
        // NAMING ONE IS THE WHOLE OF THE EXEMPTION. This used to read `isElevated && (args.Length > 0
        // || !armed)`, so an admin who fired with EMPTY HANDS was handed an AKM out of the air — and
        // then asked, quite reasonably, "why am I holding an unloaded AKM anyway?" They were not
        // holding anything. A dev convenience that arms you silently is the same shape of fault as a
        // trigger on a screen reader's key: the sound happens and the player cannot tell why.
        // `/fire akm` still works and is what the convenience was for.
        if (isElevated && args.Length > 0)
        {
            string id = args[0];
            if (!WeaponRegistry.TryGet(id, out weapon))
            {
                Say(reply, $"No weapon called '{id}'. Try: {string.Join(", ", WeaponRegistry.All.Select(w => w.Id))}");
                return;
            }
        }
        else if (!armed)
        {
            Say(reply, "You are not holding anything you can fire.");
            return;
        }

        var rotation = world.Get<Transform>(session.Entity).Rotation;
        var forward = Vector3.Transform(new Vector3(0, 0, 1), rotation);
        var muzzle = position + new Vector3(0, 1.5f, 0) + forward * 0.5f;

        // 1. The shot itself. Named rather than described, because a gunshot is a blast wave, a body
        //    resonance, a brightness sweep and the action working — and a model for that already
        //    exists and is better than four numbers.
        _server.EmitWorldAudio(session.CurrentMapId, session.Entity.Id, weapon.DisplayName, new[]
        {
            new TransientSound
            {
                Character = SoundCharacter.Knock,
                Position = muzzle,
                LevelDb = Loudness.MuzzleBlastDb(weapon),
                SynthKey = "weapon:" + weapon.Id,
                DecaySeconds = 0.6f,
            },
        });

        // 2. What it hit, if anything.
        string hit = "nothing in the first hundred metres";
        foreach (var candidate in grid.GetItemsInRadius(position, 100f).OrderBy(
                     e => Vector3.Distance(position, world.Get<Transform>(e).Position)))
        {
            if (candidate.Id == session.Entity.Id || !world.Has<ColliderComponent>(candidate)) continue;
            var t = world.Get<Transform>(candidate);
            var c = world.Get<ColliderComponent>(candidate);
            if (!c.IsSolid) continue;

            var toTarget = t.Position - muzzle;
            float distance = toTarget.Length();
            if (distance < 0.1f || Vector3.Dot(Vector3.Normalize(toTarget), forward) < 0.97f) continue;

            string material = world.Has<MaterialComponent>(candidate)
                ? world.Get<MaterialComponent>(candidate).Material ?? "Generic" : "Generic";
            hit = $"{material} at {distance:F0} metres";

            if (material.Equals("Glass", StringComparison.OrdinalIgnoreCase))
                BreakGlass(session, world, candidate, t, c, weapon);
            else
                _server.EmitWorldAudio(session.CurrentMapId, candidate.Id, "impact",
                    ImpactAcoustics.Between(AcousticRegistry.GetProperties("Metal"),
                                            AcousticRegistry.GetProperties(material),
                                            t.Position, weapon.MuzzleVelocity * 0.02f,
                                            0.01f, 500f, c.Size.X, c.Size.Y, MathF.Max(0.02f, c.Size.Z)));
            break;
        }
        Say(reply, $"You fire the {weapon.DisplayName}. It hits {hit}.");
    }

    /// <summary>
    /// A window going out, which is two sounds most of two seconds apart and from two different places.
    ///
    /// The whole reason `GlassBreak` was worth writing: the break is up at the window, then nothing,
    /// then the glass arrives at the FOOT of the wall — and the gap between them is sqrt(2h/g), a
    /// direct readout of which floor the shot was on. One crash sample throws that away, and a sighted
    /// game would never notice it was gone.
    /// </summary>
    private void BreakGlass(UserSession session, World world, Entity pane, Transform t,
                            ColliderComponent collider, WeaponDefinition weapon)
    {
        var glass = new GlassPane(
            Centre: t.Position,
            Size: new Vector2(MathF.Max(0.3f, collider.Size.X), MathF.Max(0.3f, collider.Size.Y)),
            Normal: Vector3.Transform(new Vector3(0, 0, 1), t.Rotation),
            // Tempered: what modern glazing is, and the type that always fails completely rather
            // than taking a neat hole. Held in compression, so there is no such thing as a tidy
            // bullet hole in it.
            Type: GlassType.Tempered,
            HeightAboveGround: MathF.Max(0f, t.Position.Y - collider.Size.Y * 0.5f));

        Span<GlassEvent> buffer = stackalloc GlassEvent[48];
        int count = GlassBreak.Resolve(glass, t.Position, weapon, pane.Id, buffer);
        if (count == 0) return;

        var events = new List<GlassEvent>(count);
        for (int i = 0; i < count; i++) events.Add(buffer[i]);

        _server.EmitWorldAudio(session.CurrentMapId, pane.Id, "glass",
                               GlassSound.From(events, glass.Type, glass.Size,
                                               MathF.Max(0.003f, collider.Size.Z)));
        _maps.DestroyEntity(session.CurrentMapId, pane);
        _server.BroadcastRemoval(session.CurrentMapId, pane.Id);
    }

    // ── Doors ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// How far away a door can be and still be one you can reach.
    ///
    /// The same range as every other interaction rather than a number of its own: a door is a thing
    /// you reach for, and there is no reason reaching for one should work at a different distance
    /// from reaching for anything else.
    /// </summary>
    private const float DoorReach = PhysicsConstants.InteractionRange;

    /// <summary>
    /// /open [name] — opens the door you meant, which is nearly always the nearest one.
    ///
    /// From a seat it is YOUR door: the one nearest the seat you are sitting in, which is the
    /// question "which door serves this seat" answered by proximity rather than by a table somebody
    /// has to author and keep in step. That is automatically right for a two-door, a four-door, a bus
    /// with a middle door, and whatever anybody invents next.
    /// </summary>
    private void HandleDoor(UserSession session, string[] args, Action<IMessage> reply, bool open)
    {
        if (!TryGetBody(session, reply, out var world, out _, out var position)) return;

        var from = ReachingFrom(world, session.Entity, position);
        string wanted = args.Length > 0 ? string.Join(" ", args) : "";

        var door = NearestDoor(world, from, wanted, out float distance, out string name);
        if (door == null)
        {
            // Say how far the nearest one actually is. "No door within five metres" while /doors is
            // cheerfully reporting one at five and a half is the tool contradicting itself, and the
            // player has no way to tell which of the two is lying.
            var anywhere = NearestDoor(world, from, wanted, out float away, out string itsName, reach: 40f);
            Say(reply, anywhere != null
                ? $"The nearest {itsName} is {away:F1} metres away. Get closer."
                : string.IsNullOrEmpty(wanted)
                    ? "There is no door anywhere near you."
                    : $"There is no door called '{wanted}' near you.");
            return;
        }

        var state = world.Get<DoorComponent>(door.Value);
        if (!DoorSystem.Set(world, door.Value, open))
        {
            Say(reply, state.Openness >= 1f ? $"The {name} is already open."
                     : state.Openness <= 0f ? $"The {name} is already shut."
                     : $"The {name} is already moving.");
            return;
        }
        Say(reply, $"The {name} swings {(open ? "open" : "shut")}, {distance:F1} metres away.");
    }

    /// <summary>
    /// /doors — what doors are near, how far, and whether they are open.
    ///
    /// The replacement for glancing round a room. It has to say the STATE as well as the name: an
    /// open door and a shut one in the same place are different facts, and the only other way to
    /// find out which you have is to walk into it.
    /// </summary>
    private void HandleListDoors(UserSession session, Action<IMessage> reply)
    {
        if (!TryGetBody(session, reply, out var world, out _, out var position)) return;
        var from = ReachingFrom(world, session.Entity, position);

        var found = new List<(string Name, float Distance, float Openness, string Direction)>();
        var rotation = world.Get<Transform>(session.Entity).Rotation;
        world.Query(new QueryDescription().WithAll<Transform, DoorComponent>(), (Entity e, ref Transform t, ref DoorComponent d) =>
        {
            float distance = Vector3.Distance(from, t.Position);
            if (distance > 20f) return;
            var offset = t.Position - from;
            found.Add((DoorName(world, e), distance, d.Openness,
                       offset.LengthSquared() > 0.01f ? GetRelativeDirection(rotation, Vector3.Normalize(offset)) : "right here"));
        });

        if (found.Count == 0) { Say(reply, "No doors within twenty metres."); return; }
        foreach (var d in found.OrderBy(x => x.Distance))
            Say(reply, $"  {d.Name}: {(d.Openness >= 1f ? "open" : d.Openness <= 0f ? "shut" : $"{d.Openness * 100f:F0} per cent open")}, "
                     + $"{d.Direction}, {d.Distance:F1} metres.");
    }

    /// <summary>
    /// Where a player is reaching from — their seat if they are in one, otherwise their feet.
    ///
    /// Sitting in a car, the door you mean is the one beside YOU, not the one nearest the middle of
    /// the vehicle. On a bus those are different doors.
    /// </summary>
    private static Vector3 ReachingFrom(World world, Entity player, Vector3 position)
        => world.Has<OccupantComponent>(player) && world.Has<Transform>(player)
            ? world.Get<Transform>(player).Position
            : position;

    private static Entity? NearestDoor(World world, Vector3 from, string named,
                                       out float distance, out string name, float? reach = null)
    {
        Entity? best = null;
        float bestDistance = reach ?? (string.IsNullOrEmpty(named) ? DoorReach : 20f);
        string bestName = "door";
        world.Query(new QueryDescription().WithAll<Transform, DoorComponent>(), (Entity e, ref Transform t, ref DoorComponent _) =>
        {
            string thisName = DoorName(world, e);
            if (!string.IsNullOrEmpty(named)
                && thisName.IndexOf(named, StringComparison.OrdinalIgnoreCase) < 0) return;
            float d = Vector3.Distance(from, t.Position);
            if (d > bestDistance) return;
            bestDistance = d; best = e; bestName = thisName;
        });
        distance = bestDistance; name = bestName;
        return best;
    }

    private static string DoorName(World world, Entity e)
        => world.Has<IdentityComponent>(e) && !string.IsNullOrWhiteSpace(world.Get<IdentityComponent>(e).Name)
            ? world.Get<IdentityComponent>(e).Name
            : world.Has<NameComponent>(e) && !string.IsNullOrWhiteSpace(world.Get<NameComponent>(e).Name)
                ? world.Get<NameComponent>(e).Name
                : "door";

    /// <summary>/composites — what there is to place.</summary>
    private void HandleListComposites(Action<IMessage> reply)
    {
        var svc = Composites(reply); if (svc == null) return;
        var all = svc.Templates.All;
        if (all.Count == 0) { Say(reply, "Nothing has been saved yet. Build something and use /group then /saveas."); return; }
        Say(reply, $"{all.Count} composite(s) available:");
        foreach (var kv in all)
            Say(reply, $"  {kv.Key}: {kv.Value.Name}, {kv.Value.Parts.Count} part(s), "
                     + $"{(kv.Value.Anchored ? "fixed" : "free")}.");
    }

    /// <summary>
    /// /savemap — writes this map to disk exactly as it now stands.
    ///
    /// Explicit rather than automatic on purpose. A world that rewrites its own map file every time
    /// somebody experiments cannot be experimented with, and the first thing anyone does with a
    /// building tool is put something in the wrong place.
    /// </summary>
    private void HandleSaveMap(UserSession session, Action<IMessage> reply)
    {
        if (!_maps.SaveMap(session.CurrentMapId, out string error))
        { Say(reply, $"Could not save the map: {error}."); return; }
        Say(reply, $"Map '{session.CurrentMapId}' saved. Anything you placed is now permanent.");
    }

    /// <summary>
    /// /ignition [on|off] — the key, from the driver's seat. No argument turns it the other way from
    /// wherever it is, which is what a key does.
    /// </summary>
    private void HandleIgnition(UserSession session, string[] args, Action<IMessage> reply)
    {
        if (!_maps.TryGetMap(session.CurrentMapId, out var world, out _, out _, out var lookup)
            || session.Entity == Entity.Null || !world.IsAlive(session.Entity))
        { Say(reply, "You are not in the world yet."); return; }
        if (!world.Has<OccupantComponent>(session.Entity))
        { Say(reply, "You are not sitting in anything."); return; }
        var occupant = world.Get<OccupantComponent>(session.Entity);
        if (!occupant.Controls) { Say(reply, "The key is in front of the driver's seat."); return; }
        if (!lookup.TryGetValue(occupant.RootEntityId, out var root) || !world.IsAlive(root))
        { Say(reply, "There is nothing here to start."); return; }
        bool on = world.Has<DriveComponent>(root) && !world.Get<DriveComponent>(root).EngineOn;
        if (args.Length > 0) on = !args[0].Equals("off", StringComparison.OrdinalIgnoreCase);
        Say(reply, DrivingSystem.SetIgnition(world, root, on, _server.SyncAudioComponent));
    }

    private void HandleMove(UserSession session, string[] args, Action<IMessage> reply)
    {
        if (args.Length < 3)
        {
            Say(reply, "Usage: /move x y z — x east, y north, z height");
            return;
        }

        if (!float.TryParse(args[0], out float x) || !float.TryParse(args[1], out float y) || !float.TryParse(args[2], out float z))
        {
            Say(reply, "Usage: /move x y z — x east, y north, z height");
            return;
        }

        if (!TryGetBody(session, reply, out var world, out var grid, out _)) return;

        // In the player's order — x east, y north, z height — which is the order C reads out.
        Vector3 targetPos = PlayerCoordinates.ToWorld(x, y, z);

        // COLLISION AWARE TELEPORT (Cylinder-based)
        if (OpenFPS.Server.Systems.MovementSystem.CheckCollision(world, grid, targetPos, PhysicsConstants.PlayerRadius, PhysicsConstants.PlayerHeight))
        {
            Say(reply, "Cannot move there: Area is solid.");
            return;
        }

        // Out of whatever you were sitting in first. The seat owns a passenger's position and puts
        // them back in it every tick, so a teleport from a seat moved you for one tick and no further.
        if (world.Has<OccupantComponent>(session.Entity)) CompositeService.Disembark(world, session.Entity);

        ref var t = ref world.Get<Transform>(session.Entity);
        t.Position = targetPos;
        t.IsDirty = true;

        // Force client reset
        reply(new PlayerSpawned { EntityId = session.Entity.Id, SpawnTransform = t });
        Say(reply, $"Moved to {PlayerCoordinates.Format(targetPos)}");
    }

    private void HandlePrivateMessage(UserSession session, string[] args, Action<IMessage> reply)
    {
        if (args.Length < 2)
        {
            Say(reply, "Usage: /pm [username] [message]");
            return;
        }

        string targetUsername = args[0];
        string message = string.Join(" ", args.Skip(1));

        var targetSession = _sessions.GetAllSessions().FirstOrDefault(s => s.Username.Equals(targetUsername, StringComparison.OrdinalIgnoreCase));
        if (targetSession == null)
        {
            Say(reply, $"User '{targetUsername}' not found.");
            return;
        }

        _server.SendToSession(targetSession, new ChatMessage
        {
            Sender = session.Username, Text = message, Channel = ChatChannel.Private,
            FromStaff = session.Role is UserRole.Admin or UserRole.Dev,
        });
        reply(new ChatMessage { Sender = session.Username, Text = message, Channel = ChatChannel.Private, To = targetSession.Username });
    }

    // ── People and places ───────────────────────────────────────────────────────────────────────

    /// <summary>The online session of a user, if they are on.</summary>
    private UserSession? OnlineSession(string username) =>
        _sessions.GetAllSessions().FirstOrDefault(s => s.Username.Equals(username, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A user's name as the server stores it, and their role, if they are registered. Falls back on
    /// who is online when there is no user store to ask (a test rig).
    /// </summary>
    private bool TryFindUser(string name, out string username, out UserRole role)
    {
        username = name; role = UserRole.Player;
        var record = _users?.GetUser(name);
        if (record != null) { username = record.Username; role = record.Role; return true; }
        var online = OnlineSession(name);
        if (online != null) { username = online.Username; role = online.Role; return true; }
        return false;
    }

    private static string RoleWord(UserRole role) => role switch
    {
        UserRole.Admin => "administrator",
        UserRole.Dev => "developer",
        _ => "player",
    };

    /// <summary>/friend add NAME, /friend remove NAME, /friend NAME (add), /unfriend NAME.</summary>
    private void HandleFriend(UserSession session, string commandName, string[] args, Action<IMessage> reply)
    {
        if (_friends == null) { Say(reply, "Friends are not available on this server."); return; }

        bool remove = commandName == "unfriend";
        string? name = null;
        if (args.Length >= 1)
        {
            string verb = args[0].ToLowerInvariant();
            if (!remove && verb is "add" or "remove" or "delete" or "rm")
            {
                remove = verb != "add";
                name = args.Length >= 2 ? args[1] : null;
            }
            else name = args[0];
        }
        if (string.IsNullOrWhiteSpace(name))
        {
            Say(reply, "Usage: /friend add [name], or /friend remove [name]. /friends lists them.");
            return;
        }

        if (remove)
        {
            // Removal does not need the account to exist any more: a deleted account is exactly the
            // friend somebody wants off their list.
            string shown = TryFindUser(name, out var stored, out _) ? stored : name;
            Say(reply, _friends.Remove(session.Username, shown)
                ? $"{shown} removed from your friends."
                : $"{shown} is not on your friends list.");
            return;
        }

        if (!TryFindUser(name, out var username, out _)) { Say(reply, $"There is no player called {name}."); return; }
        if (username.Equals(session.Username, StringComparison.OrdinalIgnoreCase))
        {
            Say(reply, "You cannot add yourself as a friend.");
            return;
        }
        Say(reply, _friends.Add(session.Username, username)
            ? $"{username} added to your friends."
            : $"{username} is already your friend.");
    }

    /// <summary>/profile NAME — what the server knows about somebody, in a sentence or two.</summary>
    private void HandleProfile(UserSession session, string[] args, Action<IMessage> reply)
    {
        if (args.Length < 1) { Say(reply, "Usage: /profile [name]"); return; }
        if (!TryFindUser(args[0], out var username, out var role)) { Say(reply, $"There is no player called {args[0]}."); return; }

        var target = OnlineSession(username);
        string friend = _friends != null && _friends.IsFriend(session.Username, username) ? " On your friends list." : "";
        string you = username.Equals(session.Username, StringComparison.OrdinalIgnoreCase) ? " (you)" : "";
        if (target != null) role = target.Role;
        string head = $"{username}{you}, {RoleWord(role)}.";

        if (target == null) { Say(reply, $"{head} Not online.{friend}"); return; }
        if (!target.CurrentMapId.Equals(session.CurrentMapId, StringComparison.OrdinalIgnoreCase))
        {
            Say(reply, $"{head} Online, on {target.CurrentMapId}.{friend}");
            return;
        }
        string here = you.Length > 0 ? "" : RelativeTo(session, target);
        Say(reply, $"{head} Online, here on {target.CurrentMapId}{(here.Length > 0 ? ", " + here : "")}.{friend}");
    }

    /// <summary>/where NAME — which way and how far, if they are on your map; which map otherwise.</summary>
    private void HandleWhere(UserSession session, string[] args, Action<IMessage> reply)
    {
        if (args.Length < 1) { Say(reply, "Usage: /where [name]"); return; }
        var target = OnlineSession(args[0]);
        if (target == null)
        {
            Say(reply, TryFindUser(args[0], out var stored, out _) ? $"{stored} is not online." : $"There is no player called {args[0]}.");
            return;
        }
        if (target.ConnectionId == session.ConnectionId)
        {
            string place = (_maps.TryGetMap(session.CurrentMapId, out var w, out _, out _, out _)
                            && session.Entity != Entity.Null && w.IsAlive(session.Entity)
                ? PlaceAt(w, w.Get<Transform>(session.Entity).Position) : null) ?? "";
            Say(reply, $"You are on {session.CurrentMapId}{(place.Length > 0 ? ", at " + place : "")}.");
            return;
        }
        if (!target.CurrentMapId.Equals(session.CurrentMapId, StringComparison.OrdinalIgnoreCase))
        {
            Say(reply, $"{target.Username} is on the map {target.CurrentMapId}.");
            return;
        }
        string relative = RelativeTo(session, target);
        Say(reply, relative.Length == 0
            ? $"{target.Username} is on this map, but not in the world yet."
            : $"{target.Username} is {relative}.");
    }

    /// <summary>
    /// "25 metres away at 11 o'clock, 4 metres above you, at Main Street east pavement" — the other
    /// player's bearing from the asker's facing, from the same clock face /scan uses. Empty if
    /// either of them has no body yet.
    /// </summary>
    private string RelativeTo(UserSession asker, UserSession target)
    {
        if (!_maps.TryGetMap(asker.CurrentMapId, out var world, out _, out _, out _)) return "";
        if (asker.Entity == Entity.Null || target.Entity == Entity.Null) return "";
        if (!world.IsAlive(asker.Entity) || !world.IsAlive(target.Entity)) return "";

        var me = world.Get<Transform>(asker.Entity);
        var them = world.Get<Transform>(target.Entity).Position;
        var offset = them - me.Position;
        var flat = new Vector3(offset.X, 0, offset.Z);
        float distance = flat.Length();

        string where = distance < 1.5f
            ? "right beside you"
            : $"{MathF.Round(distance):0} metres away at {GetRelativeDirection(me.Rotation, Vector3.Normalize(flat))}";
        if (MathF.Abs(offset.Y) > 2.5f)
            where += $", {MathF.Abs(offset.Y):0} metres {(offset.Y > 0 ? "above" : "below")} you";
        if (PlaceAt(world, them) is { Length: > 0 } place) where += $", at {place}";
        return where;
    }

    /// <summary>
    /// The name of the place a point is in: the smallest named region volume that contains it, the
    /// same boxes the client names places from. Null where nowhere is named.
    /// </summary>
    public static string? PlaceAt(World world, Vector3 point)
    {
        string? best = null;
        float bestVolume = float.MaxValue;
        world.Query(new QueryDescription().WithAll<Transform, RegionComponent>(), (ref Transform t, ref RegionComponent r) =>
        {
            var size = r.RoomSize;
            if (size.X <= 0 || size.Y <= 0 || size.Z <= 0 || string.IsNullOrWhiteSpace(r.FriendlyName)) return;
            var local = Vector3.Transform(point - t.Position, Quaternion.Inverse(t.Rotation));
            if (MathF.Abs(local.X) > size.X / 2 || MathF.Abs(local.Y) > size.Y / 2 || MathF.Abs(local.Z) > size.Z / 2) return;
            float volume = size.X * size.Y * size.Z;
            if (volume >= bestVolume) return;
            bestVolume = volume;
            best = r.FriendlyName;
        });
        return best;
    }

    /// <summary>/join MAP — go to another loaded map, if it is public, yours, or you are staff.</summary>
    private void HandleJoin(UserSession session, string[] args, Action<IMessage> reply)
    {
        var enterable = _maps.LoadedMapIds.Where(id => OpenFPS.Server.Services.DiscoveryService.CanEnter(_maps, id, session))
                                          .OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList();
        if (args.Length < 1) { Say(reply, $"Usage: /join [map]. Maps: {string.Join(", ", enterable)}."); return; }

        string? mapId = _maps.LoadedMapIds.FirstOrDefault(id => id.Equals(args[0], StringComparison.OrdinalIgnoreCase));
        if (mapId == null)
        {
            Say(reply, $"There is no map called {args[0]}. Maps: {string.Join(", ", enterable)}.");
            return;
        }
        if (!OpenFPS.Server.Services.DiscoveryService.CanEnter(_maps, mapId, session))
        {
            Say(reply, $"{mapId} is private.");
            return;
        }
        if (mapId.Equals(session.CurrentMapId, StringComparison.OrdinalIgnoreCase) && session.Entity != Entity.Null)
        {
            Say(reply, $"You are already on {mapId}.");
            return;
        }
        _server.MoveToMap(session, mapId, reply);
    }

    private static string GetRelativeDirection(Quaternion rotation, Vector3 targetDir)
    {
        Matrix4x4 rotMat = Matrix4x4.CreateFromQuaternion(Quaternion.Inverse(rotation));
        Vector3 localDir = Vector3.Transform(targetDir, rotMat);
        float angle = MathF.Atan2(localDir.X, localDir.Z) * (180.0f / MathF.PI);
        if (angle < 0) angle += 360.0f;
        return angle switch
        {
            < 22.5f or >= 337.5f => "12 o'clock", < 67.5f => "2 o'clock", < 112.5f => "3 o'clock",
            < 157.5f => "5 o'clock", < 202.5f => "6 o'clock", < 247.5f => "8 o'clock",
            < 292.5f => "9 o'clock", _ => "11 o'clock"
        };
    }
}

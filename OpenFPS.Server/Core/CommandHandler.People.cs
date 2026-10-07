using System.Numerics;
using OpenFPS.Common.Networking;
using OpenFPS.Common.Components;
using Arch.Core;

namespace OpenFPS.Server.Core;

/// <summary>Players: private messages, friends, profiles and /where.</summary>
public partial class CommandHandler
{
    private void HandlePrivateMessage(UserSession session, string[] args, Action<IMessage> reply)
    {
        if (args.Length < 2)
        {
            Say(reply, "Usage: /pm [username] [message]");
            return;
        }

        if (session.MutedUntilUtc > DateTime.UtcNow) { Say(reply, "You are muted."); return; }
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
            FromStaff = session.Role is UserRole.Admin or UserRole.Dev or UserRole.Moderator,
        });
        reply(new ChatMessage { Sender = session.Username, Text = message, Channel = ChatChannel.Private, To = targetSession.Username });
    }

    /// <summary>
    /// An online player by name: the exact name, or else the one player whose name starts with what was
    /// typed ("/bring se" finds sean). Never a guess between two.
    /// </summary>
    private UserSession? OnlineSession(string username)
    {
        var all = _sessions.GetAllSessions().ToList();
        var exact = all.FirstOrDefault(s => s.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
        if (exact != null || username.Length < 2) return exact;
        var starts = all.Where(s => s.Username.StartsWith(username, StringComparison.OrdinalIgnoreCase)).ToList();
        return starts.Count == 1 ? starts[0] : null;
    }

    /// <summary>
    /// A registered user's stored name and role; from who is online when there is no user store (a test rig).
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
        UserRole.Moderator => "moderator",
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
            // No account needed: a deleted account is exactly the friend somebody wants off their list.
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

    /// <summary>
    /// /profile NAME: rank, real name, whether they are on, and which map, never where on it (/where is
    /// staff's). A private map the asker could not enter is not named.
    /// </summary>
    private void HandleProfile(UserSession session, string[] args, Action<IMessage> reply)
    {
        if (args.Length < 1) { Say(reply, "Usage: /profile [name]"); return; }
        if (!TryFindUser(args[0], out var username, out var role)) { Say(reply, $"There is no player called {args[0]}."); return; }

        var target = OnlineSession(username);
        string friend = _friends != null && _friends.IsFriend(session.Username, username) ? " On your friends list." : "";
        string you = username.Equals(session.Username, StringComparison.OrdinalIgnoreCase) ? " (you)" : "";
        if (target != null) role = target.Role;
        string realName = _users?.GetUser(username)?.RealName is { Length: > 0 } rn ? $" Real name {rn}." : "";
        string head = $"{username}{you}, {RoleWord(role)}.{realName}";

        if (target == null) { Say(reply, $"{head} Not online.{friend}"); return; }

        string status = target.Status(DateTime.UtcNow);
        status = char.ToUpperInvariant(status[0]) + status[1..];
        string map = target.CurrentMapId.Equals(session.CurrentMapId, StringComparison.OrdinalIgnoreCase)
            ? $"here on {target.CurrentMapId}"
            : OpenFPS.Server.Services.DiscoveryService.CanEnter(_maps, target.CurrentMapId, session)
                ? $"on {target.CurrentMapId}"
                : "on a private map";
        Say(reply, $"{head} {status}, {map}.{friend}");
    }

    /// <summary>/realname [name|clear]: the name your profile shows; on its own, what is set.</summary>
    private void HandleRealName(UserSession session, string[] args, Action<IMessage> reply)
    {
        if (_users == null) { Say(reply, "Profiles are not kept on this server."); return; }
        if (args.Length == 0)
        {
            string? current = _users.GetUser(session.Username)?.RealName;
            Say(reply, string.IsNullOrEmpty(current)
                ? "Your profile shows no real name. Set one with /realname followed by the name."
                : $"Your profile shows the real name {current}. /realname clear takes it off.");
            return;
        }

        bool clear = args.Length == 1 && args[0].Equals("clear", StringComparison.OrdinalIgnoreCase);
        string name = clear ? "" : string.Join(" ", args).Trim();
        if (name.Length > 64 || name.Any(char.IsControl))
        {
            Say(reply, "A real name can be at most 64 characters, with no control characters.");
            return;
        }
        if (!_users.SetRealName(session.Username, clear ? null : name))
        {
            Say(reply, "Profiles are not kept on this server.");
            return;
        }
        Say(reply, clear ? "Your profile no longer shows a real name." : $"Your profile now shows the real name {name}.");
    }

    /// <summary>/where NAME: which way and how far if they are on your map, otherwise which map.</summary>
    private void HandleWhere(UserSession session, string[] args, Action<IMessage> reply)
    {
        if (args.Length < 1) { Say(reply, "Usage: /where [name]"); return; }
        var target = OnlineSession(args[0]);
        // A character, not a player: Alex.
        if (target == null && _server.Characters.Find(args[0]) is { } character)
        {
            WhereCharacter(session, character.MapId, character.Body, reply);
            return;
        }
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
            Say(reply, $"You are on {_maps.DisplayName(session.CurrentMapId)}{(place.Length > 0 ? ", at " + place : "")}.");
            return;
        }
        if (!target.CurrentMapId.Equals(session.CurrentMapId, StringComparison.OrdinalIgnoreCase))
        {
            Say(reply, $"{target.Username} is on the map {_maps.DisplayName(target.CurrentMapId)}.");
            return;
        }
        string relative = RelativeTo(session, target);
        Say(reply, relative.Length == 0
            ? $"{target.Username} is on this map, but not in the world yet."
            : $"{target.Username} is {relative}.");
    }

    /// <summary>/where for a character: where they are from you, and what they are doing.</summary>
    private void WhereCharacter(UserSession session, string mapId, Entity body, Action<IMessage> reply)
    {
        if (!_maps.TryGetMap(mapId, out var world, out _, out _, out _) || !world.IsAlive(body)) return;
        string name = world.Has<IdentityComponent>(body) ? world.Get<IdentityComponent>(body).Name : "They";
        var view = _server.Characters.ViewOf(mapId, body.Id);
        string doing = "";
        if (view is { } v && v.Place.Length > 0)
            doing = v.Doing == OpenFPS.Server.Systems.CharacterSystem.Doing.Lingering
                ? $" He is at {v.Place}, for about {Math.Max(1, (int)Math.Round(v.Until / 60))} more minutes."
                : v.Doing == OpenFPS.Server.Systems.CharacterSystem.Doing.WaitingForBus
                    ? $" He is at {v.Place}."
                    : $" He is {v.Place}.";
        if (!mapId.Equals(session.CurrentMapId, StringComparison.OrdinalIgnoreCase))
        {
            Say(reply, $"{name} is on the map {_maps.DisplayName(mapId)}.{doing}");
            return;
        }
        var them = world.Get<Transform>(body).Position;
        string where = "";
        if (session.Entity != Entity.Null && world.IsAlive(session.Entity))
        {
            var me = world.Get<Transform>(session.Entity);
            var offset = them - me.Position;
            var flat = new Vector3(offset.X, 0, offset.Z);
            float distance = flat.Length();
            where = distance < 1.5f ? "right beside you"
                  : $"{MathF.Round(distance):0} metres away, {GetRelativeDirection(me.Rotation, Vector3.Normalize(flat))}";
        }
        if (PlaceAt(world, them) is { Length: > 0 } place) where += (where.Length > 0 ? ", at " : "at ") + place;
        // x east, y north, z height: what /tp and /move take.
        Say(reply, $"{name} is {where}, {them.X:F0} {them.Z:F0} {them.Y:F0}.{doing}");
    }

    /// <summary>
    /// "25 metres away, left in front, 4 metres above you, at Main Street sidewalk": the other player
    /// from the asker's facing, in the words /scan uses. Empty if either has no body yet.
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
            : $"{MathF.Round(distance):0} metres away, {GetRelativeDirection(me.Rotation, Vector3.Normalize(flat))}";
        if (MathF.Abs(offset.Y) > 2.5f)
            where += $", {MathF.Abs(offset.Y):0} metres {(offset.Y > 0 ? "above" : "below")} you";
        if (PlaceAt(world, them) is { Length: > 0 } place) where += $", at {place}";
        return where;
    }

    /// <summary>
    /// The name of the place a point is in: the named part of a room holding it (a flight of stairs, a
    /// landing), else the smallest named region holding it, as the client names places. Null if none.
    /// </summary>
    public static string? PlaceAt(World world, Vector3 point)
    {
        static bool Holds(Vector3 point, in Transform t, Vector3 size)
        {
            if (size.X <= 0 || size.Y <= 0 || size.Z <= 0) return false;
            var local = Vector3.Transform(point - t.Position, Quaternion.Inverse(t.Rotation));
            return MathF.Abs(local.X) <= size.X / 2 && MathF.Abs(local.Y) <= size.Y / 2 && MathF.Abs(local.Z) <= size.Z / 2;
        }

        string? part = null;
        float partVolume = float.MaxValue;
        world.Query(new QueryDescription().WithAll<Transform, IdentityComponent, ColliderComponent>(),
            (ref Transform t, ref IdentityComponent id, ref ColliderComponent c) =>
            {
                if (id.PrefabId != NamedPlacePrefab || c.IsSolid || string.IsNullOrWhiteSpace(id.Name)) return;
                var size = c.Size;
                if (!Holds(point, t, size) || size.X * size.Y * size.Z >= partVolume) return;
                partVolume = size.X * size.Y * size.Z;
                part = id.Name;
            });
        if (part != null) return part;

        string? best = null;
        float bestVolume = float.MaxValue;
        world.Query(new QueryDescription().WithAll<Transform, RegionComponent>(), (ref Transform t, ref RegionComponent r) =>
        {
            var size = r.RoomSize;
            if (string.IsNullOrWhiteSpace(r.FriendlyName) || !Holds(point, t, size)) return;
            float volume = size.X * size.Y * size.Z;
            if (volume >= bestVolume) return;
            bestVolume = volume;
            best = r.FriendlyName;
        });
        return best;
    }

    /// <summary>The prefab of a named part of a room, a name and not a room (prefabs/named_place.json;
    /// the client's NamedPlaces).</summary>
    public const string NamedPlacePrefab = "named_place";

    /// <summary>Whether an entity is a named part of a room: a place you are in, not a thing near you.</summary>
    private static bool IsNamedPlace(World world, Entity e)
        => world.Has<IdentityComponent>(e) && world.Get<IdentityComponent>(e).PrefabId == NamedPlacePrefab;
}

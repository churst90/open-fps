using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Services;
using OpenFPS.Server.Systems;

namespace OpenFPS.Server.Core;

/// <summary>
/// /tp: the teleporter's command. It needs a teleporter in your hands or on your back (an administrator
/// needs none: tp-free); with one it takes you to a player, a named place, a map, or a point, as often as
/// you like. Going to a player or a place on another map follows the same rule as /join. See
/// <see cref="Teleporter"/> for the charge and the three sound events.
/// </summary>
public partial class CommandHandler
{
    /// <summary>Exactly what somebody without one is told.</summary>
    public const string NoTeleporter = "You don't have a teleporter.";

    private void HandleTeleport(UserSession session, string[] args, Action<IMessage> reply)
    {
        if (!session.Can(Permissions.TeleportFree) && !CarriesTeleporter(session)) { Say(reply, NoTeleporter); return; }
        if (args.Length == 0)
        {
            Say(reply, "Usage: /tp PLACE, /tp PLAYER, /tp MAP, or /tp x y z (x east, y north, z height).");
            return;
        }
        if (!TryGetBody(session, reply, out var world, out var grid, out var here)) return;

        Func<(string Map, Vector3 At)?> where;
        string arrived;
        if (args.Length >= 3 && float.TryParse(args[0], out float x) && float.TryParse(args[1], out float y) && float.TryParse(args[2], out float z))
        {
            string map = session.CurrentMapId;
            var to = PlayerCoordinates.ToWorld(x, y, z);
            if (MovementSystem.CheckCollision(world, grid, to, PhysicsConstants.PlayerRadius, PhysicsConstants.PlayerHeight))
            { Say(reply, "Cannot teleport there: it is solid."); return; }
            where = () => (map, to);
            arrived = $"You are at {PlayerCoordinates.Format(to)}.";
        }
        else
        {
            string name = string.Join(" ", args);
            if (OnlineSession(name) is { } other)
            {
                if (other == session) { Say(reply, "You are already where you are."); return; }
                if (!DiscoveryService.CanEnter(_maps, other.CurrentMapId, session))
                { Say(reply, $"{other.Username} is on a map you cannot enter."); return; }
                where = () => SpotBeside(other);
                arrived = $"You are beside {other.Username}.";
            }
            else if (FindPlace(session, name) is { } place)
            {
                where = () => (place.Map, place.At);
                arrived = place.Map.Equals(session.CurrentMapId, StringComparison.OrdinalIgnoreCase)
                    ? $"You are at {place.Name}." : $"You are at {place.Name}, on {place.Map}.";
            }
            else if (_maps.LoadedMapIds.FirstOrDefault(m => m.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } mapId
                     && DiscoveryService.CanEnter(_maps, mapId, session))
            {
                if (mapId.Equals(session.CurrentMapId, StringComparison.OrdinalIgnoreCase)) { Say(reply, $"You are already on {mapId}."); return; }
                var spawn = _maps.GetSpawnPoint(mapId).Position;
                where = () => (mapId, spawn);
                arrived = $"You are on {mapId}.";
            }
            else
            {
                Say(reply, $"There is no player, place or map called {name} that you can go to.");
                return;
            }
        }
        if (where() == null) { Say(reply, "There is no room to arrive there."); return; }

        // The charge, where you stand and on you, and the jump after it. Nothing cancels it.
        _server.EmitWorldAudio(session.CurrentMapId, session.Entity.Id, Teleporter.Charge, Teleporter.Sound(Teleporter.Charge, here));
        Say(reply, "The teleporter charges.");
        _server.After(TimeSpan.FromSeconds(Teleporter.ChargeSeconds), () => Jump(session, where, arrived, reply));
    }

    /// <summary>The move itself, when the charge is done: heard leaving, then arriving.</summary>
    private void Jump(UserSession session, Func<(string Map, Vector3 At)?> where, string arrived, Action<IMessage> reply)
    {
        if (!_sessions.TryGetSession(session.ConnectionId, out var still) || still != session) return;
        if (!_maps.TryGetMap(session.CurrentMapId, out var world, out _, out _, out _)
            || session.Entity == Entity.Null || !world.IsAlive(session.Entity)) return;
        if (where() is not { } to) { Say(reply, "The teleporter found no room to arrive there."); return; }

        string fromMap = session.CurrentMapId;
        var from = world.Get<Transform>(session.Entity).Position;
        _server.EmitWorldAudio(fromMap, -1, Teleporter.Leave, Teleporter.Sound(Teleporter.Leave, from));
        if (to.Map.Equals(fromMap, StringComparison.OrdinalIgnoreCase))
        {
            Teleport(session, world, to.At);
            _server.EmitWorldAudio(fromMap, session.Entity.Id, Teleporter.Arrive, Teleporter.Sound(Teleporter.Arrive, to.At));
            Say(reply, arrived);
            return;
        }
        // Another map: the body there is made where it is to arrive, and is heard arriving as it is.
        session.ArriveAt = (to.Map, to.At, true);
        Say(reply, arrived);
        _server.MoveToMap(session, to.Map, reply);
    }

    /// <summary>Whether a teleporter is in this player's hands or on their back.</summary>
    private bool CarriesTeleporter(UserSession session)
    {
        if (!_maps.TryGetMap(session.CurrentMapId, out var world, out _, out _, out var lookup)
            || session.Entity == Entity.Null || !world.IsAlive(session.Entity)) return false;
        bool IsOne(Entity? e) => e is { } item && world.IsAlive(item) && world.Has<IdentityComponent>(item)
                                 && world.Get<IdentityComponent>(item).PrefabId == Teleporter.PrefabId;
        var (right, left) = HandsService.Holding(world, session.Entity, lookup);
        if (IsOne(right) || IsOne(left)) return true;
        return HandsService.Stowed(world, session.Entity, lookup).Any(e => IsOne(e));
    }

    /// <summary>A clear spot beside a player, on their map, worked out now: they may have moved.</summary>
    private (string Map, Vector3 At)? SpotBeside(UserSession target)
    {
        if (!_maps.TryGetMap(target.CurrentMapId, out var world, out _, out var grid, out _)
            || target.Entity == Entity.Null || !world.IsAlive(target.Entity)) return null;
        var t = world.Get<Transform>(target.Entity);
        return FreeSpotNear(world, grid, t.Position, t.Rotation, 1.2f) is { } spot ? (target.CurrentMapId, spot) : null;
    }

    /// <summary>A spot clear for a standing body about <paramref name="distance"/> from a point, tried in
    /// front first and then round it an eighth of a turn at a time.</summary>
    private static Vector3? FreeSpotNear(World world, SpatialGrid<Entity> grid, Vector3 centre, Quaternion facing, float distance)
    {
        Vector3 forward = Vector3.Transform(Vector3.UnitZ, facing);
        forward.Y = 0f;
        forward = forward.LengthSquared() > 1e-6f ? Vector3.Normalize(forward) : Vector3.UnitZ;
        for (int k = 0; k < 8; k++)
        {
            var dir = Vector3.Transform(forward, Quaternion.CreateFromAxisAngle(Vector3.UnitY, k * MathF.PI / 4f));
            var p = centre + dir * distance;
            if (!MovementSystem.CheckCollision(world, grid, p, PhysicsConstants.PlayerRadius, PhysicsConstants.PlayerHeight))
                return p;
        }
        return null;
    }

    /// <summary>
    /// A named place a player can go to: a region or a named part of a room, by its name, on the map
    /// they are on and then on the others they may enter. The exact name first, then one that starts
    /// with what was typed, then one that contains it; the smallest such place on a map. Where to stand
    /// is its middle, on the floor, or the nearest clear spot to it.
    /// </summary>
    private (string Map, Vector3 At, string Name)? FindPlace(UserSession session, string typed)
    {
        var maps = new List<string> { session.CurrentMapId };
        maps.AddRange(_maps.LoadedMapIds.Where(m => !m.Equals(session.CurrentMapId, StringComparison.OrdinalIgnoreCase)
                                                 && DiscoveryService.CanEnter(_maps, m, session)));
        foreach (var mapId in maps)
        {
            if (!_maps.TryGetMap(mapId, out var world, out _, out var grid, out _)) continue;
            var places = new List<(string Name, Vector3 Centre, Vector3 Size)>();
            world.Query(new QueryDescription().WithAll<Transform, RegionComponent>(), (ref Transform t, ref RegionComponent r) =>
            {
                if (!string.IsNullOrWhiteSpace(r.FriendlyName)) places.Add((r.FriendlyName, t.Position, r.RoomSize));
            });
            world.Query(new QueryDescription().WithAll<Transform, IdentityComponent, ColliderComponent>(),
                (ref Transform t, ref IdentityComponent id, ref ColliderComponent c) =>
                {
                    if (id.PrefabId == NamedPlacePrefab && !string.IsNullOrWhiteSpace(id.Name)) places.Add((id.Name, t.Position, c.Size));
                });
            foreach (var match in new Func<string, bool>[]
            {
                n => n.Equals(typed, StringComparison.OrdinalIgnoreCase),
                n => n.StartsWith(typed, StringComparison.OrdinalIgnoreCase),
                n => n.Contains(typed, StringComparison.OrdinalIgnoreCase),
            })
            {
                foreach (var p in places.Where(p => match(p.Name)).OrderBy(p => p.Size.X * p.Size.Y * p.Size.Z))
                    if (StandingSpot(world, grid, p.Centre, p.Size) is { } at) return (mapId, at, p.Name);
            }
        }
        return null;
    }

    /// <summary>Where to stand in a place: its middle, on whatever floor is there, or near it.</summary>
    private static Vector3? StandingSpot(World world, SpatialGrid<Entity> grid, Vector3 centre, Vector3 size)
    {
        float bottom = centre.Y - MathF.Max(0f, size.Y) * 0.5f;
        var probe = new Vector3(centre.X, MathF.Min(bottom + 1.5f, centre.Y + MathF.Max(0f, size.Y) * 0.5f), centre.Z);
        float ground = PhysicsUtils.GetGroundHeight(world, grid, probe, out _);
        if (ground < -500f) return null;
        var feet = new Vector3(centre.X, ground + 0.05f, centre.Z);
        if (!MovementSystem.CheckCollision(world, grid, feet, PhysicsConstants.PlayerRadius, PhysicsConstants.PlayerHeight)) return feet;
        return FreeSpotNear(world, grid, feet, Quaternion.Identity, 1.5f);
    }
}

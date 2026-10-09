using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Server.Repositories;
using OpenFPS.Server.Systems;
using Serilog;

namespace OpenFPS.Server.Core;

/// <summary>
/// A player's body leaves the world when they log out, lose their connection, are kicked or change map,
/// and what it was doing is kept for when it comes back: where it stood and faced on that map, how hurt
/// it was, and what it was carrying (<see cref="PlayerState"/>, <see cref="Belongings"/>). Both go into
/// the accounts database in one write, on the tick thread, at the moment the body goes.
///
/// The things carried are taken out of the world when they are stored and made again when they are
/// taken back, and the store gives them up as it hands them over (<see cref="IUserRepository.TakeBelongings"/>).
/// So a thing is in exactly one place: on a body, on the ground, or in the store. A rifle put down
/// before logging out is on the ground and not in the store, and cannot come back with its old owner.
/// The cost is that a server that dies without shutting down loses what was being carried at that
/// moment, the way it loses everything else in the world; a clean shutdown stores everybody first.
///
/// If the store cannot keep them (no table for it, or a failed write), they are put down where the body stood.
/// </summary>
public sealed class PlayerStore
{
    private readonly IUserRepository _users;
    private readonly MapManager _maps;
    private readonly HandsService _hands;

    /// <summary>How far above whatever it stands on a remembered place may be and still count as
    /// standing on it, metres. More than the metre a map's spawn is stood above its floor
    /// (MapManager.VerifySpawnPoint), and less than a storey.</summary>
    public const float StandingTolerance = 1.5f;

    public PlayerStore(IUserRepository users, MapManager maps, HandsService hands)
    {
        _users = users;
        _maps = maps;
        _hands = hands;
    }

    /// <summary>What the store has for an account: a new record for one it has nothing for.</summary>
    public PlayerState Load(string username)
    {
        try { return PlayerState.Parse(_users.GetUser(username)?.PlayerState, username); }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not read the saved state for {User}.", username);
            return new PlayerState();
        }
    }

    /// <summary>
    /// The map a player arriving lands on: the one they left from, if it is still loaded and they may
    /// still enter it; otherwise the landing map.
    /// </summary>
    public string LandingMap(UserSession session, PlayerState state, string landing)
    {
        if (string.IsNullOrEmpty(state.Map)) return landing;
        // Not into a frame of the world: its tiles may have been let go since (keeping the world position
        // and arriving there again is stage 3 of docs/WORLD_STREAMING.md).
        if (OpenFPS.Server.OneWorld.WorldMaps.IsWorldMap(state.Map)) return landing;
        foreach (string id in _maps.LoadedMapIds)
            if (id.Equals(state.Map, StringComparison.OrdinalIgnoreCase))
                return Services.DiscoveryService.CanEnter(_maps, id, session) ? id : landing;
        return landing;
    }

    /// <summary>
    /// Where a body arriving on a map stands and which way it faces: the place it left from on that map
    /// if that place is still somewhere a person can stand (<see cref="IsSafe"/>), the map's spawn
    /// otherwise. <paramref name="remembered"/> says which.
    /// </summary>
    public (Transform At, float Yaw) Arrival(string mapId, PlayerState? state, out bool remembered)
    {
        remembered = false;
        var spawn = _maps.GetSpawnPoint(mapId);
        if (state?.PlaceOn(mapId) is not { } place) return (spawn, YawOf(spawn.Rotation));
        if (!_maps.TryGetMap(mapId, out var world, out _, out var grid, out _)
            || !_maps.TryGetMapData(mapId, out var data)
            || !IsSafe(world, grid, data, new Vector3(place.X, place.Y, place.Z), out var standing))
            return (spawn, YawOf(spawn.Rotation));
        remembered = true;
        return (new Transform
        {
            Position = standing,
            Rotation = Quaternion.CreateFromYawPitchRoll(place.Yaw, 0f, 0f),
            Scale = spawn.Scale,
        }, place.Yaw);
    }

    /// <summary>
    /// Whether a remembered place is still somewhere to stand: inside the map's walkable bounds, with a
    /// floor under it no more than <see cref="StandingTolerance"/> below, and room for a person on that
    /// floor. The world changes while a player is away — a door, a parked car, a map edited — so the
    /// place is checked against the world as it is now. <paramref name="standing"/> is the place put
    /// down onto that floor.
    /// </summary>
    public static bool IsSafe(World world, SpatialGrid<Entity> grid, MapData map, Vector3 place, out Vector3 standing)
    {
        standing = place;
        if (!float.IsFinite(place.X) || !float.IsFinite(place.Y) || !float.IsFinite(place.Z)) return false;
        Vector3 min = map.WalkMin, max = map.WalkMax;
        if (place.X < min.X || place.X > max.X || place.Z < min.Z || place.Z > max.Z) return false;
        if (place.Y < map.MinimumY || place.Y > max.Y) return false;

        float floor = PhysicsUtils.GetGroundHeight(world, grid, place, out _);
        if (floor < -900f || place.Y - floor > StandingTolerance) return false;
        standing = new Vector3(place.X, floor, place.Z);
        return !MovementSystem.CheckCollision(world, grid, standing, PhysicsConstants.PlayerRadius, PhysicsConstants.PlayerHeight);
    }

    /// <summary>
    /// A body leaving the world from <paramref name="mapId"/>: its place, health and things written to
    /// the store, and the things taken off it and out of the world once they are. Call it after the
    /// body is out of any seat, so the place kept is where getting out put it. Returns the ids of the
    /// things destroyed (for the clients to be told); none if the store would not keep them, in which
    /// case they are still on the body and the caller puts them down.
    /// </summary>
    public List<int> Leave(UserSession session, string mapId, World world, Entity body, Dictionary<int, Entity> lookup)
    {
        var state = session.Saved ?? Load(session.Username);
        state.Map = mapId;

        bool dead = world.Has<DeadComponent>(body);
        state.DeadUntilUtc = null;
        if (dead)
        {
            // Somebody who leaves dead comes back at the spawn, where the respawn would have stood them
            // up, and still dead until the wait is over: leaving does not skip it.
            state.SetPlace(mapId, null);
            state.Health = state.MaxHealth = null;
            double left = world.Get<DeadComponent>(body).DiedAt + CombatService.PlayerRespawnSeconds - AudioClock.Now;
            if (left > 0) state.DeadUntilUtc = DateTime.UtcNow.AddSeconds(Math.Min(left, CombatService.PlayerRespawnSeconds));
        }
        else
        {
            var t = world.Get<Transform>(body);
            float yaw = world.Has<PlayerComponent>(body) ? world.Get<PlayerComponent>(body).Yaw : YawOf(t.Rotation);
            state.SetPlace(mapId, new SavedPlace { X = t.Position.X, Y = t.Position.Y, Z = t.Position.Z, Yaw = yaw });
            if (world.Has<HealthComponent>(body))
            {
                var h = world.Get<HealthComponent>(body);
                state.Health = h.Current;
                state.MaxHealth = h.Max;
            }
        }
        session.Saved = state;

        var kept = _hands.Pack(world, body, lookup, out var packed);
        bool stored;
        try { stored = _users.SavePlayer(session.Username, state.ToJson(), kept.IsEmpty ? null : kept.ToJson()); }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not store {User} on leaving; their things are put down instead.", session.Username);
            stored = false;
        }
        if (!stored) return new List<int>();

        var gone = _hands.Release(mapId, world, body, packed);
        if (!kept.IsEmpty)
            Log.Information("{User} left {Map} carrying {Count} thing(s) and {Spares} spare round(s); kept for their return.",
                            session.Username, mapId, kept.Items.Count, Sum(kept.Spares));
        return gone;
    }

    /// <summary>
    /// A body just put into the world: its health as it was, dead still if it left dead and the wait is
    /// not over, and what it was carrying given back, taken out of the store as it is. Returns how many
    /// things came back.
    /// </summary>
    public int Arrive(UserSession session, string mapId, World world, Entity body)
    {
        var state = session.Saved;
        if (state?.Health is int health && world.Has<HealthComponent>(body))
        {
            ref var h = ref world.Get<HealthComponent>(body);
            if (state.MaxHealth is int max && max > 0) h.Max = max;
            h.Current = Math.Clamp(health, 1, h.Max);
        }
        if (state?.DeadUntilUtc is DateTime until && until > DateTime.UtcNow)
        {
            double left = Math.Min((until - DateTime.UtcNow).TotalSeconds, CombatService.PlayerRespawnSeconds);
            world.Add(body, new DeadComponent { DiedAt = AudioClock.Now - (CombatService.PlayerRespawnSeconds - left) });
            if (world.Has<HealthComponent>(body)) world.Get<HealthComponent>(body).Current = 0;
        }

        string? json;
        try { json = _users.TakeBelongings(session.Username); }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not take back what {User} was carrying; it stays in the store.", session.Username);
            return 0;
        }
        if (Belongings.Parse(json, session.Username) is not { } kept) return 0;
        int made = _hands.Unpack(mapId, world, body, kept, session.Username);
        Log.Information("{User} came back to {Map} with {Count} thing(s) and {Spares} spare round(s).",
                        session.Username, mapId, made, Sum(kept.Spares));
        return made;
    }

    private static int Sum(Dictionary<string, int> spares)
    {
        int n = 0;
        foreach (var v in spares.Values) n += v;
        return n;
    }

    /// <summary>The yaw a rotation faces, in the body's own sense (CreateFromYawPitchRoll).</summary>
    private static float YawOf(Quaternion q)
    {
        var forward = Vector3.Transform(Vector3.UnitZ, q);
        return MathF.Atan2(forward.X, forward.Z);
    }
}

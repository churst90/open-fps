using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Server.Systems;
using Serilog;

namespace OpenFPS.Server.Core;

/// <summary>
/// Getting into a composite's seats and out again. A chair and a driver's seat differ only in
/// <see cref="Seat.Controls"/>. While seated the seat owns where you are (<see cref="OccupancySystem"/>
/// puts you there after the root has moved); where you look stays yours.
/// </summary>
public class OccupancyService
{
    private readonly MapManager _maps;

    /// <summary>How close you must be to a seat to get into it, metres. Arm's length plus a step.</summary>
    public const float BoardingRange = 5.0f;

    private readonly Action<string, int, string, IReadOnlyList<TransientSound>>? _heard;

    /// <param name="heard">Where the sounds of getting in and out go (world audio); null in tests.</param>
    public OccupancyService(MapManager maps, Action<string, int, string, IReadOnlyList<TransientSound>>? heard = null)
    {
        _maps = maps;
        _heard = heard;
    }

    /// <summary>A car door is about a metre square of steel skin on a frame, and weighs about this.</summary>
    private const float CarDoorKg = 22f;

    /// <summary>
    /// The door beside a seat, opened and shut 1.3 s later: getting in or out of a car. Only for things
    /// that drive; a bus's doors are air.
    /// </summary>
    private void CarDoor(string mapId, World world, Entity root, Seat seat)
    {
        if (_heard == null || !world.Has<DriveComponent>(root)) return;
        var rootT = world.Get<Transform>(root);
        float side = seat.LocalPosition.X < 0f ? -1f : 1f;
        var right = Vector3.Transform(Vector3.UnitX, rootT.Rotation);
        var forward = Vector3.Transform(Vector3.UnitZ, rootT.Rotation);
        var seatPos = SeatPosition(rootT, seat);
        var centre = seatPos + right * side * 0.75f + new Vector3(0f, 0.55f, 0f);
        _heard(mapId, root.Id, "car door", CarDoorSounds(centre, forward, 1.3f));
    }

    /// <summary>
    /// A car door opening and, <paramref name="closeAfter"/> seconds later, shutting. <paramref name="centre"/>
    /// is the middle of the door at hip height; <paramref name="forward"/> is the way the car points,
    /// where the hinge is. Used by players and by parking drivers.
    /// </summary>
    internal static List<TransientSound> CarDoorSounds(Vector3 centre, Vector3 forward, float closeAfter)
    {
        var hinge = centre + forward * 0.5f;
        var latch = centre - forward * 0.5f;
        var steel = AcousticRegistry.GetProperties("Metal");
        const float width = 1.0f, height = 1.1f, skin = 0.0008f;

        // The waveform is the CarDoor model's; the level is DoorAcoustics' loudest part for a 22 kg leaf
        // opened, and shut at a firm push's edge speed.
        float openDb = DoorAcoustics.Opening(steel, latch, hinge, width, height, skin, CarDoorKg, 0.8f, 0f, hasSeal: true)
                                    .Max(s => s.LevelDb);
        float closeSpeed = DoorAcoustics.EdgeSpeed(width, 1.1f, 0.5f);
        float closeDb = DoorAcoustics.Closing(steel, latch, centre, width, height, skin, CarDoorKg, closeSpeed, hasSeal: true)
                                     .Max(s => s.LevelDb);
        return new List<TransientSound>
        {
            new() { Character = SoundCharacter.Knock, Position = latch, LevelDb = openDb, Hz = 500f,
                    DecaySeconds = 0.95f, Noisiness = 1f, SynthKey = OpenFPS.Common.CarDoor.Key(closing: false) },
            new() { Character = SoundCharacter.Knock, Position = latch, LevelDb = closeDb, Hz = 500f,
                    DecaySeconds = 1.15f, Noisiness = 1f, SynthKey = OpenFPS.Common.CarDoor.Key(closing: true), DelaySeconds = closeAfter },
        };
    }

    /// <summary>Where a seat is in the world right now, given where its composite is.</summary>
    public static Vector3 SeatPosition(Transform rootTransform, Seat seat)
        => rootTransform.Position + Vector3.Transform(seat.LocalPosition, rootTransform.Rotation);

    /// <summary>Which way a seat faces in the world right now.</summary>
    public static float SeatYaw(Transform rootTransform, Seat seat)
    {
        MathHelper.ToYawPitch(rootTransform.Rotation, out float rootYaw, out _);
        return MathHelper.WrapAngle(rootYaw + seat.LocalYaw);
    }

    /// <summary>Whether anybody is in this seat.</summary>
    public static bool SeatTaken(World world, int rootId, int seatIndex)
    {
        bool taken = false;
        var q = new QueryDescription().WithAll<OccupantComponent>();
        world.Query(in q, (ref OccupantComponent o) =>
        { if (o.RootEntityId == rootId && o.SeatIndex == seatIndex) taken = true; });
        return taken;
    }

    /// <summary>
    /// Whether a thing is going too fast to get on or off. A car somebody drives says so in its
    /// DriveComponent; a bus the map drives says so in its VehicleComponent.
    /// </summary>
    public static bool Moving(World world, Entity root)
    {
        const float WalkingPace = 1.0f;
        if (world.Has<DriveComponent>(root) && MathF.Abs(world.Get<DriveComponent>(root).Speed) > 2.0f) return true;
        if (world.Has<VehicleComponent>(root) && !world.Has<DriveComponent>(root)
            && MathF.Abs(world.Get<VehicleComponent>(root).Speed) > WalkingPace) return true;
        return false;
    }

    /// <summary>The composite with a seat nearest a point, or -1: by its seats, so "get in" beside a car
    /// against a wall means the car, not the house.</summary>
    public int NearestEnterable(string mapId, Vector3 near, float radius) => NearestEnterable(mapId, near, radius, out _);

    /// <summary>As above, and where its nearest seat is.</summary>
    public int NearestEnterable(string mapId, Vector3 near, float radius, out Vector3 seatAt)
    {
        seatAt = default;
        if (!_maps.TryGetMap(mapId, out var world, out _, out _, out _)) return -1;
        int best = -1; float bestD2 = radius * radius;
        Vector3 at = default;
        var q = new QueryDescription().WithAll<Transform, CompositeComponent, OccupancyComponent>();
        world.Query(in q, (Entity e, ref Transform t, ref CompositeComponent _, ref OccupancyComponent o) =>
        {
            if (o.Seats == null || o.Seats.Count == 0) return;
            foreach (var seat in o.Seats)
            {
                var p = SeatPosition(t, seat);
                float d2 = Vector3.DistanceSquared(p, near);
                if (d2 <= bestD2) { bestD2 = d2; best = e.Id; at = p; }
            }
        });
        seatAt = at;
        return best;
    }

    /// <summary>What the seats of a composite are called, and whether each is free.</summary>
    public List<(string Name, bool Controls, bool Taken, float Distance)> Seats(string mapId, int rootId, Vector3 from)
    {
        var listed = new List<(string, bool, bool, float)>();
        if (!_maps.TryGetMap(mapId, out var world, out _, out _, out var lookup)) return listed;
        if (!lookup.TryGetValue(rootId, out var root) || !world.IsAlive(root) || !world.Has<OccupancyComponent>(root)) return listed;

        var rootT = world.Get<Transform>(root);
        var seats = world.Get<OccupancyComponent>(root).Seats;
        for (int i = 0; i < seats.Count; i++)
            listed.Add((seats[i].Name, seats[i].Controls, SeatTaken(world, rootId, i),
                        Vector3.Distance(SeatPosition(rootT, seats[i]), from)));
        return listed;
    }

    /// <summary>
    /// Puts a player in a seat. A named seat is taken literally, refused when occupied. With no name,
    /// the first free seat they may use within reach, else the nearest: in your own car, the driver's.
    /// </summary>
    public bool Enter(UserSession session, int rootId, string? seatName, out string message)
    {
        message = "";
        if (!_maps.TryGetMap(session.CurrentMapId, out var world, out _, out _, out var lookup))
        { message = "The map is not loaded."; return false; }
        if (session.Entity == Entity.Null || !world.IsAlive(session.Entity))
        { message = "You are not in the world yet."; return false; }
        if (world.Has<OccupantComponent>(session.Entity))
        { message = "You are already inside something. Get out first."; return false; }
        if (!lookup.TryGetValue(rootId, out var root) || !world.IsAlive(root) || !world.Has<OccupancyComponent>(root))
        { message = "There is nothing to get into there."; return false; }

        var rootT = world.Get<Transform>(root);
        var seats = world.Get<OccupancyComponent>(root).Seats;
        if (seats == null || seats.Count == 0) { message = "It has no seats."; return false; }

        var composite = world.Get<CompositeComponent>(root);
        bool elevated = session.Can(Permissions.EditAny);
        bool mayDrive = CompositeService.MayModify(world, root, session.Username, elevated);
        var playerPos = world.Get<Transform>(session.Entity).Position;

        int chosen = -1;
        if (!string.IsNullOrWhiteSpace(seatName))
        {
            chosen = seats.FindIndex(x => string.Equals(x.Name, seatName, StringComparison.OrdinalIgnoreCase));
            if (chosen < 0)
            { message = $"There is no seat called '{seatName}'.{Alternatives(world, rootId, seats, mayDrive)}"; return false; }
            if (SeatTaken(world, rootId, chosen))
            { message = $"The {seats[chosen].Name} seat is taken.{Alternatives(world, rootId, seats, mayDrive)}"; return false; }
            if (seats[chosen].Controls && !mayDrive)
            { message = $"{composite.Name} belongs to {composite.Owner}; you cannot drive it."
                      + Alternatives(world, rootId, seats, mayDrive); return false; }
        }
        else
        {
            // Only seats within reach count as "first": a bus's first seat is at the front whichever door you are at.
            int nearest = -1; float nearestD = float.MaxValue;
            for (int i = 0; i < seats.Count; i++)
            {
                if (SeatTaken(world, rootId, i)) continue;
                if (seats[i].Controls && !mayDrive) continue;
                float d = Vector3.Distance(playerPos, SeatPosition(rootT, seats[i]));
                if (d <= BoardingRange) { chosen = i; break; }
                if (d < nearestD) { nearestD = d; nearest = i; }
            }
            if (chosen < 0) chosen = nearest;
            if (chosen < 0) { message = $"There is nowhere free in {composite.Name}."; return false; }
        }

        if (Moving(world, root))
        { message = $"{composite.Name} is moving. Wait for it to stop."; return false; }

        var seat = seats[chosen];
        var seatPos = SeatPosition(rootT, seat);
        float reach = Vector3.Distance(playerPos, seatPos);
        if (reach > BoardingRange)
        { message = $"The {seat.Name} seat is {reach:F0} metres away. Get closer."; return false; }

        world.Add(session.Entity, new OccupantComponent
        {
            RootEntityId = rootId,
            SeatIndex = chosen,
            Controls = seat.Controls,
            BoardedFrom = playerPos,
        });

        ref var player = ref world.Get<PlayerComponent>(session.Entity);
        player.IsInVehicle = true;
        player.Yaw = SeatYaw(rootT, seat);
        player.IsGrounded = true;

        ref var t = ref world.Get<Transform>(session.Entity);
        t.Position = seatPos;
        t.Rotation = Quaternion.CreateFromYawPitchRoll(player.Yaw, player.Pitch, 0f);
        t.IsDirty = true;
        if (world.Has<Velocity>(session.Entity)) world.Get<Velocity>(session.Entity).Linear = Vector3.Zero;

        // Queued input was the walk up to it; spent now, it would walk them out of the seat.
        while (session.InputQueue.TryDequeue(out _)) { }
        session.GroundProbe.Invalidate();

        CarDoor(session.CurrentMapId, world, root, seat);

        // Said aloud: an idling engine is quiet, and there is no dashboard to look at.
        string engine = world.Has<DriveComponent>(root)
            ? world.Get<DriveComponent>(root).EngineOn ? " The engine is running." : " The engine is off; T starts it."
            : "";
        message = seat.Controls
            ? $"You are in the {seat.Name} seat of {composite.Name}.{engine} W and S to drive, A and D to steer, shift T switches it off."
            : $"You are in the {seat.Name} seat of {composite.Name}.";
        Log.Information("{User} took the '{Seat}' seat of composite {Root} ('{Name}').",
                        session.Username, seat.Name, rootId, composite.Name);
        return true;
    }

    /// <summary>The seats still free to them, named, so a refusal saves a lap of the car trying doors.</summary>
    private static string Alternatives(World world, int rootId, List<Seat> seats, bool mayDrive)
    {
        var free = new List<string>();
        for (int i = 0; i < seats.Count; i++)
        {
            if (SeatTaken(world, rootId, i)) continue;
            if (seats[i].Controls && !mayDrive) continue;
            free.Add(seats[i].Name);
        }
        if (free.Count == 0) return " Nothing else is free either.";
        return $" Free: {string.Join(", ", free)}.";
    }

    /// <summary>
    /// Puts a player on clear ground beside whatever they were in. <paramref name="leavingWorld"/> is a
    /// body leaving from its seat (logout, lost connection, map change): neither motion nor shut doors
    /// refuse it, and where it is put is kept for the player's return.
    /// </summary>
    public bool Exit(UserSession session, out string message, bool leavingWorld = false)
    {
        message = "";
        if (!_maps.TryGetMap(session.CurrentMapId, out var world, out _, out var grid, out var lookup))
        { message = "The map is not loaded."; return false; }
        if (session.Entity == Entity.Null || !world.IsAlive(session.Entity) || !world.Has<OccupantComponent>(session.Entity))
        { message = "You are not inside anything."; return false; }

        var occupant = world.Get<OccupantComponent>(session.Entity);
        string name = "it";
        string left = "";
        Vector3 from = world.Get<Transform>(session.Entity).Position;
        Vector3 spot = from;

        if (lookup.TryGetValue(occupant.RootEntityId, out var root) && world.IsAlive(root))
        {
            if (world.Has<CompositeComponent>(root)) name = world.Get<CompositeComponent>(root).Name;
            if (!leavingWorld && Moving(world, root))
            {
                message = $"{name} is still moving. Stop first.";
                return false;
            }
            // A vehicle whose passenger doors beep (VehicleProfile.DoorChime) lets passengers off only at
            // its stops, not at a light; the driver's own door is not that door.
            if (!leavingWorld && !occupant.Controls && world.Has<SoundEmitterComponent>(root)
                && world.Get<SoundEmitterComponent>(root) is { SoundId: { } sid } em
                && sid.StartsWith("engine:", StringComparison.OrdinalIgnoreCase)
                && MachineRegistry.Knows(sid[7..]) && MachineRegistry.VehicleFor(sid[7..]).DoorChime
                && !em.ServingStop)
            {
                message = $"The doors of {name} are shut. It lets passengers off at its stops.";
                return false;
            }
            spot = FindStandingRoom(world, grid, root, session.Entity, from, occupant.BoardedFrom);
            // You step down facing the way you were carried, not the way your head was turned.
            if (world.Has<Transform>(root))
            {
                MathHelper.ToYawPitch(world.Get<Transform>(root).Rotation, out float heading, out _);
                ref var p = ref world.Get<PlayerComponent>(session.Entity);
                p.Yaw = heading;
                world.Get<Transform>(session.Entity).Rotation = Quaternion.CreateFromYawPitchRoll(p.Yaw, p.Pitch, 0f);
            }
            if (occupant.Controls && world.Has<DriveComponent>(root) && world.Get<DriveComponent>(root).EngineOn)
                left = " You left the engine running.";
            if (world.Has<OccupancyComponent>(root) && occupant.SeatIndex < world.Get<OccupancyComponent>(root).Seats.Count)
                CarDoor(session.CurrentMapId, world, root, world.Get<OccupancyComponent>(root).Seats[occupant.SeatIndex]);
        }

        CompositeService.Disembark(world, session.Entity);

        ref var t = ref world.Get<Transform>(session.Entity);
        t.Position = spot;
        t.IsDirty = true;
        if (world.Has<Velocity>(session.Entity)) world.Get<Velocity>(session.Entity).Linear = Vector3.Zero;
        while (session.InputQueue.TryDequeue(out _)) { }
        session.GroundProbe.Invalidate();

        message = $"You get out of {name}.{left}";
        Log.Information("{User} got out of composite {Root}.", session.Username, occupant.RootEntityId);
        return true;
    }

    /// <summary>
    /// A clear patch of ground to step out onto, searched from the seat, not the composite's origin
    /// (from a bus's origin you would land by its front bumper). Where they got in is tried first, if
    /// the thing has not moved away from it since.
    /// </summary>
    private static Vector3 FindStandingRoom(World world, SpatialGrid<Entity> grid, Entity root, Entity self,
                                            Vector3 seatPosition, Vector3 boardedFrom)
    {
        var members = CompositeService.MembersOf(world, root.Id);

        if (Vector3.Distance(boardedFrom, seatPosition) <= BoardingRange
            && !CollidesIgnoring(world, grid, boardedFrom, root, self, members))
        {
            var back = boardedFrom;
            back.Y = PhysicsUtils.GetGroundHeight(world, grid, back, out _);
            if (!CollidesIgnoring(world, grid, back, root, self, members)) return back;
        }

        // Outward from the seat, sideways first (where the door nearly always is), widening until something fits.
        MathHelper.ToYawPitch(world.Get<Transform>(root).Rotation, out float rootYaw, out _);
        float[] offsets = { MathF.PI / 2f, -MathF.PI / 2f, MathF.PI, 0f,
                            3f * MathF.PI / 4f, -3f * MathF.PI / 4f, MathF.PI / 4f, -MathF.PI / 4f };
        float[] distances = { 1.6f, 2.6f, 4.0f, 6.0f };

        foreach (float distance in distances)
            foreach (float offset in offsets)
            {
                float angle = rootYaw + offset;
                var candidate = seatPosition + new Vector3(MathF.Sin(angle), 0f, MathF.Cos(angle)) * distance;
                candidate.Y = PhysicsUtils.GetGroundHeight(world, grid, candidate, out _);
                if (!CollidesIgnoring(world, grid, candidate, root, self, members))
                    return candidate;
            }

        // Nothing fits (a garage barely its own size): the seat, rather than inside a wall.
        return seatPosition;
    }

    /// <summary>The standing-room test, blind to the player and every part of the composite;
    /// <see cref="MovementSystem.CheckCollision"/> can ignore only one entity.</summary>
    private static bool CollidesIgnoring(World world, SpatialGrid<Entity> grid, Vector3 pos,
                                         Entity root, Entity self, List<Entity> members)
    {
        float footPadding = 0.4f;
        float checkHeight = PhysicsConstants.PlayerHeight - footPadding;
        Vector3 centre = pos + new Vector3(0, footPadding + checkHeight / 2f, 0);

        foreach (var e in grid.GetItemsInRadius(pos, 10.0f))
        {
            if (e.Id == self.Id || e.Id == root.Id) continue;
            if (members.Contains(e)) continue;
            if (!world.Has<ColliderComponent>(e) || !world.Has<Transform>(e)) continue;
            ref var t = ref world.Get<Transform>(e);
            ref var c = ref world.Get<ColliderComponent>(e);
            if (!c.IsSolid) continue;

            var worldToLocal = Matrix4x4.CreateFromQuaternion(Quaternion.Inverse(t.Rotation));
            var local = Vector3.Transform(centre - t.Position, worldToLocal);
            if (GeometryUtils.AABBIntersectsCylinder(-c.Size / 2f, c.Size / 2f, local,
                                                     PhysicsConstants.PlayerRadius, checkHeight))
                return true;
        }
        return false;
    }
}

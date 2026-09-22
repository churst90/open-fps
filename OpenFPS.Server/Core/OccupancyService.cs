using System.Numerics;
using Arch.Core;
using Arch.Core.Extensions;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Server.Systems;
using Serilog;

namespace OpenFPS.Server.Core;

/// <summary>
/// Getting in and getting out.
///
/// The last of the four things a composite is for — a set of entities with a local origin, which can
/// be saved, placed again, owned, and ENTERED — and the one that makes driving stop being a separate
/// feature. Sitting in a kitchen chair and sitting in a driver's seat are the same act; the only
/// difference is whether the seat has <see cref="Seat.Controls"/> set, and whether anything is
/// willing to move the root underneath you.
///
/// While somebody is in a seat their body is not theirs to move: the seat owns where they are, and
/// <see cref="OccupancySystem"/> puts them there after everything that could have moved the root has
/// run. What stays theirs is where they are LOOKING, because for a player who navigates by ear that
/// is most of what being a passenger consists of.
/// </summary>
public class OccupancyService
{
    private readonly MapManager _maps;

    /// <summary>How close you must be to a seat to get into it, metres. Arm's length plus a step.</summary>
    public const float BoardingRange = 5.0f;

    private readonly Action<string, int, string, IReadOnlyList<TransientSound>>? _heard;

    /// <param name="heard">Where the sounds of getting in and out go — the server's world-audio
    /// channel. Null in tests that only care where people end up.</param>
    public OccupancyService(MapManager maps, Action<string, int, string, IReadOnlyList<TransientSound>>? heard = null)
    {
        _maps = maps;
        _heard = heard;
    }

    /// <summary>A car door is about a metre square of steel skin on a frame, and weighs about this.</summary>
    private const float CarDoorKg = 22f;

    /// <summary>
    /// The door beside a seat, opened and shut: getting in or out of a car.
    ///
    /// The same door model a building's door uses (<see cref="DoorAcoustics"/>) — a steel skin, a
    /// seal and a latch — so a car door is a thunk and a click and a shed door is a clatter without
    /// either being told. It opens, and a second and a bit later, once you are in or out, it shuts.
    /// Only for things that drive: a bus has its own doors, and they are air.
    /// </summary>
    private void CarDoor(string mapId, World world, Entity root, Seat seat)
    {
        if (_heard == null || !world.Has<DriveComponent>(root)) return;
        var rootT = world.Get<Transform>(root);
        float side = seat.LocalPosition.X < 0f ? -1f : 1f;
        var right = Vector3.Transform(Vector3.UnitX, rootT.Rotation);
        var forward = Vector3.Transform(Vector3.UnitZ, rootT.Rotation);
        var seatPos = SeatPosition(rootT, seat);
        // The door is in the side of the car beside the seat, at about the height of your hip.
        var centre = seatPos + right * side * 0.75f + new Vector3(0f, 0.55f, 0f);
        var hinge = centre + forward * 0.5f;
        var latch = centre - forward * 0.5f;
        var steel = AcousticRegistry.GetProperties("Metal");
        const float width = 1.0f, height = 1.1f, skin = 0.0008f;

        var sounds = new List<TransientSound>();
        foreach (var s in DoorAcoustics.Opening(steel, latch, hinge, width, height, skin, 0.8f, 0f, hasSeal: true))
            sounds.Add(s.ToTransient());
        float closeSpeed = DoorAcoustics.EdgeSpeed(width, 1.1f, 0.5f);
        foreach (var s in DoorAcoustics.Closing(steel, latch, centre, width, height, skin, CarDoorKg, closeSpeed, hasSeal: true))
        {
            var t = s.ToTransient();
            t.DelaySeconds += 1.3f;
            sounds.Add(t);
        }
        _heard(mapId, root.Id, "car door", sounds);
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
    /// The composite with seats nearest a point, or -1.
    ///
    /// Nearest-with-seats rather than nearest outright: standing beside a car parked against a wall,
    /// "get in" means the car. A house you are also within thirty metres of is not what you meant.
    /// </summary>
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

    public int NearestEnterable(string mapId, Vector3 near, float radius)
    {
        if (!_maps.TryGetMap(mapId, out var world, out _, out _, out _)) return -1;
        int best = -1; float bestD2 = radius * radius;
        var q = new QueryDescription().WithAll<Transform, CompositeComponent, OccupancyComponent>();
        world.Query(in q, (Entity e, ref Transform t, ref CompositeComponent _, ref OccupancyComponent o) =>
        {
            if (o.Seats == null || o.Seats.Count == 0) return;
            foreach (var seat in o.Seats)
            {
                float d2 = Vector3.DistanceSquared(SeatPosition(t, seat), near);
                if (d2 <= bestD2) { bestD2 = d2; best = e.Id; }
            }
        });
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
    /// Puts a player in a seat.
    ///
    /// A named seat is taken literally, including refusing when it is occupied — telling somebody
    /// they are in the passenger seat when they asked to drive is worse than telling them no. With no
    /// name, the first seat they are ALLOWED into wins, and since a driver's seat is nearly always
    /// declared first, getting into your own car puts you behind the wheel without saying so.
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
        bool elevated = session.Role is UserRole.Dev or UserRole.Admin;
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
            // The first free seat in the order they were declared — the driver's, in your own car —
            // among the ones you can actually reach. A bus is eleven metres long, and "the first free
            // seat" on it is at the front whichever door you are standing at.
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

        // Nobody steps on or off something that is going along the road. A bus you are waiting for
        // opens its doors when it has stopped, and that is when you get on.
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

        // Whatever they had queued was them walking up to it. Spending it now would have them try to
        // walk out of the seat they just sat down in.
        while (session.InputQueue.TryDequeue(out _)) { }
        session.GroundProbe.Invalidate();

        CarDoor(session.CurrentMapId, world, root, seat);

        message = seat.Controls
            ? $"You are in the {seat.Name} seat of {composite.Name}. T turns the key. Forward and back to drive, left and right to steer."
            : $"You are in the {seat.Name} seat of {composite.Name}.";
        Log.Information("{User} took the '{Seat}' seat of composite {Root} ('{Name}').",
                        session.Username, seat.Name, rootId, composite.Name);
        return true;
    }

    /// <summary>
    /// What else they could have asked for, named.
    ///
    /// A refusal that only says no is a refusal a player has to go and investigate, and investigating
    /// a car you cannot see means walking round it trying doors. Every no here carries the yeses with
    /// it, which costs one sentence and saves a lap of the vehicle.
    /// </summary>
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
    /// Puts a player back on the ground beside whatever they were in.
    ///
    /// Beside it, not inside it: stepping out of a car and finding yourself standing in its engine
    /// bay is the kind of thing that is merely odd to look at and completely disorienting to listen
    /// to. The spot is searched for around the composite rather than assumed, so getting out against
    /// a wall puts you on the other side rather than in the wall.
    /// </summary>
    public bool Exit(UserSession session, out string message)
    {
        message = "";
        if (!_maps.TryGetMap(session.CurrentMapId, out var world, out _, out var grid, out var lookup))
        { message = "The map is not loaded."; return false; }
        if (session.Entity == Entity.Null || !world.IsAlive(session.Entity) || !world.Has<OccupantComponent>(session.Entity))
        { message = "You are not inside anything."; return false; }

        var occupant = world.Get<OccupantComponent>(session.Entity);
        string name = "it";
        Vector3 from = world.Get<Transform>(session.Entity).Position;
        Vector3 spot = from;

        if (lookup.TryGetValue(occupant.RootEntityId, out var root) && world.IsAlive(root))
        {
            if (world.Has<CompositeComponent>(root)) name = world.Get<CompositeComponent>(root).Name;
            if (Moving(world, root))
            {
                message = $"{name} is still moving. Stop first.";
                return false;
            }
            spot = FindStandingRoom(world, grid, root, session.Entity, from, occupant.BoardedFrom);
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

        message = $"You get out of {name}.";
        Log.Information("{User} got out of composite {Root}.", session.Username, occupant.RootEntityId);
        return true;
    }

    /// <summary>
    /// A clear patch of ground to step out onto, searched outward from the SEAT rather than from the
    /// middle of the thing.
    ///
    /// From the seat, because that is where the person is. Searching from the composite's origin
    /// works for a car, whose origin is a metre from every seat in it, and is absurd for a bus or a
    /// house: stepping off a bus would put you level with its front bumper, and leaving an upstairs
    /// room would put you outside the building.
    ///
    /// Where they got IN is tried first and usually wins, because it is somewhere they demonstrably
    /// fitted a moment ago — but only if the thing has not driven off since, or a passenger stepping
    /// out at the far end of the road would be returned to the car park.
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

        // Otherwise: outward from the seat, sideways first — that is where the door is on nearly
        // everything — widening until something fits.
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

        // Nothing fits — parked in a garage barely its own size. The seat is at least somewhere the
        // player already was, rather than inside a wall.
        return seatPosition;
    }

    /// <summary>
    /// The standing-room test, blind to the composite itself.
    ///
    /// <see cref="MovementSystem.CheckCollision"/> can ignore one entity, and here there are always at
    /// least two to ignore — the player and the thing they are climbing out of, which is made of as
    /// many solid parts as somebody cared to build it from. Every one of those would otherwise report
    /// that there is no room to stand anywhere near the car you are sitting in.
    /// </summary>
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

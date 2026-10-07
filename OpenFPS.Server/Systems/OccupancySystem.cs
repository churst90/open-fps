using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Server.Core;
using Serilog;

namespace OpenFPS.Server.Systems;

/// <summary>
/// Carries everybody who is inside something. Runs last, after every system that could move a root.
///
/// Position belongs to the seat. Heading is the occupant's own, with the vehicle's turn added to it,
/// which is why occupants get no <see cref="ParentComponent"/> as parts do. Adding the turn rather than
/// overwriting the yaw reaches the client as an ordinary heading correction, so the listener turns with
/// the car instead of hearing the world spin through every corner.
/// </summary>
public sealed class OccupancySystem
{
    /// <summary>Last tick's heading for every root that is carrying somebody.</summary>
    private readonly Dictionary<int, float> _headings = new();
    private readonly List<(Entity Occupant, int RootId)> _stranded = new();

    public void Update(World world, Dictionary<int, Entity> lookup)
    {
        _stranded.Clear();

        var query = new QueryDescription().WithAll<Transform, OccupantComponent, PlayerComponent>();
        world.Query(in query, (Entity e, ref Transform transform, ref OccupantComponent occupant, ref PlayerComponent player) =>
        {
            if (!lookup.TryGetValue(occupant.RootEntityId, out var root)
                || !world.IsAlive(root)
                || !world.Has<OccupancyComponent>(root)
                || !world.Has<Transform>(root))
            {
                // What they were in is gone: they are put out where it left them.
                _stranded.Add((e, occupant.RootEntityId));
                return;
            }

            var seats = world.Get<OccupancyComponent>(root).Seats;
            if (seats == null || occupant.SeatIndex < 0 || occupant.SeatIndex >= seats.Count)
            {
                _stranded.Add((e, occupant.RootEntityId));
                return;
            }

            var rootTransform = world.Get<Transform>(root);
            var seat = seats[occupant.SeatIndex];

            MathHelper.ToYawPitch(rootTransform.Rotation, out float rootYaw, out _);
            if (_headings.TryGetValue(root.Id, out float previous))
            {
                float turned = MathHelper.WrapAngle(rootYaw - previous);
                if (turned != 0f) player.Yaw = MathHelper.WrapAngle(player.Yaw + turned);
            }

            var seatPosition = OccupancyService.SeatPosition(rootTransform, seat);
            var rotation = Quaternion.CreateFromYawPitchRoll(player.Yaw, player.Pitch, 0f);
            if (transform.Position != seatPosition || transform.Rotation != rotation)
            {
                transform.Position = seatPosition;
                transform.Rotation = rotation;
                transform.IsDirty = true;
            }
            player.IsGrounded = true;

            // A passenger moves at the vehicle's speed; anything reading their velocity (the client's
            // footsteps most of all) must see that.
            if (world.Has<Velocity>(e) && world.Has<Velocity>(root))
                world.Get<Velocity>(e).Linear = world.Get<Velocity>(root).Linear;
        });

        // Somebody who is not a player in a seat (Alex on the bus): carried the same way, facing the
        // way the seat faces, since nobody is turning their head for them.
        var others = new QueryDescription().WithAll<Transform, OccupantComponent>().WithNone<PlayerComponent>();
        world.Query(in others, (Entity e, ref Transform transform, ref OccupantComponent occupant) =>
        {
            if (!lookup.TryGetValue(occupant.RootEntityId, out var root) || !world.IsAlive(root)
                || !world.Has<OccupancyComponent>(root) || !world.Has<Transform>(root))
            { _stranded.Add((e, occupant.RootEntityId)); return; }
            var seats = world.Get<OccupancyComponent>(root).Seats;
            if (seats == null || occupant.SeatIndex < 0 || occupant.SeatIndex >= seats.Count)
            { _stranded.Add((e, occupant.RootEntityId)); return; }
            var rootTransform = world.Get<Transform>(root);
            var seat = seats[occupant.SeatIndex];
            var seatPosition = OccupancyService.SeatPosition(rootTransform, seat);
            var rotation = Quaternion.CreateFromYawPitchRoll(OccupancyService.SeatYaw(rootTransform, seat), 0f, 0f);
            if (transform.Position != seatPosition || transform.Rotation != rotation)
            {
                transform.Position = seatPosition;
                transform.Rotation = rotation;
                transform.IsDirty = true;
            }
            if (world.Has<Velocity>(e) && world.Has<Velocity>(root))
                world.Get<Velocity>(e).Linear = world.Get<Velocity>(root).Linear;
        });

        foreach (var (occupant, rootId) in _stranded)
        {
            CompositeService.Disembark(world, occupant);
            Log.Information("Entity {Id} was put out: composite {Root} is gone.", occupant.Id, rootId);
        }

        // Rebuilt each tick, so roots that no longer carry anyone are forgotten.
        _headings.Clear();
        var carrying = new QueryDescription().WithAll<OccupantComponent>();
        world.Query(in carrying, (ref OccupantComponent o) =>
        {
            if (!lookup.TryGetValue(o.RootEntityId, out var root) || !world.IsAlive(root) || !world.Has<Transform>(root)) return;
            MathHelper.ToYawPitch(world.Get<Transform>(root).Rotation, out float yaw, out _);
            _headings[o.RootEntityId] = yaw;
        });
    }
}

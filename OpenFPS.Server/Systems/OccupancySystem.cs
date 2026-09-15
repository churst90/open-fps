using System;
using System.Collections.Generic;
using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Server.Core;
using Serilog;

namespace OpenFPS.Server.Systems;

/// <summary>
/// Carries everybody who is inside something.
///
/// Runs last, after every system that could have moved a root: the seat is where the occupant is,
/// so the seat has to have finished moving before anyone is put in it.
///
/// Two things are carried, and they are carried differently. POSITION belongs to the seat outright —
/// a passenger does not walk about, so there is nothing of theirs to preserve. HEADING is theirs,
/// but the rotation of the vehicle is added to it: turn a car ninety degrees and its driver is now
/// facing ninety degrees further round, having turned their own head not at all. That distinction is
/// the whole of the difference between a person riding in something and a wall bolted to it, and it
/// is why occupants are NOT given a <see cref="ParentComponent"/> like the parts are. A wall keeps
/// no opinion of its own about which way it is pointing.
///
/// Adding the delta to the player's own yaw rather than overwriting it is also what makes this
/// survive the trip to the client: the client reconciles its heading against the server's, so a yaw
/// the server turned arrives as an ordinary correction and the listener turns with the car. Anything
/// else would leave a driver hearing the world spin around them through every corner.
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
                // Whatever they were in has stopped existing. Leave them exactly where it left them —
                // this is not getting out, it is the floor disappearing.
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

            // A passenger is moving at the speed of what they are in. Anything that reads a player's
            // velocity — the client's own footstep generation most of all — has to see that rather
            // than a person standing still at a hundred miles an hour.
            if (world.Has<Velocity>(e) && world.Has<Velocity>(root))
                world.Get<Velocity>(e).Linear = world.Get<Velocity>(root).Linear;
        });

        foreach (var (occupant, rootId) in _stranded)
        {
            CompositeService.Disembark(world, occupant);
            Log.Information("Entity {Id} was put out: composite {Root} is gone.", occupant.Id, rootId);
        }

        // Remember every carrying root's heading for next tick, and forget the ones that no longer
        // carry anyone, or this grows for the life of the server.
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

using System;
using System.Collections.Generic;
using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Core;
using Serilog;

namespace OpenFPS.Server.Systems;

/// <summary>
/// A composite with somebody in its driving seat.
///
/// There are no handling numbers in this file, and that is the point. What a car pulls comes out of
/// its engine's torque through its own gearbox; what it will corner and stop at comes out of its
/// tyres' peak grip against its mass; what it will not exceed comes out of its drag area, because
/// top speed is where the engine stops out-pulling the air. Give it a truck's profile and it drives
/// like a truck without a line here changing. That is the same bargain the rest of the audio engine
/// makes: behaviour falls out of what a thing is made of, never out of a table of per-vehicle
/// constants.
///
/// The one thing that IS decided here is how a person's two axes of input become throttle, brake and
/// steering, because that is a question about players rather than about cars.
///
/// The friction circle is shared with the tyre audio deliberately — <see cref="TyreFriction.Demand"/>
/// is the same function the client uses to decide whether a tyre squeals. Ask for more cornering than
/// is left after the braking and the car runs wide, and the noise it makes doing so is the same
/// number that made it run wide. Neither half was written for the other.
/// </summary>
public static class DrivingSystem
{
    /// <summary>Full steering lock, radians. A road car is about thirty-five degrees at the wheel.</summary>
    private const float MaxSteerAngle = 0.61f;

    /// <summary>How long held controls survive a silent client before they start decaying, seconds.
    /// A driver does not lift off because a packet was lost; a driver who has gone does coast to a
    /// stop rather than drive away forever.</summary>
    private const float ControlHoldSeconds = 0.75f;

    /// <summary>How fast a thing will reverse, m/s. Reverse is one low gear and a short one.</summary>
    private const float ReverseTopSpeed = 8.0f;

    /// <summary>Below this the car is stopped, not creeping. Stops resistances oscillating about zero.</summary>
    private const float StandstillSpeed = 0.15f;

    private const float AirDensity = 1.225f;

    /// <summary>How far a body is held off the floor when testing whether it has hit something.
    /// Everything drives over something; without this, everything is permanently crashed into it.</summary>
    private const float GroundClearance = 0.35f;

    /// <summary>
    /// How much of its peak torque an engine absorbs on a closed throttle, at the redline.
    ///
    /// A lifted throttle does not disconnect the engine, it drags it — pumping losses and friction,
    /// through the same gearing that drives the wheels. It is why lifting off slows a car so much
    /// faster than freewheeling, why that slowing is stronger in a low gear, and why the exhaust pops
    /// on the overrun. Without it a coasting car takes several minutes to stop, which is what a car
    /// in neutral actually does and not what anyone lifting off expects.
    /// </summary>
    private const float EngineBrakingFraction = 0.15f;

    /// <summary>
    /// Turns one input packet into what the driver is asking for.
    ///
    /// Forward and back are throttle and brake, and which one "back" means depends on whether the
    /// thing is already moving — press back while rolling forward and you are braking; press it again
    /// once stopped and you are reversing. That is the mapping every driving game settles on because
    /// it is the one that needs no explaining, and it needs none here least of all: a player who
    /// cannot see the gear selector has nothing else to go on.
    /// </summary>
    public static void ApplyControls(World world, Entity root, ClientInputUpdate input)
    {
        if (!world.Has<DriveComponent>(root)) return;
        ref var drive = ref world.Get<DriveComponent>(root);

        float forward = Math.Clamp(input.MoveDirection.Z, -1f, 1f);
        float lateral = Math.Clamp(input.MoveDirection.X, -1f, 1f);

        if (forward > 0f)
        {
            // Forward against backward motion is the brake, not the throttle.
            if (drive.Speed < -StandstillSpeed) { drive.Throttle = 0f; drive.Brake = forward; }
            else { drive.Throttle = forward; drive.Brake = 0f; }
        }
        else if (forward < 0f)
        {
            if (drive.Speed > StandstillSpeed) { drive.Throttle = 0f; drive.Brake = -forward; }
            else { drive.Throttle = forward; drive.Brake = 0f; }
        }
        else
        {
            drive.Throttle = 0f;
            // A hand off the keys is a lift, not a brake. The car slows on drag and rolling
            // resistance, which is what makes coasting audible as something different from braking.
            drive.Brake = input.Jump ? 1f : 0f;
        }

        drive.Steer = lateral;
        drive.ControlAge = 0f;
    }

    /// <summary>Everything currently under its own power on this map, moved one tick.</summary>
    public static void Update(World world, SpatialGrid<Entity> grid, Vector3 mapMin, Vector3 mapMax, float dt)
    {
        var query = new QueryDescription().WithAll<Transform, DriveComponent, Velocity>();
        var driven = new List<Entity>();
        world.Query(in query, (Entity e, ref Transform _, ref DriveComponent __, ref Velocity ___) => driven.Add(e));

        foreach (var root in driven)
        {
            try { Step(world, grid, root, mapMin, mapMax, dt); }
            catch (Exception ex) { Log.Error(ex, "DrivingSystem: entity {Id} failed to move.", root.Id); }
        }
    }

    private static void Step(World world, SpatialGrid<Entity> grid, Entity root, Vector3 mapMin, Vector3 mapMax, float dt)
    {
        ref var drive = ref world.Get<DriveComponent>(root);
        if (string.IsNullOrEmpty(drive.Preset)) return;
        var profile = VehicleProfile.ByName(drive.Preset);

        // A driver who has gone quiet. Not an instant cut — that would make ordinary packet loss
        // stutter the throttle — but a lift, then a coast.
        drive.ControlAge += dt;
        if (drive.ControlAge > ControlHoldSeconds)
        {
            float fade = MathF.Max(0f, 1f - (drive.ControlAge - ControlHoldSeconds));
            drive.Throttle *= fade;
            drive.Steer *= fade;
        }
        if (!HasDriver(world, root.Id)) { drive.Throttle = 0f; drive.Steer = 0f; }

        float v = drive.Speed;
        float speed = MathF.Abs(v);
        float mass = MathF.Max(1f, profile.MassKg);
        float grip = MathF.Max(0.1f, profile.Tyres.PeakGripG);
        float capacity = grip * TyreFriction.G;          // the most this car can do, any direction

        // ── What the engine is pulling ──────────────────────────────────────────────────────────
        var gb = profile.Gearbox;
        int gear = SelectGear(gb, speed);
        float rpm = MathF.Max(profile.Engine.IdleRpm, gb.RpmFor(speed, gear));
        float torque = profile.Engine.PeakTorqueNm * TorqueFraction(rpm, profile);
        float ratio = gb.Ratios[gear - 1] * gb.FinalDrive;
        float tractive = MathF.Abs(drive.Throttle) * torque * ratio / MathF.Max(0.05f, gb.WheelRadiusMetres);
        // Reverse is geared low and runs out early — you cannot reverse a car up to its top speed.
        if (drive.Throttle < 0f && speed > ReverseTopSpeed) tractive = 0f;

        // ── What is holding it back ─────────────────────────────────────────────────────────────
        float drag = 0.5f * AirDensity * profile.DragArea * v * v;
        float rolling = profile.RollingResistance * mass * TyreFriction.G;
        float braking = Math.Clamp(drive.Brake, 0f, 1f) * capacity * mass;
        float engineBraking = (1f - MathF.Abs(drive.Throttle))
                            * profile.Engine.PeakTorqueNm * EngineBrakingFraction
                            * (rpm / MathF.Max(1f, profile.Engine.RedlineRpm))
                            * ratio / MathF.Max(0.05f, gb.WheelRadiusMetres);

        float longitudinal = MathF.Sign(drive.Throttle) * tractive / mass;
        if (speed > StandstillSpeed)
            longitudinal -= MathF.Sign(v) * (drag + rolling + braking + engineBraking) / mass;
        else if (MathF.Abs(drive.Throttle) < 0.01f) longitudinal = 0f;

        // ── What the steering is asking for ─────────────────────────────────────────────────────
        //
        // The geometry gives a radius; the radius gives a lateral acceleration. Nothing yet says the
        // tyres can deliver it.
        float steerAngle = Math.Clamp(drive.Steer, -1f, 1f) * MaxSteerAngle;
        float wheelbase = MathF.Max(1.2f, profile.FrontAxleZ - profile.RearAxleZ);
        float lateral = 0f;
        if (MathF.Abs(steerAngle) > 0.001f && speed > StandstillSpeed)
        {
            float radius = wheelbase / MathF.Tan(MathF.Abs(steerAngle));
            lateral = MathF.Sign(steerAngle) * v * v / MathF.Max(0.5f, radius);
        }

        // ── The friction circle ─────────────────────────────────────────────────────────────────
        //
        // One contact patch, two demands. Ask for more than it has and BOTH are scaled back, which is
        // why braking hard into a turn makes a car run wide rather than merely making it slower: the
        // cornering it cannot do is the cornering the brakes are already using.
        float demand = TyreFriction.Demand(longitudinal, lateral, grip);
        if (demand > 1f)
        {
            longitudinal /= demand;
            lateral /= demand;
        }

        // ── Integrate ───────────────────────────────────────────────────────────────────────────
        float next = v + longitudinal * dt;
        // Resistance may not drag a car backwards through a standstill; it can only stop it. And
        // below a crawl with nothing driving it, a car is stopped rather than creeping — otherwise it
        // rolls on forever at the speed where the resistances stopped being applied.
        if (MathF.Abs(drive.Throttle) < 0.01f
            && (MathF.Sign(next) != MathF.Sign(v) || MathF.Abs(next) < StandstillSpeed)) next = 0f;
        drive.Speed = next;

        if (speed > StandstillSpeed)
        {
            float yawRate = lateral / v;                 // signed by v: reversing steers the other way
            drive.Heading = MathHelper.WrapAngle(drive.Heading + yawRate * dt);
        }

        ref var transform = ref world.Get<Transform>(root);
        var heading = new Vector3(MathF.Sin(drive.Heading), 0f, MathF.Cos(drive.Heading));
        var wanted = transform.Position + heading * drive.Speed * dt;
        wanted.Y = PhysicsUtils.GetGroundHeight(world, grid, wanted, out _);
        wanted = Vector3.Clamp(wanted, mapMin, mapMax);

        // Hitting something stops it. What hitting something SOUNDS like — the impulse, the damage,
        // the noise of two materials meeting at a closing speed — is the collision-response work that
        // comes after this; stopping dead is the honest placeholder, not a pretence that it is done.
        if (Blocked(world, grid, root, wanted, drive.Heading, out float hitSpeedLoss))
        {
            drive.Speed = 0f;
            drive.Throttle = 0f;
            if (hitSpeedLoss > 3f)
                Log.Debug("Driven composite {Id} hit something at {Speed:F1} m/s.", root.Id, hitSpeedLoss);
        }
        else
        {
            transform.Position = wanted;
        }

        transform.Rotation = Quaternion.CreateFromYawPitchRoll(drive.Heading, 0f, 0f);
        transform.IsDirty = true;

        ref var velocity = ref world.Get<Velocity>(root);
        velocity.Linear = heading * drive.Speed;

        // The client synthesises the engine from the speed this entity reports, and picks its own
        // gear from it with the same rule used above — so the gear you hear is the gear you are in.
        if (world.Has<VehicleComponent>(root))
        {
            ref var vehicle = ref world.Get<VehicleComponent>(root);
            vehicle.Speed = drive.Speed;
        }
    }

    /// <summary>Whether anybody is actually holding the wheel.</summary>
    private static bool HasDriver(World world, int rootId)
    {
        bool found = false;
        var q = new QueryDescription().WithAll<OccupantComponent>();
        world.Query(in q, (ref OccupantComponent o) =>
        { if (o.RootEntityId == rootId && o.Controls) found = true; });
        return found;
    }

    /// <summary>
    /// The gear a driver would be in at this speed — the lowest that is not yet past the upshift.
    ///
    /// The same rule the client's engine processor uses to pick a gear from the speed it is sent.
    /// Written twice on purpose rather than shared: the client must be able to reach an answer for a
    /// car it is only listening to, with nothing from the server but how fast it is going.
    /// </summary>
    private static int SelectGear(Gearbox gb, float speed)
    {
        for (int g = 1; g <= gb.TopGear; g++)
            if (gb.RpmFor(speed, g) <= gb.UpshiftRpm) return g;
        return gb.TopGear;
    }

    /// <summary>
    /// How much of its peak torque an engine is making at this speed, 0..1.
    ///
    /// A shallow curve either side of the peak, falling away above it toward the redline: enough to
    /// make a short-geared engine audibly different from a lazy one without pretending to be a dyno
    /// sheet. The engine profiles carry a peak and where it is, so that is what this uses.
    /// </summary>
    private static float TorqueFraction(float rpm, VehicleProfile profile)
    {
        var e = profile.Engine;
        if (rpm <= e.IdleRpm) return 0.55f;
        if (rpm >= e.RedlineRpm) return 0.35f;
        float peak = Math.Clamp(e.PeakTorqueRpm, e.IdleRpm + 1f, e.RedlineRpm - 1f);
        if (rpm <= peak)
        {
            float t = (rpm - e.IdleRpm) / (peak - e.IdleRpm);
            return 0.55f + 0.45f * t;
        }
        float f = (rpm - peak) / (e.RedlineRpm - peak);
        return 1.0f - 0.65f * f * f;
    }

    /// <summary>
    /// Whether the body of this thing would be inside something solid at a proposed position.
    ///
    /// Sampled at the nose, the middle and the tail rather than as one lump, because a car is long:
    /// a single check at the centre lets half of it into a wall before anything notices. Its own
    /// parts and its own occupants are invisible to the test — they travel with it, and a car that
    /// collided with its own doors would never move at all.
    /// </summary>
    private static bool Blocked(World world, SpatialGrid<Entity> grid, Entity root, Vector3 position,
                                float heading, out float closingSpeed)
    {
        closingSpeed = MathF.Abs(world.Get<DriveComponent>(root).Speed);
        if (!world.Has<ColliderComponent>(root)) return false;

        var size = world.Get<ColliderComponent>(root).Size;
        float radius = MathF.Max(0.3f, MathF.Min(size.X, size.Z) * 0.5f);
        // Lifted clear of the floor, exactly as the player's own collision check is. A body that
        // starts at the ground finds the ground: the car would report itself blocked by the road it
        // is standing on and never move an inch.
        float clearance = MathF.Min(GroundClearance, size.Y * 0.5f);
        float height = MathF.Max(0.3f, size.Y - clearance);
        var forward = new Vector3(MathF.Sin(heading), 0f, MathF.Cos(heading));
        float reach = MathF.Max(0f, size.Z * 0.5f - radius);

        var members = CompositeService.MembersOf(world, root.Id);
        var occupants = CompositeService.OccupantsOf(world, root.Id);

        Span<float> samples = stackalloc float[] { reach, 0f, -reach };
        foreach (float along in samples)
        {
            var at = position + forward * along;
            var centre = at + new Vector3(0f, clearance + height * 0.5f, 0f);
            foreach (var other in grid.GetItemsInRadius(at, radius + 3f))
            {
                if (other.Id == root.Id) continue;
                if (members.Contains(other) || occupants.Contains(other)) continue;
                if (!world.Has<ColliderComponent>(other) || !world.Has<Transform>(other)) continue;
                ref var t = ref world.Get<Transform>(other);
                ref var c = ref world.Get<ColliderComponent>(other);
                if (!c.IsSolid) continue;

                var toLocal = Matrix4x4.CreateFromQuaternion(Quaternion.Inverse(t.Rotation));
                var local = Vector3.Transform(centre - t.Position, toLocal);
                if (GeometryUtils.AABBIntersectsCylinder(-c.Size / 2f, c.Size / 2f, local, radius, height))
                    return true;
            }
        }
        return false;
    }
}

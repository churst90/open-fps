using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Geometry;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Core;
using Serilog;

namespace OpenFPS.Server.Systems;

/// <summary>
/// A composite with somebody in its driving seat. No handling numbers live here: pull, grip and top
/// speed come from the preset's engine, gearbox, tyres and drag, and the car stands on the same
/// per-wheel model as traffic (<see cref="WheelDynamics"/>). Only how a player's two input axes become
/// throttle, brake and steering is decided here.
/// </summary>
public static class DrivingSystem
{
    /// <summary>
    /// Seconds for the wheel to go from straight ahead to full lock while a steering key is held, so a
    /// tap is a small correction and a hold a tightening turn rather than a jolt of full lock.
    /// </summary>
    private const float SteerSecondsToLock = 0.7f;

    /// <summary>Seconds for the wheel to come back to the centre when the keys are let go. Faster
    /// than turning it: a real wheel returns itself through the caster, and hands let it.</summary>
    private const float SteerSecondsToCentre = 0.3f;

    /// <summary>
    /// How far past the angle that uses all the tyres' grip the wheel may be turned at speed: a little
    /// over, so holding the key into a bend reaches the edge and the tyres say so.
    /// </summary>
    private const float SteerPastGrip = 1.15f;

    /// <summary>How long a starter turns an engine over before it runs, seconds. The client's synth
    /// finds its own moment of catching; this is the server not pulling away before it has.</summary>
    private const float CrankingSeconds = 1.0f;

    /// <summary>
    /// Turns the key. Returns what the driver hears said: the engine starting, or stopping. A car
    /// moving with the engine off still rolls and brakes — it just has no drive.
    /// </summary>
    public static string SetIgnition(World world, Entity root, bool on, Action<int>? resendDefinition)
    {
        if (!world.Has<DriveComponent>(root)) return "There is no engine in this.";
        ref var drive = ref world.Get<DriveComponent>(root);
        if (drive.EngineOn == on) return on ? "The engine is already running." : "The engine is already off.";
        drive.EngineOn = on;
        drive.EngineOnFor = 0f;
        if (world.Has<SoundEmitterComponent>(root))
        {
            ref var em = ref world.Get<SoundEmitterComponent>(root);
            em.SynthRunning = on;
            // Running lives in the definition, which is sent once unless something asks again.
            resendDefinition?.Invoke(root.Id);
        }
        return on ? "You turn the key. The engine starts." : "You switch the engine off.";
    }

    /// <summary>How long held controls survive a silent client before they start decaying, seconds:
    /// longer than a lost packet, so a driver who has gone coasts to a stop.</summary>
    private const float ControlHoldSeconds = 0.75f;

    /// <summary>How fast a thing will reverse, m/s. Reverse is one low gear and a short one.</summary>
    private const float ReverseTopSpeed = 8.0f;

    /// <summary>Below this the car is stopped, not creeping. Stops resistances oscillating about zero.</summary>
    private const float StandstillSpeed = 0.15f;

    private const float AirDensity = 1.225f;

    /// <summary>
    /// Each driven car on its wheels, by entity. Kept here rather than on the component: it is a
    /// simulation, not something the wire or a save carries.
    /// </summary>
    private sealed class Running
    {
        public required string Preset;
        public required WheelDynamics Body;
        public required WheelState[] Wire;
        public byte Surface = RoadSurfaces.IndexOf(RoadData.DefaultSurface);
        /// <summary>The surface under each wheel from the last tick's wheel rays, when the map has a
        /// triangle world; null leaves every wheel on <see cref="Surface"/>.</summary>
        public byte[]? WheelSurfaces;
        public WheelContact[]? Contacts;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, Running> _running = new();

    /// <summary>The map being stepped, for the water on its roads (RoadWaterSystem); null in tests that
    /// do not say, which drive on a dry road.</summary>
    [ThreadStatic] private static string? _mapId;

    private static Running RunningFor(int id, string preset, VehicleProfile profile)
    {
        if (_running.TryGetValue(id, out var r) && r.Preset == preset) return r;
        var body = new WheelDynamics(profile);
        r = new Running { Preset = preset, Body = body, Wire = new WheelState[body.Wheels.Length] };
        _running[id] = r;
        return r;
    }

    /// <summary>A driven car's wheels as last worked out, for the wire.</summary>
    public static bool TryGetWheels(int entityId, out WheelState[]? wheels)
    {
        wheels = _running.TryGetValue(entityId, out var r) ? r.Wire : null;
        return wheels != null;
    }

    /// <summary>How far a body is held off the floor when testing whether it has hit something.</summary>
    private const float GroundClearance = 0.35f;

    /// <summary>
    /// How much of its peak torque an engine absorbs on a closed throttle, at the redline: pumping
    /// losses and friction through the gearing. Without it a car that lifts off takes minutes to stop.
    /// </summary>
    private const float EngineBrakingFraction = 0.15f;

    /// <summary>
    /// Turns one input packet into what the driver is asking for. Back while rolling forward is the
    /// brake; back once stopped is reverse, so there is no gear selector to find.
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
            // A hand off the keys is a lift, not a brake: coasting must sound different from braking.
            drive.Brake = input.Jump ? 1f : 0f;
        }

        drive.SteerTarget = lateral;
        drive.ControlAge = 0f;
    }

    /// <summary>Everything currently under its own power on this map, moved one tick.</summary>
    public static void Update(World world, SpatialGrid<Entity> grid, Vector3 mapMin, Vector3 mapMax, float dt,
                              Action<int, string, IReadOnlyList<TransientSound>>? heard = null, string? mapId = null)
    {
        _mapId = mapId;
        var query = new QueryDescription().WithAll<Transform, DriveComponent, Velocity>();
        var driven = new List<Entity>();
        world.Query(in query, (Entity e, ref Transform _, ref DriveComponent __, ref Velocity ___) => driven.Add(e));

        foreach (var root in driven)
        {
            try { Step(world, grid, root, mapMin, mapMax, dt, heard); }
            catch (Exception ex) { Log.Error(ex, "DrivingSystem: entity {Id} failed to move.", root.Id); }
        }
    }

    private static void Step(World world, SpatialGrid<Entity> grid, Entity root, Vector3 mapMin, Vector3 mapMax,
                             float dt, Action<int, string, IReadOnlyList<TransientSound>>? heard)
    {
        ref var drive = ref world.Get<DriveComponent>(root);
        if (string.IsNullOrEmpty(drive.Preset)) return;
        // Frozen by the admin gun: it does not move until it wears off.
        if (world.Has<OpenFPS.Server.Core.FrozenComponent>(root))
        {
            world.Get<Velocity>(root).Linear = Vector3.Zero;
            return;
        }
        var profile = MachineRegistry.VehicleFor(drive.Preset);

        // A driver gone quiet lifts, then coasts; an instant cut would stutter on packet loss.
        drive.ControlAge += dt;
        if (drive.ControlAge > ControlHoldSeconds)
        {
            float fade = MathF.Max(0f, 1f - (drive.ControlAge - ControlHoldSeconds));
            drive.Throttle *= fade;
            drive.SteerTarget *= fade;
        }
        if (!HasDriver(world, root.Id)) { drive.Throttle = 0f; drive.SteerTarget = 0f; }

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
        // No engine, no drive: the ignition is off, or it is still turning over on the starter.
        if (drive.EngineOn) drive.EngineOnFor += dt;
        if (!drive.EngineOn || drive.EngineOnFor < CrankingSeconds) tractive = 0f;
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
        var running = RunningFor(root.Id, drive.Preset, profile);
        var body = running.Body;
        var chassis = body.Chassis;
        float wheelbase = MathF.Max(0.5f, body.Wheelbase);
        float maxSteer = chassis.MaxSteerAngleRad;

        float target = Math.Clamp(drive.SteerTarget, -1f, 1f);
        bool returning = MathF.Abs(target) < MathF.Abs(drive.Steer) || MathF.Sign(target) != MathF.Sign(drive.Steer);
        float rate = dt / (returning ? SteerSecondsToCentre : SteerSecondsToLock);
        drive.Steer += Math.Clamp(target - drive.Steer, -rate, rate);

        // No further than the tyres can use: the angle whose corner needs all the grip is
        // atan(wheelbase / r), r = v^2 / (grip g).
        float usableLock = maxSteer;
        if (speed > StandstillSpeed)
            usableLock = MathF.Min(maxSteer,
                SteerPastGrip * MathF.Atan(wheelbase * capacity / (speed * speed)));
        float steerAngle = Math.Clamp(drive.Steer, -1f, 1f) * usableLock;

        // ── On its tyres ────────────────────────────────────────────────────────────────────────
        //
        // The wheels decide how much of the asked acceleration and cornering the road gives, each from
        // its own load, slip and surface.
        body.Vx = v;
        body.ForwardOnly = false;
        if (running.WheelSurfaces is { } under && under.Length == body.Wheels.Length)
            for (int i = 0; i < under.Length; i++) body.SetSurface(i, under[i]);
        else body.SetSurface(running.Surface);
        // The water under each wheel where that wheel is: a puddle at the kerb is under the kerb-side wheels.
        if (_mapId != null)
        {
            ref var at = ref world.Get<Transform>(root);
            var fwd = new Vector3(MathF.Sin(drive.Heading), 0f, MathF.Cos(drive.Heading));
            var rgt = new Vector3(MathF.Cos(drive.Heading), 0f, -MathF.Sin(drive.Heading));
            float cog = body.Chassis.CentreOfGravityZ;
            for (int i = 0; i < body.Wheels.Length; i++)
            {
                ref var w = ref body.Wheels[i];
                body.SetWater(i, RoadWaterSystem.WaterAt(_mapId, at.Position + fwd * (w.X + cog) + rgt * w.Y, running.Surface));
            }
        }
        if (MathF.Abs(drive.Throttle) < 0.01f && speed <= StandstillSpeed && MathF.Abs(longitudinal) < 1e-3f)
            body.Halt();
        else
            body.Step(dt, steerAngle, longitudinal);
        float next = body.Vx;
        // Resistance may stop a car but not drag it backwards; and below a crawl with nothing driving
        // it, it is stopped, or it rolls on forever where the resistances stop being applied.
        if (MathF.Abs(drive.Throttle) < 0.01f
            && ((v != 0f && MathF.Sign(next) != MathF.Sign(v)) || MathF.Abs(next) < StandstillSpeed))
        {
            next = 0f;
            body.Halt();
        }
        drive.Speed = next;
        drive.TyreDemand = MathF.Min(2f, body.MaxDemand);

        ref var transform = ref world.Get<Transform>(root);
        float was = drive.Heading;
        var forwardWas = new Vector3(MathF.Sin(was), 0f, MathF.Cos(was));
        var rightWas = new Vector3(MathF.Cos(was), 0f, -MathF.Sin(was));
        drive.Heading = MathHelper.WrapAngle(was + body.TickYaw);
        var heading = new Vector3(MathF.Sin(drive.Heading), 0f, MathF.Cos(drive.Heading));
        var right = new Vector3(MathF.Cos(drive.Heading), 0f, -MathF.Sin(drive.Heading));
        var wanted = transform.Position + forwardWas * body.TickForward + rightWas * body.TickRight;
        // Its own parts and occupants are not the road: a car would climb onto its own floor every tick.
        var aboard = CompositeService.MembersOf(world, root.Id);
        aboard.AddRange(CompositeService.OccupantsOf(world, root.Id));
        wanted.Y = PhysicsUtils.GetGroundHeight(world, grid, wanted, aboard, out string ground);
        running.Surface = RoadSurfaces.IndexOf(ground);
        // On its wheels (docs/GEOMETRY.md 3.11): a ray down at each; the car sits at their mean height and
        // pitches and rolls with them.
        float pitch = 0f, roll = 0f;
        var geometry = TriangleGeometry.Enabled ? grid.Geometry : null;
        if (geometry != null && body.Wheels.Length > 0 && body.Wheels.Length <= 16)
        {
            Span<Vector3> at = stackalloc Vector3[body.Wheels.Length];
            Span<float> along = stackalloc float[body.Wheels.Length], across = stackalloc float[body.Wheels.Length];
            var contacts = running.Contacts ??= new WheelContact[body.Wheels.Length];
            float cog = body.Chassis.CentreOfGravityZ;
            for (int i = 0; i < body.Wheels.Length; i++)
            {
                ref var w = ref body.Wheels[i];
                along[i] = w.X + cog; across[i] = w.Y;
                at[i] = wanted + heading * along[i] + right * across[i];
                at[i].Y = transform.Position.Y;
            }
            var mine = new WheelFilter(aboard, root.Id);
            WheelRays.Contacts(geometry, ref mine, at, PhysicsConstants.StepHeight, contacts);
            var (h, p, r) = WheelRays.Rest(contacts, along, across);
            if (!float.IsNaN(h) && MathF.Abs(h - wanted.Y) <= PhysicsConstants.StepHeight + 0.01f)
            {
                wanted.Y = h; pitch = p; roll = r;
                var surfaces = running.WheelSurfaces ??= new byte[body.Wheels.Length];
                for (int i = 0; i < contacts.Length; i++)
                    surfaces[i] = contacts[i].Found ? RoadSurfaces.IndexOf(contacts[i].Material) : running.Surface;
            }
            else running.WheelSurfaces = null;
        }
        wanted = Vector3.Clamp(wanted, mapMin, mapMax);

        // Hitting something stops it, and is heard (ImpactAcoustics).
        // TODO: the impulse and the damage: a car that hits something should be damaged and push it.
        if (Blocked(world, grid, root, wanted, drive.Heading, out float hitSpeed, out var struck))
        {
            drive.Speed = 0f;
            drive.Throttle = 0f;
            body.Halt();
            if (hitSpeed > ImpactAcoustics.MinimumSpeed && heard != null && struck != null)
                Collision(world, root, struck.Value, wanted, hitSpeed, profile.MassKg, heard);
        }
        else
        {
            transform.Position = wanted;
        }

        // Pitch about the car's own right, nose up positive; roll about its own forward, right side up. A
        // positive pitch in CreateFromYawPitchRoll tips the nose down, so the nose-up pitch goes in negated.
        transform.Rotation = Quaternion.CreateFromYawPitchRoll(drive.Heading, -pitch, roll);
        transform.IsDirty = true;

        ref var velocity = ref world.Get<Velocity>(root);
        velocity.Linear = heading * drive.Speed + right * (drive.Speed == 0f ? 0f : body.Vy);

        for (int i = 0; i < running.Wire.Length; i++)
        {
            ref var w = ref body.Wheels[i];
            running.Wire[i] = WheelState.Encode(w.Load, w.AngularSpeed, w.SlipRatio, w.SlipAngle, w.Surface, w.Demand, w.Water);
        }

        // The client picks its gear from this speed by the same rule as SelectGear.
        if (world.Has<VehicleComponent>(root))
        {
            ref var vehicle = ref world.Get<VehicleComponent>(root);
            vehicle.Speed = drive.Speed;
        }
    }

    /// <summary>The ground a car's wheels stand on: not its own parts, not who is aboard it.</summary>
    private readonly struct WheelFilter : IGeometryFilter
    {
        private readonly List<Entity> _skip;
        private readonly int _root;
        public WheelFilter(List<Entity> skip, int root) { _skip = skip; _root = root; }
        public bool Accept(int owner, in Surface surface)
        {
            if (owner == _root) return false;
            foreach (var e in _skip) if (e.Id == owner) return false;
            return true;
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
    /// The gear a driver would be in at this speed: the lowest not yet past the upshift. The client's
    /// engine processor has the same rule, written twice on purpose: it must reach the answer from the
    /// speed alone. Keep the two in step.
    /// </summary>
    private static int SelectGear(Gearbox gb, float speed)
    {
        for (int g = 1; g <= gb.TopGear; g++)
            if (gb.RpmFor(speed, g) <= gb.UpshiftRpm) return g;
        return gb.TopGear;
    }

    /// <summary>
    /// How much of its peak torque an engine is making at this speed, 0..1: a shallow curve about the
    /// profile's peak, falling away toward the redline. Not a dyno sheet.
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
    /// What the car just hit, heard as any impact is: two materials, two masses, a closing speed and
    /// the size of what was struck, handed to ImpactAcoustics.
    /// </summary>
    private static void Collision(World world, Entity root, Entity struck, Vector3 where, float speed,
                                  float massKg, Action<int, string, IReadOnlyList<TransientSound>> heard)
    {
        var hitter = AcousticRegistry.GetProperties(
            world.Has<MaterialComponent>(root) ? world.Get<MaterialComponent>(root).Material ?? "Metal" : "Metal");
        var target = AcousticRegistry.GetProperties(
            world.Has<MaterialComponent>(struck) ? world.Get<MaterialComponent>(struck).Material ?? "Generic" : "Generic");

        var size = world.Has<ColliderComponent>(struck) ? world.Get<ColliderComponent>(struck).Size : Vector3.One;
        // A fixed thing gives all the energy back; a movable one's mass is its volume times density.
        bool fixedInPlace = !world.Has<Velocity>(struck);
        float struckMass = fixedInPlace
            ? massKg * 50f                       // effectively the planet
            : MathF.Max(1f, size.X * size.Y * size.Z * MathF.Max(100f, target.DensityKgM3));

        var sounds = ImpactAcoustics.Between(hitter, target, where, speed, massKg, struckMass,
                                             size.X, size.Y, MathF.Max(0.01f, size.Z), fixedInPlace);
        if (sounds.Count > 0) heard(root.Id, "impact", sounds);
    }

    /// <summary>
    /// Whether the body would be inside something solid at a proposed position. Sampled at the nose,
    /// middle and tail, because a car is long; its own parts and occupants are not obstacles.
    /// </summary>
    private static bool Blocked(World world, SpatialGrid<Entity> grid, Entity root, Vector3 position,
                                float heading, out float closingSpeed, out Entity? struck)
    {
        struck = null;
        closingSpeed = MathF.Abs(world.Get<DriveComponent>(root).Speed);
        if (!world.Has<ColliderComponent>(root)) return false;

        var size = world.Get<ColliderComponent>(root).Size;
        float radius = MathF.Max(0.3f, MathF.Min(size.X, size.Z) * 0.5f);
        // Lifted clear of the floor, as the player's check is, or the car is blocked by its own road.
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
                {
                    struck = other;
                    return true;
                }
            }
        }
        return false;
    }
}

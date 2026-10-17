using System.Numerics;
using OpenFPS.Common.Geometry;
using OpenFPS.Common.Networking;

namespace OpenFPS.Common;

/// <summary>
/// One map's driven cars on their wheels (docs/PHYSICS_BOUNDARY.md stage 3): what a car does with what its
/// driver asks for. The engine's pull through the gear its speed puts it in, the air, the road and the brakes
/// holding it back, the steering held under the angle that uses all the grip, the per-wheel tyres
/// (<see cref="WheelDynamics"/>), the pose on the four wheel rays, and the test that stops it against
/// whatever it would drive into. The host keeps the controls (how keys become throttle, brake and the
/// wheel's turn), the ignition, the tile fences and the hearing of an impact.
///
/// <para>One per map: each map is its own world with its own entity ids, so a car is known by its id and the
/// version of that id. Moved out of the server's DrivingSystem line for line; the driving replay holds it to
/// the tracks it made there.</para>
/// </summary>
public sealed class VehicleStep
{
    /// <summary>How far past the angle that uses all the tyres' grip the wheel may be turned at speed: a little
    /// over, so holding the key into a bend reaches the edge and the tyres say so.</summary>
    public const float SteerPastGrip = 1.15f;

    /// <summary>How fast a thing will reverse, m/s. Reverse is one low gear and a short one.</summary>
    public const float ReverseTopSpeed = 8.0f;

    /// <summary>Below this the car is stopped, not creeping. Stops resistances oscillating about zero.</summary>
    public const float StandstillSpeed = 0.15f;

    /// <summary>How far a body is held off the floor when testing whether it has hit something.</summary>
    public const float GroundClearance = 0.35f;

    /// <summary>How much of its peak torque an engine absorbs on a closed throttle, at the redline: pumping
    /// losses and friction through the gearing. Without it a car that lifts off takes minutes to stop.</summary>
    public const float EngineBrakingFraction = 0.15f;

    /// <summary>One car on its wheels. A simulation, not something the wire or a save carries.</summary>
    public sealed class Car
    {
        public required string Preset;
        /// <summary>The entity's version: an id taken again by a new car is a new simulation.</summary>
        public required int Version;
        public required WheelDynamics Body;
        /// <summary>Each wheel as the wire carries it, as last worked out.</summary>
        public required WheelState[] Wire;
        public byte Surface = RoadSurfaces.IndexOf(RoadSurfaces.Default);
        /// <summary>The surface under each wheel from the last step's wheel rays, when the map has a triangle
        /// world; null leaves every wheel on <see cref="Surface"/>.</summary>
        public byte[]? WheelSurfaces;
        public WheelContact[]? Contacts;
    }

    private readonly Dictionary<int, Car> _cars = new();

    /// <summary>The cars being stepped.</summary>
    public int Count => _cars.Count;

    /// <summary>The car of entity <paramref name="id"/> (at <paramref name="version"/>) driving
    /// <paramref name="preset"/>, made on its wheels if it is new, or another car now has its id, or it was
    /// given another preset.</summary>
    public Car For(int id, int version, string preset, VehicleMechanics mechanics)
    {
        if (_cars.TryGetValue(id, out var car) && car.Preset == preset && car.Version == version) return car;
        var body = new WheelDynamics(mechanics);
        car = new Car { Preset = preset, Version = version, Body = body, Wire = new WheelState[body.Wheels.Length] };
        _cars[id] = car;
        return car;
    }

    /// <summary>A car being stepped, if <paramref name="id"/> at <paramref name="version"/> is one.</summary>
    public bool TryGet(int id, int version, out Car? car)
    {
        car = _cars.TryGetValue(id, out var c) && c.Version == version ? c : null;
        return car != null;
    }

    /// <summary>Forgets every car <paramref name="driven"/> does not say is still driven, so its id is free for
    /// whatever is made next.</summary>
    public void Keep(Func<int, int, bool> driven)
    {
        if (_cars.Count == 0) return;
        List<int>? gone = null;
        foreach (var (id, car) in _cars)
            if (!driven(id, car.Version)) (gone ??= new List<int>()).Add(id);
        if (gone != null) foreach (int id in gone) _cars.Remove(id);
    }

    /// <summary>The most a car can do on its tyres in any direction, m/s².</summary>
    public static float Capacity(VehicleMechanics mechanics, float gravity) => MathF.Max(0.1f, mechanics.Tyres.PeakGripG) * gravity;

    /// <summary>What a car is told for one step: where it is, what its driver has on the pedals and the wheel,
    /// the world's gravity and air, and what it must not stand on or drive into.</summary>
    public struct Input
    {
        /// <summary>The car's entity, and its parts and the people in it: not the road, not an obstacle.</summary>
        public int Self;
        public IReadOnlyList<int> Aboard;
        public Vector3 Position;
        /// <summary>Its speed, heading and the steering wheel's position (already turned by the hands this
        /// step) as the last step left them.</summary>
        public float Speed, Heading, Steer;
        public float Throttle, Brake;
        /// <summary>Whether the engine is running and has caught: no engine, no drive.</summary>
        public bool EngineDrives;
        public float Gravity, AirDensity;
        public Vector3 MapMin, MapMax;
        /// <summary>Its collision box (width, height, length), or null for a car with no collider, which is
        /// never stopped.</summary>
        public Vector3? Size;
        /// <summary>What the car is made of, for its contacts.</summary>
        public string Surface;
        /// <summary>The water on the road at a point over a surface (mm), or null for a dry road.</summary>
        public Func<Vector3, byte, float>? WaterAt;
        /// <summary>Whether the car may go from where it is to where its wheels would take it (the world's
        /// tiles not built yet hold it), or null for anywhere.</summary>
        public Func<Vector3, Vector3, bool>? MayGo;
    }

    /// <summary>What one step left: the car's state, where it is now and how it is turned and moving, whether it
    /// was held where it was, and what it struck.</summary>
    public struct Output
    {
        public VehicleState State;
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 Velocity;
        /// <summary>Held where it was by <see cref="Input.MayGo"/>.</summary>
        public bool Held;
        /// <summary>Stopped dead against something: what, at what speed, and the contact.</summary>
        public ContactEvent? Struck;
    }

    /// <summary>One step of <paramref name="car"/> with <paramref name="mechanics"/> in <paramref name="world"/>.</summary>
    public Output Step(Car car, VehicleMechanics mechanics, in Input input, IPhysicsWorld world, float dt)
    {
        float gravity = input.Gravity;
        float throttle = input.Throttle, brake = input.Brake;
        float v = input.Speed;
        float speed = MathF.Abs(v);
        float mass = MathF.Max(1f, mechanics.MassKg);
        float capacity = Capacity(mechanics, gravity);

        // ── What the engine is pulling ──────────────────────────────────────────────────────────────
        var gb = mechanics.Gearbox!;
        var engine = mechanics.Engine;
        int gear = SelectGear(gb, speed);
        float rpm = MathF.Max(engine.IdleRpm, gb.RpmFor(speed, gear));
        float torque = engine.PeakTorqueNm * TorqueFraction(rpm, engine);
        float ratio = gb.Ratios[gear - 1] * gb.FinalDrive;
        float tractive = MathF.Abs(throttle) * torque * ratio / MathF.Max(0.05f, gb.WheelRadiusMetres);
        if (!input.EngineDrives) tractive = 0f;
        // Reverse is geared low and runs out early — you cannot reverse a car up to its top speed.
        if (throttle < 0f && speed > ReverseTopSpeed) tractive = 0f;

        // ── What is holding it back ─────────────────────────────────────────────────────────────────
        float drag = 0.5f * input.AirDensity * mechanics.DragArea * v * v;
        float rolling = mechanics.RollingResistance * mass * gravity;
        float braking = Math.Clamp(brake, 0f, 1f) * capacity * mass;
        float engineBraking = (1f - MathF.Abs(throttle))
                            * engine.PeakTorqueNm * EngineBrakingFraction
                            * (rpm / MathF.Max(1f, engine.RedlineRpm))
                            * ratio / MathF.Max(0.05f, gb.WheelRadiusMetres);

        float longitudinal = MathF.Sign(throttle) * tractive / mass;
        if (speed > StandstillSpeed)
            longitudinal -= MathF.Sign(v) * (drag + rolling + braking + engineBraking) / mass;
        else if (MathF.Abs(throttle) < 0.01f) longitudinal = 0f;

        // ── How far the wheel is turned ─────────────────────────────────────────────────────────────
        var body = car.Body;
        body.Gravity = gravity;
        float wheelbase = MathF.Max(0.5f, body.Wheelbase);
        float maxSteer = body.Chassis.MaxSteerAngleRad;
        // No further than the tyres can use: the angle whose corner needs all the grip is
        // atan(wheelbase / r), r = v^2 / (grip g).
        float usableLock = maxSteer;
        if (speed > StandstillSpeed)
            usableLock = MathF.Min(maxSteer, SteerPastGrip * MathF.Atan(wheelbase * capacity / (speed * speed)));
        float steerAngle = Math.Clamp(input.Steer, -1f, 1f) * usableLock;

        // ── On its tyres ────────────────────────────────────────────────────────────────────────────
        //
        // The wheels decide how much of the asked acceleration and cornering the road gives, each from its own
        // load, slip and surface.
        body.Vx = v;
        body.ForwardOnly = false;
        if (car.WheelSurfaces is { } under && under.Length == body.Wheels.Length)
            for (int i = 0; i < under.Length; i++) body.SetSurface(i, under[i]);
        else body.SetSurface(car.Surface);
        // The water under each wheel where that wheel is: a puddle at the kerb is under the kerb-side wheels.
        if (input.WaterAt is { } waterAt)
        {
            var fwd = new Vector3(MathF.Sin(input.Heading), 0f, MathF.Cos(input.Heading));
            var rgt = new Vector3(MathF.Cos(input.Heading), 0f, -MathF.Sin(input.Heading));
            float cog = body.Chassis.CentreOfGravityZ;
            for (int i = 0; i < body.Wheels.Length; i++)
            {
                ref var w = ref body.Wheels[i];
                body.SetWater(i, waterAt(input.Position + fwd * (w.X + cog) + rgt * w.Y, car.Surface));
            }
        }
        if (MathF.Abs(throttle) < 0.01f && speed <= StandstillSpeed && MathF.Abs(longitudinal) < 1e-3f)
            body.Halt();
        else
            body.Step(dt, steerAngle, longitudinal);
        float next = body.Vx;
        // Resistance may stop a car but not drag it backwards; and below a crawl with nothing driving it, it is
        // stopped, or it rolls on forever where the resistances stop being applied.
        if (MathF.Abs(throttle) < 0.01f
            && ((v != 0f && MathF.Sign(next) != MathF.Sign(v)) || MathF.Abs(next) < StandstillSpeed))
        {
            next = 0f;
            body.Halt();
        }
        float newSpeed = next;
        float demand = MathF.Min(2f, body.MaxDemand);

        // ── Where its wheels take it ────────────────────────────────────────────────────────────────
        float was = input.Heading;
        var forwardWas = new Vector3(MathF.Sin(was), 0f, MathF.Cos(was));
        var rightWas = new Vector3(MathF.Cos(was), 0f, -MathF.Sin(was));
        float newHeading = MathHelper.WrapAngle(was + body.TickYaw);
        var heading = new Vector3(MathF.Sin(newHeading), 0f, MathF.Cos(newHeading));
        var right = new Vector3(MathF.Cos(newHeading), 0f, -MathF.Sin(newHeading));
        var wanted = input.Position + forwardWas * body.TickForward + rightWas * body.TickRight;
        // Its own parts and occupants are not the road: a car would climb onto its own floor every tick.
        var scratch = _bodies;
        wanted.Y = Footing.GroundOn(world, wanted, int.MinValue, input.Aboard, scratch, out string ground);
        car.Surface = RoadSurfaces.IndexOf(ground);
        // On its wheels (docs/GEOMETRY.md 3.11): a ray down at each; the car sits at their mean height and
        // pitches and rolls with them.
        float pitch = 0f, roll = 0f;
        var geometry = world.Triangles;
        if (geometry != null && body.Wheels.Length > 0 && body.Wheels.Length <= 16)
        {
            Span<Vector3> at = stackalloc Vector3[body.Wheels.Length];
            Span<float> along = stackalloc float[body.Wheels.Length], across = stackalloc float[body.Wheels.Length];
            var contacts = car.Contacts ??= new WheelContact[body.Wheels.Length];
            float cog = body.Chassis.CentreOfGravityZ;
            for (int i = 0; i < body.Wheels.Length; i++)
            {
                ref var w = ref body.Wheels[i];
                along[i] = w.X + cog; across[i] = w.Y;
                at[i] = wanted + heading * along[i] + right * across[i];
                at[i].Y = input.Position.Y;
            }
            // The ground a car's wheels stand on: not its own parts, not who is aboard it.
            var mine = new BodyFilter(world, input.Self, input.Aboard);
            WheelRays.Contacts(geometry, ref mine, at, BodyConstants.StepHeight, contacts);
            var (h, p, r) = WheelRays.Rest(contacts, along, across);
            if (!float.IsNaN(h) && MathF.Abs(h - wanted.Y) <= BodyConstants.StepHeight + 0.01f)
            {
                wanted.Y = h; pitch = p; roll = r;
                var surfaces = car.WheelSurfaces ??= new byte[body.Wheels.Length];
                for (int i = 0; i < contacts.Length; i++)
                    surfaces[i] = contacts[i].Found ? RoadSurfaces.IndexOf(contacts[i].Material) : car.Surface;
            }
            else car.WheelSurfaces = null;
        }
        wanted = Vector3.Clamp(wanted, input.MapMin, input.MapMax);

        var output = new Output();
        // The last resort, when the brakes could not do it (a tile let go under it, or faster than the world is
        // built): held where it is, never driven onto ground that is not there.
        if (input.MayGo is { } mayGo && !mayGo(input.Position, wanted))
        {
            newSpeed = 0f;
            throttle = 0f;
            body.Halt();
            output.Held = true;
            wanted = input.Position;
        }

        // Hitting something stops it dead (stage 4 exchanges the momentum and damages it).
        var position = wanted;
        if (input.Size is { } size && Blocked(world, input, size, wanted, newHeading, out var struck))
        {
            float closing = MathF.Abs(newSpeed);
            var way = MathF.Sign(newSpeed) is var sign && sign != 0f ? heading * sign : heading;
            output.Struck = new ContactEvent(input.Self, struck.Id, wanted, -way, mass * closing, closing, 0f,
                                             string.IsNullOrEmpty(input.Surface) ? "Generic" : input.Surface, struck.Material,
                                             ContactPhase.Began);
            newSpeed = 0f;
            throttle = 0f;
            body.Halt();
            position = input.Position;
        }

        for (int i = 0; i < car.Wire.Length; i++)
        {
            ref var w = ref body.Wheels[i];
            car.Wire[i] = WheelState.Encode(w.Load, w.AngularSpeed, w.SlipRatio, w.SlipAngle, w.Surface, w.Demand, w.Water);
        }

        // Pitch about the car's own right, nose up positive; roll about its own forward, right side up. A
        // positive pitch in CreateFromYawPitchRoll tips the nose down, so the nose-up pitch goes in negated.
        output.Position = position;
        output.Rotation = Quaternion.CreateFromYawPitchRoll(newHeading, -pitch, roll);
        output.Velocity = heading * newSpeed + right * (newSpeed == 0f ? 0f : body.Vy);
        output.State = new VehicleState
        {
            Speed = newSpeed,
            Heading = newHeading,
            YawRate = dt > 0f ? body.TickYaw / dt : 0f,
            LongitudinalAcceleration = dt > 0f ? (newSpeed - v) / dt : 0f,
            LateralAcceleration = newSpeed * (dt > 0f ? body.TickYaw / dt : 0f),
            Steer = input.Steer,
            Throttle = throttle,
            Brake = brake,
            Gear = gear,
            Rpm = rpm,
            Clutch = 1f,
            LoadTorqueNm = MathF.Abs(throttle) > 0f && tractive > 0f
                ? MathF.Abs(throttle) * torque
                : -(1f - MathF.Abs(throttle)) * engine.PeakTorqueNm * EngineBrakingFraction * (rpm / MathF.Max(1f, engine.RedlineRpm)),
            TyreDemand = demand,
            Wheels = car.Wire,
        };
        return output;
    }

    private readonly List<PhysicsBody> _bodies = new(32);

    /// <summary>
    /// The gear a driver would be in at this speed: the lowest not yet past the upshift. The client's engine
    /// processor has the same rule, written twice on purpose: it must reach the answer from the speed alone.
    /// Keep the two in step.
    /// </summary>
    public static int SelectGear(Gearbox gb, float speed)
    {
        for (int g = 1; g <= gb.TopGear; g++)
            if (gb.RpmFor(speed, g) <= gb.UpshiftRpm) return g;
        return gb.TopGear;
    }

    /// <summary>
    /// How much of its peak torque an engine is making at this speed, 0..1: a shallow curve about the engine's
    /// peak, falling away toward the redline. Not a dyno sheet: stage 6 takes the engine's own mean torque.
    /// </summary>
    public static float TorqueFraction(float rpm, EngineMechanics e)
    {
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
    /// Whether the car would be inside something solid at a proposed position, and what. Sampled at the nose,
    /// middle and tail, because a car is long; its own parts and occupants are not obstacles. Every solid is
    /// met as a box, those the triangles hold too (stage 4 meets them as triangles with the car as a mesh).
    /// </summary>
    private bool Blocked(IPhysicsWorld world, in Input input, Vector3 size, Vector3 position, float heading, out PhysicsBody struck)
    {
        struck = default;
        float radius = MathF.Max(0.3f, MathF.Min(size.X, size.Z) * 0.5f);
        // Lifted clear of the floor, as the player's check is, or the car is blocked by its own road.
        float clearance = MathF.Min(GroundClearance, size.Y * 0.5f);
        float height = MathF.Max(0.3f, size.Y - clearance);
        var forward = new Vector3(MathF.Sin(heading), 0f, MathF.Cos(heading));
        float reach = MathF.Max(0f, size.Z * 0.5f - radius);
        var skip = new BodyFilter { Own = input.Self, Ignore = input.Aboard is { Count: > 0 } ? input.Aboard : null };

        Span<float> samples = stackalloc float[] { reach, 0f, -reach };
        foreach (float along in samples)
        {
            var at = position + forward * along;
            var centre = at + new Vector3(0f, clearance + height * 0.5f, 0f);
            world.Boxes(at, radius + 3f, _bodies);
            foreach (var other in _bodies)
            {
                if (skip.Ignores(other.Id)) continue;
                var local = Vector3.Transform(centre - other.Centre, Matrix4x4.CreateFromQuaternion(Quaternion.Inverse(other.Rotation)));
                if (GeometryUtils.AABBIntersectsCylinder(-other.Size / 2f, other.Size / 2f, local, radius, height))
                {
                    struck = other;
                    return true;
                }
            }
        }
        return false;
    }
}

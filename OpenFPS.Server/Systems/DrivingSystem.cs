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
/// A composite with somebody in its driving seat. No handling numbers live here: what the car does is the
/// physics' (<see cref="VehicleStep"/>, one per map: pull, grip and top speed from the preset's engine,
/// gearbox, tyres and drag, the per-wheel model traffic stands on, the pose and the collision test). Here are
/// how a player's two input axes become throttle, brake and the wheel's turn, the ignition, the world's tiles
/// not built yet, and what is heard when the car strikes something.
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

    /// <summary>Below this the car is stopped, not creeping (<see cref="VehicleStep.StandstillSpeed"/>): back
    /// while rolling forward is the brake, back once stopped is reverse.</summary>
    private const float StandstillSpeed = VehicleStep.StandstillSpeed;

    /// <summary>
    /// Each world's driven cars (<see cref="VehicleStep"/>, the physics' one per map) and which of them the
    /// driver has been told is held at a tile not built yet. By world, because ids start again in every map's
    /// world, and a world that goes takes its cars with it.
    /// </summary>
    private sealed class MapCars
    {
        public readonly VehicleStep Step = new();
        public readonly HashSet<int> AtEdge = new();
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<World, MapCars> _maps = new();

    /// <summary>The map being stepped, for the water on its roads (RoadWaterSystem); null in tests that
    /// do not say, which drive on a dry road.</summary>
    [ThreadStatic] private static string? _mapId;

    /// <summary>The water on the map's roads at a point, for the wheels: the map being stepped's.</summary>
    private static readonly Func<Vector3, byte, float> WaterOnTheRoad = (at, surface) => RoadWaterSystem.WaterAt(_mapId!, at, surface);

    /// <summary>A driven car's wheels as last worked out, for the wire.</summary>
    public static bool TryGetWheels(World world, Entity car, out WheelState[]? wheels)
    {
        wheels = _maps.TryGetValue(world, out var cars) && cars.Step.TryGet(car.Id, car.Version, out var c) ? c!.Wire : null;
        return wheels != null;
    }

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

    /// <summary>On the world, which tiles are built (docs/WORLD_STREAMING.md, At an edge that is not ready):
    /// a car is braked to a stop short of one that is not, and held at its edge if it gets there.</summary>
    public readonly record struct TileFence(Func<TileKey, bool> Ready, float TileMetres);

    /// <summary>Metres kept between a car and a tile not built yet when it is braked to a stop for it: half
    /// a long car and a little more.</summary>
    public const float EdgeMarginMetres = 4f;

    /// <summary>Everything currently under its own power on this map, moved one tick. <paramref name="stopped"/>
    /// hears of a car stopped (or held) for a tile not built yet, once per stop.</summary>
    public static void Update(World world, SpatialGrid<Entity> grid, Vector3 mapMin, Vector3 mapMax, float dt,
                              Action<int, string, IReadOnlyList<TransientSound>>? heard = null, string? mapId = null,
                              TileFence? fence = null, Action<int>? stopped = null, Air? air = null)
    {
        _mapId = mapId;
        float gravity = PhysicsConstants.GravityOf(world);
        float airDensity = (air ?? Air.Standard).Density;
        var query = new QueryDescription().WithAll<Transform, DriveComponent, Velocity>();
        var driven = new List<Entity>();
        world.Query(in query, (Entity e, ref Transform _, ref DriveComponent __, ref Velocity ___) => driven.Add(e));

        var cars = _maps.GetValue(world, static _ => new MapCars());
        var view = ServerPhysicsView.Of(world, grid);
        var contacts = ContactStreams.Of(world);
        foreach (var root in driven)
        {
            try { Step(world, view, cars, root, mapMin, mapMax, dt, gravity, airDensity, heard, fence, stopped, contacts); }
            catch (Exception ex) { Log.Error(ex, "DrivingSystem: entity {Id} failed to move.", root.Id); }
        }

        // A car destroyed or no longer driven is forgotten, so its id is free for whatever is made next.
        cars.Step.Keep((id, version) => driven.Exists(e => e.Id == id && e.Version == version));
        cars.AtEdge.RemoveWhere(id => !driven.Exists(e => e.Id == id));
    }

    /// <summary>Whether a tile not built yet lies within <paramref name="metres"/> of <paramref name="at"/>
    /// along <paramref name="direction"/> (a flat unit vector).</summary>
    internal static bool UnbuiltWithin(Vector3 at, Vector3 direction, float metres, TileFence fence)
    {
        float step = MathF.Max(1f, fence.TileMetres / 8f);
        for (float s = 0f; ; s = MathF.Min(metres, s + step))
        {
            if (!fence.Ready(TileKey.Of(at + direction * s, fence.TileMetres))) return true;
            if (s >= metres) return false;
        }
    }

    private static void Step(World world, ServerPhysicsView view, MapCars cars, Entity root, Vector3 mapMin, Vector3 mapMax,
                             float dt, float gravity, float airDensity, Action<int, string, IReadOnlyList<TransientSound>>? heard,
                             TileFence? fence, Action<int>? stopped, ContactStream contacts)
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
        var mechanics = profile.Mechanics;

        // A driver gone quiet lifts, then coasts; an instant cut would stutter on packet loss.
        drive.ControlAge += dt;
        if (drive.ControlAge > ControlHoldSeconds)
        {
            float fade = MathF.Max(0f, 1f - (drive.ControlAge - ControlHoldSeconds));
            drive.Throttle *= fade;
            drive.SteerTarget *= fade;
        }
        if (!HasDriver(world, root.Id)) { drive.Throttle = 0f; drive.SteerTarget = 0f; }

        // No engine, no drive: the ignition is off, or it is still turning over on the starter.
        if (drive.EngineOn) drive.EngineOnFor += dt;
        bool engineDrives = drive.EngineOn && drive.EngineOnFor >= CrankingSeconds;

        float v = drive.Speed;
        float speed = MathF.Abs(v);
        var car = cars.Step.For(root.Id, root.Version, drive.Preset, mechanics);
        ref var transform = ref world.Get<Transform>(root);

        // On the world, braked to a stop short of ground not built yet: its stopping distance at 80 % of the
        // tyres' grip, two ticks' travel and the margin, looked along the way it is going. Kept braking while
        // that holds, whatever the driver asks; backing away is never stopped.
        float going = speed > StandstillSpeed ? MathF.Sign(v) : MathF.Sign(drive.Throttle);
        if (fence is { } f && going != 0f)
        {
            var way = new Vector3(MathF.Sin(drive.Heading), 0f, MathF.Cos(drive.Heading)) * going;
            float stop = speed * speed / (2f * 0.8f * VehicleStep.Capacity(mechanics, gravity)) + 2f * speed * dt + EdgeMarginMetres;
            var here = transform.Position;
            if (UnbuiltWithin(here, way, stop, f))
            {
                drive.Throttle = 0f;
                drive.Brake = 1f;
                if (cars.AtEdge.Add(root.Id)) stopped?.Invoke(root.Id);
            }
            // Told again only after it has been well clear of the edge.
            else if (cars.AtEdge.Contains(root.Id) && !UnbuiltWithin(here, way, stop + 20f, f)) cars.AtEdge.Remove(root.Id);
        }

        // The hands on the wheel: toward where the keys hold it, back to the centre faster.
        float target = Math.Clamp(drive.SteerTarget, -1f, 1f);
        bool returning = MathF.Abs(target) < MathF.Abs(drive.Steer) || MathF.Sign(target) != MathF.Sign(drive.Steer);
        float rate = dt / (returning ? SteerSecondsToCentre : SteerSecondsToLock);
        drive.Steer += Math.Clamp(target - drive.Steer, -rate, rate);

        // Its own parts and occupants are not the road, nor anything it can drive into.
        var aboard = new List<int>();
        foreach (var e in CompositeService.MembersOf(world, root.Id)) aboard.Add(e.Id);
        foreach (var e in CompositeService.OccupantsOf(world, root.Id)) aboard.Add(e.Id);

        var input = new VehicleStep.Input
        {
            Self = root.Id,
            Aboard = aboard,
            Position = transform.Position,
            Speed = v,
            Heading = drive.Heading,
            Steer = drive.Steer,
            Throttle = drive.Throttle,
            Brake = drive.Brake,
            EngineDrives = engineDrives,
            Gravity = gravity,
            AirDensity = airDensity,
            MapMin = mapMin,
            MapMax = mapMax,
            Size = world.Has<ColliderComponent>(root) ? world.Get<ColliderComponent>(root).Size : null,
            Surface = world.Has<MaterialComponent>(root) ? world.Get<MaterialComponent>(root).Material ?? "Metal" : "Metal",
            WaterAt = _mapId != null ? WaterOnTheRoad : null,
            // The last resort, when the brakes could not do it (a tile let go under it, or faster than the
            // world is built): held where it is, never driven onto ground that is not there.
            MayGo = fence is { } held ? (from, to) => held.Ready(TileKey.Of(to, held.TileMetres)) || !held.Ready(TileKey.Of(from, held.TileMetres)) : null,
        };
        var output = cars.Step.Step(car, mechanics, input, view, dt);

        // What the car did, onto the components the wire carries.
        var state = output.State;
        drive.Speed = state.Speed;
        drive.Heading = state.Heading;
        drive.Throttle = state.Throttle;
        drive.TyreDemand = state.TyreDemand;
        if (output.Held && cars.AtEdge.Add(root.Id)) stopped?.Invoke(root.Id);
        transform.Position = output.Position;
        transform.Rotation = output.Rotation;
        transform.IsDirty = true;
        world.Get<Velocity>(root).Linear = output.Velocity;

        // Hitting something stops it, and is heard (ImpactAcoustics).
        if (output.Struck is { } struck)
        {
            var contact = contacts.Add(struck);
            if (contact.ClosingSpeed > ImpactAcoustics.MinimumSpeed && heard != null && view.TryEntity(contact.Other, out var hit))
                Collision(world, root, hit, contact.Point, contact.ClosingSpeed, profile.MassKg, heard);
        }

        // The client picks its gear from this speed by the same rule as VehicleStep.SelectGear.
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
}

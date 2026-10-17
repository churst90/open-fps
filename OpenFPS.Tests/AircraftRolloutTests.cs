using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using OpenFPS.Server.Systems;
using Xunit.Abstractions;
using OpenFPS.Sound;

namespace OpenFPS.Tests;

/// <summary>
/// "Aircraft roll out after landing instead of reversing at the end of the runway." It lands, rolls out on its
/// type's figures, turns round on the runway and takes off back up the line; an approach used to stop in the
/// air over the runway point and spin round.
/// </summary>
public class AircraftRolloutTests
{
    private readonly ITestOutputHelper _o;
    public AircraftRolloutTests(ITestOutputHelper o) => _o = o;

    [Theory]
    [InlineData("piston_single")]
    [InlineData("turboprop")]
    [InlineData("airliner")]
    public void TheGroundRunRollsOutTurnsRoundAndLiftsOffWhereItTouchedDown(string preset)
    {
        var air = AircraftProfile.ByName(preset);
        var spec = air.Ground!;
        var touchdown = new Vector3(0f, 0.1f, 0f);
        var run = new AircraftGroundRun(spec, touchdown, Vector3.UnitZ, spec.TouchdownSpeedMps, holdSeconds: 5f);
        float dt = 1f / 30f, t = 0f, lastHeading = run.Heading, maxTurnRate = 0f, maxLateral = 0f, farthest = 0f;
        float rolloutEnd = -1f, rolloutDist = 0f, holdAt = -1f;
        var phases = new List<AircraftGroundRun.Phase>();
        while (run.State != AircraftGroundRun.Phase.Done && t < 900f)
        {
            var before = run.State;
            run.Update(dt);
            t += dt;
            if (phases.Count == 0 || phases[^1] != run.State) phases.Add(run.State);
            if (before == AircraftGroundRun.Phase.Rollout && run.State != before) { rolloutEnd = t; rolloutDist = run.Position.Z; }
            if (run.State == AircraftGroundRun.Phase.Hold && holdAt < 0f) holdAt = t;
            float dh = MathF.Abs(MathF.IEEERemainder(run.Heading - lastHeading, 2f * MathF.PI));
            maxTurnRate = MathF.Max(maxTurnRate, dh / dt);
            lastHeading = run.Heading;
            maxLateral = MathF.Max(maxLateral, MathF.Abs(run.Position.X));
            farthest = MathF.Max(farthest, run.Position.Z);
            Assert.Equal(touchdown.Y, run.Position.Y, 3);
        }
        _o.WriteLine($"{preset}: phases {string.Join(" > ", phases)}; roll-out {rolloutDist:F0} m in {rolloutEnd:F1} s, "
                   + $"farthest {farthest:F0} m, widest {maxLateral:F1} m, hold at {holdAt:F1} s, off at {t:F1} s at {run.Speed:F1} m/s, "
                   + $"turn rate up to {maxTurnRate * 180f / MathF.PI:F1} deg/s");
        Assert.Equal(AircraftGroundRun.Phase.Done, run.State);
        // The landing roll is the type's: down to taxi speed in its published distance.
        Assert.InRange(rolloutDist, spec.LandingRollMetres * 0.9f, spec.LandingRollMetres * 1.1f);
        // Turned round within its radius of the centreline, smoothly (no spin on the spot).
        Assert.InRange(maxLateral, spec.TurnRadiusMetres * 0.9f, spec.TurnRadiusMetres * 1.1f);
        // Its turning speed round its radius: v / R, under 30 degrees a second.
        float expected = spec.TurnSpeedMps / spec.TurnRadiusMetres * 180f / MathF.PI;
        Assert.True(maxTurnRate * 180f / MathF.PI < MathF.Max(30f, expected * 1.3f), "it turned faster than a wheel on the ground can");
        // ...and lifted off at rotation speed where it touched down, facing back up the approach.
        Assert.InRange(run.Position.Z, -0.5f, 0.5f);
        Assert.InRange(run.Speed, spec.RotateSpeedMps * 0.97f, spec.RotateSpeedMps * 1.05f);
        Assert.True(MathF.Cos(run.Heading) < -0.99f, "it takes off the way it came");
        Assert.True(holdAt > 0f);
    }

    [Fact]
    public void AnApproachOnTheMapLandsInsteadOfStoppingInTheAir()
    {
        var prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
        var maps = new MapManager(new MapRepository(Path.Combine(AppContext.BaseDirectory, "maps")), prefabs);
        maps.Initialize();
        Assert.True(maps.TryGetMap("city", out var world, out _, out _, out _));
        var vehicles = new VehicleSystem();
        var air = AircraftProfile.ByName("piston_single");
        var from = new Vector3(380f, 90f, -1700f);
        var to = new Vector3(380f, 0.1f, -250f);
        var e = vehicles.SpawnOne(maps, null, "city", new VehicleData
        {
            Name = "Test single", Preset = "piston_single", RoadStart = from, RoadEnd = to,
            SpeedsKmh = new[] { air.ApproachSpeedMps * 3.6f, 160f }, AccelerationMps2 = 1.5f, BrakingMps2 = 2f,
            WaitSeconds = 6f,
        });
        Assert.NotEqual(Entity.Null, e);
        float dt = 1f / 30f, t = 0f;
        float touchdownAt = -1f, slowestAirborne = float.MaxValue, beyond = 0f, liftoffAt = -1f;
        bool onGround = false;
        Vector3 last = world.Get<Transform>(e).Position;
        while (t < 400f && liftoffAt < 0f)
        {
            vehicles.Update("city", world, dt);
            t += dt;
            var p = world.Get<Transform>(e).Position;
            float speed = world.Get<Velocity>(e).Linear.Length();
            bool ground = p.Y < 0.2f;
            if (ground && !onGround && touchdownAt < 0f) touchdownAt = t;
            // The last 400 m of the approach (the shuttle starts its leg from rest, far out).
            if (!ground && touchdownAt < 0f && p.Z > to.Z - 400f) slowestAirborne = MathF.Min(slowestAirborne, speed);
            if (ground) beyond = MathF.Max(beyond, p.Z - to.Z);
            if (onGround && !ground && touchdownAt > 0f) liftoffAt = t;
            onGround = ground;
            last = p;
        }
        _o.WriteLine($"touchdown at {touchdownAt:F1} s, slowest in the air before it {slowestAirborne:F1} m/s, "
                   + $"rolled {beyond:F0} m past the touchdown point, lifted off at {liftoffAt:F1} s at {last}");
        Assert.True(touchdownAt > 0f, "it never reached the runway");
        // It flew the approach: nowhere in the air did it slow below its touchdown speed.
        Assert.True(slowestAirborne >= air.Ground!.TouchdownSpeedMps * 0.95f);
        // It rolled on down the runway after touching down, rather than stopping on the spot.
        Assert.True(beyond > air.Ground.LandingRollMetres * 0.8f);
        Assert.True(liftoffAt > touchdownAt, "it never took off again");
        Assert.InRange(last.Z, to.Z - 30f, to.Z + 5f);
    }

    /// <summary>
    /// An aeroplane's power lever is worked out by the server that flies it and sent (audit P7). A piston single
    /// down an approach and back out: on the way down its lever is its flight path's, low; within a wingspan of
    /// the runway its throttle is closed; on its wheels it idles until the take-off roll, which is everything;
    /// and it climbs out on full power. The client guessed all of it from the motion.
    /// </summary>
    [Fact]
    public void AnAeroplanesLeverIsWhatItIsBeingFlownThrough()
    {
        var prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
        var maps = new MapManager(new MapRepository(Path.Combine(AppContext.BaseDirectory, "maps")), prefabs);
        maps.Initialize();
        Assert.True(maps.TryGetMap("city", out var world, out _, out _, out _));
        var vehicles = new VehicleSystem();
        var air = AircraftProfile.ByName("piston_single");
        var from = new Vector3(380f, 90f, -1700f);
        var to = new Vector3(380f, 0.1f, -250f);
        var e = vehicles.SpawnOne(maps, null, "city", new VehicleData
        {
            Name = "Test single", Preset = "piston_single", RoadStart = from, RoadEnd = to,
            SpeedsKmh = new[] { air.ApproachSpeedMps * 3.6f, 160f }, AccelerationMps2 = 1.5f, BrakingMps2 = 2f,
            WaitSeconds = 6f,
        });
        Assert.NotEqual(Entity.Null, e);
        Assert.False(vehicles.TryGetLever("city", e.Id + 100_000, out _));

        float dt = 1f / 30f, t = 0f;
        float approach = 0f, flare = 0f, idleOnWheels = 0f, takeoffRoll = 0f, climb = 0f;
        bool touched = false;
        while (t < 400f && climb < 3f)
        {
            vehicles.Update("city", world, dt);
            t += dt;
            var p = world.Get<Transform>(e).Position;
            var v = world.Get<Velocity>(e).Linear;
            Assert.True(vehicles.TryGetLever("city", e.Id, out float lever));
            bool ground = p.Y < 0.2f;
            if (ground) touched = true;
            float height = p.Y - to.Y;
            if (!touched && v.Y < -0.2f && height >= air.WingspanMetres)
            {
                // Down the approach, above the flare: the flight path's lever.
                Assert.Equal(MachinePower.FlightLever(v), lever);
                Assert.True(lever < 0.62f, $"{lever} coming down");
                approach += dt;
            }
            else if (!touched && v.Y < -0.2f)
            {
                Assert.Equal(MachinePower.PistonIdle, lever);
                flare += dt;
            }
            else if (ground && takeoffRoll == 0f && lever < 1f)
            {
                Assert.Equal(MachinePower.PistonIdle, lever);
                idleOnWheels += dt;
            }
            else if (ground && lever == 1f) takeoffRoll += dt;
            else if (takeoffRoll > 0f)
            {
                // From the tick its wheels leave: never down to cruise between the roll and the climb.
                Assert.True(lever > 0.62f, $"{lever} climbing out");
                climb += dt;
            }
        }
        _o.WriteLine($"approach {approach:F1} s, flare {flare:F1} s, idle on its wheels {idleOnWheels:F1} s, take-off roll {takeoffRoll:F1} s, climb-out {climb:F1} s");
        Assert.True(approach > 2f, "it never came down the approach");
        Assert.True(flare > 0.3f, "it never flared");
        Assert.True(idleOnWheels > 5f, "it never rolled out and taxied");
        Assert.True(takeoffRoll > 2f, "it never ran up for take-off");
        Assert.True(climb >= 3f, "it never climbed out");
    }

    /// <summary>
    /// An aircraft lapping a circuit has its lever from its flight path every tick, as the client worked it out:
    /// a circuit is flown level, so once it is under way it is at cruise. It kept the lever it was made with,
    /// standing.
    /// </summary>
    [Fact]
    public void AnAircraftOnACircuitHasItsFlightPathsLever()
    {
        var prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
        var maps = new MapManager(new MapRepository(Path.Combine(AppContext.BaseDirectory, "maps")), prefabs);
        maps.Initialize();
        Assert.True(maps.TryGetMap("city", out var world, out _, out _, out _));
        Assert.True(maps.TryGetMapData("city", out var data));
        // A circle 600 m across, 240 m up.
        var circuit = new List<Vector3>();
        for (int i = 0; i < 48; i++)
        {
            float a = i * MathF.Tau / 48f;
            circuit.Add(new Vector3(300f * MathF.Cos(a), 240f, -1500f + 300f * MathF.Sin(a)));
        }
        data.Tracks ??= new List<TrackData>();
        data.Tracks.Add(new TrackData { Id = "test circuit", Waypoints = circuit });
        var vehicles = new VehicleSystem();
        var e = vehicles.SpawnOne(maps, null, "city", new VehicleData
        {
            Name = "Test single", Preset = "piston_single", Track = "test circuit", TopSpeedKmh = 180f,
        });
        Assert.NotEqual(Entity.Null, e);

        float dt = 1f / 30f, underWay = 0f;
        for (float t = 0f; t < 60f; t += dt)
        {
            vehicles.Update("city", world, dt);
            var v = world.Get<Velocity>(e).Linear;
            Assert.True(vehicles.TryGetLever("city", e.Id, out float lever));
            Assert.Equal(MachinePower.FlightLever(v), lever);
            if (v.Length() > 20f) { Assert.Equal(0.62f, lever); underWay += dt; }
        }
        _o.WriteLine($"under way for {underWay:F0} s of 60, at cruise");
        Assert.True(underWay > 30f, "it never got under way");
    }

    /// <summary>
    /// A patrol car's siren is one decision, made by the server from how the car is driven and the same for
    /// every client (audit P7): what the server says of it is what a controller of the car's own says, fed the
    /// speed it is driven at each tick, and in ten minutes it has been on a call and off one. A car with no
    /// siren has nothing said of it.
    /// </summary>
    [Fact]
    public void APatrolCarsSirenIsDecidedByTheServerThatDrivesIt()
    {
        var prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
        var maps = new MapManager(new MapRepository(Path.Combine(AppContext.BaseDirectory, "maps")), prefabs);
        maps.Initialize();
        Assert.True(maps.TryGetMap("city", out var world, out _, out _, out _));
        var vehicles = new VehicleSystem();
        var road = (From: new Vector3(380f, 0.1f, -1700f), To: new Vector3(380f, 0.1f, -250f));
        Entity Spawn(string name, string preset, float x) => vehicles.SpawnOne(maps, null, "city", new VehicleData
        {
            Name = name, Preset = preset, RoadStart = road.From + new Vector3(x, 0f, 0f), RoadEnd = road.To + new Vector3(x, 0f, 0f),
            SpeedsKmh = new[] { 60f }, AccelerationMps2 = 2f, BrakingMps2 = 3f, WaitSeconds = 2f,
        });
        var patrol = Spawn("Test patrol car", "police_interceptor", 0f);
        var saloon = Spawn("Test car", "v8_muscle", 8f);
        Assert.NotEqual(Entity.Null, patrol);
        Assert.NotEqual(Entity.Null, saloon);
        Assert.False(vehicles.TryGetSiren("city", saloon.Id, out _));

        var own = new SirenController(patrol.Id);
        float dt = 1f / 30f, on = 0f, off = 0f;
        var modes = new HashSet<SirenMode>();
        for (float t = 0f; t < 600f; t += dt)
        {
            vehicles.Update("city", world, dt);
            Assert.True(vehicles.TryGetSiren("city", patrol.Id, out var mode));
            Assert.True(vehicles.TryInspect("city", patrol.Id, out var state));
            Assert.Equal(own.Update(state.Speed, dt), mode);
            modes.Add(mode);
            if (mode == SirenMode.Off) off += dt; else on += dt;
        }
        _o.WriteLine($"in ten minutes: on a call {on:F0} s, off {off:F0} s; modes {string.Join(", ", modes)}");
        Assert.True(on > 10f, "it was never on a call");
        Assert.True(off > 10f, "it never went off one");
    }
}

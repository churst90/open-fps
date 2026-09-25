using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using Arch.Core;
using OpenFPS.Common.Components;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using OpenFPS.Server.Systems;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// How a vehicle on a route moves: the physics under the city's traffic, one vehicle at a time on a
/// map made for the test. The city tests show that things happen — a bus stops, a car waits at a
/// crossing. These pin HOW: the corner speed the friction circle allows, braking along
/// v = sqrt(2 a s) and never harder than the vehicle's brake, stopping where the map says for as long
/// as it says, counting laps, and what a hard stop asks of the tyres.
/// Written for the survivors of the 2026-09-24 mutation run over VehicleSystem.
/// </summary>
public class VehicleMotionTests : IDisposable
{
    private const float Dt = 1f / 30f;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "openfps-motion-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    // ── The test map ──────────────────────────────────────────────────────────────────────────

    private static List<Vector3> Circle(float r, int n = 48)
        => Enumerable.Range(0, n).Select(k =>
        {
            double a = 2 * Math.PI * k / n;
            return new Vector3((float)(r * Math.Sin(a)), 0.1f, (float)(r * Math.Cos(a)));
        }).ToList();

    /// <summary>Two 200 m straights joined by two half-circles of radius <paramref name="r"/>; the
    /// first waypoint is the start of the first straight.</summary>
    private static List<Vector3> Stadium(float r, float straight = 200f)
    {
        var pts = new List<Vector3>();
        for (int k = 0; k < 20; k++) pts.Add(new Vector3(r, 0.1f, straight * k / 20f));                       // north
        for (int k = 0; k < 24; k++) { double a = Math.PI * k / 24; pts.Add(new Vector3((float)(r * Math.Cos(a)), 0.1f, straight + (float)(r * Math.Sin(a)))); }
        for (int k = 0; k < 20; k++) pts.Add(new Vector3(-r, 0.1f, straight - straight * k / 20f));           // south
        for (int k = 0; k < 24; k++) { double a = Math.PI * k / 24; pts.Add(new Vector3(-(float)(r * Math.Cos(a)), 0.1f, -(float)(r * Math.Sin(a)))); }
        return pts;
    }

    private sealed class Rig
    {
        public required VehicleSystem Vehicles;
        public required World World;
        public required int Id;
        public required Entity Entity;
        public VehicleSystem.Inspection State
        {
            get { Assert.True(Vehicles.TryInspect(Id, out var s)); return s; }
        }
        public Vector3 Position => World.Get<Transform>(Entity).Position;
        public void Tick(int n = 1) { for (int i = 0; i < n; i++) Vehicles.Update("motion", World, Dt); }
    }

    private Rig Build(List<Vector3> track, VehicleData vehicle, List<TrackStopData>? stops = null, float width = 12f,
                      List<VehicleData>? more = null, bool shuttle = false, StreetLifeData? life = null)
    {
        string maps = Path.Combine(_dir, "maps");
        Directory.CreateDirectory(maps);
        if (!shuttle) vehicle.Track ??= "loop";
        var vehicles = new List<VehicleData> { vehicle };
        if (more != null) vehicles.AddRange(more);
        var data = new MapData
        {
            Id = "motion",
            MinBound = new Vector3(-400, -10, -400),
            MaxBound = new Vector3(400, 50, 600),
            Tracks = new List<TrackData> { new() { Id = "loop", Waypoints = track, WidthMetres = width, Stops = stops ?? new() } },
            Vehicles = vehicles,
            StreetLife = life,
        };
        File.WriteAllText(Path.Combine(maps, "motion.json"), JsonSerializer.Serialize(data, MapRepository.JsonOptions));
        var prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
        var manager = new MapManager(new MapRepository(maps), prefabs);
        manager.Initialize();
        var system = new VehicleSystem();
        system.Spawn(manager);
        Assert.True(manager.TryGetMap("motion", out World world, out _, out _, out _));
        Entity found = Entity.Null;
        world.Query(new QueryDescription().WithAll<NameComponent>(), (Entity e, ref NameComponent n) =>
        {
            if (n.Name == vehicle.Name) found = e;
        });
        Assert.True(found != Entity.Null, "the vehicle was not spawned");
        _lastWorld = world;
        return new Rig { Vehicles = system, World = world, Id = found.Id, Entity = found };
    }

    private World? _lastWorld;

    /// <summary>Every entity the last map spawned, by name.</summary>
    private Dictionary<string, Entity> Spawned()
    {
        var all = new Dictionary<string, Entity>();
        _lastWorld!.Query(new QueryDescription().WithAll<NameComponent>(), (Entity e, ref NameComponent n) => all[n.Name] = e);
        return all;
    }

    private static VehicleData Car(string name = "Car", float topKmh = 250f, float corneringG = 0.5f, float gripG = 0f,
                                   float accel = 3f, float brake = 4f, float start = 0f, string preset = "i4_economy", float lane = 0f)
        => new()
        {
            Name = name, Preset = preset, TopSpeedKmh = topKmh, CorneringG = corneringG, GripG = gripG,
            AccelerationMps2 = accel, BrakingMps2 = brake, StartOffsetMetres = start, LaneOffsetMetres = lane,
        };

    // ── Cornering ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Round a constant bend a car settles at the speed its cornering number allows,
    /// sqrt(mu * 9.81 * R), and that is 1.0 of its cornering and 1.0 of its tyres when the map gives
    /// no separate grip.
    /// </summary>
    [Fact]
    public void RoundABendACarHoldsTheSpeedItsCorneringAllows()
    {
        var rig = Build(Circle(60f), Car(corneringG: 0.5f));
        rig.Tick(600);
        var s = rig.State;
        float limit = MathF.Sqrt(0.5f * 9.81f * 60f);
        Assert.InRange(s.Speed, limit * 0.95f, limit * 1.02f);
        Assert.InRange(s.TyreDemand, 0.9f, 1.05f);
    }

    /// <summary>...and a car with more grip than it corners on uses only that share of its tyres —
    /// the rule that stopped every vehicle on the city screeching through every junction.</summary>
    [Fact]
    public void GripAboveTheCorneringNumberIsHeadroom()
    {
        var rig = Build(Circle(60f), Car(corneringG: 0.4f, gripG: 0.8f));
        rig.Tick(600);
        var s = rig.State;
        Assert.InRange(s.Speed, MathF.Sqrt(0.4f * 9.81f * 60f) * 0.95f, MathF.Sqrt(0.4f * 9.81f * 60f) * 1.02f);
        Assert.InRange(s.TyreDemand, 0.45f, 0.55f);
    }

    /// <summary>A top speed below the corner's is the one it holds.</summary>
    [Fact]
    public void TheTopSpeedCapsIt()
    {
        var rig = Build(Circle(60f), Car(topKmh: 36f, corneringG: 1.0f));
        rig.Tick(600);
        Assert.InRange(rig.State.Speed, 9.9f, 10.01f);
        // Under its corner limit it uses (v / limit)^2 of its tyres, not all of them.
        float share = 10f / MathF.Sqrt(1.0f * 9.81f * 60f);
        Assert.InRange(rig.State.TyreDemand, share * share * 0.9f, share * share * 1.1f);
    }

    /// <summary>
    /// Into a bend off a straight it has ALREADY slowed to the corner's speed on arrival, having
    /// braked no harder than its brake: it reads the line a braking distance ahead, v^2/2a.
    /// </summary>
    [Fact]
    public void ItBrakesForABendBeforeReachingIt()
    {
        const float r = 30f, brake = 3f;
        var rig = Build(Stadium(r), Car(topKmh: 90f, corneringG: 0.5f, brake: brake));
        float bend = MathF.Sqrt(0.5f * 9.81f * r);
        float prev = rig.State.Speed, worstDecel = 0f, fastestInBend = 0f;
        for (int i = 0; i < 30 * 60; i++)
        {
            rig.Tick();
            var s = rig.State;
            worstDecel = MathF.Max(worstDecel, (prev - s.Speed) / Dt);
            prev = s.Speed;
            // Well inside a bend: past its first ten metres, on the northern half-circle.
            var p = rig.Position;
            if (p.Z > 212f) fastestInBend = MathF.Max(fastestInBend, s.Speed);
        }
        Assert.True(worstDecel <= brake + 0.05f, $"it braked at {worstDecel:F2} m/s^2 on a {brake} m/s^2 brake");
        Assert.True(fastestInBend <= bend * 1.06f, $"it was doing {fastestInBend:F1} m/s in a {bend:F1} m/s bend");
        Assert.True(fastestInBend >= bend * 0.9f, $"it crawled round a {bend:F1} m/s bend at {fastestInBend:F1}");
    }

    // ── Laps ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void LapsAreCountedOnceEachAndTheDistanceWrapsRound()
    {
        var rig = Build(Circle(40f), Car(topKmh: 72f, corneringG: 1.5f));
        double travelled = 0;
        float lapLength = rig.State.LapLength;
        for (int i = 0; i < 30 * 120; i++)
        {
            float before = rig.State.Speed;
            rig.Tick();
            travelled += rig.State.Speed * Dt;
            var s = rig.State;
            Assert.InRange(s.Lap, 0f, lapLength);
        }
        Assert.Equal((int)(travelled / lapLength), rig.State.Laps);
        Assert.True(rig.State.Laps >= 4);
    }

    // ── Stops ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// It stops where the map says, stands still for the dwell, and comes into it on the brakes by
    /// v = sqrt(2 a s): late enough that it is still near that curve twenty metres out, and never
    /// harder than its brake.
    /// </summary>
    [Fact]
    public void AStopIsMadeWhereAndForAsLongAsTheMapSays()
    {
        const float brake = 3f, at = 180f, dwell = 5f;
        var rig = Build(Circle(80f), Car(topKmh: 70f, corneringG: 1.0f, brake: brake),
                        new() { new TrackStopData { AtMetres = at, DwellSeconds = dwell, Kind = "stop" } });
        float prev = rig.State.Speed, worstDecel = 0f, at20 = -1f, haltedAt = -1f;
        int stillTicks = 0;
        for (int i = 0; i < 30 * 40 && (haltedAt < 0 || rig.State.Speed == 0f); i++)
        {
            rig.Tick();
            var s = rig.State;
            worstDecel = MathF.Max(worstDecel, (prev - s.Speed) / Dt);
            prev = s.Speed;
            if (at20 < 0 && s.Lap >= at - 20f) at20 = s.Speed;
            if (s.Speed == 0f) { stillTicks++; if (haltedAt < 0) haltedAt = s.Lap; }
        }
        // Braking at its own rate in 1/30 s steps it rolls a little past the ideal curve's end.
        Assert.InRange(haltedAt, at - 0.4f, at + 0.4f);
        Assert.InRange(stillTicks * Dt, dwell - 0.1f, dwell + 0.2f);
        float curve = MathF.Sqrt(2f * brake * 20f);
        Assert.InRange(at20, curve * 0.8f, curve * 1.05f);
        Assert.True(worstDecel <= brake + 0.05f, $"it braked at {worstDecel:F2} m/s^2 on a {brake} m/s^2 brake");
    }

    /// <summary>Having served a stop it pulls away and does not serve it again, until next lap.</summary>
    [Fact]
    public void AStopIsServedOnceALap()
    {
        var rig = Build(Circle(40f), Car(topKmh: 50f, corneringG: 1.0f),
                        new() { new TrackStopData { AtMetres = 100f, DwellSeconds = 2f, Kind = "stop" } });
        int halts = 0;
        bool still = false;
        float lapLength = rig.State.LapLength;
        for (int guard = 0; rig.State.Laps < 2; guard++)
        {
            Assert.True(guard < 30 * 120, "two laps never came");
            rig.Tick();
            bool now = rig.State.Speed == 0f;
            if (now && !still) halts++;
            still = now;
        }
        Assert.Equal(2, halts);
        Assert.True(lapLength > 200f);
    }

    /// <summary>A vehicle that starts past a stop goes to the next one ahead, not back round.</summary>
    [Fact]
    public void ItHeadsForTheStopAheadOfWhereItStarts()
    {
        var rig = Build(Circle(60f), Car(start: 150f, corneringG: 1.0f, topKmh: 50f),
                        new() { new TrackStopData { AtMetres = 100f, DwellSeconds = 3f },
                                new TrackStopData { AtMetres = 300f, DwellSeconds = 3f } });
        Assert.Equal(1, rig.State.NextStop);
        for (int guard = 0; rig.State.Speed > 0f; guard++) { Assert.True(guard < 30 * 60, "it never stopped"); rig.Tick(); }
        Assert.InRange(rig.State.Lap, 299.6f, 300.4f);
    }

    /// <summary>A bus stop is for buses: a car on the same route drives past it.</summary>
    [Fact]
    public void ABusStopStopsBusesAndNotCars()
    {
        var stops = new List<TrackStopData> { new() { AtMetres = 120f, DwellSeconds = 4f, Kind = "bus_stop", ForPreset = "bus" } };
        var car = Build(Circle(60f), Car(topKmh: 50f, corneringG: 1.0f), stops);
        car.Tick(30 * 30);
        Assert.True(car.State.Laps >= 1 && car.State.DwellLeft <= 0f);

        var bus = Build(Circle(60f), Car(name: "Bus", preset: "school_bus_na", topKmh: 50f, corneringG: 1.0f), stops);
        bool stopped = false;
        for (int i = 0; i < 30 * 30 && !stopped; i++) { bus.Tick(); stopped = bus.State.DwellLeft > 0f; }
        Assert.True(stopped, "the bus drove past its own stop");
        Assert.InRange(bus.State.Lap, 119.6f, 120.4f);
    }

    // ── What the map leaves out ───────────────────────────────────────────────────────────────

    /// <summary>A vehicle given no brake or acceleration gets 5.5 and 3.2 m/s^2, and one given no
    /// grip corners and grips on its cornering number.</summary>
    [Fact]
    public void DefaultsForWhatTheMapDoesNotSay()
    {
        var rig = Build(Circle(60f), Car(accel: 0f, brake: 0f, corneringG: 0.5f, gripG: 0f));
        Assert.Equal(5.5f, rig.State.Brake);
        Assert.Equal(3.2f, rig.State.Accel);
        // It starts AT speed — a lap in progress, not a standing start.
        Assert.InRange(rig.State.Speed, MathF.Sqrt(0.5f * 9.81f * 60f) * 0.95f, MathF.Sqrt(0.5f * 9.81f * 60f) * 1.02f);
    }

    /// <summary>A lane offset wider than the road is held to the road: no further out than half the
    /// width less a car's clearance.</summary>
    [Fact]
    public void ALaneIsHeldToTheRoad()
    {
        var rig = Build(Circle(60f), Car(lane: 100f, corneringG: 1.0f, topKmh: 40f), width: 12f);
        rig.Tick(30);
        var p = rig.Position;
        float radius = MathF.Sqrt(p.X * p.X + p.Z * p.Z);
        // Half the road less 1.2 m is 4.8 m; the polygon's chords and the line's smoothing sit about
        // 0.3 m inside a true circle.
        Assert.InRange(radius, 60f - 5.3f, 60f + 5.3f);
        Assert.True(MathF.Abs(radius - 60f) > 4f, $"it is {radius:F1} m out: the lane was not used at all");
    }

    // ── Standing on the brakes ────────────────────────────────────────────────────────────────

    /// <summary>
    /// A hard stop sheds speed at 0.92 of what the tyres grip, down to the speed asked for, and the
    /// tyres report being worked near their limit while it does.
    /// </summary>
    [Fact]
    public void AHardStopBrakesAtWhatTheTyresGive()
    {
        var rig = Build(Stadium(200f, 400f), Car(topKmh: 60f, corneringG: 0.5f, gripG: 0.8f, brake: 2f));
        rig.Tick(30 * 3);
        float from = rig.State.Speed;
        Assert.True(from > 15f);
        Assert.True(rig.Vehicles.BrakeHard(rig.Id, 3f));
        float prev = from, worstDecel = 0f, worstDemand = 0f, lowest = from;
        for (int i = 0; i < 30 * 4; i++)
        {
            rig.Tick();
            var s = rig.State;
            worstDecel = MathF.Max(worstDecel, (prev - s.Speed) / Dt);
            worstDemand = MathF.Max(worstDemand, s.TyreDemand);
            lowest = MathF.Min(lowest, s.Speed);
            prev = s.Speed;
        }
        float expected = 0.92f * 0.8f * 9.81f;
        Assert.InRange(worstDecel, expected * 0.97f, expected * 1.01f);
        Assert.InRange(lowest, 2.9f, 3.3f);
        Assert.InRange(worstDemand, 0.85f, 1.0f);
        Assert.False(rig.Vehicles.BrakeHard(-12345, 1f));
        // ...and it is a moment, not a new speed limit: it gets going again.
        rig.Tick(30 * 12);
        Assert.True(rig.State.Speed > from * 0.9f, $"still at {rig.State.Speed:F1} m/s twelve seconds after a hard stop from {from:F1}");
    }

    // ── What a preset becomes ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// A map names a preset and gets the right kind of thing: a road vehicle, an aircraft, a small
    /// machine or a person, each with its own voice prefix, size, solidity and description; and a
    /// preset nobody knows, or a track that is missing or too short, spawns nothing.
    /// </summary>
    [Fact]
    public void APresetBecomesTheRightKindOfThing()
    {
        var road = new Vector3(0, 0.1f, 0); var end = new Vector3(0, 0.1f, 50);
        var rig = Build(Circle(60f), Car(name: "Car", corneringG: 1f), more: new()
        {
            new() { Name = "Plane", Preset = "airliner", RoadStart = road + new Vector3(0, 300, 0), RoadEnd = end + new Vector3(0, 300, 0) },
            new() { Name = "Mower", Preset = "mower_push", RoadStart = road, RoadEnd = end },
            new() { Name = "Person", Preset = "walker", RoadStart = road, RoadEnd = end },
            new() { Name = "Nothing", Preset = "no_such_machine", RoadStart = road, RoadEnd = end },
            new() { Name = "Lost", Preset = "i4_economy", Track = "no_such_track" },
        });
        var w = rig.World;
        var all = Spawned();
        Assert.False(all.ContainsKey("Nothing"));
        Assert.False(all.ContainsKey("Lost"));

        var car = all["Car"]; var plane = all["Plane"]; var mower = all["Mower"]; var person = all["Person"];
        var profile = OpenFPS.Common.MachineRegistry.VehicleFor("i4_economy");
        var air = OpenFPS.Common.AircraftProfile.ByName("airliner");

        Assert.Equal("engine:i4_economy", w.Get<SoundEmitterComponent>(car).SoundId);
        Assert.Equal("aircraft:airliner", w.Get<SoundEmitterComponent>(plane).SoundId);
        Assert.Equal("machine:mower_push", w.Get<SoundEmitterComponent>(mower).SoundId);
        Assert.False(w.Has<SoundEmitterComponent>(person));
        Assert.False(w.Has<VehicleComponent>(person));
        foreach (var e in new[] { car, plane, mower })
        {
            var em = w.Get<SoundEmitterComponent>(e);
            Assert.True(em.IsSynth);
            Assert.Equal(3f, em.MinDistance);
            Assert.Equal(OpenFPS.Common.Components.PlaybackMode.LoopOne, em.Mode);
        }
        Assert.Equal(OpenFPS.Common.Loudness.AudibleRange(profile.SourceLevelDb), w.Get<SoundEmitterComponent>(car).Range);
        Assert.Equal(OpenFPS.Common.Loudness.AudibleRange(air.SourceLevelDb), w.Get<SoundEmitterComponent>(plane).Range);

        Assert.Equal(new Vector3(profile.WidthMetres, profile.HeightMetres, profile.LengthMetres), w.Get<ColliderComponent>(car).Size);
        Assert.Equal(new Vector3(air.WingspanMetres, 6f, air.LengthMetres), w.Get<ColliderComponent>(plane).Size);
        Assert.Equal(new Vector3(0.6f, 1.0f, 0.9f), w.Get<ColliderComponent>(mower).Size);
        Assert.Equal(new Vector3(0.5f, 1.8f, 0.5f), w.Get<ColliderComponent>(person).Size);
        Assert.True(w.Get<ColliderComponent>(car).IsSolid);
        Assert.True(w.Get<ColliderComponent>(mower).IsSolid);
        Assert.False(w.Get<ColliderComponent>(plane).IsSolid);
        Assert.False(w.Get<ColliderComponent>(person).IsSolid);

        Assert.Equal(2, w.Get<VehicleComponent>(car).MaxSeats);
        Assert.Equal(0, w.Get<VehicleComponent>(plane).MaxSeats);
        Assert.Equal(0, w.Get<VehicleComponent>(mower).MaxSeats);
        Assert.Equal("i4_economy", w.Get<VehicleComponent>(car).VehicleType);

        Assert.Equal($"{profile.Engine.Name}, driving the road", w.Get<IdentityComponent>(car).Description);
        Assert.Equal($"{air.Name}, in the air", w.Get<IdentityComponent>(plane).Description);
        Assert.EndsWith(", being worked", w.Get<IdentityComponent>(mower).Description);
        Assert.Equal("walking", w.Get<IdentityComponent>(person).Description);
        Assert.Equal("Person", w.Get<IdentityComponent>(person).Name);

        // A shuttle starts at its road's start, facing its end.
        var mt = w.Get<Transform>(mower);
        Assert.Equal(road, mt.Position);
        Assert.Equal(Quaternion.CreateFromYawPitchRoll(0f, 0f, 0f), mt.Rotation);
    }

    [Fact]
    public void ATrackOfFewerThanThreePointsSpawnsNothing()
    {
        string maps = Path.Combine(_dir, "maps");
        Directory.CreateDirectory(maps);
        var data = new MapData
        {
            Id = "motion",
            Tracks = new List<TrackData> { new() { Id = "loop", Waypoints = new() { Vector3.Zero, Vector3.UnitZ * 50 } } },
            Vehicles = new List<VehicleData> { Car() },
        };
        data.Vehicles[0].Track = "loop";
        File.WriteAllText(Path.Combine(maps, "motion.json"), JsonSerializer.Serialize(data, MapRepository.JsonOptions));
        var manager = new MapManager(new MapRepository(maps), new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs")));
        manager.Initialize();
        var system = new VehicleSystem();
        system.Spawn(manager);
        Assert.Equal(0, system.Count);
    }

    /// <summary>A road vehicle on a map with street life is on the street and carries its horn;
    /// without street life, or as a mower, it is not.</summary>
    [Fact]
    public void OnlyRoadVehiclesOnAStreetMapAreOnTheStreet()
    {
        var road = new Vector3(0, 0.1f, 0); var end = new Vector3(0, 0.1f, 50);
        var rig = Build(Circle(60f), Car(corneringG: 1f), life: new StreetLifeData(),
                        more: new() { new() { Name = "Mower", Preset = "mower_push", RoadStart = road, RoadEnd = end } });
        Assert.True(rig.Vehicles.TryInspect(rig.Id, out var car));
        Assert.True(car.OnStreet);
        Assert.Equal(OpenFPS.Common.VehicleProfile.HornFor(OpenFPS.Common.MachineRegistry.VehicleFor("i4_economy")), car.Horn);
        Assert.True(rig.Vehicles.TryInspect(Spawned()["Mower"].Id, out var mower));
        Assert.False(mower.OnStreet);
        Assert.Equal("", mower.Horn);

        var quiet = Build(Circle(60f), Car(corneringG: 1f));
        Assert.False(quiet.State.OnStreet);
    }

    // ── Shuttles ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Out and back along a straight road: up to the pass's speed at its acceleration, braking by
    /// v = sqrt(2 a s) to stop at the end, turning round over 2.5 s, waiting, and coming back at the
    /// next speed in its list.
    /// </summary>
    [Fact]
    public void AShuttleRunsOutTurnsWaitsAndComesBack()
    {
        var a = new Vector3(0, 0.1f, 0); var b = new Vector3(0, 0.1f, 100);
        var rig = Build(Circle(60f), new VehicleData
        {
            Name = "Van", Preset = "diesel_i4", RoadStart = a, RoadEnd = b, SpeedsKmh = new[] { 36f, 18f },
            AccelerationMps2 = 2f, BrakingMps2 = 3f, WaitSeconds = 1f, StartDelaySeconds = 0.5f,
        }, shuttle: true);
        float prev = 0f, top = 0f, worstUp = 0f, worstDown = 0f;
        int firstMove = -1, arrived = -1, leftAgain = -1;
        float returnTop = 0f;
        for (int i = 0; i < 30 * 40; i++)
        {
            rig.Tick();
            var s = rig.State;
            var p = rig.Position;
            if (firstMove < 0 && s.Speed > 0f) firstMove = i;
            worstUp = MathF.Max(worstUp, (s.Speed - prev) / Dt);
            worstDown = MathF.Max(worstDown, (prev - s.Speed) / Dt);
            if (arrived < 0) top = MathF.Max(top, s.Speed);
            if (arrived < 0 && firstMove >= 0 && s.Speed == 0f)
            {
                arrived = i;
                Assert.InRange(p.Z, 99.9f, 100.01f);
            }
            if (arrived >= 0 && leftAgain < 0 && s.Speed > 0f) leftAgain = i;
            if (leftAgain >= 0) returnTop = MathF.Max(returnTop, s.Speed);
            prev = s.Speed;
        }
        // The start delay, then the wait, before it moves at all.
        Assert.InRange(firstMove * Dt, 1.5f - 0.1f, 1.5f + 0.1f);
        Assert.InRange(top, 9.9f, 10.01f);
        Assert.True(worstUp <= 2f + 0.01f, $"accelerated at {worstUp:F2} m/s^2 on 2");
        Assert.True(worstDown <= 3f + 0.01f, $"braked at {worstDown:F2} m/s^2 on 3");
        // 2.5 s turning round and 1 s waiting.
        Assert.InRange((leftAgain - arrived) * Dt, 3.5f - 0.1f, 3.5f + 0.15f);
        Assert.InRange(returnTop, 4.9f, 5.01f);
        Assert.True(rig.Position.Z < 99f, "it did not come back");
    }

    /// <summary>Turned round, it faces the other way; half way through the turn it is side-on.</summary>
    [Fact]
    public void AShuttleTurnsRoundSmoothly()
    {
        var rig = Build(Circle(60f), new VehicleData
        {
            Name = "Van", Preset = "diesel_i4", RoadStart = new Vector3(0, 0.1f, 0), RoadEnd = new Vector3(0, 0.1f, 20),
            SpeedsKmh = new[] { 18f }, AccelerationMps2 = 2f, BrakingMps2 = 3f, WaitSeconds = 0.1f,
        }, shuttle: true);
        float Yaw() { var q = rig.World.Get<Transform>(rig.Entity).Rotation; return MathF.Atan2(2f * (q.W * q.Y), 1f - 2f * q.Y * q.Y); }
        for (int guard = 0; !(rig.State.Speed == 0f && rig.Position.Z > 19.9f); guard++) { Assert.True(guard < 30 * 20); rig.Tick(); }
        Assert.InRange(MathF.Abs(Yaw()), 0f, 0.01f);
        // A quarter of the way through it has turned less than a quarter: it eases in
        // (smoothstep, 0.156 of the half-turn at a quarter of the time).
        rig.Tick((int)(0.625f / Dt));
        Assert.InRange(MathF.Abs(Yaw()), MathF.PI * 0.12f, MathF.PI * 0.2f);
        rig.Tick((int)(0.625f / Dt));
        Assert.InRange(MathF.Abs(Yaw()), MathF.PI / 2 - 0.1f, MathF.PI / 2 + 0.1f);
        rig.Tick((int)(1.5f / Dt));
        Assert.InRange(MathF.Abs(Yaw()), MathF.PI - 0.01f, MathF.PI + 0.01f);
    }

    /// <summary>A shuttle given no speeds or wait gets 30/60/90 km/h and four seconds.</summary>
    [Fact]
    public void AShuttlesDefaults()
    {
        var rig = Build(Circle(60f), new VehicleData
        {
            Name = "Van", Preset = "diesel_i4", RoadStart = new Vector3(0, 0.1f, 0), RoadEnd = new Vector3(0, 0.1f, 300),
            AccelerationMps2 = 5f, BrakingMps2 = 5f,
        }, shuttle: true);
        Assert.Equal(4f, rig.State.Wait);
        int moved = -1; float top = 0f;
        for (int i = 0; i < 30 * 30; i++) { rig.Tick(); if (moved < 0 && rig.State.Speed > 0f) moved = i; top = MathF.Max(top, rig.State.Speed); }
        Assert.InRange(moved * Dt, 3.9f, 4.1f);
        Assert.InRange(top, 30f / 3.6f - 0.01f, 30f / 3.6f + 0.01f);
    }
}

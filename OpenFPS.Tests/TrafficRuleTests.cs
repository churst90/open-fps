using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using OpenFPS.Server.Systems;
using Xunit;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// The rules of the road, one at a time, on a map made for the test: a crossroads in the middle of a
/// square of roads, with one, two or four vehicles, and a walker where one is wanted.
///
/// The city tests (CrosswalkTests, CarFollowingTests) watch the whole city's traffic for minutes and
/// pass or fail with whatever the mix of cars happens to do. With them failing, the 2026-10-01 mutation
/// run found that walkers crossing only when traffic was coming, junctions seeing nobody, or the
/// long-truck fix switched off all went unnoticed (docs/MUTATION_2026-10-01.md). Each rule is pinned
/// here by a scene in which it alone decides what happens.
/// </summary>
public class TrafficRuleTests : IDisposable
{
    private const float Dt = 1f / 30f;
    /// <summary>Half the side of the square, metres. Every approach to the middle is 104 m of lane.</summary>
    private const float L = 120f;
    private readonly ITestOutputHelper _o;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "openfps-rules-" + Guid.NewGuid().ToString("N"));

    public TrafficRuleTests(ITestOutputHelper o) => _o = o;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    // ── The test map ──────────────────────────────────────────────────────────────────────────
    //
    // Two roads cross in the middle ("ns" and "ew"), and four more make a square round them, meeting
    // the first two at T junctions. Only the middle junction gives way; the rest are uncontrolled.
    // Two lanes everywhere, one each way, driving on the right.

    private static RoadData Road(string id, Vector3 a, Vector3 b) => new()
    {
        Id = id, Name = id, Centreline = { a, b }, WidthMetres = 7f,
        Lanes =
        {
            new LaneData { OffsetMetres = 1.75f, Direction = 1, WidthMetres = 3.5f },
            new LaneData { OffsetMetres = -1.75f, Direction = -1, WidthMetres = 3.5f },
        },
    };

    private static Vector3 P(float x, float z) => new(x, 0.05f, z);

    /// <summary>A road from a to b that winds from side to side by <paramref name="amplitude"/> metres,
    /// a wave every 16 m, on its straight line at every junction. The smoothed line a vehicle drives is
    /// metres shorter than the lanes round the bends, which is what puts the lap ahead of where the
    /// vehicle really is when it comes to the middle (see VehicleSystem.ShortOfTheLine).</summary>
    private static RoadData Winding(string id, Vector3 a, Vector3 b, float amplitude)
    {
        var road = Road(id, a, b);
        if (amplitude <= 0f) return road;
        float length = Vector3.Distance(a, b);
        var along = Vector3.Normalize(b - a);
        var side = new Vector3(along.Z, 0f, -along.X);
        road.Centreline = Enumerable.Range(0, (int)length + 1)
            .Select(k => a + along * k + side * (amplitude * MathF.Sin(2f * MathF.PI * k / 16f)))
            .ToList();
        return road;
    }

    private static JunctionData Junction(string id, float x, float z, string control = "none") => new()
    {
        Id = id, Name = id, Position = P(x, z), RadiusMetres = 8f, Control = control,
    };

    /// <summary>A car as the city drives them.</summary>
    private static VehicleData Car(string name, string[] via, float start = 0f, string preset = "i4_economy") => new()
    {
        Name = name, Preset = preset, TopSpeedKmh = 52f, CorneringG = 0.48f, GripG = 0.85f,
        AccelerationMps2 = 2.2f, BrakingMps2 = 2.92f, StartOffsetMetres = start,
        Route = new RouteData { Via = via.ToList() },
    };

    /// <summary>Somebody walking north across "ew" at <paramref name="x"/>, from 12 m south of its
    /// middle to 12 m north, as a city walker walks.</summary>
    private static VehicleData Walker(float x, float delay) => new()
    {
        Name = "Walker", Preset = "walker", RoadStart = new Vector3(x, 0.15f, -12f), RoadEnd = new Vector3(x, 0.15f, 12f),
        SpeedsKmh = new[] { 5f }, AccelerationMps2 = 0.8f, BrakingMps2 = 1.0f, WaitSeconds = 1f, StartDelaySeconds = delay,
    };

    // Ways round the square through the middle. Each starts on the lane 104 m from the middle's line,
    // except the late ones, which start a side of the square further back.
    private static readonly string[] North = { "s", "n", "ne", "se" };
    private static readonly string[] NorthLate = { "se", "s", "n", "ne" };
    private static readonly string[] East = { "w", "e", "se", "sw" };
    private static readonly string[] West = { "e", "w", "nw", "ne" };
    private static readonly string[] South = { "n", "s", "sw", "nw" };
    private static readonly string[] SouthLate = { "nw", "n", "s", "sw" };
    /// <summary>From the east, turning left in the middle to go south.</summary>
    private static readonly string[] WestThenLeft = { "e", "c", "s", "se" };

    private sealed class Scene
    {
        public required VehicleSystem Vehicles;
        public required World World;
        public float Time;
        public void Tick() { Vehicles.Update("rules", World, Dt); Time += Dt; }
        public (string Name, string Preset, Vector3 Position, float Heading, float Length, float Speed, int Laps, bool OnRoute, string StopKind, bool Dwelling) Car(string name)
            => Vehicles.DriversForTest("rules", World).Single(c => c.Name == name);
        public (string Name, Vector3 Position, bool Crossing, bool OnCarriageway, bool Waiting, float Waited, int Crossings) Walker()
            => Vehicles.WalkersForTest("rules", World).Single();
    }

    /// <param name="centre">How the middle junction is controlled; "give_way" with no priority roads
    /// is everybody giving way.</param>
    private Scene Build(List<VehicleData> vehicles, string centre = "give_way", List<string>? priority = null,
                        List<RoadStopData>? stops = null, float winding = 0f)
    {
        string maps = Path.Combine(_dir, "maps");
        Directory.CreateDirectory(maps);
        var middle = Junction("c", 0f, 0f, centre);
        middle.PriorityRoads = priority ?? new();
        var data = new MapData
        {
            Id = "rules",
            MinBound = new Vector3(-200, -10, -200),
            MaxBound = new Vector3(200, 50, 200),
            Roads = new List<RoadData>
            {
                Road("ns", P(0, -L), P(0, L)), Road("ew", P(-L, 0), P(L, 0)),
                Winding("north", P(-L, L), P(L, L), winding), Winding("south", P(-L, -L), P(L, -L), winding),
                Winding("east", P(L, -L), P(L, L), winding), Winding("west", P(-L, -L), P(-L, L), winding),
            },
            Junctions = new List<JunctionData>
            {
                middle,
                Junction("n", 0, L), Junction("s", 0, -L), Junction("e", L, 0), Junction("w", -L, 0),
                Junction("ne", L, L), Junction("nw", -L, L), Junction("se", L, -L), Junction("sw", -L, -L),
            },
            RoadStops = stops ?? new(),
            Vehicles = vehicles,
            // Street life on (it is what turns the rules on), and nothing staged: no horns, hard stops or parking.
            StreetLife = new StreetLifeData(),
            Entities = new(),
        };
        File.WriteAllText(Path.Combine(maps, "rules.json"), JsonSerializer.Serialize(data, MapRepository.JsonOptions));
        var prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
        var manager = new MapManager(new MapRepository(maps), prefabs);
        manager.Initialize();
        Assert.True(manager.TryGetRoads("rules", out var net));
        Assert.True(net.Problems.Count == 0, string.Join("\n", net.Problems));
        var system = new VehicleSystem();
        system.Spawn(manager);
        Assert.True(manager.TryGetMap("rules", out World world, out _, out _, out _));
        Assert.Equal(vehicles.Count, system.Count);
        return new Scene { Vehicles = system, World = world };
    }

    /// <summary>In the middle junction: the centre of the body inside the square its radius reaches.</summary>
    private static bool InJunction(Vector3 p) => MathF.Abs(p.X) <= 8f && MathF.Abs(p.Z) <= 8f;

    /// <summary>
    /// When each vehicle first came into the middle junction and first left it again (NaN for never),
    /// each one's slowest from 30 m out until it left, and the closest any two came, centre to centre,
    /// while either was in it.
    /// </summary>
    private sealed class Watch
    {
        public readonly Dictionary<string, float> In = new(), Out = new(), Slowest = new();
        public float Closest = float.MaxValue;
        public string ClosestWho = "";

        public void Look(Scene s)
        {
            var cars = s.Vehicles.DriversForTest("rules", s.World).ToList();
            foreach (var c in cars)
            {
                bool inside = InJunction(c.Position);
                if (inside && !In.ContainsKey(c.Name)) In[c.Name] = s.Time;
                if (!inside && In.ContainsKey(c.Name) && !Out.ContainsKey(c.Name)) Out[c.Name] = s.Time;
                if (MathF.Abs(c.Position.X) < 30f && MathF.Abs(c.Position.Z) < 30f && !Out.ContainsKey(c.Name))
                    Slowest[c.Name] = MathF.Min(Slowest.GetValueOrDefault(c.Name, float.MaxValue), c.Speed);
            }
            for (int i = 0; i < cars.Count; i++)
                for (int k = i + 1; k < cars.Count; k++)
                {
                    if (!InJunction(cars[i].Position) && !InJunction(cars[k].Position)) continue;
                    float d = Vector3.Distance(cars[i].Position, cars[k].Position);
                    if (d < Closest) { Closest = d; ClosestWho = $"{cars[i].Name} and {cars[k].Name} at {s.Time:F1} s"; }
                }
        }

        public float InAt(string name) => In.TryGetValue(name, out float t) ? t : float.NaN;
        public float OutAt(string name) => Out.TryGetValue(name, out float t) ? t : float.NaN;

        public override string ToString()
            => string.Join("; ", Slowest.Keys.OrderBy(k => k).Select(k => $"{k} in {InAt(k):F1} s, out {OutAt(k):F1} s, slowest {Slowest[k]:F1} m/s"))
             + $"; closest {Closest:F1} m ({ClosestWho})";
    }

    private Watch Run(Scene s, float seconds, Action? each = null)
    {
        var w = new Watch();
        for (int t = 0; t < seconds * 30; t++)
        {
            s.Tick();
            w.Look(s);
            each?.Invoke();
        }
        _o.WriteLine(w.ToString());
        return w;
    }

    /// <summary>One went through the middle and out before the other came in.</summary>
    private static void FirstThenSecond(Watch w, string first, string second)
    {
        Assert.False(float.IsNaN(w.OutAt(first)), $"{first} never got through: {w}");
        Assert.False(float.IsNaN(w.InAt(second)), $"{second} never got through: {w}");
        Assert.True(w.InAt(second) > w.OutAt(first), $"{second} came in before {first} was out: {w}");
    }

    // ── People crossing ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Somebody reaching the kerb with a car 40 m off stands there, off the road, until it has gone by,
    /// and then crosses. Without the gap check, the kerb, or with a car on the crossing counted as no
    /// car at all, they step out in front of it.
    /// </summary>
    [Fact]
    public void A_walker_stands_at_the_kerb_until_the_car_coming_has_gone_by()
    {
        // The car passes x = 40 at about 11.5 s; the walker is at the kerb from about 8.5 s.
        var s = Build(new() { Car("B", East), Walker(40f, 0f) }, centre: "none");
        float past = float.NaN, cleared = float.NaN, onRoad = float.NaN, over = float.NaN;
        float atKerbWhenPast = float.NaN;
        Run(s, 30f, () =>
        {
            var b = s.Car("B");
            var w = s.Walker();
            // Its tail clear of the crossing's strip (half a metre either side of the walkers' line).
            if (float.IsNaN(past) && b.Position.X > 40f + 0.5f * b.Length + 0.5f) { past = s.Time; atKerbWhenPast = w.Position.Z; }
            if (float.IsNaN(cleared) && w.Crossing) cleared = s.Time;
            if (float.IsNaN(onRoad) && MathF.Abs(w.Position.Z) < 3.5f) onRoad = s.Time;
            if (float.IsNaN(over) && w.Position.Z > 3.5f) over = s.Time;
        });
        _o.WriteLine($"car past {past:F2} s, the walker at z {atKerbWhenPast:F2} then; given the crossing {cleared:F2} s, "
                   + $"on the road {onRoad:F2} s, over {over:F2} s");
        Assert.False(float.IsNaN(past), "the car never passed the crossing");
        Assert.InRange(atKerbWhenPast, -4.0f, -3.5f);                        // standing at the kerb
        Assert.True(cleared >= past, $"the walker took the crossing at {cleared:F2} s with the car not past until {past:F2} s");
        Assert.True(onRoad >= past, $"the walker stepped into the road at {onRoad:F2} s with the car not past until {past:F2} s");
        Assert.True(over < past + 15f, $"the walker was not over the road 15 s after the car had gone (over at {over:F2} s)");
    }

    /// <summary>
    /// A car standing on the crossing (here at a stop on it) is a car on the crossing: the walker waits
    /// at the kerb until it has driven off, and never walks into it.
    /// </summary>
    [Fact]
    public void A_walker_does_not_step_out_into_a_car_standing_on_the_crossing()
    {
        // The car stands over the walkers' line from about 14 s to 29 s; the walker is at the kerb from about 17 s.
        var s = Build(new() { Car("B", East), Walker(40f, 8f) }, centre: "none",
                      stops: new() { new RoadStopData { Name = "on the crossing", Position = P(40f, -1.75f), Kind = "stop", DwellSeconds = 15f } });
        float stood = 0f, onRoad = float.NaN, over = float.NaN, gone = float.NaN;
        var hit = new List<string>();
        Run(s, 45f, () =>
        {
            var b = s.Car("B");
            var w = s.Walker();
            if (b.Dwelling) stood += Dt;
            else if (stood > 0f && float.IsNaN(gone)) gone = s.Time;
            if (float.IsNaN(onRoad) && MathF.Abs(w.Position.Z) < 3.5f) onRoad = s.Time;
            if (float.IsNaN(over) && w.Position.Z > 3.5f) over = s.Time;
            var fwd = new Vector3(MathF.Sin(b.Heading), 0f, MathF.Cos(b.Heading));
            var d = w.Position - b.Position;
            if (MathF.Abs(Vector3.Dot(d, fwd)) < 0.5f * b.Length + 0.25f && MathF.Abs(d.X * fwd.Z - d.Z * fwd.X) < 1.2f)
                hit.Add($"{s.Time:F1} s");
        });
        _o.WriteLine($"the car stood {stood:F1} s and drove off at {gone:F2} s; the walker on the road {onRoad:F2} s, over {over:F2} s");
        Assert.True(stood > 10f, "the car never stood on the crossing");
        Assert.True(onRoad >= gone, $"the walker stepped into the road at {onRoad:F2} s with the car standing on the crossing until {gone:F2} s");
        Assert.True(hit.Count == 0, $"the walker was inside the car at {string.Join(", ", hit.Take(5))}");
        Assert.True(over < gone + 15f, $"the walker was not over the road 15 s after the car had gone (over at {over:F2} s)");
    }

    // ── Junctions ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A vehicle in the junction has it. A van stands in the middle (at a stop put there) for twenty
    /// seconds; a car arriving across its path, with nothing else to give way to, waits at the line
    /// until it has gone.
    /// </summary>
    [Fact]
    public void Nobody_drives_into_a_junction_while_a_vehicle_stands_in_it()
    {
        var s = Build(new() { Car("A", NorthLate), Car("B", East, preset: "mail_truck") }, centre: "none",
                      stops: new() { new RoadStopData { Name = "middle", Position = P(-1f, -1.75f), Kind = "stop", DwellSeconds = 20f, ForPreset = "mail_truck" } });
        var w = Run(s, 50f);
        FirstThenSecond(w, "B", "A");
        Assert.True(w.Slowest["A"] < 0.5f, $"the car never waited: {w}");
        Assert.True(w.Closest > 4f, $"they met: {w}");
    }

    /// <summary>
    /// With nothing coming, the priority road is driven straight through at the speed limit, and a
    /// driver giving way slows to look and goes on without stopping.
    /// </summary>
    [Fact]
    public void A_clear_junction_is_crossed_without_stopping()
    {
        var major = Run(Build(new() { Car("A", North) }, priority: new() { "ns" }), 20f);
        Assert.True(major.Slowest["A"] > 12f, $"the priority road slowed: {major}");
        var minor = Run(Build(new() { Car("B", East) }, priority: new() { "ns" }), 20f);
        Assert.InRange(minor.Slowest["B"], 2f, 6f);
        Assert.False(float.IsNaN(minor.OutAt("B")));
    }

    /// <summary>
    /// Two cars arriving together on crossing paths: the one on the priority road goes first, though
    /// the other comes from its right.
    /// </summary>
    [Fact]
    public void The_priority_road_goes_first()
    {
        var w = Run(Build(new() { Car("A", North), Car("B", East) }, priority: new() { "ew" }), 30f);
        FirstThenSecond(w, "B", "A");
        Assert.True(w.Slowest["B"] > 12f, $"the priority road slowed: {w}");
    }

    /// <summary>
    /// On the priority road a left turn crosses the oncoming stream and gives way to it: a car turning
    /// left waits for the one coming the other way to go straight on.
    /// </summary>
    [Fact]
    public void On_the_priority_road_a_left_turn_waits_for_the_oncoming_car()
    {
        var w = Run(Build(new() { Car("B", East), Car("C", WestThenLeft) }, priority: new() { "ew" }), 30f);
        FirstThenSecond(w, "B", "C");
        Assert.True(w.Closest > 4f, $"they met: {w}");
    }

    /// <summary>
    /// Where everybody gives way, between two cars arriving together the one coming from the right goes
    /// first, without stopping, even when the other is a little ahead.
    /// </summary>
    [Fact]
    public void Between_equals_the_car_from_the_right_goes_first()
    {
        // B, going east, is 5 m nearer; A, going north, comes from B's right.
        var w = Run(Build(new() { Car("A", North), Car("B", East, 5f) }), 30f);
        FirstThenSecond(w, "A", "B");
        Assert.True(w.Slowest["A"] > 2f, $"the car from the right stopped: {w}");
    }

    /// <summary>
    /// A driver standing at the line giving way pulls out only into a gap at least the critical headway
    /// for the movement (6.5 s straight across) plus the time to reach the line: with the car on the
    /// priority road nine and a half seconds off it waits, with thirteen it goes.
    /// </summary>
    [Theory]
    [InlineData(16f, false)]
    [InlineData(6f, true)]
    public void A_driver_giving_way_takes_a_gap_only_as_long_as_the_critical_headway(float start, bool goes)
    {
        // B stands a moment at a stop four and a half metres short of the line, and is let go about
        // 9.7 s (start 16) or 13.3 s (start 6) before D, on the priority road, reaches it.
        var s = Build(new() { Car("B", East), Car("D", SouthLate, start) }, priority: new() { "ns" },
                      stops: new() { new RoadStopData { Name = "at the line", Position = P(-12f, -1.75f), Kind = "stop", DwellSeconds = 1f } });
        bool was = false;
        float gap = float.NaN;
        var w = Run(s, 30f, () =>
        {
            var b = s.Car("B");
            var d = s.Car("D");
            if (was && !b.Dwelling && float.IsNaN(gap)) gap = (d.Position.Z - 8f) / MathF.Max(0.1f, d.Speed);
            was = b.Dwelling;
        });
        _o.WriteLine($"D {gap:F2} s from the line when B could go");
        if (goes) FirstThenSecond(w, "B", "D");
        else FirstThenSecond(w, "D", "B");
    }

    /// <summary>
    /// A bus holding at the line is half a bus back from it, where the lap it drives, shorter than the
    /// lanes round the bends, already puts it in the junction. It is still short of the line, and still
    /// waits for the car on the priority road (the long-truck fix of 2026-09-28).
    /// </summary>
    [Fact]
    public void A_long_vehicle_waiting_at_the_line_is_not_taken_to_be_in_the_junction()
    {
        var w = Run(Build(new() { Car("A", North), Car("B", East, 30f, preset: "transit_bus") }, priority: new() { "ns" }, winding: 2f), 30f);
        FirstThenSecond(w, "A", "B");
        Assert.True(w.Slowest["B"] < 0.5f, $"the bus never waited: {w}");
        Assert.True(w.Closest > 4f, $"they met: {w}");
    }

    /// <summary>
    /// A driver giving way comes to the line at the speed it looks at, 15 km/h, and no faster, however
    /// far the lap it drives has run ahead of the lanes (docs/MUTATION_2026-10-01.md, item 9).
    /// </summary>
    [Theory]
    [InlineData(0f)]
    [InlineData(2f)]
    public void A_driver_giving_way_comes_to_the_line_at_its_looking_speed(float winding)
    {
        var s = Build(new() { Car("B", East) }, priority: new() { "ns" }, winding: winding);
        float prevX = float.NegativeInfinity, atLine = float.NaN;
        Run(s, 20f, () =>
        {
            var b = s.Car("B");
            if (prevX < -8f && b.Position.X >= -8f) atLine = b.Speed;
            prevX = b.Position.X;
        });
        float look = new StreetLifeData().GiveWayApproachKmh / 3.6f;
        _o.WriteLine($"at the line at {atLine:F2} m/s, looking at {look:F2}");
        // Within a tick or two of the driver's lag (4.5 m/s measured); 6.2 and 10.9 m/s before 2026-10-02.
        Assert.InRange(atLine, 0.5f * look, 1.15f * look);
    }
}

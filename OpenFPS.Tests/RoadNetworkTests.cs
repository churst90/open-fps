using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// The city's roads as data (docs/NEXT_BODIES_WHEELS_ROADS.md, stage 1): the network the server
/// builds from them, checked against the map it describes.
/// </summary>
public class RoadNetworkTests
{
    private readonly ITestOutputHelper _o;
    public RoadNetworkTests(ITestOutputHelper o) => _o = o;

    private static (MapManager Maps, RoadNetwork Net, World World) City()
    {
        var prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
        var maps = new MapManager(new MapRepository(Path.Combine(AppContext.BaseDirectory, "maps")), prefabs);
        maps.Initialize();
        Assert.True(maps.TryGetMap("city", out World world, out _, out _, out _));
        Assert.True(maps.TryGetRoads("city", out var net));
        return (maps, net, world);
    }

    [Fact]
    public void The_city_network_has_no_problems()
    {
        var (_, net, _) = City();
        _o.WriteLine($"{net.Roads.Count} roads, {net.Junctions.Count} junctions, {net.Segments.Count} lane segments, {net.DeadEnds} dead ends");
        Assert.True(net.Problems.Count == 0, string.Join("\n", net.Problems));
        Assert.True(net.Roads.Count >= 15);
        Assert.True(net.Junctions.Count >= 20);
    }

    /// <summary>Every lane runs on asphalt, sampled every two metres: the road data and the geometry come
    /// from the same call.</summary>
    [Fact]
    public void Every_lane_lies_on_the_road_surface()
    {
        var (_, net, world) = City();
        var asphalt = EntityDefinitionFactory.StaticDefinitions(world)
            .Where(d => d.Collider.Shape == ColliderShape.Box && d.Material.Material == "Asphalt")
            .ToList();
        var off = new List<string>();
        foreach (var s in net.Segments)
            for (float d = 0f; d <= s.LengthMetres; d += 2f)
            {
                var p = RoadNetwork.PointAt(s.Path, d);
                var probe = new Vector3(p.X, 0.03f, p.Z);
                if (!asphalt.Any(a => GeometryUtils.IsPointInOBB(probe, a.Transform.Position, a.Collider.Size, a.Transform.Rotation)))
                { off.Add($"{s.Road.Name} lane {s.Lane.OffsetMetres:F1} at ({p.X:F1}, {p.Z:F1})"); break; }
            }
        Assert.True(off.Count == 0, string.Join("\n", off.Take(10)));
    }

    /// <summary>Traffic keeps to the right: going north, a lane is east of the centreline.</summary>
    [Fact]
    public void Traffic_keeps_to_the_right()
    {
        var (_, net, _) = City();
        var main = net.Segments.Where(s => s.Road.Name == "Main Street").ToList();
        Assert.NotEmpty(main);
        foreach (var s in main)
        {
            bool north = s.Path[^1].Z > s.Path[0].Z;
            Assert.True(north == s.Path[0].X > 0f, $"lane at x {s.Path[0].X:F1} goes {(north ? "north" : "south")}");
        }
    }

    /// <summary>At a crossroads a lane can go left, straight on or right, and never back.</summary>
    [Fact]
    public void A_crossroads_offers_every_turn_but_back()
    {
        var (_, net, _) = City();
        var j = net.Junctions.Single(x => x.Name == "Central Street and Main Street");
        var arriving = net.Segments.Where(s => s.To == j).ToList();
        Assert.Equal(8, arriving.Count);                  // two lanes in on each of four arms
        foreach (var a in arriving)
        {
            var turns = a.Next.Select(n => n.Turn).Distinct().ToHashSet();
            Assert.Contains(Turn.Left, turns);
            Assert.Contains(Turn.Straight, turns);
            Assert.Contains(Turn.Right, turns);
            Assert.DoesNotContain(Turn.UTurn, turns);
            Assert.DoesNotContain(a.Next, n => n.Next.Road == a.Road && n.Next.Lane.Direction != a.Lane.Direction);
        }
    }

    /// <summary>From any lane downtown you can reach every other lane downtown.</summary>
    [Fact]
    public void Downtown_is_one_connected_grid()
    {
        var (_, net, _) = City();
        bool Downtown(RoadNetwork.LaneSegment s) => s.Path.All(p => MathF.Abs(p.X) <= 140f && p.Z >= -140f && p.Z <= 270f);
        var core = net.Segments.Where(s => Downtown(s) && s.From != null && s.To != null).ToList();
        Assert.NotEmpty(core);
        var start = core[0];
        var seen = new HashSet<int> { start.Index };
        var queue = new Queue<RoadNetwork.LaneSegment>(new[] { start });
        while (queue.Count > 0)
            foreach (var (next, _) in queue.Dequeue().Next)
                if (seen.Add(next.Index)) queue.Enqueue(next);
        var missed = core.Where(s => !seen.Contains(s.Index)).Select(s => $"{s.Road.Name} {s.Lane.OffsetMetres:F1}").ToList();
        Assert.True(missed.Count == 0, string.Join("\n", missed.Take(10)));
    }

    /// <summary>A surface stretch is read by distance along the road; outside every stretch it is the default.</summary>
    [Fact]
    public void A_road_reads_its_surface_by_distance()
    {
        var road = new RoadData
        {
            Surfaces = { new SurfaceData { FromMetres = 10f, ToMetres = 20f, Material = "Concrete" } },
        };
        Assert.Equal("Asphalt", RoadNetwork.SurfaceAt(road, 5f));
        Assert.Equal("Concrete", RoadNetwork.SurfaceAt(road, 15f));
        Assert.Equal("Asphalt", RoadNetwork.SurfaceAt(road, 20f));
    }

    /// <summary>The generator writes the shipped city.json byte for byte: an unregenerated generator change
    /// or a hand edit fails here.</summary>
    [Fact]
    public void The_generator_reproduces_the_shipped_city()
    {
        string repo = RepoRoot();
        string outFile = Path.Combine(Path.GetTempPath(), $"city-{Guid.NewGuid():N}.json");
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("python3", $"tools/gen_city.py --out={outFile}")
            {
                WorkingDirectory = repo, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi)!;
            string err = proc.StandardError.ReadToEnd();
            proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            Assert.True(proc.ExitCode == 0, err);
            var shipped = File.ReadAllBytes(Path.Combine(repo, "OpenFPS.Server", "maps", "city.json"));
            var made = File.ReadAllBytes(outFile);
            Assert.True(shipped.AsSpan().SequenceEqual(made), "city.json differs from what tools/gen_city.py makes");
        }
        finally { File.Delete(outFile); }
    }

    // ── Bad map data ────────────────────────────────────────────────────────────────────────
    // A NaN in a map, or a pair of lanes only just short of parallel, once meant a loop that never
    // ended and grew a list until the machine ran out of memory (found by a mutation run, 2026-10-01).

    private static RoadData Straight(string id, Vector3 from, Vector3 to) => new()
    {
        Id = id, WidthMetres = 7f, Centreline = new() { from, to },
        Lanes = new() { new LaneData { OffsetMetres = 1.75f, Direction = 1 }, new LaneData { OffsetMetres = -1.75f, Direction = -1 } },
    };

    [Fact]
    public void A_road_with_a_NaN_point_is_reported_and_left_out()
    {
        var good = Straight("good", new Vector3(0, 0, 0), new Vector3(100, 0, 0));
        var bad = Straight("bad", new Vector3(0, 0, 20), new Vector3(float.NaN, 0, 20));
        var net = new RoadNetwork(new[] { good, bad }, null);
        Assert.Contains(net.Problems, p => p.StartsWith("road bad:"));
        Assert.All(net.Segments, s => Assert.Equal("good", s.Road.Id));
        Assert.All(net.Segments, s => Assert.True(float.IsFinite(s.LengthMetres)));
    }

    [Fact]
    public void A_junction_with_a_NaN_radius_cuts_no_lane()
    {
        var road = Straight("r", new Vector3(0, 0, 0), new Vector3(100, 0, 0));
        var j = new JunctionData { Id = "j", Position = new Vector3(50, 0, 0), RadiusMetres = float.NaN };
        var net = new RoadNetwork(new[] { road }, new[] { j });
        Assert.All(net.Segments, s => Assert.True(float.IsFinite(s.LengthMetres) && s.Path.All(p => float.IsFinite(p.X))));
    }

    [Fact]
    public void Nearly_parallel_lanes_join_with_a_short_curve()
    {
        // Two lanes 3 m apart sideways, a hair off parallel: their lines meet kilometres away.
        var inPath = new List<Vector3> { new(0, 0, 0), new(10, 0, 0) };
        var outPath = new List<Vector3> { new(12, 0, 3), new(22, 0, 3.02f) };
        var pts = LaneRoutes.Connector(inPath, outPath);
        float len = 0f;
        for (int i = 0; i + 1 < pts.Count; i++) len += Vector3.Distance(pts[i], pts[i + 1]);
        Assert.True(pts.Count < 20, $"{pts.Count} points");
        Assert.True(len < 2f * Vector3.Distance(inPath[^1], outPath[0]), $"{len:F1} m");
        Assert.Equal(inPath[^1], pts[0]);
        Assert.Equal(outPath[0], pts[^1]);
    }

    [Fact]
    public void A_right_angle_turn_keeps_its_curve()
    {
        var inPath = new List<Vector3> { new(0, 0, 0), new(10, 0, 0) };
        var outPath = new List<Vector3> { new(15, 0, 5), new(15, 0, 15) };
        var pts = LaneRoutes.Connector(inPath, outPath);
        // The curve bows towards the corner at (15, 0, 0): its middle 1.8 m from it, the chord's 3.5 m.
        var mid = pts[pts.Count / 2];
        Assert.True(Vector3.Distance(mid, new Vector3(15, 0, 0)) < 2.5f, $"middle at {mid}");
    }

    [Fact]
    public void A_circuit_with_a_NaN_waypoint_says_which()
    {
        var pts = new List<Vector3> { new(0, 0, 0), new(100, 0, 0), new(100, 0, float.NaN), new(0, 0, 100) };
        var e = Assert.Throws<ArgumentException>(() => new RaceLine(pts, 0f, 30f, 0.8f, 6f));
        Assert.Contains("Waypoint 2", e.Message);
    }

    [Fact]
    public void A_circuit_the_size_of_a_continent_is_refused()
    {
        var pts = new List<Vector3> { new(0, 0, 0), new(1e7f, 0, 0), new(1e7f, 0, 1e7f) };
        Assert.Throws<ArgumentException>(() => new RaceLine(pts, 0f, 30f, 0.8f, 6f));
    }

    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, ".."));
}

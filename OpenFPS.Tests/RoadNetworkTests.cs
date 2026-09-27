using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using Xunit;
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

    /// <summary>Every lane runs on asphalt: the road data and the geometry came from the same call and
    /// must agree. Sampled every two metres along every lane.</summary>
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
}

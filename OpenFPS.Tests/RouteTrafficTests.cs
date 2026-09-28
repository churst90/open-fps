using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// The city's traffic on its roads (docs/NEXT_BODIES_WHEELS_ROADS.md, stage 1): every vehicle a tour of
/// lanes and turns through junctions, keeping moving, and the bus serving its shelters.
/// </summary>
public class RouteTrafficTests
{
    private readonly ITestOutputHelper _o;
    public RouteTrafficTests(ITestOutputHelper o) => _o = o;

    [Fact]
    public void Every_road_vehicle_drives_the_roads_and_keeps_moving()
    {
        var (world, vehicles) = CarFollowingTests.City(streetLife: true);
        var start = vehicles.DriversForTest("city", world).Where(d => d.OnRoute).ToList();
        _o.WriteLine($"{start.Count} vehicles on the roads");
        Assert.True(start.Count >= 30, $"only {start.Count} vehicles found a way round the roads");

        // Five minutes: every one must have gone somewhere, and none may sit still for two of them.
        var moved = start.ToDictionary(d => d.Name, d => 0f);
        var still = start.ToDictionary(d => d.Name, d => 0.0);
        var longest = start.ToDictionary(d => d.Name, d => 0.0);
        var last = start.ToDictionary(d => d.Name, d => d.Position);
        const float dt = 1f / 30f;
        for (int tick = 0; tick < 5 * 60 * 30; tick++)
        {
            vehicles.Update("city", world, dt);
            if (tick % 30 != 0) continue;
            foreach (var d in vehicles.DriversForTest("city", world).Where(d => d.OnRoute))
            {
                float step = System.Numerics.Vector3.Distance(d.Position, last[d.Name]);
                moved[d.Name] += step;
                last[d.Name] = d.Position;
                still[d.Name] = step < 0.2f ? still[d.Name] + 1.0 : 0.0;
                longest[d.Name] = Math.Max(longest[d.Name], still[d.Name]);
            }
        }
        foreach (var kv in moved.OrderBy(k => k.Value).Take(5)) _o.WriteLine($"least moved: {kv.Key} {kv.Value:F0} m, longest still {longest[kv.Key]:F0} s");
        Assert.All(moved, kv => Assert.True(kv.Value > 500f, $"{kv.Key} went {kv.Value:F0} m in five minutes"));
        Assert.All(longest, kv => Assert.True(kv.Value < 120.0, $"{kv.Key} stood still for {kv.Value:F0} s"));
    }

    [Fact]
    public void The_bus_stops_at_both_shelters()
    {
        var (world, vehicles) = CarFollowingTests.City(streetLife: true);
        var served = new HashSet<int>();
        const float dt = 1f / 30f;
        for (int tick = 0; tick < 8 * 60 * 30; tick++)
        {
            vehicles.Update("city", world, dt);
            if (tick % 15 != 0) continue;
            foreach (var d in vehicles.DriversForTest("city", world))
                if (d.Preset == "transit_bus" && d.Dwelling && d.StopKind == "bus_stop")
                    served.Add(d.Position.Z < 0f ? 0 : 1);
        }
        Assert.Contains(0, served);         // the shelter at z -60
        Assert.Contains(1, served);         // the shelter at z 86
    }

    /// <summary>The downtown square as traffic drives it now: the lanes and turns through its four
    /// corner junctions, clockwise from Wharf and Dock.</summary>
    internal static System.Collections.Generic.List<System.Numerics.Vector3> DowntownTour()
    {
        var prefabs = new OpenFPS.Server.Repositories.PrefabRepository(System.IO.Path.Combine(AppContext.BaseDirectory, "prefabs"));
        var maps = new OpenFPS.Server.Core.MapManager(new OpenFPS.Server.Repositories.MapRepository(System.IO.Path.Combine(AppContext.BaseDirectory, "maps")), prefabs);
        maps.Initialize();
        Assert.True(maps.TryGetRoads("city", out var net));
        var corners = OpenFPS.Common.RoadNetwork.DowntownCorners
            .Select(id => net.Junctions.Single(j => j.Id == id)).ToList();
        var route = OpenFPS.Common.LaneRoutes.Via(net, corners);
        Assert.NotNull(route);
        return route!.Points;
    }
}

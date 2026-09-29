using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OpenFPS.Server.Systems;
using Xunit;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// People on foot cross the roads (VehicleSystem.Crosswalks): at the kerb they wait for a gap, and
/// drivers stop for anybody on a crossing. Until 2026-09-28 the walkers crossed the side streets' mouths
/// without looking, and the traffic drove through them.
/// </summary>
public class CrosswalkTests
{
    private readonly ITestOutputHelper _o;
    public CrosswalkTests(ITestOutputHelper o) => _o = o;

    [Fact]
    public void Walkers_wait_for_a_gap_and_nobody_is_driven_through()
    {
        var (world, vehicles) = CarFollowingTests.City(streetLife: true);
        var crossings = vehicles.CrosswalksForTest("city").ToList();
        Assert.NotEmpty(crossings);
        const float dt = 1f / 30f;
        var hit = new List<string>();
        int started = 0, samples = 0;
        float longestWait = 0f;
        string longestWho = "";
        for (int tick = 0; tick < 300 * 30; tick++)
        {
            vehicles.Update("city", world, dt);
            if (tick % 15 != 0) continue;
            var walkers = vehicles.WalkersForTest("city", world).ToList();
            foreach (var w in walkers)
            {
                if (w.Waited > longestWait) { longestWait = w.Waited; longestWho = $"{w.Name} at ({w.Position.X:F0}, {w.Position.Z:F0})"; }
            }
            int now = walkers.Count(w => w.Crossing);
            if (tick < 20 * 30) continue;
            samples++;
            started += now;
            var cars = vehicles.DriversForTest("city", world).ToList();
            foreach (var w in walkers)
            {
                // Out in the road. Somebody standing at a corner can have a turning van's body pass close
                // by; that is the turn's path, not the crossing (see todo.md).
                if (!w.OnCarriageway) continue;
                foreach (var c in cars)
                {
                    var fwd = new Vector3(MathF.Sin(c.Heading), 0f, MathF.Cos(c.Heading));
                    var d = w.Position - c.Position;
                    float along = MathF.Abs(Vector3.Dot(d, fwd));
                    float side = MathF.Abs(d.X * fwd.Z - d.Z * fwd.X);
                    if (along < 0.5f * c.Length + 0.25f && side < 1.2f)
                        hit.Add($"{c.Name} ({c.Speed:F1} m/s) over {w.Name} at ({w.Position.X:F1}, {w.Position.Z:F1})");
                }
            }
        }
        _o.WriteLine($"{crossings.Count} crossings; walker-samples on a crossing: {started} over {samples} samples; "
                   + $"longest kerb wait {longestWait:F1} s ({longestWho}); {hit.Count} walker-in-vehicle samples");
        foreach (var h in hit.Distinct().Take(12)) _o.WriteLine(h);
        var stuck = vehicles.WalkersForTest("city", world).OrderByDescending(w => w.Waited).First();
        _o.WriteLine($"waiting longest at the end: {stuck.Name} {stuck.Waited:F0} s at ({stuck.Position.X:F1}, {stuck.Position.Z:F1})");
        foreach (var line in vehicles.CrosswalkStateForTest("city", stuck.Position)) _o.WriteLine(line);
        Assert.True(started > 0, "nobody crossed a road");
        Assert.Empty(hit);
        Assert.True(longestWait < 60f, $"somebody waited {longestWait:F0} s at a kerb");
    }

    /// <summary>Traffic still flows with people crossing: every vehicle on a route gets round.</summary>
    [Fact]
    public void Traffic_is_not_stopped_for_ever_by_the_crossings()
    {
        var (world, vehicles) = CarFollowingTests.City(streetLife: true);
        const float dt = 1f / 30f;
        var still = new Dictionary<string, double>();
        double worst = 0; string who = "";
        for (int tick = 0; tick < 300 * 30; tick++)
        {
            vehicles.Update("city", world, dt);
            if (tick % 30 != 0) continue;
            foreach (var c in vehicles.DriversForTest("city", world))
            {
                if (!c.OnRoute || c.Dwelling) { still[c.Name] = 0; continue; }
                still[c.Name] = c.Speed < 0.2f ? still.GetValueOrDefault(c.Name) + 1 : 0;
                if (still[c.Name] > worst) { worst = still[c.Name]; who = $"{c.Name} at ({c.Position.X:F0}, {c.Position.Z:F0})"; }
            }
        }
        _o.WriteLine($"longest standstill {worst:F0} s: {who}");
        Assert.True(worst < 90, $"{who} stood still for {worst:F0} s");
    }
}

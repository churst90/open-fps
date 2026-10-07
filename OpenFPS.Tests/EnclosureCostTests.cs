using System.Diagnostics;
using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>What the room survey costs walking the real city, where the openness boundary's extra probes
/// and a moving listener keep it busy.</summary>
public class EnclosureCostTests
{
    private readonly ITestOutputHelper _o;
    public EnclosureCostTests(ITestOutputHelper o) => _o = o;

    [Trait("Category", "Timing")] // depends on this machine's speed or on real time; not run on CI
    [Fact]
    public void TheSurveyStaysCheapWhileYouWalkDownMainStreet()
    {
        var prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
        var maps = new MapManager(new MapRepository(Path.Combine(AppContext.BaseDirectory, "maps")), prefabs);
        maps.Initialize();
        Assert.True(maps.TryGetMap("city", out var world, out _, out _, out _));
        var solids = new List<Enclosure.Solid>();
        foreach (var d in EntityDefinitionFactory.StaticDefinitions(world))
        {
            if (!d.Collider.IsSolid || d.Collider.Shape != OpenFPS.Common.Components.ColliderShape.Box) continue;
            if (!string.IsNullOrEmpty(d.SoundEmitter.SoundId)) continue;
            solids.Add(new Enclosure.Solid(d.Transform.Position, d.Collider.Size, d.Transform.Rotation, d.Material.Material));
        }

        var sw = new Stopwatch();
        double worst = 0, total = 0; int n = 0;
        // Walking north up the pavement at a jog, a survey every quarter second.
        for (float z = -120f; z <= 40f; z += 1.0f)
        {
            sw.Restart();
            Enclosure.Look(new Vector3(-7.5f, 1.6f, z), solids);
            double ms = sw.Elapsed.TotalMilliseconds;
            worst = Math.Max(worst, ms); total += ms; n++;
        }
        _o.WriteLine($"{n} surveys along Main Street: mean {total / n:F1} ms, worst {worst:F1} ms ({solids.Count} solids)");
        // On the acoustic worker a few times a second, which can spare tens of milliseconds. Measured
        // 30 ms before the openness boundary, 48 with it, 55 as the city grew, and 33 since boxes the
        // ray's line cannot touch are rejected first (2026-09-24).
        Assert.True(total / n < 60, $"a survey costs {total / n:F1} ms on average");
    }
}

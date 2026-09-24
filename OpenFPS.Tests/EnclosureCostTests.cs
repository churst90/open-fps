using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using Xunit;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// What the room survey costs on the real city, walking — the openness boundary casts extra probes
/// from the middle of every long ray, and a listener who keeps moving keeps asking new questions.
/// </summary>
public class EnclosureCostTests
{
    private readonly ITestOutputHelper _o;
    public EnclosureCostTests(ITestOutputHelper o) => _o = o;

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
        // It runs on the acoustic worker a few times a second, not on the mixer; tens of milliseconds
        // is what that thread can spare.
        // Measured 30 ms before the openness boundary and 48 ms with it (14-ray probes, 2 m by 0.5 m
        // cache cells, skipped for a listener already a third open). It had crept to 55 ms as the city
        // grew — every ray tested against every box within range — and is 33 ms since a box the ray's
        // LINE cannot touch is rejected before the exact test (2026-09-24).
        Assert.True(total / n < 60, $"a survey costs {total / n:F1} ms on average");
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
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
/// "I heard gun shots but didn't really hear them reflect off walls, no wash like they would in a real
/// city ... a bunch of different cracks off every surface all at slightly different times." The
/// flutter: a shot handed back and forth across a street, a crossing at a time, off whichever
/// building is on that side at that point.
/// </summary>
public class FlutterTests
{
    private readonly ITestOutputHelper _o;
    public FlutterTests(ITestOutputHelper o) { _o = o; AcousticRegistry.Initialize(); }

    private static readonly Quaternion Q = Quaternion.Identity;

    /// <summary>Two rows of brick buildings 20 m apart, each row six 20 m buildings with no gaps, and
    /// a road. <paramref name="gapAt"/> leaves one building out on the east side.</summary>
    private static List<EarlyReflections.Solid> Street(int gapAt = -1)
    {
        var s = new List<EarlyReflections.Solid> { new(new Vector3(0, -0.25f, 0), new Vector3(400, 0.5f, 400), Q, "Asphalt") };
        for (int i = 0; i < 6; i++)
        {
            float z = -50f + i * 20f;
            s.Add(new(new Vector3(-15f, 9f, z), new Vector3(10f, 18f, 20f), Q, "Brick"));
            if (i != gapAt) s.Add(new(new Vector3(15f, 9f, z), new Vector3(10f, 18f, 20f), Q, "Brick"));
        }
        return s;
    }

    private static List<EarlyReflections.Arrival> Shot(List<EarlyReflections.Solid> street, Vector3 source, Vector3 ear)
    {
        var into = new List<EarlyReflections.Arrival>();
        EarlyReflections.Find(source, ear, street, into, maxOrder: EarlyReflections.MaxOrder, separateFirst: true, flutter: true);
        return into;
    }

    /// <summary>Past the third crossing, a copy per crossing, a street's width of travel apart, and
    /// it goes on well beyond what three bounces could reach.</summary>
    [Fact]
    public void AShotRollsDownTheStreetACrossingAtATime()
    {
        var a = Shot(Street(), new Vector3(-6f, 1.5f, -30f), new Vector3(4f, 1.7f, 10f));
        var flutter = a.Where(x => x.Order > EarlyReflections.MaxOrder).OrderBy(x => x.ExtraDelaySeconds).ToList();
        foreach (var x in a.OrderBy(x => x.ExtraDelaySeconds))
            _o.WriteLine($"order {x.Order,2}  +{x.ExtraDelaySeconds * 1000,6:F0} ms  mid {20 * Math.Log10(x.GainMid),6:F1} dB  scatter {x.Scattering:F2}");
        Assert.True(flutter.Count >= 6, $"only {flutter.Count} crossings past the third");
        Assert.True(flutter.Max(x => x.Order) >= 8);
        // Later crossings are more diffuse than earlier ones.
        var byOrder = flutter.GroupBy(x => x.Order).OrderBy(g => g.Key).Select(g => g.Max(x => x.Scattering)).ToList();
        Assert.True(byOrder[^1] > byOrder[0]);
    }

    /// <summary>A gap in the east row is where the sound leaves the street: chains that would cross
    /// it are not paths, so there are fewer of them.</summary>
    [Fact]
    public void AGapBetweenBuildingsLetsTheSoundOut()
    {
        var src = new Vector3(-6f, 1.5f, -30f); var ear = new Vector3(4f, 1.7f, 10f);
        int whole = Shot(Street(), src, ear).Count(x => x.Order > EarlyReflections.MaxOrder);
        int gapped = Shot(Street(gapAt: 2), src, ear).Count(x => x.Order > EarlyReflections.MaxOrder);
        Assert.True(gapped < whole, $"{gapped} with a gap against {whole} without");
    }

    /// <summary>One wall has nothing to hand the sound back: no flutter.</summary>
    [Fact]
    public void OneWallDoesNotFlutter()
    {
        var one = Street().Where(s => s.Center.X < 0f || s.Material == "Asphalt").ToList();
        var a = Shot(one, new Vector3(-6f, 1.5f, -30f), new Vector3(4f, 1.7f, 10f));
        Assert.DoesNotContain(a, x => x.Order > EarlyReflections.MaxOrder);
    }

    /// <summary>On the real city, a shot on Main Street heard from along it: the flutter arrives, and
    /// the search stays cheap enough to run on the game thread once per shot.</summary>
    [Fact]
    public void OnMainStreetAShotFluttersAndItIsCheap()
    {
        var prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
        var maps = new MapManager(new MapRepository(Path.Combine(AppContext.BaseDirectory, "maps")), prefabs);
        maps.Initialize();
        Assert.True(maps.TryGetMap("city", out var world, out _, out _, out _));
        var solids = new List<EarlyReflections.Solid>();
        foreach (var d in EntityDefinitionFactory.StaticDefinitions(world))
        {
            if (!d.Collider.IsSolid || d.Collider.Shape != OpenFPS.Common.Components.ColliderShape.Box) continue;
            if (!string.IsNullOrEmpty(d.SoundEmitter.SoundId)) continue;
            solids.Add(new EarlyReflections.Solid(d.Transform.Position, d.Collider.Size, d.Transform.Rotation, d.Material.Material));
        }
        var into = new List<EarlyReflections.Arrival>();
        var trace = new List<string>();
        EarlyReflections.FlutterTrace = t => { if (trace.Count < 60) trace.Add(t); };
        EarlyReflections.Find(new Vector3(7f, 1.5f, -10f), new Vector3(-7.5f, 1.6f, -40f), solids, into,
                              maxOrder: EarlyReflections.MaxOrder, separateFirst: true, flutter: true);
        EarlyReflections.FlutterTrace = null;
        foreach (var t in trace) _o.WriteLine(t);
        var sw = new Stopwatch();
        double total = 0, worst = 0; int n = 0, flutter = 0;
        for (float z = -120f; z <= 40f; z += 8f)
        {
            sw.Restart();
            EarlyReflections.Find(new Vector3(7f, 1.5f, z + 30f), new Vector3(-7.5f, 1.6f, z), solids, into,
                                  maxOrder: EarlyReflections.MaxOrder, separateFirst: true, flutter: true);
            double ms = sw.Elapsed.TotalMilliseconds;
            total += ms; worst = Math.Max(worst, ms); n++;
            flutter += into.Count(a => a.Order > EarlyReflections.MaxOrder);
            if (z == -40f) _o.WriteLine("z=-40: " + string.Join(", ", into.Where(a => a.Order > 3).OrderBy(a => a.ExtraDelaySeconds)
                                                                           .Select(a => $"{a.Order}:+{a.ExtraDelaySeconds * 1000:F0}")));
        }
        _o.WriteLine($"{n} shots on Main Street: mean {total / n:F1} ms, worst {worst:F1} ms; {flutter} flutter arrivals");
        Assert.True(flutter > n * 3, $"only {flutter} flutter arrivals in {n} shots");
        Assert.True(total / n < 40, $"a search costs {total / n:F1} ms");
    }
}

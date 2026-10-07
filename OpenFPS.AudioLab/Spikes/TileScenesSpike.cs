using System.Diagnostics;
using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// The Steam Audio scene a client holds at the spawn of a streamed map, three ways: one mesh on the
/// default tracer (as before), one mesh on Embree, and a sub-scene per tile on Embree (TileSceneSet).
/// What each costs to build, what assembling the tiled one costs, and whether they answer the same:
/// occlusion and transmission from the direct stage, and reverberation times from the reflection stage,
/// for sources round the listener, with each tracer's own run-to-run spread for comparison.
///
///   --tile-scenes [map=magnolia_tx] [detail=medium] [sources=48]
/// </summary>
public static class TileScenesSpike
{
    public static int Run(string[] args)
    {
        string Arg(string name, string fallback) => args.FirstOrDefault(a => a.StartsWith(name + "="))?[(name.Length + 1)..] ?? fallback;
        string mapId = Arg("map", "magnolia_tx");
        int sourceCount = int.Parse(Arg("sources", "48"));
        var radii = StreamRadii.Named(Arg("detail", "medium")) ?? StreamRadii.Default;
        AcousticRegistry.Initialize();

        string dir = Path.Combine(Path.GetTempPath(), "openfps-tile-scenes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "maps", "places"));
        File.Copy(OpenFPS.AudioLab.LabPaths.Server("maps", "places", mapId + ".json"), Path.Combine(dir, "maps", "places", mapId + ".json"));
        var maps = new MapManager(new MapRepository(Path.Combine(dir, "maps")), new PrefabRepository(OpenFPS.AudioLab.LabPaths.Server("prefabs")));
        maps.Initialize();
        Directory.Delete(dir, true);
        if (!maps.TryGetMap(mapId, out var ecs, out _, out _, out var lookup) || !maps.TryGetTiles(mapId, out var tiles))
        { Console.WriteLine($"FAIL: no streamed map {mapId}"); return 1; }
        var spawn = maps.GetSpawnPoint(mapId).Position;
        var interest = new TileInterest { Radii = radii };
        var world = new WorldSnapshot();
        foreach (int id in TileStreamer.Begin(interest, tiles, spawn))
        {
            if (!lookup.TryGetValue(id, out var e) && !tiles.TryGetGlobal(id, out e)) continue;
            var def = EntityDefinitionFactory.From(ecs, e);
            world.Entities[id] = new EntitySnapshot { Id = id, Definition = def, Transform = def.Transform };
        }
        var boxes = SteamAudioScene.BoxesFromWorld(world);
        Console.WriteLine($"  {mapId} at the spawn, {Arg("detail", "medium")}: {world.Entities.Count} entities, {boxes.Count} boxes, {interest.Levels.Count} tiles");

        var cs = Phonon.DefaultContextSettings();
        Phonon.iplContextCreate(ref cs, out IntPtr ctxDefault);
        Phonon.iplContextCreate(ref cs, out IntPtr ctxEmbree);
        if (!SteamAudioScene.UseEmbree(ctxEmbree)) { Console.WriteLine("FAIL: Embree did not start"); return 1; }

        var sw = Stopwatch.StartNew();
        var defaultWhole = new SteamAudioScene(ctxDefault); defaultWhole.Build(boxes);
        double tDefault = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        var embreeWhole = new SteamAudioScene(ctxEmbree); embreeWhole.Build(boxes);
        double tEmbree = sw.Elapsed.TotalMilliseconds;
        var set = new TileSceneSet(ctxEmbree, tiles.TileMetres);
        sw.Restart();
        set.Update(boxes);
        double tTiles = sw.Elapsed.TotalMilliseconds;
        var assembles = new List<double>();
        (SteamAudioScene Full, SteamAudioScene Listener) tiled = default;
        for (int i = 0; i < 20; i++)
        {
            sw.Restart();
            tiled = set.Assemble();
            assembles.Add(sw.Elapsed.TotalMilliseconds);
        }
        assembles.Sort();
        // One tile changes: the cost of a door swinging in it, or of a tile arriving.
        var changed = boxes.ToList();
        int moved = changed.FindIndex(b => TileKey.Of(b.Center, tiles.TileMetres) == TileKey.Of(spawn, tiles.TileMetres) && b.Size.Y > 2f);
        changed[moved] = changed[moved] with { Center = changed[moved].Center + new Vector3(0.3f, 0, 0) };
        sw.Restart();
        set.Update(changed);
        double tOne = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        set.Assemble();
        double tOneAssemble = sw.Elapsed.TotalMilliseconds;
        set.Update(boxes);
        set.Assemble();
        tiled = set.Assemble();
        Console.WriteLine($"  build: default one mesh {tDefault:F0} ms; Embree one mesh {tEmbree:F0} ms; Embree, {set.TileCount} tiles {tTiles:F0} ms; " +
                          $"one tile changed {tOne:F0} ms ({set.LastBuilt} rebuilt; the triangle store {set.LastStoreMs:F0} ms of it: open ground in {set.Store.LastDirtyTiles} tile(s) {set.Store.LastFlagsMs:F0} ms, tiles {set.Store.Builder.LastBuildMs:F0} ms) and {tOneAssemble:F2} ms to swap it in; assemble with nothing changed median {assembles[assembles.Count / 2]:F2} ms, worst {assembles[^1]:F2} ms");

        Console.WriteLine($"  listener scene: {tiled.Listener.Solids.Count} boxes tiled, {SteamAudioScene.WithoutOpenGround(boxes).Count} by the whole-map filter; full {tiled.Full.Solids.Count} of {boxes.Count}");

        // The same sources, the same listener, through each.
        var rng = new Random(11);
        var listener = spawn + new Vector3(0, 1.7f, 0);
        var at = Enumerable.Range(0, sourceCount).Select(_ =>
        {
            float a = (float)(rng.NextDouble() * Math.PI * 2), r = 3f + (float)rng.NextDouble() * 120f;
            return listener + new Vector3(MathF.Cos(a) * r, (float)rng.NextDouble() * 2f - 0.5f, MathF.Sin(a) * r);
        }).ToArray();

        (SteamAudioSimulator.DirectResult[] Direct, SteamAudioSimulator.ReverbResult[] Reverb, double Ms) Measure(IntPtr ctx, SteamAudioScene scene)
        {
            using var sim = new SteamAudioSimulator(ctx, sourceCount, enablePathing: false, enableReflections: true);
            sim.SetScene(scene);
            var src = at.Select(_ => sim.AcquireSource()).ToArray();
            sim.SetListener(listener);
            for (int i = 0; i < at.Length; i++) sim.SetSourceInputs(src[i], at[i], 0.3f);
            var t = Stopwatch.StartNew();
            sim.Run();
            double ms = t.Elapsed.TotalMilliseconds;
            return (src.Select(sim.GetResult).ToArray(), src.Select(sim.GetReverb).ToArray(), ms);
        }

        var d1 = Measure(ctxDefault, defaultWhole);
        var d2 = Measure(ctxDefault, defaultWhole);
        var e1 = Measure(ctxEmbree, embreeWhole);
        var t1 = Measure(ctxEmbree, tiled.Full);
        var t2 = Measure(ctxEmbree, tiled.Full);
        Console.WriteLine($"  one run (direct and reflections, {sourceCount} sources): default {d1.Ms:F0} ms, Embree one mesh {e1.Ms:F0} ms, Embree tiled {t1.Ms:F0} ms");

        static string Compare(string what, (SteamAudioSimulator.DirectResult[] Direct, SteamAudioSimulator.ReverbResult[] Reverb, double) a,
                                           (SteamAudioSimulator.DirectResult[] Direct, SteamAudioSimulator.ReverbResult[] Reverb, double) b)
        {
            double occ = 0, occMax = 0, tr = 0, rt = 0, rtMax = 0; int n = a.Direct.Length, rtN = 0;
            for (int i = 0; i < n; i++)
            {
                double o = Math.Abs(a.Direct[i].Visibility - b.Direct[i].Visibility);
                occ += o; occMax = Math.Max(occMax, o);
                tr += Math.Abs(Db(a.Direct[i].TransMid) - Db(b.Direct[i].TransMid));
                if (a.Reverb[i].Rt60Mid > 0.01f && b.Reverb[i].Rt60Mid > 0.01f)
                {
                    double r = Math.Abs(a.Reverb[i].Rt60Mid - b.Reverb[i].Rt60Mid) / Math.Max(a.Reverb[i].Rt60Mid, b.Reverb[i].Rt60Mid);
                    rt += r; rtMax = Math.Max(rtMax, r); rtN++;
                }
            }
            return $"  {what}: occlusion differs by {occ / n:F3} on average (worst {occMax:F3}); transmission {tr / n:F2} dB; " +
                   $"mid reverb time {rt / Math.Max(1, rtN) * 100:F1} % (worst {rtMax * 100:F0} %)";
            static double Db(float g) => 20 * Math.Log10(Math.Max(1e-6, g));
        }
        Console.WriteLine(Compare("default against itself (run to run)", d1, d2));
        Console.WriteLine(Compare("Embree tiled against itself (run to run)", t1, t2));
        Console.WriteLine(Compare("Embree one mesh against default", e1, d1));
        Console.WriteLine(Compare("Embree tiled against default", t1, d1));
        Console.WriteLine(Compare("Embree tiled against Embree one mesh", t1, e1));
        double meanRt = d1.Reverb.Where(r => r.Rt60Mid > 0.01f).Select(r => (double)r.Rt60Mid).DefaultIfEmpty().Average();
        Console.WriteLine($"  (mean mid reverb time here {meanRt:F2} s; {d1.Direct.Count(d => d.Visibility < 0.5f)} of {sourceCount} sources mostly occluded)");

        set.Dispose();
        defaultWhole.Dispose(); embreeWhole.Dispose();
        return 0;
    }
}

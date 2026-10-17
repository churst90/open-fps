using System.Collections.Concurrent;
using System.Diagnostics;
using OpenFPS.Audio.Core;

namespace OpenFPS.AudioLab.Spikes;

/// <summary>
/// --prerender-doors out=DIR [threads=N]: every door render the shipped maps' doors can ask for (DoorSystem.RenderKeys,
/// what the server sends with each map) and the cars' windows, as DoorRenderCache files for this build; keys already
/// in DIR are kept. publish-windows.sh ships them as ASSETS/rendercache/BUILD, so a first door is heard at once.
/// </summary>
public static class PrerenderDoorsSpike
{
    public static int Run(string[] args)
    {
        string? outDir = args.FirstOrDefault(a => a.StartsWith("out=", StringComparison.Ordinal))?[4..];
        if (string.IsNullOrWhiteSpace(outDir)) { Console.Error.WriteLine("--prerender-doors needs out=DIR"); return 2; }
        int threads = Math.Max(1, System.Environment.ProcessorCount - 1);
        if (args.FirstOrDefault(a => a.StartsWith("threads=", StringComparison.Ordinal)) is { } t && int.TryParse(t[8..], out int n))
            threads = Math.Max(1, n);

        var cache = new DoorRenderCache(outDir, outDir);
        var maps = new OpenFPS.Server.Core.MapManager(new OpenFPS.Server.Repositories.MapRepository(OpenFPS.AudioLab.LabPaths.Server("maps")),
                                                      new OpenFPS.Server.Repositories.PrefabRepository(OpenFPS.AudioLab.LabPaths.Server("prefabs")));
        maps.Initialize();
        var keys = new List<string>();
        foreach (var (id, _) in maps.GetAllMaps())
            if (maps.TryGetMap(id, out var world, out _, out _, out _))
            {
                var mine = OpenFPS.Server.Systems.DoorSystem.RenderKeys(world);
                Console.WriteLine($"  {id}: {mine.Length} door renders");
                keys.AddRange(mine);
            }
        keys.AddRange(OneOffSounds.CarWindowKeys());
        keys = keys.Distinct().ToList();
        var levels = new ConcurrentDictionary<string, float>();
        int done = 0, failed = 0;
        var clock = Stopwatch.StartNew();
        Console.WriteLine($"Door models {DoorRenderCache.Name}: {keys.Count} door renders into {outDir}, {threads} at a time.");
        Parallel.ForEach(keys, new ParallelOptions { MaxDegreeOfParallelism = threads }, key =>
        {
            try
            {
                var pcm = OneOffSounds.RenderDoorKey(key, levels, cache);
                if (pcm.Length <= 16 || pcm.Any(v => !float.IsFinite(v))) { Interlocked.Increment(ref failed); Console.Error.WriteLine($"!! {key}: empty or not finite"); }
            }
            catch (Exception ex) { Interlocked.Increment(ref failed); Console.Error.WriteLine($"!! {key}: {ex.Message}"); }
            int d = Interlocked.Increment(ref done);
            if (d % 20 == 0) Console.WriteLine($"  {d}/{keys.Count} after {clock.Elapsed.TotalSeconds:F0} s");
        });
        long bytes = Directory.EnumerateFiles(outDir, "*.pcm").Sum(f => new FileInfo(f).Length);
        Console.WriteLine($"Done: {keys.Count - failed} rendered, {failed} failed, {bytes / 1048576.0:F0} MB, {clock.Elapsed.TotalSeconds:F0} s.");
        return failed == 0 ? 0 : 1;
    }
}

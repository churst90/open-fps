using OpenFPS.Common;

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// --scene-cost [map=city]: what a door costs, the Steam Audio scene and the listener's ground-free
/// scene rebuilt from the real map, as AsyncAcousticWorker.RebuildSceneIfNeeded does when a leaf near
/// the listener moves.
/// </summary>
public static class SceneCostSpike
{
    public static int Run(string[] args)
    {
        AcousticRegistry.Initialize();
        string mapId = args.FirstOrDefault(a => a.StartsWith("map="))?[4..] ?? "city";
        string root = OpenFPS.AudioLab.LabPaths.Server() + System.IO.Path.DirectorySeparatorChar;
        var (world, _) = OpenFPS.Client.Core.AudioEngine.SteamAudio.SirenRouteSpike.Load(root + "maps/" + mapId + ".json", root + "prefabs", "none", 0f);
        var cs = Phonon.DefaultContextSettings();
        if (Phonon.iplContextCreate(ref cs, out IntPtr ctx) != Phonon.IPL_STATUS_SUCCESS) { Console.WriteLine("FAIL: no context"); return 1; }
        using var full = new SteamAudioScene(ctx);
        using var listener = new SteamAudioScene(ctx);
        for (int run = 0; run < 4; run++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var boxes = SteamAudioScene.BoxesFromWorld(world);
            double tBoxes = sw.Elapsed.TotalMilliseconds;
            full.Build(boxes);
            double tFull = sw.Elapsed.TotalMilliseconds - tBoxes;
            var lb = SteamAudioScene.WithoutOpenGround(boxes);
            double tFilter = sw.Elapsed.TotalMilliseconds - tBoxes - tFull;
            listener.Build(lb);
            double tList = sw.Elapsed.TotalMilliseconds - tBoxes - tFull - tFilter;
            Console.WriteLine($"  {mapId}: {boxes.Count} boxes; boxes {tBoxes:F0} ms, scene {tFull:F0} ms, ground filter {tFilter:F0} ms, listener scene {tList:F0} ms, total {sw.Elapsed.TotalMilliseconds:F0} ms");
        }
        return 0;
    }
}

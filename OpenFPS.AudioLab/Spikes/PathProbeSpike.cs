using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using OpenFPS.Client.AudioEngine.Acoustics;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Common;

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// --path-probe [map=city] ear=x,y,z src=x,y,z [src=...]
///
/// What the game's occlusion worker hands the mixer for one source and one ear on a real map: occlusion
/// and the three band gains in dB. Game coordinates (y up), not /tp's. Written for "I hear people
/// walking outside through concrete": the walkers' steps came through a 35 cm brick wall at a flat
/// -24 dB in every band.
/// </summary>
public static class PathProbeSpike
{
    public static int Run(string[] args)
    {
        AcousticRegistry.Initialize();
        string mapId = args.FirstOrDefault(a => a.StartsWith("map="))?[4..] ?? "city";
        Vector3 P(string s) { var f = s.Split(',').Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToArray(); return new Vector3(f[0], f[1], f[2]); }
        var ear = P(args.First(a => a.StartsWith("ear="))[4..]);
        var srcs = args.Where(a => a.StartsWith("src=")).Select(a => P(a[4..])).ToList();
        string root = "/home/cody/external-rescue/Github/open-fps/OpenFPS.Server/";
        var (world, _) = SirenRouteSpike.Load(root + "maps/" + mapId + ".json", root + "prefabs", "none", 0f);
        using var worker = new AsyncAcousticWorker(new SpatialAcoustics());
        worker.UpdateWorld(world);
        worker.Start();
        // The game has had the probe graph for a while by the time anyone is standing in a flat: wait
        // for it, or this measures an engine without pathing (2026-09-29: the probe said -41 dB while
        // the game played the same walkers at full level through the route the graph found).
        var ready = DateTime.UtcNow.AddSeconds(120);
        while (!worker.PathingReady && DateTime.UtcNow < ready)
        {
            worker.EnqueueRequest(new AcousticRequest { EntityId = 999, ListenerPos = ear, SourcePos = ear + Vector3.UnitX, SourceRadius = 0.1f });
            Thread.Sleep(200);
        }
        Console.WriteLine($"  pathing {(worker.PathingReady ? "ready" : "NOT ready after 120 s")}");
        int id = 1;
        foreach (var src in srcs)
        {
            List<AcousticPathData>? paths = null;
            var until = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < until)
            {
                worker.EnqueueRequest(new AcousticRequest { EntityId = id, ListenerPos = ear, SourcePos = src, SourceRadius = AudioEmission.MinOcclusionRadius });
                Thread.Sleep(50);
                if (worker.TryGetResult(id, out paths) && paths.Count > 0 && paths[0].SourcePosition == src) break;
            }
            if (paths == null || paths.Count == 0) { Console.WriteLine($"  src {src}: no answer"); continue; }
            var p = paths[0];
            static float Db(float g) => 20f * MathF.Log10(MathF.Max(1e-5f, g));
            Console.WriteLine($"  src ({src.X:F1}, {src.Y:F1}, {src.Z:F1}) {Vector3.Distance(ear, src):F1} m: occlusion {p.Occlusion:F2}, "
                            + $"low {Db(p.EqLow):F1} mid {Db(p.EqMid):F1} high {Db(p.EqHigh):F1} dB, bleed {p.TransmissionBleed:F3}, "
                            + $"apparent ({p.ApparentPosition.X:F1}, {p.ApparentPosition.Y:F1}, {p.ApparentPosition.Z:F1})");
            id++;
        }
        return 0;
    }
}

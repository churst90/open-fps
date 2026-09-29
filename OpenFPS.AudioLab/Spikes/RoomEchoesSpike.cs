using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Core;

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// --room-echoes [map=city] ear=x,y,z src=x,y,z: the placed reflections a one-off sound gets in the
/// game (WorldAudioPlayer.QueueRoomEchoes) over the real map, each with the box it came off. Written
/// when a clap in Marlow flat 01F placed its echoes in coincident pairs (two at 5.8 m, two at 4.9 m,
/// two at 2.4 m, 2026-09-29): the same wall twice is a coherent copy, a comb, and twice the energy.
/// </summary>
public static class RoomEchoesSpike
{
    public static int Run(string[] args)
    {
        AcousticRegistry.Initialize();
        string mapId = args.FirstOrDefault(a => a.StartsWith("map="))?[4..] ?? "city";
        Vector3 P(string s) { var f = s.Split(',').Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToArray(); return new Vector3(f[0], f[1], f[2]); }
        var ear = P(args.First(a => a.StartsWith("ear="))[4..]);
        var src = P(args.First(a => a.StartsWith("src="))[4..]);
        string root = "/home/cody/external-rescue/Github/open-fps/OpenFPS.Server/";
        var (world, _) = SirenRouteSpike.Load(root + "maps/" + mapId + ".json", root + "prefabs", "none", 0f);
        var boxes = SteamAudioScene.BoxesFromWorld(world);
        var solids = boxes.Select(b => new EarlyReflections.Solid(b.Center, b.Size, b.Rotation, b.Material)).ToList();
        Console.WriteLine($"  {solids.Count} solids; ear ({ear.X:F2}, {ear.Y:F2}, {ear.Z:F2}), source ({src.X:F2}, {src.Y:F2}, {src.Z:F2}), direct {Vector3.Distance(ear, src):F2} m");
        var into = new List<EarlyReflections.Arrival>();
        EarlyReflections.Find(src, ear, solids, into, AudioPhysics.SpeedOfSound, maxOrder: EarlyReflections.MaxOrder, keep: 48);
        // What one search costs on the game thread, which is where every footstep runs it.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 20; i++) EarlyReflections.Find(src, ear, solids, into, AudioPhysics.SpeedOfSound, maxOrder: EarlyReflections.MaxOrder, keep: 48);
        Console.WriteLine($"  search: {sw.Elapsed.TotalMilliseconds / 20:F1} ms per sound over {solids.Count} solids (order {EarlyReflections.MaxOrder}, 200 m)");
        sw.Restart();
        for (int i = 0; i < 20; i++) EarlyReflections.Find(src, ear, solids, into, AudioPhysics.SpeedOfSound, maxOrder: EarlyReflections.MaxOrder, keep: 48, maxExtraPathMetres: 0.08f * AudioPhysics.SpeedOfSound);
        Console.WriteLine($"  search: {sw.Elapsed.TotalMilliseconds / 20:F2} ms per sound as the room echoes ask (80 ms window)");
        sw.Restart();
        for (int i = 0; i < 20; i++) EarlyReflections.Find(src, ear, solids, into, AudioPhysics.SpeedOfSound, maxOrder: 1, keep: 48);
        Console.WriteLine($"  search: {sw.Elapsed.TotalMilliseconds / 20:F1} ms per sound, first order only");
        into.Sort((a, b) => a.ExtraDelaySeconds.CompareTo(b.ExtraDelaySeconds));
        float direct = MathF.Max(1f, Vector3.Distance(src, ear));
        foreach (var a in into)
        {
            float gain = Math.Clamp(a.GainMid * a.PathLength / direct, 0f, 1f);
            string box = "";
            if (a.Order == 1)
            {
                int si = a.SurfaceId / 6, face = a.SurfaceId % 6;
                if (si >= 0 && si < solids.Count)
                {
                    var s = solids[si];
                    box = $"box {si} face {face} {s.Material} at ({s.Center.X:F2}, {s.Center.Y:F2}, {s.Center.Z:F2}) size ({s.Size.X:F2}, {s.Size.Y:F2}, {s.Size.Z:F2})";
                }
            }
            Console.WriteLine($"  order {a.Order} {a.ExtraDelaySeconds * 1000,5:F1} ms  {20 * MathF.Log10(MathF.Max(1e-5f, gain)),6:F1} dB  "
                            + $"image ({a.ImagePosition.X:F1}, {a.ImagePosition.Y:F1}, {a.ImagePosition.Z:F1}) {Vector3.Distance(ear, a.ImagePosition),5:F1} m  "
                            + $"hit ({a.HitPoint.X:F2}, {a.HitPoint.Y:F2}, {a.HitPoint.Z:F2})  {box}");
        }
        return 0;
    }
}

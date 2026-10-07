using System.Globalization;
using System.Numerics;
using OpenFPS.Common;

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// What a shot echoes off: every arrival EarlyReflections finds for an impulse on a real map, with the
/// solid (material and size) each first-order copy came off.
/// `--shot-echoes [map=city] at=x,z [shot=x,z]`: `shot` defaults to half a metre from you, 1.5 m up.
/// </summary>
public static class ShotEchoSpike
{
    public static int Run(string[] args)
    {
        AcousticRegistry.Initialize();
        string? S(string k) => args.FirstOrDefault(a => a.StartsWith(k + "="))?[(k.Length + 1)..];
        float[] P(string s) => s.Split(',').Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToArray();
        string map = S("map") ?? "city";
        var at = P(S("at") ?? "130,50");
        var ear = new Vector3(at[0], 1.7f, at[1]);
        var shotXz = S("shot") is { } sh ? P(sh) : new[] { at[0] + 0.5f, at[1] };
        var src = new Vector3(shotXz[0], 1.5f, shotXz[1]);

        string root = OpenFPS.AudioLab.LabPaths.Server() + System.IO.Path.DirectorySeparatorChar;
        var (world, _) = SirenRouteSpike.Load(root + "maps/" + map + ".json", root + "prefabs", "none", 0f);
        var boxes = SteamAudioScene.BoxesFromWorld(world);
        var solids = boxes.Select(b => new EarlyReflections.Solid(b.Center, b.Size, b.Rotation, b.Material)).ToList();
        var into = new List<EarlyReflections.Arrival>();
        EarlyReflections.Find(src, ear, solids, into, 343f, EarlyReflections.MaxOrder, separateFirst: true, flutter: true);
        float direct = Vector3.Distance(src, ear);
        Console.WriteLine($"  {solids.Count} solids; shot ({src.X},{src.Z}) ear ({ear.X},{ear.Z}), direct {direct:F1} m; {into.Count} arrivals");
        Console.WriteLine("  order  delay ms   mid dB  (vs direct, at the ear)   off");
        foreach (var a in into.OrderByDescending(a => a.GainMid))
        {
            string off = "";
            if (a.Order == 1)
            {
                int i = a.SurfaceId / 6;
                if (i >= 0 && i < solids.Count)
                    off = $"{solids[i].Material} {solids[i].Size.X:F1}x{solids[i].Size.Y:F1}x{solids[i].Size.Z:F1} at ({solids[i].Center.X:F0},{solids[i].Center.Y:F1},{solids[i].Center.Z:F0})";
            }
            float rel = a.GainMid * direct / MathF.Max(direct, 1e-3f);
            Console.WriteLine($"  {a.Order,5} {a.ExtraDelaySeconds * 1000f,9:F1} {20f * MathF.Log10(MathF.Max(1e-6f, a.GainMid)),8:F1}   sep={EarlyReflections.IsSeparateEvent(a)}   {off}");
        }
        return 0;
    }
}

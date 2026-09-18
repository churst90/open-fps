using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Core.Signals;

namespace OpenFPS.Client.Core.AudioEngine.Fmod;

/// <summary>
/// Horns, whistles and bells on their own, at a metre, so they can be measured before they are
/// placed. Nothing here moves and nothing is at a distance: this is the bench.
///
///   --signals [horn|whistle|bell] [preset ...]
/// </summary>
public static class SignalSpike
{
    private const int Sr = VehicleSynth.SampleRate;

    public static int Run(string[] args)
    {
        bool wantHorn = args.Contains("horn"), wantWhistle = args.Contains("whistle"), wantBell = args.Contains("bell");
        if (!wantHorn && !wantWhistle && !wantBell) wantHorn = wantWhistle = wantBell = true;

        string dir = Path.Combine(AppContext.BaseDirectory, "ASSETS", "SOUNDS", "SIGNALS");
        Directory.CreateDirectory(dir);
        Console.WriteLine("\n  Horns, whistles and bells at one metre.\n");

        if (wantHorn)
            foreach (var key in Keys(args, ModelLibrary.Ids(ModelLibrary.Kinds.Horn)))
            {
                var spec = ModelLibrary.Horn(key);
                var horn = new ChimeHorn(spec, Sr, 11);
                Console.WriteLine($"  horn {key}");
                foreach (var l in horn.Describe()) Console.WriteLine($"    {l}");
                // Two longs, a short and a long: the grade-crossing signal, and the reason an
                // American horn is heard as a phrase rather than a noise.
                var pcm = Blast(horn, new[] { (2.2f, 0.45f), (2.2f, 0.45f), (0.7f, 0.45f), (4.0f, 1.2f) });
                Report(pcm, dir, $"horn_{key}");
            }

        if (wantWhistle)
            foreach (var key in Keys(args, ModelLibrary.Ids(ModelLibrary.Kinds.Whistle)))
            {
                var spec = ModelLibrary.Whistle(key);
                var w = new SteamWhistle(spec, Sr, 23);
                Console.WriteLine($"  whistle {key}");
                foreach (var l in w.Describe()) Console.WriteLine($"    {l}");
                var pcm = WhistleBlast(w, new[] { (2.6f, 0.5f), (2.6f, 0.5f), (0.8f, 0.5f), (3.6f, 1.4f) });
                Report(pcm, dir, $"whistle_{key}");
            }

        if (wantBell)
            foreach (var key in Keys(args, ModelLibrary.Ids(ModelLibrary.Kinds.Bell)))
            {
                var spec = ModelLibrary.Bell(key);
                var b = new StruckBell(spec, Sr, 31);
                Console.WriteLine($"  bell {key}");
                foreach (var l in b.Describe()) Console.WriteLine($"    {l}");
                int n = (int)(8f * Sr);
                var pcm = new float[n];
                b.Ringing = true;
                for (int i = 0; i < n; i++)
                {
                    if (i == (int)(6f * Sr)) b.Ringing = false;    // let the last one ring out
                    b.Step();
                    pcm[i] = b.Out;
                }
                Report(pcm, dir, $"bell_{key}");
            }

        return 0;
    }

    private static IEnumerable<string> Keys(string[] args, IEnumerable<string> all)
    {
        var chosen = all.Where(args.Contains).ToList();
        return chosen.Count > 0 ? chosen : all;
    }

    /// <summary>A sequence of (seconds blowing, seconds silent).</summary>
    private static float[] Blast(ChimeHorn horn, (float On, float Off)[] phrase)
    {
        var buf = new List<float>();
        foreach (var (on, off) in phrase)
        {
            horn.Blowing = true;
            for (int i = 0; i < (int)(on * Sr); i++) { horn.Step(); buf.Add(horn.Out); }
            horn.Blowing = false;
            for (int i = 0; i < (int)(off * Sr); i++) { horn.Step(); buf.Add(horn.Out); }
        }
        return buf.ToArray();
    }

    private static float[] WhistleBlast(SteamWhistle w, (float On, float Off)[] phrase)
    {
        var buf = new List<float>();
        foreach (var (on, off) in phrase)
        {
            w.Blowing = true;
            for (int i = 0; i < (int)(on * Sr); i++) { w.Step(); buf.Add(w.Out); }
            w.Blowing = false;
            for (int i = 0; i < (int)(off * Sr); i++) { w.Step(); buf.Add(w.Out); }
        }
        return buf.ToArray();
    }

    /// <summary>Peak and RMS in real decibels, the band balance, then the file.</summary>
    private static void Report(float[] pcm, string dir, string name)
    {
        float peak = 0f; double e = 0; int loud = 0;
        foreach (var x in pcm) { peak = MathF.Max(peak, MathF.Abs(x)); if (MathF.Abs(x) > 0.02f) { e += x * (double)x; loud++; } }
        float peakDb = 20f * MathF.Log10(MathF.Max(1e-9f, peak) / 2e-5f);
        float rmsDb = 20f * MathF.Log10(MathF.Max(1e-9f, (float)Math.Sqrt(e / Math.Max(1, loud))) / 2e-5f);
        var bands = Spectrum.BandsDb(pcm, Sr);
        Console.WriteLine($"    at 1 m: peak {peakDb:F0} dB, rms while sounding {rmsDb:F0} dB");
        Console.WriteLine("    bands: " + string.Join("  ", bands.Select((d, i) => $"{Spectrum.BandEdges[i]:F0}:{d:F0}")));

        var wav = new float[pcm.Length];
        float g = peak > 1e-9f ? 0.89f / peak : 0f;
        for (int i = 0; i < wav.Length; i++) wav[i] = pcm[i] * g;
        string path = Path.Combine(dir, name + ".wav");
        File.WriteAllBytes(path, VehicleSynth.ToWav16(wav));
        Console.WriteLine($"    wrote {path}\n");
    }
}

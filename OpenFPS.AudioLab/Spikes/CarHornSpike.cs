using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Core.Signals;

namespace OpenFPS.Client.Core.AudioEngine.Fmod;

/// <summary>
/// Electric car horns on the bench, at a metre on axis, next to two air horns for comparison.
///
///   --car-horn [preset ...] [out=DIR]
///
/// Each horn plays a tap (0.15 s), a double tap (0.15 on, 0.12 off, 0.2 on) and a 1.2 s hold. The
/// hold's steady part is measured — note, crest, centroid, band balance, RMS at a metre against the
/// declared anchor — BEFORE anything is written for listening. WAVs are normalised per horn (one
/// gain across its three files), so tap against hold is real, horn against horn is not.
/// </summary>
public static class CarHornSpike
{
    private const int Sr = VehicleSynth.SampleRate;
    private static readonly string[] AirHorns = { "truck_dual", "bus_horn" };

    public static int Run(string[] args)
    {
        string dir = args.FirstOrDefault(a => a.StartsWith("out=", StringComparison.Ordinal))?.Substring(4)
                     ?? "/home/cody/external-rescue/Github/open-fps/inbox/car-horns-2026-09-23";
        Directory.CreateDirectory(dir);
        var chosen = ElectricHornSpec.Presets.Keys.Where(args.Contains).ToList();
        if (chosen.Count == 0) chosen = ElectricHornSpec.Presets.Keys.ToList();

        Console.WriteLine("\n  Electric horns at one metre, on axis.\n");
        int failures = 0;
        foreach (var key in chosen)
        {
            var spec = ElectricHornSpec.ByName(key);
            var horn = new ElectricHorn(spec, Sr, 11);
            Console.WriteLine($"  {key}");
            foreach (var l in horn.Describe()) Console.WriteLine($"    {l}");
            var patterns = Patterns(on => { horn.Blowing = on; horn.Step(); return horn.Out; });
            if (!Check(patterns["hold"], spec.ReferenceDb, $"car_{key}")) failures++;
            Write(dir, $"car_{key}", patterns);
        }

        Console.WriteLine("  Air horns for comparison.\n");
        foreach (var key in AirHorns)
        {
            var spec = ChimeHornSpec.ByName(key);
            var horn = new ChimeHorn(spec, Sr, 11);
            Console.WriteLine($"  {key}");
            foreach (var l in horn.Describe()) Console.WriteLine($"    {l}");
            var patterns = Patterns(on => { horn.Blowing = on; horn.Step(); return horn.Out; });
            Check(patterns["hold"], spec.ReferenceDb, $"air_{key}", airHorn: true);
            Write(dir, $"air_{key}", patterns);
        }

        Console.WriteLine(failures == 0 ? "  every electric horn within 1.5 dB of its anchor" : $"  {failures} electric horn(s) OFF their anchor");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>Tap, double tap and hold, each with 0.4 s of lead-in silence and a second of tail.</summary>
    private static Dictionary<string, float[]> Patterns(Func<bool, float> step)
    {
        float[] Play(params (float Sec, bool On)[] seq)
        {
            var buf = new List<float>();
            foreach (var (sec, on) in seq)
                for (int i = 0; i < (int)(sec * Sr); i++) buf.Add(step(on));
            return buf.ToArray();
        }
        return new Dictionary<string, float[]>
        {
            ["tap"] = Play((0.4f, false), (0.15f, true), (1.0f, false)),
            ["double"] = Play((0.4f, false), (0.15f, true), (0.12f, false), (0.2f, true), (1.0f, false)),
            ["hold"] = Play((0.4f, false), (1.2f, true), (1.0f, false)),
        };
    }

    /// <summary>The hold's steady part: level against the anchor, band balance, onset and release.</summary>
    private static bool Check(float[] hold, float referenceDb, string name, bool airHorn = false)
    {
        int s0 = (int)(0.4f * Sr), a = s0 + (int)(0.3f * Sr), b = s0 + (int)(1.1f * Sr), end = s0 + (int)(1.2f * Sr);
        double e = 0; float peak = 0f;
        for (int i = a; i < b; i++) { e += hold[i] * (double)hold[i]; peak = MathF.Max(peak, MathF.Abs(hold[i])); }
        float rms = (float)Math.Sqrt(e / (b - a));
        float db = 20f * MathF.Log10(MathF.Max(1e-12f, rms) / 2e-5f);
        bool ok = MathF.Abs(db - referenceDb) <= 1.5f;
        var bands = Spectrum.AverageBandsDb(hold.AsSpan(a, b - a), Sr, new[] { 0, 4096, 8192, 12288, 16384, 20480, 24576, 28672 });
        float onsetMs = -1f, win = 0.002f;
        int w = (int)(win * Sr);
        for (int i = s0; i + w < b; i += w)
        {
            double we = 0; for (int j = i; j < i + w; j++) we += hold[j] * (double)hold[j];
            if (Math.Sqrt(we / w) >= rms * 0.5f) { onsetMs = (i + w - s0) * 1000f / Sr; break; }
        }
        float ReleaseDb(float fromMs, float toMs)
        {
            int p = end + (int)(fromMs * Sr / 1000f), q = end + (int)(toMs * Sr / 1000f);
            double re = 0; for (int j = p; j < q; j++) re += hold[j] * (double)hold[j];
            return 10f * MathF.Log10((float)(re / (q - p)) / MathF.Max(1e-24f, rms * rms) + 1e-12f);
        }
        Console.WriteLine($"    hold: rms {db:F1} dB at 1 m (anchor {referenceDb:F0}, {(ok ? "OK" : "OFF")}), crest {peak / rms:F1}, "
                          + $"-6 dB reached {onsetMs:F0} ms after press");
        Console.WriteLine($"    release: {ReleaseDb(0, 10):F0} dB rel in 0-10 ms, {ReleaseDb(20, 30):F0} at 20-30, {ReleaseDb(40, 50):F0} at 40-50");
        Console.WriteLine("    bands: " + string.Join("  ", bands.Select((d, i) => $"{Spectrum.BandEdges[i]:F0}:{d:F0}")));
        return ok || airHorn;
    }

    private static void Write(string dir, string name, Dictionary<string, float[]> patterns)
    {
        float peak = patterns.Values.SelectMany(p => p).Select(MathF.Abs).DefaultIfEmpty(0f).Max();
        float g = peak > 1e-9f ? 0.89f / peak : 0f;
        foreach (var (pat, pcm) in patterns)
        {
            var wav = new float[pcm.Length];
            for (int i = 0; i < wav.Length; i++) wav[i] = pcm[i] * g;
            string path = Path.Combine(dir, $"{name}_{pat}.wav");
            File.WriteAllBytes(path, VehicleSynth.ToWav16(wav));
            Console.WriteLine($"    wrote {path}");
        }
        Console.WriteLine();
    }
}

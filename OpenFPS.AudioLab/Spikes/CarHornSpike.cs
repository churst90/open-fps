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
    private static readonly string[] AirHorns = { "truck_dual", "bus_horn", "rs3l", "k5la" };

    public static int Run(string[] args)
    {
        string dir = args.FirstOrDefault(a => a.StartsWith("out=", StringComparison.Ordinal))?.Substring(4)
                     ?? "/home/cody/external-rescue/Github/open-fps/inbox/car-horns-2026-09-24";
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
            Harmonics(patterns["hold"], spec.Bells.Select(bl => bl.Hz).ToArray());
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
        int w = (int)(0.002f * Sr);
        float Reached(int from, int to, float relDb, bool rising)
        {
            float lim = rms * MathF.Pow(10f, relDb / 20f);
            for (int i = from; i + w < to; i += w)
            {
                double we = 0; for (int j = i; j < i + w; j++) we += hold[j] * (double)hold[j];
                float r = (float)Math.Sqrt(we / w);
                if (rising ? r >= lim : r < lim) return (i + w - from) * 1000f / Sr;
            }
            return -1f;
        }
        float onsetMs = Reached(s0, b, -6f, true);
        float ReleaseDb(float fromMs, float toMs)
        {
            int p = end + (int)(fromMs * Sr / 1000f), q = end + (int)(toMs * Sr / 1000f);
            double re = 0; for (int j = p; j < q; j++) re += hold[j] * (double)hold[j];
            return 10f * MathF.Log10((float)(re / (q - p)) / MathF.Max(1e-24f, rms * rms) + 1e-12f);
        }
        Console.WriteLine($"    hold: rms {db:F1} dB at 1 m (anchor {referenceDb:F0}, {(ok ? "OK" : "OFF")}), crest {peak / rms:F1}, "
                          + $"-6 dB reached {onsetMs:F0} ms after press");
        Console.WriteLine($"    onset: -20 dB at {Reached(s0, b, -20f, true):F0} ms, -6 at {onsetMs:F0}, -1 at {Reached(s0, b, -1f, true):F0}");
        Console.WriteLine($"    release: {ReleaseDb(0, 10):F0} dB rel in 0-10 ms, {ReleaseDb(20, 30):F0} at 20-30, {ReleaseDb(40, 50):F0} at 40-50; "
                          + $"below -20 dB at {Reached(end, hold.Length, -20f, false):F0} ms, -40 at {Reached(end, hold.Length, -40f, false):F0}, -60 at {Reached(end, hold.Length, -60f, false):F0}");
        Console.WriteLine("    bands: " + string.Join("  ", bands.Select((d, i) => $"{Spectrum.BandEdges[i]:F0}:{d:F0}")));
        return ok || airHorn;
    }

    /// <summary>
    /// What makes an air horn brassy or brittle: where its energy sits. Spectral centroid, and the
    /// first ten harmonics of the lowest bell (Goertzel at k·f over the steady hold), relative to the
    /// strongest of them.
    /// </summary>
    private static void Harmonics(float[] hold, float[] notes)
    {
        float f1 = notes[0];
        int s0 = (int)(0.4f * Sr), a = s0 + (int)(0.3f * Sr), n = (int)(0.8f * Sr);
        var x = hold.AsSpan(a, n).ToArray();
        double Pow(float hz)
        {
            double w = 2 * Math.PI * hz / Sr, c = 2 * Math.Cos(w), s1 = 0, s2 = 0;
            for (int i = 0; i < x.Length; i++)
            {
                double hann = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (x.Length - 1));
                double s = x[i] * hann + c * s1 - s2; s2 = s1; s1 = s;
            }
            return s1 * s1 + s2 * s2 - c * s1 * s2;
        }
        // Peak-picked within 2% of each k·f: the loop settles a hertz or so off the design note, and
        // at the tenth harmonic that is further than a 0.8 s window's main lobe.
        double Peak(float hz) { double best = 0; for (float d = -0.02f * hz; d <= 0.02f * hz; d += 0.5f) best = Math.Max(best, Pow(hz + d)); return best; }
        var h = Enumerable.Range(1, 10).Select(k => Peak(f1 * k)).ToArray();
        double top = h.Max();
        // Centroid: a 0.2 s window (Hann main lobe +-10 Hz) swept in 5 Hz steps to 12 kHz, so no
        // harmonic can fall between the steps and leave the noise to decide the answer.
        x = x.AsSpan(0, (int)(0.2f * Sr)).ToArray();
        // ...and the NOISE: everything further than 3% from every harmonic of every bell. A horn is
        // a tone with some air in it; how much air is a number, not an impression.
        double num = 0, den = 0, noise = 0;
        for (float hz = 50f; hz < 12000f; hz += 5f)
        {
            double p = Pow(hz); num += hz * p; den += p;
            bool near = notes.Any(f => { float k = MathF.Round(hz / f); return k >= 1 && MathF.Abs(hz - k * f) < 0.03f * f; });
            if (!near) noise += p;
        }
        Console.WriteLine($"    centroid {num / Math.Max(1e-30, den):F0} Hz, noise {10 * Math.Log10(noise / Math.Max(1e-30, den) + 1e-12):F0} dB; bell 1 {f1:F0} Hz harmonics dB: "
                          + string.Join(" ", h.Select(v => $"{10 * Math.Log10(v / top + 1e-12):F0}")));
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

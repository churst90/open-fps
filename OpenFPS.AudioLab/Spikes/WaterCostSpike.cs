using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using OpenFPS.Client.AudioEngine.Core.Nature;
using OpenFPS.Common;

namespace OpenFPS.AudioLab.Spikes;

/// <summary>
/// --water-cost: what a shore stretch (ShoreSynth) and a running water source (RunningWaterSynth) cost
/// a core, rendered as the game renders them (every place, NextPlaces, Control every 256 samples), and a
/// fingerprint of every sample so two builds can be compared for a null test.
///
///   --water-cost [shore|flow] [preset ...] [sec=20] [reps=3] [wind=] [out=DIR]
///        each preset settled (25 s for a surf beach, 5 s for another shore, 2 s for running water),
///        then sec= seconds timed, reps= times from a fresh synth; the cheapest and the median rep as a
///        share of one core, the bytes allocated while timed, and a hash of the places' samples. out=DIR
///        writes the first rep's places, interleaved float32, as DIR/shore_KEY.f32 (or flow_KEY.f32),
///        for tools/null_test.py.
/// </summary>
public static class WaterCostSpike
{
    private const int Rate = 48000;
    private const int ControlBlock = 256;

    public static int Run(string[] args)
    {
        AcousticRegistry.Initialize();
        float sec = Arg(args, "sec=", 20f);
        int reps = (int)Arg(args, "reps=", 3f);
        float wind = Arg(args, "wind=", float.NaN);
        string? dir = args.FirstOrDefault(a => a.StartsWith("out=", StringComparison.Ordinal))?[4..];
        if (dir != null) Directory.CreateDirectory(dir);
        bool shores = !args.Contains("flow"), flows = !args.Contains("shore");
        var wanted = args.Where(a => !a.StartsWith("--") && !a.Contains('=') && a is not ("shore" or "flow")).ToList();
        bool Wanted(string k) => wanted.Count == 0 || wanted.Any(w => k.Equals(w, StringComparison.OrdinalIgnoreCase));

        if (shores)
            foreach (var key in ShoreSpec.Presets.Keys.Where(Wanted))
            {
                var spec = ShoreSpec.ByName(key);
                float u = float.IsNaN(wind) ? spec.ReferenceWind : wind;
                var geo = spec.DefaultGeometry;
                float settle = spec.BreakRowMetres > 0f ? 25f : 5f;
                Measure("shore_" + key, $"wind {u:F1} m/s", spec.TotalPlaces, settle, sec, reps, dir, () =>
                {
                    var s = new ShoreSynth(spec, Rate, 7, geo) { WindSpeed = u, WindFromDegrees = geo.WaterBearingDegrees, Spread = 1f };
                    return (s.Control, s.NextPlaces);
                });
            }
        if (flows)
            foreach (var key in RunningWaterSpec.Presets.Keys.Where(Wanted))
            {
                var spec = RunningWaterSpec.ByName(key);
                float q = spec.ReferenceFlow;
                float rain = spec.CatchmentSquareMetres > 0f ? spec.ReferenceRainMmPerHour : 0f;
                Measure("flow_" + key, $"{q:0.####} L/s", Math.Max(1, spec.Places), 2f, sec, reps, dir, () =>
                {
                    var s = new RunningWaterSynth(spec, Rate, 7) { Flow = q, RainOnWater = rain, Spread = 1f };
                    return (s.Control, s.NextPlaces);
                });
            }
        return 0;
    }

    private delegate void Places(Span<float> places);

    private static void Measure(string name, string what, int places, float settle, float sec, int reps, string? dir,
                                Func<(Action<float> Control, Places Next)> make)
    {
        var costs = new List<double>();
        long allocated = 0;
        string hash = "";
        double peak = 0, sum = 0;
        int n = (int)(sec * Rate);
        for (int r = 0; r < reps; r++)
        {
            var (control, next) = make();
            var buf = new float[places];
            int lead = (int)(settle * Rate);
            for (int i = 0; i < lead; i++) { if (i % ControlBlock == 0) control((float)ControlBlock / Rate); next(buf); }
            float[]? keep = r == 0 ? new float[(long)n * places] : null;
            long a0 = GC.GetAllocatedBytesForCurrentThread();
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < n; i++)
            {
                if (i % ControlBlock == 0) control((float)ControlBlock / Rate);
                next(buf);
                if (keep != null) buf.AsSpan().CopyTo(keep.AsSpan(i * places, places));
            }
            sw.Stop();
            if (r == 0) allocated = GC.GetAllocatedBytesForCurrentThread() - a0;
            costs.Add(sw.Elapsed.TotalSeconds / sec);
            if (keep != null)
            {
                var bytes = new byte[keep.Length * 4];
                Buffer.BlockCopy(keep, 0, bytes, 0, bytes.Length);
                hash = Convert.ToHexString(SHA256.HashData(bytes))[..16];
                foreach (float v in keep) { peak = Math.Max(peak, Math.Abs(v)); sum += (double)v * v; }
                if (dir != null) File.WriteAllBytes(Path.Combine(dir, name + ".f32"), bytes);
            }
        }
        costs.Sort();
        double rms = Math.Sqrt(sum / Math.Max(1, (long)n * places));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{name,-22} {what,-14} places {places,2}  cost min {costs[0] * 100,6:F2} %  median {costs[costs.Count / 2] * 100,6:F2} %  " +
            $"alloc {allocated,8} B  rms {rms:E3} Pa  peak {peak:E3} Pa  hash {hash}"));
    }

    private static float Arg(string[] args, string prefix, float fallback)
        => float.TryParse(args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.Ordinal))?.Substring(prefix.Length),
                          NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : fallback;
}

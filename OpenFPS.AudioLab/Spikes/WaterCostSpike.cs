using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using OpenFPS.Client.AudioEngine.Core.Nature;
using OpenFPS.Common;

namespace OpenFPS.AudioLab.Spikes;

/// <summary>
/// --water-cost: what a shore stretch (ShoreSynth) and a running water source (RunningWaterSynth) cost
/// a core, rendered as the game renders them (every place, NextPlaces, Control every 256 samples), and a
/// fingerprint of every sample so two builds can be compared for a null test. Time it with two cores or
/// more: on one the JIT's tiering thread barely runs and the hot loops stay in their first, unoptimised
/// code (docs/WAVES_AND_SHORES.md 8.2).
///
///   --water-cost [shore|flow] [preset ...] [sec=20] [reps=3] [wind=] [out=DIR]
///        each preset settled (25 s for a surf beach, 5 s for another shore, 2 s for running water),
///        then sec= seconds timed, reps= times from a fresh synth; the cheapest and the median rep as a
///        share of one core, the bytes allocated while timed, and a hash of the places' samples. out=DIR
///        writes the first rep's places, interleaved float32, as DIR/shore_KEY.f32 (or flow_KEY.f32),
///        for a null test.
///   --water-cost rain out=DIR
///        the render fingerprint's three rain renders (RenderFingerprintTests: 2 s at 8 mm/h on asphalt,
///        steel and a puddle, seed 9) as DIR/rain_SURFACE.f32, for a null test of a change that moves them.
///   --water-cost bubbles
///        what one sample of a ringing bubble (EventSum.Bubble) costs at five sizes, and of a splash's
///        steep burst (EventSum.Burst) at two decays.
///   --water-cost null DIR_A DIR_B
///        every .f32 in both: whether they are the same to the bit, and if not, how far apart: the
///        difference's rms against the signal's, and its largest sample against the signal's peak.
/// </summary>
public static class WaterCostSpike
{
    private const int Rate = 48000;
    private const int ControlBlock = 256;

    public static int Run(string[] args)
    {
        if (args.Contains("null")) return Null(args);
        if (args.Contains("bubbles")) return Bubbles();
        if (args.Contains("rain")) return Rain(args);
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

    private static int Rain(string[] args)
    {
        AcousticRegistry.Initialize();
        string dir = args.First(a => a.StartsWith("out=", StringComparison.Ordinal))[4..];
        Directory.CreateDirectory(dir);
        var surfaces = new (string Name, RainLayer Layer)[]
        {
            ("asphalt", new RainLayer { Kind = RainSurfaceKind.Hard, Material = "Asphalt", ModulusGPa = 3f }),
            ("steel", new RainLayer { Kind = RainSurfaceKind.Plate, Material = "Metal", ModulusGPa = 200f, Plate = new RainPlate("Metal", 0.0007f, 1.2f, 0.6f) }),
            ("puddle", new RainLayer { Kind = RainSurfaceKind.Pool, Material = "Water", ModulusGPa = 2.2f }),
        };
        foreach (var (name, layer) in surfaces)
        {
            var synth = new RainSynth(Rate, 9) { Patch = new RainPatch { Layers = new[] { layer.Single() }, ReferenceDistance = 1f }, RainRate = 8f };
            var x = new float[2 * Rate];
            for (int i = 0; i < x.Length; i++) x[i] = synth.Next();
            var bytes = new byte[x.Length * 4];
            Buffer.BlockCopy(x, 0, bytes, 0, bytes.Length);
            File.WriteAllBytes(Path.Combine(dir, "rain_" + name + ".f32"), bytes);
        }
        return 0;
    }

    /// <summary>What one sample of a ringing bubble costs, by its size.</summary>
    private static int Bubbles()
    {
        Console.WriteLine($"Vector<float>: {Vector<float>.Count} lanes, accelerated {Vector.IsHardwareAccelerated}");
        var sum = new EventSum(Rate, 3);
        foreach (float mm in new[] { 13f, 6.5f, 3.3f, 1.6f, 0.8f })
        {
            float hz = FallingWaterSynth.MinnaertHzMetres / (mm * 1e-3f);
            float damping = FallingWaterSynth.BubbleDamping(mm);
            int length = (int)(4.6f / (MathF.PI * damping * hz) * Rate);
            double best = double.MaxValue;
            for (int rep = 0; rep < 5; rep++)
            {
                var sw = Stopwatch.StartNew();
                for (int k = 0; k < 2000; k++)
                {
                    sum.Bubble(k % 64, hz, damping, 0.01f, FallingWaterSynth.BubbleRise);
                    if ((k & 63) == 63) for (int i = 0; i < 64; i++) sum.Next();
                }
                best = Math.Min(best, sw.Elapsed.TotalSeconds);
            }
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"bubble {mm,5:F1} mm {hz,6:F0} Hz: {length,6} samples, {best / 2000 * 1e9 / length:F2} ns a sample, {best / 2000 * 1e6:F1} us a bubble"));
        }
        foreach (float decay in new[] { 0.005f, 0.025f })
        {
            int length = (int)((0.0002f + 6f * decay) * Rate);
            double best = double.MaxValue;
            for (int rep = 0; rep < 5; rep++)
            {
                var sw = Stopwatch.StartNew();
                for (int k = 0; k < 500; k++)
                {
                    sum.Burst(k % 64, 0.0002f, decay, 0.01f, 2000f, 4500f, steep: true);
                    if ((k & 63) == 63) for (int i = 0; i < 64; i++) sum.Next();
                }
                best = Math.Min(best, sw.Elapsed.TotalSeconds);
            }
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"splash burst {decay * 1e3,4:F0} ms: {length,6} samples, {best / 500 * 1e9 / length:F2} ns a sample, {best / 500 * 1e6:F1} us a burst"));
        }
        return 0;
    }

    private static int Null(string[] args)
    {
        var dirs = args.Where(a => !a.StartsWith("--") && a != "null").ToList();
        if (dirs.Count != 2) { Console.WriteLine("--water-cost null DIR_A DIR_B"); return 2; }
        foreach (var pathA in Directory.GetFiles(dirs[0], "*.f32").OrderBy(p => p, StringComparer.Ordinal))
        {
            string pathB = Path.Combine(dirs[1], Path.GetFileName(pathA));
            if (!File.Exists(pathB)) { Console.WriteLine($"{Path.GetFileName(pathA),-26} missing in {dirs[1]}"); continue; }
            byte[] a = File.ReadAllBytes(pathA), b = File.ReadAllBytes(pathB);
            int n = Math.Min(a.Length, b.Length) / 4, same = 0;
            double sa = 0, sd = 0, peak = 0, worst = 0;
            for (int i = 0; i < n; i++)
            {
                float x = BitConverter.ToSingle(a, 4 * i), y = BitConverter.ToSingle(b, 4 * i);
                if (BitConverter.SingleToInt32Bits(x) == BitConverter.SingleToInt32Bits(y)) same++;
                double d = (double)y - x;
                sa += (double)x * x; sd += d * d;
                peak = Math.Max(peak, Math.Abs(x)); worst = Math.Max(worst, Math.Abs(d));
            }
            string verdict = same == n && a.Length == b.Length ? "identical to the bit"
                : string.Create(CultureInfo.InvariantCulture,
                    $"{100.0 * same / n:F2} % of samples identical; difference rms {10 * Math.Log10(Math.Max(1e-30, sd) / Math.Max(1e-30, sa)):F1} dB re the signal's, largest {20 * Math.Log10(Math.Max(1e-30, worst) / Math.Max(1e-30, peak)):F1} dB re its peak");
            Console.WriteLine($"{Path.GetFileName(pathA),-26} {verdict}");
        }
        return 0;
    }

    private static float Arg(string[] args, string prefix, float fallback)
        => float.TryParse(args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.Ordinal))?.Substring(prefix.Length),
                          NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : fallback;
}

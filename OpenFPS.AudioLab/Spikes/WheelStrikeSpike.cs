using System.Globalization;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Common;

namespace OpenFPS.AudioLab.Spikes;

/// <summary>
/// --wheel-strike [out=DIR]: one car wheel (0.33 m) over a level crossing's rail at 15, 30 and 60 km/h,
/// as WheelStrikes renders it, alone: pascals at a metre, 4 kN on the wheel. Prints, for each, the peak,
/// how long it rings (to -20 and -40 dB of its peak envelope), the octave bands, and how tonal it is (the
/// strongest line over the median of the spectrum, 100 Hz-8 kHz). Writes strike_KMH.wav (a strike a
/// second, five of them, scaled so 3 Pa is full scale) when out= is given.
/// </summary>
public static class WheelStrikeSpike
{
    private const int Rate = 48000;

    public static int Run(string[] args)
    {
        string? dir = args.FirstOrDefault(a => a.StartsWith("out=", StringComparison.Ordinal))?[4..];
        if (dir != null) Directory.CreateDirectory(dir);
        Console.WriteLine("\n  A car wheel over a crossing's rail, alone, at a metre.\n");
        foreach (float kmh in new[] { 15f, 30f, 60f })
        {
            float v = kmh / 3.6f;
            float pa = WheelStrikes.PeakPascals(v, 4000f, WheelStrikes.CrossingStepMetres);
            float tc = WheelStrikes.ContactSeconds(v, 0.33f);
            var strikes = new WheelStrikes(new[] { 0.33f }, Rate);
            int n = Rate * 5;
            var pcm = new float[n];
            var batch = new List<WheelStrike>();
            for (int k = 0; k < 5; k++) batch.Add(new WheelStrike(0, 0.2 + k, pa, tc));
            strikes.Queue(batch);
            strikes.Drain(0, 0, 0.0, Rate, 1f);
            for (int i = 0; i < n; i++) { strikes.Advance(i); pcm[i] = strikes.Out(0); }

            // The first strike alone, for its shape.
            int s0 = (int)(0.2 * Rate), len = Rate / 2;
            var one = pcm.AsSpan(s0, len).ToArray();
            float peak = one.Max(MathF.Abs);
            var env = Envelope(one, Rate / 1000);
            float t20 = Until(env, peak * 0.1f), t40 = Until(env, peak * 0.01f);
            var bands = Spectrum.BandsDb(one, Rate);
            float tonal = Tonality(one);
            Console.WriteLine($"  {kmh,3:F0} km/h: asked {pa:F2} Pa, peak {peak:F2} Pa, contact {tc * 1000f:F1} ms; rings to -20 dB in {t20 * 1000f:F0} ms, to -40 dB in {t40 * 1000f:F0} ms; strongest line {tonal:F1} dB over the median");
            Console.WriteLine("        bands: " + string.Join("  ", bands.Select((d, i) => $"{Spectrum.BandEdges[i]:F0}:{d:F0}")));
            if (dir != null)
            {
                var wav = pcm.Select(x => x / 3f).ToArray();
                File.WriteAllBytes(Path.Combine(dir, $"strike_{kmh:F0}kmh.wav"), WeaponSynth.ToWav16(wav, Rate));
            }
        }
        if (dir != null) Console.WriteLine($"\n  wrote {dir}/strike_*.wav (3 Pa = full scale)");
        return 0;
    }

    private static float[] Envelope(float[] x, int win)
    {
        var e = new float[x.Length / win];
        for (int i = 0; i < e.Length; i++)
        {
            float m = 0f;
            for (int k = 0; k < win; k++) m = MathF.Max(m, MathF.Abs(x[i * win + k]));
            e[i] = m;
        }
        return e;
    }

    /// <summary>Seconds until the envelope stays under <paramref name="level"/>.</summary>
    private static float Until(float[] env, float level)
    {
        int last = 0;
        for (int i = 0; i < env.Length; i++) if (env[i] >= level) last = i;
        return (last + 1) / 1000f;
    }

    /// <summary>The strongest bin over the median, 100 Hz to 8 kHz, of a 2^14-point spectrum, dB.</summary>
    private static float Tonality(float[] x)
    {
        int n = 1 << 14;
        var re = new double[n]; var im = new double[n];
        for (int i = 0; i < Math.Min(n, x.Length); i++) re[i] = x[i];
        Fft(re, im);
        int lo = 100 * n / Rate, hi = 8000 * n / Rate;
        var mag = Enumerable.Range(lo, hi - lo).Select(k => re[k] * re[k] + im[k] * im[k]).OrderBy(m => m).ToArray();
        double median = mag[mag.Length / 2], max = mag[^1];
        return (float)(10 * Math.Log10(max / Math.Max(1e-30, median)));
    }

    private static void Fft(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len;
            double wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    double ur = re[i + k], ui = im[i + k];
                    double vr = re[i + k + len / 2] * cr - im[i + k + len / 2] * ci;
                    double vi = re[i + k + len / 2] * ci + im[i + k + len / 2] * cr;
                    re[i + k] = ur + vr; im[i + k] = ui + vi;
                    re[i + k + len / 2] = ur - vr; im[i + k + len / 2] = ui - vi;
                    double nr = cr * wr - ci * wi; ci = cr * wi + ci * wr; cr = nr;
                }
            }
        }
    }
}

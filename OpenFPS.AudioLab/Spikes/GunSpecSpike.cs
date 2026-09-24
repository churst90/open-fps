using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenFPS.Client.AudioEngine.Core;

namespace OpenFPS.Client.Core.AudioEngine.Fmod;

/// <summary>
/// A gunshot synthesized to the spec measured from real ones, against the real ones.
///
///   --gun-spec [out=DIR] [refs=DIR]
///
/// The spec (docs/GUNFIRE.md): at 20 m and beyond a rifle's report is a positive phase of 0.3-0.5 ms,
/// down 20 dB within 2-4 ms and 30 dB within about 5; its spectrum is roughly flat from 125 Hz to
/// 2 kHz and falls about 13 dB by 4 kHz, 20 by 8 and 28 by 16. The shot here is a Friedlander pulse
/// and a short turbulent burst at one metre, carried to the listener by the inverse distance law,
/// ISO 9613-1 air absorption and a ground reflection. Each render is measured with the same numbers
/// as the recordings, and written next to the real shot with the range's echo cut away.
/// </summary>
public static class GunSpecSpike
{
    private const int Sr = 48000;

    /// <summary>What a gun's report is at one metre, from the spec.</summary>
    /// <param name="BurstLevel">Turbulent gas behind the shock, against the pulse's peak.</param>
    /// <param name="BurstDecayMs">Its time constant: the fast first drop, 10 dB in about a millisecond.</param>
    /// <param name="TrailLevel">What is left after it, against the burst.</param>
    /// <param name="TrailDecayMs">Its time constant: the slower fall from -20 to -30 dB.</param>
    /// <param name="CornerHz">One pole: the measured fall is about 6 dB an octave above 1-2 kHz.</param>
    public sealed record BlastSpec(string Name, float PositivePhaseMs, float BurstLevel, float BurstDecayMs,
                                   float TrailLevel, float TrailDecayMs, float CornerHz, float HighPassHz);

    public static readonly BlastSpec M16 = new("m16", 0.40f, 2.4f, 0.45f, 0.18f, 1.8f, 2500f, 220f);
    public static readonly BlastSpec Akm = new("wasr", 0.44f, 2.4f, 0.50f, 0.20f, 1.9f, 2300f, 200f);

    public static int Run(string[] args)
    {
        string root = "/home/cody/external-rescue/Github/open-fps/inbox";
        string dir = Arg(args, "out") ?? Path.Combine(root, "gunfire-spec-2026-09-24");
        string refs = Arg(args, "refs") ?? Path.Combine(root, "gunfire-references-2026-09-24");
        Directory.CreateDirectory(dir);

        var geoms = new (string File, float AngleDeg, float Metres)[]
        {
            ("side_90deg_20m", 90f, 20f), ("side_90deg_40m", 90f, 40f),
            ("behind_180deg_20m", 180f, 20f), ("behind_180deg_40m", 180f, 40f),
        };
        Console.WriteLine("\n  Rifle reports, synthesized to spec, against the real ones (echo cut away).");
        Console.WriteLine("  +phase ms, then ms to -10/-20/-30 dB, then octave bands 125..16k dB re the loudest.\n");
        foreach (var spec in new[] { M16, Akm })
        {
            Console.WriteLine($"  {spec.Name} source at 1 m: {Describe(Source(spec, 0.08f))}");
            Console.WriteLine($"  {spec.Name} at 20 m, no ground: {Describe(Air(Source(spec, 0.08f), 20f))}");
            foreach (var (file, angle, metres) in geoms)
            {
                string real = Path.Combine(refs, $"{spec.Name}_{file}.wav");
                if (!File.Exists(real)) continue;
                var rec = Cut(WeaponSynth.ReadWav16Mono(File.ReadAllBytes(real)), 0.080f);
                var syn = Render(spec, angle, metres, 0.080f);
                Console.WriteLine($"  {spec.Name} {file}");
                Console.WriteLine($"    real  {Describe(rec)}");
                Console.WriteLine($"    synth {Describe(syn)}");
                // Matched on the energy of their first 20 ms, so the pair differs in shape only.
                float g = Rms(rec, 0, (int)(0.02f * Sr)) / MathF.Max(1e-9f, Rms(syn, 0, (int)(0.02f * Sr)));
                for (int i = 0; i < syn.Length; i++) syn[i] *= g;
                var pair = new List<float>();
                pair.AddRange(new float[(int)(0.3f * Sr)]); pair.AddRange(rec);
                pair.AddRange(new float[(int)(0.8f * Sr)]); pair.AddRange(syn);
                pair.AddRange(new float[(int)(0.5f * Sr)]);
                Write(Path.Combine(dir, $"{spec.Name}_{file}_real_then_synth.wav"), pair.ToArray());
            }
        }
        Console.WriteLine($"\n  Each file: the real shot, a pause, then the synthesized one. In {dir}");
        return 0;
    }

    // ── The shot ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>The report at one metre: a Friedlander pulse and a short turbulent burst.</summary>
    public static float[] Source(BlastSpec s, float seconds, int seed = 1)
    {
        int n = (int)(seconds * Sr);
        var x = new float[n];
        var rng = new Random(seed);
        float T = s.PositivePhaseMs * 1e-3f;
        float tau = s.BurstDecayMs * 1e-3f;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Sr;
            // p(t) = (1 - t/T) e^(-t/T): the jump, the fall through zero at T, and the shallow
            // negative phase after it.
            float fried = (1f - t / T) * MathF.Exp(-t / T);
            float env = MathF.Exp(-t / tau) + s.TrailLevel * MathF.Exp(-t / (s.TrailDecayMs * 1e-3f));
            float burst = (float)(rng.NextDouble() * 2 - 1) * s.BurstLevel * env;
            x[i] = fried + burst;
        }
        // The spectrum: flat to the corner, then one pole, and little below the high-pass.
        float a = 1f - MathF.Exp(-2f * MathF.PI * s.CornerHz / Sr);
        float h = 1f - MathF.Exp(-2f * MathF.PI * s.HighPassHz / Sr);
        float l1 = 0f, hp = 0f;
        for (int i = 0; i < n; i++)
        {
            l1 += a * (x[i] - l1);
            hp += h * (l1 - hp);
            x[i] = l1 - hp;
        }
        return x;
    }

    /// <summary>What a listener hears at this angle and distance in the open, before any room.</summary>
    public static float[] Render(BlastSpec s, float angleDeg, float metres, float seconds)
    {
        var src = Source(s, seconds);
        // Air: ISO 9613-1 at 20 C and 50% humidity, applied per frequency over the whole path.
        var direct = Air(src, metres);
        // Ground: source and listener 1.5 m up over hard ground. The reflected path is a little
        // longer, a little later and a little weaker (inverse distance and a coefficient of 0.8).
        float hs = 1.5f, hr = 1.5f;
        float reflected = MathF.Sqrt(metres * metres + (hs + hr) * (hs + hr));
        int lag = (int)MathF.Round((reflected - metres) / 343f * Sr);
        float gr = 0.8f * metres / reflected;
        var y = new float[direct.Length];
        for (int i = 0; i < y.Length; i++)
            y[i] = (direct[i] + (i >= lag ? gr * direct[i - lag] : 0f)) / metres;
        return y;
    }

    /// <summary>ISO 9613-1 atmospheric absorption, dB per metre, at 20 C, 50% RH, 101.325 kPa.</summary>
    public static double AlphaDbPerMetre(double f)
    {
        const double T = 293.15, T0 = 293.15, T01 = 273.16, pr = 1.0, hr = 50.0;
        double psat = Math.Pow(10, -6.8346 * Math.Pow(T01 / T, 1.261) + 4.6151);
        double h = hr * psat / pr;
        double frO = pr * (24 + 4.04e4 * h * (0.02 + h) / (0.391 + h));
        double frN = pr * Math.Pow(T / T0, -0.5) * (9 + 280 * h * Math.Exp(-4.170 * (Math.Pow(T / T0, -1.0 / 3) - 1)));
        return 8.686 * f * f * (1.84e-11 / pr * Math.Sqrt(T / T0)
            + Math.Pow(T / T0, -2.5) * (0.01275 * Math.Exp(-2239.1 / T) / (frO + f * f / frO)
                                        + 0.1068 * Math.Exp(-3352.0 / T) / (frN + f * f / frN)));
    }

    /// <summary>
    /// Air absorption applied in the frequency domain. Padded 40 ms either side and trimmed back:
    /// a filter applied by DFT is circular, and without the pad the spread it adds wrapped round to
    /// the END of the buffer, a faint copy of the shot 80 ms after it — heard as an echo.
    /// </summary>
    private static float[] Air(float[] x0, float metres)
    {
        int pad = Sr / 25;
        var x = new float[x0.Length + 2 * pad];
        Array.Copy(x0, 0, x, pad, x0.Length);
        var full = AirCircular(x, metres);
        return full.AsSpan(pad, x0.Length).ToArray();
    }

    private static float[] AirCircular(float[] x, float metres)
    {
        int n = x.Length;
        var re = new double[n]; var im = new double[n];
        for (int k = 0; k <= n / 2; k++)
        {
            double sr = 0, si = 0, w = -2 * Math.PI * k / n;
            for (int i = 0; i < n; i++) { sr += x[i] * Math.Cos(w * i); si += x[i] * Math.Sin(w * i); }
            double g = Math.Pow(10, -AlphaDbPerMetre(k * (double)Sr / n) * metres / 20);
            re[k] = sr * g; im[k] = si * g;
        }
        var y = new float[n];
        for (int i = 0; i < n; i++)
        {
            double v = re[0];
            for (int k = 1; k < (n + 1) / 2; k++)
            {
                double w = 2 * Math.PI * k * i / n;
                v += 2 * (re[k] * Math.Cos(w) - im[k] * Math.Sin(w));
            }
            if (n % 2 == 0) v += re[n / 2] * Math.Cos(Math.PI * i);
            y[i] = (float)(v / n);
        }
        return y;
    }

    // ── Measuring, the same way as the recordings ────────────────────────────────────────────────

    private static float[] Cut(float[] x, float seconds)
    {
        int pk = 0; for (int i = 1; i < x.Length; i++) if (MathF.Abs(x[i]) > MathF.Abs(x[pk])) pk = i;
        float lim = 0.05f * MathF.Abs(x[pk]);
        int i0 = pk; while (i0 > 0 && MathF.Abs(x[i0]) > lim) i0--;
        i0 = Math.Max(0, i0 - (int)(0.0005f * Sr));
        int n = Math.Min((int)(seconds * Sr), x.Length - i0);
        var y = x.AsSpan(i0, n).ToArray();
        int fade = (int)(0.010f * Sr);
        for (int i = 0; i < fade; i++) y[n - 1 - i] *= i / (float)fade;
        return y;
    }

    private static string Describe(float[] x)
    {
        int pk = 0; for (int i = 1; i < x.Length; i++) if (MathF.Abs(x[i]) > MathF.Abs(x[pk])) pk = i;
        float peak = MathF.Abs(x[pk]);
        int i0 = pk; while (i0 > 0 && MathF.Abs(x[i0]) > 0.05f * peak) i0--;
        int sign = x[pk] > 0 ? 1 : -1;
        int zc = pk; while (zc < x.Length && x[zc] * sign >= 0) zc++;
        float pos = (zc - i0) * 1000f / Sr;
        int w = Sr / 4000;
        var env = new List<float>();
        for (int i = i0; i + w < Math.Min(x.Length, i0 + (int)(0.025f * Sr)); i += w) env.Add(Rms(x, i, i + w));
        float e0 = env.Max(); int ie = env.IndexOf(e0);
        string T(float db) { for (int i = ie; i < env.Count; i++) if (20 * MathF.Log10(env[i] / e0 + 1e-12f) < db) return $"{(i - ie) * 0.25f,5:F2}"; return "  >25"; }
        var bands = new[] { 125, 250, 500, 1000, 2000, 4000, 8000, 16000 }.Select(fc => Band(x, i0, fc)).ToArray();
        float top = bands.Max();
        return $"+phase {pos,4:F2}  -10 {T(-10)}  -20 {T(-20)}  -30 {T(-30)}  bands " + string.Join(" ", bands.Select(b => $"{b - top,4:F0}"));
    }

    /// <summary>
    /// Energy in one octave band over the event: a flat-topped (Tukey) window from a millisecond
    /// before the onset to 20 ms after. A Hann window starting AT the onset is nearly zero over the
    /// first three milliseconds, where almost all of a gunshot is, and measured its tail instead.
    /// </summary>
    private static float Band(float[] x, int onset, float fc)
    {
        int i0 = Math.Max(0, onset - Sr / 1000);
        int n = Math.Min((int)(0.021f * Sr), x.Length - i0);
        int taper = n / 10;
        double e = 0;
        for (int k = 0; k < 5; k++)
        {
            double f = fc * Math.Pow(2, (k - 2) / 6.0), w = 2 * Math.PI * f / Sr, re = 0, im = 0;
            for (int i = 0; i < n; i++)
            {
                double win = i < taper ? 0.5 - 0.5 * Math.Cos(Math.PI * i / taper)
                           : i >= n - taper ? 0.5 - 0.5 * Math.Cos(Math.PI * (n - 1 - i) / taper) : 1.0;
                re += x[i0 + i] * win * Math.Cos(w * i); im -= x[i0 + i] * win * Math.Sin(w * i);
            }
            e += re * re + im * im;
        }
        return (float)(10 * Math.Log10(e / 5 + 1e-20));
    }

    private static float Rms(float[] x, int a, int b)
    {
        double e = 0; b = Math.Min(b, x.Length);
        for (int i = a; i < b; i++) e += x[i] * (double)x[i];
        return (float)Math.Sqrt(e / Math.Max(1, b - a));
    }

    private static void Write(string path, float[] x)
    {
        float peak = x.Select(MathF.Abs).Max();
        var y = x.Select(v => v * 0.89f / MathF.Max(1e-9f, peak)).ToArray();
        File.WriteAllBytes(path, WeaponSynth.ToWav16(y, Sr));
    }

    private static string? Arg(string[] args, string name)
        => args.FirstOrDefault(a => a.StartsWith(name + "=", StringComparison.Ordinal))?.Substring(name.Length + 1);
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Common;

namespace OpenFPS.AudioLab.Spikes;

/// <summary>
/// A person walking into things, for listening before any of it goes in the game.
///
///   --bumps [--speed=1.4] [--out=DIR]
///
/// A body part meeting a surface as a mass on a spring (the contact), driving the surface's own bending
/// modes from its registry material at a real size and thickness. Nothing here is a bump sound: the
/// level and the colour are what the masses, stiffnesses and damping give. See Strike.
/// </summary>
public static class BumpSpike
{
    // What you walk into: material, face width x height, thickness, metres.
    static readonly (string Name, string Material, float W, float H, float T)[] Surfaces =
    {
        ("concrete_wall", "Concrete", 3.0f, 3.0f, 0.20f),
        ("brick_wall", "Brick", 3.0f, 3.0f, 0.23f),
        ("plasterboard_wall", "Plaster", 1.2f, 2.4f, 0.0125f),
        ("wooden_door", "Wood", 0.9f, 2.0f, 0.04f),
        ("steel_door", "Metal", 0.9f, 2.0f, 0.0015f),
        ("car_door", "Metal", 1.0f, 0.6f, 0.0008f),
        ("car_window", "Glass", 0.8f, 0.45f, 0.004f),
        ("shop_window", "Glass", 2.0f, 2.2f, 0.010f),
        ("steel_fence", "Fence", 2.4f, 1.8f, 0.004f),
        ("hedge", "Foliage", 2.0f, 1.8f, 0.8f),
    };

    /// <summary>
    /// One part of the body meeting the surface: mass, contact stiffness and damping, and the size of
    /// the part (an equivalent sphere's radius, for the sound its own stop makes).
    ///
    /// The shoulder is measured: 12.8 kN/m and 377 N s/m at an effective mass of 12.9 kg (shoulder
    /// checks in ice hockey, Sports Biomechanics 23(10), doi 10.1080/14763141.2021.1951828, and 14(1),
    /// doi 10.1080/14763141.2015.1025236). That contact lasts about 100 ms and drives next to nothing
    /// above 20 Hz. The toe is an ESTIMATE: a foot and shoe of 1.1 kg (1.45 % of body mass, Dempster)
    /// on a heel-pad stiffness of 150 kN/m, about 9 ms of contact.
    /// </summary>
    readonly record struct BodyPart(string Name, float MassKg, float StiffnessNPerM, float DampingNsPerM, float RadiusM, float LagSeconds);

    static readonly BodyPart[] Parts =
    {
        new("toe", 1.1f, 150_000f, 80f, 0.062f, 0.00f),
        new("shoulder", 12.9f, 12_800f, 377f, 0.19f, 0.15f),
    };

    const float Rho0 = 1.2f, C0 = 343f;

    public static int Run(string[] args)
    {
        float speed = Arg(args, "--speed=", 1.4f);   // walking pace, m/s
        string outDir = Array.Find(args, a => a.StartsWith("--out="))?[6..]
                        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "openfps-listen", "bumps");
        Directory.CreateDirectory(outDir);
        AcousticRegistry.Initialize();
        foreach (var s in Surfaces)
        {
            if (s.Material == "Foliage") { Console.WriteLine($"  {s.Name,-18} skipped: brushing through leaves is a rustle, not a bump"); continue; }
            var m = AcousticRegistry.GetProperties(s.Material);
            int n = TransientSynth.SampleRate * 2;
            var p = new double[n];
            foreach (var part in Parts) Strike(part, m, s.W, s.H, s.T, speed, p);
            double peak = 0, sum = 0;
            foreach (double x in p) { peak = Math.Max(peak, Math.Abs(x)); sum += x * x; }
            // Level: the loudest 125 ms, as a sound level meter's FAST would read it.
            int w = TransientSynth.SampleRate / 8; double best = 0, acc = 0;
            for (int i = 0; i < n; i++) { acc += p[i] * p[i]; if (i >= w) acc -= p[i - w] * p[i - w]; best = Math.Max(best, acc / w); }
            Console.WriteLine($"  {s.Name,-18} {10 * Math.Log10(best / 4e-10):F1} dB SPL fast at 1 m, peak {20 * Math.Log10(peak / 2e-5):F1} dB, " +
                              $"band <200 {Band(p, 0, 200):F1} | 200-1k {Band(p, 200, 1000):F1} | 1k-4k {Band(p, 1000, 4000):F1} | >4k {Band(p, 4000, 20000):F1} dB");
            // Written as pressure: 1 Pa (94 dB) is full scale, so the files compare by level.
            var mono = new float[n];
            for (int i = 0; i < n; i++) mono[i] = (float)Math.Clamp(p[i], -1.0, 1.0);
            Write(Path.Combine(outDir, s.Name + ".wav"), mono);
        }
        Console.WriteLine($"wrote {outDir} (1 Pa, 94 dB SPL at 1 m, is full scale)");
        return 0;
    }

    /// <summary>
    /// One body part striking a simply supported plate at its centre, walking pace, integrated at the
    /// sample rate. The plate answers on every odd-odd bending mode (PanelAcoustics.Modes' rule), each
    /// with a modal mass of a quarter of the plate's, and radiates as a baffled source of its net
    /// volume velocity: p = rho0 dQ/dt / (2 pi r). The part itself radiates as a rigid sphere brought
    /// to a stop: p = rho0 R^3 d(accel)/dt / (2 r c) on its axis.
    /// </summary>
    static void Strike(BodyPart part, MaterialProperties m, float w, float h, float t, float speed, double[] p)
    {
        int sr = TransientSynth.SampleRate;
        double dt = 1.0 / sr;
        double mass = m.DensityKgM3 * w * h * t;
        var modes = PanelAcoustics.Modes(m, w, h, t, maxHz: 12000f, order: 31);
        int k = modes.Count;
        var u = new double[k]; var ud = new double[k];
        double modal = mass / 4.0, area = w * h, eta = m.LossFactor + PanelAcoustics.MountedLoss;
        double x = 0, v = speed, aPrev = 0;         // the part: position into the plate, speed
        int start = (int)(part.LagSeconds * sr) + sr / 10;
        double qPrev = 0;
        bool touching = true;
        for (int i = start; i < p.Length; i++)
        {
            double centre = 0, centreV = 0;
            for (int j = 0; j < k; j++) { double sign = ((modes[j].M + modes[j].N) / 2 % 2 == 0) ? 1 : -1; centre += sign * u[j]; centreV += sign * ud[j]; }
            double f = 0;
            if (touching)
            {
                double squeeze = x - centre;
                // Hunt-Crossley: the damping grows with the squeeze, so the force starts from zero. A
                // plain spring-and-damper jumps to c v on touching, an infinitely sharp edge that rings
                // every mode there is. The measured damping is matched at the squeeze of the peak force.
                double peakSqueeze = speed * Math.Sqrt(part.MassKg / part.StiffnessNPerM);
                f = squeeze > 0 ? Math.Max(0, part.StiffnessNPerM * squeeze
                                              + part.DampingNsPerM * (squeeze / peakSqueeze) * (v - centreV)) : 0;
                if (squeeze <= 0 && v < centreV) touching = false;         // it has come away
            }
            double a = -f / part.MassKg;
            v += a * dt; x += v * dt;
            if (!touching && i > start + sr / 2 && k == 0) break;
            double q = 0;
            for (int j = 0; j < k; j++)
            {
                double om = 2 * Math.PI * modes[j].Hz;
                double sign = ((modes[j].M + modes[j].N) / 2 % 2 == 0) ? 1 : -1;
                double acc = sign * f / modal - eta * om * ud[j] - om * om * u[j];
                ud[j] += acc * dt; u[j] += ud[j] * dt;
                q += ud[j] * area * 4.0 / (modes[j].M * modes[j].N * Math.PI * Math.PI);
            }
            p[i] += Rho0 * (q - qPrev) / dt / (2 * Math.PI);                   // the plate, at 1 m
            p[i] += Rho0 * Math.Pow(part.RadiusM, 3) * ((a - aPrev) / dt) / (2 * C0);   // the part's own stop, at 1 m
            qPrev = q; aPrev = a;
        }
    }

    /// <summary>Energy in a band, dB SPL, by a brick-wall FFT of the whole buffer.</summary>
    static double Band(double[] p, double lo, double hi)
    {
        int n = 1; while (n < p.Length) n <<= 1;
        var re = new double[n]; var im = new double[n];
        Array.Copy(p, re, p.Length);
        Fft(re, im);
        double e = 0, df = (double)TransientSynth.SampleRate / n;
        for (int i = 1; i < n / 2; i++) { double f = i * df; if (f >= lo && f < hi) e += re[i] * re[i] + im[i] * im[i]; }
        e = 2 * e / ((double)n * n) * n / (TransientSynth.SampleRate / 8.0);      // as a 125 ms mean square
        return 10 * Math.Log10(Math.Max(1e-20, e) / 4e-10);
    }

    static void Fft(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1; for (; (j & bit) != 0; bit >>= 1) j ^= bit; j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len; double wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (int j = 0; j < len / 2; j++)
                {
                    double ur = re[i + j], ui = im[i + j];
                    double vr = re[i + j + len / 2] * cr - im[i + j + len / 2] * ci, vi = re[i + j + len / 2] * ci + im[i + j + len / 2] * cr;
                    re[i + j] = ur + vr; im[i + j] = ui + vi; re[i + j + len / 2] = ur - vr; im[i + j + len / 2] = ui - vi;
                    double t = cr * wr - ci * wi; ci = cr * wi + ci * wr; cr = t;
                }
            }
        }
    }

    static float Arg(string[] args, string key, float fallback)
    {
        string? a = Array.Find(args, x => x.StartsWith(key));
        return a != null && float.TryParse(a[key.Length..], System.Globalization.NumberStyles.Float,
                                           System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : fallback;
    }

    static void Write(string path, float[] mono)
    {
        string raw = Path.GetTempFileName();
        var b = new byte[mono.Length * 4];
        Buffer.BlockCopy(mono, 0, b, 0, b.Length);
        File.WriteAllBytes(raw, b);
        Process.Start(new ProcessStartInfo("sox", $"-t f32 -r {TransientSynth.SampleRate} -c 1 \"{raw}\" -b 16 \"{path}\"") { UseShellExecute = false })!.WaitForExit();
        File.Delete(raw);
    }
}

using System;
using System.Linq;
using System.Numerics;
using OpenFPS.Client.Core.AudioEngine.SteamAudio;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// The late part as a field (DiffuseLate): one independent noise per direction under the averaged
/// energy. The directions must be independent, carry the energy the one-channel tail carried, band
/// by band, and not ring; the convolver must play exactly that, from where the late part begins.
/// </summary>
public class DiffuseLateTests
{
    private const int Rate = 44100, Sub = 256;
    private static readonly int Length = (int)(1.2 * Rate);
    private static readonly DiffuseLateNoise Noise = DiffuseLateNoise.Shared(Rate, 2 * Rate, DiffuseBranch.Count);

    /// <summary>A decaying Gaussian noise tail, white, 60 dB down in about 3.5 s.</summary>
    private static float[] Tail(int seed)
    {
        var r = new Random(seed); var w = new float[Length];
        for (int i = 0; i < Length; i++)
        {
            double g = Math.Sqrt(-2 * Math.Log(1 - r.NextDouble())) * Math.Cos(2 * Math.PI * r.NextDouble());
            w[i] = (float)(g * Math.Exp(-i / (0.5 * Rate)));
        }
        return w;
    }

    private static Vector3[] Dirs()
    {
        var d = new Vector3[DiffuseBranch.Count];
        for (int i = 0; i < d.Length; i++) d[i] = DiffuseTail.Direction(i);
        return d;
    }

    /// <summary>A smoothed tail with directions, fed one trace of a field from everywhere.</summary>
    private static SmoothTail Room(int seed = 1)
    {
        var s = new SmoothTail(Rate, Length, DiffuseBranch.Count);
        var w = Tail(seed);
        var r = new Random(seed + 100);
        var c = new float[3][];
        for (int k = 0; k < 3; k++) c[k] = w.Select(v => v * (float)(r.NextDouble() * 2 - 1) * 0.3f).ToArray();
        s.Add(w, c[0], c[1], c[2], Vector3.UnitY, Vector3.UnitZ, Vector3.UnitX, Dirs(), null, Vector3.Zero, 1, false);
        return s;
    }

    private static double[] BandEnergy(float[] x, int from, int to)
    {
        var split = new SmoothTail.Splitter(Rate);
        var e = new double[SmoothTail.Bands];
        var band = new double[SmoothTail.Bands];
        for (int i = 0; i < Math.Min(to, x.Length); i++)
        {
            split.Run(x[i], band);
            if (i >= from) for (int b = 0; b < band.Length; b++) e[b] += band[b] * band[b];
        }
        return e;
    }

    /// <summary>% of bins 10 dB over the median of the third octave round them, 100 Hz-10 kHz, and
    /// the spectral flatness; of <paramref name="h"/> from <paramref name="a"/> to <paramref name="b"/>
    /// seconds, its decay flattened first. Noise: 0.1 % and 0.56 (the lab's --tail-steady measure).</summary>
    private static (double Over10, double Flat) Ring(float[] h, double a, double b)
    {
        int i0 = (int)(a * Rate), i1 = Math.Min(h.Length, (int)(b * Rate)), n = 32768;
        var re = new float[n]; var im = new float[n];
        int sm = Rate / 100;
        for (int i = 0; i < i1 - i0 && i < n; i++)
        {
            double e = 0; int c = 0;
            for (int j = Math.Max(i0, i0 + i - sm); j < Math.Min(i1, i0 + i + sm); j += 4) { e += h[j] * (double)h[j]; c++; }
            re[i] = (float)(h[i0 + i] / Math.Sqrt(e / Math.Max(1, c) + 1e-30) * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (i1 - i0))));
        }
        new Fft(n).Forward(re, im);
        var p = new double[n / 2];
        for (int k = 0; k < p.Length; k++) p[k] = re[k] * (double)re[k] + im[k] * (double)im[k];
        double hz = (double)Rate / n;
        int k0 = (int)(100 / hz), k1 = (int)(10000 / hz), over = 0, cnt = 0;
        double logSum = 0, linSum = 0;
        for (int k = k0; k < k1; k++)
        {
            int lo = (int)(k / Math.Pow(2, 1.0 / 6)), hi = (int)(k * Math.Pow(2, 1.0 / 6));
            var win = new double[hi - lo + 1];
            Array.Copy(p, lo, win, 0, win.Length);
            Array.Sort(win);
            if (p[k] > 10 * win[win.Length / 2]) over++;
            logSum += Math.Log(p[k] + 1e-30); linSum += p[k]; cnt++;
        }
        return (over / (double)cnt, Math.Exp(logSum / cnt) / (linSum / cnt));
    }

    [Fact]
    public void EachDirectionIsItsOwnNoise()
    {
        var ir = Room().BuildDiffuseLate(Noise);
        var h = Enumerable.Range(0, DiffuseBranch.Count).Select(ir.ToTime).ToArray();
        int a = (int)(0.3 * Rate), b = (int)(1.0 * Rate);
        double worst = 0;
        for (int i = 0; i < h.Length; i++)
            for (int j = i + 1; j < h.Length; j++)
            {
                // Within a millisecond either way: what two ears could pick up.
                for (int lag = -44; lag <= 44; lag += 4)
                {
                    double c = 0, ei = 0, ej = 0;
                    for (int t = a; t < b; t++) { double x = h[i][t], y = h[j][t + lag]; c += x * y; ei += x * x; ej += y * y; }
                    worst = Math.Max(worst, Math.Abs(c) / Math.Sqrt(ei * ej));
                }
            }
        // Independent noise over 0.7 s: about 1/sqrt(B T) by chance, under 0.05.
        Assert.True(worst < 0.05, $"two directions correlate {worst:F3}");
    }

    [Fact]
    public void EveryDirectionCarriesTheLateEnergyBandByBand()
    {
        // What the one-channel tail played after the directional part, and what each direction of the
        // field plays: the same energy per band (each direction is then sent at 1/sqrt(20)). And what
        // the field plays is what its own envelope says, through its frequency-domain shaping.
        var s = Room(2);
        var want = BandEnergy(s.LateWindowed(afterDirectional: true), 0, Length);
        var ir = s.BuildDiffuseLate(Noise);
        int dirs = DiffuseBranch.Count, P = ir.Partitions, B = DiffuseLateNoise.Block;
        var got = new double[SmoothTail.Bands];
        for (int d = 0; d < dirs; d++)
        {
            var e = BandEnergy(ir.ToTime(d), 0, Length + 2 * B);
            for (int b = 0; b < got.Length; b++) got[b] += e[b] / dirs;
        }
        const double T = 0.25;                                   // the tail's energy lasts about this
        for (int b = 0; b < got.Length; b++)
        {
            double lo = b == 0 ? 0 : SmoothTail.EdgesHz[b - 1], hi = b < SmoothTail.EdgesHz.Length ? SmoothTail.EdgesHz[b] : Rate / 2.0;
            // Noise in a band W wide over T seconds has its energy known to about 4.3/sqrt(WT) dB; three of those.
            double tolOne = Math.Max(0.5, 3 * 4.34 / Math.Sqrt((hi - lo) * T));
            double tolField = Math.Max(0.3, 3 * 4.34 / Math.Sqrt((hi - lo) * T * dirs));
            double db = 10 * Math.Log10(got[b] / want[b]);
            double env = 0;
            for (int p = 0; p < P; p++) { double c0 = ir.C0[b * P + p], c1 = ir.C1[b * P + p]; env += B * (c0 * c0 + c0 * c1 + c1 * c1) / 3; }
            double db2 = 10 * Math.Log10(got[b] / env);
            Console.WriteLine($"band {b}: field - one channel {db:F2} dB (allowed {tolOne:F1}), field - its envelope {db2:F2} dB (allowed {tolField:F2})");
            Assert.True(Math.Abs(db) < tolOne, $"band {b}: {db:F2} dB off the one-channel tail (allowed {tolOne:F1})");
            Assert.True(Math.Abs(db2) < tolField, $"band {b}: {db2:F2} dB off its own envelope (allowed {tolField:F2})");
        }
        double total = 0; foreach (var v in want) total += v;
        Assert.InRange(10 * Math.Log10(ir.Energy / total), -0.3, 0.3);
    }

    [Fact]
    public void TheFieldDoesNotRingWhereTheVelvetCopiesDid()
    {
        // The twenty directions summed (as two ears hear them, before the head): noise. The old way,
        // one channel through twenty velvet filters summed and a velvet filter after: a product of
        // random spectra, the metallic ring (--tail-steady: 10-11 % at the ear).
        var s = Room(3);
        var ir = s.BuildDiffuseLate(Noise);
        var sum = new float[Length + 2 * DiffuseLateNoise.Block];
        for (int d = 0; d < DiffuseBranch.Count; d++)
        {
            var h = ir.ToTime(d);
            for (int i = 0; i < sum.Length && i < h.Length; i++) sum[i] += h[i];
        }
        var field = Ring(sum, 0.4, 0.9);

        var w = s.LateWindowed(afterDirectional: true);
        var old = new float[w.Length];
        for (int d = 0; d < DiffuseBranch.Count; d++)
        {
            var br = new DiffuseBranch(d);
            for (int i = 0; i < w.Length; i++) old[i] += br.Process(w[i]);
        }
        var (ear, _) = DiffuseBranch.EarPair(101);
        for (int i = 0; i < old.Length; i++) old[i] = ear.Process(old[i]);
        var velvet = Ring(old, 0.4, 0.9);

        Assert.True(field.Over10 < 0.005, $"the field: {field.Over10 * 100:F2} % of bins 10 dB over their neighbours");
        Assert.True(field.Flat > 0.45, $"the field's flatness {field.Flat:F2}");
        Assert.True(velvet.Over10 > 3 * field.Over10 + 0.01, $"the velvet copies: {velvet.Over10 * 100:F2} % against {field.Over10 * 100:F2} %");
    }

    [Fact]
    public void TheConvolverPlaysTheFieldFromWhereTheLatePartBegins()
    {
        // An impulse in: each direction's answer is the field's response, nothing before its start.
        var ir = Room(4).BuildDiffuseLate(Noise);
        var conv = new DiffuseLateConvolver(Sub, Noise.Partitions, DiffuseBranch.Count, Noise.Start);
        conv.Set(ir);
        int n = Noise.Start + (ir.Partitions + 2) * DiffuseLateNoise.Block;
        n = (n / Sub + 1) * Sub;
        var y = Enumerable.Range(0, DiffuseBranch.Count).Select(_ => new float[n]).ToArray();
        var outs = Enumerable.Range(0, DiffuseBranch.Count).Select(_ => new float[Sub]).ToArray();
        var x = new float[Sub];
        for (int at = 0; at < n; at += Sub)
        {
            Array.Clear(x); if (at == 0) x[0] = 1f;
            conv.Process(x, outs);
            for (int d = 0; d < outs.Length; d++) Array.Copy(outs[d], 0, y[d], at, Sub);
        }
        foreach (int d in new[] { 0, 7, 19 })
        {
            var h = ir.ToTime(d);
            double before = 0, err = 0, sig = 0;
            for (int i = 0; i < Noise.Start; i++) before += y[d][i] * (double)y[d][i];
            // Each block's own span; ToTime lays the few milliseconds each block spills past its end
            // after it, which an impulse at a block's start does not hear.
            for (int p = 0; p < ir.Partitions; p++)
            {
                int a = Noise.Start + p * DiffuseLateNoise.Block;
                var only = BlockOnly(ir, d, p);
                for (int t = 0; t < DiffuseLateNoise.Block; t++) { double e = y[d][a + t] - only[t]; err += e * e; sig += only[t] * (double)only[t]; }
            }
            Assert.Equal(0.0, before, 12);
            Assert.True(err / sig < 1e-4, $"direction {d}: relative error {err / sig:E2}");
            // And what spills past each block is small: the shaping is smooth across frequency.
            double all = 0; foreach (var v in h) all += v * (double)v;
            Assert.InRange(sig / all, 0.97, 1.001);
        }
    }

    /// <summary>Block <paramref name="p"/> of direction <paramref name="d"/>: the first Block samples
    /// of its response (what an impulse at a block's start hears).</summary>
    private static float[] BlockOnly(DiffuseLateIr ir, int d, int p)
    {
        int B = DiffuseLateNoise.Block, n = 2 * B, bins = ir.Bins;
        var re = new float[n]; var im = new float[n];
        int o = (d * ir.Noise.Partitions + p) * bins, a = p * bins;
        for (int k = 0; k < bins; k++)
        {
            re[k] = ir.A0[a + k] * ir.Noise.N0Re[o + k] + ir.A1[a + k] * ir.Noise.N1Re[o + k];
            im[k] = ir.A0[a + k] * ir.Noise.N0Im[o + k] + ir.A1[a + k] * ir.Noise.N1Im[o + k];
        }
        for (int k = 1; k < B; k++) { re[n - k] = re[k]; im[n - k] = -im[k]; }
        new Fft(n).Inverse(re, im);
        return re.Take(B).ToArray();
    }

    [Fact]
    public void ANewFieldTakesOverWithoutAStep()
    {
        var a = Room(5).BuildDiffuseLate(Noise);
        var s = new SmoothTail(Rate, Length, DiffuseBranch.Count);
        var w = Tail(6).Select(v => v * 0.5f).ToArray();
        s.Add(w, w, w, w, Vector3.UnitY, Vector3.UnitZ, Vector3.UnitX, Dirs(), null, Vector3.Zero, 1, false);
        var b = s.BuildDiffuseLate(Noise);
        var conv = new DiffuseLateConvolver(Sub, Noise.Partitions, DiffuseBranch.Count, Noise.Start);
        conv.Set(a);
        var r = new Random(7);
        int n = 3 * Rate / Sub * Sub, swap = 2 * Rate / Sub * Sub;
        var y = new float[n];
        var outs = Enumerable.Range(0, DiffuseBranch.Count).Select(_ => new float[Sub]).ToArray();
        var x = new float[Sub];
        for (int at = 0; at < n; at += Sub)
        {
            for (int i = 0; i < Sub; i++) x[i] = (float)(r.NextDouble() * 2 - 1);
            if (at == swap) conv.Set(b);
            conv.Process(x, outs);
            Array.Copy(outs[3], 0, y, at, Sub);
        }
        // No sample-to-sample jump after the handover larger than the signal's own before it.
        float before = 0f, after = 0f;
        for (int i = swap - Rate / 2; i < swap; i++) before = MathF.Max(before, MathF.Abs(y[i] - y[i - 1]));
        for (int i = swap; i < swap + Rate / 2; i++) after = MathF.Max(after, MathF.Abs(y[i] - y[i - 1]));
        Assert.True(after <= before * 1.2f, $"step {after:F4} after the handover against {before:F4} before");
    }
}

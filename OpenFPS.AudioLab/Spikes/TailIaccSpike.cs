using System;
using System.Linq;
using OpenFPS.Client.Core.AudioEngine.SteamAudio;

namespace OpenFPS.Client.Core.AudioEngine.Fmod;

/// <summary>
/// The tail's spatial rendering alone: three seconds of noise as the tail's omnidirectional channel,
/// through DiffuseTail (each direction straight through its own head response), and the two ears'
/// coherence per octave — the maximum normalised
/// cross-correlation within a millisecond either way, after an octave band-pass, as the research of
/// 2026-09-29 measured it. A head in a real diffuse field: about 0.95 at 125 Hz, 0.8 at 250, 0.4 at
/// 500, 0.2 at 1 kHz and under 0.15 above.
///
///   --tail-iacc
/// </summary>
public static class TailIaccSpike
{
    private const int Fs = 44100, Sub = 256;

    public static int Run(string[] args)
    {
        var cs = Phonon.DefaultContextSettings();
        if (Phonon.iplContextCreate(ref cs, out IntPtr ctx) != Phonon.IPL_STATUS_SUCCESS) { Console.WriteLine("FAIL: no context"); return 1; }
        var au = new Phonon.IPLAudioSettings { samplingRate = Fs, frameSize = Sub };
        var hs = new Phonon.IPLHRTFSettings { type = Phonon.IPL_HRTFTYPE_DEFAULT, volume = 1f, normType = Phonon.IPL_HRTFNORMTYPE_NONE };
        Phonon.iplHRTFCreate(ctx, ref au, ref hs, out IntPtr hrtf);
        int ch = TracedReverb.Channels, n = Fs * 3;
        var rng = new Random(5);
        var noise = new float[n];
        for (int i = 0; i < n; i++) noise[i] = (float)(rng.NextDouble() * 2 - 1) * 0.3f;

        foreach (float yaw in new[] { 0f, 23f, 45f, 90f })
        {
            var rot = System.Numerics.Quaternion.CreateFromYawPitchRoll(yaw * MathF.PI / 180f, 0f, 0f);
            var df = DiffuseTail.Create(ctx, Sub, ch, hrtf);
            if (df == null) { Console.WriteLine("FAIL: no binaural effects"); return 1; }
            df.SetListenerRotation(rot);
            var L = new float[n]; var R = new float[n];
            var inter = new float[Sub * ch];
            for (int at = 0; at + Sub <= n; at += Sub)
            {
                Array.Clear(inter);
                for (int k = 0; k < Sub; k++) inter[k * ch] = noise[at + k];
                df.RenderBinaural(inter, Sub, ch);
                for (int k = 0; k < Sub; k++) { L[at + k] = df.Stereo[k * 2] + df.Low[k]; R[at + k] = df.Stereo[k * 2 + 1] + df.Low[k]; }
            }
            double eIn = noise.Skip(Fs / 2).Sum(x => (double)x * x), eOut = L.Skip(Fs / 2).Zip(R.Skip(Fs / 2), (l, r) => ((double)l * l + (double)r * r) / 2).Sum();
            var bands = new[] { 125.0, 250, 500, 1000, 2000, 4000 };
            var iacc = bands.Select(f => Iacc(Band(L, f), Band(R, f))).ToArray();
            // Ringing: how strongly an ear repeats itself at some lag from 0.2 to 10 ms, fed noise. A
            // filter with a regular tap spacing is a comb and shows here as a peak (a metallic pitch).
            var seg = L.Skip(Fs).Take(Fs).ToArray();
            double e0 = seg.Sum(v => (double)v * v), ring = 0; int ringLag = 0;
            for (int lag = 9; lag <= 441; lag++)
            {
                double c = 0; for (int i = 0; i + lag < seg.Length; i++) c += seg[i] * (double)seg[i + lag];
                if (Math.Abs(c) / e0 > ring) { ring = Math.Abs(c) / e0; ringLag = lag; }
            }
            Console.WriteLine($"yaw {yaw,3:F0}: level {10 * Math.Log10(eOut / eIn):F1} dB; ringing {ring:F2} at {ringLag * 1000.0 / Fs:F2} ms; IACC "
                            + string.Join("  ", bands.Select((f, i) => $"{f:F0}:{iacc[i]:F2}"))
                            + $"   (head's diffuse-field gain {df.DiffuseFieldGainDb:F1} dB)");
        }
        // The best this head and this estimator can do: 200 directions spread evenly over the sphere
        // (a Fibonacci lattice), each fed its OWN independent noise. Whatever this reads is the real
        // target for this HRTF; the textbook figures are real heads.
        {
            const int N = 200;
            var bs = new Phonon.IPLBinauralEffectSettings { hrtf = hrtf };
            var fx = new IntPtr[N];
            for (int b = 0; b < N; b++) Phonon.iplBinauralEffectCreate(ctx, ref au, ref bs, out fx[b]);
            var mono = new Phonon.IPLAudioBuffer(); Phonon.iplAudioBufferAllocate(ctx, 1, Sub, ref mono);
            var st2 = new Phonon.IPLAudioBuffer(); Phonon.iplAudioBufferAllocate(ctx, 2, Sub, ref st2);
            var m = new float[Sub]; var s2 = new float[Sub * 2];
            var L = new float[n]; var R = new float[n];
            var rngs = Enumerable.Range(0, N).Select(i => new Random(1000 + i)).ToArray();
            for (int at = 0; at + Sub <= n; at += Sub)
                for (int b = 0; b < N; b++)
                {
                    double y = 1 - 2 * (b + 0.5) / N, rad = Math.Sqrt(1 - y * y), th = b * Math.PI * (3 - Math.Sqrt(5));
                    var dir = new Phonon.IPLVector3 { x = (float)(rad * Math.Cos(th)), y = (float)y, z = (float)(rad * Math.Sin(th)) };
                    for (int k = 0; k < Sub; k++) m[k] = (float)(rngs[b].NextDouble() * 2 - 1) * 0.3f / MathF.Sqrt(N);
                    Phonon.iplAudioBufferDeinterleave(ctx, m, ref mono);
                    var ep = new Phonon.IPLBinauralEffectParams { direction = dir, interpolation = Phonon.IPL_HRTFINTERPOLATION_BILINEAR, spatialBlend = 1f, hrtf = hrtf };
                    Phonon.iplBinauralEffectApply(fx[b], ref ep, ref mono, ref st2);
                    Phonon.iplAudioBufferInterleave(ctx, ref st2, s2);
                    for (int k = 0; k < Sub; k++) { L[at + k] += s2[k * 2]; R[at + k] += s2[k * 2 + 1]; }
                }
            var bands = new[] { 125.0, 250, 500, 1000, 2000, 4000 };
            Console.WriteLine("200 independent directions (this HRTF's diffuse field): IACC "
                            + string.Join("  ", bands.Select(f => $"{f:F0}:{Iacc(Band(L, f), Band(R, f)):F2}")));
        }
        Console.WriteLine("head in a diffuse field:  125:0.95  250:0.80  500:0.40  1000:0.20  2000:<0.15  4000:<0.15");
        return 0;
    }

    /// <summary>An octave band-pass (two RBJ biquads), from half a second in.</summary>
    private static float[] Band(float[] x, double f0)
    {
        var y = x.Skip(Fs / 2).ToArray();
        for (int pass = 0; pass < 2; pass++)
        {
            double w = 2 * Math.PI * f0 / Fs, q = Math.Sqrt(2) / 1.0, alpha = Math.Sin(w) / (2 * q);
            double b0 = alpha, b2 = -alpha, a0 = 1 + alpha, a1 = -2 * Math.Cos(w), a2 = 1 - alpha;
            double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
            for (int i = 0; i < y.Length; i++)
            {
                double xi = y[i], yi = (b0 * xi + b2 * x2 - a1 * y1 - a2 * y2) / a0;
                x2 = x1; x1 = xi; y2 = y1; y1 = yi; y[i] = (float)yi;
            }
        }
        return y;
    }

    private static double Iacc(float[] l, float[] r)
    {
        int lag = Fs / 1000;
        double el = l.Sum(v => (double)v * v), er = r.Sum(v => (double)v * v), best = 0;
        for (int d = -lag; d <= lag; d++)
        {
            double c = 0;
            for (int i = Math.Max(0, -d); i < l.Length && i + d < r.Length; i++) c += l[i] * (double)r[i + d];
            best = Math.Max(best, Math.Abs(c) / Math.Sqrt(el * er + 1e-30));
        }
        return best;
    }
}

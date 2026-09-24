using System;
using System.IO;

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// Spike: white noise driven past a standing listener through Steam Audio's binaural effect, the
/// way the game does it — one direction per mixer block — and the same pass rendered with the
/// direction updated every 64 samples, as the reference for what a smoothly moving source is.
///
/// "Anything that passes close sounds inside out; a little further away it is fine." What changes
/// with distance is how fast the direction turns: at 2 m and 15 m/s the bearing swings about 25
/// degrees in one 1024-sample block. The measures, per block round the closest approach:
///   iacc   — the peak of the normalised left/right cross-correlation within +-1 ms. A real source
///            at one place is highly coherent between the ears; "inside out" is the ears disagreeing.
///   ripple — the spread (dB) of the block's spectrum against the reference's, 1-10 kHz: a comb
///            from two delayed copies being cross-faded shows as notches the reference lacks.
///
/// --pass-by [out=DIR]   writes pass_{d}m_{block}.wav for listening.
/// </summary>
public static class PassBySpike
{
    const int Rate = 44100;

    public static int Run(string[] args)
    {
        string outDir = "/tmp/claude-1000/passby";
        foreach (var a in args) if (a.StartsWith("out=")) outDir = a[4..];
        Directory.CreateDirectory(outDir);

        const float speed = 15f;           // m/s, a car in town
        const float seconds = 4f;
        int n = (int)(seconds * Rate);
        var noise = new float[n];
        uint s = 2463534242;
        for (int i = 0; i < n; i++) { s ^= s << 13; s ^= s >> 17; s ^= s << 5; noise[i] = (int)s / (float)int.MaxValue * 0.25f; }

        Console.WriteLine("dist  block interp   iacc(close)  iacc(ref)  ripple dB(close)");
        foreach (float d in new[] { 2f, 4f, 8f, 16f })
        {
            Vector(d, speed, seconds, 0f, out var dirs);
            var reference = Render(noise, dirs, 64, Phonon.IPL_HRTFINTERPOLATION_BILINEAR);
            foreach (int block in new[] { 1024, 256 })
            foreach (int interp in new[] { Phonon.IPL_HRTFINTERPOLATION_BILINEAR, Phonon.IPL_HRTFINTERPOLATION_NEAREST })
            {
                var y = Render(noise, dirs, block, interp);
                // The second either side of the closest approach, which is at the middle.
                int a0 = n / 2 - Rate / 4, a1 = n / 2 + Rate / 4;
                double iacc = MeanIacc(y, a0, a1), iaccRef = MeanIacc(reference, a0, a1);
                double ripple = Ripple(y, reference, a0, a1);
                Console.WriteLine($"{d,4:F0}  {block,5} {(interp == 1 ? "bilin" : "near "),6}   {iacc,8:F3}    {iaccRef,8:F3}    {ripple,8:F2}");
                if (interp == Phonon.IPL_HRTFINTERPOLATION_BILINEAR)
                    Wav(Path.Combine(outDir, $"pass_{d:F0}m_{block}.wav"), y);
            }
            Wav(Path.Combine(outDir, $"pass_{d:F0}m_ref64.wav"), reference);
        }
        return 0;
    }

    /// <summary>Direction per sample (Steam Audio frame: +x right, -z forward) of a source moving
    /// left to right across the listener's front at lateral distance d, ear height difference h.</summary>
    static void Vector(float d, float v, float seconds, float h, out Phonon.IPLVector3[] dirs)
    {
        int n = (int)(seconds * Rate);
        dirs = new Phonon.IPLVector3[n];
        for (int i = 0; i < n; i++)
        {
            float x = v * (i / (float)Rate - seconds / 2f);
            float z = -d;
            float len = MathF.Sqrt(x * x + z * z + h * h);
            dirs[i] = new Phonon.IPLVector3 { x = x / len, y = h / len, z = z / len };
        }
    }

    static float[] Render(float[] mono, Phonon.IPLVector3[] dirs, int frame, int interp)
    {
        var cs = Phonon.DefaultContextSettings();
        Phonon.iplContextCreate(ref cs, out IntPtr ctx);
        var au = new Phonon.IPLAudioSettings { samplingRate = Rate, frameSize = frame };
        var hs = new Phonon.IPLHRTFSettings { type = Phonon.IPL_HRTFTYPE_DEFAULT, volume = 1f, normType = Phonon.IPL_HRTFNORMTYPE_NONE };
        Phonon.iplHRTFCreate(ctx, ref au, ref hs, out IntPtr hrtf);
        var es = new Phonon.IPLBinauralEffectSettings { hrtf = hrtf };
        Phonon.iplBinauralEffectCreate(ctx, ref au, ref es, out IntPtr eff);
        var inBuf = new Phonon.IPLAudioBuffer(); var outBuf = new Phonon.IPLAudioBuffer();
        Phonon.iplAudioBufferAllocate(ctx, 1, frame, ref inBuf);
        Phonon.iplAudioBufferAllocate(ctx, 2, frame, ref outBuf);
        var m = new float[frame]; var st = new float[frame * 2];
        var y = new float[mono.Length * 2];
        for (int f = 0; f + frame <= mono.Length; f += frame)
        {
            Array.Copy(mono, f, m, 0, frame);
            Phonon.iplAudioBufferDeinterleave(ctx, m, ref inBuf);
            // The game writes the direction on its own thread; the block sees whatever was last set.
            var prm = new Phonon.IPLBinauralEffectParams { direction = dirs[f], interpolation = interp, spatialBlend = 1f, hrtf = hrtf, peakDelays = IntPtr.Zero };
            Phonon.iplBinauralEffectApply(eff, ref prm, ref inBuf, ref outBuf);
            Phonon.iplAudioBufferInterleave(ctx, ref outBuf, st);
            Array.Copy(st, 0, y, f * 2, frame * 2);
        }
        Phonon.iplAudioBufferFree(ctx, ref inBuf); Phonon.iplAudioBufferFree(ctx, ref outBuf);
        Phonon.iplBinauralEffectRelease(ref eff); Phonon.iplHRTFRelease(ref hrtf); Phonon.iplContextRelease(ref ctx);
        return y;
    }

    static double MeanIacc(float[] y, int a0, int a1)
    {
        const int W = 1024; int maxLag = Rate / 1000;
        double sum = 0; int count = 0;
        for (int b = a0; b + W <= a1; b += W)
        {
            double el = 0, er = 0;
            for (int i = b; i < b + W; i++) { el += y[i * 2] * (double)y[i * 2]; er += y[i * 2 + 1] * (double)y[i * 2 + 1]; }
            double best = 0;
            for (int lag = -maxLag; lag <= maxLag; lag++)
            {
                double c = 0;
                for (int i = b + maxLag; i < b + W - maxLag; i++) c += y[i * 2] * (double)y[(i + lag) * 2 + 1];
                best = Math.Max(best, c / Math.Sqrt(el * er + 1e-30));
            }
            sum += best; count++;
        }
        return sum / count;
    }

    /// <summary>Std-dev (dB) over 1-10 kHz of 1/12-octave band level differences, both ears,
    /// averaged over blocks.</summary>
    static double Ripple(float[] y, float[] r, int a0, int a1)
    {
        const int W = 2048;
        double total = 0; int count = 0;
        for (int b = a0; b + W <= a1; b += W / 2)
        for (int ch = 0; ch < 2; ch++)
        {
            var sy = Spectrum(y, b, W, ch); var sr = Spectrum(r, b, W, ch);
            var diffs = new System.Collections.Generic.List<double>();
            for (double fc = 1000; fc < 10000; fc *= Math.Pow(2, 1.0 / 12))
            {
                int k0 = (int)(fc * Math.Pow(2, -1.0 / 24) * W / Rate), k1 = (int)(fc * Math.Pow(2, 1.0 / 24) * W / Rate);
                double py = 0, pr = 0;
                for (int k = k0; k <= k1; k++) { py += sy[k]; pr += sr[k]; }
                diffs.Add(10 * Math.Log10((py + 1e-20) / (pr + 1e-20)));
            }
            double mean = 0; foreach (var v in diffs) mean += v; mean /= diffs.Count;
            double var_ = 0; foreach (var v in diffs) var_ += (v - mean) * (v - mean);
            total += Math.Sqrt(var_ / diffs.Count); count++;
        }
        return total / count;
    }

    static double[] Spectrum(float[] y, int b, int W, int ch)
    {
        var p = new double[W / 2];
        for (int k = 0; k < W / 2; k++)
        {
            double re = 0, im = 0;
            for (int i = 0; i < W; i++)
            {
                double w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / W);
                double v = y[(b + i) * 2 + ch] * w;
                double ph = -2 * Math.PI * k * i / W;
                re += v * Math.Cos(ph); im += v * Math.Sin(ph);
            }
            p[k] = re * re + im * im;
        }
        return p;
    }

    static void Wav(string path, float[] st)
    {
        using var fs = new FileStream(path, FileMode.Create);
        using var w = new BinaryWriter(fs);
        int bytes = st.Length * 2;
        w.Write("RIFF".ToCharArray()); w.Write(36 + bytes); w.Write("WAVE".ToCharArray());
        w.Write("fmt ".ToCharArray()); w.Write(16); w.Write((short)1); w.Write((short)2);
        w.Write(Rate); w.Write(Rate * 4); w.Write((short)4); w.Write((short)16);
        w.Write("data".ToCharArray()); w.Write(bytes);
        foreach (var v in st) w.Write((short)(Math.Clamp(v, -1f, 1f) * short.MaxValue));
    }
}

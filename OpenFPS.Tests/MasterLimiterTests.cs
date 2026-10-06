using System;
using OpenFPS.Client.AudioEngine.Fmod;
using Xunit;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// The master limiter (TruePeakLimiter): look-ahead, true peak, linked, no flat tops. FMOD's limiter,
/// which it replaced, had no look-ahead and clipped the leading edge of every shot and thunder crack
/// flat at its ceiling (975 flat-topped runs in a 16-minute capture; docs/AUDIO_QUALITY_2026-10-06.md).
/// </summary>
public class MasterLimiterTests
{
    private readonly ITestOutputHelper _o;
    public MasterLimiterTests(ITestOutputHelper o) => _o = o;

    private const int Rate = 48000;
    private static readonly float Ceiling = MathF.Pow(10f, TruePeakLimiter.DefaultCeilingDb / 20f);

    private static float[] Run(TruePeakLimiter l, float[] stereo)
    {
        var y = new float[stereo.Length];
        for (int at = 0; at < stereo.Length / 2; at += 1024)
        {
            int len = Math.Min(1024, stereo.Length / 2 - at) * 2;
            l.Process(stereo.AsSpan(at * 2, len), y.AsSpan(at * 2, len), 2);
        }
        return y;
    }

    private static float[] Tone(double hz, float amp, double seconds, double hz2 = 0, float amp2 = 0)
    {
        int n = (int)(seconds * Rate);
        var x = new float[n * 2];
        for (int i = 0; i < n; i++)
        {
            float v = amp * (float)Math.Sin(2 * Math.PI * hz * i / Rate) + amp2 * (float)Math.Sin(2 * Math.PI * hz2 * i / Rate);
            x[2 * i] = v; x[2 * i + 1] = v;
        }
        return x;
    }

    private static float Over(float db) => Ceiling * MathF.Pow(10f, db / 20f);

    /// <summary>Everything that is not the tone, against the tone, on channel 0 of a settled stretch:
    /// a least-squares sine at <paramref name="hz"/> taken out (THD+N).</summary>
    private static double ThdN(float[] y, double hz, int from, int count)
    {
        double ss = 0, sc = 0, cc = 0, ys = 0, yc = 0;
        for (int i = from; i < from + count; i++)
        {
            double s = Math.Sin(2 * Math.PI * hz * i / Rate), c = Math.Cos(2 * Math.PI * hz * i / Rate);
            ss += s * s; cc += c * c; sc += s * c; ys += y[2 * i] * s; yc += y[2 * i] * c;
        }
        double det = ss * cc - sc * sc, a = (ys * cc - yc * sc) / det, b = (yc * ss - ys * sc) / det;
        double eTone = 0, eRest = 0;
        for (int i = from; i < from + count; i++)
        {
            double fit = a * Math.Sin(2 * Math.PI * hz * i / Rate) + b * Math.Cos(2 * Math.PI * hz * i / Rate);
            eTone += fit * fit; eRest += (y[2 * i] - fit) * (y[2 * i] - fit);
        }
        return 10 * Math.Log10(eRest / eTone + 1e-30);
    }

    /// <summary>The longest run of samples on channel 0 that are exactly equal and over half scale.</summary>
    private static int LongestFlatTop(float[] y)
    {
        int best = 0, run = 0;
        for (int i = 1; i < y.Length / 2; i++)
        {
            run = y[2 * i] == y[2 * i - 2] && MathF.Abs(y[2 * i]) > 0.5f ? run + 1 : 0;
            best = Math.Max(best, run + (run > 0 ? 1 : 0));
        }
        return best;
    }

    /// <summary>The output's peak, 8x oversampled by a long windowed sinc: a reconstruction, not the
    /// detector's own estimate, so the test does not mark its own homework.</summary>
    private static float TruePeak(float[] y, int from, int count)
    {
        const int Up = 8, Half = 32;
        float peak = 0f;
        for (int i = from; i < from + count; i++)
            for (int p = 0; p < Up; p++)
            {
                double t = i + (double)p / Up, acc = 0;
                for (int k = i - Half + 1; k <= i + Half; k++)
                {
                    if (k < 0 || k >= y.Length / 2) continue;
                    double d = t - k, w = 0.5 + 0.5 * Math.Cos(Math.PI * d / Half);
                    acc += y[2 * k] * (Math.Abs(d) < 1e-9 ? 1.0 : Math.Sin(Math.PI * d) / (Math.PI * d)) * w;
                }
                peak = MathF.Max(peak, (float)Math.Abs(acc));
            }
        return peak;
    }

    [Fact]
    public void UnderTheCeilingItIsADelayAndTheMakeup()
    {
        var l = new TruePeakLimiter(Rate, makeupDb: 6f);
        Assert.InRange(l.LatencySamples, 48, 192);           // one to four milliseconds
        var x = new float[4096 * 2];
        x[100 * 2] = 0.2f; x[100 * 2 + 1] = -0.1f;
        var y = Run(l, x);
        float g = MathF.Pow(10f, 6f / 20f);
        for (int i = 0; i < 4096; i++)
        {
            float wantL = i == 100 + l.LatencySamples ? 0.2f * g : 0f, wantR = i == 100 + l.LatencySamples ? -0.1f * g : 0f;
            Assert.Equal(wantL, y[2 * i], 5);
            Assert.Equal(wantR, y[2 * i + 1], 5);
        }
        _o.WriteLine($"latency {l.LatencySamples} samples, {l.LatencySeconds * 1000:F2} ms at {Rate} Hz");
    }

    /// <summary>The leading edge of a shot: a step from silence to 12 dB over the ceiling. Without
    /// look-ahead the first samples go through at full height and are clipped; here the gain is
    /// already down, and nothing is flat.</summary>
    [Fact]
    public void AShotsLeadingEdgeIsNotCut()
    {
        var l = new TruePeakLimiter(Rate);
        int n = Rate / 2;
        var x = new float[n * 2];
        var rng = new Random(3);
        // 5 ms of noise burst, 12 dB over, with nothing at the very top of the band: a shot as the
        // mix carries it (through the head's response and the air), not white noise to Nyquist, whose
        // peaks between samples the standard's interpolator is not made to see.
        float prev = 0f;
        for (int i = 2000; i < 2000 + 240; i++)
        {
            float w = (float)(rng.NextDouble() * 2 - 1);
            float v = Over(12f) * 0.5f * (w + prev) * MathF.Exp(-(i - 2000) / 80f);
            prev = w;
            x[2 * i] = v; x[2 * i + 1] = -v;
        }
        var y = Run(l, x);
        float peak = 0f;
        for (int i = 0; i < n * 2; i++) peak = MathF.Max(peak, MathF.Abs(y[i]));
        float tp = TruePeak(y, 0, 4000);
        _o.WriteLine($"sample peak {20 * MathF.Log10(peak):F2} dBFS, true peak {20 * MathF.Log10(tp):F2} dBTP (ceiling {TruePeakLimiter.DefaultCeilingDb:F1})");
        Assert.True(peak <= Ceiling * 1.0001f, $"sample peak {20 * MathF.Log10(peak):F2} dBFS over the ceiling");
        Assert.True(tp <= Ceiling * MathF.Pow(10f, 0.1f / 20f), $"true peak {20 * MathF.Log10(tp):F2} dBTP");
        Assert.True(LongestFlatTop(y) < 3, $"a flat top {LongestFlatTop(y)} samples long");
    }

    /// <summary>Tones held 6 and 12 dB over the ceiling: brought down to it, with the gain steady
    /// (it does not ride the waveform), so they come out clean.</summary>
    [Theory]
    [InlineData(50.0, 6f)]
    [InlineData(50.0, 12f)]
    [InlineData(1000.0, 6f)]
    [InlineData(1000.0, 12f)]
    [InlineData(5000.0, 12f)]
    public void ATonePushedOverComesOutClean(double hz, float overDb)
    {
        var l = new TruePeakLimiter(Rate);
        var y = Run(l, Tone(hz, Over(overDb), 3.0));
        int from = Rate * 2, count = Rate / 2;
        double thd = ThdN(y, hz, from, count);
        float tp = TruePeak(y, from, 4800);
        _o.WriteLine($"{hz} Hz {overDb} dB over: THD+N {thd:F1} dB, true peak {20 * MathF.Log10(tp):F2} dBTP, reduction {l.ReductionDb:F2} dB");
        Assert.True(thd < -80, $"THD+N {thd:F1} dB");
        Assert.InRange(20 * MathF.Log10(tp), TruePeakLimiter.DefaultCeilingDb - 0.5f, TruePeakLimiter.DefaultCeilingDb + 0.1f);
        Assert.Equal(0, LongestFlatTop(y) >= 3 && hz > 100 ? 1 : 0);
    }

    /// <summary>Two tones (SMPTE: 60 Hz and 7 kHz, 4 to 1) 12 dB over: no sidebands at 7 kHz +- 60.</summary>
    [Fact]
    public void TwoTonesDoNotModulateEachOther()
    {
        var l = new TruePeakLimiter(Rate);
        float total = Over(12f);
        var y = Run(l, Tone(60, total * 0.8f, 3.0, 7000, total * 0.2f));
        int from = Rate * 2, count = Rate;
        double Level(double f)
        {
            double re = 0, im = 0;
            for (int i = from; i < from + count; i++)
            {
                double w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * (i - from) / (count - 1));
                re += w * y[2 * i] * Math.Cos(2 * Math.PI * f * i / Rate); im += w * y[2 * i] * Math.Sin(2 * Math.PI * f * i / Rate);
            }
            return 20 * Math.Log10(Math.Sqrt(re * re + im * im) + 1e-30);
        }
        double carrier = Level(7000), side = Math.Max(Level(6940), Level(7060)), side2 = Math.Max(Level(6880), Level(7120));
        _o.WriteLine($"IMD sidebands: first {side - carrier:F1} dB, second {side2 - carrier:F1} dB under the 7 kHz carrier");
        Assert.True(side - carrier < -60, $"first sideband {side - carrier:F1} dB");
    }

    /// <summary>One shot: the gain comes back within a fifth of a second. A long roll held down: it is
    /// let go of more slowly, so it does not pump.</summary>
    [Fact]
    public void ReleaseFollowsTheProgramme()
    {
        float Recover(double burstSeconds)
        {
            var l = new TruePeakLimiter(Rate);
            int n = Rate * 6, start = Rate / 4, len = (int)(burstSeconds * Rate);
            var x = new float[n * 2];
            for (int i = 0; i < n; i++)
            {
                float v = 0.05f * MathF.Sin(2 * MathF.PI * 400f * i / Rate);
                if (i >= start && i < start + len) v += Over(10f) * MathF.Sin(2 * MathF.PI * 100f * i / Rate);
                x[2 * i] = v; x[2 * i + 1] = v;
            }
            var y = new float[n * 2];
            for (int i = 0; i < n; i++)
            {
                l.Process(x.AsSpan(2 * i, 2), y.AsSpan(2 * i, 2), 2);
                if (i > start + len + l.LatencySamples && l.ReductionDb < 1f) return (float)(i - start - len) / Rate;
            }
            return float.PositiveInfinity;
        }
        float shot = Recover(0.005), roll = Recover(2.0);
        _o.WriteLine($"back within 1 dB: {shot * 1000:F0} ms after a 5 ms burst, {roll * 1000:F0} ms after a 2 s one");
        Assert.True(shot < 0.2f, $"a single shot held the mix down {shot * 1000:F0} ms");
        Assert.True(roll > shot * 1.5f, "a long roll is let go of no more slowly than a shot");
        Assert.True(roll < 2.0f, $"a long roll held the mix down {roll * 1000:F0} ms");
    }

    /// <summary>Linked: a shot on the left brings the right down with it, so the image does not move.</summary>
    [Fact]
    public void BothEarsGetTheSameGain()
    {
        var l = new TruePeakLimiter(Rate);
        int n = Rate / 2;
        var x = new float[n * 2];
        for (int i = 0; i < n; i++)
        {
            x[2 * i] = i > 4000 && i < 8000 ? Over(9f) * MathF.Sin(2 * MathF.PI * 300f * i / Rate) : 0f;
            x[2 * i + 1] = 0.1f * MathF.Sin(2 * MathF.PI * 500f * i / Rate);
        }
        var y = Run(l, x);
        int at = 6000 + l.LatencySamples;
        float gR = 0f, nR = 0f;
        for (int i = at; i < at + 480; i++) { gR += MathF.Abs(y[2 * i + 1]); nR += MathF.Abs(x[2 * (i - l.LatencySamples) + 1]); }
        _o.WriteLine($"right ear's gain while the left is limited: {20 * MathF.Log10(gR / nR):F1} dB");
        Assert.True(20 * MathF.Log10(gR / nR) < -6f);
    }

    [Fact]
    public void NotANumberIsSilenceAndNothingAllocates()
    {
        var l = new TruePeakLimiter(Rate, makeupDb: 7f);
        var x = Tone(440, Over(3f), 0.2);
        x[1000] = float.NaN; x[1001] = float.PositiveInfinity;
        var y = new float[x.Length];
        Run(l, x);                                           // warm: the JIT, if anything
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int at = 0; at + 1024 <= x.Length / 2; at += 1024) l.Process(x.AsSpan(2 * at, 2048), y.AsSpan(2 * at, 2048), 2);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        foreach (float v in y) Assert.True(float.IsFinite(v));
        Assert.True(l.NonFiniteSamples >= 2);
        Assert.Equal(0, allocated);
    }

    /// <summary>The latency is the same time at any rate the mixer runs at.</summary>
    [Theory]
    [InlineData(44100)]
    [InlineData(48000)]
    [InlineData(96000)]
    public void TheLatencyIsAboutTwoMilliseconds(int rate)
    {
        var l = new TruePeakLimiter(rate);
        Assert.InRange(l.LatencySeconds, 0.0019, 0.0024);
    }
}

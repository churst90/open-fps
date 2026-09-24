using System;
using System.Linq;
using OpenFPS.Client.AudioEngine.Fmod;
using Xunit;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// An echo of a sustained sound must not phase against it.
///
/// "Sirens inside out" and "a laser beam into my ears" were an engine's reflection being the engine's
/// own waveform, a few milliseconds late. The direct sound plus that copy is a comb filter: notches at
/// EXACTLY regular spacing, which the ear hears as a pitch of its own and, as either end moves, as a
/// flanger. A real wall hands the sound back from a patch, not a point, so the copy is the same
/// sound but not the same waveform. This measures the difference: the ripple the echo puts on the
/// combined spectrum, and how periodic that ripple is.
/// </summary>
public class EchoDiffusionTests
{
    private readonly ITestOutputHelper _o;
    public EchoDiffusionTests(ITestOutputHelper o) => _o = o;

    private const float Rate = 48000f;

    /// <summary>The direct sound plus an echo of it: an impulse response.</summary>
    private static float[] DirectPlusEcho(int delay, float gain, EchoDiffuser? diffuser)
    {
        var h = new float[16384];
        h[0] = 1f;
        for (int n = 0; n + delay < h.Length; n++)
        {
            float x = n == 0 ? gain : 0f;
            h[n + delay] += diffuser != null ? diffuser.Process(x) : x;
        }
        return h;
    }

    /// <summary>|H(f)| in dB at every 5 Hz from 200 Hz to 5 kHz.</summary>
    private static float[] RippleDb(float[] h)
    {
        var freqs = Enumerable.Range(0, (5000 - 200) / 5).Select(i => 200f + 5f * i).ToArray();
        var db = new float[freqs.Length];
        for (int k = 0; k < freqs.Length; k++)
        {
            double w = 2 * Math.PI * freqs[k] / Rate, re = 0, im = 0;
            for (int n = 0; n < h.Length; n++) { re += h[n] * Math.Cos(w * n); im -= h[n] * Math.Sin(w * n); }
            db[k] = (float)(10 * Math.Log10(Math.Max(1e-12, re * re + im * im)));
        }
        return db;
    }

    /// <summary>How periodic the ripple is: its normalised autocorrelation at the comb's own spacing
    /// (1/delay), searched over a few bins either side. 1 is a perfect comb.</summary>
    private static float Periodicity(float[] db, int delay)
    {
        float mean = db.Average();
        var x = db.Select(v => v - mean).ToArray();
        double zero = x.Sum(v => (double)v * v);
        float spacingHz = Rate / delay;
        int lag0 = (int)MathF.Round(spacingHz / 5f);
        double best = 0;
        for (int lag = Math.Max(1, lag0 - 3); lag <= lag0 + 3; lag++)
        {
            double sum = 0;
            for (int i = 0; i + lag < x.Length; i++) sum += x[i] * x[i + lag];
            best = Math.Max(best, sum / zero);
        }
        return (float)best;
    }

    [Theory]
    [InlineData(0.2f)]   // concrete, generic
    [InlineData(0.5f)]   // brick, a facade with windows in it
    [InlineData(0.9f)]   // a crowd, foliage
    public void AnEchoOffARoughWallIsNotACombFilter(float scattering)
    {
        const int delay = 480;          // 10 ms: a wall about a metre and a half behind the car
        const float gain = 0.5f;        // -6 dB, a hard wall close by: the worst case for phasing
        var coherent = RippleDb(DirectPlusEcho(delay, gain, null));
        var smeared = RippleDb(DirectPlusEcho(delay, gain, new EchoDiffuser(scattering, 7, Rate)));
        float pc = Periodicity(coherent, delay), ps = Periodicity(smeared, delay);
        _o.WriteLine($"scattering {scattering}: comb periodicity coherent {pc:F2}, smeared {ps:F2}; "
                   + $"ripple sd {Sd(coherent):F1} dB vs {Sd(smeared):F1} dB");
        Assert.True(pc > 0.9f, "the reference is not a comb");
        Assert.True(ps < 0.35f, $"still a comb at scattering {scattering}: {ps:F2}");
    }

    [Fact]
    public void SmearingAnEchoDoesNotChangeHowLoudItIs()
    {
        foreach (float s in new[] { 0f, 0.5f, 1f })
        {
            var d = new EchoDiffuser(s, 3, Rate);
            double energy = 0;
            for (int n = 0; n < 48000; n++) { float y = d.Process(n == 0 ? 1f : 0f); energy += y * y; }
            _o.WriteLine($"scattering {s}: energy {10 * Math.Log10(energy):F2} dB");
            Assert.InRange(10 * Math.Log10(energy), -0.2, 0.2);
        }
    }

    [Fact]
    public void AMirrorSmearsLittleAndBrickSmearsMore()
    {
        float Spread(float s)
        {
            var d = new EchoDiffuser(s, 5, Rate);
            double total = 0, weighted = 0;
            for (int n = 0; n < 48000; n++)
            {
                float y = d.Process(n == 0 ? 1f : 0f);
                total += y * y; weighted += y * y * n;
            }
            return (float)(weighted / total / Rate * 1000);   // energy centroid, ms
        }
        float glass = Spread(0.05f), brick = Spread(0.6f);
        _o.WriteLine($"energy centroid: polished {glass:F1} ms, brick {brick:F1} ms");
        Assert.True(glass < 3f);
        Assert.True(brick > 3f * glass);
    }

    private static float Sd(float[] x) { float m = x.Average(); return MathF.Sqrt(x.Average(v => (v - m) * (v - m))); }
}

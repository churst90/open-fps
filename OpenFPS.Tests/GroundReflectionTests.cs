using OpenFPS.Client.AudioEngine.Acoustics;

namespace OpenFPS.Tests;

/// <summary>
/// The road answering a source that stands on it: the direct sound plus itself a fraction of a
/// millisecond later, off the ground. A lift where the two are in phase and a notch where they are not.
/// </summary>
public class GroundReflectionTests
{
    private const float Sr = 44100f;

    /// <summary>Steady-state gain of a sine through the processor, dB.</summary>
    private static double GainDb(GroundReflection g, float hz)
    {
        double inE = 0, outE = 0;
        for (int i = 0; i < (int)Sr; i++)
        {
            float x = MathF.Sin(2f * MathF.PI * hz * i / Sr);
            float y = g.Process(x);
            if (i < Sr / 2) continue;                 // past the glide
            inE += x * (double)x; outE += y * (double)y;
        }
        return 10 * Math.Log10(outE / inE);
    }

    private static GroundReflection Hard(float delaySeconds)
    {
        var g = new GroundReflection(Sr);
        g.Set(delaySeconds, 1f, 1f);
        return g;
    }

    /// <summary>A hard road, in phase: the pressure doubles, six decibels.</summary>
    [Fact]
    public void InPhaseTheGroundDoublesThePressure()
    {
        Assert.InRange(GainDb(Hard(0.0003f), 60f), 5.5, 6.1);
    }

    /// <summary>...and at c/(2 delta) it cancels: 0.3 ms puts the notch at 1667 Hz.</summary>
    [Fact]
    public void HalfAWavelengthLateItCancels()
    {
        float delay = 0.0003f, notch = 1f / (2f * delay);
        Assert.True(GainDb(Hard(delay), notch) < -15.0);
        Assert.InRange(GainDb(Hard(delay), 2f * notch), 5.0, 6.1);
    }

    /// <summary>Grass takes the top off: a surface that reflects nothing above a kilohertz leaves the
    /// high end as it was and still lifts the bass.</summary>
    [Fact]
    public void ASoftSurfaceLeavesTheTopAlone()
    {
        var g = new GroundReflection(Sr);
        g.Set(0.00001f, 1f, 0f);
        Assert.InRange(GainDb(g, 60f), 5.0, 6.1);
        var h = new GroundReflection(Sr);
        h.Set(0.00001f, 1f, 0f);
        Assert.InRange(GainDb(h, 8000f), -0.5, 1.0);
    }

    /// <summary>No ground: exactly the input.</summary>
    [Fact]
    public void NoGroundIsTransparent()
    {
        var g = new GroundReflection(Sr);
        for (int i = 0; i < 1000; i++)
        {
            float x = MathF.Sin(i * 0.37f);
            Assert.Equal(x, g.Process(x));
        }
    }

    /// <summary>A voice that is all tyre — two centimetres off the road — gets the lift and no notch;
    /// half tyre, and the notch is a few decibels rather than twenty. A car is not one point.</summary>
    [Fact]
    public void TyresOnTheRoadFillTheNotch()
    {
        float delay = 0.0003f, notch = 1f / (2f * delay);
        var tyres = Hard(delay); tyres.SetNear(1f);
        Assert.InRange(GainDb(tyres, notch), 5.0, 6.1);
        var half = Hard(delay); half.SetNear(0.5f);
        Assert.InRange(GainDb(half, notch), -1.0, 4.0);
    }

    /// <summary>Turbulence decorrelates the two paths more at high frequency and long range.</summary>
    [Fact]
    public void TheAirDecorrelatesTheTopAtRange()
    {
        float nearLow = OpenFPS.Client.Core.ClientAudioSystem.Coherence(250f, 10f, 0.5f);
        float farHigh = OpenFPS.Client.Core.ClientAudioSystem.Coherence(2500f, 100f, 0.5f);
        Assert.True(nearLow > 0.99f);
        Assert.True(farHigh < nearLow && farHigh > 0.5f);
    }

    /// <summary>Twenty minutes in, a half-sample delay still interpolates: a float read position has no
    /// fraction past 2^24 samples (6.3 min), heard as "high bit crushy frequencies ... after a while".</summary>
    [Fact]
    public void AfterTwentyMinutesTheDelayStillHasAFraction()
    {
        var g = Hard(0.5f / Sr);              // half a sample
        int n = (int)(Sr * 60 * 20);
        for (int i = 0; i < n; i++) g.Process(0f);
        // A ramp: x[k] = k. Half a sample late, the copy is k - 0.5, so the output is 2k - 0.5.
        float last = 0f;
        for (int k = 0; k < 64; k++) last = g.Process(k);
        Assert.InRange(last, 2 * 63 - 0.5f - 0.05f, 2 * 63 - 0.5f + 0.05f);
    }

    // ── A texture's ground: its power, not a copy ────────────────────────────────────────────────

    private static GroundReflection Texture(float delaySeconds, float gain)
    {
        var g = new GroundReflection(Sr) { Texture = true };
        g.Set(delaySeconds, gain, gain);
        return g;
    }

    /// <summary>A texture's ground hands back power, not a copy: no notches, no echo at the delay (Cody,
    /// 2026-10-06: a gutter outlet 2.8 m up combed every 125 Hz, "a flanging/very fast repeating sound").</summary>
    [Fact]
    public void ATextureHasNoComb()
    {
        const float delay = 0.008f;
        float notch = 1f / (2f * delay);
        // Through a copy: the notches at the odd multiples of 62.5 Hz cut deep.
        Assert.True(GainDb(Hard(delay), 7f * notch) < -10.0);
        // Through a texture's ground: within a decibel of each other at a notch and a peak.
        double atNotch = GainDb(Texture(delay, 0.45f), 7f * notch), atPeak = GainDb(Texture(delay, 0.45f), 8f * notch);
        Assert.InRange(atNotch - atPeak, -1.0, 1.0);

        var g = Texture(delay, 0.45f);
        var rng = new Random(5);
        int n = (int)Sr * 4, lag = (int)MathF.Round(delay * Sr);
        var y = new float[n];
        for (int i = 0; i < n; i++) y[i] = g.Process((float)(rng.NextDouble() * 2 - 1));
        double r0 = 0, r1 = 0;
        for (int i = (int)Sr; i < n - lag; i++) { r0 += y[i] * (double)y[i]; r1 += y[i] * (double)y[i + lag]; }
        Assert.InRange(r1 / r0, -0.03, 0.03);
    }

    /// <summary>...and it is still the ground: above the corner c / 4Δ it adds the reflection's power,
    /// 1 + g², and below it the bass lifts by the full pressure, (1 + g)², as for any source.</summary>
    [Fact]
    public void ATexturesGroundKeepsItsPowerAndItsBass()
    {
        const float delay = 0.0007f, gain = 0.9f;       // a downpipe's shoe 0.15 m up, 1.5 m away
        double high = GainDb(Texture(delay, gain), 6000f), low = GainDb(Texture(delay, gain), 40f);
        Assert.InRange(high, 10 * Math.Log10(1 + gain * gain) - 0.5, 10 * Math.Log10(1 + gain * gain) + 0.5);
        Assert.InRange(low, 20 * Math.Log10(1 + gain) - 0.7, 20 * Math.Log10(1 + gain) + 0.3);
    }
}

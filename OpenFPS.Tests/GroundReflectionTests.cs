using System;
using OpenFPS.Client.AudioEngine.Acoustics;
using Xunit;

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
}

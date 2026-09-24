using System;
using System.Linq;
using OpenFPS.Client.Core;
using Xunit;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// The echo of a one-off sound is a wash, not a copy. Every echo of a gunshot, a clap or a door used
/// to be the sound itself placed at the mirror point — a second, clean gunshot off a brick wall.
/// Asked for: "reflections shouldn't be a mirror image of the sound, it should just be a smeared wash
/// of sound." A rough surface hands the sound back through the same all-pass smear the engine echoes
/// use, so the energy is kept and the shape is not.
/// </summary>
public class DiffusedEchoTests
{
    private readonly ITestOutputHelper _o;
    public DiffusedEchoTests(ITestOutputHelper o) => _o = o;

    private static float[] Click()
    {
        // A gunshot-like transient: a fast pulse over a couple of milliseconds.
        var x = new float[2000];
        for (int i = 0; i < 100; i++) x[i] = (1f - i / 100f) * MathF.Exp(-i / 20f);
        return x;
    }

    private static double Energy(float[] x) => x.Sum(v => (double)v * v);
    private static float Peak(float[] x) => x.Max(MathF.Abs);

    /// <summary>How long it takes to deliver 90% of its energy, samples.</summary>
    private static int Spread(float[] x)
    {
        double total = Energy(x), acc = 0;
        for (int i = 0; i < x.Length; i++) { acc += x[i] * (double)x[i]; if (acc >= 0.9 * total) return i; }
        return x.Length;
    }

    [Fact]
    public void AWashKeepsTheEnergyAndLosesThePeak()
    {
        var click = Click();
        var brick = WorldAudioPlayer.Diffuse(click, 0.5f, 3);
        double energyDb = 10 * Math.Log10(Energy(brick) / Energy(click));
        double peakDb = 20 * Math.Log10(Peak(brick) / Peak(click));
        _o.WriteLine($"brick: energy {energyDb:+0.0;-0.0} dB, peak {peakDb:+0.0;-0.0} dB");
        Assert.InRange(energyDb, -1.0, 1.0);        // an all-pass moves energy in time, it does not add or remove it
        Assert.True(peakDb < -6, $"the peak only fell {peakDb:F1} dB: still a copy, not a wash");
    }

    [Fact]
    public void ARougherSurfaceSmearsItLonger()
    {
        var click = Click();
        int glass = Spread(WorldAudioPlayer.Diffuse(click, 0f, 3));
        int brick = Spread(WorldAudioPlayer.Diffuse(click, 0.5f, 3));
        int rough = Spread(WorldAudioPlayer.Diffuse(click, 1f, 3));
        _o.WriteLine($"90% of the energy by sample: glass {glass}, brick {brick}, rough {rough}");
        Assert.True(glass < brick && brick < rough);
        // A mirror stays nearly a mirror: glass delivers it within about 3 ms at 44.1 kHz.
        Assert.True(glass < 150, $"glass smeared the click over {glass} samples");
    }

    [Fact]
    public void TheWashHasRoomToRingOut()
    {
        var click = Click();
        var rough = WorldAudioPlayer.Diffuse(click, 1f, 3);
        Assert.True(rough.Length > click.Length);
        // The last few milliseconds are near silence, so the buffer does not end on a step.
        float tailPeak = rough.Skip(rough.Length - 100).Max(MathF.Abs);
        Assert.True(tailPeak < 0.05f * Peak(rough), $"ends at {tailPeak / Peak(rough):P0} of its peak");
    }
}

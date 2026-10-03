using System;
using OpenFPS.Common;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>The physical knob door (<see cref="KnobDoor"/>) as the game uses it.</summary>
public class KnobDoorTests
{
    [Fact]
    public void AKeyNamesItsDoor()
    {
        string key = KnobDoor.Key(closing: true, KnobDoor.Construction.HollowCore, variant: 7, swingSeconds: 0.9f,
                                  fromOpenness: 0.55f, width: 1.1f, height: 2.1f);
        Assert.True(KnobDoor.TryParseKey(key, out bool closing, out var door, out float swing, out float from));
        Assert.True(closing);
        Assert.Equal(KnobDoor.Construction.HollowCore, door.Leaf);
        Assert.Equal(1.1f, door.Width, 2);
        Assert.Equal(2.1f, door.Height, 2);
        Assert.Equal(0.9f, swing, 2);
        Assert.Equal(0.6f, from, 2);   // tenths
        Assert.Equal(KnobDoor.WearOf(7 % KnobDoor.Variants), door.HingeWear);
        Assert.False(KnobDoor.TryParseKey("knobdoor:open:hollow", out _, out _, out _, out _));
        Assert.False(KnobDoor.TryParseKey("car:door:open", out _, out _, out _, out _));
    }

    [Fact]
    public void MostDoorsDoNotSqueak()
    {
        // A creak on every door made a building sound haunted. A dry pin is a fault, and three
        // characters in four have oiled or barely worn hinges.
        int squeaky = 0;
        for (int v = 0; v < KnobDoor.Variants; v++)
            if (Array.Exists(KnobDoor.WearOf(v), w => w > 0.4f)) squeaky++;
        Assert.Equal(1, squeaky);
    }

    [Fact]
    public void TheDeclaredLevelsAreWhatTheModelRenders()
    {
        // The model's own pressure at a metre, A-weighted and fast, against what the server declares
        // plus the calibration it takes off.
        var door = new KnobDoor.Door { HingeWear = KnobDoor.WearOf(0), Seed = 1 };
        double open = LafMax(KnobDoor.RenderOpen(door, 48000, 0.9));
        double close = LafMax(KnobDoor.RenderGameClose(door, 48000, 0.9, 1.0));
        Assert.InRange(open, KnobDoor.OpenLevelDb + KnobDoor.LevelCalibrationDb - 2.5, KnobDoor.OpenLevelDb + KnobDoor.LevelCalibrationDb + 2.5);
        Assert.InRange(close, KnobDoor.CloseLevelDb + KnobDoor.LevelCalibrationDb - 2.5, KnobDoor.CloseLevelDb + KnobDoor.LevelCalibrationDb + 2.5);
    }

    [Fact]
    public void ClosingMeetsTheFrameWhenTheSwingEnds()
    {
        // Sent as the leaf starts back, so its loudest moment must be where the server's leaf arrives.
        var pcm = KnobDoor.RenderGameClose(new KnobDoor.Door { HingeWear = KnobDoor.WearOf(0) }, 48000, 0.9, 1.0);
        int at = 0;
        for (int i = 1; i < pcm.Length; i++) if (MathF.Abs(pcm[i]) > MathF.Abs(pcm[at])) at = i;
        Assert.InRange(at / 48000.0, 0.85, 1.05);
    }

    /// <summary>LAFmax, dB re 20 uPa, of samples in units of <see cref="KnobDoor.PascalsAtFullScale"/>.</summary>
    private static double LafMax(float[] x)
    {
        // A-weighting at 48 kHz (bilinear, the usual coefficients), then a 125 ms exponential meter.
        double[] b = { 0.234301792299513, -0.468603584599026, -0.234301792299513, 0.937207168598053, -0.234301792299513, -0.468603584599026, 0.234301792299513 };
        double[] a = { 1.0, -4.113043408775871, 6.553121752655047, -4.990849294163381, 1.785737302937573, -0.246190595319487, 0.011224250033231 };
        var xs = new double[7]; var ys = new double[7];
        double k = Math.Exp(-1 / (0.125 * 48000)), e = 0, max = 0;
        foreach (float s in x)
        {
            Array.Copy(xs, 0, xs, 1, 6); xs[0] = s * KnobDoor.PascalsAtFullScale;
            double y = 0;
            for (int i = 0; i < 7; i++) y += b[i] * xs[i];
            for (int i = 1; i < 7; i++) y -= a[i] * ys[i - 1];
            Array.Copy(ys, 0, ys, 1, 6); ys[0] = y;
            e = k * e + (1 - k) * y * y;
            max = Math.Max(max, e);
        }
        return 10 * Math.Log10(max / 4e-10);
    }
}

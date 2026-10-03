using System;
using System.Linq;
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
                                  how: KnobDoor.Shut.Gentle, width: 1.1f, height: 2.1f);
        Assert.True(KnobDoor.TryParseKey(key, out bool closing, out var door, out float swing, out var how));
        Assert.True(closing);
        Assert.Equal(KnobDoor.Construction.HollowCore, door.Leaf);
        Assert.Equal(1.1f, door.Width, 2);
        Assert.Equal(2.1f, door.Height, 2);
        Assert.Equal(0.9f, swing, 2);
        Assert.Equal(KnobDoor.Shut.Gentle, how);
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
        double close = LafMax(KnobDoor.RenderGameClose(door, 48000, KnobDoor.Shut.Normal));
        double cal = KnobDoor.LevelCalibrationDb;
        Assert.InRange(open, KnobDoor.OpenLevelDb + cal - 2.5, KnobDoor.OpenLevelDb + cal + 2.5);
        Assert.InRange(close, KnobDoor.CloseLevelDb(KnobDoor.Shut.Normal) + cal - 2.5, KnobDoor.CloseLevelDb(KnobDoor.Shut.Normal) + cal + 2.5);
        // Where the measured doors are (research note 2026-10-03): a normal latching close 70-82 dBA at a
        // metre, and opening heard down a corridor.
        Assert.InRange(KnobDoor.CloseLevelDb(KnobDoor.Shut.Normal), 70, 82.5);
        Assert.True(KnobDoor.OpenLevelDb >= KnobDoor.CloseLevelDb(KnobDoor.Shut.Normal) - 15);
    }

    [Fact]
    public void ALatchIsHeardBeforeTheDoorMeetsItsStop()
    {
        // Cody: "you can hear the audible separation between the latch and door". At a hand's closing speed
        // the bevel meets the strike's lip about 10 mm out, so tens of milliseconds before the stop.
        var report = new KnobDoor.Report();
        KnobDoor.RenderGameClose(new KnobDoor.Door { HingeWear = KnobDoor.WearOf(0) }, 48000, KnobDoor.Shut.Normal, report);
        double Start(string what) => report.Events
            .Where(e => e.Contains("  " + what + ":"))
            .Select(e => double.Parse(e.Trim().Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture))
            .DefaultIfEmpty(double.NaN).Min();
        double bevel = Start("bevel"), stop = Start("stop");
        Assert.False(double.IsNaN(bevel)); Assert.False(double.IsNaN(stop));
        Assert.InRange(stop - bevel, 30, 200);
    }

    [Fact]
    public void APushBarKeyNamesItsDoorAndItsLevelsAreItsOwn()
    {
        string key = PushBarDoor.Key(closing: false, variant: 5, swingSeconds: 1.4f, width: 1.0f, height: 2.1f);
        Assert.True(PushBarDoor.TryParseKey(key, out bool closing, out var door, out float swing));
        Assert.False(closing);
        Assert.Equal(1, door.Variant);
        Assert.Equal(1.4f, swing, 2);
        double open = LafMax(PushBarDoor.RenderOpen(new PushBarDoor.Door { Variant = 1, Seed = 2 }, 48000, 1.4), PushBarDoor.PascalsAtFullScale);
        Assert.InRange(open, PushBarDoor.OpenLevelDb(1) + KnobDoor.LevelCalibrationDb - 2.5, PushBarDoor.OpenLevelDb(1) + KnobDoor.LevelCalibrationDb + 2.5);
    }

    /// <summary>LAFmax, dB re 20 uPa, of samples in units of <see cref="KnobDoor.PascalsAtFullScale"/>.</summary>
    private static double LafMax(float[] x, double fullScale = KnobDoor.PascalsAtFullScale)
    {
        // A-weighting at 48 kHz (bilinear, the usual coefficients), then a 125 ms exponential meter.
        double[] b = { 0.234301792299513, -0.468603584599026, -0.234301792299513, 0.937207168598053, -0.234301792299513, -0.468603584599026, 0.234301792299513 };
        double[] a = { 1.0, -4.113043408775871, 6.553121752655047, -4.990849294163381, 1.785737302937573, -0.246190595319487, 0.011224250033231 };
        var xs = new double[7]; var ys = new double[7];
        double k = Math.Exp(-1 / (0.125 * 48000)), e = 0, max = 0;
        foreach (float s in x)
        {
            Array.Copy(xs, 0, xs, 1, 6); xs[0] = s * fullScale;
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

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
        // A world sound's level is its buffer's full scale at a metre, so a door declares its render's peak
        // and is heard at the model's own LAFmax. No calibration: Cody, 2026-10-03, "way way way too quiet
        // ... if I'm 5 feet away from a door at these levels I'd barely know someone opened a door", and
        // nothing to take level off the doors. These pin the model's own levels (AudioLab --heard-levels
        // survey, 2026-10-04), not the published figures, which a normal close here is about ten over.
        string key = KnobDoor.Key(true, KnobDoor.Construction.HollowCore, 0, 0.9f, KnobDoor.Shut.Normal, 1.4f, 2.1f);
        var buf = KnobDoor.RenderKey(key, 48000, out float own);
        // Its own peak, within a few decibels of the figure the server sends for every knob door's normal close.
        Assert.InRange(own, KnobDoor.CloseLevelDb(KnobDoor.Shut.Normal) - 3, KnobDoor.CloseLevelDb(KnobDoor.Shut.Normal) + 3);
        // Placed at that, it is heard at the model's LAFmax: the buffer's LAFmax under full scale, plus full scale.
        double heard = LafMax(buf, 2e-5 * Math.Pow(10, own / 20.0));
        Assert.InRange(heard, KnobDoor.CloseLafDb(KnobDoor.Shut.Normal) - 2.5, KnobDoor.CloseLafDb(KnobDoor.Shut.Normal) + 2.5);
        Assert.Equal(1.0, buf.Max(v => Math.Abs((double)v)), 3);
        // How the events stand against each other is the physics': an opening carries down a corridor.
        Assert.True(KnobDoor.OpenLafDb >= KnobDoor.CloseLafDb(KnobDoor.Shut.Normal) - 15);
        Assert.True(KnobDoor.CloseLafDb(KnobDoor.Shut.Gentle) < KnobDoor.CloseLafDb(KnobDoor.Shut.Normal));
        Assert.True(KnobDoor.CloseLafDb(KnobDoor.Shut.Normal) < KnobDoor.CloseLafDb(KnobDoor.Shut.Hard));
    }

    [Fact]
    public void ADoorIsHeardAsLoudAsItsModelSays()
    {
        // The fault this replaced: each door render was brought to a peak of one and declared at its LAFmax,
        // so a crack 20-29 dB over its own LAFmax played that far under it (a normal close heard at 51 dBA at
        // 1.5 m on "real", where the model puts 86). Placed at its own peak, a gentle close comes out at
        // 1.5 m, on "real", within a decibel of the model's LAFmax less the distance.
        float saved = Loudness.DynamicRangeCompression;
        try
        {
            Loudness.DynamicRangeCompression = 1f;
            string key = KnobDoor.Key(true, KnobDoor.Construction.HollowCore, 2, 0.9f, KnobDoor.Shut.Gentle, 1.4f, 2.1f);
            var buf = KnobDoor.RenderKey(key, 48000, out float own);
            double model = LafMax(buf, 2e-5 * Math.Pow(10, own / 20.0));
            var (gain, reference) = Loudness.Place(own);
            double atEar = 20 * Math.Log10(Loudness.RenderedGain(gain, reference, Loudness.AudibleRange(own), 1.5f))
                         + LafMax(buf, 2e-5) + Loudness.RenderCeilingDb;
            // Unless its peak runs into the ceiling ("real" puts full scale at about 89 dB at the ear): then
            // the whole sound comes down with it, and that is the setting, not the door.
            double peakAtEar = own - 20 * Math.Log10(1.5);
            if (peakAtEar <= Loudness.RenderCeilingDb)
                Assert.InRange(atEar, model - 20 * Math.Log10(1.5) - 1, model - 20 * Math.Log10(1.5) + 1);
            else
                Assert.InRange(atEar, Loudness.RenderCeilingDb - (own - model) - 1, Loudness.RenderCeilingDb - (own - model) + 1);
        }
        finally { Loudness.DynamicRangeCompression = saved; }
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
        // Its declared level is its render's peak and it is heard at the model's own LAFmax; no calibration.
        var buf = PushBarDoor.RenderKey(PushBarDoor.Key(false, 1, 1.4f, 1.0f, 2.1f), 48000, out float own);
        Assert.InRange(own, PushBarDoor.OpenLevelDb(1) - 2, PushBarDoor.OpenLevelDb(1) + 2);
        double open = LafMax(buf, 2e-5 * Math.Pow(10, own / 20.0));
        Assert.InRange(open, PushBarDoor.OpenLafDb(1) - 2.5, PushBarDoor.OpenLafDb(1) + 2.5);
    }

    [Fact]
    public void ASlidingDoorKeyNamesItsDoorAndItsLevelsAreItsOwn()
    {
        string key = SlidingDoor.Key(SlidingDoor.Kind.Automatic, closing: true, variant: 5, travelSeconds: 4.9f, width: 1.15f, height: 2.1f);
        Assert.True(SlidingDoor.TryParseKey(key, out bool closing, out var door, out float travel));
        Assert.True(closing);
        Assert.Equal(SlidingDoor.Kind.Automatic, door.Kind);
        Assert.Equal(1, door.Variant);
        Assert.Equal(4.9f, travel, 2);
        Assert.Equal(1.15f, door.Width, 2);
        Assert.False(SlidingDoor.TryParseKey("slidingdoor:garage:open:1:140:90:210", out _, out _, out _));
        // The declared levels are the render's peak, and what is heard is the model's LAFmax, at the lab's sizes.
        var patioBuf = SlidingDoor.RenderKey(SlidingDoor.Key(SlidingDoor.Kind.Patio, true, 1, 1.4f, 1.0f, 2.1f), 48000, out float patioOwn);
        Assert.InRange(patioOwn, SlidingDoor.CloseLevelDb(SlidingDoor.Kind.Patio, 1) - 2, SlidingDoor.CloseLevelDb(SlidingDoor.Kind.Patio, 1) + 2);
        double patio = LafMax(patioBuf, 2e-5 * Math.Pow(10, patioOwn / 20.0));
        Assert.InRange(patio, SlidingDoor.CloseLafDb(SlidingDoor.Kind.Patio, 1) - 2.5, SlidingDoor.CloseLafDb(SlidingDoor.Kind.Patio, 1) + 2.5);
        var autoBuf = SlidingDoor.RenderKey(SlidingDoor.Key(SlidingDoor.Kind.Automatic, false, 1, SlidingDoor.AutomaticSeconds(1.0f, true), 1.0f, 2.1f),
                                            48000, out float autoOwn);
        Assert.InRange(autoOwn, SlidingDoor.OpenLevelDb(SlidingDoor.Kind.Automatic, 1) - 2, SlidingDoor.OpenLevelDb(SlidingDoor.Kind.Automatic, 1) + 2);
        double auto = LafMax(autoBuf, 2e-5 * Math.Pow(10, autoOwn / 20.0));
        Assert.InRange(auto, SlidingDoor.OpenLafDb(SlidingDoor.Kind.Automatic, 1) - 2.5, SlidingDoor.OpenLafDb(SlidingDoor.Kind.Automatic, 1) + 2.5);
    }

    [Fact]
    public void AnAutomaticDoorRunsAtItsControllersSpeeds()
    {
        // 0.7 m/s open and 0.3 m/s shut with a check zone into each end: a metre-wide leaf takes about 3 s
        // to open and 5 s to shut, and a wider one longer.
        Assert.InRange(SlidingDoor.AutomaticSeconds(1.0f, opening: true), 2.2, 3.6);
        Assert.InRange(SlidingDoor.AutomaticSeconds(1.0f, opening: false), 4.0, 5.6);
        Assert.True(SlidingDoor.AutomaticSeconds(1.15f, opening: false) > SlidingDoor.AutomaticSeconds(1.0f, opening: false));
    }

    /// <summary>LAFmax, dB re 20 uPa, of samples whose full scale is <paramref name="fullScale"/> pascals.</summary>
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

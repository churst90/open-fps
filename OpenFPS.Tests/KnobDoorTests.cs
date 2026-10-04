using System;
using System.Collections.Generic;
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
        // nothing to take level off the doors. These pin the model's own levels (AudioLab --knob-renders,
        // 2026-10-04, round 4), not published figures.
        string key = KnobDoor.Key(true, KnobDoor.Construction.HollowCore, 0, 0.9f, KnobDoor.Shut.Normal, 1.4f, 2.1f);
        var buf = KnobDoor.RenderKey(key, 48000, out float own);
        // Its own peak, within a few decibels of the figure the server sends for every knob door's normal close.
        Assert.InRange(own, KnobDoor.CloseLevelDb(KnobDoor.Shut.Normal) - 3, KnobDoor.CloseLevelDb(KnobDoor.Shut.Normal) + 3);
        // Placed at that, it is heard at the model's LAFmax: the buffer's LAFmax under full scale, plus full scale.
        double heard = LafMax(buf, 2e-5 * Math.Pow(10, own / 20.0));
        Assert.InRange(heard, KnobDoor.CloseLafDb(KnobDoor.Shut.Normal) - 2.5, KnobDoor.CloseLafDb(KnobDoor.Shut.Normal) + 2.5);
        Assert.Equal(1.0, buf.Max(v => Math.Abs((double)v)), 3);
        // How the events stand against each other is the physics': an opening carries down a corridor. Kyles'
        // light wood door opens 9-16 dB under its closes; the model's, whose turn is quieter, 13.5-21.5.
        Assert.True(KnobDoor.OpenLafDb >= KnobDoor.CloseLafDb(KnobDoor.Shut.Normal) - 18);
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

    // ---------------------------------------------------------------------------------------------
    // Round 4 (2026-10-04): the knob door measured against Kyles' light wood door and Sudd's lever, in time
    // first and then by band (inbox/knob-door-round4-2026-10-04/README.txt). The recordings are yardsticks.

    private static readonly Lazy<(float[] Pcm, KnobDoor.Report Rep)> NormalClose = new(() =>
    {
        var rep = new KnobDoor.Report();
        var pcm = KnobDoor.RenderGameClose(new KnobDoor.Door { Width = 1.1f, HingeWear = KnobDoor.WearOf(0), Seed = 1 }, 48000, KnobDoor.Shut.Normal, rep);
        return (pcm, rep);
    });
    private static readonly Lazy<(float[] Pcm, KnobDoor.Report Rep)> Opening = new(() =>
    {
        var rep = new KnobDoor.Report();
        var pcm = KnobDoor.RenderOpen(new KnobDoor.Door { Width = 1.1f, HingeWear = KnobDoor.WearOf(0), Seed = 1 }, 48000, 0.9, rep);
        return (pcm, rep);
    });

    [Fact]
    public void AWideLeafOpensLikeAnyOther()
    {
        // The fault (2026-10-04): the 1.4 m leaf of the third character rested on a bowed head stop with its bolt
        // half a millimetre into the keeper, and the first step of its opening threw it out with an 8.8 kN blow:
        // 104 dBA, 127 dB peak, about 25 dB over every other opening, and the opening the game sends most. The
        // head stop now runs the leaf's width and the strike is fitted to where the leaf rests.
        var rep = new KnobDoor.Report();
        var pcm = KnobDoor.RenderKey(KnobDoor.Key(false, KnobDoor.Construction.HollowCore, 2, 0.9f, KnobDoor.Shut.Normal, 1.4f, 2.1f), 48000, out float peak);
        Assert.InRange(peak, KnobDoor.OpenLevelDb - 6, KnobDoor.OpenLevelDb + 4);
        Assert.InRange(LafHeard(pcm, 2e-5 * Math.Pow(10, peak / 20.0)), KnobDoor.OpenLafDb - 6, KnobDoor.OpenLafDb + 10);
        KnobDoor.RenderOpen(new KnobDoor.Door { Width = 1.4f, HingeWear = KnobDoor.WearOf(2), Seed = 3 }, 48000, 0.9, rep);
        Assert.DoesNotContain(Contacts(rep, "keeper"), c => c.Ms < 5 && c.Newtons > 50);
    }

    [Fact]
    public void AnOpeningIsAClickNotAThump()
    {
        // Kyles' openings are their clicks: under 300 Hz holds 9 dB under the whole (7-13), and the loudest
        // thing is the release, whose first 30 ms has 1-4 kHz at -3.7 and 4-16 kHz at -4.4 dB re itself. The
        // model's opening was a thump: the hand's pull threw the bolt's face onto the keeper (55 N, metal on
        // metal) and rang the leaf, 82 dBA, all of it under 300 Hz (-0.5 dB re the whole).
        var (pcm, rep) = Opening.Value;
        var x = HighPass20(pcm);
        Assert.True(BandShare(x, 0, 300) < -5, $"under 300 Hz {BandShare(x, 0, 300):F1} dB of the whole");
        double letGo = rep.Events.Where(e => e.Contains("knob let go"))
            .Select(e => double.Parse(e.Trim().Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture)).Single();
        int loudest = LoudestFrame(Band(x, 2000, 20000), 96);
        Assert.True(loudest * 2.0 > letGo, $"the loudest click at {loudest * 2} ms, the knob let go at {letGo} ms");
        var bal = FirstThirtyMs(x, loudest * 96);
        Assert.InRange(bal[2], -3.7 - 5, -3.7 + 5);
        Assert.InRange(bal[3], -4.4 - 5, -4.4 + 5);
        Assert.True(bal[0] < -10, $"under 300 Hz in the release {bal[0]:F1}");
    }

    [Fact]
    public void AnOpeningIsQuieterThanANormalClose()
    {
        // Kyles' light wood door: its openings LAFmax 14 dB under its closes (9-16). The model's were 9-11 under
        // and, played at their peaks against the 89 dB ceiling on "real", heard as loud as a close; its opening
        // was a thump of the leaf, its close a leaf let go to bounce.
        double open = LafHeard(Opening.Value.Pcm, KnobDoor.PascalsAtFullScale);
        double close = LafHeard(NormalClose.Value.Pcm, KnobDoor.PascalsAtFullScale);
        Assert.InRange(close - open, 8, 18);
    }

    [Fact]
    public void TheLatchSnapsBeforeTheDoorMeetsItsStop()
    {
        // Cody: "the door and the latch are too close together". The bolt snaps out as its face passes the keeper,
        // so the gap is the keeper's play over the edge's speed: 10 ms at 1.5 mm, 20-40 ms at 3 mm (the
        // recordings' blows 6-38 ms after the click before them).
        var rep = NormalClose.Value.Rep;
        double snap = Contacts(rep, "bolt-stop").Where(c => c.Newtons > 500).Min(c => c.Ms);
        double stop = Contacts(rep, "stop").Concat(Contacts(rep, "stop-head")).Min(c => c.Ms);
        Assert.InRange(stop - snap, 15, 50);
    }

    [Fact]
    public void AGuidedCloseIsOneBlow()
    {
        // Let go as the bolt dropped, the leaf bounced off its stop onto the keeper and back three times in 200 ms,
        // each within 2-9 dB of the shut; the recordings have one blow and nothing within 33 dB of it after. The
        // hand leans on the leaf until it has been home a moment: the leaf never reaches the keeper again, and any
        // later landing comes in at under a third of the first one's speed.
        var rep = NormalClose.Value.Rep;
        double firstStop = Contacts(rep, "stop").Concat(Contacts(rep, "stop-head")).Min(c => c.Ms);
        Assert.DoesNotContain(Contacts(rep, "keeper"), c => c.Ms > firstStop && c.Newtons > 5);
        var landings = rep.Events.Where(e => e.Contains("leaf off the stop: edge in"))
            .Select(e => double.Parse(e.Split("edge in ")[1].Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture))
            .Where(v => v > 0).ToList();
        Assert.NotEmpty(landings);
        Assert.All(landings.Skip(1), v => Assert.True(v < 0.35 * landings[0], $"a landing at {v:F3} m/s after {landings[0]:F3}"));
    }

    [Fact]
    public void AGameSlamComesInThrown()
    {
        // The game's close starts 12 degrees out, 0.23 m on a 1.1 m leaf, inside the quarter metre where a
        // slamming hand lets go: rendered from rest, a slam drifted shut at 39 dBA. It comes in already coasting.
        var slam = KnobDoor.RenderGameClose(new KnobDoor.Door { Width = 1.1f, HingeWear = KnobDoor.WearOf(0), Seed = 1 }, 48000, KnobDoor.Shut.Slam);
        double laf = LafHeard(slam, KnobDoor.PascalsAtFullScale);
        Assert.InRange(laf, KnobDoor.CloseLafDb(KnobDoor.Shut.Slam) - 4, KnobDoor.CloseLafDb(KnobDoor.Shut.Slam) + 4);
        Assert.True(laf > LafHeard(NormalClose.Value.Pcm, KnobDoor.PascalsAtFullScale) + 8);
    }

    private static List<(double Ms, string Name, double Newtons)> Contacts(KnobDoor.Report rep, string name)
    {
        var list = new List<(double, string, double)>();
        foreach (var e in rep.Events)
        {
            var t = e.Trim();
            int colon = t.IndexOf(": peak ", StringComparison.Ordinal);
            if (colon < 0) continue;
            var head = t.Substring(0, colon).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (head.Length != 3 || head[1] != "ms" || head[2] != name) continue;
            double ms = double.Parse(head[0], System.Globalization.CultureInfo.InvariantCulture);
            double n = double.Parse(t.Substring(colon + 7).Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture);
            list.Add((ms, name, n));
        }
        return list;
    }

    /// <summary>LAFmax after a 20 Hz high-pass: a leaf's swing pushes the air at 1-2 Hz, unheard, and that leaks
    /// several decibels into the A-weighting coefficients below at 48 kHz.</summary>
    private static double LafHeard(float[] x, double fullScale)
        => LafMax(HighPass20(x).Select(v => (float)v).ToArray(), fullScale);

    private static double[] HighPass20(float[] x)
    {
        var y = x.Select(v => (double)v).ToArray();
        for (int k = 0; k < 2; k++) y = Biquad(y, 20, high: true);
        return y;
    }

    /// <summary>A second-order Butterworth section (RBJ) at 48 kHz.</summary>
    private static double[] Biquad(double[] x, double hz, bool high)
    {
        double w = 2 * Math.PI * hz / 48000, c = Math.Cos(w), alpha = Math.Sin(w) / (2 * Math.Sqrt(0.5));
        double b0 = high ? (1 + c) / 2 : (1 - c) / 2, b1 = high ? -(1 + c) : 1 - c, b2 = b0;
        double a0 = 1 + alpha, a1 = -2 * c, a2 = 1 - alpha;
        var y = new double[x.Length];
        double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
        for (int i = 0; i < x.Length; i++)
        {
            double v = (b0 * x[i] + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2) / a0;
            x2 = x1; x1 = x[i]; y2 = y1; y1 = v; y[i] = v;
        }
        return y;
    }

    /// <summary>A band, fourth-order each side; 0 for no high-pass, 20000 or more for no low-pass.</summary>
    private static double[] Band(double[] x, double lo, double hi)
    {
        var y = x;
        if (lo > 0) { y = Biquad(y, lo, true); y = Biquad(y, lo, true); }
        if (hi < 20000) { y = Biquad(y, hi, false); y = Biquad(y, hi, false); }
        return y;
    }

    private static double BandShare(double[] x, double lo, double hi)
        => 10 * Math.Log10(Band(x, lo, hi).Sum(v => v * v) / Math.Max(1e-30, x.Sum(v => v * v)));

    private static int LoudestFrame(double[] x, int frame)
    {
        int best = 0; double top = -1;
        for (int f = 0; (f + 1) * frame <= x.Length; f++)
        {
            double e = 0;
            for (int i = f * frame; i < (f + 1) * frame; i++) e += x[i] * x[i];
            if (e > top) { top = e; best = f; }
        }
        return best;
    }

    /// <summary>A blow's first 30 ms by band (under 300 Hz, 300 Hz-1 kHz, 1-4 kHz, 4-16 kHz), dB re its whole.</summary>
    private static double[] FirstThirtyMs(double[] x, int at)
    {
        int a = Math.Max(0, at - 96), b = Math.Min(x.Length, a + 1440);
        double Energy(double[] y) { double e = 0; for (int i = a; i < b; i++) e += y[i] * y[i]; return e; }
        double whole = Math.Max(1e-30, Energy(x));
        return new[] { (0.0, 300.0), (300.0, 1000.0), (1000.0, 4000.0), (4000.0, 16000.0) }
            .Select(r => 10 * Math.Log10(Math.Max(1e-30, Energy(Band(x, r.Item1, r.Item2))) / whole)).ToArray();
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

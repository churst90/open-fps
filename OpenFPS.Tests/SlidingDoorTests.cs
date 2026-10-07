using OpenFPS.Common;

namespace OpenFPS.Tests;

/// <summary>
/// The patio door (<see cref="SlidingDoor"/>) against the recording Cody named as the patio door
/// (inbox/door-sounds-2026-10-02/patio-slide/ref-goblinjack-slides.wav), measured as the lab's
/// --patio-vs-ref measures it. The recording is a yardstick for the model's physics; nothing of it is played.
/// </summary>
public class SlidingDoorTests
{
    /// <summary>The recording's slides in octaves, 125 Hz to 8 kHz, dB re the loudest (500 Hz).</summary>
    private static readonly double[] Octaves = { 125, 250, 500, 1000, 2000, 4000, 8000 };
    private static readonly double[] Recorded = { -7.4, -1.5, 0.0, -2.4, -9.4, -14.2, -16.2 };

    private static readonly Lazy<(float[] Pcm, double HomeSeconds, SlidingDoor.Report Report)> StandardClose = new(() =>
    {
        var report = new SlidingDoor.Report();
        var pcm = SlidingDoor.RenderClose(new SlidingDoor.Door { Kind = SlidingDoor.Kind.Patio, Variant = 1, Seed = 2 }, 48000, 1.4, report);
        return (pcm, EventSeconds(report, " home"), report);
    });

    private static readonly Lazy<(float[] Pcm, SlidingDoor.Report Report)> StandardOpen = new(() =>
    {
        var report = new SlidingDoor.Report();
        var pcm = SlidingDoor.RenderOpen(new SlidingDoor.Door { Kind = SlidingDoor.Kind.Patio, Variant = 1, Seed = 2 }, 48000, 1.4, report);
        return (pcm, report);
    });

    [Fact]
    public void APatioSlideHasTheRecordedDoorsBalance()
    {
        // Energy from 250 Hz to 1 kHz, thin below 125 Hz and falling steeply above 2 kHz: each octave within
        // 4 dB of the recording's, 125 Hz within 5 (the leaf's bounce still stands about 4.5 dB over it
        // there). Round 2 was flat to 8 kHz, 10-20 dB over the recording at both ends.
        var (pcm, home, _) = StandardClose.Value;
        Assert.False(double.IsNaN(home));
        var bands = Bands(pcm, 48000, 0.25, home - 0.08);
        for (int o = 0; o < Octaves.Length; o++)
            Assert.True(Math.Abs(bands[o] - Recorded[o]) <= (o == 0 ? 5 : 4),
                $"{Octaves[o]} Hz octave at {bands[o]:F1} dB, the recording's {Recorded[o]:F1}");
        Assert.True(bands[6] <= bands[2] - 12, "the slide is not broadband: 8 kHz well under 500 Hz");
    }

    // ── Shutting: the blow ─────────────────────────────────────────────────────────────────────────────
    // Measured on the recording's two shuttings (7.05 s and 11.32 s) as here: the blow's first 30 ms by
    // band, dB re its whole energy: under 300 Hz, 300 Hz-1 kHz, 1-4 kHz, 4-16 kHz.
    private static readonly double[] BlowBands = { 30, 300, 1000, 4000, 16000 };
    private static readonly double[] RecordedBlow = { -4.8, -4.0, -13.3, -20.9 };

    [Fact]
    public void ALeafPushedHomeMeetsTheJambFrameOnFrame()
    {
        // A casual push home squashes the jamb's pile and bulb flat and the stile meets the jamb: hundreds
        // of newtons on the stile's own mass, not a 20 ms push into a soft bulb.
        var (_, _, report) = StandardClose.Value;
        Assert.InRange(ContactPeak(report, "frame-on-frame"), 300, 2000);
    }

    [Fact]
    public void TheBlowIsTheWallsOfStileAndJambNotAHollowBox()
    {
        // Cody, 2026-10-04, on the goblinjack round: "the bumps sound the same, hollow". That blow was its
        // lows (under 300 Hz 1 dB under the whole, 300 Hz-1 kHz 15 down, 4-16 kHz 42 down): a frame mode at
        // 76 Hz and the glass. The recording's is its 300 Hz-1 kHz, the aluminium walls of stile and jamb.
        var (pcm, _, report) = StandardClose.Value;
        int a = (int)((EventSeconds(report, " frame-on-frame") - 0.002) * 48000), b = a + (int)(0.03 * 48000);
        var blow = BandShares(pcm, a, b);
        for (int k = 0; k < RecordedBlow.Length; k++)
            Assert.True(Math.Abs(blow[k] - RecordedBlow[k]) <= 5,
                $"{BlowBands[k]}-{BlowBands[k + 1]} Hz at {blow[k]:F1} dB of the blow, the recording's {RecordedBlow[k]:F1}");
        Assert.True(blow[1] >= blow[0], $"300 Hz-1 kHz ({blow[1]:F1}) leads the lows ({blow[0]:F1})");
    }

    [Fact]
    public void NoFrameModeRingsLowAfterTheBlow()
    {
        // What rang after the old blow was one line at 76 Hz standing 25 dB over its neighbours for half a
        // second: the frame taken as a sheet 6 m long from 60 Hz. The recording's lows ring as a dozen
        // lines within 6 dB of each other. Nothing under 180 Hz stands 15 dB over its neighbours now.
        var (pcm, _, report) = StandardClose.Value;
        double t = EventSeconds(report, " frame-on-frame");
        var (hz, over) = StrongestLine(pcm, 48000, t + 0.03, t + 0.4, 40, 180);
        Assert.True(over <= 15, $"a line at {hz:F0} Hz stands {over:F1} dB over its neighbours");
    }

    [Fact]
    public void TheGlassSoundsItsNoteAsTheRecordingsDoes()
    {
        // The sealed unit's panes on the air between them: the recording's stops ring at 220 Hz, 5.7 Hz wide,
        // and through a band-pass at 216 Hz (Q 4.8) hold 10-15 dB under the blow's first 30 ms over the next
        // 0.4 s. Ours is 4-16-4 at 212 Hz.
        var (pcm, _, report) = StandardClose.Value;
        double t = EventSeconds(report, " frame-on-frame");
        var (hz, _) = StrongestLine(pcm, 48000, t + 0.03, t + 0.4, 150, 300);
        Assert.InRange(hz, 195, 240);
        int a = (int)((t - 0.002) * 48000);
        double blow = 0, note = 0;
        for (int i = a; i < a + (int)(0.03 * 48000); i++) blow += pcm[i] * (double)pcm[i];
        var y = BandPass(pcm, 216, 4.8, 48000);
        for (int i = a + (int)(0.03 * 48000); i < a + (int)(0.4 * 48000); i++) note += y[i] * y[i];
        Assert.InRange(10 * Math.Log10(note / blow), -18, -7);
    }

    [Fact]
    public void TheHandleKnocksInThePileBeforeTheStileLands()
    {
        // The recording has a bright brush 25-38 ms before each blow, 4-5 dB over the blow itself in
        // 4-16 kHz. Here it is the pull handle thrown across its play as the jamb's pile stops the leaf
        // under it, zinc on zinc, a double knock or two before the stile meets the jamb.
        var (pcm, _, report) = StandardClose.Value;
        double t = EventSeconds(report, " frame-on-frame");
        Assert.Contains(report.Events, e => e.Contains("handle:") && Seconds(e) > t - 0.04 && Seconds(e) < t - 0.003);
        int a = (int)((t - 0.002) * 48000), f = (int)(0.005 * 48000);
        double blowTop = Energy(pcm, a, a + f, 4000, 16000), before = 0;
        for (int s = a - (int)(0.06 * 48000); s + f <= a; s += f) before = Math.Max(before, Energy(pcm, s, s + f, 4000, 16000));
        Assert.True(10 * Math.Log10(before / blowTop) >= -8, $"the knock's 4-16 kHz is {10 * Math.Log10(before / blowTop):F1} dB re the blow's");
    }

    [Fact]
    public void AShuttingStaysLoudIntoTheBlow()
    {
        // Both of the recording's shuttings are at their loudest just before the stop: the leaf is pushed
        // home, not let slow to a touch (as the model did, so a shutting faded into its stop as an opening
        // fades to rest).
        var (pcm, home, _) = StandardClose.Value;
        double loudest = MaxFrameDb(pcm, 0.1, home - 0.05, 0.05);
        double last = MaxFrameDb(pcm, home - 0.15, home - 0.01, 0.05);
        Assert.True(last >= loudest - 8, $"the last of the slide is {loudest - last:F1} dB under its loudest");
    }

    // ── Opening ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AnOpeningStrikesNothing()
    {
        // Cody, 2026-10-04: "the open sound sounds like a close sound". The leaf used to be run into its
        // bumper and let go. Now the hand brings it to rest short of it, as both of the recording's openings
        // end: the rollers slowing to nothing, no blow.
        var (pcm, report) = StandardOpen.Value;
        Assert.DoesNotContain(report.Events, e => e.Contains("frame-on-frame") || e.Contains("open-bumper"));
        Assert.True(ContactPeak(report, "shut-bumper") < 5, "the leaf leaves its jamb, it does not strike it");
        double slide = MaxFrameDb(pcm, 0.3, 1.6, 0.05);
        double after = MaxFrameDb(pcm, 1.65, pcm.Length / 48000.0, 0.005);
        Assert.True(after <= slide - 20, $"once the leaf is at rest, something only {slide - after:F1} dB under the slide");
    }

    [Fact]
    public void AnOpeningBeginsWithTheLatchThenTheHand()
    {
        // The recording's openings begin with small clicks 65-105 ms apart, 6-17 dB under the slide, then the
        // leaf breaking away. Here: the hook thrown off its keeper, then the hand taking up the handle's play
        // a tenth of a second later, then the leaf. Neither is a blow: both under the slide.
        var (pcm, report) = StandardOpen.Value;
        double hook = report.Events.Where(e => e.Contains("hook:")).Select(Seconds).DefaultIfEmpty(double.NaN).Min();
        double hand = report.Events.Where(e => e.Contains("handle:")).Select(Seconds).DefaultIfEmpty(double.NaN).Min();
        Assert.InRange(hand - hook, 0.06, 0.25);
        double slide = MaxFrameDb(pcm, 0.3, 1.6, 0.05);
        double clicks = MaxFrameDb(pcm, 0, hand + 0.03, 0.005);
        Assert.True(clicks <= slide, $"the clicks stand {clicks - slide:F1} dB over the slide");
    }

    private static double Seconds(string e) => double.Parse(e.Trim().Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture) / 1000;

    private static double EventSeconds(SlidingDoor.Report report, string what)
        => report.Events.Where(e => e.Contains(what, StringComparison.Ordinal)).Select(Seconds).DefaultIfEmpty(double.NaN).First();

    private static double ContactPeak(SlidingDoor.Report report, string what)
        => report.Events.Where(e => e.Contains(what + ": peak", StringComparison.Ordinal))
            .Select(e => double.Parse(e.Split("peak ")[1].Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture))
            .DefaultIfEmpty(0).Max();

    /// <summary>The loudest frame of <paramref name="frame"/> seconds between two times, dB.</summary>
    private static double MaxFrameDb(float[] x, double from, double to, double frame)
    {
        int n = (int)(frame * 48000), a = (int)(from * 48000), b = Math.Min(x.Length, (int)(to * 48000));
        double best = 1e-30;
        for (int s = a; s + n <= b; s += n)
        {
            double e = 0;
            for (int i = s; i < s + n; i++) e += x[i] * (double)x[i];
            best = Math.Max(best, e / n);
        }
        return 10 * Math.Log10(best);
    }

    /// <summary>Energy of x[a..b) between two frequencies, 48 kHz (a zero-padded FFT, no window).</summary>
    private static double Energy(float[] x, int a, int b, double lo, double hi)
    {
        int n = 1;
        while (n < b - a) n <<= 1;
        var re = new double[n]; var im = new double[n];
        for (int i = a; i < b; i++) re[i - a] = x[i];
        Fft(re, im);
        double sum = 0;
        for (int k = 1; k < n / 2; k++)
        {
            double f = (double)k * 48000 / n;
            if (f >= lo && f < hi) sum += re[k] * re[k] + im[k] * im[k];
        }
        return sum;
    }

    /// <summary>An RBJ band-pass (0 dB at its centre).</summary>
    private static double[] BandPass(float[] x, double f0, double q, int rate)
    {
        double w = 2 * Math.PI * f0 / rate, al = Math.Sin(w) / (2 * q), a0 = 1 + al;
        double b0 = al / a0, b2 = -al / a0, a1 = -2 * Math.Cos(w) / a0, a2 = (1 - al) / a0;
        var y = new double[x.Length];
        double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
        for (int i = 0; i < x.Length; i++)
        {
            double v = b0 * x[i] + b2 * x2 - a1 * y1 - a2 * y2;
            x2 = x1; x1 = x[i]; y2 = y1; y1 = v; y[i] = v;
        }
        return y;
    }

    /// <summary>A stretch's share in each of <see cref="BlowBands"/>, dB re its whole.</summary>
    private static double[] BandShares(float[] x, int a, int b)
    {
        double all = Energy(x, a, b, 0, 24000);
        var shares = new double[BlowBands.Length - 1];
        for (int k = 0; k < shares.Length; k++) shares[k] = 10 * Math.Log10(Energy(x, a, b, BlowBands[k], BlowBands[k + 1]) / all);
        return shares;
    }

    /// <summary>The strongest spectral line between two frequencies over [from, to] s (Hann window, padded to
    /// 2^16), and how far it stands over the median of the spectrum within 30 Hz of it.</summary>
    private static (double Hz, double OverDb) StrongestLine(float[] x, int rate, double from, double to, double lo, double hi)
    {
        const int n = 65536;
        int a = (int)(from * rate), b = (int)(to * rate);
        var re = new double[n]; var im = new double[n];
        for (int i = a; i < b; i++) re[i - a] = x[i] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * (i - a) / (b - a - 1)));
        Fft(re, im);
        double binHz = (double)rate / n;
        var db = new double[n / 2];
        for (int k = 0; k < db.Length; k++) db[k] = 10 * Math.Log10(re[k] * re[k] + im[k] * im[k] + 1e-30);
        int best = (int)(lo / binHz);
        for (int k = (int)(lo / binHz); k <= (int)(hi / binHz); k++) if (db[k] > db[best]) best = k;
        int w = (int)(30 / binHz);
        var near = db.Skip(Math.Max(0, best - w)).Take(2 * w + 1).OrderBy(v => v).ToArray();
        return (best * binHz, db[best] - near[near.Length / 2]);
    }

    /// <summary>Octave bands over [from, to] seconds by Welch's method (8192-point Hann frames), dB re the
    /// loudest of them.</summary>
    private static double[] Bands(float[] x, int rate, double from, double to)
    {
        const int n = 8192;
        var psd = new double[n / 2 + 1];
        int a = (int)(from * rate), b = (int)(to * rate);
        var re = new double[n]; var im = new double[n];
        for (int s = a; s + n <= b; s += n / 2)
        {
            for (int i = 0; i < n; i++) { re[i] = x[s + i] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (n - 1))); im[i] = 0; }
            Fft(re, im);
            for (int k = 0; k < psd.Length; k++) psd[k] += re[k] * re[k] + im[k] * im[k];
        }
        var bands = new double[Octaves.Length];
        for (int o = 0; o < Octaves.Length; o++)
        {
            double sum = 0;
            for (int k = 1; k < psd.Length; k++)
            {
                double f = (double)k * rate / n;
                if (f >= Octaves[o] / Math.Sqrt(2) && f < Octaves[o] * Math.Sqrt(2)) sum += psd[k];
            }
            bands[o] = 10 * Math.Log10(Math.Max(sum, 1e-30));
        }
        double top = bands.Max();
        return bands.Select(v => v - top).ToArray();
    }

    private static void Fft(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len, wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int p = i + k, q = p + len / 2;
                    double tr = re[q] * cr - im[q] * ci, ti = re[q] * ci + im[q] * cr;
                    re[q] = re[p] - tr; im[q] = im[p] - ti; re[p] += tr; im[p] += ti;
                    double nr = cr * wr - ci * wi; ci = cr * wi + ci * wr; cr = nr;
                }
            }
        }
    }
}

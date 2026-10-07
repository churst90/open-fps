using System.Text.RegularExpressions;
using OpenFPS.Common;

namespace OpenFPS.Tests;

/// <summary>
/// The push-bar door (<see cref="PushBarDoor"/>) against recordings of real ones, measured as the lab's
/// --pushbar-vs-ref measures them: kyles's institutional door (inbox/door-sounds-2026-10-02/pushbar/
/// ref-kyles-institutional-bar-and-slam.wav: the bar pushed, let go, and the door slamming) and five slams
/// of berumen's fire exit door (inbox/door-types-2026-10-02/real/2-firedoor-berumen-fire-exit-latch.wav).
/// The recordings are a yardstick for the model's physics; nothing of them is played.
///
/// A hit is a peak of the 1 ms envelope above 1 kHz that rises 6 dB over the 4 ms before it and stands
/// within 30 dB of the loudest; a blow's balance is its loudest hit's first 30 ms by band, dB re its whole.
/// </summary>
public class PushBarDoorTests
{
    private static readonly double[] BandEdges = { 30, 300, 1000, 4000, 16000 };

    private static (float[] Pcm, PushBarDoor.Report Report) Render(int variant, bool closing)
    {
        var report = new PushBarDoor.Report();
        var door = new PushBarDoor.Door { Variant = variant, Seed = 1 + variant };
        var pcm = closing ? PushBarDoor.RenderClose(door, 48000, report) : PushBarDoor.RenderOpen(door, 48000, 1.4, report);
        return (pcm, report);
    }

    private static readonly Lazy<(float[] Pcm, PushBarDoor.Report Report)> StandardOpen = new(() => Render(1, false));
    private static readonly Lazy<(float[] Pcm, PushBarDoor.Report Report)> StandardClose = new(() => Render(1, true));
    private static readonly Lazy<(float[] Pcm, PushBarDoor.Report Report)> OldOpen = new(() => Render(3, false));
    private static readonly Lazy<(float[] Pcm, PushBarDoor.Report Report)> OldClose = new(() => Render(3, true));

    // ── The release ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheReleaseIsAClackNotARattle()
    {
        // Cody, 2026-10-03: "release shorter, not a rattle", "think of the bar being loose, remove the
        // dampening". The recording's release is a clack of several knocks within about 25 ms (hits at 14,
        // 17, 27, 30, 35, 37 ms, the loudest at 27), then knocks 11 dB and more under it 30-75 ms later, and
        // nothing within 20 dB of it after 120 ms. Round 7's pad bounced on its stop every 30 ms: knocks
        // within 2 dB of the clack 36 ms after it, and 11-16 dB under it for 150 ms.
        var (pcm, report) = StandardOpen.Value;
        double letGo = EventSeconds(report, "bar let go");
        Assert.False(double.IsNaN(letGo));
        var hits = Hits(pcm, 48000, letGo - 0.01, letGo + 0.4);
        Assert.NotEmpty(hits);
        var loudest = hits.OrderByDescending(h => h.Db).First();
        int clack = hits.Count(h => Math.Abs(h.Seconds - loudest.Seconds) <= 0.012 && h.Db >= loudest.Db - 6);
        Assert.True(clack >= 2, $"the release is {clack} knock(s) within 12 ms of its loudest: one click, not a clack");
        var after = hits.Where(h => h.Seconds > loudest.Seconds + 0.025).ToList();
        Assert.True(after.All(h => h.Db <= loudest.Db - 5),
            $"a knock {after.Max(h => h.Db) - loudest.Db:F1} dB from the clack after it: the pad bouncing on its stop");
        var late = hits.Where(h => h.Seconds > loudest.Seconds + 0.12).ToList();
        Assert.True(late.All(h => h.Db <= loudest.Db - 20),
            $"a knock {late.Select(h => h.Db - loudest.Db).DefaultIfEmpty(-99).Max():F1} dB from the clack 120 ms after it: still rattling");
    }

    [Fact]
    public void TheReleaseIsTheBarsSteelNotTheDoor()
    {
        // The recording's release, its loudest knock's first 30 ms: under 300 Hz -19.0, 300 Hz-1 kHz -11.9,
        // 1-4 kHz -3.1, 4-16 kHz -3.7. Round 7's, on plastic stops a millisecond long: -3.7, -7.3, -4.2,
        // -23.7: the door's thud with nothing over 4 kHz. Steel tabs on steel stops: 1-4 kHz within 5 dB of the
        // recording's and over the lows, 4-16 kHz within 7 (the door still thuds 10 dB more than in the
        // recording, which was made with the microphone at the latch).
        var (pcm, report) = StandardOpen.Value;
        double letGo = EventSeconds(report, "bar let go");
        var blow = LoudestBlow(pcm, letGo - 0.01, letGo + 0.4);
        Assert.InRange(blow[2], -3.1 - 5, -3.1 + 5);
        Assert.InRange(blow[3], -3.7 - 7, -3.7 + 7);
        Assert.True(blow[2] > blow[0], $"1-4 kHz ({blow[2]:F1}) leads the lows ({blow[0]:F1})");
    }

    // ── The push ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ThePushIsAWhiteNoiseTransientNotAPaddedClunk()
    {
        // Cody: "no clack ... like a heavily padded pushbar ... white noise type transient". The recording's
        // push, its loudest knock's first 30 ms: 4-16 kHz 7.0 dB under the whole; round 7's 20.9 under.
        var (pcm, report) = StandardOpen.Value;
        double letGo = EventSeconds(report, "bar let go");
        var blow = LoudestBlow(pcm, 0, letGo - 0.05);
        Assert.InRange(blow[3], -7.0 - 5, -7.0 + 5);
    }

    // ── The slam ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheSlamRingsTheLatchsSteel()
    {
        // Six recorded slams (five of berumen's, kyles's): the blow's first 30 ms has 4-16 kHz 3.2 to 9.0 dB
        // under its whole. Round 7's standard door, its bolt stopped on a damped lump: 23.8 under. The bolt's
        // stops are the device's chassis and ring its case: at least within 6 dB of the recordings' range.
        var (pcm, _) = StandardClose.Value;
        var blow = LoudestBlow(pcm, 0, pcm.Length / 48000.0);
        Assert.True(blow[3] >= -9.0 - 6, $"4-16 kHz at {blow[3]:F1} dB of the blow");
    }

    [Fact]
    public void AnOldDoorsSlamDoesNotSizzleAfterwards()
    {
        // Round 7's old door (no silencers) lay on bare steel after its slam and the stop chattered at 10 kHz
        // for half a second, 4-16 kHz as loud as the slam: the leaf's modes to 10 kHz fed back into a resting
        // steel contact a step late. Above 4 kHz the skins carry the leaf now; the tail is quiet.
        var (pcm, _) = OldClose.Value;
        double slam = BandEnergyDb(pcm, 0, 0.25, 4000, 16000, 0.03);
        double tail = BandEnergyDb(pcm, 0.3, pcm.Length / 48000.0, 4000, 16000, 0.03);
        Assert.True(tail <= slam - 25, $"4-16 kHz after the slam {tail - slam:F1} dB re the slam's");
    }

    [Fact]
    public void NothingInTheMechanismBuzzes()
    {
        // Cody, on round 8 (2026-10-03): "why do i hear a buzzing on the push bar open". A buzz is one contact
        // repeating at a steady period with steady force: a driven limit cycle, where a bounce's knocks come
        // ever faster and weaker. Round 7 had the pad on its back stop every 4.3 ms at 45-49 N after the
        // release, and every 3.3 ms at 29-32 N after the slam. (The pad's plastic sides touch their rails at
        // under 20 N after a slam, 60 dB under the clack.)
        foreach (var (name, r) in new[] { ("standard open", StandardOpen.Value.Report), ("standard close", StandardClose.Value.Report),
                                          ("old open", OldOpen.Value.Report), ("old close", OldClose.Value.Report) })
        {
            var run = SteadyRun(r, minForce: 25, minCount: 5);
            Assert.True(run == null, $"{name}: {run}");
        }
    }

    // ── Tools ──────────────────────────────────────────────────────────────────────────────────────────

    private static double EventSeconds(PushBarDoor.Report report, string what)
        => report.Events.Where(e => e.Contains(what, StringComparison.Ordinal))
            .Select(e => double.Parse(e.Trim().Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture) / 1000)
            .DefaultIfEmpty(double.NaN).First();

    /// <summary>The first run of one contact repeating at least <paramref name="minCount"/> times at a period
    /// steady within 25 % and peaks within 3 dB, above <paramref name="minForce"/> newtons; null if none.</summary>
    private static string? SteadyRun(PushBarDoor.Report report, double minForce, int minCount)
    {
        var rx = new Regex(@"^\s*([0-9.]+) ms\s+([a-z-]+): peak ([0-9.]+) N");
        var byName = new Dictionary<string, List<(double T, double P)>>();
        foreach (var e in report.Events)
        {
            var m = rx.Match(e);
            if (!m.Success) continue;
            double p = double.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (p < minForce) continue;
            if (!byName.TryGetValue(m.Groups[2].Value, out var list)) byName[m.Groups[2].Value] = list = new();
            list.Add((double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), p));
        }
        foreach (var (name, list) in byName)
        {
            var ev = list.OrderBy(x => x.T).ToList();
            for (int i = 0; i + minCount <= ev.Count; i++)
            {
                double d0 = ev[i + 1].T - ev[i].T;
                if (d0 <= 0 || d0 > 20) continue;
                int j = i + 1;
                while (j < ev.Count && Math.Abs(ev[j].T - ev[j - 1].T - d0) <= 0.25 * d0
                       && Math.Abs(20 * Math.Log10(ev[j].P / ev[i].P)) <= 3) j++;
                if (j - i >= minCount)
                    return $"{name} x{j - i} from {ev[i].T:F1} ms every {d0:F1} ms at {ev[i].P:F0}-{ev[j - 1].P:F0} N";
            }
        }
        return null;
    }

    /// <summary>The hits between two times: (seconds, dB of the 1 ms envelope above 1 kHz).</summary>
    private static List<(double Seconds, double Db)> Hits(float[] pcm, int rate, double from, double to)
    {
        var x = Biquad(pcm.Select(v => (double)v).ToArray(), rate, 30, true);
        var hi = Biquad(Biquad(Biquad(Biquad(x, rate, 1000, true), rate, 1000, true), rate, 16000, false), rate, 16000, false);
        int ms = rate / 1000, n = x.Length / ms;
        var eh = new double[n];
        for (int i = 0; i < n; i++)
        {
            double e = 0;
            for (int k = i * ms; k < (i + 1) * ms; k++) e += hi[k] * hi[k];
            eh[i] = 10 * Math.Log10(e / ms + 1e-24);
        }
        int a = Math.Max(5, (int)(from * 1000)), b = Math.Min(n - 1, (int)(to * 1000));
        double top = double.MinValue;
        for (int i = a; i < b; i++) top = Math.Max(top, eh[i]);
        var hits = new List<(double, double)>();
        for (int i = a; i < b; i++)
        {
            double before = double.MaxValue;
            for (int k = i - 5; k < i - 1; k++) before = Math.Min(before, eh[k]);
            if (eh[i] >= eh[i - 1] && eh[i] > eh[i + 1] && eh[i] > before + 6 && eh[i] > top - 30) hits.Add((i / 1000.0, eh[i]));
        }
        return hits;
    }

    /// <summary>The loudest hit's first 30 ms between two times, by band, dB re its whole.</summary>
    private static double[] LoudestBlow(float[] pcm, double from, double to)
    {
        var hits = Hits(pcm, 48000, from, to);
        Assert.NotEmpty(hits);
        double at = hits.OrderByDescending(h => h.Db).First().Seconds;
        var x = Biquad(pcm.Select(v => (double)v).ToArray(), 48000, 30, true);
        return BandShares(x, (int)((at - 0.001) * 48000), (int)((at + 0.029) * 48000));
    }

    /// <summary>The loudest stretch of <paramref name="frame"/> seconds between two times, energy between two
    /// frequencies, dB.</summary>
    private static double BandEnergyDb(float[] pcm, double from, double to, double lo, double hi, double frame)
    {
        var x = Biquad(Biquad(Biquad(Biquad(pcm.Select(v => (double)v).ToArray(), 48000, lo, true), 48000, lo, true), 48000, hi, false), 48000, hi, false);
        int n = (int)(frame * 48000), a = (int)(from * 48000), b = Math.Min(x.Length, (int)(to * 48000));
        double best = 1e-30;
        for (int s = a; s + n <= b; s += n / 2)
        {
            double e = 0;
            for (int i = s; i < s + n; i++) e += x[i] * x[i];
            best = Math.Max(best, e / n);
        }
        return 10 * Math.Log10(best);
    }

    private static double[] BandShares(double[] x, int a, int b)
    {
        int n = 1;
        while (n < b - a) n <<= 1;
        var re = new double[n]; var im = new double[n];
        for (int i = a; i < b; i++) re[i - a] = x[i];
        Fft(re, im);
        double all = 0;
        var bands = new double[BandEdges.Length - 1];
        for (int k = 1; k < n / 2; k++)
        {
            double f = (double)k * 48000 / n, p = re[k] * re[k] + im[k] * im[k];
            all += p;
            for (int j = 0; j < bands.Length; j++) if (f >= BandEdges[j] && f < BandEdges[j + 1]) bands[j] += p;
        }
        return bands.Select(v => 10 * Math.Log10(Math.Max(v, 1e-30) / Math.Max(all, 1e-30))).ToArray();
    }

    private static double[] Biquad(double[] x, int rate, double hz, bool high)
    {
        double w = 2 * Math.PI * hz / rate, cs = Math.Cos(w), al = Math.Sin(w) / Math.Sqrt(2), a0 = 1 + al;
        double b0 = (high ? (1 + cs) / 2 : (1 - cs) / 2) / a0, b1 = (high ? -(1 + cs) : 1 - cs) / a0, b2 = b0;
        double a1 = -2 * cs / a0, a2 = (1 - al) / a0;
        var y = new double[x.Length];
        double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
        for (int i = 0; i < x.Length; i++)
        {
            double v = b0 * x[i] + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
            x2 = x1; x1 = x[i]; y2 = y1; y1 = v; y[i] = v;
        }
        return y;
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

using OpenFPS.Common;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// A clap is a ringing pocket of air (two leaky palms, Q about three) and a thump of flesh, measured
/// against a recording; a clap of edge alone was a tick, and a crowd of them cellophane.
/// docs/COMMON_NOTES.md, "The clap".
/// </summary>
public class ClapTests
{
    private const int Sr = 48000;
    private readonly ITestOutputHelper _o;
    public ClapTests(ITestOutputHelper o) => _o = o;

    private static (float[] Clap, int Peak) OneClap(int seed)
    {
        var buf = Applause.Render(new CrowdApplause(1, 0.7f, 2f), Sr, seed);
        int peak = 0; float best = 0;
        for (int i = 0; i < buf.Length; i++) if (MathF.Abs(buf[i]) > best) { best = MathF.Abs(buf[i]); peak = i; }
        var clap = new float[Math.Min(Sr / 10, buf.Length - peak)];
        Array.Copy(buf, peak, clap, 0, clap.Length);
        return (clap, peak);
    }

    /// <summary>
    /// The band balance of a measured clap: the 67 clean claps tools/split_footsteps.py cut from
    /// `approved/applause/Slow Clapping  HQ Sound Effects.mp3` (2026-09-19), normalised to their own total,
    /// so shape only; level is <see cref="Applause.SingleClapDb"/>'s. A peak at 1-2 kHz, flesh 8 dB under
    /// it from 125 to 500 Hz, a cliff below 60 Hz. Baked in so the test runs anywhere; re-measure with
    /// `--applause compare=DIR` if the recording is replaced.
    /// </summary>
    private static readonly float[] RealClap =
        { -36.3f, -22.8f, -17.4f, -15.9f, -7.2f, -2.0f, -11.5f, -13.8f, -17.5f };

    /// <summary>A ratchet, not a target: fitted to 3.8 dB worst band on 2026-09-19; tighten it if the fit
    /// improves.</summary>
    private const float BandToleranceDb = 5f;

    [Fact]
    public void TheModelMatchesTheShapeOfARealClap()
    {
        var acc = new double[Spectrum.BandCount];
        for (int seed = 0; seed < 32; seed++)
        {
            var (clap, _) = OneClap(seed);
            var e = Spectrum.BandEnergy(clap, Sr);
            for (int i = 0; i < e.Length; i++) acc[i] += e[i];
        }
        double total = 0;
        foreach (double v in acc) total += v;
        Assert.True(total > 0, "the model rendered nothing to measure");

        float worst = 0f; int worstBand = 0;
        for (int i = 0; i < Spectrum.BandCount; i++)
        {
            float db = 10f * MathF.Log10((float)Math.Max(acc[i] / total, 1e-9));
            float gap = db - RealClap[i];
            _o.WriteLine($"{Spectrum.BandName(i),-14} real {RealClap[i],6:F1}   synth {db,6:F1}   {gap,+6:F1}");
            if (MathF.Abs(gap) > MathF.Abs(worst)) { worst = gap; worstBand = i; }
        }
        _o.WriteLine($"worst: {Spectrum.BandName(worstBand)} at {worst:+0.0;-0.0} dB");
        Assert.True(MathF.Abs(worst) <= BandToleranceDb,
            $"{Spectrum.BandName(worstBand)} is {worst:+0.0;-0.0} dB from a real clap, past the {BandToleranceDb:F0} dB ratchet.");
    }

    /// <summary>A real clap is 20 dB down 5 ms after its peak (median of the 67, 1 ms RMS envelope); a
    /// clap that lingers is a clap in a room, and the room is the engine's job.</summary>
    [Fact]
    public void AClapIsOverAlmostAtOnce()
    {
        var times = new List<float>();
        for (int seed = 0; seed < 16; seed++)
        {
            var (clap, _) = OneClap(seed);
            float peak = 0f; int peakAt = 0;
            for (int i = 0; i < clap.Length; i++) if (MathF.Abs(clap[i]) > peak) { peak = MathF.Abs(clap[i]); peakAt = i; }
            float floor = peak * 0.1f;
            int win = Sr / 1000, last = peakAt;
            for (int at = clap.Length - win; at > peakAt; at -= win)
            {
                double sum = 0;
                for (int i = at; i < at + win; i++) sum += clap[i] * (double)clap[i];
                if (Math.Sqrt(sum / win) > floor) { last = at + win; break; }
            }
            times.Add(1000f * (last - peakAt) / Sr);
        }
        times.Sort();
        float median = times[times.Count / 2];
        _o.WriteLine($"gone by 20 dB: median {median:F1} ms (real 5.0)");
        Assert.True(median < 8f, $"the clap takes {median:F1} ms to fall 20 dB; a real one takes 5.");
    }

    /// <summary>A clap is not a tick: most of it lives below 1.5 kHz, where hands are.</summary>
    [Fact]
    public void AClapHasABodyAndNotJustAnEdge()
    {
        var (clap, _) = OneClap(5);
        // Per octave, as `--applause` reports and the ear weighs: per hertz, the ten kilohertz above
        // 1.5 kHz swamp the two octaves where hands are and a paper bag measures as balanced.
        var bands = VehicleBody.Bands(clap, Sr);
        _o.WriteLine($"below 200 Hz {bands.Low * 100:F0}%   200-1500 Hz {bands.Mid * 100:F0}%   " +
                     $"above 1.5 kHz {bands.High * 100:F0}%");

        Assert.True(bands.Mid > 0.45,
            $"only {bands.Mid * 100:F0}% of the clap is in the two octaves where hands are.");
        Assert.True(bands.Low > 0.08, "there is no weight under it at all.");
        Assert.True(bands.Low < 0.40, "it is all weight: that is a drum, not a pair of hands.");
        // And it still has an edge: a clap with no top is a thud.
        Assert.True(bands.High > 0.04, $"only {bands.High * 100:F0}% above 1.5 kHz — the crack has gone.");
    }

    /// <summary>
    /// A clap has a tail past its crack, gone by 30 ms. The recording reads -31 dB at 8 ms, -35 at 16, -52
    /// at 30 and -64 at 45 (median of 67, 1 ms RMS against the peak); its slow part is the room it was
    /// made in.
    /// </summary>
    [Fact]
    public void AClapOutlastsItsOwnEdge()
    {
        var (clap, _) = OneClap(5);
        float peak = clap.Max(MathF.Abs);

        float At(int ms)
        {
            int from = ms * Sr / 1000, to = Math.Min(clap.Length, from + Sr / 1000);
            if (from >= clap.Length) return -180f;
            double sum = 0; int n = 0;
            for (int i = from; i < to; i++) { sum += clap[i] * (double)clap[i]; n++; }
            return (float)(20 * Math.Log10(Math.Max(1e-9, Math.Sqrt(sum / Math.Max(1, n)) / peak)));
        }

        _o.WriteLine($"8 ms {At(8):F1} dB, 16 ms {At(16):F1} dB, 30 ms {At(30):F1} dB, 45 ms {At(45):F1} dB   (real -31, -35, -52, -64)");
        Assert.True(At(8) > -50f, $"the clap is already gone at 8 ms ({At(8):F1} dB): it is all edge.");
        Assert.True(At(30) < -40f, $"the clap is still {At(30):F1} dB at 30 ms; a real one is -52 and that includes its room.");
        Assert.True(At(16) < At(8), "it should be decaying, not sustaining.");
    }

    /// <summary>A crowd is not a wash because it has a near edge: people fill an area, so a handful of
    /// near ones are much louder than the hundreds behind. Measured as the spread of the loudest
    /// moments.</summary>
    [Fact]
    public void ACrowdHasANearEdgeAndIsNotAWash()
    {
        // A small crowd: four hundred people is 1,500 claps a second, with no isolated clap to measure.
        var buf = Applause.Render(new CrowdApplause(40, 0.7f, 3f), Sr, 9);

        // The loudest sample in each 10 ms window, roughly its biggest clap.
        int win = Sr / 100;
        var peaks = new List<float>();
        for (int at = 0; at + win < buf.Length; at += win)
        {
            float p = 0;
            for (int i = at; i < at + win; i++) p = MathF.Max(p, MathF.Abs(buf[i]));
            if (p > 0) peaks.Add(p);
        }
        peaks.Sort();
        float median = peaks[peaks.Count / 2], top = peaks[(int)(peaks.Count * 0.98f)];
        float spread = 20f * MathF.Log10(top / MathF.Max(1e-6f, median));

        double sum = 0; float peak = 0;
        foreach (float x in buf) { sum += x * (double)x; peak = MathF.Max(peak, MathF.Abs(x)); }
        float crest = (float)(20 * Math.Log10(peak / Math.Sqrt(sum / buf.Length)));
        _o.WriteLine($"40 people: crest {crest:F1} dB, loudest-to-typical {spread:F1} dB over {peaks.Count} windows");

        Assert.True(crest > 12f, $"crest is only {crest:F1} dB — the claps have averaged into a wash.");
        // Measured: 8.6 dB with the near/far spread, 6.3 dB with every clapper the same distance away.
        Assert.True(spread > 7.5f,
            $"the loudest claps are only {spread:F1} dB over the typical one: the crowd has no near edge.");
    }

    /// <summary>The same crowd rendered with two seeds is not the same buffer.</summary>
    [Fact]
    public void NoTwoClapsAreTheSame()
    {
        var a = Applause.Render(new CrowdApplause(50, 0.6f, 1f), Sr, 1);
        var b = Applause.Render(new CrowdApplause(50, 0.6f, 1f), Sr, 2);
        // Only where there is sound: a second of fifty people is mostly gaps.
        int sounding = 0, same = 0;
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            if (a[i] == 0f && b[i] == 0f) continue;
            sounding++;
            if (a[i] == b[i]) same++;
        }
        _o.WriteLine($"{sounding} sounding samples, {same} identical");
        Assert.True(sounding > 1000, "the render is nearly silent; this test is measuring nothing.");
        Assert.True(same < sounding / 100, "two renders of the same crowd came out identical.");
    }
}

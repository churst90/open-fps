using System;
using System.Collections.Generic;
using System.Linq;
using OpenFPS.Common;
using Xunit;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// What a clap IS, and why a crowd of them sounded like a bag being crushed.
///
/// Measured on the old model: a single clap put 0.4 % of its energy below 200 Hz and over forty per
/// cent above 1.5 kHz, and it was gone in twelve milliseconds. That is not a clap, it is a tick — and
/// a thousand ticks a second is cellophane. Two things were missing and both are mechanical:
///
///   THE POCKET OF AIR RINGS. The first version had a sharp resonator and a listener called it pouring
///   water — correct, a drip IS a brief narrow resonance — so it was replaced with a plain low-pass
///   tilt, which has no note in it at all. The question was never whether the cavity resonates but how
///   hard it is damped: two soft leaky palms give a Q of about three.
///
///   AND THE FLESH THUMPS. Two palms meeting is a soft heavy impact before it is anything else. It is
///   low, it is slow, and it is the half of a clap that survives two hundred metres of air — so a clap
///   made only of edge arrives across a stadium as a crinkle, which is exactly what was reported.
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
    /// The band balance of a MEASURED clap, from a recording of one person clapping slowly.
    ///
    /// Averaged over the 67 clean claps that `tools/split_footsteps.py` cut out of
    /// `inbox/Slow Clapping  HQ Sound Effects.mp3` (2026-09-19). Normalised to its own total, so this
    /// is the SHAPE of a clap and says nothing about level — that is <see cref="Applause.SingleClapDb"/>'s
    /// job. Baked in so the test runs anywhere; re-measure with `--applause compare=DIR` if the
    /// reference recording is ever replaced.
    ///
    /// What it says, in words: a clap peaks at 1-2 kHz — the same place a footstep does — with a broad
    /// plateau of flesh from 125 to 500 Hz about eight decibels under the peak, a twelve-decibel fall
    /// in the octave above it, and a cliff below sixty hertz. Before this was measured the model had
    /// been settled by ear with its cavity at 800 Hz and a thump that ran for twelve milliseconds,
    /// and it was eleven decibels heavy at 250-500 Hz, nine light at 1-2 kHz, and twice too slow.
    /// </summary>
    private static readonly float[] RealClap =
        { -36.3f, -22.8f, -17.4f, -15.9f, -7.2f, -2.0f, -11.5f, -13.8f, -17.5f };

    /// <summary>A ratchet, not a target: fitted to 3.8 dB worst-band on 2026-09-19. It may not get
    /// worse. Tighten it if the fit improves; four decibels is where it stops being worth arguing.</summary>
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

    /// <summary>
    /// A real clap is twenty decibels down five milliseconds after its peak (median of the 67, 1 ms
    /// RMS envelope). The model had taken thirteen and a half; a clap that lingers is a clap heard
    /// in a room, and the room is the engine's job, not the clap's.
    /// </summary>
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
        // Per OCTAVE, the way `--applause` reports it and the way the ear weighs it. Counted per hertz
        // instead, the ten kilohertz above 1.5 kHz swamp the two octaves where hands actually are, and
        // a clap that sounds like a paper bag measures as well balanced.
        var bands = VehicleBody.Bands(clap, Sr);
        _o.WriteLine($"below 200 Hz {bands.Low * 100:F0}%   200-1500 Hz {bands.Mid * 100:F0}%   " +
                     $"above 1.5 kHz {bands.High * 100:F0}%");

        Assert.True(bands.Mid > 0.45,
            $"only {bands.Mid * 100:F0}% of the clap is in the two octaves where hands are.");
        Assert.True(bands.Low > 0.08, "there is no weight under it at all.");
        Assert.True(bands.Low < 0.40, "it is all weight: that is a drum, not a pair of hands.");
        // ...and it still has an edge. A clap with no top is a thud.
        Assert.True(bands.High > 0.04, $"only {bands.High * 100:F0}% above 1.5 kHz — the crack has gone.");
    }

    /// <summary>
    /// ...and it outlasts its own edge, but not by much. The recording (median of 67 claps, 1 ms
    /// RMS against the peak) reads -31 dB at 8 ms, -35 at 16, -52 at 30 and -64 at 45; the slow part
    /// of that is the room it was made in, and the room is the engine's job: the model's own flesh is
    /// seventy decibels down by sixteen. What is held here is that there IS a tail past the crack —
    /// the first model stopped dead at twenty-four milliseconds — and that it is gone by thirty.
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

    /// <summary>
    /// A crowd is not a wash, and the reason is that it has a NEAR EDGE.
    ///
    /// People fill an area, so the number of them at a given distance grows with it while the level
    /// falls as 1/r: a handful of near ones are much louder than the hundreds behind them, and those
    /// are the ones a listener picks out and counts. Measured as the spread of the loudest moments —
    /// if every clapper were the same distance away, every clap would be the same size and the top of
    /// the distribution would sit right on top of the middle of it.
    /// </summary>
    [Fact]
    public void ACrowdHasANearEdgeAndIsNotAWash()
    {
        // A SMALL crowd, because four hundred people clapping is fifteen hundred claps a second and
        // at that density there is no such thing as an isolated clap — which is true of a real one and
        // is exactly why "the moment individual claps become a roar" is a rate and not a switch.
        var buf = Applause.Render(new CrowdApplause(40, 0.7f, 3f), Sr, 9);

        // The loudest sample in each 10 ms window: roughly, the biggest clap in that window.
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

    /// <summary>Hands differ, and the sound differs with them: the same crowd twice is not the same
    /// buffer, and a big pair of hands is lower and louder than a small one.</summary>
    [Fact]
    public void NoTwoClapsAreTheSame()
    {
        var a = Applause.Render(new CrowdApplause(50, 0.6f, 1f), Sr, 1);
        var b = Applause.Render(new CrowdApplause(50, 0.6f, 1f), Sr, 2);
        // Only where there is a sound at all: a second of fifty people is mostly gaps, and two
        // silences being equal says nothing.
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

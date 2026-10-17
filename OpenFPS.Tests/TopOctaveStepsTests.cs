using OpenFPS.AudioLab.Spikes;

namespace OpenFPS.Tests;

/// <summary>
/// The lab's click measure (TopOctaveSteps, AudioLab --moving-echoes). A sound that has ended is not a click because
/// the silence after it is quieter still: the patio door's run reported a 54 dB "click" that was a frame 200 dB under
/// the door against a round of zeros (2026-10-17). A step the ear could hear still stands out.
/// </summary>
public class TopOctaveStepsTests
{
    private const int Rate = 48000;

    /// <summary>A 500 Hz ring at 0.3 decaying 20 dB a second for <paramref name="seconds"/>, then digital silence; faded
    /// over its last 0.2 s, or cut.</summary>
    private static (float[] L, float[] R) RingThenSilence(double seconds, double total, bool faded)
    {
        var l = new float[(int)(total * Rate)];
        int n = (int)(seconds * Rate), fade = Rate / 5;
        for (int i = 0; i < n; i++)
        {
            double g = faded && i > n - fade ? 0.5 * (1 - Math.Cos(Math.PI * (n - i) / fade)) : 1.0;
            l[i] = (float)(g * 0.3 * Math.Pow(10.0, -20.0 * i / Rate / 20.0) * Math.Sin(2 * Math.PI * 500.0 * i / Rate));
        }
        return (l, (float[])l.Clone());
    }

    [Fact]
    public void DustLeftInTheSilenceAfterASoundIsNotAClick()
    {
        var (l, r) = RingThenSilence(1.0, 2.0, faded: true);
        // What the patio door's run left 0.65 s after the door: a few samples of float dust, 200 dB under it.
        for (int i = 0; i < 4; i++) { l[(int)(1.6 * Rate) + i] = 3e-11f; r[(int)(1.6 * Rate) + i] = -3e-11f; }
        var frames = TopOctaveSteps.Measure(l, r, Rate, Rate / 50, l.Length);
        var worst = frames.MaxBy(f => f.OverRoundDb);
        Assert.True(worst.OverRoundDb < 10.0, $"{worst.OverRoundDb:F1} dB over its round at {worst.Seconds:F3} s, {worst.UnderLoudestDb:F0} dB against the run's loudest frame");
    }

    [Fact]
    public void ARingCutWhileItCanStillBeHeardIsAClick()
    {
        // Cut at its crest half a second in, 10 dB under where it began: the step stands far out of the ring's own top.
        var (l, r) = RingThenSilence(0.5005, 1.5, faded: false);
        var frames = TopOctaveSteps.Measure(l, r, Rate, Rate / 50, l.Length);
        var worst = frames.MaxBy(f => f.OverRoundDb);
        Assert.True(worst.OverRoundDb > 20.0 && Math.Abs(worst.Seconds + 0.02 - 0.5) < 0.004,
                    $"{worst.OverRoundDb:F1} dB over its round at {worst.Seconds + 0.02:F3} s");
    }
}

using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Common;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// A tyre over a crossing's rail is a clack with a thump under it, not a note (Cody, 2026-10-07: "too
/// tonal, like hollow, it should be more like a clack"). Before, the strike was its 90 Hz and 200 Hz modes
/// alone: one line 62 dB over the rest of the spectrum, nothing over 250 Hz within 40 dB, ringing 140 ms.
/// </summary>
public class WheelStrikeClackTests
{
    private readonly ITestOutputHelper _o;
    public WheelStrikeClackTests(ITestOutputHelper o) => _o = o;

    private const int Rate = 48000;

    [Theory]
    [InlineData(15f)]
    [InlineData(30f)]
    [InlineData(60f)]
    public void A_wheel_over_a_rail_clacks(float kmh)
    {
        float v = kmh / 3.6f;
        var strikes = new WheelStrikes(new[] { 0.33f }, Rate);
        float pa = WheelStrikes.PeakPascals(v, 4000f, WheelStrikes.CrossingStepMetres);
        strikes.Queue(new[] { new WheelStrike(0, 0.0, pa, WheelStrikes.ContactSeconds(v, 0.33f)) });
        strikes.Drain(0, 0, 0.0, Rate, 1f);
        var x = new float[Rate / 2];
        for (int i = 0; i < x.Length; i++) { strikes.Advance(i); x[i] = strikes.Out(0); }

        float peak = x.Max(MathF.Abs);
        // How long it rings: the last millisecond whose peak is within 40 dB of the strike's.
        int last = 0;
        for (int ms = 0; ms < x.Length / 48; ms++)
            if (x.AsSpan(ms * 48, 48).ToArray().Max(MathF.Abs) > peak * 0.01f) last = ms;
        // The energy the ear weights most against the thump's: A-weighted, 500 Hz to 4 kHz against 63 Hz.
        var bands = Spectrum.BandsDb(x, Rate);
        double Band(int edge) => bands[Array.IndexOf(Spectrum.BandEdges, (float)edge)];
        double mids = 10 * Math.Log10(new[] { (500, -3.2), (1000, 0.0), (2000, 1.2), (4000, 1.0) }
                                       .Sum(b => Math.Pow(10, (Band(b.Item1) + b.Item2) / 10)));
        double thump = Band(60) - 26.2;
        _o.WriteLine($"{kmh} km/h: peak {peak:F2} Pa of {pa:F2} asked, rings {last} ms, A-weighted mids {mids - thump:+0.0;-0.0} dB over the thump");
        Assert.InRange(peak, pa * 0.6f, pa * 1.5f);
        Assert.True(last < 90, $"it rings {last} ms: a note, not a clack");
        Assert.True(mids - thump > 3, $"the clack is {mids - thump:F1} dB against the thump, A-weighted");
    }
}

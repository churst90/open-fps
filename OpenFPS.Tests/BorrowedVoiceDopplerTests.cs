using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Fmod;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// A borrowed voice carries its own Doppler and nobody else's. A far car voiced from a near car's ring also
/// inherited that car's Doppler, because it read back from the near car's play position, which advances at
/// the channel's pitch. Driven directly: a source consumed faster than real time, as a channel pitched up
/// for an approaching car consumes it, and what comes out must be at the synthesized pitch.
/// </summary>
[Collection(nameof(ValveFlowSwitch))]
public class BorrowedVoiceDopplerTests
{
    private readonly ITestOutputHelper _o;
    public BorrowedVoiceDopplerTests(ITestOutputHelper o) => _o = o;

    private const float Rate = 48000f;
    private const int Block = 1024;

    /// <summary>
    /// Blocks rendered before anything is measured: 2.7 s. PlaceAtSpeed leaves the driver to settle, and
    /// the stock car at 200 km/h swings 5815 -> 6015 -> 5803 rpm over its first two seconds. While the
    /// crank sweeps, its period smears and the strongest autocorrelation in a half second can be the
    /// exhaust's fixed resonance near 316 Hz instead: after 64 blocks the two were 0.005 apart, and the
    /// valve solver's tolerance change (2026-10-09) tipped the measure to the pipe. Settled, the crank's
    /// period leads it by 0.35.
    /// </summary>
    private const int WarmBlocks = 128;

    private static EngineVoiceState StockCarAt200(double consumeRate)
    {
        var profile = VehicleProfile.StockCar;
        float speed = 200f / 3.6f;
        // The provider tells a voice the rate its channel is pitched at whenever it pitches it
        // (EngineVoiceState.ConsumeRate); a reflection follows that rate rather than Played's blocks.
        var source = new EngineVoiceState(profile, Rate, 11) { TargetSpeed = speed, ConsumeRate = (float)consumeRate };
        source.PlaceAtSpeed(speed);
        return source;
    }

    /// <summary>A source consumed at <paramref name="consumeRate"/> times real time with an echo reading
    /// from it; returns the echo's output and the crank's speed at the end.</summary>
    private static (float[] Output, float Rpm) Run(bool ownCursor, double consumeRate, int blocks)
    {
        var source = StockCarAt200(consumeRate);
        var echo = new EngineEchoState(source)
        {
            TargetDelaySeconds = 0.25f,
            TargetGain = 1f,
            OwnCursor = ownCursor,
            SampleRate = Rate,
        };

        // Fill the ring far enough back that the echo has something to read, and let the engine settle.
        var warm = new float[Block];
        float rpmBefore = 0f;
        for (int i = 0; i < WarmBlocks; i++)
        {
            if (i == WarmBlocks - 8) rpmBefore = source.Engine.Rpm;
            source.Render(warm);
        }
        // Steady to a part in a thousand over the last eight blocks, or the measure below means nothing.
        Assert.InRange(source.Engine.Rpm, rpmBefore * 0.999f, rpmBefore * 1.001f);

        var outBuf = new float[blocks * Block];
        var srcBlock = new float[(int)(Block * consumeRate)];
        var echoBlock = new float[Block];
        for (int b = 0; b < blocks; b++)
        {
            // The source is consumed at the pitched rate; the echo always produces one block.
            source.Render(srcBlock);
            echo.Render(echoBlock);
            Array.Copy(echoBlock, 0, outBuf, b * Block, Block);
        }
        return (outBuf, source.Engine.Rpm);
    }

    /// <summary>The strongest period in the signal, in samples, by autocorrelation.</summary>
    private static float PeriodSamples(float[] signal, int from, int to)
    {
        int n = signal.Length;
        double best = 0; int bestLag = from;
        for (int lag = from; lag <= to; lag++)
        {
            double sum = 0;
            for (int i = 0; i < n - lag; i++) sum += signal[i] * (double)signal[i + lag];
            if (sum > best) { best = sum; bestLag = lag; }
        }
        return bestLag;
    }

    [Fact]
    public void ABorrowedVoiceDoesNotInheritTheDopplerOfTheCarItBorrowedFrom()
    {
        // 25 % faster consumption is what a channel pitched up for a car approaching at 70 m/s does.
        const double approaching = 1.25;

        // The valves' broadband flow noise pulls the autocorrelation about; the cursor is under test, not the noise.
        var (truth, control, inherited, own) = ValveFlowSwitch.Without(() => (
            Run(ownCursor: true, consumeRate: 1.0, blocks: 24).Output,
            Run(ownCursor: false, consumeRate: 1.0, blocks: 24).Output,
            Run(ownCursor: false, consumeRate: approaching, blocks: 24).Output,
            Run(ownCursor: true, consumeRate: approaching, blocks: 24).Output));

        // A stock car at 200 km/h fires somewhere around 200-400 Hz; look for a period between.
        int from = (int)(Rate / 600f), to = (int)(Rate / 80f);
        float pTruth = PeriodSamples(truth, from, to);
        float pControl = PeriodSamples(control, from, to);
        float pInherited = PeriodSamples(inherited, from, to);
        float pOwn = PeriodSamples(own, from, to);

        _o.WriteLine($"source consumed at real time      : period {pTruth:F0} samples ({Rate / pTruth:F1} Hz)");
        _o.WriteLine($"consumed at real time, following  : period {pControl:F0} samples ({Rate / pControl:F1} Hz)");
        _o.WriteLine($"consumed 25% fast, following it   : period {pInherited:F0} samples ({Rate / pInherited:F1} Hz)");
        _o.WriteLine($"consumed 25% fast, own cursor     : period {pOwn:F0} samples ({Rate / pOwn:F1} Hz)");

        // The control: at real-time consumption following the play position is fine, which is why this hid and
        // was heard only as "sounds like cruising" on a pass.
        Assert.InRange(pControl, pTruth * 0.93f, pTruth * 1.07f);

        // Following the source's play position pulls the borrowed voice along with it...
        Assert.True(pInherited < pTruth * 0.95f,
            $"the old behaviour should be pitched UP by the source's consumption: {pInherited:F0} vs {pTruth:F0} samples.");
        // ...and keeping our own cursor does not.
        Assert.InRange(pOwn, pTruth * 0.93f, pTruth * 1.07f);
    }

    /// <summary>
    /// What an engine synthesizes does not depend on how fast its channel takes it: consumed at 0.8, 1 and
    /// 1.25 times real time (blocks of 819, 1024 and 1280, as a pitched FMOD channel asks), the stream is the
    /// same to the bit. A reader at the synthesized rate hears the crank's own period; one following the play
    /// position hears it scaled by the rate, which is the Doppler the channel is meant to carry. Measured
    /// against the crank's revolution at its own rpm, so a measure that wanders to another peak fails here
    /// rather than passing for a cursor fault.
    /// </summary>
    [Theory]
    [InlineData(0.8)]
    [InlineData(1.0)]
    [InlineData(1.25)]
    public void AnEngineConsumedAtAnyRateSynthesizesTheSameStreamAtTheCranksPitch(double rate)
    {
        const int Samples = 160 * Block;
        static float[] Stream(double consumeRate)
        {
            var source = StockCarAt200(consumeRate);
            var all = new float[Samples];
            var block = new float[(int)(Block * consumeRate)];
            for (int at = 0; at < Samples; at += block.Length)
            {
                source.Render(block);
                Array.Copy(block, 0, all, at, Math.Min(block.Length, Samples - at));
            }
            return all;
        }

        var (reference, consumed, own, following) = ValveFlowSwitch.Without(() => (
            Stream(1.0), Stream(rate),
            Run(ownCursor: true, consumeRate: rate, blocks: 24),
            Run(ownCursor: false, consumeRate: rate, blocks: 24)));

        int differing = 0;
        for (int i = 0; i < Samples; i++) if (consumed[i] != reference[i]) differing++;
        Assert.Equal(0, differing);

        // One crank revolution, the strongest period of this V8 settled at speed; searched from 0.6 to 1.45
        // revolutions, which holds the revolution at every rate here and neither the pipe's resonance nor
        // the four-stroke cycle.
        float rev = Rate * 60f / own.Rpm;
        int from = (int)(0.6f * rev), to = (int)(1.45f * rev);
        float pOwn = PeriodSamples(own.Output, from, to);
        float pFollowing = PeriodSamples(following.Output, from, to);
        _o.WriteLine($"consumed at {rate:F2}: crank revolution {rev:F1} samples ({own.Rpm:F0} rpm); "
                     + $"own cursor {pOwn:F0}, following {pFollowing:F0} (expected {rev / rate:F1})");

        // The own cursor may lean a per cent toward its place (EngineEchoState.MaxRateCorrection).
        Assert.InRange(pOwn, rev * 0.97f, rev * 1.03f);
        Assert.InRange(pFollowing, rev / rate * 0.97f, rev / rate * 1.03f);
    }
}

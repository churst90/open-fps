using System;
using System.Linq;
using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Fmod;
using Xunit;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// A borrowed voice must carry its OWN Doppler and nobody else's.
///
/// A car too far away to be worth its own engine is voiced by reading a near car's ring buffer. It is
/// placed at its own position, moves at its own velocity, and is pitched by its own Doppler — and it
/// was ALSO inheriting the Doppler of the car it borrowed from, because it read back from that car's
/// PLAY POSITION and the play position advances at whatever rate the mixer is consuming that voice,
/// which is its channel pitch, which is its Doppler. Two cars' Dopplers on one voice, belonging to two
/// cars going different ways.
///
/// The test drives the mechanism directly: a source consumed faster than real time, exactly as a
/// channel pitched up for an approaching car makes the mixer ask for more input per block, and a
/// borrowed voice reading from it. What comes out must be at the pitch the source was SYNTHESIZED at.
/// </summary>
[Collection(nameof(ValveFlowSwitch))]
public class BorrowedVoiceDopplerTests
{
    private readonly ITestOutputHelper _o;
    public BorrowedVoiceDopplerTests(ITestOutputHelper o) => _o = o;

    private const float Rate = 48000f;
    private const int Block = 1024;

    /// <summary>
    /// Runs a source being consumed at <paramref name="consumeRate"/> times real time with an echo
    /// reading from it, and returns the echo's output.
    /// </summary>
    private static float[] Run(bool ownCursor, double consumeRate, int blocks)
    {
        var profile = VehicleProfile.StockCar;
        float speed = 200f / 3.6f;
        var source = new EngineVoiceState(profile, Rate, 11) { TargetSpeed = speed };
        source.PlaceAtSpeed(speed);

        var echo = new EngineEchoState(source)
        {
            TargetDelaySeconds = 0.25f,
            TargetGain = 1f,
            OwnCursor = ownCursor,
            SampleRate = Rate,
        };

        // Fill the ring far enough back that the echo has something to read.
        var warm = new float[Block];
        for (int i = 0; i < 64; i++) source.Render(warm);

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
        return outBuf;
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

        // The period is found by autocorrelation, which the valves' broadband flow noise pulls about;
        // the mechanism under test is the cursor, not the noise.
        var (truth, control, inherited, own) = ValveFlowSwitch.Without(() => (
            Run(ownCursor: true, consumeRate: 1.0, blocks: 24),
            Run(ownCursor: false, consumeRate: 1.0, blocks: 24),
            Run(ownCursor: false, consumeRate: approaching, blocks: 24),
            Run(ownCursor: true, consumeRate: approaching, blocks: 24)));

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

        // The control: while the source is consumed at real time, following its play position is fine.
        // Nothing about the OLD path is wrong until the source's own pitch moves — which is exactly
        // why this was so hard to see, and why it showed up as "sounds like cruising" on a pass.
        Assert.InRange(pControl, pTruth * 0.93f, pTruth * 1.07f);

        // Following the source's play position pulls the borrowed voice along with it...
        Assert.True(pInherited < pTruth * 0.95f,
            $"the old behaviour should be pitched UP by the source's consumption: {pInherited:F0} vs {pTruth:F0} samples.");
        // ...and keeping our own cursor does not.
        Assert.InRange(pOwn, pTruth * 0.93f, pTruth * 1.07f);
    }
}

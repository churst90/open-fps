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

    /// <summary>A source consumed at <paramref name="consumeRate"/> times real time with an echo reading
    /// from it; returns the echo's output.</summary>
    private static float[] Run(bool ownCursor, double consumeRate, int blocks)
    {
        var profile = VehicleProfile.StockCar;
        float speed = 200f / 3.6f;
        // The provider tells a voice the rate its channel is pitched at whenever it pitches it
        // (EngineVoiceState.ConsumeRate); a reflection follows that rate rather than Played's blocks.
        var source = new EngineVoiceState(profile, Rate, 11) { TargetSpeed = speed, ConsumeRate = (float)consumeRate };
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

        // The valves' broadband flow noise pulls the autocorrelation about; the cursor is under test, not the noise.
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

        // The control: at real-time consumption following the play position is fine, which is why this hid and
        // was heard only as "sounds like cruising" on a pass.
        Assert.InRange(pControl, pTruth * 0.93f, pTruth * 1.07f);

        // Following the source's play position pulls the borrowed voice along with it...
        Assert.True(pInherited < pTruth * 0.95f,
            $"the old behaviour should be pitched UP by the source's consumption: {pInherited:F0} vs {pTruth:F0} samples.");
        // ...and keeping our own cursor does not.
        Assert.InRange(pOwn, pTruth * 0.93f, pTruth * 1.07f);
    }
}

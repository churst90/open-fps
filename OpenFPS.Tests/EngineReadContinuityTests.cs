using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Fmod;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// What reads a live engine in step with it — a reflection of the car, the front of the car as a voice
/// of its own — must read it continuously, however the mixer takes the car.
///
/// FMOD resamples a pitched DSP channel by calling it more or fewer times per mixer block, a whole block
/// each time (measured in the lab: a car closing at 60 km/h was called 4.9 % more often, every call 1024
/// samples). So the car's play position moves in whole blocks. A reflection read a fixed distance behind
/// it skipped 23 ms of the car at every extra call, and the front voice stepped up to ten samples toward
/// it at nearly every block: 4-8 discontinuities a second on a car going past (--quality echo).
/// </summary>
[Collection(nameof(ValveFlowSwitch))]
public class EngineReadContinuityTests
{
    private readonly ITestOutputHelper _o;
    public EngineReadContinuityTests(ITestOutputHelper o) => _o = o;

    private const float Rate = 44100f;
    private const int Block = 1024;

    /// <summary>Samples whose second difference stands far over its local level: a skip or a repeat
    /// in a read leaves one; the engine's own waveform, read straight, does not.</summary>
    private static int Discontinuities(float[] x, int skip)
    {
        int n = x.Length - 2;
        var r = new double[n];
        for (int i = 0; i < n; i++) r[i] = x[i + 2] - 2.0 * x[i + 1] + x[i];
        int count = 0, last = -1000;
        const int W = 1024;
        for (int i = skip; i < n - W; i++)
        {
            double e = 0;
            if ((i & 63) != 0 && last > i - 64) continue;
            for (int k = Math.Max(0, i - W); k < i + W; k += 4) e += r[k] * r[k];
            double rms = Math.Sqrt(e / (W / 2)) + 1e-12;
            if (Math.Abs(r[i]) > 8 * rms && i - last > 64) { count++; last = i; }
        }
        return count;
    }

    private static (float[] Source, float[] Echo, float[] Front) Drive(float pitch, int blocks)
    {
        var profile = VehicleProfile.ByName("i4_midsize");
        float speed = 60f / 3.6f;
        var source = new EngineVoiceState(profile, Rate, 7) { TargetSpeed = speed, ConsumeRate = pitch };
        source.PlaceAtSpeed(speed);
        var echo = new EngineEchoState(source) { TargetDelaySeconds = 0.03f, TargetGain = 1f, SampleRate = Rate };
        var front = new EngineTapState(source) { ChannelRate = 1f };

        var src = new List<float>();
        var e = new float[blocks * Block];
        var f = new float[blocks * Block];
        var block = new float[Block];
        double owed = 0;
        for (int b = 0; b < blocks; b++)
        {
            source.Produce();
            // The mixer takes the pitched car a whole block at a time, as many times as it is owed.
            owed += pitch;
            while (owed >= 1.0)
            {
                source.Consume(block);
                src.AddRange(block);
                owed -= 1.0;
            }
            echo.Render(e.AsSpan(b * Block, Block));
            front.Render(f.AsSpan(b * Block, Block));
        }
        return (src.ToArray(), e, f);
    }

    [Fact]
    public void AReflectionAndAFrontVoiceReadAPitchedCarWithoutSkipping()
    {
        var (source, echo, front) = ValveFlowSwitch.Without(() => Drive(1.05f, 260));
        int skip = 60 * Block;
        int s = Discontinuities(source, skip), e = Discontinuities(echo, skip), f = Discontinuities(front, skip);
        _o.WriteLine($"discontinuities after warm-up: the car itself {s}, its reflection {e}, its front voice {f}");
        Assert.True(e <= s + 1, $"the reflection skipped: {e} against the car's own {s}");
        Assert.True(f <= s + 1, $"the front voice stepped: {f} against the car's own {s}");
    }
}

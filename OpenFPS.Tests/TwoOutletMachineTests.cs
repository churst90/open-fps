using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Common;

namespace OpenFPS.Tests;

/// <summary>
/// A machine close enough for its two ends (intake and exhaust) to be told apart gets a voice for each,
/// and its level must not change when it does: a level that moved with the voice count would be heard as
/// the car jumping.
/// </summary>
public class TwoOutletMachineTests
{
    private const int Rate = 44100, Block = 1024;

    /// <summary>Same engine and seed, rendered as one voice and as exhaust plus intake: the pair sums to
    /// the single voice sample for sample, so the split changes where the sound comes from, not how much.</summary>
    [Fact]
    public void TheTwoOutletsSumToTheOneVoice()
    {
        var v = VehicleProfile.ByName("v8_muscle");
        const float speed = 25f;

        var whole = new EngineVoiceState(v, Rate, 7) { TargetSpeed = speed };
        whole.PlaceAtSpeed(speed);
        var split = new EngineVoiceState(v, Rate, 7) { TargetSpeed = speed, SplitVoices = true };
        split.PlaceAtSpeed(speed);
        var front = new EngineTapState(split);

        var a = new float[Block];
        var rear = new float[Block];
        var intake = new float[Block];

        double worst = 0, energy = 0;
        int blocks = Rate * 2 / Block;           // two seconds
        for (int b = 0; b < blocks; b++)
        {
            whole.Produce();
            split.Produce();
            // The intake voice reads the block the exhaust voice is about to take; here, unlike in FMOD,
            // the callback order is ours, so the alignment is exact.
            front.Render(intake);
            whole.Consume(a);
            split.Consume(rear);

            // The first tenth of a second is the crossfade, the exhaust voice still handing the front over.
            if (b < Rate / 10 / Block) continue;
            for (int i = 0; i < Block; i++)
            {
                double diff = Math.Abs(a[i] - (rear[i] + intake[i]));
                if (diff > worst) worst = diff;
                energy += a[i] * a[i];
            }
        }

        Assert.True(energy > 1e-3, $"the voice rendered nothing to compare (energy {energy:G3})");
        Assert.True(worst < 1e-4, $"the two outlets do not add back up to the one voice: worst sample difference {worst:G4}");
    }

    /// <summary>The exhaust voice alone is not the whole machine, or the intake voice would be inaudible.</summary>
    [Fact]
    public void TheIntakeVoiceCarriesSomethingWorthHearing()
    {
        var v = VehicleProfile.ByName("v8_muscle");
        const float speed = 25f;
        var engine = new EngineVoiceState(v, Rate, 7) { TargetSpeed = speed, SplitVoices = true };
        engine.PlaceAtSpeed(speed);
        var front = new EngineTapState(engine);

        var rear = new float[Block];
        var intake = new float[Block];
        double rearEnergy = 0, intakeEnergy = 0;
        int blocks = Rate / Block;
        for (int b = 0; b < blocks; b++)
        {
            engine.Produce();
            front.Render(intake);
            engine.Consume(rear);
            if (b < 8) continue;                  // past the crossfade and the gain ramp
            for (int i = 0; i < Block; i++)
            {
                rearEnergy += rear[i] * rear[i];
                intakeEnergy += intake[i] * intake[i];
            }
        }

        double db = 10.0 * Math.Log10(Math.Max(1e-12, intakeEnergy) / Math.Max(1e-12, rearEnergy));
        // The front is well down on the back but not nothing: 40 dB under would be a voice spent on silence.
        Assert.InRange(db, -30.0, -1.0);
    }

    /// <summary>
    /// A school bus idling is its block, 97.7 dB under the bonnet against an 83 dB silenced pipe; that
    /// clatter left by the tailpipe voice eleven metres away ("the front of the bus and the exhaust are in
    /// the same place"). Idling, the nose is the louder end.
    /// </summary>
    [Fact]
    public void AnIdlingBusIsLouderAtItsEngineThanAtItsTailpipe()
    {
        var v = VehicleProfile.ByName("school_bus");
        var engine = new EngineVoiceState(v, Rate, 7) { TargetSpeed = 0f, SplitVoices = true };
        engine.PlaceAtSpeed(0f);
        var front = new EngineTapState(engine);

        var rear = new float[Block];
        var nose = new float[Block];
        double rearEnergy = 0, noseEnergy = 0;
        for (int b = 0; b < Rate * 2 / Block; b++)
        {
            engine.Produce();
            front.Render(nose);
            engine.Consume(rear);
            if (b < 8) continue;
            for (int i = 0; i < Block; i++)
            {
                rearEnergy += rear[i] * rear[i];
                noseEnergy += nose[i] * nose[i];
            }
        }
        double db = 10.0 * Math.Log10(Math.Max(1e-12, noseEnergy) / Math.Max(1e-12, rearEnergy));
        Assert.True(db > 3.0, $"the idling bus is {db:F1} dB louder at its nose than at its tail");
    }

    /// <summary>Two ends are two sources while the angle between them is wide enough: the angle, which the
    /// ear measures, not the distance.</summary>
    [Fact]
    public void OutletsMergeAtTheDistanceTheAngleSays()
    {
        // A car: airbox to tailpipe, about three and a half metres.
        float car = 3.4f;
        Assert.True(Localisation.Resolvable(car, 5f));
        Assert.False(Localisation.Resolvable(car, 60f));

        // A motorcycle's ends are a metre apart, so it merges much sooner, by the same arithmetic.
        float bike = 1.0f;
        Assert.True(Localisation.MergingDistance(bike) < Localisation.MergingDistance(car));
        Assert.True(Localisation.Resolvable(bike, 3f));
        Assert.False(Localisation.Resolvable(bike, 20f));

        // The merging distance is where the test flips, both ways.
        float d = Localisation.MergingDistance(car);
        Assert.True(Localisation.Resolvable(car, d * 0.95f));
        Assert.False(Localisation.Resolvable(car, d * 1.05f));
    }

    /// <summary>Every machine in the library says where both its ends are.</summary>
    [Fact]
    public void EveryMachineHasTwoEndsThatAreNotTheSamePlace()
    {
        foreach (string key in VehicleProfile.Presets.Keys)
        {
            var v = VehicleProfile.ByName(key);
            float separation = MathF.Sqrt(
                MathF.Pow(v.ExhaustOffsetZ - v.IntakeOffsetZ, 2) +
                MathF.Pow(v.ExhaustHeight - v.IntakeHeight, 2));
            Assert.True(separation > 0.5f, $"{key}: its outlets are {separation:F2} m apart");
            Assert.True(separation < 12f, $"{key}: its outlets are {separation:F2} m apart, which is a train");
        }
    }
}

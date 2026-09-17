using System;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Common;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// A machine heard through two voices is the same machine.
///
/// A car is a rig: the exhaust is a couple of metres behind the intake, and at close range that
/// separation is most of how a listener knows which way it is pointing. Heard through one voice the
/// geometry is lost. So a machine close enough for its two ends to be told apart gets a voice for
/// each — and the one thing that must not change when it does is HOW LOUD IT IS. A level that moved
/// when the mixer changed its mind about how many voices to spend would be heard as the car jumping,
/// which is the kind of fault a listener notices and cannot name.
/// </summary>
public class TwoOutletMachineTests
{
    private const int Rate = 44100, Block = 1024;

    /// <summary>
    /// The two outlets sum to exactly the one voice.
    ///
    /// Rendered twice from the same engine and the same seed: once as a single voice, once split into
    /// an exhaust voice and an intake voice. Sample for sample, the split pair adds up to the single
    /// one — which is what makes crossing the threshold a change in WHERE the sound comes from and
    /// not in how much of it there is.
    /// </summary>
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
            // The intake voice reads the block the exhaust voice is about to take. In the mixer the
            // order of the two callbacks is FMOD's business; here it is ours, so the alignment is
            // exact and what is being measured is the split itself.
            front.Render(intake);
            whole.Consume(a);
            split.Consume(rear);

            // The first tenth of a second is the crossfade: the exhaust voice is still handing the
            // front of the machine over. Both are correct; they simply overlap.
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

    /// <summary>
    /// ...and the exhaust voice alone is NOT the whole machine, which is the other half of the
    /// claim. If it were, the intake voice would be free and it would also be inaudible.
    /// </summary>
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
        // The front of a car is well down on the back of it and is not nothing: an intake buried
        // forty decibels under the exhaust would be a voice spent on silence.
        Assert.InRange(db, -30.0, -1.0);
    }

    /// <summary>
    /// Two sources are two sources while the angle between them is wide enough, and one source after
    /// that — and it is the ANGLE, not the distance, because that is the thing an ear measures.
    /// </summary>
    [Fact]
    public void OutletsMergeAtTheDistanceTheAngleSays()
    {
        // A car: airbox to tailpipe, about three and a half metres.
        float car = 3.4f;
        Assert.True(Localisation.Resolvable(car, 5f));
        Assert.False(Localisation.Resolvable(car, 60f));

        // A motorcycle's ends are a metre apart, so it becomes one thing much sooner. Nothing was
        // authored for either: it is the same arithmetic on a different machine.
        float bike = 1.0f;
        Assert.True(Localisation.MergingDistance(bike) < Localisation.MergingDistance(car));
        Assert.True(Localisation.Resolvable(bike, 3f));
        Assert.False(Localisation.Resolvable(bike, 20f));

        // And the merging distance is where the test flips, in both directions.
        float d = Localisation.MergingDistance(car);
        Assert.True(Localisation.Resolvable(car, d * 0.95f));
        Assert.False(Localisation.Resolvable(car, d * 1.05f));
    }

    /// <summary>Every machine in the library says where both its ends are — a rig with one end is
    /// a machine that cannot be heard pointing anywhere.</summary>
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

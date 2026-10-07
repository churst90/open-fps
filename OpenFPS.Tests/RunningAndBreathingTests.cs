using System.Numerics;
using OpenFPS.Client.Core;
using OpenFPS.Common;

namespace OpenFPS.Tests;

/// <summary>
/// Running, and what it costs a body: footfalls come from ground covered, not the clock, and being out
/// of breath outlasts the running long enough to find somebody by it.
/// </summary>
public class RunningAndBreathingTests
{
    private static readonly Quaternion Facing = Quaternion.Identity;

    /// <summary>
    /// One step of ground is one footstep however often the accumulator is asked: polled every 5 cm or
    /// every 40, ten metres is the same count, or a body would sound like its frame rate.
    /// </summary>
    [Theory]
    [InlineData(0.05f)]   // many small updates, as a fast renderer produces
    [InlineData(0.15f)]   // a walk at 60 Hz
    [InlineData(0.4f)]    // a hard run at 20 Hz
    public void TenMetresIsTheSameNumberOfStepsHoweverOftenYouLook(float metresPerUpdate)
    {
        const float speed = 5f;
        var stride = new StrideAccumulator();
        var velocity = new Vector3(0, 0, speed);
        var at = Vector3.Zero;

        int steps = 0;
        int updates = (int)MathF.Round(10f / metresPerUpdate);   // ten metres of ground
        for (int i = 0; i < updates; i++)
        {
            at += new Vector3(0, 0, metresPerUpdate);
            if (stride.Update(at, velocity, isGrounded: true, Facing).Stepped) steps++;
        }

        // Ten metres over this speed's step, plus the one that started the walk.
        int expected = (int)(10f / StrideAccumulator.StepLength(speed));
        Assert.InRange(steps, expected, expected + 2);
    }

    /// <summary>
    /// A faster body takes longer steps, so the same ground is fewer footsteps and the cadence barely
    /// moves. A fixed half-metre stride "sounds like cockroaches running" (docs/COMMON_NOTES.md, Walking;
    /// <see cref="StrideAccumulator.StepLength"/>).
    /// </summary>
    [Fact]
    public void ARunIsLongerStepsAndBarelyAFasterCadence()
    {
        int Walk(float speed, float seconds)
        {
            var stride = new StrideAccumulator();
            var velocity = new Vector3(0, 0, speed);
            var at = Vector3.Zero;
            int steps = 0;
            const float dt = 1f / 60f;
            for (float t = 0; t < seconds; t += dt)
            {
                at += new Vector3(0, 0, speed * dt);
                if (stride.Update(at, velocity, isGrounded: true, Facing).Stepped) steps++;
            }
            return steps;
        }

        int walking = Walk(PhysicsConstants.WalkSpeed, 4f);
        int running = Walk(PhysicsConstants.SprintSpeed, 4f);

        // Faster, but only just: sixty per cent more speed buys about a fifth more footfalls.
        Assert.True(running > walking, $"running made {running} steps against {walking} walking");
        Assert.True(running < walking * 1.4f,
            $"four seconds of running made {running} steps against {walking} walking — that is a "
          + "machine gun, not a cadence");

        // And nobody, at any speed this game can produce, steps faster than a human can.
        Assert.True(running / 4f < 4.5f, $"{running / 4f:F1} footfalls a second is not a body running");

        // So the same ground is fewer steps at a run.
        Assert.True(StrideAccumulator.StepLength(PhysicsConstants.SprintSpeed)
                  > StrideAccumulator.StepLength(PhysicsConstants.WalkSpeed) * 1.2f);
    }

    /// <summary>Alexander's gait curve gives the textbook answers for a human at the speeds humans are
    /// studied at.</summary>
    [Theory]
    [InlineData(1.4f, 0.60f, 0.80f, 1.7f, 2.4f)]    // a real walk: 70 cm steps, 2 a second
    [InlineData(4.5f, 1.20f, 1.55f, 2.8f, 3.8f)]    // a jog, which is what this game calls walking
    [InlineData(7.2f, 1.60f, 2.10f, 3.3f, 4.4f)]    // a hard run
    public void TheGaitMatchesRealBodies(float speed, float stepLo, float stepHi, float rateLo, float rateHi)
    {
        float step = StrideAccumulator.StepLength(speed);
        Assert.InRange(step, stepLo, stepHi);
        Assert.InRange(speed / step, rateLo, rateHi);
    }

    [Fact]
    public void RunningIsFasterThanWalkingOnBothSidesOfTheWire()
    {
        Assert.True(PhysicsConstants.SprintSpeed > PhysicsConstants.WalkSpeed);
        Assert.Equal(PhysicsConstants.WalkSpeed * PhysicsConstants.SprintMultiplier, PhysicsConstants.SprintSpeed, 4);
    }

    // ── Breathing ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Runs a body at one speed for a while and reports what its lungs did.</summary>
    private static (float Exertion, int Breaths, float LoudestDb) Work(Breathing lungs, float speed, float seconds)
    {
        const float dt = 1f / 30f;
        int breaths = 0;
        float loudest = 0f;
        for (float t = 0; t < seconds; t += dt)
        {
            if (lungs.Update(speed, dt, out var breath))
            {
                breaths++;
                loudest = MathF.Max(loudest, breath.LevelDb);
            }
        }
        return (lungs.Exertion, breaths, loudest);
    }

    [Fact]
    public void StandingStillIsNotWork()
    {
        var lungs = new Breathing();
        var (exertion, _, _) = Work(lungs, speed: 0f, seconds: 60f);
        Assert.InRange(exertion, 0f, 0.05f);
    }

    [Fact]
    public void ARestingBodyBreathesTooQuietlyToHear()
    {
        var lungs = new Breathing();
        var (_, breaths, _) = Work(lungs, speed: 0f, seconds: 60f);
        Assert.Equal(0, breaths);
    }

    [Fact]
    public void ARunGetsYouOutOfBreath()
    {
        var lungs = new Breathing();
        var (exertion, breaths, loudest) = Work(lungs, PhysicsConstants.SprintSpeed, seconds: 45f);

        Assert.True(exertion > 0.75f, $"forty-five seconds flat out only reached {exertion:F2}");
        Assert.True(breaths > 30, $"only {breaths} breaths in forty-five seconds of running");
        Assert.True(loudest > Breathing.AudibleFloorDb + 10f, $"loudest breath was only {loudest:F0} dB");
    }

    /// <summary>
    /// Getting your breath back takes longer than losing it, so a body that has been running is still
    /// findable well after it has stopped.
    /// </summary>
    [Fact]
    public void YouAreStillAudibleAfterYouStopRunning()
    {
        var lungs = new Breathing();
        Work(lungs, PhysicsConstants.SprintSpeed, seconds: 45f);
        float afterRunning = lungs.Exertion;

        // Fifteen seconds of standing perfectly still.
        var (exertion, breaths, _) = Work(lungs, speed: 0f, seconds: 15f);

        Assert.True(exertion > afterRunning * 0.5f,
            $"fifteen seconds of rest dropped exertion from {afterRunning:F2} to {exertion:F2}");
        Assert.True(breaths > 0, "a body that has just sprinted should still be breathing audibly");
    }

    [Fact]
    public void RecoveryIsSlowerThanOnset()
    {
        Assert.True(Breathing.RecoverySeconds > Breathing.OnsetSeconds);
    }

    [Fact]
    public void WalkingIsHalfTheWorkOfRunning()
    {
        var walking = new Breathing();
        var running = new Breathing();
        Work(walking, PhysicsConstants.WalkSpeed, seconds: 60f);
        Work(running, PhysicsConstants.SprintSpeed, seconds: 60f);

        Assert.True(running.Exertion > walking.Exertion * 1.3f,
            $"walking reached {walking.Exertion:F2} and running {running.Exertion:F2}");
    }

    /// <summary>Being carried fast is not running: demand is clamped at what the body itself can do.</summary>
    [Fact]
    public void BeingCarriedFasterThanYouCanRunIsNotRunning()
    {
        var carried = new Breathing();
        var running = new Breathing();
        Work(carried, PhysicsConstants.SprintSpeed * 10f, seconds: 60f);
        Work(running, PhysicsConstants.SprintSpeed, seconds: 60f);

        Assert.Equal(running.Exertion, carried.Exertion, 3);
    }
}

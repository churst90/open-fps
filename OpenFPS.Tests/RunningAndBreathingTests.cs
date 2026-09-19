using System;
using System.Numerics;
using OpenFPS.Client.Core;
using OpenFPS.Common;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// Running, and what it costs a body.
///
/// A run is not "walking, but sooner". It decides how often a body is heard, how far away, and — for
/// a good while after it stops — whether it is heard at all. These hold the two claims that make it
/// information rather than decoration: that a footfall is half a metre of GROUND and not a tick of
/// the clock, and that being out of breath outlasts the running by long enough to find somebody.
/// </summary>
public class RunningAndBreathingTests
{
    private static readonly Quaternion Facing = Quaternion.Identity;

    /// <summary>
    /// One step of ground is one footstep, and the answer does not depend on how often the
    /// accumulator is asked.
    ///
    /// The number of footsteps in ten metres is a property of the BODY and the speed, not of the
    /// renderer: a fast machine polling every five centimetres and a slow one polling every forty
    /// have to agree, or how somebody sounds depends on their frame rate.
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

        // Ten metres divided by the step this speed takes, plus the one that started the walk.
        int expected = (int)(10f / StrideAccumulator.StepLength(speed));
        Assert.InRange(steps, expected, expected + 2);
    }

    /// <summary>
    /// A faster body takes LONGER steps, so the same ground is FEWER footsteps — and the cadence
    /// barely moves.
    ///
    /// This is the opposite of what the model used to claim, and the old claim was audible: a
    /// constant half-metre stride made this game's walk nine footfalls a second and its sprint
    /// fourteen, reported from the chair as *"sounds like cockroaches running"*. A leg is a pendulum
    /// and it cannot be swung round faster than about four times a second by anybody, so past a walk
    /// the speed is bought with stride instead. See <see cref="StrideAccumulator.StepLength"/>.
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

        // ...which means the same ground is FEWER steps at a run, because each one is longer.
        Assert.True(StrideAccumulator.StepLength(PhysicsConstants.SprintSpeed)
                  > StrideAccumulator.StepLength(PhysicsConstants.WalkSpeed) * 1.2f);
    }

    /// <summary>
    /// The gait curve against the bodies it was measured on. Alexander's relation is not a fit to
    /// this game, it is the curve every legged animal that has been filmed lies on, so it has to give
    /// the textbook answers for a human at the speeds humans are studied at.
    /// </summary>
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
    /// Getting your breath back takes longer than losing it, and that asymmetry is the point: a body
    /// that has been running is still findable well after it has stopped and gone quiet.
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

    /// <summary>A body has not been running just because something carried it fast. Exertion is
    /// measured against what the body itself can do, and demand is clamped there.</summary>
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

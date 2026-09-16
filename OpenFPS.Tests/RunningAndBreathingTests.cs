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
    /// Half a metre of walking is one footstep, whatever speed it happened at.
    ///
    /// There used to be a cadence floor of five steps a second, which sounds generous until you
    /// notice a walk is 4.5 m/s — a footfall every 111 ms — so more than half of every walk was
    /// silent and a run lost seven in ten.
    /// </summary>
    [Theory]
    [InlineData(0.05f)]   // many small updates, as a fast renderer produces
    [InlineData(0.15f)]   // a walk at 60 Hz
    [InlineData(0.4f)]    // a hard run at 20 Hz
    public void EveryHalfMetreIsOneFootstep(float metresPerUpdate)
    {
        var stride = new StrideAccumulator();
        var velocity = new Vector3(0, 0, 5f);
        var at = Vector3.Zero;

        int steps = 0;
        int updates = (int)MathF.Round(10f / metresPerUpdate);   // ten metres of ground
        for (int i = 0; i < updates; i++)
        {
            at += new Vector3(0, 0, metresPerUpdate);
            if (stride.Update(at, velocity, isGrounded: true, Facing).Stepped) steps++;
        }

        // Twenty strides in ten metres, give or take where the last part-stride fell.
        Assert.InRange(steps, 19, 20);
    }

    /// <summary>The same ground is the same number of footsteps at any speed; the same TIME is not.
    /// That is the whole of what a listener hears in the difference.</summary>
    [Fact]
    public void ARunIsTheSameStepsOverGroundAndMoreStepsOverTime()
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

        Assert.True(running > walking * 1.4f,
            $"four seconds of running made {running} steps against {walking} walking");
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

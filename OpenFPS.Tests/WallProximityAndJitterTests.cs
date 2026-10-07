using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Client.Core;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Fmod;
using static OpenFPS.Common.SharedMovementEngine;

namespace OpenFPS.Tests;

/// <summary>
/// Pressing into a wall: footsteps that never stopped and pops while pushing were one bug.
/// `SharedMovementEngine.Step` pushed the original position out by the penetration measured at the
/// target, so the player went back about a step every tick and walked in again: no net movement, 3 m/s
/// of path, and the listener's region flipping at 15 Hz in a doorway.
/// </summary>
public class WallProximityAndJitterTests
{
    private const float Dt = 1f / 30f; // the real tick

    private static MovementContext Walking(Vector3 position, Vector3 inputDir) => new()
    {
        Position = position,
        Velocity = Vector3.Zero,
        InputDirection = inputDir,
        DeltaTime = Dt,
        GroundHeight = 0f,
        Gravity = PhysicsConstants.Gravity,
        JumpForce = PhysicsConstants.JumpPower,
        Speed = PhysicsConstants.WalkSpeed,
        PlayerRadius = PhysicsConstants.PlayerRadius,
        PlayerHeight = PhysicsConstants.PlayerHeight,
        StepHeight = PhysicsConstants.StepHeight,
        IsJumpRequested = false,
        MapMin = new Vector3(-50, -50, -50),
        MapMax = new Vector3(50, 50, 50),
    };

    /// <summary>A tall, wide wall lying across +Z at z = 5, far too high to step onto.</summary>
    private static Collider[] WallAtZ5() => new[]
    {
        new Collider
        {
            Position = new Vector3(0, 2f, 5f),
            Size = new Vector3(20f, 4f, 0.5f),
            Rotation = Quaternion.Identity,
            Material = "Concrete"
        }
    };

    [Fact]
    public void PressingIntoAWallComesToRest_RatherThanOscillating()
    {
        var colliders = WallAtZ5();
        var pos = new Vector3(0, 0, 3f);
        var vel = Vector3.Zero;
        var forward = new Vector3(0, 0, 1);

        // Walk up to the wall.
        for (int i = 0; i < 60; i++)
        {
            var ctx = Walking(pos, forward) with { Velocity = vel };
            (pos, vel, _) = Step(ctx, colliders);
        }

        // Pressing on: the position must stop changing, as the footsteps and the region lookup read it.
        var settled = pos;
        float worstStep = 0f;
        float pathLength = 0f;
        for (int i = 0; i < 60; i++)
        {
            var before = pos;
            var ctx = Walking(pos, forward) with { Velocity = vel };
            (pos, vel, _) = Step(ctx, colliders);

            float moved = new Vector3(pos.X - before.X, 0, pos.Z - before.Z).Length();
            pathLength += moved;
            worstStep = Math.Max(worstStep, moved);
        }

        Assert.True(worstStep < 0.005f,
            $"pressing into a wall still moves the player {worstStep:F4} m per tick (it should be still)");
        // Two seconds of pressing used to walk 3 m of path going nowhere: six footsteps.
        Assert.True(pathLength < 0.05f,
            $"two seconds of pressing accumulated {pathLength:F3} m of stride against a wall");
        Assert.True(Vector3.Distance(settled, pos) < 0.01f, "the resting position drifted while blocked");
    }

    [Fact]
    public void AWallStopsThePlayerAtItsFace_NotAStepShortOfIt()
    {
        var colliders = WallAtZ5();
        var pos = new Vector3(0, 0, 3f);
        var vel = Vector3.Zero;

        for (int i = 0; i < 90; i++)
        {
            var ctx = Walking(pos, new Vector3(0, 0, 1)) with { Velocity = vel };
            (pos, vel, _) = Step(ctx, colliders);
        }

        // The face is at z = 4.75; the cylinder rests a radius off it, give or take the skin width.
        float expected = 4.75f - PhysicsConstants.PlayerRadius;
        Assert.InRange(pos.Z, expected - 0.05f, expected + 0.01f);
    }

    [Fact]
    public void SlidingAlongAWallStillMoves()
    {
        // Pressing diagonally into a wall keeps the tangential component.
        var colliders = WallAtZ5();
        var pos = new Vector3(0, 0, 4f);
        var vel = Vector3.Zero;
        var diagonal = Vector3.Normalize(new Vector3(1, 0, 1));

        for (int i = 0; i < 30; i++)
        {
            var ctx = Walking(pos, diagonal) with { Velocity = vel };
            (pos, vel, _) = Step(ctx, colliders);
        }

        Assert.True(pos.X > 1.5f, $"sliding along the wall only travelled {pos.X:F2} m in X");
    }

    /// <summary>Being moved (spawn, correction, teleport) is not walking: the stride accumulator banked
    /// it as distance, and landing on a map played footsteps from a player who had touched no key.</summary>
    [Fact]
    public void ATeleportDoesNotSoundLikeFootsteps()
    {
        var state = new LocalPlayerState { IsGrounded = true, CurrentMaterial = "Concrete" };
        var controller = new LocalPlayerController(state);
        int steps = 0;
        controller.OnStepTriggered += (_, _, _, _) => steps++;

        var at = new Vector3(0, 0, 0);
        for (int i = 0; i < 5; i++) controller.Update(at, Vector3.Zero);
        Assert.Equal(0, steps);

        // Moved a long way repeatedly, standing still throughout.
        for (int i = 0; i < 20; i++)
        {
            at += new Vector3(0, 0, 7f);
            controller.Update(at, Vector3.Zero);
            System.Threading.Thread.Sleep(1);
        }

        Assert.Equal(0, steps);
    }

    /// <summary>
    /// A spawn reconciles in many corrections each under the one-metre teleport rule, together more than
    /// a stride, and was heard as a few footsteps petering out. The player's own velocity tells a
    /// correction from a stride: being dragged does not move your legs.
    /// </summary>
    [Fact]
    public void ASpawnReconciliationDoesNotSoundLikeFootsteps()
    {
        var state = new LocalPlayerState { IsGrounded = true, CurrentMaterial = "Concrete" };
        var controller = new LocalPlayerController(state);
        int steps = 0;
        controller.OnStepTriggered += (_, _, _, _) => steps++;

        var at = new Vector3(0, 0, 0);
        for (int i = 0; i < 5; i++) controller.Update(at, Vector3.Zero);

        // Thirty corrections of 30 cm, 9 m in all, standing still.
        for (int i = 0; i < 30; i++)
        {
            at += new Vector3(0, 0, 0.3f);
            controller.Update(at, Vector3.Zero);
            Thread.Sleep(1);
        }

        Assert.Equal(0, steps);
    }

    [Fact]
    public void AFootstepAccumulatorSeesNoStrideWhileBlocked()
    {
        // Through the controller that decides when a footstep plays.
        var state = new LocalPlayerState { IsGrounded = true, CurrentMaterial = "Concrete" };
        var controller = new LocalPlayerController(state);
        int steps = 0;
        controller.OnStepTriggered += (_, _, _, _) => steps++;

        var colliders = WallAtZ5();
        var pos = new Vector3(0, 0, 3f);
        var vel = Vector3.Zero;
        var forward = new Vector3(0, 0, 1);

        // Walk in (steps), then press for two seconds (none).
        for (int i = 0; i < 60; i++)
        {
            var ctx = Walking(pos, forward) with { Velocity = vel };
            (pos, vel, _) = Step(ctx, colliders);
            state.IsGrounded = true;
            controller.Update(pos, vel);
        }

        int stepsOnArrival = steps;
        Assert.True(stepsOnArrival > 0, "walking to the wall should have produced footsteps");

        for (int i = 0; i < 60; i++)
        {
            var ctx = Walking(pos, forward) with { Velocity = vel };
            (pos, vel, _) = Step(ctx, colliders);
            state.IsGrounded = true;
            controller.Update(pos, vel);
            Thread.Sleep(6); // real time, so the 200 ms footstep timer expires twice
        }

        Assert.Equal(stepsOnArrival, steps);
    }
}

/// <summary>
/// The near-field boundary: a surface near the head returns a copy 2d/c late, a comb with its first
/// notch at c/4d that slides up as you close in. Asserted on the rendered spectrum, not the parameters:
/// an old version had plausible parameters, a delay seven times too short and feedback that made a
/// fixed-pitch resonator.
/// </summary>
public class BoundaryReflectionTests
{
    private const int SampleRate = 44100;

    [Theory]
    [InlineData(0.25f)]
    [InlineData(0.5f)]
    [InlineData(1.0f)]
    [InlineData(2.0f)]
    public void TheDelayIsTheRoundTripToTheSurface(float distance)
    {
        var probe = new BoundaryProbe(Vector3.UnitX, distance, "Concrete");
        Assert.True(BoundaryModel.TryBuildTap(probe, AudioPhysics.SpeedOfSound, SampleRate, out var tap));

        float expected = 2f * distance / AudioPhysics.SpeedOfSound;
        // The far ear carries the round trip; the near one leads by up to the interaural delay.
        Assert.Equal(expected, tap.DelayRSeconds, 5);
        Assert.InRange(tap.DelayLSeconds, expected, expected + BoundaryModel.MaxInterauralDelay + 1e-6f);
    }

    [Fact]
    public void ASurfaceBeyondRangeProducesNoReflectionAtAll()
    {
        var probe = new BoundaryProbe(Vector3.UnitX, BoundaryModel.MaxDistance + 0.01f, "Concrete");
        Assert.False(BoundaryModel.TryBuildTap(probe, AudioPhysics.SpeedOfSound, SampleRate, out _));
    }

    [Fact]
    public void ASurfaceOnTheRightArrivesAtTheRightEarFirstAndLoudest()
    {
        Assert.True(BoundaryModel.TryBuildTap(
            new BoundaryProbe(Vector3.UnitX, 0.5f, "Concrete"), AudioPhysics.SpeedOfSound, SampleRate, out var right));
        Assert.True(BoundaryModel.TryBuildTap(
            new BoundaryProbe(-Vector3.UnitX, 0.5f, "Concrete"), AudioPhysics.SpeedOfSound, SampleRate, out var left));

        Assert.True(right.GainR > right.GainL, "a wall on the right should be louder in the right ear");
        Assert.True(right.DelayRSeconds < right.DelayLSeconds, "a wall on the right should reach the right ear first");
        Assert.Equal(right.GainR, left.GainL, 4);
        Assert.Equal(right.DelayRSeconds, left.DelayLSeconds, 6);
    }

    [Fact]
    public void ACarpetedWallReflectsLessAndDullerThanConcrete()
    {
        AcousticRegistry.EnsureInitialized();
        Assert.True(BoundaryModel.TryBuildTap(
            new BoundaryProbe(Vector3.UnitZ, 0.5f, "Concrete"), AudioPhysics.SpeedOfSound, SampleRate, out var hard));
        Assert.True(BoundaryModel.TryBuildTap(
            new BoundaryProbe(Vector3.UnitZ, 0.5f, "Carpet"), AudioPhysics.SpeedOfSound, SampleRate, out var soft));

        Assert.True(soft.GainL < hard.GainL, "carpet should reflect less energy than concrete");
        Assert.True(soft.LowpassAlpha < hard.LowpassAlpha, "carpet should reflect a duller sound than concrete");
    }

    [Fact]
    public void ColderAirMovesTheWholeCombDown()
    {
        // c = 331.3 sqrt(1 + T / 273.15) m/s falls in cold air, so the same round trip takes longer and
        // every notch of the comb, at (2k + 1) c / 2d, falls with it.
        var probe = new BoundaryProbe(Vector3.UnitZ, 1.0f, "Concrete");
        Assert.True(BoundaryModel.TryBuildTap(probe, AudioPhysics.SpeedOfSoundAt(-20f), SampleRate, out var cold));
        Assert.True(BoundaryModel.TryBuildTap(probe, AudioPhysics.SpeedOfSoundAt(40f), SampleRate, out var warm));
        Assert.True(cold.DelayRSeconds > warm.DelayRSeconds);
    }

    [Theory]
    [InlineData(0.4f)]
    [InlineData(0.8f)]
    [InlineData(1.5f)]
    public void TheRenderedSpectrumHasItsNotchWhereTheGeometrySaysItShould(float distance)
    {
        // The real processor's impulse response, measured; the old implementation's notch did not move.
        float notchHz = BoundaryModel.FirstNotchHz(distance);
        float peakHz = notchHz * 2f;   // first constructive peak: c/2d

        var (left, _) = RenderThroughBoundary(distance, "Concrete", Vector3.UnitZ);

        float atNotch = Magnitude(left, notchHz);
        float atPeak = Magnitude(left, peakHz);

        Assert.True(atNotch < atPeak * 0.6f,
            $"at {distance} m the notch ({notchHz:F0} Hz, {atNotch:F3}) should be well below the peak " +
            $"({peakHz:F0} Hz, {atPeak:F3})");
    }

    [Fact]
    public void MovingTheWallMovesTheNotchWithIt()
    {
        // A near wall's notch sits higher than a far one's; a fixed delay fails this.
        float near = 0.4f, far = 1.2f;
        var (nearBuf, _) = RenderThroughBoundary(near, "Concrete", Vector3.UnitZ);
        var (farBuf, _) = RenderThroughBoundary(far, "Concrete", Vector3.UnitZ);

        float farNotch = BoundaryModel.FirstNotchHz(far);
        Assert.True(Magnitude(nearBuf, farNotch) > Magnitude(farBuf, farNotch) * 1.5f,
            "a wall at 0.4 m and a wall at 1.2 m are producing the same notch");
    }

    [Fact]
    public void NoNearbySurfaceLeavesTheSignalUntouched()
    {
        var state = new BoundaryVoiceState(SampleRate);
        var input = Constant(4096, 2, 0.3f);
        var output = new float[input.Length];
        BoundaryProximityProcessor.Process(state, input, output, 2, 2);

        for (int i = 0; i < input.Length; i++)
            Assert.Equal(input[i], output[i], 5);
    }

    [Fact]
    public void AWallAppearingDoesNotStepTheSignal()
    {
        // The reflection fades in: a gain jumping from 0 to its target between blocks is a click.
        var state = new BoundaryVoiceState(SampleRate);
        var probe = new BoundaryProbe(Vector3.UnitZ, 0.4f, "Concrete");
        Assert.True(BoundaryModel.TryBuildTap(probe, AudioPhysics.SpeedOfSound, SampleRate, out var tap));

        // Prime the delay line so the reflection has something to return.
        var warm = Constant(8192, 2, 0.25f);
        var warmOut = new float[warm.Length];
        BoundaryProximityProcessor.Process(state, warm, warmOut, 2, 2);

        // The wall appears at full strength in one update.
        state.TargetDelayL[0] = tap.DelayLSeconds;
        state.TargetDelayR[0] = tap.DelayRSeconds;
        state.TargetGainL[0] = tap.GainL;
        state.TargetGainR[0] = tap.GainR;
        state.LowpassAlpha[0] = tap.LowpassAlpha;

        var input = Constant(2048, 2, 0.25f);
        var output = new float[input.Length];
        BoundaryProximityProcessor.Process(state, input, output, 2, 2);

        float worstJump = 0f;
        for (int i = 1; i < output.Length / 2; i++)
            worstJump = Math.Max(worstJump, Math.Abs(output[i * 2] - output[(i - 1) * 2]));

        Assert.True(worstJump < 0.01f, $"the reflection faded in with a {worstJump:F4} step");
    }

    // ── Harness ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>The processor's impulse response with one surface at the distance, the glide settled. An
    /// impulse rather than noise, so the notch and peak carry no estimator variance.</summary>
    private static (float[] Left, float[] Right) RenderThroughBoundary(float distance, string material, Vector3 direction)
    {
        AcousticRegistry.EnsureInitialized();
        var state = new BoundaryVoiceState(SampleRate);
        Assert.True(BoundaryModel.TryBuildTap(new BoundaryProbe(direction, distance, material),
            AudioPhysics.SpeedOfSound, SampleRate, out var tap));

        state.TargetDelayL[0] = tap.DelayLSeconds;
        state.TargetDelayR[0] = tap.DelayRSeconds;
        state.TargetGainL[0] = tap.GainL;
        state.TargetGainR[0] = tap.GainR;
        state.LowpassAlpha[0] = tap.LowpassAlpha;
        // Settled: a ramp would smear the spectrum.
        state.CurrentDelayL[0] = tap.DelayLSeconds;
        state.CurrentDelayR[0] = tap.DelayRSeconds;
        state.CurrentGainL[0] = tap.GainL;
        state.CurrentGainR[0] = tap.GainR;

        const int frames = 8192;
        var input = new float[frames * 2];
        input[0] = 1f; input[1] = 1f; // one sample, both ears
        var output = new float[input.Length];
        BoundaryProximityProcessor.Process(state, input, output, 2, 2);

        var left = new float[frames];
        var right = new float[frames];
        for (int i = 0; i < frames; i++)
        {
            left[i] = output[i * 2];
            right[i] = output[i * 2 + 1];
        }
        return (left, right);
    }

    /// <summary>The impulse response's DTFT magnitude at one frequency.</summary>
    private static float Magnitude(float[] impulseResponse, float frequencyHz)
    {
        double w = 2.0 * Math.PI * frequencyHz / SampleRate;
        double re = 0, im = 0;
        for (int i = 0; i < impulseResponse.Length; i++)
        {
            re += impulseResponse[i] * Math.Cos(w * i);
            im -= impulseResponse[i] * Math.Sin(w * i);
        }
        return (float)Math.Sqrt(re * re + im * im);
    }

    private static float[] Constant(int frames, int channels, float value)
    {
        var buf = new float[frames * channels];
        Array.Fill(buf, value);
        return buf;
    }
}

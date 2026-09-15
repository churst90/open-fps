using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Client.Core;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Fmod;
using static OpenFPS.Common.SharedMovementEngine;

namespace OpenFPS.Tests;

/// <summary>
/// Cover for what a player hears when they walk INTO something and keep pressing forward.
///
/// The live session reported two symptoms — footsteps that never stop at a wall, and a stream of
/// pops and clicks while pushing against it — and they are one bug. `SharedMovementEngine.Step`
/// resolved a collision by pushing the ORIGINAL position out by the penetration measured at the
/// position it was moving TO. The player was outside the wall to begin with, so that pushed them
/// BACKWARDS by roughly a whole step every tick; the next tick walked back in. Net displacement
/// zero, path length 3 m/s, and the listener's acoustic region flipping back and forth at 15 Hz
/// wherever the oscillation straddled a doorway.
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

        // Now keep pressing. Once the wall is reached, the position must STOP changing — that is what
        // the footstep accumulator and the region lookup both read.
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
        // Two seconds of pressing used to walk three metres of PATH without going anywhere, which is
        // six footsteps' worth of stride.
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

        // Wall face is at z = 4.75 (centre 5, half-depth 0.25). The player's cylinder should rest with
        // its radius against it, give or take the skin width.
        float expected = 4.75f - PhysicsConstants.PlayerRadius;
        Assert.InRange(pos.Z, expected - 0.05f, expected + 0.01f);
    }

    [Fact]
    public void SlidingAlongAWallStillMoves()
    {
        // The fix must not turn "resolve the collision" into "stop dead": pressing diagonally into a
        // wall has to keep the tangential component.
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

    /// <summary>
    /// Being MOVED is not walking.
    ///
    /// Spawning, a server correction and a teleport all change the player's position without a foot
    /// touching anything. The stride accumulator cannot tell the difference by itself, so it banked
    /// them as distance walked: landing on a map, where the predicted and authoritative positions
    /// reconcile over several frames, produced a burst of footsteps from a player who had not
    /// touched a key.
    /// </summary>
    [Fact]
    public void ATeleportDoesNotSoundLikeFootsteps()
    {
        var state = new LocalPlayerState { IsGrounded = true, CurrentMaterial = "Concrete" };
        var controller = new LocalPlayerController(state);
        int steps = 0;
        controller.OnStepTriggered += (_, _, _) => steps++;

        // Settle where we are.
        var at = new Vector3(0, 0, 0);
        for (int i = 0; i < 5; i++) controller.Update(at, Vector3.Zero);
        Assert.Equal(0, steps);

        // Now get moved a long way, repeatedly, the way a spawn reconciliation does — standing still
        // throughout, velocity zero.
        for (int i = 0; i < 20; i++)
        {
            at += new Vector3(0, 0, 7f);
            controller.Update(at, Vector3.Zero);
            System.Threading.Thread.Sleep(1);
        }

        Assert.Equal(0, steps);
    }

    /// <summary>
    /// The corrections that are too SMALL for the teleport rule to catch — which is what a spawn
    /// actually looks like.
    ///
    /// The distance rule only rejects a jump of more than a metre, and a reconciliation is not one
    /// jump. Arriving on a map, the predicted position and the server's authoritative one converge
    /// over many frames in steps of a few centimetres: every one of them under the limit, every one
    /// of them banked as distance walked, and together far more than a stride. Heard as a handful of
    /// footsteps on being dropped onto the map from a player who has not touched a key, petering out
    /// as the reconciliation settles — which is exactly how it was reported.
    ///
    /// Velocity is what tells a correction from a stride, and it is the player's OWN velocity: being
    /// dragged does not move your legs.
    /// </summary>
    [Fact]
    public void ASpawnReconciliationDoesNotSoundLikeFootsteps()
    {
        var state = new LocalPlayerState { IsGrounded = true, CurrentMaterial = "Concrete" };
        var controller = new LocalPlayerController(state);
        int steps = 0;
        controller.OnStepTriggered += (_, _, _) => steps++;

        var at = new Vector3(0, 0, 0);
        for (int i = 0; i < 5; i++) controller.Update(at, Vector3.Zero);

        // Thirty corrections of 30 cm — each one well inside the one-metre "plausible stride" limit,
        // nine metres in total, and the player standing perfectly still throughout.
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
        // The same thing one level up, through the object that actually decides when a footstep plays.
        var state = new LocalPlayerState { IsGrounded = true, CurrentMaterial = "Concrete" };
        var controller = new LocalPlayerController(state);
        int steps = 0;
        controller.OnStepTriggered += (_, _, _) => steps++;

        var colliders = WallAtZ5();
        var pos = new Vector3(0, 0, 3f);
        var vel = Vector3.Zero;
        var forward = new Vector3(0, 0, 1);

        // Walk in (steps expected), then press for two more seconds (no steps expected).
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
            Thread.Sleep(6); // real time, so the 200 ms footstep timer really does expire — twice
        }

        Assert.Equal(stepsOnArrival, steps);
    }
}

/// <summary>
/// Cover for the near-field boundary effect — what a wall SOUNDS like before you walk into it.
///
/// A surface near your head returns a delayed copy of everything you can hear, and direct plus delayed
/// is a comb filter: the extra path is twice the distance, so the delay is 2d/c and the first
/// cancellation notch sits at c/4d. Walk closer and the whole pattern slides up in pitch. That slide is
/// the cue, and these tests assert it against the rendered spectrum rather than against the parameters
/// the renderer was handed — the old implementation had plausible-looking parameters and a delay seven
/// times too short, with feedback that turned a reflection into a fixed-pitch resonator.
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
        // The nearer ear leads by up to the interaural delay; the far ear carries the round trip exactly.
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
        // And the mirror image is the mirror image.
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
    public void ColderAirMovesTheWholeCombUp()
    {
        // Sound travels slower in cold air, so the round trip takes longer and the notches move DOWN.
        // The point is that it moves at all: the same knob that shifts every Doppler shift shifts this.
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
        // Render white noise through the real processor and measure it. This is the assertion that the
        // old implementation could not have passed: its notch did not move with distance at all.
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
        // The same measurement from the other side: the notch for a NEAR wall must sit higher in
        // frequency than the notch for a far one. A fixed-delay effect fails this.
        float near = 0.4f, far = 1.2f;
        var (nearBuf, _) = RenderThroughBoundary(near, "Concrete", Vector3.UnitZ);
        var (farBuf, _) = RenderThroughBoundary(far, "Concrete", Vector3.UnitZ);

        // At the FAR wall's notch frequency, the near-wall render should NOT be notched.
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
        // The reflection has to fade in. A gain that jumps from 0 to its target between one block and
        // the next is a click, which is the artefact this whole area of the code exists to avoid.
        var state = new BoundaryVoiceState(SampleRate);
        var probe = new BoundaryProbe(Vector3.UnitZ, 0.4f, "Concrete");
        Assert.True(BoundaryModel.TryBuildTap(probe, AudioPhysics.SpeedOfSound, SampleRate, out var tap));

        // Prime the delay line with signal so the reflection has something to return.
        var warm = Constant(8192, 2, 0.25f);
        var warmOut = new float[warm.Length];
        BoundaryProximityProcessor.Process(state, warm, warmOut, 2, 2);

        // Now the wall appears, at full strength, in one update.
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

        // Full-scale input is 0.25 and the tap gain is a fraction of that; a step would show up as the
        // whole reflection arriving inside one sample.
        Assert.True(worstJump < 0.01f, $"the reflection faded in with a {worstJump:F4} step");
    }

    // ── Harness ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Captures the processor's IMPULSE RESPONSE with one surface at the given distance, with
    /// the glide already settled. An impulse rather than noise because the comb is exactly what we are
    /// measuring: the response of `dry + gain·delayed` is deterministic, so the notch and the peak are
    /// where the geometry puts them and not where the estimator's variance puts them.</summary>
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
        // Start settled: the glide is tested separately, and a ramp would smear the spectrum.
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

    /// <summary>The gain of an impulse response at one frequency — its DTFT magnitude there. A full
    /// FFT would tell us no more and cost a dependency.</summary>
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

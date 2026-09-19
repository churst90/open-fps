using System;
using System.Numerics;
using OpenFPS.Client.Core;
using OpenFPS.Client.Core.Platform;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.Core.Session;
using OpenFPS.Common;
using OpenFPS.Common.Networking;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// One press of a movement key is one footstep.
///
/// Reported from play: *"each W A S D key press should hear a footstep, not every 2 or 3 presses"*.
/// Two separate things were eating them, and both are arithmetic rather than taste.
///
/// A tap moves the player for one 30 Hz tick at 4.5 m/s, which is fifteen centimetres. A footfall
/// was half a metre of ground counted from a standstill, so three or four taps were needed before
/// one sounded — and a tap shorter than the 33 ms between two input drains was pressed and released
/// before the tick ever looked at the keyboard, so it moved the player nowhere at all.
///
/// The fixes are in <see cref="StrideAccumulator"/> (a walk's first footfall is at the START of the
/// walk, because a body cannot cross half a metre without having already put a foot down) and in the
/// session's input gather (a key that went down and back up between two drains is still worth the one
/// tick of movement it asked for).
/// </summary>
public class FootstepPerPressTests
{
    private static readonly Quaternion Facing = Quaternion.Identity;

    /// <summary>How far one tick of held W moves a body: the tap the fault was reported about.</summary>
    private const float OneTickOfWalking = PhysicsConstants.WalkSpeed * PhysicsConstants.FixedDeltaTime;

    /// <summary>Taps the key: one tick moving, then long enough stopped for the feet to be together.</summary>
    private static int Tap(StrideAccumulator stride, ref Vector3 at)
    {
        int steps = 0;
        at += new Vector3(0, 0, OneTickOfWalking);
        if (stride.Update(at, new Vector3(0, 0, PhysicsConstants.WalkSpeed), true, Facing).Stepped) steps++;

        // Key up. The renderer keeps calling at its own rate with the body standing still.
        for (int i = 0; i < 5; i++)
            if (stride.Update(at, Vector3.Zero, true, Facing).Stepped) steps++;

        return steps;
    }

    [Fact]
    public void EveryTapIsExactlyOneFootstep()
    {
        var stride = new StrideAccumulator();
        var at = Vector3.Zero;

        // Fifteen centimetres a tap: before the stride was phased from the start of a walk, the first
        // several of these were silent and only the last sounded.
        Assert.True(OneTickOfWalking < StrideAccumulator.StepLength(PhysicsConstants.WalkSpeed) / 3f,
            $"a tap is {OneTickOfWalking:F2} m, which is no longer small against a step");

        for (int i = 1; i <= 8; i++)
            Assert.Equal(1, Tap(stride, ref at));
    }

    /// <summary>
    /// Holding the key is one footfall a STEP, and a step is as long as the speed makes it. Ten
    /// metres held down is the opening footfall and then one every 1.38 m.
    /// </summary>
    [Fact]
    public void HoldingTheKeyIsAFootfallEveryStep()
    {
        var stride = new StrideAccumulator();
        var at = Vector3.Zero;
        var velocity = new Vector3(0, 0, PhysicsConstants.WalkSpeed);

        int steps = 0;
        int ticks = (int)MathF.Round(10f / OneTickOfWalking);
        for (int i = 0; i < ticks; i++)
        {
            at += new Vector3(0, 0, OneTickOfWalking);
            if (stride.Update(at, velocity, true, Facing).Stepped) steps++;
        }

        float expected = 10f / StrideAccumulator.StepLength(PhysicsConstants.WalkSpeed);
        Assert.InRange(steps, (int)expected, (int)expected + 2);   // plus the one that started it
    }

    /// <summary>
    /// A body being CARRIED still makes no step when it starts, which is the rule the opening
    /// footfall must not break: a correction, a spawn slide or a car all move a body whose own
    /// velocity is zero, and zero is not walking however far the position jumps.
    /// </summary>
    [Fact]
    public void BeingMovedStillStartsNoWalk()
    {
        var stride = new StrideAccumulator();
        var at = Vector3.Zero;

        for (int i = 0; i < 40; i++)
        {
            at += new Vector3(0, 0, 0.2f);   // a reconciliation converging, or a seat travelling
            Assert.False(stride.Update(at, Vector3.Zero, true, Facing).Stepped);
        }
    }

    /// <summary>
    /// A velocity sitting on the walking threshold does not restart the walk over and over.
    ///
    /// The opening footfall is a latch, and a latch that reads one number both ways chatters: a remote
    /// body's velocity arrives a server tick at a time and can straddle the threshold for as long as
    /// it likes. It has to fall to a stop before the feet are together again.
    /// </summary>
    [Fact]
    public void HoveringOnTheWalkingThresholdIsNotAStreamOfFirstSteps()
    {
        var stride = new StrideAccumulator();
        var at = Vector3.Zero;
        int steps = 0;

        for (int i = 0; i < 60; i++)
        {
            // Either side of MinStrideSpeed, alternating, and barely moving over the ground.
            float speed = (i % 2 == 0) ? StrideAccumulator.MinStrideSpeed + 0.01f
                                       : StrideAccumulator.MinStrideSpeed - 0.01f;
            at += new Vector3(0, 0, speed / 60f);
            if (stride.Update(at, new Vector3(0, 0, speed), true, Facing).Stepped) steps++;
        }

        Assert.Equal(1, steps);   // the one that started it, and no more
    }

    /// <summary>
    /// Landing is itself a foot going down, so it does not sound beside a first step.
    ///
    /// Jumping from a standstill and steering in the air lands a body that is moving and whose feet
    /// were never together on the ground — the one case where "it started walking" and "it landed"
    /// fall on the same update.
    /// </summary>
    [Fact]
    public void LandingWhileMovingIsOneSoundAndNotTwo()
    {
        var stride = new StrideAccumulator();
        var at = Vector3.Zero;

        stride.Update(at, Vector3.Zero, isGrounded: true, Facing);

        // A body in the air is FALLING — a jump reaches about five metres a second on the way back
        // down — and that is what makes arriving a landing rather than a blip in the floor.
        var airborne = new Vector3(0, -5f, PhysicsConstants.WalkSpeed);
        for (int i = 0; i < 10; i++)
        {
            at += airborne * PhysicsConstants.FixedDeltaTime;
            stride.Update(at, airborne, isGrounded: false, Facing);
        }

        at += new Vector3(0, 0, PhysicsConstants.WalkSpeed) * PhysicsConstants.FixedDeltaTime;
        var landing = stride.Update(at, new Vector3(0, 0, PhysicsConstants.WalkSpeed), isGrounded: true, Facing);

        Assert.True(landing.Landed);
        Assert.False(landing.Stepped);
    }

    // ── The other half: a press the tick never saw ──────────────────────────────────────────────

    /// <summary>
    /// A key pressed and released between two input drains still moves the player one tick.
    ///
    /// Held state is sampled once per fixed step. A tap shorter than 33 ms was over before the step
    /// looked, so the press produced no movement, no footstep and no packet — the player pressed a
    /// key and the world did not answer.
    /// </summary>
    [Fact]
    public void AKeyTappedBetweenTwoTicksStillMovesThePlayer()
    {
        var session = NewSession();
        session.HandleMessage(new PlayerSpawned { EntityId = 1 });
        var from = session.PlayerState.Position;

        // Down and up with no tick in between: exactly what a quick tap looks like to the buffer.
        session.Input.SetKey(GameKey.W, true);
        session.Input.SetKey(GameKey.W, false);

        session.SimStep(PhysicsConstants.FixedDeltaTime);

        var moved = session.PlayerState.Position - from;
        Assert.True(new Vector2(moved.X, moved.Z).Length() > OneTickOfWalking * 0.5f,
            $"a tap moved the player {moved} — the press was dropped between two drains");
    }

    /// <summary>The same tap is worth exactly one tick and is not paid twice.</summary>
    [Fact]
    public void ATapIsPaidOnce()
    {
        var session = NewSession();
        session.HandleMessage(new PlayerSpawned { EntityId = 1 });
        var from = session.PlayerState.Position;

        session.Input.SetKey(GameKey.W, true);
        session.Input.SetKey(GameKey.W, false);

        for (int i = 0; i < 5; i++) session.SimStep(PhysicsConstants.FixedDeltaTime);

        var moved = session.PlayerState.Position - from;
        Assert.True(new Vector2(moved.X, moved.Z).Length() < OneTickOfWalking * 1.5f,
            $"one tap moved the player {moved}, which is more than the one tick it asked for");
    }

    private sealed class FakeSpeech : ISpeechOutput
    {
        public string BackendName => "fake";
        public bool Initialize() => true;
        public void Speak(string text, bool interrupt = true) { }
        public void Interrupt() { }
        public void Dispose() { }
    }

    private sealed class FakeShell : IClientShell
    {
        public bool IsGameInputActive { get; set; } = true;
        public event Action<string>? CommandEntered;
        public void ShowLoading(string status) { }
        public void UpdateLoadingStatus(string text, int percent) { }
        public void EnterGame() { }
        public void OpenCommandConsole() { }
        public void RequestQuit() { }
    }

    private static ClientGameSession NewSession()
    {
        AcousticRegistry.Initialize();
        return new ClientGameSession(
            new ClientNetworkService(), new FakeSpeech(), new FakeShell(), new AudioEngineFacade(),
            microphone: new NullMicrophoneCapture("no microphone here"),
            enableAudio: false);
    }
}

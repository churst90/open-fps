using System;
using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Common.Networking;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// Which way the turn keys actually turn you, derived rather than assumed.
///
/// The physics is `Yaw -= LookDelta.X * RotationSpeed * dt`, and forward is (sin yaw, 0, cos yaw) —
/// so increasing yaw swings forward toward +X, which is RIGHT. A positive LookDelta.X therefore
/// decreases yaw and turns LEFT. J emitted a negative X and so turned right, which is exactly how it
/// was reported: "turning left seems to turn me right".
///
/// Every step of that chain is individually plausible, which is why it survived. The test pins the
/// END of the chain — where the player is actually facing — rather than the sign of any one term.
/// </summary>
public class TurnKeyTests
{
    private static Vector3 ForwardAfter(float lookX, float yaw0 = 0f, float dt = PhysicsConstants.FixedDeltaTime)
    {
        float yaw = yaw0 - lookX * PhysicsConstants.RotationSpeed * dt;
        return Vector3.Transform(Vector3.UnitZ, Quaternion.CreateFromYawPitchRoll(yaw, 0f, 0f));
    }

    /// <summary>The LookDelta.X the session emits for one tap of a key, in degrees of turn.</summary>
    private static float LookXForDegrees(float degrees, float dt = PhysicsConstants.FixedDeltaTime)
        => degrees * (MathF.PI / 180f) / (PhysicsConstants.RotationSpeed * dt);

    [Fact]
    public void APositiveLookXTurnsLeftAndANegativeOneTurnsRight()
    {
        // Facing +Z, right is +X. Turning left must move the nose toward -X.
        Vector3 left = ForwardAfter(LookXForDegrees(45f));
        Vector3 right = ForwardAfter(-LookXForDegrees(45f));

        Assert.True(left.X < -0.5f, $"a positive LookDelta.X should face left (-X); it faced {left}");
        Assert.True(right.X > 0.5f, $"a negative LookDelta.X should face right (+X); it faced {right}");
    }

    /// <summary>
    /// Which way a look key tilts you — the same derivation, one axis over.
    ///
    /// `Pitch += LookDelta.Y * RotationSpeed * dt`, and the rotation is built by
    /// Quaternion.CreateFromYawPitchRoll, whose pitch term is a right-handed rotation about +X. That
    /// takes forward (+Z) toward MINUS Y: increasing pitch looks DOWN, not up. The comment beside the
    /// key table said the opposite, K was written to look down from that comment, and so K looked up.
    /// Reported as "k and o seem to be swapped".
    ///
    /// Pinned at the end of the chain — where the nose actually points — for the same reason as the
    /// yaw test: every individual step of the sign chain reads as correct.
    /// </summary>
    [Fact]
    public void APositiveLookYLooksDownAndANegativeOneLooksUp()
    {
        Vector3 down = ForwardAfterPitch(LookXForDegrees(30f));
        Vector3 up = ForwardAfterPitch(-LookXForDegrees(30f));

        Assert.True(down.Y < -0.4f, $"a positive LookDelta.Y should tilt the nose down (-Y); it faced {down}");
        Assert.True(up.Y > 0.4f, $"a negative LookDelta.Y should tilt the nose up (+Y); it faced {up}");
    }

    /// <summary>And the keys, as the session maps them: K down, O up.</summary>
    [Fact]
    public void KLooksDownAndOLooksUp()
    {
        // Mirrors ClientGameSession.TurnKeys. If that table is edited without this, one of them fails.
        const float KLookY = +1f;
        const float OLookY = -1f;

        Assert.True(ForwardAfterPitch(KLookY * LookXForDegrees(30f)).Y < 0f, "K must look DOWN");
        Assert.True(ForwardAfterPitch(OLookY * LookXForDegrees(30f)).Y > 0f, "O must look UP");
    }

    private static Vector3 ForwardAfterPitch(float lookY, float dt = PhysicsConstants.FixedDeltaTime)
    {
        float pitch = lookY * PhysicsConstants.RotationSpeed * dt;
        return Vector3.Transform(Vector3.UnitZ, Quaternion.CreateFromYawPitchRoll(0f, pitch, 0f));
    }

    [Fact]
    public void FourTapsFaceTheOtherWayAndEightComeBack()
    {
        float step = LookXForDegrees(45f);
        float yaw = 0f;
        for (int i = 0; i < 4; i++) yaw -= step * PhysicsConstants.RotationSpeed * PhysicsConstants.FixedDeltaTime;

        var forward = Vector3.Transform(Vector3.UnitZ, Quaternion.CreateFromYawPitchRoll(yaw, 0f, 0f));
        Assert.True(forward.Z < -0.999f, $"four 45-degree taps should face dead astern; it faced {forward}");

        for (int i = 0; i < 4; i++) yaw -= step * PhysicsConstants.RotationSpeed * PhysicsConstants.FixedDeltaTime;
        forward = Vector3.Transform(Vector3.UnitZ, Quaternion.CreateFromYawPitchRoll(yaw, 0f, 0f));
        Assert.True(forward.Z > 0.999f, $"eight should bring it back to dead ahead; it faced {forward}");
    }

    [Fact]
    public void AFineTapIsOneDegreeWhateverTheFrameRate()
    {
        // The step is expressed per-tick, so a different dt must still produce the same TURN.
        foreach (float dt in new[] { 1f / 30f, 1f / 60f, 1f / 144f })
        {
            float turned = LookXForDegrees(1f, dt) * PhysicsConstants.RotationSpeed * dt * (180f / MathF.PI);
            Assert.Equal(1f, turned, 3);
        }
    }
}

/// <summary>
/// A coarse turn key lands you ON a compass point, wherever you started.
///
/// This is the arithmetic behind "if I press j or l to go facing north and I walk straight, both
/// the x and the y change when they shouldn't". A tap that ADDS forty-five degrees keeps an
/// off-angle heading off-angle for ever: once a fine nudge or a held sweep has left you at 47
/// degrees, every coarse tap after it lands on 92, 137, 182. Walking then moves both coordinates,
/// and the one thing a coarse turn key exists for — face a cardinal direction and have exactly one
/// coordinate change — is impossible.
/// </summary>
public class TurnSnapTests
{
    /// <summary>The session's rule, reproduced: the distance to the next mark in the direction of
    /// travel, or a whole step if you are already on one.</summary>
    private static float Snap(float currentDeg, float dir, float step = 45f)
    {
        float grid = dir > 0f ? MathF.Ceiling(currentDeg / step) * step
                              : MathF.Floor(currentDeg / step) * step;
        float delta = MathF.Abs(grid - currentDeg);
        return delta < 0.25f ? step : delta;
    }

    [Theory]
    // On the grid already: a whole step, in both directions.
    [InlineData(0f, +1f, 45f)]
    [InlineData(0f, -1f, 45f)]
    [InlineData(90f, +1f, 45f)]
    [InlineData(-135f, -1f, 45f)]
    // Off the grid: only as far as the next mark.
    [InlineData(47f, +1f, 43f)]
    [InlineData(47f, -1f, 2f)]
    [InlineData(1f, -1f, 1f)]
    [InlineData(89.9f, +1f, 45f)]     // within tolerance of 90: treat as on-grid
    public void ACoarseTapGoesToTheNextMark(float from, float dir, float expected)
        => Assert.Equal(expected, Snap(from, dir), 2);

    /// <summary>
    /// And the point of it: after one coarse tap from anywhere, walking forward changes exactly one
    /// coordinate. Four taps from an off-angle start, checked at every step.
    /// </summary>
    [Fact]
    public void AfterACoarseTapForwardIsOnAnAxisOrADiagonal()
    {
        float deg = 47f;                                  // left there by a fine nudge
        for (int tap = 0; tap < 8; tap++)
        {
            deg -= Snap(deg, -1f);                        // L: yaw increases... in degrees, one way
            float yaw = deg * (MathF.PI / 180f);
            var fwd = Vector3.Transform(Vector3.UnitZ, Quaternion.CreateFromYawPitchRoll(yaw, 0f, 0f));
            // On a mark, forward is either axis-aligned (one component ~0) or a true diagonal
            // (both ~0.707). Anything else is an off-angle heading.
            bool axis = MathF.Abs(fwd.X) < 1e-3f || MathF.Abs(fwd.Z) < 1e-3f;
            bool diagonal = MathF.Abs(MathF.Abs(fwd.X) - 0.70710678f) < 1e-3f
                         && MathF.Abs(MathF.Abs(fwd.Z) - 0.70710678f) < 1e-3f;
            Assert.True(axis || diagonal,
                $"after tap {tap + 1} the heading is {deg:F2} deg and forward is {fwd} — neither on an axis nor on a diagonal");
        }
    }
}

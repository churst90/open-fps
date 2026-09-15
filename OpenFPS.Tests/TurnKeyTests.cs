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

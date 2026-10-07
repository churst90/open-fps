using System.Numerics;
using OpenFPS.Common;

namespace OpenFPS.Tests;

/// <summary>
/// A body walking off a kerb does not leave the ground: the floor may be a full step below and still be the
/// floor, unless the body is already moving vertically. A 12 cm lip put the body in the air for two ticks and
/// landed it with a heavy step sound, and standing on one, reconciliation's jitter flipped the five-point
/// ground probe: "walk a few steps, stop, and for like 10 seconds, periodic bangs".
/// </summary>
public class StepDownTests
{
    private static SharedMovementEngine.MovementContext Walking(Vector3 at, Vector3 velocity, float groundHeight)
        => new()
        {
            Position = at,
            Velocity = velocity,
            InputDirection = new Vector3(1, 0, 0),
            DeltaTime = PhysicsConstants.FixedDeltaTime,
            GroundHeight = groundHeight,
            Gravity = PhysicsConstants.Gravity,
            JumpForce = PhysicsConstants.JumpPower,
            Speed = PhysicsConstants.WalkSpeed,
            PlayerRadius = PhysicsConstants.PlayerRadius,
            PlayerHeight = PhysicsConstants.PlayerHeight,
            StepHeight = PhysicsConstants.StepHeight,
            MapMin = new Vector3(-500, -100, -500),
            MapMax = new Vector3(500, 100, 500),
        };

    /// <summary>Nothing in the way: these are about the floor, not walls.</summary>
    private static readonly SharedMovementEngine.Collider[] Nothing = Array.Empty<SharedMovementEngine.Collider>();

    /// <summary>The one that was reported: a pavement's edge, twelve centimetres.</summary>
    [Theory]
    [InlineData(0.02f)]   // a threshold
    [InlineData(0.12f)]   // a kerb — the city map's own lip
    [InlineData(0.30f)]   // a deep step
    [InlineData(0.39f)]   // just inside a full step
    public void WalkingOffALipKeepsYouOnTheGround(float drop)
    {
        var ctx = Walking(new Vector3(0, 0, 0), Vector3.Zero, groundHeight: -drop);
        var (pos, vel, grounded) = SharedMovementEngine.Step(ctx, Nothing);

        Assert.True(grounded, $"a {drop * 100f:F0} cm drop put the body in the air");
        Assert.Equal(-drop, pos.Y, 3);          // it followed the floor down
        Assert.Equal(0f, vel.Y, 3);             // ...without falling
    }

    /// <summary>A real drop is still a real drop: past a step the body falls, as off a roof or a platform.</summary>
    [Theory]
    [InlineData(0.6f)]
    [InlineData(2.5f)]
    [InlineData(12f)]
    public void WalkingOffSomethingTallerThanAStepIsStillAFall(float drop)
    {
        var ctx = Walking(new Vector3(0, 0, 0), Vector3.Zero, groundHeight: -drop);
        var (pos, vel, grounded) = SharedMovementEngine.Step(ctx, Nothing);

        Assert.False(grounded, $"a {drop:F1} m drop was treated as a step down");
        Assert.True(vel.Y < 0f, "the body should be falling");
        Assert.True(pos.Y > -drop, "it should not have been snapped to the bottom");
    }

    /// <summary>A body already moving vertically keeps the tight tolerance: a jump leaves the ground on its
    /// tick, and a fall is not caught by a floor a step below.</summary>
    [Fact]
    public void JumpingAndFallingAreUntouched()
    {
        var jump = Walking(new Vector3(0, 0, 0), Vector3.Zero, groundHeight: 0f);
        jump.IsJumpRequested = true;
        var jumped = SharedMovementEngine.Step(jump, Nothing);
        Assert.False(jumped.IsGrounded);
        Assert.True(jumped.NewVelocity.Y > 0f);

        // Falling past a ledge a step below: it keeps falling rather than being snatched onto it.
        var falling = Walking(new Vector3(0, 0, 0), new Vector3(0, -4f, 0), groundHeight: -0.35f);
        var fell = SharedMovementEngine.Step(falling, Nothing);
        Assert.False(fell.IsGrounded, "a falling body was caught by a floor a step below it");
    }

    /// <summary>Stepping down puts the body on the new floor, not hovering or below it, so the listener's ears
    /// are where the geometry says.</summary>
    [Fact]
    public void AfterSteppingDownTheBodyIsOnTheFloorItSteppedTo()
    {
        var ctx = Walking(new Vector3(0, 0, 0), Vector3.Zero, groundHeight: -0.12f);
        var first = SharedMovementEngine.Step(ctx, Nothing);
        Assert.Equal(-0.12f, first.NewPosition.Y, 3);

        // ...and it stays there, rather than sinking a step each tick.
        var next = Walking(first.NewPosition, first.NewVelocity, groundHeight: -0.12f);
        var second = SharedMovementEngine.Step(next, Nothing);
        Assert.Equal(-0.12f, second.NewPosition.Y, 3);
        Assert.True(second.IsGrounded);
    }
}

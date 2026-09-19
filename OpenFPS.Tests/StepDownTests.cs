using System;
using System.Numerics;
using OpenFPS.Common;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// A body walking off a kerb does not leave the ground.
///
/// <see cref="SharedMovementEngine"/> has always been able to step UP: walk into something no taller
/// than <see cref="PhysicsConstants.StepHeight"/> and the body is lifted onto it. There was no
/// matching allowance going DOWN, and that asymmetry was audible rather than visible. A lip of twelve
/// centimetres — the edge of a pavement, the end of a road surface — put the body in the air for two
/// ticks and then LANDED it, and a landing is a heavy sound played out of the footstep bank.
///
/// Worse where you stop on one. The ground probe samples five points around the feet, so standing on
/// a lip it straddles the edge, and any jitter in the position — server reconciliation keeps nudging
/// it after you stop — flips the answer, drops the body, and lands it again. Gated to one landing
/// every half second, that is a bang every half second for as long as you stand there. Reported from
/// the chair as "walk a few steps, stop, and for like 10 seconds, periodic bangs".
///
/// So the floor may now be a full step BELOW you and still be the floor you are on — but only if you
/// are not already going up or down, so that walking off a roof is still walking off a roof.
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

    /// <summary>Nothing in the way — these are about the FLOOR, not about walls.</summary>
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

    /// <summary>
    /// And a real drop is still a real drop. Past a step, the body leaves the ground and falls, which
    /// is what walking off a roof, a platform edge or a garage deck has to do.
    /// </summary>
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

    /// <summary>
    /// A body already moving vertically keeps the old, tight tolerance. A jump must leave the ground
    /// on the tick it is asked for, and a body in mid-fall must not be caught by a floor it is still
    /// a step above — the step down is for walking, and only for walking.
    /// </summary>
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

    /// <summary>
    /// Stepping down is not free height: the body ends up ON the new floor, not hovering over it and
    /// not below it. Held because "grounded" and "at the ground" are two different claims and only
    /// the second one keeps the listener's ears where the geometry says they are.
    /// </summary>
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

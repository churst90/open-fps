using System.Numerics;
using OpenFPS.Common;
using static OpenFPS.Common.SharedMovementEngine;

namespace OpenFPS.Tests;

/// <summary>
/// Unit tests for the SharedMovementEngine — the deterministic physics kernel shared
/// between client prediction and server authoritative simulation.
/// </summary>
public class SharedMovementEngineTests
{
    // ─── Helpers ────────────────────────────────────────────────────────────────

    private static MovementContext DefaultContext(Vector3 position, Vector3 inputDir = default) => new()
    {
        Position      = position,
        Velocity      = Vector3.Zero,
        InputDirection = inputDir,
        DeltaTime     = 0.05f,
        GroundHeight  = 0f,
        Gravity       = PhysicsConstants.Gravity,
        JumpForce     = PhysicsConstants.JumpPower,
        Speed         = PhysicsConstants.WalkSpeed,
        PlayerRadius  = PhysicsConstants.PlayerRadius,
        PlayerHeight  = PhysicsConstants.PlayerHeight,
        StepHeight    = PhysicsConstants.StepHeight,
        IsJumpRequested = false,
        MapMin        = new Vector3(-50, -50, -50),
        MapMax        = new Vector3( 50,  50,  50),
    };

    private static ReadOnlySpan<Collider> NoColliders() => ReadOnlySpan<Collider>.Empty;

    // ─── Grounding ───────────────────────────────────────────────────────────────

    [Fact]
    public void PlayerOnGround_WhenAtGroundHeight_IsGrounded()
    {
        var ctx = DefaultContext(new Vector3(0, 0, 0));
        var (_, _, isGrounded) = Step(ctx, NoColliders());
        Assert.True(isGrounded);
    }

    [Fact]
    public void PlayerAboveGround_WhenInAir_IsNotGrounded()
    {
        var ctx = DefaultContext(new Vector3(0, 5, 0));    // 5m above ground
        var (_, _, isGrounded) = Step(ctx, NoColliders());
        Assert.False(isGrounded);
    }

    // ─── Gravity ─────────────────────────────────────────────────────────────────

    [Fact]
    public void FallingPlayer_AccumulatesNegativeVerticalVelocity()
    {
        var ctx = DefaultContext(new Vector3(0, 5, 0));    // In the air, no ground
        ctx = ctx with { GroundHeight = PhysicsConstants.DefaultGroundCheckLimit - 1 }; // No valid ground
        var (_, vel, _) = Step(ctx, NoColliders());
        Assert.True(vel.Y < 0, $"Expected downward velocity but got {vel.Y}");
    }

    [Fact]
    public void GroundedPlayer_DoesNotAccumulateGravity()
    {
        var ctx = DefaultContext(new Vector3(0, 0, 0));    // On the ground
        var (_, vel, _) = Step(ctx, NoColliders());
        Assert.True(vel.Y >= 0, $"Expected zero/positive Y velocity on ground but got {vel.Y}");
    }

    // ─── Jumping ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Jump_WhenGrounded_AppliesUpwardVelocity()
    {
        var ctx = DefaultContext(new Vector3(0, 0, 0)) with { IsJumpRequested = true };
        var (_, vel, isGrounded) = Step(ctx, NoColliders());
        Assert.True(vel.Y > 0, $"Expected upward velocity after jump but got {vel.Y}");
        Assert.False(isGrounded, "Should not be grounded immediately after a jump");
    }

    [Fact]
    public void Jump_WhenInAir_DoesNotDoubleJump()
    {
        // Player in the air (5m up, no valid ground)
        var ctx = DefaultContext(new Vector3(0, 5, 0)) with
        {
            IsJumpRequested = true,
            GroundHeight = PhysicsConstants.DefaultGroundCheckLimit - 1
        };
        var (_, vel, _) = Step(ctx, NoColliders());
        // Velocity should be negative (falling), not positive (jumping)
        Assert.True(vel.Y < 0, "Should not be able to jump in mid-air");
    }

    // ─── Horizontal Movement ─────────────────────────────────────────────────────

    [Fact]
    public void Movement_WithForwardInput_MovesPositiveZ()
    {
        var ctx = DefaultContext(new Vector3(0, 0, 0), inputDir: new Vector3(0, 0, 1));
        var (newPos, _, _) = Step(ctx, NoColliders());
        Assert.True(newPos.Z > 0, $"Expected forward movement but got Z={newPos.Z}");
    }

    [Fact]
    public void Movement_WithNoInput_DoesNotMoveHorizontally()
    {
        var ctx = DefaultContext(new Vector3(0, 0, 0), inputDir: Vector3.Zero);
        var (newPos, _, _) = Step(ctx, NoColliders());
        Assert.Equal(0f, newPos.X, precision: 4);
        Assert.Equal(0f, newPos.Z, precision: 4);
    }

    [Fact]
    public void Movement_Speed_MatchesWalkSpeedOverDeltaTime()
    {
        var ctx = DefaultContext(new Vector3(0, 0, 0), inputDir: new Vector3(1, 0, 0));
        float expected = PhysicsConstants.WalkSpeed * ctx.DeltaTime;
        var (newPos, _, _) = Step(ctx, NoColliders());
        Assert.Equal(expected, newPos.X, precision: 3);
    }

    // ─── Map Boundary Clamping ────────────────────────────────────────────────────

    [Fact]
    public void MapBoundary_PlayerAtEdge_ClampedToRadius()
    {
        // Position the player at the positive X boundary of the map
        float edgeX = 50f;
        var ctx = DefaultContext(new Vector3(edgeX, 0, 0), inputDir: new Vector3(1, 0, 0));
        var (newPos, vel, _) = Step(ctx, NoColliders());

        float maxX = ctx.MapMax.X - ctx.PlayerRadius;
        Assert.True(newPos.X <= maxX + 0.001f, $"Player X={newPos.X} should be <= {maxX}");
        Assert.Equal(0f, vel.X, precision: 4);
    }

    [Fact]
    public void MapBoundary_NegativeX_ClampedToMinRadius()
    {
        float edgeX = -50f;
        var ctx = DefaultContext(new Vector3(edgeX, 0, 0), inputDir: new Vector3(-1, 0, 0));
        var (newPos, vel, _) = Step(ctx, NoColliders());

        float minX = ctx.MapMin.X + ctx.PlayerRadius;
        Assert.True(newPos.X >= minX - 0.001f, $"Player X={newPos.X} should be >= {minX}");
        Assert.Equal(0f, vel.X, precision: 4);
    }

    // ─── Collision Resolution ─────────────────────────────────────────────────────

    [Fact]
    public void Collision_WithSolidWall_PlayerDoesNotPassThrough()
    {
        // Player at origin moving toward a wall at X=1
        var ctx = DefaultContext(new Vector3(0, 0, 0), inputDir: new Vector3(1, 0, 0));
        var wall = new Collider
        {
            Position = new Vector3(1f, 0.5f, 0f),
            Size     = new Vector3(0.5f, 2f, 4f),
            Rotation = Quaternion.Identity,
            Material = "Concrete"
        };

        var (newPos, _, _) = Step(ctx, new[] { wall });

        // Player should not have crossed x=0.7 (1m wall minus player radius 0.3m)
        Assert.True(newPos.X < 1.0f, $"Player penetrated the wall: X={newPos.X}");
    }

    // ─── Determinism ─────────────────────────────────────────────────────────────

    [Fact]
    public void Step_IsIdenticalAcrossTwoCalls_GivenSameInputs()
    {
        var ctx = DefaultContext(new Vector3(3, 0, 7), inputDir: new Vector3(0.5f, 0, 0.5f));
        var wall = new Collider
        {
            Position = new Vector3(4f, 0.5f, 8f),
            Size     = new Vector3(1f, 2f, 2f),
            Rotation = Quaternion.Identity,
            Material = "Concrete"
        };

        var (pos1, vel1, grounded1) = Step(ctx, new[] { wall });
        var (pos2, vel2, grounded2) = Step(ctx, new[] { wall });

        Assert.Equal(pos1, pos2);
        Assert.Equal(vel1, vel2);
        Assert.Equal(grounded1, grounded2);
    }
}

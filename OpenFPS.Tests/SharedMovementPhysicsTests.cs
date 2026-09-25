using System;
using System.Numerics;
using OpenFPS.Common;
using Xunit;
using static OpenFPS.Common.PhysicsConstants;

namespace OpenFPS.Tests;

/// <summary>
/// One step of a walking body, the function the client predicts with and the server decides with:
/// the ground under it, jumping and falling, walls it stops at and slides along, kerbs it steps up,
/// a ceiling it does not, the map's edge. Written for the survivors of the 2026-09-24 mutation run
/// over SharedMovementEngine.
/// </summary>
public class SharedMovementPhysicsTests
{
    private const float Dt = FixedDeltaTime;

    private static SharedMovementEngine.MovementContext At(Vector3 pos, Vector3 vel = default, Vector3 input = default,
                                                           float ground = 0f, bool jump = false)
        => new()
        {
            Position = pos, Velocity = vel, InputDirection = input, DeltaTime = Dt, GroundHeight = ground,
            Gravity = Gravity, JumpForce = JumpPower, Speed = WalkSpeed, PlayerRadius = PlayerRadius,
            PlayerHeight = PlayerHeight, StepHeight = StepHeight, IsJumpRequested = jump,
            MapMin = new Vector3(-50, -50, -50), MapMax = new Vector3(50, 50, 50),
        };

    private static SharedMovementEngine.Collider Box(Vector3 centre, Vector3 size, float yaw = 0f)
        => new() { Position = centre, Size = size, Rotation = Quaternion.CreateFromYawPitchRoll(yaw, 0, 0), Material = "Brick" };

    private static (Vector3 P, Vector3 V, bool G) Step(SharedMovementEngine.MovementContext c, params SharedMovementEngine.Collider[] cols)
        => SharedMovementEngine.Step(c, cols);

    private static (Vector3 P, Vector3 V, bool G) Walk(Vector3 from, Vector3 dir, int ticks, params SharedMovementEngine.Collider[] cols)
    {
        var p = from; var v = Vector3.Zero; bool g = true;
        for (int i = 0; i < ticks; i++) (p, v, g) = Step(At(p, v, dir), cols);
        return (p, v, g);
    }

    // ── The ground ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void LandingStopsTheFall()
    {
        var (p, v, g) = Step(At(new Vector3(0, 0.05f, 0), vel: new Vector3(0, -0.05f, 0)));
        Assert.True(g);
        Assert.Equal(0f, v.Y);
        Assert.Equal(0f, p.Y);
    }

    /// <summary>Stepping off a kerb lower than a step is still walking; falling or rising is not
    /// snapped to the floor under you.</summary>
    [Fact]
    public void AKerbDownIsStillTheGroundButAFallIsNot()
    {
        var level = Step(At(new Vector3(0, 0.3f, 0)));
        Assert.True(level.G);
        Assert.Equal(0f, level.P.Y);

        var falling = Step(At(new Vector3(0, 0.3f, 0), vel: new Vector3(0, -0.5f, 0)));
        Assert.False(falling.G);
        Assert.True(falling.P.Y > 0.25f);

        // Just inside the tolerance while falling: 0.1 m.
        var nearly = Step(At(new Vector3(0, 0.09f, 0), vel: new Vector3(0, -0.5f, 0)));
        Assert.True(nearly.G);

        var rising = Step(At(new Vector3(0, 0.02f, 0), vel: new Vector3(0, 1f, 0)));
        Assert.False(rising.G);
        Assert.Equal(1f - Gravity * Dt, rising.V.Y, 4);

        // Above a step's height, even standing still, it is a drop.
        var ledge = Step(At(new Vector3(0, StepHeight + 0.05f, 0)));
        Assert.False(ledge.G);
    }

    [Fact]
    public void WithNoGroundItFallsAtG()
    {
        var (p, v, g) = Step(At(new Vector3(0, 5f, 0), ground: DefaultGroundCheckLimit));
        Assert.False(g);
        Assert.Equal(-Gravity * Dt, v.Y, 4);
        var (_, _, g2) = Step(At(new Vector3(0, DefaultGroundCheckLimit, 0), ground: DefaultGroundCheckLimit));
        Assert.False(g2);
    }

    [Fact]
    public void AJumpLeavesTheGroundAtTheJumpForce()
    {
        var (p, v, g) = Step(At(Vector3.Zero, jump: true));
        Assert.False(g);
        Assert.Equal(JumpPower - Gravity * Dt, v.Y, 4);
        Assert.True(p.Y > 0f);
    }

    /// <summary>Falling faster than a tick's worth of height lands on the ground rather than passing
    /// through it.</summary>
    [Fact]
    public void AFastFallLandsOnTheGround()
    {
        var (p, v, g) = Step(At(new Vector3(0, 0.5f, 0), vel: new Vector3(0, -30f, 0)));
        Assert.True(g);
        Assert.Equal(0f, p.Y);
        Assert.Equal(0f, v.Y);
    }

    // ── Walls ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AWallStopsYouAtItsFace()
    {
        var wall = Box(new Vector3(0, 1.5f, 2f), new Vector3(10f, 3f, 0.2f));
        var (p, v, _) = Walk(Vector3.Zero, Vector3.UnitZ, 30, wall);
        float face = 2f - 0.1f;
        Assert.InRange(p.Z, face - PlayerRadius - 0.01f, face - PlayerRadius + 0.001f);
        Assert.InRange(v.Z, -0.01f, 0.01f);          // nothing left going into the wall
        Assert.Equal(0f, p.Y);                       // and not lifted up it
    }

    /// <summary>At an angle it slides along the wall and keeps what was along it.</summary>
    [Fact]
    public void AtAnAngleItSlidesAlongTheWall()
    {
        var wall = Box(new Vector3(0, 1.5f, 2f), new Vector3(40f, 3f, 0.2f));
        var dir = Vector3.Normalize(new Vector3(1f, 0f, 1f));
        var (p, v, _) = Walk(Vector3.Zero, dir, 60, wall);
        Assert.True(p.Z < 1.9f - PlayerRadius + 0.001f);
        Assert.True(p.X > 4f, $"it stuck at x {p.X:F2} instead of sliding");
        Assert.InRange(v.Z, -0.01f, 0.01f);
        Assert.InRange(v.X, WalkSpeed * 0.7f - 0.05f, WalkSpeed * 0.7f + 0.05f);
    }

    /// <summary>Standing inside a wall with nothing pressed, it is pushed out sideways, not lifted.</summary>
    [Fact]
    public void OverlappingAWallItIsPushedOutNotUp()
    {
        var wall = Box(new Vector3(0, 1.5f, 0.35f), new Vector3(10f, 3f, 0.2f));
        var (p, _, _) = Step(At(Vector3.Zero), wall);
        Assert.Equal(0f, p.Y);
        Assert.True(p.Z <= 0.25f - PlayerRadius + 0.002f, $"left at z {p.Z:F3}");
    }

    /// <summary>The same against a wall that faces along X, so both halves of the slide are used.</summary>
    [Fact]
    public void ItSlidesAlongAWallFacingEitherWay()
    {
        var wall = Box(new Vector3(2f, 1.5f, 0), new Vector3(0.2f, 3f, 40f));
        var dir = Vector3.Normalize(new Vector3(1f, 0f, 1f));
        var (p, v, _) = Walk(Vector3.Zero, dir, 60, wall);
        Assert.True(p.X < 1.9f - PlayerRadius + 0.001f);
        Assert.True(p.Z > 4f, $"it stuck at z {p.Z:F2} instead of sliding");
        Assert.InRange(v.X, -0.01f, 0.01f);
        Assert.InRange(v.Z, WalkSpeed * 0.7f - 0.05f, WalkSpeed * 0.7f + 0.05f);
    }

    /// <summary>Standing astride a kerb's edge with nothing pressed, it is eased off it sideways, not
    /// lifted onto it: a step is something you walk up.</summary>
    [Fact]
    public void OverlappingAKerbItIsNotLiftedOntoIt()
    {
        var kerb = Box(new Vector3(0, 0.15f, 0.35f), new Vector3(10f, 0.3f, 0.2f));
        var (p, _, _) = Step(At(Vector3.Zero), kerb);
        Assert.Equal(0f, p.Y);
    }

    // ── Kerbs and ceilings ────────────────────────────────────────────────────────────────────

    [Fact]
    public void AKerbIsSteppedUpAndABlockIsNot()
    {
        var kerb = Box(new Vector3(0, 0.15f, 2f), new Vector3(10f, 0.3f, 2f));
        var (up, _, _) = Walk(new Vector3(0, 0, 0.5f), Vector3.UnitZ, 12, kerb);
        Assert.True(up.Z > 1.0f, $"stopped at the kerb, z {up.Z:F2}");
        Assert.True(up.Y > 0.25f, $"not lifted onto it, y {up.Y:F2}");

        var block = Box(new Vector3(0, 0.3f, 2f), new Vector3(10f, 0.6f, 2f));
        var (stop, _, _) = Walk(new Vector3(0, 0, 0.5f), Vector3.UnitZ, 12, block);
        Assert.True(stop.Z < 1.0f - PlayerRadius + 0.01f);
        Assert.Equal(0f, stop.Y);
    }

    [Fact]
    public void AKerbUnderALowCeilingIsNotSteppedUp()
    {
        var kerb = Box(new Vector3(0, 0.15f, 2f), new Vector3(10f, 0.3f, 2f));
        var ceiling = Box(new Vector3(0, 2.1f, 2f), new Vector3(10f, 0.2f, 2f));      // underside at 2.0
        var (p, _, _) = Walk(new Vector3(0, 0, 0.5f), Vector3.UnitZ, 12, kerb, ceiling);
        Assert.True(p.Z < 1.0f - PlayerRadius + 0.01f, $"stepped up under a ceiling, z {p.Z:F2}");
        Assert.Equal(0f, p.Y);
    }

    /// <summary>The body is as tall as it is: a beam just over its head lets it through, one at head
    /// height does not.</summary>
    [Fact]
    public void ABeamOverheadIsMissedAndOneAtHeadHeightIsNot()
    {
        var high = Box(new Vector3(0, PlayerHeight + 0.1f + 0.25f, 2f), new Vector3(10f, 0.5f, 0.3f));
        var (under, _, _) = Walk(Vector3.Zero, Vector3.UnitZ, 30, high);
        Assert.True(under.Z > 3f);
        var head = Box(new Vector3(0, PlayerHeight - 0.1f + 0.25f, 2f), new Vector3(10f, 0.5f, 0.3f));
        var (stopped, _, _) = Walk(Vector3.Zero, Vector3.UnitZ, 30, head);
        Assert.True(stopped.Z < 2f);
    }

    // ── The map's edge ────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1f, 0f)]
    [InlineData(-1f, 0f)]
    [InlineData(0f, 1f)]
    [InlineData(0f, -1f)]
    public void TheMapsEdgeHoldsYou(float x, float z)
    {
        var dir = new Vector3(x, 0, z);
        var (p, v, _) = Walk(new Vector3(48f * x, 0, 48f * z), dir, 30);
        float edge = 50f - PlayerRadius;
        Assert.Equal(edge, MathF.Abs(x != 0 ? p.X : p.Z), 3);
        Assert.Equal(0f, x != 0 ? v.X : v.Z);
    }
}

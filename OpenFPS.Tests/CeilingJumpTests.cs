using System.Numerics;
using OpenFPS.Common;
using static OpenFPS.Common.SharedMovementEngine;

namespace OpenFPS.Tests;

/// <summary>
/// Jumping under a ceiling hits your head on it; it does not put you through a wall. Collision was
/// sideways only, so a head in a house's roof slab was pushed out of the slab's footprint — through
/// the nearest wall (64 Alder Street, 2026-09-28). The house's own dimensions.
/// </summary>
public class CeilingJumpTests
{
    private const float Dt = 1f / 30f;

    private static Collider Box(float x0, float x1, float y0, float y1, float z0, float z1) => new()
    {
        Position = new Vector3((x0 + x1) / 2, (y0 + y1) / 2, (z0 + z1) / 2),
        Size = new Vector3(x1 - x0, y1 - y0, z1 - z0),
        Rotation = Quaternion.Identity,
        Material = "Brick",
    };

    /// <summary>A bungalow: walls 2.6 m, a roof slab on top of them, a floor at 0.08.</summary>
    private static Collider[] House() => new[]
    {
        Box(-5.5f, -5.25f, 0f, 2.6f, -4.25f, 4.25f),   // west wall
        Box(5.25f, 5.5f, 0f, 2.6f, -4.25f, 4.25f),     // east wall
        Box(-5.5f, 5.5f, 0f, 2.6f, -4.25f, -4f),       // south wall
        Box(-5.5f, 5.5f, 0f, 2.6f, 4f, 4.25f),         // north wall
        Box(-5.5f, 5.5f, 2.6f, 2.8f, -4.25f, 4.25f),   // roof
    };

    /// <summary>A jump that rises 0.83 m, enough to put the head into this roof (the game's half-metre
    /// jump does not reach it).</summary>
    private static readonly float HighJump = MathF.Sqrt(2f * PhysicsConstants.Gravity * 0.83f);

    private static MovementContext Ctx(Vector3 pos, Vector3 vel, Vector3 input, bool jump) => new()
    {
        Position = pos, Velocity = vel, InputDirection = input, DeltaTime = Dt,
        GroundHeight = 0.08f, Gravity = PhysicsConstants.Gravity, JumpForce = HighJump,
        Speed = PhysicsConstants.WalkSpeed, PlayerRadius = PhysicsConstants.PlayerRadius,
        PlayerHeight = PhysicsConstants.PlayerHeight, StepHeight = PhysicsConstants.StepHeight,
        IsJumpRequested = jump, MapMin = new Vector3(-50, -50, -50), MapMax = new Vector3(50, 50, 50),
    };

    [Theory]
    [InlineData(-4.9f, 0f, -1f, 0f)]   // beside the west wall, pushing into it
    [InlineData(0f, 0f, 0f, 0f)]       // in the middle of the room, standing
    [InlineData(4.8f, 3.5f, 1f, 1f)]   // in a corner, pushing into it
    public void JumpingUnderTheRoofStaysInTheRoom(float x, float z, float ix, float iz)
    {
        var walls = House();
        var input = new Vector3(ix, 0, iz);
        if (input.LengthSquared() > 0) input = Vector3.Normalize(input);
        var pos = new Vector3(x, 0.08f, z);
        var vel = Vector3.Zero;
        float highest = 0f;
        for (int tick = 0; tick < 90; tick++)
        {
            var (p, v, _) = Step(Ctx(pos, vel, input, jump: tick % 20 == 0), walls);
            pos = p; vel = v;
            highest = MathF.Max(highest, pos.Y);
            Assert.InRange(pos.X, -5.25f, 5.25f);
            Assert.InRange(pos.Z, -4f, 4f);
        }
        // The head met the roof: the feet stop about 0.72 m up (2.6 - 1.8 - 0.08) instead of 0.83.
        Assert.True(highest < 0.83f, $"rose to {highest:F2} m through a 2.6 m ceiling");
    }
}

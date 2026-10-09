using System.Numerics;
using OpenFPS.Server;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// E with a shut door and a car both in reach picks the one you face (Cody, 2026-10-08: E beside a car
/// opened the apartment door behind him, because any shut door in reach came first).
/// </summary>
public class InteractChoiceTests
{
    private static readonly Vector3 Me = new(10f, 0f, 10f);

    [Fact]
    public void TheCarYouFaceWinsOverTheNearerDoorBehindYou()
    {
        var doorBehind = Me + new Vector3(0f, 1f, -1f);   // facing +Z at yaw 0
        var carAhead = Me + new Vector3(0f, 0f, 2.5f);
        Assert.False(GameServer.Prefer(Me, 0f, doorBehind, carAhead));
    }

    [Fact]
    public void TheDoorYouFaceWinsOverTheNearerCarBehindYou()
    {
        var doorAhead = Me + new Vector3(0f, 1f, 3f);
        var carBehind = Me + new Vector3(0f, 0f, -1f);
        Assert.True(GameServer.Prefer(Me, 0f, doorAhead, carBehind));
    }

    [Fact]
    public void FacingTurnsWithYaw()
    {
        // Increasing yaw swings forward toward +X.
        var doorEast = Me + new Vector3(3f, 0f, 0f);
        var carNorth = Me + new Vector3(0f, 0f, 1f);
        Assert.True(GameServer.Prefer(Me, MathF.PI / 2f, doorEast, carNorth));
        Assert.False(GameServer.Prefer(Me, 0f, doorEast, carNorth));
    }

    [Theory]
    [InlineData(0f)]            // both ahead
    [InlineData(MathF.PI)]      // both behind
    public void WithBothOrNeitherInFrontTheNearerWins(float yaw)
    {
        var near = Me + new Vector3(0.3f, 0f, 1f);
        var far = Me + new Vector3(-0.3f, 0f, 3f);
        Assert.True(GameServer.Prefer(Me, yaw, near, far));
        Assert.False(GameServer.Prefer(Me, yaw, far, near));
    }

    [Fact]
    public void ADoorsHeightDoesNotCountAgainstIt()
    {
        var tallDoor = Me + new Vector3(0f, 2f, 1f);
        var car = Me + new Vector3(0f, 0f, 1.5f);
        Assert.True(GameServer.Prefer(Me, 0f, tallDoor, car));
    }
}

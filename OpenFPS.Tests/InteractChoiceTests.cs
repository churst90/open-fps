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

/// <summary>
/// E between two doors opens the one you face (Cody, 2026-10-10, Marlow Tower's floor 3 corridor: "if I'm
/// facing away from the wall and facing another door like I'm between two doors, it doesn't know which one to
/// open"). The corridor's flats face each other across it, so in its middle both doors are as near as each
/// other, and the nearer by a hand's breadth took the key whichever way you faced.
/// </summary>
public class DoorChoiceTests
{
    private static readonly Lazy<(Arch.Core.World World, OpenFPS.Server.Core.MapManager Maps)> City = new(() =>
    {
        var prefabs = new OpenFPS.Server.Repositories.PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
        var maps = new OpenFPS.Server.Core.MapManager(new OpenFPS.Server.Repositories.MapRepository(Path.Combine(AppContext.BaseDirectory, "maps")), prefabs);
        maps.Initialize();
        Assert.True(maps.TryGetMap("city", out Arch.Core.World world, out _, out _, out _));
        return (world, maps);
    });

    private static string Chosen(Vector3 at, float yaw)
    {
        var door = GameServer.ShutDoorInReach(City.Value.World, at, yaw, out _, out string name);
        Assert.NotNull(door);
        return name;
    }

    // The corridor runs north and south between the B flats (west) and the F flats (east); flat 33's doors
    // face each other at z -49.14, their thresholds at 9.25 m.
    private static readonly Vector3 Between33 = new(-19.95f, 9.25f, -49.14f);

    [Theory]
    [InlineData(0f)]
    [InlineData(0.3f)]       // a hand's breadth nearer the F door
    [InlineData(-0.3f)]      // ...and nearer the B door
    public void BetweenTwoDoorsTheOneYouFaceOpens(float offsetEast)
    {
        var at = Between33 + new Vector3(offsetEast, 0f, 0f);
        Assert.Equal("Marlow Tower flat 33F door", Chosen(at, MathF.PI / 2f));    // facing east
        Assert.Equal("Marlow Tower flat 33B door", Chosen(at, -MathF.PI / 2f));   // facing west
    }

    /// <summary>Facing along the corridor, neither door ahead: the nearer.</summary>
    [Fact]
    public void FacingAlongTheCorridorTheNearerOpens()
    {
        Assert.Equal("Marlow Tower flat 33F door", Chosen(Between33 + new Vector3(0.3f, 0f, 0f), 0f));
        Assert.Equal("Marlow Tower flat 33B door", Chosen(Between33 + new Vector3(-0.3f, 0f, 0f), 0f));
    }

    /// <summary>Flat 23's and flat 43's doors are a storey down and up, exactly as near in plan and as much ahead:
    /// never the ones you reach for. (The one below is two metres under your feet, within the old five-metre reach.)</summary>
    [Fact]
    public void ADoorOnAnotherFloorIsNotInReach()
    {
        string name = Chosen(Between33, MathF.PI / 2f);
        Assert.DoesNotContain("flat 4", name);
        Assert.DoesNotContain("flat 2", name);
    }
}

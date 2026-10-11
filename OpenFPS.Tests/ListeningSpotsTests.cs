using System.Globalization;
using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Server.Core;
using OpenFPS.Server.Systems;

namespace OpenFPS.Tests;

/// <summary>
/// The /tp spots handed to Cody for listening, each one he can stand on: in the room it is said to be in, on its floor,
/// and clear to the server's own teleport check (MovementSystem.CheckCollision), as /tp asks; and each the spot
/// StandingSpots.InRoom gives for that room, so a list made with it is right by construction. Cody, 2026-10-10:
/// "those cords you gave me ... said cannot move there, area is solid" (they were the middles of the rooms' boxes,
/// half their height up, and one a 4 cm sliver in a wall).
/// </summary>
public static class ListeningSpots
{
    /// <summary>Checks one spot on a loaded map; the room is named by its map-file id.</summary>
    public static void Check(MapManager maps, string mapId, int roomId, string typed, string expectName)
    {
        Assert.True(maps.TryGetMap(mapId, out World world, out _, out var grid, out _));
        Assert.True(maps.AuthoredEntities(mapId).TryGetValue(roomId, out var room), $"no room #{roomId} on {mapId}");
        var region = world.Get<RegionComponent>(room);
        Assert.Contains(expectName, region.FriendlyName);
        var f = typed.Split(' ').Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        var at = PlayerCoordinates.ToWorld(f[0], f[1], f[2]);
        Assert.False(MovementSystem.CheckCollision(world, grid, at, PhysicsConstants.PlayerRadius, PhysicsConstants.PlayerHeight),
                     $"{typed} on {mapId} is solid");
        float ground = PhysicsUtils.GetGroundHeight(world, grid, at + new Vector3(0f, 0.1f, 0f), out _);
        Assert.InRange(at.Y - ground, 0f, 0.15f);
        // In the room as the server finds it: its flood where it flooded.
        Assert.True(MapManager.RegionAt(world, at + new Vector3(0f, 1f, 0f)) == room.Id, $"{typed} is not in {region.FriendlyName}");
        Assert.Equal(typed, StandingSpots.InRoom(world, grid, room) is { } spot ? StandingSpots.Typed(spot) : "none");
    }
}

public class CityListeningSpotsTests
{
    [Theory]
    [InlineData(1489, "-14.2 -102.7 9.3", "Marlow Tower stairwell, floor 3")]
    [InlineData(2013, "-10.6 -105.5 24.1", "Marlow Tower roof access")]
    [InlineData(5824, "0.0 -212.5 0.1", "Tunnel, north portal")]
    public void You_can_stand_there(int room, string typed, string name)
        => ListeningSpots.Check(DoorChoiceTests.Maps, "city", room, typed, name);
}

public partial class RealPlaceMapTests
{
    [Theory]
    [InlineData("magnolia_tx", 16390, "-88.8 -253.0 3.9", "19415 Turtle Dove Lane, barn")]
    [InlineData("magnolia_tx", 14083, "-301.4 -268.7 5.2", "19617 Turtle Dove Lane, house")]
    [InlineData("magnolia_tx", 13874, "-333.3 697.2 -0.5", "16510 Country Star, house")]
    [InlineData("albany_or", 42733, "1464.5 1274.2 0.1", "Linn County Juvenile Detention")]
    [InlineData("albany_or", 33365, "-82.3 628.8 -1.3", "Favorite Mistake")]
    public void A_listening_spot_can_be_stood_on(string id, int room, string typed, string name)
        => ListeningSpots.Check(_maps.Get(id).Manager, id, room, typed, name);
}

using System.Numerics;
using OpenFPS.Client.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Common.Systems;

namespace OpenFPS.Tests;

/// <summary>
/// Where two named zones overlap, you are in the smaller one — a spot inside a street, a room inside a
/// hall — whichever order the map lists them in. It used to be whichever came first (the boxes) or
/// last (the voxel grid), so the two lookups could even disagree.
/// </summary>
public class OverlappingZoneTests
{
    private static EntityDefinition Zone(int id, string name, Vector3 at, Vector3 size) => new()
    {
        EntityId = id,
        Transform = new Transform { Position = at, Rotation = Quaternion.Identity, Scale = Vector3.One },
        Region = new RegionComponent { FriendlyName = name, RoomSize = size, Materials = new int[6] },
    };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheSmallestZoneHoldingThePointWins(bool bigFirst)
    {
        var big = Zone(10, "Calder Avenue", new Vector3(0, 2, 0), new Vector3(20, 4, 100));
        var small = Zone(11, "Bus stop", new Vector3(0, 2, 5), new Vector3(4, 4, 6));
        var defs = bigFirst ? new List<EntityDefinition> { big, small } : new List<EntityDefinition> { small, big };
        var map = AcousticVolumeGenerator.GenerateRegions(defs, new Vector3(200, 20, 200), new Vector3(-100, -1, -100));
        var world = new WorldSnapshot { AcousticMap = map };
        foreach (var d in defs)
            world.Entities[d.EntityId] = new EntitySnapshot { Id = d.EntityId, Definition = d, Transform = d.Transform };
        var spatial = new SpatialService();

        var atStop = new Vector3(0, 1.7f, 5);
        var upTheStreet = new Vector3(0, 1.7f, 30);
        Assert.Equal(11, spatial.GetRegionAt(world, atStop));
        Assert.Equal(10, spatial.GetRegionAt(world, upTheStreet));
        Assert.Equal(11, map.VoxelGrid.GetRegionAt(atStop));
        Assert.Equal(10, map.VoxelGrid.GetRegionAt(upTheStreet));
    }

    /// <summary>
    /// Standing against the outside of a room is outside it. The voxel grid is half a metre at a
    /// time, so the first voxel beyond a wall can carry the room; a room with its own box decides
    /// for itself (64 Alder Street, 2026-09-28: "right up against the building it sounds like I'm
    /// inside").
    /// </summary>
    [Fact]
    public void JustOutsideAWallIsOutside()
    {
        // A house whose east wall face is not on a voxel boundary.
        var house = Zone(20, "64 Alder Street", new Vector3(-214.75f, 1.34f, 130.55f), new Vector3(10.5f, 2.52f, 8.0f));
        var defs = new List<EntityDefinition> { house };
        var map = AcousticVolumeGenerator.GenerateRegions(defs, new Vector3(600, 20, 600), new Vector3(-300, -1, -300));
        var world = new WorldSnapshot { AcousticMap = map };
        world.Entities[20] = new EntitySnapshot { Id = 20, Definition = house, Transform = house.Transform };
        var spatial = new SpatialService();

        Assert.Equal(20, spatial.GetRegionAt(world, new Vector3(-212f, 1.7f, 130f)));
        for (float x = -209.45f; x <= -209.0f; x += 0.05f)       // the house ends at -209.5
            Assert.Equal(AcousticConstants.GlobalRegionId, spatial.GetRegionAt(world, new Vector3(x, 1.7f, 130f)));
    }
}

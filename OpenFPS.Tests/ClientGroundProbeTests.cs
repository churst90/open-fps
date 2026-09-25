using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// The client's ground probe, the other half of walking prediction: five points across the body's
/// footprint, the highest solid top at or under a step above the feet. Written for the survivors of
/// the 2026-09-24 mutation run over PhysicsUtils.
/// </summary>
public class ClientGroundProbeTests
{
    private static WorldSnapshot World(params (int Id, Vector3 Centre, Vector3 Size, bool Solid, string Material)[] boxes)
    {
        var snap = new WorldSnapshot();
        foreach (var b in boxes)
            snap.Entities[b.Id] = new EntitySnapshot
            {
                Id = b.Id,
                Definition = new EntityDefinition
                {
                    EntityId = b.Id,
                    Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = b.Size, IsSolid = b.Solid },
                    Material = new MaterialComponent { Material = b.Material },
                },
                Transform = new Transform { Position = b.Centre, Rotation = Quaternion.Identity, Scale = Vector3.One },
            };
        return snap;
    }

    private static readonly (int, Vector3, Vector3, bool, string) Floor = (2, new(0, -0.5f, 0), new(100, 1, 100), true, "Concrete");

    [Fact]
    public void TheFloorUnderYouAndItsMaterial()
    {
        float y = PhysicsUtils.GetGroundHeight(World(Floor), new Vector3(3, 0.02f, 3), 1, out string mat);
        Assert.Equal(0f, y, 4);
        Assert.Equal("Concrete", mat);
    }

    [Fact]
    public void NothingUnderYouIsNoGround()
    {
        float y = PhysicsUtils.GetGroundHeight(World(), new Vector3(0, 5, 0), 1, out string mat);
        Assert.True(y < -900f);
        Assert.Equal("Generic", mat);
    }

    /// <summary>Of a floor and a kerb on it, the higher is what you stand on — if it is within a step.</summary>
    [Fact]
    public void TheHighestTopWithinAStepWins()
    {
        var kerb = (3, new Vector3(0, 0.15f, 0), new Vector3(2, 0.3f, 2), true, "Stone");
        float y = PhysicsUtils.GetGroundHeight(World(Floor, kerb), new Vector3(0, 0f, 0), 1, out string mat);
        Assert.Equal(0.3f, y, 4);
        Assert.Equal("Stone", mat);

        // A wall's top, more than a step above your feet, is not your floor.
        var wall = (4, new Vector3(0, 1.5f, 0), new Vector3(2, 3f, 2), true, "Brick");
        float under = PhysicsUtils.GetGroundHeight(World(Floor, wall), new Vector3(0, 0f, 0), 1, out string m2);
        Assert.Equal(0f, under, 4);
        Assert.Equal("Concrete", m2);
        // ...but exactly a step up still is.
        var step = (5, new Vector3(0, 0.2f, 0), new Vector3(2, 0.4f, 2), true, "Wood");
        Assert.Equal(0.4f, PhysicsUtils.GetGroundHeight(World(Floor, step), Vector3.Zero, 1, out _), 4);
    }

    /// <summary>Standing with one edge of the body over a kerb is standing on it: all five points
    /// across the footprint count, on every side.</summary>
    [Theory]
    [InlineData(1f, 0f)]
    [InlineData(-1f, 0f)]
    [InlineData(0f, 1f)]
    [InlineData(0f, -1f)]
    public void AnEdgeOverAKerbIsOnIt(float x, float z)
    {
        // A kerb whose near face is 0.2 m from the centre: only the probe on that side reaches it.
        var kerb = (3, new Vector3(x * 1.2f, 0.15f, z * 1.2f), new Vector3(2, 0.3f, 2), true, "Stone");
        float y = PhysicsUtils.GetGroundHeight(World(Floor, kerb), Vector3.Zero, 1, out string mat);
        Assert.Equal(0.3f, y, 4);
        Assert.Equal("Stone", mat);
    }

    /// <summary>A kerb off to one side, under none of the body, is not what you stand on.</summary>
    [Fact]
    public void AKerbBesideYouIsNotUnderYou()
    {
        var kerb = (3, new Vector3(3f, 0.15f, 0), new Vector3(2, 0.3f, 2), true, "Stone");
        Assert.Equal(0f, PhysicsUtils.GetGroundHeight(World(Floor, kerb), Vector3.Zero, 1, out string mat), 4);
        Assert.Equal("Concrete", mat);
    }

    [Fact]
    public void YourOwnBodyAndThingsNotSolidAreNotFloors()
    {
        var self = (1, new Vector3(0, 0.15f, 0), new Vector3(2, 0.3f, 2), true, "Flesh");
        Assert.Equal(0f, PhysicsUtils.GetGroundHeight(World(Floor, self), Vector3.Zero, 1, out _), 4);
        var mat = (3, new Vector3(0, 0.15f, 0), new Vector3(2, 0.3f, 2), false, "Cloth");
        Assert.Equal(0f, PhysicsUtils.GetGroundHeight(World(Floor, mat), Vector3.Zero, 1, out _), 4);
    }

    [Fact]
    public void AFloorWithNoMaterialNameIsGeneric()
    {
        var bare = (2, new Vector3(0, -0.5f, 0), new Vector3(100, 1, 100), true, "");
        PhysicsUtils.GetGroundHeight(World(bare), Vector3.Zero, 1, out string mat);
        Assert.Equal("Generic", mat);
    }
}

using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Common.Systems;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// A structure's open faces are its openings (AcousticVolumeGenerator, step 4b): a tunnel's sections
/// join each other and its mouths join the outside, where the faces actually are and as big as they
/// are, with no portal authored. Before this the tunnel was coupled to nothing, and from its mouth you
/// heard none of its reverb (2026-09-29).
/// </summary>
public class OpeningsFromOpenFacesTests
{
    private static int Concrete => AcousticRegistry.GetProperties("Concrete").ResonanceIndex;
    private const int Open = RoomAcoustics.OpenFaceMaterial;

    private static EntityDefinition Region(int id, Vector3 at, Vector3 size, int[] materials) => new()
    {
        EntityId = id,
        Transform = new Transform { Position = at, Rotation = Quaternion.Identity },
        Region = new RegionComponent { FriendlyName = $"r{id}", RoomSize = size, Materials = materials },
    };

    private static AcousticMap Build(params EntityDefinition[] defs)
    {
        AcousticRegistry.EnsureInitialized();
        return AcousticVolumeGenerator.GenerateRegions(defs, new Vector3(200f, 40f, 200f), new Vector3(-100f, -10f, -100f));
    }

    [Fact]
    public void ATunnelsSectionsJoinEachOtherAndItsMouthsTheOutside()
    {
        // floor, ceiling, north, south, east, west: walled along its length, open at both ends.
        int c = Concrete;
        var walled = new[] { c, c, Open, Open, c, c };
        var map = Build(Region(1, new Vector3(0, 2.5f, -12.5f), new Vector3(12, 5, 25), walled),
                        Region(2, new Vector3(0, 2.5f, 12.5f), new Vector3(12, 5, 25), walled));
        var openings = map.Portals.Values.ToList();
        Assert.Equal(3, openings.Count);

        float aperture = MathF.Sqrt(12f * 5f);
        var between = Assert.Single(openings, p => (p.Portal.RegionAId, p.Portal.RegionBId) is (1, 2) or (2, 1));
        Assert.Equal(0f, between.Position.Z, 2);
        Assert.Equal(aperture, between.Portal.ApertureSize, 3);
        Assert.Contains(openings, p => MathF.Abs(p.Position.Z + 25f) < 0.01f && (p.Portal.RegionAId == map.GlobalEnvironmentId || p.Portal.RegionBId == map.GlobalEnvironmentId));
        Assert.Contains(openings, p => MathF.Abs(p.Position.Z - 25f) < 0.01f && (p.Portal.RegionAId == map.GlobalEnvironmentId || p.Portal.RegionBId == map.GlobalEnvironmentId));
    }

    [Fact]
    public void AClosedRoomAndAPatchOfGroundGetNone()
    {
        int c = Concrete;
        var map = Build(Region(1, new Vector3(-30, 2.5f, 0), new Vector3(6, 5, 6), new[] { c, c, c, c, c, c }),
                        Region(2, new Vector3(30, 2.5f, 0), new Vector3(20, 5, 20), new[] { Open, Open, Open, Open, Open, Open }));
        Assert.Empty(map.Portals);
    }
}

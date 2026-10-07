using System.Numerics;
using OpenFPS.Client.AudioEngine.Acoustics;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Common.Systems;

namespace OpenFPS.Tests;

/// <summary>
/// Every side of a room that is not closed in is an opening, sized and placed from the gap in its walls
/// (FaceOpenings, AcousticVolumeGenerator.AddFaceOpenings), and a door is an opening with a leaf in it.
/// One opening per gap, not one per pair of rooms: until 2026-10-02 a building with a door to the street
/// lost its open side onto the same street, and two doorways between two rooms were one.
/// </summary>
public class OpeningsFromGapsTests
{
    private static int Index(string material) => AcousticRegistry.GetProperties(material).ResonanceIndex;
    private const int Open = RoomAcoustics.OpenFaceMaterial;
    private static readonly Quaternion AcrossZ = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f);

    private sealed class Scene
    {
        public readonly List<EntityDefinition> Defs = new();
        private int _id = 1;

        public void Box(float x0, float x1, float y0, float y1, float z0, float z1, string material = "Brick")
            => Defs.Add(Solid(_id++, new Vector3((x0 + x1) / 2, (y0 + y1) / 2, (z0 + z1) / 2),
                              new Vector3(x1 - x0, y1 - y0, z1 - z0), Quaternion.Identity, material));

        public void Room(int id, float x0, float x1, float y0, float y1, float z0, float z1, int[] materials)
            => Defs.Add(new EntityDefinition
            {
                EntityId = id,
                Transform = new Transform { Position = new Vector3((x0 + x1) / 2, (y0 + y1) / 2, (z0 + z1) / 2), Rotation = Quaternion.Identity },
                Region = new RegionComponent { FriendlyName = $"r{id}", RoomSize = new Vector3(x1 - x0, y1 - y0, z1 - z0), Materials = materials, IsIndoor = true },
            });

        public WorldSnapshot World()
        {
            AcousticRegistry.EnsureInitialized();
            var world = new WorldSnapshot();
            foreach (var d in Defs)
                world.Entities[d.EntityId] = new EntitySnapshot { Id = d.EntityId, Definition = d, Transform = d.Transform };
            world.AcousticMap = AcousticVolumeGenerator.GenerateRegions(Defs, new Vector3(100, 20, 100), new Vector3(-50, -5, -50));
            return world;
        }
    }

    private static EntityDefinition Solid(int id, Vector3 at, Vector3 size, Quaternion rot, string material) => new()
    {
        EntityId = id,
        Type = EntityType.StaticObject,
        Transform = new Transform { Position = at, Rotation = rot, Scale = Vector3.One },
        Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = size, IsSolid = true },
        Material = new MaterialComponent { Material = material },
    };

    private static IEnumerable<(int Id, PortalComponent Portal, Vector3 Position)> FaceOpenings(AcousticMap map)
        => map.Portals.Where(p => p.Key <= AcousticVolumeGenerator.FirstFaceOpeningId)
                      .Select(p => (p.Key, p.Value.Portal, p.Value.Position));

    private static bool Joins(PortalComponent p, int a, int b)
        => (p.RegionAId == a && p.RegionBId == b) || (p.RegionAId == b && p.RegionBId == a);

    // ── A lobby with a front door AND an open side, both onto the street ──────────────────────────

    private const int Lobby = 701, FrontDoor = 801;

    /// <summary>x 0..6, z 0..8, 3 m high. The west wall has a doorway at z 3..4 with a leaf in it; the
    /// east side has no wall at all.</summary>
    private static WorldSnapshot LobbyWithDoorAndOpenSide(bool doorAjar)
    {
        var s = new Scene();
        s.Box(-30, 40, -0.2f, 0, -20, 30, "Concrete");                 // the ground
        s.Box(-0.3f, 6.0f, 3.0f, 3.2f, -0.3f, 8.3f, "Concrete");         // the roof
        s.Box(-0.3f, 0, 0, 3, -0.3f, 3);                                 // west wall, south of the door
        s.Box(-0.3f, 0, 0, 3, 4, 8.3f);                                  // ...north of it
        s.Box(-0.3f, 0, 2.1f, 3, 3, 4);                                  // ...over it
        s.Box(-0.3f, 6.3f, 0, 3, 8, 8.3f);                               // north wall
        s.Box(-0.3f, 6.3f, 0, 3, -0.3f, 0);                              // south wall
        var leaf = Solid(FrontDoor, new Vector3(-0.15f, 1.075f, 3.5f), new Vector3(1.1f, 2.15f, 0.05f), AcrossZ, "Wood");
        leaf.Portal = new PortalComponent
        {
            RegionAId = Lobby, RegionBId = AcousticConstants.GlobalRegionId,
            ApertureSize = doorAjar ? 0.5f : 0f,
            OpeningCentre = new Vector3(-0.15f, 1.075f, 3.5f), OpeningRotation = AcrossZ,
        };
        s.Defs.Add(leaf);
        int c = Index("Concrete"), b = Index("Brick");
        s.Room(Lobby, 0, 6, 0, 3, 0, 8, new[] { c, c, b, b, Open, b });
        return s.World();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ARoomWithADoorAndAnOpenSideHasBothOpenings(bool doorAjar)
    {
        var world = LobbyWithDoorAndOpenSide(doorAjar);
        var map = world.AcousticMap!;
        int outside = map.GlobalEnvironmentId;

        // The open side is an opening to the street even though the door already joins the two.
        var side = Assert.Single(FaceOpenings(map));
        Assert.True(Joins(side.Portal, Lobby, outside));
        Assert.Equal(6f, side.Position.X, 2);
        if (doorAjar) Assert.True(map.Portals.ContainsKey(FrontDoor), "the ajar door is not on the map");

        // ...and both are in the graph the routes run through, each joining the lobby to the outdoors.
        var routes = new SpatialAcoustics().RoutesFor(world)!;
        Assert.Empty(routes.Problems);
        var door = Assert.Single(routes.Openings, o => o.Kind == "door");
        var open = Assert.Single(routes.Openings, o => o.Kind == OpeningRoutes.FaceKind);
        Assert.Equal(FrontDoor, door.Id);
        foreach (var o in new[] { door, open })
            Assert.True((o.NodeA, o.NodeB) is (Lobby, OpeningRoutes.Outside) or (OpeningRoutes.Outside, Lobby),
                        $"{o.Kind} {o.Id} joins {o.NodeA} and {o.NodeB}");
    }

    // ── Two doorways between the same two rooms ───────────────────────────────────────────────────

    private const int RoomA = 711, RoomB = 712;

    /// <summary>Two rooms side by side, A at x 0..4.85 and B at x 5.15..10, z 0..8, 2.7 m high, sharing a
    /// 0.3 m wall with two doorways in it, 1.0 m wide and 2.1 m high, centred at z 2 and z 6. No doors.</summary>
    private static WorldSnapshot TwoRoomsTwoDoorways()
    {
        var s = new Scene();
        s.Box(-30, 40, -0.2f, 0, -20, 30, "Concrete");
        s.Box(-0.3f, 10.3f, 2.7f, 2.9f, -0.3f, 8.3f, "Concrete");        // roof
        s.Box(-0.3f, 0, 0, 2.7f, -0.3f, 8.3f);                           // west
        s.Box(10, 10.3f, 0, 2.7f, -0.3f, 8.3f);                          // east
        s.Box(-0.3f, 10.3f, 0, 2.7f, 8, 8.3f);                           // north
        s.Box(-0.3f, 10.3f, 0, 2.7f, -0.3f, 0);                          // south
        // The shared wall, x 4.85..5.15, with its two doorways.
        s.Box(4.85f, 5.15f, 0, 2.7f, 0, 1.5f, "Plaster");
        s.Box(4.85f, 5.15f, 0, 2.7f, 2.5f, 5.5f, "Plaster");
        s.Box(4.85f, 5.15f, 0, 2.7f, 6.5f, 8, "Plaster");
        s.Box(4.85f, 5.15f, 2.1f, 2.7f, 1.5f, 2.5f, "Plaster");
        s.Box(4.85f, 5.15f, 2.1f, 2.7f, 5.5f, 6.5f, "Plaster");
        int c = Index("Concrete"), b = Index("Brick"), p = Index("Plaster");
        s.Room(RoomA, 0, 4.85f, 0, 2.7f, 0, 8, new[] { c, c, b, b, p, b });
        s.Room(RoomB, 5.15f, 10, 0, 2.7f, 0, 8, new[] { c, c, b, b, b, p });
        return s.World();
    }

    [Fact]
    public void TwoOpeningsBetweenTheSameRoomsBothExistAndBothCarryRoutes()
    {
        var world = TwoRoomsTwoDoorways();
        var map = world.AcousticMap!;
        var between = FaceOpenings(map).Where(o => Joins(o.Portal, RoomA, RoomB)).OrderBy(o => o.Position.Z).ToList();
        Assert.Equal(2, between.Count);
        Assert.Equal(2, FaceOpenings(map).Count());           // the same gaps seen from B are not counted again

        var routes = new SpatialAcoustics().RoutesFor(world)!;
        Assert.Empty(routes.Problems);
        var openings = routes.Openings.Where(o => o.Kind == OpeningRoutes.FaceKind).ToList();
        Assert.Equal(2, openings.Count);
        Assert.All(openings, o => Assert.True((o.NodeA, o.NodeB) is (RoomA, RoomB) or (RoomB, RoomA)));

        // A voice in A heard in B, both well clear of the doorways' lines of sight: it arrives by both.
        Assert.True(routes.Route(new Vector3(1.0f, 1.5f, 4f), RoomA, new Vector3(9.0f, 1.5f, 4f), RoomB, out var answer),
                    "no route between the rooms");
        Assert.Equal(2, answer.Routes);

        // Shut one of them (a wall where the doorway was) and only the other carries a route.
        var oneShut = TwoRoomsTwoDoorways();
        var shutAt = new EntityDefinition
        {
            EntityId = 999,
            Type = EntityType.StaticObject,
            Transform = new Transform { Position = new Vector3(5.0f, 1.05f, 6f), Rotation = Quaternion.Identity, Scale = Vector3.One },
            Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(0.3f, 2.1f, 1.0f), IsSolid = true },
            Material = new MaterialComponent { Material = "Plaster" },
        };
        oneShut.Entities[999] = new EntitySnapshot { Id = 999, Definition = shutAt, Transform = shutAt.Transform };
        var defs = oneShut.Entities.Values.Select(e => e.Definition).ToList();
        oneShut.AcousticMap = AcousticVolumeGenerator.GenerateRegions(defs, new Vector3(100, 20, 100), new Vector3(-50, -5, -50));
        var single = Assert.Single(FaceOpenings(oneShut.AcousticMap));
        Assert.Equal(2f, single.Position.Z, 2);
    }

    [Fact]
    public void AnOpeningIsTheSizeOfItsGapAndWhereItIs()
    {
        var map = TwoRoomsTwoDoorways().AcousticMap!;
        var between = FaceOpenings(map).OrderBy(o => o.Position.Z).ToList();
        float[] zs = { 2f, 6f };
        for (int k = 0; k < 2; k++)
        {
            var (id, portal, position) = between[k];
            // In the middle of the wall it is cut through, the middle of the gap across and up.
            Assert.Equal(5.0f, position.X, 2);
            Assert.Equal(1.05f, position.Y, 2);
            Assert.Equal(zs[k], position.Z, 2);
            // A square of its area: the doorway is 1.0 m by 2.1 m.
            Assert.Equal(MathF.Sqrt(2.1f), portal.ApertureSize, 3);

            var frame = map.OpeningFrames[id];
            Assert.Equal(1.0f, frame.Size.X, 2);      // across
            Assert.Equal(2.1f, frame.Size.Y, 2);      // up
            Assert.Equal(0.3f, frame.Size.Z, 2);      // through: the wall's thickness
            Assert.Equal(1f, MathF.Abs(Vector3.Transform(Vector3.UnitZ, frame.Rotation).X), 3);   // faces along x
            Assert.Equal(1f, Vector3.Transform(Vector3.UnitY, frame.Rotation).Y, 3);              // upright
        }

        // The graph takes the same rectangle.
        var routes = new SpatialAcoustics().RoutesFor(TwoRoomsTwoDoorways())!;
        foreach (var o in routes.Openings)
        {
            Assert.Equal(0.5f, o.HalfWidth, 2);
            Assert.Equal(1.05f, o.HalfHeight, 2);
            Assert.Equal(0.15f, o.HalfDepth, 2);
            Assert.Equal(Vector3.One, o.Tau);
        }
    }

    /// <summary>A wall the region names but the map never built is the author's: no geometry, no gap.
    /// Without this every hand-described room with no walls modelled would be open on every side.</summary>
    [Fact]
    public void AFaceNamedAsAWallWithNothingBuiltThereIsNoOpening()
    {
        var s = new Scene();
        int c = Index("Concrete");
        s.Room(721, 0, 6, 0, 3, 0, 6, new[] { c, c, c, c, c, c });
        Assert.Empty(FaceOpenings(s.World().AcousticMap!));
    }

    /// <summary>A gap narrower than half a wavelength of the middle band is a misfit between two boxes,
    /// not a hole: a wall that stops a few centimetres short of its neighbour leaves no opening.</summary>
    [Fact]
    public void ASlotBetweenTwoBoxesIsNotAnOpening()
    {
        var s = new Scene();
        s.Box(-30, 40, -0.2f, 0, -20, 30, "Concrete");
        s.Box(-0.3f, 6.3f, 3.0f, 3.2f, -0.3f, 6.3f, "Concrete");
        s.Box(-0.3f, 0, 0, 3, -0.3f, 6.3f);
        s.Box(6, 6.3f, 0, 3, -0.3f, 6.3f);
        s.Box(-0.3f, 6.3f, 0, 3, 6, 6.3f);
        s.Box(-0.3f, 2.96f, 0, 3, -0.3f, 0);                             // south wall, in two pieces
        s.Box(3.04f, 6.3f, 0, 3, -0.3f, 0);                              // ...8 cm apart
        int c = Index("Concrete");
        s.Room(731, 0, 6, 0, 3, 0, 6, new[] { c, c, c, c, c, c });
        Assert.Empty(FaceOpenings(s.World().AcousticMap!));
    }
}

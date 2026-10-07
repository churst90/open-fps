using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Common.Systems;
using OpenFPS.Client.Core;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using OpenFPS.Server.Systems;

namespace OpenFPS.Tests;

/// <summary>
/// A composite derives its own room from its geometry, by one rule for a shed, a cathedral and a cab:
/// big enough to be in, mostly empty, mostly covered. These hold that rule, the things that must not
/// become rooms, that the room is made of the walls' materials, and that it reaches the client's
/// acoustic map.
/// </summary>
public class CompositeRoomTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"openfps-rooms-{Guid.NewGuid():N}");

    public void Dispose() { try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { } }

    // ── What is a room ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FourWallsAFloorAndARoofAreARoom()
    {
        var f = new Fixture(_dir);
        int root = f.Shed(Fixture.Clear);

        var room = f.RoomOf(root);
        Assert.NotNull(room);
        Assert.True(room!.Value.IsIndoor);
        Assert.Equal("shed", room.Value.FriendlyName);

        // The size of the thing.
        Assert.Equal(Fixture.ShedSpan, room.Value.RoomSize.X, 0);
        Assert.Equal(Fixture.ShedSpan, room.Value.RoomSize.Z, 0);
        Assert.True(room.Value.RoomSize.Y > 2.5f, $"a room {room.Value.RoomSize.Y:F1} m high is a crawlspace");
    }

    /// <summary>The room sits at the middle of the space, not at the composite's origin, which is at the
    /// ground: centred there, half the room was underground and you stood up outdoors.</summary>
    [Fact]
    public void TheRoomIsWhereTheSpaceIsAndNotWhereTheOriginIs()
    {
        var f = new Fixture(_dir);
        int root = f.Shed(new Vector3(120, 0, -60));

        var (position, rotation, size) = f.RoomVolume(root);
        Assert.True(position.Y > 1.0f, $"the room's middle is {position.Y:F2} m up, so it is buried");

        // Standing height in the middle is inside: the client's own question.
        Assert.True(GeometryUtils.IsPointInOBB(new Vector3(120, 1.7f, -60), position, size, rotation),
                    "standing up inside your own shed puts you outdoors");
        // And well above the roof is not.
        Assert.False(GeometryUtils.IsPointInOBB(new Vector3(120, 12f, -60), position, size, rotation));
        Assert.False(GeometryUtils.IsPointInOBB(new Vector3(120, -4f, -60), position, size, rotation),
                    "the room reaches below the floor, so a cellar would be in the sitting room");
    }

    [Fact]
    public void TheRoomTurnsAndTravelsWithTheBuilding()
    {
        var f = new Fixture(_dir);
        int root = f.Shed(Fixture.Clear, anchored: false);
        var travelled = new Vector3(30, 0, 12);

        f.World.Get<Transform>(f.Entity(root)).Position += travelled;
        f.World.Get<Transform>(f.Entity(root)).Rotation = Quaternion.CreateFromYawPitchRoll(MathF.PI / 2f, 0, 0);
        ParentSystem.Update(f.World, f.Lookup);

        var (position, rotation, size) = f.RoomVolume(root);
        var standingInside = Fixture.Clear + travelled + new Vector3(0, 1.7f, 0);
        Assert.True(GeometryUtils.IsPointInOBB(standingInside, position, size, rotation),
                    "the shed moved and left its inside behind");
        Assert.False(GeometryUtils.IsPointInOBB(Fixture.Clear + new Vector3(0, 1.7f, 0), position, size, rotation),
                    "its inside is still back where it started as well");
        MathHelper.ToYawPitch(rotation, out float yaw, out _);
        Assert.Equal(MathF.PI / 2f, yaw, 2);
    }

    // ── What is NOT a room ──────────────────────────────────────────────────────────────────────

    /// <summary>A fence is not a room: whatever its footprint, one dimension is a wall's thickness.</summary>
    [Fact]
    public void AFenceIsNotARoom()
    {
        var f = new Fixture(_dir);
        int root = f.Fence(new Vector3(200, 0, 0));
        Assert.Null(f.RoomOf(root));
    }

    /// <summary>A stack of crates the size of a garage is not a garage: there is nowhere in it to stand.</summary>
    [Fact]
    public void ASolidBlockIsNotARoom()
    {
        var f = new Fixture(_dir);
        int root = f.SolidStack(new Vector3(-200, 0, 0));
        Assert.Null(f.RoomOf(root));
    }

    /// <summary>Two walls facing each other across a yard are not a room. Four faces of six is the line,
    /// on purpose: a walled courtyard does sound closer to a room than to a field.</summary>
    [Fact]
    public void TwoWallsAndSkyAreNotARoom()
    {
        var f = new Fixture(_dir);
        int root = f.TwoSides(new Vector3(0, 0, 200));
        Assert.Null(f.RoomOf(root));
    }

    // ── What it is made of ──────────────────────────────────────────────────────────────────────

    /// <summary>The room's materials are the walls' materials.</summary>
    [Fact]
    public void TheRoomTakesItsMaterialsFromTheParts()
    {
        var f = new Fixture(_dir);
        int concrete = f.Shed(Fixture.Clear, wall: "concrete_wall", floor: "concrete_floor");
        int carpeted = f.Shed(Fixture.Clear + new Vector3(60, 0, 0), wall: "carpet_wall", floor: "carpet_floor");

        var hard = f.RoomOf(concrete)!.Value;
        var soft = f.RoomOf(carpeted)!.Value;

        AcousticRegistry.TryGetResonanceIndex("Concrete", out int concreteIndex);
        AcousticRegistry.TryGetResonanceIndex("Carpet", out int carpetIndex);
        Assert.Equal(concreteIndex, hard.Materials[0]);     // floor
        Assert.Equal(carpetIndex, soft.Materials[0]);

        // One absorbs and the other does not.
        float hardAbsorption = AcousticRegistry.GetPropertiesByResonanceIndex(hard.Materials[0]).Absorption;
        float softAbsorption = AcousticRegistry.GetPropertiesByResonanceIndex(soft.Materials[0]).Absorption;
        Assert.True(softAbsorption > hardAbsorption * 4f,
                    $"a carpeted room ({softAbsorption:F2}) should swallow far more than a concrete one ({hardAbsorption:F2})");
    }

    // ── The parts of it that are not parts of it ────────────────────────────────────────────────

    /// <summary>The derived room is carried like a part but is not saved as one: it has no prefab to be
    /// rebuilt from.</summary>
    [Fact]
    public void TheDerivedRoomIsNeverSavedAsAPart()
    {
        var f = new Fixture(_dir);
        int root = f.Shed(Fixture.Clear);
        int parts = CompositeService.PartsOf(f.World, root).Count;
        Assert.Equal(parts + 1, CompositeService.MembersOf(f.World, root).Count);

        Assert.True(f.Composites.SaveAsTemplate(f.MapId, root, "shed", "builder", false, out int saved, out string error), error);
        Assert.Equal(parts, saved);
        Assert.True(f.Composites.Templates.TryGet("shed", out var template));
        Assert.All(template.Parts, p => Assert.False(string.IsNullOrWhiteSpace(p.PrefabId)));
    }

    /// <summary>A placed copy derives its own room.</summary>
    [Fact]
    public void APlacedCopyEnclosesItsOwnRoom()
    {
        var f = new Fixture(_dir);
        int root = f.Shed(Fixture.Clear);
        Assert.True(f.Composites.SaveAsTemplate(f.MapId, root, "shed", "builder", false, out _, out string error), error);

        int copy = f.Composites.Place(f.MapId, "shed", new Vector3(-300, 0, 150), Quaternion.Identity,
                                      "builder", out _, out error);
        Assert.True(copy >= 0, error);
        Assert.NotNull(f.RoomOf(copy));
    }

    /// <summary>Taking a building apart removes its room too.</summary>
    [Fact]
    public void UngroupingLeavesNoRoomBehind()
    {
        var f = new Fixture(_dir);
        int root = f.Shed(Fixture.Clear);
        Assert.NotNull(f.RoomOf(root));

        Assert.True(f.Composites.Ungroup(f.MapId, root, "builder", false, out _, out _, out string error), error);

        int orphans = 0;
        f.World.Query(new QueryDescription().WithAll<DerivedRoomComponent>(), (Entity _) => orphans++);
        Assert.Equal(0, orphans);
    }

    // ── All the way to the client ───────────────────────────────────────────────────────────────

    /// <summary>Derived on the server, put on the wire as the broadcast does, and found by the client's
    /// acoustic map where the listener asks.</summary>
    [Fact]
    public void TheRoomSurvivesTheTripToTheClientsAcousticMap()
    {
        var f = new Fixture(_dir);
        f.Shed(Fixture.Clear);

        var definitions = EntityDefinitionFactory.StaticDefinitions(f.World);
        var map = AcousticVolumeGenerator.GenerateRegions(definitions, new Vector3(400, 60, 400),
                                                          new Vector3(-200, -10, -200), 0.5f, 0.15f);

        int inside = map.VoxelGrid.GetRegionAt(Fixture.Clear + new Vector3(0, 1.7f, 0));
        Assert.NotEqual(AcousticConstants.GlobalRegionId, inside);
        Assert.True(map.Regions.TryGetValue(inside, out var region));
        Assert.Equal("shed", region.FriendlyName);

        // Twenty metres away is open air, or the room has swallowed the map.
        Assert.Equal(AcousticConstants.GlobalRegionId,
                     map.VoxelGrid.GetRegionAt(Fixture.Clear + new Vector3(20, 1.7f, 0)));
    }

    /// <summary>A shed with one side left off is still a room, and that side is open: no material, and an
    /// opening to the outdoors where it is. It used to take the material of whatever covered most of it,
    /// or Generic, and was a wall to everything that asked.</summary>
    [Fact]
    public void ACompositesOpenSideIsAnOpeningAndNotAWall()
    {
        var f = new Fixture(_dir);
        int root = f.Shed(Fixture.Clear, openEast: true);
        var room = f.RoomOf(root);
        Assert.NotNull(room);
        const int east = 4;
        Assert.Equal(RoomAcoustics.OpenFaceMaterial, room!.Value.Materials[east]);
        for (int face = 0; face < 6; face++)
            if (face != east) Assert.NotEqual(RoomAcoustics.OpenFaceMaterial, room.Value.Materials[face]);

        var definitions = EntityDefinitionFactory.StaticDefinitions(f.World);
        var map = AcousticVolumeGenerator.GenerateRegions(definitions, new Vector3(400, 60, 400),
                                                          new Vector3(-200, -10, -200), 0.5f, 0.15f);
        int roomId = f.RoomEntityId(root);
        var (position, _, size) = f.RoomVolume(root);
        var openings = map.Portals.Where(p => p.Key <= AcousticVolumeGenerator.FirstFaceOpeningId
                                              && (p.Value.Portal.RegionAId == roomId || p.Value.Portal.RegionBId == roomId)).ToList();
        var side = Assert.Single(openings);
        int beyond = side.Value.Portal.RegionAId == roomId ? side.Value.Portal.RegionBId : side.Value.Portal.RegionAId;
        Assert.Equal(map.GlobalEnvironmentId, beyond);
        // On the east face, across most of it (the north and south walls' ends take a little of each edge).
        var frame = map.OpeningFrames[side.Key];
        Assert.Equal(position.X + size.X / 2f, frame.Centre.X, 1);
        Assert.Equal(1f, Vector3.Transform(Vector3.UnitZ, frame.Rotation).X, 3);
        Assert.True(frame.Size.X > size.Z - 1.1f, $"the open side is {frame.Size.X:F2} m wide of {size.Z:F2}");
        Assert.True(frame.Size.Y > size.Y - 0.6f, $"the open side is {frame.Size.Y:F2} m high of {size.Y:F2}");
    }

    /// <summary>One put down after the bake gets its opening when its room arrives and loses it when the
    /// room goes.</summary>
    [Fact]
    public void AnOpenSidedRoomThatArrivesAfterTheBakeGetsItsOpening()
    {
        var f = new Fixture(_dir);
        int root = f.Shed(Fixture.Clear, openEast: true);
        int roomId = f.RoomEntityId(root);

        var client = new ClientWorldState();
        client.SetAcousticMap(AcousticVolumeGenerator.GenerateRegions(
            new List<EntityDefinition>(), new Vector3(400, 60, 400), new Vector3(-200, -10, -200), 0.5f, 0.15f));
        foreach (var part in CompositeService.PartsOf(f.World, root))
            client.RegisterDefinition(EntityDefinitionFactory.From(f.World, part));
        client.RegisterDefinition(EntityDefinitionFactory.From(f.World, f.Entity(roomId)));

        var frame = Assert.Single(client.AcousticMap!.OpeningFrames.Values);
        Assert.Equal(roomId, frame.Room);
        Assert.Equal(1f, Vector3.Transform(Vector3.UnitZ, frame.Rotation).X, 3);

        client.RemoveEntities(new[] { roomId });
        Assert.Empty(client.AcousticMap!.OpeningFrames);
        Assert.DoesNotContain(client.AcousticMap.Portals.Keys, id => id <= AcousticVolumeGenerator.FirstFaceOpeningId);
    }

    /// <summary>A room that turns up after the client's one bake of static geometry (a building put down
    /// later, a car's cabin) is registered at run time.</summary>
    [Fact]
    public void ARoomThatArrivesAfterTheBakeStillReachesTheAcousticMap()
    {
        var f = new Fixture(_dir);
        int root = f.Shed(Fixture.Clear, anchored: false);
        int roomId = f.RoomEntityId(root);

        var client = new ClientWorldState();
        client.SetAcousticMap(AcousticVolumeGenerator.GenerateRegions(
            new List<EntityDefinition>(), new Vector3(400, 60, 400), new Vector3(-200, -10, -200), 0.5f, 0.15f));
        Assert.False(client.AcousticMap!.Regions.ContainsKey(roomId), "it was baked after all; this proves nothing");

        client.RegisterDefinition(EntityDefinitionFactory.From(f.World, f.Entity(roomId)));
        Assert.True(client.AcousticMap!.Regions.ContainsKey(roomId), "the room never reached the acoustic map");
        Assert.Equal("shed", client.AcousticMap.Regions[roomId].FriendlyName);

        // And it goes with the thing that enclosed it.
        client.RemoveEntities(new[] { roomId });
        Assert.False(client.AcousticMap!.Regions.ContainsKey(roomId));
    }

    // ── Fixture ─────────────────────────────────────────────────────────────────────────────────

    private sealed class Fixture
    {
        /// <summary>Across the flats of the shed, metres. A 10 m floor slab with walls around it.</summary>
        public const float ShedSpan = 10f;

        /// <summary>Empty ground, clear of the shipped map's own floor and grass, which a sweep may
        /// take.</summary>
        public static readonly Vector3 Clear = new(140, 0, 140);

        public readonly MapManager Maps;
        public readonly CompositeService Composites;
        public readonly string MapId = "default";
        public World World = null!;
        public Dictionary<int, Entity> Lookup = null!;

        private readonly PrefabRepository _prefabs;

        public Fixture(string dir)
        {
            string mapDir = Path.Combine(dir, "maps");
            Directory.CreateDirectory(mapDir);
            foreach (string file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "maps"), "*.json"))
                File.Copy(file, Path.Combine(mapDir, Path.GetFileName(file)), overwrite: true);

            _prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
            Maps = new MapManager(new MapRepository(mapDir), _prefabs);
            Maps.Initialize();
            Composites = new CompositeService(Maps, _prefabs, new CompositeRepository(Path.Combine(dir, "composites")));
            Assert.True(Maps.TryGetMap(MapId, out World, out _, out _, out Lookup));
            AcousticRegistry.Initialize();
        }

        public Entity Entity(int id) => Lookup[id];

        /// <summary>The room a composite derived, or null if it decided it was not one.</summary>
        public RegionComponent? RoomOf(int rootId)
        {
            foreach (var member in CompositeService.MembersOf(World, rootId))
                if (World.Has<DerivedRoomComponent>(member) && World.Has<RegionComponent>(member))
                    return World.Get<RegionComponent>(member);
            return null;
        }

        /// <summary>The entity carrying the derived room.</summary>
        public int RoomEntityId(int rootId)
        {
            foreach (var member in CompositeService.MembersOf(World, rootId))
                if (World.Has<DerivedRoomComponent>(member)) return member.Id;
            throw new InvalidOperationException("that composite has no room");
        }

        /// <summary>Where that room is in the world.</summary>
        public (Vector3 Position, Quaternion Rotation, Vector3 Size) RoomVolume(int rootId)
        {
            foreach (var member in CompositeService.MembersOf(World, rootId))
                if (World.Has<DerivedRoomComponent>(member))
                {
                    var t = World.Get<Transform>(member);
                    return (t.Position, t.Rotation, World.Get<RegionComponent>(member).RoomSize);
                }
            throw new InvalidOperationException("that composite has no room");
        }

        /// <summary>A shed: a floor, a roof, and walls all the way round.</summary>
        public int Shed(Vector3 where, bool anchored = true, string wall = "concrete_wall", string floor = "concrete_floor",
                        bool openEast = false)
        {
            const float half = ShedSpan / 2f;
            const float height = 3f;

            Spawn(floor, where + new Vector3(0, 0.05f, 0), Quaternion.Identity);
            Spawn(floor, where + new Vector3(0, height, 0), Quaternion.Identity);

            // Five two-metre panels a side, turned to face inward on the east and west runs.
            var acrossZ = Quaternion.Identity;
            var acrossX = Quaternion.CreateFromYawPitchRoll(MathF.PI / 2f, 0f, 0f);
            for (int i = 0; i < 5; i++)
            {
                float along = -half + 1f + i * 2f;
                Spawn(wall, where + new Vector3(along, height / 2f, +half), acrossZ);
                Spawn(wall, where + new Vector3(along, height / 2f, -half), acrossZ);
                if (!openEast) Spawn(wall, where + new Vector3(+half, height / 2f, along), acrossX);
                Spawn(wall, where + new Vector3(-half, height / 2f, along), acrossX);
            }

            int root = Composites.Group(MapId, where, ShedSpan, "shed", anchored, "builder", out int parts);
            Assert.True(root >= 0, "the shed could not be grouped");
            Assert.Equal(openEast ? 17 : 22, parts);
            return root;
        }

        /// <summary>A run of hoardings with gaps, a wall's thickness deep. Spaced so the hollowness rule
        /// passes and only the minimum-dimension rule (half a metre thick) keeps it from being a room.</summary>
        public int Fence(Vector3 where)
        {
            for (int i = 0; i < 5; i++)
                Spawn("concrete_wall", where + new Vector3(-8f + i * 4f, 1.5f, 0f), Quaternion.Identity);
            int root = Composites.Group(MapId, where, 12f, "fence", true, "builder", out int parts);
            Assert.Equal(5, parts);
            return root;
        }

        /// <summary>Walls packed shoulder to shoulder in a block. Room-sized, and no room in it.</summary>
        public int SolidStack(Vector3 where)
        {
            for (int x = 0; x < 3; x++)
                for (int z = 0; z < 6; z++)
                    Spawn("concrete_wall", where + new Vector3(-2f + x * 2f, 1.5f, -1.25f + z * 0.5f), Quaternion.Identity);
            int root = Composites.Group(MapId, where, 8f, "block", true, "builder", out int parts);
            Assert.Equal(18, parts);
            return root;
        }

        /// <summary>Two walls facing each other across open ground, with sky above and nothing at the ends.</summary>
        public int TwoSides(Vector3 where)
        {
            for (int i = 0; i < 5; i++)
            {
                Spawn("concrete_wall", where + new Vector3(-4f + i * 2f, 1.5f, +5f), Quaternion.Identity);
                Spawn("concrete_wall", where + new Vector3(-4f + i * 2f, 1.5f, -5f), Quaternion.Identity);
            }
            int root = Composites.Group(MapId, where, 9f, "yard", true, "builder", out int parts);
            Assert.Equal(10, parts);
            return root;
        }

        private void Spawn(string prefabId, Vector3 at, Quaternion rotation)
            => Assert.NotEqual(Arch.Core.Entity.Null,
                               Maps.SpawnEntity(MapId, w => _prefabs.Spawn(w, prefabId, at, rotation, Vector3.One)));
    }
}

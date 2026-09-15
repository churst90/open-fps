using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// Building something you can be INSIDE, without anyone authoring the inside of it.
///
/// Put four walls, a floor and a roof around yourself and you are indoors. That is a fact about the
/// geometry, not a property somebody remembered to tick, and it has to be true of a shed, a
/// cathedral and the cab of a lorry by the same rule — otherwise every building a player makes is
/// silent inside until a developer visits it.
///
/// These hold the three things that make a composite a room (big enough to be in, mostly empty,
/// mostly covered), the things that are NOT rooms and must not become them, that the room is made of
/// what the walls are made of, and that the whole of it survives the trip to the client's acoustic
/// map — which is the only place any of it actually matters.
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

        // It is the size of the thing, not of a guess.
        Assert.Equal(Fixture.ShedSpan, room.Value.RoomSize.X, 0);
        Assert.Equal(Fixture.ShedSpan, room.Value.RoomSize.Z, 0);
        Assert.True(room.Value.RoomSize.Y > 2.5f, $"a room {room.Value.RoomSize.Y:F1} m high is a crawlspace");
    }

    /// <summary>
    /// The room sits at the middle of the space, not at the composite's origin.
    ///
    /// A composite's origin is where it meets the GROUND — that is what makes a house placed at your
    /// feet have its floor at your feet. Put the room volume there and half of it is underground and
    /// its ceiling is at your knees, so standing up in your own building puts you outdoors.
    /// </summary>
    [Fact]
    public void TheRoomIsWhereTheSpaceIsAndNotWhereTheOriginIs()
    {
        var f = new Fixture(_dir);
        int root = f.Shed(new Vector3(120, 0, -60));

        var (position, rotation, size) = f.RoomVolume(root);
        Assert.True(position.Y > 1.0f, $"the room's middle is {position.Y:F2} m up, so it is buried");

        // Standing height, in the middle of it, is inside. That is the test the client actually runs.
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

    /// <summary>A fence is a wall with more wall next to it. Whatever its footprint, one of its
    /// dimensions is a wall's thickness, and you cannot be inside that.</summary>
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

    /// <summary>Two walls facing each other across a yard are not a room. Four of the six faces is
    /// the line, and it is drawn there on purpose — a walled courtyard genuinely does sound closer to
    /// a room than to a field.</summary>
    [Fact]
    public void TwoWallsAndSkyAreNotARoom()
    {
        var f = new Fixture(_dir);
        int root = f.TwoSides(new Vector3(0, 0, 200));
        Assert.Null(f.RoomOf(root));
    }

    // ── What it is made of ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The room is made of the walls. Nobody authors that either: a carpeted room is dead and a
    /// concrete one rings because of what somebody built them out of.
    /// </summary>
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

        // And the thing that matters about the difference: one of them absorbs and the other does not.
        float hardAbsorption = AcousticRegistry.GetPropertiesByResonanceIndex(hard.Materials[0]).Absorption;
        float softAbsorption = AcousticRegistry.GetPropertiesByResonanceIndex(soft.Materials[0]).Absorption;
        Assert.True(softAbsorption > hardAbsorption * 4f,
                    $"a carpeted room ({softAbsorption:F2}) should swallow far more than a concrete one ({hardAbsorption:F2})");
    }

    // ── The parts of it that are not parts of it ────────────────────────────────────────────────

    /// <summary>
    /// The room is carried like a part and is not one. Nobody built it, it cannot be rebuilt from a
    /// prefab, and a template that tried to save it would fail on a wall it never had.
    /// </summary>
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

    /// <summary>...and a placed copy derives its own, so a shed put down twice is a room twice.</summary>
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

    /// <summary>Taking a building apart takes its inside apart too. Left behind, it is an invisible
    /// volume in an empty field that still sounds like a room.</summary>
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

    /// <summary>
    /// The only place any of this matters is the client's acoustic map, so the test goes all the way
    /// there: derive it on the server, put it on the wire as the broadcast would, and ask the map the
    /// question the listener asks every frame.
    /// </summary>
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

    /// <summary>
    /// A room that turns up AFTER the bake.
    ///
    /// The client bakes its acoustic map once, from the static geometry the server streams at map
    /// load. A building somebody puts down while you are standing there is not in that, and the
    /// inside of a car never can be — it moves, so it is not static geometry by definition. Without
    /// the runtime registration the map never hears of either and stepping inside sounds like
    /// stepping nowhere.
    /// </summary>
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

        // ...and goes again with the thing that enclosed it, or an empty field keeps sounding like a shed.
        client.RemoveEntities(new[] { roomId });
        Assert.False(client.AcousticMap!.Regions.ContainsKey(roomId));
    }

    // ── Fixture ─────────────────────────────────────────────────────────────────────────────────

    private sealed class Fixture
    {
        /// <summary>Across the flats of the shed, metres. A 10 m floor slab with walls around it.</summary>
        public const float ShedSpan = 10f;

        /// <summary>Empty ground, well clear of the shipped map's own floor and grass — a sweep is
        /// entitled to take those and this is not the test that says so.</summary>
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

        /// <summary>Where that room actually is in the world — the question the listener asks.</summary>
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
        public int Shed(Vector3 where, bool anchored = true, string wall = "concrete_wall", string floor = "concrete_floor")
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
                Spawn(wall, where + new Vector3(+half, height / 2f, along), acrossX);
                Spawn(wall, where + new Vector3(-half, height / 2f, along), acrossX);
            }

            int root = Composites.Group(MapId, where, ShedSpan, "shed", anchored, "builder", out int parts);
            Assert.True(root >= 0, "the shed could not be grouped");
            Assert.Equal(22, parts);
            return root;
        }

        /// <summary>
        /// A run of hoardings in a line with gaps between them. Long, tall, airy, and a wall's
        /// thickness deep.
        ///
        /// Spaced rather than butted together on purpose: shoulder to shoulder it is dense enough
        /// that the hollowness rule throws it out, which would leave the minimum-dimension rule
        /// untested and passing for free. With gaps it is hollow, its six faces are all "covered",
        /// and the only thing standing between it and being a room is that it is half a metre thick.
        /// </summary>
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

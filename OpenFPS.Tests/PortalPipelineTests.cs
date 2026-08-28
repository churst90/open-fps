using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Common.Systems;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;

namespace OpenFPS.Tests;

/// <summary>
/// End-to-end cover for the portal pipeline: map JSON -> PrefabRepository/MapManager -> ECS components
/// -> streamed EntityDefinition -> client-side AcousticMap.
///
/// This existed because every stage of that chain silently dropped authored portals: the `portal` prefab
/// carried no portal fields, so PrefabRepository never attached a PortalComponent, so MapManager's linking
/// pass (which only wrote into an existing component) had nothing to write into, so every EntityDefinition
/// reported ApertureSize 0, so AcousticVolumeGenerator registered no portals at all. Nothing threw and
/// nothing logged; the only symptom was that room reverb arrived from the wrong wall.
/// </summary>
public class PortalPipelineTests
{
    // Positions authored in maps/default.json, used to identify entities without depending on ECS ids.
    private static readonly Vector3 RoomAConcrete = new(-7, 2, 15);
    private static readonly Vector3 RoomBWood = new(7, 2, 15);
    private static readonly Vector3 LabGym = new(5, 2, 35);
    private static readonly Vector3 LabCarpet = new(-5, 2, 35);

    private static readonly Lazy<LoadedMap> Map = new(LoadDefaultMap, isThreadSafe: true);

    private sealed record LoadedMap(List<EntityDefinition> Definitions, Vector3 Size, Vector3 MinBound);

    private static LoadedMap LoadDefaultMap()
    {
        // maps/ and prefabs/ are copied next to the test assembly from the real server data, so this
        // exercises the shipped content rather than a fixture that can drift away from it.
        var prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
        var maps = new MapRepository(Path.Combine(AppContext.BaseDirectory, "maps"));
        var manager = new MapManager(maps, prefabs);
        manager.Initialize();

        Assert.True(manager.TryGetMap("default", out World world, out Vector3 size, out _, out _),
            "maps/default.json failed to load — check it was copied to the test output.");

        var data = manager.GetAllMaps().First(kv => kv.Key == "default").Value.data;
        return new LoadedMap(EntityDefinitionFactory.StaticDefinitions(world), size, data.MinBound);
    }

    private static EntityDefinition RegionAt(IEnumerable<EntityDefinition> defs, Vector3 position) =>
        defs.Single(d => d.Region.RoomSize.X > 0 && Vector3.Distance(d.Transform.Position, position) < 0.01f);

    private static EntityDefinition PortalAt(IEnumerable<EntityDefinition> defs, Vector3 position) =>
        defs.Single(d => d.Portal.ApertureSize > 0 && Vector3.Distance(d.Transform.Position, position) < 0.01f);

    // --- The regression itself -------------------------------------------------------------------

    [Fact]
    public void DefaultMap_AuthoredPortals_ReachTheEcsAsPortalComponents()
    {
        var portals = Map.Value.Definitions.Where(d => d.Portal.ApertureSize > 0).ToList();

        Assert.Equal(4, portals.Count);
    }

    [Fact]
    public void DefaultMap_PortalsAreAtTheAuthoredDoorways_NotOnArbitraryWalls()
    {
        var defs = Map.Value.Definitions;

        // Both demo rooms open SOUTH at z=10; the doorway, not the east wall, is where sound gets out.
        Assert.Equal(2.0f, PortalAt(defs, new Vector3(-7, 1, 10)).Portal.ApertureSize, 3);
        Assert.Equal(2.0f, PortalAt(defs, new Vector3(7, 1, 10)).Portal.ApertureSize, 3);
        // Material lab: the two halves are open to each other at x=0, and the gym has a south entry.
        Assert.Equal(8.0f, PortalAt(defs, new Vector3(0, 2, 35)).Portal.ApertureSize, 3);
        Assert.Equal(4.0f, PortalAt(defs, new Vector3(5, 1, 30)).Portal.ApertureSize, 3);
    }

    [Fact]
    public void DefaultMap_PortalRegionIds_ResolveToTheRoomsTheyJoin()
    {
        var defs = Map.Value.Definitions;

        int roomA = RegionAt(defs, RoomAConcrete).EntityId;
        int roomB = RegionAt(defs, RoomBWood).EntityId;
        int gym = RegionAt(defs, LabGym).EntityId;
        int carpet = RegionAt(defs, LabCarpet).EntityId;

        AssertJoins(PortalAt(defs, new Vector3(-7, 1, 10)), AcousticConstants.GlobalRegionId, roomA);
        AssertJoins(PortalAt(defs, new Vector3(7, 1, 10)), AcousticConstants.GlobalRegionId, roomB);
        AssertJoins(PortalAt(defs, new Vector3(0, 2, 35)), gym, carpet);
        AssertJoins(PortalAt(defs, new Vector3(5, 1, 30)), AcousticConstants.GlobalRegionId, gym);
    }

    private static void AssertJoins(EntityDefinition portal, int expectedA, int expectedB)
    {
        var actual = new[] { portal.Portal.RegionAId, portal.Portal.RegionBId };
        Assert.Contains(expectedA, actual);
        Assert.Contains(expectedB, actual);
        Assert.NotEqual(portal.Portal.RegionAId, portal.Portal.RegionBId);
    }

    // --- The client-side acoustic map built from those definitions -------------------------------

    [Fact]
    public void DefaultMap_AcousticMap_RegistersEveryAuthoredPortalAndGuessesNothing()
    {
        var m = Map.Value;
        var acoustic = AcousticVolumeGenerator.GenerateRegions(m.Definitions, m.Size, m.MinBound, 0.5f, 0.15f);

        Assert.Equal(4, acoustic.Portals.Count);
        // Synthesized portals use negative keys from -1000 down; authored ones keep their entity id.
        Assert.All(acoustic.Portals.Keys, id => Assert.True(id > 0, $"Portal {id} was guessed, not authored."));

        foreach (var doorway in new[] { new Vector3(-7, 1, 10), new Vector3(7, 1, 10), new Vector3(0, 2, 35), new Vector3(5, 1, 30) })
            Assert.Contains(acoustic.Portals.Values, p => Vector3.Distance(p.Position, doorway) < 0.01f);
    }

    [Fact]
    public void DefaultMap_AcousticMap_FindsTheFourRoomsPlusTheOutside()
    {
        var m = Map.Value;
        var acoustic = AcousticVolumeGenerator.GenerateRegions(m.Definitions, m.Size, m.MinBound, 0.5f, 0.15f);

        Assert.Equal(5, acoustic.Regions.Count); // 4 rooms + the global "Outside" environment
        Assert.True(acoustic.Regions.ContainsKey(AcousticConstants.GlobalRegionId));
    }

    [Fact]
    public void ListenerInsideARoom_ResolvesToThatRoomsRegion()
    {
        var m = Map.Value;
        var acoustic = AcousticVolumeGenerator.GenerateRegions(m.Definitions, m.Size, m.MinBound, 0.5f, 0.15f);

        int roomA = RegionAt(m.Definitions, RoomAConcrete).EntityId;
        Assert.Equal(roomA, acoustic.VoxelGrid.GetRegionAt(new Vector3(-7, 1.7f, 15)));
        // Just outside the south doorway is open air.
        Assert.Equal(AcousticConstants.GlobalRegionId, acoustic.VoxelGrid.GetRegionAt(new Vector3(-7, 1.7f, 8)));
    }

    // --- Guard rails on the pieces that failed ---------------------------------------------------

    [Fact]
    public void PortalPrefab_AloneYieldsAPortalComponent()
    {
        // The prefab must be self-describing: spawning it outside of map loading (an editor, a console
        // command) has to produce something that is actually a portal.
        var repo = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
        var world = World.Create();
        try
        {
            var entity = repo.Spawn(world, "portal", Vector3.Zero, Quaternion.Identity, Vector3.One);
            Assert.True(world.Has<PortalComponent>(entity), "prefabs/portal.json no longer produces a PortalComponent.");
            Assert.True(world.Get<PortalComponent>(entity).ApertureSize > 0);
        }
        finally { World.Destroy(world); }
    }

    [Fact]
    public void UnlinkedPortal_ProbesTheVoxelGridForTheRoomsItJoins()
    {
        // An author who drops a portal in a doorway without naming the rooms should still get a link.
        var region = new EntityDefinition
        {
            EntityId = 42,
            Transform = new Transform { Position = new Vector3(0, 2, 0), Rotation = Quaternion.Identity, Scale = Vector3.One },
            Region = new RegionComponent { FriendlyName = "Room", IsIndoor = true, RoomSize = new Vector3(10, 5, 10) }
        };
        var portal = new EntityDefinition
        {
            EntityId = 43,
            Transform = new Transform { Position = new Vector3(0, 2, 5), Rotation = Quaternion.Identity, Scale = Vector3.One },
            Portal = new PortalComponent { RegionAId = -1, RegionBId = -1, ApertureSize = 2f }
        };

        var acoustic = AcousticVolumeGenerator.GenerateRegions(
            new[] { region, portal }, new Vector3(100, 40, 100), new Vector3(-50, 0, -50));

        Assert.True(acoustic.Portals.TryGetValue(43, out var linked), "Unlinked portal was dropped instead of probed.");
        var ends = new[] { linked.Portal.RegionAId, linked.Portal.RegionBId };
        Assert.Contains(42, ends);
        Assert.Contains(AcousticConstants.GlobalRegionId, ends);
    }

    [Fact]
    public void AutoDiscovery_IsOptIn_AndOnlyFillsBoundariesTheMapDidNotDescribe()
    {
        var m = Map.Value;

        var off = AcousticVolumeGenerator.GenerateRegions(m.Definitions, m.Size, m.MinBound, 0.5f, 0.15f);
        var on = AcousticVolumeGenerator.GenerateRegions(m.Definitions, m.Size, m.MinBound, 0.5f, 0.15f, autoDiscoverPortals: true);

        Assert.Equal(4, off.Portals.Count);
        // The carpet half of the lab has no authored opening to the outside, so enabling the guess adds
        // one there — on a wall, which is exactly why this is off by default.
        Assert.True(on.Portals.Count > off.Portals.Count);
        Assert.Contains(on.Portals.Keys, id => id <= -1000);
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using OpenFPS.Server.Systems;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// Building a thing out of parts, and it still being there tomorrow.
///
/// A map, a house, a vehicle and an object somebody invented are the same idea at four scales: a set
/// of entities with a local origin, which can be saved, placed again, owned and entered. These tests
/// hold the four verbs that make that true — group, ungroup, save, place — and the one property that
/// makes it worth anything, which is that a building survives a restart.
/// </summary>
public class CompositeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"openfps-composites-{Guid.NewGuid():N}");

    public void Dispose() { try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void GroupingMakesOneThingOutOfWhatIsStandingThere()
    {
        var (svc, maps, mapId) = Build();
        var walls = Walls(maps, mapId, new Vector3(40, 0, 40), 4);

        int root = svc.Group(mapId, new Vector3(40, 0, 40), radius: 10f, "shed", anchored: true, "builder", out int parts);
        Assert.True(root >= 0);
        Assert.Equal(4, parts);

        Assert.True(maps.TryGetMap(mapId, out var world, out _, out _, out var lookup));
        Assert.True(lookup.ContainsKey(root));
        Assert.Equal("shed", world.Get<CompositeComponent>(lookup[root]).Name);

        // Every wall is now a member, and none of them moved to become one.
        foreach (var (entity, wasAt) in walls)
        {
            Assert.True(world.Has<ParentComponent>(entity));
            Assert.Equal(root, world.Get<ParentComponent>(entity).ParentEntityId);
            Assert.Equal(wasAt, world.Get<Transform>(entity).Position);
        }
    }

    /// <summary>
    /// The origin is where the thing meets the ground, not its middle. A house placed at your feet
    /// should have its floor at your feet — otherwise every placement buries or floats the building.
    /// </summary>
    [Fact]
    public void TheOriginSitsOnTheGroundUnderTheMiddleOfIt()
    {
        var (svc, maps, mapId) = Build();
        Walls(maps, mapId, new Vector3(100, 0, 100), 4, height: 3f);

        int root = svc.Group(mapId, new Vector3(100, 0, 100), 10f, "hut", true, "builder", out _);
        Assert.True(maps.TryGetMap(mapId, out var world, out _, out _, out var lookup));
        var origin = world.Get<Transform>(lookup[root]).Position;

        Assert.Equal(100f, origin.X, 1);
        Assert.Equal(100f, origin.Z, 1);
        Assert.Equal(0f, origin.Y, 1);   // the lowest part, not the average
    }

    [Fact]
    public void UngroupingLeavesEverythingExactlyWhereItWas()
    {
        var (svc, maps, mapId) = Build();
        var walls = Walls(maps, mapId, new Vector3(60, 0, 60), 3);
        int root = svc.Group(mapId, new Vector3(60, 0, 60), 10f, "stall", true, "builder", out _);

        Assert.True(svc.Ungroup(mapId, root, "builder", false, out string name, out int parts, out _));
        Assert.Equal("stall", name);
        Assert.Equal(3, parts);

        Assert.True(maps.TryGetMap(mapId, out var world, out _, out _, out var lookup));
        Assert.False(lookup.ContainsKey(root));
        foreach (var (entity, wasAt) in walls)
        {
            Assert.False(world.Has<ParentComponent>(entity));
            Assert.Equal(wasAt, world.Get<Transform>(entity).Position);
        }
    }

    /// <summary>
    /// Build something, save it, place it somewhere else: the arrangement comes with it, not the
    /// coordinates. That is the whole of "classify it as an object later".
    /// </summary>
    [Fact]
    public void SavingThenPlacingReproducesTheArrangementSomewhereElse()
    {
        var (svc, maps, mapId) = Build();
        Walls(maps, mapId, new Vector3(-80, 0, -80), 4);
        int root = svc.Group(mapId, new Vector3(-80, 0, -80), 10f, "Cabin", true, "builder", out int built);

        Assert.True(svc.SaveAsTemplate(mapId, root, "cabin", "builder", false, out int saved, out string error), error);
        Assert.Equal(built, saved);

        // It is now on disk and placeable by id.
        Assert.True(svc.Templates.TryGet("cabin", out var template));
        Assert.Equal(built, template.Parts.Count);

        var where = new Vector3(150, 0, -20);
        int placed = svc.Place(mapId, "cabin", where, Quaternion.Identity, "tester", out int parts, out error);
        Assert.True(placed >= 0, error);
        Assert.Equal(built, parts);

        // The parts arrived in the same shape, around the new origin.
        Assert.True(maps.TryGetMap(mapId, out var world, out _, out _, out _));
        var members = CompositeService.MembersOf(world, placed);
        Assert.Equal(built, members.Count);
        foreach (var m in members)
        {
            var p = world.Get<Transform>(m).Position;
            Assert.True(Vector3.Distance(p, where) < 12f, $"a part landed {Vector3.Distance(p, where):F1} m from the origin");
        }
    }

    /// <summary>
    /// A composite that is not anchored is carried by its root, and ParentSystem — which has run
    /// every tick since long before any of this — is what carries it. A house and a vehicle body are
    /// the same structure; only whether anything moves the root differs.
    /// </summary>
    [Fact]
    public void MovingAFreeCompositeCarriesItsParts()
    {
        var (svc, maps, mapId) = Build();
        Walls(maps, mapId, new Vector3(0, 0, 200), 3);
        int root = svc.Group(mapId, new Vector3(0, 0, 200), 10f, "caravan", anchored: false, "builder", out _);

        Assert.True(maps.TryGetMap(mapId, out var world, out _, out _, out var lookup));
        Assert.False(world.Get<CompositeComponent>(lookup[root]).Anchored);

        var before = CompositeService.MembersOf(world, root)
            .ToDictionary(m => m, m => world.Get<Transform>(m).Position);

        ref var t = ref world.Get<Transform>(lookup[root]);
        t.Position += new Vector3(25, 0, 0);
        ParentSystem.Update(world, lookup);

        foreach (var kv in before)
            Assert.Equal(kv.Value + new Vector3(25, 0, 0), world.Get<Transform>(kv.Key).Position);
    }

    /// <summary>A house cannot swallow the person building it, or the car parked outside.</summary>
    [Fact]
    public void GroupingLeavesPlayersAndVehiclesAlone()
    {
        var (svc, maps, mapId) = Build();
        Walls(maps, mapId, new Vector3(300, 0, 0), 2);
        Assert.True(maps.TryGetMap(mapId, out var world, out _, out _, out _));

        var player = world.Create(
            new Transform { Position = new Vector3(300, 0, 1), Rotation = Quaternion.Identity },
            new PlayerComponent { ConnectionId = 9, Username = "builder" }, EntityType.Player);
        var car = world.Create(
            new Transform { Position = new Vector3(301, 0, 0), Rotation = Quaternion.Identity },
            new VehicleComponent(), EntityType.StaticObject);
        maps.IndexEntity(mapId, player);
        maps.IndexEntity(mapId, car);

        svc.Group(mapId, new Vector3(300, 0, 0), 10f, "wall", true, "builder", out int parts);
        Assert.Equal(2, parts);
        Assert.False(world.Has<ParentComponent>(player));
        Assert.False(world.Has<ParentComponent>(car));
    }

    /// <summary>
    /// The property that makes a building a building: it is still there after a restart.
    ///
    /// Placing records the placement on the map's own data; saving the map commits it; loading it
    /// again rebuilds it. This walks the whole of that, through the real files.
    /// </summary>
    [Fact]
    public void APlacedBuildingSurvivesARestart()
    {
        var (svc, maps, mapId) = Build();
        Walls(maps, mapId, new Vector3(-200, 0, 90), 4);
        int root = svc.Group(mapId, new Vector3(-200, 0, 90), 10f, "Hut", true, "builder", out int built);
        Assert.True(svc.SaveAsTemplate(mapId, root, "hut", "builder", false, out _, out string error), error);

        var where = new Vector3(-320, 0, 40);
        Assert.True(svc.Place(mapId, "hut", where, Quaternion.Identity, "tester", out _, out error) >= 0, error);

        // The map now knows it is there...
        Assert.True(maps.TryGetMapData(mapId, out var data));
        Assert.NotNull(data.Composites);
        Assert.Contains(data.Composites!, p => p.TemplateId == "hut" && p.Owner == "tester");

        // ...and saying so on disk is what makes it permanent.
        Assert.True(maps.SaveMap(mapId, out error), error);

        // A second server over the same files: the restart.
        var (svc2, maps2, _) = Build();
        Assert.True(maps2.TryGetMapData(mapId, out var reloaded));
        Assert.Contains(reloaded.Composites ?? new List<CompositePlacement>(), p => p.TemplateId == "hut");

        svc2.PlaceRecorded(maps2);
        Assert.True(maps2.TryGetMap(mapId, out var world2, out _, out _, out _));
        int standing = 0;
        var q = new QueryDescription().WithAll<Transform, CompositeComponent>();
        world2.Query(in q, (Entity e, ref Transform t, ref CompositeComponent c) =>
        { if (c.TemplateId == "hut" && Vector3.Distance(t.Position, where) < 0.5f) standing++; });
        Assert.Equal(1, standing);
    }

    // ── Fixtures ────────────────────────────────────────────────────────────────────────────────

    private string _mapDir = "";

    private (CompositeService svc, MapManager maps, string mapId) Build()
    {
        // The shipped maps are copied beside the test assembly; work on a COPY so a test that saves a
        // map cannot rewrite the one that ships.
        if (string.IsNullOrEmpty(_mapDir))
        {
            _mapDir = Path.Combine(_dir, "maps");
            Directory.CreateDirectory(_mapDir);
            foreach (string f in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "maps"), "*.json"))
                File.Copy(f, Path.Combine(_mapDir, Path.GetFileName(f)), overwrite: true);
        }

        var prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
        var mapRepo = new MapRepository(_mapDir);
        var maps = new MapManager(mapRepo, prefabs);
        maps.Initialize();
        var composites = new CompositeRepository(Path.Combine(_dir, "composites"));
        return (new CompositeService(maps, prefabs, composites), maps, "default");
    }

    /// <summary>A row of walls, and where each one started, so a test can check nothing moved.</summary>
    private static List<(Entity Entity, Vector3 At)> Walls(MapManager maps, string mapId, Vector3 centre,
                                                           int count, float height = 0f)
    {
        var made = new List<(Entity, Vector3)>();
        var prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
        for (int i = 0; i < count; i++)
        {
            // Spread symmetrically about the centre, so a test can say where the middle is.
            var at = centre + new Vector3((i - (count - 1) / 2f) * 2f, i == 0 ? 0f : height, 0f);
            var e = maps.SpawnEntity(mapId, w => prefabs.Spawn(w, "concrete_wall", at, Quaternion.Identity, Vector3.One));
            made.Add((e, at));
        }
        return made;
    }
}

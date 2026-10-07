using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// Geometry stage 2 (docs/GEOMETRY.md section 10) and the decisions taken on stage 1: the ground a map
/// gets when it has none, the turns made unit length at load.
/// </summary>
public class GeometryStage2Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "openfps-geo2-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private MapManager Load(params string[] mapIds)
    {
        string mapDir = Path.Combine(_dir, Guid.NewGuid().ToString("N"), "maps");
        Directory.CreateDirectory(mapDir);
        foreach (var id in mapIds)
            File.Copy(Path.Combine(AppContext.BaseDirectory, "maps", id + ".json"), Path.Combine(mapDir, id + ".json"));
        var maps = new MapManager(new MapRepository(mapDir), new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs")));
        maps.Initialize();
        return maps;
    }

    private static List<Entity> Grounds(World world, Dictionary<int, Entity> lookup)
        => lookup.Values.Where(e => world.Has<IdentityComponent>(e) && world.Get<IdentityComponent>(e).Name == "Ground").ToList();

    /// <summary>The city lays its own ground over all of its play area: the loader adds nothing under
    /// it, so nothing lies flush under the map's own dirt.</summary>
    [Fact]
    public void AMapWithItsOwnGroundGetsNoFoundation()
    {
        var maps = Load("city");
        Assert.True(maps.TryGetMap("city", out var world, out _, out _, out var lookup));
        var grounds = Grounds(world, lookup);
        Assert.Single(grounds);
        Assert.Equal("Dirt", world.Get<MaterialComponent>(grounds[0]).Material);
        Assert.DoesNotContain(lookup.Values, e => world.Has<IdentityComponent>(e) && world.Get<IdentityComponent>(e).PrefabId == "concrete_floor"
                                                  && world.Get<ColliderComponent>(e).Size.X > 1000f);
    }

    /// <summary>The speedway lays patches (grass, the track) but no ground under all of it: the loader
    /// lays natural ground, dirt, under its bounds, its top at 0.</summary>
    [Fact]
    public void AMapWithoutGroundGetsDirt()
    {
        var maps = Load("speedway");
        Assert.True(maps.TryGetMap("speedway", out var world, out _, out _, out var lookup));
        Assert.True(maps.TryGetMapData("speedway", out var data));
        var ground = Assert.Single(Grounds(world, lookup));
        Assert.Equal("Dirt", world.Get<MaterialComponent>(ground).Material);
        var t = world.Get<Transform>(ground); var c = world.Get<ColliderComponent>(ground);
        Assert.Equal(0f, t.Position.Y + c.Size.Y / 2f, 4);
        Assert.True(c.Size.X >= data.MaxBound.X - data.MinBound.X - 0.01f && c.Size.Z >= data.MaxBound.Z - data.MinBound.Z - 0.01f);
    }

    /// <summary>A new map (/map new) is dirt, and its own: nothing is laid under it.</summary>
    [Fact]
    public void ANewMapIsDirt()
    {
        string mapDir = Path.Combine(_dir, "new", "maps");
        Directory.CreateDirectory(mapDir);
        var maps = new MapManager(new MapRepository(mapDir), new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs")));
        maps.Initialize();
        Assert.True(maps.CreateMap(MapTemplates.Flat("plot", "tester"), out string error), error);
        Assert.True(maps.TryGetMap("plot", out var world, out _, out _, out var lookup));
        var solids = lookup.Values.Where(e => world.Has<ColliderComponent>(e) && world.Get<ColliderComponent>(e).IsSolid).ToList();
        var ground = Assert.Single(solids);
        Assert.Equal("Dirt", world.Get<MaterialComponent>(ground).Material);
        Assert.Equal("Ground", world.Get<IdentityComponent>(ground).Name);
    }

    /// <summary>Maps write turns in six digits (Magnolia's 0.707082, 0.707131 is not unit length); every
    /// one is unit length once loaded, and it is the same turn. A turn left out is the identity.</summary>
    [Fact]
    public void TurnsAreUnitLengthOnceLoaded()
    {
        string mapDir = Path.Combine(_dir, "turns", "maps");
        Directory.CreateDirectory(mapDir);
        var maps = new MapManager(new MapRepository(mapDir), new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs")));
        maps.Initialize();
        var map = MapTemplates.Flat("turns", "tester");
        var written = new Quaternion(0f, 0.707082f, 0f, 0.707131f);
        Assert.True(MathF.Abs(written.LengthSquared() - 1f) > 1e-7f);
        map.Entities.Add(new OpenFPS.Server.Repositories.EntityData { EntityId = 2, PrefabId = "concrete_wall", Name = "turned wall", Position = new Vector3(5, 1.5f, 5), Rotation = written });
        map.Entities.Add(new OpenFPS.Server.Repositories.EntityData { EntityId = 3, PrefabId = "concrete_wall", Name = "unturned wall", Position = new Vector3(-5, 1.5f, 5), Rotation = default });
        Assert.True(maps.CreateMap(map, out string error), error);
        Assert.True(maps.TryGetMap("turns", out var world, out _, out _, out var lookup));
        Entity Named(string name) => lookup.Values.Single(e => world.Has<IdentityComponent>(e) && world.Get<IdentityComponent>(e).Name == name);
        var q = world.Get<Transform>(Named("turned wall")).Rotation;
        Assert.True(MathF.Abs(q.LengthSquared() - 1f) < 1e-7f, $"length squared {q.LengthSquared()}");
        Assert.True(MathF.Abs(Quaternion.Dot(q, Quaternion.Normalize(written))) > 1f - 1e-6f);
        Assert.Equal(Quaternion.Identity, world.Get<Transform>(Named("unturned wall")).Rotation);
    }
}

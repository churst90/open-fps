using System.Numerics;
using OpenFPS.Client.AudioEngine.Acoustics;
using OpenFPS.Common;
using OpenFPS.Common.Systems;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// The city's openings as a client gets them: the server's loader, the static definitions, the acoustic
/// map generated from them, and the opening graph over the scene's boxes. Prints how many openings there
/// are and every one the geometry disagrees with.
/// </summary>
public class CityOpeningsTests
{
    private readonly ITestOutputHelper _o;
    public CityOpeningsTests(ITestOutputHelper o) => _o = o;

    [Theory]
    [InlineData("city")]
    [InlineData("default")]
    [InlineData("speedway")]
    public void TheOpeningsAgreeWithTheGeometry(string mapId)
    {
        AcousticRegistry.Initialize();
        var prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
        var maps = new MapManager(new MapRepository(Path.Combine(AppContext.BaseDirectory, "maps")), prefabs);
        maps.Initialize();
        Assert.True(maps.TryGetMap(mapId, out var ecs, out Vector3 size, out _, out _));
        Assert.True(maps.TryGetMapData(mapId, out var data));

        var defs = EntityDefinitionFactory.StaticDefinitions(ecs);
        var world = new WorldSnapshot();
        foreach (var def in defs)
            world.Entities[def.EntityId] = new EntitySnapshot { Id = def.EntityId, Definition = def, Transform = def.Transform };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        world.AcousticMap = AcousticVolumeGenerator.GenerateRegions(defs, size, data.MinBound, data.VoxelResolution, data.OcclusionFloor);
        long genMs = sw.ElapsedMilliseconds;

        var acoustics = new SpatialAcoustics();
        var routes = acoustics.RoutesFor(world)!;
        var kinds = routes.Openings.GroupBy(o => o.Kind).Select(g => $"{g.Key} {g.Count()}");
        _o.WriteLine($"{mapId}: {world.AcousticMap.Portals.Count} portals in the acoustic map ({genMs} ms); " +
                     $"{routes.Openings.Count} openings in the graph ({string.Join(", ", kinds)}); " +
                     $"{routes.Problems.Count} disagreeing with the geometry");
        foreach (var p in routes.Problems) _o.WriteLine("  " + p);
        foreach (var o in routes.Openings.Where(o => o.Problem != null))
        {
            _o.WriteLine($"  {o.Kind} {o.Id}: centre {o.Centre} normal {o.Normal} half {o.HalfWidth:F2}x{o.HalfHeight:F2} depth {o.HalfDepth:F2} tau {o.Tau}");
            foreach (int r in new[] { o.RegionA, o.RegionB })
                if (world.AcousticMap.Regions.TryGetValue(r, out var reg))
                    _o.WriteLine($"     region {r} '{reg.FriendlyName}' at {world.AcousticMap.RegionPositions.GetValueOrDefault(r)} size {reg.RoomSize} rot {world.AcousticMap.RegionRotations.GetValueOrDefault(r)} materials [{string.Join(",", reg.Materials)}]");
            foreach (int c in o.Contents)
                _o.WriteLine($"     contains {routes.Solids[c].Material} at {routes.Solids[c].Center} size {routes.Solids[c].Size} leaf {routes.Solids[c].IsLeaf}");
        }
        var faces = routes.Openings.Where(o => o.Kind == OpeningRoutes.FaceKind).ToList();
        if (faces.Count > 0)
        {
            int pairs = faces.Select(o => (Math.Min(o.NodeA, o.NodeB), Math.Max(o.NodeA, o.NodeB))).Distinct().Count();
            _o.WriteLine($"  {faces.Count} face openings over {pairs} pairs of places; " +
                         $"{faces.Count(o => o.NodeA == OpeningRoutes.Outside || o.NodeB == OpeningRoutes.Outside)} to the outdoors; " +
                         $"narrowest side under 0.5 m: {faces.Count(o => 2 * MathF.Min(o.HalfWidth, o.HalfHeight) < 0.5f)}, " +
                         $"under 1 m: {faces.Count(o => 2 * MathF.Min(o.HalfWidth, o.HalfHeight) < 1f)}; " +
                         $"in a ceiling: {faces.Count(o => MathF.Abs(o.Normal.Y) > 0.9f)}");
        }
        foreach (var o in faces.OrderBy(o => MathF.Min(o.HalfWidth, o.HalfHeight)).Take(mapId == "city" ? 40 : 1000))
            {
                string Name(int r) => world.AcousticMap.Regions.TryGetValue(r, out var g) ? g.FriendlyName : r.ToString();
                _o.WriteLine($"  {o.Id}: {Name(o.RegionA)} -> {Name(o.RegionB)} at {o.Centre} normal {o.Normal} {2 * o.HalfWidth:F2} x {2 * o.HalfHeight:F2}, depth {2 * o.HalfDepth:F2}");
            }
        // Every opening found in a face agrees with the geometry it was found in; on the city, so do the
        // doors and doorways since the three that did not were refitted (2026-10-02).
        Assert.Empty(routes.Openings.Where(o => o.Kind == OpeningRoutes.FaceKind && o.Problem != null).Select(o => o.Problem));
        if (mapId == "city") Assert.Empty(routes.Problems);
    }
}

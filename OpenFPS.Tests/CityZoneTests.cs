using System;
using System.IO;
using System.Linq;
using System.Numerics;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Common;
using OpenFPS.Common.Systems;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using Xunit;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// An outdoor zone's box must not run through a building. Market Square was drawn to the building line
/// across Brandt Court, and the wall gaps between the tower's rooms, in no room's box, were named
/// "Market Square" (Cody, 2026-10-02) and heard as the open air for a step.
/// </summary>
public class CityZoneTests
{
    private readonly ITestOutputHelper _o;
    public CityZoneTests(ITestOutputHelper o) => _o = o;

    [Fact]
    public void NoOutdoorZoneRunsThroughABuilding()
    {
        AcousticRegistry.Initialize();
        var prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
        var maps = new MapManager(new MapRepository(Path.Combine(AppContext.BaseDirectory, "maps")), prefabs);
        maps.Initialize();
        Assert.True(maps.TryGetMap("city", out var ecs, out Vector3 size, out _, out _));
        Assert.True(maps.TryGetMapData("city", out var data));
        var defs = EntityDefinitionFactory.StaticDefinitions(ecs);
        var world = new WorldSnapshot();
        foreach (var def in defs)
            world.Entities[def.EntityId] = new EntitySnapshot { Id = def.EntityId, Definition = def, Transform = def.Transform };
        world.AcousticMap = AcousticVolumeGenerator.GenerateRegions(defs, size, data.MinBound, data.VoxelResolution, data.OcclusionFloor);
        var map = world.AcousticMap;
        var bad = new System.Collections.Generic.List<string>();
        foreach (var (id, o) in map.Regions)
        {
            if (o.IsIndoor || id == AcousticConstants.GlobalRegionId || !map.RegionPositions.TryGetValue(id, out var oc)) continue;
            var orot = map.RegionRotations.GetValueOrDefault(id, Quaternion.Identity);
            foreach (var (jd, r) in map.Regions)
            {
                if (!r.IsIndoor || !map.RegionPositions.TryGetValue(jd, out var rc)) continue;
                if (GeometryUtils.IsPointInOBB(rc, oc, o.RoomSize, orot))
                    bad.Add($"'{o.FriendlyName}' ({o.RoomSize}) holds the room '{r.FriendlyName}'");
            }
        }
        foreach (var b in bad.Distinct().Take(40)) _o.WriteLine(b);
        Assert.True(bad.Count == 0, $"{bad.Count} rooms lie inside an outdoor zone");

        // The gap in the wall between Brandt Court's stairwell and corridor, where it was heard.
        var spatial = new OpenFPS.Client.Core.SpatialService();
        int at = spatial.GetRegionAt(world, new Vector3(-18.64f, 1.7f, 156.975f));
        Assert.NotEqual("Market Square", map.Regions.TryGetValue(at, out var here) ? here.FriendlyName : "");
    }
}

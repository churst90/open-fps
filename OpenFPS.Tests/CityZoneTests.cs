using System.Numerics;
using OpenFPS.Client.AudioEngine.Acoustics;
using OpenFPS.Common;
using OpenFPS.Common.Systems;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
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

    private static WorldSnapshot City()
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
        {
            world.Entities[def.EntityId] = new EntitySnapshot { Id = def.EntityId, Definition = def, Transform = def.Transform };
            if (OpenFPS.Client.Core.ClientWorldState.IsMarker(def)) world.MarkerEntityIds.Add(def.EntityId);
        }
        world.AcousticMap = AcousticVolumeGenerator.GenerateRegions(defs, size, data.MinBound, data.VoxelResolution, data.OcclusionFloor);
        return world;
    }

    /// <summary>
    /// The doorways Cody stood in on 2026-10-03 in Brandt Court, where every crossing let the city in:
    /// in no zone's box, and for sound in one of the two rooms, never the outdoors. Eye height.
    /// </summary>
    [Theory]
    [InlineData(-18.517f, 157.279f)]   // stairwell, floor 0 to corridor, floor 0
    [InlineData(-18.817f, 157.278f)]
    [InlineData(-21.183f, 157.279f)]   // corridor, floor 0 to flat 00B
    [InlineData(-21.251f, 156.881f)]
    public void BrandtCourtDoorwaysAreRoomsForSound(float x, float z)
    {
        var world = City();
        var acoustics = new OpenFPS.Client.AudioEngine.Acoustics.SpatialAcoustics();
        Assert.NotNull(acoustics.RoutesFor(world));
        var eye = new Vector3(x, 0.05f + 1.7f, z);
        Assert.Equal(AcousticConstants.GlobalRegionId, acoustics.GetZoneAt(world, eye));
        int room = acoustics.GetRegionAt(world, eye);
        Assert.NotEqual(AcousticConstants.GlobalRegionId, room);
        _o.WriteLine($"{x}, {z}: {world.AcousticMap!.Regions[room].FriendlyName}");
    }

    [Fact]
    public void NoOutdoorZoneRunsThroughABuilding()
    {
        var world = City();
        var map = world.AcousticMap!;
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

    /// <summary>
    /// A flight of stairs and a landing are names, not rooms. Standing in the middle of each one in
    /// every tower, the client names the flight or the landing — and the server, saying where another
    /// player is, names the same — while the room for sound is the stairwell (or, at the top of the
    /// last flight, the roof's stair housing) exactly as it was: no named place is a region in the
    /// acoustic map, so the shaft's reverberation and its openings are untouched.
    /// </summary>
    [Fact]
    public void AFlightAndALandingAreNamesNotRooms()
    {
        var world = City();
        var map = world.AcousticMap!;
        // The stairs' places: the city has others since (Elm Park's fountain, a garden gate).
        var places = world.MarkerEntityIds.Where(id => OpenFPS.Client.Core.NamedPlaces.Is(world.Entities[id].Definition))
                                          .Where(id => world.Entities[id].Definition.Identity.Name is var n
                                                       && (n.Contains(" stairs, ") || n.Contains(" landing, ")))
                                          .ToList();
        Assert.Equal(74, places.Count);                                     // a flight and a landing a storey, 37 storeys
        var acoustics = new SpatialAcoustics();
        Assert.NotNull(acoustics.RoutesFor(world));

        AcousticRegistry.Initialize();
        var maps = new MapManager(new MapRepository(Path.Combine(AppContext.BaseDirectory, "maps")),
                                  new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs")));
        maps.Initialize();
        Assert.True(maps.TryGetMap("city", out var ecs, out _, out _, out _));

        foreach (int id in places)
        {
            var e = world.Entities[id];
            string name = e.Definition.Identity.Name;
            Assert.False(map.Regions.ContainsKey(id), $"'{name}' is a region");
            // At eye height over the middle of its floor: the bottom of the box is the floor (a
            // landing) or the floor at the foot (a flight, whose middle tread is half a storey up).
            var size = e.Definition.Collider.Size;
            var floorY = e.Transform.Position.Y - size.Y / 2;
            bool flight = name.Contains(" stairs, ");
            var eye = e.Transform.Position with { Y = floorY + (flight ? 1.5f : 0f) + 1.7f };
            Assert.Equal(id, acoustics.GetZoneAt(world, eye));
            Assert.Equal(name, CommandHandler.PlaceAt(ecs, eye));
            int room = acoustics.GetRegionAt(world, eye);
            string roomName = map.Regions.TryGetValue(room, out var r) ? r.FriendlyName : "";
            Assert.True(roomName.Contains(" stairwell, floor ") || roomName.EndsWith(" roof access"),
                        $"'{name}': the room for sound is '{roomName}'");
            Assert.Equal(room, acoustics.GetRoomAt(world, eye));
        }
    }

    /// <summary>
    /// "Sound struggles to come through the door only when loud things pass by" (Cody, 2026-10-02).
    /// Brandt Court's front door swung open, heard 2.5 m inside: a car 17 m down Main Street on the
    /// leaf's side and one 18 m up it. The route's legs ended on the door's edge, in the corner between
    /// the jamb and the leaf hinged on it, so every way round the open leaf was refused and it was
    /// charged as solid steel: the leaf's side came in at -68 dB in the mids, the other at -18.
    /// </summary>
    [Fact]
    public void AnOpenFrontDoorLetsTheStreetInFromBothSides()
    {
        var world = City();
        var leaf = world.Entities.Values.Where(e => OpeningGraph.IsDoorLeaf(e.Definition))
                        .OrderBy(e => Vector3.Distance(e.Transform.Position, new Vector3(-9.68f, 1.1f, 157.08f))).First();
        var def = leaf.Definition;
        var rot = leaf.Transform.Rotation;
        var hinge = leaf.Transform.Position + Vector3.Transform(new Vector3(def.Collider.Size.X * 0.5f, 0, 0), rot);
        var swung = rot * Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f);
        var moved = leaf;
        moved.Transform.Rotation = swung;
        moved.Transform.Position = hinge + Vector3.Transform(new Vector3(-def.Collider.Size.X * 0.5f, 0, 0), swung);
        world.Entities[leaf.Id] = moved;

        var acoustics = new SpatialAcoustics();
        var routes = acoustics.RoutesFor(world)!;
        var ear = new Vector3(-12f, 1.6f, 157f);
        int earRegion = acoustics.GetRegionAt(world, ear);
        float Mid(Vector3 src)
        {
            Assert.True(routes.Route(src, acoustics.GetRegionAt(world, src), ear, earRegion, out var a), $"no route from {src}");
            return 20f * MathF.Log10(MathF.Max(1e-6f, a.Mid));
        }
        float south = Mid(new Vector3(2f, 0.6f, 140f)), north = Mid(new Vector3(2f, 0.6f, 175f));
        _o.WriteLine($"mid: south {south:F1} dB, north {north:F1} dB");
        Assert.True(MathF.Min(south, north) > -45f, $"mid: south {south:F1} dB, north {north:F1} dB");
    }
}

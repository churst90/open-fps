using System;
using System.IO;
using System.Linq;
using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Common.Systems;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// Naming a place must not put a roof over it.
///
/// The speedway got its zones — Front straight, Turns one and two, Infield, Grandstand — and from
/// that moment the whole map sounded like the inside of a building, because every open-air behaviour
/// in the engine was keyed on "the listener is in the GLOBAL region id" and a named region is not
/// that. The estimate underneath was wrong in the same direction: six faces of material "None" are
/// six OPENINGS, and Sabine reads absorption zero as a perfect mirror, so an unbounded 277,000 m³
/// infield came out as a sealed box — and then, because nothing had absorbed, fell through to a
/// 500 ms default room at full wet.
///
/// These tests ask the boundary, which is the only thing that can answer it for a map nobody has
/// seen yet.
/// </summary>
public class OpenAirReverbTests
{
    private static (System.Collections.Generic.List<EntityDefinition> Defs, AcousticMap Map) Load(string mapId)
    {
        var prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
        var maps = new MapRepository(Path.Combine(AppContext.BaseDirectory, "maps"));
        var manager = new MapManager(maps, prefabs);
        manager.Initialize();
        Assert.True(manager.TryGetMap(mapId, out World world, out Vector3 size, out _, out _), $"maps/{mapId}.json failed to load.");
        var data = manager.GetAllMaps().First(kv => kv.Key == mapId).Value.data;
        var defs = EntityDefinitionFactory.StaticDefinitions(world);
        return (defs, AcousticVolumeGenerator.GenerateRegions(defs, size, data.MinBound, 1.0f));
    }

    private static RegionComponent Box(float x, float y, float z, params string[] faces)
    {
        var materials = new int[6];
        for (int f = 0; f < 6 && f < faces.Length; f++)
            materials[f] = AcousticRegistry.TryGetResonanceIndex(faces[f], out int i) ? i : 0;
        return new RegionComponent { RoomSize = new Vector3(x, y, z), Materials = materials, ReverbTimeScale = 1.0f };
    }

    /// <summary>A face made of nothing takes everything that reaches it and returns none of it.</summary>
    [Fact]
    public void AnOpenFaceIsNotASurface()
    {
        var sealedRoom = Box(10, 5, 10, "Concrete", "Concrete", "Concrete", "Concrete", "Concrete", "Concrete");
        var courtyard = Box(10, 5, 10, "Concrete", "None", "Concrete", "Concrete", "Concrete", "Concrete");

        Assert.True(RoomAcoustics.IsEnclosure(sealedRoom));
        Assert.False(RoomAcoustics.IsEnclosure(courtyard));
        Assert.Equal(0, RoomAcoustics.OpenFaceCount(sealedRoom));
        Assert.Equal(1, RoomAcoustics.OpenFaceCount(courtyard));

        // The ceiling of a 10 x 5 x 10 box is a quarter of its boundary, and the enclosure is measured
        // by area for exactly that reason: an open sky over an infield is not one face out of six.
        Assert.Equal(1.0f, RoomAcoustics.Enclosure(sealedRoom), 3);
        Assert.Equal(0.75f, RoomAcoustics.Enclosure(courtyard), 3);
    }

    /// <summary>
    /// No closed boundary, no estimate. Not a small number, not a default: the model does not apply,
    /// and the ray tracer is what governs the place instead.
    /// </summary>
    [Fact]
    public void AnUnclosedRegionGetsNoSabineEstimate()
    {
        Assert.Equal(0f, RoomAcoustics.DecayMs(Box(220, 6, 210)));                       // infield, no surfaces at all
        Assert.Equal(0f, RoomAcoustics.DecayMs(Box(30, 6, 55, "Asphalt")));              // the ground underfoot is not a room
        Assert.True(RoomAcoustics.DecayMs(Box(10, 5, 10, "Concrete", "Concrete", "Concrete",
                                              "Concrete", "Concrete", "Concrete")) > 1000f);
    }

    /// <summary>
    /// A closed boundary that absorbs nothing rings until the clamp stops it. It used to be handed
    /// the 500 ms default room, which is the number that made an open infield sound like a hall.
    /// </summary>
    [Fact]
    public void ABoundaryThatAbsorbsNothingIsAHallOfMirrorsAndNotA500msRoom()
    {
        var mirrors = new RegionComponent
        {
            RoomSize = new Vector3(10, 5, 10),
            // Not "None" — a real surface that happens to absorb nothing.
            Materials = new[] { 99, 99, 99, 99, 99, 99 },
            ReverbTimeScale = 1.0f,
        };
        // Index 99 is not in the registry, so it falls back to Generic rather than to an opening;
        // what matters is that a closed boundary never lands on the old default.
        Assert.NotEqual(AcousticConstants.DefaultReverbDecayMs, RoomAcoustics.DecayMs(mirrors));
    }

    /// <summary>Every named place on the speedway is open ground, and the map says so itself.</summary>
    [Fact]
    public void TheSpeedwayIsOutside()
    {
        var (defs, map) = Load("speedway");

        foreach (var def in defs.Where(d => d.Region.RoomSize.X > 0))
        {
            Assert.False(RoomAcoustics.IsEnclosure(def.Region),
                $"'{def.Region.FriendlyName}' is a closed boundary; a stretch of racetrack has no ceiling.");
            Assert.Equal(0f, RoomAcoustics.DecayMs(def.Region));
        }

        // Including the one you land in on login, which is the one the player complained about.
        var infield = defs.Single(d => d.Region.FriendlyName == "Infield");
        Assert.Equal(0f, RoomAcoustics.DecayMs(infield.Region));

        // And the outdoors itself, which used to be given six real materials and kept dry only by a
        // test for its id.
        Assert.False(RoomAcoustics.IsEnclosure(map.Regions[map.GlobalEnvironmentId]));
    }

    /// <summary>The rooms that did work still work: the default map's four regions are unchanged.</summary>
    [Fact]
    public void TheRoomsOnTheDefaultMapStillReverberate()
    {
        var (defs, _) = Load("default");
        var rooms = defs.Where(d => d.Region.RoomSize.X > 0 && d.Region.IsIndoor).ToList();
        Assert.NotEmpty(rooms);

        foreach (var room in rooms)
        {
            Assert.True(RoomAcoustics.IsEnclosure(room.Region),
                $"'{room.Region.FriendlyName}' stopped being a room.");
            float decay = RoomAcoustics.DecayMs(room.Region);
            Assert.True(decay >= AcousticConstants.MinReverbDecayMs,
                $"'{room.Region.FriendlyName}' went dry: {decay:F0} ms.");
        }

        // The carpeted room is still much deader than the concrete one — the material, not the id, is
        // what makes the difference, and it still does.
        float[] decays = rooms.Select(r => RoomAcoustics.DecayMs(r.Region)).OrderBy(d => d).ToArray();
        Assert.True(decays[^1] > decays[0] * 4f);
    }
}

/// <summary>
/// What you are standing on does not decide whether you are in a room.
///
/// The client overrides a region's FLOOR material with whatever the server says is underfoot, so that
/// a carpeted room deadens when you walk onto the carpet. Out of doors that same override was the
/// thing that made the reverb change as you walked: a speedway sector declares no surfaces, so while
/// you were on the grass it had no absorption at all and fell through to the 500 ms default room, and
/// the moment you stepped onto the asphalt it had exactly one absorbing surface in a nine-thousand
/// cubic metre box — which Sabine reads as ten seconds, the clamp.
/// </summary>
public class UnderfootMaterialTests
{
    [Fact]
    public void AFloorUnderfootDoesNotMakeARoom()
    {
        var sector = new RegionComponent
        {
            FriendlyName = "Front straight",
            IsIndoor = false,
            RoomSize = new Vector3(30, 6, 52),
            ReverbTimeScale = 1.0f,
            Materials = new int[6],
        };
        Assert.Equal(0f, RoomAcoustics.DecayMs(sector));

        // Step onto the track: the client writes the underfoot material into face 0.
        Assert.True(AcousticRegistry.TryGetResonanceIndex("Concrete", out int concrete));
        sector.Materials[0] = concrete;

        Assert.False(RoomAcoustics.IsEnclosure(sector),
            "a floor is not an enclosure; the other five faces are still open sky.");
        Assert.Equal(0f, RoomAcoustics.DecayMs(sector));
        Assert.Equal(5, RoomAcoustics.OpenFaceCount(sector));

        // And a real room still responds to what is underfoot, which is why the override exists.
        var room = Box(10, 5, 10, "Concrete", "Concrete", "Concrete", "Concrete", "Concrete", "Concrete");
        float bare = RoomAcoustics.DecayMs(room);
        Assert.True(AcousticRegistry.TryGetResonanceIndex("Carpet", out int carpet));
        room.Materials[0] = carpet;
        Assert.True(RoomAcoustics.DecayMs(room) < bare * 0.9f,
            "carpeting the floor of a sealed room should shorten its decay.");
    }

    private static RegionComponent Box(float x, float y, float z, params string[] faces)
    {
        var materials = new int[6];
        for (int f = 0; f < 6 && f < faces.Length; f++)
            materials[f] = AcousticRegistry.TryGetResonanceIndex(faces[f], out int i) ? i : 0;
        return new RegionComponent { RoomSize = new Vector3(x, y, z), Materials = materials, ReverbTimeScale = 1.0f };
    }
}

using System.Numerics;
using OpenFPS.Common.Components;
using OpenFPS.Common.Editing;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Core;
using OpenFPS.Server.Editor;
using OpenFPS.Server.Repositories;

namespace OpenFPS.Tests;

/// <summary>
/// /setmapsize EAST NORTH HEIGHT (Cody, 2026-10-09: "as it is my map I should be able to"). The map's
/// south-west corner at the ground stays put and the size runs from it; things that would be left
/// outside are counted and refused unless the player says force.
/// </summary>
public class MapSizeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "openfps-mapsize-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static MapData Data(WorldEditorTests.Rig rig, string mapId = "mine")
    {
        Assert.True(rig.Maps.TryGetMapData(mapId, out var d));
        return d;
    }

    [Fact]
    public void OnItsOwnItSaysTheSize()
    {
        var rig = new WorldEditorTests.Rig(_dir, UserRole.Player);
        Assert.Equal("mine is 100 metres east, 100 north and 40 high, from its south-west corner at -50.0, -50.0, 0.0. "
                   + "/setmapsize EAST NORTH HEIGHT changes it.", rig.Run("setmapsize"));
        // Anybody may ask.
        Assert.StartsWith("mine is 100 metres east", rig.RunAs(rig.Other, "setmapsize"));
    }

    [Fact]
    public void TheOwnerSetsItFromTheSouthWestCornerAndEverybodyOnTheMapIsSent()
    {
        var rig = new WorldEditorTests.Rig(_dir, UserRole.Player);

        string said = rig.Run("setmapsize", "200", "300", "60");

        Assert.Equal("mine is 200 metres east, 300 north and 60 high, from its south-west corner at -50.0, -50.0, 0.0.", said);
        var d = Data(rig);
        Assert.Equal(new Vector3(-50, 0, -50), d.MinBound);
        Assert.Equal(new Vector3(150, 60, 250), d.MaxBound);
        Assert.Equal(new Vector3(150, 60, 250), d.WalkMax);
        Assert.Equal("200 300 60", rig.Overlay("mine").Settings![MapSettings.Size]);
        Assert.Contains("\"Size\"", File.ReadAllText(Path.Combine(rig.MapDir, "overlays", "mine.json")));

        var update = rig.SentTo("other").OfType<MapSettingsUpdate>().Last();
        Assert.True(update.HasPlayArea);
        Assert.Equal(new Vector3(-50, 0, -50), update.PlayMin);
        Assert.Equal(new Vector3(150, 60, 250), update.PlayMax);

        // The zone and the ground follow: the map's own hundred metres no longer cover it, so natural
        // ground is laid under the new bounds as loading would.
        var world = rig.World("mine");
        Assert.True(MapManager.GroundCovers(world, d.WalkMin, d.WalkMax));
        ZoneComponent zone = default;
        world.Query(new Arch.Core.QueryDescription().WithAll<ZoneComponent>(), (ref ZoneComponent z) => zone = z);
        Assert.Equal(d.MaxBound, zone.MaxBound);
    }

    [Fact]
    public void ItIsKeptForTheNextStart()
    {
        var rig = new WorldEditorTests.Rig(_dir, UserRole.Player);
        rig.Run("setmapsize", "200", "300", "60");

        var again = new MapManager(new MapRepository(rig.MapDir), rig.Prefabs) { Overlays = new MapOverlayStore(Path.Combine(rig.MapDir, "overlays")) };
        again.Initialize();

        Assert.True(again.TryGetMapData("mine", out var d));
        Assert.Equal(new Vector3(150, 60, 250), d.MaxBound);
        Assert.True(again.TryGetMap("mine", out var world, out _, out _, out _));
        Assert.True(MapManager.GroundCovers(world, d.WalkMin, d.WalkMax));
    }

    [Fact]
    public void SomebodyElseIsRefusedWithTheReason()
    {
        var rig = new WorldEditorTests.Rig(_dir, UserRole.Player);
        Assert.Equal("mine is not yours: only its owner, or somebody with maps-any, can change its size.",
                     rig.RunAs(rig.Other, "setmapsize", "200", "200", "40"));
        Assert.Equal(new Vector3(50, 40, 50), Data(rig).MaxBound);
        Assert.Null(rig.Overlay("mine").Settings);
    }

    [Fact]
    public void MapsAnySetsTheSizeOfSomebodyElsesMap()
    {
        var rig = new WorldEditorTests.Rig(_dir, UserRole.Admin);
        rig.On("theirs");
        Assert.StartsWith("theirs is 150 metres east", rig.Run("setmapsize", "150", "120", "40"));
        Assert.Equal(new Vector3(100, 40, 70), Data(rig, "theirs").MaxBound);
    }

    /// <summary>A shipped map's file is written by a generator (tools/gen_city.py and the rest).</summary>
    [Fact]
    public void AGeneratedMapIsRefusedEvenToAnAdministrator()
    {
        string maps = Path.Combine(_dir, "shipped");
        Directory.CreateDirectory(maps);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "maps", "default.json"), Path.Combine(maps, "default.json"));
        var rig = new WorldEditorTests.Rig(_dir, UserRole.Owner, mapDir: maps);
        var before = Data(rig, rig.Tester.CurrentMapId).MaxBound;

        Assert.Contains("is made by a program in tools, so its size is set there.", rig.Run("setmapsize", "200", "200", "40"));
        Assert.Equal(before, Data(rig, rig.Tester.CurrentMapId).MaxBound);
    }

    [Fact]
    public void ThingsThatWouldBeLeftOutsideAreCountedAndRefused()
    {
        var rig = new WorldEditorTests.Rig(_dir, UserRole.Player);

        // Twenty metres from the south-west corner leaves the ground's middle and the spawn outside.
        string said = rig.Run("setmapsize", "20", "20", "40");

        Assert.Equal("That would leave 1 thing: Ground, and the spawn point outside the map. "
                   + "/setmapsize 20 20 40 force does it anyway; nothing is moved or deleted, and nobody can walk out to what is outside.", said);
        Assert.Equal(new Vector3(50, 40, 50), Data(rig).MaxBound);
    }

    [Fact]
    public void ForceDoesItAndSaysWhoIsBroughtInside()
    {
        var rig = new WorldEditorTests.Rig(_dir, UserRole.Player);

        string said = rig.Run("setmapsize", "20", "20", "40", "force");

        Assert.Equal("mine is 20 metres east, 20 north and 40 high, from its south-west corner at -50.0, -50.0, 0.0. "
                   + "Left outside: 1 thing: Ground, and the spawn point. 2 players past the new edge are brought inside at the next step.", said);
        Assert.Equal(new Vector3(-30, 40, -30), Data(rig).MaxBound);
        Assert.Contains(rig.SentTo("other"), m => m is TextEvent t && t.Text.StartsWith("tester made this map smaller."));
        // Nothing is moved or deleted.
        Assert.True(rig.Exists(1));
    }

    [Fact]
    public void ItTakesPartInTheEditorsUndo()
    {
        var rig = new WorldEditorTests.Rig(_dir, UserRole.Player);
        rig.Run("setmapsize", "200", "300", "60");

        Assert.Equal("Undid: set the map's size.", rig.Run("edit", "undo"));
        Assert.Equal(new Vector3(50, 40, 50), Data(rig).MaxBound);
        Assert.Equal("Redid: set the map's size.", rig.Run("edit", "redo"));
        Assert.Equal(new Vector3(150, 60, 250), Data(rig).MaxBound);
    }

    [Theory]
    [InlineData("200", "300")]
    [InlineData("200", "300", "sixty")]
    [InlineData("5", "300", "40")]
    [InlineData("200", "300", "4000")]
    [InlineData("NaN", "300", "40")]
    public void ABadSizeIsRefusedAndNothingChanges(params string[] args)
    {
        var rig = new WorldEditorTests.Rig(_dir, UserRole.Player);
        string said = rig.Run("setmapsize", args);
        Assert.True(said.StartsWith("Say /setmapsize EAST NORTH HEIGHT") || said.StartsWith("East and north are"), said);
        Assert.Equal(new Vector3(50, 40, 50), Data(rig).MaxBound);
    }

    [Fact]
    public void TheSameSizeIsSaid()
    {
        var rig = new WorldEditorTests.Rig(_dir, UserRole.Player);
        Assert.Equal("The map is already that size.", rig.Run("setmapsize", "100", "100", "40"));
    }

    [Fact]
    public void TheEditorsMapMenuOffersItTypedToTheOwnerOnly()
    {
        var rig = new WorldEditorTests.Rig(_dir, UserRole.Player);
        var item = rig.Menu("menu", "map")!.Items.Single(i => i.Command == "/setmapsize ");
        Assert.Equal(EditorItemKind.Input, item.Kind);
        Assert.Equal(3, (int)item.Count);
        Assert.Equal("100 100 40", item.Value);
        Assert.Equal("metres", item.Unit);

        // A developer edits any map but has not maps-any: somebody else's map is not theirs to resize.
        var dev = new WorldEditorTests.Rig(_dir, UserRole.Dev);
        dev.On("theirs");
        Assert.DoesNotContain(dev.Menu("menu", "map")!.Items, i => i.Command == "/setmapsize ");
    }
}

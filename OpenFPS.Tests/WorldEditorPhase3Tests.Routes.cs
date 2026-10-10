using System.Numerics;
using OpenFPS.Client.Core;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Core;
using OpenFPS.Server.Editor;
using OpenFPS.Server.Repositories;
using Rig = OpenFPS.Tests.WorldEditorTests.Rig;

namespace OpenFPS.Tests;

/// <summary>Roads, paths and railways laid as data: walked or typed, laid as pieces and as the map's own
/// road and track data, with stations, crossings and a train, and taken up again.</summary>
public partial class WorldEditorPhase3Tests
{
    private static Rig Mine(string dir, UserRole role = UserRole.Player)
    {
        var rig = new Rig(dir, role);
        rig.On("mine");
        return rig;
    }

    /// <summary>The tester walks to a point a metre at a time, the editor ticking as the server does.</summary>
    private static void WalkTo(Rig rig, Vector3 to)
    {
        var from = rig.World("mine").Get<Transform>(rig.Tester.Entity).Position;
        int steps = (int)MathF.Ceiling(new Vector2(to.X - from.X, to.Z - from.Z).Length());
        for (int i = 1; i <= steps; i++)
        {
            rig.Stand(rig.Tester, "mine", Vector3.Lerp(from, to, i / (float)steps));
            rig.Editor.Tick();
        }
    }

    private List<(string Name, string Prefab)> PiecesOf(Rig rig, OverlayRoute r)
    {
        var world = rig.World("mine");
        return r.Pieces.Where(rig.Exists).Select(id => (world.Get<IdentityComponent>(rig.Thing(id)).Name, world.Get<IdentityComponent>(rig.Thing(id)).PrefabId ?? "")).ToList();
    }

    [Fact]
    public void ARoadIsLaidByWalkingItItsStraightsJoinedAndIsTheMapsRoadAtTheNextLoad()
    {
        var rig = Mine(_dir);
        string said = rig.Run("edit", "route", "start", "road", "name", "Elm", "Road");
        Assert.StartsWith("Laying a road, Elm Road. The first point is where you stand.", said);
        WalkTo(rig, new Vector3(5, 0.05f, 35));
        WalkTo(rig, new Vector3(25, 0.05f, 35));
        Assert.Null(rig.Overlay("mine").Routes);

        said = rig.Run("edit", "route", "finish");
        Assert.StartsWith("Laid a road, Elm Road: 3 points, 50 metres long, 7 metres wide, asphalt, 40 km/h.", said);
        Assert.Contains("One undo takes it up.", said);
        var road = Assert.Single(rig.Overlay("mine").Routes!);
        Assert.Equal("editor_elm_road", road.Id);
        Assert.Equal(new[] { new Vector3(5, 0.05f, 5), new Vector3(5, 0.05f, 35), new Vector3(25, 0.05f, 35) }, road.Points);
        var pieces = PiecesOf(rig, road);
        Assert.Equal(2, pieces.Count);
        Assert.All(pieces, p => Assert.Equal(("Elm Road", "asphalt_road"), p));
        Assert.True(rig.Maps.TryGetMapData("mine", out var data));
        var roadData = Assert.Single(data.Roads!);
        Assert.Equal(0.07f, roadData.Centreline[0].Y, 3);
        Assert.Equal(2, roadData.Lanes.Count);
        // Its surface is what you stand on.
        Assert.Contains(rig.Overlay("mine").Added, a => a.Entity.PrefabId == "asphalt_road" && a.PlacedBy == "tester");

        Assert.Equal("Undid: laid Elm Road.", rig.Run("edit", "undo"));
        Assert.Null(rig.Overlay("mine").Routes);
        Assert.Empty(rig.Overlay("mine").Added);
        Assert.True(data.Roads == null || data.Roads.Count == 0);
        Assert.Equal("Redid: laid Elm Road.", rig.Run("edit", "redo"));

        // Loaded again, the map has it as a road of its own, and the network is built from it.
        var again = new Rig(_dir, UserRole.Player, mapDir: rig.MapDir);
        Assert.True(again.Maps.TryGetRoads("mine", out var net));
        Assert.Equal("Elm Road", Assert.Single(net.Roads).Name);
    }

    [Fact]
    public void ARailwayTypedWithAStationAndACrossingRunsATrainAndUndoTakesItAllUp()
    {
        var rig = Mine(_dir, UserRole.Dev);
        Assert.StartsWith("Laying a railway, Loop line, from points typed", rig.Run("edit", "route", "new", "railway", "train", "light_rail", "name", "Loop", "line"));
        Assert.Equal("4 points added: 4 points, 160 metres round.", rig.Run("edit", "route", "points", "0 0; 40 0; 40 40; 0 40"));
        Assert.StartsWith("Central: trains stop here, 5 metres round the line", rig.Run("edit", "route", "station", "Central"));
        Assert.StartsWith("Level crossing 1: a level crossing,", rig.Run("edit", "route", "crossing"));

        string said = rig.Run("edit", "route", "finish");
        Assert.StartsWith("Laid a railway, Loop line: 4 points, 160 metres round, 4.2 metres wide, gravel, on the ground, 1 station, 1 level crossing, a light rail train at up to 60 km/h.", said);
        Assert.Contains("Loop line train runs it now.", said);
        Assert.True(rig.Maps.TryGetMapData("mine", out var data));
        var track = Assert.Single(data.Tracks!);
        Assert.Equal(4, track.Waypoints.Count);
        Assert.Equal(0.05f + 0.22f, track.Waypoints[0].Y, 3);
        Assert.Equal(5f, Assert.Single(track.Stops).AtMetres, 2);
        Assert.Equal("platform", track.Stops[0].Kind);
        Assert.Single(data.Crossings!);
        Assert.Equal("Loop line train", Assert.Single(data.Trains!).Name);
        Assert.Equal(1, rig.Server.Rail.CountOn("mine"));
        var line = Assert.Single(rig.Overlay("mine").Routes!);
        var pieces = PiecesOf(rig, line);
        Assert.Equal(4, pieces.Count(p => p.Name == "Loop line, track bed"));
        Assert.Single(pieces, p => p.Name == "Central, platform");

        Assert.Equal("Undid: laid Loop line.", rig.Run("edit", "undo"));
        Assert.Equal(0, rig.Server.Rail.CountOn("mine"));
        Assert.True(data.Tracks == null || data.Tracks.Count == 0);
        Assert.True(data.Trains == null || data.Trains.Count == 0);
        Assert.Empty(rig.Overlay("mine").Added);

        // Taken up by name, and laid again by undo.
        rig.Run("edit", "redo");
        Assert.StartsWith("Took up Loop line and 5 pieces of it.", rig.Run("edit", "route", "remove", "loop"));
        Assert.Equal(0, rig.Server.Rail.CountOn("mine"));
        Assert.Equal("Undid: took up Loop line.", rig.Run("edit", "undo"));
        Assert.Equal(1, rig.Server.Rail.CountOn("mine"));
        Assert.Equal(5, PiecesOf(rig, Assert.Single(rig.Overlay("mine").Routes!)).Count);
    }

    [Fact]
    public void AnUndergroundLineMakesItsOwnTunnelAndARaisedOneItsDeckAndPillars()
    {
        var rig = Mine(_dir, UserRole.Dev);
        rig.Run("edit", "route", "new", "railway", "level", "underground", "name", "Tube");
        rig.Run("edit", "route", "points", "0 0; 40 0; 40 40; 0 40");
        rig.Run("edit", "route", "station", "Deep");
        string said = rig.Run("edit", "route", "finish");
        Assert.Contains("underground, 8 metres down, in a tunnel of its own", said);
        Assert.Contains("Deep is underground, so it has a stop but no platform or stairs yet", said);
        var tube = Assert.Single(rig.Overlay("mine").Routes!);
        var pieces = PiecesOf(rig, tube);
        Assert.Equal(4, pieces.Count(p => p.Name == "Tube, tunnel floor"));
        Assert.Equal(4, pieces.Count(p => p.Name == "Tube, tunnel roof"));
        Assert.Equal(8, pieces.Count(p => p.Name == "Tube, tunnel wall"));
        Assert.True(rig.Maps.TryGetMapData("mine", out var data));
        Assert.Equal(0.05f - 8f + 0.2f, data.Tracks!.Single().Waypoints[0].Y, 3);

        rig.Run("edit", "route", "new", "railway", "level", "raised", "height", "7", "name", "High line");
        rig.Run("edit", "route", "points", "-40 -40; 0 -40; 0 -10");
        said = rig.Run("edit", "route", "finish");
        Assert.Contains("raised 7 metres on pillars", said);
        var high = rig.Overlay("mine").Routes!.Single(r => r.Name == "High line");
        var raised = PiecesOf(rig, high);
        Assert.Equal(3, raised.Count(p => p.Name == "High line, deck"));
        Assert.True(raised.Count(p => p.Name == "High line, pillar") >= 3);
        Assert.Equal(0.05f + 7.2f, data.Tracks!.Single(t => t.Id == high.Id).Waypoints[0].Y, 3);
    }

    [Fact]
    public void ARouteIsRefusedWithTooFewPointsAndWhatIsLaidIsListed()
    {
        var rig = Mine(_dir);
        Assert.Equal("Nothing is being laid. /edit route start road, path or railway begins one.", rig.Run("edit", "route", "finish"));
        rig.Run("edit", "route", "start", "railway");
        Assert.StartsWith("A railway is a loop of at least three points", rig.Run("edit", "route", "finish"));
        Assert.StartsWith("The width is 0.5 to 60 metres.", rig.Run("edit", "route", "set", "width", "100"));
        Assert.Equal("Railway 1 is not laid; its points are dropped.", rig.Run("edit", "route", "cancel"));
        rig.Run("edit", "route", "start", "road");
        Assert.Equal("Stations and level crossings are on a railway.", rig.Run("edit", "route", "station"));
        rig.Run("edit", "route", "cancel");

        rig.Run("edit", "route", "new", "path", "name", "Garden", "walk");
        rig.Run("edit", "route", "points", "0 0; 0 30");
        Assert.Contains("People walking and characters use it as a pavement.", rig.Run("edit", "route", "finish"));
        var menu = rig.Menu("routes")!;
        Assert.Contains(menu.Items, i => i.Label.StartsWith("a path, Garden walk: 2 points, 30 metres long, 2 metres wide, concrete") && i.Label.Contains("laid by tester"));
        Assert.Equal("Garden walk, pavement", PiecesOf(rig, rig.Overlay("mine").Routes!.Single()).Single().Name);
    }

    [Fact]
    public void AVersionRestoredLaysItsRoadsAgainAndTakesUpTheOnesItHasNot()
    {
        var rig = Mine(_dir);
        rig.Run("edit", "route", "new", "road", "name", "Old", "Road");
        rig.Run("edit", "route", "points", "0 0; 0 40");
        rig.Run("edit", "route", "finish");
        rig.Run("edit", "map", "save", "with", "the", "old", "road");
        rig.Run("edit", "route", "remove", "Old Road");
        rig.Run("edit", "route", "new", "road", "name", "New", "Road");
        rig.Run("edit", "route", "points", "20 0; 20 40");
        rig.Run("edit", "route", "finish");

        string said = rig.Run("edit", "map", "restore", "1");
        Assert.Contains("2 roads, paths or railways taken up or laid", said);
        var road = Assert.Single(rig.Overlay("mine").Routes!);
        Assert.Equal("Old Road", road.Name);
        Assert.Single(PiecesOf(rig, road));
        Assert.True(rig.Maps.TryGetMapData("mine", out var data));
        Assert.Equal("Old Road", Assert.Single(data.Roads!).Name);
        Assert.Equal("Undid: restored version 1, with the old road.", rig.Run("edit", "undo"));
        Assert.Equal("New Road", Assert.Single(rig.Overlay("mine").Routes!).Name);
        Assert.Equal("New Road", Assert.Single(data.Roads!).Name);
    }

    [Fact]
    public void JoiningTheStraightsKeepsTheCornersAndDropsTheRest()
    {
        var walked = new List<Vector3>();
        for (int i = 0; i <= 30; i++) walked.Add(new Vector3(0, 0, i));
        for (int i = 1; i <= 20; i++) walked.Add(new Vector3(i, 0, 30 + 0.1f * (i % 2)));
        var kept = WorldEditor.Simplify(walked);
        Assert.Equal(new[] { new Vector3(0, 0, 0), new Vector3(0, 0, 30), walked[^1] }, kept);
    }

    [Fact]
    public void ThePlaceTabLaysARouteFromItsForm()
    {
        var rig = Mine(_dir);
        rig.Run("edit", "dialog", "open", "place");
        var sent = new List<string>();
        var editor = new EditorDialog(rig.LastMenu!, new EditorDialogMemory { Tab = "place" }, new BuildMemory(), sent.Add);
        var tab = editor.Tabs[0];
        var section = tab.Sections.Single(s => s.Id == "route");
        Assert.Equal("Lay a road, path or railway", section.Title);
        var kind = tab.Find("place.routekind")!;
        Assert.Equal(new[] { "Road", "Path", "Railway" }, kind.Items.Select(i => i.Label));
        Assert.False(tab.Find("place.routelevel")!.Enabled);
        Assert.False(tab.Find("place.routefinish")!.Enabled);
        Assert.Equal("Nothing is being laid", tab.Find("place.routestatus")!.Label);

        kind.Select("railway");
        editor.Changed(kind);
        Assert.True(tab.Find("place.routelevel")!.Enabled);
        tab.Find("place.routename")!.Text = "Loop";
        editor.Press("place.routestart");
        Assert.Equal("/edit route start railway level ground train none name Loop", sent[^1]);

        rig.Run("edit", "route", "new", "railway", "name", "Loop");
        rig.Run("edit", "dialog", "tab", "place");
        editor.Update(rig.Replies.OfType<EditorMenu>().Last(m => m.Path == "dialog.place"));
        Assert.StartsWith("Laying a railway, Loop: 0 points", tab.Find("place.routestatus")!.Label);
        Assert.True(tab.Find("place.routefinish")!.Enabled);
        tab.Find("place.routepoints")!.Text = "0 0; 40 0; 40 40";
        editor.Press("place.routeadd");
        Assert.Equal("/edit route points 0 0; 40 0; 40 40", sent[^1]);
        editor.Press("place.routefinish");
        Assert.Equal("/edit route finish level ground train none name Loop", sent[^1]);
    }
}

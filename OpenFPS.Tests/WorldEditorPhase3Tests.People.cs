using System.Numerics;
using OpenFPS.Client.Core;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Core;
using OpenFPS.Server.Editor;
using OpenFPS.Server.Repositories;
using Rig = OpenFPS.Tests.WorldEditorTests.Rig;

namespace OpenFPS.Tests;

/// <summary>People: characters put on a map with places from the map, and how many people walk its pavements.</summary>
public partial class WorldEditorPhase3Tests
{
    /// <summary>A map of the tester's with two bus stops and a pavement 50 metres long.</summary>
    private Rig Town()
    {
        var rig = new Rig(_dir, UserRole.Player);
        var map = MapTemplates.Flat("town", "tester");
        map.RoadStops = new List<RoadStopData>
        {
            new() { Name = "Main Street", Position = new Vector3(20, 0.05f, -20), Kind = "bus_stop" },
            new() { Name = "Elm Road", Position = new Vector3(-20, 0.05f, 20), Kind = "bus_stop" },
        };
        map.Entities.Add(new EntityData
        {
            EntityId = 2, PrefabId = "concrete_floor", Name = "Elm Road sidewalk", Position = new Vector3(30, 0.06f, 0), Scale = new Vector3(0.3f, 1f, 5f),
        });
        Assert.True(rig.Maps.CreateMap(map, out string error), error);
        rig.On("town");
        return rig;
    }

    [Fact]
    public void APersonIsPutOnTheMapTheirPlacesChosenFromTheMapsAndUndone()
    {
        var rig = Town();
        int before = rig.Server.Characters.Count;
        string said = rig.Run("edit", "person", "add", "Sam", "voice", "alex");
        Assert.StartsWith("Sam lives on this map now, homeless, voice alex, goes to any place the map has. The map has 2 places for their day", said);
        Assert.Equal(before + 1, rig.Server.Characters.Count);
        Assert.True(rig.Maps.TryGetMapData("town", out var data));
        Assert.Equal("Sam", Assert.Single(data.Characters!).Name);
        Assert.Equal("Sam", Assert.Single(rig.Overlay("town").People!).Name);

        Assert.Equal("Sam goes to the bus stop, Main Street: 1 place in their day.", rig.Run("edit", "person", "place", "add", "the bus stop, Main"));
        Assert.Equal(new[] { "the bus stop, Main Street" }, rig.Overlay("town").People![0].Places);
        Assert.StartsWith("This map has no place called the moon", rig.Run("edit", "person", "place", "add", "the moon"));
        Assert.Equal("Sam goes to the bus stop, Main Street already.", rig.Run("edit", "person", "place", "add", "the bus stop, Main Street"));

        Assert.Equal("Undid: changed where Sam goes.", rig.Run("edit", "undo"));
        Assert.Null(rig.Overlay("town").People![0].Places);
        Assert.Equal("Redid: changed where Sam goes.", rig.Run("edit", "redo"));

        // Kept: a new server over the same folders has Sam, with the place.
        var again = new Rig(_dir, UserRole.Player, mapDir: rig.MapDir);
        Assert.True(again.Maps.TryGetMapData("town", out var kept));
        Assert.Equal(new[] { "the bus stop, Main Street" }, Assert.Single(kept.Characters!).Places);

        Assert.Equal("Sam is off the map. Undo puts them back.", rig.Run("edit", "person", "remove", "Sam"));
        Assert.Equal(before, rig.Server.Characters.Count);
        Assert.True(data.Characters == null || data.Characters.Count == 0);
        Assert.Equal("Undid: took Sam off the map.", rig.Run("edit", "undo"));
        Assert.Equal(before + 1, rig.Server.Characters.Count);
        Assert.StartsWith("There is somebody called Sam on this map already.", rig.Run("edit", "person", "add", "sam"));
    }

    [Fact]
    public void WalkersFillTheMapsPavementsAndAPathLaidIsOne()
    {
        var rig = Town();
        Assert.Equal("2 people walk this map's 48 metres of pavement now, 4 per 100 metres, besides its own.", rig.Run("edit", "walkers", "4"));
        Assert.Equal(2, rig.Editor.WalkersOn("town"));
        Assert.Equal("4", rig.Overlay("town").Settings!["Walkers"]);
        Assert.Equal("Undid: set the map's walkers.", rig.Run("edit", "undo"));
        Assert.Equal(0, rig.Editor.WalkersOn("town"));
        Assert.Equal("Redid: set the map's walkers.", rig.Run("edit", "redo"));
        Assert.Equal(2, rig.Editor.WalkersOn("town"));

        // A path laid with the editor is pavement too.
        rig.Run("edit", "route", "new", "path", "name", "Long", "walk");
        rig.Run("edit", "route", "points", "-40 -40; -40 40");
        rig.Run("edit", "route", "finish");
        rig.Run("edit", "walkers", "5");
        Assert.True(rig.Editor.WalkersOn("town") > 5);

        // Started again at the next load, as the setting asks.
        var again = new Rig(_dir, UserRole.Player, mapDir: rig.MapDir);
        Assert.True(again.Editor.WalkKeptWalkers() > 5);
        Assert.StartsWith("Say /edit walkers NUMBER", rig.Run("edit", "walkers", "50"));
    }

    [Fact]
    public void TheWorldTabListsPeopleAndTicksTheirPlaces()
    {
        var rig = Town();
        rig.Run("edit", "person", "add", "Sam");
        rig.Run("edit", "dialog", "open", "world");
        var sent = new List<string>();
        var editor = new EditorDialog(rig.LastMenu!, new EditorDialogMemory { Tab = "world" }, new BuildMemory(), sent.Add);
        var tab = editor.Tabs[3];
        var section = tab.Sections.Single(s => s.Id == "people");
        Assert.Equal("People", section.Title);
        Assert.Equal(new[] { "world.f./edit walkers ", "world.walkersset", "world.people", "world.places", "world.personname", "world.personvoice", "world.personadd", "world.personremove" },
                     section.Controls.Select(c => c.Id));
        var people = tab.Find("world.people")!;
        Assert.StartsWith("Sam, homeless, voice alex", Assert.Single(people.Items).Label);
        Assert.Equal("Sam", people.SelectedValue);
        var places = tab.Find("world.places")!;
        Assert.True(places.Checkable);
        Assert.Equal(2, places.Items.Count);
        Assert.All(places.Items, p => Assert.False(p.Ticked));

        // Space on a place ticks it for the chosen person, through the server.
        editor.Toggle(places, 0);
        Assert.Equal($"/edit person place add {places.Items[0].Value} dialog", sent[^1]);
        rig.Run("edit", "person", "place", "add", places.Items[0].Value, "dialog");
        editor.Update(rig.Replies.OfType<EditorMenu>().Last(m => m.Path == "dialog.world"));
        Assert.True(tab.Find("world.places")!.Items[0].Ticked);

        tab.Find("world.personname")!.Text = "Jo";
        editor.Press("world.personadd");
        Assert.Equal("/edit person add Jo voice alex", sent[^1]);
        string? question = null;
        editor.ConfirmAsked += (q, _) => question = q;
        editor.Press("world.personremove");
        Assert.Equal("Take Sam off the map?", question);
    }
}

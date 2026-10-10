using System.Numerics;
using OpenFPS.Client.Core;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Core;
using OpenFPS.Server.Editor;
using OpenFPS.Server.Repositories;
using Rig = OpenFPS.Tests.WorldEditorTests.Rig;

namespace OpenFPS.Tests;

/// <summary>Versions of a map's edits: saved, listed, restored as one undo, and baked into a map file.</summary>
public partial class WorldEditorPhase3Tests
{
    /// <summary>The yard as version 1 has it: a fire placed, the wall moved, the brick wall removed, rain.</summary>
    private static int EditTheYard(Rig rig)
    {
        rig.Run("edit", "place", "fire_pit");
        int fire = rig.Selected;
        rig.Run("edit", "select", "#2");
        rig.Run("edit", "nudge", "east", "2");
        rig.Run("edit", "select", "#4");
        rig.Run("edit", "delete");
        rig.Run("edit", "map", "set", "weather", "rain");
        return fire;
    }

    [Fact]
    public void AVersionIsSavedListedAndRestoredAsOneUndo()
    {
        var rig = Yard();
        var file2 = rig.PoseOf(2);
        int fire = EditTheYard(rig);
        var fireAt = rig.PoseOf(fire);
        var moved = rig.PoseOf(2);
        Assert.Equal("Saved version 1 of this map, before the market: 1 placed, 1 changed, 1 removed, 1 map setting.",
                     rig.Run("edit", "map", "save", "before", "the", "market"));

        // Everything changed again: the fire gone, the wall back, the brick wall back, a new wall, snow.
        rig.Run("edit", "remove", $"#{fire}");
        rig.Run("edit", "putback", "#2");
        rig.Run("edit", "putback", "#4");
        rig.Run("edit", "place", "concrete_wall");
        int wall = rig.Selected;
        rig.Run("edit", "map", "set", "weather", "snow");

        string said = rig.Run("edit", "map", "restore", "1");
        Assert.StartsWith("Restored version 1, before the market: ", said);
        Assert.EndsWith("What the map had is saved as version 2. Undo puts it back.", said);
        Assert.True(rig.Exists(fire));
        Assert.True(rig.PoseOf(fire).Near(fireAt));
        Assert.False(rig.Exists(wall));
        Assert.True(rig.PoseOf(2).Near(moved));
        Assert.False(rig.Exists(4));
        Assert.True(rig.Maps.TryGetMapData("yard", out var data));
        Assert.Equal("rain", data.HeldWeather);
        var versions = rig.Editor.Versions.Of("yard");
        Assert.Equal(new[] { "before the market", "before restoring version 1" }, versions.Select(v => v.Name));
        Assert.True(versions[1].Automatic);
        Assert.Equal("tester", versions[1].Author);
        Assert.Equal("This map is already as version 1, before the market, has it.", rig.Run("edit", "map", "restore", "before the market"));

        // One undo takes the whole restore back.
        Assert.Equal("Undid: restored version 1, before the market.", rig.Run("edit", "undo"));
        Assert.False(rig.Exists(fire));
        Assert.True(rig.Exists(wall));
        Assert.True(rig.PoseOf(2).Near(file2));
        Assert.True(rig.Exists(4));
        Assert.Equal("snow", data.HeldWeather);
        Assert.Equal("Redid: restored version 1, before the market.", rig.Run("edit", "redo"));
        Assert.True(rig.Exists(fire));

        // Kept on disk: a new server over the same folders has them.
        var again = new Rig(_dir, UserRole.Player, mapDir: rig.MapDir);
        var kept = again.Editor.Versions.Of("yard");
        Assert.Equal(new[] { "before the market", "before restoring version 1" }, kept.Select(v => v.Name));
        Assert.Equal(1, kept[0].Overlay.Added.Count);
    }

    [Fact]
    public void TheWorldTabListsVersionsAndAsksBeforeRestoringOrBaking()
    {
        var rig = Yard();
        EditTheYard(rig);
        rig.Run("edit", "map", "save", "first");
        rig.Run("edit", "dialog", "open", "world");
        var sent = new List<string>();
        var editor = new EditorDialog(rig.LastMenu!, new EditorDialogMemory { Tab = "world" }, new BuildMemory(), sent.Add);
        var tab = editor.Tabs[3];
        var section = tab.Sections.Single(s => s.Id == "versions");
        Assert.Equal("Versions of this map", section.Title);
        Assert.Equal(new[] { "world.versionname", "world.versionsave", "world.versions", "world.versionrestore", "world.bake" }, section.Controls.Select(c => c.Id));
        var list = tab.Find("world.versions")!;
        Assert.StartsWith("Version 1, first, by tester, ", Assert.Single(list.Items).Label);
        Assert.EndsWith(": 1 placed, 1 changed, 1 removed, 1 map setting", list.Items[0].Label);
        Assert.StartsWith("1 version saved, newest first.", list.Description);
        // The tester owns the yard, a map players make: it can be baked.
        var bake = tab.Find("world.bake")!;
        Assert.True(bake.Enabled);
        Assert.StartsWith("Writes this map's edits, 1 placed, 1 changed, 1 removed, 1 map setting, into its file.", bake.Description);

        string? question = null;
        Action? yes = null;
        editor.ConfirmAsked += (q, y) => { question = q; yes = y; };
        list.Selected = 0;
        editor.Press("world.versionrestore");
        Assert.Equal("Restore version 1, first? What the map has now is saved as a version first.", question);
        yes!();
        Assert.Equal("/edit map restore 1", sent[^1]);
        editor.Press("world.bake");
        Assert.StartsWith("Write this map's edits into its file?", question);
        yes!();
        Assert.Equal("/edit map bake now", sent[^1]);

        string? why = null;
        editor.Said += w => why = w;
        editor.Press("world.versionsave");
        Assert.Equal("Type a name for the version.", why);
        tab.Find("world.versionname")!.Text = "second";
        editor.Press("world.versionsave");
        Assert.Equal("/edit map save second", sent[^1]);
    }

    [Fact]
    public void BakingIsRefusedOnAMapAProgramWritesAndForSomebodyElsesMap()
    {
        var admin = new Rig(_dir, UserRole.Admin);
        admin.On("open");
        admin.Run("edit", "place", "fire_pit");
        Assert.StartsWith("open is written by a program in tools and must stay byte for byte what that program writes",
                          admin.Run("edit", "map", "bake", "now"));

        // An editor the owner asked in may save and restore versions, but not write the owner's file.
        var rig = Yard();
        rig.Stand(rig.Other, "theirs", new Vector3(5, 0.05f, 15));
        rig.RunAs(rig.Other, "map", "editor", "add", "tester");
        rig.On("theirs");
        Assert.StartsWith("Only the owner of theirs, or somebody with maps-any, can write its edits", rig.Run("edit", "map", "bake"));
        Assert.StartsWith("Saved version 1 of this map, mine too", rig.Run("edit", "map", "save", "mine", "too"));
    }

    [Fact]
    public void BakingWritesTheEditsIntoTheFileAndLeavesWhatAFileCannotHold()
    {
        var rig = Yard();
        int fire = EditTheYard(rig);
        rig.Run("edit", "select", "#2");
        rig.Run("edit", "set", "name", "Old Wall");
        rig.Run("edit", "select", "#3");
        rig.Run("edit", "set", "volume", "0.5");
        rig.Run("edit", "spawn", "here");
        var moved = rig.PoseOf(2);
        string path = rig.Maps.FileOf("yard")!;
        string before = File.ReadAllText(path);

        Assert.StartsWith("Baking writes this map's edits into its file: 1 placed, 2 changed, 1 removed, spawn moved, 1 map setting.", rig.Run("edit", "map", "bake"));
        Assert.Equal(before, File.ReadAllText(path));

        string said = rig.Run("edit", "map", "bake", "now");
        Assert.StartsWith("Baked: this map's file now has its edits. Left in the overlay, since a map file does not hold them: 0 placed, 1 changed, 0 removed.", said);
        var baked = MapRepository.LoadFromFile(path)!;
        var wall = baked.Entities.Single(e => e.EntityId == 2);
        Assert.True(new Pose(wall.Position, wall.Rotation, wall.Scale).Near(moved));
        Assert.Equal("Old Wall", wall.Name);
        Assert.DoesNotContain(baked.Entities, e => e.EntityId == 4);
        Assert.Contains(baked.Entities, e => e.EntityId == fire && e.PrefabId == "fire_pit");
        Assert.Equal("rain", baked.HeldWeather);
        Assert.Equal(new Vector3(5, 0.05f, 5), baked.SpawnPoint.Position);
        // The fire pit's volume is not a field of a map entry: it stays, laid on the baked place.
        var left = Assert.Single(rig.Overlay("yard").Changed);
        Assert.Equal(3, left.Id);
        Assert.Equal("0.5", left.Settings!["Volume"]);
        Assert.Empty(rig.Overlay("yard").Removed);
        Assert.Empty(rig.Overlay("yard").Added);
        Assert.Null(rig.Overlay("yard").Spawn);
        Assert.Null(rig.Overlay("yard").Settings);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!, "yard.json.before-bake-*"));
        Assert.Equal(before, File.ReadAllText(Directory.GetFiles(Path.GetDirectoryName(path)!, "yard.json.before-bake-*")[0]));
        Assert.Equal("before baking", rig.Editor.Versions.Of("yard")[^1].Name);
        Assert.Equal("Nothing to undo.", rig.Run("edit", "undo"));

        // Loaded again, it is the same map.
        var again = new Rig(_dir, UserRole.Player, mapDir: rig.MapDir);
        again.On("yard");
        Assert.True(again.PoseOf(2).Near(moved));
        Assert.False(again.Exists(4));
        Assert.True(again.Exists(fire));
        Assert.Equal(0.5f, again.World("yard").Get<SoundEmitterComponent>(again.Thing(3)).Volume);
        Assert.Equal("Old Wall", again.World("yard").Get<IdentityComponent>(again.Thing(2)).Name);
    }
}

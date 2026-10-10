using System.Numerics;
using OpenFPS.Client.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Core;
using OpenFPS.Server.Editor;
using OpenFPS.Server.Repositories;
using Rig = OpenFPS.Tests.WorldEditorTests.Rig;

namespace OpenFPS.Tests;

/// <summary>
/// World editor phase 3 (docs/WORLD_EDITOR.md section 18): the list of things changed from the map file
/// and putting them back, versions of a map's edits and baking them into a map file, roads and railways
/// laid as data, and people.
/// </summary>
public partial class WorldEditorPhase3Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "openfps-editor3-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static IEnumerable<EditorMenuItem> In(EditorMenu menu, string section) => menu.Items.Where(i => i.Section == section);

    private static string NameOf(Rig rig, int id) => rig.World(rig.Tester.CurrentMapId).Get<IdentityComponent>(rig.Thing(id)).Name;

    /// <summary>A map of the tester's with things of its own: ground, a wall, a fire pit and a brick wall.</summary>
    private Rig Yard(UserRole role = UserRole.Player, bool composites = false)
    {
        var rig = new Rig(_dir, role, composites: composites);
        var map = MapTemplates.Flat("yard", "tester");
        map.Entities.Add(new EntityData { EntityId = 2, PrefabId = "concrete_wall", Position = new Vector3(10, 1.5f, 0) });
        map.Entities.Add(new EntityData { EntityId = 3, PrefabId = "fire_pit", Position = new Vector3(-10, 0.3f, 0), Name = "Pit" });
        map.Entities.Add(new EntityData { EntityId = 4, PrefabId = "brick_wall", Position = new Vector3(0, 1.5f, 20) });
        Assert.True(rig.Maps.CreateMap(map, out string error), error);
        rig.On("yard");
        return rig;
    }

    // ── Changed on this map ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void ChangedListsWhatWasDoneToTheMapFilesThingsAndWhereTheyAre()
    {
        var rig = Yard();
        rig.Run("edit", "select", "#2");
        rig.Run("edit", "nudge", "east", "2");
        rig.Run("edit", "turn", "90");
        rig.Run("edit", "set", "name", "Old Wall");
        rig.Run("edit", "select", "#3");
        rig.Run("edit", "set", "volume", "0.5");
        rig.Run("edit", "select", "#4");
        Assert.Equal("Deleted Brick Wall. Undo puts it back.", rig.Run("edit", "delete"));

        var menu = rig.Menu("changed")!;
        Assert.StartsWith("3 things from the map file changed or removed, nearest first", menu.Title);
        var rows = menu.Items.Skip(1).Select(i => i.Label).ToList();
        Assert.Equal(3, rows.Count);
        Assert.Contains(rows, r => r.StartsWith("Old Wall, Concrete Wall, moved 2 metres east, turned 90 degrees clockwise, name Old Wall, "));
        Assert.Contains(rows, r => r.StartsWith("Pit, Fire, volume 0.5, "));
        Assert.Contains(rows, r => r.StartsWith("Brick Wall, removed, it was ") && r.EndsWith("north"));

        // Filtered by what was done, and by distance.
        Assert.Single(rig.Menu("changed", "removed")!.Items.Skip(1));
        Assert.Single(rig.Menu("changed", "within", "10")!.Items.Skip(1));
        // The root menu counts them.
        Assert.Contains(rig.Menu("menu")!.Items, i => i.Label == "Changed on this map, 3");
    }

    [Fact]
    public void PutBackMakesItWhatTheMapFileSaysAndUndoChangesItAgain()
    {
        var rig = Yard();
        var file = rig.PoseOf(2);
        rig.Run("edit", "select", "#2");
        rig.Run("edit", "nudge", "east", "2");
        rig.Run("edit", "turn", "90");
        rig.Run("edit", "set", "name", "Old Wall");
        var moved = rig.PoseOf(2);
        Assert.Single(rig.Overlay("yard").Changed);

        string said = rig.Run("edit", "putback", "#2");
        Assert.StartsWith("Put back Old Wall as the map has it, ", said);
        Assert.EndsWith("Undo changes it again.", said);
        Assert.True(rig.PoseOf(2).Near(file));
        Assert.Equal("Concrete Wall", NameOf(rig, 2));
        Assert.Empty(rig.Overlay("yard").Changed);
        Assert.Empty(rig.Overlay("yard").Removed);

        Assert.Equal("Undid: put back Old Wall as the map has it.", rig.Run("edit", "undo"));
        Assert.True(rig.PoseOf(2).Near(moved));
        Assert.Equal("Old Wall", NameOf(rig, 2));
        Assert.Equal("Old Wall", Assert.Single(rig.Overlay("yard").Changed).Settings!["Name"]);
        Assert.Empty(rig.Overlay("yard").Removed);
        Assert.Equal("Redid: put back Old Wall as the map has it.", rig.Run("edit", "redo"));
        Assert.Empty(rig.Overlay("yard").Changed);
    }

    [Fact]
    public void ARemovedThingIsBroughtBackAndGoneToWhereItWas()
    {
        var rig = Yard(UserRole.Dev);
        rig.Run("edit", "select", "#4");
        rig.Run("edit", "delete");
        Assert.False(rig.Exists(4));
        Assert.Equal("Brick Wall", Assert.Single(rig.Overlay("yard").Removed).Name);

        Assert.Equal("You are beside where Brick Wall was, facing it.", rig.Run("edit", "goto", "#4"));
        rig.On("yard");
        string said = rig.Run("edit", "putback", "#4");
        Assert.StartsWith("Brought back Brick Wall, ", said);
        Assert.True(rig.Exists(4));
        Assert.Equal(new Vector3(0, 1.5f, 20), rig.PoseOf(4).Position);
        Assert.Empty(rig.Overlay("yard").Removed);
        Assert.Equal("Undid: brought back Brick Wall.", rig.Run("edit", "undo"));
        Assert.False(rig.Exists(4));
        Assert.Single(rig.Overlay("yard").Removed);
    }

    [Fact]
    public void PutBackIsRefusedWhereSomebodyStandsAndForWhatWasNeverChanged()
    {
        var rig = Yard();
        rig.Run("edit", "select", "#2");
        rig.Run("edit", "nudge", "north", "3");
        // The other player stands where the map file has the wall.
        rig.Stand(rig.Other, "yard", new Vector3(10, 0.05f, 0));
        var moved = rig.PoseOf(2);
        Assert.Equal("Nothing put back: that would put Concrete Wall through other.", rig.Run("edit", "putback", "#2"));
        Assert.True(rig.PoseOf(2).Near(moved));
        Assert.Single(rig.Overlay("yard").Changed);
        Assert.Empty(rig.Overlay("yard").Removed);

        Assert.StartsWith("Nothing put back: number 3 is not something from the map file", rig.Run("edit", "putback", "#3"));
        Assert.StartsWith("Say /edit putback #NUMBER", rig.Run("edit", "putback"));
    }

    [Fact]
    public void TheDialogListsChangedThingsAndKeepsTheChoiceInTheListAfterAPutBack()
    {
        var rig = Yard();
        foreach (var id in new[] { "#2", "#3" })
        {
            rig.Run("edit", "select", id);
            rig.Run("edit", "nudge", "south", "1");
        }
        rig.Run("edit", "select", "#4");
        rig.Run("edit", "delete");
        rig.Run("edit", "dialog", "open", "edit");
        var sent = new List<string>();
        var editor = new EditorDialog(rig.LastMenu!, new EditorDialogMemory { Tab = "edit" }, new BuildMemory(), sent.Add);
        var tab = editor.Tabs[1];
        var section = tab.Sections.Single(s => s.Id == "changed");
        Assert.Equal("Changed on this map", section.Title);
        Assert.Equal(new[] { "edit.changedfilter", "edit.changedfiltergo", "edit.changed", "edit.changedputback", "edit.changedgoto", "edit.changedchoose" },
                     section.Controls.Select(c => c.Id));
        // It comes after the list of things placed.
        Assert.Equal(tab.Sections.ToList().FindIndex(s => s.Id == "placed") + 1, tab.Sections.ToList().FindIndex(s => s.Id == "changed"));
        var list = tab.Find("edit.changed")!;
        Assert.Equal("Changed on this map", list.Label);
        Assert.StartsWith("3 things from the map file changed or removed, nearest first.", list.Description);
        Assert.Equal(3, list.Items.Count);

        // The middle one put back: asked, sent, and the one after it chosen once it has gone.
        list.Selected = 1;
        editor.Changed(list);
        string? question = null;
        Action? yes = null;
        editor.ConfirmAsked += (q, y) => { question = q; yes = y; };
        string middle = list.Items[1].Value, next = list.Items[2].Value;
        editor.Press("edit.changedputback");
        Assert.EndsWith("as the map has it?", question);
        yes!();
        Assert.Equal($"/edit putback #{middle} dialog", sent[^1]);
        Assert.Contains(" as the map has it", rig.Run("edit", "putback", $"#{middle}", "dialog"));
        editor.Update(rig.Replies.OfType<EditorMenu>().Last(m => m.Path == "dialog.edit"));
        list = tab.Find("edit.changed")!;
        Assert.Equal(2, list.Items.Count);
        Assert.Equal(next, list.SelectedValue);

        // A removed row cannot be edited until it is back; the filter is sent and kept.
        string? why = null;
        editor.Said += w => why = w;
        list.Select("4");
        editor.Press("edit.changedchoose");
        Assert.Equal("Brick Wall has been removed. Put it back first to change it.", why);
        tab.Find("edit.changedfilter")!.Text = "removed";
        editor.Press("edit.changedfiltergo");
        Assert.Equal("/edit changed removed dialog", sent[^1]);
        rig.Run("edit", "changed", "removed", "dialog");
        editor.Update(rig.Replies.OfType<EditorMenu>().Last(m => m.Path == "dialog.edit"));
        Assert.Single(tab.Find("edit.changed")!.Items);
        Assert.Equal("removed", tab.Find("edit.changedfilter")!.Text);
    }

    [Fact]
    public void ADialogWithNothingChangedSaysSoInItsList()
    {
        var rig = Yard();
        rig.Run("edit", "dialog", "open", "edit");
        var editor = new EditorDialog(rig.LastMenu!, new EditorDialogMemory { Tab = "edit" }, new BuildMemory(), _ => { });
        var list = editor.Tabs[1].Find("edit.changed")!;
        Assert.Equal("Nothing from the map file has been changed or removed with the editor", Assert.Single(list.Items).Label);
        Assert.False(editor.Tabs[1].Find("edit.changedputback")!.Enabled);
    }
}

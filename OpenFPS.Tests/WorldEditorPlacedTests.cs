using System.Numerics;
using System.Text.Json;
using OpenFPS.Client.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Editing;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Core;
using OpenFPS.Server.Editor;
using OpenFPS.Server.Repositories;
using Rig = OpenFPS.Tests.WorldEditorTests.Rig;

namespace OpenFPS.Tests;

/// <summary>
/// Place's categories, vehicles and buildings, and the list of everything placed on a map with the
/// editor (docs/WORLD_EDITOR.md section 17): who placed it and when, filtered, removed and gone to from
/// anywhere, undone, and the dialog keeping its choice in the list after a removal.
/// </summary>
public class WorldEditorPlacedTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "openfps-placed-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static IEnumerable<EditorMenuItem> In(EditorMenu menu, string section) => menu.Items.Where(i => i.Section == section);

    private Rig Mine(UserRole role = UserRole.Player, bool composites = false, bool models = false)
    {
        var rig = new Rig(_dir, role, composites: composites, models: models);
        rig.On("mine");
        return rig;
    }

    // ── Categories ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EveryPrefabIsInTheCategoryOfWhatItIs()
    {
        var prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs")).Prefabs;
        string Of(string id) => WorldEditor.CategoryOf(prefabs[id]);
        Assert.Equal(WorldEditor.BuildingsCategory, Of("building_box"));
        foreach (var id in new[] { "concrete_stairs", "wooden_stairs", "concrete_ramp", "staircase_acoustic" }) Assert.Equal("Stairs and ramps", Of(id));
        foreach (var id in new[] { "furniture_soft", "grandstand_seating" }) Assert.Equal("Furniture and seating", Of(id));
        // A beacon is a thing that sounds; a place is a room, a region, a doorway or a name.
        foreach (var id in new[] { "pa_speaker", "space_megaphone", "chirp_beacon", "sound_emitter" }) Assert.Equal("Sounds", Of(id));
        foreach (var id in new[] { "named_place", "portal", "acoustic_region", "stair_marker", "crowd" }) Assert.Equal("Places and markers", Of(id));
        Assert.Equal("Walls and fences", Of("brick_arch"));
        Assert.Equal("Machines", Of("mower_push"));
        // Every category a prefab lands in is listed, and nothing is left over as Other.
        Assert.All(prefabs.Values, t => Assert.Contains(WorldEditor.CategoryOf(t), WorldEditor.Categories));
        Assert.DoesNotContain(prefabs.Values, t => WorldEditor.CategoryOf(t) == "Other");
        Assert.Equal(new[] { WorldEditor.BuildingsCategory, WorldEditor.VehiclesCategory }, WorldEditor.Categories.Take(2));
    }

    [Fact]
    public void PlaceListsEveryVehicleSpawnTakesUnderVehiclesNearTheTop()
    {
        var rig = Mine(composites: true);
        var place = rig.Menu("menu", "place")!;
        var categories = place.Items.Where(i => i.Command.StartsWith("place.cat:")).Select(i => i.Command["place.cat:".Length..]).ToList();
        Assert.Equal(new[] { WorldEditor.BuildingsCategory, WorldEditor.VehiclesCategory }, categories.Take(2));

        var vehicles = rig.Menu("menu", "place.cat:Vehicles")!;
        var presets = WorldEditor.VehiclePresets();
        Assert.Contains("helicopter", presets);
        Assert.Contains("i4_economy", presets);
        Assert.DoesNotContain("airliner", presets);
        Assert.Equal(presets.Count, vehicles.Items.Length);
        Assert.All(presets, p => Assert.Contains(vehicles.Items, i => i.Command == $"edit place vehicle:{p}"));
        Assert.Contains(vehicles.Items, i => i.Label.StartsWith("1.6 hatchback, "));

        // The dialog's rows come in the same order, so its categories do too.
        rig.Run("edit", "dialog", "open", "place");
        var rows = In(rig.LastMenu!, "place.prefab").ToList();
        Assert.Equal(new[] { WorldEditor.BuildingsCategory, WorldEditor.VehiclesCategory }, rows.Select(r => r.Prompt).Distinct().Take(2));
        Assert.Contains(rows, r => r.Value == "vehicle:i4_economy" && r.Prompt == WorldEditor.VehiclesCategory && r.Count == 0);
    }

    [Fact]
    public void AVehiclePlacedIsParkedKeptInTheOverlayUndoneAndParkedAgainOnRestart()
    {
        var rig = Mine(composites: true);
        string said = rig.Run("edit", "place", "vehicle:i4_economy");
        Assert.StartsWith("Placed: 1.6 hatchback, parked ", said);
        Assert.Contains("Anyone may drive it", said);
        var added = Assert.Single(rig.Overlay("mine").Added);
        Assert.Equal("vehicle:i4_economy", added.Entity.PrefabId);
        Assert.Equal("tester", added.PlacedBy);
        Assert.NotNull(added.PlacedAt);
        Assert.Single(Parked(rig, "vehicle:i4_economy"));
        // Kept in the overlay, never in the map's own data: /savemap does not write it.
        Assert.True(rig.Maps.TryGetMapData("mine", out var data));
        Assert.DoesNotContain(data.Entities, e => e.PrefabId.StartsWith("vehicle:"));
        Assert.True(data.Composites == null || data.Composites.Count == 0);

        Assert.Equal("Undid: parked 1.6 hatchback.", rig.Run("edit", "undo"));
        Assert.Empty(Parked(rig, "vehicle:i4_economy"));
        Assert.Empty(rig.Overlay("mine").Added);
        Assert.Equal("Redid: parked 1.6 hatchback.", rig.Run("edit", "redo"));
        Assert.Single(Parked(rig, "vehicle:i4_economy"));
        var at = Assert.Single(rig.Overlay("mine").Added).Entity.Position;

        // A new server over the same folders parks it again where it was left.
        var again = new Rig(_dir, UserRole.Player, mapDir: rig.MapDir, composites: true);
        Assert.False(again.Maps.TryGetMapData("mine", out var reloaded) && reloaded.Entities.Any(e => e.PrefabId.StartsWith("vehicle:")));
        Assert.Equal(1, again.Editor.ParkKeptVehicles());
        var parked = Assert.Single(Parked(again, "vehicle:i4_economy"));
        Assert.Equal(at, parked);
    }

    [Fact]
    public void AnAircraftIsParkedOnOpenGroundAndPreviewHasNothingToPlay()
    {
        var rig = Mine(composites: true);
        Assert.Equal("A parked vehicle has its engine off, so there is nothing to hear until somebody starts it.", rig.Run("edit", "preview", "vehicle:helicopter"));
        Assert.StartsWith("Placed: Helicopter, parked ", rig.Run("edit", "place", "vehicle", "helicopter"));
        Assert.Equal("vehicle:helicopter", Assert.Single(rig.Overlay("mine").Added).Entity.PrefabId);
        Assert.StartsWith("There is no vehicle called airliner", rig.Run("edit", "place", "vehicle:airliner"));
    }

    private static List<Vector3> Parked(Rig rig, string template)
    {
        var found = new List<Vector3>();
        rig.World("mine").Query(new Arch.Core.QueryDescription().WithAll<CompositeComponent, Transform>(), (ref CompositeComponent c, ref Transform t) =>
        {
            if (c.TemplateId == template) found.Add(t.Position);
        });
        return found;
    }

    [Fact]
    public void ThingsSavedAsABuildingAreListedUnderBuildings()
    {
        var rig = Mine(UserRole.Dev, models: true);
        rig.Run("edit", "place", "concrete_wall");
        int first = rig.Selected;
        rig.Run("edit", "nudge", "east", "3");
        rig.Run("edit", "place", "concrete_floor");
        int second = rig.Selected;
        rig.Run("edit", "select", "add", $"#{first}");
        rig.Run("edit", "select", "add", $"#{second}");
        string house = "house_" + Guid.NewGuid().ToString("N")[..6];
        Assert.StartsWith($"Made the building {house} from 2 things", rig.Run("edit", "building", house));
        Assert.True(rig.Editor.GroupOf(house)!.Building);
        var buildings = rig.Menu("menu", "place.cat:Buildings")!;
        Assert.Contains(buildings.Items, i => i.Command == $"edit place group {house}");
        Assert.Contains(buildings.Items, i => i.Command == "edit place building_box");
        var groups = rig.Menu("menu", "place.cat:Groups");
        Assert.True(groups == null || !groups.Items.Any(i => i.Command == $"edit place group {house}"));
        // Placed again from Buildings, as the dialog sends it.
        rig.Stand(rig.Tester, "mine", new Vector3(-20, 0.05f, -20));
        Assert.StartsWith($"Placed the group {house}, 2 things", rig.Run("edit", "place", $"group:{house}"));
    }

    // ── The list of what was placed ─────────────────────────────────────────────────────────────

    [Fact]
    public void ThePlacedListSaysWhereWhoAndWhenAndFilters()
    {
        var rig = Mine();
        rig.Run("edit", "place", "space_megaphone");
        int megaphone = rig.Selected;
        rig.Run("edit", "place", "fire_pit");
        rig.Stand(rig.Tester, "mine", new Vector3(-35, 0.05f, -35));

        var menu = rig.Menu("placed")!;
        Assert.Equal("2 things placed on this map, nearest first", menu.Title);
        var row = Assert.Single(menu.Items, i => i.Label.StartsWith("Space Megaphone, "));
        Assert.Matches(@"^Space Megaphone, \d+ metres north east, placed by tester, \d+ \w+ \d\d:\d\d$", row.Label);
        Assert.Equal($"placedone:{megaphone}", row.Command);

        var filtered = rig.Menu("placed", "megaphone")!;
        Assert.Equal("1 of 2 placed on this map match megaphone, nearest first", filtered.Title);
        Assert.Single(filtered.Items, i => i.Kind == EditorItemKind.Menu);
        Assert.Empty(rig.Menu("placed", "within", "10", "metres")!.Items.Where(i => i.Kind == EditorItemKind.Menu));
        Assert.Equal(2, rig.Menu("placed", "within", "60")!.Items.Count(i => i.Kind == EditorItemKind.Menu));
        Assert.Equal(2, rig.Menu("placed", "tester")!.Items.Count(i => i.Kind == EditorItemKind.Menu));

        // An entry kept before anybody was recorded.
        rig.Overlay("mine").AdditionFor(megaphone)!.PlacedBy = null;
        Assert.Contains(rig.Menu("placed", "megaphone")!.Items, i => i.Label.EndsWith(", placed earlier"));

        // A text client hears it as a line, with numbers to type.
        rig.Tester.IsTextClient = true;
        string said = rig.Run("edit", "placed");
        Assert.Contains($"#{megaphone} Space Megaphone, ", said);
        Assert.Contains("/edit remove #NUMBER", said);
    }

    [Fact]
    public void ThingsAreRemovedFromAfarByNumberAndOneUndoPutsThemBack()
    {
        var rig = Mine();
        rig.Run("edit", "place", "space_megaphone");
        int megaphone = rig.Selected;
        rig.Run("edit", "place", "fire_pit");
        int fire = rig.Selected;
        rig.Stand(rig.Tester, "mine", new Vector3(-35, 0.05f, -35));

        string said = rig.Run("edit", "remove", $"#{megaphone}");
        Assert.Matches(@"^Removed Space Megaphone, \d+ metres north east\. Undo puts it back\.$", said);
        Assert.False(rig.Exists(megaphone));
        Assert.Null(rig.Overlay("mine").AdditionFor(megaphone));
        Assert.Equal("Undid: deleted Space Megaphone.", rig.Run("edit", "undo"));
        Assert.True(rig.Exists(megaphone));
        // Who placed it is kept through the removal and its undo.
        Assert.Equal("tester", rig.Overlay("mine").AdditionFor(megaphone)!.PlacedBy);

        Assert.StartsWith("Removed 2 things: ", rig.Run("edit", "remove", $"#{megaphone}", $"#{fire}"));
        Assert.Empty(rig.Overlay("mine").Added);
        Assert.Equal("Undid: removed 2 things.", rig.Run("edit", "undo"));
        Assert.Equal(2, rig.Overlay("mine").Added.Count);

        Assert.StartsWith("Nothing removed: there is nothing numbered 12345", rig.Run("edit", "remove", "#12345"));
        Assert.Equal(2, rig.Overlay("mine").Added.Count);
    }

    [Fact]
    public void TickedThingsAreRemovedTogether()
    {
        var rig = Mine();
        rig.Run("edit", "place", "space_megaphone");
        rig.Run("edit", "place", "fire_pit");
        rig.Run("edit", "placed", "megaphone", "dialog");
        Assert.StartsWith("Holding 1 thing more", rig.Run("edit", "select", "add", "placed"));
        Assert.StartsWith("Removed Space Megaphone", rig.Run("edit", "remove", "held"));
        Assert.Single(rig.Overlay("mine").Added);
        Assert.Equal("Nothing is ticked or held to remove.", rig.Run("edit", "remove", "held"));
    }

    [Fact]
    public void GoingToAThingNeedsTheMovePermissionHere()
    {
        var rig = Mine();
        rig.Run("edit", "place", "fire_pit");
        int fire = rig.Selected;
        rig.Stand(rig.Tester, "mine", new Vector3(-35, 0.05f, -35));
        Assert.Equal("You are beside Fire, facing it.", rig.Run("edit", "goto", $"#{fire}"));
        var at = rig.World("mine").Get<Transform>(rig.Tester.Entity).Position;
        Assert.InRange(Vector3.Distance(new Vector3(at.X, 0, at.Z), new Vector3(5, 0, 5.65f)), 0.5f, 2.5f);

        // An editor the owner named may edit here, but /move is not theirs.
        Assert.True(rig.Maps.TryGetMapData("mine", out var d));
        d.Editors.Add("other");
        Assert.StartsWith("Going to a thing needs the move permission", rig.RunAs(rig.Other, "edit", "goto", $"#{fire}"));
    }

    [Fact]
    public void WhoMayNotEditHereMayNotListOrRemove()
    {
        var rig = new Rig(_dir, UserRole.Player);
        rig.On("theirs");
        Assert.Equal("You do not have permission to execute this command.", rig.Run("edit", "placed"));
        Assert.Equal("You do not have permission to execute this command.", rig.Run("edit", "remove", "#1"));
    }

    [Fact]
    public void WhoAndWhenAreKeptInTheOverlayFileAndOldFilesStillRead()
    {
        var rig = Mine();
        rig.Run("edit", "place", "fire_pit");
        string json = MapOverlayStore.ToJson(rig.Overlay("mine"));
        Assert.Contains("\"PlacedBy\": \"tester\"", json);
        Assert.Contains("\"PlacedAt\"", json);
        var old = JsonSerializer.Deserialize<MapOverlay>("{\"MapId\":\"mine\",\"Added\":[{\"Entity\":{\"EntityId\":900000000,\"PrefabId\":\"fire_pit\"}}]}", MapRepository.JsonOptions)!;
        Assert.Null(Assert.Single(old.Added).PlacedBy);
    }

    // ── The dialog ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheDialogListsWhatWasPlacedAndKeepsTheChoiceInTheListAfterARemoval()
    {
        var rig = Mine();
        rig.Run("edit", "place", "space_megaphone");
        rig.Run("edit", "place", "fire_pit");
        rig.Run("edit", "nudge", "east", "2");
        rig.Run("edit", "place", "pa_speaker");
        rig.Stand(rig.Tester, "mine", new Vector3(-35, 0.05f, -35));
        rig.Run("edit", "dialog", "open", "edit");
        var sent = new List<string>();
        var editor = new EditorDialog(rig.LastMenu!, new EditorDialogMemory { Tab = "edit" }, new BuildMemory(), sent.Add);
        var tab = editor.Tabs[1];
        var section = tab.Sections.Single(s => s.Id == "placed");
        Assert.Equal("Placed on this map", section.Title);
        Assert.Equal(new[] { "edit.placedfilter", "edit.placedfiltergo", "edit.placed", "edit.placedremove", "edit.placedgoto", "edit.placedchoose", "edit.placedtickall" },
                     section.Controls.Select(c => c.Id));
        var list = tab.Find("edit.placed")!;
        Assert.Equal("Placed on this map", list.Label);
        Assert.StartsWith("3 things placed on this map, nearest first.", list.Description);
        Assert.Equal(3, list.Items.Count);
        Assert.True(list.Checkable);
        Assert.Equal("edit.placedremove", list.Delete);
        Assert.True(tab.Find("edit.placedgoto")!.Enabled);

        // Remove the middle one: asked, sent, and the one after it is chosen once it has gone.
        list.Selected = 1;
        editor.Changed(list);
        string? question = null;
        Action? yes = null;
        editor.ConfirmAsked += (q, y) => { question = q; yes = y; };
        string middle = list.Items[1].Value, next = list.Items[2].Value;
        editor.Press("edit.placedremove");
        Assert.StartsWith("Remove ", question);
        Assert.EndsWith("?", question);
        yes!();
        Assert.Equal($"/edit remove #{middle} dialog", sent[^1]);
        Assert.StartsWith("Removed ", rig.Run("edit", "remove", $"#{middle}", "dialog"));
        editor.Update(rig.Replies.OfType<EditorMenu>().Last(m => m.Path == "dialog.edit"));
        list = tab.Find("edit.placed")!;
        Assert.Equal(2, list.Items.Count);
        Assert.Equal(next, list.SelectedValue);

        // Ticking goes through the server, as Things near you does.
        editor.Toggle(list, 0);
        Assert.Equal($"/edit select add #{list.Items[0].Value} dialog", sent[^1]);

        // The filter is the server's: sent, and kept.
        var filter = tab.Find("edit.placedfilter")!;
        filter.Text = "fire";
        editor.Press("edit.placedfiltergo");
        Assert.Equal("/edit placed fire dialog", sent[^1]);
        rig.Run("edit", "placed", "fire", "dialog");
        editor.Update(rig.Replies.OfType<EditorMenu>().Last(m => m.Path == "dialog.edit"));
        Assert.Single(tab.Find("edit.placed")!.Items);
        Assert.Equal("fire", tab.Find("edit.placedfilter")!.Text);
    }

    [Fact]
    public void ADialogWithNothingPlacedSaysSoInItsList()
    {
        var rig = Mine();
        rig.Run("edit", "dialog", "open", "edit");
        var editor = new EditorDialog(rig.LastMenu!, new EditorDialogMemory { Tab = "edit" }, new BuildMemory(), _ => { });
        var list = editor.Tabs[1].Find("edit.placed")!;
        Assert.Equal("Nothing has been placed on this map with the editor", Assert.Single(list.Items).Label);
        Assert.False(editor.Tabs[1].Find("edit.placedremove")!.Enabled);
    }
}

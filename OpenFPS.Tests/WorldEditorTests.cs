using System.Numerics;
using System.Security.Cryptography;
using Arch.Core;
using MemoryPack;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Editing;
using OpenFPS.Common.Networking;
using OpenFPS.Server;
using OpenFPS.Server.Core;
using OpenFPS.Server.Editor;
using OpenFPS.Server.Repositories;
using EntityData = OpenFPS.Server.Repositories.EntityData;

namespace OpenFPS.Tests;

/// <summary>
/// The world editor, phase 1 (docs/WORLD_EDITOR.md): who may use it, every operation and its undo,
/// the overlay that keeps edits (and leaves a generated map byte for byte as it was), the ranges a
/// field holds a value to, and models changed for every map.
/// </summary>
public class WorldEditorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "openfps-editor-" + Guid.NewGuid().ToString("N"));
    private const string Denied = "You do not have permission to execute this command.";

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    // ── Who may ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AnOwnerEditsTheirOwnMapAndIsRefusedOnSomebodyElses()
    {
        var rig = new Rig(_dir, UserRole.Player);
        rig.On("mine");
        Assert.IsType<EditorMenu>(rig.Menu("menu"));
        Assert.StartsWith("Placed: Concrete Wall", rig.Run("edit", "place", "concrete_wall"));

        rig.On("theirs");
        Assert.Equal(WorldEditor.Refusal, rig.Run("edit"));
        Assert.Equal(WorldEditor.Refusal, rig.Run("edit", "menu"));
        Assert.Equal(Denied, rig.Run("edit", "place", "concrete_wall"));
        Assert.Null(rig.LastMenu);
    }

    [Fact]
    public void AnEditorTheOwnerNamedMayEditAndNothingMore()
    {
        var rig = new Rig(_dir, UserRole.Player);
        rig.On("theirs");
        Assert.Equal(WorldEditor.Refusal, rig.Run("edit"));
        Assert.True(rig.Maps.TryGetMapData("theirs", out var theirs));
        theirs.Editors.Add("tester");

        Assert.IsType<EditorMenu>(rig.Menu("menu"));
        Assert.StartsWith("Placed", rig.Run("edit", "place", "concrete_wall"));
        // The editor and nothing else an owner may do.
        Assert.Equal(Denied, rig.Run("spawn", "Box", "Metal", "1", "1", "1"));
        Assert.Equal(Denied, rig.Run("savemap"));
    }

    [Fact]
    public void TheOwnerNamesEditorsAndItIsKept()
    {
        var rig = new Rig(_dir, UserRole.Player);
        rig.On("mine");
        Assert.Equal("other may edit mine with the world editor.", rig.Run("map", "editor", "add", "other"));
        Assert.True(rig.Maps.IsEditor("mine", "other"));
        Assert.Contains(rig.SentTo("other"), m => m is TextEvent t && t.Text.Contains("F12"));
        // Kept with the access, not in the map file, and an editor may come in.
        var access = new MapAccessRepository(rig.AccessPath);
        var copy = new MapData { Id = "mine" };
        access.ApplyTo(copy);
        Assert.Contains("other", copy.Editors);
        Assert.Contains("other", copy.Invited);

        rig.On("theirs");
        Assert.Equal("theirs is not yours.", rig.Run("map", "editor", "add", "other"));
    }

    [Theory]
    [InlineData(UserRole.Moderator, false)]
    [InlineData(UserRole.Dev, true)]
    [InlineData(UserRole.Admin, true)]
    public void StaffEditAnyMapButModeratorsDoNot(UserRole role, bool may)
    {
        var rig = new Rig(_dir, role);
        rig.On("theirs");
        if (may) Assert.IsType<EditorMenu>(rig.Menu("menu"));
        else Assert.Equal(WorldEditor.Refusal, rig.Run("edit"));
    }

    [Fact]
    public void ChangingAModelNeedsEditModels()
    {
        string id = TestModel();
        var owner = new Rig(_dir, UserRole.Player);
        owner.On("mine");
        Assert.Equal(WorldEditor.ModelsRefusal, owner.Run("edit", "model", "set", "small_machine", id, "Compressor.HumDb", "66"));
        // Looking is everybody's who may edit.
        Assert.Contains("version 0", owner.Run("edit", "model", "show", "machine", id));
    }

    // ── Every operation, and its undo ───────────────────────────────────────────────────────────

    [Fact]
    public void PlaceUndoesAndRedoesExactly()
    {
        var rig = new Rig(_dir, UserRole.Player);
        rig.On("mine");
        Assert.Equal("Placed: Concrete Wall, 2 by 0.5 by 3 metres high, 0.65 metres in front of you, facing north. It is selected.", rig.Run("edit", "place", "concrete_wall"));
        int id = rig.Selected;
        Assert.True(id >= MapOverlay.FirstAddedId);
        var placed = rig.PoseOf(id);
        Assert.Equal(new Vector3(5f, 0.05f + 1.5f, 5f + 0.65f), placed.Position);
        Assert.Single(rig.Overlay("mine").Added);

        Assert.Equal("Undid: placed Concrete Wall.", rig.Run("edit", "undo"));
        Assert.False(rig.Exists(id));
        Assert.Empty(rig.Overlay("mine").Added);

        Assert.Equal("Redid: placed Concrete Wall.", rig.Run("edit", "redo"));
        Assert.True(rig.Exists(id));
        Assert.Equal(placed, rig.PoseOf(id));
        Assert.Equal("Nothing to redo.", rig.Run("edit", "redo"));
    }

    [Fact]
    public void MovesNudgesAndTurnsUndoExactly()
    {
        var rig = new Rig(_dir, UserRole.Player);
        rig.On("mine");
        rig.Run("edit", "place", "fire_pit");
        int id = rig.Selected;
        var start = rig.PoseOf(id);

        // Player axes: east, north, up.
        Assert.Equal("Moved Fire 2 metres north and 1 metre east and 0.5 metres up.", rig.Run("edit", "move", "1", "2", "0.5"));
        Assert.Equal(start.Position + new Vector3(1f, 0.5f, 2f), rig.PoseOf(id).Position);
        var moved = rig.PoseOf(id);

        Assert.Equal("Moved Fire 0.5 metres north.", rig.Run("edit", "nudge", "north"));
        Assert.Equal("Step 0.25 metres.", rig.Run("edit", "step", "0.25"));
        Assert.Equal("Moved Fire 0.25 metres east.", rig.Run("edit", "nudge", "right"));   // facing north
        Assert.Equal("Turned Fire 90 degrees clockwise.", rig.Run("edit", "turn", "90"));
        Assert.Equal(1f, Vector3.Transform(Vector3.UnitZ, rig.PoseOf(id).Rotation).X, 4);   // faces east
        Assert.Equal("Fire faces south.", rig.Run("edit", "face", "south"));

        // Four undos come back to the first move, exactly.
        for (int i = 0; i < 4; i++) Assert.StartsWith("Undid:", rig.Run("edit", "undo"));
        Assert.Equal(moved, rig.PoseOf(id));
        Assert.Equal("Undid: moved Fire.", rig.Run("edit", "undo"));
        Assert.Equal(start, rig.PoseOf(id));
        // ...and the overlay says where it is now.
        var added = rig.Overlay("mine").AdditionFor(id)!;
        Assert.Equal(start.Position, added.Entity.Position);
    }

    [Fact]
    public void DuplicateAndDeleteUndo()
    {
        var rig = new Rig(_dir, UserRole.Player);
        rig.On("mine");
        rig.Run("edit", "place", "concrete_wall");
        int wall = rig.Selected;
        Assert.Equal("Copied Concrete Wall, 0.5 metres north of it. The copy is selected.", rig.Run("edit", "duplicate"));
        int copy = rig.Selected;
        Assert.NotEqual(wall, copy);
        Assert.Equal(rig.PoseOf(wall).Position + new Vector3(0, 0, 0.5f), rig.PoseOf(copy).Position);
        Assert.Equal("Undid: copied Concrete Wall.", rig.Run("edit", "undo"));
        Assert.False(rig.Exists(copy));

        // The map's own ground (dirt): deleted, and put back where it was.
        Assert.StartsWith("Selected Ground", rig.Run("edit", "select", "#1"));
        var floor = rig.PoseOf(1);
        Assert.Equal("Deleted Ground. Undo puts it back.", rig.Run("edit", "delete"));
        Assert.False(rig.Exists(1));
        Assert.Single(rig.Overlay("mine").Removed);
        Assert.Equal("Undid: deleted Ground.", rig.Run("edit", "undo"));
        Assert.True(rig.Exists(1));
        Assert.Equal(floor, rig.PoseOf(1));
        Assert.Empty(rig.Overlay("mine").Removed);
    }

    [Fact]
    public void AMapFileThingUndoneBackToTheFileLeavesNothingInTheOverlay()
    {
        var rig = new Rig(_dir, UserRole.Player);
        rig.On("mine");
        rig.Run("edit", "select", "#1");
        rig.Run("edit", "nudge", "east");
        var change = Assert.Single(rig.Overlay("mine").Changed);
        Assert.Equal(Vector3.Zero, change.Was);
        rig.Run("edit", "undo");
        Assert.Empty(rig.Overlay("mine").Changed);
        // A setting keeps it, even where the file has it.
        rig.Run("edit", "set", "name", "Slab");
        Assert.Equal("Slab", Assert.Single(rig.Overlay("mine").Changed).Settings!["Name"]);
    }

    [Fact]
    public void SettingsChangeTheThingAndUndo()
    {
        var rig = new Rig(_dir, UserRole.Player);
        rig.On("mine");
        rig.Run("edit", "place", "fire_pit");
        int id = rig.Selected;
        var e = rig.Thing(id);
        rig.Server.DrainCommandBuffer();

        Assert.Equal("Volume, 0.5.", rig.Run("edit", "set", "volume", "0.5"));
        Assert.Equal(0.5f, rig.World("mine").Get<SoundEmitterComponent>(e).Volume);
        Assert.Contains(e.Id, rig.Server.PendingDefinitionResends);
        Assert.Equal("Volume must be 0 to 4; 9 is outside it.", rig.Run("edit", "set", "volume", "9"));
        Assert.Equal("Volume, 0.55.", rig.Run("edit", "up", "volume"));
        Assert.Equal("0.55", rig.Overlay("mine").AdditionFor(id)!.Settings!["Volume"]);

        Assert.Equal("Width of Fire, 1.2 m.", rig.Run("edit", "set", "width", "1.2"));
        Assert.Equal(1.2f, rig.World("mine").Get<ColliderComponent>(e).Size.X, 4);
        Assert.Equal(2f, rig.PoseOf(id).Scale.X, 4);

        Assert.Equal("Name, Camp fire.", rig.Run("edit", "set", "name", "Camp", "fire"));
        Assert.Equal("Camp fire", rig.World("mine").Get<IdentityComponent>(e).Name);

        Assert.Equal("Undid: set name of Fire.", rig.Run("edit", "undo"));
        Assert.Equal("Fire", rig.World("mine").Get<IdentityComponent>(e).Name);
        Assert.Equal("Undid: resized Fire.", rig.Run("edit", "undo"));
        Assert.Equal(0.6f, rig.World("mine").Get<ColliderComponent>(e).Size.X, 4);
        Assert.StartsWith("Undid", rig.Run("edit", "undo"));
        Assert.StartsWith("Undid", rig.Run("edit", "undo"));
        Assert.Equal(1f, rig.World("mine").Get<SoundEmitterComponent>(e).Volume);
    }

    [Fact]
    public void SpawnHereMovesTheSpawnAndUndoes()
    {
        var rig = new Rig(_dir, UserRole.Player);
        rig.On("mine");
        Assert.True(rig.Maps.TryGetMapData("mine", out var d));
        var before = d.SpawnPoint.Position;
        Assert.StartsWith("Spawn point set here, at 5.0, 5.0", rig.Run("edit", "spawn", "here"));
        Assert.Equal(new Vector3(5f, 0.05f, 5f), d.SpawnPoint.Position);
        Assert.NotNull(rig.Overlay("mine").Spawn);
        Assert.Equal("Undid: moved the spawn point.", rig.Run("edit", "undo"));
        Assert.Equal(before, d.SpawnPoint.Position);
        Assert.Null(rig.Overlay("mine").Spawn);
    }

    [Fact]
    public void ASolidThingIsNeverPutThroughAPlayer()
    {
        var rig = new Rig(_dir, UserRole.Player);
        rig.On("mine");
        rig.Stand(rig.Other, "mine", new Vector3(5f, 0.05f, 5.7f));
        Assert.Equal("Not placed: that would put Concrete Wall through other.", rig.Run("edit", "place", "concrete_wall"));
        // Something with no solid body may go anywhere.
        Assert.StartsWith("Placed: Fire", rig.Run("edit", "place", "fire_pit"));

        rig.Stand(rig.Other, "mine", new Vector3(5f, 0.05f, 9f));
        rig.Run("edit", "place", "concrete_wall");
        rig.Stand(rig.Other, "mine", new Vector3(5f, 0.05f, 7f));
        Assert.Equal("Not moved: that would put Concrete Wall through other.", rig.Run("edit", "nudge", "north", "1.2"));
    }

    [Fact]
    public void AnUndoIsRefusedWhenSomebodyElseHasChangedTheThingSince()
    {
        var rig = new Rig(_dir, UserRole.Player);
        rig.On("mine");
        rig.Run("edit", "place", "fire_pit");
        int id = rig.Selected;
        rig.Run("edit", "nudge", "north");

        rig.Stand(rig.Other, "mine", new Vector3(20f, 0.05f, 20f));
        Assert.True(rig.Maps.TryGetMapData("mine", out var d));
        d.Editors.Add("other");
        Assert.StartsWith("Selected", rig.RunAs(rig.Other, "edit", "select", $"#{id}"));
        Assert.StartsWith("Moved", rig.RunAs(rig.Other, "edit", "nudge", "east"));
        // The tester was told.
        Assert.Contains(rig.SentTo("tester"), m => m is TextEvent t && t.Text == "other moved Fire.");

        Assert.Equal("Cannot undo: other has changed Fire since.", rig.Run("edit", "undo"));
        // Other's own undo still works, and then the tester's does.
        Assert.Equal("Undid: moved Fire.", rig.RunAs(rig.Other, "edit", "undo"));
        Assert.Equal("Undid: moved Fire.", rig.Run("edit", "undo"));
    }

    [Fact]
    public void SelectingFindsTheNearestByNameAndByNumber()
    {
        var rig = new Rig(_dir, UserRole.Player);
        rig.On("mine");
        rig.Run("edit", "place", "fire_pit");
        int fire = rig.Selected;
        rig.Run("edit", "move", "3", "0", "0");
        Assert.StartsWith("Selected Fire, 2.7 metres right", rig.Run("edit", "select", "nearest"));
        Assert.Equal(fire, rig.Selected);
        Assert.StartsWith("Selected Fire", rig.Run("edit", "select", "fire"));
        Assert.StartsWith("Selected Ground, under you", rig.Run("edit", "select", "#1"));
        Assert.Equal("Nothing here is called piano.", rig.Run("edit", "select", "piano"));
        var within = Assert.IsType<EditorMenu>(rig.Menu("select", "within", "10"));
        Assert.Equal("select.within:10", within.Path);
        Assert.Contains(within.Items, i => i.Label.StartsWith("Ground, under you") && i.Command == "edit select #1");
    }

    // ── Keeping it: the overlay ─────────────────────────────────────────────────────────────────

    [Fact]
    public void TheOverlayReloadsTheMapAsItWasLeftAndTheMapFileIsUntouched()
    {
        var rig = new Rig(_dir, UserRole.Player);
        rig.On("mine");
        string mapFile = Path.Combine(rig.MapDir, "players", "mine.json");
        string before = Hash(mapFile);

        rig.Run("edit", "place", "fire_pit");
        int fire = rig.Selected;
        rig.Run("edit", "move", "2", "1", "0");
        rig.Run("edit", "set", "volume", "0.7");
        rig.Run("edit", "set", "name", "Brazier");
        rig.Run("edit", "select", "#1");
        rig.Run("edit", "set", "depth", "80");
        rig.Run("edit", "spawn", "here");
        var firePose = rig.PoseOf(fire);
        var floorPose = rig.PoseOf(1);

        Assert.Equal(before, Hash(mapFile));
        Assert.True(File.Exists(Path.Combine(rig.MapDir, "overlays", "mine.json")));

        // A new server over the same folders.
        var maps = new MapManager(new MapRepository(rig.MapDir), rig.Prefabs)
        {
            Access = new MapAccessRepository(rig.AccessPath),
            Overlays = new MapOverlayStore(Path.Combine(rig.MapDir, "overlays")),
        };
        maps.Initialize();
        Assert.True(maps.TryGetMap("mine", out var world, out _, out _, out _));
        var authored = maps.AuthoredEntities("mine");
        var e = authored[fire];
        var t = world.Get<Transform>(e);
        Assert.Equal(firePose, new Pose(t.Position, t.Rotation, t.Scale));
        Assert.Equal(0.7f, world.Get<SoundEmitterComponent>(e).Volume, 4);
        Assert.Equal("Brazier", world.Get<IdentityComponent>(e).Name);
        var ft = world.Get<Transform>(authored[1]);
        Assert.Equal(floorPose, new Pose(ft.Position, ft.Rotation, ft.Scale));
        Assert.Equal(80f, world.Get<ColliderComponent>(authored[1]).Size.Z, 3);
        Assert.True(maps.TryGetMapData("mine", out var d));
        Assert.Equal(5f, d.SpawnPoint.Position.X);
        Assert.Equal(5f, d.SpawnPoint.Position.Z);
        Assert.Equal(before, Hash(mapFile));
    }

    [Fact]
    public void AGeneratedMapStaysByteForByteWhatItsGeneratorWrote()
    {
        string mapDir = Path.Combine(_dir, "city-maps");
        Directory.CreateDirectory(mapDir);
        string city = Path.Combine(mapDir, "city.json");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "maps", "city.json"), city);
        string before = Hash(city);

        var rig = new Rig(_dir, UserRole.Dev, mapDir: mapDir, overlays: true);
        Assert.True(rig.Maps.TryGetMapData("city", out var data));
        rig.Stand(rig.Tester, "city", data.SpawnPoint.Position);
        // Something of the map's own near the spawn that is not the ground: a wall, a kerb, a sign.
        var within = rig.Menu("select", "within", "30")!;
        var pick = within.Items.First(i => !i.Label.StartsWith("Ground") && !i.Label.Contains("under you") && !i.Label.Contains("around you"));
        Assert.StartsWith("Selected", rig.Run(pick.Command.Split(' ')[0], pick.Command.Split(' ')[1..]));
        int near = rig.Selected;
        Assert.True(near < MapOverlay.FirstAddedId, "the thing is the map's own");
        Assert.StartsWith("Moved", rig.Run("edit", "nudge", "up", "0.25"));
        var moved = rig.PoseOf(near);
        rig.Run("edit", "place", "fire_pit");
        int fire = rig.Selected;
        var firePose = rig.PoseOf(fire);

        Assert.Equal(before, Hash(city));

        var maps = new MapManager(new MapRepository(mapDir), rig.Prefabs) { Overlays = new MapOverlayStore(Path.Combine(mapDir, "overlays")) };
        maps.Initialize();
        Assert.True(maps.TryGetMap("city", out var world, out _, out _, out _));
        var t = world.Get<Transform>(maps.AuthoredEntities("city")[near]);
        Assert.Equal(moved, new Pose(t.Position, t.Rotation, t.Scale));
        var f = world.Get<Transform>(maps.AuthoredEntities("city")[fire]);
        Assert.Equal(firePose, new Pose(f.Position, f.Rotation, f.Scale));
        Assert.Equal(before, Hash(city));
    }

    [Fact]
    public void AnOverlayFollowsAThingAGeneratorRenumbered()
    {
        var overlay = new MapOverlay
        {
            MapId = "m",
            Changed = { new OverlayChange { Id = 2, Prefab = "concrete_wall", Was = new Vector3(10, 1.5f, 0), Position = new Vector3(11, 1.5f, 0), Rotation = Quaternion.Identity, Scale = Vector3.One } },
            Removed = { new OverlayRemoval { Id = 3, Prefab = "brick_wall", Was = new Vector3(20, 1.5f, 0) } },
            Added = { new OverlayAddition { Entity = new EntityData { EntityId = MapOverlay.FirstAddedId, PrefabId = "fire_pit", Position = new Vector3(1, 0.3f, 1) } } },
        };
        // The generator added a thing at the front: every id is one higher.
        MapData Generated() => new()
        {
            Id = "m",
            Entities =
            {
                new EntityData { EntityId = 1, PrefabId = "concrete_floor" },
                new EntityData { EntityId = 2, PrefabId = "glass_wall", Position = new Vector3(5, 1.5f, 0) },
                new EntityData { EntityId = 3, PrefabId = "concrete_wall", Position = new Vector3(10, 1.5f, 0) },
                new EntityData { EntityId = 4, PrefabId = "brick_wall", Position = new Vector3(20, 1.5f, 0) },
            },
        };
        var store = new MapOverlayStore(null);
        var o = store.Get("m");
        o.Changed.AddRange(overlay.Changed);
        o.Removed.AddRange(overlay.Removed);
        o.Added.AddRange(overlay.Added);

        var map = Generated();
        store.ApplyBefore(map);
        Assert.Equal(new Vector3(5, 1.5f, 0), map.Entities.Single(e => e.EntityId == 2).Position);   // the glass is left alone
        Assert.Equal(new Vector3(11, 1.5f, 0), map.Entities.Single(e => e.EntityId == 3).Position);  // the wall moved
        Assert.DoesNotContain(map.Entities, e => e.PrefabId == "brick_wall");
        Assert.Single(map.Entities, e => e.PrefabId == "fire_pit");
        Assert.Equal(3, o.Changed[0].Id);   // and the entry now names it by its new number

        // Laid twice (a map /savemap'd with its edits in it): the same.
        store.ApplyBefore(map);
        Assert.Single(map.Entities, e => e.PrefabId == "fire_pit");
        Assert.Equal(new Vector3(11, 1.5f, 0), map.Entities.Single(e => e.EntityId == 3).Position);
    }

    [Fact]
    public void AHandWrittenMapsThingsWithoutIdsAreNumberedInFileOrder()
    {
        var map = new MapData
        {
            Id = "h",
            Entities = { new EntityData { PrefabId = "a" }, new EntityData { EntityId = 7, PrefabId = "b" }, new EntityData { PrefabId = "c" } },
        };
        MapOverlayStore.AssignMissingIds(map);
        Assert.Equal(new[] { 8, 7, 9 }, map.Entities.Select(e => e.EntityId));
    }

    // ── Fields and their ranges ─────────────────────────────────────────────────────────────────

    [Fact]
    public void AFieldRefusesWhatItCannotHoldAndSaysTheRange()
    {
        var number = new FieldDescriptor { Path = "HumDb", Label = "hum level", Unit = "dB", Min = 30, Max = 90, Step = 1 };
        Assert.True(number.TryParse("66", out var v, out _));
        Assert.Equal("66", v);
        Assert.False(number.TryParse("120", out _, out string e1));
        Assert.Equal("Hum level must be 30 to 90 dB; 120 is outside it.", e1);
        Assert.False(number.TryParse("loud", out _, out string e2));
        Assert.Equal("Hum level needs a number, 30 to 90 dB.", e2);
        Assert.Equal("90", number.Stepped("89.6", +1));
        Assert.Equal("30", number.Stepped("30", -1));

        var whole = new FieldDescriptor { Path = "PolePairs", Label = "pole pairs", Type = FieldType.Integer, Min = 1, Max = 4 };
        Assert.False(whole.TryParse("1.5", out _, out string e3));
        Assert.Equal("Pole pairs needs a whole number, 1 to 4.", e3);

        var choice = new FieldDescriptor { Path = "Material", Label = "material", Type = FieldType.Choice, Choices = new[] { "Metal", "Wood", "Concrete" } };
        Assert.True(choice.TryParse("wo", out var m, out _));
        Assert.Equal("Wood", m);
        Assert.False(choice.TryParse("steel", out _, out string e4));
        Assert.Equal("Material is one of: Metal, Wood, Concrete.", e4);

        var flag = new FieldDescriptor { Path = "Ducted", Label = "ducted", Type = FieldType.Bool };
        Assert.True(flag.TryParse("on", out var b, out _));
        Assert.Equal("true", b);

        var text = new FieldDescriptor { Path = "Name", Label = "name", Type = FieldType.Text };
        Assert.False(text.TryParse(new string('a', 61), out _, out _));
        Assert.False(new FieldDescriptor { Path = "X", Label = "x", ReadOnly = true }.TryParse("1", out _, out _));
    }

    [Fact]
    public void AMachineDescribesItselfAndEveryBuiltInValueIsInsideItsRange()
    {
        var nodes = ModelKinds.Describe(typeof(SmallMachineSpec));
        var compressor = nodes.Single(n => n.Name == "Compressor");
        Assert.Equal(FieldNodeKind.Group, compressor.Kind);
        var hum = compressor.Children.Single(n => n.Name == "HumDb").Field!;
        Assert.Equal(("hum level", "dB", 30.0, 90.0, false), (hum.Label, hum.Unit, hum.Min, hum.Max, hum.ReadOnly));
        // A property nobody described is shown and cannot be changed.
        var blade = nodes.Single(n => n.Name == "Blade");
        Assert.True(blade.Children.Single(n => n.Name == "BladeVortexInteraction").Field!.ReadOnly);
        Assert.Equal("Compressor.HumDb", ModelKinds.NodeAt(typeof(SmallMachineSpec), "compressor.humdb", out var f) is { } && f != null ? f.Path : "");

        // Every model of every kind: the values it ships with are inside the ranges its fields declare.
        var outside = new List<string>();
        foreach (var kind in ModelLibrary.AllKinds)
        {
            var type = ModelLibrary.TypeOf(kind)!;
            foreach (var id in ModelLibrary.Ids(kind))
            {
                var root = System.Text.Json.Nodes.JsonNode.Parse(ModelLibrary.SpecJson(ModelLibrary.Model(kind, id)))!;
                Walk(ModelKinds.Describe(type), "", root, $"{kind}:{id}", outside);
            }
        }
        Assert.True(outside.Count == 0, "Outside their declared ranges: " + string.Join("; ", outside));
    }

    private static void Walk(IReadOnlyList<FieldNode> nodes, string prefix, System.Text.Json.Nodes.JsonNode root, string model, List<string> outside)
    {
        foreach (var n in nodes)
        {
            string path = prefix + n.Name;
            if (n.Kind == FieldNodeKind.Group) { Walk(n.Children, path + ".", root, model, outside); continue; }
            if (n.Kind == FieldNodeKind.List)
            {
                if (ModelKinds.Get(root, path) is System.Text.Json.Nodes.JsonArray arr)
                    for (int i = 0; i < arr.Count; i++) Walk(n.Children, $"{path}[{i}].", root, model, outside);
                continue;
            }
            var field = n.Field!;
            if (field.ReadOnly || field.Type is not (FieldType.Number or FieldType.Integer)) continue;
            string? value = ModelKinds.GetValue(root, path, n);
            if (value == null) continue;
            double d = double.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
            if (d < field.Min || d > field.Max) outside.Add($"{model} {path} = {value} ({field.RangeText})");
        }
    }

    // ── A model changed for every map ───────────────────────────────────────────────────────────

    [Fact]
    public void AModelChangeIsANewVersionSentToEveryClientAndUndoesToTheOldOne()
    {
        string id = TestModel();
        var rig = new Rig(_dir, UserRole.Dev, models: true);
        rig.On("mine");
        Assert.Equal("Compressor hum level must be 30 to 90 dB; 120 is outside it.",
                     rig.Run("edit", "model", "set", "small_machine", id, "Compressor.HumDb", "120"));
        Assert.Equal($"Compressor hum level of the machine {id}, 66 dB. Version 1, on every map.",
                     rig.Run("edit", "model", "set", "machine", id, "compressor.humdb", "66"));
        Assert.Equal(66f, ModelLibrary.SmallMachine(id).Compressor!.HumDb);
        var update = rig.SentTo("other").OfType<ModelUpdate>().Single();
        Assert.Equal((SmallMachineKind, id, 1), (update.Kind, update.Id, update.Version));
        Assert.True(File.Exists(Path.Combine(rig.ModelDir!, $"small_machine.{id}.json")));

        Assert.StartsWith("Compressor hum level", rig.Run("edit", "model", "up", "machine", id, "Compressor.HumDb"));
        Assert.Equal(67f, ModelLibrary.SmallMachine(id).Compressor!.HumDb);
        Assert.Equal($"Undid: changed compressor hum level of the machine {id}.", rig.Run("edit", "undo"));
        Assert.Equal(66f, ModelLibrary.SmallMachine(id).Compressor!.HumDb);
        Assert.StartsWith("Undid", rig.Run("edit", "undo"));
        Assert.Equal(63f, ModelLibrary.SmallMachine(id).Compressor!.HumDb);

        // A new store over the same folder: the version in use comes back.
        rig.Run("edit", "redo");
        var store = new ModelStore(rig.ModelDir);
        ModelLibrary.Add(SmallMachineKind, id, SmallMachineSpec.AirConditionerCondenser);
        Assert.Equal(1, store.LoadAll());
        Assert.Equal(66f, ModelLibrary.SmallMachine(id).Compressor!.HumDb);
        // ...and every arriving player is sent it.
        Assert.Contains(store.Updates(), u => u.Id == id && u.Version == 1);
    }

    private const string SmallMachineKind = ModelLibrary.Kinds.SmallMachine;

    /// <summary>A machine of the test's own, so a change never reaches the real condenser other tests hear.</summary>
    private static string TestModel()
    {
        string id = "editor_test_" + Guid.NewGuid().ToString("N")[..8];
        ModelLibrary.Add(SmallMachineKind, id, SmallMachineSpec.AirConditionerCondenser);
        return id;
    }

    // ── Menus ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheMenusAreMadeFromWhatIsThere()
    {
        string id = TestModel();
        var rig = new Rig(_dir, UserRole.Dev);
        rig.On("mine");
        var root = rig.Menu("menu")!;
        Assert.Equal("World editor, mine", root.Title);
        Assert.Equal(new[] { "Map", "Place", "Select", "Placed on this map, 0", "Changed on this map, 0", "Roads, paths and railways, 0", "Places and rooms", "Library", "Test tools", "Nothing to undo", "Nothing to redo" }, root.Items.Select(i => i.Label));

        var place = rig.Menu("menu", "place")!;
        Assert.Contains(place.Items, i => i.Label.StartsWith("Walls and fences, ") && i.Kind == EditorItemKind.Menu && i.Command == "place.cat:Walls and fences");
        var walls = rig.Menu("menu", "place.cat:Walls", "and", "fences")!;
        Assert.Contains(walls.Items, i => i.Label == "Concrete Wall, 2 by 0.5 by 3 high" && i.Command == "edit place concrete_wall" && i.Stay);

        rig.Run("edit", "place", "ac_condenser");
        var selected = rig.Menu("menu", "selected")!;
        Assert.Equal("Condensing unit", selected.Title);
        Assert.Contains(selected.Items, i => i.Label == "Its model: machine ac_condenser, version 0" && i.Command == "model:small_machine:ac_condenser");
        Assert.Contains(selected.Items, i => i.Kind == EditorItemKind.Input && i.Command == "/edit move ");

        var settings = rig.Menu("menu", "settings")!;
        Assert.Equal(new[] { "Name, Condensing unit", "Width, 0.95 m", "Height, 0.9 m", "Depth, 0.95 m", "Model, ac_condenser", "Volume, 1", "Range, 160 m", "Minimum distance, 1.8 m" },
                     settings.Items.Select(i => i.Label));

        var model = rig.Menu("menu", $"model:small_machine:{id}")!;
        Assert.Contains(model.Items, i => i.Label == "Compressor" && i.Command == $"model:small_machine:{id}:Compressor");
        var comp = rig.Menu("menu", $"model:small_machine:{id}:Compressor")!;
        Assert.Contains(comp.Items, i => i.Label == "Hum level, 63 dB" && i.Command == $"mfield:small_machine:{id}:Compressor.HumDb");
        var hum = rig.Menu("menu", $"mfield:small_machine:{id}:Compressor.HumDb")!;
        Assert.Equal("Compressor hum level, 63 dB, 30 to 90 dB", hum.Items[0].Label);
        Assert.Contains(hum.Items, i => i.Label == "Up 1 dB" && i.Command == $"edit model up small_machine {id} Compressor.HumDb" && i.Stay);
        Assert.Contains(hum.Items, i => i.Kind == EditorItemKind.Input && i.Command == $"/edit model set small_machine {id} Compressor.HumDb ");

        // After a change the menu last asked for is sent again, to replace itself.
        rig.Menu("menu", $"mfield:small_machine:{id}:Compressor.HumDb");
        rig.Run("edit", "model", "up", "small_machine", id, "Compressor.HumDb");
        var refreshed = rig.Replies.OfType<EditorMenu>().Last();
        Assert.True(refreshed.Refresh);
        Assert.Equal("Compressor hum level, 64 dB, 30 to 90 dB", refreshed.Items[0].Label);
    }

    [Fact]
    public void ATypedItemCarriesWhatItsDialogShows()
    {
        string id = TestModel();
        var rig = new Rig(_dir, UserRole.Dev);
        rig.On("mine");

        // A model's field: the value now, its unit, range and help, checked as the server checks it.
        var hum = rig.Menu("menu", $"mfield:small_machine:{id}:Compressor.HumDb")!;
        var typed = hum.Items.Single(i => i.Kind == EditorItemKind.Input);
        Assert.Equal("Type a value", typed.Label);
        Assert.Equal((FieldType.Number, "dB", 30.0, 90.0, "63"), (typed.ValueType, typed.Unit, typed.Min, typed.Max, typed.Value));
        Assert.Equal("compressor hum level", typed.Prompt);
        Assert.False(string.IsNullOrWhiteSpace(typed.Help));

        // A placed thing's own setting: a float's stored 0.800000011920929 is put in the box as 0.8.
        rig.Run("edit", "place", "ac_condenser");
        rig.Run("edit", "set", "Volume", "0.8");
        var volume = rig.Menu("menu", "setting:Volume")!.Items.Single(i => i.Kind == EditorItemKind.Input);
        Assert.Equal(("0.8", "/edit set Volume "), (volume.Value, volume.Command));

        // Moving takes three numbers; the step is a number in metres with the step now in the box.
        var move = rig.Menu("menu", "selected")!.Items.Single(i => i.Kind == EditorItemKind.Input && i.Command == "/edit move ");
        Assert.Equal((FieldType.Number, (byte)3, -1000.0, 1000.0), (move.ValueType, move.Count, move.Min, move.Max));
        var step = rig.Menu("menu", "nudge")!.Items.Single(i => i.Kind == EditorItemKind.Input);
        Assert.Equal(("0.5", "m", 0.01, 50.0), (step.Value, step.Unit, step.Min, step.Max));

        // Every typed item on the way says what its box is for.
        foreach (var path in new[] { "select", "place", "turn", "rows", "map" })
            Assert.All(rig.Menu("menu", path)!.Items.Where(i => i.Kind == EditorItemKind.Input), i => Assert.NotEqual("", i.Prompt));
    }

    [Fact]
    public void ATextPlayerIsSentTheSameMenuAsNumberedLines()
    {
        var menu = new EditorMenu
        {
            Title = "Nudge",
            Items = new[]
            {
                new EditorMenuItem { Label = "Step, 0.5 metres, typed", Kind = EditorItemKind.Input, Command = "/edit step " },
                new EditorMenuItem { Label = "North", Kind = EditorItemKind.Action, Command = "edit nudge north", Stay = true },
                new EditorMenuItem { Label = "Turn", Kind = EditorItemKind.Menu, Command = "turn" },
            },
        };
        Assert.Equal("Nudge:\n  1. Step, 0.5 metres, typed (type: edit step and a value)\n  2. North (type: edit nudge north)\n  3. Turn (type: edit menu turn)",
                     MudGateway.FormatEditorMenu(menu));
    }

    // ── The wire ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheEditorsMessagesComeBackExactly()
    {
        var menu = new EditorMenu
        {
            Path = "mfield:small_machine:ac_condenser:Compressor.HumDb", Title = "Compressor hum level", Refresh = true,
            Items = new[] { new EditorMenuItem { Label = "Up 1 dB", Kind = EditorItemKind.Action, Command = "edit model up small_machine ac_condenser Compressor.HumDb", Stay = true } },
        };
        var back = (EditorMenu)MemoryPackSerializer.Deserialize<IMessage>(MemoryPackSerializer.Serialize<IMessage>(menu))!;
        Assert.Equal((menu.Path, menu.Title, menu.Refresh), (back.Path, back.Title, back.Refresh));
        Assert.Equal(menu.Items[0], back.Items[0]);

        var input = new EditorMenu
        {
            Items = new[] { new EditorMenuItem { Label = "Type a value", Kind = EditorItemKind.Input, Command = "/edit set Volume ", Prompt = "volume",
                                                 Value = "0.8", ValueType = FieldType.Number, Unit = "", Min = 0, Max = 4, Help = "How loud.", Count = 1 } },
        };
        var inputBack = (EditorMenu)MemoryPackSerializer.Deserialize<IMessage>(MemoryPackSerializer.Serialize<IMessage>(input))!;
        Assert.Equal(input.Items[0], inputBack.Items[0]);

        var update = new ModelUpdate { Kind = "small_machine", Id = "ac_condenser", Version = 3, SpecJson = ModelLibrary.SpecJson(SmallMachineSpec.AirConditionerCondenser) };
        var u = (ModelUpdate)MemoryPackSerializer.Deserialize<IMessage>(MemoryPackSerializer.Serialize<IMessage>(update))!;
        Assert.Equal((update.Kind, update.Id, update.Version, update.SpecJson), (u.Kind, u.Id, u.Version, u.SpecJson));
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    // ── Rig ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Three flat maps (the tester's, other's, and an open one with no owner), the real command handler,
    /// the tester and other standing on the tester's map. Overlays are kept in maps/overlays.
    /// </summary>
    internal sealed class Rig
    {
        public readonly MapManager Maps;
        public readonly PrefabRepository Prefabs;
        public readonly CompositeService? Composites;
        public readonly GameServer Server;
        public readonly FakeUsers Users = new();
        public readonly UserSession Tester, Other;
        public readonly string MapDir, AccessPath;
        public readonly string? ModelDir;
        public readonly List<IMessage> Replies = new();
        private readonly Dictionary<string, List<IMessage>> _sentTo = new(StringComparer.OrdinalIgnoreCase);
        private readonly CommandHandler _commands;
        private readonly SessionManager _sessions = new();

        public Rig(string root, UserRole role, string? mapDir = null, bool overlays = true, bool models = false, bool composites = false)
        {
            string dir = Path.Combine(root, Guid.NewGuid().ToString("N"));
            MapDir = mapDir ?? Path.Combine(dir, "maps");
            AccessPath = Path.Combine(dir, "map_access.json");
            Directory.CreateDirectory(MapDir);
            Prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
            Maps = new MapManager(new MapRepository(MapDir), Prefabs)
            {
                Access = new MapAccessRepository(AccessPath),
                Overlays = overlays ? new MapOverlayStore(Path.Combine(MapDir, "overlays")) : null,
            };
            Maps.Initialize();
            if (mapDir == null)
            {
                Assert.True(Maps.CreateMap(MapTemplates.Flat("mine", "tester"), out string e1), e1);
                Assert.True(Maps.CreateMap(MapTemplates.Flat("theirs", "other"), out string e2), e2);
                var open = MapTemplates.Flat("open", "");
                open.IsPublic = true;
                Assert.True(Maps.CreateMap(open, out string e3), e3);
            }

            Server = new GameServer(Users);
            Server.Attach(Maps, _sessions, new OccupancyService(Maps), new HandsService(Maps));
            if (models)
            {
                ModelDir = Path.Combine(dir, "model_versions");
                Server.Models = new ModelStore(ModelDir);
            }
            Server.Sent = (to, message) =>
            {
                if (!_sentTo.TryGetValue(to.Username, out var list)) _sentTo[to.Username] = list = new List<IMessage>();
                list.Add(message);
            };
            // Composites park vehicles, as /spawn vehicle does; most tests have none.
            Composites = composites ? new CompositeService(Maps, Prefabs, new CompositeRepository(Path.Combine(dir, "composites"))) : null;
            _commands = new CommandHandler(_sessions, Maps, Server, Composites, users: Users);

            Users.Add("tester", role);
            Users.Add("other", UserRole.Player);
            string first = mapDir == null ? "mine" : Maps.LoadedMapIds.First();
            Tester = Body(1, "tester", role, first, new Vector3(5, 0.05f, 5));
            Other = Body(2, "other", UserRole.Player, first, new Vector3(5, 0.05f, 15));
        }

        private UserSession Body(int id, string name, UserRole role, string mapId, Vector3 at)
        {
            var session = new UserSession { ConnectionId = id, Username = name, Role = role, CurrentMapId = mapId, Welcomed = true };
            _sessions.AddSession(id, session);
            Stand(session, mapId, at);
            return session;
        }

        /// <summary>Puts a session's body on a map at a point, facing north.</summary>
        public void Stand(UserSession session, string mapId, Vector3 at)
        {
            if (session.Entity != Entity.Null && Maps.TryGetMap(session.CurrentMapId, out var old, out _, out _, out _) && old.IsAlive(session.Entity))
                Maps.DestroyEntity(session.CurrentMapId, session.Entity);
            Assert.True(Maps.TryGetMap(mapId, out var world, out _, out _, out _));
            var entity = world.Create(
                new PlayerComponent { ConnectionId = session.ConnectionId, Username = session.Username, Role = session.Role },
                EntityType.Player,
                new Transform { Position = at, Rotation = Quaternion.Identity },
                new Velocity(), new MaterialComponent { Material = "Generic" },
                new InventoryComponent { ItemEntityIds = new List<int>() },
                new ColliderComponent { Shape = ColliderShape.Cylinder, Size = new Vector3(PhysicsConstants.PlayerRadius * 2, PhysicsConstants.PlayerHeight, PhysicsConstants.PlayerRadius * 2), IsSolid = true });
            Maps.IndexEntity(mapId, entity);
            session.Entity = entity;
            session.CurrentMapId = mapId;
        }

        public void On(string mapId) => Stand(Tester, mapId, new Vector3(5, 0.05f, 5));

        public string Run(string command, params string[] args) => RunAs(Tester, command, args);

        public string RunAs(UserSession who, string command, params string[] args)
        {
            var said = new List<string>();
            LastMenu = null;
            _commands.HandleTextCommand(who.ConnectionId, new TextCommand { Command = command, Args = args },
                m =>
                {
                    if (m is TextEvent t) said.Add(t.Text);
                    else
                    {
                        if (who == Tester) Replies.Add(m);
                        if (m is EditorMenu { Refresh: false } menu) LastMenu = menu;
                    }
                });
            Server.DrainCommandBuffer();
            return string.Join(" | ", said);
        }

        public EditorMenu? LastMenu { get; private set; }

        /// <summary>The editor the command handler made.</summary>
        public WorldEditor Editor => _commands.Editor;

        /// <summary>/edit with these words, and the menu it answered with.</summary>
        public EditorMenu? Menu(params string[] args)
        {
            Run("edit", args);
            return LastMenu;
        }

        public List<IMessage> SentTo(string username) => _sentTo.TryGetValue(username, out var l) ? l : new List<IMessage>();

        public World World(string mapId)
        {
            Assert.True(Maps.TryGetMap(mapId, out var world, out _, out _, out _));
            return world;
        }

        public Entity Thing(int id) => Maps.AuthoredEntities(Tester.CurrentMapId)[id];

        public bool Exists(int id) => Maps.AuthoredEntities(Tester.CurrentMapId).TryGetValue(id, out var e) && World(Tester.CurrentMapId).IsAlive(e);

        public Pose PoseOf(int id)
        {
            var t = World(Tester.CurrentMapId).Get<Transform>(Thing(id));
            return new Pose(t.Position, t.Rotation, t.Scale);
        }

        public MapOverlay Overlay(string mapId) => Maps.Overlays!.Get(mapId);

        /// <summary>What the tester has selected, read back from the selected menu's summary.</summary>
        public int Selected
        {
            get
            {
                string said = Run("edit", "selected");
                int at = said.LastIndexOf("number ", StringComparison.Ordinal);
                Assert.True(at >= 0, said);
                return int.Parse(said[(at + 7)..].TrimEnd('.'));
            }
        }
    }

    internal sealed class FakeUsers : IUserRepository
    {
        private readonly Dictionary<string, UserRole> _roles = new(StringComparer.OrdinalIgnoreCase);
        public void Add(string name, UserRole role) => _roles[name] = role;
        public UserData? GetUser(string username) =>
            _roles.TryGetValue(username.Trim(), out var role) ? new UserData { Username = username.Trim().ToLowerInvariant(), Role = role } : null;
        public bool AddUser(string username, string password, UserRole role) => false;
        public bool VerifyPassword(string username, string password) => false;
        public bool SetRole(string username, UserRole role) { if (!_roles.ContainsKey(username)) return false; _roles[username] = role; return true; }
    }
}

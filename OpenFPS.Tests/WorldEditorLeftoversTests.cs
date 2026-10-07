using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Editor;
using OpenFPS.Server.Systems;
using Rig = OpenFPS.Tests.WorldEditorTests.Rig;

namespace OpenFPS.Tests;

/// <summary>
/// The world editor's leftovers from phase 2 (docs/WORLD_EDITOR.md section 13): a thing
/// made again keeps what it is doing, parts-list vehicles are library vehicles, a held weather holds its
/// lightning, a prefab's lists are editable, and a placed group moves as one.
/// </summary>
public class WorldEditorLeftoversTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "openfps-editor-left-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    // ── 1. A thing made again keeps what it is doing ────────────────────────────────────────────

    [Fact]
    public void ANewPrefabVersionKeepsADoorOpenAndLocked()
    {
        var rig = new Rig(_dir, UserRole.Dev, models: true);
        rig.On("mine");
        rig.Run("edit", "place", "door");
        int door = rig.Selected;
        Assert.Equal("Locked side, front.", rig.Run("edit", "set", "KeyedSide", "front"));
        // Out of the leaf's swing, so nobody holds it.
        rig.Stand(rig.Tester, "mine", new Vector3(-20, 0.05f, -20));
        var world = rig.World("mine");
        var doors = new DoorSystem();
        var leaf = rig.Thing(door);
        doors.Update(world, 0.05f, _ => { });
        Assert.True(DoorSystem.Set(world, leaf, true));
        for (int i = 0; i < 80; i++) doors.Update(world, 0.05f, _ => { });
        Assert.Equal(1f, world.Get<DoorComponent>(leaf).Openness);
        var open = rig.PoseOf(door);

        Assert.StartsWith("Name of the prefab door", rig.Run("edit", "model", "set", "prefab", "door", "Name", "Test", "Door"));
        var made = rig.Thing(door);
        Assert.NotEqual(leaf, made);
        var d = world.Get<DoorComponent>(made);
        Assert.Equal((1f, 1f, 1f), (d.Openness, d.Target, d.KeyedSide));
        // Still swung open where it was, and shut where its doorway is when it closes.
        Assert.True(rig.PoseOf(door).Near(open), $"{rig.PoseOf(door)} against {open}");
        Assert.True(DoorSystem.Set(world, made, false));
        for (int i = 0; i < 80; i++) doors.Update(world, 0.05f, _ => { });
        Assert.Equal(0f, world.Get<DoorComponent>(made).Openness);
        Assert.False(rig.PoseOf(door).Near(open));
        Assert.Equal(open.Position.Y, rig.PoseOf(door).Position.Y, 3);
    }

    [Fact]
    public void AMovedDoorTakesItsDoorwayWithItShutOrOpen()
    {
        var rig = new Rig(_dir, UserRole.Dev, models: true);
        rig.On("mine");
        rig.Run("edit", "place", "door");
        int door = rig.Selected;
        rig.Stand(rig.Tester, "mine", new Vector3(-20, 0.05f, -20));
        var world = rig.World("mine");
        var doors = new DoorSystem();
        void Run(float seconds) { for (float t = 0; t < seconds; t += 0.05f) doors.Update(world, 0.05f, _ => { }); }
        Run(0.1f);
        var shut = rig.PoseOf(door);

        // Moved shut, then opened: it swings in its new doorway.
        Assert.StartsWith("Moved", rig.Run("edit", "move", "2", "0", "0"));
        Assert.True(DoorSystem.Set(world, rig.Thing(door), true));
        Run(4f);
        DoorSystem.Doorway(world, rig.Thing(door), out var centre, out _);
        Assert.True(Vector3.Distance(shut.Position + new Vector3(2, 0, 0), centre) < 1e-3f, $"{centre}");

        // Moved open: the doorway goes with the leaf, the overlay keeps the doorway, and a new version
        // leaves it there, still open.
        Assert.StartsWith("Moved", rig.Run("edit", "move", "0", "3", "0"));
        var moved = shut.Position + new Vector3(2, 0, 3);
        DoorSystem.Doorway(world, rig.Thing(door), out centre, out _);
        Assert.True(Vector3.Distance(moved, centre) < 1e-3f, $"{centre}");
        Assert.True(Vector3.Distance(moved, rig.Overlay("mine").AdditionFor(door)!.Entity.Position) < 1e-3f);
        var open = rig.PoseOf(door);
        rig.Run("edit", "model", "set", "prefab", "door", "Name", "Moved", "Door");
        Assert.True(rig.PoseOf(door).Near(open));
        Assert.True(DoorSystem.Set(world, rig.Thing(door), false));
        Run(4f);
        Assert.True(Vector3.Distance(moved, rig.PoseOf(door).Position) < 1e-3f);
    }

    [Fact]
    public void ANewModelKeepsAFiresLitTimeAndAMachineSwitchedOff()
    {
        var rig = new Rig(_dir, UserRole.Dev, models: true);
        rig.On("mine");
        var world = rig.World("mine");
        rig.Run("edit", "place", "fire_pit");
        int fire = rig.Selected;
        world.Get<SoundEmitterComponent>(rig.Thing(fire)).SoundId = FireSpec.KeyFor("fire_pit", 1234.5);
        Assert.Equal("Model, campfire.", rig.Run("edit", "set", "Model", "campfire"));
        Assert.Equal("fire:campfire/lit=1234.5", world.Get<SoundEmitterComponent>(rig.Thing(fire)).SoundId);
        // A new version of its prefab: made again, still lit at that moment.
        rig.Run("edit", "model", "set", "prefab", "fire_pit", "Range", "150");
        Assert.Equal("fire:campfire/lit=1234.5", world.Get<SoundEmitterComponent>(rig.Thing(fire)).SoundId);

        rig.Stand(rig.Tester, "mine", new Vector3(-20, 0.05f, 20));
        rig.Run("edit", "place", "ac_condenser");
        int unit = rig.Selected;
        world.Get<SoundEmitterComponent>(rig.Thing(unit)).SynthRunning = false;
        Assert.Equal("Model, ac_window.", rig.Run("edit", "set", "Model", "ac_window"));
        Assert.False(world.Get<SoundEmitterComponent>(rig.Thing(unit)).SynthRunning);
    }

    // ── 2. Parts-list vehicles are library vehicles ─────────────────────────────────────────────

    [Fact]
    public void AVehicleWrittenAsAPartsListIsALibraryVehicleWithVersionsAndPins()
    {
        string id = "parts_" + Guid.NewGuid().ToString("N")[..6];
        MachineRegistry.Add(new MachineDefinition
        {
            Id = id, Name = "Test pipes", Base = "v8_muscle",
            Parts = new[]
            {
                new MachinePart
                {
                    Model = MachineModels.Chassis,
                    Settings = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase) { ["massKg"] = 1700f },
                },
            },
        });
        try
        {
            Assert.Contains(id, ModelLibrary.Ids(ModelLibrary.Kinds.Vehicle));
            Assert.True(ModelLibrary.Knows(ModelLibrary.Kinds.Vehicle, id));
            var rig = new Rig(_dir, UserRole.Dev, models: true);
            rig.On("mine");
            var kind = rig.Menu("menu", "kind:vehicle")!;
            Assert.Contains(kind.Items, i => i.Command == $"model:vehicle:{id}");
            var templates = rig.Menu("menu", "templates:vehicle")!;
            Assert.Contains(templates.Items, i => i.Command == $"/edit model new vehicle {id} ");

            Assert.StartsWith($"Chassis mass of the vehicle {id}, 2000 kg. Version 1", rig.Run("edit", "model", "set", "vehicle", id, "Chassis.MassKg", "2000"));
            var built = MachineRegistry.VehicleFor(id);
            Assert.Equal(2000f, built.MassKg);
            // Everything else is still its parts list on its base.
            Assert.Equal(MachineRegistry.Unedited(id).Engine.Name, built.Engine.Name);
            Assert.Equal("Test pipes", built.Name);
            Assert.Contains(rig.SentTo("other").OfType<ModelUpdate>(), u => u.Kind == "vehicle" && u.Id == id && u.Version == 1);

            // Version 0 is the parts list as written, and a map may pin it.
            Assert.StartsWith($"This map uses the vehicle {id} at version 0, as built", rig.Run("edit", "model", "pin", "vehicle", id, "0"));
            var pinned = rig.SentTo("other").OfType<ModelUpdate>().Last();
            Assert.Equal((id, 0), (pinned.Id, pinned.Version));
            Assert.Equal(1700f, ((VehicleSpec)ModelLibrary.FromSpecJson(ModelLibrary.Kinds.Vehicle, pinned.SpecJson)).Chassis.MassKg);
            Assert.StartsWith("Undid", rig.Run("edit", "undo"));
            Assert.StartsWith("Undid", rig.Run("edit", "undo"));
            Assert.Equal(1700f, MachineRegistry.VehicleFor(id).MassKg);
        }
        finally
        {
            ModelLibrary.Clear();
            MachineRegistry.Clear();
        }
    }

    // ── 3. A held weather holds its lightning ───────────────────────────────────────────────────

    private static int Flashes(Rig rig, string user)
        => rig.SentTo(user).OfType<WorldAudioEvent>().Count(e => e.Label.StartsWith("lightning", StringComparison.Ordinal));

    [Fact]
    public void AMapHoldingAStormHasLightningAndAMapHoldingClearHasNone()
    {
        var rig = new Rig(_dir, UserRole.Player);
        rig.On("mine");
        rig.Stand(rig.Other, "open", new Vector3(5, 0.05f, 5));
        rig.Server.WorldEnvironment.PinScenario(WeatherType.Clear);
        Assert.Equal("This map's weather: storm.", rig.Run("edit", "map", "set", "weather", "storm"));
        for (int i = 0; i < 4 * 3600; i++) rig.Server.Lightning(1f);
        Assert.True(Flashes(rig, "tester") > 0, "no lightning on the map holding a storm");
        Assert.Equal(0, Flashes(rig, "other"));

        // The other way about: the server's storm, and a map holding a clear sky.
        rig.Server.WorldEnvironment.PinScenario(WeatherType.Storm);
        Assert.Equal("This map's weather: clear.", rig.Run("edit", "map", "set", "weather", "clear"));
        int before = Flashes(rig, "tester");
        for (int i = 0; i < 4 * 3600; i++) rig.Server.Lightning(1f);
        Assert.Equal(before, Flashes(rig, "tester"));
        Assert.True(Flashes(rig, "other") > 0, "no lightning under the server's storm");
    }

    // ── 4. A prefab's lists are editable ────────────────────────────────────────────────────────

    [Fact]
    public void APrefabsListsAreAddedToSetAndTakenFromAndCheckedByThePrefabValidator()
    {
        var rig = new Rig(_dir, UserRole.Dev, models: true);
        rig.On("mine");
        var fields = rig.Editor.Catalog.Get(PrefabKind.KindId)!.Fields;
        var faces = fields.Single(n => n.Name == "MissingFaces");
        Assert.True(faces.IsValueList);
        Assert.Contains("North", faces.Field!.Choices);
        Assert.True(fields.Single(n => n.Name == "RoomMaterials").IsValueList);

        Assert.StartsWith("Added North, East to the missing faces of the prefab concrete_wall. Version 1; 2 now",
                          rig.Run("edit", "model", "add", "prefab", "concrete_wall", "MissingFaces", "north", "east"));
        Assert.Equal(new[] { "North", "East" }, rig.Maps.Prefabs["concrete_wall"].MissingFaces);
        Assert.StartsWith("Missing faces 2 of the prefab concrete_wall, West", rig.Run("edit", "model", "set", "prefab", "concrete_wall", "MissingFaces[1]", "West"));
        Assert.Equal(new[] { "North", "West" }, rig.Maps.Prefabs["concrete_wall"].MissingFaces);
        Assert.Contains("is one of", rig.Run("edit", "model", "add", "prefab", "concrete_wall", "MissingFaces", "sideways"));
        Assert.StartsWith("Took missing faces 1 out", rig.Run("edit", "model", "remove", "prefab", "concrete_wall", "MissingFaces[0]"));
        Assert.StartsWith("Took missing faces 1 out", rig.Run("edit", "model", "remove", "prefab", "concrete_wall", "MissingFaces[0]"));
        Assert.Null(rig.Maps.Prefabs["concrete_wall"].MissingFaces);

        // A room's six materials: the validator refuses five, takes six.
        Assert.Contains("needs exactly 6", rig.Run("edit", "model", "add", "prefab", "acoustic_region", "RoomMaterials", "carpet", "plaster", "plaster", "plaster", "plaster"));
        Assert.Null(rig.Maps.Prefabs["acoustic_region"].RoomMaterials);
        Assert.StartsWith("Added", rig.Run("edit", "model", "add", "prefab", "acoustic_region", "RoomMaterials", "carpet", "plaster", "plaster", "plaster", "plaster", "plaster"));
        Assert.Equal("Carpet", rig.Maps.Prefabs["acoustic_region"].RoomMaterials![0]);
        Assert.StartsWith("Room materials 2 of the prefab acoustic_region, Concrete", rig.Run("edit", "model", "set", "prefab", "acoustic_region", "RoomMaterials[1]", "concrete"));
        Assert.Contains("needs exactly 6", rig.Run("edit", "model", "remove", "prefab", "acoustic_region", "RoomMaterials[5]"));

        // The menus: each item with its value, a way to add, and a way to take one out.
        var list = rig.Menu("menu", "model:prefab:acoustic_region:RoomMaterials")!;
        Assert.Contains(list.Items, i => i.Label == "Room materials 2, Concrete" && i.Command == "mfield:prefab:acoustic_region:RoomMaterials[1]");
        var item = rig.Menu("menu", "mfield:prefab:acoustic_region:RoomMaterials[1]")!;
        Assert.Contains(item.Items, i => i.Command == "edit model set prefab acoustic_region RoomMaterials[1] Carpet");
        Assert.Contains(item.Items, i => i.Command == "edit model remove prefab acoustic_region RoomMaterials[1]");
        var facesMenu = rig.Menu("menu", "model:prefab:concrete_wall:MissingFaces")!;
        Assert.Contains(facesMenu.Items, i => i.Command == "edit model add prefab concrete_wall MissingFaces Top");
    }

    [Fact]
    public void AddingToAListOfRecordsCopiesTheLastOne()
    {
        var rig = new Rig(_dir, UserRole.Dev, models: true);
        rig.On("mine");
        string id = "fountain_" + Guid.NewGuid().ToString("N")[..6];
        rig.Run("edit", "model", "new", "water", "park_fountain", id);
        int falls = ModelLibrary.Water(id).Falls.Length;
        Assert.StartsWith($"Added a copy of falls {falls} to the falls of the water feature {id}", rig.Run("edit", "model", "add", "water", id, "Falls"));
        Assert.Equal(falls + 1, ModelLibrary.Water(id).Falls.Length);
        Assert.Equal(ModelLibrary.Water(id).Falls[falls - 1].FallMetres, ModelLibrary.Water(id).Falls[falls].FallMetres);
        Assert.StartsWith("Undid", rig.Run("edit", "undo"));
        Assert.Equal(falls, ModelLibrary.Water(id).Falls.Length);
    }

    // ── 5. A placed group moves as one ──────────────────────────────────────────────────────────

    [Fact]
    public void APlacedGroupIsHeldAndMovedAndTurnedAsOneWithOneUndo()
    {
        var rig = new Rig(_dir, UserRole.Dev, models: true);
        rig.On("mine");
        rig.Run("edit", "place", "fire_pit");
        int first = rig.Selected;
        rig.Run("edit", "nudge", "east", "3");
        rig.Run("edit", "place", "ac_condenser");
        int second = rig.Selected;
        rig.Run("edit", "select", "add", $"#{first}");
        rig.Run("edit", "select", "add", $"#{second}");
        string group = "yard_" + Guid.NewGuid().ToString("N")[..6];
        rig.Run("edit", "group", group);

        rig.Stand(rig.Tester, "mine", new Vector3(-20, 0.05f, -20));
        rig.Run("edit", "place", "group", group);
        rig.Run("edit", "select", "clear");
        int part = rig.Selected;
        Assert.Equal(2, rig.Overlay("mine").Added.Count(a => a.Placement != null));
        Assert.StartsWith($"Holding the 2 things of the group {group}", rig.Run("edit", "select", "group"));
        var placed = rig.Overlay("mine").Added.Where(a => a.Placement != null).Select(a => a.Entity.EntityId).ToList();
        var before = placed.ToDictionary(i => i, rig.PoseOf);

        Assert.StartsWith($"Moved the group {group} 2 metres north. One undo puts them back.", rig.Run("edit", "held", "move", "0", "2", "0"));
        foreach (int i in placed) Assert.Equal(before[i].Position.Z + 2f, rig.PoseOf(i).Position.Z, 3);
        Assert.StartsWith($"Undid: moved the group {group}", rig.Run("edit", "undo"));
        foreach (int i in placed) Assert.True(rig.PoseOf(i).Near(before[i]));

        // Turned about their middle: the distance between them is kept, each turned with them.
        float apart = Vector3.Distance(before[placed[0]].Position, before[placed[1]].Position);
        Assert.StartsWith($"Turned the group {group} 90 degrees clockwise", rig.Run("edit", "held", "turn", "90"));
        Assert.Equal(apart, Vector3.Distance(rig.PoseOf(placed[0]).Position, rig.PoseOf(placed[1]).Position), 3);
        foreach (int i in placed)
            Assert.Equal(90f, MathF.Round(WorldEditor.YawOf(rig.PoseOf(i).Rotation) * 180f / MathF.PI - WorldEditor.YawOf(before[i].Rotation) * 180f / MathF.PI + 360f) % 360f, 1);

        // Kept: the placing survives a deletion and its undo, and the overlay keeps where they went.
        Assert.Equal(rig.PoseOf(part).Position, rig.Overlay("mine").AdditionFor(part)!.Entity.Position);
        rig.Run("edit", "select", $"#{part}");
        rig.Run("edit", "delete");
        rig.Run("edit", "undo");
        Assert.Equal(2, rig.Overlay("mine").Added.Count(a => a.Placement != null));

        // A thing placed on its own is not a group's.
        rig.Run("edit", "place", "fire_pit");
        Assert.Contains("was not placed as part of a group", rig.Run("edit", "select", "group"));
        Assert.Equal(5, WorldEditor.EditCost(new[] { "held", "move", "0", "1", "0" }));
    }
}

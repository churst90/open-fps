using System.Numerics;
using Arch.Core;
using MemoryPack;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Editing;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Core;
using OpenFPS.Server.Editor;
using OpenFPS.Server.Systems;
using EntityData = OpenFPS.Server.Repositories.EntityData;
using Rig = OpenFPS.Tests.WorldEditorTests.Rig;

namespace OpenFPS.Tests;

/// <summary>
/// The world editor, phase 2 (docs/WORLD_EDITOR.md sections 11 and 12): every kind described, vehicles,
/// engines and prefabs as kinds, pins and per-map versions, the library, placing, groups, door sides and
/// room materials, tiles worked out again, and the map's own settings.
/// </summary>
public class WorldEditorPhase2Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "openfps-editor2-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static string TestMachine()
    {
        string id = "editor2_" + Guid.NewGuid().ToString("N")[..8];
        ModelLibrary.Add(ModelLibrary.Kinds.SmallMachine, id, SmallMachineSpec.AirConditionerCondenser);
        return id;
    }

    // ── Every kind describes itself ─────────────────────────────────────────────────────────────

    [Fact]
    public void EveryKindHasPhysicalFieldsThatMayBeChanged()
    {
        var catalog = new ModelCatalog();
        foreach (var kind in catalog.All)
        {
            int editable = 0;
            void Count(IReadOnlyList<FieldNode> nodes)
            {
                foreach (var n in nodes)
                {
                    if (n.Kind == FieldNodeKind.Scalar && !n.Field!.ReadOnly) editable++;
                    Count(n.Children);
                }
            }
            Count(kind.Fields);
            Assert.True(editable >= 3, $"the {kind.Kind} kind describes only {editable} fields");
        }
        // The kinds named in the plan, all there.
        foreach (var k in new[] { "water", "fire", "foliage", "flow", "shore", "horn", "whistle", "bell", "air", "train", "rail_vehicle", "track", "engine", "vehicle" })
            Assert.NotNull(catalog.Get(k));
        // A render trim is shown, and not offered for change.
        var headroom = ModelKinds.NodeAt(typeof(WaterFeatureSpec), "PeakHeadroomDb", out var f);
        Assert.NotNull(headroom);
        Assert.True(f!.ReadOnly);
        // A train names its horn from the library.
        var choices = ModelKinds.Describe(typeof(RailTractionSpec)).Single(n => n.Name == "HornKey").Field!.Choices;
        Assert.Contains("k5la", choices);
    }

    [Fact]
    public void AVehicleAsTheEditorShowsItBuildsTheSameVehicle()
    {
        foreach (var id in new[] { "school_bus", "v8_muscle" })
        {
            if (!VehicleProfile.Presets.ContainsKey(id)) continue;
            var v = VehicleProfile.ByName(id);
            var built = VehicleSpec.Of(id).Build(id);
            Assert.Equal((v.MassKg, v.DragArea, v.ExhaustHeight, v.TyreCount, v.Engine.Name, v.Gearbox.FinalDrive),
                         (built.MassKg, built.DragArea, built.ExhaustHeight, built.TyreCount, built.Engine.Name, built.Gearbox.FinalDrive));
        }
    }

    [Fact]
    public void AnEditedVehicleIsWhatMachineRegistryGivesAndAnEditedEngineIsInEveryVehicleOnIt()
    {
        string id = VehicleProfile.Presets.Keys.First();
        var spec = VehicleSpec.Of(id);
        try
        {
            ModelLibrary.Add(ModelLibrary.Kinds.Vehicle, id, spec with { Chassis = spec.Chassis with { MassKg = 4321f } });
            Assert.Equal(4321f, MachineRegistry.VehicleFor(id).MassKg);

            string engine = MachineRegistry.EngineKeyFor(id);
            var e = EngineProfile.Presets[engine]();
            ModelLibrary.Add(ModelLibrary.Kinds.Engine, engine, e with { CompressionRatio = 12.5f });
            Assert.Equal(12.5f, MachineRegistry.VehicleFor(id).Engine.CompressionRatio);
            Assert.Equal(12.5f, EngineProfile.ByName(engine).CompressionRatio);
        }
        finally { ModelLibrary.Clear(); }
        Assert.NotEqual(4321f, MachineRegistry.VehicleFor(id).MassKg);
    }

    [Fact]
    public void APrefabIsDescribedFromItsSchemaAndANewVersionRemakesWhatIsMadeFromIt()
    {
        var rig = new Rig(_dir, UserRole.Dev, models: true);
        rig.On("mine");
        var editor = rig.Editor;
        var prefab = editor.Catalog.Get(PrefabKind.KindId)!;
        Assert.Contains(prefab.Fields, n => n.Name == "ColliderSize" && n.Kind == FieldNodeKind.Group);
        Assert.Contains(prefab.Fields, n => n.Name == "Material" && n.Field!.Type == FieldType.Choice && n.Field.Choices.Contains("Metal"));
        Assert.True(prefab.Fields.Single(n => n.Name == "Id").Field!.ReadOnly);

        rig.Run("edit", "place", "concrete_wall");
        int wall = rig.Selected;
        var before = rig.Thing(wall);
        Assert.StartsWith("Name of the prefab concrete_wall, Test Wall. Version 1", rig.Run("edit", "model", "set", "prefab", "concrete_wall", "Name", "Test Wall"));
        Assert.Equal("Test Wall", rig.World("mine").Get<IdentityComponent>(rig.Thing(wall)).Name);
        Assert.NotEqual(before, rig.Thing(wall));
        // Not sent to clients: they hold things, not prefabs.
        Assert.DoesNotContain(rig.SentTo("other").OfType<ModelUpdate>(), u => u.Kind == PrefabKind.KindId);

        Assert.StartsWith("Undid", rig.Run("edit", "undo"));
        Assert.Equal("Concrete Wall", rig.World("mine").Get<IdentityComponent>(rig.Thing(wall)).Name);
        // The schema's range holds: a negative mass is refused.
        Assert.Contains("outside", rig.Run("edit", "model", "set", "prefab", "concrete_wall", "Mass", "-3"));
    }

    // ── Pins and per-map versions ───────────────────────────────────────────────────────────────

    [Fact]
    public void AMapPinsAVersionAndItsPlayersAreSentThatVersion()
    {
        string id = TestMachine();
        var rig = new Rig(_dir, UserRole.Dev, models: true);
        rig.On("mine");
        rig.Run("edit", "model", "set", "machine", id, "Compressor.HumDb", "66");
        // The other player is on "mine" too: pin "mine" at version 0, and they are sent version 0.
        Assert.StartsWith($"This map uses the machine {id} at version 0, as built", rig.Run("edit", "model", "pin", "machine", id, "0"));
        var sent = rig.SentTo("other").OfType<ModelUpdate>().Last();
        Assert.Equal((id, 0), (sent.Id, sent.Version));
        Assert.Equal(63f, ((SmallMachineSpec)ModelLibrary.FromSpecJson(ModelLibrary.Kinds.SmallMachine, sent.SpecJson)).Compressor!.HumDb);

        // A change now is not sent to a map that pins the model.
        int count = rig.SentTo("other").OfType<ModelUpdate>().Count();
        Assert.Contains("This map pins version 0, so it is not heard here", rig.Run("edit", "model", "up", "machine", id, "Compressor.HumDb"));
        Assert.Equal(count, rig.SentTo("other").OfType<ModelUpdate>().Count());

        // On arrival at a map: the version that map uses.
        var store = rig.Server.Models;
        Assert.Equal(0, store.UpdatesFor(rig.Overlay("mine").Pins).Single(u => u.Id == id).Version);
        Assert.Equal(2, store.UpdatesFor(rig.Overlay("open").Pins).Single(u => u.Id == id).Version);

        Assert.Contains("Version 0, as built, pinned here.", rig.Run("edit", "model", "versions", "machine", id));
        Assert.StartsWith("Undid: changed", rig.Run("edit", "undo"));
        Assert.StartsWith($"Undid: pinned the machine {id} at version 0", rig.Run("edit", "undo"));
        Assert.Empty(rig.Overlay("mine").Pins);
        Assert.Equal(1, rig.SentTo("other").OfType<ModelUpdate>().Last().Version);
    }

    [Fact]
    public void AnOwnerMayPinOnTheirMapButNotChangeTheModel()
    {
        string id = TestMachine();
        var dev = new Rig(_dir, UserRole.Dev, models: true);
        dev.On("mine");
        dev.Run("edit", "model", "set", "machine", id, "Compressor.HumDb", "66");
        var owner = new Rig(_dir, UserRole.Player, models: true);
        owner.On("mine");
        Assert.Equal(WorldEditor.ModelsRefusal, owner.Run("edit", "model", "use", "machine", id, "0"));
        Assert.Equal(WorldEditor.ModelsRefusal, owner.Run("edit", "model", "copy", "machine", id, "mine_too"));
    }

    [Fact]
    public void UseAnEarlierVersionOnEveryMapAndTheVersionsMenuOffersIt()
    {
        string id = TestMachine();
        var rig = new Rig(_dir, UserRole.Dev, models: true);
        rig.On("mine");
        rig.Run("edit", "model", "set", "machine", id, "Compressor.HumDb", "66");
        var versions = rig.Menu("menu", $"versions:small_machine:{id}")!;
        Assert.Contains(versions.Items, i => i.Label.StartsWith("Version 1, in use: Compressor.HumDb 63 dB to 66 dB"));
        var v0 = rig.Menu("menu", $"version:small_machine:{id}:0")!;
        Assert.Contains(v0.Items, i => i.Command == $"edit model use small_machine {id} 0");
        Assert.Contains(v0.Items, i => i.Command == $"edit model pin small_machine {id} 0");

        Assert.StartsWith($"The machine {id} is at version 0, as built", rig.Run("edit", "model", "use", "machine", id, "0"));
        Assert.Equal(63f, ModelLibrary.SmallMachine(id).Compressor!.HumDb);
        Assert.StartsWith("Undid", rig.Run("edit", "undo"));
        Assert.Equal(66f, ModelLibrary.SmallMachine(id).Compressor!.HumDb);
    }

    // ── Library ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void NewFromATemplateCopyRetireAndRestore()
    {
        var rig = new Rig(_dir, UserRole.Dev, models: true);
        rig.On("mine");
        string mine = "test_" + Guid.NewGuid().ToString("N")[..6];
        Assert.StartsWith($"Made the machine {mine}, made from ac_condenser, as built. Version 1.", rig.Run("edit", "model", "new", "machine", "ac_condenser", mine));
        Assert.True(ModelLibrary.Knows(ModelLibrary.Kinds.SmallMachine, mine));
        Assert.Contains(rig.SentTo("other").OfType<ModelUpdate>(), u => u.Id == mine);
        Assert.Equal($"There is already a machine called {mine}.", rig.Run("edit", "model", "new", "machine", "ac_condenser", mine));

        string copy = mine + "_b";
        Assert.StartsWith($"Made the machine {copy}, copied from {mine}", rig.Run("edit", "model", "copy", "machine", mine, copy));

        Assert.StartsWith($"Retired the machine {copy}", rig.Run("edit", "model", "retire", "machine", copy));
        Assert.True(rig.Server.Models.IsRetired(ModelLibrary.Kinds.SmallMachine, copy));
        var kindMenu = rig.Menu("menu", "kind:small_machine")!;
        Assert.DoesNotContain(kindMenu.Items, i => i.Label.StartsWith(copy + ":"));
        Assert.Contains(kindMenu.Items, i => i.Command == "retired:small_machine");
        Assert.StartsWith($"The machine {copy} is offered again", rig.Run("edit", "model", "restore", "machine", copy));

        // Undoing a model's making retires it: versions are never deleted.
        rig.Run("edit", "undo");   // the restore
        rig.Run("edit", "undo");   // the retire
        Assert.StartsWith($"Undid: made the machine {copy}", rig.Run("edit", "undo"));
        Assert.True(rig.Server.Models.IsRetired(ModelLibrary.Kinds.SmallMachine, copy));
    }

    [Fact]
    public void ReplaceOnThisMapMakesEveryThingPlayTheOtherModelAndOneUndoPutsThemBack()
    {
        var rig = new Rig(_dir, UserRole.Dev, models: true);
        rig.On("mine");
        rig.Run("edit", "place", "ac_condenser");
        int a = rig.Selected;
        rig.Run("edit", "duplicate");
        int b = rig.Selected;
        string Sound(int id) => rig.World("mine").Get<SoundEmitterComponent>(rig.Thing(id)).SoundId;
        Assert.StartsWith("machine:ac_condenser", Sound(a));

        Assert.StartsWith("Replaced the machine ac_condenser with ac_window on 2 things on this map", rig.Run("edit", "model", "replace", "machine", "ac_condenser", "with", "ac_window"));
        Assert.StartsWith("machine:ac_window", Sound(a));
        Assert.StartsWith("machine:ac_window", Sound(b));
        Assert.Equal("ac_window", rig.Overlay("mine").AdditionFor(a)!.Settings!["Model"]);

        Assert.StartsWith("Undid: replaced", rig.Run("edit", "undo"));
        Assert.StartsWith("machine:ac_condenser", Sound(a));
        Assert.StartsWith("machine:ac_condenser", Sound(b));
    }

    [Fact]
    public void AThingsModelIsASettingAndARetiredModelIsNotOffered()
    {
        var rig = new Rig(_dir, UserRole.Dev, models: true);
        rig.On("mine");
        rig.Run("edit", "place", "ac_condenser");
        int id = rig.Selected;
        Assert.Equal("There is no machine called nothing_here.", rig.Run("edit", "set", "Model", "nothing_here"));
        Assert.Equal("Model, ac_window.", rig.Run("edit", "set", "Model", "ac_window"));
        Assert.StartsWith("machine:ac_window", rig.World("mine").Get<SoundEmitterComponent>(rig.Thing(id)).SoundId);
        var menu = rig.Menu("menu", "setting:Model")!;
        Assert.Contains(menu.Items, i => i.Command == "edit set Model ac_condenser");
        rig.Run("edit", "model", "retire", "machine", "ac_condenser");
        try
        {
            Assert.Contains("retired", rig.Run("edit", "set", "Model", "ac_condenser"));
        }
        finally { rig.Run("edit", "model", "restore", "machine", "ac_condenser"); }
    }

    [Fact]
    public void TakingAnItemOutOfAListIsANewVersion()
    {
        var rig = new Rig(_dir, UserRole.Dev, models: true);
        rig.On("mine");
        string id = "fountain_" + Guid.NewGuid().ToString("N")[..6];
        rig.Run("edit", "model", "new", "water", "park_fountain", id);
        int falls = ModelLibrary.Water(id).Falls.Length;
        Assert.StartsWith($"Took falls 1 out of the water feature {id}. Version 2", rig.Run("edit", "model", "remove", "water", id, "Falls[0]"));
        Assert.Equal(falls - 1, ModelLibrary.Water(id).Falls.Length);
        Assert.StartsWith("Undid", rig.Run("edit", "undo"));
        Assert.Equal(falls, ModelLibrary.Water(id).Falls.Length);
    }

    // ── Place ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SearchFindsPrefabsByEveryWord()
    {
        var rig = new Rig(_dir, UserRole.Dev);
        rig.On("mine");
        var found = rig.Menu("find", "concrete", "wall")!;
        Assert.Contains(found.Items, i => i.Command == "edit place concrete_wall");
        Assert.DoesNotContain(found.Items, i => i.Command == "edit place concrete_floor");
        rig.Run("edit", "place", "mode", "preview");
        found = rig.Menu("find", "concrete", "wall")!;
        Assert.Contains(found.Items, i => i.Command == "edit preview concrete_wall");
        rig.Run("edit", "place", "mode", "cursor");
        found = rig.Menu("find", "concrete", "wall")!;
        Assert.Contains(found.Items, i => i.Command == "edit place concrete_wall at cursor");
    }

    [Fact]
    public void APreviewIsSentToYouAloneAndChangesNothing()
    {
        var rig = new Rig(_dir, UserRole.Dev);
        rig.On("mine");
        int things = rig.Maps.AuthoredEntities("mine").Count;
        Assert.StartsWith("Playing Condensing unit two metres in front of you", rig.Run("edit", "preview", "ac_condenser"));
        var def = rig.SentTo("tester").OfType<EntityDefinition>().Single();
        Assert.True(def.EntityId >= WorldEditor.FirstPreviewId);
        Assert.StartsWith("machine:ac_condenser", def.SoundEmitter.SoundId);
        Assert.False(def.Collider.IsSolid);
        Assert.Empty(rig.SentTo("other").OfType<EntityDefinition>());
        Assert.Equal(things, rig.Maps.AuthoredEntities("mine").Count);
        Assert.Contains("makes no sound", rig.Run("edit", "preview", "concrete_wall"));
    }

    [Fact]
    public void ARowIsOneUndoAndAgainPlacesTheLastOneWhereYouStand()
    {
        var rig = new Rig(_dir, UserRole.Dev);
        rig.On("mine");
        rig.Run("edit", "place", "fire_pit");
        int added = rig.Overlay("mine").Added.Count;
        Assert.StartsWith("A row of 3 ", rig.Run("edit", "row", "3", "2"));
        Assert.Equal(added + 3, rig.Overlay("mine").Added.Count);
        Assert.StartsWith("Undid: made a row of 3", rig.Run("edit", "undo"));
        Assert.Equal(added, rig.Overlay("mine").Added.Count);

        Assert.StartsWith("Placed", rig.Run("edit", "again"));
        Assert.Equal(added + 1, rig.Overlay("mine").Added.Count);
    }

    [Fact]
    public void PlacingAtTheBuildCursor()
    {
        var rig = new Rig(_dir, UserRole.Dev);
        rig.On("mine");
        Assert.Contains("no build cursor", rig.Run("edit", "place", "fire_pit", "at", "cursor"));
        rig.Tester.Build.SetOrigin(new Vector3(5, 0.05f, 5), 0f);
        rig.Tester.Build.Cursor = new Vector3(0, 0, 4);
        Assert.StartsWith("Placed", rig.Run("edit", "place", "fire_pit", "at", "cursor"));
        var pose = rig.PoseOf(rig.Selected);
        Assert.Equal(9f, pose.Position.Z, 3);
    }

    // ── Groups ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void HeldThingsBecomeAGroupThatPlacesAsThoseThings()
    {
        var rig = new Rig(_dir, UserRole.Dev, models: true);
        rig.On("mine");
        rig.Run("edit", "place", "fire_pit");
        int first = rig.Selected;
        rig.Run("edit", "nudge", "east", "3");
        rig.Run("edit", "place", "ac_condenser");
        int second = rig.Selected;
        Assert.StartsWith("Holding", rig.Run("edit", "select", "add", $"#{first}"));
        Assert.Contains("2 things held", rig.Run("edit", "select", "add", $"#{second}"));
        string group = "yard_" + Guid.NewGuid().ToString("N")[..6];
        Assert.StartsWith($"Made the group {group} from 2 things", rig.Run("edit", "group", group));
        var spec = rig.Editor.GroupOf(group)!;
        Assert.Equal(2, spec.Parts.Length);
        Assert.Equal(new[] { "ac_condenser", "fire_pit" }, spec.Parts.Select(p => p.PrefabId).OrderBy(p => p));

        rig.Stand(rig.Tester, "mine", new Vector3(-20, 0.05f, -20));
        int added = rig.Overlay("mine").Added.Count;
        Assert.StartsWith($"Placed the group {group}, 2 things", rig.Run("edit", "place", "group", group));
        Assert.Equal(added + 2, rig.Overlay("mine").Added.Count);
        var place = rig.Menu("menu", "place.cat:Groups")!;
        Assert.Contains(place.Items, i => i.Command == $"edit place group {group}");
        Assert.StartsWith($"Undid: placed the group {group}", rig.Run("edit", "undo"));
        Assert.Equal(added, rig.Overlay("mine").Added.Count);

        // A group's parts are its model's: a part can be moved or taken out, a new version.
        Assert.StartsWith("Parts 1 forward", rig.Run("edit", "model", "set", "group", group, "Parts[0].ForwardMetres", "2"));
        Assert.StartsWith("Took parts 2", rig.Run("edit", "model", "remove", "group", group, "Parts[1]"));
        Assert.Single(rig.Editor.GroupOf(group)!.Parts);
    }

    // ── Doors, rooms, places ────────────────────────────────────────────────────────────────────

    [Fact]
    public void ADoorsSidesAndARoomsMaterialsAndNameAreSettings()
    {
        var rig = new Rig(_dir, UserRole.Dev);
        rig.On("mine");
        rig.Run("edit", "place", "door");
        int door = rig.Selected;
        Assert.Equal("Locked side, front.", rig.Run("edit", "set", "KeyedSide", "front"));
        Assert.Equal(1f, rig.World("mine").Get<DoorComponent>(rig.Thing(door)).KeyedSide);
        Assert.Equal("Push side, back.", rig.Run("edit", "set", "PushSide", "back"));
        Assert.Equal(-1f, rig.World("mine").Get<DoorComponent>(rig.Thing(door)).PushSide);
        var menu = rig.Menu("menu", "setting:KeyedSide")!;
        Assert.Contains(menu.Items, i => i.Command == "edit set KeyedSide neither");

        rig.Stand(rig.Tester, "mine", new Vector3(-30, 0.05f, 30));
        rig.Run("edit", "place", "acoustic_region");
        int room = rig.Selected;
        Assert.Equal("Floor, Carpet.", rig.Run("edit", "set", "Floor", "carpet"));
        Assert.True(AcousticRegistry.TryGetResonanceIndex("Carpet", out int carpet));
        Assert.Equal(carpet, rig.World("mine").Get<RegionComponent>(rig.Thing(room)).Materials[0]);
        Assert.Equal("Name, The Lounge.", rig.Run("edit", "set", "Name", "The", "Lounge"));
        Assert.Equal("The Lounge", rig.World("mine").Get<RegionComponent>(rig.Thing(room)).FriendlyName);
        var places = rig.Menu("menu", "places")!;
        Assert.Contains(places.Items, i => i.Command == $"edit select #{room}");
        Assert.StartsWith("Undid", rig.Run("edit", "undo"));
        Assert.StartsWith("Undid", rig.Run("edit", "undo"));
        Assert.NotEqual(carpet, rig.World("mine").Get<RegionComponent>(rig.Thing(room)).Materials[0]);
    }

    [Fact]
    public void AThingMovedIntoAnotherTileIsInThatTile()
    {
        var rig = new Rig(_dir, UserRole.Dev);
        var map = MapTemplates.Flat("tiled", "tester");
        map.TileMetres = 250f;
        map.MinBound = new Vector3(-600, 0, -600);
        map.MaxBound = new Vector3(600, 40, 600);
        map.Entities[0].Scale = new Vector3(120, 1, 120);
        map.Entities.Add(new EntityData { EntityId = 7, PrefabId = "concrete_wall", Position = new Vector3(10, 1.55f, 10) });
        Assert.True(rig.Maps.CreateMap(map, out string error), error);
        rig.Stand(rig.Tester, "tiled", new Vector3(0, 0.05f, 0));
        Assert.True(rig.Maps.TryGetTiles("tiled", out var tiles));
        Assert.StartsWith("Selected", rig.Run("edit", "select", "#7"));
        Assert.StartsWith("Moved", rig.Run("edit", "move", "300", "0", "0"));
        var e = rig.Maps.AuthoredEntities("tiled")[7];
        Assert.True(tiles.TryGet(e.Id, out var membership));
        Assert.Contains(TileKey.Of(new Vector3(310, 0, 10), 250f), membership.Tiles);
        Assert.DoesNotContain(TileKey.Of(new Vector3(10, 0, 10), 250f), membership.Tiles);
    }

    // ── Map settings ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AMapHoldsItsWeatherAndHourAndItsBeaconRulesGoToItsPlayersAtOnce()
    {
        var rig = new Rig(_dir, UserRole.Player);
        rig.On("mine");
        Assert.Equal("This map's weather: rain.", rig.Run("edit", "map", "set", "weather", "rain"));
        Assert.Equal("This map's time of day: 14:30.", rig.Run("edit", "map", "set", "time", "14:30"));
        Assert.True(rig.Maps.TryGetMapData("mine", out var data));
        var env = new WorldEnvironmentSystem();
        var state = env.GetStateForMap(MapAtmosphere.Of(data));
        Assert.Equal(14.5f, state.GameTime, 3);
        Assert.Equal(0.6f, state.PrecipitationIntensity, 3);
        Assert.Equal("rain", rig.Overlay("mine").Settings!["Weather"]);

        Assert.Equal("This map's door beacons: never.", rig.Run("edit", "map", "set", "beacon", "door", "never"));
        var update = rig.SentTo("other").OfType<MapSettingsUpdate>().Last();
        Assert.Equal(("mine", "door=forbidden"), (update.MapId, update.BeaconPolicy.Single()));

        Assert.StartsWith("Undid: set the map's door beacons", rig.Run("edit", "undo"));
        Assert.Empty(rig.SentTo("other").OfType<MapSettingsUpdate>().Last().BeaconPolicy);
        Assert.Contains("The weather is one of", rig.Run("edit", "map", "set", "weather", "fog"));
        Assert.Contains("The time is server", rig.Run("edit", "map", "set", "time", "25"));

        // Kept: a new store over the same overlay folder lays them on the map as it loads.
        var copy = MapTemplates.Flat("mine", "tester");
        new MapOverlayStore(Path.Combine(rig.MapDir, "overlays")).ApplyBefore(copy);
        Assert.Equal(("rain", 14.5f), (copy.HeldWeather, copy.HeldHour));
    }

    [Fact]
    public void TheNaturalGroundIsLaidAgainAsTheChosenPrefab()
    {
        var rig = new Rig(_dir, UserRole.Dev);
        var bare = MapTemplates.Flat("bare", "tester");
        bare.Entities.Clear();
        Assert.True(rig.Maps.CreateMap(bare, out string error), error);
        rig.Stand(rig.Tester, "bare", new Vector3(0, 0.05f, 0));
        Assert.Equal("This map's natural ground: Grass Area.", rig.Run("edit", "map", "set", "ground", "grass"));
        bool grass = false;
        rig.World("bare").Query(new QueryDescription().WithAll<IdentityComponent>(), (ref IdentityComponent i) => { if (i.Name == "Ground" && i.PrefabId == "grass_floor") grass = true; });
        Assert.True(grass);
        Assert.Contains("lays its own ground", new Rig(_dir, UserRole.Dev).Run("edit", "map", "set", "ground", "gravel"));
    }

    [Fact]
    public void TheMapSettingsMessageComesBackExactly()
    {
        var m = new MapSettingsUpdate { MapId = "city", BeaconPolicy = new[] { "door=forbidden", "item=default_off" } };
        var back = (MapSettingsUpdate)MemoryPackSerializer.Deserialize<IMessage>(MemoryPackSerializer.Serialize<IMessage>(m))!;
        Assert.Equal(m.MapId, back.MapId);
        Assert.Equal(m.BeaconPolicy, back.BeaconPolicy);
    }
}

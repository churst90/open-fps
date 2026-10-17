using System.Text.Json;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using OpenFPS.Server.Systems;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// The one table of model kinds (docs/SPECIAL_CASES_AUDIT.md, P2): every sound id a shipped map or
/// prefab carries resolves in it, and its bodies give the answers the prefix lists they replaced gave.
/// </summary>
public class ModelKindTableTests
{
    private readonly ITestOutputHelper _o;
    public ModelKindTableTests(ITestOutputHelper o) => _o = o;

    /// <summary>A synthesised sound id and the entity type it is on, from every shipped map once spawned
    /// (traffic, trains, crossings) and every prefab.</summary>
    private static readonly Lazy<List<(string Where, string SoundId, EntityType Type, bool IsSynth)>> Shipped = new(Collect);

    private static List<(string, string, EntityType, bool)> Collect()
    {
        var found = new List<(string, string, EntityType, bool)>();
        string prefabDir = Path.Combine(AppContext.BaseDirectory, "prefabs");
        foreach (var file in Directory.EnumerateFiles(prefabDir, "*.json"))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) continue;
            if (!root.TryGetProperty("SoundId", out var sid) || sid.ValueKind != JsonValueKind.String) continue;
            bool synth = root.TryGetProperty("IsSynth", out var s) && s.ValueKind == JsonValueKind.True;
            found.Add(("prefab " + Path.GetFileName(file), sid.GetString()!, EntityType.StaticObject, synth));
        }

        // The shipped maps, and each real place in a folder of its own (RealPlaceMapTests says why).
        var dirs = new List<string> { Path.Combine(AppContext.BaseDirectory, "maps") };
        var temp = new List<string>();
        foreach (var place in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "places"), "*.json"))
        {
            string id = Path.GetFileNameWithoutExtension(place);
            string dir = Path.Combine(Path.GetTempPath(), "openfps-kinds-" + Guid.NewGuid().ToString("N"));
            string places = Path.Combine(dir, "maps", "places");
            Directory.CreateDirectory(places);
            File.Copy(place, Path.Combine(places, id + ".json"));
            RealPlaceMapTests.CopyGround(id, places);
            temp.Add(dir);
            dirs.Add(Path.Combine(dir, "maps"));
        }
        try
        {
            AcousticRegistry.Initialize();
            foreach (var dir in dirs)
            {
                var maps = new MapManager(new MapRepository(dir), new PrefabRepository(prefabDir));
                maps.Initialize();
                new VehicleSystem().Spawn(maps);
                var rail = new RailSystem();
                rail.Spawn(maps);
                new CrossingSystem(rail).Spawn(maps);
                foreach (var (mapId, entry) in maps.GetAllMaps())
                {
                    entry.world.Query(new QueryDescription().WithAll<SoundEmitterComponent, EntityType>(),
                        (ref SoundEmitterComponent em, ref EntityType type) =>
                        {
                            if (!string.IsNullOrEmpty(em.SoundId)) found.Add(("map " + mapId, em.SoundId, type, em.IsSynth));
                        });
                }
            }
        }
        finally
        {
            foreach (var d in temp)
                try { Directory.Delete(d, true); } catch (IOException) { }
        }
        return found;
    }

    /// <summary>
    /// A synthesised sound on a shipped map or prefab that names no kind, or a model its kind does not
    /// have, was silent before the table: a nameless synth, placed and rendered by nothing.
    /// </summary>
    [Fact]
    public void EverySynthesisedSoundOnTheShippedMapsAndPrefabsResolves()
    {
        var synth = Shipped.Value.Where(s => s.IsSynth).ToList();
        var missing = new List<string>();
        foreach (var group in synth.GroupBy(s => s.SoundId, StringComparer.OrdinalIgnoreCase))
            if (!ModelKindTable.Resolves(group.Key, out string why))
                missing.Add($"{group.Key} ({why}), {group.Count()} on {string.Join(", ", group.Select(g => g.Where).Distinct().Take(3))}");
        foreach (var kind in synth.GroupBy(s => ModelKindTable.KindOf(s.SoundId)?.Prefix ?? "(none)").OrderBy(g => g.Key))
            _o.WriteLine($"{kind.Key}: {kind.Count()} placed, {kind.Select(s => s.SoundId).Distinct(StringComparer.OrdinalIgnoreCase).Count()} ids");
        foreach (var m in missing) _o.WriteLine("missing: " + m);
        Assert.True(synth.Count > 1000, $"only {synth.Count} synthesised sounds were found");
        Assert.Empty(missing);
    }

    /// <summary>A sound id with a model's prefix is synthesised everywhere it is shipped, so the bodies read
    /// from the id alone agree with the old lists, which asked IsSynth for some kinds and not others.</summary>
    [Fact]
    public void NoShippedRecordingWearsAModelsPrefix()
    {
        var odd = Shipped.Value.Where(s => !s.IsSynth && ModelKindTable.KindOf(s.SoundId) != null)
                               .Select(s => $"{s.SoundId} on {s.Where}").Distinct().ToList();
        Assert.Empty(odd);
    }

    /// <summary>The footstep test it replaced (OtherBodies before 2026-10-11), kept as the record.</summary>
    private static bool HadLegs(string sid, bool isSynth)
    {
        if (sid.StartsWith("engine:", StringComparison.OrdinalIgnoreCase)) return false;
        return !(isSynth
                 && (sid.StartsWith("aircraft:", StringComparison.OrdinalIgnoreCase)
                     || sid.StartsWith("rail:", StringComparison.OrdinalIgnoreCase)
                     || (sid.StartsWith("machine:", StringComparison.OrdinalIgnoreCase)
                         && !sid.Equals("machine:mower_push", StringComparison.OrdinalIgnoreCase))));
    }

    /// <summary>The scope's test it replaced, kept as the record: 0 vehicle, 1 aircraft, 2 train, 3 person, -1 none.</summary>
    private static int OldSight(string sid, EntityType type)
    {
        if (sid.StartsWith("engine:", StringComparison.OrdinalIgnoreCase)) return 0;
        if (sid.StartsWith("aircraft:", StringComparison.OrdinalIgnoreCase)) return 1;
        if (sid.StartsWith("rail:", StringComparison.OrdinalIgnoreCase)) return 2;
        if (type == EntityType.NPC && !sid.StartsWith("machine:", StringComparison.OrdinalIgnoreCase)) return 3;
        return -1;
    }

    private static int NewSight(string sid, EntityType type) => ModelKindTable.BodyOf(sid) switch
    {
        ModelBody.Rolls => 0,
        ModelBody.Flies => 1,
        ModelBody.Rails => 2,
        null when type == EntityType.NPC => 3,
        _ => -1,
    };

    /// <summary>
    /// On every body the shipped maps put down, the table's body gives the footsteps and the scope's
    /// answer the two prefix lists gave, the push mower included: footsteps, and not a person in the scope.
    /// </summary>
    [Fact]
    public void TheBodiesAgreeWithTheListsTheyReplacedOnEveryShippedBody()
    {
        var bodies = Shipped.Value.Where(s => s.Type is EntityType.NPC or EntityType.Player).ToList();
        Assert.Contains(bodies, b => b.SoundId.Equals("machine:mower_push", StringComparison.OrdinalIgnoreCase));
        foreach (var b in bodies)
        {
            bool legs = ModelKindTable.BodyOf(b.SoundId) is not { } body || body == ModelBody.Walks;
            Assert.True(HadLegs(b.SoundId, b.IsSynth) == legs, $"{b.SoundId} on {b.Where}: legs {legs}, were {!legs}");
            Assert.True(OldSight(b.SoundId, b.Type) == NewSight(b.SoundId, b.Type), $"{b.SoundId} on {b.Where}: the scope's answer changed");
        }
    }

    [Theory]
    [InlineData("engine:school_bus", ModelSynth.Engine, ModelBody.Rolls)]
    [InlineData("ENGINE:school_bus", ModelSynth.Engine, ModelBody.Rolls)]
    [InlineData("aircraft:piston_single", ModelSynth.Aircraft, ModelBody.Flies)]
    [InlineData("rail:light_rail/a/0", ModelSynth.Rail, ModelBody.Rails)]
    [InlineData("machine:ac_condenser", ModelSynth.Machine, ModelBody.Fixed)]
    [InlineData("machine:mower_riding", ModelSynth.Machine, ModelBody.Fixed)]
    [InlineData("machine:mower_push", ModelSynth.Machine, ModelBody.Walks)]
    [InlineData("wood:park_tree@20x30", ModelSynth.Wood, ModelBody.Fixed)]
    [InlineData("horn:electric:disc_pair:hold", ModelSynth.Horn, ModelBody.Fixed)]
    public void AKindIsReadFromItsPrefix(string soundId, ModelSynth synth, ModelBody body)
    {
        Assert.Equal(synth, ModelKindTable.KindOf(soundId)!.Synth);
        Assert.Equal(body, ModelKindTable.BodyOf(soundId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("FOOTSTEPS/concrete")]
    [InlineData("ENGINE/v8")]
    [InlineData("nonsense:thing")]
    public void ARecordingOrAnUnknownPrefixIsNoModel(string? soundId)
    {
        Assert.Null(ModelKindTable.KindOf(soundId));
        Assert.Null(ModelKindTable.BodyOf(soundId));
        Assert.False(ModelKindTable.Resolves(soundId, out _));
    }

    /// <summary>A model its kind does not have is not placed, and is logged once by id; it used to be
    /// swallowed and dropped from the ranking and the submit alike.</summary>
    [Fact]
    public void AMissingModelIsLoggedOnceAndNotPlaced()
    {
        const string id = "machine:no_such_machine_for_the_table_test";
        Assert.False(ModelKindTable.TryLevel(id, out _, out _));
        Assert.False(ModelKindTable.TryLevel(id, out _, out _));
        Assert.True(ModelKindTable.Failures.ContainsKey(id));
        Assert.False(ModelKindTable.Resolves(id, out string why));
        Assert.NotEmpty(why);
    }

    /// <summary>A water tap past its feature's taps, or a train source past its layout, names no source.</summary>
    [Fact]
    public void AnIndexPastTheLayoutDeclaresNoLevel()
    {
        Assert.True(ModelKindTable.TryLevel("rail:light_rail/x/0", out float level, out _));
        Assert.True(level > 0f);
        Assert.False(ModelKindTable.TryLevel("rail:light_rail/x/9999", out _, out _));
        Assert.False(ModelKindTable.TryLevel("water:park_fountain/elm_park/99", out _, out _));
        Assert.True(ModelKindTable.TryLevel("water:park_fountain/elm_park/0", out _, out _));
    }

    /// <summary>The editor's question goes to the table, with the kinds it answered before.</summary>
    [Theory]
    [InlineData("engine:school_bus", ModelLibrary.Kinds.Vehicle, "school_bus")]
    [InlineData("water:park_fountain/elm_park/0", ModelLibrary.Kinds.Water, "park_fountain")]
    [InlineData("stove:hob4", ModelLibrary.Kinds.GasHob, "hob4")]
    public void TheEditorsModelOfASoundReadsTheTable(string soundId, string kind, string id)
    {
        Assert.True(OpenFPS.Common.Editing.ModelKinds.TryModelOfSound(soundId, out var k, out var i));
        Assert.Equal(kind, k);
        Assert.Equal(id, i);
    }

    /// <summary>The server writes its ids through the table, and they read back as the kind they were written as.</summary>
    [Fact]
    public void AnIdWrittenThroughTheTableReadsBackAsItsKind()
    {
        foreach (var kind in ModelKindTable.All)
            Assert.Same(kind, ModelKindTable.KindOf(ModelKindTable.IdOf(kind.Synth, "x")));
        Assert.Equal("bell:crossing_gong", ModelKindTable.IdOf(ModelSynth.Bell, "crossing_gong"));
        Assert.Equal("rail:light_rail/t7/3", ModelKindTable.RailSource("light_rail", "t7", 3));
        Assert.True(ModelKindTable.TryRailSource(ModelKindTable.RailSource("light_rail", "t7", 3), out var preset, out var train, out int index));
        Assert.Equal(("light_rail", "t7", 3), (preset, train, index));
    }

    /// <summary>Every shipped horn, aeroplane and air-braked vehicle's door declares what its voice needs:
    /// none falls back (S27).</summary>
    [Fact]
    public void EveryShippedHornAircraftAndCabinDoorIsDeclared()
    {
        var failed = new List<string>();
        foreach (var name in VehicleProfile.Presets.Keys)
        {
            var v = VehicleProfile.ByName(name);
            string horn = VehicleProfile.HornFor(v);
            Honk.LevelDb(horn, ModelLibrary.Presets);
            if (ModelKindTable.Failures.TryGetValue(Honk.Prefix + horn, out var why)) failed.Add($"{name}: horn {horn} {why}");
            if (string.IsNullOrEmpty(v.AirSystem) || !v.DoorChime) continue;
            OpenFPS.Client.AudioEngine.Core.Engine.CabinPaths.For(v, ModelLibrary.Presets);
            if (ModelKindTable.Failures.TryGetValue(ModelLibrary.Kinds.Air + ":" + v.AirSystem, out why)) failed.Add($"{name}: door {why}");
        }
        foreach (var name in AircraftProfile.Presets.Keys)
        {
            string id = ModelKindTable.IdOf(ModelSynth.Aircraft, name);
            Assert.True(ModelKindTable.TryLevel(id, out _, out _), id);
            if (ModelKindTable.Failures.TryGetValue(id, out var why)) failed.Add($"{id}: {why}");
        }
        Assert.Empty(failed);
    }

    /// <summary>A horn with no model keeps its 110 dB but says so, once, by its id.</summary>
    [Fact]
    public void AHornWithNoModelIsLoggedOnce()
    {
        Assert.Equal(110f, Honk.LevelDb("klaxon:no_such_horn_for_the_table_test", ModelLibrary.Presets));
        Assert.Equal(110f, Honk.LevelDb("air:no_such_horn_for_the_table_test", ModelLibrary.Presets));
        Assert.True(ModelKindTable.Failures.ContainsKey("horn:klaxon:no_such_horn_for_the_table_test"));
        Assert.True(ModelKindTable.Failures.ContainsKey("horn:air:no_such_horn_for_the_table_test"));
    }

    /// <summary>
    /// The exhaust a vehicle's voice comes out of follows the registry (S26): a preset asked for before it
    /// is known is no longer held at the origin once it is.
    /// </summary>
    [Fact]
    public void AnExhaustAskedForBeforeItsVehicleIsKnownFollowsItOnceItIs()
    {
        string id = "s26_" + Guid.NewGuid().ToString("N")[..6];
        var def = new OpenFPS.Common.Networking.EntityDefinition { SoundEmitter = new SoundEmitterComponent { IsSynth = true, SoundId = ModelKindTable.IdOf(ModelSynth.Engine, id) } };
        Assert.Equal(System.Numerics.Vector3.Zero, EntityEmission.LocalOffset(def));
        Assert.True(ModelKindTable.Failures.ContainsKey(def.SoundEmitter.SoundId));
        MachineRegistry.Add(new MachineDefinition { Id = id, Name = "S26", Base = "v8_muscle", Parts = Array.Empty<MachinePart>() });
        var offset = EntityEmission.LocalOffset(def);
        Assert.NotEqual(System.Numerics.Vector3.Zero, offset);
        Assert.Equal(MachineRegistry.VehicleFor(id).ExhaustOffset, offset);
    }
}

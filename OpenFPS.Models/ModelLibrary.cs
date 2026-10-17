using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenFPS.Common;

/// <summary>
/// The models a map can name (trains, track, horns, bells, machines, water, engines, vehicles...), as
/// data that can be written, shared and overridden without recompiling. The specs are records of
/// scalars, so the serializer is the translation and the round trip is its test.
///
/// One directory, one file per model, each saying what kind of thing it is:
///
/// <code>
/// { "kind": "horn", "id": "k3la", "spec": { "Name": "...", "Bells": [ ... ] } }
/// </code>
///
/// An authored model of the same name overrides the built-in. Never put an exported copy of the library
/// in the load directory: it freezes every model at that day's numbers (<see cref="Export"/>; the same
/// warning is on MachineRegistry).
/// </summary>
public static class ModelLibrary
{
    /// <summary>The kinds of model this library holds; strings, as <see cref="MachineModels"/> uses,
    /// because the list grows.</summary>
    public static class Kinds
    {
        public const string Train = "train";
        public const string RailVehicle = "rail_vehicle";
        public const string Track = "track";
        public const string Horn = "horn";
        public const string Whistle = "whistle";
        public const string Bell = "bell";
        public const string Air = "air";

        /// <summary>A machine that stands still and runs: a mower, a condenser unit, a generator.</summary>
        public const string SmallMachine = "small_machine";

        /// <summary>Water falling into water: a fountain, a cascade, a weir.</summary>
        public const string Water = "water";
        /// <summary>A wood fire.</summary>
        public const string Fire = "fire";
        /// <summary>A tree or a hedge with the wind in it.</summary>
        public const string Foliage = "foliage";
        /// <summary>Water that runs: a creek, a gutter, a drain, a downpipe, an overflow.</summary>
        public const string Flow = "flow";
        /// <summary>Waves at an edge: a beach, a rocky shore, a harbour wall, a river bank, a boat's side.</summary>
        public const string Shore = "shore";
        /// <summary>A gas hob: its burners, spark module and flame safety (docs/GAS_HOB.md).</summary>
        public const string GasHob = "gas_hob";

        /// <summary>A road vehicle's engine: EngineProfile. Every vehicle built on it has the change.</summary>
        public const string Engine = "engine";
        /// <summary>A road vehicle, as the world editor edits one (VehicleSpec): MachineRegistry's
        /// vehicles, with the engine, chassis, outlets, tyres, body and gearbox as data.</summary>
        public const string Vehicle = "vehicle";
        /// <summary>A loudspeaker that plays a recording into the world: a paging horn, a megaphone.</summary>
        public const string Loudspeaker = "loudspeaker";
    }

    internal sealed class ModelFile
    {
        public string? Kind { get; set; }
        public string? Id { get; set; }
        public JsonElement Spec { get; set; }
    }

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        // An engine says "derived" with NaN (RevolutionsBeforeFiring); JSON has no NaN of its own.
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        // A Vector3 keeps its numbers in fields, which the serializer does not write: an engine's
        // tailpipe exits came back as the origin.
        Converters = { new OpenFPS.Common.Networking.Vector3Converter() },
    };

    // ── The built-in library ────────────────────────────────────────────────────────────────────

    /// <summary>Every model that ships in C#, by kind. Rail vehicles are named one by one, so a consist
    /// can be "a Genesis and six of my coaches".</summary>
    internal static readonly Dictionary<string, Dictionary<string, Func<object>>> BuiltIn = new(StringComparer.OrdinalIgnoreCase)
    {
        [Kinds.Train] = TrainProfile.Presets.ToDictionary(p => p.Key, p => (Func<object>)(() => p.Value()), StringComparer.OrdinalIgnoreCase),
        [Kinds.Horn] = ChimeHornSpec.Presets.ToDictionary(p => p.Key, p => (Func<object>)(() => p.Value()), StringComparer.OrdinalIgnoreCase),
        [Kinds.Whistle] = WhistleSpec.Presets.ToDictionary(p => p.Key, p => (Func<object>)(() => p.Value()), StringComparer.OrdinalIgnoreCase),
        [Kinds.Bell] = StruckBellSpec.Presets.ToDictionary(p => p.Key, p => (Func<object>)(() => p.Value()), StringComparer.OrdinalIgnoreCase),
        [Kinds.Air] = AirSystemSpec.Presets.ToDictionary(p => p.Key, p => (Func<object>)(() => p.Value()), StringComparer.OrdinalIgnoreCase),
        [Kinds.SmallMachine] = SmallMachineSpec.Presets.ToDictionary(p => p.Key, p => (Func<object>)(() => p.Value()), StringComparer.OrdinalIgnoreCase),
        [Kinds.Water] = WaterFeatureSpec.Presets.ToDictionary(p => p.Key, p => (Func<object>)(() => p.Value()), StringComparer.OrdinalIgnoreCase),
        [Kinds.Fire] = FireSpec.Presets.ToDictionary(p => p.Key, p => (Func<object>)(() => p.Value()), StringComparer.OrdinalIgnoreCase),
        [Kinds.Foliage] = FoliageSpec.Presets.ToDictionary(p => p.Key, p => (Func<object>)(() => p.Value()), StringComparer.OrdinalIgnoreCase),
        [Kinds.Flow] = RunningWaterSpec.Presets.ToDictionary(p => p.Key, p => (Func<object>)(() => p.Value()), StringComparer.OrdinalIgnoreCase),
        [Kinds.Shore] = ShoreSpec.Presets.ToDictionary(p => p.Key, p => (Func<object>)(() => p.Value()), StringComparer.OrdinalIgnoreCase),
        [Kinds.GasHob] = GasHobSpec.Presets.ToDictionary(p => p.Key, p => (Func<object>)(() => p.Value()), StringComparer.OrdinalIgnoreCase),
        [Kinds.Engine] = EngineProfile.Presets.ToDictionary(p => p.Key, p => (Func<object>)(() => p.Value()), StringComparer.OrdinalIgnoreCase),
        [Kinds.Vehicle] = VehicleProfile.Presets.Keys.ToDictionary(k => k, k => (Func<object>)(() => VehicleSpec.Of(k)), StringComparer.OrdinalIgnoreCase),
        [Kinds.Loudspeaker] = LoudspeakerSpec.Presets.ToDictionary(p => p.Key, p => (Func<object>)(() => p.Value()), StringComparer.OrdinalIgnoreCase),
        [Kinds.RailVehicle] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["genesis_p42"] = () => TrainProfile.GenesisP42,
            ["emd_road_switcher"] = () => TrainProfile.EmdRoadSwitcher,
            ["passenger_coach"] = () => TrainProfile.PassengerCoach,
            ["freight_wagon"] = () => TrainProfile.FreightWagon,
            ["light_rail_car"] = () => TrainProfile.LightRailCar,
            ["metro_car"] = () => TrainProfile.MetroCar,
            ["steam_northern"] = () => TrainProfile.SteamNorthern,
            ["steam_tender"] = () => TrainProfile.SteamTender,
            ["heavyweight_coach"] = () => TrainProfile.HeavyweightCoach,
        },
        [Kinds.Track] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["welded_main_line"] = () => TrackSpec.WeldedMainLine,
            ["jointed_timber"] = () => TrackSpec.JointedTimber,
            ["street_tramway"] = () => TrackSpec.StreetTramway,
            ["metro_slab"] = () => TrackSpec.MetroSlab,
        },
    };

    /// <summary>The runtime type each kind deserializes into.</summary>
    internal static readonly Dictionary<string, Type> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [Kinds.Train] = typeof(TrainProfile),
        [Kinds.RailVehicle] = typeof(RailVehicleSpec),
        [Kinds.Track] = typeof(TrackSpec),
        [Kinds.Horn] = typeof(ChimeHornSpec),
        [Kinds.Whistle] = typeof(WhistleSpec),
        [Kinds.Bell] = typeof(StruckBellSpec),
        [Kinds.Air] = typeof(AirSystemSpec),
        [Kinds.SmallMachine] = typeof(SmallMachineSpec),
        [Kinds.Water] = typeof(WaterFeatureSpec),
        [Kinds.Fire] = typeof(FireSpec),
        [Kinds.Foliage] = typeof(FoliageSpec),
        [Kinds.Flow] = typeof(RunningWaterSpec),
        [Kinds.Shore] = typeof(ShoreSpec),
        [Kinds.GasHob] = typeof(GasHobSpec),
        [Kinds.Engine] = typeof(EngineProfile),
        [Kinds.Vehicle] = typeof(VehicleSpec),
        [Kinds.Loudspeaker] = typeof(LoudspeakerSpec),
    };

    // ── Kinds and their JSON ────────────────────────────────────────────────────────────────────

    /// <summary>Every kind there is.</summary>
    public static IEnumerable<string> AllKinds => Types.Keys;

    /// <summary>The type a kind's models are, or null for a kind that is not one.</summary>
    public static Type? TypeOf(string kind) => Types.TryGetValue(kind, out var t) ? t : null;

    /// <summary>A model as JSON, the way the library reads and writes one (the spec alone, no wrapper).</summary>
    public static string SpecJson(object spec) => JsonSerializer.Serialize(spec, spec.GetType(), Json);

    /// <summary>A model of a kind read from its JSON, as <see cref="SpecJson"/> writes it. Throws if it
    /// does not read as that kind.</summary>
    public static object FromSpecJson(string kind, string json)
    {
        if (!Types.TryGetValue(kind, out var type)) throw new ArgumentException($"'{kind}' is not a kind of model.");
        return JsonSerializer.Deserialize(json, type, Json) ?? throw new ArgumentException($"The {kind} model is empty.");
    }

    // ── The shared catalogue's models ───────────────────────────────────────────────────────────
    //
    // A static facade over the shared catalogue (Catalogue.Shared), until the readers are handed an instance
    // (docs/SOUND_LIBRARY_BOUNDARY.md, stage 5 and decision 7). What each does is said on the catalogue.

    internal static string Key(string kind, string id) => kind + ":" + id;

    /// <inheritdoc cref="Catalogue.Generation"/>
    public static int Generation => Catalogue.Shared.Generation;
    /// <inheritdoc cref="Catalogue.LoadModels"/>
    public static int Load(string directory) => Catalogue.Shared.LoadModels(directory);
    /// <inheritdoc cref="Catalogue.EnsureModelsLoaded"/>
    public static void EnsureLoaded(string directory = "models") => Catalogue.Shared.EnsureModelsLoaded(directory);
    /// <inheritdoc cref="Catalogue.ClearModels"/>
    public static void Clear() => Catalogue.Shared.ClearModels();
    /// <inheritdoc cref="Catalogue.AddModel"/>
    public static void Add(string kind, string id, object spec) => Catalogue.Shared.AddModel(kind, id, spec);
    /// <inheritdoc cref="Catalogue.Knows"/>
    public static bool Knows(string kind, string id) => Catalogue.Shared.Knows(kind, id);
    /// <inheritdoc cref="Catalogue.IsAuthored"/>
    public static bool IsAuthored(string kind, string id) => Catalogue.Shared.IsAuthored(kind, id);
    /// <inheritdoc cref="Catalogue.BuiltInModel"/>
    public static object? BuiltInModel(string kind, string id) => Catalogue.Shared.BuiltInModel(kind, id);
    /// <inheritdoc cref="Catalogue.Ids"/>
    public static IEnumerable<string> Ids(string kind) => Catalogue.Shared.Ids(kind);
    /// <inheritdoc cref="Catalogue.AddJson"/>
    public static bool AddJson(string kind, string id, string json) => Catalogue.Shared.AddJson(kind, id, json);
    /// <inheritdoc cref="Catalogue.Model"/>
    public static object Model(string kind, string id) => Catalogue.Shared.Model(kind, id);
    /// <inheritdoc cref="Catalogue.Get"/>
    public static T Get<T>(string kind, string id) where T : class => Catalogue.Shared.Get<T>(kind, id);

    public static TrainProfile Train(string id) => Catalogue.Shared.Train(id);
    public static RailVehicleSpec RailVehicle(string id) => Catalogue.Shared.RailVehicle(id);
    public static TrackSpec Track(string id) => Catalogue.Shared.Track(id);
    public static ChimeHornSpec Horn(string id) => Catalogue.Shared.Horn(id);
    public static WhistleSpec Whistle(string id) => Catalogue.Shared.Whistle(id);
    public static StruckBellSpec Bell(string id) => Catalogue.Shared.Bell(id);
    public static AirSystemSpec Air(string id) => Catalogue.Shared.Air(id);
    public static SmallMachineSpec SmallMachine(string id) => Catalogue.Shared.SmallMachine(id);
    public static WaterFeatureSpec Water(string id) => Catalogue.Shared.Water(id);
    public static FireSpec Fire(string id) => Catalogue.Shared.Fire(id);
    public static FoliageSpec Foliage(string id) => Catalogue.Shared.Foliage(id);
    public static RunningWaterSpec Flow(string id) => Catalogue.Shared.Flow(id);
    public static ShoreSpec Shore(string id) => Catalogue.Shared.Shore(id);
    public static LoudspeakerSpec Loudspeaker(string id) => Catalogue.Shared.Loudspeaker(id);
    public static GasHobSpec GasHob(string id) => Catalogue.Shared.GasHob(id);
    /// <inheritdoc cref="Catalogue.TryLoudspeaker"/>
    public static bool TryLoudspeaker(string? id, out LoudspeakerSpec spec) => Catalogue.Shared.TryLoudspeaker(id, out spec);

    /// <summary>
    /// The shared catalogue as the sound models are handed it (<see cref="IPresets"/>): a preset by name or by
    /// key, a map's own first. The presets' own lookups through the library (EngineProfile.ByName,
    /// FireSpec.ByName and the rest) were these before the catalogue sat above the sound
    /// (docs/ENGINE_PACKAGES.md, stage 2).
    /// </summary>
    public static IPresets Presets => Catalogue.Shared;

    // ── Writing ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>The file one model would be written as: the wrapper, then the spec.</summary>
    public static string ToJson(string kind, string id, object spec)
        => JsonSerializer.Serialize(new
        {
            kind,
            id,
            spec = JsonSerializer.SerializeToElement(spec, spec.GetType(), Json),
        }, Json);

    /// <summary>
    /// Writes every built-in model to a directory, one file each, as the loader reads them. Never into
    /// the load directory: authored files override the built-ins, so the copy would silently keep every
    /// model at today's numbers after the library improves. Export somewhere to read and copy from.
    /// </summary>
    public static int Export(string directory)
    {
        Directory.CreateDirectory(directory);
        int written = 0;
        foreach (var (kind, lib) in BuiltIn)
            foreach (var (id, make) in lib)
            {
                File.WriteAllText(Path.Combine(directory, $"{kind}.{id}.json"), ToJson(kind, id, make()));
                written++;
            }
        return written;
    }

    /// <summary>
    /// Whether this model survives being written out and read back unchanged. Compares the JSON texts:
    /// a record compares its arrays by reference, so a perfect copy of a spec with bells is unequal.
    /// </summary>
    public static bool RoundTrips(string kind, object spec, out string before, out string after)
    {
        before = JsonSerializer.Serialize(spec, spec.GetType(), Json);
        var back = JsonSerializer.Deserialize(before, Types[kind], Json);
        after = back == null ? "" : JsonSerializer.Serialize(back, Types[kind], Json);
        return before == after;
    }
}

/// <summary>
/// The models' half of the catalogue: what has been put in from data (models/, a map, the world editor), over
/// the built-in library (<see cref="ModelLibrary"/>'s tables), and the presets by key as the sound models are
/// handed them (<see cref="IPresets"/>).
/// </summary>
public sealed partial class Catalogue : IPresets
{
    private volatile Dictionary<string, object> _models = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _modelsLock = new();
    private volatile string? _modelsFrom;

    /// <summary>Changes each time a model is added, so a reader holding a model built from an older one
    /// (a client's cached level) can tell it is out of date.</summary>
    public int Generation => _generation;
    private int _generation;

    // ── Loading models ──────────────────────────────────────────────────────────────────────────

    /// <summary>Reads every <c>*.json</c> in a directory, additively. A file that will not parse is named
    /// and stepped over, so one bad model does not take the map's other sounds with it.</summary>
    public int LoadModels(string directory)
    {
        if (!Directory.Exists(directory)) return 0;
        lock (_modelsLock)
        {
            var next = new Dictionary<string, object>(_models, StringComparer.OrdinalIgnoreCase);
            int loaded = 0;
            foreach (string file in Directory.EnumerateFiles(directory, "*.json"))
            {
                try
                {
                    var wrapper = JsonSerializer.Deserialize<ModelLibrary.ModelFile>(File.ReadAllText(file), ModelLibrary.Json);
                    if (wrapper?.Kind == null) continue;
                    if (!ModelLibrary.Types.TryGetValue(wrapper.Kind, out var type))
                    {
                        Console.WriteLine($"[WARN] ModelLibrary: {Path.GetFileName(file)} is a '{wrapper.Kind}', which is not a kind of model. "
                                        + $"Known: {string.Join(", ", ModelLibrary.Types.Keys)}");
                        continue;
                    }
                    string id = wrapper.Id ?? Path.GetFileNameWithoutExtension(file);
                    object? spec = wrapper.Spec.ValueKind == JsonValueKind.Undefined
                        ? null
                        : wrapper.Spec.Deserialize(type, ModelLibrary.Json);
                    if (spec == null) continue;
                    next[ModelLibrary.Key(wrapper.Kind, id)] = spec;
                    loaded++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WARN] ModelLibrary: {Path.GetFileName(file)} will not load: {ex.Message}");
                }
            }
            _models = next;
            System.Threading.Interlocked.Increment(ref _generation);
            return loaded;
        }
    }

    /// <summary>Loads the authored library once, from the folder next to the maps: server, client and lab
    /// must agree what a model name means.</summary>
    public void EnsureModelsLoaded(string directory = "models")
    {
        if (_modelsFrom == directory) return;
        lock (_modelsLock) { if (_modelsFrom == directory) return; }
        LoadModels(directory);
        _modelsFrom = directory;
    }

    /// <summary>Forgets every model loaded from data. For tests, and for a map change.</summary>
    public void ClearModels()
    {
        lock (_modelsLock)
        {
            _models = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            _modelsFrom = null;
            // Whatever was built from a model just forgotten (MachineRegistry's vehicles) is stale too.
            System.Threading.Interlocked.Increment(ref _generation);
        }
    }

    /// <summary>Adds a model directly, as a map or a test would.</summary>
    public void AddModel(string kind, string id, object spec)
    {
        lock (_modelsLock)
        {
            _models = new Dictionary<string, object>(_models, StringComparer.OrdinalIgnoreCase)
            { [ModelLibrary.Key(kind, id)] = spec };
            System.Threading.Interlocked.Increment(ref _generation);
        }
    }

    // ── Asking for a model ──────────────────────────────────────────────────────────────────────

    /// <summary>Is there a model of this kind and name — authored, or built in?</summary>
    public bool Knows(string kind, string id)
        => !string.IsNullOrEmpty(id)
           && (_models.ContainsKey(ModelLibrary.Key(kind, id))
               || (ModelLibrary.BuiltIn.TryGetValue(kind, out var lib) && lib.ContainsKey(id))
               || IsPartsList(kind, id));

    /// <summary>
    /// A vehicle written as a parts list in machines/ (MachineRegistry.Authored): a vehicle model as it
    /// ships, like a built-in one, whose version 0 is its parts assembled. Asked of the machines each time,
    /// since their folder is loaded after this library is made.
    /// </summary>
    private bool IsPartsList(string kind, string id)
        => string.Equals(kind, ModelLibrary.Kinds.Vehicle, StringComparison.OrdinalIgnoreCase) && _machines.ContainsKey(id);

    /// <summary>Whether a model of this kind and name has been put in from data (a file, a map, the world
    /// editor) rather than being only the built-in.</summary>
    public bool IsAuthored(string kind, string id) => !string.IsNullOrEmpty(id) && _models.ContainsKey(ModelLibrary.Key(kind, id));

    /// <summary>The built-in model of this kind and name, as it ships (never an authored one), or null.</summary>
    public object? BuiltInModel(string kind, string id)
        => ModelLibrary.BuiltIn.TryGetValue(kind, out var lib) && lib.TryGetValue(id, out var make) ? make()
         : IsPartsList(kind, id) ? VehicleSpec.Of(id) : null;

    /// <summary>Every model of a kind, authored and built in.</summary>
    public IEnumerable<string> Ids(string kind)
    {
        var built = ModelLibrary.BuiltIn.TryGetValue(kind, out var lib) ? lib.Keys : Enumerable.Empty<string>();
        if (string.Equals(kind, ModelLibrary.Kinds.Vehicle, StringComparison.OrdinalIgnoreCase))
            built = built.Concat(_machines.Keys.Where(k => !lib!.ContainsKey(k))).ToList();
        string prefix = kind + ":";
        var authored = _models.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                                     .Select(k => k[prefix.Length..]);
        return built.Concat(authored.Where(a => !built.Contains(a, StringComparer.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Puts a model sent as JSON into the library, as an authored one: what the world editor's changes
    /// arrive as (ModelUpdate). False, and nothing changed, if it does not read as that kind.
    /// </summary>
    public bool AddJson(string kind, string id, string json)
    {
        try
        {
            AddModel(kind, id, ModelLibrary.FromSpecJson(kind, json));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>A model by kind and name as the base type, for code that does not know the kind's type.</summary>
    public object Model(string kind, string id) => Get<object>(kind, id);

    /// <summary>A model by kind and name, the authored one first, so a map can replace a built-in.</summary>
    public T Get<T>(string kind, string id) where T : class
    {
        if (_models.TryGetValue(ModelLibrary.Key(kind, id), out var authored) && authored is T typed) return typed;
        if (ModelLibrary.BuiltIn.TryGetValue(kind, out var lib) && lib.TryGetValue(id, out var make) && make() is T built) return built;
        if (IsPartsList(kind, id) && VehicleSpec.Of(id) is T parts) return parts;
        throw new ArgumentException(
            $"No {kind} called '{id}'. Known: {string.Join(", ", Ids(kind))}");
    }

    public TrainProfile Train(string id) => Get<TrainProfile>(ModelLibrary.Kinds.Train, id);
    public RailVehicleSpec RailVehicle(string id) => Get<RailVehicleSpec>(ModelLibrary.Kinds.RailVehicle, id);
    public TrackSpec Track(string id) => Get<TrackSpec>(ModelLibrary.Kinds.Track, id);
    public ChimeHornSpec Horn(string id) => Get<ChimeHornSpec>(ModelLibrary.Kinds.Horn, id);
    public WhistleSpec Whistle(string id) => Get<WhistleSpec>(ModelLibrary.Kinds.Whistle, id);
    public StruckBellSpec Bell(string id) => Get<StruckBellSpec>(ModelLibrary.Kinds.Bell, id);
    public AirSystemSpec Air(string id) => Get<AirSystemSpec>(ModelLibrary.Kinds.Air, id);
    public SmallMachineSpec SmallMachine(string id) => Get<SmallMachineSpec>(ModelLibrary.Kinds.SmallMachine, id);
    public WaterFeatureSpec Water(string id) => Get<WaterFeatureSpec>(ModelLibrary.Kinds.Water, id);
    public FireSpec Fire(string id) => Get<FireSpec>(ModelLibrary.Kinds.Fire, id);
    public FoliageSpec Foliage(string id) => Get<FoliageSpec>(ModelLibrary.Kinds.Foliage, id);
    public RunningWaterSpec Flow(string id) => Get<RunningWaterSpec>(ModelLibrary.Kinds.Flow, id);
    public ShoreSpec Shore(string id) => Get<ShoreSpec>(ModelLibrary.Kinds.Shore, id);
    public LoudspeakerSpec Loudspeaker(string id) => Get<LoudspeakerSpec>(ModelLibrary.Kinds.Loudspeaker, id);
    public GasHobSpec GasHob(string id) => Get<GasHobSpec>(ModelLibrary.Kinds.GasHob, id);

    /// <summary>The speaker a placed emitter names, an authored one first; false for one there is not.</summary>
    public bool TryLoudspeaker(string? id, out LoudspeakerSpec spec)
    {
        spec = null!;
        if (string.IsNullOrEmpty(id) || !Knows(ModelLibrary.Kinds.Loudspeaker, id)) return false;
        spec = Loudspeaker(id);
        return true;
    }

    // ── The presets by key, as a sound model is handed them ─────────────────────────────────────

    // As changed in the world editor if it has been (it is a library kind, Kinds.Engine), otherwise as built.
    // Every vehicle, small machine, train and aircraft that names it has the change.
    EngineProfile IPresets.Engine(string key)
        => IsAuthored(ModelLibrary.Kinds.Engine, key) ? Get<EngineProfile>(ModelLibrary.Kinds.Engine, key)
         : EngineProfile.Presets.TryGetValue(key, out var make) ? make()
         : throw new ArgumentException($"No engine preset '{key}'. Known: {string.Join(", ", EngineProfile.Presets.Keys)}");

    ChimeHornSpec IPresets.Horn(string id) => Horn(id);
    WhistleSpec IPresets.Whistle(string id) => Whistle(id);
    StruckBellSpec IPresets.Bell(string id) => Bell(id);
    AirSystemSpec IPresets.Air(string id) => Air(id);
    SmallMachineSpec IPresets.SmallMachine(string key) => SmallMachine(key);
    WaterFeatureSpec IPresets.Water(string key) => Water(key);
    FoliageSpec IPresets.Foliage(string key) => Foliage(key);

    // A drainage line of the ground's key says what it is ("ground/...", GroundChannels).
    RunningWaterSpec IPresets.Flow(string key)
        => GroundChannels.TryParse(key, out var ground) ? ground : Flow(key);

    // A map's key with its geometry ("shore:lake_sand/300/90/20") names its preset; a whole outline's
    // ("shore:pond_bank/outline/410") is as loud as its stretches together.
    ShoreSpec IPresets.Shore(string key)
    {
        if (ShoreSpec.TryOutline(key, out string outlined, out float perimeter)) return Shore(outlined).AlongOutline(perimeter);
        ShoreSpec.ParseKey(key, out string preset, out _);
        return Shore(preset);
    }

    // Burning over the shape the key gives it.
    FireSpec IPresets.Fire(string key)
    {
        FireSpec.ParseKey(key, out string preset, out _, out var shape);
        var spec = Fire(preset);
        return shape == null ? spec : spec.WithShape(shape);
    }

    GasHobSpec IPresets.GasHob(string key)
    {
        HobKey.TryParse(key, out var k);
        return GasHob(k.Preset.Length > 0 ? k.Preset : key);
    }

    // The kind table is a static facade over the shared catalogue for now: its kinds' declarations ask it.
    System.Numerics.Vector3[]? IPresets.Places(string soundId) => ModelKindTable.Places(soundId);
}

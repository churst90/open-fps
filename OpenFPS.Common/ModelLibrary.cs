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

        /// <summary>A road vehicle's engine: EngineProfile. Every vehicle built on it has the change.</summary>
        public const string Engine = "engine";
        /// <summary>A road vehicle, as the world editor edits one (VehicleSpec): MachineRegistry's
        /// vehicles, with the engine, chassis, outlets, tyres, body and gearbox as data.</summary>
        public const string Vehicle = "vehicle";
    }

    private sealed class ModelFile
    {
        public string? Kind { get; set; }
        public string? Id { get; set; }
        public JsonElement Spec { get; set; }
    }

    private static readonly JsonSerializerOptions Json = new()
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

    private static volatile Dictionary<string, object> _authored = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _lock = new();
    private static volatile string? _loadedFrom;

    private static string Key(string kind, string id) => kind + ":" + id;

    /// <summary>Changes each time a model is added, so a reader holding a model built from an older one
    /// (a client's cached level) can tell it is out of date.</summary>
    public static int Generation => _generation;
    private static int _generation;

    // ── The built-in library ────────────────────────────────────────────────────────────────────

    /// <summary>Every model that ships in C#, by kind. Rail vehicles are named one by one, so a consist
    /// can be "a Genesis and six of my coaches".</summary>
    private static readonly Dictionary<string, Dictionary<string, Func<object>>> BuiltIn = new(StringComparer.OrdinalIgnoreCase)
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
        [Kinds.Engine] = EngineProfile.Presets.ToDictionary(p => p.Key, p => (Func<object>)(() => p.Value()), StringComparer.OrdinalIgnoreCase),
        [Kinds.Vehicle] = VehicleProfile.Presets.Keys.ToDictionary(k => k, k => (Func<object>)(() => VehicleSpec.Of(k)), StringComparer.OrdinalIgnoreCase),
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
    private static readonly Dictionary<string, Type> Types = new(StringComparer.OrdinalIgnoreCase)
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
        [Kinds.Engine] = typeof(EngineProfile),
        [Kinds.Vehicle] = typeof(VehicleSpec),
    };

    // ── Loading ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Reads every <c>*.json</c> in a directory, additively. A file that will not parse is named
    /// and stepped over, so one bad model does not take the map's other sounds with it.</summary>
    public static int Load(string directory)
    {
        if (!Directory.Exists(directory)) return 0;
        lock (_lock)
        {
            var next = new Dictionary<string, object>(_authored, StringComparer.OrdinalIgnoreCase);
            int loaded = 0;
            foreach (string file in Directory.EnumerateFiles(directory, "*.json"))
            {
                try
                {
                    var wrapper = JsonSerializer.Deserialize<ModelFile>(File.ReadAllText(file), Json);
                    if (wrapper?.Kind == null) continue;
                    if (!Types.TryGetValue(wrapper.Kind, out var type))
                    {
                        Console.WriteLine($"[WARN] ModelLibrary: {Path.GetFileName(file)} is a '{wrapper.Kind}', which is not a kind of model. "
                                        + $"Known: {string.Join(", ", Types.Keys)}");
                        continue;
                    }
                    string id = wrapper.Id ?? Path.GetFileNameWithoutExtension(file);
                    object? spec = wrapper.Spec.ValueKind == JsonValueKind.Undefined
                        ? null
                        : wrapper.Spec.Deserialize(type, Json);
                    if (spec == null) continue;
                    next[Key(wrapper.Kind, id)] = spec;
                    loaded++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WARN] ModelLibrary: {Path.GetFileName(file)} will not load: {ex.Message}");
                }
            }
            _authored = next;
            System.Threading.Interlocked.Increment(ref _generation);
            return loaded;
        }
    }

    /// <summary>Loads the authored library once, from the folder next to the maps: server, client and lab
    /// must agree what a model name means.</summary>
    public static void EnsureLoaded(string directory = "models")
    {
        if (_loadedFrom == directory) return;
        lock (_lock) { if (_loadedFrom == directory) return; }
        Load(directory);
        _loadedFrom = directory;
    }

    /// <summary>Forgets everything loaded from data. For tests, and for a map change.</summary>
    public static void Clear()
    {
        lock (_lock)
        {
            _authored = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            _loadedFrom = null;
            // Whatever was built from a model just forgotten (MachineRegistry's vehicles) is stale too.
            System.Threading.Interlocked.Increment(ref _generation);
        }
    }

    /// <summary>Adds a model directly, as a map or a test would.</summary>
    public static void Add(string kind, string id, object spec)
    {
        lock (_lock)
        {
            _authored = new Dictionary<string, object>(_authored, StringComparer.OrdinalIgnoreCase)
            { [Key(kind, id)] = spec };
            System.Threading.Interlocked.Increment(ref _generation);
        }
    }

    // ── Asking ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>Is there a model of this kind and name — authored, or built in?</summary>
    public static bool Knows(string kind, string id)
        => !string.IsNullOrEmpty(id)
           && (_authored.ContainsKey(Key(kind, id))
               || (BuiltIn.TryGetValue(kind, out var lib) && lib.ContainsKey(id))
               || IsPartsList(kind, id));

    /// <summary>
    /// A vehicle written as a parts list in machines/ (MachineRegistry.Authored): a vehicle model as it
    /// ships, like a built-in one, whose version 0 is its parts assembled. Asked of MachineRegistry each
    /// time, since its folder is loaded after this library is made.
    /// </summary>
    private static bool IsPartsList(string kind, string id)
        => string.Equals(kind, Kinds.Vehicle, StringComparison.OrdinalIgnoreCase) && MachineRegistry.Authored.ContainsKey(id);

    /// <summary>Whether a model of this kind and name has been put in from data (a file, a map, the world
    /// editor) rather than being only the built-in.</summary>
    public static bool IsAuthored(string kind, string id) => !string.IsNullOrEmpty(id) && _authored.ContainsKey(Key(kind, id));

    /// <summary>The built-in model of this kind and name, as it ships (never an authored one), or null.</summary>
    public static object? BuiltInModel(string kind, string id)
        => BuiltIn.TryGetValue(kind, out var lib) && lib.TryGetValue(id, out var make) ? make()
         : IsPartsList(kind, id) ? VehicleSpec.Of(id) : null;

    /// <summary>Every model of a kind, authored and built in.</summary>
    public static IEnumerable<string> Ids(string kind)
    {
        var built = BuiltIn.TryGetValue(kind, out var lib) ? lib.Keys : Enumerable.Empty<string>();
        if (string.Equals(kind, Kinds.Vehicle, StringComparison.OrdinalIgnoreCase))
            built = built.Concat(MachineRegistry.Authored.Keys.Where(k => !lib!.ContainsKey(k))).ToList();
        string prefix = kind + ":";
        var authored = _authored.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                                     .Select(k => k[prefix.Length..]);
        return built.Concat(authored.Where(a => !built.Contains(a, StringComparer.OrdinalIgnoreCase)));
    }

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

    /// <summary>
    /// Puts a model sent as JSON into the library, as an authored one: what the world editor's changes
    /// arrive as (ModelUpdate). False, and nothing changed, if it does not read as that kind.
    /// </summary>
    public static bool AddJson(string kind, string id, string json)
    {
        try
        {
            Add(kind, id, FromSpecJson(kind, json));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>A model by kind and name as the base type, for code that does not know the kind's type.</summary>
    public static object Model(string kind, string id) => Get<object>(kind, id);

    /// <summary>A model by kind and name, the authored one first, so a map can replace a built-in.</summary>
    public static T Get<T>(string kind, string id) where T : class
    {
        if (_authored.TryGetValue(Key(kind, id), out var authored) && authored is T typed) return typed;
        if (BuiltIn.TryGetValue(kind, out var lib) && lib.TryGetValue(id, out var make) && make() is T built) return built;
        if (IsPartsList(kind, id) && VehicleSpec.Of(id) is T parts) return parts;
        throw new ArgumentException(
            $"No {kind} called '{id}'. Known: {string.Join(", ", Ids(kind))}");
    }

    public static TrainProfile Train(string id) => Get<TrainProfile>(Kinds.Train, id);
    public static RailVehicleSpec RailVehicle(string id) => Get<RailVehicleSpec>(Kinds.RailVehicle, id);
    public static TrackSpec Track(string id) => Get<TrackSpec>(Kinds.Track, id);
    public static ChimeHornSpec Horn(string id) => Get<ChimeHornSpec>(Kinds.Horn, id);
    public static WhistleSpec Whistle(string id) => Get<WhistleSpec>(Kinds.Whistle, id);
    public static StruckBellSpec Bell(string id) => Get<StruckBellSpec>(Kinds.Bell, id);
    public static AirSystemSpec Air(string id) => Get<AirSystemSpec>(Kinds.Air, id);
    public static SmallMachineSpec SmallMachine(string id) => Get<SmallMachineSpec>(Kinds.SmallMachine, id);
    public static WaterFeatureSpec Water(string id) => Get<WaterFeatureSpec>(Kinds.Water, id);
    public static FireSpec Fire(string id) => Get<FireSpec>(Kinds.Fire, id);
    public static FoliageSpec Foliage(string id) => Get<FoliageSpec>(Kinds.Foliage, id);
    public static RunningWaterSpec Flow(string id) => Get<RunningWaterSpec>(Kinds.Flow, id);
    public static ShoreSpec Shore(string id) => Get<ShoreSpec>(Kinds.Shore, id);

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

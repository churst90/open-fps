using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenFPS.Common;

/// <summary>
/// The models a map can name: trains, rail vehicles, track, horns, whistles, bells and air systems.
///
/// Every one of these is already a parts list with dimensions rather than a bag of tuning — that is
/// the rule the whole audio engine is built on. What this adds is the other half of the same idea:
/// **a model is DATA, so it can be written, shared and overridden without recompiling anything.**
/// `MachineRegistry` did it for vehicles, and had to do real work to get there, because
/// `VehicleProfile` is not a data shape — it needed a `Describe`/`Assemble` translation and a round
/// trip to prove the vocabulary was sufficient. These specs were written as records of scalars from
/// the start, so the translation is the serializer, and the round trip is the test that they really
/// were.
///
/// One directory, one file per model, each saying what kind of thing it is:
///
/// <code>
/// { "kind": "horn", "id": "k3la", "spec": { "Name": "...", "Bells": [ ... ] } }
/// </code>
///
/// **An authored model of the same name OVERRIDES the built-in**, which is how a map replaces the
/// crossing bell on one line without touching the library — and it is also the trap: never check an
/// exported copy of the library back into the load directory, because a generated file freezes every
/// model at the numbers it had the day it was written. <see cref="Export"/> writes somewhere else to
/// read and copy from. (The same warning is on MachineRegistry, in blood.)
/// </summary>
public static class ModelLibrary
{
    /// <summary>The kinds of model this library holds. A string, not an enum, for the same reason
    /// <see cref="MachineModels"/> uses strings: the list is going to grow.</summary>
    public static class Kinds
    {
        public const string Train = "train";
        public const string RailVehicle = "rail_vehicle";
        public const string Track = "track";
        public const string Horn = "horn";
        public const string Whistle = "whistle";
        public const string Bell = "bell";
        public const string Air = "air";
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
    };

    private static volatile Dictionary<string, object> _authored = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _lock = new();
    private static volatile string? _loadedFrom;

    private static string Key(string kind, string id) => kind + ":" + id;

    // ── The built-in library ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every model that ships in C#, by kind. Rail vehicles are named individually because a consist
    /// is assembled from them and an author wants to say "a Genesis and six of MY coaches".
    /// </summary>
    private static readonly Dictionary<string, Dictionary<string, Func<object>>> BuiltIn = new(StringComparer.OrdinalIgnoreCase)
    {
        [Kinds.Train] = TrainProfile.Presets.ToDictionary(p => p.Key, p => (Func<object>)(() => p.Value()), StringComparer.OrdinalIgnoreCase),
        [Kinds.Horn] = ChimeHornSpec.Presets.ToDictionary(p => p.Key, p => (Func<object>)(() => p.Value()), StringComparer.OrdinalIgnoreCase),
        [Kinds.Whistle] = WhistleSpec.Presets.ToDictionary(p => p.Key, p => (Func<object>)(() => p.Value()), StringComparer.OrdinalIgnoreCase),
        [Kinds.Bell] = StruckBellSpec.Presets.ToDictionary(p => p.Key, p => (Func<object>)(() => p.Value()), StringComparer.OrdinalIgnoreCase),
        [Kinds.Air] = AirSystemSpec.Presets.ToDictionary(p => p.Key, p => (Func<object>)(() => p.Value()), StringComparer.OrdinalIgnoreCase),
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
    };

    // ── Loading ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads every <c>*.json</c> in a directory. Additive, and never fatal: a file that will not
    /// parse is named and stepped over, because one bad model in an author's folder should not take
    /// the rest of the map's sounds with it.
    /// </summary>
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
            return loaded;
        }
    }

    /// <summary>
    /// Loads the authored library once, from the well-known folder next to the maps. Called by
    /// whatever starts up — server, client, lab — because all three have to agree what a model name
    /// means.
    /// </summary>
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
        }
    }

    /// <summary>Adds a model directly, as a map or a test would.</summary>
    public static void Add(string kind, string id, object spec)
    {
        lock (_lock)
        {
            _authored = new Dictionary<string, object>(_authored, StringComparer.OrdinalIgnoreCase)
            { [Key(kind, id)] = spec };
        }
    }

    // ── Asking ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>Is there a model of this kind and name — authored, or built in?</summary>
    public static bool Knows(string kind, string id)
        => !string.IsNullOrEmpty(id)
           && (_authored.ContainsKey(Key(kind, id))
               || (BuiltIn.TryGetValue(kind, out var lib) && lib.ContainsKey(id)));

    /// <summary>Every model of a kind, authored and built in.</summary>
    public static IEnumerable<string> Ids(string kind)
    {
        var built = BuiltIn.TryGetValue(kind, out var lib) ? lib.Keys : Enumerable.Empty<string>();
        string prefix = kind + ":";
        var authored = _authored.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                                     .Select(k => k[prefix.Length..]);
        return built.Concat(authored.Where(a => !built.Contains(a, StringComparer.OrdinalIgnoreCase)));
    }

    /// <summary>Every kind there is.</summary>
    public static IEnumerable<string> AllKinds => Types.Keys;

    /// <summary>
    /// A model by kind and name. Authored first, always: that is what lets a map replace one without
    /// editing the library.
    /// </summary>
    public static T Get<T>(string kind, string id) where T : class
    {
        if (_authored.TryGetValue(Key(kind, id), out var authored) && authored is T typed) return typed;
        if (BuiltIn.TryGetValue(kind, out var lib) && lib.TryGetValue(id, out var make) && make() is T built) return built;
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
    /// Writes every built-in model to a directory, one file each, in exactly the form the loader
    /// reads.
    ///
    /// **Not into the load directory.** An authored file overrides the built-in of the same name, so
    /// a generated copy of the library silently freezes every model at the numbers it had the day it
    /// was written — the next time somebody improves the bell, every map that has this folder keeps
    /// the old one and nobody can see why. Export somewhere to READ and copy the one line you want.
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
    /// The round trip, as a question a test can ask: does this model survive being written out and
    /// read back unchanged?
    ///
    /// Comparing the two JSON texts rather than the two objects, because a record's equality compares
    /// its arrays BY REFERENCE — so a spec holding an array of bells would report itself unequal to a
    /// perfect copy, and a test written the obvious way would fail for a reason that has nothing to
    /// do with the model.
    /// </summary>
    public static bool RoundTrips(string kind, object spec, out string before, out string after)
    {
        before = JsonSerializer.Serialize(spec, spec.GetType(), Json);
        var back = JsonSerializer.Deserialize(before, Types[kind], Json);
        after = back == null ? "" : JsonSerializer.Serialize(back, Types[kind], Json);
        return before == after;
    }
}

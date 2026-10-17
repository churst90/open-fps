using System.Collections.Concurrent;
using System.Numerics;
using OpenFPS.Sound;
using OpenFPS.Sound.Core.Nature;

namespace OpenFPS.Models;

/// <summary>How a modelled thing moves: what decides whether it has footsteps and whether it is a vehicle.</summary>
public enum ModelBody : byte
{
    /// <summary>Stands where it is put and runs: a condenser, a fountain, a fire, a crossing's bell.</summary>
    Fixed,
    /// <summary>Moves at a walk because somebody walks it: a push mower and the person behind it.</summary>
    Walks,
    /// <summary>On wheels on a road: a road vehicle.</summary>
    Rolls,
    /// <summary>An aeroplane.</summary>
    Flies,
    /// <summary>Rolling stock on a track.</summary>
    Rails,
}

/// <summary>Which synth makes a model's voice. The client's provider has one constructor for each.</summary>
public enum ModelSynth : byte
{
    Engine, Machine, Aircraft, Rail, Siren, Bell, Gate, Water, Fire, Foliage, Flow, Shore, Wood, Rain, Stove, Horn,
}

/// <summary>
/// One kind of model, named by the prefix of the sound ids it owns ("machine:ac_condenser"): its synth,
/// its body, the library kind its ids name, and how its ids declare their level, size, headroom and places.
/// </summary>
public sealed class ModelKind
{
    /// <summary>The prefix, without its colon.</summary>
    public required string Prefix { get; init; }
    public required ModelSynth Synth { get; init; }
    /// <summary>The kind's own body. A machine can say otherwise of itself (SmallMachineSpec.WalkedBehind).</summary>
    public ModelBody Body { get; init; } = ModelBody.Fixed;
    /// <summary>The ModelLibrary kind the id after the prefix names, for the editor; null where the editor
    /// does not edit this kind through its sound id.</summary>
    public string? LibraryKind { get; init; }

    /// <summary>Level at a metre and size, from the whole id; null for a kind placed by its own path (an
    /// engine, a siren, a horn, the rain) or an id that names no source of it (a train's index past its
    /// layout). Throws when the id names a model that is not there.</summary>
    internal Func<string, (float LevelDb, float ExtentMetres)?>? Level { get; init; }
    /// <summary>Headroom its voice renders with, dB; null for the shared one.</summary>
    internal Func<string, float?>? Headroom { get; init; }
    /// <summary>The places it is heard from (ExtendedSources.Layout), before the lab's scale and the cap.</summary>
    internal Func<string, Vector3[]?>? Places { get; init; }
    /// <summary>Whether the id names a model that exists, for a kind with no declared level.</summary>
    internal Func<string, bool>? Knows { get; init; }

    /// <summary>The sound id with its prefix taken off.</summary>
    public string Rest(string soundId) => soundId[(Prefix.Length + 1)..];
}

/// <summary>
/// Every kind of model a sound id can name, in one table: what makes it, how it moves, its level,
/// size, headroom and places. The ranking, the voice, the places, the editor, the scope and the
/// footsteps all read it. Each kept its own prefix list before, and a kind missing from one was
/// silent there: the crossing bell twice, cars that walked. An id that names a kind but not a model
/// is logged once, by id; it is never quietly a nameless synth.
/// </summary>
public static class ModelKindTable
{
    private static readonly ModelKind[] Rows =
    {
        new() { Prefix = "engine", Synth = ModelSynth.Engine, Body = ModelBody.Rolls, LibraryKind = ModelLibrary.Kinds.Vehicle,
                Knows = id => MachineRegistry.Knows(id[7..]) },
        new() { Prefix = "machine", Synth = ModelSynth.Machine, LibraryKind = ModelLibrary.Kinds.SmallMachine,
                Level = id => { var s = ModelLibrary.Presets.SmallMachine(id[8..]); return (s.SourceLevelDb, s.ExtentMetres); } },
        new() { Prefix = "aircraft", Synth = ModelSynth.Aircraft, Body = ModelBody.Flies, Level = AircraftLevel },
        new() { Prefix = "rail", Synth = ModelSynth.Rail, Body = ModelBody.Rails, Level = RailLevel, Headroom = RailHeadroom },
        new() { Prefix = "siren", Synth = ModelSynth.Siren, Knows = id => SirenSpec.ByName(id[6..]) != null },
        new() { Prefix = "bell", Synth = ModelSynth.Bell, LibraryKind = ModelLibrary.Kinds.Bell,
                // A level crossing's gong: its size is the bell.
                Level = id => { var b = ModelLibrary.Bell(id[5..]); return (b.ReferenceDb, MathF.Max(0.5f, b.DiameterMetres)); },
                Headroom = id => ModelLibrary.Bell(id[5..]).PeakHeadroomDb },
        // A level crossing's gate mechanism, by its motor's level; the case at the mast's foot is half a metre of steel.
        new() { Prefix = "gate", Synth = ModelSynth.Gate, Level = id => (CrossingGateSpec.ByName(id[5..]).SourceLevelDb, 0.5f) },
        new() { Prefix = "water", Synth = ModelSynth.Water, LibraryKind = ModelLibrary.Kinds.Water,
                Level = WaterLevel, Headroom = WaterHeadroom, Places = id => ExtendedSources.WaterTapPlacesOf(id, ModelLibrary.Presets) },
        new() { Prefix = "fire", Synth = ModelSynth.Fire, LibraryKind = ModelLibrary.Kinds.Fire,
                Level = id => { var f = ModelLibrary.Presets.Fire(id[5..]); return (f.SourceLevelDb, f.ExtentMetres); },
                Headroom = id => ModelLibrary.Presets.Fire(id[5..]).PeakHeadroomDb, Places = id => ExtendedSources.FirePlacesOf(id, ModelLibrary.Presets) },
        new() { Prefix = "foliage", Synth = ModelSynth.Foliage, LibraryKind = ModelLibrary.Kinds.Foliage,
                Level = id => { var t = ModelLibrary.Presets.Foliage(id[8..]); return (t.SourceLevelDb, t.ExtentMetres); },
                Headroom = id => ModelLibrary.Presets.Foliage(id[8..]).PeakHeadroomDb, Places = id => ExtendedSources.TreePlacesOf(id, ModelLibrary.Presets) },
        // A wood heard as one (WoodChorus): one tree's level, since its synth renders its trees, and the wood's size.
        new() { Prefix = "wood", Synth = ModelSynth.Wood, Level = WoodLevel, Headroom = WoodHeadroom, Places = ExtendedSources.WoodPlacesOf },
        // Running water: its size is its length or its opening, its level declared at its base flow or reference rain.
        new() { Prefix = "flow", Synth = ModelSynth.Flow, LibraryKind = ModelLibrary.Kinds.Flow,
                Level = id => { var f = ModelLibrary.Presets.Flow(id[5..]); return (f.SourceLevelDb, f.ExtentMetres); },
                Headroom = id => ModelLibrary.Presets.Flow(id[5..]).PeakHeadroomDb, Places = id => ExtendedSources.FlowPlacesOf(id, ModelLibrary.Presets) },
        // Waves at an edge, declared at the reference wind straight onshore; its size a stretch of the edge.
        new() { Prefix = "shore", Synth = ModelSynth.Shore, LibraryKind = ModelLibrary.Kinds.Shore,
                Level = id => { var s = ModelLibrary.Presets.Shore(id); return (s.SourceLevelDb, s.ExtentMetres); },
                Headroom = id => ModelLibrary.Presets.Shore(id).PeakHeadroomDb, Places = id => ExtendedSources.ShorePlacesOf(id, ModelLibrary.Presets) },
        // A gas hob, declared with every burner on full; its size is the hob.
        new() { Prefix = "stove", Synth = ModelSynth.Stove, LibraryKind = ModelLibrary.Kinds.GasHob,
                Level = id => { var h = ModelLibrary.Presets.GasHob(id); return (h.SourceLevelDb, h.ExtentMetres); },
                Headroom = id => ModelLibrary.Presets.GasHob(id).PeakHeadroomDb },
        new() { Prefix = "horn", Synth = ModelSynth.Horn, Knows = id => Honk.TryParse(id, out _, out _) },
        new() { Prefix = "rain", Synth = ModelSynth.Rain, Knows = _ => true },
    };

    private static readonly Dictionary<string, ModelKind> ByPrefix =
        Rows.ToDictionary(r => r.Prefix, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<ModelSynth, ModelKind> BySynth = Rows.ToDictionary(r => r.Synth);

    /// <summary>Every kind, in the table's order.</summary>
    public static IReadOnlyList<ModelKind> All => Rows;

    /// <summary>The kind a sound id names by its prefix, or null for a recording or anything no kind owns.</summary>
    public static ModelKind? KindOf(string? soundId)
    {
        if (string.IsNullOrEmpty(soundId)) return null;
        int colon = soundId.IndexOf(':');
        if (colon <= 0) return null;
        return ByPrefix.GetValueOrDefault(soundId[..colon]);
    }

    /// <summary>Whether a sound id is of this kind.</summary>
    public static bool Is(string? soundId, ModelSynth synth) => KindOf(soundId)?.Synth == synth;

    /// <summary>The sound id of a model of a kind: the kind's prefix, a colon and <paramref name="rest"/>
    /// ("bell:" and "crossing_gong").</summary>
    public static string IdOf(ModelSynth synth, string rest) => BySynth[synth].Prefix + ":" + rest;

    // ── Level, headroom, places ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// A model's level at a metre and its size, for the ranking and the voice alike. False for an id
    /// that declares none; a model that will not load is logged once and is not placed.
    /// </summary>
    public static bool TryLevel(string soundId, out float levelDb, out float extentMetres)
    {
        levelDb = 0f; extentMetres = 1f;
        if (KindOf(soundId) is not { Level: { } level }) return false;
        try
        {
            if (level(soundId) is not { } declared) return false;
            (levelDb, extentMetres) = declared;
            return true;
        }
        catch (Exception ex)
        {
            Fail(soundId, ex.Message);
            return false;
        }
    }

    /// <summary>The headroom a model's voice renders with, dB: its spec's own where it declares one, the shared
    /// one otherwise and for a model that will not load (logged once).</summary>
    public static float Headroom(string soundId)
    {
        if (KindOf(soundId) is not { Headroom: { } headroom }) return VehicleProfile.PeakHeadroomDb;
        try { return headroom(soundId) ?? VehicleProfile.PeakHeadroomDb; }
        catch (Exception ex)
        {
            Fail(soundId, ex.Message);
            return VehicleProfile.PeakHeadroomDb;
        }
    }

    /// <summary>The places a model is heard from, unscaled and uncapped (ExtendedSources.Layout caps and
    /// caches them, asking here through <see cref="ModelLibrary.Presets"/>); null for one heard from one point.</summary>
    internal static Vector3[]? Places(string soundId)
    {
        if (KindOf(soundId) is not { Places: { } places }) return null;
        try { return places(soundId); }
        catch (Exception ex)
        {
            Fail(soundId, ex.Message);
            return null;
        }
    }

    // ── Bodies ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// How the thing a sound id names moves, or null when it names no model. A machine walked behind (a
    /// push mower) walks: somebody is behind it. Memoised: asked per body per frame, and a machine's
    /// spec is built whole each time it is asked for.
    /// </summary>
    public static ModelBody? BodyOf(string? soundId)
    {
        var kind = KindOf(soundId);
        if (kind == null) return null;
        if (kind.Synth != ModelSynth.Machine) return kind.Body;
        return Catalogue.Shared.MachineBody(soundId!);
    }

    /// <summary>Whether a sound id names a road vehicle, known or not.</summary>
    public static bool IsVehicle(string? soundId) => KindOf(soundId)?.Body == ModelBody.Rolls;

    /// <summary>The vehicle preset a sound id names ("engine:school_bus" is "school_bus"), known or not; null
    /// for anything that is not a road vehicle.</summary>
    public static string? VehiclePreset(string? soundId)
        => KindOf(soundId) is { Body: ModelBody.Rolls } kind ? kind.Rest(soundId!) : null;

    /// <summary>The vehicle preset a sound id names, when MachineRegistry knows it; an unknown one is logged once.</summary>
    public static string? KnownVehiclePreset(string? soundId)
    {
        if (VehiclePreset(soundId) is not { } preset) return null;
        if (MachineRegistry.Knows(preset)) return preset;
        Fail(soundId!, "names a vehicle that is not known");
        return null;
    }

    /// <summary>The profile of the vehicle a sound id names, or null for anything that is not a known vehicle.</summary>
    public static VehicleProfile? Vehicle(string? soundId)
        => KnownVehiclePreset(soundId) is { } preset ? MachineRegistry.VehicleFor(preset) : null;

    // ── Resolving ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Whether a synthesised sound id names a model that is there: its kind is in the table and the
    /// model it names loads. <paramref name="why"/> says what is missing. For the tests and the map
    /// checks; asked of every shipped map's and prefab's sound ids.
    /// </summary>
    public static bool Resolves(string? soundId, out string why)
    {
        why = "";
        var kind = KindOf(soundId);
        if (kind == null) { why = "names no kind of model"; return false; }
        try
        {
            if (kind.Level != null)
            {
                if (kind.Level(soundId!) != null) return true;
                why = "declares no level";
                return false;
            }
            if (kind.Knows != null && kind.Knows(soundId!)) return true;
            why = "names no model of its kind";
            return false;
        }
        catch (Exception ex)
        {
            why = ex.Message;
            return false;
        }
    }

    /// <summary>Logs, once per id, that a model id does not resolve (<see cref="ModelFailures"/>, which the
    /// sound models below the table write to as well). Never thrown: a voice that cannot be made is not
    /// made, and the log says which.</summary>
    public static void Fail(string soundId, string what) => ModelFailures.Fail(soundId, what);

    /// <summary>The ids logged so far, with why.</summary>
    public static IReadOnlyDictionary<string, string> Failures => ModelFailures.All;

    // ── The kinds' own keys ─────────────────────────────────────────────────────────────────────

    /// <summary>"rail:&lt;preset&gt;/&lt;train&gt;/&lt;source index&gt;": what the server writes as the SoundId of
    /// each entity of a consist.</summary>
    public static string RailSource(string preset, string train, int index) => IdOf(ModelSynth.Rail, $"{preset}/{train}/{index}");

    /// <summary>Reads what <see cref="RailSource"/> writes.</summary>
    public static bool TryRailSource(string? soundId, out string preset, out string train, out int index)
    {
        preset = ""; train = ""; index = -1;
        if (soundId == null || !soundId.StartsWith("rail:", StringComparison.OrdinalIgnoreCase)) return false;
        var parts = soundId[5..].Split('/');
        if (parts.Length != 3) return false;
        preset = parts[0]; train = parts[1];
        return int.TryParse(parts[2], out index);
    }

    /// <summary>"water:&lt;preset&gt;/&lt;feature&gt;/&lt;tap&gt;": one tap of one feature on a map. A plain
    /// "water:&lt;preset&gt;" is the whole feature at one point and is not a tap.</summary>
    public static bool TryWaterTap(string? soundId, out string preset, out string feature, out int tap)
    {
        preset = ""; feature = ""; tap = -1;
        if (soundId == null || !soundId.StartsWith("water:", StringComparison.OrdinalIgnoreCase)) return false;
        var parts = soundId[6..].Split('/');
        if (parts.Length != 3) return false;
        preset = parts[0]; feature = parts[1];
        return int.TryParse(parts[2], out tap) && tap >= 0;
    }

    // ── The kinds' declarations ─────────────────────────────────────────────────────────────────

    // The server places one entity per source in TrainLayout order; the level is that source's own.
    private static (float, float)? RailLevel(string id)
    {
        if (!TryRailSource(id, out string preset, out _, out int index)) return null;
        var layout = TrainLayout.Sources(TrainProfile.ByName(preset), ModelLibrary.Presets);
        return index >= 0 && index < layout.Count ? (layout[index].LevelDb, layout[index].ExtentMetres) : null;
    }

    // A train's bell renders with the bell's own room, as a crossing's does (TrainLayout).
    private static float? RailHeadroom(string id)
    {
        if (!TryRailSource(id, out string preset, out _, out int index)) return null;
        var layout = TrainLayout.Sources(TrainProfile.ByName(preset), ModelLibrary.Presets);
        return index >= 0 && index < layout.Count ? layout[index].HeadroomDb : null;
    }

    // One tap takes the whole feature's level and its own landing's size: each renders its share against
    // the whole's scale, so the taps sum to the feature at any distance. A bare "water:<preset>" is the whole.
    private static (float, float)? WaterLevel(string id)
    {
        if (TryWaterTap(id, out string preset, out _, out int tap))
        {
            var feature = ModelLibrary.Presets.Water(preset);
            return tap < feature.Taps.Length ? (feature.SourceLevelDb, feature.Taps[tap].ExtentMetres) : null;
        }
        // A whole feature placed once ("water:<preset>/<feature>", heard from its taps): the whole.
        var water = ModelLibrary.Presets.Water(WaterFeatureSpec.TryFeatureKey(id, out string whole, out _) ? whole : id[6..]);
        return (water.SourceLevelDb, water.ExtentMetres);
    }

    private static float? WaterHeadroom(string id)
        => TryWaterTap(id, out string preset, out _, out _) || WaterFeatureSpec.TryFeatureKey(id, out preset, out _)
            ? ModelLibrary.Presets.Water(preset).PeakHeadroomDb
            : ModelLibrary.Presets.Water(id[6..]).PeakHeadroomDb;

    private static (float, float)? WoodLevel(string id)
    {
        if (!WoodChorus.ParseKey(id, out string preset, out float rx, out float rz)) return null;
        return (ModelLibrary.Presets.Foliage(preset).SourceLevelDb, MathF.Max(rx, rz));
    }

    private static float? WoodHeadroom(string id)
        => WoodChorus.ParseKey(id, out string preset, out _, out _) ? ModelLibrary.Presets.Foliage(preset).PeakHeadroomDb : null;

    // The size is what radiates (the disc or the nozzle), which only has to stop the inverse law running
    // away directly underneath; with more than one engine it is their span (11.5 m on a twin), as a bus's
    // length is its size.
    private static (float, float)? AircraftLevel(string id)
    {
        var p = AircraftProfile.ByName(id[9..]);
        float? radiates = p.Propeller?.DiameterMetres ?? p.Turbine?.Fan?.DiameterMetres ?? p.Turbine?.BypassNozzleDiameterMetres;
        if (p.EngineSpanMetres <= 0f && radiates == null) Fail(id, "declares no propeller, fan or nozzle to size it by; sized 2 m");
        float span = p.EngineSpanMetres > 0f ? p.EngineSpanMetres : MathF.Max(2f, radiates ?? 2f);
        return (p.SourceLevelDb, span);
    }
}

/// <summary>A machine's body, as the kind table asks it of the catalogue.</summary>
public sealed partial class Catalogue
{
    /// <summary>How a machine's sound id ("machine:push_mower") moves: it walks if somebody walks behind it.
    /// Memoised, and forgotten when a model changes.</summary>
    internal ModelBody MachineBody(string soundId)
    {
        int generation = Generation;
        if (_bodyGeneration != generation)
        {
            _bodies.Clear();
            _bodyGeneration = generation;
        }
        return _bodies.GetOrAdd(soundId, static (id, self) =>
        {
            try { return self.SmallMachine(id[8..]).WalkedBehind ? ModelBody.Walks : ModelBody.Fixed; }
            catch (Exception ex)
            {
                ModelKindTable.Fail(id, ex.Message);
                return ModelBody.Fixed;
            }
        }, this);
    }

    private readonly ConcurrentDictionary<string, ModelBody> _bodies = new(StringComparer.OrdinalIgnoreCase);
    private volatile int _bodyGeneration = int.MinValue;
}

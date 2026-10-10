using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenFPS.Common;

/// <summary>
/// The kinds of part a machine can be assembled from. Strings, not a type hierarchy, so a map author
/// can name a model in data and nothing in C# is recompiled for it.
/// </summary>
public static class MachineModels
{
    /// <summary>The engine itself. <see cref="MachinePart.Profile"/> is an EngineProfile preset key.
    /// It has no offset of its own: an engine is heard through its outlets and its block.</summary>
    public const string Engine = "engine";

    /// <summary>Where the exhaust gas leaves. An emitter slot.</summary>
    public const string Exhaust = "exhaust";

    /// <summary>Where the engine breathes, and where the block radiates from. An emitter slot.</summary>
    public const string Intake = "intake";

    /// <summary>The tyres. <see cref="MachinePart.Profile"/> is a TyreProfile preset key.</summary>
    public const string Tyres = "tyres";

    /// <summary>The shell the engine is bolted into. Profile is a VehicleBody preset key.</summary>
    public const string Body = "body";

    /// <summary>The gearbox. <see cref="MachinePart.Series"/> is its ratios, first gear first.</summary>
    public const string Gearbox = "gearbox";

    /// <summary>Mass, drag, and where the axles are: how it moves, and so what the sounds do.</summary>
    public const string Chassis = "chassis";

    /// <summary>A bogie: a frame with wheelsets in it. Profile is a rail vehicle in the
    /// <see cref="ModelLibrary"/>, which carries the wheel and where the axles sit.</summary>
    public const string Bogie = "bogie";

    /// <summary>What it runs on. Profile is a track in the <see cref="ModelLibrary"/>: the one part
    /// that belongs to the world, here because what a wheel sounds like is half the rail's doing.</summary>
    public const string Track = "track";

    /// <summary>A prime mover that is not a road engine: a diesel-electric's alternator set, an
    /// electric drive, a steam front end. Profile names which.</summary>
    public const string Traction = "traction";

    /// <summary>An air horn. Profile is a horn in the <see cref="ModelLibrary"/>.</summary>
    public const string Horn = "horn";

    /// <summary>A steam whistle. Profile is a whistle in the <see cref="ModelLibrary"/>.</summary>
    public const string Whistle = "whistle";

    /// <summary>A struck bell — a locomotive bell, a tram gong, a crossing gong on its mast.
    /// Profile is a bell in the <see cref="ModelLibrary"/>.</summary>
    public const string Bell = "bell";

    /// <summary>A compressed-air system: reservoir, governor and the ports that let it out. Profile
    /// is an air system in the <see cref="ModelLibrary"/>.</summary>
    public const string Air = "air";
}

/// <summary>
/// One part of a machine: what kind of thing it is, which one, and where it sits. The same shape for
/// a tailpipe, a rotor and a fountain. <see cref="Settings"/> and <see cref="Series"/> carry the
/// numbers not worth a preset; an absent setting means "whatever the profile said", not zero.
/// </summary>
public sealed record MachinePart
{
    public required string Model { get; init; }

    /// <summary>A preset key within this model's registry, or empty to take the base machine's.</summary>
    public string Profile { get; init; } = "";

    /// <summary>What the part is made of, a name in the <see cref="AcousticRegistry"/>; empty for the
    /// profile's own.</summary>
    public string Material { get; init; } = "";

    /// <summary>Where this part sits relative to the machine's origin, in its own frame
    /// (x right, y up, z forward). The emission point, for a part that emits.</summary>
    public Vector3 At { get; init; }

    /// <summary>How big this source is, metres; zero is a point. The mixer can place an extended source
    /// (<see cref="Loudness.Place(float, float)"/>).</summary>
    // TODO: nothing reads it yet; it should replace ClientAudioSystem's car-sized MathF.Max(reference, 3f), with audibility ranking.
    public float ExtentMetres { get; init; }

    /// <summary>What this part measures at one metre, dB SPL, where that is a property of the part
    /// rather than of the whole machine. Zero means "the machine's own level covers it".</summary>
    public float LevelDb { get; init; }

    /// <summary>Scalars this model needs. Case-insensitive; an absent key means "unchanged".</summary>
    public IReadOnlyDictionary<string, float> Settings { get; init; } = EmptySettings;

    /// <summary>A list of numbers: a gearbox's ratios, a body's panel spans.</summary>
    public IReadOnlyList<float> Series { get; init; } = Array.Empty<float>();

    internal static readonly IReadOnlyDictionary<string, float> EmptySettings =
        new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

    public bool Has(string key) => Settings.ContainsKey(key);
    public float Get(string key, float fallback) => Settings.TryGetValue(key, out float v) ? v : fallback;
    public int Get(string key, int fallback) => Settings.TryGetValue(key, out float v) ? (int)MathF.Round(v) : fallback;
}

/// <summary>
/// A machine as a parts list: data a map author can write (a JSON file next to the maps), where
/// <see cref="VehicleProfile.Presets"/> can only be named.
/// </summary>
public sealed record MachineDefinition
{
    public required string Id { get; init; }
    public string Name { get; init; } = "";

    /// <summary>A machine this one starts from (the same car with a different exhaust), or empty to
    /// build from parts alone.</summary>
    public string Base { get; init; } = "";

    public IReadOnlyList<MachinePart> Parts { get; init; } = Array.Empty<MachinePart>();

    /// <summary>The first part of a model, or null. Machines are small; a scan is cheaper than a map.</summary>
    public MachinePart? Part(string model)
    {
        for (int i = 0; i < Parts.Count; i++)
            if (string.Equals(Parts[i].Model, model, StringComparison.OrdinalIgnoreCase)) return Parts[i];
        return null;
    }
}

/// <summary>
/// Machines by name: the built-in library, plus whatever a map's author has written.
/// <see cref="Describe"/> and <see cref="Assemble"/> are inverses: every built-in preset survives the
/// round trip unchanged (MachineTests), which proves the parts vocabulary holds the whole library.
/// </summary>
public static class MachineRegistry
{
    private static volatile Dictionary<string, MachineDefinition> _authored =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _loadLock = new();

    /// <summary>What has been loaded from data, by id.</summary>
    public static IReadOnlyDictionary<string, MachineDefinition> Authored => _authored;

    /// <summary>
    /// Reads every <c>*.json</c> in a directory as a machine definition. Additive and never fatal: a
    /// file that will not parse is logged past. A fresh dictionary is published whole, so the audio
    /// thread never sees a reload half done.
    /// </summary>
    public static int Load(string directory)
    {
        if (!Directory.Exists(directory)) return 0;
        lock (_loadLock)
        {
            var next = new Dictionary<string, MachineDefinition>(_authored, StringComparer.OrdinalIgnoreCase);
            int loaded = 0;
            foreach (string file in Directory.EnumerateFiles(directory, "*.json"))
            {
                try
                {
                    var dto = JsonSerializer.Deserialize<MachineDto>(File.ReadAllText(file), JsonOptions);
                    if (dto == null) continue;
                    string id = dto.Id ?? Path.GetFileNameWithoutExtension(file);
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    next[id] = dto.ToDefinition(id);
                    loaded++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WARN] MachineRegistry: {Path.GetFileName(file)} is not a machine: {ex.Message}");
                }
            }
            _authored = next;
            _assembled.Clear();
            return loaded;
        }
    }

    /// <summary>Loads the authored library once, from the folder next to the maps (relative to the
    /// working directory, as prefabs/ is). Server, client and lab all call it: they must agree on what
    /// a machine name means.</summary>
    public static void EnsureLoaded(string directory = "machines")
    {
        if (_loadedFrom == directory) return;
        lock (_loadLock) { if (_loadedFrom == directory) return; }
        Load(directory);
        _loadedFrom = directory;
    }

    private static volatile string? _loadedFrom;

    /// <summary>Forgets everything loaded from data. For tests, and for a map change.</summary>
    public static void Clear()
    {
        lock (_loadLock)
        {
            _authored = new Dictionary<string, MachineDefinition>(StringComparer.OrdinalIgnoreCase);
            _assembled.Clear();
            _loadedFrom = null;
        }
    }

    /// <summary>Adds a definition directly, as a map or a test would.</summary>
    public static void Add(MachineDefinition def)
    {
        lock (_loadLock)
        {
            var next = new Dictionary<string, MachineDefinition>(_authored, StringComparer.OrdinalIgnoreCase)
            { [def.Id] = def };
            _authored = next;
            _assembled.Clear();
        }
    }

    /// <summary>A machine by name: an authored one first (so a map can replace a car in the library),
    /// otherwise the built-in of that name taken apart into its parts.</summary>
    public static MachineDefinition? Find(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        // Changed in the world editor: the vehicle as it is now, taken apart.
        if (ModelLibrary.IsAuthored(ModelLibrary.Kinds.Vehicle, id)) return Describe(VehicleFor(id), id);
        if (_authored.TryGetValue(id, out var authored)) return authored;
        if (VehicleProfile.Presets.ContainsKey(id)) return Describe(VehicleProfile.ByName(id), id);
        return null;
    }

    /// <summary>Is there a machine of this name at all — authored, or in the built-in library?</summary>
    public static bool Knows(string id)
        => !string.IsNullOrEmpty(id) && (_authored.ContainsKey(id) || VehicleProfile.Presets.ContainsKey(id)
                                         || ModelLibrary.IsAuthored(ModelLibrary.Kinds.Vehicle, id));

    /// <summary>Every machine there is, built-in and authored, and those made in the world editor.</summary>
    public static IEnumerable<string> Ids
        => VehicleProfile.Presets.Keys.Concat(_authored.Keys.Where(k => !VehicleProfile.Presets.ContainsKey(k)))
               .Concat(ModelLibrary.Ids(ModelLibrary.Kinds.Vehicle)
                   .Where(k => !VehicleProfile.Presets.ContainsKey(k) && !_authored.ContainsKey(k)));

    /// <summary>The vehicle a machine names, assembled. Memoised, as <see cref="VehicleProfile.ByName"/>
    /// is: the audio path asks per car per frame, and assembling one builds a whole engine.</summary>
    public static VehicleProfile VehicleFor(string id)
    {
        // A model changed in the world editor (a vehicle, or an engine some vehicle has) makes every
        // assembled vehicle stale: forgotten, and built again as each is next asked for.
        int generation = ModelLibrary.Generation;
        if (generation != _seenGeneration)
        {
            _assembled.Clear();
            _seenGeneration = generation;
        }
        return _assembled.GetOrAdd(id, static k =>
        {
            if (ModelLibrary.IsAuthored(ModelLibrary.Kinds.Vehicle, k))
            {
                var spec = ModelLibrary.Get<VehicleSpec>(ModelLibrary.Kinds.Vehicle, k);
                // Built on itself it starts from the unedited vehicle, which never asks for this one
                // again; marked as assembling, a parts list's own base check took it for a circle.
                if (spec.Base.Length == 0 || spec.Base.Equals(k, StringComparison.OrdinalIgnoreCase)) return spec.Build(k);
                _assembling ??= new List<string>();
                if (_assembling.Contains(k, StringComparer.OrdinalIgnoreCase))
                    throw new ArgumentException($"Vehicles are built on each other in a circle: {string.Join(" -> ", _assembling)} -> {k}.");
                _assembling.Add(k);
                try { return spec.Build(k); }
                finally { _assembling.Remove(k); }
            }
            return WithEditedEngine(Unedited(k));
        });
    }

    private static volatile int _seenGeneration = int.MinValue;

    /// <summary>A vehicle as it is without the world editor (a vehicle model's version 0): an authored
    /// parts list assembled, or the built-in preset.</summary>
    public static VehicleProfile Unedited(string id)
    {
        if (_authored.TryGetValue(id, out var def)) return Assemble(def);
        if (VehicleProfile.Presets.ContainsKey(id)) return VehicleProfile.ByName(id);
        throw new ArgumentException($"No machine '{id}'.");
    }

    /// <summary>An engine by its preset name, as changed in the world editor if it has been.</summary>
    public static EngineProfile EngineFor(string key)
        => ModelLibrary.IsAuthored(ModelLibrary.Kinds.Engine, key)
            ? ModelLibrary.Get<EngineProfile>(ModelLibrary.Kinds.Engine, key)
            : EngineProfile.ByName(key);

    /// <summary>A vehicle with its engine as changed in the world editor, if it has been.</summary>
    private static VehicleProfile WithEditedEngine(VehicleProfile v)
    {
        string key = EngineKeyOf(v.Engine);
        return key.Length > 0 && ModelLibrary.IsAuthored(ModelLibrary.Kinds.Engine, key) ? v with { Engine = EngineFor(key) } : v;
    }

    /// <summary>The engine preset a vehicle (by id) is built on, or "".</summary>
    public static string EngineKeyFor(string vehicleId)
    {
        try
        {
            if (ModelLibrary.IsAuthored(ModelLibrary.Kinds.Vehicle, vehicleId))
                return ModelLibrary.Get<VehicleSpec>(ModelLibrary.Kinds.Vehicle, vehicleId).Engine;
            return Knows(vehicleId) ? EngineKeyOf(Unedited(vehicleId).Engine) : "";
        }
        catch (ArgumentException) { return ""; }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, VehicleProfile> _assembled =
        new(StringComparer.OrdinalIgnoreCase);

    // ── Parts → machine ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the vehicle a parts list describes. It starts as the whole base vehicle and each part
    /// overrides only what it names; what has no part (the air system, the siren, the fan, where the
    /// engine is) comes with the base. With no base, the parts must carry an engine.
    /// </summary>
    public static VehicleProfile Assemble(MachineDefinition def)
    {
        VehicleProfile? b = ResolveBase(def);

        var enginePart = def.Part(MachineModels.Engine);
        EngineProfile engine = enginePart is { Profile.Length: > 0 }
            ? EngineFor(enginePart.Profile)
            : b?.Engine ?? throw new ArgumentException(
                $"Machine '{def.Id}' has no engine and no base to take one from.");

        var gearboxPart = def.Part(MachineModels.Gearbox);
        Gearbox gearbox = b?.Gearbox ?? Gearbox.SixSpeedSports;
        if (gearboxPart != null)
        {
            if (gearboxPart.Series.Count > 0) gearbox = gearbox with { Ratios = gearboxPart.Series.ToArray() };
            gearbox = gearbox with
            {
                FinalDrive = gearboxPart.Get("finalDrive", gearbox.FinalDrive),
                WheelRadiusMetres = gearboxPart.Get("wheelRadius", gearbox.WheelRadiusMetres),
                ShiftSeconds = gearboxPart.Get("shiftSeconds", gearbox.ShiftSeconds),
                UpshiftRpm = gearboxPart.Get("upshiftRpm", gearbox.UpshiftRpm),
                DownshiftRpm = gearboxPart.Get("downshiftRpm", gearbox.DownshiftRpm),
            };
        }

        var tyrePart = def.Part(MachineModels.Tyres);
        TyreProfile tyres = b?.Tyres ?? TyreProfile.SportsOnAsphalt;
        if (tyrePart != null)
        {
            if (tyrePart.Profile.Length > 0) tyres = TyreProfile.ByName(tyrePart.Profile);
            tyres = tyres with
            {
                TreadBlocks = tyrePart.Get("treadBlocks", tyres.TreadBlocks),
                SurfaceRoughness = tyrePart.Get("surfaceRoughness", tyres.SurfaceRoughness),
                ReferenceDb = tyrePart.Get("referenceDb", tyres.ReferenceDb),
                PeakGripG = tyrePart.Get("peakGripG", tyres.PeakGripG),
                SquealHz = tyrePart.Get("squealHz", tyres.SquealHz),
                SquealQ = tyrePart.Get("squealQ", tyres.SquealQ),
                SquealDb = tyrePart.Get("squealDb", tyres.SquealDb),
                InflationKPa = tyrePart.Get("inflationKPa", tyres.InflationKPa),
                TreadDepthMm = tyrePart.Get("treadDepthMm", tyres.TreadDepthMm),
            };
        }

        var bodyPart = def.Part(MachineModels.Body);
        VehicleBody body = b?.Body ?? VehicleBody.Saloon;
        if (bodyPart != null)
        {
            if (bodyPart.Profile.Length > 0) body = VehicleBody.ByName(bodyPart.Profile);
            if (bodyPart.Series.Count > 0) body = body with { PanelSpansM = bodyPart.Series.ToArray() };
            body = body with
            {
                PanelMaterial = bodyPart.Material.Length > 0 ? bodyPart.Material : body.PanelMaterial,
                PanelThicknessM = bodyPart.Get("panelThickness", body.PanelThicknessM),
                PanelLoss = bodyPart.Get("panelLoss", body.PanelLoss),
                Coupling = bodyPart.Get("coupling", body.Coupling),
                CabinLengthM = bodyPart.Get("cabinLength", body.CabinLengthM),
                CabinWidthM = bodyPart.Get("cabinWidth", body.CabinWidthM),
                CabinHeightM = bodyPart.Get("cabinHeight", body.CabinHeightM),
                CabinAbsorption = bodyPart.Get("cabinAbsorption", body.CabinAbsorption),
                CabinLeak = bodyPart.Get("cabinLeak", body.CabinLeak),
                SealLeak = bodyPart.Get("sealLeak", body.SealLeak),
                StarterPathLossDb = bodyPart.Get("starterPathLossDb", body.StarterPathLossDb),
                WindNoiseDbAt110 = bodyPart.Get("windNoiseDbAt110", body.WindNoiseDbAt110),
                SealedBox = bodyPart.Get("sealedBox", body.SealedBox ? 1f : 0f) > 0.5f,
                MaxModes = bodyPart.Get("maxModes", body.MaxModes),
            };
        }

        var chassis = def.Part(MachineModels.Chassis);
        var exhaust = def.Part(MachineModels.Exhaust);
        var intake = def.Part(MachineModels.Intake);
        // A part written without "at" has no place of its own (see ToJson): the base's stands.
        Vector3? exhaustAt = exhaust is { At: var ea } && ea != Vector3.Zero ? ea : null;
        Vector3? intakeAt = intake is { At: var ia } && ia != Vector3.Zero ? ia : null;

        // The whole base, or with none a vehicle's defaults; the running gear comes with the base,
        // its wheels placed where this machine's axles are.
        var start = b ?? new VehicleProfile { Name = def.Id, Engine = engine, Gearbox = gearbox, Tyres = tyres };
        return start with
        {
            Name = def.Name.Length > 0 ? def.Name : start.Name,
            EngineKey = def.Id,
            Engine = engine,
            Gearbox = gearbox,
            Tyres = tyres,
            Body = body,
            MassKg = chassis?.Get("massKg", start.MassKg) ?? start.MassKg,
            DragArea = chassis?.Get("dragArea", start.DragArea) ?? start.DragArea,
            RollingResistance = chassis?.Get("rollingResistance", start.RollingResistance) ?? start.RollingResistance,
            FrontAxleZ = chassis?.Get("frontAxleZ", start.FrontAxleZ) ?? start.FrontAxleZ,
            RearAxleZ = chassis?.Get("rearAxleZ", start.RearAxleZ) ?? start.RearAxleZ,
            LengthMetres = chassis?.Get("lengthMetres", start.LengthMetres) ?? start.LengthMetres,
            WidthMetres = chassis?.Get("widthMetres", start.WidthMetres) ?? start.WidthMetres,
            HeightMetres = chassis?.Get("heightMetres", start.HeightMetres) ?? start.HeightMetres,
            // The exhaust's own level is the machine's, as Describe writes it; the chassis setting,
            // where there is one, says so outright.
            SourceLevelDb = chassis?.Has("sourceLevelDb") == true ? chassis.Get("sourceLevelDb", start.SourceLevelDb)
                          : exhaust is { LevelDb: > 0f } ? exhaust.LevelDb : start.SourceLevelDb,
            TyreCount = chassis?.Get("tyreCount", start.TyreCount) ?? start.TyreCount,
            ExhaustOffsetX = exhaustAt?.X ?? start.ExhaustOffsetX,
            ExhaustHeight = exhaustAt?.Y ?? start.ExhaustHeight,
            ExhaustOffsetZ = exhaustAt?.Z ?? start.ExhaustOffsetZ,
            IntakeHeight = intakeAt?.Y ?? start.IntakeHeight,
            IntakeOffsetZ = intakeAt?.Z ?? start.IntakeOffsetZ,
        };
    }

    /// <summary>
    /// The machine a definition starts from. A machine based on itself means the built-in of that
    /// name ("v8_sports, but heavier"): read as the authored one it recursed for ever and hung the test
    /// run. A deeper circle (a on b on a) is an authoring error and says so.
    /// </summary>
    private static VehicleProfile? ResolveBase(MachineDefinition def)
    {
        if (string.IsNullOrEmpty(def.Base)) return null;
        if (string.Equals(def.Base, def.Id, StringComparison.OrdinalIgnoreCase))
            return VehicleProfile.Presets.ContainsKey(def.Base) ? VehicleProfile.ByName(def.Base)
                 : throw new ArgumentException($"Machine '{def.Id}' is based on itself and there is no built-in of that name.");

        _assembling ??= new List<string>();
        if (_assembling.Contains(def.Id, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"Machines are based on each other in a circle: {string.Join(" -> ", _assembling)} -> {def.Id}.");
        _assembling.Add(def.Id);
        try { return VehicleFor(def.Base); }
        finally { _assembling.Remove(def.Id); }
    }

    [ThreadStatic] private static List<string>? _assembling;

    // ── Machine → parts ─────────────────────────────────────────────────────────────────────────

    /// <summary>The same vehicle taken apart into parts: how the built-in library is exported to data.
    /// The inverse of <see cref="Assemble"/>.</summary>
    public static MachineDefinition Describe(VehicleProfile v, string id)
    {
        var parts = new List<MachinePart>
        {
            new() { Model = MachineModels.Engine, Profile = EngineKeyOf(v.Engine) },
            new()
            {
                Model = MachineModels.Exhaust,
                At = v.ExhaustSlot,
                LevelDb = v.SourceLevelDb,
            },
            new() { Model = MachineModels.Intake, At = new Vector3(0f, v.IntakeHeight, v.IntakeOffsetZ) },
            new()
            {
                Model = MachineModels.Tyres,
                Profile = TyreKeyOf(v.Tyres),
                Settings = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
                {
                    ["treadBlocks"] = v.Tyres.TreadBlocks,
                    ["surfaceRoughness"] = v.Tyres.SurfaceRoughness,
                    ["referenceDb"] = v.Tyres.ReferenceDb,
                    ["peakGripG"] = v.Tyres.PeakGripG,
                    ["squealHz"] = v.Tyres.SquealHz,
                    ["squealQ"] = v.Tyres.SquealQ,
                    ["squealDb"] = v.Tyres.SquealDb,
                    ["inflationKPa"] = v.Tyres.InflationKPa,
                    ["treadDepthMm"] = v.Tyres.TreadDepthMm,
                },
            },
            new()
            {
                Model = MachineModels.Body,
                Profile = BodyKeyOf(v.Body),
                Material = v.Body?.PanelMaterial ?? "",
                Series = v.Body?.PanelSpansM ?? Array.Empty<float>(),
                Settings = v.Body == null ? MachinePart.EmptySettings
                    : new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["panelThickness"] = v.Body.PanelThicknessM,
                        ["panelLoss"] = v.Body.PanelLoss,
                        ["coupling"] = v.Body.Coupling,
                        ["cabinLength"] = v.Body.CabinLengthM,
                        ["cabinWidth"] = v.Body.CabinWidthM,
                        ["cabinHeight"] = v.Body.CabinHeightM,
                        ["cabinAbsorption"] = v.Body.CabinAbsorption,
                        ["cabinLeak"] = v.Body.CabinLeak,
                        ["sealLeak"] = v.Body.SealLeak,
                        ["starterPathLossDb"] = v.Body.StarterPathLossDb,
                        ["windNoiseDbAt110"] = v.Body.WindNoiseDbAt110,
                        ["sealedBox"] = v.Body.SealedBox ? 1f : 0f,
                        ["maxModes"] = v.Body.MaxModes,
                    },
            },
            new()
            {
                Model = MachineModels.Gearbox,
                Series = v.Gearbox.Ratios,
                Settings = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
                {
                    ["finalDrive"] = v.Gearbox.FinalDrive,
                    ["wheelRadius"] = v.Gearbox.WheelRadiusMetres,
                    ["shiftSeconds"] = v.Gearbox.ShiftSeconds,
                    ["upshiftRpm"] = v.Gearbox.UpshiftRpm,
                    ["downshiftRpm"] = v.Gearbox.DownshiftRpm,
                },
            },
            new()
            {
                Model = MachineModels.Chassis,
                Settings = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
                {
                    ["massKg"] = v.MassKg,
                    ["dragArea"] = v.DragArea,
                    ["rollingResistance"] = v.RollingResistance,
                    ["frontAxleZ"] = v.FrontAxleZ,
                    ["rearAxleZ"] = v.RearAxleZ,
                    ["lengthMetres"] = v.LengthMetres,
                    ["widthMetres"] = v.WidthMetres,
                    ["heightMetres"] = v.HeightMetres,
                    ["sourceLevelDb"] = v.SourceLevelDb,
                    ["tyreCount"] = v.TyreCount,
                },
            },
        };
        return new MachineDefinition { Id = id, Name = v.Name, Parts = parts };
    }

    /// <summary>
    /// Which engine preset this is, found by the engine's Name, which a test holds unique. A vehicle's
    /// own EngineKey is the vehicle's key, not always the engine's (the school bus runs "diesel_bus").
    /// </summary>
    public static string EngineKeyOf(EngineProfile engine)
    {
        var byName = _engineKeys ??= EngineProfile.Presets.ToDictionary(kv => kv.Value().Name, kv => kv.Key);
        if (byName.TryGetValue(engine.Name, out var key)) return key;
        // An engine changed in the world editor may have been renamed: the edited one, by its name.
        foreach (var id in ModelLibrary.Ids(ModelLibrary.Kinds.Engine))
            if (ModelLibrary.IsAuthored(ModelLibrary.Kinds.Engine, id)
                && ModelLibrary.Get<EngineProfile>(ModelLibrary.Kinds.Engine, id).Name == engine.Name) return id;
        return "";
    }

    private static Dictionary<string, string>? _engineKeys;

    /// <summary>Which tyre this is, by record equality (all scalars). A miss costs nothing: the
    /// settings carry every field anyway.</summary>
    private static string TyreKeyOf(TyreProfile t)
    {
        foreach (var kv in TyreProfile.Presets) if (kv.Value() == t) return kv.Key;
        return "";
    }

    /// <summary>Which body this is. Not by record equality: a record compares its panel-span array by
    /// reference, so two identical bodies are unequal; the spans are compared as numbers.</summary>
    private static string BodyKeyOf(VehicleBody? body)
    {
        if (body == null) return "";
        foreach (var kv in VehicleBody.Presets)
        {
            var b = kv.Value();
            if (b.PanelSpansM.AsSpan().SequenceEqual(body.PanelSpansM)
                && b with { PanelSpansM = body.PanelSpansM } == body) return kv.Key;
        }
        return "";
    }

    // ── Data ────────────────────────────────────────────────────────────────────────────────────

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>A machine on disk. Nullable throughout: an omitted field means "unchanged".</summary>
    internal sealed class MachineDto
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Base { get; set; }
        public List<PartDto>? Parts { get; set; }

        public MachineDefinition ToDefinition(string id) => new()
        {
            Id = id,
            Name = Name ?? "",
            Base = Base ?? "",
            Parts = (Parts ?? new List<PartDto>()).Select(p => p.ToPart()).ToList(),
        };
    }

    internal sealed class PartDto
    {
        public string? Model { get; set; }
        public string? Profile { get; set; }
        public string? Material { get; set; }
        /// <summary>[x, y, z], metres, in the machine's own frame.</summary>
        public float[]? At { get; set; }
        public float? Extent { get; set; }
        public float? LevelDb { get; set; }
        public Dictionary<string, float>? Settings { get; set; }
        public float[]? Series { get; set; }

        public MachinePart ToPart() => new()
        {
            Model = Model ?? throw new JsonException("a part with no model"),
            Profile = Profile ?? "",
            Material = Material ?? "",
            At = At is { Length: >= 3 } ? new Vector3(At[0], At[1], At[2]) : Vector3.Zero,
            ExtentMetres = Extent ?? 0f,
            LevelDb = LevelDb ?? 0f,
            Settings = Settings != null
                ? new Dictionary<string, float>(Settings, StringComparer.OrdinalIgnoreCase)
                : MachinePart.EmptySettings,
            Series = Series ?? Array.Empty<float>(),
        };
    }

    /// <summary>A definition as it would be written on disk.</summary>
    public static string ToJson(MachineDefinition def)
    {
        var dto = new MachineDto
        {
            Id = def.Id,
            Name = def.Name.Length > 0 ? def.Name : null,
            Base = def.Base.Length > 0 ? def.Base : null,
            Parts = def.Parts.Select(p => new PartDto
            {
                Model = p.Model,
                Profile = p.Profile.Length > 0 ? p.Profile : null,
                Material = p.Material.Length > 0 ? p.Material : null,
                At = p.At == Vector3.Zero ? null : new[] { p.At.X, p.At.Y, p.At.Z },
                Extent = p.ExtentMetres > 0f ? p.ExtentMetres : null,
                LevelDb = p.LevelDb > 0f ? p.LevelDb : null,
                Settings = p.Settings.Count > 0 ? new Dictionary<string, float>(p.Settings) : null,
                Series = p.Series.Count > 0 ? p.Series.ToArray() : null,
            }).ToList(),
        };
        return JsonSerializer.Serialize(dto, JsonOptions);
    }

    /// <summary>Reads one machine from JSON text — the other half of <see cref="ToJson"/>.</summary>
    public static MachineDefinition FromJson(string json, string fallbackId = "")
    {
        var dto = JsonSerializer.Deserialize<MachineDto>(json, JsonOptions)
                  ?? throw new JsonException("empty machine");
        return dto.ToDefinition(dto.Id ?? fallbackId);
    }
}

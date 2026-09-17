using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenFPS.Common;

/// <summary>
/// The kinds of part a machine can be assembled from.
///
/// A vocabulary rather than a type hierarchy, because the list is going to grow — a rotor, a
/// turbine, a fountain's jet — and every one of those is "a model, a profile for it, and where it
/// sits". Strings keep that open to data: a map author writes the model's name and nothing in C#
/// has to be recompiled for a machine that uses it.
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

    /// <summary>Mass, drag, and where the axles are — everything about the machine that is not a
    /// sound but decides how it moves, and therefore what the sounds do.</summary>
    public const string Chassis = "chassis";
}

/// <summary>
/// One part of a machine: what kind of thing it is, which one, and where it sits.
///
/// The shape is deliberately the same for a tailpipe, a rotor and a fountain — a model, a profile
/// within that model, an offset in the machine's own frame, and how big a source it is. That is what
/// lets a helicopter, a bus and a tree be the same kind of thing rather than three subsystems.
///
/// <see cref="Settings"/> and <see cref="Series"/> carry the numbers a model needs that are not
/// worth a preset of their own: a gearbox's ratios are a list, a chassis is half a dozen scalars.
/// A setting that is absent is not zero — it means "whatever the profile said", which is what makes
/// a machine definition able to say "a school bus, but with open pipes" in three lines.
/// </summary>
public sealed record MachinePart
{
    public required string Model { get; init; }

    /// <summary>A preset key within this model's registry, or empty to take the base machine's.</summary>
    public string Profile { get; init; } = "";

    /// <summary>What the part is made of — a name in the <see cref="AcousticRegistry"/>. Empty means
    /// the profile's own. A body's panels, an airframe's skin and a fountain's basin are all this.</summary>
    public string Material { get; init; } = "";

    /// <summary>Where this part sits relative to the machine's origin, in its own frame
    /// (x right, y up, z forward). The emission point, for a part that emits.</summary>
    public Vector3 At { get; init; }

    /// <summary>
    /// How big this source is, metres. Zero means a point.
    ///
    /// Carried here because a source's size is a property of the source — a 40 m airliner and a
    /// tailpipe are not the same thing at ten metres, and the crowd already proves the mixer can
    /// place an extended source (<see cref="Loudness.Place(float, float)"/>). Nothing consumes it
    /// yet: replacing ClientAudioSystem's car-sized `MathF.Max(reference, 3f)` with it is the next
    /// step, where extent and audibility ranking are done together.
    /// </summary>
    public float ExtentMetres { get; init; }

    /// <summary>What this part measures at one metre, dB SPL, where that is a property of the part
    /// rather than of the whole machine. Zero means "the machine's own level covers it".</summary>
    public float LevelDb { get; init; }

    /// <summary>Scalars this model needs. Case-insensitive; an absent key means "unchanged".</summary>
    public IReadOnlyDictionary<string, float> Settings { get; init; } = EmptySettings;

    /// <summary>The one thing a part can need that is a LIST of numbers: a gearbox's ratios, a
    /// body's panel spans.</summary>
    public IReadOnlyList<float> Series { get; init; } = Array.Empty<float>();

    internal static readonly IReadOnlyDictionary<string, float> EmptySettings =
        new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

    public bool Has(string key) => Settings.ContainsKey(key);
    public float Get(string key, float fallback) => Settings.TryGetValue(key, out float v) ? v : fallback;
    public int Get(string key, int fallback) => Settings.TryGetValue(key, out float v) ? (int)MathF.Round(v) : fallback;
}

/// <summary>
/// A machine, as a parts list.
///
/// This is the thing a map can name and — the point of it — a map author can WRITE. Until now the
/// composition of a vehicle lived in C#: <see cref="VehicleProfile.Presets"/> is a dictionary of
/// factory functions, so a map could say "nascar_v8" and could not say "that engine, in that body,
/// with the pipes out of the side". A definition is data, so it can come from a JSON file next to
/// the maps, and the same structure describes a helicopter (a turbine and two rotors) or a fountain.
/// </summary>
public sealed record MachineDefinition
{
    public required string Id { get; init; }
    public string Name { get; init; } = "";

    /// <summary>
    /// A machine this one starts from, or empty to build from parts alone.
    ///
    /// Most authored machines are a variation on something that exists — the same car with a
    /// different exhaust, the same bus without a silencer — and saying so is both shorter and more
    /// honest than restating every number. It is also how the built-in library stays the library:
    /// an author's machine can lean on it instead of copying it.
    /// </summary>
    public string Base { get; init; } = "";

    public IReadOnlyList<MachinePart> Parts { get; init; } = Array.Empty<MachinePart>();

    /// <summary>The first part of a model, or null. Machines are small; a scan is cheaper than a map.</summary>
    public MachinePart? Part(string model)
    {
        for (int i = 0; i < Parts.Count; i++)
            if (string.Equals(Parts[i].Model, model, StringComparison.OrdinalIgnoreCase)) return Parts[i];
        return null;
    }

    /// <summary>Every part of a model — a machine may have several of a kind (two rotors, four
    /// tailpipes), and anything placing emitters wants all of them.</summary>
    public IEnumerable<MachinePart> AllParts(string model)
        => Parts.Where(p => string.Equals(p.Model, model, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Machines by name: the built-in library, plus whatever a map's author has written.
///
/// <see cref="Describe"/> and <see cref="Assemble"/> are the two directions of one translation, and
/// the round trip is the test that the vocabulary above is actually sufficient: every built-in
/// vehicle preset must survive being taken apart into parts and put back together unchanged
/// (MachineTests). That is what makes it safe for an authored machine to use the same parts.
/// </summary>
public static class MachineRegistry
{
    private static volatile Dictionary<string, MachineDefinition> _authored =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _loadLock = new();

    /// <summary>What has been loaded from data, by id.</summary>
    public static IReadOnlyDictionary<string, MachineDefinition> Authored => _authored;

    /// <summary>
    /// Reads every <c>*.json</c> in a directory as a machine definition.
    ///
    /// Additive, and never fatal: a machine that will not parse is logged past and the rest load,
    /// because one bad file in an author's folder should not take a map's whole field of cars with
    /// it. Built the same way as AcousticRegistry — build a fresh dictionary and publish it — so a
    /// reload cannot be observed half-done by the audio thread.
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

    /// <summary>
    /// Loads the authored library once, from the well-known folder next to the maps.
    ///
    /// Called by whatever starts up — server, client, lab — because all three have to agree about
    /// what a machine name means. They resolve it relative to their own working directory, the same
    /// way materials.json and prefabs/ already do.
    /// </summary>
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

    /// <summary>
    /// A machine by name — an authored one if there is one, otherwise the built-in of that name
    /// taken apart into its parts.
    ///
    /// Authored first on purpose: it is how a map replaces a car in the library without editing the
    /// library, and how the library itself can eventually move out of C# a machine at a time.
    /// </summary>
    public static MachineDefinition? Find(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        if (_authored.TryGetValue(id, out var authored)) return authored;
        if (VehicleProfile.Presets.ContainsKey(id)) return Describe(VehicleProfile.ByName(id), id);
        return null;
    }

    /// <summary>Is there a machine of this name at all — authored, or in the built-in library?</summary>
    public static bool Knows(string id)
        => !string.IsNullOrEmpty(id) && (_authored.ContainsKey(id) || VehicleProfile.Presets.ContainsKey(id));

    /// <summary>Every machine there is, built-in and authored.</summary>
    public static IEnumerable<string> Ids
        => VehicleProfile.Presets.Keys.Concat(_authored.Keys.Where(k => !VehicleProfile.Presets.ContainsKey(k)));

    /// <summary>
    /// The vehicle a machine names, assembled — authored parts if the machine is authored, and the
    /// built-in preset otherwise.
    ///
    /// Memoised for the same reason <see cref="VehicleProfile.ByName"/> is: this is asked on the
    /// audio path, per car per frame, and assembling one builds a whole engine.
    /// </summary>
    public static VehicleProfile VehicleFor(string id)
        => _assembled.GetOrAdd(id, static k =>
        {
            var def = Find(k) ?? throw new ArgumentException($"No machine '{k}'.");
            return _authored.ContainsKey(k) ? Assemble(def) : VehicleProfile.ByName(k);
        });

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, VehicleProfile> _assembled =
        new(StringComparer.OrdinalIgnoreCase);

    // ── Parts → machine ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the vehicle a parts list describes.
    ///
    /// Every part is an OVERRIDE of what the base machine already had, which is why a definition can
    /// be three lines. With no base, the parts have to carry an engine between them — the one thing
    /// a vehicle cannot be assembled without.
    /// </summary>
    public static VehicleProfile Assemble(MachineDefinition def)
    {
        VehicleProfile? b = ResolveBase(def);

        var enginePart = def.Part(MachineModels.Engine);
        EngineProfile engine = enginePart is { Profile.Length: > 0 }
            ? EngineProfile.ByName(enginePart.Profile)
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
                SealedBox = bodyPart.Get("sealedBox", body.SealedBox ? 1f : 0f) > 0.5f,
                MaxModes = bodyPart.Get("maxModes", body.MaxModes),
            };
        }

        var chassis = def.Part(MachineModels.Chassis);
        var exhaust = def.Part(MachineModels.Exhaust);
        var intake = def.Part(MachineModels.Intake);

        var v = new VehicleProfile
        {
            Name = def.Name.Length > 0 ? def.Name : b?.Name ?? def.Id,
            EngineKey = def.Id,
            Engine = engine,
            Gearbox = gearbox,
            Tyres = tyres,
            Body = body,
            MassKg = chassis?.Get("massKg", b?.MassKg ?? 1620f) ?? b?.MassKg ?? 1620f,
            DragArea = chassis?.Get("dragArea", b?.DragArea ?? 0.62f) ?? b?.DragArea ?? 0.62f,
            RollingResistance = chassis?.Get("rollingResistance", b?.RollingResistance ?? 0.013f)
                              ?? b?.RollingResistance ?? 0.013f,
            FrontAxleZ = chassis?.Get("frontAxleZ", b?.FrontAxleZ ?? 1.25f) ?? b?.FrontAxleZ ?? 1.25f,
            RearAxleZ = chassis?.Get("rearAxleZ", b?.RearAxleZ ?? -1.35f) ?? b?.RearAxleZ ?? -1.35f,
            SourceLevelDb = chassis?.Get("sourceLevelDb", b?.SourceLevelDb ?? 116f) ?? b?.SourceLevelDb ?? 116f,
            ExhaustOffsetZ = exhaust?.At.Z ?? b?.ExhaustOffsetZ ?? -2.05f,
            ExhaustHeight = exhaust?.At.Y ?? b?.ExhaustHeight ?? 0.3f,
            IntakeOffsetZ = intake?.At.Z ?? b?.IntakeOffsetZ ?? 1.35f,
            IntakeHeight = intake?.At.Y ?? b?.IntakeHeight ?? 0.7f,
        };
        return v;
    }

    /// <summary>
    /// The machine a definition starts from.
    ///
    /// A machine may name ITSELF as its base, and that is the override idiom rather than a mistake:
    /// "v8_sports, but heavier" is written as a machine called v8_sports based on v8_sports, and it
    /// means the BUILT-IN of that name. Reading it as the authored one is an infinite regress — the
    /// first thing the test folder did was hang the test run.
    ///
    /// Anything deeper (a based on b based on a) is a genuine authoring error and says so, rather
    /// than filling a stack.
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

    /// <summary>
    /// The same vehicle, taken apart: the parts list a built-in preset already IS.
    ///
    /// Nothing is invented here. A vehicle has always been a rig — an exhaust three metres behind an
    /// intake, a body, a set of tyres — and this only says so in the vocabulary an author can use.
    /// It is what exports the built-in library to data, and the round trip through
    /// <see cref="Assemble"/> is the proof that the vocabulary can hold everything the library has.
    /// </summary>
    public static MachineDefinition Describe(VehicleProfile v, string id)
    {
        var parts = new List<MachinePart>
        {
            new() { Model = MachineModels.Engine, Profile = EngineKeyOf(v.Engine) },
            new()
            {
                Model = MachineModels.Exhaust,
                At = new Vector3(0f, v.ExhaustHeight, v.ExhaustOffsetZ),
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
                    ["sourceLevelDb"] = v.SourceLevelDb,
                },
            },
        };
        return new MachineDefinition { Id = id, Name = v.Name, Parts = parts };
    }

    /// <summary>
    /// Which engine preset this is, by name.
    ///
    /// A vehicle holds an EngineProfile, not the key it came from — and its own EngineKey is the
    /// VEHICLE's key, which is not always the engine's (the school bus runs a "diesel_bus"). The
    /// engine's Name is unique across the library and a test holds it that way.
    /// </summary>
    private static string EngineKeyOf(EngineProfile engine)
    {
        foreach (var kv in EngineProfile.Presets)
            if (kv.Value().Name == engine.Name) return kv.Key;
        return "";
    }

    /// <summary>Which tyre this is. A TyreProfile is all scalars, so the record's own equality is
    /// the whole answer — and the settings above carry every field anyway, so a miss costs nothing.</summary>
    private static string TyreKeyOf(TyreProfile t)
    {
        foreach (var kv in TyreProfile.Presets) if (kv.Value() == t) return kv.Key;
        return "";
    }

    /// <summary>
    /// Which body this is.
    ///
    /// Not the record's own equality: VehicleBody holds its panel spans in an ARRAY, and a record
    /// compares arrays by reference, so two identical bodies built a moment apart are unequal. The
    /// spans are compared as the list of numbers they are.
    /// </summary>
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

    /// <summary>What a machine looks like on disk. Nullable throughout: an omitted field means
    /// "unchanged", and that is the whole reason a definition can be short.</summary>
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

    /// <summary>A definition as it would be written on disk. The export side of the library.</summary>
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

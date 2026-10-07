using System.Globalization;
using System.Text.Json.Nodes;
using OpenFPS.Common;
using OpenFPS.Common.Editing;
using OpenFPS.Server.Repositories;

namespace OpenFPS.Server.Editor;

/// <summary>
/// One kind of model as the world editor sees it (docs/WORLD_EDITOR.md sections 4 and 11.1): what its
/// fields are, which models there are, each one as JSON, and how a new version is checked and put in
/// use. The library's kinds are one class (<see cref="LibraryKind"/>); prefabs, which live in the
/// server's PrefabRepository and are described by prefab-schema.json, are another (<see cref="PrefabKind"/>).
/// The editor knows nothing about any one kind beyond this.
/// </summary>
public abstract class EditorKind
{
    /// <summary>"small_machine", "prefab".</summary>
    public abstract string Kind { get; }
    /// <summary>What the kind is called aloud: "machine".</summary>
    public virtual string Spoken => ModelKinds.Spoken(Kind);
    /// <summary>Its fields, as the menus show them.</summary>
    public abstract IReadOnlyList<FieldNode> Fields { get; }
    public abstract IEnumerable<string> Ids { get; }
    public abstract bool Knows(string id);
    /// <summary>The model as the server has it in use now.</summary>
    public abstract string CurrentJson(string id);
    /// <summary>The model as it ships (version 0), or null for one that does not: one made in the editor.</summary>
    public abstract string? BuiltInJson(string id);
    /// <summary>Reads JSON as a model of this kind, refusing (by throwing, with the reason) what is not one;
    /// returns it as the kind writes it.</summary>
    public abstract string Check(string id, string json);
    /// <summary>Puts a version in use on the server.</summary>
    public abstract void Apply(string id, string json);
    /// <summary>Whether clients hold this kind too and are sent its versions (ModelUpdate).</summary>
    public virtual bool ToClients => true;
    /// <summary>The JSON of a model with its own id written in it changed to another (a prefab's Id).</summary>
    public virtual string Renamed(string json, string newId) => json;

    /// <summary>The model's own name, when it has one ("Condenser unit, 3 ton").</summary>
    public string Name(string id)
    {
        try
        {
            if (JsonNode.Parse(CurrentJson(id)) is JsonObject o && o.TryGetPropertyValue("Name", out var n)
                && n is JsonValue v && v.TryGetValue(out string? s) && !string.IsNullOrWhiteSpace(s))
                return s;
        }
        catch (Exception) { }
        return id;
    }

    /// <summary>A model's id as it is spelled in the library, or null if there is none of that name.</summary>
    public string? Canonical(string id) => Ids.FirstOrDefault(i => i.Equals(id, StringComparison.OrdinalIgnoreCase));
}

/// <summary>A kind in <see cref="ModelLibrary"/>: described by its type's [Tunable] attributes, held by every client too.</summary>
public sealed class LibraryKind : EditorKind
{
    public LibraryKind(string kind) { Kind = kind; }

    public override string Kind { get; }
    public Type Type => ModelLibrary.TypeOf(Kind)!;
    public override IReadOnlyList<FieldNode> Fields => ModelKinds.Describe(Type);
    public override IEnumerable<string> Ids => ModelLibrary.Ids(Kind);
    public override bool Knows(string id) => ModelLibrary.Knows(Kind, id);
    public override string CurrentJson(string id) => ModelLibrary.SpecJson(ModelLibrary.Model(Kind, id));
    public override string? BuiltInJson(string id) => ModelLibrary.BuiltInModel(Kind, id) is { } m ? ModelLibrary.SpecJson(m) : null;
    public override string Check(string id, string json) => ModelLibrary.SpecJson(ModelLibrary.FromSpecJson(Kind, json));

    public override void Apply(string id, string json)
    {
        if (!ModelLibrary.AddJson(Kind, id, json)) throw new ArgumentException($"The {Spoken} {id} would not read back.");
    }
}

/// <summary>
/// Prefabs as a kind: the server's PrefabRepository, described from prefab-schema.json (its types,
/// enums, minimum and maximum, and its descriptions as the help). A version is checked by
/// PrefabValidator, as a file is at load. Clients never hold prefabs: they are sent the things made from
/// them, so a new version is heard by making those things again (WorldEditor.RemakeAll).
/// </summary>
public sealed class PrefabKind : EditorKind
{
    public const string KindId = "prefab";
    private readonly PrefabRepository _prefabs;
    private readonly Dictionary<string, string> _asLoaded = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<FieldNode>? _fields;

    public PrefabKind(PrefabRepository prefabs)
    {
        _prefabs = prefabs;
        // Version 0 of each: the file, as it was read at start.
        foreach (var (id, t) in prefabs.Prefabs) _asLoaded[id] = PrefabRepository.ToJson(t);
    }

    public override string Kind => KindId;
    public override string Spoken => "prefab";
    public override bool ToClients => false;
    public override IReadOnlyList<FieldNode> Fields => _fields ??= PrefabSchema.Describe(Path.Combine(_prefabs.Folder, "prefab-schema.json"));
    public override IEnumerable<string> Ids => _prefabs.Prefabs.Values.Select(t => t.Id);
    public override bool Knows(string id) => _prefabs.Prefabs.ContainsKey(id.ToLowerInvariant());
    public override string CurrentJson(string id) => PrefabRepository.ToJson(_prefabs.Prefabs[id.ToLowerInvariant()]);
    public override string? BuiltInJson(string id) => _asLoaded.TryGetValue(id, out var j) ? j : null;

    public override string Check(string id, string json)
    {
        var t = PrefabRepository.FromJson(json);
        if (!t.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException($"The prefab's id is {t.Id}, not {id}.");
        return PrefabRepository.ToJson(t);
    }

    public override void Apply(string id, string json) => _prefabs.Put(PrefabRepository.FromJson(json));

    public override string Renamed(string json, string newId)
    {
        var node = JsonNode.Parse(json)!.AsObject();
        node["Id"] = newId;
        return node.ToJsonString();
    }
}

/// <summary>Every kind the editor has: the library's, and those the server adds (prefabs).</summary>
public sealed class ModelCatalog
{
    private readonly Dictionary<string, EditorKind> _kinds = new(StringComparer.OrdinalIgnoreCase);

    public ModelCatalog()
    {
        foreach (var kind in ModelLibrary.AllKinds) _kinds[kind] = new LibraryKind(kind);
    }

    public void Add(EditorKind kind) => _kinds[kind.Kind] = kind;

    public EditorKind? Get(string kind) => _kinds.TryGetValue(kind, out var k) ? k : null;

    public IEnumerable<EditorKind> All => _kinds.Values;

    /// <summary>A kind as typed: its id ("small_machine") or its spoken name ("machine").</summary>
    public EditorKind? Named(string word)
    {
        string w = word.Replace(' ', '_');
        foreach (var k in _kinds.Values)
            if (k.Kind.Equals(w, StringComparison.OrdinalIgnoreCase) || k.Spoken.Replace(' ', '_').Equals(w, StringComparison.OrdinalIgnoreCase))
                return k;
        return null;
    }
}

/// <summary>
/// The fields of a prefab, read from prefab-schema.json: a number with its minimum and maximum, a whole
/// number, on or off, a choice from an enum (materials from AcousticRegistry), words; a vector is a
/// group of x, y and z. Lists (a room's six materials, missing faces) are not shown here. A prefab's
/// identity (its id, its type, whether it is an item or a weapon) is shown and not changed: changing
/// those makes a different thing, which is a new prefab.
/// </summary>
public static class PrefabSchema
{
    private static readonly HashSet<string> Fixed = new(StringComparer.OrdinalIgnoreCase)
        { "Id", "Type", "IsItem", "Premium", "WeaponId", "Hands" };

    /// <summary>Units the names do not carry.</summary>
    private static readonly Dictionary<string, string> Units = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ColliderSize"] = "m", ["RoomSize"] = "m", ["EmitterOffset"] = "m", ["ItemWeight"] = "kg", ["Mass"] = "kg",
        ["Range"] = "m", ["MinDistance"] = "m", ["ShellThickness"] = "m", ["ApertureSize"] = "m", ["Landing"] = "m",
        ["Thickness"] = "m", ["SynthFreq"] = "Hz", ["SynthLfoRate"] = "Hz", ["ConeInsideAngle"] = "degrees",
        ["ConeOutsideAngle"] = "degrees", ["GranularGrainSize"] = "ms", ["GranularDensity"] = "per second",
        ["CrowdReactRadiusMetres"] = "m", ["DoorSkinMetres"] = "m", ["SensorMetres"] = "m",
    };

    public static IReadOnlyList<FieldNode> Describe(string schemaPath)
    {
        if (!File.Exists(schemaPath)) return Array.Empty<FieldNode>();
        var root = JsonNode.Parse(File.ReadAllText(schemaPath))!.AsObject();
        var defs = root["definitions"] as JsonObject;
        var props = root["properties"] as JsonObject;
        return props == null ? Array.Empty<FieldNode>() : Properties(props, defs, parentUnit: "");
    }

    private static List<FieldNode> Properties(JsonObject props, JsonObject? defs, string parentUnit)
    {
        var nodes = new List<FieldNode>();
        foreach (var (name, value) in props)
        {
            if (value is not JsonObject p) continue;
            if (Node(name, p, defs, parentUnit) is { } n) nodes.Add(n);
        }
        return nodes;
    }

    private static FieldNode? Node(string name, JsonObject p, JsonObject? defs, string parentUnit)
    {
        var (derived, derivedUnit) = FieldDescriptor.Describe(name);
        string unit = Units.TryGetValue(name, out var u) ? u : derivedUnit.Length > 0 ? derivedUnit : parentUnit;
        string help = FirstSentence(p["description"]?.GetValue<string>() ?? "");
        string? reference = p["$ref"]?.GetValue<string>();
        if (reference != null)
        {
            string defName = reference[(reference.LastIndexOf('/') + 1)..];
            if (defName == "material")
                return Scalar(name, derived, new FieldDescriptor
                {
                    Path = name, Label = derived, Type = FieldType.Choice, Help = help.Length > 0 ? help : "What it is made of.",
                    Choices = AcousticRegistry.KnownMaterials(),
                });
            if (defs?[defName] is JsonObject def && def["properties"] is JsonObject inner)
                return new FieldNode { Name = name, Label = derived, Kind = FieldNodeKind.Group, Children = Properties(inner, defs, unit) };
            return null;
        }

        string type = p["type"]?.GetValue<string>() ?? "";
        switch (type)
        {
            case "object":
                return p["properties"] is JsonObject inner
                    ? new FieldNode { Name = name, Label = derived, Kind = FieldNodeKind.Group, Children = Properties(inner, defs, unit) }
                    : null;
            case "array":
                return null;
            case "boolean":
                return Scalar(name, derived, new FieldDescriptor { Path = name, Label = derived, Type = FieldType.Bool, Help = help, ReadOnly = Fixed.Contains(name) });
            case "string":
            {
                var choices = p["enum"] is JsonArray e ? e.Select(x => x!.GetValue<string>()).ToArray() : null;
                return Scalar(name, derived, new FieldDescriptor
                {
                    Path = name, Label = derived, Type = choices != null ? FieldType.Choice : FieldType.Text,
                    Choices = choices ?? Array.Empty<string>(), Help = help, ReadOnly = Fixed.Contains(name),
                });
            }
            case "number":
            case "integer":
            {
                double min = Bound(p, "minimum", "exclusiveMinimum", double.MinValue);
                double max = Bound(p, "maximum", "exclusiveMaximum", double.MaxValue);
                if (p["enum"] is JsonArray values && values.Count > 0)
                {
                    var nums = values.Select(x => x!.GetValue<double>()).ToList();
                    min = nums.Min(); max = nums.Max();
                }
                // Lengths and times have no maximum in the schema; the editor's range is a sensible one.
                if (max == double.MaxValue && min >= 0)
                    max = unit switch { "m" => 1000, "s" => 600, "kg" => 100000, "Hz" => 20000, "degrees" => 360, _ => 1000 };
                if (min == double.MinValue) min = unit == "degrees" ? -360 : -1000;
                return Scalar(name, derived, new FieldDescriptor
                {
                    Path = name, Label = derived, Type = type == "integer" ? FieldType.Integer : FieldType.Number,
                    Unit = unit, Min = min, Max = max, Step = StepFor(unit, min, max, type == "integer"), Help = help,
                    ReadOnly = Fixed.Contains(name),
                });
            }
        }
        return null;
    }

    private static FieldNode Scalar(string name, string label, FieldDescriptor field)
        => new() { Name = name, Label = label, Kind = FieldNodeKind.Scalar, Field = field };

    private static double Bound(JsonObject p, string inclusive, string exclusive, double none)
    {
        if (p[inclusive] is JsonValue v && v.TryGetValue(out double d)) return d;
        if (p[exclusive] is JsonValue x && x.TryGetValue(out double e)) return e;
        return none;
    }

    private static double StepFor(string unit, double min, double max, bool integer)
    {
        if (integer) return 1;
        if (max - min <= 1.0001) return 0.05;
        return unit switch { "m" => 0.05, "s" => 0.1, "degrees" => 5, "Hz" => 10, "kg" => 0.1, "ms" => 5, _ => 0.1 };
    }

    private static string FirstSentence(string s)
    {
        int dot = s.IndexOf(". ", StringComparison.Ordinal);
        string first = dot > 0 ? s[..(dot + 1)] : s;
        return first.Length > 240 ? first[..240].TrimEnd() + "..." : first;
    }

    internal static string Format(double d) => d.ToString("R", CultureInfo.InvariantCulture);
}

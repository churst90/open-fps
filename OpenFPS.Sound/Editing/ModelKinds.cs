using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;

namespace OpenFPS.Common.Editing;

/// <summary>Whether a node of a model's description is a value, a part with values of its own, or a
/// list of such parts.</summary>
public enum FieldNodeKind : byte { Scalar, Group, List }

/// <summary>
/// One property of a model, as the editor sees it. A scalar has a <see cref="Field"/>; a group (a
/// record inside the model: a compressor, a casing) and a list (an array of records: a fountain's
/// falls) have the properties of their own type as <see cref="Children"/>. A list of values (a room's
/// six materials) has no children: its <see cref="Field"/> describes each item.
/// </summary>
public sealed class FieldNode
{
    public required string Name { get; init; }
    public required string Label { get; init; }
    public FieldNodeKind Kind { get; init; }
    /// <summary>For a scalar: its description, with <see cref="FieldDescriptor.Path"/> its own name only.</summary>
    public FieldDescriptor? Field { get; init; }
    public IReadOnlyList<FieldNode> Children { get; init; } = Array.Empty<FieldNode>();
    /// <summary>An enum's type, so its value can be said and set by name.</summary>
    public Type? EnumType { get; init; }

    /// <summary>A list whose items are values, not records.</summary>
    public bool IsValueList => Kind == FieldNodeKind.List && Field != null;
}

/// <summary>
/// Reads a model's description of itself: its type's properties, with what <see cref="TunableAttribute"/>
/// says about each. The editor's menus are made from it, so a kind added to <see cref="ModelLibrary"/>
/// needs no editor code (docs/WORLD_EDITOR.md section 4). Values are changed on the model's JSON, the
/// round trip ModelLibrary tests for every built-in model.
/// </summary>
public static class ModelKinds
{
    private static readonly ConcurrentDictionary<Type, (int Generation, IReadOnlyList<FieldNode> Nodes)> Cache = new();

    /// <summary>What a kind is called aloud: "small_machine" is "machine", "rail_vehicle" "rail vehicle".</summary>
    public static string Spoken(string kind) => kind switch
    {
        ModelLibrary.Kinds.SmallMachine => "machine",
        ModelLibrary.Kinds.Water => "water feature",
        ModelLibrary.Kinds.Flow => "running water",
        ModelLibrary.Kinds.Air => "air system",
        ModelLibrary.Kinds.GasHob => "gas hob",
        _ => kind.Replace('_', ' '),
    };

    /// <summary>The properties of a model type, described. Made again when the library changes, since a
    /// choice of models (<c>Choices = "models:horn"</c>) lists what the library holds.</summary>
    public static IReadOnlyList<FieldNode> Describe(Type type)
    {
        int generation = ModelLibrary.Generation;
        if (Cache.TryGetValue(type, out var cached) && cached.Generation == generation) return cached.Nodes;
        var nodes = Build(type);
        Cache[type] = (generation, nodes);
        return nodes;
    }

    private static IReadOnlyList<FieldNode> Build(Type type)
    {
        var nodes = new List<FieldNode>();
        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            // Only what the JSON carries: a property with a setter (or init), not a computed one.
            if (p.SetMethod == null || p.GetIndexParameters().Length > 0) continue;
            if (p.GetCustomAttribute<System.Text.Json.Serialization.JsonIgnoreAttribute>() != null) continue;
            var t = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
            var tunable = p.GetCustomAttribute<TunableAttribute>();
            var (derivedLabel, derivedUnit) = FieldDescriptor.Describe(p.Name);
            string label = tunable?.Label is { Length: > 0 } l ? l : derivedLabel;

            if (IsScalar(t))
            {
                nodes.Add(new FieldNode
                {
                    Name = p.Name, Label = label, Kind = FieldNodeKind.Scalar,
                    EnumType = t.IsEnum ? t : null,
                    Field = Scalar(p.Name, label, t, tunable, derivedUnit),
                });
            }
            else if (t.IsArray && t.GetElementType() is { } element && IsRecord(element))
            {
                nodes.Add(new FieldNode { Name = p.Name, Label = label, Kind = FieldNodeKind.List, Children = Describe(element) });
            }
            else if (IsRecord(t))
            {
                nodes.Add(new FieldNode { Name = p.Name, Label = label, Kind = FieldNodeKind.Group, Children = Describe(t) });
            }
            // Arrays of numbers and anything else: not shown in phase 1.
        }
        return nodes;
    }

    private static FieldDescriptor Scalar(string name, string label, Type t, TunableAttribute? tunable, string derivedUnit)
    {
        FieldType type = t == typeof(bool) ? FieldType.Bool
                       : t.IsEnum ? FieldType.Choice
                       : t == typeof(string) ? (tunable?.Choices is { Length: > 0 } ? FieldType.Choice : FieldType.Text)
                       : t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte) ? FieldType.Integer
                       : FieldType.Number;
        IReadOnlyList<string> choices = t.IsEnum ? Enum.GetNames(t)
            : tunable?.Choices == "materials" ? AcousticRegistry.KnownMaterials()
            : tunable?.Choices is { } m && m.StartsWith("models:", StringComparison.Ordinal)
                ? ModelLibrary.Ids(m["models:".Length..]).OrderBy(i => i, StringComparer.OrdinalIgnoreCase).ToArray()
            : tunable?.Choices is { Length: > 0 } c ? c.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            : Array.Empty<string>();
        return new FieldDescriptor
        {
            Path = name,
            Label = label,
            Type = type,
            Unit = tunable?.Unit is { Length: > 0 } u ? u : derivedUnit,
            Min = tunable?.Min ?? double.MinValue,
            Max = tunable?.Max ?? double.MaxValue,
            Step = tunable?.Step ?? 0,
            Help = tunable?.Help ?? "Not described yet.",
            Source = tunable?.Source ?? "",
            Choices = choices,
            ReadOnly = tunable == null,
        };
    }

    private static bool IsScalar(Type t)
        => t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal);

    private static bool IsRecord(Type t)
        => t.IsClass && t != typeof(string) && !t.IsArray
           && t.Namespace?.StartsWith("OpenFPS", StringComparison.Ordinal) == true;

    // ── Paths ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>"Compressor.HumDb" into ("Compressor", -1), ("HumDb", -1); "Falls[2].Height" into ("Falls", 2), ("Height", -1).</summary>
    public static bool TryParsePath(string path, out List<(string Name, int Index)> segments)
    {
        segments = new List<(string, int)>();
        if (string.IsNullOrWhiteSpace(path)) return false;
        foreach (var raw in path.Split('.'))
        {
            string part = raw.Trim();
            int index = -1;
            int open = part.IndexOf('[');
            if (open >= 0)
            {
                if (!part.EndsWith(']')) return false;
                if (!int.TryParse(part[(open + 1)..^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out index) || index < 0) return false;
                part = part[..open];
            }
            if (part.Length == 0) return false;
            segments.Add((part, index));
        }
        return segments.Count > 0;
    }

    /// <summary>The description of the value at a path in a model of this type, with its full path; null if
    /// the path names no scalar. Names match whatever the case, as the model's JSON does.</summary>
    public static FieldNode? NodeAt(Type type, string path, out FieldDescriptor? field)
        => NodeAt(Describe(type), path, out field);

    /// <summary>The same, over a description that did not come from a type (a prefab's, from its schema).</summary>
    public static FieldNode? NodeAt(IReadOnlyList<FieldNode> root, string path, out FieldDescriptor? field)
    {
        field = null;
        if (!TryParsePath(path, out var segments)) return null;
        IReadOnlyList<FieldNode> level = root;
        FieldNode? node = null;
        var canonical = new List<string>();
        for (int i = 0; i < segments.Count; i++)
        {
            var (name, index) = segments[i];
            node = level.FirstOrDefault(n => n.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (node == null) return null;
            if (node.Kind == FieldNodeKind.List)
            {
                if (index < 0 && i < segments.Count - 1) return null;
                canonical.Add(index >= 0 ? $"{node.Name}[{index}]" : node.Name);
            }
            else
            {
                if (index >= 0) return null;
                canonical.Add(node.Name);
            }
            level = node.Children;
        }
        // A list of values is a field item by item ("RoomMaterials[2]"), never as a whole.
        if (node?.Field != null && (node.Kind == FieldNodeKind.Scalar || segments[^1].Index >= 0))
            field = node.Field with { Path = string.Join(".", canonical) };
        return node;
    }

    /// <summary>The JSON node at a path, or null when it, or a part on the way to it, is absent.</summary>
    public static JsonNode? Get(JsonNode root, string path)
    {
        if (!TryParsePath(path, out var segments)) return null;
        JsonNode? at = root;
        foreach (var (name, index) in segments)
        {
            if (at is not JsonObject obj) return null;
            at = Property(obj, name);
            if (index >= 0)
            {
                if (at is not JsonArray arr || index >= arr.Count) return null;
                at = arr[index];
            }
        }
        return at;
    }

    /// <summary>A value at a path, as stored: "63", "true", "Metal"; an enum by its name. Null if absent.</summary>
    public static string? GetValue(JsonNode root, string path, FieldNode? node = null)
    {
        var at = Get(root, path);
        if (at == null) return null;
        if (at is JsonValue v)
        {
            if (node?.EnumType != null && v.TryGetValue(out int e)) return Enum.GetName(node.EnumType, e) ?? e.ToString(CultureInfo.InvariantCulture);
            if (v.TryGetValue(out string? s)) return s ?? "";
            if (v.TryGetValue(out bool b)) return b ? "true" : "false";
            return at.ToJsonString();
        }
        return null;
    }

    /// <summary>
    /// Puts a value (as <see cref="FieldDescriptor.TryParse"/> stores it) at a path. Refused, with the
    /// reason, if a part on the way is absent: phase 1 changes what a model has, it does not add parts.
    /// </summary>
    public static bool TrySet(JsonNode root, string path, string stored, FieldDescriptor field, FieldNode? node, out string error)
    {
        error = "";
        if (!TryParsePath(path, out var segments)) { error = $"There is no {path} here."; return false; }
        JsonNode? at = root;
        for (int i = 0; i < segments.Count; i++)
        {
            var (name, index) = segments[i];
            if (at is not JsonObject obj) { error = $"{field.Label} is not part of this model."; return false; }
            string key = obj.Select(kv => kv.Key).FirstOrDefault(k => k.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? name;
            bool last = i == segments.Count - 1;
            if (last && index < 0)
            {
                obj[key] = ToJson(stored, field, node);
                return true;
            }
            var next = obj[key];
            if (index >= 0)
            {
                if (next is not JsonArray arr || index >= arr.Count) { error = $"There is no item {index + 1} of {FieldDescriptor.Words(name)}."; return false; }
                if (last && node?.IsValueList == true)
                {
                    arr[index] = ToJson(stored, field, node);
                    return true;
                }
                next = arr[index];
            }
            if (next == null) { error = $"This model has no {FieldDescriptor.Words(name)}, so {field.Label} cannot be set."; return false; }
            at = next;
        }
        error = $"There is no {path} here.";
        return false;
    }

    /// <summary>
    /// Puts values (stored forms) on the end of a list of values, making the list if the model has none
    /// yet. Refused, with the reason, if a part on the way to it is absent.
    /// </summary>
    public static bool TryAppend(JsonNode root, string listPath, IReadOnlyList<string> stored, FieldDescriptor field, FieldNode node, out int count, out string error)
    {
        count = 0;
        if (!TryList(root, listPath, create: true, out var arr, out error)) return false;
        foreach (var v in stored) arr!.Add(ToJson(v, field, node));
        count = arr!.Count;
        return true;
    }

    /// <summary>
    /// The array at a path ("Falls", "Bells[0].Partials"), made empty there if it is absent and
    /// <paramref name="create"/> says so.
    /// </summary>
    public static bool TryList(JsonNode root, string listPath, bool create, out JsonArray? list, out string error)
    {
        list = null;
        error = "";
        if (!TryParsePath(listPath, out var segments) || segments[^1].Index >= 0) { error = $"{listPath} is not a list."; return false; }
        int dot = listPath.LastIndexOf('.');
        var parent = dot < 0 ? root : Get(root, listPath[..dot]);
        if (parent is not JsonObject obj) { error = $"This model has no {FieldDescriptor.Words(segments[^2].Name)}."; return false; }
        string name = segments[^1].Name;
        string key = obj.Select(kv => kv.Key).FirstOrDefault(k => k.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? name;
        if (obj[key] is JsonArray arr) { list = arr; return true; }
        if (obj[key] != null || !create) { error = $"This model has no {FieldDescriptor.Words(name)}."; return false; }
        obj[key] = list = new JsonArray();
        return true;
    }

    private static JsonNode? ToJson(string stored, FieldDescriptor field, FieldNode? node)
    {
        switch (field.Type)
        {
            case FieldType.Bool: return JsonValue.Create(stored == "true");
            case FieldType.Integer: return JsonValue.Create(long.Parse(stored, CultureInfo.InvariantCulture));
            case FieldType.Number: return JsonValue.Create(double.Parse(stored, CultureInfo.InvariantCulture));
            case FieldType.Choice when node?.EnumType != null:
                return JsonValue.Create(Convert.ToInt32(Enum.Parse(node.EnumType, stored, ignoreCase: true), CultureInfo.InvariantCulture));
            default: return JsonValue.Create(stored);
        }
    }

    private static JsonNode? Property(JsonObject obj, string name)
    {
        foreach (var kv in obj)
            if (kv.Key.Equals(name, StringComparison.OrdinalIgnoreCase)) return kv.Value;
        return null;
    }

    // ── Which model a sound is ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// The model a placed thing's sound id names: "machine:ac_condenser" is the small machine
    /// ac_condenser, "water:park_fountain/elm_park/0" the water feature park_fountain. False for a
    /// sound that is a recording or that no kind owns.
    /// </summary>
    public static bool TryModelOfSound(string? soundId, out string kind, out string id)
    {
        kind = id = "";
        if (string.IsNullOrEmpty(soundId)) return false;
        int colon = soundId.IndexOf(':');
        if (colon <= 0) return false;
        string prefix = soundId[..colon].ToLowerInvariant();
        string rest = soundId[(colon + 1)..];
        int slash = rest.IndexOf('/');
        string key = slash >= 0 ? rest[..slash] : rest;
        kind = prefix switch
        {
            "machine" => ModelLibrary.Kinds.SmallMachine,
            "water" => ModelLibrary.Kinds.Water,
            "fire" => ModelLibrary.Kinds.Fire,
            "foliage" => ModelLibrary.Kinds.Foliage,
            "flow" => ModelLibrary.Kinds.Flow,
            "bell" => ModelLibrary.Kinds.Bell,
            "shore" => ModelLibrary.Kinds.Shore,
            "stove" => ModelLibrary.Kinds.GasHob,
            // A vehicle's sound is its engine: "engine:school_bus" is the vehicle school_bus.
            "engine" => ModelLibrary.Kinds.Vehicle,
            _ => "",
        };
        id = key;
        return kind.Length > 0 && ModelLibrary.Knows(kind, id);
    }

    /// <summary>A sound id with the model it names changed to another of the same kind:
    /// "water:park_fountain/elm_park/0" with "pond_jet" is "water:pond_jet/elm_park/0". Null if the
    /// sound does not name a model.</summary>
    public static string? WithModel(string? soundId, string newId)
    {
        if (string.IsNullOrEmpty(soundId)) return null;
        int colon = soundId.IndexOf(':');
        if (colon <= 0) return null;
        string rest = soundId[(colon + 1)..];
        int slash = rest.IndexOf('/');
        return soundId[..(colon + 1)] + newId + (slash >= 0 ? rest[slash..] : "");
    }
}

using System.Text.Json;
using OpenFPS.Common;
using Serilog;

namespace OpenFPS.Server.Repositories;

/// <summary>
/// Loads <see cref="PrefabTemplate"/> files, the only reader of them, from the folder its host names, and
/// writes them back. Every file is checked by <see cref="PrefabValidator"/>; one the engine cannot honour is
/// rejected with the reasons named, and a map that places a rejected id is told so rather than "not found".
/// Making a prefab into an entity is the host's (the server's PrefabRepository).
/// </summary>
public class PrefabLibrary
{
    private readonly string _directory;
    private Dictionary<string, PrefabTemplate> _prefabs = new();
    private readonly Dictionary<string, int> _rejected = new(StringComparer.OrdinalIgnoreCase);

    public PrefabLibrary(string directory)
    {
        _directory = directory;
        if (!Directory.Exists(_directory)) Directory.CreateDirectory(_directory);
        // Validation resolves material NAMES, and the server never initialized the registry.
        AcousticRegistry.EnsureInitialized();
        LoadAll();
    }

    /// <summary>Ids that failed validation, with how many problems each had.</summary>
    public IReadOnlyDictionary<string, int> RejectedPrefabs => _rejected;

    public IReadOnlyDictionary<string, PrefabTemplate> Prefabs => _prefabs;

    private static JsonSerializerOptions ReadOptions() => new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Converters = {
            new System.Text.Json.Serialization.JsonStringEnumConverter(),
            new OpenFPS.Common.Networking.Vector3Converter(),
            new OpenFPS.Common.Networking.QuaternionConverter()
        }
    };

    /// <summary>The folder the prefabs are read from (prefab-schema.json is there too).</summary>
    public string Folder => _directory;

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Converters =
        {
            new System.Text.Json.Serialization.JsonStringEnumConverter(),
            new OpenFPS.Common.Networking.Vector3Converter(),
            new OpenFPS.Common.Networking.QuaternionConverter()
        }
    };

    /// <summary>A prefab as its file would have it: enums by name, nothing written for an omitted field.</summary>
    public static string ToJson(PrefabTemplate t) => JsonSerializer.Serialize(t, WriteOptions);

    /// <summary>
    /// A prefab read from JSON and checked as the loader checks a file. Throws, saying what is wrong, if
    /// it is not a prefab the engine can honour.
    /// </summary>
    public static PrefabTemplate FromJson(string json)
    {
        List<string> keys;
        using (var doc = JsonDocument.Parse(json))
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("A prefab is a JSON object.");
            keys = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();
        }
        var t = JsonSerializer.Deserialize<PrefabTemplate>(json, ReadOptions()) ?? throw new ArgumentException("The prefab is empty.");
        var result = PrefabValidator.Validate(t, keys);
        if (!result.IsValid) throw new ArgumentException(string.Join(" ", result.Errors.Take(3)));
        return t;
    }

    /// <summary>Puts a prefab in use in memory, replacing one of the same id (the world editor's versions;
    /// no file is written).</summary>
    public void Put(PrefabTemplate t) => _prefabs[t.Id.ToLowerInvariant()] = t;

    public void LoadAll()
    {
        _prefabs.Clear();
        _rejected.Clear();
        var options = ReadOptions();
        var sourceFile = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in Directory.GetFiles(_directory, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            string name = Path.GetFileName(file);
            if (name.EndsWith("-schema.json", StringComparison.OrdinalIgnoreCase)) continue;

            PrefabTemplate? template;
            List<string> jsonProperties;
            try
            {
                string json = File.ReadAllText(file);
                // Parsed twice: System.Text.Json drops an unknown key without a word, and the validator
                // needs the keys to name a mistyped field.
                using (var doc = JsonDocument.Parse(json, new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                }))
                {
                    if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    {
                        Log.Error("PrefabRepository: {File} is not a JSON object — skipped.", name);
                        continue;
                    }
                    jsonProperties = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();
                }

                template = JsonSerializer.Deserialize<PrefabTemplate>(json, options);
            }
            catch (Exception ex)
            {
                Log.Error("PrefabRepository: {File} could not be read as a prefab and was REJECTED. {Error}", name, ex.Message);
                continue;
            }

            if (template == null)
            {
                Log.Error("PrefabRepository: {File} deserialized to nothing and was REJECTED.", name);
                continue;
            }

            var result = PrefabValidator.Validate(template, jsonProperties);
            string id = string.IsNullOrWhiteSpace(template.Id) ? Path.GetFileNameWithoutExtension(file) : template.Id;

            string stem = Path.GetFileNameWithoutExtension(file);
            if (!string.IsNullOrWhiteSpace(template.Id) && !string.Equals(template.Id, stem, StringComparison.OrdinalIgnoreCase))
                result.Warnings.Add($"Id '{template.Id}' does not match the file name '{stem}'. Maps reference the Id, not the file.");

            if (sourceFile.TryGetValue(id, out var firstFile))
                result.Errors.Add($"Id '{id}' is already defined by {firstFile}. One of the two would silently replace the other.");

            foreach (var warning in result.Warnings)
                Log.Warning("PrefabRepository: {File} — {Warning}", name, warning);

            if (!result.IsValid)
            {
                _rejected[id] = result.Errors.Count;
                Log.Error("PrefabRepository: REJECTED {File} — {Count} problem(s):", name, result.Errors.Count);
                foreach (var error in result.Errors) Log.Error("PrefabRepository:   • {Error}", error);
                continue;
            }

            sourceFile[id] = name;
            _prefabs[id.ToLowerInvariant()] = template;
        }

        if (_rejected.Count > 0)
            Log.Error("PrefabRepository: {Loaded} prefab(s) loaded, {Rejected} REJECTED ({Ids}). " +
                      "Any map entity using a rejected prefab will fail to spawn.",
                _prefabs.Count, _rejected.Count, string.Join(", ", _rejected.Keys));
        else
            Log.Information("PrefabRepository: {Loaded} prefab(s) loaded, all valid.", _prefabs.Count);
    }

    public void Save(PrefabTemplate prefab)
    {
        string path = Path.Combine(_directory, $"{prefab.Id}.json");
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            Converters = { 
                new System.Text.Json.Serialization.JsonStringEnumConverter(),
                new OpenFPS.Common.Networking.Vector3Converter(),
                new OpenFPS.Common.Networking.QuaternionConverter()
            }
        };
        File.WriteAllText(path, JsonSerializer.Serialize(prefab, options));
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OpenFPS.Common;
using OpenFPS.Common.Networking;
using Serilog;

namespace OpenFPS.Server.Editor;

/// <summary>One saved version of a model.</summary>
public sealed class ModelVersion
{
    public int Version { get; set; }
    public string Author { get; set; } = "";
    public DateTime SavedUtc { get; set; }
    public string Note { get; set; } = "";
    public JsonNode? Spec { get; set; }
}

/// <summary>Every version of one model the editor has changed, and which is in use.</summary>
public sealed class ModelHistory
{
    public string Kind { get; set; } = "";
    public string Id { get; set; } = "";
    /// <summary>The version every map uses unless it pins another. 0 is <see cref="Base"/>.</summary>
    public int Current { get; set; }
    /// <summary>Version 0: the model as it was before the first change (built in, or from models/).</summary>
    public JsonNode? Base { get; set; }
    public List<ModelVersion> Versions { get; set; } = new();
}

/// <summary>
/// The library's models as the world editor changes them: every version kept, one file per model in
/// model_versions/ (docs/WORLD_EDITOR.md section 5). At start each model's current version goes into
/// ModelLibrary, which is how an authored model already overrides a built-in one. A null folder keeps
/// everything in memory (a test rig).
/// </summary>
public sealed class ModelStore
{
    private readonly string? _directory;
    private readonly Dictionary<string, ModelHistory> _models = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public ModelStore(string? directory)
    {
        _directory = directory == null ? null : Path.GetFullPath(directory);
    }

    private static string Key(string kind, string id) => kind + ":" + id;

    /// <summary>Model ids are file names here, so nothing that could leave the folder.</summary>
    public static bool IsSafeId(string s) => Regex.IsMatch(s, "^[A-Za-z0-9_-]{1,64}$");

    /// <summary>Reads every saved model and puts each one's current version into ModelLibrary.</summary>
    public int LoadAll()
    {
        if (_directory == null || !Directory.Exists(_directory)) return 0;
        int loaded = 0;
        foreach (string file in Directory.EnumerateFiles(_directory, "*.json"))
        {
            try
            {
                var h = JsonSerializer.Deserialize<ModelHistory>(File.ReadAllText(file), Options);
                if (h == null || ModelLibrary.TypeOf(h.Kind) == null || !IsSafeId(h.Id)) continue;
                _models[Key(h.Kind, h.Id)] = h;
                if (h.Current > 0 && SpecJson(h, h.Current) is { } json && ModelLibrary.AddJson(h.Kind, h.Id, json)) loaded++;
            }
            catch (Exception ex)
            {
                Log.Error("ModelStore: {File} will not load ({Error}); that model stays as built.", Path.GetFileName(file), ex.Message);
            }
        }
        if (loaded > 0) Log.Information("ModelStore: {Count} model(s) changed in the world editor are in use.", loaded);
        return loaded;
    }

    public ModelHistory? History(string kind, string id) => _models.TryGetValue(Key(kind, id), out var h) ? h : null;

    /// <summary>The version in use now: 0 if it has never been changed.</summary>
    public int CurrentVersion(string kind, string id) => History(kind, id)?.Current ?? 0;

    /// <summary>The model in use now, as JSON.</summary>
    public string CurrentJson(string kind, string id) => ModelLibrary.SpecJson(ModelLibrary.Model(kind, id));

    private static string? SpecJson(ModelHistory h, int version)
    {
        if (version == 0) return h.Base?.ToJsonString();
        return h.Versions.FirstOrDefault(v => v.Version == version)?.Spec?.ToJsonString();
    }

    /// <summary>
    /// Keeps a changed model as a new version and puts it in use: in ModelLibrary here, and as the
    /// update every client is sent. Throws if the JSON does not read as the kind.
    /// </summary>
    public ModelUpdate Commit(string kind, string id, string specJson, string author, string note)
    {
        // Checked before anything is kept: a model that will not read back is not a version.
        ModelLibrary.FromSpecJson(kind, specJson);
        if (!_models.TryGetValue(Key(kind, id), out var h))
        {
            h = new ModelHistory { Kind = kind, Id = id, Base = JsonNode.Parse(CurrentJson(kind, id)) };
            _models[Key(kind, id)] = h;
        }
        int version = h.Versions.Count == 0 ? 1 : h.Versions.Max(v => v.Version) + 1;
        h.Versions.Add(new ModelVersion { Version = version, Author = author, SavedUtc = DateTime.UtcNow, Note = note, Spec = JsonNode.Parse(specJson) });
        h.Current = version;
        ModelLibrary.AddJson(kind, id, specJson);
        Save(h);
        return new ModelUpdate { Kind = kind, Id = id, Version = version, SpecJson = specJson };
    }

    /// <summary>Puts an existing version back in use (undo and redo). Null if there is no such version.</summary>
    public ModelUpdate? SetCurrent(string kind, string id, int version)
    {
        var h = History(kind, id);
        if (h == null || SpecJson(h, version) is not { } json) return null;
        h.Current = version;
        ModelLibrary.AddJson(kind, id, json);
        Save(h);
        return new ModelUpdate { Kind = kind, Id = id, Version = version, SpecJson = json };
    }

    /// <summary>What a player is sent on arriving: every changed model as it is in use now.</summary>
    public IEnumerable<ModelUpdate> Updates()
    {
        foreach (var h in _models.Values.OrderBy(h => h.Kind).ThenBy(h => h.Id))
            if (SpecJson(h, h.Current) is { } json)
                yield return new ModelUpdate { Kind = h.Kind, Id = h.Id, Version = h.Current, SpecJson = json };
    }

    private void Save(ModelHistory h)
    {
        if (_directory == null) return;
        try
        {
            Directory.CreateDirectory(_directory);
            string path = Path.Combine(_directory, $"{h.Kind}.{h.Id}.json");
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(h, Options));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("ModelStore: could not write {Kind}.{Id}: {Error}", h.Kind, h.Id, ex.Message);
        }
    }
}

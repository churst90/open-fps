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
    /// <summary>Version 0: the model as it was before the first change (built in, or from models/). Null
    /// for a model made in the editor, which has no version 0.</summary>
    public JsonNode? Base { get; set; }
    public List<ModelVersion> Versions { get; set; } = new();
    /// <summary>Retired: not offered for new things or as a template. Things already using it keep it.</summary>
    public bool Retired { get; set; }
    /// <summary>For a model made in the editor: what it was made from ("small_machine:ac_condenser, as built").</summary>
    public string? MadeFrom { get; set; }
}

/// <summary>
/// The models as the world editor changes them: every version kept, one file per model in
/// model_versions/ (docs/WORLD_EDITOR.md sections 5 and 11). At start each model's current version is put
/// in use through its kind (<see cref="ModelCatalog"/>): into ModelLibrary for the library's kinds, into
/// the PrefabRepository for prefabs. A null folder keeps everything in memory (a test rig).
/// </summary>
public sealed class ModelStore
{
    private readonly string? _directory;
    private readonly Dictionary<string, ModelHistory> _models = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public ModelStore(string? directory, ModelCatalog? catalog = null)
    {
        _directory = directory == null ? null : Path.GetFullPath(directory);
        Catalog = catalog ?? new ModelCatalog();
    }

    /// <summary>The kinds this store keeps versions of.</summary>
    public ModelCatalog Catalog { get; }

    private static string Key(string kind, string id) => kind + ":" + id;

    /// <summary>Model ids are file names here, so nothing that could leave the folder.</summary>
    public static bool IsSafeId(string s) => OpenFPS.Server.Core.SafeText.IsFileName(s);

    /// <summary>Reads every saved model and puts each one's current version in use.</summary>
    public int LoadAll()
    {
        if (_directory == null || !Directory.Exists(_directory)) return 0;
        int loaded = 0;
        foreach (string file in Directory.EnumerateFiles(_directory, "*.json"))
        {
            try
            {
                var h = JsonSerializer.Deserialize<ModelHistory>(File.ReadAllText(file), Options);
                if (h == null || !IsSafeId(h.Id)) continue;
                var kind = Catalog.Get(h.Kind);
                if (kind == null) { Log.Warning("ModelStore: {File} is a {Kind}, which this server has no kind for; kept, not used.", Path.GetFileName(file), h.Kind); continue; }
                _models[Key(h.Kind, h.Id)] = h;
                if ((h.Current > 0 || h.Base == null) && SpecJson(h, h.Current) is { } json)
                {
                    kind.Apply(h.Id, json);
                    loaded++;
                }
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

    public IEnumerable<ModelHistory> Histories => _models.Values;

    /// <summary>The version in use now: 0 if it has never been changed.</summary>
    public int CurrentVersion(string kind, string id) => History(kind, id)?.Current ?? 0;

    /// <summary>Whether a model has been retired.</summary>
    public bool IsRetired(string kind, string id) => History(kind, id)?.Retired == true;

    /// <summary>The model in use now on the server, as JSON.</summary>
    public string CurrentJson(string kind, string id)
        => Catalog.Get(kind)?.CurrentJson(id) ?? throw new ArgumentException($"'{kind}' is not a kind of model.");

    private static string? SpecJson(ModelHistory h, int version)
    {
        if (version == 0) return h.Base?.ToJsonString();
        return h.Versions.FirstOrDefault(v => v.Version == version)?.Spec?.ToJsonString();
    }

    /// <summary>A kept version of a model as JSON, or null if there is no such version.</summary>
    public string? SpecJson(string kind, string id, int version)
    {
        var h = History(kind, id);
        if (h != null) return SpecJson(h, version);
        return version == 0 && Catalog.Get(kind) is { } k && k.Knows(id) ? k.CurrentJson(id) : null;
    }

    /// <summary>Whether a version of a model is kept.</summary>
    public bool HasVersion(string kind, string id, int version) => SpecJson(kind, id, version) != null;

    /// <summary>
    /// Keeps a changed model as a new version and puts it in use on the server, and gives the update
    /// clients are sent. A model the kind does not know yet is made: it has no version 0. Throws if the
    /// JSON is not a model of the kind.
    /// </summary>
    public ModelUpdate Commit(string kind, string id, string specJson, string author, string note, string? madeFrom = null)
    {
        // Checked before anything is kept: a model that will not read back is not a version, and an id
        // that is not a plain name is not a file name.
        if (!IsSafeId(id) || !IsSafeId(kind)) throw new ArgumentException($"'{kind}:{id}' cannot be kept as a file.");
        var k = Catalog.Get(kind) ?? throw new ArgumentException($"'{kind}' is not a kind of model.");
        string json = k.Check(id, specJson);
        if (!_models.TryGetValue(Key(kind, id), out var h))
        {
            h = new ModelHistory { Kind = kind, Id = id, Base = k.Knows(id) ? JsonNode.Parse(k.CurrentJson(id)) : null, MadeFrom = madeFrom };
            _models[Key(kind, id)] = h;
        }
        int version = h.Versions.Count == 0 ? 1 : h.Versions.Max(v => v.Version) + 1;
        h.Versions.Add(new ModelVersion { Version = version, Author = author, SavedUtc = DateTime.UtcNow, Note = note, Spec = JsonNode.Parse(json) });
        h.Current = version;
        k.Apply(id, json);
        Save(h);
        return new ModelUpdate { Kind = kind, Id = id, Version = version, SpecJson = json };
    }

    /// <summary>Puts an existing version back in use (undo and redo, "use on every map"). Null if there is no such version.</summary>
    public ModelUpdate? SetCurrent(string kind, string id, int version)
    {
        var h = History(kind, id);
        if (h == null || SpecJson(h, version) is not { } json || Catalog.Get(kind) is not { } k) return null;
        h.Current = version;
        k.Apply(id, json);
        Save(h);
        return new ModelUpdate { Kind = kind, Id = id, Version = version, SpecJson = json };
    }

    /// <summary>Retires a model, or brings it back. False if it was already so.</summary>
    public bool SetRetired(string kind, string id, bool retired)
    {
        var h = History(kind, id);
        if (h == null)
        {
            if (!retired) return false;
            var k = Catalog.Get(kind);
            if (k == null || !k.Knows(id)) return false;
            h = new ModelHistory { Kind = kind, Id = id, Base = JsonNode.Parse(k.CurrentJson(id)) };
            _models[Key(kind, id)] = h;
        }
        if (h.Retired == retired) return false;
        h.Retired = retired;
        Save(h);
        return true;
    }

    /// <summary>
    /// What a player on a map is sent: every changed model the clients hold, at the version that map uses
    /// (its pin, or the current one). Sent on arrival, so a model pinned on the map just left is put back.
    /// </summary>
    public IEnumerable<ModelUpdate> UpdatesFor(IReadOnlyDictionary<string, int>? pins)
    {
        foreach (var h in _models.Values.OrderBy(h => h.Kind).ThenBy(h => h.Id))
        {
            if (Catalog.Get(h.Kind) is not { ToClients: true }) continue;
            int version = pins != null && pins.TryGetValue(Key(h.Kind, h.Id), out int pinned) && SpecJson(h, pinned) != null ? pinned : h.Current;
            if (version == 0 && h.Current == 0 && h.Versions.Count == 0) continue;
            if (SpecJson(h, version) is { } json)
                yield return new ModelUpdate { Kind = h.Kind, Id = h.Id, Version = version, SpecJson = json };
        }
    }

    /// <summary>What a player is sent on arriving at a map that pins nothing.</summary>
    public IEnumerable<ModelUpdate> Updates() => UpdatesFor(null);

    /// <summary>The key a map's pins use for a model: "small_machine:ac_condenser".</summary>
    public static string PinKey(string kind, string id) => Key(kind, id);

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

using System.Text.Json;
using System.Text.Json.Serialization;
using OpenFPS.Server.Repositories;
using Serilog;

namespace OpenFPS.Server.Editor;

/// <summary>One saved version of a map's edits: the whole overlay as it was, with a name and who saved it.</summary>
public sealed class MapVersion
{
    public int Number { get; set; }
    public string Name { get; set; } = "";
    public string Author { get; set; } = "";
    public DateTime SavedUtc { get; set; }
    /// <summary>True for a version the editor saved by itself (before a restore or a bake).</summary>
    public bool Automatic { get; set; }
    public MapOverlay Overlay { get; set; } = new();
}

/// <summary>Every saved version of one map's edits, oldest first.</summary>
public sealed class MapVersionFile
{
    public string MapId { get; set; } = "";
    public List<MapVersion> Versions { get; set; } = new();
}

/// <summary>
/// Named versions of each map's edits (docs/WORLD_EDITOR.md section 18): one file per map,
/// maps/overlays/versions/&lt;mapId&gt;.json, beside the overlays and ignored by git with them. A version
/// is a copy of the whole overlay, so restoring one is laying it in place of the overlay now. A null
/// folder keeps them in memory only (a test rig).
/// </summary>
public sealed class MapVersionStore
{
    /// <summary>The most versions kept for one map; the oldest made by the editor itself go first.</summary>
    public const int MaxVersions = 100;

    private readonly string? _directory;
    private readonly Dictionary<string, MapVersionFile> _files = new(StringComparer.OrdinalIgnoreCase);

    public MapVersionStore(string? overlayDirectory)
    {
        _directory = overlayDirectory == null ? null : Path.Combine(overlayDirectory, "versions");
    }

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters =
        {
            new JsonStringEnumConverter(),
            new OpenFPS.Common.Networking.Vector3Converter(),
            new OpenFPS.Common.Networking.QuaternionConverter(),
        },
    };

    private string? PathFor(string mapId) => _directory == null ? null : Path.Combine(_directory, mapId + ".json");

    /// <summary>A map's versions, oldest first.</summary>
    public IReadOnlyList<MapVersion> Of(string mapId) => FileOf(mapId).Versions;

    private MapVersionFile FileOf(string mapId)
    {
        if (_files.TryGetValue(mapId, out var f)) return f;
        f = Read(mapId) ?? new MapVersionFile { MapId = mapId };
        _files[mapId] = f;
        return f;
    }

    private MapVersionFile? Read(string mapId)
    {
        string? path = PathFor(mapId);
        if (path == null || !File.Exists(path)) return null;
        try
        {
            var f = JsonSerializer.Deserialize<MapVersionFile>(File.ReadAllText(path), MapRepository.JsonOptions);
            if (f != null) f.MapId = mapId;
            return f;
        }
        catch (Exception ex)
        {
            Log.Error("MapVersionStore: {Path} could not be read ({Error}); it is copied to .bad and the map starts a new list.", path, ex.Message);
            try { File.Copy(path, path + ".bad", overwrite: true); } catch { }
            return null;
        }
    }

    /// <summary>A copy of an overlay that shares nothing with it.</summary>
    public static MapOverlay Copy(MapOverlay o)
    {
        var copy = JsonSerializer.Deserialize<MapOverlay>(JsonSerializer.Serialize(o, WriteOptions), MapRepository.JsonOptions)!;
        copy.MapId = o.MapId;
        return copy;
    }

    /// <summary>Keeps a copy of an overlay as the map's next version, and writes the file.</summary>
    public MapVersion Save(string mapId, MapOverlay overlay, string name, string author, bool automatic)
    {
        var f = FileOf(mapId);
        int number = f.Versions.Count == 0 ? 1 : f.Versions.Max(v => v.Number) + 1;
        var version = new MapVersion
        {
            Number = number, Name = name, Author = author, SavedUtc = DateTime.UtcNow, Automatic = automatic, Overlay = Copy(overlay),
        };
        f.Versions.Add(version);
        // Over the limit: the oldest the editor saved by itself goes first, then the oldest of all.
        while (f.Versions.Count > MaxVersions)
            f.Versions.Remove(f.Versions.FirstOrDefault(v => v.Automatic) ?? f.Versions[0]);
        Write(mapId, f);
        return version;
    }

    /// <summary>A version by its number, or by its name (whole, or the start of it, any case).</summary>
    public MapVersion? Find(string mapId, string words)
    {
        var all = Of(mapId);
        string w = words.Trim().TrimStart('#');
        if (w.StartsWith("version ", StringComparison.OrdinalIgnoreCase)) w = w[8..].Trim();
        if (int.TryParse(w, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int n))
            return all.FirstOrDefault(v => v.Number == n);
        return all.LastOrDefault(v => v.Name.Equals(w, StringComparison.OrdinalIgnoreCase))
            ?? all.LastOrDefault(v => v.Name.StartsWith(w, StringComparison.OrdinalIgnoreCase));
    }

    private void Write(string mapId, MapVersionFile f)
    {
        string? path = PathFor(mapId);
        if (path == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(f, WriteOptions));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("MapVersionStore: could not write {Path}: {Error}", path, ex.Message);
        }
    }
}

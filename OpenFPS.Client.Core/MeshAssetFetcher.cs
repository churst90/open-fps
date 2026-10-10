using OpenFPS.Common.Geometry;
using OpenFPS.Common.Networking;

namespace OpenFPS.Client.Core;

/// <summary>
/// The mesh assets a client's definitions name (docs/GEOMETRY.md 4.4): from memory, else the cache on disk (by id,
/// beside the door renders: LocalApplicationData/OpenFPS/meshes, OPENFPS_MESH_CACHE to move it or "off"), else
/// asked of the server once. A thing whose mesh has not come is its box; when it comes, the things that use it are
/// built again (<see cref="ClientWorldState.MeshesArrived"/>).
/// </summary>
public sealed class MeshAssetFetcher
{
    private readonly object _lock = new();
    private readonly Dictionary<string, HashSet<int>> _users = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _wanted = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _asked = new(StringComparer.OrdinalIgnoreCase);
    private readonly MeshLibrary _library;

    public MeshAssetFetcher(MeshLibrary? library = null) => _library = library ?? MeshLibrary.Shared;

    /// <summary>Where assets are kept between sessions; null keeps none.</summary>
    public static string? Folder { get; set; } = DefaultFolder();

    private static string? DefaultFolder()
    {
        string? env = Environment.GetEnvironmentVariable("OPENFPS_MESH_CACHE");
        if (env == "off") return null;
        if (!string.IsNullOrWhiteSpace(env)) return env;
        string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrEmpty(root) ? null : Path.Combine(root, "OpenFPS", "meshes");
    }

    /// <summary>A definition names a mesh: it is here, read from the cache now, or to be asked for.</summary>
    public void Want(string? id, int entityId)
    {
        if (string.IsNullOrEmpty(id)) return;
        lock (_lock)
        {
            if (!_users.TryGetValue(id, out var users)) _users[id] = users = new HashSet<int>();
            users.Add(entityId);
            if (_library.Contains(id) || _asked.Contains(id)) return;
        }
        if (TryCache(id)) return;
        lock (_lock) _wanted.Add(id);
    }

    private bool TryCache(string id)
    {
        if (Folder == null || id.Length != 16 || !id.All(Uri.IsHexDigit)) return false;
        string path = Path.Combine(Folder, id + ".mesh");
        try
        {
            if (!File.Exists(path)) return false;
            var bytes = File.ReadAllBytes(path);
            return _library.Add(MeshAssetData.FromFile(bytes), bytes) == null && _library.Contains(id);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or MemoryPack.MemoryPackSerializationException)
        {
            return false;
        }
    }

    /// <summary>What to ask the server for now (each id once), or null.</summary>
    public MeshAssetRequest? TakeRequest()
    {
        lock (_lock)
        {
            if (_wanted.Count == 0) return null;
            var ids = _wanted.Take(MeshAssetBatch.MostAssets).ToList();
            foreach (var id in ids) { _wanted.Remove(id); _asked.Add(id); }
            return new MeshAssetRequest { Ids = ids };
        }
    }

    /// <summary>A batch arrived: its assets kept (in memory and on disk); the things that use them, to build again.</summary>
    public List<int> Arrived(MeshAssetBatch batch)
    {
        var users = new List<int>();
        foreach (var bytes in batch.Files)
        {
            MeshAssetData data;
            try { data = MeshAssetData.FromFile(bytes); }
            catch (Exception e) when (e is InvalidDataException or MemoryPack.MemoryPackSerializationException) { continue; }
            if (_library.Add(data, bytes) is { } why)
            {
                Serilog.Log.Warning("Mesh {Id} refused: {Why}", data.Id, why);
                continue;
            }
            Save(data.Id, bytes);
            lock (_lock)
                if (_users.TryGetValue(data.Id, out var u)) users.AddRange(u);
        }
        foreach (var id in batch.Missing) Serilog.Log.Warning("The server has no mesh {Id}; things made of it stay boxes.", id);
        return users;
    }

    private static void Save(string id, byte[] bytes)
    {
        if (Folder == null) return;
        try
        {
            Directory.CreateDirectory(Folder);
            string path = Path.Combine(Folder, id + ".mesh");
            if (!File.Exists(path)) File.WriteAllBytes(path, bytes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

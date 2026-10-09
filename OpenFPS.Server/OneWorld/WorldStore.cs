using System.Globalization;
using System.Text.Json;
using Serilog;

namespace OpenFPS.Server.OneWorld;

/// <summary>
/// The world's tiles on the server's disk (docs/WORLD_STREAMING.md, Persistence):
/// <code>
///   ROOT/tiles/v{generator}/{zone}{N|S}/{x}/{z}/full.json.gz   the tile
///   ROOT/tiles/v{generator}/{zone}{N|S}/{x}/{z}.lock           while it is being made
///   ROOT/index.json                                            each tile's size and when it was last visited
/// </code>
/// A tile is written whole (to a temporary name, then renamed), by one generation at a time (a lock file
/// made with CreateNew; one older than ten minutes is broken). A new generator writes beside the old one's
/// tiles and never reads them.
///
/// <para><b>The cap.</b> The store holds at most <see cref="CapBytes"/> (20 GB unless the server's
/// world.json says otherwise). When a write takes it over, the tiles visited least recently are dropped
/// until it is under nine tenths of the cap: an older generator's tiles first (never visited by this
/// one), then by when anyone last stood in or near them. A tile some player has loaded is never dropped. A
/// dropped tile is made again the next time it is wanted, so the cap costs the next visitor a wait, never
/// a hole in the world.</para>
///
/// <para>Thread-safe: the generator writes from its own threads while the game reads.</para>
/// </summary>
public sealed class WorldStore
{
    /// <summary>What makes tiles now. A change to what a tile holds is a new version: the old tiles are kept
    /// beside the new ones, never read, and are the first the cap drops.</summary>
    public const int GeneratorVersion = 1;

    public const long DefaultCapBytes = 20L * 1024 * 1024 * 1024;

    /// <summary>A lock this old belongs to a generation that died.</summary>
    public static readonly TimeSpan StaleLock = TimeSpan.FromMinutes(10);

    public string Root { get; }
    public long CapBytes { get; set; }
    public int Generator { get; }

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _index = new(StringComparer.Ordinal);
    private long _total;
    private bool _indexDirty;

    /// <summary>A stored tile: its size on disk, when it was last visited, and which generator made it.</summary>
    private sealed class Entry
    {
        public long Bytes { get; set; }
        public DateTime VisitedUtc { get; set; }
        public int Generator { get; set; }
    }

    /// <summary>Tiles dropped to keep under the cap since the store opened.</summary>
    public int Evicted { get; private set; }

    public WorldStore(string root, long capBytes = DefaultCapBytes, int generator = GeneratorVersion)
    {
        Root = Path.GetFullPath(root);
        CapBytes = capBytes > 0 ? capBytes : DefaultCapBytes;
        Generator = generator;
        Directory.CreateDirectory(TilesRoot);
        LoadIndex();
    }

    private string TilesRoot => Path.Combine(Root, "tiles");
    private string IndexPath => Path.Combine(Root, "index.json");
    private string VersionRoot(int generator) => Path.Combine(TilesRoot, "v" + generator.ToString(CultureInfo.InvariantCulture));
    private string TileDir(WorldTileKey key, int generator) => Path.Combine(VersionRoot(generator), key.RelativePath);
    private string TileFile(WorldTileKey key, int generator) => Path.Combine(TileDir(key, generator), "full.json.gz");
    private string LockFile(WorldTileKey key) => TileDir(key, Generator) + ".lock";
    private static string IndexKey(WorldTileKey key, int generator) => $"v{generator}/{key}";

    /// <summary>Bytes the store holds now.</summary>
    public long TotalBytes { get { lock (_gate) return _total; } }
    public int Count { get { lock (_gate) return _index.Count; } }

    public bool Has(WorldTileKey key) => File.Exists(TileFile(key, Generator));

    /// <summary>The stored tile, or false when it has not been made (or was dropped).</summary>
    public bool TryRead(WorldTileKey key, out WorldTile tile)
    {
        tile = null!;
        string path = TileFile(key, Generator);
        if (!File.Exists(path)) return false;
        try
        {
            tile = WorldTile.FromBytes(File.ReadAllBytes(path));
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
        {
            // Half a file is not a tile: it is made again.
            Log.Warning(ex, "WorldStore: tile {Key} could not be read; it will be made again.", key);
            Forget(key, Generator);
            return false;
        }
    }

    /// <summary>Writes a tile whole and records it; then keeps the store under its cap, never dropping a tile
    /// <paramref name="inUse"/> says is loaded.</summary>
    public void Write(WorldTileKey key, WorldTile tile, Func<WorldTileKey, bool>? inUse = null)
    {
        var bytes = tile.ToBytes();
        string dir = TileDir(key, Generator);
        Directory.CreateDirectory(dir);
        string path = TileFile(key, Generator);
        string temp = path + "." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
        lock (_gate)
        {
            string k = IndexKey(key, Generator);
            if (_index.TryGetValue(k, out var had)) _total -= had.Bytes;
            _index[k] = new Entry { Bytes = bytes.Length, VisitedUtc = DateTime.UtcNow, Generator = Generator };
            _total += bytes.Length;
            _indexDirty = true;
        }
        KeepUnderCap(inUse);
        SaveIndex();
    }

    /// <summary>Someone stood in or near the tile: it is the last the cap drops.</summary>
    public void Touch(WorldTileKey key)
    {
        lock (_gate)
        {
            if (!_index.TryGetValue(IndexKey(key, Generator), out var e)) return;
            e.VisitedUtc = DateTime.UtcNow;
            _indexDirty = true;
        }
    }

    /// <summary>When the tile was last visited, or null if it is not stored.</summary>
    public DateTime? VisitedUtc(WorldTileKey key)
    {
        lock (_gate) return _index.TryGetValue(IndexKey(key, Generator), out var e) ? e.VisitedUtc : null;
    }

    /// <summary>Drops the least recently visited tiles until the store is under nine tenths of its cap.</summary>
    public void KeepUnderCap(Func<WorldTileKey, bool>? inUse = null)
    {
        List<(string Key, Entry Entry)> victims;
        lock (_gate)
        {
            if (_total <= CapBytes) return;
            long target = CapBytes / 10 * 9;
            victims = new List<(string, Entry)>();
            long freed = 0;
            // An older generator's first (their visits are of tiles nothing reads), then the least recent.
            foreach (var (k, e) in _index.OrderBy(kv => kv.Value.Generator == Generator ? 1 : 0)
                                         .ThenBy(kv => kv.Value.VisitedUtc).ThenBy(kv => kv.Key, StringComparer.Ordinal))
            {
                if (_total - freed <= target) break;
                if (e.Generator == Generator && inUse != null && TryParseIndexKey(k, out var key, out _) && inUse(key)) continue;
                victims.Add((k, e));
                freed += e.Bytes;
            }
        }
        foreach (var (k, e) in victims)
        {
            if (!TryParseIndexKey(k, out var key, out int gen)) continue;
            if (gen == Generator && File.Exists(LockFile(key))) continue;      // being made again right now
            Forget(key, gen);
            Evicted++;
        }
        if (victims.Count > 0)
            Log.Information("WorldStore: over its cap of {Cap:F1} GB; dropped {Count} least recently visited tile(s), {Total:F2} GB now.",
                            CapBytes / 1073741824.0, victims.Count, TotalBytes / 1073741824.0);
    }

    private void Forget(WorldTileKey key, int generator)
    {
        try
        {
            string dir = TileDir(key, generator);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (IOException ex) { Log.Warning(ex, "WorldStore: could not drop tile {Key}.", key); }
        lock (_gate)
        {
            if (_index.Remove(IndexKey(key, generator), out var e)) _total -= e.Bytes;
            _indexDirty = true;
        }
    }

    private static bool TryParseIndexKey(string k, out WorldTileKey key, out int generator)
    {
        key = default; generator = 0;
        int slash = k.IndexOf('/');
        return slash > 1 && k[0] == 'v' && int.TryParse(k.AsSpan(1, slash - 1), NumberStyles.None, CultureInfo.InvariantCulture, out generator)
               && WorldTileKey.TryParse(k[(slash + 1)..], out key);
    }

    // ═══ Making a tile: one at a time ════════════════════════════════════════════════════════════

    /// <summary>
    /// Takes the right to make a tile, across server processes: a lock file made with CreateNew, holding
    /// who made it and when. A lock older than <see cref="StaleLock"/> is broken. False while another
    /// generation holds it.
    /// </summary>
    public bool TryLock(WorldTileKey key)
    {
        string path = LockFile(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var f = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using var w = new StreamWriter(f);
                w.Write($"{Environment.ProcessId} {DateTime.UtcNow:O}");
                return true;
            }
            catch (IOException) when (File.Exists(path))
            {
                var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(path);
                if (age < StaleLock) return false;
                Log.Warning("WorldStore: breaking a stale lock on tile {Key} ({Minutes:F0} minutes old).", key, age.TotalMinutes);
                try { File.Delete(path); } catch (IOException) { return false; }
            }
        }
        return false;
    }

    public void Unlock(WorldTileKey key)
    {
        try { File.Delete(LockFile(key)); } catch (IOException) { }
    }

    // ═══ The index ═══════════════════════════════════════════════════════════════════════════════

    private sealed class IndexFile
    {
        public Dictionary<string, Entry> Tiles { get; set; } = new();
    }

    /// <summary>Reads index.json; any stored tile it does not list is found by looking (its file's time as
    /// its visit), and a listed tile that is gone is forgotten.</summary>
    private void LoadIndex()
    {
        lock (_gate)
        {
            _index.Clear();
            _total = 0;
            try
            {
                if (File.Exists(IndexPath))
                {
                    var file = JsonSerializer.Deserialize<IndexFile>(File.ReadAllText(IndexPath));
                    if (file != null) foreach (var (k, e) in file.Tiles) _index[k] = e;
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                Log.Warning(ex, "WorldStore: index.json could not be read; the tiles are counted again.");
                _index.Clear();
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (Directory.Exists(TilesRoot))
                foreach (var file in Directory.EnumerateFiles(TilesRoot, "full.json.gz", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(TilesRoot, Path.GetDirectoryName(file)!).Replace('\\', '/').Split('/');
                    if (rel.Length != 4 || !rel[0].StartsWith('v')) continue;
                    string k = $"{rel[0]}/{rel[1]}/{rel[2]}/{rel[3]}";
                    if (!TryParseIndexKey(k, out _, out int gen)) continue;
                    seen.Add(k);
                    long bytes = new FileInfo(file).Length;
                    if (_index.TryGetValue(k, out var e)) { e.Bytes = bytes; e.Generator = gen; }
                    else _index[k] = new Entry { Bytes = bytes, VisitedUtc = File.GetLastWriteTimeUtc(file), Generator = gen };
                }
            foreach (var k in _index.Keys.Where(k => !seen.Contains(k)).ToList()) _index.Remove(k);
            foreach (var e in _index.Values) _total += e.Bytes;
            _indexDirty = true;
        }
        SaveIndex();
    }

    /// <summary>Writes index.json if anything changed since it was last written (whole, then renamed).</summary>
    public void SaveIndex()
    {
        string json;
        lock (_gate)
        {
            if (!_indexDirty) return;
            json = JsonSerializer.Serialize(new IndexFile { Tiles = new Dictionary<string, Entry>(_index) });
            _indexDirty = false;
        }
        try
        {
            string temp = IndexPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, IndexPath, overwrite: true);
        }
        catch (IOException ex) { Log.Warning(ex, "WorldStore: index.json could not be written."); }
    }
}

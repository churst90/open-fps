using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Parquet;
using Parquet.Schema;
using Parquet.Serialization;
using Serilog;

namespace OpenFPS.Server.OneWorld;

/// <summary>A building's footprint as Overture Maps has it: the outer ring of its largest polygon (closed, degrees
/// rounded to a ten millionth, as tools/fetch_place.py keeps them), its height and storeys where known, what it is,
/// its name, and where each part of it came from with that source's licence ("dataset|licence").</summary>
public sealed class Footprint
{
    public string Id { get; set; } = "";
    public double[] Lat { get; set; } = Array.Empty<double>();
    public double[] Lon { get; set; } = Array.Empty<double>();
    public double? Height { get; set; }
    public int? Floors { get; set; }
    public string? Class { get; set; }
    public string? Subtype { get; set; }
    public string? Name { get; set; }
    public string[] Sources { get; set; } = Array.Empty<string>();

    private double[]? _bounds;
    private double[] Bounds => _bounds ??= new[] { Lat.Min(), Lon.Min(), Lat.Max(), Lon.Max() };
    [JsonIgnore] public double South => Bounds[0];
    [JsonIgnore] public double West => Bounds[1];
    [JsonIgnore] public double North => Bounds[2];
    [JsonIgnore] public double East => Bounds[3];

    /// <summary>The attribution for a set of buildings from a source: the source, then each of the datasets they came
    /// from with its licence, as the source records them.</summary>
    public static string Attribution(string source, IEnumerable<Footprint> buildings)
    {
        var sources = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var b in buildings)
            foreach (var s in b.Sources)
            {
                int bar = s.IndexOf('|');
                string dataset = bar < 0 ? s : s[..bar], licence = bar < 0 ? "" : s[(bar + 1)..];
                if (dataset.Length == 0) continue;
                sources.Add(dataset == "OpenStreetMap" ? "(c) OpenStreetMap contributors, " + (licence.Length > 0 ? licence : "ODbL-1.0")
                            : licence.Length > 0 ? dataset + ", " + licence : dataset);
            }
        return sources.Count == 0 ? source : source + ": " + string.Join("; ", sources);
    }
}

/// <summary>Where a world tile's buildings come from.</summary>
public interface IBuildingSource
{
    /// <summary>What it is, for the log and the cache's note.</summary>
    string Name { get; }

    /// <summary>
    /// Every building whose footprint reaches into the box (degrees), whole, each once, in order of id. The same
    /// building is the same everywhere it is answered (one release of the data), so two tiles that both see a
    /// house see the same house. Throws when the data cannot be had now.
    /// </summary>
    Task<IReadOnlyList<Footprint>> BuildingsAsync(double south, double west, double north, double east, CancellationToken ct);
}

/// <summary>
/// Overture Maps' buildings theme (docs/WORLD_STREAMING.md, "Buildings on the world's tiles"): Microsoft's footprints
/// traced from imagery, OpenStreetMap's, and others, with heights from USGS lidar, as GeoParquet on Overture's public
/// S3 bucket, read in the server by bounding box.
///
/// <para><b>Reading only what a tile needs.</b> The release's STAC catalogue gives each of its 512 files' bounding
/// box (one request of about 110 KB, once). A file is about 600 MB of some five million buildings in 128 to 256 row
/// groups, sorted by place, and its footer carries every row group's bounding box (the min and max of the bbox
/// columns). So a tile reads: the footer of each file whose box reaches it (1.4 MB, once per file), then, by HTTP byte
/// range, only the columns it uses of only the row groups whose box reaches it (about 2.3 MB for 18,000 buildings, a
/// region some 12 km across in the country, a few hundred metres in a city). Each range is kept in the regional cache
/// as it came (sources/overture/{release}/building/{file}/), so a row group fetched once serves every tile in it, and
/// is read from disk ever after, offline as well. The Parquet is decoded by Parquet.Net (MIT, fully managed, reads a
/// seekable stream: here one that holds only the fetched ranges).</para>
///
/// <para><b>One release for all the data</b> (<see cref="Release"/>), as the roads ask for one date: a building on
/// the edge of two row groups fetched a month apart is the same building in both. Overture keeps about two months of
/// releases online; when this one is withdrawn the server can no longer fetch places it has not been to (the log says
/// so), and moving to a newer release is a new generator version.</para>
///
/// <para><b>Licences.</b> Overture records each source's licence with the building (OpenStreetMap ODbL-1.0, Microsoft
/// ML Buildings ODbL-1.0 as Overture distributes them, USGS lidar heights public domain), and every footprint keeps
/// them; a tile records the sources of the buildings it holds (<see cref="Footprint.Attribution"/>).</para>
/// </summary>
public sealed class OvertureBuildings : IBuildingSource
{
    /// <summary>The release every row group is read from.</summary>
    public const string Release = "2026-09-23.1";

    public const string StacRoot = "https://stac.overturemaps.org";

    public string Name => $"Overture Maps Foundation buildings, release {Release}";

    /// <summary>The release's catalogue of the building files.</summary>
    public static string CollectionUrl => $"{StacRoot}/{Release}/buildings/building/collection.json";

    public string CacheDir { get; }
    private readonly IByteRanges _ranges;
    private readonly Func<string, CancellationToken, Task<byte[]?>> _get;
    private readonly ConcurrentDictionary<string, Lazy<Task<object>>> _loading = new();
    private readonly object _recentGate = new();
    private readonly LinkedList<(string Key, List<Footprint> Rows)> _recent = new();
    private int _requests;
    private long _bytes;

    /// <summary>Row groups kept in memory, decoded (about 18,000 buildings each).</summary>
    public const int RowGroupsInMemory = 8;

    /// <summary>Requests made over the network since this was made, and the bytes they brought (not what came
    /// from the cache).</summary>
    public int Requests => _requests;
    public long BytesFetched => Interlocked.Read(ref _bytes);

    /// <param name="cacheDir">The regional cache (the store's sources/overture).</param>
    /// <param name="ranges">Byte ranges of the release's files (HTTP by default); a test's stands in.</param>
    /// <param name="get">A whole small file (the catalogue), null when there is no such file (HTTP by default).</param>
    public OvertureBuildings(string cacheDir, IByteRanges? ranges = null, Func<string, CancellationToken, Task<byte[]?>>? get = null)
    {
        CacheDir = Path.GetFullPath(cacheDir);
        _ranges = ranges ?? new HttpRanges();
        _get = get ?? GetAsync;
        Directory.CreateDirectory(ReleaseDir);
        string note = Path.Combine(CacheDir, "SOURCE.txt");
        try
        {
            if (!File.Exists(note))
                File.WriteAllText(note, Name + ", from " + CollectionUrl + " and the GeoParquet files it lists (Overture's public S3 bucket).\n"
                                        + "Byte ranges of the files as they came: each file's footer, and the columns a world tile reads of the row groups\n"
                                        + "it needed. Licences as Overture records them for each building's sources: OpenStreetMap (ODbL 1.0, (c) OpenStreetMap\n"
                                        + "contributors), Microsoft ML Buildings (ODbL 1.0 as Overture distributes them), USGS 3DEP lidar heights (public domain),\n"
                                        + "and the others Overture lists. A derived database of OpenStreetMap: the ODbL applies.\n");
        }
        catch (IOException) { }
    }

    private string ReleaseDir => Path.Combine(CacheDir, Release, "building");

    // ═══ Asking ═══════════════════════════════════════════════════════════════════════════════════════

    public async Task<IReadOnlyList<Footprint>> BuildingsAsync(double south, double west, double north, double east, CancellationToken ct)
    {
        var catalogue = await CatalogueAsync(ct).ConfigureAwait(false);
        var byId = new SortedDictionary<string, Footprint>(StringComparer.Ordinal);
        for (int f = 0; f < catalogue.Files.Count; f++)
        {
            var box = catalogue.Files[f].Bbox;
            if (box[2] < west || box[0] > east || box[3] < south || box[1] > north) continue;
            var index = await IndexAsync(catalogue, f, ct).ConfigureAwait(false);
            for (int g = 0; g < index.Groups.Count; g++)
            {
                var rg = index.Groups[g];
                if (rg.East < west || rg.West > east || rg.North < south || rg.South > north) continue;
                foreach (var b in await RowGroupAsync(catalogue, index, g, ct).ConfigureAwait(false))
                {
                    if (b.East < west || b.West > east || b.North < south || b.South > north) continue;
                    byId.TryAdd(b.Id, b);
                }
            }
        }
        return byId.Values.ToList();
    }

    /// <summary>Loads something once however many tiles ask at once; one that failed is asked again next time.</summary>
    private async Task<T> Once<T>(string key, Func<Task<T>> load, CancellationToken ct) where T : class
    {
        var lazy = _loading.GetOrAdd(key, _ => new Lazy<Task<object>>(async () => await load().ConfigureAwait(false)));
        try { return (T)await lazy.Value.WaitAsync(ct).ConfigureAwait(false); }
        catch when (lazy.Value.IsFaulted || lazy.Value.IsCanceled)
        {
            _loading.TryRemove(KeyValuePair.Create(key, lazy));
            throw;
        }
    }

    // ═══ The catalogue ════════════════════════════════════════════════════════════════════════════════

    /// <summary>The release's building files: each one's catalogue entry and bounding box (west, south, east, north).</summary>
    public sealed class Catalogue
    {
        public string Release { get; set; } = "";
        public List<FileEntry> Files { get; set; } = new();
    }

    public sealed class FileEntry
    {
        /// <summary>The file's STAC item, which says where the file is and how big.</summary>
        public string Item { get; set; } = "";
        public double[] Bbox { get; set; } = Array.Empty<double>();
    }

    private Task<Catalogue> CatalogueAsync(CancellationToken ct) => Once("catalogue", () => LoadCatalogueAsync(CancellationToken.None), ct);

    private async Task<Catalogue> LoadCatalogueAsync(CancellationToken ct)
    {
        string path = Path.Combine(ReleaseDir, "catalogue.json");
        if (File.Exists(path))
        {
            try
            {
                var c = JsonSerializer.Deserialize<Catalogue>(await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false));
                if (c != null && c.Release == Release && c.Files.Count > 0) return c;
            }
            catch (JsonException ex) { Log.Warning("World: the Overture catalogue {Path} could not be read ({Error}); asked for again.", path, ex.Message); }
        }
        var bytes = await FetchWholeAsync(CollectionUrl, ct).ConfigureAwait(false)
                    ?? throw new HttpRequestException($"Overture release {Release} is no longer published ({CollectionUrl} is gone); "
                                                      + "the world's buildings need a newer release, which is a new generator version");
        var cat = ParseCollection(bytes, CollectionUrl);
        WriteWhole(path, JsonSerializer.SerializeToUtf8Bytes(cat));
        Log.Information("World: Overture buildings release {Release}: {Files} files catalogued.", Release, cat.Files.Count);
        return cat;
    }

    /// <summary>A STAC collection of the buildings: its items in order, each with its box (the collection's extent
    /// lists the whole first, then one box per item, in the items' order).</summary>
    public static Catalogue ParseCollection(byte[] json, string url)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var items = root.GetProperty("links").EnumerateArray()
                        .Where(l => l.GetProperty("rel").GetString() == "item")
                        .Select(l => l.GetProperty("href").GetString() ?? "").ToList();
        var boxes = root.GetProperty("extent").GetProperty("spatial").GetProperty("bbox").EnumerateArray()
                        .Select(b => b.EnumerateArray().Select(v => v.GetDouble()).ToArray()).ToList();
        if (boxes.Count != items.Count + 1)
            throw new InvalidDataException($"{url}: {items.Count} items and {boxes.Count - 1} boxes");
        var cat = new Catalogue { Release = Release };
        for (int i = 0; i < items.Count; i++)
            cat.Files.Add(new FileEntry { Item = Resolve(url, items[i]), Bbox = boxes[i + 1] });
        return cat;
    }

    private static string Resolve(string baseUrl, string href) => new Uri(new Uri(baseUrl), href).ToString();

    // ═══ A file: where it is, and its row groups ═════════════════════════════════════════════════════

    /// <summary>A file's row groups: each one's box, and where in the file the columns read of it are.</summary>
    private sealed class FileIndex
    {
        public required string Name;
        public required string Url;
        public required long Size;
        public required byte[] Footer;
        public required List<GroupIndex> Groups;
    }

    private sealed class GroupIndex
    {
        public double West, South, East, North;
        public List<(long Offset, long Length)> Ranges = new();
    }

    private sealed class ItemFile
    {
        public string Url { get; set; } = "";
        public long Size { get; set; }
        public double[] Bbox { get; set; } = Array.Empty<double>();
    }

    /// <summary>The columns read: what a footprint is made of (<see cref="OvertureRow"/>).</summary>
    private static bool Wanted(FieldPath path)
    {
        var parts = path.ToList();
        return parts[0] switch
        {
            "id" or "geometry" or "height" or "num_floors" or "class" or "subtype" or "bbox" => true,
            "names" => parts[^1] == "primary" && parts.Count == 2,
            "sources" => parts[^1] is "dataset" or "license",
            _ => false,
        };
    }

    private Task<FileIndex> IndexAsync(Catalogue catalogue, int f, CancellationToken ct)
        => Once("file:" + f, () => LoadIndexAsync(catalogue, f, CancellationToken.None), ct);

    private async Task<FileIndex> LoadIndexAsync(Catalogue catalogue, int f, CancellationToken ct)
    {
        var entry = catalogue.Files[f];
        string dir = Path.Combine(ReleaseDir, f.ToString("00000", CultureInfo.InvariantCulture));
        string itemPath = Path.Combine(dir, "item.json");
        ItemFile? item = null;
        if (File.Exists(itemPath))
        {
            try { item = JsonSerializer.Deserialize<ItemFile>(await File.ReadAllBytesAsync(itemPath, ct).ConfigureAwait(false)); }
            catch (JsonException) { item = null; }
        }
        if (item == null || item.Url.Length == 0)
        {
            var bytes = await FetchWholeAsync(entry.Item, ct).ConfigureAwait(false)
                        ?? throw new HttpRequestException($"Overture release {Release}: {entry.Item} is gone");
            item = ParseItem(bytes, entry.Item);
            // The catalogue's box for this file must be the file's own, or the files are not where the boxes say.
            for (int k = 0; k < 4; k++)
                if (Math.Abs(item.Bbox[k] - entry.Bbox[k]) > 1e-6)
                    throw new InvalidDataException($"Overture release {Release}: the catalogue's box for file {f} is not its item's");
            WriteWhole(itemPath, JsonSerializer.SerializeToUtf8Bytes(item));
        }

        // The footer: its length from the last eight bytes, then the footer itself.
        var tail = await RangeAsync(dir, item.Url, item.Size - 8, 8, ct).ConfigureAwait(false);
        if (tail.Length != 8 || tail[4] != 'P' || tail[5] != 'A' || tail[6] != 'R' || tail[7] != '1')
            throw new InvalidDataException($"{item.Url} is not a Parquet file");
        int footerLength = BinaryPrimitives.ReadInt32LittleEndian(tail);
        var footer = await RangeAsync(dir, item.Url, item.Size - 8 - footerLength, footerLength, ct).ConfigureAwait(false);

        var groups = new List<GroupIndex>();
        await using (var reader = await ParquetReader.CreateAsync(new FetchedStream(item.Size, FooterParts(item.Size, footer, tail)), leaveStreamOpen: false, cancellationToken: ct).ConfigureAwait(false))
        {
            var fields = reader.Schema.DataFields;
            DataField Bbox(string side) => fields.First(d => d.Path.Equals(new FieldPath("bbox", side)));
            var (x0, x1, y0, y1) = (Bbox("xmin"), Bbox("xmax"), Bbox("ymin"), Bbox("ymax"));
            for (int g = 0; g < reader.RowGroupCount; g++)
            {
                var rg = reader.RowGroups[g];
                var gi = new GroupIndex();
                // A row group with no statistics is read whatever box is asked.
                gi.West = Stat(rg.GetStatistics(x0)?.MinValue, double.NegativeInfinity);
                gi.East = Stat(rg.GetStatistics(x1)?.MaxValue, double.PositiveInfinity);
                gi.South = Stat(rg.GetStatistics(y0)?.MinValue, double.NegativeInfinity);
                gi.North = Stat(rg.GetStatistics(y1)?.MaxValue, double.PositiveInfinity);
                foreach (var field in fields)
                {
                    if (!Wanted(field.Path)) continue;
                    var md = rg.GetMetadata(field)?.MetaData;
                    if (md == null) continue;
                    long start = md.DictionaryPageOffset is long d && d > 0 ? Math.Min(d, md.DataPageOffset) : md.DataPageOffset;
                    gi.Ranges.Add((start, md.TotalCompressedSize));
                }
                gi.Ranges = Coalesce(gi.Ranges);
                groups.Add(gi);
            }
        }
        return new FileIndex { Name = f.ToString("00000", CultureInfo.InvariantCulture), Url = item.Url, Size = item.Size, Footer = Concat(footer, tail), Groups = groups };

        static double Stat(object? v, double none) => v == null ? none : Convert.ToDouble(v, CultureInfo.InvariantCulture);
    }

    private static ItemFile ParseItem(byte[] json, string url)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var aws = root.GetProperty("assets").GetProperty("aws");
        return new ItemFile
        {
            Url = Resolve(url, aws.GetProperty("href").GetString() ?? ""),
            Size = aws.GetProperty("file:size").GetInt64(),
            Bbox = root.GetProperty("bbox").EnumerateArray().Select(v => v.GetDouble()).ToArray(),
        };
    }

    /// <summary>Ranges in order, those less than 64 KB apart joined into one request (the gap read and thrown
    /// away): a row group's columns lie in the schema's order, and most of the ones read sit close together.</summary>
    private static List<(long Offset, long Length)> Coalesce(List<(long Offset, long Length)> ranges)
    {
        const long gap = 64 * 1024;
        var sorted = ranges.OrderBy(r => r.Offset).ToList();
        var merged = new List<(long Offset, long Length)>();
        foreach (var (o, l) in sorted)
        {
            if (merged.Count > 0 && o <= merged[^1].Offset + merged[^1].Length + gap)
            {
                var (mo, ml) = merged[^1];
                merged[^1] = (mo, Math.Max(ml, o + l - mo));
            }
            else merged.Add((o, l));
        }
        return merged;
    }

    private static SortedDictionary<long, byte[]> FooterParts(long size, byte[] footer, byte[] tail) => new()
    {
        // Parquet.Net checks the magic at the start; the end's was checked above.
        [0] = "PAR1"u8.ToArray(),
        [size - 8 - footer.Length] = footer,
        [size - 8] = tail,
    };

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var c = new byte[a.Length + b.Length];
        a.CopyTo(c, 0);
        b.CopyTo(c, a.Length);
        return c;
    }

    // ═══ A row group ══════════════════════════════════════════════════════════════════════════════════

    private async Task<List<Footprint>> RowGroupAsync(Catalogue catalogue, FileIndex index, int g, CancellationToken ct)
    {
        string key = index.Name + ":" + g;
        lock (_recentGate)
            for (var n = _recent.First; n != null; n = n.Next)
                if (n.Value.Key == key) { _recent.Remove(n); _recent.AddFirst(n); return n.Value.Rows; }
        var rows = await Once(key, () => LoadRowGroupAsync(index, g, CancellationToken.None), ct).ConfigureAwait(false);
        lock (_recentGate)
        {
            if (!_recent.Any(r => r.Key == key)) _recent.AddFirst((key, rows));
            while (_recent.Count > RowGroupsInMemory) _recent.RemoveLast();
        }
        _loading.TryRemove(key, out _);
        return rows;
    }

    private async Task<List<Footprint>> LoadRowGroupAsync(FileIndex index, int g, CancellationToken ct)
    {
        string dir = Path.Combine(ReleaseDir, index.Name);
        var parts = new SortedDictionary<long, byte[]>
        {
            [0] = "PAR1"u8.ToArray(),
            [index.Size - index.Footer.Length] = index.Footer,
        };
        foreach (var (o, l) in index.Groups[g].Ranges)
            parts[o] = await RangeAsync(dir, index.Url, o, (int)l, ct).ConfigureAwait(false);
        var stream = new FetchedStream(index.Size, parts, (o, l) => RangeAsync(dir, index.Url, o, l, CancellationToken.None).GetAwaiter().GetResult());
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var rows = (await ParquetSerializer.DeserializeAsync<OvertureRow>(stream, rowGroupIndex: g, cancellationToken: ct).ConfigureAwait(false)).Data;
        var list = new List<Footprint>(rows.Count);
        foreach (var r in rows)
            if (ToFootprint(r) is { } fp) list.Add(fp);
        if (stream.Misses > 0)
            Log.Debug("World: Overture file {File} row group {Group} read {Misses} range(s) beyond its columns.", index.Name, g, stream.Misses);
        Log.Debug("World: Overture file {File} row group {Group}: {Count} buildings ({Ms} ms).", index.Name, g, list.Count, clock.ElapsedMilliseconds);
        return list;
    }

    /// <summary>A row as a footprint: the outer ring of its largest polygon; null for one with no polygon.</summary>
    public static Footprint? ToFootprint(OvertureRow r)
    {
        if (r.Geometry == null || r.Id == null) return null;
        var ring = Wkb.LargestOuterRing(r.Geometry);
        if (ring == null || ring.Count < 4) return null;
        var fp = new Footprint
        {
            Id = r.Id,
            Lat = ring.Select(p => Math.Round(p.Lat, 7)).ToArray(),
            Lon = ring.Select(p => Math.Round(p.Lon, 7)).ToArray(),
            Height = r.Height is double h && double.IsFinite(h) && h > 0 ? Math.Round(h, 2) : null,
            Floors = r.NumFloors,
            Class = r.Class,
            Subtype = r.Subtype,
            Name = string.IsNullOrWhiteSpace(r.Names?.Primary) ? null : r.Names!.Primary!.Trim(),
            Sources = r.Sources?.Select(s => string.Intern((s.Dataset ?? "") + "|" + (s.License ?? ""))).Distinct().ToArray() ?? Array.Empty<string>(),
        };
        return fp;
    }

    // ═══ Bytes ════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A range of a file, from the cache or fetched and kept there as it came.</summary>
    private async Task<byte[]> RangeAsync(string dir, string url, long offset, int length, CancellationToken ct)
    {
        string path = Path.Combine(dir, $"r{offset.ToString(CultureInfo.InvariantCulture)}-{length.ToString(CultureInfo.InvariantCulture)}.bin");
        if (File.Exists(path))
        {
            var cached = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            if (cached.Length == length) return cached;
        }
        var bytes = await _ranges.ReadAsync(url, offset, length, ct).ConfigureAwait(false)
                    ?? throw new HttpRequestException($"Overture release {Release}: {url} is gone");
        if (bytes.Length != length) throw new IOException($"{url}: asked for {length} bytes at {offset}, given {bytes.Length}");
        Interlocked.Increment(ref _requests);
        Interlocked.Add(ref _bytes, bytes.Length);
        WriteWhole(path, bytes);
        return bytes;
    }

    private async Task<byte[]?> FetchWholeAsync(string url, CancellationToken ct)
    {
        var bytes = await _get(url, ct).ConfigureAwait(false);
        if (bytes != null)
        {
            Interlocked.Increment(ref _requests);
            Interlocked.Add(ref _bytes, bytes.Length);
        }
        return bytes;
    }

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("openfps-world/0.1");
        return c;
    }

    private static async Task<byte[]?> GetAsync(string url, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
        if (resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    private static void WriteWhole(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Environment.ProcessId + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }
}

/// <summary>The columns of a row of Overture's buildings that a footprint is made of, by their names in the files
/// (Parquet.Net reads these columns and no others).</summary>
public sealed class OvertureRow
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("names")] public OvertureNames? Names { get; set; }
    [JsonPropertyName("sources")] public List<OvertureSource>? Sources { get; set; }
    [JsonPropertyName("height")] public double? Height { get; set; }
    [JsonPropertyName("num_floors")] public int? NumFloors { get; set; }
    [JsonPropertyName("subtype")] public string? Subtype { get; set; }
    [JsonPropertyName("class")] public string? Class { get; set; }
    [JsonPropertyName("geometry")] public byte[]? Geometry { get; set; }
    [JsonPropertyName("bbox")] public OvertureBbox? Bbox { get; set; }
}

public sealed class OvertureNames
{
    [JsonPropertyName("primary")] public string? Primary { get; set; }
}

public sealed class OvertureSource
{
    [JsonPropertyName("dataset")] public string? Dataset { get; set; }
    [JsonPropertyName("license")] public string? License { get; set; }
}

public sealed class OvertureBbox
{
    [JsonPropertyName("xmin")] public double? Xmin { get; set; }
    [JsonPropertyName("xmax")] public double? Xmax { get; set; }
    [JsonPropertyName("ymin")] public double? Ymin { get; set; }
    [JsonPropertyName("ymax")] public double? Ymax { get; set; }
}

/// <summary>Well-known binary polygons (GeoParquet's geometry column), longitude first.</summary>
public static class Wkb
{
    /// <summary>The outer ring of a polygon, or of a multipolygon's largest part (by its area in degrees, as
    /// shapely measures it); null for anything else.</summary>
    public static List<(double Lat, double Lon)>? LargestOuterRing(byte[] wkb)
    {
        int at = 0;
        var polygons = new List<List<(double Lat, double Lon)>>();
        ReadGeometry(wkb, ref at, polygons);
        List<(double Lat, double Lon)>? best = null;
        double bestArea = -1;
        foreach (var ring in polygons)
        {
            double a = 0;
            for (int i = 0; i < ring.Count - 1; i++) a += ring[i].Lon * ring[i + 1].Lat - ring[i + 1].Lon * ring[i].Lat;
            a = Math.Abs(a) / 2;
            if (a > bestArea) { bestArea = a; best = ring; }
        }
        return best;
    }

    private static void ReadGeometry(byte[] b, ref int at, List<List<(double, double)>> polygons)
    {
        bool little = b[at++] == 1;
        uint type = U32(b, ref at, little);
        bool hasZ = (type & 0x80000000) != 0 || (type % 10000) / 1000 is 1 or 3;
        bool hasM = (type & 0x40000000) != 0 || (type % 10000) / 1000 is 2 or 3;
        uint kind = (type & 0x0FFFFFFF) % 1000;
        int dims = 2 + (hasZ ? 1 : 0) + (hasM ? 1 : 0);
        switch (kind)
        {
            case 3:
                {
                    uint rings = U32(b, ref at, little);
                    for (uint r = 0; r < rings; r++)
                    {
                        var ring = Points(b, ref at, little, dims);
                        if (r == 0) polygons.Add(ring);
                    }
                    break;
                }
            case 6:
            case 7:
                {
                    uint parts = U32(b, ref at, little);
                    for (uint p = 0; p < parts; p++) ReadGeometry(b, ref at, polygons);
                    break;
                }
            default:
                throw new InvalidDataException($"a WKB geometry of type {type} where a polygon was expected");
        }
    }

    private static List<(double, double)> Points(byte[] b, ref int at, bool little, int dims)
    {
        uint n = U32(b, ref at, little);
        var pts = new List<(double, double)>((int)n);
        for (uint i = 0; i < n; i++)
        {
            double x = F64(b, at, little), y = F64(b, at + 8, little);
            at += 8 * dims;
            pts.Add((y, x));
        }
        return pts;
    }

    private static uint U32(byte[] b, ref int at, bool little)
    {
        uint v = little ? BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at)) : BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(at));
        at += 4;
        return v;
    }

    private static double F64(byte[] b, int at, bool little)
        => little ? BinaryPrimitives.ReadDoubleLittleEndian(b.AsSpan(at)) : BinaryPrimitives.ReadDoubleBigEndian(b.AsSpan(at));
}

/// <summary>A file of known length of which only some ranges are held, read through as a seekable stream (how
/// Parquet.Net is given a remote file). A read outside them asks <paramref name="fetch"/>, or fails.</summary>
internal sealed class FetchedStream(long length, SortedDictionary<long, byte[]> parts, Func<long, int, byte[]>? fetch = null) : Stream
{
    /// <summary>Reads that fell outside the ranges held.</summary>
    public int Misses;

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => length;
    public override long Position { get; set; }
    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (count == 0 || Position >= length) return 0;
        foreach (var (o, b) in parts)
        {
            if (o > Position) break;
            if (Position < o + b.Length)
            {
                int k = (int)Math.Min(count, o + b.Length - Position);
                Array.Copy(b, Position - o, buffer, offset, k);
                Position += k;
                return k;
            }
        }
        if (fetch == null) throw new IOException($"bytes at {Position} were not fetched");
        Misses++;
        // At least 64 KB at a time: a reader that asks a few bytes at once must not be a request each.
        int want = (int)Math.Min(Math.Max(count, 65536), length - Position);
        var got = fetch(Position, want);
        parts[Position] = got;
        return Read(buffer, offset, count);
    }

    public override long Seek(long offset, SeekOrigin origin)
        => Position = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => Position + offset, _ => length + offset };

    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

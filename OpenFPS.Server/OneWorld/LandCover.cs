using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Serilog;

namespace OpenFPS.Server.OneWorld;

/// <summary>What covers the ground, for a tile's cells (docs/WORLD_STREAMING.md, Stage 2, Land cover).</summary>
public interface ILandCoverSource
{
    /// <summary>What it is, with its licence's attribution, for the tile's record.</summary>
    string Name { get; }

    /// <summary>
    /// The land cover class (ESA WorldCover's codes, <see cref="LandCoverMaterials"/>) at the middle of each of
    /// a tile's cells, <paramref name="cells"/> a side and <paramref name="spacing"/> metres wide from its
    /// south-west corner, row by row from the south; 0 where there is no data. Null when the source covers
    /// none of the tile (the open sea). Throws when it cannot be asked (no network and nothing cached).
    /// </summary>
    Task<byte[]?> ClassesAsync(WorldTileKey key, int cells, double spacing, CancellationToken ct);
}

/// <summary>Byte ranges of files on a server: what a Cloud-Optimised GeoTIFF is read through.</summary>
public interface IByteRanges
{
    /// <summary><paramref name="length"/> bytes from <paramref name="offset"/> (fewer at the end of the file);
    /// null when there is no such file.</summary>
    Task<byte[]?> ReadAsync(string url, long offset, int length, CancellationToken ct);
}

/// <summary>Byte ranges over HTTP, with a generic User-Agent and nothing that says who runs the server.</summary>
public sealed class HttpRanges : IByteRanges
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("openfps-world/0.1");
        return c;
    }

    public async Task<byte[]?> ReadAsync(string url, long offset, int length, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(offset, offset + length - 1);
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        // A server that ignores the range answers the whole file.
        if (resp.StatusCode == HttpStatusCode.OK && bytes.Length > length)
            bytes = bytes.AsSpan((int)Math.Min(offset, bytes.Length), (int)Math.Min(length, Math.Max(0, bytes.Length - offset))).ToArray();
        return bytes;
    }
}

/// <summary>
/// The material a land cover class puts on the ground, from the registry's names (OpenFPS.Common
/// AcousticRegistry: an unknown name would quietly be Generic). One table for every tile, so the same cover is
/// the same ground everywhere.
/// </summary>
public static class LandCoverMaterials
{
    /// <summary>ESA WorldCover 2021 v200's classes (its product user manual, table 1) and the ground each is:
    /// <list type="bullet">
    /// <item>trees and mangroves: the forest floor, leaf litter (Foliage: soft and scattering; footsteps in
    /// leaves);</item>
    /// <item>grassland, wetland, moss and lichen: Grass;</item>
    /// <item>shrubland, cropland, bare or sparse ground, and permanent snow (no snow in the registry): Dirt;</item>
    /// <item>built-up: Asphalt, the paving between buildings (footsteps as cement);</item>
    /// <item>permanent water: Water, the reflecting surface the survey's flattened lakes are.</item>
    /// </list></summary>
    public static readonly IReadOnlyDictionary<byte, string> ByClass = new Dictionary<byte, string>
    {
        [10] = "Foliage",   // tree cover
        [20] = "Dirt",      // shrubland
        [30] = "Grass",     // grassland
        [40] = "Dirt",      // cropland
        [50] = "Asphalt",   // built-up
        [60] = "Dirt",      // bare or sparse vegetation
        [70] = "Dirt",      // snow and ice
        [80] = "Water",     // permanent water bodies
        [90] = "Grass",     // herbaceous wetland
        [95] = "Foliage",   // mangroves
        [100] = "Grass",    // moss and lichen
    };

    /// <summary>What a cell is where there is no land cover: as every cell was before.</summary>
    public const string Default = "Dirt";

    /// <summary>The order materials are listed in a tile, so the same cells are the same bytes.</summary>
    private static readonly string[] Order = { "Dirt", "Grass", "Foliage", "Asphalt", "Water" };

    public static string Of(byte cls) => ByClass.TryGetValue(cls, out var m) ? m : Default;

    /// <summary>A tile's cells and the materials they index, from its classes (null: every cell
    /// <see cref="Default"/>). Only the materials present are listed, in a fixed order.</summary>
    public static (byte[] Cells, string[] Materials) Cells(byte[]? classes, int count)
    {
        var cells = new byte[count];
        if (classes == null) return (cells, new[] { Default });
        var present = new bool[Order.Length];
        foreach (byte c in classes) present[Array.IndexOf(Order, Of(c))] = true;
        var materials = Order.Where((_, k) => present[k]).ToArray();
        if (materials.Length == 0) return (cells, new[] { Default });
        var index = new Dictionary<string, byte>();
        for (int k = 0; k < materials.Length; k++) index[materials[k]] = (byte)k;
        for (int k = 0; k < count; k++) cells[k] = index[Of(classes[k])];
        return (cells, materials);
    }
}

/// <summary>
/// ESA WorldCover 2021 v200 (CC BY 4.0), 10 m land cover classes, read from its Cloud-Optimised GeoTIFFs on
/// S3: one file per 3 x 3 degrees, 36,000 pixels a side in latitude and longitude, in deflated blocks of 1,024
/// pixels (about 8.5 km). Only the blocks a tile's cells fall in are fetched, by byte range, and each is kept
/// in the regional cache as it came (the world store's sources/worldcover): a block fetched once serves every
/// tile in it, offline as well. A file that does not exist is the open sea; that is remembered too.
/// Deterministic: a cell's class is the pixel its middle is in.
/// </summary>
public sealed class EsaWorldCover : ILandCoverSource
{
    public const string Attribution = "ESA WorldCover 2021 v200, CC BY 4.0: (c) ESA WorldCover project 2021 / "
                                      + "Contains modified Copernicus Sentinel data (2021) processed by ESA WorldCover consortium";
    public string Name => Attribution;

    public const string UrlFormat = "https://esa-worldcover.s3.eu-central-1.amazonaws.com/v200/2021/map/ESA_WorldCover_10m_2021_v200_{0}_Map.tif";

    /// <summary>Pixels a degree, and a file's side in degrees.</summary>
    public const int PerDegree = 12000;
    public const int FileDegrees = 3;

    public string CacheDir { get; }
    private readonly IByteRanges _ranges;
    private readonly ConcurrentDictionary<string, Lazy<Task<Header?>>> _headers = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<byte[]>>> _blocks = new();
    private readonly object _recentGate = new();
    private readonly LinkedList<(string Key, byte[] Pixels)> _recent = new();

    /// <summary>Decoded blocks kept in memory (a megabyte each).</summary>
    public const int BlocksInMemory = 6;

    public int Fetches => _fetches;
    private int _fetches;

    public EsaWorldCover(string cacheDir, IByteRanges? ranges = null)
    {
        CacheDir = Path.GetFullPath(cacheDir);
        _ranges = ranges ?? new HttpRanges();
        Directory.CreateDirectory(CacheDir);
        string note = Path.Combine(CacheDir, "SOURCE.txt");
        try
        {
            if (!File.Exists(note))
                File.WriteAllText(note, Attribution + "\nBlocks of " + string.Format(UrlFormat, "{tile}")
                                        + ", as they came (deflated), fetched by byte range when a world tile first wanted them.\n");
        }
        catch (IOException) { }
    }

    /// <summary>The file a point is in ("N30W096"), named by its south-west corner.</summary>
    public static string FileOf(double lat, double lon)
    {
        int la = (int)Math.Floor(lat / FileDegrees) * FileDegrees, lo = (int)Math.Floor(lon / FileDegrees) * FileDegrees;
        return $"{(la >= 0 ? 'N' : 'S')}{Math.Abs(la):00}{(lo >= 0 ? 'E' : 'W')}{Math.Abs(lo):000}";
    }

    public async Task<byte[]?> ClassesAsync(WorldTileKey key, int cells, double spacing, CancellationToken ct)
    {
        var classes = new byte[cells * cells];
        // Each cell's pixel, then the cells grouped by the block they need.
        var need = new Dictionary<(string File, int Row, int Col), List<(int Cell, int Pixel)>>();
        for (int j = 0; j < cells; j++)
            for (int i = 0; i < cells; i++)
            {
                var (lat, lon) = Utm.ToLatLon(key.Zone, key.North, key.Easting + (i + 0.5) * spacing, key.Northing + (j + 0.5) * spacing);
                string file = FileOf(lat, lon);
                double top = Math.Floor(lat / FileDegrees) * FileDegrees + FileDegrees, left = Math.Floor(lon / FileDegrees) * FileDegrees;
                int row = Math.Clamp((int)Math.Floor((top - lat) * PerDegree), 0, FileDegrees * PerDegree - 1);
                int col = Math.Clamp((int)Math.Floor((lon - left) * PerDegree), 0, FileDegrees * PerDegree - 1);
                var block = (file, row / 1024, col / 1024);
                if (!need.TryGetValue(block, out var list)) need[block] = list = new List<(int, int)>();
                list.Add((j * cells + i, (row % 1024) * 1024 + col % 1024));
            }
        bool any = false;
        foreach (var ((file, br, bc), list) in need.OrderBy(kv => kv.Key))
        {
            var header = await HeaderAsync(file, ct).ConfigureAwait(false);
            if (header == null) continue;                       // no file: the sea
            if (header.TileWidth != 1024 || header.TileHeight != 1024)
                throw new InvalidDataException($"WorldCover {file}: blocks of {header.TileWidth} x {header.TileHeight}, not 1024");
            var pixels = await BlockAsync(file, header, br, bc, ct).ConfigureAwait(false);
            foreach (var (cell, pixel) in list) classes[cell] = pixels[pixel];
            any = true;
        }
        return any ? classes : null;
    }

    // ═══ The file's header ═════════════════════════════════════════════════════════════════════════

    /// <summary>What is needed of a file's first image to find and read its blocks.</summary>
    public sealed class Header
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public int TileWidth { get; set; }
        public int TileHeight { get; set; }
        public int Compression { get; set; }
        public int Predictor { get; set; } = 1;
        public long[] Offsets { get; set; } = Array.Empty<long>();
        public int[] Counts { get; set; } = Array.Empty<int>();
    }

    private string HeaderPath(string file) => Path.Combine(CacheDir, file + ".json");
    private string MissingPath(string file) => Path.Combine(CacheDir, file + ".none");
    private string BlockPath(string file, int row, int col) => Path.Combine(CacheDir, file, $"{row}_{col}.bin");

    /// <summary>A file's header, read once however many tiles ask at once (each asker may give up on its own);
    /// one that could not be fetched is asked again next time, not remembered.</summary>
    private async Task<Header?> HeaderAsync(string file, CancellationToken ct)
    {
        var lazy = _headers.GetOrAdd(file, f => new Lazy<Task<Header?>>(() => LoadHeaderAsync(f, CancellationToken.None)));
        try { return await lazy.Value.WaitAsync(ct).ConfigureAwait(false); }
        catch when (lazy.Value.IsFaulted || lazy.Value.IsCanceled)
        {
            _headers.TryRemove(KeyValuePair.Create(file, lazy));
            throw;
        }
    }

    private async Task<Header?> LoadHeaderAsync(string file, CancellationToken ct)
    {
        if (File.Exists(MissingPath(file))) return null;
        if (File.Exists(HeaderPath(file)))
        {
            try { return JsonSerializer.Deserialize<Header>(await File.ReadAllBytesAsync(HeaderPath(file), ct).ConfigureAwait(false)); }
            catch (JsonException ex) { Log.Warning(ex, "WorldCover: {File} could not be read; fetched again.", HeaderPath(file)); }
        }
        string url = string.Format(UrlFormat, file);
        Interlocked.Increment(ref _fetches);
        var first = await _ranges.ReadAsync(url, 0, 16384, ct).ConfigureAwait(false);
        if (first == null)
        {
            WriteWhole(MissingPath(file), System.Text.Encoding.UTF8.GetBytes(url + " does not exist: no land there\n"));
            return null;
        }
        var header = await ParseAsync(url, first, ct).ConfigureAwait(false);
        WriteWhole(HeaderPath(file), JsonSerializer.SerializeToUtf8Bytes(header));
        return header;
    }

    /// <summary>The first image's directory of a classic TIFF, either byte order: its size, blocks, compression,
    /// and where every block is. Reads past <paramref name="first"/> when the lists lie further in.</summary>
    private async Task<Header> ParseAsync(string url, byte[] first, CancellationToken ct)
    {
        var b = first;
        if (b.Length < 8) throw new InvalidDataException("not a TIFF");
        bool le = b[0] == 'I' && b[1] == 'I';
        if (!le && !(b[0] == 'M' && b[1] == 'M')) throw new InvalidDataException("not a TIFF");
        ushort U16(byte[] s, int o) => le ? BinaryPrimitives.ReadUInt16LittleEndian(s.AsSpan(o)) : BinaryPrimitives.ReadUInt16BigEndian(s.AsSpan(o));
        uint U32(byte[] s, int o) => le ? BinaryPrimitives.ReadUInt32LittleEndian(s.AsSpan(o)) : BinaryPrimitives.ReadUInt32BigEndian(s.AsSpan(o));
        if (U16(b, 2) != 42) throw new InvalidDataException("not a classic TIFF");
        int ifd = (int)U32(b, 4);
        if (ifd + 2 > b.Length) throw new InvalidDataException("the TIFF's directory is not in its first block");
        int count = U16(b, ifd);
        if (ifd + 2 + 12 * count > b.Length) throw new InvalidDataException("the TIFF's directory is not in its first block");
        var h = new Header();
        async Task<long[]> Values(int entry)
        {
            ushort type = U16(b, entry + 2);
            int n = (int)U32(b, entry + 4);
            int size = type == 3 ? 2 : type == 16 ? 8 : 4;
            byte[] src = b;
            int at;
            if (n * size <= 4) at = entry + 8;
            else
            {
                at = (int)U32(b, entry + 8);
                if (at + n * size > b.Length)
                {
                    src = await _ranges.ReadAsync(url, at, n * size, ct).ConfigureAwait(false)
                          ?? throw new InvalidDataException("the TIFF ended early");
                    at = 0;
                }
            }
            var v = new long[n];
            for (int k = 0; k < n; k++)
                v[k] = type == 3 ? U16(src, at + 2 * k) : type == 16 ? (long)(le ? BinaryPrimitives.ReadUInt64LittleEndian(src.AsSpan(at + 8 * k))
                                                                                  : BinaryPrimitives.ReadUInt64BigEndian(src.AsSpan(at + 8 * k)))
                                                  : U32(src, at + 4 * k);
            return v;
        }
        int bits = 8, samples = 1, format = 1;
        for (int k = 0; k < count; k++)
        {
            int entry = ifd + 2 + 12 * k;
            switch (U16(b, entry))
            {
                case 256: h.Width = (int)(await Values(entry))[0]; break;
                case 257: h.Height = (int)(await Values(entry))[0]; break;
                case 258: bits = (int)(await Values(entry))[0]; break;
                case 259: h.Compression = (int)(await Values(entry))[0]; break;
                case 277: samples = (int)(await Values(entry))[0]; break;
                case 317: h.Predictor = (int)(await Values(entry))[0]; break;
                case 322: h.TileWidth = (int)(await Values(entry))[0]; break;
                case 323: h.TileHeight = (int)(await Values(entry))[0]; break;
                case 324: h.Offsets = await Values(entry); break;
                case 325: h.Counts = (await Values(entry)).Select(v => (int)v).ToArray(); break;
                case 339: format = (int)(await Values(entry))[0]; break;
            }
        }
        if (bits != 8 || samples != 1 || format != 1) throw new InvalidDataException("not one band of bytes");
        if (h.TileWidth <= 0 || h.Offsets.Length == 0 || h.Offsets.Length != h.Counts.Length) throw new InvalidDataException("not a tiled TIFF");
        if (h.Compression is not (1 or 8 or 32946)) throw new InvalidDataException($"TIFF compression {h.Compression} is not read here");
        return h;
    }

    // ═══ Blocks ══════════════════════════════════════════════════════════════════════════════════════

    private async Task<byte[]> BlockAsync(string file, Header header, int row, int col, CancellationToken ct)
    {
        string key = $"{file}/{row}_{col}";
        lock (_recentGate)
            for (var n = _recent.First; n != null; n = n.Next)
                if (n.Value.Key == key) { _recent.Remove(n); _recent.AddFirst(n); return n.Value.Pixels; }
        var lazy = _blocks.GetOrAdd(key, _ => new Lazy<Task<byte[]>>(() => LoadBlockAsync(file, header, row, col, CancellationToken.None)));
        try
        {
            var pixels = await lazy.Value.WaitAsync(ct).ConfigureAwait(false);
            lock (_recentGate)
            {
                if (!_recent.Any(r => r.Key == key)) _recent.AddFirst((key, pixels));
                while (_recent.Count > BlocksInMemory) _recent.RemoveLast();
            }
            return pixels;
        }
        finally { _blocks.TryRemove(new KeyValuePair<string, Lazy<Task<byte[]>>>(key, lazy)); }
    }

    private async Task<byte[]> LoadBlockAsync(string file, Header header, int row, int col, CancellationToken ct)
    {
        int across = (header.Width + header.TileWidth - 1) / header.TileWidth;
        int index = row * across + col;
        if (index < 0 || index >= header.Offsets.Length) throw new InvalidDataException($"WorldCover {file}: no block {row}, {col}");
        string path = BlockPath(file, row, col);
        byte[]? raw = null;
        if (File.Exists(path))
        {
            raw = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            if (raw.Length != header.Counts[index]) { Log.Warning("WorldCover: {Path} is not whole; fetched again.", path); raw = null; }
        }
        if (raw == null)
        {
            Interlocked.Increment(ref _fetches);
            raw = header.Counts[index] == 0 ? Array.Empty<byte>()
                : await _ranges.ReadAsync(string.Format(UrlFormat, file), header.Offsets[index], header.Counts[index], ct).ConfigureAwait(false)
                  ?? throw new InvalidDataException($"WorldCover {file} is gone");
            if (raw.Length != header.Counts[index]) throw new InvalidDataException($"WorldCover {file}: block {row}, {col} came short");
            WriteWhole(path, raw);
        }
        return Decode(raw, header);
    }

    /// <summary>A block's pixels from its bytes as stored: deflated (zlib) or plain, with or without the
    /// horizontal predictor. A block with no bytes (a sparse file) is all no-data.</summary>
    public static byte[] Decode(byte[] raw, Header header)
    {
        int size = header.TileWidth * header.TileHeight;
        var pixels = new byte[size];
        if (raw.Length == 0) return pixels;
        if (header.Compression == 1) Array.Copy(raw, pixels, Math.Min(raw.Length, size));
        else
        {
            using var z = new ZLibStream(new MemoryStream(raw), CompressionMode.Decompress);
            int got = 0, n;
            while (got < size && (n = z.Read(pixels, got, size - got)) > 0) got += n;
        }
        if (header.Predictor == 2)
            for (int y = 0; y < header.TileHeight; y++)
                for (int x = 1; x < header.TileWidth; x++)
                    pixels[y * header.TileWidth + x] += pixels[y * header.TileWidth + x - 1];
        return pixels;
    }

    private static void WriteWhole(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Environment.ProcessId + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }
}

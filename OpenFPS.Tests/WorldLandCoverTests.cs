using System.IO.Compression;
using System.Text.Json;
using OpenFPS.Common;
using OpenFPS.Server.OneWorld;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// The world's ground materials from ESA WorldCover (docs/WORLD_STREAMING.md, Land cover): the table onto the
/// registry's materials, reading the Cloud-Optimised GeoTIFF by byte range from a recorded fixture (never the
/// network), the regional cache that serves it offline, and dirt with a log line when there is none.
/// </summary>
public class WorldLandCoverTests : IDisposable
{
    private readonly ITestOutputHelper _o;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "openfps-world-cover-" + Guid.NewGuid().ToString("N"));

    public WorldLandCoverTests(ITestOutputHelper o) => _o = o;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Bobcat Lane's tile, Magnolia, Texas: all of it in one block of WorldCover's N30W096.</summary>
    private static readonly WorldTileKey Bobcat = new(15, true, 943, 13342);

    private static string FixturePath([System.Runtime.CompilerServices.CallerFilePath] string here = "")
        => Path.Combine(Path.GetDirectoryName(here)!, "Fixtures", "worldcover_N30W096_bobcat_lane.ranges.gz");

    private static string RepoFile(string relative, [System.Runtime.CompilerServices.CallerFilePath] string here = "")
        => Path.Combine(Path.GetDirectoryName(here)!, "..", relative);

    /// <summary>The byte ranges one reading of WorldCover asked for, as they came, replayed; anything else is
    /// "no network".</summary>
    internal sealed class RecordedRanges : IByteRanges
    {
        private readonly List<(string Url, long Offset, byte[] Bytes)> _ranges = new();
        public int Reads;

        public RecordedRanges(string path)
        {
            using var gz = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
            using var r = new BinaryReader(gz);
            int n = r.ReadInt32();
            for (int k = 0; k < n; k++)
            {
                string url = r.ReadString();
                long offset = r.ReadInt64();
                var bytes = r.ReadBytes(r.ReadInt32());
                _ranges.Add((url, offset, bytes));
            }
        }

        public Task<byte[]?> ReadAsync(string url, long offset, int length, CancellationToken ct)
        {
            Interlocked.Increment(ref Reads);
            foreach (var (u, o, b) in _ranges)
                if (u == url && o == offset) return Task.FromResult<byte[]?>(b.Length > length ? b[..length] : b);
            if (!url.Contains("N30W096")) return Task.FromResult<byte[]?>(null);       // as S3 answers for a file it does not have
            throw new HttpRequestException($"no network (not recorded: {offset} + {length})");
        }

        /// <summary>Writes what <paramref name="live"/> was asked and answered.</summary>
        public static void Write(string path, IReadOnlyList<(string Url, long Offset, byte[] Bytes)> ranges)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var gz = new GZipStream(File.Create(path), CompressionLevel.SmallestSize);
            using var w = new BinaryWriter(gz);
            w.Write(ranges.Count);
            foreach (var (u, o, b) in ranges) { w.Write(u); w.Write(o); w.Write(b.Length); w.Write(b); }
        }
    }

    private sealed class Recording(IByteRanges live) : IByteRanges
    {
        public readonly List<(string Url, long Offset, byte[] Bytes)> Ranges = new();

        public async Task<byte[]?> ReadAsync(string url, long offset, int length, CancellationToken ct)
        {
            var b = await live.ReadAsync(url, offset, length, ct);
            if (b != null) lock (Ranges) Ranges.Add((url, offset, b));
            return b;
        }
    }

    private sealed class NoNetwork : IByteRanges
    {
        public int Reads;
        public Task<byte[]?> ReadAsync(string url, long offset, int length, CancellationToken ct)
        {
            Interlocked.Increment(ref Reads);
            throw new HttpRequestException("no network");
        }
    }

    /// <summary>Records the fixture from S3 (OPENFPS_WORLD_NET=1 only): what reading Bobcat Lane's tile asks.</summary>
    [Fact]
    public async Task Record_the_fixture_from_the_network()
    {
        if (Environment.GetEnvironmentVariable("OPENFPS_WORLD_NET") != "1") return;
        var rec = new Recording(new HttpRanges());
        var cover = new EsaWorldCover(Path.Combine(_dir, "live"), rec);
        var classes = await cover.ClassesAsync(Bobcat, WorldTileService.Posts - 1, WorldTileService.Spacing, CancellationToken.None);
        Assert.NotNull(classes);
        RecordedRanges.Write(FixturePath(), rec.Ranges);
        _o.WriteLine($"recorded {rec.Ranges.Count} ranges, {rec.Ranges.Sum(r => r.Bytes.Length)} bytes, to {FixturePath()}");
    }

    // ═══ The table ═════════════════════════════════════════════════════════════════════════════════

    /// <summary>Every class is a material the registry knows (an unknown one would quietly be Generic).</summary>
    [Fact]
    public void Every_class_is_a_registry_material()
    {
        foreach (var (cls, m) in LandCoverMaterials.ByClass)
            Assert.True(AcousticRegistry.IsKnown(m), $"class {cls} is '{m}', which the registry does not know");
        Assert.True(AcousticRegistry.IsKnown(LandCoverMaterials.Default));
        Assert.Equal("Foliage", LandCoverMaterials.Of(10));
        Assert.Equal("Grass", LandCoverMaterials.Of(30));
        Assert.Equal("Asphalt", LandCoverMaterials.Of(50));
        Assert.Equal("Water", LandCoverMaterials.Of(80));
        Assert.Equal("Dirt", LandCoverMaterials.Of(0));
        Assert.Equal("Dirt", LandCoverMaterials.Of(123));
    }

    /// <summary>Only the materials present are listed, always in the same order, so the same cover is the same
    /// bytes; no cover is one material, dirt, as before.</summary>
    [Fact]
    public void Cells_list_their_materials_in_a_fixed_order()
    {
        var (c0, m0) = LandCoverMaterials.Cells(null, 4);
        Assert.Equal(new[] { "Dirt" }, m0);
        Assert.All(c0, c => Assert.Equal(0, c));
        var (c1, m1) = LandCoverMaterials.Cells(new byte[] { 80, 10, 30, 10 }, 4);
        Assert.Equal(new[] { "Grass", "Foliage", "Water" }, m1);
        Assert.Equal(new byte[] { 2, 1, 0, 1 }, c1);
        var (c2, m2) = LandCoverMaterials.Cells(new byte[] { 10, 80, 10, 30 }, 4);
        Assert.Equal(m1, m2);
        Assert.Equal(new byte[] { 1, 2, 1, 0 }, c2);
        var (c3, m3) = LandCoverMaterials.Cells(new byte[] { 0, 0, 0, 0 }, 4);
        Assert.Equal(new[] { "Dirt" }, m3);
        Assert.All(c3, c => Assert.Equal(0, c));
    }

    // ═══ Reading WorldCover ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Bobcat Lane's tile from the recorded ranges, cell by cell against Magnolia's landcover.json (the same
    /// file read with rasterio by tools/fetch_place.py): the same class at every cell but those whose middle lies
    /// within the window's 1e-7 degree rounding of a pixel's edge. Then the same again from the cache alone.
    /// </summary>
    [Fact]
    public async Task Bobcat_Lane_reads_as_Magnolia_s_own_land_cover()
    {
        var ranges = new RecordedRanges(FixturePath());
        var cover = new EsaWorldCover(Path.Combine(_dir, "sources", "worldcover"), ranges);
        int n = WorldTileService.Posts - 1;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var classes = await cover.ClassesAsync(Bobcat, n, WorldTileService.Spacing, CancellationToken.None);
        long ms = clock.ElapsedMilliseconds;
        Assert.NotNull(classes);
        Assert.Equal(n * n, classes!.Length);

        using var doc = JsonDocument.Parse(File.ReadAllText(RepoFile("tools/places/magnolia_tx/landcover.json")));
        var root = doc.RootElement;
        double west = root.GetProperty("west").GetDouble(), north = root.GetProperty("north").GetDouble();
        double dlon = root.GetProperty("dlon").GetDouble(), dlat = root.GetProperty("dlat").GetDouble();
        var rows = root.GetProperty("rows").EnumerateArray().Select(r => r.GetString()!).ToArray();
        var letter = new Dictionary<char, byte> { ['T'] = 10, ['S'] = 20, ['G'] = 30, ['C'] = 40, ['B'] = 50, ['D'] = 60,
                                                  ['I'] = 70, ['W'] = 80, ['M'] = 90, ['N'] = 95, ['L'] = 100, ['.'] = 0 };
        int same = 0;
        var counts = new Dictionary<byte, int>();
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                var (lat, lon) = Utm.ToLatLon(15, true, Bobcat.Easting + (i + 0.5) * 2, Bobcat.Northing + (j + 0.5) * 2);
                int r = (int)((north - lat) / dlat), c = (int)((lon - west) / dlon);
                byte want = letter[rows[r][c]];
                byte got = classes[j * n + i];
                if (want == got) same++;
                counts[got] = counts.GetValueOrDefault(got) + 1;
            }
        _o.WriteLine($"{Bobcat}: {ms} ms from the recording, {ranges.Reads} ranges read; {same} of {n * n} cells as landcover.json; "
                     + string.Join(", ", counts.OrderBy(k => k.Key).Select(k => $"{LandCoverMaterials.Of(k.Key)} ({k.Key}) {k.Value}")));
        Assert.True(same >= n * n * 0.995, $"{same} of {n * n} cells agree");
        Assert.True(counts.Count >= 2, "a tile of Magnolia is not all one cover");

        // The cache: header and block, as they came. Offline, the same classes, nothing asked.
        var dir = Path.Combine(_dir, "sources", "worldcover");
        Assert.True(File.Exists(Path.Combine(dir, "N30W096.json")));
        Assert.Single(Directory.GetFiles(Path.Combine(dir, "N30W096"), "*.bin"));
        Assert.True(File.Exists(Path.Combine(dir, "SOURCE.txt")));
        var offline = new NoNetwork();
        var again = await new EsaWorldCover(dir, offline).ClassesAsync(Bobcat, n, WorldTileService.Spacing, CancellationToken.None);
        Assert.Equal(classes, again);
        Assert.Equal(0, offline.Reads);
    }

    /// <summary>Where WorldCover has no file (the open sea), the tile is covered by nothing, and that is
    /// remembered: not asked again.</summary>
    [Fact]
    public async Task No_file_is_the_sea_and_is_remembered()
    {
        var ranges = new RecordedRanges(FixturePath());
        var dir = Path.Combine(_dir, "cover");
        var sea = WorldTileKey.OfLatLon(10.5, -140.5);
        Assert.Null(await new EsaWorldCover(dir, ranges).ClassesAsync(sea, 125, 2, CancellationToken.None));
        Assert.Equal(1, ranges.Reads);
        Assert.True(File.Exists(Path.Combine(dir, EsaWorldCover.FileOf(10.5, -140.5) + ".none")));
        var offline = new NoNetwork();
        Assert.Null(await new EsaWorldCover(dir, offline).ClassesAsync(sea, 125, 2, CancellationToken.None));
        Assert.Equal(0, offline.Reads);
    }

    [Theory]
    [InlineData(30.12, -95.74, "N30W096")]
    [InlineData(44.59, -123.11, "N42W126")]
    [InlineData(-33.86, 151.21, "S36E150")]
    [InlineData(0.5, 0.5, "N00E000")]
    [InlineData(-0.5, -0.5, "S03W003")]
    public void Files_are_named_by_their_south_west_corner(double lat, double lon, string name)
        => Assert.Equal(name, EsaWorldCover.FileOf(lat, lon));

    // ═══ Tiles ══════════════════════════════════════════════════════════════════════════════════════

    private sealed class Flat : IElevationSource
    {
        public string Name => "flat";
        public Task<float[]?> HeightsAsync(WorldTileKey key, int posts, double spacing, CancellationToken ct)
            => Task.FromResult<float[]?>(Enumerable.Repeat(60f, posts * posts).ToArray());
    }

    /// <summary>A tile made by the service carries the land cover's materials and its attribution; read back
    /// from the store, the same; its coarse ground (the far ring's) takes them too.</summary>
    [Fact]
    public async Task A_made_tile_has_its_ground_s_materials()
    {
        var store = new WorldStore(Path.Combine(_dir, "world"));
        var service = new WorldTileService(store, new Flat())
        {
            LandCover = new EsaWorldCover(Path.Combine(store.Root, "sources", "worldcover"), new RecordedRanges(FixturePath())),
        };
        var tile = await service.GetAsync(Bobcat);
        Assert.NotNull(tile);
        var t = tile!.Terrain!;
        Assert.Contains("Foliage", t.Materials);
        Assert.True(t.Materials.Length >= 2);
        Assert.Equal(EsaWorldCover.Attribution, tile.LandCover);
        Assert.True(store.TryRead(Bobcat, out var back));
        Assert.Equal(t.Cells, back.Terrain!.Cells);
        Assert.Equal(t.Materials, back.Terrain.Materials);
        var coarse = t.ToComponent().Coarse();
        Assert.Equal(t.Materials, coarse.Materials);
        Assert.True(coarse.Cells.Distinct().Count() >= 2);
        var share = t.Cells.GroupBy(c => t.Materials[c]).ToDictionary(g => g.Key, g => g.Count() * 100.0 / t.Cells.Length);
        _o.WriteLine($"{Bobcat}: " + string.Join(", ", share.Select(kv => $"{kv.Key} {kv.Value:F1} %")) + $"; {store.TotalBytes / 1024.0:F1} KB on disk");
    }

    /// <summary>With no network and nothing cached the tile is still made, every cell dirt, and the log says
    /// why; it is not left unmade.</summary>
    [Fact]
    public async Task No_land_cover_is_dirt()
    {
        var store = new WorldStore(Path.Combine(_dir, "world"));
        var offline = new NoNetwork();
        var service = new WorldTileService(store, new Flat()) { LandCover = new EsaWorldCover(Path.Combine(_dir, "empty"), offline) };
        var tile = await service.GetAsync(Bobcat);
        Assert.NotNull(tile);
        Assert.Equal(new[] { "Dirt" }, tile!.Terrain!.Materials);
        Assert.All(tile.Terrain.Cells, c => Assert.Equal(0, c));
        Assert.Null(tile.LandCover);
        Assert.True(offline.Reads >= 1);
        Assert.True(store.Has(Bobcat));
    }

    /// <summary>The same tile made in either order with its neighbour, by two servers, is the same tile
    /// (all but the time it was made).</summary>
    [Fact]
    public async Task The_same_tile_whichever_is_made_first()
    {
        var east = Bobcat.Offset(1, 0);
        async Task<(byte[] A, byte[] B)> Make(string name, bool bobcatFirst)
        {
            var store = new WorldStore(Path.Combine(_dir, name));
            var service = new WorldTileService(store, new Flat())
            {
                LandCover = new EsaWorldCover(Path.Combine(store.Root, "sources", "worldcover"), new RecordedRanges(FixturePath())),
            };
            var order = bobcatFirst ? new[] { Bobcat, east } : new[] { east, Bobcat };
            foreach (var k in order) await service.GetAsync(k);
            return (Bytes(store, Bobcat), Bytes(store, east));
        }
        static byte[] Bytes(WorldStore store, WorldTileKey k)
        {
            Assert.True(store.TryRead(k, out var t));
            t.MadeUtc = default;
            return t.ToBytes();
        }
        var one = await Make("one", true);
        var two = await Make("two", false);
        Assert.Equal(one.A, two.A);
        Assert.Equal(one.B, two.B);
    }
}

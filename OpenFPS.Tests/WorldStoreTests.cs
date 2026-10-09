using System.Buffers.Binary;
using System.Diagnostics;
using OpenFPS.Server.OneWorld;
using OpenFPS.Server.Repositories;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// The world's tiles on the server (step W1 of docs/WORLD_STREAMING.md "Stage 2 with terrain"): UTM keys,
/// the store and its cap, the locks, and making tiles on demand.
/// </summary>
public class WorldStoreTests : IDisposable
{
    private readonly ITestOutputHelper _o;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "openfps-world-store-" + Guid.NewGuid().ToString("N"));

    public WorldStoreTests(ITestOutputHelper o) => _o = o;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Heights for a made-up tile: a slope and a wave, metres over the sea.</summary>
    private static float[] Hills(WorldTileKey key, int posts = WorldTileService.Posts, float spacing = WorldTileService.Spacing)
    {
        var h = new float[posts * posts];
        for (int j = 0; j < posts; j++)
            for (int i = 0; i < posts; i++)
            {
                double e = key.Easting + i * spacing, n = key.Northing + j * spacing;
                h[j * posts + i] = (float)(60 + 0.01 * (e % 1000) + 3 * Math.Sin(n / 70.0));
            }
        return h;
    }

    // ═══ UTM ═══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Against PROJ (pyproj 3, EPSG:4326 to the zone's EPSG code, 2026-10-09), to the centimetre, and
    /// back to a ten millionth of a degree.</summary>
    [Theory]
    [InlineData(30.123703, -95.740935, 15, true, 235921.30, 3335664.38)]     // Magnolia, Texas
    [InlineData(44.590205, -123.113244, 10, true, 491011.25, 4937435.25)]    // Albany, Oregon
    [InlineData(-33.8568, 151.2153, 56, false, 334900.57, 6252288.75)]       // Sydney
    [InlineData(64.1466, -21.9426, 27, true, 454138.38, 7113689.87)]         // Reykjavik
    [InlineData(0.0, -93.0, 15, true, 500000.0, 0.0)]                        // the equator on zone 15's middle
    public void Utm_goes_there_and_back(double lat, double lon, int zone, bool north, double easting, double northing)
    {
        var (z, n, e, nn) = Utm.FromLatLon(lat, lon);
        Assert.Equal(zone, z);
        Assert.Equal(north, n);
        Assert.Equal(easting, e, 0.015);
        Assert.Equal(northing, nn, 0.015);
        var (lat2, lon2) = Utm.ToLatLon(z, n, e, nn);
        Assert.True(Math.Abs(lat2 - lat) < 1e-7 && Math.Abs(lon2 - lon) < 1e-7, $"({lat}, {lon}) came back as ({lat2}, {lon2})");
        Assert.InRange(e, 160_000, 840_000);
        if (lat == 0 && lon == -93) { Assert.Equal(500_000, e, 3); Assert.Equal(0, nn, 3); }
        _o.WriteLine($"({lat}, {lon}): zone {z}{(n ? 'N' : 'S')}, {e:F2} E, {nn:F2} N, tile {WorldTileKey.OfLatLon(lat, lon)}");
    }

    /// <summary>A kilometre along a meridian is a kilometre of northing, scaled by the zone's 0.9996 in its
    /// middle: the grid is metres.</summary>
    [Fact]
    public void Utm_metres_are_metres()
    {
        // One kilometre north of a point on zone 15's middle, by the meridian's length per degree there.
        var (_, _, e0, n0) = Utm.FromLatLon(30.0, -93.0);
        var (_, _, e1, n1) = Utm.FromLatLon(30.0 + 1.0 / 110.85246, -93.0);   // 110,852.46 m a degree at 30 N
        Assert.Equal(e0, e1, 3);
        Assert.Equal(1000 * 0.9996, n1 - n0, 0);
    }

    [Fact]
    public void A_tile_key_reads_and_writes()
    {
        var key = new WorldTileKey(15, true, 1023, 13345);
        Assert.Equal("15N/1023/13345", key.ToString());
        Assert.True(WorldTileKey.TryParse("15N/1023/13345", out var back));
        Assert.Equal(key, back);
        Assert.True(WorldTileKey.TryParse("56s/-3/24000", out var south));
        Assert.Equal(new WorldTileKey(56, false, -3, 24000), south);
        foreach (var bad in new[] { "", "15/1/2", "61N/1/2", "15N/x/2", "15N/1", "15Q/1/2" })
            Assert.False(WorldTileKey.TryParse(bad, out _), bad);
        Assert.Equal(255_750, key.Easting);
        Assert.Equal(3_336_250, key.Northing);
        Assert.Equal(WorldTileKey.Of(15, true, 255_999.9, 3_336_250.0), key);
    }

    // ═══ The store ═════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void A_tile_is_written_whole_and_read_back()
    {
        var store = new WorldStore(_dir);
        var key = new WorldTileKey(15, true, 1023, 13345);
        Assert.False(store.Has(key));
        var tile = WorldTileService.Make(key, Hills(key), "test");
        store.Write(key, tile);
        Assert.True(store.TryRead(key, out var back));
        Assert.Equal(tile.Terrain!.HeightsCm, back.Terrain!.HeightsCm);
        Assert.Equal(tile.Terrain.BaseY, back.Terrain.BaseY);
        Assert.Equal("15N/1023/13345", back.Key);
        Assert.Empty(Directory.EnumerateFiles(_dir, "*.tmp", SearchOption.AllDirectories));
        Assert.True(File.Exists(Path.Combine(_dir, "tiles", "v1", "15N", "1023", "13345", "full.json.gz")));
        long total = store.TotalBytes;
        Assert.True(total > 0);
        _o.WriteLine($"a tile of hills: {total / 1024.0:F1} KB on disk");

        // Opened again, the store knows what it holds.
        var again = new WorldStore(_dir);
        Assert.Equal(total, again.TotalBytes);
        Assert.Equal(1, again.Count);
        Assert.True(again.Has(key));
    }

    /// <summary>
    /// Over its cap, the store drops the tiles visited least recently until it is under nine tenths of it,
    /// never one somebody has loaded; a dropped tile is simply not there, and is made again when wanted.
    /// </summary>
    [Fact]
    public void The_cap_drops_the_least_recently_visited_tiles()
    {
        var keys = Enumerable.Range(0, 4).Select(i => new WorldTileKey(15, true, 100 + i, 200)).ToList();
        // Room for the first four and not a fifth, by what each takes.
        var probe = new WorldStore(Path.Combine(_dir, "probe"));
        var sizes = new List<long>();
        foreach (var k in keys)
        {
            long before = probe.TotalBytes;
            probe.Write(k, WorldTileService.Make(k, Hills(k), "test"));
            sizes.Add(probe.TotalBytes - before);
        }

        var store = new WorldStore(Path.Combine(_dir, "store"), capBytes: sizes.Sum() + sizes.Min() / 2);
        foreach (var k in keys) { store.Write(k, WorldTileService.Make(k, Hills(k), "test")); Thread.Sleep(20); }
        Assert.Equal(4, store.Count);
        // The first is visited again, so the second is now the least recent; the third is loaded.
        store.Touch(keys[0]);
        var loaded = keys[2];
        Thread.Sleep(20);
        var fifth = new WorldTileKey(15, true, 200, 200);
        store.Write(fifth, WorldTileService.Make(fifth, Hills(fifth), "test"), inUse: k => k == loaded);
        Assert.True(store.TotalBytes <= store.CapBytes, $"{store.TotalBytes} over {store.CapBytes}");
        Assert.False(store.Has(keys[1]), "the least recently visited is kept");
        Assert.True(store.Has(keys[0]), "a tile visited since was dropped");
        Assert.True(store.Has(loaded), "a loaded tile was dropped");
        Assert.True(store.Has(fifth));
        Assert.True(store.Evicted >= 1);
        Assert.False(store.TryRead(keys[1], out _));
    }

    /// <summary>A new generator's tiles live beside the old one's, and the old are the first the cap drops.</summary>
    [Fact]
    public void An_older_generator_is_dropped_first()
    {
        var old = new WorldStore(_dir, generator: 1);
        var a = new WorldTileKey(15, true, 1, 1);
        old.Write(a, WorldTileService.Make(a, Hills(a), "test"));
        long one = old.TotalBytes;
        Thread.Sleep(20);
        var store = new WorldStore(_dir, capBytes: one * 2 + one / 2, generator: 2);
        Assert.False(store.Has(a), "a new generator reads an old one's tile");
        var b = new WorldTileKey(15, true, 2, 1);
        var c = new WorldTileKey(15, true, 3, 1);
        store.Write(b, WorldTileService.Make(b, Hills(b), "test"));
        store.Touch(b);
        store.Write(c, WorldTileService.Make(c, Hills(c), "test"));
        Assert.True(store.Has(b) && store.Has(c));
        Assert.False(Directory.Exists(Path.Combine(_dir, "tiles", "v1", "15N", "1", "1")), "the old generator's tile is still there");
    }

    [Fact]
    public void One_making_at_a_time_and_a_stale_lock_is_broken()
    {
        var store = new WorldStore(_dir);
        var key = new WorldTileKey(15, true, 5, 5);
        Assert.True(store.TryLock(key));
        Assert.False(store.TryLock(key));
        store.Unlock(key);
        Assert.True(store.TryLock(key));
        // A lock left by a server that died eleven minutes ago.
        string lockFile = Directory.EnumerateFiles(Path.Combine(_dir, "tiles"), "*.lock", SearchOption.AllDirectories).Single();
        File.SetLastWriteTimeUtc(lockFile, DateTime.UtcNow - TimeSpan.FromMinutes(11));
        Assert.True(store.TryLock(key));
        store.Unlock(key);
    }

    // ═══ Making tiles ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>A survey that counts what it is asked, takes a while, and can be made to fail or to have
    /// nothing.</summary>
    private sealed class FakeSurvey : IElevationSource
    {
        public string Name => "a made-up survey";
        public int Calls, AtOnce, MostAtOnce;
        public bool Fail, Nothing;
        public int DelayMs = 100;

        public async Task<float[]?> HeightsAsync(WorldTileKey key, int posts, double spacing, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            int now = Interlocked.Increment(ref AtOnce);
            lock (this) MostAtOnce = Math.Max(MostAtOnce, now);
            try
            {
                await Task.Delay(DelayMs, ct);
                if (Fail) throw new HttpRequestException("no network");
                return Nothing ? null : Hills(key, posts, (float)spacing);
            }
            finally { Interlocked.Decrement(ref AtOnce); }
        }
    }

    [Fact]
    public async Task A_tile_is_made_once_and_read_from_disk_after()
    {
        var survey = new FakeSurvey();
        var service = new WorldTileService(new WorldStore(_dir), survey);
        var key = new WorldTileKey(15, true, 1023, 13345);
        Assert.Equal(WorldTileService.TileState.Unknown, service.StateOf(key));
        var asks = Enumerable.Range(0, 5).Select(_ => service.GetAsync(key)).ToList();
        Assert.Equal(WorldTileService.TileState.Making, service.StateOf(key));
        var tiles = await Task.WhenAll(asks);
        Assert.All(tiles, t => Assert.NotNull(t));
        Assert.Equal(1, survey.Calls);
        Assert.Equal(WorldTileService.TileState.Ready, service.StateOf(key));
        Assert.Equal(survey.Name, tiles[0]!.Source);

        // Another server, or this one after a restart: from disk, the survey not asked.
        var later = new WorldTileService(new WorldStore(_dir), survey);
        Assert.NotNull(await later.GetAsync(key));
        Assert.Equal(1, survey.Calls);
    }

    [Fact]
    public async Task At_most_two_are_made_at_once()
    {
        var survey = new FakeSurvey { DelayMs = 150 };
        var service = new WorldTileService(new WorldStore(_dir), survey, maxAtOnce: 2);
        var keys = Enumerable.Range(0, 6).Select(i => new WorldTileKey(15, true, i, 0)).ToList();
        var made = await Task.WhenAll(keys.Select(service.GetAsync));
        Assert.All(made, t => Assert.NotNull(t));
        Assert.Equal(6, survey.Calls);
        Assert.Equal(2, survey.MostAtOnce);
        Assert.Equal(6, service.MadeCount);
    }

    /// <summary>When the survey cannot be asked the tile is not stored, and is tried again after a while; where
    /// the survey has nothing, the tile is flat open ground at sea level, stored.</summary>
    [Fact]
    public async Task A_tile_that_cannot_be_made_is_tried_again_and_nowhere_is_open_ground()
    {
        var survey = new FakeSurvey { Fail = true, DelayMs = 10 };
        var service = new WorldTileService(new WorldStore(_dir), survey) { RetryAfter = TimeSpan.FromSeconds(30) };
        var key = new WorldTileKey(15, true, 7, 7);
        Assert.Null(await service.GetAsync(key));
        Assert.False(service.Store.Has(key));
        Assert.Equal(WorldTileService.TileState.Failed, service.StateOf(key));
        Assert.Null(await service.GetAsync(key));
        Assert.Equal(1, survey.Calls);                 // waiting out the failure, not asking again

        service.RetryAfter = TimeSpan.Zero;
        Assert.Null(await service.GetAsync(key));      // the wait that was set still runs
        survey.Fail = false;
        survey.Nothing = true;
        var quick = new WorldTileService(service.Store, survey) { RetryAfter = TimeSpan.Zero };
        var tile = await quick.GetAsync(key);
        Assert.NotNull(tile);
        Assert.Equal("none", tile!.Source);
        Assert.All(tile.Terrain!.HeightsCm, h => Assert.Equal(0, h));
        Assert.Equal(0f, tile.Terrain.BaseY);
        Assert.True(quick.Store.Has(key));
    }

    // ═══ The survey's answer ════════════════════════════════════════════════════════════════════════

    private static byte[] Tiff(int w, int h, Func<int, int, float> value, bool bigEndian, int tile = 0)
    {
        // A header, the picture, then the directory.
        var body = new List<byte>();
        void U16(List<byte> l, int v) { var s = new byte[2]; if (bigEndian) BinaryPrimitives.WriteUInt16BigEndian(s, (ushort)v); else BinaryPrimitives.WriteUInt16LittleEndian(s, (ushort)v); l.AddRange(s); }
        void U32(List<byte> l, uint v) { var s = new byte[4]; if (bigEndian) BinaryPrimitives.WriteUInt32BigEndian(s, v); else BinaryPrimitives.WriteUInt32LittleEndian(s, v); l.AddRange(s); }
        body.AddRange(bigEndian ? "MM"u8.ToArray() : "II"u8.ToArray());
        U16(body, 42);
        U32(body, 0);                                   // the directory's place, filled in below
        var offsets = new List<uint>(); var lengths = new List<uint>();
        if (tile > 0)
        {
            for (int ty = 0; ty < h; ty += tile)
                for (int tx = 0; tx < w; tx += tile)
                {
                    offsets.Add((uint)body.Count);
                    for (int y = 0; y < tile; y++)
                        for (int x = 0; x < tile; x++)
                            U32(body, BitConverter.SingleToUInt32Bits(tx + x < w && ty + y < h ? value(tx + x, ty + y) : 0f));
                    lengths.Add((uint)(tile * tile * 4));
                }
        }
        else
        {
            offsets.Add((uint)body.Count);
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) U32(body, BitConverter.SingleToUInt32Bits(value(x, y)));
            lengths.Add((uint)(w * h * 4));
        }
        // Arrays of offsets out of line.
        uint offAt = (uint)body.Count; foreach (var o in offsets) U32(body, o);
        uint lenAt = (uint)body.Count; foreach (var l in lengths) U32(body, l);
        uint ifd = (uint)body.Count;
        var entries = new List<(int Tag, int Type, uint Count, uint Value)>
        {
            (256, 3, 1, (uint)w), (257, 3, 1, (uint)h), (258, 3, 1, 32), (259, 3, 1, 1), (277, 3, 1, 1), (339, 3, 1, 3),
        };
        if (tile > 0) { entries.Add((322, 3, 1, (uint)tile)); entries.Add((323, 3, 1, (uint)tile)); entries.Add((324, 4, (uint)offsets.Count, offsets.Count == 1 ? offsets[0] : offAt)); entries.Add((325, 4, (uint)lengths.Count, lengths.Count == 1 ? lengths[0] : lenAt)); }
        else { entries.Add((273, 4, 1, offsets[0])); entries.Add((278, 3, 1, (uint)h)); entries.Add((279, 4, 1, lengths[0])); }
        entries.Sort((a, b) => a.Tag.CompareTo(b.Tag));
        U16(body, entries.Count);
        foreach (var (tag, type, count, v) in entries)
        {
            U16(body, tag); U16(body, type); U32(body, count);
            if (type == 3 && count == 1) { U16(body, (int)v); U16(body, 0); } else U32(body, v);
        }
        U32(body, 0);
        var bytes = body.ToArray();
        if (bigEndian) BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4), ifd); else BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), ifd);
        return bytes;
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 16)]
    [InlineData(true, 16)]
    public void The_surveys_float_tiff_is_read(bool bigEndian, int tile)
    {
        var grid = FloatTiff.Read(Tiff(37, 21, (x, y) => 50f + x * 0.5f - y * 0.25f, bigEndian, tile));
        Assert.Equal(37, grid.Width);
        Assert.Equal(21, grid.Height);
        for (int y = 0; y < 21; y++)
            for (int x = 0; x < 37; x++)
                Assert.Equal(50f + x * 0.5f - y * 0.25f, grid.Values[y * 37 + x]);
    }

    /// <summary>
    /// What a tile costs on disk: Magnolia's own ground (the map's survey) laid as a world tile, 2 m posts,
    /// gzip JSON. Tens of kilobytes, so a 20 GB store holds several hundred thousand tiles: every tile of
    /// a few US states.
    /// </summary>
    [Fact]
    public void What_a_tile_of_real_ground_costs_on_disk()
    {
        var map = MapRepository.LoadFromFile(Path.Combine(AppContext.BaseDirectory, "places", "magnolia_tx.json"));
        Assert.NotNull(map?.Elevation);
        var store = new WorldStore(_dir);
        var sizes = new List<long>();
        for (int t = 0; t < 4; t++)
        {
            var key = new WorldTileKey(15, true, t, 0);
            var h = new float[WorldTileService.Posts * WorldTileService.Posts];
            for (int j = 0; j < WorldTileService.Posts; j++)
                for (int i = 0; i < WorldTileService.Posts; i++)
                    h[j * WorldTileService.Posts + i] = (float)map!.Elevation!.HeightAt(-500 + t * 250 + i * 2.0, -125 + j * 2.0) + 60f;
            long before = store.TotalBytes;
            store.Write(key, WorldTileService.Make(key, h, "test"));
            sizes.Add(store.TotalBytes - before);
        }
        _o.WriteLine($"tiles of Magnolia's ground: {string.Join(", ", sizes.Select(s => $"{s / 1024.0:F1} KB"))}; a 20 GB cap holds about {WorldStore.DefaultCapBytes / sizes.Average():N0} of them");
        Assert.All(sizes, s => Assert.InRange(s, 1000, 60 * 1024));
    }

    /// <summary>A tile from the real survey, over the network (OPENFPS_WORLD_NET=1 only): Bobcat Lane's
    /// tile, its heights against Magnolia's own survey, and what it took.</summary>
    [Fact]
    public async Task A_real_tile_from_3dep()
    {
        if (Environment.GetEnvironmentVariable("OPENFPS_WORLD_NET") != "1") return;
        var key = WorldTileKey.OfLatLon(30.123703, -95.740935);
        var clock = Stopwatch.StartNew();
        var service = new WorldTileService(new WorldStore(_dir), new Usgs3Dep());
        var tile = await service.GetAsync(key);
        Assert.NotNull(tile);
        var t = tile!.Terrain!;
        float lo = t.BaseY, hi = t.BaseY + t.HeightsCm.Max() * 0.01f;
        _o.WriteLine($"{key}: made in {clock.ElapsedMilliseconds} ms, {service.Store.TotalBytes / 1024.0:F1} KB on disk, ground {lo:F2} to {hi:F2} m");
        Assert.InRange(lo, 50f, 80f);
        Assert.Equal(tile.Source, new Usgs3Dep().Name);
    }
}

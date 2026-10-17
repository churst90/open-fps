using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Serilog;

namespace OpenFPS.Server.OneWorld;

/// <summary>An OpenStreetMap way: its nodes, where each is, and its tags.</summary>
public sealed class OsmWay
{
    public long Id { get; set; }
    public long[] Nodes { get; set; } = Array.Empty<long>();
    public double[] Lat { get; set; } = Array.Empty<double>();
    public double[] Lon { get; set; } = Array.Empty<double>();
    public Dictionary<string, string> Tags { get; set; } = new();

    public string Tag(string key, string fallback = "") => Tags.TryGetValue(key, out var v) ? v : fallback;
}

/// <summary>Where a world tile's OpenStreetMap features come from.</summary>
public interface IOsmSource
{
    /// <summary>What it is, with the attribution its licence asks for.</summary>
    string Name { get; }

    /// <summary>
    /// Every way with a highway tag that reaches into the box (degrees), whole, each once, in order of id. The
    /// same way is the same everywhere it is answered (one date of the data), so two tiles that both see a
    /// road see the same road. Throws when the data cannot be had now.
    /// </summary>
    Task<IReadOnlyList<OsmWay>> HighwaysAsync(double south, double west, double north, double east, CancellationToken ct);
}

/// <summary>
/// OpenStreetMap (ODbL) through the Overpass API, a region at a time (docs/WORLD_STREAMING.md, Stage 2: data),
/// kept in the world store's regional cache (sources/osm): a region is a square of <see cref="RegionDegrees"/>
/// (about 5.5 x 4.8 km at 30 degrees north, a few hundred world tiles), asked for once, the first time any tile
/// in it is made, and read from disk ever after, offline as well.
///
/// <para>Every region is asked for the data as it stood at one moment, <see cref="DataDate"/> (Overpass's
/// date setting), so a road that runs through two regions fetched a month apart is the same road in both: a
/// tile made from either agrees with its neighbour. A newer date is a new generator version.</para>
///
/// <para>Overpass's usage policy is about 10,000 requests and a gigabyte a day for the public instances; a
/// region is one request of a few hundred kilobytes, asked one at a time, a second apart at least, and
/// retried with backing off when the server is busy (504, 429). A server that makes tiles faster than that
/// should read a Geofabrik extract instead, through the same interface.</para>
/// </summary>
public sealed class OverpassRegions : IOsmSource
{
    public const string Endpoint = "https://overpass-api.de/api/interpreter";

    /// <summary>The moment every region's data is asked for.</summary>
    public const string DataDate = "2026-10-01T00:00:00Z";

    /// <summary>A region's side, degrees of latitude and of longitude (twenty to the degree).</summary>
    public const int RegionsPerDegree = 20;
    public const double RegionDegrees = 1.0 / RegionsPerDegree;

    public string Name => $"(c) OpenStreetMap contributors, ODbL 1.0 (Overpass API, the data as of {DataDate[..10]})";

    public string CacheDir { get; }
    private readonly Func<string, CancellationToken, Task<string>> _ask;
    private readonly ConcurrentDictionary<(int, int), Lazy<Task<List<OsmWay>>>> _loading = new();
    private readonly object _recentGate = new();
    private readonly LinkedList<((int, int) Key, List<OsmWay> Ways)> _recent = new();
    private static readonly SemaphoreSlim OneAtATime = new(1, 1);
    private static DateTime _lastAsked = DateTime.MinValue;
    private int _fetches;

    /// <summary>Regions asked of Overpass since this was made (not those read from the cache).</summary>
    public int Fetches => _fetches;

    /// <summary>Regions kept in memory, parsed.</summary>
    public const int RegionsInMemory = 12;

    /// <param name="cacheDir">The regional cache (the store's sources/osm).</param>
    /// <param name="ask">Asks Overpass a query and gives its answer (HTTP by default); a test's stands in.</param>
    public OverpassRegions(string cacheDir, Func<string, CancellationToken, Task<string>>? ask = null)
    {
        CacheDir = Path.GetFullPath(cacheDir);
        _ask = ask ?? AskOverpassAsync;
        Directory.CreateDirectory(RegionDir);
        string note = Path.Combine(CacheDir, "SOURCE.txt");
        try
        {
            if (!File.Exists(note))
                File.WriteAllText(note, Name + "\nWays with a highway tag, a region of " + RegionDegrees.ToString(CultureInfo.InvariantCulture)
                                        + " degrees at a time, from " + Endpoint + ". A derived database of OpenStreetMap: the ODbL applies.\n");
        }
        catch (IOException) { }
    }

    private string RegionDir => Path.Combine(CacheDir, "highways-" + DataDate[..10]);
    private string RegionPath((int Lat, int Lon) r) => Path.Combine(RegionDir, $"{r.Lat}_{r.Lon}.json.gz");

    /// <summary>The region a point is in: its south-west corner in twentieths of a degree.</summary>
    public static (int Lat, int Lon) RegionOf(double lat, double lon)
        => ((int)Math.Floor(lat * RegionsPerDegree), (int)Math.Floor(lon * RegionsPerDegree));

    public async Task<IReadOnlyList<OsmWay>> HighwaysAsync(double south, double west, double north, double east, CancellationToken ct)
    {
        var (r0, c0) = RegionOf(south, west);
        var (r1, c1) = RegionOf(north, east);
        var byId = new SortedDictionary<long, OsmWay>();
        for (int r = r0; r <= r1; r++)
            for (int c = c0; c <= c1; c++)
                foreach (var w in await RegionAsync((r, c), ct).ConfigureAwait(false))
                    byId.TryAdd(w.Id, w);
        return byId.Values.ToList();
    }

    private async Task<List<OsmWay>> RegionAsync((int, int) key, CancellationToken ct)
    {
        lock (_recentGate)
            for (var n = _recent.First; n != null; n = n.Next)
                if (n.Value.Key == key) { _recent.Remove(n); _recent.AddFirst(n); return n.Value.Ways; }
        var lazy = _loading.GetOrAdd(key, k => new Lazy<Task<List<OsmWay>>>(() => LoadRegionAsync(k, CancellationToken.None)));
        try
        {
            var ways = await lazy.Value.WaitAsync(ct).ConfigureAwait(false);
            lock (_recentGate)
            {
                if (!_recent.Any(r => r.Key == key)) _recent.AddFirst((key, ways));
                while (_recent.Count > RegionsInMemory) _recent.RemoveLast();
            }
            return ways;
        }
        finally
        {
            if (lazy.Value.IsCompleted) _loading.TryRemove(KeyValuePair.Create(key, lazy));
        }
    }

    private sealed class RegionFile
    {
        public string Date { get; set; } = "";
        public double South { get; set; }
        public double West { get; set; }
        public List<OsmWay> Ways { get; set; } = new();
    }

    private async Task<List<OsmWay>> LoadRegionAsync((int Lat, int Lon) key, CancellationToken ct)
    {
        string path = RegionPath(key);
        if (File.Exists(path))
        {
            try
            {
                await using var f = File.OpenRead(path);
                await using var gz = new GZipStream(f, CompressionMode.Decompress);
                var file = await JsonSerializer.DeserializeAsync<RegionFile>(gz, cancellationToken: ct).ConfigureAwait(false);
                if (file != null && file.Date == DataDate) return file.Ways;
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
            {
                Log.Warning("World: the OpenStreetMap region {Path} could not be read ({Error}); asked for again.", path, ex.Message);
            }
        }
        double s = key.Lat * RegionDegrees, w = key.Lon * RegionDegrees;
        string F(double v) => v.ToString("0.0#####", CultureInfo.InvariantCulture);
        string query = $"[out:json][timeout:180][date:\"{DataDate}\"];way[\"highway\"]({F(s)},{F(w)},{F(s + RegionDegrees)},{F(w + RegionDegrees)});out body geom;";
        var clock = System.Diagnostics.Stopwatch.StartNew();
        string answer = await _ask(query, ct).ConfigureAwait(false);
        Interlocked.Increment(ref _fetches);
        var ways = Parse(answer);
        var region = new RegionFile { Date = DataDate, South = s, West = w, Ways = ways };
        Directory.CreateDirectory(RegionDir);
        string temp = path + "." + Environment.ProcessId + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await using (var f = File.Create(temp))
        await using (var gz = new GZipStream(f, CompressionLevel.SmallestSize))
            await JsonSerializer.SerializeAsync(gz, region, cancellationToken: ct).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
        Log.Information("World: OpenStreetMap region {Lat}_{Lon} fetched, {Ways} ways, {KB:F0} KB kept ({Ms} ms).",
                        key.Lat, key.Lon, ways.Count, new FileInfo(path).Length / 1024.0, clock.ElapsedMilliseconds);
        return ways;
    }

    /// <summary>Overpass's JSON answer to <c>out body geom</c>: its ways, in order of id. A way with a node
    /// the data no longer has is left out.</summary>
    public static List<OsmWay> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("elements", out var elements))
            throw new InvalidDataException("not an Overpass answer" + (doc.RootElement.TryGetProperty("remark", out var r) ? ": " + r.GetString() : ""));
        if (doc.RootElement.TryGetProperty("remark", out var remark) && remark.GetString() is { } said && said.Contains("error", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Overpass: " + said);
        var ways = new List<OsmWay>();
        foreach (var e in elements.EnumerateArray())
        {
            if (e.GetProperty("type").GetString() != "way" || !e.TryGetProperty("geometry", out var geom)) continue;
            var nodes = e.GetProperty("nodes").EnumerateArray().Select(n => n.GetInt64()).ToArray();
            var pts = geom.EnumerateArray().ToList();
            if (pts.Count != nodes.Length || pts.Any(p => p.ValueKind != JsonValueKind.Object)) continue;
            var w = new OsmWay
            {
                Id = e.GetProperty("id").GetInt64(),
                Nodes = nodes,
                Lat = pts.Select(p => p.GetProperty("lat").GetDouble()).ToArray(),
                Lon = pts.Select(p => p.GetProperty("lon").GetDouble()).ToArray(),
            };
            if (e.TryGetProperty("tags", out var tags))
                foreach (var t in tags.EnumerateObject()) w.Tags[t.Name] = t.Value.GetString() ?? "";
            ways.Add(w);
        }
        ways.Sort((a, b) => a.Id.CompareTo(b.Id));
        return ways;
    }

    // ═══ Asking Overpass ═══════════════════════════════════════════════════════════════════════════

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(200) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("openfps-world/0.1");
        return c;
    }

    /// <summary>One query at a time for the whole server, a second apart at least; a busy answer (429, 504,
    /// another 5xx, or the server's own "too busy") is tried again after 5, 15 and 45 s.</summary>
    private static async Task<string> AskOverpassAsync(string query, CancellationToken ct)
    {
        await OneAtATime.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                var since = DateTime.UtcNow - _lastAsked;
                if (since < TimeSpan.FromSeconds(1)) await Task.Delay(TimeSpan.FromSeconds(1) - since, ct).ConfigureAwait(false);
                _lastAsked = DateTime.UtcNow;
                HttpStatusCode code;
                string body;
                try
                {
                    using var content = new FormUrlEncodedContent(new[] { KeyValuePair.Create("data", query) });
                    using var resp = await Http.PostAsync(Endpoint, content, ct).ConfigureAwait(false);
                    code = resp.StatusCode;
                    body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                }
                catch (HttpRequestException) when (attempt < 3) { await Task.Delay(Backoff(attempt), ct).ConfigureAwait(false); continue; }
                bool busy = code == HttpStatusCode.TooManyRequests || (int)code >= 500 || !body.TrimStart().StartsWith('{');
                if (!busy) return body;
                if (attempt >= 3) throw new HttpRequestException($"Overpass is busy ({(int)code})");
                Log.Information("World: Overpass is busy ({Code}); asking again in {Seconds} s.", (int)code, Backoff(attempt).TotalSeconds);
                await Task.Delay(Backoff(attempt), ct).ConfigureAwait(false);
            }
        }
        finally { OneAtATime.Release(); }
    }

    private static TimeSpan Backoff(int attempt) => TimeSpan.FromSeconds(attempt switch { 0 => 5, 1 => 15, _ => 45 });
}

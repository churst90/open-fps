using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using OpenFPS.Common.Geometry;
using OpenFPS.Server.OneWorld;
using OpenFPS.Server.Repositories;
using Parquet;
using Parquet.Schema;
using Parquet.Serialization;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// Buildings on the world's tiles (docs/WORLD_STREAMING.md, "Buildings on the world's tiles"): Overture's footprints
/// read by byte range from a trimmed extract of its GeoParquet, built as tools/gen_osm.py builds a place's at medium
/// detail, each stored whole in the tile its middle is in, the ground graded under them the same whichever tile is
/// made first. Never the network, except the two tests that record the fixture and measure real tiles
/// (OPENFPS_WORLD_NET=1).
/// </summary>
public class WorldBuildingsTests : IDisposable
{
    private readonly ITestOutputHelper _o;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "openfps-world-buildings-" + Guid.NewGuid().ToString("N"));

    public WorldBuildingsTests(ITestOutputHelper o) => _o = o;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string FixtureDir(string name, [System.Runtime.CompilerServices.CallerFilePath] string here = "")
        => Path.Combine(Path.GetDirectoryName(here)!, "Fixtures", name);

    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, ".."));

    private static readonly PrefabRepository PrefabRepo = new(Path.Combine(AppContext.BaseDirectory, "prefabs"));
    private static readonly Dictionary<string, Vector3> Sizes =
        PrefabRepo.Prefabs.ToDictionary(kv => kv.Key, kv => kv.Value.ColliderSize ?? Vector3.One, StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, WorldBuildings.Prefab> Prefabs = WorldBuildings.PrefabsFrom(PrefabRepo.Prefabs);

    /// <summary>Gentle hills, the same height at a point whatever is asked: windows and tiles agree exactly.</summary>
    private sealed class Hills : IElevationSource
    {
        public string Name => "made-up hills";
        public readonly List<int> Windows = new();
        public static float At(double e, double n) => (float)(60 + 3 * Math.Sin(e / 80.0) + 2 * Math.Cos(n / 65.0));

        public Task<float[]?> HeightsAsync(WorldTileKey key, int posts, double spacing, CancellationToken ct)
            => WindowAsync(key.Zone, key.North, key.Easting, key.Northing, posts, spacing, ct);

        public Task<float[]?> WindowAsync(int zone, bool north, double west, double south, int posts, double spacing, CancellationToken ct)
        {
            lock (Windows) Windows.Add(posts);
            var h = new float[posts * posts];
            for (int j = 0; j < posts; j++)
                for (int i = 0; i < posts; i++) h[j * posts + i] = At(west + i * spacing, south + j * spacing);
            return Task.FromResult<float[]?>(h);
        }
    }

    /// <summary>Made-up OpenStreetMap: ways given in the zone's metres, answered whole to any box they reach.</summary>
    private sealed class MadeUpOsm : IOsmSource
    {
        public string Name => "made-up roads";
        public readonly List<OsmWay> Ways = new();

        public void Add(long id, Dictionary<string, string> tags, params (double E, double N)[] pts)
        {
            var w = new OsmWay { Id = id, Tags = tags, Nodes = new long[pts.Length], Lat = new double[pts.Length], Lon = new double[pts.Length] };
            for (int k = 0; k < pts.Length; k++)
            {
                (w.Lat[k], w.Lon[k]) = Utm.ToLatLon(15, true, pts[k].E, pts[k].N);
                w.Nodes[k] = (long)Math.Round(pts[k].E * 10) * 100_000_000L + (long)Math.Round(pts[k].N * 10);
            }
            Ways.Add(w);
        }

        public Task<IReadOnlyList<OsmWay>> HighwaysAsync(double south, double west, double north, double east, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<OsmWay>>(Ways.Where(w => w.Lat.Min() <= north && w.Lat.Max() >= south && w.Lon.Min() <= east && w.Lon.Max() >= west)
                                                          .OrderBy(w => w.Id).ToList());
    }

    /// <summary>Made-up footprints, given in the zone's metres.</summary>
    private sealed class MadeUpBuildings : IBuildingSource
    {
        public string Name => "made-up buildings";
        public readonly List<Footprint> All = new();
        public bool Fail;

        public void Add(string id, double? height, string? cls, string? name, params (double E, double N)[] corners)
        {
            var ring = corners.Append(corners[0]).Select(p => Utm.ToLatLon(15, true, p.E, p.N)).ToList();
            All.Add(new Footprint
            {
                Id = id, Lat = ring.Select(p => Math.Round(p.Lat, 7)).ToArray(), Lon = ring.Select(p => Math.Round(p.Lon, 7)).ToArray(),
                Height = height, Class = cls, Name = name, Sources = new[] { "Microsoft ML Buildings|ODbL-1.0" },
            });
        }

        /// <summary>A rectangle w by d metres centred on (e, n), turned by a degrees.</summary>
        public void Box(string id, double e, double n, double w, double d, double a = 0, double? height = null, string? cls = null, string? name = null)
        {
            double c = Math.Cos(a * Math.PI / 180), s = Math.Sin(a * Math.PI / 180);
            (double, double) P(double u, double v) => (e + u * c - v * s, n + u * s + v * c);
            Add(id, height, cls, name, P(-w / 2, -d / 2), P(w / 2, -d / 2), P(w / 2, d / 2), P(-w / 2, d / 2));
        }

        public Task<IReadOnlyList<Footprint>> BuildingsAsync(double south, double west, double north, double east, CancellationToken ct)
        {
            if (Fail) throw new HttpRequestException("no network");
            return Task.FromResult<IReadOnlyList<Footprint>>(All.Where(b => b.South <= north && b.North >= south && b.West <= east && b.East >= west)
                                                                .OrderBy(b => b.Id, StringComparer.Ordinal).ToList());
        }
    }

    private static readonly WorldTileKey A = new(15, true, 1000, 13300);
    private static readonly WorldTileKey B = A.Offset(1, 0);

    /// <summary>Main Street running east across the edge between A and B, Oak Lane off it, and buildings along them:
    /// a house straddling the edge (its middle in A), a house beside it in B with a mapped driveway, an L-shaped house, a mobile home, a
    /// shed across the edge, a church by name, a warehouse 150 m long whose pad is further from B than B's usual
    /// window, and a canopy over the road (not made).</summary>
    private static (MadeUpOsm Osm, MadeUpBuildings Buildings) Street()
    {
        var osm = new MadeUpOsm();
        double e = B.Easting, n = A.Northing;
        osm.Add(1, new() { ["highway"] = "residential", ["name"] = "Main Street" }, (e - 240, n + 100), (e - 60, n + 105), (e + 90, n + 112), (e + 240, n + 100));
        osm.Add(2, new() { ["highway"] = "residential", ["name"] = "Oak Lane" }, (e + 90, n + 112), (e + 95, n + 10));
        // house-b's own driveway, mapped: it gets no made-up one.
        osm.Add(3, new() { ["highway"] = "service", ["service"] = "driveway" }, (e + 34, n + 110), (e + 34, n + 124));
        var b = new MadeUpBuildings();
        b.Box("house-across", e - 4, n + 130, 18, 11, 3, height: 5.2);
        b.Box("house-b", e + 30, n + 131, 16, 12, -2, height: 4.8);
        b.Add("house-l", 5.0, null, null, (e - 70, n + 125), (e - 50, n + 125), (e - 50, n + 140), (e - 60, n + 140), (e - 60, n + 150), (e - 70, n + 150));
        b.Box("mobile", e - 120, n + 125, 20, 4.5, 0);
        b.Box("shed", e + 1, n + 150, 4, 3, 10);
        b.Box("church", e + 60, n + 80, 25, 14, 0, height: 9, cls: "church", name: "First Baptist Church");
        b.Box("warehouse", e - 80, n + 40, 150, 40, 0, height: 8, cls: "warehouse");
        b.Box("canopy", e - 150, n + 103, 12, 8, 0, cls: null);
        return (osm, b);
    }

    private WorldTileService Service(string name, IOsmSource osm, IBuildingSource buildings, IElevationSource? survey = null,
                                     Func<WorldTileKey, bool>? placed = null)
    {
        var store = new WorldStore(Path.Combine(_dir, name));
        return new WorldTileService(store, survey ?? new Hills())
        {
            Features = new WorldFeatures(osm, Sizes, buildings, Prefabs) { IsPlaced = placed },
        };
    }

    private static byte[] Bytes(WorldStore store, WorldTileKey k)
    {
        Assert.True(store.TryRead(k, out var t));
        t.MadeUtc = default;
        return t.ToBytes();
    }

    private static Heightfield Field(WorldTile.TerrainData t) => t.ToComponent().Field(0f);

    // ═══ The port against gen_osm.py ═════════════════════════════════════════════════════════════════

    /// <summary>Magnolia's inputs: its buildings by Overture id, as footprints, and its roads.</summary>
    private static (Dictionary<string, Footprint> Buildings, List<OsmWay> Ways) MagnoliaInputs()
    {
        var buildings = new Dictionary<string, Footprint>();
        using (var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "tools", "places", "magnolia_tx", "buildings.json"))))
            foreach (var b in doc.RootElement.EnumerateArray())
            {
                var ring = b.GetProperty("ring").EnumerateArray().Select(p => (Lat: p[0].GetDouble(), Lon: p[1].GetDouble())).ToList();
                string? S(string k) => b.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                buildings[b.GetProperty("id").GetString()!] = new Footprint
                {
                    Id = b.GetProperty("id").GetString()!,
                    Lat = ring.Select(p => p.Lat).ToArray(), Lon = ring.Select(p => p.Lon).ToArray(),
                    Height = b.GetProperty("h").ValueKind == JsonValueKind.Number ? b.GetProperty("h").GetDouble() : null,
                    Class = S("class"), Name = S("name"), Sources = new[] { (S("src") ?? "") + "|" },
                };
            }
        var ways = new List<OsmWay>();
        using (var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "tools", "places", "magnolia_tx", "osm.json"))))
        {
            var nodes = doc.RootElement.GetProperty("nodes");
            foreach (var w in doc.RootElement.GetProperty("ways").EnumerateArray())
            {
                var ids = w.GetProperty("nodes").EnumerateArray().Select(x => x.GetInt64()).ToArray();
                if (!ids.All(id => nodes.TryGetProperty(id.ToString(), out _))) continue;
                var way = new OsmWay { Id = w.GetProperty("id").GetInt64(), Nodes = ids };
                way.Lat = ids.Select(id => nodes.GetProperty(id.ToString())[0].GetDouble()).ToArray();
                way.Lon = ids.Select(id => nodes.GetProperty(id.ToString())[1].GetDouble()).ToArray();
                foreach (var t in w.GetProperty("tags").EnumerateObject()) way.Tags[t.Name] = t.Value.GetString() ?? "";
                ways.Add(way);
            }
        }
        ways.Sort((a, b) => a.Id.CompareTo(b.Id));
        return (buildings, ways);
    }

    /// <summary>gen_osm.py run on Magnolia with --decisions: what it decided for each building (its kind, name, label,
    /// street, pad, and which of the map's entities it made).</summary>
    private List<JsonElement> MagnoliaDecisions()
    {
        Directory.CreateDirectory(_dir);
        string outFile = Path.Combine(_dir, "magnolia_tx.json"), decisions = Path.Combine(_dir, "decisions.json");
        var psi = new ProcessStartInfo("python3", $"tools/gen_osm.py tools/places/magnolia_tx --out={outFile} --decisions={decisions}")
        {
            WorkingDirectory = RepoRoot(), RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var proc = Process.Start(psi)!;
        string err = proc.StandardError.ReadToEnd();
        proc.StandardOutput.ReadToEnd();
        proc.WaitForExit();
        Assert.True(proc.ExitCode == 0, err);
        using var doc = JsonDocument.Parse(File.ReadAllText(decisions));
        return doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
    }

    private static Vector3 Extent(EntityData e) => (Sizes.TryGetValue(e.PrefabId, out var s) ? s : Vector3.One) * e.Scale;

    /// <summary>
    /// The port's shell against gen_osm.py's, for every building of Magnolia built at medium detail: given what gen_osm
    /// decided for it (kind, name, the street it faces, its pad), the port makes the same entities in the same order,
    /// with the same ids, prefabs, names and rooms, each within a centimetre of where gen_osm put it, the same size and
    /// the same turn.
    /// </summary>
    [Fact]
    public void Magnolia_s_buildings_are_built_as_gen_osm_built_them()
    {
        var map = MapRepository.LoadFromFile(Path.Combine(AppContext.BaseDirectory, "places", "magnolia_tx.json"))!;
        var u = map.Utm!;
        var e = map.Elevation!;
        var (buildings, _) = MagnoliaInputs();
        int compared = 0, entities = 0, matched = 0, same = 0;
        var ties = new List<string>();
        double worstXz = 0, worstY = 0, worstSize = 0, worstTurn = 0;
        var kinds = new Dictionary<string, int>();
        foreach (var d in MagnoliaDecisions())
        {
            if (d.GetProperty("detail").GetInt32() != 1) continue;
            var fp = buildings[d.GetProperty("id").GetString()!];
            var ring = fp.Lat.Select((lat, k) => Utm.FromLatLon(lat, fp.Lon[k], u.Zone, u.North)).Select(p => (p.Easting - u.Easting, p.Northing - u.Northing)).ToList();
            string kind = d.GetProperty("kind").GetString()!;
            var plan = new WorldBuildings.Plan
            {
                Shape = new WorldBuildings.Shape(ring), Kind = kind, Name = d.GetProperty("name").GetString()!, Seed = d.GetProperty("label").GetString()!,
                PlaceKind = d.GetProperty("place").ValueKind == JsonValueKind.String ? d.GetProperty("place").GetString() : null,
                Height = fp.Height,
                Street = d.TryGetProperty("street", out var st) ? (st[0].GetDouble(), st[1].GetDouble()) : default,
                Pad = d.GetProperty("pad").ValueKind == JsonValueKind.Number ? d.GetProperty("pad").GetDouble() : null,
                RoomSuffix = kind is "house" or "mobile_home" ? ", house" : "",
            };
            int first = d.GetProperty("entities")[0].GetInt32(), last = d.GetProperty("entities")[1].GetInt32();
            int next = map.Entities[first].EntityId - 1;
            var made = WorldBuildings.Build(plan, Prefabs, (x, z) => e.HeightAt(x, z), (0, 0), ref next);
            compared++;
            kinds[kind] = kinds.GetValueOrDefault(kind) + 1;
            Assert.Equal(last - first, made.Count);
            bool whole = true, shellSame = true;
            for (int k = 0; k < made.Count; k++)
            {
                var want = map.Entities[first + k];
                var got = made[k];
                entities++;
                Assert.Equal(want.EntityId, got.EntityId);
                Assert.Equal(want.PrefabId, got.PrefabId);
                Assert.Equal(want.Name, got.Name);
                Assert.Equal(want.Layer, got.Layer);
                bool regionSame = want.RegionAId == got.RegionAId;
                Assert.Equal(want.RegionBId, got.RegionBId);
                double dxz = Vector2.Distance(new Vector2(want.Position.X, want.Position.Z), new Vector2(got.Position.X, got.Position.Z));
                double dy = Math.Abs(want.Position.Y - got.Position.Y);
                double ds = (Extent(want) - Extent(got)).Length();
                double dturn = 1 - Math.Abs(Quaternion.Dot(Quaternion.Normalize(want.Rotation), Quaternion.Normalize(got.Rotation)));
                bool ok = regionSame && dxz <= 0.01 && dy <= 0.01 && ds <= 0.01 && dturn < 1e-5;
                if (ok)
                {
                    matched++;
                    worstXz = Math.Max(worstXz, dxz); worstY = Math.Max(worstY, dy); worstSize = Math.Max(worstSize, ds); worstTurn = Math.Max(worstTurn, dturn);
                }
                else
                {
                    whole = false;
                    // Walls and the door move together when the door goes to another wall; nothing else may.
                    if (!(got.Name!.EndsWith(" wall") || got.RegionAId != null)) shellSame = false;
                }
            }
            if (whole) same++;
            else
            {
                Assert.True(shellSame, $"{kind} {fp.Id}: more than its walls and door differ");
                ties.Add($"{d.GetProperty("name").GetString()} ({fp.Id})");
            }
        }
        _o.WriteLine($"{compared} buildings at medium detail ({string.Join(", ", kinds.OrderByDescending(k => k.Value).Select(k => $"{k.Key} {k.Value}"))}), "
                     + $"{entities} entities; {same} buildings the same in every part, {matched} entities within 1 cm and the same turn (worst "
                     + $"{worstXz * 1000:F2} mm across, {worstY * 1000:F2} mm in height, {worstSize * 1000:F2} mm in size, turn 1 - |dot| {worstTurn:E1}); "
                     + $"the door on another wall of the same length facing the same way (a tie gen_osm breaks by rounding): {string.Join("; ", ties)}");
        Assert.True(compared > 1000);
        Assert.True(ties.Count <= compared / 200, $"{ties.Count} buildings differ");
    }

    /// <summary>
    /// The whole world pipeline on Magnolia's own footprints and roads, over the nine tiles round its spawn, against the
    /// map: what the world decides without addresses or lots (the kind, the street faced, the pad) beside what gen_osm
    /// decided with them, and where both build the same kind of shell, how many come out the same within a centimetre.
    /// Measured, not held to equality: an address can face a house to a road that is not its nearest.
    /// </summary>
    [Fact]
    public void Magnolia_s_footprints_through_the_world_s_generator()
    {
        var map = MapRepository.LoadFromFile(Path.Combine(AppContext.BaseDirectory, "places", "magnolia_tx.json"))!;
        var u = map.Utm!;
        var e = map.Elevation!;
        var (buildings, ways) = MagnoliaInputs();
        var decided = MagnoliaDecisions().ToDictionary(d => d.GetProperty("id").GetString()!);
        var features = new WorldFeatures(new MadeUpOsm(), Sizes, new MadeUpBuildings(), Prefabs);
        var spawn = WorldTileKey.Of(u.Zone, u.North, u.Easting, u.Northing);
        int seen = 0, sameKind = 0, shells = 0, padSame = 0, shellSame = 0, solidSame = 0, solids = 0, otherRoad = 0;
        var confusion = new Dictionary<string, int>();
        var clock = Stopwatch.StartNew();
        int tiles = 0;
        for (int dx = -1; dx <= 1; dx++)
            for (int dz = -1; dz <= 1; dz++)
            {
                var key = spawn.Offset(dx, dz);
                var (s, w, n, ea) = (90.0, 180.0, -90.0, -180.0);
                foreach (var (pe, pn) in new[] { (key.Easting - 100, key.Northing - 100), (key.Easting + 350, key.Northing + 350) })
                {
                    var (lat, lon) = Utm.ToLatLon(key.Zone, key.North, pe, pn);
                    s = Math.Min(s, lat); n = Math.Max(n, lat); w = Math.Min(w, lon); ea = Math.Max(ea, lon);
                }
                var fps = buildings.Values.Where(b => b.South <= n && b.North >= s && b.West <= ea && b.East >= w).OrderBy(b => b.Id, StringComparer.Ordinal).ToList();
                double margin = features.MarginFor(key, fps, ways);
                int posts = WorldFeatures.PostsFor(margin);
                var window = new float[posts * posts];
                double west = key.Easting - margin, south = key.Northing - margin;
                for (int j = 0; j < posts; j++)
                    for (int i = 0; i < posts; i++)
                        window[j * posts + i] = (float)(e.HeightAt(west + i * 2 - u.Easting, south + j * 2 - u.Northing) - e.SeaLevelY);
                tiles++;
                foreach (var b in features.BuildingsOf(key, fps, ways, window, margin))
                {
                    if (b.Owner != key || !decided.TryGetValue(b.Fp.Id, out var d) || d.GetProperty("detail").GetInt32() != 1) continue;
                    seen++;
                    string theirs = d.GetProperty("kind").GetString()!;
                    string pair = theirs == b.Kind ? b.Kind : $"{theirs} made {b.Kind}";
                    confusion[pair] = confusion.GetValueOrDefault(pair) + 1;
                    bool solid = WorldBuildings.IsSolidBox(theirs);
                    if (solid != WorldBuildings.IsSolidBox(b.Kind)) continue;
                    if (theirs == b.Kind) sameKind++;
                    int first = d.GetProperty("entities")[0].GetInt32(), last = d.GetProperty("entities")[1].GetInt32();
                    var mapParts = map.Entities.GetRange(first, last - first);
                    bool Same(EntityData x, EntityData y)
                    {
                        var at = new Vector3((float)(x.Position.X + key.Easting - u.Easting), (float)(x.Position.Y + e.SeaLevelY), (float)(x.Position.Z + key.Northing - u.Northing));
                        return Vector2.Distance(new Vector2(at.X, at.Z), new Vector2(y.Position.X, y.Position.Z)) <= 0.01f && MathF.Abs(at.Y - y.Position.Y) <= 0.01f
                               && (Extent(x) - Extent(y)).Length() <= 0.01f;
                    }
                    bool all = b.Entities.Count == mapParts.Count && b.Entities.Zip(mapParts).All(p => Same(p.First, p.Second));
                    if (solid) { solids++; if (all) solidSame++; continue; }
                    shells++;
                    double theirPad = d.GetProperty("pad").GetDouble();
                    if (b.Pad is double pad && Math.Abs(pad + e.SeaLevelY - theirPad) <= 0.01) padSame++;
                    if (all) shellSame++;
                    else
                    {
                        // Facing another road than the nearest: gen_osm faces a house to its address's road.
                        var st = d.GetProperty("street");
                        double sx = st[0].GetDouble() + u.Easting, sz = st[1].GetDouble() + u.Northing;
                        var c = b.Fp;
                        if (Math.Sqrt((sx - b.Street.X) * (sx - b.Street.X) + (sz - b.Street.Z) * (sz - b.Street.Z)) > 1.0) otherRoad++;
                    }
                }
            }
        long ms = clock.ElapsedMilliseconds;
        _o.WriteLine($"{tiles} tiles in {ms} ms; {seen} of Magnolia's medium-detail buildings stored: kinds {string.Join(", ", confusion.OrderByDescending(k => k.Value).Select(k => $"{k.Key} {k.Value}"))}");
        _o.WriteLine($"shells on both sides {shells}: pad within 1 cm {padSame}, every part within 1 cm {shellSame}, of the rest {otherRoad} face another road "
                     + $"(gen_osm faces a house to its address's); solid boxes on both sides {solids}: the same {solidSame}");
        Assert.True(seen > 50);
        Assert.True(padSame >= shells * 0.9, $"{padSame} of {shells} pads");
        Assert.True(solidSame >= solids * 0.95, $"{solidSame} of {solids} solid boxes");
    }

    // ═══ Tiles that agree ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Buildings along a street across the edge between A and B: A then B is byte for byte B then A; each building is
    /// stored once, whole, in the tile its middle is in; the shared edge is one line of posts; no ground stands above
    /// any floor's underside on either side; the warehouse's pad, further out than B's usual window, widened it; the
    /// canopy over the road is not made.
    /// </summary>
    [Fact]
    public async Task A_house_across_an_edge_is_the_same_house_from_either_side()
    {
        var (osm, buildings) = Street();
        var hillsOne = new Hills();
        var one = Service("one", osm, buildings, hillsOne);
        var two = Service("two", osm, buildings);
        foreach (var k in new[] { A, B }) Assert.NotNull(await one.GetAsync(k));
        foreach (var k in new[] { B, A }) Assert.NotNull(await two.GetAsync(k));
        Assert.Equal(Bytes(one.Store, A), Bytes(two.Store, A));
        Assert.Equal(Bytes(one.Store, B), Bytes(two.Store, B));
        _o.WriteLine("windows asked (posts a side): " + string.Join(", ", hillsOne.Windows));
        Assert.Contains(hillsOne.Windows, p => p > WorldFeatures.WindowPosts);

        Assert.True(one.Store.TryRead(A, out var a));
        Assert.True(one.Store.TryRead(B, out var b));
        var all = a.Entities.Select(x => (Tile: A, E: x)).Concat(b.Entities.Select(x => (Tile: B, E: x))).ToList();
        var names = all.Where(x => x.E.PrefabId is "door" or "glass_pull_door").Select(x => x.E.Name).ToList();
        _o.WriteLine("doors: " + string.Join("; ", names));
        Assert.Equal(3, all.Count(x => x.E.Name == "House off Main Street front door"));
        Assert.Single(all, x => x.E.Name == "Mobile home off Main Street front door");
        Assert.Single(all, x => x.E.Name == "First Baptist Church front door");
        Assert.Single(all, x => x.E.Name == "Building off Main Street door");
        Assert.Single(all, x => x.E.Name == "Shed off Main Street");
        Assert.Equal(6, all.Count(x => x.E.PrefabId is "door" or "glass_pull_door"));
        // Driveways: the mapped one, and one made up for every other house and the mobile home, none for house-b.
        var drives = all.Where(x => x.E.Layer == "drives").ToList();
        _o.WriteLine("drives: " + string.Join("; ", drives.Select(x => $"{x.E.Name} at {x.E.Position + new Vector3((float)(x.Tile.Easting - A.Easting), 0, 0)}")));
        Assert.Contains(drives, x => x.E.Name == "Driveway" && x.E.PrefabId == "concrete_floor");
        Assert.Equal(2, drives.Where(x => x.E.Name == "House off Main Street driveway").Select(x => Math.Round(x.E.Position.X + x.Tile.Easting)).Distinct().Count());
        Assert.Contains(drives, x => x.E.Name == "Mobile home off Main Street driveway");
        Assert.DoesNotContain(drives, x => x.E.Name == "House off Main Street driveway" && Math.Abs(x.E.Position.X + x.Tile.Easting - (B.Easting + 30)) < 9);
        // The canopy over Main Street (in A, 100 m east of its corner and 103 m north) is not made.
        Assert.DoesNotContain(a.Entities, x => x.Layer == "structure" && Math.Abs(x.Position.X - 100) < 7 && Math.Abs(x.Position.Z - 103) < 5);
        // The house across the edge: everything of it in A, some of it standing over B.
        var across = a.Entities.Where(x => x.Name != null && x.Name.StartsWith("House off Main Street") && x.Position.X > 240).ToList();
        Assert.NotEmpty(across);
        Assert.Contains(a.Entities, x => x.Layer == "structure" && x.Name!.EndsWith(" floor") && x.Position.X + Extent(x).X / 2 > 250);
        // Rooms: indoors, made of what is round them; the door opens from one in its own tile.
        foreach (var (tile, door) in all.Where(x => x.E.RegionAId != null))
        {
            var room = (tile == A ? a : b).Entities.Single(x => x.EntityId == door.RegionAId);
            Assert.Equal("acoustic_region", room.PrefabId);
            Assert.Equal(-1, door.RegionBId);
            Assert.True(room.IsIndoor);
            Assert.Equal(6, room.RoomMaterials!.Length);
        }
        Assert.Contains("made-up buildings", a.Buildings);
        Assert.Contains("Microsoft ML Buildings, ODbL-1.0", b.Buildings);

        // The shared edge: A's east posts are B's west posts.
        int p = WorldTileService.Posts;
        var ha = a.Terrain!; var hb = b.Terrain!;
        for (int j = 0; j < p; j++)
            Assert.Equal(ha.BaseY + ha.HeightsCm[j * p + p - 1] * 0.01f, hb.BaseY + hb.HeightsCm[j * p] * 0.01f, 2);

        // No ground above any floor's underside, on either side of the edge.
        var fa = Field(ha); var fb = Field(hb);
        int checkedPoints = 0, graded = 0;
        foreach (var (t, x) in all.Where(y => y.E.Name != null && (y.E.Name.EndsWith(" floor") || y.E.Layer == "drives")))
        {
            var size = Extent(x);
            var centre = new Vector3(x.Position.X + (float)(t.Easting - A.Easting), x.Position.Y, x.Position.Z);
            for (float su = -0.45f; su <= 0.451f; su += 0.15f)
                for (float sv = -0.45f; sv <= 0.451f; sv += 0.15f)
                {
                    var under = centre + Vector3.Transform(new Vector3(su * size.X, -size.Y / 2, sv * size.Z), x.Rotation);
                    if (under.X < 0 || under.X > 500 || under.Z < 0 || under.Z > 250) continue;
                    var (field, baseY, ox) = under.X < 250f ? (fa, ha.BaseY, 0f) : (fb, hb.BaseY, 250f);
                    float ground = baseY + field.HeightAt(under.X - ox, under.Z);
                    Assert.True(ground <= under.Y + 0.011f, $"ground {ground:F3} over the underside {under.Y:F3} of {x.Name} at {under}");
                    if (Math.Abs(ground - Hills.At(A.Easting + under.X, A.Northing + under.Z)) > 0.02f) graded++;
                    checkedPoints++;
                }
        }
        _o.WriteLine($"{a.Entities.Count} things in A, {b.Entities.Count} in B; {checkedPoints} points under floors, {graded} on graded ground");
        Assert.True(checkedPoints > 100);
        Assert.True(graded > 0);
    }

    /// <summary>Nothing generated reaches into a real place: no building whose footprint touches one of its tiles.</summary>
    [Fact]
    public async Task No_building_reaches_into_a_real_place()
    {
        var (osm, buildings) = Street();
        var a = await Service("placed", osm, buildings, placed: k => k == B).GetAsync(A);
        Assert.NotNull(a);
        Assert.DoesNotContain(a!.Entities, x => x.Name != null && x.Name.StartsWith("House off Main Street") && x.Position.X > 240);
        Assert.Contains(a.Entities, x => x.Name == "Mobile home off Main Street front door");
    }

    /// <summary>Without its buildings a tile is not made (it would keep that hole); it is tried again later.</summary>
    [Fact]
    public async Task No_buildings_to_be_had_is_tried_again_not_stored_bare()
    {
        var (osm, buildings) = Street();
        buildings.Fail = true;
        var service = Service("fail", osm, buildings);
        service.RetryAfter = TimeSpan.FromMilliseconds(200);
        Assert.Null(await service.GetAsync(A));
        Assert.False(service.Store.Has(A));
        buildings.Fail = false;
        await Task.Delay(300);
        var tile = await service.GetAsync(A);
        Assert.NotNull(tile);
        Assert.NotNull(tile!.Buildings);
    }

    /// <summary>What a building is, from Overture's class, its size and its shape.</summary>
    [Theory]
    [InlineData(null, null, 14, 10, "house")]
    [InlineData(null, null, 22, 4.5, "mobile_home")]
    [InlineData("static_caravan", null, 14, 10, "mobile_home")]
    [InlineData("house", null, 22, 4.5, "house")]
    [InlineData(null, null, 5, 4, "shed")]
    [InlineData(null, null, 7, 6, "outbuilding")]
    [InlineData("garage", null, 7, 6, "garage")]
    [InlineData(null, null, 40, 20, "building")]
    [InlineData("commercial", null, 12, 10, "building")]
    [InlineData("church", "St Mary's", 25, 14, "church")]
    [InlineData(null, "Dollar General", 30, 20, "premises")]
    [InlineData("roof", null, 12, 8, null)]
    public void What_a_building_is(string? cls, string? name, double w, double d, string? kind)
    {
        var ring = new List<(double X, double Z)> { (0, 0), (w, 0), (w, d), (0, d), (0, 0) };
        var got = WorldFeatures.Classify(new Footprint { Id = "x", Class = cls, Name = name }, new WorldBuildings.Shape(ring));
        Assert.Equal(kind, got?.Kind);
    }

    /// <summary>gen_osm.py h01 is zlib's crc32 of the keys joined by "|".</summary>
    [Fact]
    public void H01_is_gen_osm_s()
    {
        Assert.Equal(0x352441C2u, WorldBuildings.Crc32("abc"u8.ToArray()));
        Assert.Equal(WorldBuildings.Crc32("31907 Bobcat Lane|walls"u8.ToArray()) / 4294967296.0, WorldBuildings.H01("31907 Bobcat Lane", "walls"));
    }

    /// <summary>Well-known binary: a polygon's outer ring, and a multipolygon's largest part.</summary>
    [Fact]
    public void A_footprint_is_read_from_well_known_binary()
    {
        static byte[] Polygon(params (double Lon, double Lat)[] ring)
        {
            var b = new List<byte> { 1 };
            b.AddRange(BitConverter.GetBytes(3u));
            b.AddRange(BitConverter.GetBytes(1u));
            b.AddRange(BitConverter.GetBytes((uint)ring.Length));
            foreach (var (x, y) in ring) { b.AddRange(BitConverter.GetBytes(x)); b.AddRange(BitConverter.GetBytes(y)); }
            return b.ToArray();
        }
        var small = Polygon((0, 0), (1, 0), (1, 1), (0, 0));
        var big = Polygon((10, 10), (14, 10), (14, 14), (10, 14), (10, 10));
        var multi = new List<byte> { 1 };
        multi.AddRange(BitConverter.GetBytes(6u));
        multi.AddRange(BitConverter.GetBytes(2u));
        multi.AddRange(small);
        multi.AddRange(big);
        var ring = Wkb.LargestOuterRing(multi.ToArray())!;
        Assert.Equal(5, ring.Count);
        Assert.Equal((10.0, 10.0), ring[0]);
        Assert.Equal((10.0, 14.0), ring[1]);
        var fp = OvertureBuildings.ToFootprint(new OvertureRow { Id = "a", Geometry = big, Height = 4.567, Sources = new() { new() { Dataset = "OpenStreetMap", License = "ODbL-1.0" } } })!;
        Assert.Equal(4.57, fp.Height);
        Assert.Equal(new[] { "OpenStreetMap|ODbL-1.0" }, fp.Sources);
    }

    // ═══ Overture by byte range, from the trimmed extract ═══════════════════════════════════════════════

    /// <summary>Downtown Tomball, Texas, by Main Street, 13 km south-east of Magnolia (the roads' fixture too).</summary>
    private static readonly (double Lat, double Lon) Tomball = (30.09715, -95.61606);

    private const string FixtureItem = "https://fixture.invalid/overture/item.json";
    private const string FixtureFile = "https://fixture.invalid/overture/buildings.parquet";

    /// <summary>The trimmed extract as Overture's servers: the release's catalogue, the file's item, and the file's
    /// bytes by range. Counts what it is asked.</summary>
    private sealed class FixtureNet : IByteRanges
    {
        public readonly Dictionary<string, byte[]> Files = new();
        public readonly List<(long Offset, int Length)> Ranges = new();
        public int Gets;
        public bool Fail;

        public FixtureNet()
        {
            string dir = FixtureDir("overture");
            Files[OvertureBuildings.CollectionUrl] = File.ReadAllBytes(Path.Combine(dir, "collection.json"));
            Files[FixtureItem] = File.ReadAllBytes(Path.Combine(dir, "item.json"));
            Files[FixtureFile] = File.ReadAllBytes(Path.Combine(dir, "buildings.parquet"));
        }

        public Task<byte[]?> ReadAsync(string url, long offset, int length, CancellationToken ct)
        {
            if (Fail) throw new HttpRequestException("no network");
            lock (Ranges) Ranges.Add((offset, length));
            if (!Files.TryGetValue(url, out var f)) return Task.FromResult<byte[]?>(null);
            return Task.FromResult<byte[]?>(f.AsSpan((int)offset, (int)Math.Min(length, f.Length - offset)).ToArray());
        }

        public Task<byte[]?> GetAsync(string url, CancellationToken ct)
        {
            if (Fail) throw new HttpRequestException("no network");
            Interlocked.Increment(ref Gets);
            return Task.FromResult(Files.TryGetValue(url, out var f) ? f : null);
        }
    }

    /// <summary>
    /// The trimmed extract read by byte range as a tile reads Overture: the catalogue and the file's item once, the
    /// footer once, then only the columns read of only the row groups whose box reaches the tile; the footprints are
    /// those of the whole extract that reach the box; a second reader over the same cache asks nothing.
    /// </summary>
    [Fact]
    public async Task Only_the_row_groups_a_tile_needs_are_read()
    {
        var net = new FixtureNet();
        var cache = Path.Combine(_dir, "overture");
        var src = new OvertureBuildings(cache, net, net.GetAsync);
        var centre = WorldTileKey.OfLatLon(Tomball.Lat, Tomball.Lon);
        var (lat0, lon0) = Utm.ToLatLon(centre.Zone, centre.North, centre.Easting, centre.Northing);
        var (lat1, lon1) = Utm.ToLatLon(centre.Zone, centre.North, centre.Easting + 250, centre.Northing + 250);
        var got = await src.BuildingsAsync(lat0, lon0, lat1, lon1, CancellationToken.None);

        // The whole extract, read without the network's help, filtered by the box.
        var bytes = net.Files[FixtureFile];
        await using var reader = await ParquetReader.CreateAsync(new MemoryStream(bytes));
        var everything = new List<Footprint>();
        for (int g = 0; g < reader.RowGroupCount; g++)
            foreach (var r in (await ParquetSerializer.DeserializeAsync<OvertureRow>(new MemoryStream(bytes), rowGroupIndex: g)).Data)
                if (OvertureBuildings.ToFootprint(r) is { } fp) everything.Add(fp);
        var want = everything.Where(b => b.South <= lat1 && b.North >= lat0 && b.West <= lon1 && b.East >= lon0).Select(b => b.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.Equal(want, got.Select(b => b.Id).ToList());
        Assert.NotEmpty(got);

        long fetched = net.Ranges.Sum(r => (long)r.Length);
        _o.WriteLine($"{got.Count} of {everything.Count} buildings in one tile's box; {reader.RowGroupCount} row groups in the extract; "
                     + $"{net.Gets} catalogue requests, {net.Ranges.Count} ranges, {fetched / 1024.0:F0} KB of the file's {bytes.Length / 1024.0:F0} KB");
        Assert.Equal(2, net.Gets);
        Assert.True(fetched < bytes.Length * 0.8, "only some of the row groups were read");
        Assert.True(reader.RowGroupCount >= 4);

        // Again from the cache alone.
        var offline = new FixtureNet { Fail = true };
        var again = await new OvertureBuildings(cache, offline, offline.GetAsync).BuildingsAsync(lat0, lon0, lat1, lon1, CancellationToken.None);
        Assert.Equal(want, again.Select(b => b.Id).ToList());
        Assert.True(File.Exists(Path.Combine(cache, "SOURCE.txt")));
    }

    private string CopyOsmFixture()
    {
        string from = FixtureDir("osm"), to = Path.Combine(_dir, "sources", "osm");
        foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            string dest = Path.Combine(to, Path.GetRelativePath(from, f));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(f, dest);
        }
        return to;
    }

    /// <summary>
    /// Downtown Tomball's nine tiles from the recorded roads and the trimmed Overture extract, offline: what they hold,
    /// how long a tile takes (the survey answering at once), what the store and the cache hold and what was fetched;
    /// then the same tiles made again in the other order are the same bytes, and the cache answers without the network.
    /// </summary>
    [Fact]
    public async Task Tomball_from_the_trimmed_extract()
    {
        string osmCache = CopyOsmFixture();
        var net = new FixtureNet();
        string overtureCache = Path.Combine(_dir, "sources", "overture");
        WorldTileService Make(string name, FixtureNet n) => new(new WorldStore(Path.Combine(_dir, name)), new Hills())
        {
            Features = new WorldFeatures(new OverpassRegions(osmCache, (_, _) => throw new HttpRequestException("no network")), Sizes,
                                         new OvertureBuildings(overtureCache, n, n.GetAsync), Prefabs),
        };
        var service = Make("world", net);
        var centre = WorldTileKey.OfLatLon(Tomball.Lat, Tomball.Lon);
        var keys = new List<WorldTileKey>();
        for (int dz = -1; dz <= 1; dz++) for (int dx = -1; dx <= 1; dx++) keys.Add(centre.Offset(dx, dz));
        var times = new List<long>();
        var fetchedPerTile = new List<long>();
        long before = 0;
        foreach (var k in keys)
        {
            var one = Stopwatch.StartNew();
            Assert.NotNull(await service.GetAsync(k));
            times.Add(one.ElapsedMilliseconds);
            long now = net.Ranges.Sum(r => (long)r.Length) + net.Gets * 0L;
            fetchedPerTile.Add(now - before);
            before = now;
        }
        var tiles = keys.Select(k => { Assert.True(service.Store.TryRead(k, out var t)); return t; }).ToList();
        int houses = tiles.Sum(t => t.Entities.Count(x => x.Layer == "rooms"));
        int doors = tiles.Sum(t => t.Entities.Count(x => x.PrefabId is "door" or "glass_pull_door"));
        int solids = tiles.Sum(t => t.Entities.Count(x => x.Layer == "structure" && x.Name != null && !x.Name.Contains(" wall") && !x.Name.EndsWith(" floor")
                                                        && !x.Name.EndsWith(" roof") && !x.Name.EndsWith(" ceiling") && x.PrefabId is "metal_wall" or "siding_wall"));
        var sizes = keys.Select(k => new FileInfo(Path.Combine(service.Store.Root, "tiles", "v" + WorldStore.GeneratorVersion, k.RelativePath, "full.json.gz")).Length).OrderBy(s => s).ToList();
        long cache = Directory.EnumerateFiles(overtureCache, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        var names = tiles.SelectMany(t => t.Entities).Where(x => x.Layer == "rooms").Select(x => x.Name).Distinct().Take(12).ToList();
        times.Sort();
        _o.WriteLine($"Tomball, 9 tiles: a tile median {times[4]} ms, most {times[^1]} ms; {houses} rooms, {doors} front doors, {solids} solid boxes; "
                     + $"a tile median {sizes[4] / 1024.0:F1} KB, most {sizes[^1] / 1024.0:F1} KB; Overture fetched per tile (KB): "
                     + string.Join(", ", fetchedPerTile.Select(b => (b / 1024.0).ToString("F0"))) + $"; the Overture cache {cache / 1024.0:F0} KB");
        _o.WriteLine("rooms: " + string.Join("; ", names));
        _o.WriteLine("attribution: " + tiles.First(t => t.Buildings != null).Buildings);
        Assert.True(doors > 50);
        Assert.Contains(names, n => n!.Contains(" off "));

        // The other order, a fresh store, the same caches and no network: the same tiles.
        var offline = new FixtureNet { Fail = true };
        var again = Make("again", offline);
        foreach (var k in Enumerable.Reverse(keys)) Assert.NotNull(await again.GetAsync(k));
        foreach (var k in keys) Assert.Equal(Bytes(service.Store, k), Bytes(again.Store, k));
    }

    // ═══ The network (OPENFPS_WORLD_NET=1 only) ════════════════════════════════════════════════════════

    /// <summary>
    /// Records the trimmed extract: the buildings round downtown Tomball's nine tiles, read from Overture's release by
    /// byte range, written back as a small GeoParquet file with Overture's columns that a footprint is made of (the
    /// geometry to a ten millionth of a degree), sorted west to east into six row groups so a tile's box reaches only
    /// some of them; and a catalogue and an item for it as the release's STAC has them.
    /// </summary>
    [Fact]
    public async Task Record_the_overture_fixture_from_the_network()
    {
        if (Environment.GetEnvironmentVariable("OPENFPS_WORLD_NET") != "1") return;
        var src = new OvertureBuildings(Path.Combine(_dir, "overture"));
        var features = new WorldFeatures(new MadeUpOsm(), Sizes, src, Prefabs);
        var centre = WorldTileKey.OfLatLon(Tomball.Lat, Tomball.Lon);
        var byId = new SortedDictionary<string, Footprint>(StringComparer.Ordinal);
        for (int dx = -1; dx <= 1; dx++)
            for (int dz = -1; dz <= 1; dz++)
                foreach (var b in await features.FootprintsAsync(centre.Offset(dx, dz), CancellationToken.None)) byId.TryAdd(b.Id, b);
        _o.WriteLine($"{byId.Count} buildings; {src.Requests} requests, {src.BytesFetched / 1024.0:F0} KB");

        var rows = byId.Values.Select(b =>
        {
            var wkb = new List<byte> { 1 };
            wkb.AddRange(BitConverter.GetBytes(3u));
            wkb.AddRange(BitConverter.GetBytes(1u));
            wkb.AddRange(BitConverter.GetBytes((uint)b.Lat.Length));
            for (int k = 0; k < b.Lat.Length; k++) { wkb.AddRange(BitConverter.GetBytes(b.Lon[k])); wkb.AddRange(BitConverter.GetBytes(b.Lat[k])); }
            return new OvertureRow
            {
                Id = b.Id, Geometry = wkb.ToArray(), Height = b.Height, NumFloors = b.Floors, Class = b.Class, Subtype = b.Subtype,
                Names = b.Name == null ? null : new OvertureNames { Primary = b.Name },
                Sources = b.Sources.Select(s => new OvertureSource { Dataset = s.Split('|')[0], License = s.Split('|')[1] }).ToList(),
                Bbox = new OvertureBbox { Xmin = b.West, Xmax = b.East, Ymin = b.South, Ymax = b.North },
            };
        }).OrderBy(r => r.Bbox!.Xmin).ThenBy(r => r.Id, StringComparer.Ordinal).ToList();
        string dir = FixtureDir("overture");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "buildings.parquet");
        await using (var f = File.Create(file))
            await ParquetSerializer.SerializeAsync(rows, f, new ParquetOptions { CompressionMethod = CompressionMethod.Zstd, RowGroupSize = (rows.Count + 5) / 6 });
        double w = rows.Min(r => r.Bbox!.Xmin!.Value), e = rows.Max(r => r.Bbox!.Xmax!.Value), s = rows.Min(r => r.Bbox!.Ymin!.Value), n = rows.Max(r => r.Bbox!.Ymax!.Value);
        var box = new[] { w, s, e, n };
        File.WriteAllText(Path.Combine(dir, "collection.json"), JsonSerializer.Serialize(new
        {
            type = "Collection", id = "building",
            description = $"A trimmed extract of Overture's buildings release {OvertureBuildings.Release} round downtown Tomball, Texas, for WorldBuildingsTests",
            links = new[] { new { rel = "item", href = FixtureItem } },
            extent = new { spatial = new { bbox = new[] { box, box } } },
        }));
        File.WriteAllText(Path.Combine(dir, "item.json"), JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["type"] = "Feature", ["id"] = "00000", ["bbox"] = box,
            ["assets"] = new Dictionary<string, object> { ["aws"] = new Dictionary<string, object> { ["href"] = FixtureFile, ["file:size"] = new FileInfo(file).Length } },
        }));
        File.WriteAllText(Path.Combine(dir, "SOURCE.txt"),
            $"A trimmed extract of Overture Maps Foundation's buildings theme, release {OvertureBuildings.Release}: the {rows.Count} buildings reaching\n"
            + "downtown Tomball, Texas's nine world tiles round (30.09715, -95.61606), only the columns a footprint is made of, the geometry\n"
            + "rounded to a ten millionth of a degree, in six row groups west to east. Recorded by\n"
            + "WorldBuildingsTests.Record_the_overture_fixture_from_the_network. Each building's sources and licences are its own\n"
            + "(OpenStreetMap ODbL-1.0, (c) OpenStreetMap contributors; Microsoft ML Buildings ODbL-1.0; USGS Lidar).\n");
        await using var check = await ParquetReader.CreateAsync(file);
        var xmin = check.Schema.DataFields.First(d => d.Path.Equals(new FieldPath("bbox", "xmin")));
        _o.WriteLine($"{file}: {new FileInfo(file).Length / 1024.0:F0} KB, {check.RowGroupCount} row groups, statistics "
                     + (check.RowGroups[0].GetStatistics(xmin)?.MinValue != null ? "written" : "missing"));
    }

    /// <summary>
    /// Real tiles over the network (OPENFPS_WORLD_NET=1 only): downtown Tomball's nine tiles with Overture's buildings,
    /// OpenStreetMap's roads, ESA WorldCover and 3DEP, every cache cold in a temporary store; the time and the bytes
    /// Overture took for each tile, and what the store holds; then the same tiles again from the caches.
    /// </summary>
    [Fact]
    public async Task Real_buildings_at_tomball()
    {
        if (Environment.GetEnvironmentVariable("OPENFPS_WORLD_NET") != "1") return;
        string sources = Path.Combine(_dir, "sources");
        OvertureBuildings? overture = null;
        WorldTileService Make(string name)
        {
            overture = new OvertureBuildings(Path.Combine(sources, "overture"));
            return new WorldTileService(new WorldStore(Path.Combine(_dir, name)), new Usgs3Dep())
            {
                LandCover = new EsaWorldCover(Path.Combine(sources, "worldcover")),
                Features = new WorldFeatures(new OverpassRegions(Path.Combine(sources, "osm")), Sizes, overture, Prefabs),
            };
        }
        var centre = WorldTileKey.OfLatLon(Tomball.Lat, Tomball.Lon);
        foreach (var pass in new[] { "cold", "warm" })
        {
            var service = Make(pass);
            foreach (var (dx, dz) in new[] { (0, 0), (1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (-1, -1), (1, -1), (-1, 1) })
            {
                var k = centre.Offset(dx, dz);
                int req0 = overture!.Requests;
                long bytes0 = overture.BytesFetched;
                var clock = Stopwatch.StartNew();
                var t = await service.GetAsync(k);
                Assert.NotNull(t);
                long size = new FileInfo(Path.Combine(service.Store.Root, "tiles", "v" + WorldStore.GeneratorVersion, k.RelativePath, "full.json.gz")).Length;
                _o.WriteLine($"{pass} {k}: {clock.ElapsedMilliseconds} ms, {size / 1024.0:F1} KB; Overture {overture.Requests - req0} requests, "
                             + $"{(overture.BytesFetched - bytes0) / 1024.0:F0} KB; {t!.Entities.Count(x => x.Layer == "rooms")} rooms, "
                             + $"{t.Entities.Count(x => x.Layer is "roads" or "paths")} pieces of road");
            }
            long cache = Directory.EnumerateFiles(Path.Combine(sources, "overture"), "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
            _o.WriteLine($"{pass}: store {service.Store.TotalBytes / 1024.0:F0} KB, the Overture cache {cache / 1024.0:F0} KB");
        }
    }
}

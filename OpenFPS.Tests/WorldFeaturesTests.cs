using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using OpenFPS.Common.Components;
using OpenFPS.Common.Geometry;
using OpenFPS.Server.OneWorld;
using OpenFPS.Server.Repositories;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// The per-tile generator of what stands on the world outside the real places (docs/WORLD_STREAMING.md, "Roads
/// on the world's tiles"): OpenStreetMap's roads laid as gen_osm.py lays a place's, the same answer whichever
/// tile is made first, a road across an edge stored once and graded under on both sides, the real places
/// first, and the regional cache of OpenStreetMap read offline. Never the network: recorded regions and
/// made-up roads.
/// </summary>
public class WorldFeaturesTests : IDisposable
{
    private readonly ITestOutputHelper _o;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "openfps-world-features-" + Guid.NewGuid().ToString("N"));

    public WorldFeaturesTests(ITestOutputHelper o) => _o = o;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string Fixtures([System.Runtime.CompilerServices.CallerFilePath] string here = "")
        => Path.Combine(Path.GetDirectoryName(here)!, "Fixtures", "osm");

    private static string RepoFile(string relative, [System.Runtime.CompilerServices.CallerFilePath] string here = "")
        => Path.Combine(Path.GetDirectoryName(here)!, "..", relative);

    private static readonly Dictionary<string, Vector3> Sizes = LoadSizes();

    private static Dictionary<string, Vector3> LoadSizes()
    {
        var repo = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
        return repo.Prefabs.ToDictionary(kv => kv.Key, kv => kv.Value.ColliderSize ?? Vector3.One, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Gentle hills, the same height at a point whatever is asked: windows and tiles agree exactly.</summary>
    private sealed class Hills : IElevationSource
    {
        public string Name => "made-up hills";
        public int Windows;
        public static float At(double e, double n) => (float)(60 + 3 * Math.Sin(e / 80.0) + 2 * Math.Cos(n / 65.0));

        public Task<float[]?> HeightsAsync(WorldTileKey key, int posts, double spacing, CancellationToken ct)
            => WindowAsync(key.Zone, key.North, key.Easting, key.Northing, posts, spacing, ct);

        public Task<float[]?> WindowAsync(int zone, bool north, double west, double south, int posts, double spacing, CancellationToken ct)
        {
            Interlocked.Increment(ref Windows);
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
        public bool Fail;
        public int Calls;

        public void Add(long id, Dictionary<string, string> tags, params (double E, double N)[] pts)
        {
            var w = new OsmWay { Id = id, Tags = tags, Nodes = new long[pts.Length], Lat = new double[pts.Length], Lon = new double[pts.Length] };
            for (int k = 0; k < pts.Length; k++)
            {
                var (lat, lon) = Utm.ToLatLon(15, true, pts[k].E, pts[k].N);
                w.Lat[k] = lat; w.Lon[k] = lon;
                w.Nodes[k] = NodeId(pts[k]);
            }
            Ways.Add(w);
        }

        /// <summary>The same point is the same node, so roads that meet share one.</summary>
        private static long NodeId((double E, double N) p) => (long)Math.Round(p.E * 10) * 100_000_000L + (long)Math.Round(p.N * 10);

        public Task<IReadOnlyList<OsmWay>> HighwaysAsync(double south, double west, double north, double east, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            if (Fail) throw new HttpRequestException("no network");
            var near = Ways.Where(w => Enumerable.Range(0, w.Lat.Length).Any(k => w.Lat[k] >= south && w.Lat[k] <= north && w.Lon[k] >= west && w.Lon[k] <= east)
                                       || (w.Lat.Min() <= north && w.Lat.Max() >= south && w.Lon.Min() <= east && w.Lon.Max() >= west))
                           .OrderBy(w => w.Id).ToList();
            return Task.FromResult<IReadOnlyList<OsmWay>>(near);
        }
    }

    private static readonly WorldTileKey A = new(15, true, 1000, 13300);
    private static readonly WorldTileKey B = A.Offset(1, 0);

    /// <summary>A main road running east across the edge between A and B with a bend in it, a side street off it
    /// that meets it a few metres from the edge, and a lane with sidewalks.</summary>
    private static MadeUpOsm Crossing()
    {
        var osm = new MadeUpOsm();
        double e = B.Easting, n = A.Northing;
        osm.Add(1, new() { ["highway"] = "secondary", ["name"] = "Main Street", ["lanes"] = "4" },
                (e - 220, n + 100), (e - 60, n + 120), (e + 6, n + 125), (e + 90, n + 140), (e + 230, n + 110));
        osm.Add(2, new() { ["highway"] = "residential", ["name"] = "Oak Lane", ["surface"] = "gravel" },
                (e + 6, n + 125), (e + 10, n + 40), (e + 30, n + 5));
        osm.Add(3, new() { ["highway"] = "residential", ["name"] = "Elm Street", ["sidewalk"] = "both" },
                (e - 200, n + 200), (e - 10, n + 210), (e + 200, n + 230));
        osm.Add(4, new() { ["highway"] = "footway" }, (e - 100, n + 20), (e + 100, n + 20));
        osm.Add(5, new() { ["highway"] = "service", ["service"] = "driveway" }, (e - 50, n + 60), (e - 50, n + 90));
        return osm;
    }

    private WorldTileService Service(string name, IOsmSource osm, IElevationSource? survey = null, Func<WorldTileKey, bool>? placed = null)
    {
        var store = new WorldStore(Path.Combine(_dir, name));
        return new WorldTileService(store, survey ?? new Hills()) { Features = new WorldFeatures(osm, Sizes) { IsPlaced = placed } };
    }

    private static byte[] Bytes(WorldStore store, WorldTileKey k)
    {
        Assert.True(store.TryRead(k, out var t));
        t.MadeUtc = default;
        return t.ToBytes();
    }

    // ═══ The port ═══════════════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("residential", null, null, null, 6.0)]
    [InlineData("secondary", "4", null, null, 14.2)]
    [InlineData("tertiary", "1", null, null, 4.0)]
    [InlineData("service", null, "yes", null, 4.0)]
    [InlineData("residential", null, null, "9 m", 9.0)]
    [InlineData("residential", null, null, "1.5", 2.5)]
    [InlineData("motorway", "6", null, null, 21.0)]
    public void A_road_is_as_wide_as_gen_osm_makes_it(string hw, string? lanes, string? oneway, string? width, double want)
    {
        var w = new OsmWay { Tags = new() { ["highway"] = hw } };
        if (lanes != null) w.Tags["lanes"] = lanes;
        if (oneway != null) w.Tags["oneway"] = oneway;
        if (width != null) w.Tags["width"] = width;
        Assert.Equal(want, WorldFeatures.WidthOf(w), 3);
    }

    [Fact]
    public void Which_ways_are_roads_and_what_they_are_made_of()
    {
        OsmWay W(params (string, string)[] tags) => new() { Nodes = new long[] { 1, 2 }, Tags = tags.ToDictionary(t => t.Item1, t => t.Item2) };
        Assert.True(WorldFeatures.IsRoad(W(("highway", "residential"))));
        Assert.True(WorldFeatures.IsRoad(W(("highway", "service"))));
        Assert.False(WorldFeatures.IsRoad(W(("highway", "service"), ("service", "driveway"))));
        Assert.False(WorldFeatures.IsRoad(W(("highway", "service"), ("oneway", "yes"))));
        Assert.False(WorldFeatures.IsRoad(W(("highway", "footway"))));
        Assert.False(WorldFeatures.IsRoad(W(("highway", "proposed"))));
        Assert.False(WorldFeatures.IsRoad(W(("highway", "pedestrian"), ("area", "yes"))));
        Assert.Equal("asphalt_road", WorldFeatures.SurfaceOf(W(("highway", "residential"))));
        Assert.Equal("gravel_floor", WorldFeatures.SurfaceOf(W(("surface", "unpaved"))));
        Assert.Equal("concrete_floor", WorldFeatures.SurfaceOf(W(("surface", "concrete:plates"))));
        Assert.Equal("asphalt_road", WorldFeatures.SurfaceOf(W(("surface", "something new"))));
        Assert.Equal("Private road", WorldFeatures.NameOf(W(("highway", "residential"), ("access", "private"))));
        Assert.Equal("Service road", WorldFeatures.NameOf(W(("highway", "service"))));
        Assert.Equal("Unnamed road", WorldFeatures.NameOf(W(("highway", "residential"))));
        foreach (var p in WorldFeatures.Surface.Values.Append("named_place")) Assert.True(Sizes.ContainsKey(p), $"no prefab {p}");
    }

    /// <summary>
    /// Magnolia's roads laid by the port against the same roads in Magnolia's map, which gen_osm.py laid: the
    /// same OpenStreetMap ways (the place's osm.json) on the same ground (the map's own survey posts), so every
    /// piece of carriageway and sidewalk in a tile round the spawn should be the map's piece, to the millimetre and
    /// the same turn.
    /// </summary>
    [Fact]
    public void Magnolia_s_roads_are_laid_as_gen_osm_laid_them()
    {
        var map = MapRepository.LoadFromFile(Path.Combine(AppContext.BaseDirectory, "places", "magnolia_tx.json"))!;
        var u = map.Utm!;
        var e = map.Elevation!;
        using var doc = JsonDocument.Parse(File.ReadAllText(RepoFile("tools/places/magnolia_tx/osm.json")));
        var nodes = doc.RootElement.GetProperty("nodes");
        var ways = new List<OsmWay>();
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
        ways.Sort((a, b) => a.Id.CompareTo(b.Id));

        // The tile the spawn is in and the one east of it.
        var spawn = WorldTileKey.Of(u.Zone, u.North, u.Easting, u.Northing);
        int matched = 0, total = 0;
        double worstXz = 0, worstY = 0, worstTurn = 0;
        var mapPieces = map.Entities.Where(m => m.Layer is "roads" or "paths").ToList();
        foreach (var key in new[] { spawn, spawn.Offset(1, 0), spawn.Offset(0, 1) })
        {
            int n = WorldFeatures.WindowPosts;
            double west = key.Easting - WorldFeatures.MarginMetres, south = key.Northing - WorldFeatures.MarginMetres;
            var window = new float[n * n];
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                    window[j * n + i] = (float)(e.HeightAt(west + i * 2 - u.Easting, south + j * 2 - u.Northing) - e.SeaLevelY);
            var laid = new WorldFeatures(new MadeUpOsm(), Sizes).Lay(key, ways, window);
            foreach (var p in laid.Entities.Where(x => x.Layer is "roads" or "paths"))
            {
                total++;
                var at = new Vector3((float)(p.Position.X + key.Easting - u.Easting), (float)(p.Position.Y + e.SeaLevelY),
                                     (float)(p.Position.Z + key.Northing - u.Northing));
                var twin = mapPieces.Where(m => m.PrefabId == p.PrefabId)
                                    .MinBy(m => Vector2.DistanceSquared(new Vector2(m.Position.X, m.Position.Z), new Vector2(at.X, at.Z)));
                if (twin == null) continue;
                double dxz = Vector2.Distance(new Vector2(twin.Position.X, twin.Position.Z), new Vector2(at.X, at.Z));
                if (dxz > 0.01) continue;
                matched++;
                worstXz = Math.Max(worstXz, dxz);
                worstY = Math.Max(worstY, Math.Abs(twin.Position.Y - at.Y));
                worstTurn = Math.Max(worstTurn, 1 - Math.Abs(Quaternion.Dot(Quaternion.Normalize(twin.Rotation), Quaternion.Normalize(p.Rotation))));
                Assert.Equal(twin.Scale.X, p.Scale.X, 2);
                Assert.Equal(twin.Scale.Z, p.Scale.Z, 2);
            }
        }
        _o.WriteLine($"{matched} of {total} pieces laid by the port are the map's (within 1 cm): worst {worstXz * 1000:F2} mm across, "
                     + $"{worstY * 1000:F2} mm in height, turn 1 - |dot| {worstTurn:E1}");
        Assert.True(total > 50, "the tiles round Magnolia's spawn have roads");
        Assert.True(matched >= total * 0.97, $"{matched} of {total}");
        Assert.True(worstY < 0.002, $"heights differ by {worstY} m");
        Assert.True(worstTurn < 1e-5);
    }

    // ═══ Tiles that agree ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The road across the edge between A and B: each piece is stored once, in the tile its middle is in; both
    /// tiles are graded under every piece that reaches them, so their shared edge is one line of posts and no
    /// piece has ground above its underside; and A then B is byte for byte B then A.
    /// </summary>
    [Fact]
    public async Task A_road_across_an_edge_is_the_same_road_from_either_side()
    {
        var one = Service("one", Crossing());
        var two = Service("two", Crossing());
        foreach (var k in new[] { A, B }) Assert.NotNull(await one.GetAsync(k));
        foreach (var k in new[] { B, A }) Assert.NotNull(await two.GetAsync(k));
        Assert.Equal(Bytes(one.Store, A), Bytes(two.Store, A));
        Assert.Equal(Bytes(one.Store, B), Bytes(two.Store, B));

        Assert.True(one.Store.TryRead(A, out var a));
        Assert.True(one.Store.TryRead(B, out var b));
        // Pieces in the zone's metres: none twice.
        var all = a.Entities.Select(x => (Tile: A, E: x)).Concat(b.Entities.Select(x => (Tile: B, E: x))).ToList();
        var abs = all.Select(x => (x.E.PrefabId, x.E.Name, X: Math.Round(x.E.Position.X + x.Tile.Easting, 2), Z: Math.Round(x.E.Position.Z + x.Tile.Northing, 2))).ToList();
        Assert.Equal(abs.Count, abs.Distinct().Count());
        // Each in the tile its middle is in.
        foreach (var (t, x) in all)
        {
            Assert.InRange(x.Position.X, 0f, 250f);
            Assert.InRange(x.Position.Z, 0f, 250f);
        }
        Assert.Contains(a.Entities, x => x.Name == "Main Street" && x.Layer == "roads");
        Assert.Contains(b.Entities, x => x.Name == "Main Street" && x.Layer == "roads");
        Assert.Contains(b.Entities, x => x.Name == "Oak Lane" && x.PrefabId == "gravel_floor");
        Assert.Contains(all, x => x.E.Name == "Elm Street sidewalk" && x.E.PrefabId == "concrete_floor" && x.E.Layer == "paths");
        Assert.Contains(all, x => x.E.Name == "Main Street" && x.E.Layer == "zones" && x.E.PrefabId == "named_place");
        Assert.Contains(all, x => x.E.Name == "Main Street and Oak Lane junction");
        Assert.DoesNotContain(all, x => x.E.Name is "Footpath" or "Service road");
        // Main Street with four lanes is 14.2 m across.
        var main = a.Entities.First(x => x.Name == "Main Street" && x.Layer == "roads");
        Assert.Equal(14.2f, main.Scale.Z * Sizes["asphalt_road"].Z, 2);

        // The shared edge: A's east posts are B's west posts.
        int p = WorldTileService.Posts;
        var ha = a.Terrain!; var hb = b.Terrain!;
        int edgeDiffers = 0, graded = 0;
        for (int j = 0; j < p; j++)
        {
            float ya = ha.BaseY + ha.HeightsCm[j * p + p - 1] * 0.01f, yb = hb.BaseY + hb.HeightsCm[j * p] * 0.01f;
            if (MathF.Abs(ya - yb) > 0.0101f) edgeDiffers++;
            if (MathF.Abs(ya - Hills.At(B.Easting, B.Northing + j * 2)) > 0.011f) graded++;
        }
        _o.WriteLine($"{a.Entities.Count} things in A, {b.Entities.Count} in B; {graded} of {p} posts on the shared edge graded to a road");
        Assert.Equal(0, edgeDiffers);
        Assert.True(graded > 0, "Main Street crosses the edge, and the edge is graded under it");

        // No ground above any piece's underside, on either side of the edge, under every piece of either tile.
        var fa = Field(ha); var fb = Field(hb);
        int checkedPoints = 0;
        foreach (var (t, x) in all.Where(y => y.E.Layer is "roads" or "paths"))
        {
            var size = Sizes[x.PrefabId] * x.Scale;
            var r = x.Rotation;
            var centre = new Vector3(x.Position.X + (float)(t.Easting - A.Easting), x.Position.Y, x.Position.Z + (float)(t.Northing - A.Northing));
            for (float su = -0.45f; su <= 0.451f; su += 0.15f)
                for (float sv = -0.45f; sv <= 0.451f; sv += 0.3f)
                {
                    var under = centre + Vector3.Transform(new Vector3(su * size.X, -size.Y / 2, sv * size.Z), r);
                    var (field, baseY, ox) = under.X < 250f ? (fa, ha.BaseY, 0f) : (fb, hb.BaseY, 250f);
                    if (under.X < 0 || under.X > 500 || under.Z < 0 || under.Z > 250) continue;
                    float ground = baseY + field.HeightAt(under.X - ox, under.Z);
                    Assert.True(ground <= under.Y + 0.011f, $"ground {ground:F3} over the underside {under.Y:F3} of {x.Name} at {under}");
                    checkedPoints++;
                }
        }
        Assert.True(checkedPoints > 200);
    }

    private static Heightfield Field(WorldTile.TerrainData t) => t.ToComponent().Field(0f);

    private sealed class NoUsers : IUserRepository
    {
        public UserData? GetUser(string username) => null;
        public bool AddUser(string username, string password, UserRole role) => false;
        public bool VerifyPassword(string username, string password) => false;
    }

    /// <summary>
    /// On a server: a player arriving on Main Street in the world stands on the road (asphalt underfoot, on its
    /// surface), and the tiles round them hold its pieces, its named places and its junction, spawned as a map's
    /// things are.
    /// </summary>
    [Fact]
    public void A_player_arriving_on_a_made_road_stands_on_it()
    {
        string mapDir = Path.Combine(_dir, "maps");
        Directory.CreateDirectory(mapDir);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "maps", "speedway.json"), Path.Combine(mapDir, "speedway.json"));
        var maps = new OpenFPS.Server.Core.MapManager(new MapRepository(mapDir), new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs")));
        maps.Initialize();
        var sessions = new OpenFPS.Server.Core.SessionManager();
        var server = new OpenFPS.Server.GameServer(new NoUsers());
        server.Attach(maps, sessions);
        server.StartWorld(new WorldSettings { StorePath = Path.Combine(_dir, "world"), CapGigabytes = 1, Prebuild = false }, new Hills(),
                          features: new WorldFeatures(Crossing(), Sizes));
        var alice = new OpenFPS.Server.Core.UserSession { ConnectionId = 1, Username = "alice", CurrentMapId = "speedway", Welcomed = true };
        sessions.AddSession(1, alice);
        // A node of Main Street, in A.
        var (lat, lon) = Utm.ToLatLon(15, true, B.Easting - 60, A.Northing + 120);
        server.World!.Arrive(alice, new WorldPlace("main", "Main Street", lat, lon), _ => { });
        var clock = Stopwatch.StartNew();
        while (!(WorldMaps.IsWorldMap(alice.CurrentMapId) && alice.Entity != Arch.Core.Entity.Null))
        {
            Assert.True(clock.ElapsedMilliseconds < 20_000, "waited too long for the world");
            server.WorldTickForTest();
            Thread.Sleep(10);
        }
        Assert.True(server.World.TryGetFrame(alice.CurrentMapId, out var frame));
        while (frame.Loaded.Count < 9 && clock.ElapsedMilliseconds < 20_000) { server.WorldTickForTest(); Thread.Sleep(10); }
        Assert.True(maps.TryGetMap(alice.CurrentMapId, out var world, out _, out var grid, out _));
        var at = world.Get<OpenFPS.Common.Components.Transform>(alice.Entity).Position;
        float ground = OpenFPS.Common.PhysicsUtils.GetGroundHeight(world, grid, at + new Vector3(0, 5, 0), out string material);
        _o.WriteLine($"standing at {at}, the surface {ground:F3} of {material}, {frame.Loaded.Count} tiles loaded");
        Assert.Equal("Asphalt", material);
        Assert.InRange(at.Y - ground, -0.01f, 0.5f);
        float road = Hills.At(B.Easting - 60, A.Northing + 120) - frame.BaseY;
        Assert.InRange(ground - road, -0.3f, 0.4f);

        var names = new HashSet<string>();
        world.Query(new Arch.Core.QueryDescription().WithAll<OpenFPS.Common.Components.NameComponent>(),
                    (ref OpenFPS.Common.Components.NameComponent nm) => names.Add(nm.Name ?? ""));
        Assert.Contains("Main Street", names);
        Assert.Contains("Elm Street sidewalk", names);
        Assert.Contains("Main Street and Oak Lane junction", names);
    }

    /// <summary>
    /// Woods from the tile's land cover: the west half of A is tree cover, the east half grass. Canopy volumes,
    /// trunks and crowns stand only in the wooded half and inside the tile, none over a road or trunk in one;
    /// the canopy is 5 to 18 m over the ground; the same cover makes the same woods.
    /// </summary>
    [Fact]
    public async Task Woods_stand_where_the_land_cover_has_trees_and_keep_off_the_roads()
    {
        int n = WorldTileService.Posts - 1;
        var classes = new byte[n * n];
        for (int j = 0; j < n; j++) for (int i = 0; i < n; i++) classes[j * n + i] = (byte)(i < n / 2 ? 10 : 30);
        var osm = Crossing();
        var ways = await osm.HighwaysAsync(-90, -180, 90, 180, CancellationToken.None);
        var hills = new Hills();
        var window = (await hills.WindowAsync(15, true, A.Easting - WorldFeatures.MarginMetres, A.Northing - WorldFeatures.MarginMetres,
                                             WorldFeatures.WindowPosts, 2, CancellationToken.None))!;
        var features = new WorldFeatures(osm, Sizes);
        var laid = features.Lay(A, ways, window, classes);
        var trees = laid.Entities.Where(x => x.Layer == "trees").ToList();
        var canopy = trees.Where(x => x.PrefabId == "foliage_hedge").ToList();
        var trunks = trees.Where(x => x.PrefabId == "wood_floor").ToList();
        var crowns = trees.Where(x => x.PrefabId == "tree_crown").ToList();
        _o.WriteLine($"{canopy.Count} canopy volumes, {trunks.Count} trunks, {crowns.Count} crowns in half a tile of woods");
        Assert.NotEmpty(canopy);
        Assert.InRange(trunks.Count, 10, 60);                    // 31,250 m² at one in 1,600 m², less what the roads take
        Assert.NotEmpty(crowns);
        var roads = laid.Entities.Where(x => x.Layer is "roads" or "paths").ToList();
        foreach (var t in trees)
        {
            var half = t.PrefabId == "tree_crown" ? Vector3.Zero : Sizes[t.PrefabId] * t.Scale / 2;
            Assert.True(t.Position.X - half.X >= -0.01f && t.Position.X + half.X <= 125.01f, $"{t.Name} at {t.Position} is not in the woods");
            Assert.True(t.Position.Z - half.Z >= -0.01f && t.Position.Z + half.Z <= 250.01f);
            if (t.PrefabId == "foliage_hedge")
            {
                float ground = Hills.At(A.Easting + t.Position.X, A.Northing + t.Position.Z);
                // From the lowest ground under it 5 m up to the highest 18 m up: over its middle, no higher than 5 m
                // at the bottom and no lower than 18 m at the top.
                Assert.True(t.Position.Y - half.Y - ground <= 5.01f);
                Assert.True(t.Position.Y + half.Y - ground >= 17.99f);
            }
            if (t.PrefabId == "wood_floor")
                foreach (var r in roads)
                {
                    var rs = Sizes[r.PrefabId] * r.Scale;
                    var local = Vector3.Transform(t.Position - r.Position, Quaternion.Inverse(r.Rotation));
                    Assert.False(MathF.Abs(local.X) < rs.X / 2 && MathF.Abs(local.Z) < rs.Z / 2, $"a trunk at {t.Position} stands in {r.Name}");
                }
        }
        var again = features.Lay(A, ways, window, classes);
        Assert.Equal(laid.Entities.Count, again.Entities.Count);
        Assert.Equal(trees.Select(t => (t.PrefabId, t.Position)), again.Entities.Where(x => x.Layer == "trees").Select(t => (t.PrefabId, t.Position)));
        // No land cover, no woods.
        Assert.DoesNotContain(features.Lay(A, ways, window).Entities, x => x.Layer == "trees");
    }

    /// <summary>A tile of a real place is the place's: nothing generated reaches into it, so the roads stop at
    /// its edge, not on top of the place's own.</summary>
    [Fact]
    public async Task Nothing_made_reaches_into_a_real_place()
    {
        var service = Service("placed", Crossing(), placed: k => k == B);
        var a = await service.GetAsync(A);
        Assert.NotNull(a);
        foreach (var x in a!.Entities)
        {
            var size = Sizes[x.PrefabId] * x.Scale;
            float reach = MathF.Max(size.X, size.Z) / 2;
            Assert.True(x.Position.X + reach <= 250.01f || x.Layer != "roads",
                        $"{x.Name} at {x.Position} reaches {reach:F1} m and may cross into the place");
        }
        Assert.Contains(a.Entities, x => x.Name == "Main Street");
        Assert.DoesNotContain(a.Entities, x => x.Name == "Main Street and Oak Lane junction");
    }

    /// <summary>Without its roads a tile is not made (it would keep that hole); it is tried again later, and
    /// made when they can be had.</summary>
    [Fact]
    public async Task No_roads_to_be_had_is_tried_again_not_stored_bare()
    {
        var osm = Crossing();
        osm.Fail = true;
        var service = Service("fail", osm);
        service.RetryAfter = TimeSpan.FromMilliseconds(200);
        Assert.Null(await service.GetAsync(A));
        Assert.False(service.Store.Has(A));
        Assert.Equal(WorldTileService.TileState.Failed, service.StateOf(A));
        osm.Fail = false;
        await Task.Delay(300);
        var tile = await service.GetAsync(A);
        Assert.NotNull(tile);
        Assert.NotEmpty(tile!.Entities);
        Assert.Contains("made-up roads", tile.Features);
    }

    /// <summary>Where there are no roads the tile is its ground, as before, from the same window.</summary>
    [Fact]
    public async Task A_tile_with_no_roads_is_its_ground()
    {
        var service = Service("bare", new MadeUpOsm());
        var tile = await service.GetAsync(A);
        Assert.NotNull(tile);
        Assert.Empty(tile!.Entities);
        Assert.Null(tile.Features);
        var t = tile.Terrain!;
        int p = WorldTileService.Posts;
        for (int k = 0; k < p * p; k += 97)
            Assert.Equal(Hills.At(A.Easting + k % p * 2, A.Northing + k / p * 2), t.BaseY + t.HeightsCm[k] * 0.01f, 2);
    }

    // ═══ OpenStreetMap from the regional cache ═════════════════════════════════════════════════════════

    /// <summary>Records the fixture from Overpass (OPENFPS_WORLD_NET=1 only): the regions round downtown Tomball,
    /// Texas, as the cache keeps them.</summary>
    [Fact]
    public async Task Record_the_osm_fixture_from_the_network()
    {
        if (Environment.GetEnvironmentVariable("OPENFPS_WORLD_NET") != "1") return;
        var osm = new OverpassRegions(Fixtures());
        var features = new WorldFeatures(osm, Sizes);
        var centre = WorldTileKey.OfLatLon(Tomball.Lat, Tomball.Lon);
        for (int dx = -2; dx <= 2; dx++)
            for (int dz = -2; dz <= 2; dz++)
                await features.WaysAsync(centre.Offset(dx, dz), CancellationToken.None);
        _o.WriteLine($"{osm.Fetches} regions fetched into {Fixtures()}");
    }

    /// <summary>
    /// Real tiles from the network (OPENFPS_WORLD_NET=1 only): downtown Tomball's 3 x 3 tiles made with 3DEP,
    /// Overpass and WorldCover, everything kept in temporary caches; how long the first tile takes (its regions
    /// fetched) and the rest, and what they hold.
    /// </summary>
    [Fact]
    public async Task Real_tiles_at_tomball()
    {
        if (Environment.GetEnvironmentVariable("OPENFPS_WORLD_NET") != "1") return;
        var store = new WorldStore(Path.Combine(_dir, "real"));
        var service = new WorldTileService(store, new Usgs3Dep())
        {
            LandCover = new EsaWorldCover(Path.Combine(store.Root, "sources", "worldcover")),
            Features = new WorldFeatures(new OverpassRegions(Path.Combine(store.Root, "sources", "osm")), Sizes),
        };
        var centre = WorldTileKey.OfLatLon(Tomball.Lat, Tomball.Lon);
        foreach (var (dx, dz) in new[] { (0, 0), (1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (-1, -1), (1, -1), (-1, 1) })
        {
            var k = centre.Offset(dx, dz);
            var clock = Stopwatch.StartNew();
            var t = await service.GetAsync(k);
            Assert.NotNull(t);
            long bytes = new FileInfo(Path.Combine(store.Root, "tiles", "v" + WorldStore.GeneratorVersion, k.RelativePath, "full.json.gz")).Length;
            _o.WriteLine($"{k}: {clock.ElapsedMilliseconds} ms, {bytes / 1024.0:F1} KB, {t!.Entities.Count(x => x.Layer is "roads" or "paths")} pieces, "
                         + $"{t.Entities.Count(x => x.Layer == "trees")} trees, materials {string.Join("/", t.Terrain!.Materials)}");
        }
        long cache = Directory.EnumerateFiles(Path.Combine(store.Root, "sources"), "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        _o.WriteLine($"store {store.TotalBytes / 1024.0:F0} KB, regional cache {cache / 1024.0:F0} KB");
    }

    /// <summary>Downtown Tomball, Texas, by Main Street, 13 km south-east of Magnolia.</summary>
    private static readonly (double Lat, double Lon) Tomball = (30.09715, -95.61606);

    private string CopyFixtures()
    {
        string to = Path.Combine(_dir, "sources", "osm");
        foreach (var f in Directory.EnumerateFiles(Fixtures(), "*", SearchOption.AllDirectories))
        {
            string dest = Path.Combine(to, Path.GetRelativePath(Fixtures(), f));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(f, dest);
        }
        return to;
    }

    /// <summary>
    /// Downtown Tomball from the recorded regions, offline: 25 tiles made, their roads named as OpenStreetMap
    /// names them, a junction of two of them, the time a tile takes (the survey answering at once) and what the
    /// store holds; then the same tiles made again in the other order are the same bytes.
    /// </summary>
    [Fact]
    public async Task Tomball_from_the_regional_cache()
    {
        string cache = CopyFixtures();
        int asked = 0;
        var osm = new OverpassRegions(cache, (_, _) => { Interlocked.Increment(ref asked); throw new HttpRequestException("no network"); });
        var store = new WorldStore(Path.Combine(_dir, "world"));
        var service = new WorldTileService(store, new Hills()) { Features = new WorldFeatures(osm, Sizes) };
        var centre = WorldTileKey.OfLatLon(Tomball.Lat, Tomball.Lon);
        var keys = new List<WorldTileKey>();
        for (int dz = -2; dz <= 2; dz++)
            for (int dx = -2; dx <= 2; dx++) keys.Add(centre.Offset(dx, dz));
        var clock = Stopwatch.StartNew();
        var times = new List<long>();
        foreach (var k in keys)
        {
            var one = Stopwatch.StartNew();
            Assert.NotNull(await service.GetAsync(k));
            times.Add(one.ElapsedMilliseconds);
        }
        long all = clock.ElapsedMilliseconds;
        Assert.Equal(0, asked);
        var tiles = keys.Select(k => { Assert.True(store.TryRead(k, out var t)); return t; }).ToList();
        var names = tiles.SelectMany(t => t.Entities).Where(x => x.Layer == "roads").Select(x => x.Name).Distinct().ToList();
        int pieces = tiles.Sum(t => t.Entities.Count(x => x.Layer is "roads" or "paths"));
        int zones = tiles.Sum(t => t.Entities.Count(x => x.Layer == "zones"));
        var sizes = keys.Select(k => new FileInfo(Path.Combine(store.Root, "tiles", "v" + WorldStore.GeneratorVersion, k.RelativePath, "full.json.gz")).Length).ToList();
        times.Sort();
        _o.WriteLine($"Tomball, 25 tiles round {centre}: {all} ms, a tile median {times[12]} ms, most {times[^1]} ms (the first reads the regions); "
                     + $"{pieces} pieces of road and sidewalk, {zones} named places, {names.Count} roads by name; "
                     + $"{store.TotalBytes / 1024.0:F0} KB stored, a tile median {sizes.OrderBy(s => s).ElementAt(12) / 1024.0:F1} KB, most {sizes.Max() / 1024.0:F1} KB");
        _o.WriteLine("roads: " + string.Join(", ", names.Take(30)));
        Assert.Contains("West Main Street", names);
        Assert.True(pieces > 100);
        Assert.Contains(tiles.SelectMany(t => t.Entities), x => x.Layer == "zones" && x.Name!.EndsWith(" junction"));

        // The other order, a fresh store and the same cache: the same tiles.
        var again = new WorldTileService(new WorldStore(Path.Combine(_dir, "again")), new Hills())
        {
            Features = new WorldFeatures(new OverpassRegions(cache, (_, _) => throw new HttpRequestException("no network")), Sizes),
        };
        foreach (var k in Enumerable.Reverse(keys)) Assert.NotNull(await again.GetAsync(k));
        foreach (var k in keys) Assert.Equal(Bytes(store, k), Bytes(again.Store, k));
    }

    /// <summary>Overpass's answer is read into ways, whole and in order of id, a way with a missing node left out.</summary>
    [Fact]
    public void An_overpass_answer_is_read()
    {
        const string json = """
            {"version":0.6,"elements":[
             {"type":"way","id":7,"nodes":[1,2],"geometry":[{"lat":30.1,"lon":-95.7},{"lat":30.2,"lon":-95.6}],"tags":{"highway":"residential","name":"A Street"}},
             {"type":"way","id":3,"nodes":[4,5],"geometry":[{"lat":30.1,"lon":-95.7},null],"tags":{"highway":"service"}},
             {"type":"way","id":2,"nodes":[1,9],"geometry":[{"lat":30.1,"lon":-95.7},{"lat":30.15,"lon":-95.75}],"tags":{"highway":"primary"}}]}
            """;
        var ways = OverpassRegions.Parse(json);
        Assert.Equal(new long[] { 2, 7 }, ways.Select(w => w.Id).ToArray());
        Assert.Equal("A Street", ways[1].Tag("name"));
        Assert.Equal(new long[] { 1, 2 }, ways[1].Nodes);
        Assert.Equal(-95.6, ways[1].Lon[1], 9);
        Assert.Throws<InvalidDataException>(() => OverpassRegions.Parse("{\"remark\":\"runtime error: timeout\"}"));
    }

    /// <summary>A region is asked for once, at the data's date, and read from the cache ever after; a road that
    /// reaches into two regions is one way.</summary>
    [Fact]
    public async Task A_region_is_asked_for_once()
    {
        var queries = new List<string>();
        string answer = """
            {"elements":[{"type":"way","id":11,"nodes":[1,2],"geometry":[{"lat":30.049,"lon":-95.7},{"lat":30.051,"lon":-95.7}],"tags":{"highway":"residential"}}]}
            """;
        var osm = new OverpassRegions(Path.Combine(_dir, "osm"), (q, _) => { lock (queries) queries.Add(q); return Task.FromResult(answer); });
        var ways = await osm.HighwaysAsync(30.045, -95.69, 30.055, -95.66, CancellationToken.None);
        Assert.Equal(2, queries.Count);                      // two regions, 30.00-30.05 and 30.05-30.10
        Assert.All(queries, q => Assert.Contains($"[date:\"{OverpassRegions.DataDate}\"]", q));
        Assert.Single(ways);
        var offline = new OverpassRegions(Path.Combine(_dir, "osm"), (_, _) => throw new HttpRequestException("no network"));
        Assert.Single(await offline.HighwaysAsync(30.045, -95.69, 30.055, -95.66, CancellationToken.None));
        Assert.True(File.Exists(Path.Combine(_dir, "osm", "SOURCE.txt")));
    }
}

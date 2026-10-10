using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Geometry;
using OpenFPS.Server;
using OpenFPS.Server.Core;
using OpenFPS.Server.OneWorld;
using OpenFPS.Server.Repositories;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// Magnolia copied into the one world (docs/WORLD_STREAMING.md, Places in the world): /join world magnolia
/// arrives at the map's own spawn, among the same houses, roads, lawns and named places, on the same ground,
/// with the place's roads and traffic; the tiles are kept in the store as placed tiles the cap never drops.
/// </summary>
public class WorldPlacesTests : IDisposable
{
    private readonly ITestOutputHelper _o;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "openfps-world-places-" + Guid.NewGuid().ToString("N"));

    public WorldPlacesTests(ITestOutputHelper o) => _o = o;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed class NoUsers : IUserRepository
    {
        public UserData? GetUser(string username) => null;
        public bool AddUser(string username, string password, UserRole role) => false;
        public bool VerifyPassword(string username, string password) => false;
    }

    /// <summary>Flat ground at 60 m: anything of it under a place would show.</summary>
    private sealed class Flat : IElevationSource
    {
        public string Name => "flat";
        public int Calls;
        public Task<float[]?> HeightsAsync(WorldTileKey key, int posts, double spacing, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            var h = new float[posts * posts];
            Array.Fill(h, 60f);
            return Task.FromResult<float[]?>(h);
        }
    }

    private const string Id = "magnolia_tx";

    private (MapManager Maps, SessionManager Sessions, GameServer Server, Flat Survey) Rig(double capGigabytes = 1)
    {
        string mapDir = Path.Combine(_dir, "maps");
        Directory.CreateDirectory(Path.Combine(mapDir, "places"));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "maps", "speedway.json"), Path.Combine(mapDir, "speedway.json"));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "places", Id + ".json"), Path.Combine(mapDir, "places", Id + ".json"));
        RealPlaceMapTests.CopyGround(Id, Path.Combine(mapDir, "places"));
        AcousticRegistry.Initialize();
        var maps = new MapManager(new MapRepository(mapDir), new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs")));
        maps.Initialize();
        var sessions = new SessionManager();
        var server = new GameServer(new NoUsers());
        server.Attach(maps, sessions);
        var survey = new Flat();
        var started = System.Diagnostics.Stopwatch.StartNew();
        server.StartWorld(new WorldSettings { StorePath = Path.Combine(_dir, "world"), CapGigabytes = capGigabytes, Prebuild = false }, survey);
        _o.WriteLine($"the world started, Magnolia copied out of its map, in {started.ElapsedMilliseconds} ms");
        Assert.NotNull(server.World);
        return (maps, sessions, server, survey);
    }

    private static void Until(GameServer server, Func<bool> done, int ms = 120_000)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!done())
        {
            Assert.True(clock.ElapsedMilliseconds < ms, "waited too long for the world");
            server.WorldTickForTest();
            Thread.Sleep(5);
        }
    }

    private readonly struct OnlyOwners : IGeometryFilter
    {
        private readonly HashSet<int> _owners;
        public OnlyOwners(HashSet<int> owners) => _owners = owners;
        public bool Accept(int owner, in Surface surface) => _owners.Contains(owner);
    }

    private static HashSet<int> TerrainIds(World ecs)
    {
        var ids = new HashSet<int>();
        ecs.Query(new QueryDescription().WithAll<TerrainTileComponent>(), (Entity e) => ids.Add(e.Id));
        return ids;
    }

    [Fact]
    public void Joining_the_world_at_magnolia_arrives_among_the_maps_houses_on_its_ground()
    {
        var (maps, sessions, server, survey) = Rig();
        var world = server.World!;
        var place = Assert.Single(world.Copied!.Places);
        Assert.Equal(Id, place.MapId);
        Assert.True(maps.TryGetMapData(Id, out var map));
        var target = world.FindPlace("magnolia");
        Assert.NotNull(target);
        Assert.Equal(Id, target!.Id);

        var alice = new UserSession { ConnectionId = 1, Username = "alice", CurrentMapId = "speedway", Welcomed = true };
        sessions.AddSession(1, alice);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        world.Arrive(alice, target, _ => { });
        Until(server, () => WorldMaps.IsWorldMap(alice.CurrentMapId) && alice.Entity != Entity.Null);
        _o.WriteLine($"arrived in {clock.ElapsedMilliseconds} ms; {survey.Calls} tiles asked of the survey (outside the place)");
        Assert.True(world.TryGetFrame(alice.CurrentMapId, out var frame));
        Assert.Contains(Id, frame.PlacesTaken);
        Assert.True(maps.TryGetMap(frame.Id, out var fw, out _, out var fgrid, out _));
        Assert.True(maps.TryGetMap(Id, out var mw, out _, out var mgrid, out _));

        // The map and the frame differ by a move, never a turn.
        var move = new Vector3((float)(place.Easting - frame.Origin.Easting), (float)(place.SeaY - frame.BaseY),
                               (float)(place.Northing - frame.Origin.Northing));
        var spawn = map.SpawnPoint.Position;
        var at = fw.Get<Transform>(alice.Entity).Position;
        _o.WriteLine($"spawn on the map ({spawn.X:F2}, {spawn.Z:F2}, {spawn.Y:F2}); in the world ({at.X:F2}, {at.Z:F2}, {at.Y:F2}), the map moved by {move}");
        Assert.InRange(Vector3.Distance(new Vector3(at.X, 0, at.Z), new Vector3(spawn.X + move.X, 0, spawn.Z + move.Z)), 0f, 0.01f);

        // Everything fixed of the map within 400 m of the spawn is in the world, where the map has it: the same
        // kind of thing (collider, region, door), the same name, the same turn.
        static string What(World w, Entity e)
        {
            string name = w.Has<NameComponent>(e) ? w.Get<NameComponent>(e).Name ?? "" : w.Has<IdentityComponent>(e) ? w.Get<IdentityComponent>(e).Name ?? "" : "";
            string kind = w.Has<ColliderComponent>(e) ? $"{w.Get<ColliderComponent>(e).Shape}{w.Get<ColliderComponent>(e).Size:F2}" : "";
            if (w.Has<RegionComponent>(e)) kind += $" room {w.Get<RegionComponent>(e).IsIndoor} {string.Join(",", w.Get<RegionComponent>(e).Materials)}";
            if (w.Has<PortalComponent>(e)) kind += " doorway";
            if (w.Has<DoorComponent>(e)) kind += " door";
            return name + "|" + kind;
        }
        static bool Fixed(World w, Entity e) => w.Has<Transform>(e) && !w.Has<Velocity>(e) && !w.Has<PlayerComponent>(e)
                                                && !w.Has<TerrainTileComponent>(e) && !w.Has<ZoneComponent>(e);
        var inFrame = new Dictionary<(string What, int X, int Y, int Z), int>();
        fw.Query(new QueryDescription().WithAll<Transform>(), (Entity e, ref Transform t) =>
        {
            if (!Fixed(fw, e)) return;
            var p = t.Position - move;
            var k = (What(fw, e), (int)MathF.Round(p.X * 10), (int)MathF.Round(p.Y * 10), (int)MathF.Round(p.Z * 10));
            inFrame[k] = inFrame.GetValueOrDefault(k) + 1;
        });
        int near = 0, found = 0;
        var missing = new List<string>();
        mw.Query(new QueryDescription().WithAll<Transform>(), (Entity e, ref Transform t) =>
        {
            if (!Fixed(mw, e)) return;
            var p = t.Position;
            if (Vector2.Distance(new Vector2(p.X, p.Z), new Vector2(spawn.X, spawn.Z)) > 400f) return;
            near++;
            string what = What(mw, e);
            bool hit = false;
            for (int dx = -1; dx <= 1 && !hit; dx++)
                for (int dy = -1; dy <= 1 && !hit; dy++)
                    for (int dz = -1; dz <= 1 && !hit; dz++)
                        hit = inFrame.ContainsKey((what, (int)MathF.Round(p.X * 10) + dx, (int)MathF.Round(p.Y * 10) + dy, (int)MathF.Round(p.Z * 10) + dz));
            if (hit) found++; else if (missing.Count < 10) missing.Add($"{what} at {p}");
        });
        foreach (var m in missing) _o.WriteLine("missing: " + m);
        _o.WriteLine($"{found} of {near} fixed things of the map within 400 m of the spawn are in the world, the same, where the map has them");
        Assert.True(near > 3000, $"only {near} things near the spawn");
        Assert.Equal(near, found);

        // The same ground: the world's terrain against the map's over the loaded ring, metres over the sea.
        var mapOnly = new OnlyOwners(TerrainIds(mw));
        var frameOnly = new OnlyOwners(TerrainIds(fw));
        Assert.True(maps.TryGetGeometry(Id, out var mgeo));
        Assert.True(maps.TryGetGeometry(frame.Id, out var fgeo));
        var diffs = new List<double>();
        var rng = new Random(3);
        float seaMap = (float)place.SeaY, seaFrame = frame.BaseY;
        while (diffs.Count < 4000)
        {
            float x = spawn.X + (float)(rng.NextDouble() * 1200 - 600), z = spawn.Z + (float)(rng.NextDouble() * 1200 - 600);
            float ym = mgeo.World.FloorAt(x, z, 1000f, GeometryLayers.Ground, ref mapOnly, out _);
            float yf = fgeo.World.FloorAt(x + move.X, z + move.Z, 1000f, GeometryLayers.Ground, ref frameOnly, out _);
            if (ym <= -1000f || yf <= -1000f) continue;      // a bank too steep to stand on, in either
            diffs.Add(Math.Abs((ym + seaMap) - (yf + seaFrame)));
        }
        diffs.Sort();
        double median = diffs[diffs.Count / 2], p99 = diffs[(int)(diffs.Count * 0.99)], worst = diffs[^1];
        _o.WriteLine($"map against world ground, 4,000 points within 600 m of the spawn: median {median * 100:F2} cm, 99th {p99 * 100:F2} cm, worst {worst * 100:F2} cm");
        Assert.True(worst <= 0.02, $"the ground differs by {worst:F3} m");

        // What you stand on, roads, lawns and drives included, is the same too.
        var all = new AcceptAll();
        var standing = new List<double>();
        for (int i = 0; i < 2000; i++)
        {
            float x = spawn.X + (float)(rng.NextDouble() * 600 - 300), z = spawn.Z + (float)(rng.NextDouble() * 600 - 300);
            float ym = mgeo.World.FloorAt(x, z, 200f, GeometryLayers.Ground, ref all, out _);
            float yf = fgeo.World.FloorAt(x + move.X, z + move.Z, 200f + move.Y, GeometryLayers.Ground, ref all, out _);
            if (ym <= -1000f || yf <= -1000f) continue;
            standing.Add(Math.Abs((ym + seaMap) - (yf + seaFrame)));
        }
        standing.Sort();
        _o.WriteLine($"what a body stands on, {standing.Count} points: median {standing[standing.Count / 2] * 100:F2} cm, worst {standing[^1] * 100:F2} cm");
        Assert.True(standing[(int)(standing.Count * 0.99)] <= 0.02);

        // The place's roads and junctions, whole, and its traffic.
        Assert.True(maps.TryGetMapData(frame.Id, out var fdata));
        Assert.Equal(map.Roads!.Count, fdata.Roads!.Count);
        Assert.Equal(map.Junctions!.Count, fdata.Junctions!.Count);
        Assert.Equal(map.Roads[0].Centreline[0] + move, fdata.Roads[0].Centreline[0]);
        Assert.True(maps.TryGetRoads(frame.Id, out var roads));
        Assert.Equal(map.Roads.Count, roads.Roads.Count);
        int cars = 0;
        fw.Query(new QueryDescription().WithAll<VehicleComponent>(), (Entity _) => cars++);
        _o.WriteLine($"{fdata.Roads.Count} roads, {fdata.Junctions.Count} junctions, {cars} vehicles of the place's traffic");
        Assert.True(cars >= map.Vehicles!.Count);

        // The tiles round the arrival are placed tiles in the store, of this map.
        var store = world.Service.Store;
        var arrivedIn = frame.WorldKeyOf(TileKey.Of(at, 250f));
        Assert.Equal((Id, place.Version), store.PlacedFrom(arrivedIn));
        _o.WriteLine($"store: {store.Count} tiles, {store.TotalBytes / 1024.0:F0} KB, of which placed {store.PlacedBytes / 1024.0:F0} KB");

        // What joining the world here costs a client at medium detail: ground at 2 m near, 7.8 m far.
        long joinBytes = 0;
        var joined = new List<OpenFPS.Common.Networking.IMessage>();
        server.Sent = (s, m) => { joined.Add(m); joinBytes += MemoryPack.MemoryPackSerializer.Serialize(m).Length; };
        server.SendMapData(alice, new OpenFPS.Common.Networking.MapDataRequest { MapName = frame.Id, FullDetailMetres = 300f, FarMetres = 800f });
        server.Sent = null;
        var grounds = joined.OfType<OpenFPS.Common.Networking.EntityDefinitionPack>().SelectMany(p => p.Unpack()!.Definitions).Where(d => d.Terrain != null).ToList();
        _o.WriteLine($"join of the world at Magnolia, medium: {joinBytes / 1024.0:F0} KB; {grounds.Count(g => g.Terrain!.Posts == 126)} tiles of ground at 2 m, "
                   + $"{grounds.Count(g => g.Terrain!.Posts == TerrainTileComponent.CoarseCells + 1)} at 7.8 m");
        Assert.Contains(grounds, g => g.Terrain!.Posts == TerrainTileComponent.CoarseCells + 1);

        // What the whole place costs: every tile of it, made and packed as the store keeps it.
        var all2 = System.Diagnostics.Stopwatch.StartNew();
        long bytes = 0;
        int tiles = 0, things = 0, biggest = 0;
        for (int x = place.Min.X; x <= place.Max.X; x++)
            for (int z = place.Min.Z; z <= place.Max.Z; z++)
            {
                var t = world.Copied.Make(new WorldTileKey(place.Zone, place.North, x, z))!;
                int b = t.ToBytes().Length;
                bytes += b; tiles++; things += t.Entities.Count; biggest = Math.Max(biggest, b);
            }
        _o.WriteLine($"the whole place: {tiles} tiles, {things} things, {bytes / 1048576.0:F1} MB packed ({bytes / 1024.0 / tiles:F0} KB a tile, "
                   + $"the biggest {biggest / 1024.0:F0} KB), made in {all2.ElapsedMilliseconds} ms");
        Assert.Equal(map.Entities.Count, things);
    }

    /// <summary>The cap never drops a placed tile; tiles of the survey alone go first.</summary>
    [Fact]
    public void The_cap_never_drops_a_placed_tile()
    {
        string root = Path.Combine(_dir, "store");
        var store = new WorldStore(root, capBytes: 1);
        var placed = new WorldTileKey(15, true, 1, 1);
        var plain = new WorldTileKey(15, true, 2, 1);
        var tile = WorldTileService.Make(placed, null, "test");
        tile.Place = "somewhere";
        tile.PlaceVersion = "1:abc";
        store.Write(placed, tile);
        store.Write(plain, WorldTileService.Make(plain, null, "test"));
        Assert.True(store.Has(placed));
        Assert.False(store.Has(plain));
        Assert.Equal(("somewhere", "1:abc"), store.PlacedFrom(placed));
        // The index remembers it across a restart.
        var again = new WorldStore(root, capBytes: 1);
        Assert.Equal(("somewhere", "1:abc"), again.PlacedFrom(placed));
    }
}

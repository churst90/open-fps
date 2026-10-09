using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server;
using OpenFPS.Server.Core;
using OpenFPS.Server.OneWorld;
using OpenFPS.Server.Repositories;
using OpenFPS.Server.Services;
using OpenFPS.Server.Systems;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// The one world on a server (step W2 of docs/WORLD_STREAMING.md "Stage 2 with terrain"): arriving at a
/// place, its tiles made and loaded round the player, the edge of what is not built, and the list of where
/// to go in two parts.
/// </summary>
public class WorldMapsTests : IDisposable
{
    private readonly ITestOutputHelper _o;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "openfps-world-maps-" + Guid.NewGuid().ToString("N"));

    public WorldMapsTests(ITestOutputHelper o) => _o = o;

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

    /// <summary>A survey of gentle hills, with tiles it cannot answer for until told it can.</summary>
    private sealed class Hills : IElevationSource
    {
        public string Name => "made-up hills";
        public readonly HashSet<WorldTileKey> Unreachable = new();
        public int Calls;

        public Task<float[]?> HeightsAsync(WorldTileKey key, int posts, double spacing, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            lock (Unreachable)
                if (Unreachable.Contains(key)) throw new HttpRequestException("no network");
            var h = new float[posts * posts];
            for (int j = 0; j < posts; j++)
                for (int i = 0; i < posts; i++)
                {
                    double e = key.Easting + i * spacing, n = key.Northing + j * spacing;
                    h[j * posts + i] = (float)(62 + 2 * Math.Sin(e / 90.0) + 1.5 * Math.Cos(n / 70.0));
                }
            return Task.FromResult<float[]?>(h);
        }
    }

    private (MapManager Maps, SessionManager Sessions, GameServer Server, Hills Survey) Rig(Action<GameServer>? beforeWorld = null)
    {
        string mapDir = Path.Combine(_dir, "maps");
        Directory.CreateDirectory(mapDir);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "maps", "speedway.json"), Path.Combine(mapDir, "speedway.json"));
        var maps = new MapManager(new MapRepository(mapDir), new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs")));
        maps.Initialize();
        var sessions = new SessionManager();
        var server = new GameServer(new NoUsers());
        server.Attach(maps, sessions);
        beforeWorld?.Invoke(server);
        var survey = new Hills();
        server.StartWorld(new WorldSettings { StorePath = Path.Combine(_dir, "world"), CapGigabytes = 1 }, survey);
        Assert.NotNull(server.World);
        server.World!.Service.RetryAfter = TimeSpan.FromMilliseconds(50);
        return (maps, sessions, server, survey);
    }

    private static void Until(GameServer server, Func<bool> done, int ms = 10_000)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!done())
        {
            Assert.True(clock.ElapsedMilliseconds < ms, "waited too long for the world");
            server.WorldTickForTest();
            Thread.Sleep(10);
        }
    }

    private static readonly WorldPlace Bobcat = new("bobcat", "31907 Bobcat Lane, Magnolia, Texas", 30.123703, -95.740935);

    /// <summary>
    /// A player arrives at a place: told the world is being built, put in a frame of it once the tile under
    /// them is made, stood on its ground, sent a manifest that says it is the world and where its frame is;
    /// the tiles round them load as they are made.
    /// </summary>
    [Fact]
    public void A_player_arrives_at_a_place_in_the_world()
    {
        var (maps, sessions, server, survey) = Rig();
        var alice = new UserSession { ConnectionId = 1, Username = "alice", CurrentMapId = "speedway", Welcomed = true };
        sessions.AddSession(1, alice);
        var said = new List<string>();
        server.World!.Arrive(alice, Bobcat, said.Add);
        Assert.Contains(said, s => s.StartsWith("Building the world at"));
        Until(server, () => WorldMaps.IsWorldMap(alice.CurrentMapId) && alice.Entity != Entity.Null);

        var key = WorldTileKey.OfLatLon(Bobcat.Lat, Bobcat.Lon);
        Assert.Equal("world@" + key, alice.CurrentMapId);
        Assert.True(server.World.TryGetFrame(alice.CurrentMapId, out var frame));
        Assert.Equal(key, frame.Origin);
        Assert.True(maps.TryGetMap(alice.CurrentMapId, out var world, out _, out var grid, out _));
        var at = world.Get<Transform>(alice.Entity).Position;
        // On the ground of the tile they arrived in, where the place is.
        var (_, _, e, n) = Utm.FromLatLon(Bobcat.Lat, Bobcat.Lon);
        Assert.Equal((float)(e - key.Easting), at.X, 2);
        Assert.Equal((float)(n - key.Northing), at.Z, 2);
        float ground = PhysicsUtils.GetGroundHeight(world, grid, at + new Vector3(0, 5, 0), out string material);
        Assert.InRange(at.Y - ground, 0f, 0.5f);
        Assert.Equal("Dirt", material);
        float overSea = 62 + 2 * MathF.Sin((float)(e / 90.0)) + 1.5f * MathF.Cos((float)(n / 70.0));
        Assert.Equal(overSea, ground + frame.BaseY, 1);

        // The manifest says where the frame is.
        var sent = new List<IMessage>();
        server.Sent = (s, m) => sent.Add(m);
        server.SendManifest(alice);
        var manifest = Assert.Single(sent.OfType<MapManifest>());
        Assert.True(manifest.IsWorld);
        Assert.Equal(15, manifest.WorldZone);
        Assert.True(manifest.WorldNorth);
        Assert.Equal(key.Easting, manifest.FrameEasting);
        Assert.Equal(key.Northing, manifest.FrameNorthing);
        Assert.Equal(frame.BaseY, manifest.FrameBaseY);
        Assert.Equal(250f, manifest.TileMetres);

        // The tiles round them arrive as they are made: every one within the far radius, in time.
        Assert.True(maps.TryGetTiles(alice.CurrentMapId, out var tiles));
        Until(server, () => frame.Loaded.Count >= 25);
        foreach (var k in frame.Loaded.Keys) Assert.True(tiles.IsReady(k));
        _o.WriteLine($"{frame.Loaded.Count} tiles loaded round the arrival, {survey.Calls} asked of the survey, store {server.World.Service.Store.TotalBytes / 1024} KB");

        // Where they are, said by place, not coordinates.
        Assert.Equal($"the world, at {Bobcat.Name}", server.World.Describe(frame, at));
        Assert.StartsWith("the world, 300 metres north east of", server.World.Describe(frame, at + new Vector3(212.1f, 0f, 212.1f)));
    }

    /// <summary>
    /// Walking into a tile that is not built yet is stopped a body's radius short of its edge, by the
    /// server's own movement; when the tile arrives, the same walk goes on into it.
    /// </summary>
    [Fact]
    public void A_tile_not_built_yet_stops_a_walker_at_its_edge_until_it_is()
    {
        var (maps, sessions, server, survey) = Rig();
        var key = WorldTileKey.OfLatLon(Bobcat.Lat, Bobcat.Lon);
        var blocked = key.Offset(2, 0);
        lock (survey.Unreachable) survey.Unreachable.Add(blocked);
        var alice = new UserSession { ConnectionId = 1, Username = "alice", CurrentMapId = "speedway", Welcomed = true };
        sessions.AddSession(1, alice);
        server.World!.Arrive(alice, Bobcat, _ => { });
        Until(server, () => WorldMaps.IsWorldMap(alice.CurrentMapId) && alice.Entity != Entity.Null);
        Assert.True(server.World.TryGetFrame(alice.CurrentMapId, out var frame));
        Until(server, () => frame.Loaded.ContainsKey(new TileKey(1, 0)));
        Assert.False(frame.Loaded.ContainsKey(new TileKey(2, 0)));
        Assert.True(maps.TryGetMap(alice.CurrentMapId, out var world, out _, out var grid, out var lookup));
        Assert.True(maps.TryGetTiles(alice.CurrentMapId, out var tiles));
        Assert.False(tiles.IsReady(new TileKey(2, 0)));

        void WalkEast(int ticks)
        {
            for (int t = 0; t < ticks; t++)
            {
                alice.InputQueue.Enqueue(new ClientInputUpdate { SequenceId = t, MoveDirection = new Vector3(1, 0, 0), DeltaTime = PhysicsConstants.FixedDeltaTime, Sprint = true });
                MovementSystem.Update(world, new Vector3(-6000, -600, -6000), new Vector3(6000, 1500, 6000), grid, lookup, sessions, maps, PhysicsConstants.FixedDeltaTime);
                maps.SyncGeometry(alice.CurrentMapId);
            }
        }

        // From where they arrived to the edge of tile 1 and on: about 400 m at a run is held at x = 500.
        WalkEast(60 * 30);
        var at = world.Get<Transform>(alice.Entity).Position;
        _o.WriteLine($"stopped at x = {at.X:F3}, the edge at 500");
        Assert.InRange(at.X, 500f - PhysicsConstants.PlayerRadius - 0.01f, 500f - PhysicsConstants.PlayerRadius + 0.001f);

        // The survey answers now: the tile is made and loaded, and the walk goes on into it.
        lock (survey.Unreachable) survey.Unreachable.Clear();
        Until(server, () => frame.Loaded.ContainsKey(new TileKey(2, 0)));
        WalkEast(30 * 5);
        at = world.Get<Transform>(alice.Entity).Position;
        Assert.True(at.X > 510f, $"still at {at.X}");
        float ground = PhysicsUtils.GetGroundHeight(world, grid, at + new Vector3(0, 2, 0), out _);
        Assert.InRange(at.Y - ground, -0.05f, 0.5f);
    }

    /// <summary>The fence on its own: a step toward a tile that is not there stops a body's radius short of
    /// the edge on that axis and slides along it on the other; a step into a tile that is there is a step.</summary>
    [Fact]
    public void The_fence_holds_one_axis_and_lets_the_other_slide()
    {
        var ready = new HashSet<TileKey> { new(0, 0), new(0, 1) };
        SharedMovementEngine.MovementContext Ctx(Vector3 at, Vector3 dir) => new()
        {
            Position = at, Velocity = Vector3.Zero, InputDirection = dir, DeltaTime = 0.5f, GroundHeight = 0f,
            Gravity = PhysicsConstants.Gravity, Speed = 4f, PlayerRadius = PhysicsConstants.PlayerRadius,
            PlayerHeight = PhysicsConstants.PlayerHeight, StepHeight = PhysicsConstants.StepHeight,
            MapMin = new Vector3(-1000, -100, -1000), MapMax = new Vector3(1000, 100, 1000),
            TileReady = ready.Contains, TileMetres = 250f,
        };
        // East toward tile (1, 0), not there: held at 250 less a radius, and the step north still taken.
        var diag = Vector3.Normalize(new Vector3(1, 0, 1));
        var (p, _, _) = SharedMovementEngine.Step(Ctx(new Vector3(249f, 0f, 100f), diag), ReadOnlySpan<SharedMovementEngine.Collider>.Empty, out var c);
        Assert.True(c.Fenced);
        Assert.Equal(250f - PhysicsConstants.PlayerRadius, p.X, 4);
        Assert.True(p.Z > 100.5f);
        // North into tile (0, 1), which is there: no fence.
        (p, _, _) = SharedMovementEngine.Step(Ctx(new Vector3(100f, 0f, 249.5f), Vector3.UnitZ), ReadOnlySpan<SharedMovementEngine.Collider>.Empty, out c);
        Assert.False(c.Fenced);
        Assert.True(p.Z > 250f);
        // Without a fence (every other map) nothing changes.
        var free = Ctx(new Vector3(249f, 0f, 100f), Vector3.UnitX);
        free.TileReady = null;
        (p, _, _) = SharedMovementEngine.Step(free, ReadOnlySpan<SharedMovementEngine.Collider>.Empty, out c);
        Assert.False(c.Fenced);
        Assert.True(p.X > 250f);
    }

    /// <summary>The list of where to go has the world's places first, then the maps; a frame of the world
    /// is not listed as a map of its own.</summary>
    [Fact]
    public void The_list_of_where_to_go_has_the_world_then_the_maps()
    {
        var (maps, sessions, server, _) = Rig();
        File.WriteAllText(Path.Combine(_dir, "places.json"), "[{\"Id\":\"bobcat\",\"Name\":\"31907 Bobcat Lane, Magnolia, Texas\",\"Lat\":30.123703,\"Lon\":-95.740935}]");
        var places = WorldMaps.LoadPlaces(maps, Path.Combine(_dir, "places.json"));
        Assert.Single(places);
        var alice = new UserSession { ConnectionId = 1, Username = "alice", CurrentMapId = "speedway", Welcomed = true };
        sessions.AddSession(1, alice);
        // A frame made by an arrival: the world, not a map.
        server.World!.Arrive(alice, Bobcat, _ => { });
        Until(server, () => WorldMaps.IsWorldMap(alice.CurrentMapId));

        var world = new WorldMaps(maps, server.World.Service, sessions.GetAllSessions, places);
        var dispatcher = new MessageDispatcher();
        _ = new DiscoveryService(dispatcher, sessions, maps, () => world);
        MapListResponse? listed = null;
        dispatcher.Dispatch(1, new MapListRequest { Scope = MapListScope.Server }, m => listed = (MapListResponse)m);
        Assert.NotNull(listed);
        Assert.True(listed!.Maps[0].IsWorldPlace);
        Assert.Equal("world bobcat", listed.Maps[0].Id);
        Assert.Equal("31907 Bobcat Lane, Magnolia, Texas", listed.Maps[0].Name);
        Assert.Contains(listed.Maps, m => !m.IsWorldPlace && m.Id == "speedway");
        Assert.DoesNotContain(listed.Maps, m => WorldMaps.IsWorldMap(m.Id));
        Assert.Equal(new WorldPlace("bobcat", "31907 Bobcat Lane, Magnolia, Texas", 30.123703, -95.740935), world.FindPlace("bobcat lane"));
        Assert.NotNull(world.FindPlace("30.1, -95.7"));
        Assert.Null(world.FindPlace("no such place"));
    }

    /// <summary>
    /// The real thing, over the network (OPENFPS_WORLD_NET=1 only): arriving at Bobcat Lane with nothing
    /// stored, the tiles made from 3DEP, what it took and what it cost on disk, and the ground under the
    /// arrival against Magnolia's own survey at the same place.
    /// </summary>
    [Fact]
    public void Arriving_at_bobcat_lane_from_the_real_survey()
    {
        if (Environment.GetEnvironmentVariable("OPENFPS_WORLD_NET") != "1") return;
        string mapDir = Path.Combine(_dir, "maps");
        Directory.CreateDirectory(mapDir);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "maps", "speedway.json"), Path.Combine(mapDir, "speedway.json"));
        var maps = new MapManager(new MapRepository(mapDir), new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs")));
        maps.Initialize();
        var sessions = new SessionManager();
        var server = new GameServer(new NoUsers());
        server.Attach(maps, sessions);
        server.StartWorld(new WorldSettings { StorePath = Path.Combine(_dir, "world"), CapGigabytes = 1 });
        var alice = new UserSession { ConnectionId = 1, Username = "alice", CurrentMapId = "speedway", Welcomed = true };
        sessions.AddSession(1, alice);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        server.World!.Arrive(alice, Bobcat, _ => { });
        Until(server, () => WorldMaps.IsWorldMap(alice.CurrentMapId) && alice.Entity != Entity.Null, 60_000);
        long arrived = clock.ElapsedMilliseconds;
        Assert.True(server.World.TryGetFrame(alice.CurrentMapId, out var frame));
        Until(server, () => frame.Loaded.Count >= 41, 180_000);
        long all = clock.ElapsedMilliseconds;
        Assert.True(maps.TryGetMap(alice.CurrentMapId, out var world, out _, out var grid, out _));
        var at = world.Get<Transform>(alice.Entity).Position;

        // The ground over a 30 m square round the arrival against Magnolia's own survey of it. They are not the
        // same product: asked for 2 m cells the service mosaics the 1 m lidar, asked for 5 m cells in degrees
        // (as the map's was) a coarser one, and here they part by 0.9 m (measured 2026-10-09).
        var magnolia = MapRepository.LoadFromFile(Path.Combine(AppContext.BaseDirectory, "places", "magnolia_tx.json"))!;
        double world2m = 0, map5m = 0;
        int n = 0;
        for (int i = -3; i <= 3; i++)
            for (int j = -3; j <= 3; j++, n++)
            {
                world2m += PhysicsUtils.GetGroundHeight(world, grid, at + new Vector3(i * 5f, 20f, j * 5f), out _) + frame.BaseY;
                map5m += magnolia.Elevation!.HeightAt(i * 5.0, j * 5.0) - magnolia.Elevation.SeaLevelY;
            }
        world2m /= n; map5m /= n;
        var store = server.World.Service.Store;
        _o.WriteLine($"arrived in {arrived} ms; {frame.Loaded.Count} tiles round it in {all} ms; {store.Count} tiles, {store.TotalBytes / 1024.0:F0} KB on disk " +
                     $"({store.TotalBytes / 1024.0 / store.Count:F1} KB a tile); ground round the arrival {world2m:F2} m over the sea, Magnolia's survey {map5m:F2} m");
        Assert.InRange(world2m - map5m, -1.5, 1.5);
    }

    /// <summary>An address from the Census geocoder is said as a person says it.</summary>
    [Theory]
    [InlineData("1042 BELMONT AVE SW, ALBANY, OR, 97321", "1042 Belmont Ave SW, Albany, OR")]
    [InlineData("31907 BOBCAT LN, MAGNOLIA, TX, 77355", "31907 Bobcat Ln, Magnolia, TX")]
    public void An_address_is_said_as_it_is_written(string matched, string said)
        => Assert.Equal(said, Geocoder.Spoken(matched));
}

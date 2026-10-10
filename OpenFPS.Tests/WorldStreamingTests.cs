using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using Arch.Core;
using MemoryPack;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.Core;
using OpenFPS.Client.Core.Platform;
using OpenFPS.Client.Core.Session;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// Stage 1 of docs/WORLD_STREAMING.md: a map with tiles is sent to each client a radius at a time, and
/// tiles are loaded and dropped as the player moves. Which tiles at what detail (TileSelection), the
/// wire messages, the server's per-client sets (TileStreamer), the client's load and unload and its
/// acoustic map afterwards (ClientWorldState), and two clients joining and moving through the real
/// server and client session code.
/// </summary>
public class WorldStreamingTests
{
    private readonly ITestOutputHelper _o;
    public WorldStreamingTests(ITestOutputHelper o) => _o = o;

    // ── Which tiles ──────────────────────────────────────────────────────────────────────────────

    private static readonly TileKey Min = new(-10, -10), Max = new(10, 10);

    [Fact]
    public void Tiles_within_the_full_radius_are_full_then_coarse_then_none()
    {
        var at = new Vector3(10f, 0f, 10f);   // inside tile (0, 0)
        var levels = TileSelection.Desired(at, 250f, StreamRadii.Medium, null, Min, Max);
        Assert.Equal(TileDetail.Full, levels[new TileKey(0, 0)]);
        Assert.Equal(TileDetail.Full, levels[new TileKey(-1, 0)]);       // 10 m away
        Assert.Equal(TileDetail.Full, levels[new TileKey(1, 0)]);        // 240 m away
        Assert.Equal(TileDetail.Coarse, levels[new TileKey(1, 1)]);      // 339 m away, at its corner
        Assert.Equal(TileDetail.Coarse, levels[new TileKey(2, 0)]);      // 490 m
        Assert.Equal(TileDetail.Coarse, levels[new TileKey(-3, 0)]);     // 760 m
        Assert.False(levels.ContainsKey(new TileKey(4, 0)));             // 990 m
        // Every tile is decided by its nearest point, not its centre.
        foreach (var (key, level) in levels)
        {
            float d = key.DistanceFrom(at, 250f);
            Assert.Equal(d <= 300f ? TileDetail.Full : TileDetail.Coarse, level);
            Assert.True(d <= 800f);
        }
    }

    [Fact]
    public void A_tile_is_kept_until_the_player_is_past_the_margin()
    {
        var r = StreamRadii.Medium;
        // Full at 300, kept full to 350, coarse after; coarse to 800, kept to 850, then gone.
        Assert.Equal(TileDetail.Full, TileSelection.Level(300f, r, TileDetail.None));
        Assert.Equal(TileDetail.Coarse, TileSelection.Level(320f, r, TileDetail.None));
        Assert.Equal(TileDetail.Full, TileSelection.Level(320f, r, TileDetail.Full));
        Assert.Equal(TileDetail.Full, TileSelection.Level(349f, r, TileDetail.Full));
        Assert.Equal(TileDetail.Coarse, TileSelection.Level(351f, r, TileDetail.Full));
        Assert.Equal(TileDetail.None, TileSelection.Level(820f, r, TileDetail.None));
        Assert.Equal(TileDetail.Coarse, TileSelection.Level(820f, r, TileDetail.Coarse));
        Assert.Equal(TileDetail.None, TileSelection.Level(851f, r, TileDetail.Coarse));

        // Walking back and forth across a tile's radius does not load and drop it each time.
        var levels = new Dictionary<TileKey, TileDetail>();
        int changes = 0;
        for (int step = 0; step < 40; step++)
        {
            var at = new Vector3(-300f - 20f * MathF.Sin(step * 0.7f), 0f, 125f);   // 280-320 m from tile (0, 0)
            var next = TileSelection.Desired(at, 250f, r, levels, Min, Max);
            if (step > 0 && next.GetValueOrDefault(new TileKey(0, 0)) != levels.GetValueOrDefault(new TileKey(0, 0))) changes++;
            levels = next;
        }
        Assert.Equal(0, changes);
    }

    [Fact]
    public void The_tile_you_stand_in_is_always_full_and_the_map_edge_bounds_the_set()
    {
        var levels = TileSelection.Desired(new Vector3(5f, 0f, 5f), 250f, new StreamRadii(100f, 100f), null,
                                           new TileKey(0, 0), new TileKey(1, 1));
        Assert.Equal(TileDetail.Full, levels[new TileKey(0, 0)]);
        Assert.All(levels.Keys, k => Assert.InRange(k.X, 0, 1));
        Assert.All(levels.Keys, k => Assert.InRange(k.Z, 0, 1));
    }

    [Fact]
    public void Requests_are_clamped_and_named()
    {
        Assert.Equal(StreamRadii.Default, StreamRadii.Clamp(0f, 0f));
        Assert.Equal(new StreamRadii(StreamRadii.MinFullMetres, 800f), StreamRadii.Clamp(5f, 800f));
        Assert.Equal(new StreamRadii(400f, 400f), StreamRadii.Clamp(400f, 50f));
        Assert.Equal(new StreamRadii(StreamRadii.MaxFullMetres, StreamRadii.MaxFarMetres), StreamRadii.Clamp(1e6f, 1e9f));
        Assert.Equal(StreamRadii.Default, StreamRadii.Clamp(float.NaN, float.PositiveInfinity));
        Assert.Equal(StreamRadii.Low, StreamRadii.Named("LOW"));
        Assert.Equal(StreamRadii.High, StreamRadii.Named(" high "));
        Assert.Null(StreamRadii.Named("ultra"));
        Assert.Equal(new TileKey(-1, 0), TileKey.Of(new Vector3(-0.01f, 3f, 0f), 250f));
    }

    // ── The wire ─────────────────────────────────────────────────────────────────────────────────

    private static T RoundTrip<T>(IMessage message) where T : IMessage
        => Assert.IsType<T>(MemoryPackSerializer.Deserialize<IMessage>(MemoryPackSerializer.Serialize(message)));

    [Fact]
    public void The_streaming_messages_survive_the_wire()
    {
        var update = RoundTrip<TileStreamUpdate>(new TileStreamUpdate
        {
            TileMetres = 250f, Definitions = 1234, Removed = 56,
            Tiles = { new TileState(new TileKey(-3, 4), TileDetail.Coarse), new TileState(new TileKey(0, 0), TileDetail.None) },
        });
        Assert.Equal(250f, update.TileMetres);
        Assert.Equal((1234, 56), (update.Definitions, update.Removed));
        Assert.Equal(new[] { (new TileKey(-3, 4), TileDetail.Coarse), (new TileKey(0, 0), TileDetail.None) },
                     update.Tiles.Select(t => (t.Key, t.Detail)));

        var manifest = RoundTrip<MapManifest>(new MapManifest { MapName = "magnolia_tx", TileMetres = 250f, PlayMax = new Vector3(1, 2, 3) });
        Assert.Equal((250f, new Vector3(1, 2, 3)), (manifest.TileMetres, manifest.PlayMax));
        Assert.Equal(0f, RoundTrip<MapManifest>(new MapManifest()).TileMetres);   // a map sent whole

        var request = RoundTrip<MapDataRequest>(new MapDataRequest { MapName = "m", FullDetailMetres = 150f, FarMetres = 500f });
        Assert.Equal(("m", 150f, 500f), (request.MapName, request.FullDetailMetres, request.FarMetres));
    }

    // ── A real place, loaded as the server loads it ──────────────────────────────────────────────

    private static MapManager LoadPlace(string id)
    {
        string dir = Path.Combine(Path.GetTempPath(), "openfps-stream-" + Guid.NewGuid().ToString("N"));
        string maps = Path.Combine(dir, "maps");
        Directory.CreateDirectory(Path.Combine(maps, "places"));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "places", id + ".json"), Path.Combine(maps, "places", id + ".json"));
        RealPlaceMapTests.CopyGround(id, Path.Combine(maps, "places"));
        AcousticRegistry.Initialize();
        var manager = new MapManager(new MapRepository(maps), new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs")));
        manager.Initialize();
        try { Directory.Delete(dir, true); } catch (IOException) { }
        return manager;
    }

    [Theory]
    [InlineData("magnolia_tx")]
    [InlineData("albany_or")]
    public void Every_entity_of_a_real_place_is_in_the_tiles_it_overlaps(string id)
    {
        var maps = LoadPlace(id);
        Assert.True(maps.TryGetTiles(id, out var tiles));
        Assert.True(maps.TryGetMap(id, out var world, out _, out _, out var lookup));
        Assert.True(maps.TryGetMapData(id, out var data));

        // Everything from the file is in a tile, and so is the ground the loader lays from the survey; what is
        // global is the loader's own handful.
        var terrain = lookup.Values.Where(e => world.Has<TerrainTileComponent>(e)).ToList();
        Assert.Equal(data.Entities.Count + terrain.Count, tiles.TiledCount);
        Assert.InRange(tiles.Global.Count, 1, 10);
        _o.WriteLine($"{id}: {tiles.Tiles.Count()} tiles, {tiles.TiledCount} tiled ({terrain.Count} of them ground), {tiles.Global.Count} global");

        if (data.Elevation != null)
        {
            // The ground is a tile of terrain in every tile, each in its own tile only, at coarse.
            var covered = new HashSet<TileKey>();
            foreach (var t in terrain)
            {
                Assert.True(tiles.TryGet(t.Id, out var g));
                Assert.Equal(TileDetail.Coarse, g.Needs);
                var only = Assert.Single(g.Tiles);
                Assert.Equal(TileKey.Of(world.Get<Transform>(t).Position, tiles.TileMetres), only);
                covered.Add(only);
            }
            Assert.True(covered.IsSupersetOf(tiles.Tiles), "a tile with no ground");
        }
        else
        {
            // The ground is one slab under the map: it is in every tile, at coarse.
            var ground = lookup.Values.First(e => world.Has<IdentityComponent>(e) && world.Get<IdentityComponent>(e).Name == "Ground"
                                                  && world.Has<ColliderComponent>(e) && world.Get<ColliderComponent>(e).Size.X > 1000f);
            Assert.True(tiles.TryGet(ground.Id, out var g));
            Assert.Equal(TileDetail.Coarse, g.Needs);
            Assert.True(g.Tiles.Length >= tiles.Tiles.Count() * 9 / 10, $"the ground is in {g.Tiles.Length} of {tiles.Tiles.Count()} tiles");
        }

        // Rooms are full detail; a doorway shares its rooms' tiles, so where one goes the other does.
        int doors = 0, rooms = 0, crowns = 0;
        foreach (var e in lookup.Values)
        {
            if (!tiles.TryGet(e.Id, out var m)) continue;
            if (world.Has<RegionComponent>(e) && world.Get<RegionComponent>(e).RoomSize.X > 0f) { rooms++; Assert.Equal(TileDetail.Full, m.Needs); }
            if (world.Has<PortalComponent>(e) && world.Get<PortalComponent>(e) is { } p && p.RegionAId != p.RegionBId)
            {
                doors++;
                foreach (int room in new[] { p.RegionAId, p.RegionBId })
                    if (tiles.TryGet(room, out var r)) Assert.Equal(r.Tiles, m.Tiles);
            }
            // A tree's crown is coarse: the client hears far trees together as woods (WoodChorus).
            if (world.Has<SoundEmitterComponent>(e) && world.Get<SoundEmitterComponent>(e).SoundId.StartsWith("foliage")) { crowns++; Assert.Equal(TileDetail.Coarse, m.Needs); }
            // An entity is in the tile its centre is in.
            var at = world.Get<Transform>(e).Position;
            Assert.Contains(TileKey.Of(at, tiles.TileMetres), m.Tiles);
        }
        Assert.True(doors > 100 && rooms > 100, $"{doors} doors, {rooms} rooms");
        _o.WriteLine($"  {crowns} tree crowns, coarse");
    }

    /// <summary>What the coarse ring is made of, by what sound would notice: the shells and front doors,
    /// the woods and trunks, fences, hedges and garden walls; never a room, an inner wall or furniture,
    /// a lawn, a drive or a post. Reports what it costs a tile.</summary>
    [Theory]
    [InlineData("magnolia_tx")]
    [InlineData("albany_or")]
    public void The_coarse_ring_keeps_what_sound_notices(string id)
    {
        var maps = LoadPlace(id);
        Assert.True(maps.TryGetTiles(id, out var tiles));
        Assert.True(maps.TryGetMap(id, out var world, out _, out _, out var lookup));
        var byKind = new Dictionary<string, (int Coarse, int Full)>();
        long coarseBytes = 0;
        foreach (var e in lookup.Values)
        {
            if (!tiles.TryGet(e.Id, out var m)) continue;
            string name = world.Has<IdentityComponent>(e) ? world.Get<IdentityComponent>(e).Name : "";
            string kind = name.Contains("Fence") ? "fence" : name.Contains("Hedge") ? "hedge" : name.EndsWith("wall") && !name.Contains(',') ? "outer wall"
                        : name.Contains(", ") && world.Has<RegionComponent>(e) ? "room" : name is "Tree" or "Woods" ? name.ToLowerInvariant()
                        : name == "Trees" ? "tree crown" : name.EndsWith(" post") ? "post" : name.EndsWith("front yard") ? "lawn" : "";
            if (kind.Length == 0) continue;
            var (c, f) = byKind.GetValueOrDefault(kind);
            byKind[kind] = m.Needs == TileDetail.Coarse ? (c + 1, f) : (c, f + 1);
            if (m.Needs == TileDetail.Coarse && kind is "fence" or "hedge" or "tree" or "woods")
                coarseBytes += MemoryPackSerializer.Serialize(EntityDefinitionFactory.From(world, e)).Length;
        }
        foreach (var (kind, (c, f)) in byKind.OrderBy(kv => kv.Key)) _o.WriteLine($"  {id} {kind}: {c} coarse, {f} full");
        foreach (var kind in new[] { "hedge", "tree", "woods" })
            if (byKind.TryGetValue(kind, out var n)) Assert.Equal(0, n.Full);
        // Fences and walls by the rule, not the name: a run at least 2 m long and 0.8 m tall is coarse.
        Assert.True(byKind.GetValueOrDefault("outer wall").Coarse > 1000);
        foreach (var kind in new[] { "room", "post", "lawn" })
            if (byKind.TryGetValue(kind, out var n)) Assert.Equal(0, n.Coarse);

        // The cost: the join at medium, and the average coarse tile.
        var interest = new TileInterest();
        var ids = TileStreamer.Begin(interest, tiles, maps.GetSpawnPoint(id).Position);
        long bytes = 0;
        foreach (var chunk in ids.Where(lookup.ContainsKey).Chunk(EntityDefinitionBatch.Size))
            bytes += MemoryPackSerializer.Serialize<IMessage>(EntityDefinitionPack.Pack(new EntityDefinitionBatch { Definitions = chunk.Select(i => TileStreamer.Definition(world, lookup[i], tiles, interest)).ToList() })).Length;
        int coarseTiles = interest.Levels.Count(kv => kv.Value == TileDetail.Coarse);
        int perCoarse = interest.Levels.Where(kv => kv.Value == TileDetail.Coarse)
                                       .Sum(kv => tiles.Members(kv.Key).Count(i => tiles.TryGet(i, out var mm) && mm.Needs == TileDetail.Coarse)) / Math.Max(1, coarseTiles);
        _o.WriteLine($"  {id} join at medium: {ids.Count} entities, {bytes / 1024} KB packed; {perCoarse} entities a coarse tile; " +
                     $"trees, woods, fences and hedges now coarse: {coarseBytes / 1024} KB unpacked over the whole map");
    }

    // ── The server's per-client sets ─────────────────────────────────────────────────────────────

    private sealed class NoUsers : IUserRepository
    {
        public UserData? GetUser(string username) => null;
        public bool AddUser(string username, string password, UserRole role) => false;
        public bool VerifyPassword(string username, string password) => false;
    }

    private static Entity Body(World world, Vector3 at, int id, string name)
        => world.Create(
            new PlayerComponent { ConnectionId = id, Username = name },
            EntityType.Player,
            new Transform { Position = at, Rotation = Quaternion.Identity },
            new Velocity(), new MaterialComponent { Material = "Generic" },
            new NameComponent { Name = name },
            new HealthComponent { Current = 100, Max = 100 },
            new ColliderComponent { Shape = ColliderShape.Cylinder, Size = new Vector3(0.6f, 1.8f, 0.6f), IsSolid = true });

    /// <summary>Every tiled entity the client knows is one its tiles want, and (once nothing is owed)
    /// every one its tiles want is known.</summary>
    private static void AssertConsistent(UserSession session, MapTiles tiles, HashSet<int>? clientIds = null)
    {
        foreach (int id in session.KnownEntities)
            if (tiles.IsTiled(id)) Assert.True(tiles.Wanted(id, session.Tiles.Levels), $"entity {id} known but not wanted");
        if (session.Tiles.PendingTiles != 0) return;
        foreach (var key in session.Tiles.Levels.Keys)
            foreach (int id in tiles.Members(key))
                if (tiles.Wanted(id, session.Tiles.Levels)) Assert.Contains(id, session.KnownEntities);
        if (clientIds != null)
            foreach (int id in session.KnownEntities) Assert.Contains(id, clientIds);
    }

    [Fact]
    public void The_server_sends_each_client_its_tiles_and_takes_back_the_ones_left_behind()
    {
        const string id = "magnolia_tx";
        var maps = LoadPlace(id);
        Assert.True(maps.TryGetMap(id, out var world, out _, out _, out var lookup));
        Assert.True(maps.TryGetTiles(id, out var tiles));
        var sessions = new SessionManager();
        var server = new GameServer(new NoUsers());
        server.Attach(maps, sessions);

        var spawn = maps.GetSpawnPoint(id).Position;
        var body = Body(world, spawn, 1, "alice");
        maps.IndexEntity(id, body);
        var alice = new UserSession { ConnectionId = 1, Username = "alice", Entity = body, CurrentMapId = id, Welcomed = true };
        sessions.AddSession(1, alice);

        // The join: the tiles round the spawn, a TileStreamUpdate, the map's roads, then MapLoadComplete.
        var sent = new List<IMessage>();
        long joinBytes = 0;
        server.Sent = (s, m) => { sent.Add(m); joinBytes += MemoryPackSerializer.Serialize(m).Length; };
        var clock = Stopwatch.StartNew();
        server.SendMapData(alice, new MapDataRequest { MapName = id });
        double joinMs = clock.Elapsed.TotalMilliseconds;
        Assert.IsType<MapLoadComplete>(sent[^1]);
        Assert.IsType<MapRoads>(sent[^2]);
        var joinTiles = Assert.Single(sent.OfType<TileStreamUpdate>());
        int joinDefs = Defs(sent).Count();
        Assert.Equal(joinDefs, joinTiles.Definitions);
        Assert.Equal(alice.Tiles.Levels.Count, joinTiles.Tiles.Count);
        Assert.True(joinDefs < tiles.TiledCount / 2, $"{joinDefs} of {tiles.TiledCount} sent on joining");
        Assert.Contains(TileKey.Of(spawn, 250f), alice.Tiles.Levels.Keys);
        AssertConsistent(alice, tiles);
        _o.WriteLine($"join: {joinDefs} of {tiles.TiledCount} definitions, {joinBytes / 1024.0:F0} KB, " +
                     $"{alice.Tiles.Levels.Count(kv => kv.Value == TileDetail.Full)} full + {alice.Tiles.Levels.Count(kv => kv.Value == TileDetail.Coarse)} coarse tiles, {joinMs:F0} ms");

        // Nothing farther than the far radius was sent.
        foreach (var def in Defs(sent))
                if (tiles.TryGet(def.EntityId, out var m))
                    Assert.Contains(m.Tiles, k => k.DistanceFrom(spawn, 250f) <= StreamRadii.Medium.FarMetres);

        // The ground of a full tile goes at 2 m, of a coarse one at the far ring's 7.8 m.
        int fineGround = 0, coarseGround = 0;
        foreach (var def in Defs(sent).Where(d => d.Terrain != null))
        {
            Assert.True(tiles.TryGet(def.EntityId, out var m));
            var level = alice.Tiles.Levels[Assert.Single(m.Tiles)];
            Assert.Equal(level == TileDetail.Full ? 126 : TerrainTileComponent.CoarseCells + 1, def.Terrain!.Posts);
            if (level == TileDetail.Full) fineGround++; else coarseGround++;
        }
        Assert.True(fineGround > 0 && coarseGround > 0, $"{fineGround} fine and {coarseGround} coarse tiles of ground");
        var sentCoarse = Defs(sent).Where(d => d.Terrain is { Posts: TerrainTileComponent.CoarseCells + 1 }).Select(d => d.EntityId).ToHashSet();

        // Walk 700 m east, a tick at a time at a run, and let the streamer keep up.
        var broadcast = new List<IMessage>();
        server.Sent = null;
        server.Broadcasted = (s, m) => broadcast.Add(m);
        long tick = 0;
        var tickTimes = new List<double>();
        for (int step = 0; step <= 140; step++)
        {
            world.Get<Transform>(body).Position = spawn + new Vector3(step * 5f, 0f, 0f);
            clock.Restart();
            server.BroadcastForTest(tick++);
            tickTimes.Add(clock.Elapsed.TotalMilliseconds);
        }
        for (int i = 0; i < 20 && alice.Tiles.PendingTiles > 0; i++) server.BroadcastForTest(tick++);
        Assert.Equal(0, alice.Tiles.PendingTiles);

        var updates = broadcast.OfType<TileStreamUpdate>().ToList();
        Assert.NotEmpty(updates);
        Assert.Contains(updates, u => u.Tiles.Any(t => t.Detail == TileDetail.None));
        var removed = broadcast.OfType<EntityRemoved>().SelectMany(r => r.EntityIds).ToHashSet();
        Assert.NotEmpty(removed);
        // A batch never carries more than a tick's worth.
        Assert.All(broadcast.OfType<EntityDefinitionPack>(), b => Assert.InRange(b.Count, 1, EntityDefinitionBatch.Size));
        AssertConsistent(alice, tiles);
        int streamedDefs = Defs(broadcast).Count();
        long streamedBytes = broadcast.Where(m => m is EntityDefinitionPack or EntityRemoved or TileStreamUpdate)
                                      .Sum(m => (long)MemoryPackSerializer.Serialize(m).Length);
        int tilesLoaded = updates.Sum(u => u.Tiles.Count(t => t.Detail != TileDetail.None));
        tickTimes.Sort();
        _o.WriteLine($"700 m east: {updates.Count} updates, {tilesLoaded} tile loads/upgrades, {streamedDefs} definitions, " +
                     $"{removed.Count} removed, {streamedBytes / 1024.0:F0} KB ({streamedBytes / 1024.0 / Math.Max(1, tilesLoaded):F0} KB a tile); " +
                     $"broadcast tick median {tickTimes[tickTimes.Count / 2]:F2} ms, worst {tickTimes[^1]:F1} ms");

        // Ground sent coarse whose tile came up to full detail went again whole, under the same entity; every
        // full tile the client has now has its ground at 2 m.
        var resent = Defs(broadcast).Where(d => d.Terrain is { Posts: 126 } && sentCoarse.Contains(d.EntityId)).ToList();
        Assert.NotEmpty(resent);
        foreach (var (key, level) in alice.Tiles.Levels)
            if (level == TileDetail.Full)
                foreach (int gid in tiles.Members(key))
                    Assert.DoesNotContain(gid, alice.Tiles.CoarseGround);
        _o.WriteLine($"ground: joined with {fineGround} tiles at 2 m and {coarseGround} at 7.8 m; {resent.Count} sent again whole as they came near");

        // The spawn tile is far behind now: none of its full-detail things are known any more.
        var spawnTile = TileKey.Of(spawn, 250f);
        Assert.False(alice.Tiles.Levels.TryGetValue(spawnTile, out var l) && l == TileDetail.Full);
        // And the server would not send a wall of an unloaded tile through the ordinary broadcast either.
        int before = broadcast.Count;
        server.BroadcastForTest(tick++);
        Assert.DoesNotContain(broadcast.Skip(before).OfType<EntityDefinition>(), d => tiles.IsTiled(d.EntityId));
    }

    /// <summary>
    /// The far ring's ground against the near ring's over all of Magnolia (196 tiles): how far the 7.8 m
    /// ground lies from the 2 m, that two coarse tiles meet without a crack, and how often swapping one for
    /// the other changes whether the ground stands between a sound 1 m over it and an ear 1.6 m over it in the
    /// same tile (what occlusion asks of the ground). The swap happens at the full radius, never under anybody.
    /// </summary>
    [Fact]
    public void Coarse_ground_lies_on_the_fine_and_seldom_changes_a_line_of_sight()
    {
        var maps = LoadPlace("magnolia_tx");
        Assert.True(maps.TryGetMap("magnolia_tx", out var world, out _, out _, out var lookup));
        var grounds = lookup.Values.Where(e => world.Has<TerrainTileComponent>(e))
                                   .Select(e => (At: world.Get<Transform>(e).Position, T: world.Get<TerrainTileComponent>(e))).ToList();
        Assert.Equal(196, grounds.Count);
        var rng = new Random(11);
        var diffs = new List<float>();
        int lines = 0, changed = 0, blockedFine = 0, changedUnskirted = 0;
        var byCorner = new Dictionary<(int, int), (TerrainTileComponent C, float Base)>();
        foreach (var (at, t) in grounds)
        {
            var c = t.Coarse();
            Assert.Equal(TerrainTileComponent.CoarseCells + 1, c.Posts);
            Assert.Equal(t.Size, c.Size, 3);
            var fine = t.Field(0f);
            var coarse = c.Field(0f);
            var unskirted = GeometryTerrainTests.UnskirtedCoarse(t, 0f);
            byCorner[((int)MathF.Round(at.X - t.Size / 2), (int)MathF.Round(at.Z - t.Size / 2))] = (c, at.Y);
            for (int k = 0; k < 400; k++)
            {
                float x = (float)rng.NextDouble() * t.Size, z = (float)rng.NextDouble() * t.Size;
                diffs.Add(MathF.Abs(fine.HeightAt(x, z) - coarse.HeightAt(x, z)));
            }
            for (int k = 0; k < 100; k++)
            {
                float x0 = (float)rng.NextDouble() * t.Size, z0 = (float)rng.NextDouble() * t.Size;
                float x1 = (float)rng.NextDouble() * t.Size, z1 = (float)rng.NextDouble() * t.Size;
                float y0 = fine.HeightAt(x0, z0) + 1.0f, y1 = fine.HeightAt(x1, z1) + 1.6f;
                bool Blocked(OpenFPS.Common.Geometry.Heightfield h)
                {
                    float len = MathF.Sqrt((x1 - x0) * (x1 - x0) + (z1 - z0) * (z1 - z0));
                    int n = Math.Max(2, (int)(len / 0.5f));
                    for (int s = 1; s < n; s++)
                    {
                        float f = s / (float)n;
                        if (y0 + (y1 - y0) * f < h.HeightAt(x0 + (x1 - x0) * f, z0 + (z1 - z0) * f)) return true;
                    }
                    return false;
                }
                bool a = Blocked(fine), b = Blocked(coarse);
                lines++;
                if (a) blockedFine++;
                if (a != b) changed++;
                if (a != Blocked(unskirted)) changedUnskirted++;
            }
        }
        // Two coarse tiles side by side share the posts of their edge.
        int edges = 0;
        foreach (var ((x, z), (c, cb)) in byCorner)
            if (byCorner.TryGetValue((x + 250, z), out var east))
            {
                edges++;
                for (int j = 0; j < c.Posts; j++)
                    Assert.InRange(MathF.Abs(cb + c.HeightsCm[j * c.Posts + c.Posts - 1] * 0.01f - (east.Base + east.C.HeightsCm[j * east.C.Posts] * 0.01f)), 0f, 0.0101f);
            }
        diffs.Sort();
        _o.WriteLine($"7.8 m ground against 2 m over 196 tiles: median {diffs[diffs.Count / 2] * 100:F1} cm, 99th {diffs[(int)(diffs.Count * 0.99)] * 100:F1} cm, " +
                     $"worst {diffs[^1] * 100:F0} cm; {edges} shared edges checked; of {lines} lines from 1 m to 1.6 m over the ground in a tile, " +
                     $"{blockedFine} blocked by the 2 m ground and {changed} ({100.0 * changed / lines:F2} %) changed by the swap " +
                     $"({changedUnskirted} with the edges as they were before the skirt)");
        Assert.True(diffs[diffs.Count / 2] < 0.05f);
        Assert.True(changed < lines / 50, $"{changed} of {lines} lines of sight changed");
    }

    /// <summary>
    /// Every seam of Magnolia's ground (364 between its 196 tiles), each with one tile at 7.8 m and its neighbour
    /// at 2 m, either way round: grazing rays and lines of sight that pass the seam under the ground all meet
    /// it (GeometryTerrainTests.AcrossSeam). The coarse ground as it was, unskirted, let some through.
    /// </summary>
    [Fact]
    public void Coarse_ground_meets_full_ground_without_a_crack()
    {
        var maps = LoadPlace("magnolia_tx");
        Assert.True(maps.TryGetMap("magnolia_tx", out var world, out _, out _, out var lookup));
        var surface = EntityGeometry.SurfaceOf("Dirt", Vector3.One, 0, 0, false, 0, 0, false, false, false, "Ground");
        var tiles = new Dictionary<(int, int), (Vector3 At, TerrainTileComponent T)>();
        foreach (var e in lookup.Values.Where(e => world.Has<TerrainTileComponent>(e)))
        {
            var at = world.Get<Transform>(e).Position;
            var t = world.Get<TerrainTileComponent>(e);
            tiles[((int)MathF.Round(at.X - t.Size / 2), (int)MathF.Round(at.Z - t.Size / 2))] = (at, t);
        }
        Assert.Equal(196, tiles.Count);
        var builder = new OpenFPS.Common.Geometry.TriangleWorldBuilder(250f);
        OpenFPS.Common.Geometry.SolidSpec Fine((Vector3 At, TerrainTileComponent T) x, int id) => EntityGeometry.TerrainSpec(id, x.At, x.T, surface);
        OpenFPS.Common.Geometry.SolidSpec Coarse((Vector3 At, TerrainTileComponent T) x, int id) => EntityGeometry.TerrainSpec(id, x.At, x.T.Coarse(), surface);
        OpenFPS.Common.Geometry.SolidSpec Old((Vector3 At, TerrainTileComponent T) x, int id)
            => OpenFPS.Common.Geometry.SolidSpec.OfTerrain(id, x.At, GeometryTerrainTests.UnskirtedCoarse(x.T, x.At.Y), surface);
        int seams = 0, rays = 0, leaks = 0, sights = 0, sightLeaks = 0, oldLeaks = 0, oldSightLeaks = 0, fullLeaks = 0;
        foreach (var ((x, z), a) in tiles)
            foreach (bool alongX in new[] { false, true })
            {
                if (!tiles.TryGetValue(alongX ? (x, z + 250) : (x + 250, z), out var b)) continue;
                seams++;
                float seam = alongX ? z + 250f : x + 250f, v0 = (alongX ? x : z) + 1f, v1 = v0 + 248f;
                var fa = Fine(a, 1); var fb = Fine(b, 2);
                var truthA = GeometryTerrainTests.GroundOf(fa.Terrain!, fa.TerrainCorner);
                var truthB = GeometryTerrainTests.GroundOf(fb.Terrain!, fb.TerrainCorner);
                Func<float, float, float> truth = (wx, wz) => (alongX ? wz : wx) < seam ? truthA(wx, wz) : truthB(wx, wz);
                GeometryTerrainTests.SeamRays Probe(OpenFPS.Common.Geometry.SolidSpec lo, OpenFPS.Common.Geometry.SolidSpec hi)
                    => GeometryTerrainTests.AcrossSeam(builder.Build(new[] { lo, hi }, Array.Empty<OpenFPS.Common.Geometry.SolidSpec>()),
                                                       GeometryTerrainTests.GroundOf(lo.Terrain!, lo.TerrainCorner),
                                                       GeometryTerrainTests.GroundOf(hi.Terrain!, hi.TerrainCorner),
                                                       truth, alongX, seam, v0, v1, seams, 200, 100);
                var full = Probe(fa, fb);
                fullLeaks += full.Leaks + full.LineLeaks;
                foreach (var r in new[] { Probe(Coarse(a, 1), fb), Probe(fa, Coarse(b, 2)) })
                {
                    rays += r.Rays; leaks += r.Leaks; sights += r.Lines; sightLeaks += r.LineLeaks;
                }
                foreach (var r in new[] { Probe(Old(a, 1), fb), Probe(fa, Old(b, 2)) })
                {
                    oldLeaks += r.Leaks; oldSightLeaks += r.LineLeaks;
                }
            }
        _o.WriteLine($"{seams} seams, 7.8 m beside 2 m either way round: {leaks} of {rays} grazing rays and {sightLeaks} of {sights} " +
                     $"lines of sight under the seam got through (unskirted: {oldLeaks} and {oldSightLeaks}); 2 m beside 2 m: {fullLeaks}");
        Assert.Equal(364, seams);
        Assert.Equal(0, fullLeaks);
        Assert.Equal(0, leaks + sightLeaks);
        Assert.True(oldLeaks > 0, "the unskirted coarse ground should let some rays through, or the probe cannot see a crack");
    }

    [Fact]
    public void A_map_without_tiles_is_sent_whole_as_before()
    {
        var maps = new MapManager(new MapRepository(Path.Combine(AppContext.BaseDirectory, "maps")),
                                  new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs")));
        maps.Initialize();
        Assert.False(maps.TryGetTiles("city", out _));
        Assert.True(maps.TryGetMap("city", out var world, out _, out _, out _));
        var sessions = new SessionManager();
        var server = new GameServer(new NoUsers());
        server.Attach(maps, sessions);
        var body = Body(world, maps.GetSpawnPoint("city").Position, 1, "bob");
        maps.IndexEntity("city", body);
        var bob = new UserSession { ConnectionId = 1, Username = "bob", Entity = body, CurrentMapId = "city", Welcomed = true };
        sessions.AddSession(1, bob);

        var sent = new List<IMessage>();
        server.Sent = (s, m) => sent.Add(m);
        server.SendManifest(bob);
        var manifest = Assert.IsType<MapManifest>(sent.Single());
        Assert.Equal(0f, manifest.TileMetres);
        sent.Clear();
        server.SendMapData(bob, new MapDataRequest { MapName = "city", FullDetailMetres = 150f, FarMetres = 500f });
        Assert.DoesNotContain(sent, m => m is TileStreamUpdate);
        Assert.Equal(EntityDefinitionFactory.StaticEntities(world).Count, Defs(sent).Count());
        Assert.Equal(manifest.ExpectedEntityCount, Defs(sent).Count());
        long packed = sent.OfType<EntityDefinitionPack>().Sum(p => (long)MemoryPackSerializer.Serialize<IMessage>(p).Length);
        long raw = sent.OfType<EntityDefinitionPack>().Sum(p => (long)MemoryPackSerializer.Serialize<IMessage>(p.Unpack()!).Length);
        _o.WriteLine($"city join: {Defs(sent).Count()} definitions, {packed / 1024} KB packed, {raw / 1024} KB as plain batches");
    }

    /// <summary>Every definition in what was sent, unpacked.</summary>
    private static IEnumerable<EntityDefinition> Defs(IEnumerable<IMessage> messages)
        => messages.OfType<EntityDefinitionPack>().SelectMany(p => p.Unpack()!.Definitions);

    [Fact]
    public void A_pack_is_the_batch_it_carries_and_much_smaller()
    {
        var batch = new EntityDefinitionBatch();
        for (int i = 0; i < 256; i++)
        {
            var d = new EntityDefinition { EntityId = 1000 + i };
            d.Identity.Name = $"12{i % 40} Bobcat Lane east wall";
            d.Identity.Description = "A wall of brick on a timber frame, plastered inside: what most of the houses here are made of.";
            d.Material.Material = "Brick";
            d.Transform = new Transform { Position = new Vector3(i, 0, -i), Rotation = Quaternion.Identity };
            batch.Definitions.Add(d);
        }
        var pack = RoundTrip<EntityDefinitionPack>(EntityDefinitionPack.Pack(batch));
        var back = pack.Unpack()!;
        Assert.Equal(256, pack.Count);
        Assert.Equal(batch.Definitions.Select(d => (d.EntityId, d.Identity.Name, d.Transform.Position)),
                     back.Definitions.Select(d => (d.EntityId, d.Identity.Name, d.Transform.Position)));
        int raw = MemoryPackSerializer.Serialize<IMessage>(batch).Length;
        Assert.True(pack.Brotli.Length * 8 < raw, $"{pack.Brotli.Length} packed of {raw}");
        // Not a batch: refused, not thrown.
        Assert.Null(new EntityDefinitionPack { Brotli = EntityDefinitionPack.Pack(new EntityDefinitionBatch()).Brotli[..1] }.Unpack());
    }

    // ── The client: tiles in, tiles out, and its acoustic map after each ─────────────────────────

    [Fact]
    public void The_clients_acoustic_map_follows_its_tiles_without_being_replaced()
    {
        const string id = "magnolia_tx";
        var maps = LoadPlace(id);
        Assert.True(maps.TryGetMap(id, out var world, out _, out _, out var lookup));
        Assert.True(maps.TryGetTiles(id, out var tiles));
        Assert.True(maps.TryGetMapData(id, out var data));
        var spawn = maps.GetSpawnPoint(id).Position;

        var interest = new TileInterest();
        var first = TileStreamer.Begin(interest, tiles, spawn);
        var client = new ClientWorldState { RefreshRunner = work => work() };
        client.Clear(data.Size);
        client.ConfigureAcoustics(data.MinBound, data.VoxelResolution, data.OcclusionFloor, tiles.TileMetres);
        var defs = first.Select(i => EntityDefinitionFactory.From(world, lookup.TryGetValue(i, out var e) ? e : Global(tiles, i))).ToList();
        foreach (var d in defs) client.RegisterDefinition(d);
        var map = ClientWorldState.BuildAcousticMap(defs, data.Size, data.MinBound, data.VoxelResolution, data.OcclusionFloor, streamed: true, report: false);
        client.SetAcousticMap(map);
        int regionsAtSpawn = map.Regions.Count;
        Assert.True(regionsAtSpawn > 10, $"{regionsAtSpawn} regions at the spawn");
        AssertNoDanglingPortals(map);

        // The player goes 1.5 km north-west. What the server would now send, and take away.
        var there = spawn + new Vector3(-1100f, 0f, 1000f);
        var after = TileSelection.Desired(there, tiles.TileMetres, interest.Radii, interest.Levels, tiles.Min, tiles.Max);
        var wanted = new HashSet<int>(tiles.Global);
        foreach (var key in after.Keys) foreach (int m in tiles.Members(key)) if (tiles.Wanted(m, after)) wanted.Add(m);
        var gone = first.Where(i => !wanted.Contains(i)).ToList();
        var arriving = wanted.Where(i => !first.Contains(i)).Select(i => EntityDefinitionFactory.From(world, lookup[i])).ToList();
        Assert.NotEmpty(gone);
        Assert.NotEmpty(arriving);

        long version = client.GeometryVersion;
        var clock = Stopwatch.StartNew();
        foreach (var chunk in arriving.Chunk(EntityDefinitionBatch.Size))
            foreach (var d in chunk) client.RegisterDefinition(d, deferAcoustics: true);
        client.RemoveEntities(gone);
        var snapshot = client.GetSnapshot();    // the grid is made again here, on the game thread
        double gameThreadMs = clock.Elapsed.TotalMilliseconds;
        client.NoteTiles(after.Select(kv => new TileState(kv.Key, kv.Value)));
        clock.Restart();
        client.RequestAcousticRefresh();
        double refreshMs = clock.Elapsed.TotalMilliseconds;
        _o.WriteLine($"client: {arriving.Count} in, {gone.Count} out, game thread {gameThreadMs:F0} ms (grid rebuilt {client.GridRebuilds} times), " +
                     $"acoustic refresh {refreshMs:F0} ms; {client.EntityCount} held");

        // The same map object, with new tables in it, and the version moved on.
        var now = client.GetSnapshot();
        Assert.Same(map, now.AcousticMap);
        Assert.True(now.GeometryVersion > version);
        Assert.Equal(1, client.AcousticRefreshes);

        // The rooms held now, and only those.
        var heldRooms = now.Entities.Values.Where(e => e.Definition.Region.RoomSize.X > 0f).Select(e => e.Id).ToHashSet();
        var mapRooms = map.Regions.Keys.Where(k => k != AcousticConstants.GlobalRegionId).ToHashSet();
        Assert.Equal(heldRooms.OrderBy(i => i), mapRooms.OrderBy(i => i));
        foreach (int g in gone) Assert.False(map.Regions.ContainsKey(g));
        AssertNoDanglingPortals(map);
        // A room by the new spot is a region where it stands, in the voxel grid too.
        var room = now.Entities.Values.Where(e => e.Definition.Region.RoomSize.X > 0f && e.Definition.Region.IsIndoor)
                                      .OrderBy(e => Vector3.Distance(e.Transform.Position, there)).First();
        Assert.Equal(room.Id, map.VoxelGrid.GetRegionAt(room.Transform.Position));
        // ...and one back at the spawn is gone from both.
        Assert.Equal(AcousticConstants.GlobalRegionId, map.VoxelGrid.GetRegionAt(defs.First(d => d.Region.IsIndoor && d.Region.RoomSize.X > 0f
                                                                                                && gone.Contains(d.EntityId)).Transform.Position));

        // The static collision grid has the new walls and not the old.
        var wall = arriving.First(d => d.Collider.IsSolid && !d.Moves && d.Collider.Size.Y > 2f);
        Assert.Contains(wall.EntityId, now.StaticGrid!.GetItemsInRadius(wall.Transform.Position, 1f));
        var oldWall = defs.First(d => d.Collider.IsSolid && d.Collider.Size.Y > 2f && gone.Contains(d.EntityId));
        Assert.DoesNotContain(oldWall.EntityId, now.StaticGrid.GetItemsInRadius(oldWall.Transform.Position, 1f));
    }

    private static Entity Global(MapTiles tiles, int id)
    {
        Assert.True(tiles.TryGetGlobal(id, out var e));
        return e;
    }

    private static void AssertNoDanglingPortals(AcousticMap map)
    {
        foreach (var (pid, (portal, _)) in map.Portals)
        {
            Assert.True(portal.RegionAId == AcousticConstants.GlobalRegionId || map.Regions.ContainsKey(portal.RegionAId), $"portal {pid} into missing {portal.RegionAId}");
            Assert.True(portal.RegionBId == AcousticConstants.GlobalRegionId || map.Regions.ContainsKey(portal.RegionBId), $"portal {pid} into missing {portal.RegionBId}");
        }
    }

    [Fact]
    public void A_door_that_swings_during_a_refresh_is_where_it_is_afterwards()
    {
        var client = new ClientWorldState();
        client.Clear(new Vector3(100, 20, 100));
        client.ConfigureAcoustics(new Vector3(-50, 0, -50), 0.5f, 0.2f, 50f);
        var room = new EntityDefinition { EntityId = 10, Transform = new Transform { Position = new Vector3(0, 1.5f, 0), Rotation = Quaternion.Identity } };
        room.Region.RoomSize = new Vector3(4, 3, 4);
        room.Region.IsIndoor = true;
        var door = new EntityDefinition { EntityId = 11, Transform = new Transform { Position = new Vector3(0, 1, 2), Rotation = Quaternion.Identity } };
        door.Portal = new PortalComponent { RegionAId = 10, RegionBId = AcousticConstants.GlobalRegionId, ApertureSize = 0f };
        client.RegisterDefinition(room);
        client.RegisterDefinition(door);
        client.SetAcousticMap(ClientWorldState.BuildAcousticMap(new[] { room, door }, new Vector3(100, 20, 100), new Vector3(-50, 0, -50), 0.5f, 0.2f, true, false));
        Assert.False(client.AcousticMap!.Portals.ContainsKey(11));

        // A tile arrives with a room in it, and its refresh runs where the test says: the door opens in
        // the middle of it.
        var next = new EntityDefinition { EntityId = 12, Transform = new Transform { Position = new Vector3(20, 1.5f, 0), Rotation = Quaternion.Identity } };
        next.Region.RoomSize = new Vector3(4, 3, 4);
        client.RegisterDefinition(next, deferAcoustics: true);
        Action? held = null;
        client.RefreshRunner = work => held = work;
        client.RequestAcousticRefresh();
        var open = new EntityDefinition { EntityId = 11, Transform = door.Transform, Portal = door.Portal };
        open.Portal = new PortalComponent { RegionAId = 10, RegionBId = AcousticConstants.GlobalRegionId, ApertureSize = 1.2f };
        client.RegisterDefinition(open);
        Assert.True(client.AcousticMap!.Portals.ContainsKey(11));
        held!();
        Assert.True(client.AcousticMap!.Portals.ContainsKey(11), "the open door was put back as it was before the refresh");
        Assert.True(client.AcousticMap.Regions.ContainsKey(12));
        Assert.False(client.AcousticRefreshPending);
    }

    /// <summary>A coarse tile has a house's shell and front door but not its rooms: the door is a shut
    /// leaf until the room arrives, and then a doorway.</summary>
    [Fact]
    public void A_front_door_without_its_room_is_a_shut_leaf_until_the_room_comes()
    {
        var client = new ClientWorldState { RefreshRunner = work => work() };
        client.Clear(new Vector3(100, 20, 100));
        client.ConfigureAcoustics(new Vector3(-50, 0, -50), 0.5f, 0.2f, 50f);
        client.SetAcousticMap(ClientWorldState.BuildAcousticMap(Array.Empty<EntityDefinition>(), new Vector3(100, 20, 100), new Vector3(-50, 0, -50), 0.5f, 0.2f, true, false));
        var door = new EntityDefinition { EntityId = 21, Transform = new Transform { Position = new Vector3(0, 1, 2), Rotation = Quaternion.Identity } };
        door.Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(1, 2.1f, 0.05f), IsSolid = true };
        door.Portal = new PortalComponent { RegionAId = 20, RegionBId = AcousticConstants.GlobalRegionId, ApertureSize = 1.2f };
        client.RegisterDefinition(door);                        // swung open, re-sent alone
        client.RegisterDefinition(door, deferAcoustics: true);  // or with its coarse tile
        client.RequestAcousticRefresh();
        Assert.False(client.AcousticMap!.Portals.ContainsKey(21));

        var room = new EntityDefinition { EntityId = 20, Transform = new Transform { Position = new Vector3(0, 1.5f, 0), Rotation = Quaternion.Identity } };
        room.Region.RoomSize = new Vector3(4, 3, 4);
        room.Region.IsIndoor = true;
        client.RegisterDefinition(room, deferAcoustics: true);  // the tile goes full
        client.RequestAcousticRefresh();
        Assert.True(client.AcousticMap!.Portals.ContainsKey(21));
    }

    [Fact]
    public void Walls_alone_move_the_scene_on_without_rebuilding_the_rooms()
    {
        var client = new ClientWorldState();
        client.Clear(new Vector3(100, 20, 100));
        client.ConfigureAcoustics(new Vector3(-50, 0, -50), 0.5f, 0.2f, 50f);
        client.SetAcousticMap(ClientWorldState.BuildAcousticMap(Array.Empty<EntityDefinition>(), new Vector3(100, 20, 100), new Vector3(-50, 0, -50), 0.5f, 0.2f, true, false));
        int ran = 0;
        client.RefreshRunner = work => { ran++; work(); };
        var wall = new EntityDefinition { EntityId = 5, Transform = new Transform { Position = new Vector3(30, 1, 30), Rotation = Quaternion.Identity } };
        wall.Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(10, 3, 0.2f), IsSolid = true };
        long v = client.GeometryVersion;
        client.RegisterDefinition(wall, deferAcoustics: true);
        client.RequestAcousticRefresh();
        Assert.Equal((0, 1, v + 1), (ran, client.GeometryOnlyChanges, client.GeometryVersion));
        Assert.Equal(client.GeometryVersion, client.GetSnapshot().GeometryVersion);

        // A room is a rebuild.
        var room = new EntityDefinition { EntityId = 6, Transform = new Transform { Position = new Vector3(30, 1.5f, 35), Rotation = Quaternion.Identity } };
        room.Region.RoomSize = new Vector3(4, 3, 4);
        client.RegisterDefinition(room, deferAcoustics: true);
        client.RequestAcousticRefresh();
        Assert.Equal(1, ran);
        Assert.True(client.AcousticMap!.Regions.ContainsKey(6));
    }

    // ── Two clients through the real session code ────────────────────────────────────────────────

    private sealed class Speech : ISpeechOutput
    {
        public readonly ConcurrentQueue<string> Queue = new();
        public string BackendName => "fake";
        public bool Initialize() => true;
        public void Speak(string text, bool interrupt = true) => Queue.Enqueue(text);
        public void Interrupt() { }
        public void Dispose() { }
    }

    private sealed class Shell : IClientShell
    {
        public int Entered;
        public bool IsGameInputActive { get; set; } = true;
        public event Action<string>? CommandEntered { add { } remove { } }
        public void ShowLoading(string status, bool speak = true) { }
        public void UpdateLoadingStatus(string text, int percent) { }
        public void EnterGame() => Entered++;
        public void OpenCommandConsole() { }
        public void ShowGameMenu(Action<GameMenuChoice> chosen) { }
        public void ReturnToMenu() { }
        public void Quit() { }
    }

    /// <summary>One player: a game client with no sound card, the server's session for it, and the
    /// messages each way. Everything goes through MemoryPack, as over the wire, and is counted.</summary>
    private sealed class Player
    {
        public required ClientGameSession Client;
        public required UserSession Session;
        public readonly ConcurrentQueue<IMessage> ToServer = new();
        public readonly ConcurrentQueue<IMessage> ToClient = new();
        /// <summary>Acoustic refreshes the client asked for: run after its messages, as its own thread
        /// would, and timed apart from them.</summary>
        public readonly ConcurrentQueue<Action> Refreshes = new();
        public long BytesIn;
    }

    [Trait("Category", "Timing")] // depends on this machine's speed; not run on CI
    [Fact]
    public void Two_players_join_a_streamed_map_and_one_walks_away_from_the_other()
    {
        const string id = "magnolia_tx";
        var maps = LoadPlace(id);
        Assert.True(maps.TryGetMap(id, out var world, out _, out _, out var lookup));
        Assert.True(maps.TryGetTiles(id, out var tiles));
        var sessions = new SessionManager();
        var server = new GameServer(new NoUsers());
        server.Attach(maps, sessions);

        var players = new Dictionary<int, Player>();
        var gameThreadMs = new List<double>();
        var refreshMs = new List<double>();
        void Deliver(UserSession s, IMessage m)
        {
            var bytes = MemoryPackSerializer.Serialize(m);
            var p = players[s.ConnectionId];
            p.BytesIn += bytes.Length;
            p.ToClient.Enqueue(MemoryPackSerializer.Deserialize<IMessage>(bytes)!);
        }
        server.Sent = Deliver;
        server.Broadcasted = Deliver;

        Player Join(int conn, string name)
        {
            var network = new ClientNetworkService();
            var client = new ClientGameSession(network, new Speech(), new Shell(), new AudioEngineFacade(),
                                               microphone: new NullMicrophoneCapture("no microphone here"), enableAudio: false);
            var session = new UserSession { ConnectionId = conn, Username = name, CurrentMapId = id, Welcomed = true };
            sessions.AddSession(conn, session);
            var p = new Player { Client = client, Session = session };
            client.World.RefreshRunner = work => p.Refreshes.Enqueue(work);
            network.Sending = m => p.ToServer.Enqueue(m);
            players[conn] = p;
            return p;
        }

        // Pumps both ways until nothing moves: the server's handlers on this thread, as its tick runs them.
        void Pump(int rounds = 400)
        {
            for (int i = 0; i < rounds; i++)
            {
                bool any = false;
                foreach (var p in players.Values)
                {
                    while (p.ToServer.TryDequeue(out var m))
                    {
                        any = true;
                        if (m is MapDataRequest req) server.SendMapData(p.Session, req);
                        else if (m is TextCommand { Command: "ready" }) server.HandlePlayerReady(p.Session.ConnectionId);
                    }
                    server.DrainCommandBuffer();
                    // The game thread's share of the tiles: unpacking and filing them, the removals, and the
                    // snapshot the next frame builds (the collision grid is made again there). Timed apart
                    // from the rest, whose cost does not depend on streaming (the test teleports the body
                    // every tick, so every state update is a prediction correction).
                    double tileWork = 0;
                    bool tileTouched = false;
                    while (p.ToClient.TryDequeue(out var m))
                    {
                        any = true;
                        bool tileMessage = m is EntityDefinitionPack or EntityRemoved or TileStreamUpdate;
                        var one = Stopwatch.StartNew();
                        if (!tileMessage && tileTouched) { p.Client.World.GetSnapshot(); tileWork += one.Elapsed.TotalMilliseconds; tileTouched = false; one.Restart(); }
                        p.Client.HandleMessage(m);
                        if (tileMessage) { tileWork += one.Elapsed.TotalMilliseconds; tileTouched = true; }
                    }
                    if (tileTouched) { var snap = Stopwatch.StartNew(); p.Client.World.GetSnapshot(); tileWork += snap.Elapsed.TotalMilliseconds; }
                    if (tileWork > 0) gameThreadMs.Add(tileWork);
                    // The refresh thread.
                    while (p.Refreshes.TryDequeue(out var work))
                    {
                        var refresh = Stopwatch.StartNew();
                        work();
                        refreshMs.Add(refresh.Elapsed.TotalMilliseconds);
                    }
                }
                if (!any && players.Values.All(p => p.Client.IsInGame)) return;
                Thread.Sleep(5);
            }
        }

        var alice = Join(1, "alice");
        var bob = Join(2, "bob");
        var clock = Stopwatch.StartNew();
        server.SendManifest(alice.Session);
        server.SendManifest(bob.Session);
        Pump();
        _o.WriteLine($"both in the world after {clock.ElapsedMilliseconds} ms; alice joined with {alice.BytesIn / 1024.0:F0} KB, " +
                     $"{alice.Client.World.EntityCount} entities, {alice.Client.World.Tiles.Count} tiles");
        Assert.True(alice.Client.IsInGame && bob.Client.IsInGame);
        Assert.NotNull(alice.Client.World.AcousticMap);
        Assert.Equal(alice.Session.Tiles.Levels.Count, alice.Client.World.Tiles.Count);
        var spawn = world.Get<Transform>(alice.Session.Entity).Position;

        // A tick or two: each hears of the other.
        long tick = 0;
        for (int i = 0; i < 3; i++) { server.BroadcastForTest(tick++); Pump(5); }
        Assert.True(alice.Client.World.GetSnapshot().Entities.ContainsKey(bob.Session.Entity.Id));
        Assert.True(bob.Client.World.GetSnapshot().Entities.ContainsKey(alice.Session.Entity.Id));
        var bobTiles = bob.Client.World.Tiles.ToDictionary(kv => kv.Key, kv => kv.Value);
        long aliceBytesAtSpawn = alice.BytesIn;

        // Alice walks 1.2 km east; bob stays where he is.
        gameThreadMs.Clear();
        refreshMs.Clear();
        for (int step = 0; step <= 240; step++)
        {
            world.Get<Transform>(alice.Session.Entity).Position = spawn + new Vector3(step * 5f, 0f, 0f);
            world.Get<Transform>(alice.Session.Entity).IsDirty = true;
            server.BroadcastForTest(tick++);
            Pump(1);
        }
        for (int i = 0; i < 20; i++) { server.BroadcastForTest(tick++); Pump(2); }
        Assert.Equal(0, alice.Session.Tiles.PendingTiles);

        var aliceWorld = alice.Client.World.GetSnapshot();
        var bobWorld = bob.Client.World.GetSnapshot();
        gameThreadMs.Sort();
        refreshMs.Sort();
        _o.WriteLine($"alice after 1.2 km: {alice.Client.World.EntityCount} entities, {alice.Client.World.Tiles.Count} tiles, " +
                     $"{(alice.BytesIn - aliceBytesAtSpawn) / 1024.0:F0} KB on the way, {alice.Client.World.AcousticRefreshes} acoustic refreshes " +
                     $"(median {refreshMs[refreshMs.Count / 2]:F0} ms, worst {refreshMs[^1]:F0} ms, off the game thread); " +
                     $"game thread's tile work per tick: median {gameThreadMs[gameThreadMs.Count / 2]:F2} ms, worst {gameThreadMs[^1]:F1} ms over {gameThreadMs.Count} ticks");
        _o.WriteLine($"game thread's tile work, 90th percentile {gameThreadMs[gameThreadMs.Count * 9 / 10]:F1} ms; making the woods, worst {alice.Client.World.WoodsMsMax:F1} ms");
        // A guard against a gross regression (a whole-grid rebuild was 30-60 ms a tile), not a benchmark:
        // on a loaded machine this measured 17-24 ms.
        Assert.True(gameThreadMs[gameThreadMs.Count * 9 / 10] < 30, "a tile costs the game thread too much");

        // Each client holds exactly what the server thinks it holds.
        Assert.Equal(alice.Session.Tiles.Levels.OrderBy(kv => kv.Key.X).ThenBy(kv => kv.Key.Z),
                     alice.Client.World.Tiles.OrderBy(kv => kv.Key.X).ThenBy(kv => kv.Key.Z));
        foreach (int known in alice.Session.KnownEntities) Assert.True(aliceWorld.Entities.ContainsKey(known), $"alice was never sent {known}");
        foreach (var e in aliceWorld.Entities.Values)
            if (tiles.IsTiled(e.Id)) Assert.Contains(e.Id, alice.Session.KnownEntities);
        AssertConsistent(alice.Session, tiles);

        // Bob is 1.2 km behind her, past her full radius: his body has gone from her client, and hers from his.
        Assert.False(tiles.Holds(world.Get<Transform>(bob.Session.Entity).Position, alice.Session.Tiles.Levels));
        Assert.False(aliceWorld.Entities.ContainsKey(bob.Session.Entity.Id));
        Assert.False(bobWorld.Entities.ContainsKey(alice.Session.Entity.Id));
        // Bob's tiles have not changed.
        Assert.Equal(bobTiles.OrderBy(kv => kv.Key.X).ThenBy(kv => kv.Key.Z), bob.Client.World.Tiles.OrderBy(kv => kv.Key.X).ThenBy(kv => kv.Key.Z));

        // Her acoustic map covers her tiles and not the spawn's rooms.
        var aMap = aliceWorld.AcousticMap!;
        Assert.True(alice.Client.World.AcousticRefreshes > 0);
        var rooms = aliceWorld.Entities.Values.Where(e => e.Definition.Region.RoomSize.X > 0f && !e.Definition.Moves).Select(e => e.Id).ToHashSet();
        Assert.Equal(rooms.OrderBy(i => i), aMap.Regions.Keys.Where(k => k != AcousticConstants.GlobalRegionId).OrderBy(i => i));
        AssertNoDanglingPortals(aMap);
        // ...while bob's still has the rooms round the spawn.
        Assert.Contains(bobWorld.AcousticMap!.Regions.Keys, k => k != AcousticConstants.GlobalRegionId
            && bobWorld.Entities.TryGetValue(k, out var r) && Vector3.Distance(r.Transform.Position, spawn) < 300f);
    }
}

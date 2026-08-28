using System.Numerics;
using Arch.Core;
using Microsoft.EntityFrameworkCore;
using MemoryPack;
using OpenFPS.Client.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;

namespace OpenFPS.Tests;

/// <summary>
/// Cover for step 4 of the engineering audit — "close the server's structural holes".
///
/// The theme is state that only ever grew. The protocol could add an entity to a client and never remove
/// one, so a disconnected player stayed forever as a ghost that still collided and still made noise. The
/// loop banked unbounded elapsed time. Registration always reported success. Commands mutated the ECS
/// world from whichever thread happened to call them — and the guard that prevented that race worked by
/// throwing every MUD command away. /spawn created objects nothing could see, hear or touch.
///
/// Two things here are not unit-testable and are verified by inspection: the Ctrl-C / SIGTERM handlers in
/// <c>Program.Main</c>, and the socket-level delivery of <see cref="EntityRemoved"/>. What is tested is
/// every decision those paths depend on.
/// </summary>
public class ServerHolesTests
{
    // ── The despawn protocol ────────────────────────────────────────────────────────────────────

    [Fact]
    public void EntityRemoved_IsPartOfTheMessageUnion()
    {
        // A union tag that was never registered fails at runtime on the first send, not at compile time.
        IMessage message = new EntityRemoved { EntityIds = new List<int> { 7, 11 } };
        var bytes = MemoryPackSerializer.Serialize(message);
        var decoded = MemoryPackSerializer.Deserialize<IMessage>(bytes);

        var removed = Assert.IsType<EntityRemoved>(decoded);
        Assert.Equal(new[] { 7, 11 }, removed.EntityIds);
    }

    [Fact]
    public void RemovedEntitiesLeaveTheClientWorldEntirely()
    {
        var world = new ClientWorldState();
        world.Clear(new Vector3(100, 20, 100), new Vector3(-50, 0, -50), new Vector3(50, 20, 50));

        world.RegisterDefinition(StaticWall(id: 42, new Vector3(5, 1, 5)));
        world.RegisterDefinition(NoisyProp(id: 43, new Vector3(6, 1, 6)));

        Assert.Equal(2, world.GetSnapshot().Entities.Count);
        Assert.Contains(43, world.GetSnapshot().AudioEntityIds);

        var removed = world.RemoveEntities(new[] { 43 });

        Assert.Equal(new[] { 43 }, removed);
        var after = world.GetSnapshot();
        Assert.DoesNotContain(43, after.Entities.Keys);
        Assert.DoesNotContain(43, after.AudioEntityIds);
        Assert.Contains(42, after.Entities.Keys); // the wall is untouched
    }

    [Fact]
    public void RemovingAnEntityDropsItFromTheCollisionGrid()
    {
        var world = new ClientWorldState();
        world.Clear(new Vector3(100, 20, 100), new Vector3(-50, 0, -50), new Vector3(50, 20, 50));
        world.RegisterDefinition(StaticWall(id: 42, new Vector3(5, 1, 5)));

        var grid = world.GetSnapshot().StaticGrid!;
        Assert.Contains(42, grid.GetItemsInRadius(new Vector3(5, 1, 5), 2f));

        world.RemoveEntities(new[] { 42 });

        // A ghost left in the grid keeps blocking movement through empty space.
        grid = world.GetSnapshot().StaticGrid!;
        Assert.DoesNotContain(42, grid.GetItemsInRadius(new Vector3(5, 1, 5), 2f));
    }

    [Fact]
    public void RemovingAnUnknownEntityReportsNothingRemoved()
    {
        var world = new ClientWorldState();
        world.Clear(new Vector3(100, 20, 100), new Vector3(-50, 0, -50), new Vector3(50, 20, 50));

        Assert.Empty(world.RemoveEntities(new[] { 999 }));
    }

    // ── Area-of-interest bookkeeping ────────────────────────────────────────────────────────────

    [Fact]
    public void DepartedEntitiesAreExactlyWhatWasVisibleAndIsNot()
    {
        var previously = new HashSet<int> { 1, 2, 3 };
        var now = new HashSet<int> { 2, 3, 4 };
        var departed = new List<int>();

        GameServer.CollectDeparted(previously, now, departed);

        Assert.Equal(new[] { 1 }, departed);
    }

    [Fact]
    public void DepartedIsClearedBeforeUse()
    {
        var departed = new List<int> { 99 }; // stale content from a previous session's pass
        GameServer.CollectDeparted(new HashSet<int> { 5 }, new HashSet<int> { 5 }, departed);

        Assert.Empty(departed);
    }

    // ── The accumulator clamp ───────────────────────────────────────────────────────────────────

    [Fact]
    public void ANormalFrameIsNotClamped()
    {
        double kept = GameServer.ClampAccumulatorMs(40.0, out int dropped);

        Assert.Equal(40.0, kept);
        Assert.Equal(0, dropped);
    }

    [Fact]
    public void ALongStallIsCappedAndReported()
    {
        // Five seconds banked — a GC pause or a laptop lid. Unclamped that is 150 catch-up ticks.
        double kept = GameServer.ClampAccumulatorMs(5000.0, out int dropped);

        Assert.Equal(PhysicsConstants.MaxCatchUpSeconds * 1000.0, kept);
        Assert.True(dropped > 100, $"expected the dropped ticks to be reported, got {dropped}");
        Assert.True(kept / (1000.0 / PhysicsConstants.TickRate) < 7.0,
            "the clamp must leave at most a handful of ticks to catch up on");
    }

    // ── Auth rate limiting ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void ABurstIsAllowedAndThenRefused()
    {
        var limiter = new RateLimiter(capacity: 3, refillPerSecond: 0.0001);

        Assert.True(limiter.TryConsume("10.0.0.1"));
        Assert.True(limiter.TryConsume("10.0.0.1"));
        Assert.True(limiter.TryConsume("10.0.0.1"));
        Assert.False(limiter.TryConsume("10.0.0.1"));
    }

    [Fact]
    public void OneAddressCannotExhaustAnother()
    {
        var limiter = new RateLimiter(capacity: 1, refillPerSecond: 0.0001);

        Assert.True(limiter.TryConsume("10.0.0.1"));
        Assert.False(limiter.TryConsume("10.0.0.1"));
        Assert.True(limiter.TryConsume("10.0.0.2"));
    }

    [Fact]
    public void TheBucketRefills()
    {
        var limiter = new RateLimiter(capacity: 1, refillPerSecond: 200.0); // one token per 5 ms
        Assert.True(limiter.TryConsume("10.0.0.1"));
        Assert.False(limiter.TryConsume("10.0.0.1"));

        Thread.Sleep(60);

        Assert.True(limiter.TryConsume("10.0.0.1"));
    }

    // ── Registration tells the truth ────────────────────────────────────────────────────────────

    [Fact]
    public void RegisteringADuplicateUsernameFails()
    {
        using var db = new TempDatabase();
        var repo = new SqliteUserRepository(db.Options);

        Assert.True(repo.AddUser("newcomer", "hunter2", UserRole.Player));
        // Previously this returned nothing and the server replied Success = true regardless — the player
        // was told the account existed and then could not log into it.
        Assert.False(repo.AddUser("newcomer", "different-password", UserRole.Player));

        // And the original password still works, so the second attempt did not overwrite it.
        Assert.True(repo.VerifyPassword("newcomer", "hunter2"));
    }

    [Fact]
    public void UsernamesAreFoldedInvariantly()
    {
        using var db = new TempDatabase();
        var repo = new SqliteUserRepository(db.Options);

        Assert.True(repo.AddUser("Istanbul", "hunter2", UserRole.Player));
        Assert.False(repo.AddUser("istanbul", "hunter2", UserRole.Player));
        Assert.True(repo.VerifyPassword("ISTANBUL", "hunter2"));
    }

    // ── One spawn path ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SpawnEntityRegistersIndexesAndFlagsForBroadcast()
    {
        var maps = LoadShippedMaps();
        Assert.True(maps.TryGetMap("default", out var world, out _, out var grid, out var lookup));

        var position = new Vector3(20, 1, -20);
        var spawned = maps.SpawnEntity("default", w => w.Create(
            new Transform { Position = position, Rotation = Quaternion.Identity },
            new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(1, 1, 1), IsSolid = true },
            new MaterialComponent { Material = "Metal", Variant = "0" },
            new IdentityComponent { Name = "Test Pillar" },
            EntityType.StaticObject));

        Assert.NotEqual(Entity.Null, spawned);
        // The three things a bare world.Create left out, each of which made the object unreachable in a
        // different subsystem: /scan and interaction (lookup), collision and broadcast (grid), and the
        // client's first sight of it (the dirty flag).
        Assert.True(lookup.ContainsKey(spawned.Id), "spawned entity is missing from the map's id lookup");
        Assert.Contains(spawned, grid.GetItemsInRadius(position, 2f));
        Assert.True(world.Get<Transform>(spawned).IsDirty, "spawned entity was not flagged for broadcast");
    }

    [Fact]
    public void DestroyEntityRemovesItFromTheLookupAndTheGrid()
    {
        var maps = LoadShippedMaps();
        Assert.True(maps.TryGetMap("default", out var world, out _, out var grid, out var lookup));

        var position = new Vector3(-22, 1, 22);
        var spawned = maps.SpawnEntity("default", w => w.Create(
            new Transform { Position = position, Rotation = Quaternion.Identity },
            new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(1, 1, 1), IsSolid = true },
            EntityType.StaticObject));

        maps.DestroyEntity("default", spawned);

        Assert.False(lookup.ContainsKey(spawned.Id));
        Assert.DoesNotContain(spawned, grid.GetItemsInRadius(position, 2f));
        Assert.False(world.IsAlive(spawned));
    }

    // ── Commands: one thread, both transports ───────────────────────────────────────────────────

    [Fact]
    public void CommandsDoNotTouchTheWorldUntilTheTickThreadRunsThem()
    {
        var (server, commands, maps, sessions) = BuildCommandStack();
        var session = SpawnTestPlayer(maps, sessions, connectionId: 1, UserRole.Admin);
        Assert.True(maps.TryGetMap("default", out var world, out _, out _, out var lookup));
        int before = lookup.Count;

        var replies = new List<IMessage>();
        commands.HandleTextCommand(session.ConnectionId,
            new TextCommand { Command = "spawn", Args = new[] { "Box", "Metal", "1", "1", "1" } }, replies.Add);

        // The MUD gateway calls this from its own TCP task. Mutating the Arch world there would race the
        // simulation, which is what the old (command-dropping) peer guard was really protecting against.
        Assert.Empty(replies);
        Assert.Equal(before, lookup.Count);

        server.DrainCommandBuffer();

        Assert.Equal(before + 1, lookup.Count);
        Assert.Contains(replies, m => m is TextEvent t && t.Text.Contains("Spawned"));
    }

    [Fact]
    public void ATextClientConnectionIdIsNoLongerSilentlyDropped()
    {
        var (server, commands, maps, sessions) = BuildCommandStack();
        // MUD connection ids start at 10000 and have no LiteNetLib peer; every gameplay handler used to
        // begin with a peer lookup and return, so scan, move, spawn and chat all did nothing at all.
        var session = SpawnTestPlayer(maps, sessions, connectionId: 10001, UserRole.Player);
        session.IsTextClient = true;

        var replies = new List<IMessage>();
        commands.HandleTextCommand(session.ConnectionId, new TextCommand { Command = "scan" }, replies.Add);
        server.DrainCommandBuffer();

        Assert.NotEmpty(replies);
        Assert.All(replies, m => Assert.IsType<TextEvent>(m));
    }

    [Fact]
    public void ACommandFromAPlayerWithNoBodySaysSoInsteadOfThrowing()
    {
        var (server, commands, _, sessions) = BuildCommandStack();
        var session = new UserSession { ConnectionId = 10002, Username = "lurker", IsTextClient = true };
        sessions.AddSession(session.ConnectionId, session); // authenticated, never sent 'ready'

        var replies = new List<IMessage>();
        commands.HandleTextCommand(session.ConnectionId, new TextCommand { Command = "scan" }, replies.Add);
        server.DrainCommandBuffer();

        var text = Assert.IsType<TextEvent>(Assert.Single(replies));
        Assert.Contains("ready", text.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ElevatedCommandsAreStillRefusedToPlayers()
    {
        var (server, commands, maps, sessions) = BuildCommandStack();
        var session = SpawnTestPlayer(maps, sessions, connectionId: 2, UserRole.Player);
        Assert.True(maps.TryGetMap("default", out _, out _, out _, out var lookup));
        int before = lookup.Count;

        var replies = new List<IMessage>();
        commands.HandleTextCommand(session.ConnectionId,
            new TextCommand { Command = "spawn", Args = new[] { "Box", "Metal", "1", "1", "1" } }, replies.Add);
        server.DrainCommandBuffer();

        Assert.Equal(before, lookup.Count);
        var text = Assert.IsType<TextEvent>(Assert.Single(replies));
        Assert.Contains("permission", text.Text, StringComparison.OrdinalIgnoreCase);
    }

    // ── Fixtures ────────────────────────────────────────────────────────────────────────────────

    private static MapManager LoadShippedMaps()
    {
        var prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
        var maps = new MapRepository(Path.Combine(AppContext.BaseDirectory, "maps"));
        var manager = new MapManager(maps, prefabs);
        manager.Initialize();
        return manager;
    }

    private static (GameServer server, CommandHandler commands, MapManager maps, SessionManager sessions) BuildCommandStack()
    {
        var maps = LoadShippedMaps();
        var sessions = new SessionManager();
        // Never Start()ed: no socket is bound, so the command buffer and the reply callback are all that
        // are exercised — which is exactly the surface under test.
        var server = new GameServer(new StubUserRepository());
        return (server, new CommandHandler(sessions, maps, server), maps, sessions);
    }

    private static UserSession SpawnTestPlayer(MapManager maps, SessionManager sessions, int connectionId, UserRole role)
    {
        Assert.True(maps.TryGetMap("default", out var world, out _, out _, out _));
        var entity = world.Create(
            new Transform { Position = new Vector3(0, 2, 0), Rotation = Quaternion.Identity },
            new Velocity { Linear = Vector3.Zero },
            new PlayerComponent { ConnectionId = connectionId, Username = "tester", Role = role },
            EntityType.Player);
        maps.IndexEntity("default", entity);

        var session = new UserSession { ConnectionId = connectionId, Username = "tester", Role = role, Entity = entity };
        sessions.AddSession(connectionId, session);
        return session;
    }

    private static EntityDefinition StaticWall(int id, Vector3 position) => new()
    {
        EntityId = id,
        Type = EntityType.StaticObject,
        Transform = new Transform { Position = position, Rotation = Quaternion.Identity },
        Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(2, 3, 2), IsSolid = true }
    };

    private static EntityDefinition NoisyProp(int id, Vector3 position)
    {
        var def = StaticWall(id, position);
        def.SoundEmitter = new SoundEmitterComponent { SoundId = "hum", Mode = PlaybackMode.LoopOne, Volume = 1f, Range = 20f };
        return def;
    }

    private sealed class StubUserRepository : IUserRepository
    {
        public UserData? GetUser(string username) => null;
        public bool AddUser(string username, string password, UserRole role) => true;
        public bool VerifyPassword(string username, string password) => false;
    }

    private sealed class TempDatabase : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"openfps-test-{Guid.NewGuid():N}.db");

        public DbContextOptions<AppDbContext> Options =>
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={_path}").Options;

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (File.Exists(_path)) File.Delete(_path); } catch { /* the OS will get it */ }
        }
    }
}

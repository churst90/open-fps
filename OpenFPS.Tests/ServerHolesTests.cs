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
/// The server's structural holes (engineering audit, step 4): entities removed from clients, the tick
/// accumulator clamped, registration that tells the truth, commands on one thread from both transports,
/// one spawn path, finite movement input. The Ctrl-C / SIGTERM handlers in <c>Program.Main</c> and the
/// socket delivery of <see cref="EntityRemoved"/> are checked by inspection only.
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
        world.Clear(new Vector3(100, 20, 100));

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
        world.Clear(new Vector3(100, 20, 100));
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
        world.Clear(new Vector3(100, 20, 100));

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
        // Five seconds banked (a GC pause, a laptop lid): unclamped, 150 catch-up ticks.
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
        // The server used to reply Success regardless, and the player could not log in.
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
        // What a bare world.Create left out: the lookup (/scan, interaction), the grid (collision,
        // broadcast) and the dirty flag (the client's first sight of it).
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

        // The MUD gateway calls this from its own TCP task: the command must be queued for the tick, not
        // run against the Arch world there.
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
        // MUD connection ids start at 10000 and have no LiteNetLib peer; handlers that began with a peer
        // lookup did nothing for them.
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

    // ── Movement input is finite ────────────────────────────────────────────────────────────────

    /// <summary>A client's move and look reach the simulation only if finite: a NaN direction is not
    /// "over one" so it skipped normalising into the position and grid, and Math.Clamp passes NaN, which
    /// left the yaw NaN for good.</summary>
    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void Input_that_is_not_a_number_reaches_nothing(float bad)
    {
        var dispatcher = new MessageDispatcher();
        ClientInputUpdate? seen = null;
        dispatcher.RegisterHandler<ClientInputUpdate>((_, input, _) => seen = input);

        dispatcher.Dispatch(1, new ClientInputUpdate
        {
            SequenceId = 1,
            MoveDirection = new Vector3(bad, 0f, 1f),
            LookDelta = new Vector2(0.5f, bad),
            DeltaTime = bad,
        }, _ => { });

        Assert.NotNull(seen);
        Assert.Equal(Vector3.Zero, seen!.MoveDirection);
        Assert.Equal(Vector2.Zero, seen.LookDelta);
        Assert.True(float.IsFinite(seen.DeltaTime) && seen.DeltaTime > 0f, $"DeltaTime {seen.DeltaTime}");
    }

    [Fact]
    public void Ordinary_input_passes_the_gate_as_it_was()
    {
        var dispatcher = new MessageDispatcher();
        ClientInputUpdate? seen = null;
        dispatcher.RegisterHandler<ClientInputUpdate>((_, input, _) => seen = input);

        // A coarse turn is 45 degrees in one tick, a LookDelta far over one: the gate leaves a finite
        // look alone.
        var look = new Vector2(15.7f, -0.2f);
        dispatcher.Dispatch(1, new ClientInputUpdate
        {
            SequenceId = 1, MoveDirection = new Vector3(3f, 0f, 4f), LookDelta = look, DeltaTime = 0.033f,
        }, _ => { });

        Assert.NotNull(seen);
        Assert.Equal(1f, seen!.MoveDirection.Length(), 4);
        Assert.Equal(look, seen.LookDelta);
        Assert.Equal(0.033f, seen.DeltaTime);
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
        // Never started: no socket is bound, only the command buffer and the reply callback run.
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

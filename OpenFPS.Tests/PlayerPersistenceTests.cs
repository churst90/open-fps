using System.Numerics;
using Arch.Core;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using OpenFPS.Server.Systems;
using LiteNetLib;

namespace OpenFPS.Tests;

/// <summary>
/// A player who leaves the world comes back to it as they left (Cody, 2026-10-04: "are inventories
/// persisted across user logins?" — they were not: logging out put everything down). What they carry,
/// loaded as it was, in the hands and on the back it was in, with their spare rounds; where they stood
/// and faced; their health. Run through the real login, ready, logout and disconnect paths on a server
/// with no sockets, over a real accounts database.
/// </summary>
public class PlayerPersistenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "openfps-keep-" + Guid.NewGuid().ToString("N"));

    public PlayerPersistenceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private DbContextOptions<AppDbContext> Db(string name = "accounts.db") =>
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={Path.Combine(_dir, name)}").Options;

    /// <summary>A server with no sockets over the given maps, a real accounts database, and hands wired
    /// the way Start() wires them.</summary>
    private sealed class Rig
    {
        public readonly SqliteUserRepository Repo;
        public readonly MapManager Maps;
        public readonly SessionManager Sessions = new();
        public readonly GameServer Server;
        public readonly HandsService Hands;
        private int _connection = 100;

        public Rig(string dir, DbContextOptions<AppDbContext> db, params string[] maps)
        {
            Repo = new SqliteUserRepository(db, workFactor: 4);
            string mapDir = Path.Combine(dir, "maps");
            Directory.CreateDirectory(mapDir);
            foreach (string map in maps)
                File.Copy(Path.Combine(AppContext.BaseDirectory, "maps", map + ".json"), Path.Combine(mapDir, map + ".json"), overwrite: true);
            Maps = new MapManager(new MapRepository(mapDir), new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs")));
            Maps.Initialize();
            Hands = new HandsService(Maps);
            Server = new GameServer(Repo);
            Server.Attach(Maps, Sessions, new OccupancyService(Maps), Hands);
            Hands.Carried = Server.SyncAudioComponent;
            Server.Sent = (_, _) => { };
        }

        /// <summary>Logs in as a text client and sends 'ready': the session, with a body in the world.</summary>
        public async Task<UserSession> Arrive(string name)
        {
            int connection = ++_connection;
            await Server.Login(connection, new LoginRequest { Username = name, Password = "correct horse" }, _ => { });
            Server.DrainCommandBuffer();
            Assert.True(Sessions.TryGetSession(connection, out var session), $"{name} did not log in");
            Server.HandlePlayerReady(connection);
            Server.DrainCommandBuffer();
            Assert.NotEqual(Entity.Null, session.Entity);
            return session;
        }

        public void LogOut(UserSession session)
        {
            Server.LogOut(session.ConnectionId);
            Server.DrainCommandBuffer();
        }

        public World World(UserSession s) { Assert.True(Maps.TryGetMap(s.CurrentMapId, out var w, out _, out _, out _)); return w; }
        public Dictionary<int, Entity> Lookup(UserSession s) { Assert.True(Maps.TryGetMap(s.CurrentMapId, out _, out _, out _, out var l)); return l; }

        /// <summary>Every live thing on a map made from a prefab, held or not.</summary>
        public List<Entity> Made(string mapId, string prefab)
        {
            Assert.True(Maps.TryGetMap(mapId, out var world, out _, out _, out _));
            var found = new List<Entity>();
            world.Query(new QueryDescription().WithAll<ItemComponent, IdentityComponent>(), (Entity e, ref ItemComponent _, ref IdentityComponent id) =>
            {
                if (id.PrefabId == prefab) found.Add(e);
            });
            return found;
        }
    }

    private Rig NewRig(params string[] maps)
    {
        var rig = new Rig(_dir, Db(), maps.Length == 0 ? new[] { "default" } : maps);
        foreach (var name in new[] { "alice", "bob" }) rig.Repo.AddUser(name, "correct horse", UserRole.Player);
        return rig;
    }

    // ── What you carry ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ARifleInBothHandsAPistolOnTheBackAndSpareRoundsComeBackAsTheyWere()
    {
        var rig = NewRig();
        int akmsBefore = rig.Made("default", "akm_rifle").Count, glocksBefore = rig.Made("default", "glock_pistol").Count;

        var alice = await rig.Arrive("alice");
        var world = rig.World(alice);
        Assert.True(rig.Hands.Give(alice, "akm_rifle", 1, out _, out _, out string m), m);
        Assert.True(rig.Hands.Give(alice, "glock_pistol", 1, out _, out string placed, out m), m);
        Assert.Contains("back", placed);
        Assert.True(HandsService.TryGetHeldWeapon(world, alice.Entity, rig.Lookup(alice), out var akm, out var rifle));
        Arms.Ammo(world, rifle, akm).Rounds = 17;
        world.Get<AmmoReserveComponent>(alice.Entity).Rounds = new Dictionary<string, int> { ["9mm"] = 60 };

        rig.LogOut(alice);

        // Nothing left on the floor: they went with her.
        Assert.Equal(akmsBefore, rig.Made("default", "akm_rifle").Count);
        Assert.Equal(glocksBefore, rig.Made("default", "glock_pistol").Count);
        Assert.NotNull(rig.Repo.GetUser("alice")!.Belongings);

        var back = await rig.Arrive("alice");
        world = rig.World(back);
        var lookup = rig.Lookup(back);
        var (right, left) = HandsService.Holding(world, back.Entity, lookup);
        Assert.NotNull(right);
        Assert.Equal(right, left);                                   // both hands, as it was
        Assert.Equal("akm_rifle", world.Get<IdentityComponent>(right!.Value).PrefabId);
        Assert.Equal(17, Arms.RoundsIn(world, right.Value));
        var stowed = Assert.Single(HandsService.Stowed(world, back.Entity, lookup));
        Assert.Equal("glock_pistol", world.Get<IdentityComponent>(stowed).PrefabId);
        Assert.Equal(17, Arms.RoundsIn(world, stowed));
        Assert.Equal(60, Arms.Reserve(world, back.Entity, "9mm"));
        Assert.Equal(0, Arms.Reserve(world, back.Entity, "7.62x39"));
        foreach (var item in new[] { right.Value, stowed })
        {
            Assert.Equal(back.Entity.Id, world.Get<HeldComponent>(item).HolderEntityId);
            Assert.Contains(item.Id, rig.Server.PendingDefinitionResends);   // the Carried hook, so clients hear it is held
        }

        // One of each in the world, and the store has given them up.
        Assert.Equal(akmsBefore + 1, rig.Made("default", "akm_rifle").Count);
        Assert.Equal(glocksBefore + 1, rig.Made("default", "glock_pistol").Count);
        Assert.Null(rig.Repo.GetUser("alice")!.Belongings);
        Assert.Contains("17 rounds", rig.Hands.Readout(back));
    }

    [Fact]
    public async Task AThingPutDownBeforeLeavingStaysOnTheFloorAndDoesNotComeBack()
    {
        var rig = NewRig();
        int before = rig.Made("default", "glock_pistol").Count;
        var alice = await rig.Arrive("alice");
        Assert.True(rig.Hands.Give(alice, "glock_pistol", 1, out _, out _, out string m), m);
        Assert.True(rig.Hands.Drop(alice, "", out m), m);

        rig.LogOut(alice);
        var back = await rig.Arrive("alice");

        var world = rig.World(back);
        Assert.Null(HandsService.Holding(world, back.Entity, rig.Lookup(back)).Right);
        Assert.Empty(HandsService.Stowed(world, back.Entity, rig.Lookup(back)));
        var floor = Assert.Single(rig.Made("default", "glock_pistol").Skip(before));
        Assert.False(world.Has<HeldComponent>(floor));
    }

    [Fact]
    public async Task AThingBroughtBackThenGivenAwayIsNotBroughtBackTwiceAfterADisconnect()
    {
        var rig = NewRig();
        int before = rig.Made("default", "akm_rifle").Count;
        var alice = await rig.Arrive("alice");
        Assert.True(rig.Hands.Give(alice, "akm_rifle", 1, out _, out _, out string m), m);
        rig.LogOut(alice);

        // Back with it; she puts it down and bob picks it up; then her connection drops.
        alice = await rig.Arrive("alice");
        var bob = await rig.Arrive("bob");
        Assert.True(rig.Hands.Drop(alice, "", out m), m);
        var world = rig.World(bob);
        var rifle = rig.Made("default", "akm_rifle").Skip(before).Single();
        world.Get<Transform>(bob.Entity).Position = world.Get<Transform>(rifle).Position;
        Assert.True(rig.Hands.Take(bob, "akm", out m), m);
        rig.Server.PeerDisconnected(alice.ConnectionId, DisconnectReason.Timeout);
        rig.Server.DrainCommandBuffer();

        alice = await rig.Arrive("alice");
        Assert.Null(HandsService.Holding(rig.World(alice), alice.Entity, rig.Lookup(alice)).Right);
        Assert.Single(rig.Made("default", "akm_rifle").Skip(before));     // bob's, and only bob's

        // And bob, leaving and coming back, keeps it.
        rig.LogOut(bob);
        bob = await rig.Arrive("bob");
        var (right, _) = HandsService.Holding(rig.World(bob), bob.Entity, rig.Lookup(bob));
        Assert.Equal("akm_rifle", rig.World(bob).Get<IdentityComponent>(right!.Value).PrefabId);
        Assert.Single(rig.Made("default", "akm_rifle").Skip(before));
    }

    [Fact]
    public async Task ALoginFromSomewhereElseTakesTheThingsOverAndMakesNoSecondSet()
    {
        var rig = NewRig();
        int before = rig.Made("default", "akm_rifle").Count;
        var first = await rig.Arrive("alice");
        Assert.True(rig.Hands.Give(first, "akm_rifle", 1, out _, out _, out string m), m);

        var second = await rig.Arrive("alice");      // no logout: the newest login wins

        Assert.Equal(Entity.Null, first.Entity);
        var (right, _) = HandsService.Holding(rig.World(second), second.Entity, rig.Lookup(second));
        Assert.NotNull(right);
        Assert.Single(rig.Made("default", "akm_rifle").Skip(before));
    }

    [Fact]
    public async Task AFreshAccountStartsWithNothingAtTheSpawnAndWhole()
    {
        var rig = NewRig();
        var bob = await rig.Arrive("bob");
        var world = rig.World(bob);

        Assert.Null(HandsService.Holding(world, bob.Entity, rig.Lookup(bob)).Right);
        Assert.Empty(HandsService.Stowed(world, bob.Entity, rig.Lookup(bob)));
        Assert.Equal("", Arms.ReserveWords(world, bob.Entity));
        Assert.Equal(rig.Maps.GetSpawnPoint("default").Position, world.Get<Transform>(bob.Entity).Position);
        Assert.Equal(100, world.Get<HealthComponent>(bob.Entity).Current);
        Assert.Null(rig.Repo.GetUser("bob")!.PlayerState);
    }

    [Fact]
    public async Task AStoreThatCannotKeepThingsStillHasThemPutDown()
    {
        // The stub stores of the other tests keep nothing: leaving puts the things down, as it always did.
        var rig = NewRig();
        var alice = await rig.Arrive("alice");
        int before = rig.Made("default", "glock_pistol").Count;
        Assert.True(rig.Hands.Give(alice, "glock_pistol", 1, out _, out _, out string m), m);
        var world = rig.World(alice);
        var store = new PlayerStore(new NothingKept(), rig.Maps, rig.Hands);

        var gone = store.Leave(alice, "default", world, alice.Entity, rig.Lookup(alice));

        Assert.Empty(gone);
        var (right, _) = HandsService.Holding(world, alice.Entity, rig.Lookup(alice));
        Assert.NotNull(right);                   // still on the body, for the caller to put down
        Assert.Equal(before + 1, rig.Made("default", "glock_pistol").Count);
    }

    private sealed class NothingKept : IUserRepository
    {
        public UserData? GetUser(string username) => null;
        public bool AddUser(string username, string password, UserRole role) => true;
        public bool VerifyPassword(string username, string password) => false;
    }

    // ── Where you were ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task YouComeBackWhereYouStoodFacingTheWayYouFacedAsHurtAsYouWere()
    {
        var rig = NewRig();
        var alice = await rig.Arrive("alice");
        var world = rig.World(alice);
        var spot = rig.Maps.GetSpawnPoint("default").Position + new Vector3(3f, 0f, 2f);
        Assert.True(rig.Maps.TryGetMap("default", out _, out _, out var grid, out _));
        Assert.True(rig.Maps.TryGetMapData("default", out var data));
        Assert.True(PlayerStore.IsSafe(world, grid, data, spot, out var standing), "the test's spot is not somewhere to stand");
        world.Get<Transform>(alice.Entity).Position = standing;
        world.Get<PlayerComponent>(alice.Entity).Yaw = 1.2f;
        world.Get<HealthComponent>(alice.Entity).Current = 63;

        rig.LogOut(alice);
        var back = await rig.Arrive("alice");

        world = rig.World(back);
        Assert.True(Vector3.Distance(standing, world.Get<Transform>(back.Entity).Position) < 0.01f);
        Assert.Equal(1.2f, world.Get<PlayerComponent>(back.Entity).Yaw, 3);
        Assert.Equal(1.2f, YawOf(world.Get<Transform>(back.Entity).Rotation), 3);
        Assert.Equal(63, world.Get<HealthComponent>(back.Entity).Current);
    }

    [Fact]
    public async Task APlaceThatIsNoLongerSomewhereToStandGivesTheSpawn()
    {
        var rig = NewRig();
        var spawn = rig.Maps.GetSpawnPoint("default").Position;
        Assert.True(rig.Maps.TryGetMap("default", out var world, out _, out var grid, out _));
        Assert.True(rig.Maps.TryGetMapData("default", out var data));

        // In mid-air, off the map, and inside something solid.
        Assert.False(PlayerStore.IsSafe(world, grid, data, spawn + new Vector3(0, 30, 0), out _));
        Assert.False(PlayerStore.IsSafe(world, grid, data, new Vector3(5000, 1, 0), out _));
        Vector3? wall = null;
        world.Query(new QueryDescription().WithAll<Transform, ColliderComponent>(), (Entity e, ref Transform tr, ref ColliderComponent c) =>
        {
            if (wall == null && c.IsSolid && c.Size.Y > 2f && tr.Position.Y < 5f && !world.Has<Velocity>(e)) wall = tr.Position;
        });
        Assert.NotNull(wall);
        var inside = wall.Value with { Y = 0.05f };
        Assert.False(PlayerStore.IsSafe(world, grid, data, inside, out _));

        var state = new PlayerState { Map = "default" };
        state.SetPlace("default", new SavedPlace { X = inside.X, Y = inside.Y, Z = inside.Z, Yaw = 2f });
        Assert.True(rig.Repo.SavePlayer("alice", state.ToJson(), null));
        var alice = await rig.Arrive("alice");
        Assert.Equal(spawn, rig.World(alice).Get<Transform>(alice.Entity).Position);
    }

    [Fact]
    public async Task SomebodyWhoLeavesDeadComesBackWholeAtTheSpawn()
    {
        var rig = NewRig();
        var alice = await rig.Arrive("alice");
        var world = rig.World(alice);
        world.Get<Transform>(alice.Entity).Position += new Vector3(3f, 0f, 2f);
        world.Get<HealthComponent>(alice.Entity).Current = 0;
        world.Add(alice.Entity, new DeadComponent { DiedAt = 1 });
        Assert.True(rig.Hands.Give(alice, "glock_pistol", 1, out _, out _, out string m), m);

        rig.LogOut(alice);
        var back = await rig.Arrive("alice");

        world = rig.World(back);
        Assert.Equal(rig.Maps.GetSpawnPoint("default").Position, world.Get<Transform>(back.Entity).Position);
        Assert.Equal(100, world.Get<HealthComponent>(back.Entity).Current);
        Assert.False(world.Has<DeadComponent>(back.Entity));
        // Given while dead, after the bag was left (staff can): kept like anything else carried.
        Assert.NotNull(HandsService.Holding(world, back.Entity, rig.Lookup(back)).Right);
    }

    /// <summary>
    /// Killed, a player's things go into a bag beside their body (Cody, 2026-10-05), so logging out dead
    /// stores nothing: they do not come back with what is lying in the bag.
    /// </summary>
    [Fact]
    public async Task APlayerKilledDoesNotGetTheirThingsBackByLeaving()
    {
        var rig = NewRig();
        int akmsBefore = rig.Made("default", "akm_rifle").Count;
        var alice = await rig.Arrive("alice");
        var bob = await rig.Arrive("bob");
        var world = rig.World(alice);
        Assert.True(rig.Hands.Give(alice, "akm_rifle", 1, out _, out _, out string m), m);
        var combat = new CombatService(rig.Maps, rig.Server, rig.Sessions) { Possessions = rig.Hands };
        Assert.True(rig.Maps.TryGetMap("default", out _, out _, out var grid, out _));
        Assert.True(combat.Wound(bob, world, grid, alice.Entity, 1000, WeaponRegistry.Akm, _ => { }));

        rig.LogOut(alice);
        Assert.Null(rig.Repo.GetUser("alice")!.Belongings);
        var back = await rig.Arrive("alice");
        world = rig.World(back);
        Assert.Null(HandsService.Holding(world, back.Entity, rig.Lookup(back)).Right);
        Assert.Equal(0, Arms.Reserve(world, back.Entity, "7.62x39"));
        // The rifle is in the bag by the body, as a line in its list, and nowhere in the world.
        Assert.Equal(akmsBefore, rig.Made("default", "akm_rifle").Count);
        var bags = new List<Entity>();
        world.Query(new QueryDescription().WithAll<BelongingsBag>(), (Entity e) => bags.Add(e));
        Assert.Equal("akm_rifle", Assert.Single(world.Get<BelongingsBag>(Assert.Single(bags)).Contents.Items).Prefab);
    }

    [Fact]
    public async Task YouLogInOnTheMapYouLeftFromAndTravelRemembersEachMap()
    {
        var rig = NewRig("default", "speedway");
        string landing = rig.Maps.DefaultMapId;
        string other = landing == "default" ? "speedway" : "default";

        var alice = await rig.Arrive("alice");
        Assert.Equal(landing, alice.CurrentMapId);
        int pistolsHere = rig.Made(landing, "glock_pistol").Count;
        Assert.True(rig.Hands.Give(alice, "glock_pistol", 1, out _, out _, out string m), m);
        var world = rig.World(alice);
        var here = world.Get<Transform>(alice.Entity).Position;

        // To the other map: the pistol comes too.
        rig.Server.MoveToMap(alice, other);
        rig.Server.DrainCommandBuffer();
        rig.Server.HandlePlayerReady(alice.ConnectionId);
        rig.Server.DrainCommandBuffer();
        Assert.Equal(other, alice.CurrentMapId);
        Assert.NotNull(HandsService.Holding(rig.World(alice), alice.Entity, rig.Lookup(alice)).Right);
        Assert.Equal(pistolsHere, rig.Made(landing, "glock_pistol").Count);   // not left behind

        // Somewhere on it, then log out there.
        var otherWorld = rig.World(alice);
        Assert.True(rig.Maps.TryGetMap(other, out _, out _, out var grid, out _));
        Assert.True(rig.Maps.TryGetMapData(other, out var data));
        var spot = rig.Maps.GetSpawnPoint(other).Position + new Vector3(2f, 0f, 0f);
        Assert.True(PlayerStore.IsSafe(otherWorld, grid, data, spot, out var standing), "the test's spot is not somewhere to stand");
        otherWorld.Get<Transform>(alice.Entity).Position = standing;
        rig.LogOut(alice);

        // Logging in lands on the map she left from, where she stood.
        alice = await rig.Arrive("alice");
        Assert.Equal(other, alice.CurrentMapId);
        Assert.True(Vector3.Distance(standing, rig.World(alice).Get<Transform>(alice.Entity).Position) < 0.01f);

        // And travelling back puts her where she was on the first map.
        rig.Server.MoveToMap(alice, landing);
        rig.Server.DrainCommandBuffer();
        rig.Server.HandlePlayerReady(alice.ConnectionId);
        rig.Server.DrainCommandBuffer();
        Assert.True(Vector3.Distance(here with { Y = 0 }, rig.World(alice).Get<Transform>(alice.Entity).Position with { Y = 0 }) < 0.01f);
        Assert.NotNull(HandsService.Holding(rig.World(alice), alice.Entity, rig.Lookup(alice)).Right);
    }

    [Fact]
    public void StatsAndFieldsANewerServerWroteAreKept()
    {
        var state = PlayerState.Parse("""{"Map":"city","Stats":{"kills":3,"longestShot":412.5},"Lives":2}""");
        state.Stats["deaths"] = 1;
        var again = PlayerState.Parse(state.ToJson());
        Assert.Equal(3, again.Stats["kills"]);
        Assert.Equal(412.5, again.Stats["longestShot"]);
        Assert.Equal(1, again.Stats["deaths"]);
        Assert.Contains("\"Lives\":2", again.ToJson());
        Assert.Null(PlayerState.Parse("not json").Map);   // a broken record is a fresh one
    }

    // ── Where a new player lands on the city ────────────────────────────────────────────────────

    /// <summary>
    /// On a pavement, not in the road (Cody, 2026-10-04: "change the default spawn point for players to
    /// be on a sidewalk somewhere like 60 122 1"). His x east, y north, z height is engine (60, _, 122).
    /// </summary>
    [Fact]
    public void ANewPlayerOnTheCityLandsOnTheFoundryStreetPavementAndNotInTheRoad()
    {
        var maps = new MapManager(new MapRepository(Path.Combine(AppContext.BaseDirectory, "maps")),
                                  new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs")));
        maps.Initialize();
        Assert.True(maps.TryGetMap("city", out var world, out _, out var grid, out _));
        Assert.True(maps.TryGetMapData("city", out var data));
        var spawn = maps.GetSpawnPoint("city").Position;
        Assert.Equal(PlayerCoordinates.ToWorld(60f, 122f, spawn.Y), spawn);

        float ground = PhysicsUtils.GetGroundHeight(world, grid, spawn, out string material);
        Assert.Equal("Concrete", material);
        Assert.InRange(ground, 0.1f, 0.13f);                   // the pavement's top, not the road's 0.05
        Assert.InRange(spawn.Y - ground, 0.99f, 1.01f);        // MapManager stands a spawn a metre over its floor
        Assert.False(MovementSystem.CheckCollision(world, grid, spawn, PhysicsConstants.PlayerRadius, PhysicsConstants.PlayerHeight));
        Assert.True(PlayerStore.IsSafe(world, grid, data, spawn, out _));

        // What is under it, by name: the pavement, and no carriageway within a body's width.
        var under = new List<string>();
        world.Query(new QueryDescription().WithAll<Transform, ColliderComponent, IdentityComponent>(),
            (Entity e, ref Transform t, ref ColliderComponent c, ref IdentityComponent id) =>
            {
                var half = c.Size / 2f;
                if (MathF.Abs(spawn.X - t.Position.X) <= half.X + PhysicsConstants.PlayerRadius
                    && MathF.Abs(spawn.Z - t.Position.Z) <= half.Z + PhysicsConstants.PlayerRadius
                    && t.Position.Y - half.Y < 1f)
                    under.Add($"{id.PrefabId}: {id.Name}");
            });
        Assert.Contains(under, u => u.StartsWith("concrete_floor") && u.Contains("sidewalk", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(under, u => u.StartsWith("asphalt_road"));
    }

    private static float YawOf(Quaternion q)
    {
        var f = Vector3.Transform(Vector3.UnitZ, q);
        return MathF.Atan2(f.X, f.Z);
    }

    // ── The database ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheAccountsDatabaseAsItWasYesterdayIsUpgradedAndKeepsItsUsers()
    {
        // The Users table exactly as the 2026-10-03 server made it, with an account that has been used.
        string path = Path.Combine(_dir, "live.db");
        using (var conn = new SqliteConnection($"Data Source={path}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE "Users" (
                    "Username" TEXT NOT NULL CONSTRAINT "PK_Users" PRIMARY KEY,
                    "PasswordHash" TEXT NOT NULL,
                    "Role" TEXT NOT NULL,
                    "CreatedUtc" TEXT NULL,
                    "LastLoginUtc" TEXT NULL,
                    "LastLoginAddress" TEXT NULL,
                    "FailedLogins" INTEGER NOT NULL DEFAULT 0,
                    "LastFailedUtc" TEXT NULL,
                    "LastFailedAddress" TEXT NULL,
                    "RealName" TEXT NULL,
                    "Permissions" TEXT NULL,
                    "CustomRole" TEXT NULL
                );
                INSERT INTO "Users" ("Username", "PasswordHash", "Role", "RealName", "Permissions", "CustomRole")
                VALUES ('seanterry01', $hash, 'Moderator', 'Sean', 'kick', 'builder');
                """;
            cmd.Parameters.AddWithValue("$hash", BCrypt.Net.BCrypt.HashPassword("his secret", 4));
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var repo = new SqliteUserRepository(Db("live.db"), workFactor: 4);

        Assert.True(repo.VerifyPassword("seanterry01", "his secret"));
        var sean = repo.GetUser("seanterry01")!;
        Assert.Equal(UserRole.Moderator, sean.Role);
        Assert.Equal("Sean", sean.RealName);
        Assert.Equal("kick", sean.Permissions);
        Assert.Equal("builder", sean.CustomRole);
        Assert.Null(sean.PlayerState);
        Assert.Null(sean.Belongings);

        Assert.True(repo.SavePlayer("seanterry01", "{\"Map\":\"city\"}", "{\"Items\":[]}"));
        Assert.Equal("{\"Items\":[]}", repo.TakeBelongings("seanterry01"));
        Assert.Null(repo.TakeBelongings("seanterry01"));
        Assert.Equal("{\"Map\":\"city\"}", repo.GetUser("seanterry01")!.PlayerState);
        Assert.False(repo.SavePlayer("nobody", "{}", null));

        Assert.Single(Directory.GetFiles(_dir, "live.db.before-*"));
        SqliteConnection.ClearAllPools();
        _ = new SqliteUserRepository(Db("live.db"), workFactor: 4);
        Assert.Single(Directory.GetFiles(_dir, "live.db.before-*"));
    }
}

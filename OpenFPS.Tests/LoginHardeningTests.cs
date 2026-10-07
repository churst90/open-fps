using System.Reflection;
using System.Text;
using MemoryPack;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;

namespace OpenFPS.Tests;

/// <summary>
/// The login and account path, hardened on 2026-10-02 for a server on the open internet.
///
/// Rate limits with their rates pinned (the 2026-10-01 mutation run turned the refill's `/ 1000` into
/// `* 1000` and nothing noticed, which would have made the login limit no limit), the table that
/// holds them bounded, the per-name lockout, the rules for a new account, an older accounts database
/// upgraded in place, one session per account, and nothing but a login heard from a connection that
/// has not logged in.
///
/// Not tested here, and checked by reading: the connection caps inside LiteNetLib's connection
/// request (the decision itself, <see cref="NetworkService.Refusal"/>, is tested), the voice sender
/// overwrite and the map-data check (both need a live peer), and that an unknown name costs a bcrypt
/// verification (timing is too noisy to assert on).
/// </summary>
public class LoginHardeningTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "openfps-login-" + Guid.NewGuid().ToString("N"));

    public LoginHardeningTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private DbContextOptions<AppDbContext> Db(string name = "accounts.db") =>
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={Path.Combine(_dir, name)}").Options;

    /// <summary>bcrypt at its lowest cost: the tests are about the rules, not the hashing time.</summary>
    private SqliteUserRepository Repo(string name = "accounts.db") => new(Db(name), workFactor: 4);

    private sealed class Clock
    {
        public long Ms;
        public DateTime Utc = new(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);
        public void Advance(TimeSpan by) { Ms += (long)by.TotalMilliseconds; Utc += by; }
    }

    // ── The rate limiter ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheLoginLimitIsABurstOfSixThenOneEveryFiveSeconds()
    {
        var clock = new Clock();
        var auth = new AuthService(Repo(), () => clock.Utc, () => clock.Ms);

        for (int i = 0; i < 6; i++) Assert.True(auth.Admit("198.51.100.7"), $"attempt {i + 1} of the burst");
        Assert.False(auth.Admit("198.51.100.7"));

        clock.Advance(TimeSpan.FromMilliseconds(4999));
        Assert.False(auth.Admit("198.51.100.7"));     // * 1000 instead of / 1000 lets this through
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(auth.Admit("198.51.100.7"));
        Assert.False(auth.Admit("198.51.100.7"));

        // A minute idle does not bank more than the burst.
        clock.Advance(TimeSpan.FromMinutes(10));
        for (int i = 0; i < 6; i++) Assert.True(auth.Admit("198.51.100.7"));
        Assert.False(auth.Admit("198.51.100.7"));
    }

    [Fact]
    public void TheNewAccountLimitIsThreeThenOneEveryTwentyMinutes()
    {
        var clock = new Clock();
        var auth = new AuthService(Repo(), () => clock.Utc, () => clock.Ms);

        for (int i = 0; i < 3; i++)
            Assert.True(auth.CheckRegistration("198.51.100.7", $"newcomer{i}", "long enough").Success);
        var fourth = auth.CheckRegistration("198.51.100.7", "newcomer3", "long enough");
        Assert.False(fourth.Success);
        Assert.Equal("Too many new accounts from your address. Try again later.", fourth.Message);

        clock.Advance(TimeSpan.FromMinutes(19));
        Assert.False(auth.CheckRegistration("198.51.100.7", "newcomer3", "long enough").Success);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(auth.CheckRegistration("198.51.100.7", "newcomer3", "long enough").Success);

        // Another address has its own allowance.
        Assert.True(auth.CheckRegistration("203.0.113.1", "elsewhere", "long enough").Success);
    }

    [Fact]
    public void BucketsThatHaveRefilledArePrunedOnceAMinute()
    {
        long now = 0;
        var limiter = new RateLimiter(capacity: 6, refillPerSecond: 0.2, () => now);
        for (int i = 0; i < 300; i++) Assert.True(limiter.TryConsume($"10.0.{i / 256}.{i % 256}"));
        for (int i = 0; i < 6; i++) limiter.TryConsume("10.9.9.9");   // this one is spent
        Assert.Equal(301, limiter.Count);

        now = 59_000;
        limiter.TryConsume("10.8.8.8");
        Assert.Equal(302, limiter.Count);         // not a minute since the last prune

        now = 61_000;
        limiter.TryConsume("10.8.8.8");
        // Everything that has refilled is gone; what is left is the key just used. The spent one
        // refilled too (a minute is twelve tokens at this rate), so it went with the rest.
        Assert.Equal(1, limiter.Count);
    }

    [Fact]
    public void ABucketStillShortOfItsBurstSurvivesThePrune()
    {
        long now = 0;
        var limiter = new RateLimiter(capacity: 3, refillPerSecond: 0.001, () => now);
        for (int i = 0; i < 300; i++) limiter.TryConsume($"10.0.{i / 256}.{i % 256}");
        for (int i = 0; i < 3; i++) limiter.TryConsume("10.9.9.9");

        now = 61_000;
        limiter.TryConsume("10.8.8.8");
        Assert.False(limiter.TryConsume("10.9.9.9"));    // pruning did not hand it a fresh burst
    }

    [Fact]
    public void TheTableHasACeilingAndNewKeysWaitWhileItIsFull()
    {
        long now = 0;
        var limiter = new RateLimiter(capacity: 1, refillPerSecond: 0.0001, () => now);
        for (int i = 0; i < RateLimiter.MaxKeys; i++) limiter.TryConsume("k" + i);
        Assert.Equal(RateLimiter.MaxKeys, limiter.Count);

        Assert.False(limiter.TryConsume("one-too-many"));
        Assert.Equal(RateLimiter.MaxKeys, limiter.Count);

        // Once they have refilled, a full table makes room at once rather than waiting a minute.
        now = 20_000_000;
        Assert.True(limiter.TryConsume("one-too-many"));
        Assert.True(limiter.Count < RateLimiter.MaxKeys);
    }

    [Theory]
    [InlineData("2001:db8:1:2:3:4:5:6", "2001:db8:1:2:ffff:ffff:ffff:1", true)]   // one /64 is one source
    [InlineData("2001:db8:1:2::1", "2001:db8:1:3::1", false)]                     // the next /64 is not
    [InlineData("::ffff:192.0.2.1", "192.0.2.1", true)]                           // dual-stack IPv4
    [InlineData("192.0.2.1", "192.0.2.2", false)]
    public void AnIPv6HostCountsByItsSlash64(string a, string b, bool same)
        => Assert.Equal(same, RateLimiter.AddressKey(a) == RateLimiter.AddressKey(b));

    [Fact]
    public void ThrottledListsOnlyWhatIsRefusedNowWithItsWait()
    {
        long now = 0;
        var limiter = new RateLimiter(capacity: 1, refillPerSecond: 0.2, () => now);
        limiter.TryConsume("192.0.2.1");
        limiter.TryConsume("192.0.2.2");
        now = 6000;
        limiter.TryConsume("192.0.2.2");
        now = 7000;

        var (key, wait) = Assert.Single(limiter.Throttled());
        Assert.Equal("192.0.2.2", key);
        Assert.Equal(4.0, wait, 3);
    }

    // ── Logging in ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AWrongPasswordAndAnUnknownNameGetTheSameAnswer()
    {
        var repo = Repo();
        Assert.True(repo.AddUser("alice", "correct horse", UserRole.Player));
        var auth = new AuthService(repo);

        var wrong = auth.Check("192.0.2.1", "alice", "battery staple");
        var unknown = auth.Check("192.0.2.1", "nobody-here", "battery staple");

        Assert.False(wrong.Success);
        Assert.False(unknown.Success);
        Assert.Equal(wrong.Message, unknown.Message);
        Assert.Equal(AuthService.InvalidCredentials, wrong.Message);
    }

    [Fact]
    public void ALoginIsRecordedAndAFailureCountedUntilTheNextSuccess()
    {
        var clock = new Clock();
        var repo = Repo();
        Assert.True(repo.AddUser("alice", "correct horse", UserRole.Player));
        var auth = new AuthService(repo, () => clock.Utc, () => clock.Ms);

        Assert.True(auth.Check("192.0.2.1", "Alice", "correct horse").Success);
        var user = repo.GetUser("alice")!;
        Assert.Equal(clock.Utc, user.LastLoginUtc);
        Assert.Equal("192.0.2.1", user.LastLoginAddress);
        Assert.NotNull(user.CreatedUtc);

        clock.Advance(TimeSpan.FromMinutes(5));
        auth.Check("203.0.113.9", "alice", "nope");
        auth.Check("203.0.113.9", "alice", "nope again");
        user = repo.GetUser("alice")!;
        Assert.Equal(2, user.FailedLogins);
        Assert.Equal("203.0.113.9", user.LastFailedAddress);
        Assert.Equal(clock.Utc, user.LastFailedUtc);

        Assert.True(auth.Check("192.0.2.1", "alice", "correct horse").Success);
        Assert.Equal(0, repo.GetUser("alice")!.FailedLogins);
    }

    [Theory]
    [InlineData("", "password")]
    [InlineData("   ", "password")]
    [InlineData("alice", "")]
    [InlineData("ali\nce", "correct horse")]       // a newline would forge a log line
    [InlineData("alice\u0000", "correct horse")]
    public void AMalformedLoginIsRefusedLikeAnyOther(string user, string password)
    {
        var repo = Repo();
        repo.AddUser("alice", "correct horse", UserRole.Player);
        var outcome = new AuthService(repo).Check("192.0.2.1", user, password);
        Assert.False(outcome.Success);
        Assert.Equal(AuthService.InvalidCredentials, outcome.Message);
    }

    [Fact]
    public void AnOverlongLoginIsNotLookedUp()
    {
        var outcome = new AuthService(Repo()).Check("192.0.2.1", new string('a', AuthService.MaxLoginFieldLength + 1), "pw");
        Assert.Equal(AuthService.InvalidCredentials, outcome.Message);
    }

    [Fact]
    public void TenWrongPasswordsLockTheNameForFifteenMinutes()
    {
        var clock = new Clock();
        var repo = Repo();
        Assert.True(repo.AddUser("alice", "correct horse", UserRole.Player));
        var auth = new AuthService(repo, () => clock.Utc, () => clock.Ms);

        // From all over the place, so the per-address limit is not what stops it.
        for (int i = 0; i < AuthService.LockoutAfterFailures - 1; i++)
            Assert.Equal(AuthService.InvalidCredentials, auth.Check($"198.51.100.{i}", "alice", "guess" + i).Message);
        Assert.Empty(auth.LockedNames());
        auth.Check("198.51.100.200", "alice", "one more guess");

        var locked = auth.Check("198.51.100.201", "alice", "correct horse");
        Assert.False(locked.Success);
        Assert.Equal("Too many failed logins for that name. Try again in 15 minutes.", locked.Message);
        Assert.Equal("alice", Assert.Single(auth.LockedNames()).Name);

        clock.Advance(TimeSpan.FromMinutes(14));
        Assert.Equal("Too many failed logins for that name. Try again in 1 minute.",
                     auth.Check("198.51.100.201", "alice", "correct horse").Message);

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(auth.Check("198.51.100.201", "alice", "correct horse").Success);
        // And a success forgets the failures, so the next mistake is the first of ten again.
        Assert.Equal((0, (DateTime?)null), auth.StrikesFor("alice"));
    }

    [Fact]
    public void TheOwnersOwnAddressIsNotLockedOutByAStrangersGuessing()
    {
        var repo = Repo();
        Assert.True(repo.AddUser("alice", "correct horse", UserRole.Player));
        var auth = new AuthService(repo);
        Assert.True(auth.Check("192.0.2.1", "alice", "correct horse").Success);   // alice, at home

        for (int i = 0; i < AuthService.LockoutAfterFailures; i++) auth.Check("203.0.113.66", "alice", "guess" + i);

        Assert.False(auth.Check("203.0.113.67", "alice", "correct horse").Success);   // locked to the world
        Assert.True(auth.Check("192.0.2.1", "alice", "correct horse").Success);       // not to her
    }

    [Fact]
    public void AnUnknownNameLocksTooSoTheLockSaysNothingAboutWhetherItExists()
    {
        var auth = new AuthService(Repo());
        for (int i = 0; i < AuthService.LockoutAfterFailures; i++) auth.Check("203.0.113.66", "ghost", "guess" + i);
        Assert.StartsWith("Too many failed logins for that name.", auth.Check("203.0.113.66", "ghost", "x").Message);
    }

    [Fact]
    public void UnlockLiftsTheLock()
    {
        var repo = Repo();
        repo.AddUser("alice", "correct horse", UserRole.Player);
        var auth = new AuthService(repo);
        for (int i = 0; i < AuthService.LockoutAfterFailures; i++) auth.Check("203.0.113.66", "alice", "guess" + i);

        Assert.True(auth.Unlock("ALICE"));
        Assert.True(auth.Check("203.0.113.67", "alice", "correct horse").Success);
        Assert.False(auth.Unlock("alice"));
    }

    // ── New accounts ────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("", "A username is required.")]
    [InlineData("ab", "A username must be 3 to 20 characters.")]
    [InlineData("abcdefghijklmnopqrstu", "A username must be 3 to 20 characters.")]
    [InlineData("two words", "A username may use only letters, digits, hyphens and underscores.")]
    [InlineData("tab\there", "A username may use only letters, digits, hyphens and underscores.")]
    [InlineData("Ìstanbul", "A username may use only letters, digits, hyphens and underscores.")]
    [InlineData("_lead", "A username must start with a letter or a digit.")]
    [InlineData("Server", "That username is reserved.")]
    [InlineData("ADMIN", "That username is reserved.")]
    public void ANewUsernameMustBePlain(string name, string problem)
        => Assert.Equal(problem, AuthService.ProblemWithNewUsername(name));

    [Theory]
    [InlineData("cody")]
    [InlineData("Cody_Thurst-2")]
    [InlineData("  padded  ")]            // trimmed, as the store folds it
    public void APlainUsernameIsFine(string name) => Assert.Null(AuthService.ProblemWithNewUsername(name));

    [Fact]
    public void ANewPasswordHasALengthAndNoControlCharacters()
    {
        Assert.Equal("A password is required.", AuthService.ProblemWithNewPassword(""));
        Assert.Equal("A password must be at least 8 characters.", AuthService.ProblemWithNewPassword("short"));
        Assert.Null(AuthService.ProblemWithNewPassword(new string('x', 72)));
        Assert.StartsWith("A password can be at most 72 bytes", AuthService.ProblemWithNewPassword(new string('x', 73)));
        // Bytes, not characters: bcrypt reads the UTF-8.
        Assert.StartsWith("A password can be at most 72 bytes", AuthService.ProblemWithNewPassword(new string('é', 40)));
        Assert.Equal("A password cannot contain control characters.", AuthService.ProblemWithNewPassword("pass\u0007word"));
        Assert.Equal("A password cannot be the same as the username.", AuthService.ProblemWithNewPassword("Cody1234", "cody1234"));
    }

    [Fact]
    public void ADuplicateIsCaughtWhateverItsCase()
    {
        var auth = new AuthService(Repo());
        Assert.True(auth.Register("192.0.2.1", "Newcomer", "long enough").Success);
        var again = auth.Register("192.0.2.2", "NEWCOMER", "long enough");
        Assert.False(again.Success);
        Assert.Equal("That username is already taken.", again.Message);
    }

    // ── The accounts database ───────────────────────────────────────────────────────────────────

    [Fact]
    public void AnOlderAccountsDatabaseIsUpgradedInPlaceAndKeepsItsUsers()
    {
        // The table exactly as the first release's EnsureCreated made it, with an account in it.
        string path = Path.Combine(_dir, "old.db");
        using (var conn = new SqliteConnection($"Data Source={path}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE "Users" (
                    "Username" TEXT NOT NULL CONSTRAINT "PK_Users" PRIMARY KEY,
                    "PasswordHash" TEXT NOT NULL,
                    "Role" TEXT NOT NULL
                );
                INSERT INTO "Users" VALUES ('admin', $admin, 'Admin');
                INSERT INTO "Users" VALUES ('friend', $friend, 'Player');
                """;
            cmd.Parameters.AddWithValue("$admin", BCrypt.Net.BCrypt.HashPassword("admin secret", 4));
            cmd.Parameters.AddWithValue("$friend", BCrypt.Net.BCrypt.HashPassword("friend secret", 4));
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var repo = Repo("old.db");

        Assert.True(repo.VerifyPassword("friend", "friend secret"));
        Assert.True(repo.VerifyPassword("admin", "admin secret"));
        var friend = repo.GetUser("friend")!;
        Assert.Equal(UserRole.Player, friend.Role);
        Assert.Null(friend.CreatedUtc);              // made before dates were kept
        Assert.Equal(0, friend.FailedLogins);

        repo.RecordLogin("friend", "192.0.2.5", new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc));
        repo.RecordFailedLogin("friend", "203.0.113.1", new DateTime(2026, 10, 2, 10, 0, 0, DateTimeKind.Utc));
        Assert.True(repo.SetRealName("friend", "A Friend"));
        friend = repo.GetUser("friend")!;
        Assert.Equal("192.0.2.5", friend.LastLoginAddress);
        Assert.Equal(1, friend.FailedLogins);
        Assert.Equal("A Friend", friend.RealName);

        // A copy of the file as it was, from before anything changed.
        string backup = Assert.Single(Directory.GetFiles(_dir, "old.db.before-*"));
        using (var conn = new SqliteConnection($"Data Source={backup};Mode=ReadOnly"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM \"Users\";";
            Assert.Equal(2L, (long)cmd.ExecuteScalar()!);
        }

        // A second start finds nothing to do and makes no second copy.
        SqliteConnection.ClearAllPools();
        _ = Repo("old.db");
        Assert.Single(Directory.GetFiles(_dir, "old.db.before-*"));
    }

    [Fact]
    public void AHashMadeAtALowerCostIsRehashedAtTheCurrentOneOnLogin()
    {
        var cheap = new SqliteUserRepository(Db(), workFactor: 4);
        Assert.True(cheap.AddUser("alice", "correct horse", UserRole.Player));
        Assert.StartsWith("$2a$04$", cheap.GetUser("alice")!.PasswordHash);

        var current = new SqliteUserRepository(Db(), workFactor: 5);
        Assert.False(current.VerifyPassword("alice", "wrong"));
        Assert.StartsWith("$2a$04$", current.GetUser("alice")!.PasswordHash);   // only a right password rehashes
        Assert.True(current.VerifyPassword("alice", "correct horse"));
        Assert.StartsWith("$2a$05$", current.GetUser("alice")!.PasswordHash);
        Assert.True(current.VerifyPassword("alice", "correct horse"));
    }

    // ── Sessions ────────────────────────────────────────────────────────────────────────────────

    private (GameServer Server, SessionManager Sessions, List<(UserSession To, IMessage What)> Sent) Server(SqliteUserRepository repo)
    {
        string mapDir = Path.Combine(_dir, "maps");
        Directory.CreateDirectory(mapDir);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "maps", "default.json"), Path.Combine(mapDir, "default.json"), overwrite: true);
        var maps = new MapManager(new MapRepository(mapDir), new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs")));
        maps.Initialize();
        var sessions = new SessionManager();
        var server = new GameServer(repo);
        server.Attach(maps, sessions);
        var sent = new List<(UserSession, IMessage)>();
        server.Sent = (to, what) => sent.Add((to, what));
        return (server, sessions, sent);
    }

    private static async Task<List<IMessage>> LogIn(GameServer server, int connection, string user, string password)
    {
        var replies = new List<IMessage>();
        await server.Login(connection, new LoginRequest { Username = user, Password = password }, replies.Add)
            .Within(TestDeadline.Login, $"{user}'s login");
        server.DrainCommandBuffer();
        return replies;
    }

    [Fact]
    public async Task ALoginMakesASessionThatKnowsWhereItCameFrom()
    {
        var repo = Repo();
        repo.AddUser("alice", "correct horse", UserRole.Player);
        var (server, sessions, _) = Server(repo);

        var replies = await LogIn(server, 10001, "alice", "correct horse");

        Assert.True(Assert.IsType<LoginResponse>(replies[0]).Success);
        Assert.True(sessions.TryGetSession(10001, out var session));
        Assert.Equal("alice", session.Username);
        Assert.True(session.IsTextClient);
        Assert.Equal("conn:10001", session.RemoteAddress);   // no transport in a test, so the fallback
        Assert.True(DateTime.UtcNow - session.LoggedInUtc < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task ASecondLoginOnTheSameConnectionIsRefusedAndTheFirstStands()
    {
        var repo = Repo();
        repo.AddUser("alice", "correct horse", UserRole.Player);
        repo.AddUser("bob", "battery staple", UserRole.Admin);
        var (server, sessions, _) = Server(repo);
        await LogIn(server, 10001, "alice", "correct horse");

        var replies = await LogIn(server, 10001, "bob", "battery staple");

        var refused = Assert.IsType<LoginResponse>(Assert.Single(replies));
        Assert.False(refused.Success);
        Assert.Equal("You are already logged in as alice.", refused.Message);
        Assert.True(sessions.TryGetSession(10001, out var session));
        Assert.Equal("alice", session.Username);
        Assert.Equal(UserRole.Player, session.Role);
    }

    [Fact]
    public async Task TheSameAccountLoggingInAgainTakesOverAndTheOldSessionIsToldWhy()
    {
        var repo = Repo();
        repo.AddUser("alice", "correct horse", UserRole.Player);
        var (server, sessions, sent) = Server(repo);
        await LogIn(server, 10001, "alice", "correct horse");
        Assert.True(sessions.TryGetSession(10001, out var first));

        await LogIn(server, 10002, "ALICE", "correct horse");

        Assert.False(sessions.TryGetSession(10001, out _));
        Assert.True(sessions.TryGetSession(10002, out _));
        Assert.Single(sessions.GetAllSessions());
        Assert.Contains(sent, s => s.To == first && s.What is TextEvent t && t.Text.Contains("logged in from somewhere else"));
    }

    [Fact]
    public async Task AFailedLoginMakesNoSession()
    {
        var repo = Repo();
        repo.AddUser("alice", "correct horse", UserRole.Player);
        var (server, sessions, _) = Server(repo);

        var replies = await LogIn(server, 10001, "alice", "wrong horse");

        Assert.Equal(AuthService.InvalidCredentials, Assert.IsType<LoginResponse>(Assert.Single(replies)).Message);
        Assert.True(sessions.IsEmpty);
    }

    [Fact]
    public async Task RegistrationThroughTheServerFollowsTheRules()
    {
        var (server, _, _) = Server(Repo());
        async Task<RegisterResponse> Register(string user, string password)
        {
            var replies = new List<IMessage>();
            await server.Register(10001, new RegisterRequest { Username = user, Password = password }, replies.Add)
                .Within(TestDeadline.Login, $"{user}'s registration");
            server.DrainCommandBuffer();
            return Assert.IsType<RegisterResponse>(Assert.Single(replies));
        }

        Assert.Equal("A password must be at least 8 characters.", (await Register("newcomer", "short")).Message);
        Assert.True((await Register("newcomer", "long enough")).Success);
        Assert.Equal("That username is already taken.", (await Register("Newcomer", "long enough")).Message);
    }

    [Fact]
    public void AConnectionThatNeverLogsInIsClosedAfterTwoMinutes()
    {
        var now = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);
        var connections = new[]
        {
            (1, now - TimeSpan.FromMinutes(5)),     // logged in long ago: stays
            (2, now - TimeSpan.FromMinutes(3)),     // never logged in: goes
            (3, now - TimeSpan.FromSeconds(30)),    // still on the login form: stays
        };
        Assert.Equal(new[] { 2 }, GameServer.StaleUnauthenticated(connections, id => id == 1, now));
    }

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(10, NetworkService.MaxPeersPerAddress - 1, true)]
    [InlineData(10, NetworkService.MaxPeersPerAddress, false)]
    [InlineData(NetworkService.MaxPeers, 0, false)]
    public void ConnectionsAreCappedPerAddressAndOverall(int open, int fromThere, bool accepted)
        => Assert.Equal(accepted, NetworkService.Refusal(open, fromThere) == null);

    // ── Before login ────────────────────────────────────────────────────────────────────────────

    public static IEnumerable<object[]> EveryMessageType() =>
        typeof(IMessage).GetCustomAttributes<MemoryPackUnionAttribute>().Select(u => new object[] { u.Type.Name });

    [Theory]
    [MemberData(nameof(EveryMessageType))]
    public void NothingButALoginIsHeardBeforeLoggingIn(string typeName)
    {
        var type = typeof(IMessage).Assembly.GetType("OpenFPS.Common.Networking." + typeName)!;
        var dispatcher = new MessageDispatcher { IsAuthenticated = id => id == 1 };
        var handled = new List<(int, Type)>();
        var register = typeof(MessageDispatcher).GetMethod(nameof(MessageDispatcher.RegisterHandler))!.MakeGenericMethod(type);
        var handlerType = typeof(Action<,,>).MakeGenericType(typeof(int), type, typeof(Action<IMessage>));
        var spy = typeof(LoginHardeningTests).GetMethod(nameof(Spy), BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(type);
        register.Invoke(dispatcher, new[] { spy.Invoke(null, new object[] { handled }) });

        var message = (IMessage)Activator.CreateInstance(type)!;
        dispatcher.Dispatch(2, message, _ => { });     // a stranger
        dispatcher.Dispatch(1, message, _ => { });     // somebody logged in

        bool allowed = type == typeof(LoginRequest) || type == typeof(RegisterRequest);
        Assert.Equal(allowed ? 2 : 1, handled.Count);
        Assert.Contains((1, type), handled);
        if (!allowed) Assert.Equal(1, dispatcher.RefusedBeforeLogin);
    }

    private static Action<int, T, Action<IMessage>> Spy<T>(List<(int, Type)> into) where T : IMessage
        => (id, _, _) => into.Add((id, typeof(T)));

    [Fact]
    public void ACommandBeforeLoginIsAnsweredAndInputIsNot()
    {
        var dispatcher = new MessageDispatcher { IsAuthenticated = _ => false };
        var replies = new List<IMessage>();
        dispatcher.Dispatch(2, new TextCommand { Command = "ready" }, replies.Add);
        dispatcher.Dispatch(2, new ClientInputUpdate(), replies.Add);
        dispatcher.Dispatch(2, new VoiceData(), replies.Add);
        Assert.StartsWith("You are not logged in.", Assert.IsType<TextEvent>(Assert.Single(replies)).Text);
    }

    // ── The MUD gateway's reading and its log ───────────────────────────────────────────────────

    [Fact]
    public async Task AnOverlongLineIsThrownAwayAsItArrivesAndTheNextLineIsRead()
    {
        string huge = new string('x', 1_000_000);
        var reader = new BoundedLineReader(new StringReader($"look\r\n{huge}\nscan\r\npartial"));

        Assert.Equal(("look", false), await reader.ReadLineAsync(512));
        Assert.Equal(("", true), await reader.ReadLineAsync(512));
        Assert.Equal(("scan", false), await reader.ReadLineAsync(512));
        Assert.Equal(("partial", false), await reader.ReadLineAsync(512));
        Assert.Equal(((string?)null, false), await reader.ReadLineAsync(512));
    }

    [Fact]
    public async Task ALineOfExactlyTheLimitIsKept()
    {
        var reader = new BoundedLineReader(new StringReader(new string('y', 512) + "\n" + new string('z', 513) + "\n"));
        Assert.Equal((new string('y', 512), false), await reader.ReadLineAsync(512));
        Assert.True((await reader.ReadLineAsync(512)).TooLong);
    }

    [Fact]
    public void AMudLoginNeverReachesTheLogWithItsPassword()
    {
        string logged = MudGateway.Redact("login alice correct-horse");
        Assert.DoesNotContain("correct-horse", logged);
        Assert.Contains("alice", logged);
        Assert.Equal("scan", MudGateway.Redact("scan"));
    }

    [Fact]
    public void NetworkTextIsOneLogLineAndShort()
    {
        Assert.Equal("alice?FORGED LINE", AuthService.ForLog("alice\nFORGED LINE"));
        Assert.Equal(new string('a', 64) + "…", AuthService.ForLog(new string('a', 500)));
    }
}

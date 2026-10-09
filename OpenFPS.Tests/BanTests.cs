using System.Numerics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;

namespace OpenFPS.Tests;

/// <summary>
/// /ban, /unban and /bans (Cody, 2026-10-09: "definitely for banning players"): who may ban whom, the
/// login refused with the reason and the end, a player on the server thrown out at once, bans that end
/// lifting themselves, and an older accounts database taking the new columns in place.
/// </summary>
public class BanTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "openfps-ban-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteUserRepository _users;
    private readonly MapManager _maps;
    private readonly SessionManager _sessions = new();
    private readonly GameServer _server;
    private readonly CommandHandler _commands;
    private readonly List<(string To, IMessage Message)> _sent = new();

    public BanTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "maps"));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "maps", "default.json"), Path.Combine(_dir, "maps", "default.json"));
        _maps = new MapManager(new MapRepository(Path.Combine(_dir, "maps")),
                               new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs")));
        _maps.Initialize();
        _users = Open("accounts.db");
        _server = new GameServer(_users) { Teams = new TeamRepository(Path.Combine(_dir, "teams.json")) };
        _server.Attach(_maps, _sessions);
        _server.Sent = (to, m) => _sent.Add((to.Username, m));
        _commands = new CommandHandler(_sessions, _maps, _server, users: _users);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private SqliteUserRepository Open(string file) => new(
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={Path.Combine(_dir, file)}").Options,
        workFactor: 4);

    private void Account(string name, UserRole role)
    {
        if (!_users.AddUser(name, "long enough", role)) _users.SetRole(name, role);
    }

    private UserSession Online(string name, int id, UserRole role)
    {
        Account(name, role);
        Assert.True(_maps.TryGetMap("default", out var world, out _, out _, out _));
        var entity = world.Create(new Transform { Position = new Vector3(id * 3, 2, 0), Rotation = Quaternion.Identity },
                                  new Velocity(), new PlayerComponent { ConnectionId = id, Username = name, Role = role }, EntityType.Player);
        _maps.IndexEntity("default", entity);
        var session = new UserSession { ConnectionId = id, Username = name, Role = role, Entity = entity, CurrentMapId = "default", Welcomed = true };
        _sessions.AddSession(id, session);
        return session;
    }

    private string Run(UserSession who, string command, params string[] args)
    {
        var replies = new List<string>();
        _commands.HandleTextCommand(who.ConnectionId, new TextCommand { Command = command, Args = args },
            m => { if (m is TextEvent t) replies.Add(t.Text); });
        _server.DrainCommandBuffer();
        return string.Join(" | ", replies);
    }

    private bool IsOn(string name) => _sessions.GetAllSessions().Any(s => s.Username == name);

    // ── Permission ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BanIsTheModeratorsAndAboveAndCanBeGranted()
    {
        Assert.True(Permissions.RoleHas(UserRole.Owner, "ban"));
        Assert.True(Permissions.RoleHas(UserRole.Admin, "ban"));
        Assert.True(Permissions.RoleHas(UserRole.Moderator, "ban"));
        Assert.False(Permissions.RoleHas(UserRole.Dev, "ban"));
        Assert.False(Permissions.RoleHas(UserRole.Player, "ban"));
        Assert.True(Permissions.Grantable("ban"));
        Assert.Equal("ban", Permissions.Canonical("unban"));
        Assert.Equal("ban", Permissions.Canonical("bans"));
    }

    [Fact]
    public void APlayerAndADeveloperAreRefusedAllThree()
    {
        var dev = Online("builder", 1, UserRole.Dev);
        Online("spammer", 2, UserRole.Player);
        foreach (var c in new[] { "ban", "unban", "bans" })
            Assert.Equal("You do not have permission to execute this command.", Run(dev, c, "spammer"));
        Assert.Null(_users.GetUser("spammer")!.BannedUtc);
    }

    // ── Banning ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AModeratorBansAPlayerWhoIsThrownOutWithTheReasonAndTheEnd()
    {
        var mod = Online("modsean", 1, UserRole.Moderator);
        Online("spammer", 2, UserRole.Player);

        string said = Run(mod, "ban", "spammer", "2h", "spamming", "the", "chat");

        Assert.StartsWith("Banned spammer until ", said);
        Assert.EndsWith(": spamming the chat. They were online and have been removed.", said);
        Assert.False(IsOn("spammer"));
        var told = _sent.Where(s => s.To == "spammer").Select(s => s.Message).OfType<TextEvent>().Select(t => t.Text).ToList();
        Assert.Contains(told, t => t.StartsWith("You are banned until ") && t.EndsWith(" UTC: spamming the chat."));
        var record = _users.GetUser("spammer")!;
        Assert.Equal("modsean", record.BannedBy);
        Assert.Equal("spamming the chat", record.BanReason);
        Assert.InRange((record.BannedUntilUtc!.Value - DateTime.UtcNow).TotalMinutes, 119, 121);
    }

    [Fact]
    public void WithoutADurationTheBanLastsUntilLiftedAndAnOfflineAccountCanBeBanned()
    {
        var mod = Online("modsean", 1, UserRole.Moderator);
        Account("absent", UserRole.Player);

        Assert.Equal("Banned absent until lifted: no reason given.", Run(mod, "ban", "absent"));
        var record = _users.GetUser("absent")!;
        Assert.NotNull(record.BannedUtc);
        Assert.Null(record.BannedUntilUtc);
        Assert.True(record.IsBannedAt(DateTime.UtcNow.AddYears(5)));
    }

    [Theory]
    [InlineData("30m", 30)]
    [InlineData("2h", 120)]
    [InlineData("7d", 7 * 24 * 60)]
    [InlineData("4w", 4 * 7 * 24 * 60)]
    [InlineData("90MIN", 90)]
    public void DurationsAreMinutesHoursDaysAndWeeks(string typed, int minutes)
    {
        Assert.True(Bans.TryParseDuration(typed, out var span));
        Assert.Equal(minutes, span.TotalMinutes);
    }

    [Theory]
    [InlineData("spamming")]
    [InlineData("0h")]
    [InlineData("2")]
    [InlineData("-3d")]
    [InlineData("2y")]
    public void AnythingElseIsTheStartOfTheReason(string typed) => Assert.False(Bans.TryParseDuration(typed, out _));

    [Fact]
    public void AnUnknownNameIsSaid()
    {
        var mod = Online("modsean", 1, UserRole.Moderator);
        Assert.Equal("There is no account called nobody.", Run(mod, "ban", "nobody", "1h"));
        Assert.Equal("There is no account called nobody.", Run(mod, "unban", "nobody"));
        Assert.StartsWith("Usage: /ban NAME [DURATION] [REASON]", Run(mod, "ban"));
    }

    /// <summary>The nearest-name match /kick uses must not ban somebody else who happens to be on.</summary>
    [Fact]
    public void AnOfflineAccountIsNotConfusedWithALongerNameThatIsOn()
    {
        var mod = Online("modsean", 1, UserRole.Moderator);
        Account("bob", UserRole.Player);
        Online("bobby", 2, UserRole.Player);

        Assert.Equal("Banned bob until lifted: no reason given.", Run(mod, "ban", "bob"));
        Assert.True(IsOn("bobby"));
        Assert.Null(_users.GetUser("bobby")!.BannedUtc);
    }

    // ── Who may ban whom ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void NobodyBansAnOwnerOrThemselves()
    {
        var owner = Online("cody", 1, UserRole.Owner);
        var boss = Online("boss", 2, UserRole.Admin);
        Assert.Equal("admin is an owner; nobody can ban an owner.", Run(boss, "ban", "admin"));
        Assert.Equal("admin is an owner; nobody can ban an owner.", Run(owner, "ban", "admin"));
        Assert.Equal("You cannot ban yourself.", Run(boss, "ban", "boss"));
        Assert.Equal("You cannot ban yourself.", Run(owner, "ban", "cody"));
        Assert.Null(_users.GetUser("admin")!.BannedUtc);
    }

    [Theory]
    [InlineData(UserRole.Moderator, UserRole.Moderator, false)]
    [InlineData(UserRole.Moderator, UserRole.Dev, false)]
    [InlineData(UserRole.Moderator, UserRole.Admin, false)]
    [InlineData(UserRole.Admin, UserRole.Admin, false)]
    [InlineData(UserRole.Moderator, UserRole.Player, true)]
    [InlineData(UserRole.Admin, UserRole.Moderator, true)]
    [InlineData(UserRole.Admin, UserRole.Dev, true)]
    [InlineData(UserRole.Owner, UserRole.Admin, true)]
    public void OnlyARoleAboveTheTargetsMayBanIt(UserRole banner, UserRole target, bool may)
    {
        var by = Online("banner", 1, banner);
        Account("target", target);   // offline: the account's own role and powers decide

        string said = Run(by, "ban", "target", "1d");

        if (may) Assert.StartsWith("Banned target until ", said);
        else Assert.StartsWith("You cannot ban target", said);
        Assert.Equal(may, _users.GetUser("target")!.BannedUtc != null);
    }

    /// <summary>As /kick: a protected account (an administrator's, or one granted it) only by somebody protected too.</summary>
    [Fact]
    public void AProtectedPlayerIsBannedOnlyBySomebodyProtected()
    {
        var mod = Online("modsean", 1, UserRole.Moderator);
        var guarded = Online("guarded", 2, UserRole.Player);
        guarded.Grants.Add(Permissions.Protected);

        Assert.Equal("You cannot ban guarded.", Run(mod, "ban", "guarded"));
        Assert.True(IsOn("guarded"));
    }

    /// <summary>A player trusted with ban (a grant or a custom role) may ban players who are not.</summary>
    [Fact]
    public void APlayerGrantedBanBansPlayersButNotAnotherWhoHasIt()
    {
        var trusted = Online("trusted", 1, UserRole.Player);
        trusted.Grants.Add("ban");
        Account("plain", UserRole.Player);
        Account("alsotrusted", UserRole.Player);
        _users.SetGrants("alsotrusted", "ban");

        Assert.Equal("Banned plain until lifted: no reason given.", Run(trusted, "ban", "plain"));
        Assert.Equal("You cannot ban alsotrusted: their role is not below yours.", Run(trusted, "ban", "alsotrusted"));
    }

    // ── Logging in ──────────────────────────────────────────────────────────────────────────────

    private static readonly DateTime Now = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ABannedAccountIsRefusedAtLoginWithTheEndAndTheReason()
    {
        Account("spammer", UserRole.Player);
        _users.SetBan("spammer", Now, new DateTime(2026, 10, 14, 18, 0, 0, DateTimeKind.Utc), "modsean", "spamming");
        var auth = new AuthService(_users, () => Now);

        var outcome = auth.Check("192.0.2.1", "spammer", "long enough");

        Assert.False(outcome.Success);
        Assert.Equal("You are banned until 14 October, 18:00 UTC: spamming.", outcome.Message);
    }

    [Fact]
    public void ABanWithoutAnEndOrAReasonSaysSo()
    {
        Account("spammer", UserRole.Player);
        _users.SetBan("spammer", Now, null, "modsean", null);
        var outcome = new AuthService(_users, () => Now).Check("192.0.2.1", "spammer", "long enough");
        Assert.False(outcome.Success);
        Assert.Equal("You are banned: no reason given.", outcome.Message);
    }

    [Fact]
    public void ABanInAnotherYearSaysTheYear()
        => Assert.Equal("You are banned until 2 January 2027, 09:05 UTC: spamming.",
                        Bans.Refusal(new DateTime(2027, 1, 2, 9, 5, 0, DateTimeKind.Utc), "spamming", Now));

    /// <summary>The ban is said only to somebody with the password: to anyone else the account is as any other.</summary>
    [Fact]
    public void AWrongPasswordForABannedAccountIsAnOrdinaryRefusal()
    {
        Account("spammer", UserRole.Player);
        _users.SetBan("spammer", Now, null, "modsean", "spamming");
        var outcome = new AuthService(_users, () => Now).Check("192.0.2.1", "spammer", "wrong guess");
        Assert.Equal(AuthService.InvalidCredentials, outcome.Message);
    }

    [Fact]
    public void ABanPastItsEndLiftsItselfAtTheNextLogin()
    {
        Account("spammer", UserRole.Player);
        _users.SetBan("spammer", Now.AddDays(-2), Now.AddMinutes(-1), "modsean", "spamming");

        var outcome = new AuthService(_users, () => Now).Check("192.0.2.1", "spammer", "long enough");

        Assert.True(outcome.Success, outcome.Message);
        Assert.Null(_users.GetUser("spammer")!.BannedUtc);
        Assert.Empty(_users.Banned());
    }

    // ── Lifting and listing ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void UnbanLiftsItAndTheAccountLogsInAgain()
    {
        var mod = Online("modsean", 1, UserRole.Moderator);
        Account("spammer", UserRole.Player);
        Run(mod, "ban", "spammer", "spamming");

        Assert.Equal("spammer is no longer banned.", Run(mod, "unban", "spammer"));
        Assert.Equal("spammer is not banned.", Run(mod, "unban", "spammer"));
        Assert.True(new AuthService(_users).Check("192.0.2.1", "spammer", "long enough").Success);
    }

    [Fact]
    public void BansListsWhoByWhomWhenUntilAndWhyAndLiftsEndedOnes()
    {
        var mod = Online("modsean", 1, UserRole.Moderator);
        Assert.Equal("Nobody is banned.", Run(mod, "bans"));

        Account("spammer", UserRole.Player);
        Account("griefer", UserRole.Player);
        Account("forgiven", UserRole.Player);
        var now = DateTime.UtcNow;
        var at = new DateTime(now.Year, now.Month, now.Day, 0, 30, 0, DateTimeKind.Utc);
        _users.SetBan("griefer", at, null, "boss", null);
        _users.SetBan("spammer", at, at.AddDays(400), "modsean", "spamming");
        _users.SetBan("forgiven", at.AddDays(-3), now.AddMinutes(-1), "modsean", "long ago");

        string said = Run(mod, "bans");

        var lines = said.Split(" | ");
        Assert.Equal("2 bans:", lines[0]);
        Assert.Equal($"griefer: by boss, {Bans.When(at, now, comma: false)}, until lifted: no reason given.", lines[1]);
        Assert.Equal($"spammer: by modsean, {Bans.When(at, now, comma: false)}, until {Bans.When(at.AddDays(400), now, comma: false)}: spamming.", lines[2]);
        Assert.Equal(3, lines.Length);
        Assert.Null(_users.GetUser("forgiven")!.BannedUtc);
    }

    [Fact]
    public void UserSaysTheBan()
    {
        var owner = Online("cody", 1, UserRole.Owner);
        Account("spammer", UserRole.Player);
        _users.SetBan("spammer", DateTime.UtcNow, null, "modsean", "spamming");
        Assert.EndsWith(" Banned by modsean, " + Bans.When(_users.GetUser("spammer")!.BannedUtc!.Value, DateTime.UtcNow, comma: false)
                        + ", until lifted: spamming.", Run(owner, "user", "spammer"));
    }

    [Fact]
    public void BanningAgainReplacesTheBan()
    {
        var mod = Online("modsean", 1, UserRole.Moderator);
        Account("spammer", UserRole.Player);
        Run(mod, "ban", "spammer", "1h", "first");
        Assert.EndsWith("It replaces the ban they had.", Run(mod, "ban", "spammer", "second"));
        var record = _users.GetUser("spammer")!;
        Assert.Null(record.BannedUntilUtc);
        Assert.Equal("second", record.BanReason);
    }

    // ── The database ────────────────────────────────────────────────────────────────────────────

    /// <summary>The Users table as the 2026-10-09 server made it, before bans: upgraded in place, its rows kept.</summary>
    [Fact]
    public void AnAccountsDatabaseFromBeforeBansIsUpgradedInPlace()
    {
        string path = Path.Combine(_dir, "old.db");
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
                    "CustomRole" TEXT NULL,
                    "PlayerState" TEXT NULL,
                    "Belongings" TEXT NULL
                );
                INSERT INTO "Users" ("Username", "PasswordHash", "Role", "RealName")
                VALUES ('admin', $hash, 'Owner', NULL), ('seanterry01', $hash, 'Moderator', 'Sean');
                """;
            cmd.Parameters.AddWithValue("$hash", BCrypt.Net.BCrypt.HashPassword("his secret", 4));
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var repo = Open("old.db");

        Assert.True(repo.VerifyPassword("seanterry01", "his secret"));
        Assert.Equal("Sean", repo.GetUser("seanterry01")!.RealName);
        Assert.Null(repo.GetUser("seanterry01")!.BannedUtc);
        Assert.True(repo.SetBan("seanterry01", Now, Now.AddDays(1), "admin", "testing"));
        SqliteConnection.ClearAllPools();
        var reopened = Open("old.db").GetUser("seanterry01")!;
        Assert.Equal(Now, reopened.BannedUtc);
        Assert.Equal(Now.AddDays(1), reopened.BannedUntilUtc);
        Assert.Equal("admin", reopened.BannedBy);
        Assert.Equal("testing", reopened.BanReason);
        Assert.Single(Directory.GetFiles(_dir, "old.db.before-*"));
    }
}

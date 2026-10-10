using System.Numerics;
using Arch.Core;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OpenFPS.Client.Services;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;

namespace OpenFPS.Tests;

/// <summary>
/// The Owner role (Cody, 2026-10-09): every permission, and nothing a command can do takes it off the
/// server's last owner or changes it. Also the names in chat: "admin [Mafia] Owner: hello".
/// </summary>
public class OwnerRoleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "openfps-owner-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteUserRepository _users;
    private readonly MapManager _maps;
    private readonly SessionManager _sessions = new();
    private readonly GameServer _server;
    private readonly CommandHandler _commands;

    public OwnerRoleTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "maps"));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "maps", "default.json"), Path.Combine(_dir, "maps", "default.json"));
        _maps = new MapManager(new MapRepository(Path.Combine(_dir, "maps")),
                               new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs")));
        _maps.Initialize();
        _users = Open();
        _server = new GameServer(_users) { Teams = new TeamRepository(Path.Combine(_dir, "teams.json")) };
        _server.Attach(_maps, _sessions);
        _server.Sent = (_, _) => { };
        _commands = new CommandHandler(_sessions, _maps, _server, users: _users);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private SqliteUserRepository Open() => new(
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={Path.Combine(_dir, "accounts.db")}").Options,
        workFactor: 4);

    /// <summary>An account and its session, its role as the store has it (the seeded admin is the owner).</summary>
    private UserSession Online(string name, int id, UserRole role)
    {
        if (!_users.AddUser(name, "long enough", role)) _users.SetRole(name, role);
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

    [Fact]
    public void ANewServersAdminIsTheOwner()
    {
        Assert.Equal(UserRole.Owner, _users.GetUser("admin")!.Role);
        Assert.Equal(new[] { "admin" }, _users.UsernamesWithRole(UserRole.Owner));
    }

    /// <summary>A database from before the Owner role: its admin was an Admin, with a custom role even.</summary>
    [Fact]
    public void AnOlderDatabaseMakesItsAdminTheOwnerOnStart()
    {
        _users.SetRole("admin", UserRole.Admin);
        _users.SetCustomRole("admin", "builder");
        SqliteConnection.ClearAllPools();

        var reopened = Open();
        var admin = reopened.GetUser("admin")!;
        Assert.Equal(UserRole.Owner, admin.Role);
        Assert.Null(admin.CustomRole);
    }

    [Fact]
    public void StartingLeavesAnOwnerThatIsNotTheAdminAlone()
    {
        _users.AddUser("cody", "long enough", UserRole.Owner);
        _users.SetRole("admin", UserRole.Admin);
        SqliteConnection.ClearAllPools();

        var reopened = Open();
        Assert.Equal(UserRole.Admin, reopened.GetUser("admin")!.Role);
        Assert.Equal(UserRole.Owner, reopened.GetUser("cody")!.Role);
    }

    [Fact]
    public void AnOwnerHasEveryPermissionAndAnAdminAllButOwners()
    {
        foreach (var p in Permissions.All)
        {
            Assert.True(Permissions.RoleHas(UserRole.Owner, p), p);
            Assert.Equal(p != Permissions.Owners, Permissions.RoleHas(UserRole.Admin, p));
        }
        Assert.False(Permissions.Grantable(Permissions.Owners));
    }

    [Fact]
    public void AnAdminCannotChangeAnOwnersRoleNorMakeOne()
    {
        var boss = Online("boss", 1, UserRole.Admin);
        Online("friend", 2, UserRole.Player);

        Assert.Equal("admin is an owner; only an owner can change an owner's role.", Run(boss, "setrole", "admin", "player"));
        Assert.Equal("admin is an owner; only an owner can change an owner's role.", Run(boss, "setrole", "admin", "admin"));
        Assert.Equal("Only an owner can make somebody an owner.", Run(boss, "setrole", "friend", "owner"));
        Assert.Equal(UserRole.Owner, _users.GetUser("admin")!.Role);
        Assert.Equal(UserRole.Player, _users.GetUser("friend")!.Role);
    }

    [Fact]
    public void AnAdminCannotGiveTheOwnerACustomRole()
    {
        var boss = Online("boss", 1, UserRole.Admin);
        Run(boss, "role", "create", "builder", "spawn");
        Assert.Equal("admin is an owner; only an owner can change an owner's role.", Run(boss, "setrole", "admin", "builder"));
        Assert.Equal(UserRole.Owner, _users.GetUser("admin")!.Role);
        Assert.Null(_users.GetUser("admin")!.CustomRole);
    }

    [Fact]
    public void AnOwnerMakesAndUnmakesOwnersAndCannotUnmakeThemself()
    {
        var owner = Online("admin", 1, UserRole.Owner);
        var cody = Online("cody", 2, UserRole.Player);

        Assert.Equal("cody is now an owner.", Run(owner, "setrole", "cody", "owner"));
        Assert.Equal(UserRole.Owner, cody.Role);
        Assert.True(cody.Can(Permissions.Owners));
        Assert.Equal("cody is now an administrator.", Run(owner, "setrole", "cody", "admin"));
        Assert.Equal(UserRole.Admin, _users.GetUser("cody")!.Role);
        Assert.Equal("You cannot change your own role.", Run(owner, "setrole", "admin", "player"));
        Assert.Equal(UserRole.Owner, _users.GetUser("admin")!.Role);
    }

    /// <summary>The last owner keeps the role even against a session that still says it is an owner
    /// after its own record was changed by hand.</summary>
    [Fact]
    public void TheLastOwnerKeepsIt()
    {
        _users.AddUser("cody", "long enough", UserRole.Owner);
        var stale = Online("admin", 1, UserRole.Owner);
        _users.SetRole("admin", UserRole.Admin);

        Assert.Equal("cody is the server's last owner. Make somebody else an owner first.", Run(stale, "setrole", "cody", "player"));
        Assert.Equal(UserRole.Owner, _users.GetUser("cody")!.Role);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("add")]
    [InlineData("remove")]
    [InlineData("delete")]
    public void TheOwnerRoleCannotBeMadeChangedOrRemoved(string sub)
    {
        var owner = Online("admin", 1, UserRole.Owner);
        Assert.Equal("owner is a built-in role with every permission. It cannot be changed or removed.",
                     Run(owner, "role", sub, "owner", "spawn"));
        Assert.Equal("owner is a built-in role with every permission. It cannot be changed or removed.",
                     Run(owner, "role", sub, "OWNER", "spawn"));
        Assert.False(_server.Roles.Exists("owner"));
        Assert.Equal(UserRole.Owner, _users.GetUser("admin")!.Role);
        Assert.True(owner.Can("setrole"));
    }

    [Fact]
    public void ShowingTheOwnerRoleSaysWhatItIs()
    {
        var owner = Online("admin", 1, UserRole.Owner);
        Assert.Equal("owner: every permission. A built-in role; it cannot be changed.", Run(owner, "role", "show", "owner"));
    }

    [Fact]
    public void GrantsCannotStripAnOwner()
    {
        var boss = Online("boss", 1, UserRole.Admin);
        var owner = Online("admin", 2, UserRole.Owner);
        Assert.Equal("admin is an owner and has every permission; there is nothing to revoke.", Run(boss, "revoke", "admin", "kick"));
        Assert.Equal("admin is an owner and has every permission; there is nothing to grant.", Run(boss, "grant", "admin", "kick"));
        Assert.Null(_users.GetUser("admin")!.Permissions);
        Assert.True(owner.Can("kick"));
        Assert.StartsWith("Roles and permissions stay with administrators", Run(boss, "grant", "boss", "owners"));
        Assert.Contains("Not permissions, or not grantable: owners", Run(boss, "role", "create", "crown", "owners"));
        Assert.Empty(_server.Roles.PermissionsOf("crown"));
    }

    [Fact]
    public void PermsSaysAnOwnerHasEverything()
    {
        var owner = Online("admin", 1, UserRole.Owner);
        Assert.Equal("You are an owner: every permission. No single permissions granted.", Run(owner, "perms"));
    }

    /// <summary>roles.json from before "owner" was built in: a custom role of that name would read as the
    /// real one in chat, so it is not loaded.</summary>
    [Fact]
    public void ACustomRoleCalledOwnerIsNotLoaded()
    {
        string path = Path.Combine(_dir, "roles.json");
        File.WriteAllText(path, """{ "owner": ["kick"], "builder": ["spawn"] }""");
        var roles = new RoleRepository(path);
        Assert.False(roles.Exists("owner"));
        Assert.True(roles.Exists("builder"));
    }

    // Names in chat.

    [Theory]
    [InlineData("admin", "Mafia", "Owner", "admin [Mafia] Owner: hello")]
    [InlineData("admin", "", "Owner", "admin Owner: hello")]
    [InlineData("sean", "Mafia", "", "sean [Mafia]: hello")]
    [InlineData("kim", "", "", "kim: hello")]
    [InlineData("bob", "", "Builder", "bob Builder: hello")]
    public void AChatLineIsNameTeamThenRole(string sender, string team, string title, string expected)
    {
        var msg = new ChatMessage { Sender = sender, Team = team, Title = title, Text = "hello", Channel = ChatChannel.Map };
        Assert.Equal(expected, ChatManager.Format(msg));
    }

    [Fact]
    public void EveryChannelNamesTheSenderTheSameWay()
    {
        ChatMessage On(ChatChannel c) => new() { Sender = "admin", Team = "Mafia", Title = "Owner", Text = "hi", Channel = c };
        Assert.Equal("admin [Mafia] Owner to all: hi", ChatManager.Format(On(ChatChannel.All)));
        Assert.Equal("Private from admin [Mafia] Owner: hi", ChatManager.Format(On(ChatChannel.Private)));
        Assert.Equal("[admin [Mafia] Owner, to all]: hi", MudGateway.FormatReply(On(ChatChannel.All)));
        Assert.Equal("[admin [Mafia] Owner]: hi", MudGateway.FormatReply(On(ChatChannel.Map)));
    }

    [Fact]
    public void TheServerSendsTheTeamAndTheRole()
    {
        Assert.Null(_server.Teams!.Create("Mafia", "admin"));
        var owner = Online("admin", 1, UserRole.Owner);
        var dev = Online("devon", 2, UserRole.Dev);
        var player = Online("kim", 3, UserRole.Player);
        var builder = Online("bob", 4, UserRole.Player);
        builder.CustomRole = "builder";

        var line = _server.ChatFrom(owner, "hello", ChatChannel.Map);
        Assert.Equal(("admin", "Mafia", "Owner", true), (line.Sender, line.Team, line.Title, line.FromStaff));
        Assert.Equal("admin [Mafia] Owner: hello", ChatManager.Format(line));
        Assert.Equal("Developer", _server.ChatFrom(dev, "x", ChatChannel.Map).Title);
        var plain = _server.ChatFrom(player, "x", ChatChannel.Map);
        Assert.Equal(("", "", false), (plain.Team, plain.Title, plain.FromStaff));
        Assert.Equal("Builder", _server.ChatFrom(builder, "x", ChatChannel.Map).Title);
        // Everybody hearing a team line is in that team.
        Assert.Equal("admin Owner to team: x", ChatManager.Format(_server.ChatFrom(owner, "x", ChatChannel.Team)));
    }
}

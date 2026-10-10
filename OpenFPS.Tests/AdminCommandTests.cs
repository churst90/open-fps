using System.Numerics;
using Arch.Core;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;

namespace OpenFPS.Tests;

/// <summary>
/// The administrator's view of the server: who is connected and from where, what an account's record
/// says, what is throttled or locked, and the two things an administrator can change — a lock and a
/// role. That a Player and a Developer are refused all of these is in <see cref="StaffGateTests"/>.
/// </summary>
public class AdminCommandTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "openfps-admin-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteUserRepository _users;
    private readonly MapManager _maps;
    private readonly SessionManager _sessions = new();
    private readonly GameServer _server;
    private readonly CommandHandler _commands;
    private readonly List<(UserSession To, IMessage What)> _sent = new();

    public AdminCommandTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "maps"));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "maps", "default.json"), Path.Combine(_dir, "maps", "default.json"));
        _maps = new MapManager(new MapRepository(Path.Combine(_dir, "maps")),
                               new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs")));
        _maps.Initialize();
        _users = new SqliteUserRepository(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={Path.Combine(_dir, "accounts.db")}").Options,
            workFactor: 4);
        _server = new GameServer(_users);
        _server.Attach(_maps, _sessions);
        _server.Sent = (to, what) => _sent.Add((to, what));
        _commands = new CommandHandler(_sessions, _maps, _server, users: _users);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private UserSession Online(string name, int id, UserRole role, string address, bool text = false, bool inWorld = true)
    {
        _users.AddUser(name, "long enough", role);
        var entity = Entity.Null;
        if (inWorld)
        {
            Assert.True(_maps.TryGetMap("default", out var world, out _, out _, out _));
            entity = world.Create(new Transform { Position = new Vector3(id * 3, 2, 0), Rotation = Quaternion.Identity },
                                  new Velocity(), new PlayerComponent { ConnectionId = id, Username = name, Role = role }, EntityType.Player);
            _maps.IndexEntity("default", entity);
        }
        var session = new UserSession
        {
            ConnectionId = id, Username = name, Role = role, Entity = entity, CurrentMapId = "default",
            IsTextClient = text, RemoteAddress = address, Welcomed = true,
            LoggedInUtc = DateTime.UtcNow - TimeSpan.FromMinutes(65),
            LastActivityUtc = DateTime.UtcNow - TimeSpan.FromMinutes(3),
        };
        _sessions.AddSession(id, session);
        return session;
    }

    private List<string> Run(UserSession who, string command, params string[] args)
    {
        var replies = new List<string>();
        _commands.HandleTextCommand(who.ConnectionId, new TextCommand { Command = command, Args = args },
            m => { if (m is TextEvent t) replies.Add(t.Text); });
        _server.DrainCommandBuffer();
        return replies;
    }

    [Fact]
    public void SessionsListsEveryoneWithTransportAddressMapAndIdle()
    {
        var admin = Online("cody", 1, UserRole.Admin, "192.0.2.10");
        Online("friend", 10001, UserRole.Player, "203.0.113.5", text: true, inWorld: false);

        var lines = Run(admin, "sessions");

        Assert.Equal("2 sessions:", lines[0]);
        Assert.StartsWith("  cody, administrator, UDP from 192.0.2.10, on default, logged in ", lines[1]);
        // Typing /sessions is activity, so the asker is never idle.
        Assert.Contains("(1 h 5 min ago), idle 0 s, connection 1.", lines[1]);
        Assert.StartsWith("  friend, player, MUD from 203.0.113.5, on default, not in the world yet, ", lines[2]);
        Assert.EndsWith("(1 h 5 min ago), idle 3 min, connection 10001.", lines[2]);
        Assert.Equal(3, lines.Count);
    }

    [Fact]
    public void UserShowsTheAccountsRecord()
    {
        var admin = Online("cody", 1, UserRole.Admin, "192.0.2.10");
        _users.AddUser("friend", "correct horse", UserRole.Player);
        _users.SetRealName("friend", "A Friend");
        Assert.True(_server.Auth.Check("203.0.113.5", "friend", "correct horse").Success);
        _server.Auth.Check("198.51.100.1", "friend", "wrong");
        _server.Auth.Check("198.51.100.1", "friend", "wrong again");

        string said = Assert.Single(Run(admin, "user", "Friend"));

        Assert.StartsWith("friend, player. Created ", said);
        Assert.Contains("Last login ", said);
        Assert.Contains(" from 203.0.113.5.", said);
        Assert.Contains("2 wrong passwords since, the last ", said);
        Assert.Contains(" from 198.51.100.1.", said);
        Assert.Contains("2 failed logins in a row since the server started.", said);
        Assert.Contains("Not online.", said);
        Assert.EndsWith("Real name A Friend.", said);
    }

    [Fact]
    public void UserSaysWhenANameIsLockedAndWhenThereIsNoSuchAccount()
    {
        var admin = Online("cody", 1, UserRole.Admin, "192.0.2.10");
        _users.AddUser("friend", "correct horse", UserRole.Player);
        for (int i = 0; i < AuthService.LockoutAfterFailures; i++) _server.Auth.Check("198.51.100.1", "friend", "guess" + i);
        for (int i = 0; i < 3; i++) _server.Auth.Check("198.51.100.1", "ghost", "guess" + i);

        Assert.Contains("Locked for 14 min more after 10 failed logins in a row; /unlock friend lifts it.",
                        Assert.Single(Run(admin, "user", "friend")));
        Assert.Equal("There is no account called ghost. 3 failed logins in a row since the server started.",
                     Assert.Single(Run(admin, "user", "ghost")));
        Assert.Equal("There is no account called nobody.", Assert.Single(Run(admin, "user", "nobody")));
    }

    [Fact]
    public void ThrottledListsAddressesAndLockedNamesAndUnlockClearsThem()
    {
        var admin = Online("cody", 1, UserRole.Admin, "192.0.2.10");
        Assert.Equal("Nothing is throttled and no name is locked.", Assert.Single(Run(admin, "throttled")));

        for (int i = 0; i < 7; i++) _server.Auth.Admit("198.51.100.1");
        for (int i = 0; i < AuthService.LockoutAfterFailures; i++) _server.Auth.Check("198.51.100.1", "friend", "guess" + i);

        var lines = Run(admin, "throttled");
        Assert.Contains(lines, l => l.StartsWith("  198.51.100.1: over the login limit, next try in "));
        Assert.Contains(lines, l => l.StartsWith("  friend: locked for 14 min more after 10 failed logins."));

        Assert.Equal("friend is unlocked and its failures forgotten.", Assert.Single(Run(admin, "unlock", "Friend")));
        Assert.Equal("198.51.100.1 may log in and create accounts again.", Assert.Single(Run(admin, "unlock", "198.51.100.1")));
        Assert.Equal("Nothing is throttled and no name is locked.", Assert.Single(Run(admin, "throttled")));
        Assert.Equal("friend was not locked.", Assert.Single(Run(admin, "unlock", "friend")));
        Assert.True(_server.Auth.Admit("198.51.100.1"));
    }

    [Fact]
    public void SetRoleChangesTheAccountAndTheOnlineSessionButNeverYourOwn()
    {
        var admin = Online("cody", 1, UserRole.Admin, "192.0.2.10");
        var friend = Online("friend", 2, UserRole.Player, "203.0.113.5");

        Assert.Equal("friend is now a developer.", Assert.Single(Run(admin, "setrole", "friend", "dev")));
        Assert.Equal(UserRole.Dev, _users.GetUser("friend")!.Role);
        Assert.Equal(UserRole.Dev, friend.Role);
        Assert.True(_maps.TryGetMap("default", out var world, out _, out _, out _));
        Assert.Equal(UserRole.Dev, world.Get<PlayerComponent>(friend.Entity).Role);
        Assert.Contains(_sent, s => s.To == friend && s.What is TextEvent t && t.Text.StartsWith("cody made you a developer."));

        // And it is a real role: the developer can now do what a developer does.
        Assert.DoesNotContain("permission", string.Join(" ", Run(friend, "origin")));

        Assert.Equal("You cannot change your own role.", Assert.Single(Run(admin, "setrole", "cody", "player")));
        Assert.Equal(UserRole.Admin, _users.GetUser("cody")!.Role);
        Assert.Equal("'boss' is not a role. Roles: player, moderator, dev, admin, owner.", Assert.Single(Run(admin, "setrole", "friend", "boss")));
        Assert.Equal("There is no account called zed.", Assert.Single(Run(admin, "setrole", "zed", "dev")));
    }
}

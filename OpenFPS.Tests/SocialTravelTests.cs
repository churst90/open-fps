using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Arch.Core;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using OpenFPS.Server.Services;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// The social and travel menu's server half: real friends that persist, /where and /profile in
/// words, the player list's bare usernames, and /join — a session changing map at run time, which
/// until now no session could ever do.
/// </summary>
public class SocialTravelTests : IDisposable
{
    private readonly string _friendsPath = Path.Combine(Path.GetTempPath(), $"openfps-friends-{Guid.NewGuid():N}.json");
    private readonly MapManager _maps;
    private readonly SessionManager _sessions = new();
    private readonly GameServer _server;
    private readonly CommandHandler _commands;
    private readonly FriendRepository _friends;
    private readonly KnownUsers _users = new("alice", "bob", "carol");
    private readonly List<IMessage> _replies = new();

    public SocialTravelTests()
    {
        var prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
        _maps = new MapManager(new MapRepository(Path.Combine(AppContext.BaseDirectory, "maps")), prefabs);
        _maps.Initialize();
        _server = new GameServer(_users);
        _server.Attach(_maps, _sessions);
        _friends = new FriendRepository(_friendsPath);
        _commands = new CommandHandler(_sessions, _maps, _server, users: _users, friends: _friends);
    }

    public void Dispose()
    {
        try { if (File.Exists(_friendsPath)) File.Delete(_friendsPath); } catch { }
    }

    private UserSession Player(string name, int id, Vector3 at, string mapId = "default", UserRole role = UserRole.Player, bool text = false)
    {
        Assert.True(_maps.TryGetMap(mapId, out var world, out _, out _, out _));
        var entity = world.Create(
            new Transform { Position = at, Rotation = Quaternion.Identity },
            new Velocity { Linear = Vector3.Zero },
            new PlayerComponent { ConnectionId = id, Username = name, Role = role },
            EntityType.Player);
        _maps.IndexEntity(mapId, entity);
        var session = new UserSession
        {
            ConnectionId = id, Username = name, Role = role, Entity = entity, CurrentMapId = mapId,
            IsTextClient = text, Welcomed = true,
        };
        _sessions.AddSession(id, session);
        return session;
    }

    private string Run(UserSession session, string command, params string[] args)
    {
        _replies.Clear();
        _commands.HandleTextCommand(session.ConnectionId, new TextCommand { Command = command, Args = args }, _replies.Add);
        _server.DrainCommandBuffer();
        return string.Join(" | ", _replies.OfType<TextEvent>().Select(t => t.Text));
    }

    [Fact]
    public void FriendsPersistAndCarryOnlineFlags()
    {
        var alice = Player("alice", 1, new Vector3(0, 2, 0));
        Player("bob", 2, new Vector3(5, 2, 0));

        Assert.Equal("bob added to your friends.", Run(alice, "friend", "add", "bob"));
        Assert.Equal("carol added to your friends.", Run(alice, "friend", "carol"));   // bare = add
        Assert.Equal("bob is already your friend.", Run(alice, "friend", "add", "BOB"));
        Assert.Equal("There is no player called zed.", Run(alice, "friend", "add", "zed"));
        Assert.Equal("You cannot add yourself as a friend.", Run(alice, "friend", "add", "alice"));

        // A fresh repository over the same file: it was written down, not merely remembered.
        var reread = new FriendRepository(_friendsPath);
        Assert.Equal(new[] { "bob", "carol" }, reread.GetFriends("alice"));
        Assert.Empty(reread.GetFriends("bob"));      // one-directional

        var list = SocialService.BuildFriendList("alice", reread, _sessions);
        Assert.Equal(new[] { "bob", "carol" }, list.Friends);
        Assert.Equal(new[] { true, false }, list.Online);

        // The same list through the text command.
        _replies.Clear();
        _commands.HandleTextCommand(1, new TextCommand { Command = "friends" }, _replies.Add);
        _server.DrainCommandBuffer();
        Assert.Equal(new[] { "bob", "carol" }, Assert.Single(_replies.OfType<FriendListResponse>()).Friends);

        Assert.Equal("bob removed from your friends.", Run(alice, "friend", "remove", "bob"));
        Assert.Equal("bob is not on your friends list.", Run(alice, "unfriend", "bob"));
        Assert.Equal(new[] { "carol" }, new FriendRepository(_friendsPath).GetFriends("alice"));
    }

    [Theory]
    [InlineData(UserRole.Dev)]
    [InlineData(UserRole.Admin)]
    public void WhereSaysBearingDistanceMapOrOfflineToStaff(UserRole role)
    {
        var alice = Player("alice", 1, new Vector3(0, 2, 0), role: role);
        var bob = Player("bob", 2, new Vector3(0, 2, 10));

        string where = Run(alice, "where", "bob");
        Assert.StartsWith("bob is 10 metres away, in front", where);

        bob.CurrentMapId = "speedway";
        Assert.Equal("bob is on the map speedway.", Run(alice, "where", "bob"));
        Assert.Equal("carol is not online.", Run(alice, "where", "carol"));
        Assert.Equal("There is no player called zed.", Run(alice, "where", "zed"));
    }

    [Fact]
    public void WhereIsRefusedToAPlayer()
    {
        var alice = Player("alice", 1, new Vector3(0, 2, 0));
        Player("bob", 2, new Vector3(0, 2, 10));

        Assert.Equal("You do not have permission to execute this command.", Run(alice, "where", "bob"));
        Assert.Equal("You do not have permission to execute this command.", Run(alice, "locate", "bob"));
        // Not even yourself: the C key reads your own coordinates on the client.
        Assert.Equal("You do not have permission to execute this command.", Run(alice, "where", "alice"));
    }

    [Fact]
    public void ProfileSaysRankStatusAndMapButNeverWhereOnIt()
    {
        var alice = Player("alice", 1, new Vector3(0, 2, 0));
        var bob = Player("bob", 2, new Vector3(10, 2, 0));
        Run(alice, "friend", "add", "bob");

        Assert.Equal("bob, player. Online, here on default. On your friends list.", Run(alice, "profile", "bob"));
        Assert.Equal("carol, player. Not online.", Run(alice, "profile", "carol"));

        // Staff asking get the same: a profile is not a locator for anybody.
        var admin = Player("dana", 3, new Vector3(-10, 2, 0), role: UserRole.Admin);
        Assert.Equal("bob, player. Online, here on default.", Run(admin, "profile", "bob"));

        bob.CurrentMapId = "speedway";
        Assert.Equal("bob, player. Online, on speedway. On your friends list.", Run(alice, "profile", "bob"));

        // A private map is not named to somebody it would turn away; its owner and staff hear it.
        Assert.True(_maps.TryGetMapData("speedway", out var data));
        data.IsPublic = false;
        data.OwnerId = "carol";
        Assert.Equal("bob, player. Online, on a private map. On your friends list.", Run(alice, "profile", "bob"));
        Assert.Equal("bob, player. Online, on speedway.", Run(admin, "profile", "bob"));
    }

    [Fact]
    public void ProfileSaysAwayAndIdle()
    {
        var alice = Player("alice", 1, new Vector3(0, 2, 0));
        var bob = Player("bob", 2, new Vector3(10, 2, 0));

        Assert.Equal("You are marked away. Anything you do clears it.", Run(bob, "afk"));
        Assert.Equal("bob, player. Away, here on default.", Run(alice, "profile", "bob"));
        Run(bob, "motd");                          // doing anything is being back
        Assert.False(bob.Away);
        Assert.Equal("bob, player. Online, here on default.", Run(alice, "profile", "bob"));

        bob.LastActivityUtc = DateTime.UtcNow - TimeSpan.FromMinutes(12.5);
        Assert.Equal("bob, player. Idle for 12 minutes, here on default.", Run(alice, "profile", "bob"));
    }

    [Fact]
    public void ARealNameIsTheirsToSetAndShows()
    {
        var alice = Player("alice", 1, new Vector3(0, 2, 0));
        var bob = Player("bob", 2, new Vector3(10, 2, 0));

        Assert.StartsWith("Your profile shows no real name.", Run(bob, "realname"));
        Assert.Equal("Your profile now shows the real name Robert Smith.", Run(bob, "realname", "Robert", "Smith"));
        Assert.Equal("bob, player. Real name Robert Smith. Online, here on default.", Run(alice, "profile", "bob"));
        Assert.Equal("A real name can be at most 64 characters, with no control characters.",
                     Run(bob, "realname", new string('x', 65)));
        Assert.Equal("Your profile no longer shows a real name.", Run(bob, "realname", "clear"));
        Assert.Equal("bob, player. Online, here on default.", Run(alice, "profile", "bob"));
    }

    [Fact]
    public void PlayerListUsernamesAreParallelToTheSentences()
    {
        Player("alice", 1, new Vector3(0, 2, 0));
        Player("bob", 2, new Vector3(5, 2, 0), mapId: "speedway");
        var dispatcher = new MessageDispatcher();
        _ = new DiscoveryService(dispatcher, _sessions, _maps);

        var replies = new List<IMessage>();
        dispatcher.Dispatch(1, new PlayerListRequest { Scope = PlayerListScope.Server }, replies.Add);
        var list = Assert.Single(replies.OfType<PlayerListResponse>());

        Assert.Equal(list.Players.Length, list.Usernames.Length);
        Assert.Equal(new[] { "alice", "bob" }, list.Usernames);
        for (int i = 0; i < list.Players.Length; i++) Assert.StartsWith(list.Usernames[i], list.Players[i]);
    }

    [Fact]
    public void JoinMovesATextSessionToTheOtherMap()
    {
        var alice = Player("alice", 1, new Vector3(0, 2, 0), text: true);
        var bob = Player("bob", 2, new Vector3(3, 2, 0));
        var oldBody = alice.Entity;
        bob.KnownEntities.Add(oldBody.Id);
        alice.KnownEntities.Add(12345);
        alice.Build.SetOrigin(Vector3.Zero, 0);
        alice.Build.Placed_Entities.Add(999);

        Assert.Equal("You are already on default.", Run(alice, "join", "default"));

        string said = Run(alice, "join", "Speedway");
        Assert.Contains("Travelling to speedway.", said);

        Assert.Equal("speedway", alice.CurrentMapId);
        Assert.True(_maps.TryGetMap("default", out var oldWorld, out _, out _, out _));
        Assert.False(oldWorld.IsAlive(oldBody));
        Assert.DoesNotContain(oldBody.Id, bob.KnownEntities);      // told it is gone
        Assert.DoesNotContain(12345, alice.KnownEntities);          // everything goes out fresh
        Assert.False(alice.Build.Placed);
        Assert.Empty(alice.Build.Placed_Entities);

        // A text session has nothing to load: it is spawned on the new map straight away.
        Assert.NotEqual(Entity.Null, alice.Entity);
        Assert.True(_maps.TryGetMap("speedway", out var newWorld, out _, out _, out _));
        Assert.True(newWorld.IsAlive(alice.Entity));
        var spawn = _maps.GetSpawnPoint("speedway").Position;
        Assert.True(Vector3.Distance(spawn, newWorld.Get<Transform>(alice.Entity).Position) < 0.01f);

        Assert.Equal("You are already on speedway.", Run(alice, "join", "speedway"));
        Assert.StartsWith("There is no map called nowhere.", Run(alice, "join", "nowhere"));
    }

    [Fact]
    public void APrivateMapRefusesEveryoneButItsOwnerAndStaff()
    {
        Assert.True(_maps.TryGetMapData("speedway", out var data));
        data.IsPublic = false;
        data.OwnerId = "carol";

        var alice = Player("alice", 1, new Vector3(0, 2, 0), text: true);
        Assert.Equal("speedway is private.", Run(alice, "join", "speedway"));
        Assert.Equal("default", alice.CurrentMapId);

        var admin = Player("bob", 2, new Vector3(3, 2, 0), role: UserRole.Admin, text: true);
        Run(admin, "join", "speedway");
        Assert.Equal("speedway", admin.CurrentMapId);
    }

    private sealed class KnownUsers : IUserRepository
    {
        private readonly HashSet<string> _names;
        private readonly Dictionary<string, string> _realNames = new(StringComparer.OrdinalIgnoreCase);
        public KnownUsers(params string[] names) => _names = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        public UserData? GetUser(string username) =>
            _names.Contains(username)
                ? new UserData { Username = username.ToLowerInvariant(), Role = UserRole.Player, RealName = _realNames.GetValueOrDefault(username) }
                : null;
        public bool AddUser(string username, string password, UserRole role) => _names.Add(username);
        public bool VerifyPassword(string username, string password) => false;
        public bool SetRealName(string username, string? realName)
        {
            if (string.IsNullOrEmpty(realName)) _realNames.Remove(username);
            else _realNames[username] = realName;
            return true;
        }
    }
}

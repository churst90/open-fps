using LiteNetLib;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OpenFPS.Client.Core;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;

namespace OpenFPS.Tests;

/// <summary>
/// Presence notices: everyone else on the server is told, on the All channel, when somebody logs in,
/// logs out, loses their connection, goes away or comes back. Cody asked for them on 2026-10-03, each
/// with its own sound that can be turned off. Run through the real login, logout and command paths on
/// a server with no sockets, watching what it sends.
/// </summary>
public class PresenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "openfps-presence-" + Guid.NewGuid().ToString("N"));
    private readonly GameServer _server;
    private readonly SessionManager _sessions = new();
    private readonly CommandHandler _commands;
    private readonly List<(UserSession To, IMessage What)> _sent = new();

    public PresenceTests()
    {
        Directory.CreateDirectory(_dir);
        var repo = new SqliteUserRepository(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={Path.Combine(_dir, "accounts.db")}").Options,
            workFactor: 4);
        foreach (var name in new[] { "alice", "bob", "carol" }) repo.AddUser(name, "correct horse", UserRole.Player);

        string mapDir = Path.Combine(_dir, "maps");
        Directory.CreateDirectory(mapDir);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "maps", "default.json"), Path.Combine(mapDir, "default.json"));
        var maps = new MapManager(new MapRepository(mapDir), new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs")));
        maps.Initialize();

        _server = new GameServer(repo);
        _server.Attach(maps, _sessions);
        _server.Sent = (to, what) => _sent.Add((to, what));
        _commands = new CommandHandler(_sessions, maps, _server);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private async Task<UserSession> LogIn(int connection, string name)
    {
        await _server.Login(connection, new LoginRequest { Username = name, Password = "correct horse" }, _ => { })
            .Within(TestDeadline.Login, $"{name}'s login");
        _server.DrainCommandBuffer();
        Assert.True(_sessions.TryGetSession(connection, out var session));
        return session;
    }

    private void Run(UserSession session, string command, params string[] args)
    {
        _commands.HandleTextCommand(session.ConnectionId, new TextCommand { Command = command, Args = args }, _ => { });
        _server.DrainCommandBuffer();
    }

    /// <summary>Every presence notice sent, as (to whom, kind, words).</summary>
    private List<(string To, PresenceKind Kind, string Text)> Notices() =>
        _sent.Where(s => s.What is ChatMessage { Presence: not PresenceKind.None })
             .Select(s => (s.To.Username, ((ChatMessage)s.What).Presence, ((ChatMessage)s.What).Text))
             .ToList();

    [Fact]
    public async Task ALoginIsAnnouncedToEveryoneElseOnTheAllChannel()
    {
        await LogIn(1, "alice");
        await LogIn(2, "carol");
        _sent.Clear();

        await LogIn(3, "bob");

        var notices = _sent.Where(s => s.What is ChatMessage { Presence: PresenceKind.LoggedIn }).ToList();
        Assert.Equal(new[] { "alice", "carol" }, notices.Select(n => n.To.Username).OrderBy(n => n));
        var line = (ChatMessage)notices[0].What;
        Assert.Equal(ChatChannel.All, line.Channel);
        Assert.Equal("bob", line.Sender);
        Assert.Equal("bob is online.", line.Text);
    }

    [Fact]
    public async Task ALogoutAndALostConnectionAreToldApart()
    {
        await LogIn(1, "alice");
        await LogIn(2, "bob");
        await LogIn(3, "carol");
        _sent.Clear();

        _server.LogOut(2);
        _server.PeerDisconnected(3, DisconnectReason.Timeout);

        Assert.Equal(new[]
        {
            ("alice", PresenceKind.LoggedOut, "bob logged out."),
            ("carol", PresenceKind.LoggedOut, "bob logged out."),
            ("alice", PresenceKind.WentOffline, "carol lost connection."),
        }, Notices());
        Assert.False(_sessions.TryGetSession(2, out _));
        Assert.False(_sessions.TryGetSession(3, out _));
    }

    [Theory]
    [InlineData(DisconnectReason.RemoteConnectionClose, PresenceKind.LoggedOut)]
    [InlineData(DisconnectReason.DisconnectPeerCalled, PresenceKind.LoggedOut)]
    [InlineData(DisconnectReason.Timeout, PresenceKind.WentOffline)]
    [InlineData(DisconnectReason.HostUnreachable, PresenceKind.WentOffline)]
    [InlineData(DisconnectReason.Reconnect, PresenceKind.WentOffline)]
    public void AClosedConnectionIsALogoutAndAnythingElseIsLost(DisconnectReason reason, PresenceKind expected)
        => Assert.Equal(expected, GameServer.PresenceFor(reason));

    [Fact]
    public async Task ATextPlayerClosingTheirConnectionLoggedOut()
    {
        await LogIn(1, "alice");
        await LogIn(2, "bob");
        _sent.Clear();

        _server.HandleMudDisconnected(2);

        Assert.Equal(new[] { ("alice", PresenceKind.LoggedOut, "bob logged out.") }, Notices());
    }

    [Fact]
    public async Task TheSameAccountLoggingInAgainIsNotAnnouncedAtAll()
    {
        await LogIn(1, "alice");
        await LogIn(2, "bob");
        _sent.Clear();

        await LogIn(3, "BOB");

        Assert.False(_sessions.TryGetSession(2, out _));
        Assert.Empty(Notices());
    }

    [Fact]
    public async Task AKickSaysTheyWereRemovedAndNotWhy()
    {
        var alice = await LogIn(1, "alice");
        var bob = await LogIn(2, "bob");
        _sent.Clear();

        _server.Kick(bob, "You have been removed from the server by alice. Reason: spamming");

        Assert.Equal(new[] { ("alice", PresenceKind.LoggedOut, "bob was removed from the server.") }, Notices());
        Assert.DoesNotContain(_sent, s => s.To == alice && s.What is TextEvent t && t.Text.Contains("spamming"));
    }

    [Fact]
    public async Task AfkIsAnnouncedAsAwayAndThenBack()
    {
        await LogIn(1, "alice");
        var bob = await LogIn(2, "bob");
        _sent.Clear();

        Run(bob, "afk");
        Run(bob, "afk");
        Run(bob, "afk");
        Run(bob, "motd");   // anything else is being back too

        Assert.Equal(new[]
        {
            ("alice", PresenceKind.Away, "bob is away."),
            ("alice", PresenceKind.Back, "bob is back."),
            ("alice", PresenceKind.Away, "bob is away."),
            ("alice", PresenceKind.Back, "bob is back."),
        }, Notices());
    }

    [Fact]
    public async Task IdlingIsAnnouncedAsAwayOnceAndActivityAsBack()
    {
        await LogIn(1, "alice");
        var bob = await LogIn(2, "bob");
        _sent.Clear();
        var now = DateTime.UtcNow;

        _server.UpdatePresenceForAll(now + UserSession.IdleAfter - TimeSpan.FromSeconds(1));
        Assert.Empty(Notices());

        // Past the idle time, every second of the sweep finds bob away; he is said to be away once.
        for (int s = 0; s < 5; s++) _server.UpdatePresenceForAll(now + UserSession.IdleAfter + TimeSpan.FromSeconds(1 + s));
        // Alice is idle too, and is announced to bob.
        Assert.Equal(new[]
        {
            ("bob", PresenceKind.Away, "alice is away."),
            ("alice", PresenceKind.Away, "bob is away."),
        }, Notices());
        _sent.Clear();

        // A chat line is bob doing something.
        Run(bob, "all", "sorry, back now");
        Assert.Equal(new[] { ("alice", PresenceKind.Back, "bob is back.") }, Notices());
    }

    [Fact]
    public async Task NobodyIsToldAboutThemselves()
    {
        var alice = await LogIn(1, "alice");
        _sent.Clear();
        Run(alice, "afk");
        _server.LogOut(1);
        Assert.Empty(Notices());
    }

    // ── The client ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EachPresenceKindHasItsOwnSoundAndNoneWhenTurnedOff()
    {
        var kinds = Enum.GetValues<PresenceKind>().Where(k => k != PresenceKind.None).ToList();
        var cues = kinds.Select(k => UiSounds.CueFor(Notice(k), presenceSounds: true)).ToList();
        Assert.DoesNotContain(null, cues);
        Assert.Equal(kinds.Count, cues.Distinct().Count());
        // Not the chat sound, nor any other cue that was there before.
        Assert.DoesNotContain(UiCue.ChatAll, cues);
        foreach (var cue in cues) Assert.StartsWith("Presence", cue.ToString());

        foreach (var k in kinds) Assert.Null(UiSounds.CueFor(Notice(k), presenceSounds: false));
        // Turning them off leaves somebody talking to everyone as it was.
        Assert.Equal(UiCue.ChatAll, UiSounds.CueFor(new ChatMessage { Sender = "bob", Text = "hi", Channel = ChatChannel.All }, presenceSounds: false));

        static ChatMessage Notice(PresenceKind k) => new() { Sender = "bob", Text = "bob ...", Channel = ChatChannel.All, Presence = k };
    }

    [Fact]
    public void ANoticeIsReadAsItsWordsAndFiledWithEverythingElse()
    {
        var line = new ChatMessage { Sender = "bob", Text = "bob is online.", Channel = ChatChannel.All, Presence = PresenceKind.LoggedIn };
        Assert.Equal("bob is online.", OpenFPS.Client.Services.ChatManager.Format(line));
        Assert.Equal(OpenFPS.Client.Services.ChatBufferType.All, OpenFPS.Client.Services.ChatManager.BufferFor(line.Channel));
    }

    [Fact]
    public void ThePresenceSoundsAreLongerThanAChatLineAndDifferentFromEachOther()
    {
        var presence = new[] { UiCue.PresenceOnline, UiCue.PresenceLoggedOut, UiCue.PresenceConnectionLost, UiCue.PresenceAway, UiCue.PresenceBack };
        var chat = UiSounds.Render(UiCue.ChatAll).Length;
        var waves = presence.Select(c => UiSounds.Render(c)).ToList();
        foreach (var w in waves)
        {
            Assert.True(w.Length > 2 * chat, "a presence cue should be longer than a chat cue");
            Assert.InRange(w.Max(MathF.Abs), 0.2f, 0.89f);   // below -1 dBFS
        }
        // A rise and its fall are the same length; they must still not be the same sound.
        for (int i = 0; i < waves.Count; i++)
            for (int j = i + 1; j < waves.Count; j++)
                Assert.False(waves[i].Length == waves[j].Length && waves[i].Zip(waves[j]).All(p => MathF.Abs(p.First - p.Second) < 1e-3f),
                             $"{presence[i]} and {presence[j]} are the same sound");
    }
}

using System.Numerics;
using OpenFPS.Client.Core.Platform;
using OpenFPS.Client.Services;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;

namespace OpenFPS.Tests;

/// <summary>
/// A command's reply is spoken. Reported as "the / key ... doesn't seem to move my player": the move
/// worked, but replies are System messages filed in the Server buffer, which is not the one a player
/// starts in, so every answer went unspoken.
/// </summary>
public class CommandFeedbackTests
{
    private sealed class CapturedSpeech : ISpeechOutput
    {
        public readonly List<string> Spoken = new();
        public string BackendName => "test";
        public bool Initialize() => true;
        public void Speak(string text, bool interrupt = true) => Spoken.Add(text);
        public void Interrupt() { }
        public void Dispose() { }
        public bool Said(string fragment) => Spoken.Any(s => s.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheGameAnsweringYouIsAlwaysSpoken()
    {
        var tts = new CapturedSpeech();
        var chat = new ChatManager(tts);

        // The buffer a player starts in. Nobody has cycled anywhere.
        Assert.Equal(ChatBufferType.All, chat.ActiveBuffer);

        chat.AddServerMessage("Moved to 40.0, 0.0, 120.0");
        Assert.True(tts.Said("Moved to 40.0"),
            "a reply to a command the player just typed has to be spoken from the buffer they are actually in");

        chat.AddError("Cannot move there: Area is solid.");
        Assert.True(tts.Said("Area is solid"));

        chat.AddMessage(new ChatMessage { Sender = "dev", Text = "over here", Channel = ChatChannel.Private });
        Assert.True(tts.Said("over here"));
    }

    /// <summary>Other people's talk is still gated by buffer: replies are spoken, not everything.</summary>
    [Fact]
    public void AmbientChatterStillObeysTheBuffer()
    {
        var tts = new CapturedSpeech();
        var chat = new ChatManager(tts);

        chat.CycleBuffer(1);   // away from All, to Map
        Assert.NotEqual(ChatBufferType.All, chat.ActiveBuffer);
        tts.Spoken.Clear();

        chat.AddMessage(new ChatMessage { Sender = "someone", Text = "global chatter", Channel = ChatChannel.All });
        Assert.False(tts.Said("global chatter"),
            "chatter in a channel the player has turned away from should stay in its buffer");
    }

    // ── And the move itself, on the server ──────────────────────────────────────────────────────

    [Fact]
    public void MoveTeleportsAnElevatedPlayerAndSaysSo()
    {
        var (server, commands, maps, sessions) = BuildCommandStack();
        var session = SpawnTestPlayer(maps, sessions, connectionId: 1, UserRole.Admin);

        var replies = new List<IMessage>();
        commands.HandleTextCommand(session.ConnectionId,
            new TextCommand { Command = "move", Args = new[] { "12", "-8", "2" } }, replies.Add);
        server.DrainCommandBuffer();

        Assert.True(maps.TryGetMap("default", out var world, out _, out _, out _));
        var at = world.Get<Transform>(session.Entity).Position;
        Assert.Equal(12f, at.X, 2);
        Assert.Equal(-8f, at.Z, 2);

        // The client resets prediction off this, so without it the player snaps back next correction.
        Assert.Contains(replies, m => m is PlayerSpawned);
        // And the player is told, in coordinates they can repeat back.
        Assert.Contains(replies.OfType<TextEvent>(), t => t.Text.Contains("Moved to"));
    }

    [Fact]
    public void MoveRefusesSolidGroundAndSaysWhy()
    {
        var (server, commands, maps, sessions) = BuildCommandStack();
        var session = SpawnTestPlayer(maps, sessions, connectionId: 2, UserRole.Admin);
        Assert.True(maps.TryGetMap("default", out var world, out _, out _, out _));
        var before = world.Get<Transform>(session.Entity).Position;

        // A solid block of our own, not a wall of the shipped map.
        var block = world.Create(
            new Transform { Position = new Vector3(20, 2, 20), Rotation = Quaternion.Identity },
            new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(4, 4, 4), IsSolid = true },
            EntityType.StaticObject);
        maps.IndexEntity("default", block);

        var replies = new List<IMessage>();
        commands.HandleTextCommand(session.ConnectionId,
            new TextCommand { Command = "move", Args = new[] { "20", "20", "2" } }, replies.Add);
        server.DrainCommandBuffer();

        var after = world.Get<Transform>(session.Entity).Position;
        Assert.Equal(before, after);
        Assert.Contains(replies.OfType<TextEvent>(), t => t.Text.Contains("Cannot move there"));
    }

    [Fact]
    public void AnOrdinaryPlayerIsToldWhyRatherThanIgnored()
    {
        var (server, commands, maps, sessions) = BuildCommandStack();
        var session = SpawnTestPlayer(maps, sessions, connectionId: 3, UserRole.Player);

        var replies = new List<IMessage>();
        commands.HandleTextCommand(session.ConnectionId,
            new TextCommand { Command = "move", Args = new[] { "5", "2", "5" } }, replies.Add);
        server.DrainCommandBuffer();

        Assert.Contains(replies.OfType<TextEvent>(), t => t.Text.Contains("permission"));
    }

    private sealed class StubUserRepository : IUserRepository
    {
        public UserData? GetUser(string username) => null;
        public bool AddUser(string username, string password, UserRole role) => true;
        public bool VerifyPassword(string username, string password) => false;
    }

    private static (GameServer server, CommandHandler commands, MapManager maps, SessionManager sessions) BuildCommandStack()
    {
        var prefabs = new PrefabRepository(System.IO.Path.Combine(AppContext.BaseDirectory, "prefabs"));
        var mapRepo = new MapRepository(System.IO.Path.Combine(AppContext.BaseDirectory, "maps"));
        var maps = new MapManager(mapRepo, prefabs);
        maps.Initialize();
        var sessions = new SessionManager();
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
}

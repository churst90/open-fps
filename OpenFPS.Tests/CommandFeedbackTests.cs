using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OpenFPS.Client.Core.Platform;
using OpenFPS.Client.Services;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// Typing a command and being told what happened.
///
/// Reported as "the / key to open the command box doesn't seem to move my player to the specified
/// cords". The move itself was fine. What was broken was every ANSWER: a server reply is a System
/// message, System messages are filed in the Server buffer, and the Server buffer is not the one a
/// player starts in — so it was never spoken. "Moved to 40, 0, 120", "Cannot move there: area is
/// solid" and "You do not have permission" were all delivered into silence, and from the player's
/// side a command simply did nothing with no way to tell which of those had happened.
///
/// For a game played entirely by ear that is not a chat-routing detail, it is the difference between
/// a feature and a feature you cannot use.
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
        Assert.Equal(ChatBufferType.Global, chat.ActiveBuffer);

        chat.AddServerMessage("Moved to 40.0, 0.0, 120.0");
        Assert.True(tts.Said("Moved to 40.0"),
            "a reply to a command the player just typed has to be spoken from the buffer they are actually in");

        chat.AddError("Cannot move there: Area is solid.");
        Assert.True(tts.Said("Area is solid"));

        chat.AddMessage(new ChatMessage { Sender = "[PM from dev]", Text = "over here" });
        Assert.True(tts.Said("over here"));
    }

    /// <summary>
    /// ...and other people talking is still gated, because that is what a buffer is FOR. If this
    /// stops being true the fix above has turned into "speak everything", which is its own problem.
    /// </summary>
    [Fact]
    public void AmbientChatterStillObeysTheBuffer()
    {
        var tts = new CapturedSpeech();
        var chat = new ChatManager(tts);

        chat.CycleBuffer(1);   // away from Global
        Assert.NotEqual(ChatBufferType.Global, chat.ActiveBuffer);
        tts.Spoken.Clear();

        chat.AddMessage(new ChatMessage { Sender = "someone", Text = "global chatter" });
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
            new TextCommand { Command = "move", Args = new[] { "12", "2", "-8" } }, replies.Add);
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

        // A solid block put where we are about to try to stand, so the test does not depend on
        // knowing where the shipped map happens to have a wall.
        var block = world.Create(
            new Transform { Position = new Vector3(20, 2, 20), Rotation = Quaternion.Identity },
            new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(4, 4, 4), IsSolid = true },
            EntityType.StaticObject);
        maps.IndexEntity("default", block);

        var replies = new List<IMessage>();
        commands.HandleTextCommand(session.ConnectionId,
            new TextCommand { Command = "move", Args = new[] { "20", "2", "20" } }, replies.Add);
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

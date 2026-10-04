using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Client.Core;
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
/// Teams, on the server: made, joined by invitation or because they are open, left, kicked from,
/// written down so they outlast a restart, talked in by their members alone — and worn on each
/// member's body, re-sent when it changes, so the clients near them hear the change.
/// </summary>
public class TeamTests : IDisposable
{
    private readonly string _teamsPath = Path.Combine(Path.GetTempPath(), $"openfps-teams-{Guid.NewGuid():N}.json");
    private readonly MapManager _maps;
    private readonly SessionManager _sessions = new();
    private readonly GameServer _server;
    private readonly CommandHandler _commands;
    private readonly KnownUsers _users = new("alice", "bob", "carol", "dave");
    private readonly List<IMessage> _replies = new();
    private readonly List<(UserSession To, IMessage Message)> _sent = new();

    public TeamTests()
    {
        var prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
        _maps = new MapManager(new MapRepository(Path.Combine(AppContext.BaseDirectory, "maps")), prefabs);
        _maps.Initialize();
        _server = new GameServer(_users);
        _server.Attach(_maps, _sessions);
        _server.Teams = new TeamRepository(_teamsPath);
        _server.Sent = (to, message) => _sent.Add((to, message));
        _commands = new CommandHandler(_sessions, _maps, _server, users: _users);
    }

    public void Dispose()
    {
        try { if (File.Exists(_teamsPath)) File.Delete(_teamsPath); } catch { }
    }

    private UserSession Player(string name, int id, Vector3 at)
    {
        Assert.True(_maps.TryGetMap("default", out var world, out _, out _, out _));
        var entity = world.Create(
            new Transform { Position = at, Rotation = Quaternion.Identity },
            new Velocity { Linear = Vector3.Zero },
            new PlayerComponent { ConnectionId = id, Username = name, Role = UserRole.Player },
            EntityType.Player);
        _maps.IndexEntity("default", entity);
        var session = new UserSession { ConnectionId = id, Username = name, Role = UserRole.Player, Entity = entity, CurrentMapId = "default", Welcomed = true };
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

    private EntityDefinition Definition(UserSession s)
    {
        Assert.True(_maps.TryGetMap("default", out var world, out _, out _, out _));
        return EntityDefinitionFactory.From(world, s.Entity);
    }

    [Fact]
    public void ATeamIsMadeJoinedByInvitationLeftAndKickedFrom()
    {
        var alice = Player("alice", 1, new Vector3(0, 2, 0));
        var bob = Player("bob", 2, new Vector3(4, 2, 0));
        var carol = Player("carol", 3, new Vector3(8, 2, 0));

        Assert.StartsWith("You are not in a team.", Run(alice, "team"));
        Assert.Equal("A team name must be 2 to 20 characters.", Run(alice, "team", "create", "x"));
        Assert.StartsWith("You made team Red", Run(alice, "team", "create", "Red"));
        Assert.Equal("There is already a team called Red.", Run(bob, "team", "create", "red"));
        Assert.Equal("You are already in team Red. Leave it first.", Run(alice, "team", "create", "Blue"));

        // Closed: nobody joins without being asked.
        Assert.Equal("Team Red has not invited you.", Run(bob, "team", "join", "Red"));
        _sent.Clear();
        Assert.Equal("You invited bob to team Red.", Run(alice, "team", "invite", "bob"));
        Assert.Contains(_sent, s => s.To == bob && s.Message is TextEvent t && t.Text.Contains("invites you to team Red"));
        Assert.Equal("There is no player called zed.", Run(alice, "team", "invite", "zed"));
        Assert.StartsWith("You joined team Red.", Run(bob, "team", "join", "red"));
        Assert.Equal("You are already in team Red.", Run(bob, "team", "join", "Red"));

        Assert.Equal("You are in team Red, you lead it. 2 members, 2 online. It is invitation only.", Run(alice, "team"));
        Assert.Equal("Team Red: alice (you), leader, online; bob, online.", Run(alice, "team", "list"));

        // The leader alone removes people, and not themselves.
        Assert.Equal("Only alice, who leads team Red, can remove somebody from it.", Run(bob, "team", "kick", "alice"));
        Assert.Equal("To leave your own team, say /team leave.", Run(alice, "team", "kick", "alice"));
        Assert.Equal("carol is not in team Red.", Run(alice, "team", "kick", "carol"));
        _sent.Clear();
        Assert.Equal("You removed bob from team Red.", Run(alice, "team", "kick", "bob"));
        Assert.Contains(_sent, s => s.To == bob && s.Message is TextEvent t && t.Text == "alice removed you from team Red.");
        Assert.StartsWith("You are not in a team.", Run(bob, "team"));

        // Open, and anybody may come in; the leader leaving hands it on; the last one out ends it.
        Assert.Equal("You are not in a team.", Run(carol, "team", "open"));
        Assert.Equal("Team Red is open: anybody may join.", Run(alice, "team", "open"));
        Assert.StartsWith("You joined team Red.", Run(carol, "team", "join", "Red"));
        Assert.Equal("Only alice, who leads team Red, can change who may join it.", Run(carol, "team", "close"));
        Assert.Equal("You left team Red.", Run(alice, "team", "leave"));
        Assert.Equal("carol", _server.Teams!.Get("Red")!.Leader);
        Assert.Equal("You left team Red, and it is gone: you were the last in it.", Run(carol, "team", "leave"));
        Assert.Null(_server.Teams.Get("Red"));
        Assert.Equal("There are no teams yet. Say /team create and a name to start one.", Run(bob, "team", "list"));
    }

    [Fact]
    public void TeamsAreWrittenDownAndOutlastARestart()
    {
        var alice = Player("alice", 1, new Vector3(0, 2, 0));
        var bob = Player("bob", 2, new Vector3(4, 2, 0));
        Run(alice, "team", "create", "Owls");
        Run(alice, "team", "invite", "bob");
        Run(alice, "team", "invite", "dave");          // not on: the invitation keeps until they are
        Run(bob, "team", "join", "Owls");

        var reread = new TeamRepository(_teamsPath);
        var owls = reread.Get("owls")!;
        Assert.Equal("Owls", owls.Name);
        Assert.Equal("alice", owls.Leader);
        Assert.Equal(new[] { "alice", "bob" }, owls.Members);
        Assert.Equal(new[] { "dave" }, owls.Invited);
        Assert.Equal("Team Owls has not invited you.", reread.Join("Owls", "carol"));
        Assert.Null(reread.Join("Owls", "dave"));
        Assert.Equal("Owls", new TeamRepository(_teamsPath).NameOf("DAVE"));
    }

    [Fact]
    public void ATeamHoldsSixteen()
    {
        var teams = _server.Teams!;
        Assert.Null(teams.Create("Big", "p0"));
        teams.SetOpen("Big", true);
        for (int i = 1; i < TeamRepository.MaxMembers; i++) Assert.Null(teams.Join("Big", $"p{i}"));
        Assert.Equal(16, teams.Get("Big")!.Members.Count);
        Assert.Equal("Team Big is full, with 16 members.", teams.Join("Big", "p16"));
    }

    [Fact]
    public void TeamChatReachesTheTeamAndNobodyElse()
    {
        var alice = Player("alice", 1, new Vector3(0, 2, 0));
        var bob = Player("bob", 2, new Vector3(4, 2, 0));
        var carol = Player("carol", 3, new Vector3(8, 2, 0));
        Assert.Equal("You are not in a team.", Run(alice, "t", "hello"));
        Run(alice, "team", "create", "Red");
        Run(alice, "team", "invite", "bob");
        Run(bob, "team", "join", "Red");

        _sent.Clear();
        Run(bob, "t", "on", "my", "way");
        var lines = _sent.Where(s => s.Message is ChatMessage).ToList();
        Assert.Equal(new[] { "alice", "bob" }, lines.Select(s => s.To.Username).OrderBy(n => n));
        Assert.All(lines, s =>
        {
            var c = (ChatMessage)s.Message;
            Assert.Equal(ChatChannel.Team, c.Channel);
            Assert.Equal("bob", c.Sender);
            Assert.Equal("on my way", c.Text);
        });

        _sent.Clear();
        Run(alice, "team", "chat", "regroup");
        Assert.DoesNotContain(_sent, s => s.To == carol);
        Assert.Equal(2, _sent.Count(s => s.Message is ChatMessage { Text: "regroup", Channel: ChatChannel.Team }));
    }

    [Fact]
    public void APlayersBodyIsAPlayerBeaconAndWearsItsTeamReSentWhenItChanges()
    {
        var alice = Player("alice", 1, new Vector3(0, 2, 0));
        var bob = Player("bob", 2, new Vector3(4, 2, 0));

        var before = Definition(alice);
        Assert.Equal(Beacons.Player, before.Identity.BeaconCategory);
        Assert.Equal("", before.Team);

        Run(alice, "team", "create", "Red");
        Assert.Equal("Red", Definition(alice).Team);
        Assert.Contains(alice.Entity.Id, _server.PendingDefinitionResends);
        Assert.DoesNotContain(bob.Entity.Id, _server.PendingDefinitionResends);

        Run(alice, "team", "invite", "bob");
        Run(bob, "team", "join", "Red");
        Assert.Equal("Red", Definition(bob).Team);
        Assert.Contains(bob.Entity.Id, _server.PendingDefinitionResends);

        Run(alice, "team", "kick", "bob");
        Assert.Equal("", Definition(bob).Team);

        // Only a player is a player beacon: a wall is still nothing.
        Assert.True(_maps.TryGetMap("default", out var world, out _, out _, out _));
        foreach (var def in EntityDefinitionFactory.StaticDefinitions(world))
            Assert.NotEqual(Beacons.Player, def.Identity.BeaconCategory);
    }

    [Fact]
    public void ThePlayerCategoryIsOnByDefaultAndTheMapMayForbidIt()
    {
        Assert.True(Beacons.IsCategory("player"));
        Assert.Equal(Beacons.Policy.DefaultOn, Beacons.ReadPolicies(null)[Beacons.Player]);
        Assert.Equal(Beacons.Policy.Forbidden, Beacons.ReadPolicies(new[] { "player=forbidden" })[Beacons.Player]);
    }

    private sealed class KnownUsers : IUserRepository
    {
        private readonly HashSet<string> _names;
        public KnownUsers(params string[] names) => _names = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        public UserData? GetUser(string username) =>
            _names.Contains(username) ? new UserData { Username = username.ToLowerInvariant(), Role = UserRole.Player } : null;
        public bool AddUser(string username, string password, UserRole role) => _names.Add(username);
        public bool VerifyPassword(string username, string password) => false;
    }
}

/// <summary>
/// Player beacons, on the client: every other player calls, a teammate on another instrument, and
/// your own body never.
/// </summary>
public class PlayerBeaconTests
{
    private static EntitySnapshot Body(int id, Vector3 feet, string team)
        => new()
        {
            Id = id,
            Definition = new EntityDefinition
            {
                EntityId = id,
                Type = EntityType.Player,
                Collider = new ColliderComponent { Shape = ColliderShape.Cylinder, Size = new Vector3(0.6f, 1.8f, 0.6f), IsSolid = true },
                Identity = new IdentityComponent { BeaconCategory = Beacons.Player },
                Team = team,
                Moves = true,
            },
            Transform = new Transform { Position = feet, Rotation = Quaternion.Identity, Scale = Vector3.One },
        };

    private static WorldSnapshot World(params EntitySnapshot[] bodies)
    {
        var w = new WorldSnapshot();
        foreach (var b in bodies) { w.Entities[b.Id] = b; w.DynamicEntities.Add(b); }
        return w;
    }

    private static readonly Vector3 Feet = Vector3.Zero, Ear = new(0f, 1.7f, 0f);

    /// <summary>Every blip over four seconds, by the source it came from.</summary>
    private static List<SpatialEmitter> Listen(WorldSnapshot world, int self, BeaconPreferences? prefs = null)
    {
        var mixer = new EmitterRecordingProvider();
        var audio = new AudioEngineFacade(mixer);
        audio.InitializeForTest();
        var aids = new BeaconAids(audio, prefs ?? BeaconPreferences.InMemory());
        for (int i = 0; i < 40; i++)
        {
            audio.UpdateListener(Ear, Quaternion.Identity, Vector3.Zero, -1);
            aids.Update(world, Ear, 10 + i * 0.1, self);
            for (int k = 0; k < 3; k++) audio.PumpForTest();
        }
        return mixer.Played;
    }

    private static readonly Vector3 Mate = new(3f, 0f, 0f), Stranger = new(-3f, 0f, 2f), Loner = new(0f, 0f, -4f);

    private static bool From(SpatialEmitter e, Vector3 feet) => Vector3.Distance(e.Position, feet + new Vector3(0, OtherBodies.HeadHeight, 0)) < 1e-3f;

    [Fact]
    public void ATeammateCallsInTheTeamToneOthersInThePlayersAndYouNever()
    {
        var world = World(Body(1, Feet, "Red"), Body(2, Mate, "red"), Body(3, Stranger, "Blue"), Body(4, Loner, ""));
        var blips = Listen(world, self: 1);

        Assert.NotEmpty(blips);
        Assert.DoesNotContain(blips, e => From(e, Feet));
        Assert.All(blips.Where(e => From(e, Mate)), e => Assert.Equal("SYNTH/beacon_player_team", e.SoundId));
        Assert.All(blips.Where(e => From(e, Stranger)), e => Assert.Equal("SYNTH/beacon_player_call", e.SoundId));
        Assert.All(blips.Where(e => From(e, Loner)), e => Assert.Equal("SYNTH/beacon_player_call", e.SoundId));
        Assert.Contains(blips, e => From(e, Mate));
        Assert.Contains(blips, e => From(e, Stranger));
        Assert.Contains(blips, e => From(e, Loner));
    }

    /// <summary>Two players in no team are not on the same side.</summary>
    [Fact]
    public void WithNoTeamNobodyIsATeammate()
    {
        var world = World(Body(1, Feet, ""), Body(2, Mate, ""), Body(3, Stranger, "Blue"));
        var blips = Listen(world, self: 1);
        Assert.NotEmpty(blips);
        Assert.All(blips, e => Assert.Equal("SYNTH/beacon_player_call", e.SoundId));
    }

    [Fact]
    public void PlayerBeaconsCanBeSwitchedOff()
    {
        var prefs = BeaconPreferences.InMemory();
        var aids = new BeaconAids(new AudioEngineFacade(new EmitterRecordingProvider()), prefs);
        Assert.Equal("Player beacons off.", aids.Command(new[] { "players", "off" }));
        Assert.Empty(Listen(World(Body(1, Feet, ""), Body(2, Mate, "")), self: 1, prefs));
    }

    /// <summary>The fundamental of a stretch of a tone: the strongest bin of a fine Goertzel sweep.</summary>
    internal static float Fundamental(float[] x, int rate, int from, int to, float lo = 200f, float hi = 900f)
    {
        float best = lo; double bestE = -1;
        for (float hz = lo; hz <= hi; hz += 0.5f)
        {
            double w = 2 * Math.PI * hz / rate, c = 2 * Math.Cos(w), s1 = 0, s2 = 0;
            for (int i = from; i < to; i++) { double s = x[i] + c * s1 - s2; s2 = s1; s1 = s; }
            double e = s1 * s1 + s2 * s2 - c * s1 * s2;
            if (e > bestE) { bestE = e; best = hz; }
        }
        return best;
    }

    /// <summary>
    /// The player's call falls a minor third, G4 to E4, and the teammate's is the same call at the same
    /// pitch on another instrument: the same notes, the same length, as loud, and a different spectrum
    /// (the triangle's 3rd harmonic is there in the teammate's and not in the player's).
    /// </summary>
    [Fact]
    public void TheTeammatesCallIsThePlayersOnAnotherInstrument()
    {
        const int sr = 48000;
        var player = BeaconAids.Tone(Beacons.Player, sr);
        var mate = BeaconAids.TeammateTone(sr);
        Assert.Equal(player.Length, mate.Length);
        int split = sr * 15 / 100, end = sr * 30 / 100;
        Assert.InRange(Fundamental(player, sr, 0, split), 388f, 396f);
        Assert.InRange(Fundamental(player, sr, split + sr / 50, end), 326f, 333f);
        Assert.InRange(Fundamental(mate, sr, 0, split), 388f, 396f);
        Assert.InRange(Fundamental(mate, sr, split + sr / 50, end), 326f, 333f);

        double Rms(float[] x) => Math.Sqrt(x.Sum(v => (double)v * v) / x.Length);
        // As loud, or as near as the 0.9 peak every beacon has allows.
        double louder = 20 * Math.Log10(Rms(mate) / Rms(player));
        Assert.InRange(louder, -2.0, 0.1);

        // The 3rd harmonic of G4, 1176 Hz, against the fundamental, in the first note.
        double Band(float[] x, float hz)
        {
            double w = 2 * Math.PI * hz / sr, c = 2 * Math.Cos(w), s1 = 0, s2 = 0;
            for (int i = 0; i < split; i++) { double s0 = x[i] + c * s1 - s2; s2 = s1; s1 = s0; }
            return Math.Sqrt(s1 * s1 + s2 * s2 - c * s1 * s2);
        }
        double third(float[] x) => 20 * Math.Log10(Band(x, 1176f) / Band(x, 392f));
        Assert.True(third(mate) > third(player) + 15, $"third harmonic {third(mate):F1} dB against the player's {third(player):F1}");

        foreach (var x in new[] { player, mate })
        {
            Assert.True(x.Max(MathF.Abs) < 0.99f, "clipped");
            Assert.Equal(0f, x[0]);
            Assert.True(MathF.Abs(x[48]) < 0.2f, "a click: it did not rise over the first millisecond");
        }
    }

    [Fact]
    public void TeamChatIsSaidToYouWithItsOwnSound()
    {
        var msg = new ChatMessage { Sender = "bob", Text = "on my way", Channel = ChatChannel.Team };
        Assert.Equal("bob to team: on my way", ChatManager.Format(msg));
        Assert.Equal(ChatBufferType.Private, ChatManager.BufferFor(ChatChannel.Team));
        var team = UiSounds.Render(UiCue.ChatTeam);
        Assert.InRange(team.Max(MathF.Abs), 0.2f, 0.95f);
        Assert.NotEqual(UiSounds.Render(UiCue.ChatPrivate).Length, team.Length);
    }
}

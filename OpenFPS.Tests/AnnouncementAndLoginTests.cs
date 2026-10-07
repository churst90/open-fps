using System.Numerics;
using Arch.Core;
using LiteNetLib;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.Core;
using OpenFPS.Client.Core.Platform;
using OpenFPS.Client.Core.Session;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;

namespace OpenFPS.Tests;

/// <summary>
/// Three faults a live session found: crossing a doorway read the portal prefab's authoring notes aloud
/// (every entity is named, and the announcer spoke every name within 3 m); a rejected login was talked
/// over by the head's focus announcement; and a retry sent nothing, since LiteNetLib's <c>Connect</c>
/// returns the existing peer and fires no event.
/// </summary>
public class AnnouncementAndLoginTests
{
    private static string PrefabDirectory => Path.Combine(AppContext.BaseDirectory, "prefabs");
    private static string MapDirectory => Path.Combine(AppContext.BaseDirectory, "maps");

    // ── 1. Only things that earn it are announced ───────────────────────────────────────────────

    [Theory]
    // The acoustic scaffolding and the architecture: named, and silent.
    [InlineData("portal", false)]
    [InlineData("acoustic_region", false)]
    [InlineData("concrete_wall", false)]
    [InlineData("concrete_floor", false)]
    [InlineData("building_box", false)]
    [InlineData("pillar_round", false)]
    [InlineData("sound_emitter", false)]
    // The things a player actually encounters.
    [InlineData("sword", true)]
    [InlineData("chirp_beacon", true)]
    [InlineData("space_megaphone", true)]
    public void ShippedPrefabsAnnounceOnlyWhatAPlayerCanEncounter(string prefabId, bool expected)
    {
        var repo = new PrefabRepository(PrefabDirectory);
        using var world = World.Create();
        var entity = repo.Spawn(world, prefabId, Vector3.Zero);

        Assert.Equal(expected, world.Get<IdentityComponent>(entity).Announce);
    }

    [Fact]
    public void AnExplicitAnnounceOverridesTheTypeDefault()
    {
        string dir = Path.Combine(Path.GetTempPath(), "openfps-announce-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // A staircase is a StaticObject — silent by default — that an author wants narrated.
            File.WriteAllText(Path.Combine(dir, "stairs.json"), """
            { "Id": "stairs", "Name": "Stone Staircase", "Type": "StaticObject", "Announce": true,
              "Material": "Concrete", "ColliderSize": { "X": 2, "Y": 3, "Z": 4 } }
            """);
            // A beacon is announced by default; this one is a background ambience the author wants quiet.
            File.WriteAllText(Path.Combine(dir, "hum.json"), """
            { "Id": "hum", "Name": "Ventilation Hum", "Type": "Beacon", "Announce": false,
              "Material": "None", "HasEmitter": true, "SoundId": "BEACONS/low_osc", "Range": 10 }
            """);

            var repo = new PrefabRepository(dir);
            using var world = World.Create();

            Assert.True(world.Get<IdentityComponent>(repo.Spawn(world, "stairs", Vector3.Zero)).Announce);
            Assert.False(world.Get<IdentityComponent>(repo.Spawn(world, "hum", Vector3.Zero)).Announce);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void TheAnnounceFlagSurvivesTheTripToTheClient()
    {
        // On the real shipped map: what the server decides must be on the wire.
        var prefabs = new PrefabRepository(PrefabDirectory);
        var maps = new MapRepository(MapDirectory);
        var manager = new MapManager(maps, prefabs);
        manager.Initialize();
        Assert.True(manager.TryGetMap("default", out World world, out _, out _, out _));

        var defs = EntityDefinitionFactory.StaticDefinitions(world);

        // Every portal and every region volume on the map is silent...
        var scaffolding = defs.Where(d => d.Portal.ApertureSize > 0 || d.Region.RoomSize.X > 0).ToList();
        Assert.NotEmpty(scaffolding);
        Assert.All(scaffolding, d => Assert.False(d.Identity.Announce,
            $"'{d.Identity.Name}' is acoustic scaffolding and must not be announced."));

        // ...and so is the architecture: nothing on the default map is announced.
        Assert.All(defs.Where(d => d.Identity.Announce),
            d => Assert.True(d.Type is EntityType.Item or EntityType.NPC or EntityType.Beacon,
                $"'{d.Identity.Name}' is a {d.Type} and should not be announcing itself."));
    }

    [Fact]
    public void WalkingPastScaffoldingSaysNothing_ButAnItemIsAnnounced()
    {
        var (session, speech, _) = NewSession();
        session.HandleMessage(new PlayerSpawned { EntityId = 1 });

        // Both are a stride away from the spawn point, well inside the interaction radius.
        session.HandleMessage(Definition(10, EntityType.StaticObject, "Acoustic Portal",
            "An opening between two acoustic regions. Place it in the gap between walls and set " +
            "RegionAId / RegionBId on the MAP entity...", announce: false));
        session.HandleMessage(Definition(11, EntityType.Item, "Rusted Sword", "Notched, but serviceable.",
            announce: true));

        speech.Spoken.Clear();
        // The proximity scan counts ticks, so the steps use a tiny delta: with no floor under a hand-built
        // world, a real second would drop the player out of range first.
        for (int i = 0; i < PhysicsConstants.TickRate; i++) session.SimStep(0.001f);

        Assert.False(speech.Said("Acoustic Portal"));
        Assert.False(speech.Said("RegionAId"));   // the authoring notes, read aloud mid-stride
        Assert.True(speech.Said("Rusted Sword"));
    }

    // ── 2. A rejected login is heard, and the head is told ──────────────────────────────────────

    [Fact]
    public void ARejectedLoginIsSpokenAndRaisedToTheHead()
    {
        var (session, speech, shell) = NewSession();

        string? raised = null;
        session.LoginFailed += reason => raised = reason;
        session.LoginSucceeded += _ => Assert.Fail("A rejected login must not report success.");

        session.HandleMessage(new LoginResponse { Success = false, Message = "Invalid Credentials" });

        Assert.Equal("Invalid Credentials", raised);
        Assert.True(speech.Said("Login failed"));
        Assert.True(speech.Said("Invalid Credentials"));
        // A rejection is not a load: the head must not be told to put up a loading screen.
        Assert.Empty(shell.Loading);
    }

    [Fact]
    public void AnAcceptedLoginRaisesSuccessWithTheUsernameTheServerAccepted()
    {
        var (session, speech, shell) = NewSession();

        string? accepted = null;
        session.LoginSucceeded += user => accepted = user;
        session.LoginFailed += _ => Assert.Fail("An accepted login must not report failure.");

        session.HandleMessage(new LoginResponse { Success = true, Username = "cody" });

        Assert.Equal("cody", accepted);
        // Nothing spoken yet: where you are is said once, on arrival, with the map's name.
        Assert.Empty(speech.Spoken);
        Assert.NotEmpty(shell.Loading);
    }

    // ── 3. Retrying a rejected login actually retries ───────────────────────────────────────────

    [Fact]
    public void ConnectingWhileAlreadyConnectedReRunsTheHandshake()
    {
        // Over the loopback, because the fault was LiteNetLib's contract: Connect() returns the existing
        // peer and fires no OnPeerConnected.
        var listener = new AcceptingListener();
        var server = new NetManager(listener) { AutoRecycle = true };
        Assert.True(server.Start(0));

        var client = new ClientNetworkService();
        int connectedEvents = 0;
        client.OnConnected += () => connectedEvents++;
        client.Start();

        try
        {
            client.Connect("127.0.0.1", server.LocalPort);
            Assert.True(PumpUntil(() => client.IsConnected, server, client),
                "the client never connected to the loopback server");
            Assert.Equal(1, connectedEvents);

            // The retry after a rejected password.
            client.Connect("127.0.0.1", server.LocalPort);
            Assert.Equal(2, connectedEvents);
            Assert.True(client.IsConnected);
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public void ConnectingToAnotherServerLeavesTheFirst()
    {
        // A login refused by the VPS left the peer up; picking the dev server then "re-ran the
        // handshake" down that same connection, so every retry went to the VPS and was refused with
        // its build (Cody, 2026-10-02). A different server is a new connection.
        var first = new NetManager(new AcceptingListener()) { AutoRecycle = true };
        var second = new NetManager(new AcceptingListener()) { AutoRecycle = true };
        Assert.True(first.Start(0));
        Assert.True(second.Start(0));
        var client = new ClientNetworkService();
        var failures = new List<string>();
        client.OnConnectionFailed += failures.Add;
        client.Start();
        try
        {
            client.Connect("127.0.0.1", first.LocalPort);
            Assert.True(PumpUntil(() => client.IsConnected, first, client), "never connected to the first server");

            client.Connect("127.0.0.1", second.LocalPort);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline && !(second.ConnectedPeersCount == 1 && first.ConnectedPeersCount == 0 && client.IsConnected))
            {
                first.PollEvents(); second.PollEvents(); client.Poll();
                Thread.Sleep(5);
            }
            Assert.Equal(1, second.ConnectedPeersCount);
            Assert.Equal(0, first.ConnectedPeersCount);
            Assert.True(client.IsConnected);
            Assert.Empty(failures);   // leaving the first is not a disconnect worth announcing
        }
        finally
        {
            first.Stop();
            second.Stop();
        }
    }

    [Fact]
    public void ASecondConnectWhileOneIsStillInFlightIsReportedRatherThanQueued()
    {
        var client = new ClientNetworkService();
        var notices = new List<string>();
        int connectedEvents = 0;
        client.OnConnected += () => connectedEvents++;
        client.OnConnectionNotice += notices.Add;
        client.Start();

        // Port 1 on loopback answers nothing, so the attempt stays in flight for the whole test.
        client.Connect("127.0.0.1", 1);
        client.Connect("127.0.0.1", 1);

        Assert.Single(notices);
        Assert.Contains("Still connecting", notices[0]);
        Assert.Equal(0, connectedEvents);
    }

    // ── Harness ─────────────────────────────────────────────────────────────────────────────────

    private static EntityDefinition Definition(int id, EntityType type, string name, string description, bool announce)
    {
        var def = new EntityDefinition
        {
            EntityId = id,
            Type = type,
            Transform = new Transform { Position = new Vector3(1, 0, 1), Rotation = Quaternion.Identity, Scale = Vector3.One }
        };
        def.Identity = new IdentityComponent { Name = name, Description = description, Announce = announce };
        return def;
    }

    /// <summary>Pumps both ends until the condition holds or the deadline passes. Returns whether it held.</summary>
    private static bool PumpUntil(Func<bool> condition, NetManager server, ClientNetworkService client)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            server.PollEvents();
            client.Poll();
            if (condition()) return true;
            Thread.Sleep(5);
        }
        return condition();
    }

    private sealed class AcceptingListener : INetEventListener
    {
        public void OnConnectionRequest(ConnectionRequest request) => request.AcceptIfKey("OpenFPS_Key");
        public void OnPeerConnected(NetPeer peer) { }
        public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo) { }
        public void OnNetworkError(System.Net.IPEndPoint endPoint, System.Net.Sockets.SocketError socketError) { }
        public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod) { }
        public void OnNetworkReceiveUnconnected(System.Net.IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType) { }
        public void OnNetworkLatencyUpdate(NetPeer peer, int latency) { }
    }

    private sealed class FakeSpeech : ISpeechOutput
    {
        public readonly List<string> Spoken = new();
        public string BackendName => "fake";
        public bool Initialize() => true;
        public void Speak(string text, bool interrupt = true) => Spoken.Add(text);
        public void Interrupt() { }
        public void Dispose() { }
        public bool Said(string fragment) => Spoken.Any(s => s.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class FakeShell : IClientShell
    {
        public readonly List<string> Loading = new();
        public bool IsGameInputActive { get; set; } = true;
        public event Action<string>? CommandEntered;
        public void ShowLoading(string status, bool speak = true) => Loading.Add(status);
        public void UpdateLoadingStatus(string text, int percent) => Loading.Add($"{text} ({percent})");
        public void EnterGame() { }
        public void OpenCommandConsole() { }
        public void ShowGameMenu(Action<GameMenuChoice> chosen) { }
        public void ReturnToMenu() { }
        public void Quit() { }
        public void Raise(string text) => CommandEntered?.Invoke(text);
    }

    private static (ClientGameSession Session, FakeSpeech Speech, FakeShell Shell) NewSession()
    {
        AcousticRegistry.Initialize();
        var speech = new FakeSpeech();
        var shell = new FakeShell();
        var session = new ClientGameSession(
            new ClientNetworkService(), speech, shell, new AudioEngineFacade(),
            microphone: new NullMicrophoneCapture("no microphone here"),
            enableAudio: false);
        return (session, speech, shell);
    }
}

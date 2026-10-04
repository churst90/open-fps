using System.Collections.Concurrent;
using LiteNetLib;
using MemoryPack;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.Core;
using OpenFPS.Client.Core.Platform;
using OpenFPS.Client.Core.Session;
using OpenFPS.Common;
using OpenFPS.Common.Networking;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// The shared connection life of a session, which both heads now take from ClientGameSession: a
/// dropped connection is said, the world is torn down and the login retried; a retry that never
/// answers ends on the main menu; the game menu logs out before it leaves; and the map arrives in
/// batches. Run against a real LiteNetLib server on the loopback, because the events that drive all
/// of this are LiteNetLib's.
/// </summary>
public class ConnectionLifecycleTests
{
    [Fact]
    public void ALostConnectionIsSaidTheWorldGoesAndTheLoginIsRetried()
    {
        using var server = new FakeServer();
        var (session, speech, shell, network) = NewSession();
        session.ReconnectInterval = TimeSpan.FromMilliseconds(200);

        LogIn(session, server, network);
        session.HandleMessage(new PlayerSpawned { EntityId = 5 });
        Assert.True(session.IsInGame);

        server.Net.DisconnectAll();
        Assert.True(Pump(() => session.IsReconnecting, server, network, session), "the drop was never noticed");
        Assert.False(session.IsInGame);
        Assert.Contains(speech.Spoken, s => s.Contains("Disconnected") && s.Contains("Reconnecting"));

        // The server is still there: the retry gets in and logs in again with the same name.
        Assert.True(Pump(() => server.Logins.Count == 2 && !session.IsReconnecting, server, network, session),
            "the session never logged back in");
        Assert.Equal("cody", server.Logins[1]);
        Assert.Contains("Reconnected.", speech.Spoken);
        Assert.Equal(0, shell.ReturnedToMenu);
    }

    [Fact]
    public void ARetryThatNeverAnswersEndsOnTheMainMenu()
    {
        var server = new FakeServer();
        var (session, speech, shell, network) = NewSession();
        session.ReconnectInterval = TimeSpan.FromMilliseconds(100);
        session.ReconnectFor = TimeSpan.FromMilliseconds(600);

        LogIn(session, server, network);
        session.HandleMessage(new PlayerSpawned { EntityId = 5 });
        server.Dispose();   // gone for good

        Assert.True(Pump(() => shell.ReturnedToMenu == 1, server, network, session, seconds: 15),
            "the session never gave up");
        Assert.False(session.IsReconnecting);
        Assert.Contains(speech.Spoken, s => s.Contains("Could not reconnect"));
    }

    [Fact]
    public void MainMenuLogsOutFadesAndReturnsWithoutSayingDisconnected()
    {
        using var server = new FakeServer();
        var (session, speech, shell, network) = NewSession();
        LogIn(session, server, network);
        session.HandleMessage(new PlayerSpawned { EntityId = 5 });

        session.Input.SetKey(GameKey.Escape, true);
        session.SimStep(1f / 30f);
        Assert.NotNull(shell.Menu);
        shell.Menu!(GameMenuChoice.MainMenu);

        Assert.True(Pump(() => shell.ReturnedToMenu == 1, server, network, session), "never got back to the menu");
        Assert.True(Pump(() => server.LoggedOut, server, network, session), "the server was never told");
        Assert.False(session.IsInGame);
        Assert.False(session.IsReconnecting);
        Assert.DoesNotContain(speech.Spoken, s => s.Contains("Disconnected"));
        Assert.Equal(0, shell.Quits);
    }

    [Fact]
    public void KeepPlayingLeavesEverythingAsItWas()
    {
        using var server = new FakeServer();
        var (session, _, shell, network) = NewSession();
        LogIn(session, server, network);
        session.HandleMessage(new PlayerSpawned { EntityId = 5 });

        session.Input.SetKey(GameKey.Escape, true);
        session.SimStep(1f / 30f);
        shell.Menu!(GameMenuChoice.KeepPlaying);
        session.Tick();

        Assert.True(session.IsInGame);
        Assert.True(network.IsConnected);
        Assert.False(server.LoggedOut);
    }

    [Fact]
    public void ABatchOfDefinitionsIsFiledAsIfEachCameAlone()
    {
        var (session, _, shell, _) = NewSession();
        session.HandleMessage(new MapManifest { MapName = "test", ExpectedEntityCount = 300, WorldSize = new System.Numerics.Vector3(100, 20, 100) });

        var batch = new EntityDefinitionBatch();
        for (int i = 1; i <= 300; i++) batch.Definitions.Add(new EntityDefinition { EntityId = i });
        // Through the wire format, as the server sends it.
        var bytes = MemoryPackSerializer.Serialize<IMessage>(batch);
        var back = Assert.IsType<EntityDefinitionBatch>(MemoryPackSerializer.Deserialize<IMessage>(bytes));
        session.HandleMessage(back);

        Assert.Equal(300, session.World.EntityCount);
        Assert.Contains(shell.Loading, s => s.StartsWith("Receiving entities: 300/300"));
    }

    [Fact]
    public void TheFadeIsEvenInLoudnessAndEndsWhereItWasAsked()
    {
        Assert.Equal(0f, ClientGameSession.FadeGain(0f, 1f, 0f));
        Assert.Equal(1f, ClientGameSession.FadeGain(0f, 1f, 1f), 3);
        // Half way through a 60 dB fade-in is -30 dB, not half amplitude.
        Assert.Equal(MathF.Pow(10f, -30f / 20f), ClientGameSession.FadeGain(0f, 1f, 0.5f), 4);
        Assert.Equal(0f, ClientGameSession.FadeGain(1f, 0f, 1f));
    }

    [Fact]
    public void AServerLoggedInToByHandIsRememberedOnce()
    {
        string path = Path.Combine(Path.GetTempPath(), $"openfps-remember-{Guid.NewGuid():N}.json");
        try
        {
            var settings = new ClientSettings();
            settings.Remember("example.org:4000", "cody", "secret", rememberPassword: false, path);
            settings.Remember("example.org:4000", "cody", "secret", rememberPassword: true, path);
            var s = Assert.Single(settings.Servers);
            Assert.Equal(("example.org", 4000), (s.Host, s.Port));
            Assert.True(s.Preferred);
            Assert.Equal("secret", s.Password);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ASecondAccountOnASavedServerIsItsOwnEntryAndThePreferredOneStays()
    {
        // Cody: "you will have 2 entries for the same server and 2 different accounts ... pick the one you
        // want preferred."
        string path = Path.Combine(Path.GetTempPath(), $"openfps-remember-{Guid.NewGuid():N}.json");
        try
        {
            var settings = new ClientSettings();
            settings.Remember("example.org:4000", "cody", "secret", rememberPassword: true, path);
            settings.Servers[0].Name = "Home";
            settings.Remember("example.org:4000", "second", "other", rememberPassword: true, path);
            Assert.Equal(2, settings.Servers.Count);
            Assert.Equal("cody", settings.Preferred!.Username);
            Assert.Equal("secret", settings.Servers[0].Password);
            Assert.Equal(("Home", "second", "other"), (settings.Servers[1].Name, settings.Servers[1].Username, settings.Servers[1].Password));
            Assert.False(settings.Servers[1].Preferred);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AnAccountThatCannotBeMadeLeavesNothingConnected()
    {
        // Cody: "when I clicked quit, it said disconnected from server. huh? if I couldn't create an
        // account, why did it connect?"
        using var server = new FakeServer();
        var (session, speech, shell, network) = NewSession();
        string? failed = null;
        session.ConnectFailed += reason => failed = reason;
        session.Connect($"127.0.0.1:{server.Port}", "taken", "pw", register: true);
        Assert.True(Pump(() => failed != null, server, network, session), "the refusal was never heard");
        Assert.Contains("taken", failed);
        Assert.True(Pump(() => !network.IsConnected, server, network, session), "still connected after the refusal");
        // Nothing to announce afterwards: the drop was ours.
        for (int i = 0; i < 40; i++) { server.Poll(); network.Poll(); session.Tick(); Thread.Sleep(5); }
        Assert.DoesNotContain(speech.Spoken, s => s.Contains("Disconnected"));
        Assert.Empty(server.Logins);
    }

    // ── Harness ─────────────────────────────────────────────────────────────────────────────────

    private static void LogIn(ClientGameSession session, FakeServer server, ClientNetworkService network)
    {
        bool accepted = false;
        session.LoginSucceeded += _ => accepted = true;
        session.Connect($"127.0.0.1:{server.Port}", "cody", "pw", register: false);
        Assert.True(Pump(() => accepted, server, network, session), "the login was never accepted");
    }

    private static bool Pump(Func<bool> condition, FakeServer server, ClientNetworkService network,
                             ClientGameSession session, double seconds = 5)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            server.Poll();
            network.Poll();
            session.Tick();
            if (condition()) return true;
            Thread.Sleep(5);
        }
        return condition();
    }

    private static (ClientGameSession, Speech, Shell, ClientNetworkService) NewSession()
    {
        AcousticRegistry.Initialize();
        var speech = new Speech();
        var shell = new Shell();
        var network = new ClientNetworkService();
        var session = new ClientGameSession(network, speech, shell, new AudioEngineFacade(),
            microphone: new NullMicrophoneCapture("no microphone here"), enableAudio: false);
        network.OnMessageReceived += session.HandleMessage;
        return (session, speech, shell, network);
    }

    /// <summary>A loopback server that accepts, answers every login, and notes a logout.</summary>
    private sealed class FakeServer : INetEventListener, IDisposable
    {
        public readonly NetManager Net;
        public readonly List<string> Logins = new();
        public volatile bool LoggedOut;
        public int Port => Net.LocalPort;

        public FakeServer()
        {
            Net = new NetManager(this) { AutoRecycle = true };
            Assert.True(Net.Start(0));
        }

        public void Poll() { if (Net.IsRunning) Net.PollEvents(); }
        public void Dispose() { if (Net.IsRunning) Net.Stop(); }

        public void OnConnectionRequest(ConnectionRequest request) => request.AcceptIfKey("OpenFPS_Key");
        public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
        {
            var msg = MemoryPackSerializer.Deserialize<IMessage>(reader.GetRemainingBytes());
            if (msg is LoginRequest login)
            {
                Logins.Add(login.Username);
                peer.Send(MemoryPackSerializer.Serialize<IMessage>(new LoginResponse { Success = true, Username = login.Username }),
                          DeliveryMethod.ReliableOrdered);
            }
            else if (msg is RegisterRequest register)
            {
                // "taken" is already an account here; any other name is made.
                bool made = register.Username != "taken";
                peer.Send(MemoryPackSerializer.Serialize<IMessage>(new RegisterResponse
                          { Success = made, Message = made ? "" : "That username is taken." }), DeliveryMethod.ReliableOrdered);
            }
            else if (msg is LogoutRequest) LoggedOut = true;
        }
        public void OnPeerConnected(NetPeer peer) { }
        public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo) { }
        public void OnNetworkError(System.Net.IPEndPoint endPoint, System.Net.Sockets.SocketError socketError) { }
        public void OnNetworkReceiveUnconnected(System.Net.IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType) { }
        public void OnNetworkLatencyUpdate(NetPeer peer, int latency) { }
    }

    private sealed class Speech : ISpeechOutput
    {
        public readonly ConcurrentQueue<string> Queue = new();
        public List<string> Spoken => Queue.ToList();
        public string BackendName => "fake";
        public bool Initialize() => true;
        public void Speak(string text, bool interrupt = true) => Queue.Enqueue(text);
        public void Interrupt() { }
        public void Dispose() { }
    }

    private sealed class Shell : IClientShell
    {
        public readonly List<string> Loading = new();
        public Action<GameMenuChoice>? Menu;
        public int ReturnedToMenu, Quits;
        public bool IsGameInputActive { get; set; } = true;
        public event Action<string>? CommandEntered { add { } remove { } }
        public void ShowLoading(string status, bool speak = true) => Loading.Add(status);
        public void UpdateLoadingStatus(string text, int percent) => Loading.Add(text);
        public void EnterGame() { }
        public void OpenCommandConsole() { }
        public void ShowGameMenu(Action<GameMenuChoice> chosen) => Menu = chosen;
        public void ReturnToMenu() => ReturnedToMenu++;
        public void Quit() => Quits++;
    }
}

/// <summary>
/// Against a real server, when one is named: OPENFPS_SMOKE=host:port (a scratch server, never a
/// player's: it logs in as admin and so closes any admin session there). Logs in, times the map load,
/// then leaves by the game menu and checks the server was told. Skipped otherwise.
/// </summary>
public class LiveServerSmokeTests
{
    private readonly ITestOutputHelper _out;
    public LiveServerSmokeTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void LogInLoadTheMapAndLeaveByTheGameMenu()
    {
        string? target = Environment.GetEnvironmentVariable("OPENFPS_SMOKE");
        if (string.IsNullOrWhiteSpace(target)) return;

        AcousticRegistry.Initialize();
        var speech = new SmokeSpeech();
        var shell = new SmokeShell();
        var network = new ClientNetworkService();
        var session = new ClientGameSession(network, speech, shell, new AudioEngineFacade(),
            microphone: new NullMicrophoneCapture("none"), enableAudio: false);
        network.OnMessageReceived += session.HandleMessage;

        var started = DateTime.UtcNow;
        session.Connect(target, "admin", Environment.GetEnvironmentVariable("OPENFPS_SMOKE_PASSWORD") ?? "admin123", register: false);
        DateTime? loaded = null;
        while (DateTime.UtcNow - started < TimeSpan.FromSeconds(90))
        {
            network.Poll();
            session.Tick();
            if (session.IsInGame) { loaded = DateTime.UtcNow; break; }
            Thread.Sleep(2);
        }
        foreach (var line in speech.Lines) _out.WriteLine("said: " + line);
        Assert.NotNull(loaded);
        _out.WriteLine($"login to spawn: {(loaded.Value - started).TotalSeconds:F2} s, {session.World.EntityCount} entities");

        session.Input.SetKey(GameKey.Escape, true);
        session.SimStep(1f / 30f);
        Assert.NotNull(shell.Menu);
        shell.Menu!(GameMenuChoice.MainMenu);
        var leaving = DateTime.UtcNow;
        while (shell.ReturnedToMenu == 0 && DateTime.UtcNow - leaving < TimeSpan.FromSeconds(5))
        {
            network.Poll();
            session.Tick();
            Thread.Sleep(2);
        }
        Assert.Equal(1, shell.ReturnedToMenu);
        Assert.False(network.IsConnected);
        Assert.DoesNotContain(speech.Lines, s => s.Contains("Disconnected"));
    }

    private sealed class SmokeSpeech : ISpeechOutput
    {
        public readonly ConcurrentQueue<string> Lines = new();
        public string BackendName => "smoke";
        public bool Initialize() => true;
        public void Speak(string text, bool interrupt = true) => Lines.Enqueue(text);
        public void Interrupt() { }
        public void Dispose() { }
    }

    private sealed class SmokeShell : IClientShell
    {
        public Action<GameMenuChoice>? Menu;
        public int ReturnedToMenu;
        public bool IsGameInputActive => true;
        public event Action<string>? CommandEntered { add { } remove { } }
        public void ShowLoading(string status, bool speak = true) { }
        public void UpdateLoadingStatus(string text, int percent) { }
        public void EnterGame() { }
        public void OpenCommandConsole() { }
        public void ShowGameMenu(Action<GameMenuChoice> chosen) => Menu = chosen;
        public void ReturnToMenu() => ReturnedToMenu++;
        public void Quit() { }
    }
}

public class MicrophoneResamplerTests
{
    /// <summary>A 44.1 kHz tone fed in uneven chunks comes out at 48 kHz, the same tone, with no
    /// step at the chunk joins.</summary>
    [Fact]
    public void ResamplingKeepsTheToneAcrossChunkJoins()
    {
        var r = new OpenFPS.Client.Core.Platform.SincResampler(44100, 48000);
        var outSamples = new List<float>();
        int n = 0;
        foreach (int chunk in new[] { 441, 97, 1000, 3, 2869 })
        {
            var input = new List<float>();
            for (int i = 0; i < chunk; i++, n++) input.Add(MathF.Sin(MathF.Tau * 440f * n / 44100f));
            r.Process(input, outSamples);
        }
        // 4410 in at 44.1 kHz is 0.1 s: about 4800 out, less the filter's half-length still waiting
        // for the samples after it.
        Assert.InRange(outSamples.Count, 4770, 4801);
        // Each output sample is the tone at its own time (no delay), once the filter is past the
        // silence it started from.
        for (int k = 32; k < outSamples.Count; k++)
            Assert.InRange(outSamples[k] - MathF.Sin(MathF.Tau * 440f * k / 48000f), -0.005f, 0.005f);
    }
}

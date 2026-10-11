using System.Collections.Concurrent;
using LiteNetLib;
using MemoryPack;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.Core;
using OpenFPS.Client.Core.Platform;
using OpenFPS.Client.Core.Session;
using OpenFPS.Common;
using OpenFPS.Common.Networking;

namespace OpenFPS.Tests;

/// <summary>
/// Connecting while the server is not up yet (Cody, 2026-10-11): a client started a little before its
/// server failed at once with "The server did not answer", and the main menu kept focus, so the arrows
/// moved its cursor while the connect was in flight. Now the connect holds focus, keeps trying for about
/// 30 seconds, says so once and then at most every 10 seconds, and Escape stops it.
/// </summary>
public class ConnectRetryTests
{
    private static readonly TimeSpan For = TimeSpan.FromSeconds(30), Interval = TimeSpan.FromSeconds(2),
                                      Every = TimeSpan.FromSeconds(10);
    private static readonly DateTime T0 = new(2026, 10, 11, 1, 29, 10, DateTimeKind.Utc);

    // ── The state machine, on a made-up clock ──────────────────────────────────────────────────

    /// <summary>
    /// The server never comes, each try taking LiteNetLib's 5 s: said once when the first goes unanswered,
    /// then "still trying" at 15 and 25 s, never twice alike and never closer than 10 s, and given up at 30.
    /// </summary>
    [Fact]
    public void ItIsSaidOnceThenAtMostEveryTenSecondsAndGivenUpAtThirty()
    {
        var retry = new ConnectRetry(T0, For, Interval, Every);
        var said = new List<(double At, string Line)>();
        double tryEnds = 5;
        ConnectRetry.Next next = ConnectRetry.Next.Wait;
        double t = 0;
        for (int step = 0; step <= 800; step++)
        {
            t = step * 0.05;
            var now = T0.AddSeconds(t);
            bool out_ = t < tryEnds;
            if (Math.Abs(t - tryEnds) < 0.025 && retry.NotAnswered(now) is { } line) said.Add((t, line));
            next = retry.Tick(now, out_, out string? say);
            if (say != null) said.Add((t, say));
            if (next == ConnectRetry.Next.GiveUp) break;
            if (next == ConnectRetry.Next.Try) tryEnds = t + 5;
        }

        Assert.Equal(ConnectRetry.Next.GiveUp, next);
        Assert.InRange(t, 29.9, 30.1);
        Assert.Equal(ConnectRetry.NotAnsweringYet, said[0].Line);
        Assert.InRange(said[0].At, 4.9, 5.1);
        Assert.Equal(new[] { "Still trying, 15 seconds.", "Still trying, 25 seconds." }, said.Skip(1).Select(s => s.Line));
        for (int i = 1; i < said.Count; i++)
        {
            Assert.True(said[i].At - said[i - 1].At >= 9.99, $"'{said[i].Line}' came {said[i].At - said[i - 1].At:F1} s after the last line");
            Assert.NotEqual(said[i - 1].Line, said[i].Line);
        }
    }

    [Fact]
    public void ASecondUnansweredTryIsNotSaidAgain()
    {
        var retry = new ConnectRetry(T0, For, Interval, Every);
        Assert.Equal(ConnectRetry.NotAnsweringYet, retry.NotAnswered(T0.AddSeconds(5)));
        Assert.Null(retry.NotAnswered(T0.AddSeconds(10)));
        Assert.True(retry.Retrying);
    }

    /// <summary>A failure that comes back at once (a socket error) is retried at the steady interval,
    /// never faster, and nothing new starts while a try is out.</summary>
    [Fact]
    public void TriesAreSteadyAndNeverOverlap()
    {
        var retry = new ConnectRetry(T0, For, Interval, Every);
        retry.NotAnswered(T0);
        var tries = new List<double>();
        for (int step = 0; step < 580; step++)
            if (retry.Tick(T0.AddSeconds(step * 0.05), attemptOut: false, out _) == ConnectRetry.Next.Try) tries.Add(step * 0.05);
        Assert.InRange(tries.Count, 13, 15);
        for (int i = 1; i < tries.Count; i++) Assert.InRange(tries[i] - tries[i - 1], 1.99, 2.06);

        var busy = new ConnectRetry(T0, For, Interval, Every);
        busy.NotAnswered(T0);
        for (double t = 0; t < 29; t += 0.5)
            Assert.NotEqual(ConnectRetry.Next.Try, busy.Tick(T0.AddSeconds(t), attemptOut: true, out _));
    }

    [Fact]
    public void BeforeTheFirstTryIsAnsweredNothingIsDecided()
    {
        var retry = new ConnectRetry(T0, For, Interval, Every);
        Assert.Equal(ConnectRetry.Next.Wait, retry.Tick(T0.AddSeconds(60), attemptOut: true, out string? say));
        Assert.Null(say);
    }

    /// <summary>After a drop the drop itself was the announcement: the first try waits an interval.</summary>
    [Fact]
    public void AfterADropTheFirstTryWaitsAnInterval()
    {
        var retry = new ConnectRetry(T0, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(3), Every, retrying: true);
        Assert.Null(retry.NotAnswered(T0.AddSeconds(1)));
        Assert.Equal(ConnectRetry.Next.Wait, retry.Tick(T0.AddSeconds(2.9), false, out _));
        Assert.Equal(ConnectRetry.Next.Try, retry.Tick(T0.AddSeconds(3), false, out _));
        Assert.Equal(ConnectRetry.Next.GiveUp, retry.Tick(T0.AddSeconds(60), true, out _));
    }

    // ── The keys of the connecting window ──────────────────────────────────────────────────────

    /// <summary>Whatever moves or presses in a menu is held while connecting, so its cursor stays put.</summary>
    [Theory]
    [InlineData(GameKey.Up)]
    [InlineData(GameKey.Down)]
    [InlineData(GameKey.Left)]
    [InlineData(GameKey.Right)]
    [InlineData(GameKey.Tab)]
    [InlineData(GameKey.Enter)]
    [InlineData(GameKey.Space)]
    [InlineData(GameKey.C)]
    public void MenuKeysDoNotReachTheMenuWhileConnecting(GameKey key)
        => Assert.Equal(HoldKey.Hold, ConnectHold.Decide(key));

    [Fact]
    public void EscapeCancels() => Assert.Equal(HoldKey.Cancel, ConnectHold.Decide(GameKey.Escape));

    /// <summary>Control is how a screen reader stops talking: it, Alt, Shift, and every key the game does
    /// not map (Insert, Caps Lock, the review keypad) pass to the reader untouched.</summary>
    [Fact]
    public void TheScreenReadersKeysPass()
    {
        foreach (var key in ClientGameSession.ScreenReaderKeys.Append(GameKey.ShiftLeft).Append(GameKey.ShiftRight).Append(GameKey.None))
            Assert.Equal(HoldKey.Pass, ConnectHold.Decide(key));
    }

    // ── The session, against a real LiteNetLib server on the loopback ──────────────────────────

    [Fact]
    public void AServerThatIsUpIsJoinedAndTheHoldEndsForTheLoadingScreen()
    {
        using var server = new LoginServer(0);
        var (session, speech, shell, network) = NewSession();
        bool accepted = false;
        session.LoginSucceeded += _ => accepted = true;

        session.Connect($"127.0.0.1:{server.Port}", "cody", "pw", register: false, serverName: "Open FPS dev server");
        Assert.Equal("Connecting to Open FPS dev server. Escape to cancel.", shell.Shown[0]);
        Assert.True(session.IsConnecting);
        Assert.True(Pump(() => accepted, server, network, session), "the login was never accepted");

        Assert.False(session.IsConnecting);
        Assert.Equal(new[] { false }, shell.Ended);   // the loading screen takes focus, not the menu
        Assert.DoesNotContain(ConnectRetry.NotAnsweringYet, speech.Spoken);
    }

    /// <summary>The server comes up part way through: one "not answering yet", then in as soon as it answers.</summary>
    [Fact]
    public void AServerThatComesLateIsJoinedOnceItAnswers()
    {
        int port = FreePort();
        var (session, speech, shell, network) = NewSession(fast: true);
        session.RetryFor = TimeSpan.FromSeconds(20);
        bool accepted = false;
        string? failed = null;
        session.LoginSucceeded += _ => accepted = true;
        session.ConnectFailed += r => failed = r;

        session.Connect($"127.0.0.1:{port}", "cody", "pw", register: false);
        Assert.Equal($"Connecting to 127.0.0.1, port {port}. Escape to cancel.", shell.Shown[0]);
        Assert.True(Pump(() => session.IsRetrying, null, network, session), "the first unanswered try was not noticed");

        using var server = new LoginServer(port);
        var upAt = DateTime.UtcNow;
        Assert.True(Pump(() => accepted, server, network, session, seconds: 10), "never got in once the server was up");
        Assert.True(DateTime.UtcNow - upAt < TimeSpan.FromSeconds(3), "took too long to notice the server");

        Assert.Null(failed);
        Assert.Single(server.Logins);
        Assert.Single(speech.Spoken, ConnectRetry.NotAnsweringYet);
        Assert.Contains("Connected. Logging in.", speech.Spoken);
        Assert.DoesNotContain(speech.Spoken, s => s.Contains("Could not connect"));
        Assert.Equal(new[] { false }, shell.Ended);
    }

    /// <summary>The server never comes: the trying ends with the reason it could not connect, and focus goes back.</summary>
    [Fact]
    public void AServerThatNeverComesIsGivenUpWithTheReason()
    {
        int port = FreePort();
        var (session, speech, shell, network) = NewSession(fast: true);
        session.RetryFor = TimeSpan.FromSeconds(1.5);
        string? failed = null;
        session.ConnectFailed += r => failed = r;

        var started = DateTime.UtcNow;
        session.Connect($"127.0.0.1:{port}", "cody", "pw", register: false);
        Assert.True(Pump(() => failed != null, null, network, session, seconds: 10), "never gave up");

        Assert.True(DateTime.UtcNow - started >= TimeSpan.FromSeconds(1.4), "gave up before the time was out");
        Assert.Contains("did not answer", failed);
        Assert.Equal(failed, speech.Spoken[^1]);
        Assert.Single(speech.Spoken, ConnectRetry.NotAnsweringYet);
        Assert.Equal(new[] { true }, shell.Ended);    // focus back on the item used
        Assert.False(session.IsConnecting);
        for (int i = 1; i < speech.Spoken.Count; i++) Assert.NotEqual(speech.Spoken[i - 1], speech.Spoken[i]);
    }

    /// <summary>
    /// Escape while it is trying: "Cancelled.", focus back, and nothing after it. A server that comes up
    /// later gets no login, and the try's own end is not said over the menu.
    /// </summary>
    [Fact]
    public void EscapeCancelsAndNothingFollows()
    {
        int port = FreePort();
        var (session, speech, shell, network) = NewSession(fast: true);
        int cancelled = 0, failed = 0;
        session.ConnectCancelled += () => cancelled++;
        session.ConnectFailed += _ => failed++;

        session.Connect($"127.0.0.1:{port}", "cody", "pw", register: false);
        Assert.True(Pump(() => session.IsRetrying, null, network, session), "never started retrying");
        Assert.True(Pump(() => network.IsConnecting, null, network, session), "no try in flight to cancel");

        // As the head's connecting window does on Escape: the cancel it was handed, from the UI thread.
        shell.Cancel!();
        // A held Escape repeats; the head asks once, but a second ask must do nothing either.
        shell.Cancel!();
        Assert.True(Pump(() => !session.IsConnecting, null, network, session), "Escape did not stop it");

        using var server = new LoginServer(port);
        Pump(() => false, server, network, session, seconds: 1.5);

        Assert.Equal(1, cancelled);
        Assert.Equal(0, failed);
        Assert.Equal("Cancelled.", speech.Spoken[^1]);
        Assert.Equal(new[] { true }, shell.Ended);
        Assert.Empty(server.Logins);
        Assert.False(network.IsConnected);
    }

    /// <summary>A drop in game says how to stop the reconnect and holds focus the same way; Escape there
    /// (here through the game window, for a head without the connecting window) goes to the main menu.</summary>
    [Fact]
    public void ADropInGameHoldsFocusAndEscapeStopsTheReconnect()
    {
        var server = new LoginServer(0);
        var (session, speech, shell, network) = NewSession(fast: true);
        session.ReconnectInterval = TimeSpan.FromMilliseconds(200);
        bool accepted = false;
        session.LoginSucceeded += _ => accepted = true;
        session.Connect($"127.0.0.1:{server.Port}", "cody", "pw", register: false);
        Assert.True(Pump(() => accepted, server, network, session), "never logged in");
        session.HandleMessage(new PlayerSpawned { EntityId = 5 });

        server.Dispose();
        Assert.True(Pump(() => session.IsReconnecting, null, network, session, seconds: 10), "the drop was never noticed");
        Assert.Contains(shell.Shown, s => s.Contains("Reconnecting. Escape to cancel."));

        session.Input.SetKey(GameKey.Escape, true);
        Assert.True(Pump(() => shell.ReturnedToMenu == 1, null, network, session), "Escape did not stop the reconnect");
        Assert.False(session.IsReconnecting);
        Assert.Contains("Stopped reconnecting.", speech.Spoken);
        Assert.DoesNotContain(speech.Spoken, s => s.Contains("Could not reconnect"));
        Assert.False(shell.Ended[^1]);   // the main menu takes focus
    }

    // ── Harness ─────────────────────────────────────────────────────────────────────────────────

    private static int FreePort()
    {
        var probe = new NetManager(new NullListener());
        Assert.True(probe.Start(0));
        int port = probe.LocalPort;
        probe.Stop();
        return port;
    }

    private static bool Pump(Func<bool> condition, LoginServer? server, ClientNetworkService network,
                             ClientGameSession session, double seconds = 5)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            server?.Poll();
            network.Poll();
            session.Tick();
            if (condition()) return true;
            Thread.Sleep(5);
        }
        return condition();
    }

    /// <param name="fast">Tries of four requests 100 ms apart instead of LiteNetLib's ten 500 ms apart.</param>
    private static (ClientGameSession, Speech, Shell, ClientNetworkService) NewSession(bool fast = false)
    {
        AcousticRegistry.Initialize();
        var speech = new Speech();
        var shell = new Shell();
        var network = new ClientNetworkService();
        if (fast) { network.ConnectRequests = 4; network.ConnectRequestMs = 100; }
        var session = new ClientGameSession(network, speech, shell, new AudioEngineFacade(),
            microphone: new NullMicrophoneCapture("no microphone here"), enableAudio: false);
        network.OnMessageReceived += session.HandleMessage;
        session.RetryInterval = TimeSpan.FromMilliseconds(200);
        return (session, speech, shell, network);
    }

    /// <summary>A loopback server that accepts and answers every login.</summary>
    private sealed class LoginServer : INetEventListener, IDisposable
    {
        private readonly NetManager _net;
        public readonly List<string> Logins = new();
        public int Port => _net.LocalPort;

        public LoginServer(int port)
        {
            _net = new NetManager(this) { AutoRecycle = true };
            Assert.True(_net.Start(port));
        }

        public void Poll() { if (_net.IsRunning) _net.PollEvents(); }
        public void Dispose() { if (_net.IsRunning) _net.Stop(); }

        public void OnConnectionRequest(ConnectionRequest request) => request.AcceptIfKey("OpenFPS_Key");
        public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
        {
            if (MemoryPackSerializer.Deserialize<IMessage>(reader.GetRemainingBytes()) is not LoginRequest login) return;
            Logins.Add(login.Username);
            peer.Send(MemoryPackSerializer.Serialize<IMessage>(new LoginResponse { Success = true, Username = login.Username }),
                      DeliveryMethod.ReliableOrdered);
        }
        public void OnPeerConnected(NetPeer peer) { }
        public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo) { }
        public void OnNetworkError(System.Net.IPEndPoint endPoint, System.Net.Sockets.SocketError socketError) { }
        public void OnNetworkReceiveUnconnected(System.Net.IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType) { }
        public void OnNetworkLatencyUpdate(NetPeer peer, int latency) { }
    }

    private sealed class NullListener : INetEventListener
    {
        public void OnConnectionRequest(ConnectionRequest request) => request.Reject();
        public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod) { }
        public void OnPeerConnected(NetPeer peer) { }
        public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo) { }
        public void OnNetworkError(System.Net.IPEndPoint endPoint, System.Net.Sockets.SocketError socketError) { }
        public void OnNetworkReceiveUnconnected(System.Net.IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType) { }
        public void OnNetworkLatencyUpdate(NetPeer peer, int latency) { }
    }

    private sealed class Speech : ISpeechOutput
    {
        private readonly ConcurrentQueue<string> _queue = new();
        public List<string> Spoken => _queue.ToList();
        public string BackendName => "fake";
        public bool Initialize() => true;
        public void Speak(string text, bool interrupt = true) => _queue.Enqueue(text);
        public void Interrupt() { }
        public void Dispose() { }
    }

    /// <summary>Records what the connecting window was asked to show, the cancel it was handed, and how it ended.</summary>
    private sealed class Shell : IClientShell
    {
        public readonly List<string> Shown = new();
        public readonly List<bool> Ended = new();
        public Action? Cancel;
        public int ReturnedToMenu;
        public bool IsGameInputActive { get; set; } = true;
        public event Action<string>? CommandEntered { add { } remove { } }
        public void ShowLoading(string status, bool speak = true) { }
        public void UpdateLoadingStatus(string text, int percent) { }
        public void EnterGame() { }
        public void OpenCommandConsole() { }
        public void ShowGameMenu(Action<GameMenuChoice> chosen) { }
        public void ShowConnecting(string status, Action cancel) { lock (Shown) Shown.Add(status); Cancel = cancel; }
        public void EndConnecting(bool returnFocus) { lock (Ended) Ended.Add(returnFocus); }
        public void ReturnToMenu() => ReturnedToMenu++;
        public void Quit() { }
    }
}

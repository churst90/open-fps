using System.Collections.Concurrent;
using OpenFPS.Common;
using OpenFPS.Common.Networking;
using OpenFPS.Client.Core.Platform;

namespace OpenFPS.Client.Core.Session;

/// <summary>
/// Connecting, logging in, losing the connection and leaving, for both heads: a head calls
/// <see cref="Connect"/> from its connect form and shows what <see cref="IClientShell"/> asks for.
/// </summary>
public sealed partial class ClientGameSession
{
    /// <summary>How often a lost connection is tried again. Settable for tests.</summary>
    internal TimeSpan ReconnectInterval { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>How long a lost connection is tried for before going back to the main menu.</summary>
    internal TimeSpan ReconnectFor { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long a connect from the menu keeps trying a server that does not answer: one started a little
    /// before the server (Cody, 2026-10-11) failed at once with "The server did not answer".
    /// </summary>
    internal TimeSpan RetryFor { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The fewest seconds between the starts of two tries. An unanswered try is LiteNetLib's ten requests
    /// half a second apart, so it ends 5 s after it began (Cody's log, 2026-10-11) and the next follows it
    /// at once; this only paces a failure that comes back sooner.
    /// </summary>
    internal TimeSpan RetryInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>The fewest seconds between two "still trying" lines, connecting or reconnecting.</summary>
    internal TimeSpan AnnounceEvery { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>The world fades in over this on arrival...</summary>
    public const float FadeInSeconds = 1.0f;

    /// <summary>...and out over this on leaving.</summary>
    public const float FadeOutSeconds = 0.5f;

    private enum LinkState
    {
        /// <summary>At the menu, nothing in flight.</summary>
        Idle,
        /// <summary>Connecting, trying again while the server does not answer, or logging in, from the menu.</summary>
        Connecting,
        /// <summary>Logged in: loading or in the world.</summary>
        LoggedIn,
        /// <summary>The connection dropped and is being tried again.</summary>
        Reconnecting,
        /// <summary>Logging out on purpose; the disconnect that follows is expected.</summary>
        Leaving,
    }

    private volatile LinkState _link = LinkState.Idle;

    // Kept in memory for the session, so a dropped connection can log straight back in.
    private string _host = "127.0.0.1";
    private int _port = 33288;
    private string _user = "";
    private string _pass = "";
    private volatile bool _registering;

    /// <summary>The tries of a connect or a reconnect, while the server has not answered; null once it has.</summary>
    private volatile ConnectRetry? _retry;
    private DateTime _reconnectStarted;
    /// <summary>What the last unanswered try said, for when the trying ends.</summary>
    private string _lastFailure = "";
    /// <summary>The server as the player knows it: a saved server's name, or the address.</summary>
    private string _target = "";
    /// <summary>Set by Escape and cleared by the next connect: an answer to a cancelled login is dropped.</summary>
    private volatile bool _cancelled;

    /// <summary>Disconnects we made ourselves whose event has not arrived yet. LiteNetLib reports
    /// our own disconnect on a later poll, by which time we are back at the menu.</summary>
    private int _ownDisconnects;

    /// <summary>Work handed over from a head's UI thread, run on the game-loop thread by <see cref="Tick"/>.</summary>
    private readonly ConcurrentQueue<Action> _queued = new();

    // The world fade: from, to, over how long, from when, and what to do at the end.
    private float _fadeFrom = 1f, _fadeTo = 1f, _fadeSeconds;
    private DateTime _fadeStarted;
    private Action? _fadeDone;
    private bool _fading;

    /// <summary>The last step of the loading tone played, so each step sounds once.</summary>
    private int _loadToneStep = -1;

    /// <summary>
    /// Raised on the game-loop thread when a connect, or creating an account, fails before the login
    /// is answered. The argument is the sentence already spoken. A rejected login is
    /// <see cref="LoginFailed"/>, with the server's reason.
    /// </summary>
    public event Action<string>? ConnectFailed;

    /// <summary>True while a lost connection is being tried again.</summary>
    public bool IsReconnecting => _link == LinkState.Reconnecting;

    /// <summary>True from Connect until the login is answered, the player cancels, or the trying ends.</summary>
    public bool IsConnecting => _link == LinkState.Connecting;

    /// <summary>True while a connect from the menu is trying again because the server did not answer.</summary>
    public bool IsRetrying => _link == LinkState.Connecting && _retry is { Retrying: true };

    /// <summary>Raised on the game-loop thread when the player cancels a connect with Escape. Already
    /// said; the head's connect form only shows it.</summary>
    public event Action? ConnectCancelled;

    private void WireConnection()
    {
        _network.OnConnected += SendCredentials;
        _network.OnConnectionFailed += OnConnectFailed;
        _network.OnConnectionLost += OnConnectionLost;
        _network.OnProtocolError += reason => _speech.Speak(reason, interrupt: true);
        _network.OnConnectionNotice += notice => _speech.Speak(notice, interrupt: true);
    }

    /// <summary>
    /// Connects to a server and logs in, or creates the account first when <paramref name="register"/>
    /// is set. Callable from any thread. Focus goes to the head's connecting window until it ends; a
    /// server that does not answer is tried again for <see cref="RetryFor"/>. The outcome is spoken, and
    /// raised as <see cref="LoginSucceeded"/>, <see cref="LoginFailed"/>, <see cref="ConnectFailed"/> or
    /// <see cref="ConnectCancelled"/>. <paramref name="serverName"/> is a saved server's name, said in
    /// place of the address.
    /// </summary>
    public void Connect(string address, string user, string pass, bool register, string? serverName = null)
    {
        ServerAddress.Parse(address, out _host, out _port);
        _user = user.Trim();
        _pass = pass;
        _registering = register;
        _cancelled = false;
        _target = string.IsNullOrWhiteSpace(serverName) ? $"{_host}, port {_port}" : serverName.Trim();
        _retry = new ConnectRetry(DateTime.UtcNow, RetryFor, RetryInterval, AnnounceEvery);
        _link = LinkState.Connecting;
        // Said and shown before the socket opens: a socket that cannot open ends the hold, and the end
        // must reach the head after the start.
        string line = $"Connecting to {_target}. Escape to cancel.";
        _speech.Speak(line, interrupt: true);
        _shell.ShowConnecting(line, CancelConnect);
        _network.Start();
        if (_link != LinkState.Connecting) return;
        _network.Connect(_host, _port);
    }

    /// <summary>
    /// Escape while connecting or reconnecting: the try stops and the player is back where they were (the
    /// menu item they used, or the main menu after a drop). Callable from any thread; runs on the
    /// game-loop thread.
    /// </summary>
    public void CancelConnect() => _queued.Enqueue(DoCancelConnect);

    private void DoCancelConnect()
    {
        switch (_link)
        {
            case LinkState.Connecting:
                Serilog.Log.Information("Connect to {Target} cancelled after {Sec:F0} s.", _target,
                    _retry is { } r ? (DateTime.UtcNow - r.Started).TotalSeconds : 0);
                _cancelled = true;
                _retry = null;
                _link = LinkState.Idle;
                DropConnection();
                Ui.Play(UiCue.MenuBack);
                _speech.Speak("Cancelled.", interrupt: true);
                _shell.EndConnecting(returnFocus: true);
                ConnectCancelled?.Invoke();
                return;
            case LinkState.Reconnecting:
                Serilog.Log.Information("Reconnecting cancelled after {Sec:F0} s.", (DateTime.UtcNow - _reconnectStarted).TotalSeconds);
                _cancelled = true;
                Ui.Play(UiCue.MenuBack);
                _speech.Speak("Stopped reconnecting.", interrupt: true);
                StopReconnecting();
                return;
        }
    }

    private void SendCredentials()
    {
        if (_registering)
        {
            Serilog.Log.Information("Connected to server; creating the account '{User}'.", _user);
            _retry = null;
            _speech.Speak("Connected. Creating the account.", interrupt: true);
            _shell.ShowConnecting("Connected. Creating the account.", CancelConnect);
            _network.Send(new RegisterRequest { Username = _user, Password = _pass });
            return;
        }
        Serilog.Log.Information("Connected to server; sending login for user '{User}'.", _user);
        if (_link == LinkState.Connecting)
        {
            _retry = null;
            _speech.Speak("Connected. Logging in.", interrupt: true);
            _shell.ShowConnecting("Connected. Logging in.", CancelConnect);
        }
        _network.Send(new LoginRequest { Username = _user, Password = _pass, Build = WireContract.Hash });
    }

    /// <summary>A new account: said, and on success followed straight into a login with the same name.</summary>
    private void HandleRegisterResponse(RegisterResponse reg)
    {
        if (AnswerWasCancelled()) return;
        _registering = false;
        if (reg.Success)
        {
            _speech.Speak("Account created. Logging in.", interrupt: true);
            _shell.ShowConnecting("Account created. Logging in.", CancelConnect);
            _network.Send(new LoginRequest { Username = _user, Password = _pass, Build = WireContract.Hash });
            return;
        }
        string reason = $"Could not create the account. {reg.Message}";
        _speech.Speak(reason, interrupt: true);
        _link = LinkState.Idle;
        _shell.EndConnecting(returnFocus: true);
        // Nothing is logged in, so nothing stays connected: quitting would announce "Disconnected from
        // the server" for a connection the player never had.
        DropConnection();
        ConnectFailed?.Invoke(reason);
    }

    private void NoteLoggedIn()
    {
        if (_link == LinkState.Reconnecting) _speech.Speak("Reconnected.", interrupt: true);
        bool holding = _link is LinkState.Connecting or LinkState.Reconnecting;
        _link = LinkState.LoggedIn;
        _retry = null;
        // The loading screen that follows takes focus; the connecting window goes once it has.
        if (holding) _shell.EndConnecting(returnFocus: false);
    }

    private void NoteLoginRejected()
    {
        // Rejected while trying to get back in (a new build on the server, a changed password):
        // trying again cannot help, so back to the menu with the reason already said.
        if (_link == LinkState.Reconnecting) GiveUpReconnecting(speak: false);
        else
        {
            bool holding = _link == LinkState.Connecting;
            _link = LinkState.Idle;
            _retry = null;
            DropConnection();
            if (holding) _shell.EndConnecting(returnFocus: true);
        }
    }

    /// <summary>An answer that belongs to a connect the player cancelled, to be dropped.</summary>
    private bool AnswerWasCancelled()
    {
        if (!_cancelled || _link is LinkState.Connecting or LinkState.Reconnecting) return false;
        Serilog.Log.Information("Dropped the server's answer to a cancelled connect.");
        return true;
    }

    private void OnConnectFailed(string reason)
    {
        // An attempt to get back in that did not answer: the next tick tries again.
        if (_link == LinkState.Reconnecting)
        {
            Serilog.Log.Information("Reconnect attempt failed: {Reason}", reason);
            _lastFailure = reason;
            return;
        }
        if (_link == LinkState.Connecting)
        {
            // A socket error while a try is still out is not its end; the try reports that itself.
            if (_network.IsConnecting)
            {
                Serilog.Log.Information("While connecting: {Reason} The try goes on.", reason);
                return;
            }
            if (_retry is { } retry && _network.LastFailureWorthRetrying)
            {
                Serilog.Log.Information("Not answered: {Reason} Trying again for up to {Sec:F0} s in all.", reason, RetryFor.TotalSeconds);
                _lastFailure = reason;
                if (retry.NotAnswered(DateTime.UtcNow) is { } line) SayWhileConnecting(line);
                return;
            }
            FailConnect(reason);
            return;
        }
        if (_cancelled && _link == LinkState.Idle)
        {
            Serilog.Log.Information("After a cancelled connect: {Reason}", reason);
            return;
        }
        _speech.Speak(reason, interrupt: true);
        ConnectFailed?.Invoke(reason);
    }

    /// <summary>A connect from the menu is over without getting in: said, and focus is back where it was.</summary>
    private void FailConnect(string reason)
    {
        _retry = null;
        _link = LinkState.Idle;
        // A try still out when the time ran out goes too, so it cannot log in behind the menu.
        DropConnection();
        _speech.Speak(reason, interrupt: true);
        _shell.EndConnecting(returnFocus: true);
        ConnectFailed?.Invoke(reason);
    }

    /// <summary>A line about the trying, said without cutting off whatever the player is reading, and shown.</summary>
    private void SayWhileConnecting(string line)
    {
        _speech.Speak(line, interrupt: false);
        _shell.ShowConnecting(line, CancelConnect);
    }

    /// <summary>Drops the connection on purpose, so its disconnect event is not taken for a loss.</summary>
    private void DropConnection()
    {
        if (_network.IsConnected) _ownDisconnects++;
        _network.Disconnect();
    }

    private void OnConnectionLost(string reason)
    {
        if (_ownDisconnects > 0) { _ownDisconnects--; return; }
        switch (_link)
        {
            case LinkState.Leaving:
                return;   // we asked for it
            case LinkState.LoggedIn:
                // Out of the world at once — nothing left playing as if the game were still there —
                // and try to get back in.
                Serilog.Log.Warning("Connection lost in game: {Reason}. Reconnecting.", reason);
                string line = $"Disconnected from the server. {reason} Reconnecting. Escape to cancel.";
                _speech.Speak(line, interrupt: true);
                LeaveWorld();
                _cancelled = false;
                _lastFailure = reason;
                _reconnectStarted = DateTime.UtcNow;
                _retry = new ConnectRetry(_reconnectStarted, ReconnectFor, ReconnectInterval, AnnounceEvery, retrying: true);
                _link = LinkState.Reconnecting;
                _shell.ShowConnecting(line, CancelConnect);
                return;
            case LinkState.Reconnecting:
                return;   // a connection made during a retry dropped again; the ticks carry on
            case LinkState.Connecting:
                // Connected, then dropped before the login was answered.
                FailConnect($"Disconnected from the server. {reason}");
                return;
            default:
                string dropped = $"Disconnected from the server. {reason}";
                _speech.Speak(dropped, interrupt: true);
                _link = LinkState.Idle;
                ConnectFailed?.Invoke(dropped);
                return;
        }
    }

    private void GiveUpReconnecting(bool speak)
    {
        Serilog.Log.Warning("Gave up reconnecting after {Sec:F0} s. Last: {Reason}", (DateTime.UtcNow - _reconnectStarted).TotalSeconds, _lastFailure);
        if (speak) _speech.Speak("Could not reconnect. Back to the main menu.", interrupt: true);
        StopReconnecting();
    }

    /// <summary>The reconnect is over without getting back in: to the main menu, which takes focus from the
    /// connecting window.</summary>
    private void StopReconnecting()
    {
        _retry = null;
        _link = LinkState.Idle;
        DropConnection();
        _shell.EndConnecting(returnFocus: false);
        _shell.ReturnToMenu();
    }

    /// <summary>
    /// Housekeeping that must run whether or not the player is in the world: work handed over from a
    /// UI thread, the world fade, and reconnect attempts. Both heads call it once per game-loop
    /// iteration, on the game-loop thread.
    /// </summary>
    public void Tick()
    {
        while (_queued.TryDequeue(out var action))
        {
            try { action(); }
            catch (Exception ex) { Serilog.Log.Error(ex, "Queued session action failed."); }
        }

        if (_fading) AdvanceFade();

        if (_link is LinkState.Connecting or LinkState.Reconnecting && _retry is { } retry)
        {
            // Out of the world the bindings do not run. A head without a connecting window leaves the
            // game window focused, and Escape there still has to stop the trying.
            if (_link == LinkState.Reconnecting && !IsInGame && _shell.IsGameInputActive
                && Input.GetSnapshot().JustPressed.Contains(GameKey.Escape))
            {
                DoCancelConnect();
                return;
            }
            // Connected and logging in: the server has answered, and its answer ends the trying.
            if (_network.IsConnected) return;
            var next = retry.Tick(DateTime.UtcNow, _network.IsConnecting, out string? say);
            if (say != null) SayWhileConnecting(say);
            switch (next)
            {
                case ConnectRetry.Next.GiveUp when _link == LinkState.Reconnecting:
                    GiveUpReconnecting(speak: true);
                    break;
                case ConnectRetry.Next.GiveUp:
                    Serilog.Log.Warning("Gave up connecting to {Target} after {Sec:F0} s.", _target, (DateTime.UtcNow - retry.Started).TotalSeconds);
                    FailConnect(_lastFailure);
                    break;
                case ConnectRetry.Next.Try:
                    Ui.Play(UiCue.Reconnecting);
                    _network.Connect(_host, _port);
                    break;
            }
        }
    }

    // ── The game menu ─────────────────────────────────────────────────────────────────────────

    private void ShowGameMenu() => _shell.ShowGameMenu(choice => _queued.Enqueue(() => OnGameMenuChoice(choice)));

    private void OnGameMenuChoice(GameMenuChoice choice)
    {
        switch (choice)
        {
            case GameMenuChoice.MainMenu: Leave(toMenu: true); break;
            case GameMenuChoice.Quit: Leave(toMenu: false); break;
            default: Ui.Play(UiCue.MenuBack); break;
        }
    }

    /// <summary>
    /// Logs out and leaves the world: the server is told first, the world fades out, and then the
    /// player is on the main menu or the program closes. Runs on the game-loop thread.
    /// </summary>
    private void Leave(bool toMenu)
    {
        void Finish()
        {
            DropConnection();
            LeaveWorld();
            _link = LinkState.Idle;
            SetFade(1f);
            if (toMenu) _shell.ReturnToMenu();
            else _shell.Quit();
        }

        bool wasConnected = _network.IsConnected;
        _link = LinkState.Leaving;
        if (!toMenu) _speech.Speak("Goodbye.", interrupt: true);
        if (!wasConnected) { Finish(); return; }

        Serilog.Log.Information("Logging out ({Where}).", toMenu ? "to the main menu" : "quitting");
        _network.Send(new LogoutRequest());
        FadeWorld(0f, FadeOutSeconds, Finish);
    }

    /// <summary>
    /// Everything the world was making a sound with is stopped and forgotten, and the body goes with
    /// it. The next map arrives as a first arrival, whether it comes from a reconnect or a new login.
    /// </summary>
    private void LeaveWorld()
    {
        _menus.Close();
        if (_microphone.IsCapturing) _microphone.Stop();
        _audioSystem.LeaveWorld(_world.GetSnapshot().Entities.Keys.ToList());
        _ownEntityId = -1;
        _physics.OwnEntityId = -1;
        _physics.Spatial.OwnEntityId = -1;
        _audioSystem.OwnEntityId = -1;
        _state.RidingEntityId = -1;
        _state.RidingControls = false;
        _world.Clear(_world.CurrentMapSize);
        _others.Clear();
        _announcedNearby.Clear();
        _currentNearby.Clear();
        _arrived = false;
        _expectedEntityCount = 0;
        Input.Clear();
    }

    // ── Loading ───────────────────────────────────────────────────────────────────────────────

    /// <summary>The mesh assets the definitions name that are neither here nor cached, asked for once.</summary>
    private void AskForMeshes()
    {
        while (_world.Meshes.TakeRequest() is { } request) _network.Send(request);
    }

    private void ReportEntityProgress()
    {
        if (_expectedEntityCount <= 0) return;
        int count = _world.EntityCount;
        int pct = 10 + (int)((float)Math.Min(count, _expectedEntityCount) / _expectedEntityCount * 60);
        LoadProgress($"Receiving entities: {count}/{_expectedEntityCount}", pct);
    }

    /// <summary>Shows a step of the map load, and sounds the loading tone each time it passes a step.</summary>
    private void LoadProgress(string text, int percent)
    {
        _shell.UpdateLoadingStatus(text, percent);
        int step = Math.Clamp(percent * UiSounds.ProgressSteps / 100, 0, UiSounds.ProgressSteps);
        if (step <= _loadToneStep) return;
        _loadToneStep = step;
        Ui.PlayProgress(percent);
    }

    // ── The world fade ────────────────────────────────────────────────────────────────────────

    private void FadeWorldIn()
    {
        SetFade(0f);
        FadeWorld(1f, FadeInSeconds, null);
    }

    private void FadeWorld(float to, float seconds, Action? done)
    {
        _fadeFrom = _fadeNow;
        _fadeTo = to;
        _fadeSeconds = seconds;
        _fadeStarted = DateTime.UtcNow;
        _fadeDone = done;
        _fading = true;
        AdvanceFade();
    }

    private float _fadeNow = 1f;

    private void SetFade(float gain)
    {
        _fading = false;
        _fadeDone = null;
        _fadeFrom = _fadeTo = gain;
        ApplyFade(gain);
    }

    private void AdvanceFade()
    {
        float t = _fadeSeconds <= 0f ? 1f : (float)((DateTime.UtcNow - _fadeStarted).TotalSeconds / _fadeSeconds);
        if (t >= 1f)
        {
            ApplyFade(_fadeTo);
            _fading = false;
            var done = _fadeDone;
            _fadeDone = null;
            done?.Invoke();
            return;
        }
        ApplyFade(FadeGain(_fadeFrom, _fadeTo, t));
    }

    /// <summary>
    /// The gain part way through a fade, linear in decibels over its 60 dB, so the world neither
    /// lurches in at the start of a fade-in nor hangs on at the end of a fade-out.
    /// </summary>
    public static float FadeGain(float from, float to, float t)
    {
        const float floorDb = -60f;
        float Db(float g) => g <= 1e-3f ? floorDb : MathF.Max(floorDb, 20f * MathF.Log10(g));
        float db = Db(from) + (Db(to) - Db(from)) * Math.Clamp(t, 0f, 1f);
        return db <= floorDb ? 0f : MathF.Pow(10f, db / 20f);
    }

    private void ApplyFade(float gain)
    {
        _fadeNow = gain;
        _audioEngine.SetWorldFade(gain);
    }
}

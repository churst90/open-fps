using System;
using System.Collections.Concurrent;
using System.Linq;
using OpenFPS.Common;
using OpenFPS.Common.Networking;
using OpenFPS.Client.Core.Platform;

namespace OpenFPS.Client.Core.Session;

/// <summary>
/// Connecting, logging in, losing the connection, and leaving: the part of a session that used to
/// be written twice, once in each head, and had drifted (only Windows could create an account, and
/// only Windows told the server it was leaving). Each head now calls <see cref="Connect"/> from its
/// connect form and shows what <see cref="IClientShell"/> asks for; everything else is here.
/// </summary>
public sealed partial class ClientGameSession
{
    /// <summary>How often a lost connection is tried again. Settable for tests.</summary>
    internal TimeSpan ReconnectInterval { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>How long a lost connection is tried for before going back to the main menu.</summary>
    internal TimeSpan ReconnectFor { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>The world fades in over this on arrival...</summary>
    public const float FadeInSeconds = 1.0f;

    /// <summary>...and out over this on leaving.</summary>
    public const float FadeOutSeconds = 0.5f;

    private enum LinkState
    {
        /// <summary>At the menu, nothing in flight.</summary>
        Idle,
        /// <summary>Connecting or logging in from the connect form.</summary>
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

    private DateTime _reconnectStarted;
    private DateTime _nextAttempt;

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
    /// is set. Callable from any thread. The outcome is spoken, and raised as
    /// <see cref="LoginSucceeded"/>, <see cref="LoginFailed"/> or <see cref="ConnectFailed"/>.
    /// </summary>
    public void Connect(string address, string user, string pass, bool register)
    {
        ServerAddress.Parse(address, out _host, out _port);
        _user = user.Trim();
        _pass = pass;
        _registering = register;
        _link = LinkState.Connecting;
        _network.Start();
        _speech.Speak($"Connecting to {_host}, port {_port}.", interrupt: true);
        _network.Connect(_host, _port);
    }

    private void SendCredentials()
    {
        if (_registering)
        {
            Serilog.Log.Information("Connected to server; creating the account '{User}'.", _user);
            _speech.Speak("Connected. Creating the account.", interrupt: true);
            _network.Send(new RegisterRequest { Username = _user, Password = _pass });
            return;
        }
        Serilog.Log.Information("Connected to server; sending login for user '{User}'.", _user);
        if (_link != LinkState.Reconnecting) _speech.Speak("Connected. Logging in.", interrupt: true);
        _network.Send(new LoginRequest { Username = _user, Password = _pass, Build = WireContract.Hash });
    }

    /// <summary>A new account: said, and on success followed straight into a login with the same name.</summary>
    private void HandleRegisterResponse(RegisterResponse reg)
    {
        _registering = false;
        if (reg.Success)
        {
            _speech.Speak("Account created. Logging in.", interrupt: true);
            _network.Send(new LoginRequest { Username = _user, Password = _pass, Build = WireContract.Hash });
            return;
        }
        string reason = $"Could not create the account. {reg.Message}";
        _speech.Speak(reason, interrupt: true);
        _link = LinkState.Idle;
        ConnectFailed?.Invoke(reason);
    }

    private void NoteLoggedIn()
    {
        if (_link == LinkState.Reconnecting) _speech.Speak("Reconnected.", interrupt: true);
        _link = LinkState.LoggedIn;
    }

    private void NoteLoginRejected()
    {
        // Rejected while trying to get back in (a new build on the server, a changed password):
        // trying again cannot help, so back to the menu with the reason already said.
        if (_link == LinkState.Reconnecting) GiveUpReconnecting(speak: false);
        else _link = LinkState.Idle;
    }

    private void OnConnectFailed(string reason)
    {
        // An attempt to get back in that did not answer: the next tick tries again.
        if (_link == LinkState.Reconnecting)
        {
            Serilog.Log.Information("Reconnect attempt failed: {Reason}", reason);
            return;
        }
        _speech.Speak(reason, interrupt: true);
        if (_link == LinkState.Connecting) _link = LinkState.Idle;
        ConnectFailed?.Invoke(reason);
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
                _speech.Speak($"Disconnected from the server. {reason} Reconnecting.", interrupt: true);
                LeaveWorld();
                _link = LinkState.Reconnecting;
                _reconnectStarted = DateTime.UtcNow;
                _nextAttempt = _reconnectStarted + ReconnectInterval;
                return;
            case LinkState.Reconnecting:
                return;   // a connection made during a retry dropped again; the ticks carry on
            default:
                string line = $"Disconnected from the server. {reason}";
                _speech.Speak(line, interrupt: true);
                _link = LinkState.Idle;
                ConnectFailed?.Invoke(line);
                return;
        }
    }

    private void GiveUpReconnecting(bool speak)
    {
        Serilog.Log.Warning("Gave up reconnecting after {Sec:F0} s.", (DateTime.UtcNow - _reconnectStarted).TotalSeconds);
        _link = LinkState.Idle;
        DropConnection();
        if (speak) _speech.Speak("Could not reconnect. Back to the main menu.", interrupt: true);
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

        if (_link == LinkState.Reconnecting)
        {
            // Out of the world the bindings do not run, and Escape still has to reach the game menu.
            if (!IsInGame && Input.GetSnapshot().JustPressed.Contains(GameKey.Escape)
                && _shell.IsGameInputActive)
                ShowGameMenu();
            var now = DateTime.UtcNow;
            if (now - _reconnectStarted > ReconnectFor) { GiveUpReconnecting(speak: true); return; }
            if (now >= _nextAttempt && !_network.IsConnecting && !_network.IsConnected)
            {
                _nextAttempt = now + ReconnectInterval;
                Ui.Play(UiCue.Reconnecting);
                _network.Connect(_host, _port);
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
    /// The gain part way through a fade. Even steps of loudness, not of amplitude: linear in
    /// decibels over the 60 dB the fade spans, so the world neither lurches in at the start of a
    /// fade-in nor hangs on at the end of a fade-out.
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

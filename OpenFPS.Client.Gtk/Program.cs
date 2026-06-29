using System;
using System.IO;
using System.Threading;
using Gtk;
using Serilog;
using OpenFPS.Common;
using OpenFPS.Common.Networking;
using OpenFPS.Client.Core;          // ClientNetworkService
using OpenFPS.Client.Core.Platform; // ISpeechOutput / SpeechDispatcherOutput
using OpenFPS.Client.Gtk.Game;      // GameSession / GameWindow

// OpenFPS GTK (Linux) client — Phase B.
//   m1: accessible main menu + speech.
//   m2: connect + login to the server, speaking the result.
//   m3: enter the world — shared Core game loop, GTK key input, FMOD/Steam-Audio spatial sound.
// The network poll + fixed-step simulation run on one background thread (GameLoop); GTK widgets are
// only ever touched on the main thread (window handoff is marshaled via the captured UI context).
internal static class GtkClientProgram
{
    private static ISpeechOutput _speech = null!;
    private static ClientNetworkService _network = null!;
    private static volatile bool _networkStarted;
    private static Application _app = null!;
    private static ApplicationWindow _mainWindow = null!;
    private static SynchronizationContext? _uiContext;

    private static GameSession? _session;
    private static GameWindow? _gameWindow;
    private static bool _audioEnabled;

    private static string _pendingUser = "";
    private static string _pendingPass = "";

    public static int Main(string[] args)
    {
        Serilog.Log.Logger = new Serilog.LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console()
            .CreateLogger();
        Serilog.Log.Information("OpenFPS GTK client starting (PID {Pid}).", Environment.ProcessId);

        _speech = new SpeechDispatcherOutput();
        _speech.Initialize();

        _audioEnabled = FmodLibraryPresent();
        Serilog.Log.Information("Speech backend: {Backend}. Spatial audio: {Audio}.",
            _speech.BackendName, _audioEnabled ? "enabled" : "disabled (libfmod.so not found)");

        _network = new ClientNetworkService();
        _network.OnConnected += OnServerConnected;
        _network.OnMessageReceived += OnServerMessage;

        var loop = new Thread(GameLoop) { IsBackground = true, Name = "GameLoop" };
        loop.Start();

        _app = Application.New("org.openfps.client", Gio.ApplicationFlags.FlagsNone);
        _app.OnActivate += (sender, _) =>
        {
            _uiContext = SynchronizationContext.Current;
            BuildMainMenu((Application)sender);
        };
        int rc = _app.RunWithSynchronizationContext(null);

        _session?.Shutdown();
        _speech.Dispose();
        return rc;
    }

    /// <summary>FMOD resolves <c>libfmod.so</c> at runtime; without it, run with audio disabled.</summary>
    private static bool FmodLibraryPresent() =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, "libfmod.so"));

    // ── Game / network loop (background thread) ─────────────────────────────────
    private static void GameLoop()
    {
        var lastTime = DateTime.Now;
        double accumulator = 0.0;
        const double targetDt = PhysicsConstants.FixedDeltaTime;

        while (true)
        {
            try
            {
                if (_networkStarted) _network.Poll();

                var session = _session;
                if (session != null && session.IsInGame)
                {
                    var now = DateTime.Now;
                    double elapsed = (now - lastTime).TotalSeconds;
                    lastTime = now;
                    if (elapsed > 0.2) elapsed = 0.2;
                    accumulator += elapsed;

                    while (accumulator >= targetDt)
                    {
                        session.SimStep((float)targetDt);
                        accumulator -= targetDt;
                    }
                    session.ContinuousUpdate();
                }
                else
                {
                    lastTime = DateTime.Now; // avoid banking elapsed time while not simulating
                }
            }
            catch (Exception ex)
            {
                // A handler/sim exception must never silently kill the game loop (which pumps the network):
                // that would freeze the world-load handshake with no diagnostic. Log and keep pumping.
                Log.Error(ex, "GameLoop iteration failed.");
            }

            Thread.Sleep(5);
        }
    }

    // ── Main menu (m1) ──────────────────────────────────────────────────────────
    private static void BuildMainMenu(Application app)
    {
        _mainWindow = ApplicationWindow.New(app);
        _mainWindow.Title = "OpenFPS";
        _mainWindow.SetDefaultSize(480, 320);

        var box = VBox(24);
        box.Append(Label.New("OpenFPS — Main Menu"));
        box.Append(MenuButton("Connect to Server", ShowLoginDialog));
        box.Append(MenuButton("Settings", () => _speech.Speak("Settings. Not yet implemented.")));
        box.Append(MenuButton("Quit", () => { _speech.Speak("Goodbye."); _mainWindow.Close(); }));
        _mainWindow.SetChild(box);

        _mainWindow.Present();
        _speech.Speak("Open F P S main menu. Tab or arrow keys to move, Enter to select.", true);
        if (!_audioEnabled)
            _speech.Speak("Note: FMOD audio library not found. Running without spatial sound.");
    }

    private static void ShowLoginDialog()
    {
        var dialog = Window.New();
        dialog.Title = "Connect to Server";
        dialog.SetTransientFor(_mainWindow);
        dialog.SetModal(true);
        dialog.SetDefaultSize(420, 300);

        var box = VBox(16);
        var server = LabeledEntry(box, "Server address", "127.0.0.1:33288", false);
        var user = LabeledEntry(box, "Username", "", false);
        var pass = LabeledEntry(box, "Password", "", true);

        box.Append(MenuButton("Connect", () =>
        {
            string addr = server.GetText();
            _pendingUser = user.GetText();
            _pendingPass = pass.GetText();
            dialog.Close();
            DoConnect(addr);
        }));
        box.Append(MenuButton("Cancel", () => dialog.Close()));

        dialog.SetChild(box);
        dialog.Present();
        _speech.Speak("Connect dialog. Server address, username, and password fields.", true);
    }

    private static void DoConnect(string addr)
    {
        string host = "127.0.0.1";
        int port = 33288;
        var parts = addr.Split(':');
        if (parts.Length >= 1 && parts[0].Length > 0) host = parts[0];
        if (parts.Length >= 2 && int.TryParse(parts[1], out int p)) port = p;

        if (!_networkStarted) { _network.Start(); _networkStarted = true; }
        _speech.Speak($"Connecting to {host}, port {port}.", true);
        _network.Connect(host, port);
    }

    private static void OnServerConnected()
    {
        Log.Information("Connected to server; sending login for user '{User}'.", _pendingUser);
        _speech.Speak("Connected. Logging in.", true);
        _network.Send(new LoginRequest { Username = _pendingUser, Password = _pendingPass });
    }

    // ── Server messages (GameLoop thread) ───────────────────────────────────────
    private static void OnServerMessage(IMessage msg)
    {
        if (msg is LoginResponse lr)
        {
            Log.Information("LoginResponse: success={Success} user={User} msg={Msg}", lr.Success, lr.Username, lr.Message);
            if (lr.Success)
            {
                _speech.Speak($"Logged in as {lr.Username}. Loading world.", true);
                _session = new GameSession(_network, _speech, _audioEnabled);
                _session.GameJoined += OnGameJoined;
                _session.BeginAudioInit(); // off the network thread — must not block the load handshake
            }
            else
            {
                _speech.Speak($"Login failed. {lr.Message}", true);
            }
            return;
        }

        _session?.HandleMessage(msg);
    }

    /// <summary>Fired on the GameLoop thread when the local player spawns; build the game window on the UI thread.</summary>
    private static void OnGameJoined()
    {
        void Enter()
        {
            // Hide (don't close) the menu: closing it disrupts the new window's keyboard focus so the
            // game window stops receiving key events. The game window quits the whole app on close
            // (see GameWindow), so the hidden menu won't keep the process alive.
            _gameWindow = new GameWindow(_session!, _speech, () => _app.Quit());
            _gameWindow.Present(_app);
            _mainWindow.SetVisible(false);
        }

        if (_uiContext != null) _uiContext.Post(_ => Enter(), null);
        else Enter();
    }

    // ── UI helpers ──────────────────────────────────────────────────────────────
    private static Box VBox(int margin)
    {
        var box = Box.New(Orientation.Vertical, 8);
        box.MarginTop = margin; box.MarginBottom = margin; box.MarginStart = margin; box.MarginEnd = margin;
        return box;
    }

    private static Button MenuButton(string label, Action onActivate)
    {
        var btn = Button.NewWithLabel(label);
        btn.OnClicked += (_, _) => onActivate();
        SpeakOnFocus(btn, label);
        return btn;
    }

    private static Entry LabeledEntry(Box parent, string label, string initial, bool password)
    {
        parent.Append(Label.New(label));
        var entry = Entry.New();
        if (initial.Length > 0) entry.SetText(initial);
        if (password) entry.SetVisibility(false);
        SpeakOnFocus(entry, initial.Length > 0 ? $"{label}, {initial}" : label);
        parent.Append(entry);
        return entry;
    }

    private static void SpeakOnFocus(Widget widget, string text)
    {
        var focus = EventControllerFocus.New();
        focus.OnEnter += (_, _) => _speech.Speak(text, true);
        widget.AddController(focus);
    }
}

using System;
using Gtk;
using OpenFPS.Common.Networking;
using OpenFPS.Client.Core;          // ClientNetworkService
using OpenFPS.Client.Core.Platform; // ISpeechOutput / SpeechDispatcherOutput

// OpenFPS GTK (Linux) client — Phase B.
//   m1: accessible main menu + speech.
//   m2: connect + login to the server, speaking the result.
// Each control speaks on focus (non-visual navigation). Networking runs on a background poll
// thread; its callbacks only speak (thread-safe), so no GTK UI is touched off the main thread.
internal static class GtkClientProgram
{
    private static ISpeechOutput _speech = null!;
    private static ClientNetworkService _network = null!;
    private static volatile bool _networkStarted;
    private static ApplicationWindow _mainWindow = null!;
    private static string _pendingUser = "";
    private static string _pendingPass = "";

    public static int Main(string[] args)
    {
        _speech = new SpeechDispatcherOutput();
        _speech.Initialize();

        _network = new ClientNetworkService();
        _network.OnConnected += OnServerConnected;
        _network.OnMessageReceived += OnServerMessage;

        var poll = new System.Threading.Thread(PollLoop) { IsBackground = true, Name = "NetPoll" };
        poll.Start();

        var app = Application.New("org.openfps.client", Gio.ApplicationFlags.FlagsNone);
        app.OnActivate += (sender, _) => BuildMainMenu((Application)sender);
        int rc = app.RunWithSynchronizationContext(null);

        _speech.Dispose();
        return rc;
    }

    private static void PollLoop()
    {
        while (true)
        {
            if (_networkStarted) _network.Poll();
            System.Threading.Thread.Sleep(15);
        }
    }

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
        _speech.Speak("Connected. Logging in.", true);
        _network.Send(new LoginRequest { Username = _pendingUser, Password = _pendingPass });
    }

    private static void OnServerMessage(IMessage msg)
    {
        if (msg is LoginResponse lr)
        {
            if (lr.Success) _speech.Speak($"Logged in as {lr.Username}.", true);
            else _speech.Speak($"Login failed. {lr.Message}", true);
        }
    }

    // ── UI helpers ──────────────────────────────────────────────────────────
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

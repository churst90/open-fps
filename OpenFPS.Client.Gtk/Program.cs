using Gtk;
using Serilog;
using OpenFPS.Common;
using OpenFPS.Common.Networking;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.Core;          // ClientNetworkService
using OpenFPS.Client.Core.Platform; // ISpeechOutput / SpeechDispatcherOutput / NativeAudioLibraries
using OpenFPS.Client.Core.Session;  // ClientGameSession
using OpenFPS.Client.Gtk.Game;      // GtkClientShell / GameWindow

/// <summary>
/// The GTK (Linux) head: speech, the windows behind IClientShell and the key map. Everything else is
/// ClientGameSession, shared with the Windows head. The network poll and the simulation run on one
/// background thread (GameLoop); GTK widgets are touched only on the main thread.
/// </summary>
internal static partial class GtkClientProgram
{
    private static ISpeechOutput _speech = null!;
    private static ClientNetworkService _network = null!;
    private static Application _app = null!;
    private static ApplicationWindow _mainWindow = null!;

    private static SynchronizationContext? _uiContext;
    private static GtkClientShell _shell = null!;
    private static ClientGameSession _session = null!;
    private static string _missingAudioReport = "";

    // What the connect form submitted, remembered as a saved server once the login is accepted.
    private static string _pendingUser = "";
    private static string _pendingPass = "";
    private static string _pendingAddress = "";
    private static bool _pendingRemember;

    // The connect form stays up until the server has answered: closed on the button press, the main
    // menu's focus announcement cut off the spoken rejection of a wrong password.
    private static Window? _loginDialog;
    private static bool _loginRegister;
    /// <summary>The server's shortest password (AuthService.MinPasswordLength).</summary>
    private const int MinPassword = 8;
    private static Label? _loginStatus;
    private static string _loginStatusText = "";

    // Set just before a GrabFocus whose reason has already been spoken, so the widget's name does not
    // interrupt it.
    private static bool _suppressFocusSpeech;

    /// <summary>When this process started, so every "why did it stop" line can say how long it ran.</summary>
    private static readonly DateTime _startedUtc = DateTime.UtcNow;

    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    private static extern IntPtr signal(int signum, IntPtr handler);
    private const int SIGPIPE = 13;
    private static readonly IntPtr SIG_IGN = new(1);

    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    private static extern int prctl(int option, ulong arg2, ulong arg3, ulong arg4, ulong arg5);
    private const int PR_SET_PTRACER = 0x59616d61;
    private const ulong PR_SET_PTRACER_ANY = unchecked((ulong)-1);

    /// <summary>Allows any process to ptrace this one, so the watchdog's createdump can read it.</summary>
    private static void SetPtracerAny()
    {
        try { prctl(PR_SET_PTRACER, PR_SET_PTRACER_ANY, 0, 0, 0); }
        catch (Exception ex) { Serilog.Log.Warning(ex, "Could not allow ptrace; a hang dump will fail."); }
    }

    /// <summary>
    /// Stops a closed stdout from killing the process. .NET installs no SIGPIPE handler, and the default
    /// action terminates with no exception, core or log line; ignored, a write fails with EPIPE, which
    /// the console sink swallows.
    /// </summary>
    private static void IgnoreSigPipe()
    {
        try { signal(SIGPIPE, SIG_IGN); }
        catch (Exception ex) { Serilog.Log.Warning(ex, "Could not ignore SIGPIPE; a closed terminal can still stop the client."); }
    }

    public static int Main(string[] args)
    {
        // The log file is written by this process, not by a `| tee` in the launcher: when the terminal
        // went away, tee died and took the client with it, and the log ended mid-sentence like a crash.
        var logCfg = new Serilog.LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console();
        string? logPath = Environment.GetEnvironmentVariable("OPENFPS_LOG");
        if (!string.IsNullOrWhiteSpace(logPath))
            logCfg = logCfg.WriteTo.File(logPath, shared: true, flushToDiskInterval: TimeSpan.FromSeconds(2));
        Serilog.Log.Logger = logCfg.CreateLogger();

        IgnoreSigPipe();
        Serilog.Log.Information("OpenFPS GTK client starting (PID {Pid}).", Environment.ProcessId);

        // FMOD's logging build (libfmodL.so, `run-gtk-client.sh fmodlog`) reports API misuse by name.
        // It must be armed before System::create, so here and not in the audio provider; with the
        // ordinary libfmod.so this does nothing.
        string? fmodArmed = OpenFPS.Client.Core.AudioEngine.Fmod.FmodDebugLog.ArmFromEnvironment();
        if (fmodArmed != null) Serilog.Log.Information("{Line} Use `run-gtk-client.sh fmodlog`.", fmodArmed);

        ProcessLifeLog.Install(_startedUtc);
        Console.CancelKeyPress += (_, _) => Serilog.Log.Information("Interrupted at the keyboard.");

        // Orca when it is running, speech-dispatcher when it is not, decided per line.
        _speech = new OpenFPS.Client.Gtk.Platform.LinuxSpeechOutput();
        _speech.Initialize();

        var missingLibs = NativeAudioLibraries.FindMissing();
        _missingAudioReport = NativeAudioLibraries.DescribeMissing(missingLibs);
        bool audioEnabled = NativeAudioLibraries.IsPresent(NativeAudioLibraries.FmodFileName);
        if (missingLibs.Count > 0) Serilog.Log.Warning("DEGRADED AUDIO. {Report}", _missingAudioReport);

        DiagnosticSwitches.LogSet();

        Serilog.Log.Information("Speech backend: {Backend}. Spatial audio: {Audio}.",
            _speech.BackendName, audioEnabled ? "enabled" : "disabled (no FMOD library)");

        _network = new ClientNetworkService();
        _network.OnMessageReceived += OnServerMessage;

        // Built before login so the session handles the login response itself; audio starts on a
        // background thread in BeginAudioInit.
        var audioEngine = new AudioEngineFacade();
        _shell = new GtkClientShell(_speech, onQuit: () => _app?.Quit(), onCue: cue => _session?.Ui.Play(cue));
        _session = new ClientGameSession(_network, _speech, _shell, audioEngine,
            // FMOD's own recording, on the microphone chosen in Settings (Windows uses NAudio).
            microphone: new FmodMicrophoneCapture(audioEngine, () => _settings.InputDevice),
            enableAudio: audioEnabled);
        // The shell clears held keys around modal dialogs with the session's input buffer, which only
        // exists once the session (which needs the shell) is built.
        _shell.SetInput(_session.Input);
        // The session speaks the outcome; the head only moves the form out of the way, or puts focus
        // back where the player can correct the mistake.
        _session.LoginSucceeded += _ => OnUi(() =>
        {
            // A server you have just logged in to by hand is remembered, so Connect can go straight back.
            if (_loginDialog != null) RememberServer(_pendingAddress, _pendingUser, _pendingPass, _pendingRemember);
            CloseLoginDialog();
        });
        _session.LoginFailed += reason => OnLoginOutcome($"Login failed. {reason}", success: false);
        // Connecting, or creating the account, failed: already spoken; the form keeps the reason.
        _session.ConnectFailed += reason => OnLoginOutcome(reason, success: false);
        _settings = ClientSettings.Load();
        if (!OpenFPS.Common.Loudness.CompressionFromEnvironment)
            OpenFPS.Common.Loudness.DynamicRangeCompression = _settings.LevelCompression;
        _settings.ApplyNavigationAids();
        _settings.ApplyHearing();
        _session.BeginAudioInit(ApplyAudioSettings);

        var loop = new Thread(GameLoop) { IsBackground = true, Name = "GameLoop" };
        loop.Start();

        var watchdog = new Thread(Watchdog) { IsBackground = true, Name = "Watchdog", Priority = ThreadPriority.AboveNormal };
        watchdog.Start();

        _app = Application.New("org.openfps.client", Gio.ApplicationFlags.FlagsNone);
        _app.OnActivate += (sender, _) =>
        {
            var app = (Application)sender;
            _uiContext = SynchronizationContext.Current;
            BuildMainMenu(app);
            _shell.AttachToApplication(app, _uiContext, _mainWindow);
        };
        int rc = _app.RunWithSynchronizationContext(null);

        // Logged so a closed window can be told from a killed process.
        Serilog.Log.Information("GTK main loop returned {Rc} — the last window closed. Shutting down after {Sec:F0} s.",
                                rc, (DateTime.UtcNow - _startedUtc).TotalSeconds);
        _session.Dispose();
        _speech.Dispose();
        Serilog.Log.Information("Client shut down cleanly.");
        Serilog.Log.CloseAndFlush();
        return rc;
    }

    /// <summary>
    /// The game loop's last tick, in UTC ticks, read by the watchdog. Zero until the first tick, and the
    /// watchdog does not arm before then: the loop does not tick at the main menu.
    /// </summary>
    private static long _loopBeat;

    /// <summary>
    /// Notices a hang (the game loop stalled for eight seconds) and takes a dump of every thread with the
    /// runtime's createdump, then keeps running: a hung client that recovers is worth knowing about too.
    /// </summary>
    private static void Watchdog()
    {
        const int StallSeconds = 8;
        bool dumped = false;
        while (true)
        {
            Thread.Sleep(1000);
            long beat = System.Threading.Volatile.Read(ref _loopBeat);
            if (beat == 0) continue;                      // not in the world yet: there is nothing to stall
            double since = (DateTime.UtcNow - new DateTime(beat, DateTimeKind.Utc)).TotalSeconds;
            if (since < StallSeconds) { dumped = false; continue; }
            if (dumped) continue;
            dumped = true;

            Serilog.Log.Fatal("GAME LOOP STALLED for {Sec:F0} s — the client is hung, not crashed. "
                            + "Taking a dump of every thread so the deadlock can be read.", since);
            // Not CloseAndFlush: that shuts the logger down, and a client that recovers would leave no
            // record of it.

            try
            {
                string dir = Environment.GetEnvironmentVariable("OPENFPS_CRASHDIR")
                             ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "openfps-crashes");
                Directory.CreateDirectory(dir);
                string createdump = Path.Combine(AppContext.BaseDirectory, "createdump");
                if (!File.Exists(createdump))
                {
                    // It ships beside the runtime, not beside the app.
                    string? rt = Path.GetDirectoryName(typeof(object).Assembly.Location);
                    if (rt != null) createdump = Path.Combine(rt, "createdump");
                }
                string target = Path.Combine(dir, $"openfps-hang.{Environment.ProcessId}.dmp");
                // createdump reads /proc/<pid>/mem, which Yama forbids between unrelated processes
                // unless the target opts in ("Permission denied (13)" otherwise).
                SetPtracerAny();
                var psi = new System.Diagnostics.ProcessStartInfo(createdump,
                    $"--full --name \"{target}\" {Environment.ProcessId}") { UseShellExecute = false };
                System.Diagnostics.Process.Start(psi)?.WaitForExit(60000);
                Serilog.Log.Fatal("Hang dump written to {Path} (if createdump was present). "
                                + "Read it with: dotnet-dump analyze <file>", target);
            }
            catch (Exception ex) { Serilog.Log.Error(ex, "Could not take a hang dump."); }
        }
    }

    private static void GameLoop()
    {
        var lastTime = DateTime.Now;
        double accumulator = 0.0;
        const double targetDt = PhysicsConstants.FixedDeltaTime;
        var _lastLoopReport = DateTime.Now;
        int _loopIterations = 0;
        double _worstLoopMs = 0;

        while (true)
        {
            try
            {
                _network.Poll();
                // Reconnects, the world fade and anything a menu handed over: in or out of the world.
                _session.Tick();

                if (_session.IsInGame)
                {
                    var now = DateTime.Now;
                    double elapsed = (now - lastTime).TotalSeconds;
                    lastTime = now;
                    if (elapsed > PhysicsConstants.MaxCatchUpSeconds) elapsed = PhysicsConstants.MaxCatchUpSeconds;
                    accumulator += elapsed;

                    while (accumulator >= targetDt)
                    {
                        _session.SimStep((float)targetDt);
                        accumulator -= targetDt;
                    }
                    _session.ContinuousUpdate();

                    // Every moving sound is placed once per iteration, so this rate is their resolution.
                    // Logged beside the audio system's own figure, so a stall is pinned on one or the other.
                    _loopIterations++;
                    System.Threading.Volatile.Write(ref _loopBeat, DateTime.UtcNow.Ticks);
                    double loopMs = (DateTime.Now - now).TotalMilliseconds;
                    if (loopMs > _worstLoopMs) _worstLoopMs = loopMs;
                    if ((now - _lastLoopReport).TotalSeconds >= 5.0)
                    {
                        double hz = _loopIterations / (now - _lastLoopReport).TotalSeconds;
                        if (hz < 45 || _worstLoopMs > 100)
                            Log.Warning("Game loop: {Hz:F0} Hz, worst iteration {Worst:F0} ms. Every moving sound "
                                      + "is placed once per iteration, so this is how often a car's engine moves.",
                                        hz, _worstLoopMs);
                        else
                            Log.Information("Game loop: {Hz:F0} Hz, worst iteration {Worst:F0} ms.", hz, _worstLoopMs);
                        _lastLoopReport = now; _loopIterations = 0; _worstLoopMs = 0;
                    }
                }
                else
                {
                    lastTime = DateTime.Now; // avoid banking elapsed time while not simulating
                }

                // Costs nothing unless OPENFPS_PROFILE=1; see PerfProbe.
                PerfProbe.ReportIfDue(TimeSpan.FromSeconds(30), line => Log.Information("{Perf}", line));
            }
            catch (Exception ex)
            {
                // The loop pumps the network: one exception must not end it.
                Log.Error(ex, "GameLoop iteration failed.");
            }

            Thread.Sleep(5);
        }
    }

    private static void BuildMainMenu(Application app)
    {
        _mainWindow = ApplicationWindow.New(app);
        _mainWindow.Title = "OpenFPS";
        _mainWindow.SetDefaultSize(480, 320);
        Scene.Apply(_mainWindow);

        var box = VBox(24);
        box.Append(Label.New("OpenFPS — Main Menu"));
        box.Append(MenuButton("Connect", ConnectPreferred));
        box.Append(MenuButton("Create account", CreateAccountPreferred));
        box.Append(MenuButton("Saved Servers", ShowServers));
        box.Append(MenuButton("Settings", ShowSettings));
        box.Append(MenuButton("Quit", () => { _speech.Speak("Goodbye."); _app.Quit(); }));
        _mainWindow.SetChild(box);
        // The game window may be hidden behind it after Main menu; closing this one still quits.
        _mainWindow.OnCloseRequest += (_, _) => { _app.Quit(); return false; };

        _mainWindow.Present();
        _speech.Speak("Open F P S main menu. Tab or arrow keys to move, Enter to select.", true);
        if (_missingAudioReport.Length > 0)
            _speech.Speak("Warning. " + _missingAudioReport);
    }

    /// <summary>The Connect dialog; with <paramref name="register"/>, the Create Account form: blank, with
    /// no Connect button (one there logged in as the account that had just failed to be made).</summary>
    private static void ShowLoginDialog(OpenFPS.Client.Core.SavedServer? saved, bool register = false)
    {
        if (_loginDialog != null)
        {
            // Asked for the other form: that one goes. Asked for the same one: it comes back and says so,
            // rather than coming forward without a word.
            if (_loginRegister != register) CloseLoginDialog();
            else
            {
                _loginDialog.Present();
                _speech.Speak($"{_loginDialog.Title}. {(_loginStatusText.Length > 0 ? _loginStatusText : "")}", true);
                return;
            }
        }
        _loginRegister = register;

        var dialog = Window.New();
        dialog.Title = register ? "Create Account" : "Connect to Server";
        dialog.SetTransientFor(_mainWindow);
        dialog.SetModal(true);
        dialog.SetDefaultSize(420, 320);
        dialog.OnCloseRequest += (_, _) => { _loginDialog = null; _loginStatus = null; return false; };
        CloseOnEscape(dialog);

        var box = VBox(16);

        // Focusable, so the last outcome can be read again by tabbing back to it.
        _loginStatusText = "";
        _loginStatus = Label.New("");
        _loginStatus.SetWrap(true);
        _loginStatus.SetFocusable(true);
        SpeakOnFocus(_loginStatus, () => _loginStatusText.Length > 0 ? _loginStatusText : "No messages.");
        box.Append(_loginStatus);

        var server = LabeledEntry(box, "Server address", saved != null ? $"{saved.Host}:{saved.Port}" : "127.0.0.1:33288", false);
        // A new account starts blank: the saved account's name is not the one being made.
        var user = LabeledEntry(box, "Username", register ? "" : saved?.Username ?? "", false);
        // The server's own rule, said before it can refuse.
        var pass = LabeledEntry(box, register ? $"Password, at least {MinPassword} characters" : "Password", "", true);
        var remember = CheckButton.NewWithLabel("Remember password");
        remember.SetActive(saved?.RememberPassword ?? false);
        SpeakOnFocus(remember, () => $"Remember password, {(remember.GetActive() ? "checked" : "not checked")}");
        box.Append(remember);

        void Submit(bool register)
        {
            _pendingAddress = server.GetText().Trim();
            _pendingUser = user.GetText().Trim();
            _pendingPass = pass.GetText();
            _pendingRemember = remember.GetActive();
            _loginStatusText = register ? "Creating the account..." : "Connecting...";
            _loginStatus?.SetText(_loginStatusText);
            _session.Connect(_pendingAddress, _pendingUser, _pendingPass, register);
        }
        if (register)
            box.Append(MenuButton("Create account", () => Submit(register: true)));
        else
        {
            box.Append(MenuButton("Connect", () => Submit(register: false)));
            box.Append(MenuButton("Create account", () => Submit(register: true)));
        }
        box.Append(MenuButton("Cancel", () => { Cue(UiCue.MenuBack); CloseLoginDialog(); }));

        _loginDialog = dialog;
        dialog.SetChild(box);
        dialog.Present();
        if (register && saved != null)
        {
            _suppressFocusSpeech = true;
            user.GrabFocus();
            _speech.Speak($"Create an account on {(saved.Name.Length > 0 ? saved.Name : saved.Host)}. Username.", true);
        }
        else if (saved != null && saved.Username.Length > 0)
        {
            _suppressFocusSpeech = true;
            pass.GrabFocus();
            _speech.Speak($"Connect to {saved.Name} as {saved.Username}. Password.", true);
        }
        else _speech.Speak("Connect dialog. Server address, username, and password fields.", true);
    }

    /// <summary>Puts a login outcome, already spoken, on the open form, and moves focus there silently so
    /// the widget's name does not interrupt it.</summary>
    private static void OnLoginOutcome(string message, bool success) => OnUi(() =>
    {
        if (_loginDialog == null) return;
        _loginStatusText = message;
        _loginStatus?.SetText(message);
        if (success) { CloseLoginDialog(); return; }
        // To the status line: on Username, the screen reader read the field over the reason.
        _suppressFocusSpeech = true;
        _loginStatus?.GrabFocus();
    });

    private static void CloseLoginDialog()
    {
        var dialog = _loginDialog;
        _loginDialog = null; _loginStatus = null;
        dialog?.Close();
    }

    /// <summary>Runs an action on the GTK main thread. Login outcomes arrive on the game-loop thread.</summary>
    private static void OnUi(Action action)
    {
        var ui = _uiContext;
        if (ui != null) ui.Post(_ => action(), null);
        else action();
    }

    /// <summary>
    /// The folder the log is written to (OPENFPS_LOG's, as run-gtk-client.sh sets it), opened in the
    /// file manager. Without a log file it says so: the console is the only copy then.
    /// </summary>
    private static void OpenLogFolder()
    {
        string? log = Environment.GetEnvironmentVariable("OPENFPS_LOG");
        string? dir = string.IsNullOrWhiteSpace(log) ? null : Path.GetDirectoryName(Path.GetFullPath(log));
        if (dir == null || !Directory.Exists(dir))
        {
            _speech.Speak("This run is not writing a log file. Start it with run-gtk-client.sh to keep one.", true);
            return;
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("xdg-open", $"\"{dir}\"") { UseShellExecute = false });
            _speech.Speak($"Opening {dir}.", true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not open the log folder.");
            _speech.Speak($"Could not open the log folder. It is {dir}", true);
        }
    }

    // Server messages arrive on the GameLoop thread.
    private static void OnServerMessage(IMessage msg) => _session.HandleMessage(msg);

    private static Box VBox(int margin)
    {
        var box = Box.New(Orientation.Vertical, 8);
        box.MarginTop = margin; box.MarginBottom = margin; box.MarginStart = margin; box.MarginEnd = margin;
        return box;
    }

    private static Button MenuButton(string label, Action onActivate)
    {
        var btn = Button.NewWithLabel(label);
        btn.OnClicked += (_, _) => { Cue(UiCue.MenuSelect); onActivate(); };
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

    private static void SpeakOnFocus(Widget widget, string text) => SpeakOnFocus(widget, () => text);

    private static void SpeakOnFocus(Widget widget, Func<string> text)
    {
        var focus = EventControllerFocus.New();
        focus.OnEnter += (_, _) =>
        {
            // A programmatic focus move that already announced its reason must not speak over it.
            if (_suppressFocusSpeech) { _suppressFocusSpeech = false; return; }
            Cue(UiCue.MenuMove);
            _speech.Speak(text(), true);
        };
        widget.AddController(focus);
    }
}

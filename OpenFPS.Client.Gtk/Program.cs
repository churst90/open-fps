using System;
using System.IO;
using System.Threading;
using Gtk;
using Serilog;
using OpenFPS.Common;
using OpenFPS.Common.Networking;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.Core;          // ClientNetworkService
using OpenFPS.Client.Core.Platform; // ISpeechOutput / SpeechDispatcherOutput / NativeAudioLibraries
using OpenFPS.Client.Core.Session;  // ClientGameSession
using OpenFPS.Client.Gtk.Game;      // GtkClientShell / GameWindow

// OpenFPS GTK (Linux) client.
//
// The head is now genuinely thin: it owns speech, the GTK windows behind IClientShell, and the
// GDK-keyval-to-GameKey map. Everything else — netcode, prediction, acoustics, bindings, the spoken
// announcements — is ClientGameSession in OpenFPS.Client.Core, shared verbatim with the Windows head.
//
// The network poll + fixed-step simulation run on one background thread (GameLoop); GTK widgets are
// only ever touched on the main thread (the shell marshals through the captured UI context).
internal static partial class GtkClientProgram
{
    private static ISpeechOutput _speech = null!;
    private static ClientNetworkService _network = null!;
    private static volatile bool _networkStarted;
    private static Application _app = null!;
    private static ApplicationWindow _mainWindow = null!;

    private static SynchronizationContext? _uiContext;
    private static GtkClientShell _shell = null!;
    private static ClientGameSession _session = null!;
    private static string _missingAudioReport = "";

    private static string _pendingUser = "";
    private static string _pendingPass = "";
    private static string _pendingAddress = "";
    private static bool _pendingRemember;

    // The connect form stays up until the server has answered. Closing it on the button press dropped
    // focus back onto the main menu, and that focus announcement — spoken with interrupt — cut off the
    // rejection the session had just said, so a wrong password was indistinguishable from silence.
    private static Window? _loginDialog;
    private static Entry? _loginUser;
    private static Label? _loginStatus;
    private static string _loginStatusText = "";

    // Set immediately before a programmatic GrabFocus whose reason has ALREADY been spoken, so the
    // focus handler does not interrupt it with the name of the widget it just landed on.
    private static bool _suppressFocusSpeech;

    /// <summary>When this process started, so every "why did it stop" line can say how long it ran.</summary>
    private static readonly DateTime _startedUtc = DateTime.UtcNow;

    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    private static extern IntPtr signal(int signum, IntPtr handler);
    private const int SIGPIPE = 13;
    private static readonly IntPtr SIG_IGN = new(1);

    /// <summary>
    /// Stops a closed stdout from killing the process.
    ///
    /// .NET does not install a SIGPIPE handler, and the default action for it is to TERMINATE — no
    /// exception, no core, no dump, nothing in the log. So a client whose console reader has gone
    /// away dies silently the next time it writes a line, which on a chatty debug run is within
    /// milliseconds. Writes now fail with EPIPE, which the runtime turns into an ordinary IOException
    /// the console sink swallows, and the file sink keeps the log regardless.
    /// </summary>
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

    private static void IgnoreSigPipe()
    {
        try { signal(SIGPIPE, SIG_IGN); }
        catch (Exception ex) { Serilog.Log.Warning(ex, "Could not ignore SIGPIPE; a closed terminal can still stop the client."); }
    }

    public static int Main(string[] args)
    {
        // ── THE LOG IS WRITTEN BY THIS PROCESS, not by a pipe ────────────────────────────────────
        //
        // It used to go to the console and be captured by `| tee` in the launcher, and that puts the
        // client's life in the hands of whatever is reading its stdout. If the terminal goes away —
        // closed, or an emulator that stops reading after you alt-tab — tee dies, the client gets
        // SIGPIPE, and the process is gone: no exception, no core, no dump, and a log that ends
        // mid-sentence. Which is indistinguishable from the crash we were actually hunting, and cost
        // a session to tell apart.
        //
        // So Serilog writes the file itself. The console sink stays for watching it live, and the
        // tee in the launcher is now belt and braces rather than the only copy.
        var logCfg = new Serilog.LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console();
        string? logPath = Environment.GetEnvironmentVariable("OPENFPS_LOG");
        if (!string.IsNullOrWhiteSpace(logPath))
            logCfg = logCfg.WriteTo.File(logPath, shared: true, flushToDiskInterval: TimeSpan.FromSeconds(2));
        Serilog.Log.Logger = logCfg.CreateLogger();

        // ...and a dead reader must not be able to kill us at all. SIGPIPE's default action is to
        // terminate the process; a GUI application has no business dying because nobody is listening
        // to its stdout.
        IgnoreSigPipe();
        Serilog.Log.Information("OpenFPS GTK client starting (PID {Pid}).", Environment.ProcessId);

        // ── LET FMOD SAY WHAT IS WRONG, INSTEAD OF GUESSING FROM A CORE ──────────────────────────
        //
        // FMOD ships a LOGGING build, libfmodL.so, which validates every call and reports API misuse
        // by name — the handle that was stale, the object that was still connected, the thread it
        // happened on. Three sessions went into reading fault addresses out of core files to learn
        // things this build would have printed.
        //
        // It must be armed BEFORE System::create or it does nothing at all, which is why it is here
        // and not in the audio provider. `run-gtk-client.sh fmodlog` swaps the library in and sets
        // the variable; with the ordinary libfmod.so this call is a no-op and costs nothing.
        string? fmodArmed = OpenFPS.Client.Core.AudioEngine.Fmod.FmodDebugLog.ArmFromEnvironment();
        if (fmodArmed != null) Serilog.Log.Information("{Line} Use `run-gtk-client.sh fmodlog`.", fmodArmed);

        // ── SAY WHY IT STOPPED ───────────────────────────────────────────────────────────────────
        //
        // The client used to end its log mid-sentence whatever happened to it: a clean quit, an
        // unhandled exception on a background thread, and a native crash all look identical from the
        // chair, because what a player notices is the sound stopping. Four sessions were spent
        // guessing between them.
        //
        // These cost nothing and answer it. A clean exit now says so; an exception says what it was
        // and on which thread; and a native crash still says nothing here — which is itself the
        // answer, because then the absence of these lines is what identifies it.
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            Serilog.Log.Information("Client process exiting normally (ran {Sec:F0} s).",
                                    (DateTime.UtcNow - _startedUtc).TotalSeconds);
            Serilog.Log.CloseAndFlush();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Serilog.Log.Fatal(e.ExceptionObject as Exception,
                              "UNHANDLED EXCEPTION on a background thread — terminating={T}.", e.IsTerminating);
            Serilog.Log.CloseAndFlush();
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Serilog.Log.Error(e.Exception, "Unobserved task exception (the task was collected without anyone reading it).");
            e.SetObserved();
        };
        Console.CancelKeyPress += (_, _) => Serilog.Log.Information("Interrupted at the keyboard.");

        // Orca when it is running, speech-dispatcher when it is not — decided per line, not once.
        // See LinuxSpeechOutput.
        _speech = new OpenFPS.Client.Gtk.Platform.LinuxSpeechOutput();
        _speech.Initialize();

        // Report EXACTLY which native audio libraries are missing and what each one costs. "Audio
        // disabled" on its own tells a player nothing they can act on.
        var missingLibs = NativeAudioLibraries.FindMissing();
        _missingAudioReport = NativeAudioLibraries.DescribeMissing(missingLibs);
        bool audioEnabled = NativeAudioLibraries.IsPresent(NativeAudioLibraries.FmodFileName);
        if (missingLibs.Count > 0) Serilog.Log.Warning("DEGRADED AUDIO. {Report}", _missingAudioReport);

        // Say what the diagnostic levers are set to, every run. A session that behaved differently
        // because an environment variable was still set from the last one is a day lost.
        foreach (string key in new[] { "OPENFPS_ENGINE_ECHOES", "OPENFPS_ENGINE_VOICES",
                                       "OPENFPS_MACHINE_VOICES", "OPENFPS_WEATHER", "OPENFPS_AUDIO_DEBUG" })
        {
            string? val = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrEmpty(val)) Serilog.Log.Warning("{Key}={Value} — a diagnostic lever is set.", key, val);
        }

        Serilog.Log.Information("Speech backend: {Backend}. Spatial audio: {Audio}.",
            _speech.BackendName, audioEnabled ? "enabled" : "disabled (no FMOD library)");

        _network = new ClientNetworkService();
        _network.OnConnected += OnServerConnected;
        _network.OnMessageReceived += OnServerMessage;
        // Connection and protocol failures are spoken, not swallowed.
        _network.OnConnectionFailed += reason => { _speech.Speak(reason, true); OnLoginOutcome(reason, success: false); };
        _network.OnProtocolError += reason => _speech.Speak(reason, true);
        _network.OnConnectionNotice += notice => _speech.Speak(notice, true);

        // The session is built up front (both heads do this now) so it can handle the login response
        // itself; audio initialization is deferred to a background thread inside BeginAudioInit.
        var audioEngine = new AudioEngineFacade();
        _shell = new GtkClientShell(_speech, onQuit: () => _app?.Quit());
        _session = new ClientGameSession(_network, _speech, _shell, audioEngine,
            microphone: new NullMicrophoneCapture("Voice chat is not available on Linux yet."),
            enableAudio: audioEnabled);
        // The shell needs the session's input buffer to clear held keys around modal dialogs; the
        // session needs the shell at construction. The buffer is created by the session, so it is
        // handed over immediately afterwards.
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
        _settings = ClientSettings.Load();
        if (!OpenFPS.Common.Loudness.CompressionFromEnvironment)
            OpenFPS.Common.Loudness.DynamicRangeCompression = _settings.LevelCompression;
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

        // The GTK loop returning IS the shutdown: it happens when the last window closes. Saying so
        // distinguishes "somebody or something closed the window" from "the process was killed",
        // which are the two things that look the same and need completely different fixes.
        Serilog.Log.Information("GTK main loop returned {Rc} — the last window closed. Shutting down after {Sec:F0} s.",
                                rc, (DateTime.UtcNow - _startedUtc).TotalSeconds);
        _session.Dispose();
        _speech.Dispose();
        Serilog.Log.Information("Client shut down cleanly.");
        Serilog.Log.CloseAndFlush();
        return rc;
    }

    /// <summary>
    /// The game loop's last tick, in UTC ticks. Written by the loop, read by the watchdog.
    ///
    /// ZERO until the loop has ticked ONCE, and the watchdog will not arm before then. The loop only
    /// runs while a session is simulating, so between launch and joining a map it does not tick at
    /// all — and a watchdog that calls that a stall fires eight seconds into every single run, which
    /// is exactly what it did: a false alarm at the main menu that then wrecked the very run it was
    /// supposed to be measuring.
    /// </summary>
    private static long _loopBeat;

    /// <summary>
    /// Notices when the client has STOPPED, which is the one failure it had no instrument for.
    ///
    /// Three ways it can end were already covered — a native crash leaves a dump, a clean exit says
    /// so, a dead terminal can no longer kill it. The fourth is a HANG: everything simply stops, the
    /// log ends mid-stream, and from the chair that is identical to a crash because what you notice
    /// is the sound stopping. It has been reported as "it crashed" at least twice.
    ///
    /// A hang is almost always a deadlock, and the only useful evidence is what every thread was
    /// doing at the time — which is exactly what a dump holds. So this takes one, using the runtime's
    /// own createdump against this process, and keeps running: the client is not killed, because a
    /// hung client that recovers is worth knowing about too.
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
            // NOT CloseAndFlush. That SHUTS THE LOGGER DOWN, and the first version called it here —
            // so from the moment the watchdog fired, nothing else was ever written to the file. The
            // run it was watching went on for another ninety seconds and left no record of any of
            // it. A diagnostic that destroys the evidence it exists to collect is worse than none.

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
                // LET IT ATTACH. createdump is a separate process and reads /proc/<pid>/mem, which
                // the Yama LSM forbids between unrelated processes unless the target opts in —
                // "Permission denied (13)", which is what the first hang dump came back with. The
                // runtime does this for itself before spawning createdump on a crash; a watchdog
                // spawning it by hand has to do the same.
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

    // ── Game / network loop (background thread) ─────────────────────────────────
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
                if (_networkStarted) _network.Poll();

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

                    // ── What rate this loop is actually managing ──────────────────────────────
                    //
                    // ContinuousUpdate is where every sound in the world gets its position, so the
                    // period of THIS loop is the resolution of every moving source. Nothing measured
                    // it, and with thirty cars instead of eight there is four times the per-source
                    // work inside one iteration. Reported next to the audio system's own figure, so
                    // a stall can be attributed to the loop or to the audio pass rather than guessed
                    // at: if the loop is slow, it is the loop; if the loop is fine and the placement
                    // gap is not, it is the audio pass.
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
                // A handler/sim exception must never silently kill the game loop (which pumps the
                // network): that would freeze the world-load handshake with no diagnostic.
                Log.Error(ex, "GameLoop iteration failed.");
            }

            Thread.Sleep(5);
        }
    }

    // ── Main menu ───────────────────────────────────────────────────────────────
    private static void BuildMainMenu(Application app)
    {
        _mainWindow = ApplicationWindow.New(app);
        _mainWindow.Title = "OpenFPS";
        _mainWindow.SetDefaultSize(480, 320);

        var box = VBox(24);
        box.Append(Label.New("OpenFPS — Main Menu"));
        box.Append(MenuButton("Connect", ConnectPreferred));
        box.Append(MenuButton("Saved Servers", ShowServers));
        box.Append(MenuButton("Settings", ShowSettings));
        box.Append(MenuButton("Quit", () => { _speech.Speak("Goodbye."); _mainWindow.Close(); }));
        _mainWindow.SetChild(box);

        _mainWindow.Present();
        _speech.Speak("Open F P S main menu. Tab or arrow keys to move, Enter to select.", true);
        if (_missingAudioReport.Length > 0)
            _speech.Speak("Warning. " + _missingAudioReport);
    }

    private static void ShowLoginDialog(OpenFPS.Client.Core.SavedServer? saved)
    {
        if (_loginDialog != null) { _loginDialog.Present(); return; }

        var dialog = Window.New();
        dialog.Title = "Connect to Server";
        dialog.SetTransientFor(_mainWindow);
        dialog.SetModal(true);
        dialog.SetDefaultSize(420, 320);
        dialog.OnCloseRequest += (_, _) => { _loginDialog = null; _loginUser = null; _loginStatus = null; return false; };
        CloseOnEscape(dialog);

        var box = VBox(16);

        // A focusable status line so the last outcome can be re-read by tabbing back to it, rather than
        // existing only as speech that has already gone by.
        _loginStatusText = "";
        _loginStatus = Label.New("");
        _loginStatus.SetWrap(true);
        _loginStatus.SetFocusable(true);
        SpeakOnFocus(_loginStatus, () => _loginStatusText.Length > 0 ? _loginStatusText : "No messages.");
        box.Append(_loginStatus);

        var server = LabeledEntry(box, "Server address", saved != null ? $"{saved.Host}:{saved.Port}" : "127.0.0.1:33288", false);
        var user = LabeledEntry(box, "Username", saved?.Username ?? "", false);
        var pass = LabeledEntry(box, "Password", "", true);
        var remember = CheckButton.NewWithLabel("Remember password");
        remember.SetActive(saved?.RememberPassword ?? false);
        SpeakOnFocus(remember, () => $"Remember password, {(remember.GetActive() ? "checked" : "not checked")}");
        box.Append(remember);
        _loginUser = user;

        box.Append(MenuButton("Connect", () =>
        {
            string addr = server.GetText();
            _pendingAddress = addr;
            _pendingUser = user.GetText();
            _pendingPass = pass.GetText();
            _pendingRemember = remember.GetActive();
            // The dialog stays open: it closes only once the server has accepted the login.
            DoConnect(addr);
        }));
        box.Append(MenuButton("Cancel", () => { Cue(UiCue.MenuBack); CloseLoginDialog(); }));

        _loginDialog = dialog;
        dialog.SetChild(box);
        dialog.Present();
        if (saved != null && saved.Username.Length > 0)
        {
            _suppressFocusSpeech = true;
            pass.GrabFocus();
            _speech.Speak($"Connect to {saved.Name} as {saved.Username}. Password.", true);
        }
        else _speech.Speak("Connect dialog. Server address, username, and password fields.", true);
    }

    /// <summary>Records a connect/login outcome on the still-open form and puts focus where the player
    /// can act on it. The message itself has already been spoken by whoever raised it, so the focus move
    /// is silenced — otherwise the widget's name would interrupt the reason.</summary>
    private static void OnLoginOutcome(string message, bool success) => OnUi(() =>
    {
        if (_loginDialog == null) return;
        _loginStatusText = message;
        _loginStatus?.SetText(message);
        if (success) { CloseLoginDialog(); return; }
        _suppressFocusSpeech = true;
        _loginUser?.GrabFocus();
    });

    private static void CloseLoginDialog()
    {
        var dialog = _loginDialog;
        _loginDialog = null; _loginUser = null; _loginStatus = null;
        dialog?.Close();
    }

    /// <summary>Runs an action on the GTK main thread. Login outcomes arrive on the game-loop thread.</summary>
    private static void OnUi(Action action)
    {
        var ui = _uiContext;
        if (ui != null) ui.Post(_ => action(), null);
        else action();
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
    private static void OnServerMessage(IMessage msg) => _session.HandleMessage(msg);

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

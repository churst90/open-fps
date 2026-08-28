using System;
using System.Threading;
using System.Windows.Forms;
using OpenFPS.Common;
using OpenFPS.Common.Networking;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.Core.Platform;
using OpenFPS.Client.Core.Session;
using OpenFPS.Client.Services;
using OpenFPS.Client.UI;

namespace OpenFPS.Client.Core;

/// <summary>
/// The Windows head: the application entry point, the WinForms bootstrap, and the game thread.
///
/// The head is now thin. It owns speech (NVDA / SAPI), the WinForms windows behind
/// <see cref="IClientShell"/>, microphone capture, and the virtual-key-to-<c>GameKey</c> map — and
/// nothing else. Netcode, prediction, acoustics, bindings and every spoken announcement live in
/// <see cref="ClientGameSession"/> in OpenFPS.Client.Core, shared verbatim with the GTK head.
/// </summary>
public class ClientRunner
{
    // Platform services (the four seams).
    private readonly ISpeechOutput _speech = new NvdaSpeechOutput();
    private readonly VoiceCapture _microphone = new();
    private readonly PersistenceService _persistence = new();
    private readonly ClientNetworkService _network = new();
    private readonly AudioEngineFacade _audio;
    private readonly GlobalKeyboardHook _keyboardHook = new();

    private ClientGameSession _session = null!;
    private ClientNavigationService _navigation = null!;
    private WinFormsClientShell _shell = null!;

    private bool _isRunning = true;
    private Thread? _gameThread;

    private string _pendingUser = "";
    private string _pendingPass = "";
    private bool _isRegistering;

    public ClientRunner() : this(new AudioEngineFacade()) { }

    public ClientRunner(AudioEngineFacade audio)
    {
        _audio = audio;
    }

    /// <summary>
    /// Starts the application, initializes dependencies, and opens the main UI.
    /// </summary>
    public void Run()
    {
        _speech.Initialize();

        // Native audio libraries: FMOD (sound at all) AND phonon (HRTF binaural). phonon is required, not
        // optional — without it the game still makes noise, which is precisely the dangerous case: it looks
        // like it works while the spatial information the whole game is played on has quietly vanished.
        var missingDlls = NativeAudioLibraries.FindMissing();
        if (missingDlls.Count > 0)
        {
            string message = "OpenFPS cannot start. " + NativeAudioLibraries.DescribeMissing(missingDlls) +
                             " Please reinstall the application, or copy the FMOD and Steam Audio libraries " +
                             "next to the executable.";

            Serilog.Log.Error("Startup aborted. {Report}", NativeAudioLibraries.DescribeMissing(missingDlls));

            // Announce via screen reader / SAPI before showing any UI — accessibility-first.
            _speech.Speak(message, interrupt: true);
            MessageBox.Show(message, "Missing Dependencies", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        _persistence.Load();

        // Subscribe BEFORE Start(): a failure to open the socket is reported from inside Start(), and a
        // handler attached afterwards would never hear the one message that explains why nothing works.
        // A connection or protocol failure must be heard, not inferred from the game going quiet.
        _network.OnConnectionFailed += reason => _speech.Speak(reason, true);
        _network.OnProtocolError += reason => _speech.Speak(reason, true);
        _network.Start();

        _navigation = new ClientNavigationService(_speech, () =>
        {
            var menu = new MenuWindow(_speech, _persistence);
            menu.OnLoginRequested += (server, user, pass) =>
            {
                _isRegistering = false;
                _pendingUser = user;
                _pendingPass = pass;
                _network.Connect(server.Address, server.Port);
            };
            menu.OnRegisterRequested += (server, user, pass) =>
            {
                _isRegistering = true;
                _pendingUser = user;
                _pendingPass = pass;
                _network.Connect(server.Address, server.Port);
            };
            return menu;
        });

        _shell = new WinFormsClientShell(_navigation, () =>
        {
            _network.Send(new LogoutRequest());
            Application.Exit();
        });

        _session = new ClientGameSession(_network, _speech, _shell, _audio, _microphone);

        // Preloading the sound library is the long pole at startup, so it runs on the session's audio
        // thread and reports progress through the shell — the same path the GTK head uses.
        _navigation.ShowLoading("Initializing Sound Library...");
        _session.GameJoined += () => Serilog.Log.Information("Entered the world as entity {Id}.", _session.OwnEntityId);
        _session.BeginAudioInit(onReady: () => _navigation.ShowMenu());

        // Input: the low-level hook reports Windows virtual keys; they are mapped to the neutral GameKey
        // at this boundary and never seen by the game logic. (The cross-platform plan calls for replacing
        // the hook with focused-window key events; that is a separate change with its own risk, and the
        // seam here is what makes it a one-file swap when it happens.)
        _keyboardHook.OnKeyStateChanged += (key, isDown) =>
            _session.Input.SetKey(WinFormsKeyMap.Map(key), isDown);

        _network.OnConnected += HandleConnectedToServer;
        _network.OnMessageReceived += msg => _session.HandleMessage(msg);

        // Start the dedicated game logic thread
        _gameThread = new Thread(GameLoop) { IsBackground = true };
        _gameThread.Start();

        // Start WinForms UI message pump
        Application.Run(_navigation);

        // Cleanup on exit
        _isRunning = false;
        _keyboardHook.Dispose();
        _session.Dispose();
        _speech.Dispose();
    }

    private void HandleConnectedToServer()
    {
        if (_isRegistering)
            _network.Send(new RegisterRequest { Username = _pendingUser, Password = _pendingPass });
        else
            _network.Send(new LoginRequest { Username = _pendingUser, Password = _pendingPass });
    }

    /// <summary>
    /// The high-frequency game thread: network polling, simulation, physics, and audio updates.
    /// </summary>
    private void GameLoop()
    {
        var lastTime = DateTime.Now;
        double accumulator = 0.0;
        const double targetDt = PhysicsConstants.FixedDeltaTime;

        while (_isRunning)
        {
            try
            {
                var currentTime = DateTime.Now;
                double elapsed = (currentTime - lastTime).TotalSeconds;
                lastTime = currentTime;

                // Cap elapsed time to prevent a "spiral of death" after long pauses.
                if (elapsed > PhysicsConstants.MaxCatchUpSeconds) elapsed = PhysicsConstants.MaxCatchUpSeconds;
                accumulator += elapsed;

                _network.Poll();

                if (_session.IsInGame)
                {
                    while (accumulator >= targetDt)
                    {
                        _session.SimStep((float)targetDt);
                        accumulator -= targetDt;
                    }

                    _session.ContinuousUpdate();
                }
                else
                {
                    accumulator = 0; // don't bank elapsed time while not simulating
                }

                // Costs nothing unless OPENFPS_PROFILE=1; see PerfProbe.
                PerfProbe.ReportIfDue(TimeSpan.FromSeconds(30), line => Serilog.Log.Information("{Perf}", line));
            }
            catch (Exception ex)
            {
                // A handler or simulation exception must never silently kill the loop that pumps the
                // network: that would freeze the world-load handshake with no diagnostic at all.
                Serilog.Log.Error(ex, "GameLoop iteration failed.");
            }

            // Throttle to save CPU, but allow enough headroom for high-frequency polling
            Thread.Sleep(5);
        }
    }
}

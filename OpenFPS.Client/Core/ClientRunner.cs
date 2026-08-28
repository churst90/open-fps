using System.Windows.Forms;
using OpenFPS.Common;
using OpenFPS.Client.Services;
using OpenFPS.Client.UI;
using OpenFPS.Common.Networking;
using System.Numerics;
using System;
using System.Threading;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Client.Core.Platform;

namespace OpenFPS.Client.Core;

/// <summary>
/// Responsibility: The application entry point and main bootstrap.
/// It initializes all services and maintains the high-level game thread.
/// </summary>
public class ClientRunner
{
    // High-level Services
    private readonly TolkService _tts = new();
    private readonly PersistenceService _persistence = new();
    private readonly ClientNetworkService _network = new();
    private readonly AudioEngineFacade _audio;
    private readonly SoundMappingService _sounds;

    // Engine State
    private readonly ClientWorldState _world = new();
    private readonly LocalPlayerState _state = new();
    private readonly InputStateBuffer _inputBuffer = new();
    private readonly GlobalKeyboardHook _keyboardHook = new();

    // Game Systems
    private ClientSimulationSystem _simulation = null!;
    private ClientAudioSystem _audioSystem = null!;
    private LocalPlayerController _playerController = null!;
    private InputHandler _inputHandler = null!;

    private ClientNavigationService? _appContext;
    private bool _isRunning = true;
    private Thread? _gameThread;

    public ClientRunner() : this(new AudioEngineFacade()) { }

    public ClientRunner(AudioEngineFacade audio)
    {
        _audio = audio;
        _sounds = new SoundMappingService(_audio, _tts, _state);
    }

    private string _pendingUser = "";
    private string _pendingPass = "";
    private bool _isRegistering = false;

    /// <summary>
    /// Starts the application, initializes dependencies, and opens the main UI.
    /// </summary>
    public void Run()
    {
        _tts.Initialize();

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
            _tts.Speak(message);
            MessageBox.Show(message, "Missing Dependencies", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        _persistence.Load();
        AcousticRegistry.Initialize();
        _sounds.Initialize();
        _audio.Initialize();

        // Subscribe BEFORE Start(): a failure to open the socket is reported from inside Start(), and a
        // handler attached afterwards would never hear the one message that explains why nothing works.
        // A connection or protocol failure must be heard, not inferred from the game going quiet.
        _network.OnConnectionFailed += reason => _tts.Speak(reason, true);
        _network.OnProtocolError += reason => _tts.Speak(reason, true);
        _network.Start();

        // System Wiring
        _simulation = new ClientSimulationSystem(_network, _world, _state, _tts);
        _audioSystem = new ClientAudioSystem(_audio, _sounds, _state);
        
        // Link simulation to audio for ID synchronization
        _simulation.SetAudioSystem(_audioSystem);
        
        _playerController = new LocalPlayerController(_state);
        _inputHandler = new InputHandler(_tts, _network, _state, _world);

        _appContext = new ClientNavigationService(_tts, () => {
            var menu = new MenuWindow(_tts, _persistence);
            menu.OnLoginRequested += (server, user, pass) => {
                _isRegistering = false;
                _pendingUser = user;
                _pendingPass = pass;
                _network.Connect(server.Address, server.Port);
            };
            menu.OnRegisterRequested += (server, user, pass) => {
                _isRegistering = true;
                _pendingUser = user;
                _pendingPass = pass;
                _network.Connect(server.Address, server.Port);
            };
            return menu;
        });

        _simulation.SetNavigation(_appContext);

        // --- GLOBAL PRELOAD SEQUENCE ---
        _appContext.ShowLoading("Initializing Sound Library...");
        Task.Run(() => {
            _audio.PreloadAll((msg, pct) => {
                _appContext.UpdateLoadingStatus(msg, pct);
            });
            _tts.Speak("Sound library ready.");
            _appContext.ShowMenu();
        });

        // Physical Event Wiring
        _playerController.OnStepTriggered += _audioSystem.OnPlayerFootstep;
        _playerController.OnLandTriggered += _audioSystem.OnPlayerLand;

        // Input Wiring
        _keyboardHook.OnKeyStateChanged += (key, isDown) => _inputBuffer.SetKeyState(key, isDown);
        _network.OnConnected += HandleConnectedToServer;
        _network.OnMessageReceived += (msg) => _simulation.HandleMessage(msg);
        
        // Transition from Menu to Game
        _simulation.OnGameJoined += () => {
            _appContext.EnterGame(win => {
                _simulation.GetInputHandler().SetActiveWindow(win);
                win.OnCommandEntered += t => _simulation.GetInputHandler().HandleCommandEntered(t);
            });
        };

        // Start the dedicated game logic thread
        _gameThread = new Thread(GameLoop) { IsBackground = true };
        _gameThread.Start();

        // Start WinForms UI message pump
        Application.Run(_appContext);
        
        // Cleanup on exit
        _isRunning = false;
        _audio.Dispose();
    }

    private void HandleConnectedToServer()
    {
        if (_isRegistering)
            _network.Send(new RegisterRequest { Username = _pendingUser, Password = _pendingPass });
        else
            _network.Send(new LoginRequest { Username = _pendingUser, Password = _pendingPass });
    }

    /// <summary>
    /// The high-frequency game thread. 
    /// Responsibility: Network polling, Simulation, Physics, and Audio updates.
    /// </summary>
    private void GameLoop()
    {
        var lastTime = DateTime.Now;
        double accumulator = 0.0;
        const double targetDt = PhysicsConstants.FixedDeltaTime;

        while (_isRunning)
        {
            var currentTime = DateTime.Now;
            double elapsed = (currentTime - lastTime).TotalSeconds;
            lastTime = currentTime;

            // Cap elapsed time to prevent "Spiral of Death" after long pauses
            if (elapsed > PhysicsConstants.MaxCatchUpSeconds) elapsed = PhysicsConstants.MaxCatchUpSeconds;
            accumulator += elapsed;

            _network.Poll();

            if (_simulation.IsInGame)
            {
                // 1. Logic & Simulation (Fixed Ticks)
                while (accumulator >= targetDt)
                {
                    var inputSnapshot = _inputBuffer.GetSnapshot();
                    _simulation.Update(inputSnapshot.held, inputSnapshot.justPressed, (float)targetDt);
                    accumulator -= targetDt;
                }
                
                // 2. Continuous Updates (Render-rate or high-frequency)
                _playerController.Update(_state.Position + _state.VisualOffset, _state.Velocity);
                // Internally capped to 60 Hz; the loop below spins far faster than that to keep the
                // socket serviced. See ClientAudioSystem.UpdateHz.
                _audioSystem.Update(_world.GetSnapshot());
            }

            // Costs nothing unless OPENFPS_PROFILE=1; see PerfProbe.
            PerfProbe.ReportIfDue(TimeSpan.FromSeconds(30), line => Serilog.Log.Information("{Perf}", line));

            // Throttle to save CPU, but allow enough headroom for high-frequency polling
            Thread.Sleep(5); 
        }
    }
}

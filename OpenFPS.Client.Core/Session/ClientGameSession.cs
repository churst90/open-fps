using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.Core.Input;
using OpenFPS.Client.Core.Platform;
using OpenFPS.Client.Services;

namespace OpenFPS.Client.Core.Session;

/// <summary>
/// The client's game logic, once for every head. What differs by platform is behind
/// <see cref="ISpeechOutput"/>, <see cref="IClientShell"/>, <see cref="IMicrophoneCapture"/> and each
/// head's key map feeding <see cref="Input"/>.
///
/// Threading: <see cref="HandleMessage"/>, <see cref="SimStep"/> and <see cref="ContinuousUpdate"/> run
/// on the one game-loop thread that also pumps the network, so player state needs no locking. A head's
/// UI thread touches only <see cref="Input"/> (thread-safe) and its own shell.
/// </summary>
public sealed partial class ClientGameSession : IDisposable
{
    private readonly ClientNetworkService _network;
    private readonly ISpeechOutput _speech;
    /// <summary>The same speech, remembering the last line: see <see cref="SpeechLog"/>.</summary>
    private readonly SpeechLog _speechLog;
    private readonly IClientShell _shell;
    private readonly IMicrophoneCapture _microphone;

    private readonly ClientWorldState _world;
    private readonly LocalPlayerState _state;
    private readonly ClientPhysicsSystem _physics;
    private readonly LocalPlayerController _controller;
    private readonly OtherBodies _others;
    /// <summary>"Stairs up, 10 steps, to floor 3", and whether you are on a flight. See StairCues.</summary>
    private readonly StairCues _stairs = new();
    private readonly AudioEngineFacade _audioEngine;
    private readonly SoundMappingService _sounds;
    private readonly ClientAudioSystem _audioSystem;
    internal ClientAudioSystem AudioSystemForTest => _audioSystem;
    private readonly PredictionReconciler _reconciler;
    private readonly ChatManager _chat;

    /// <summary>The interface's sounds (menus, chat, arriving), shared with the head's menus so one
    /// switch covers them all.</summary>
    public UiSounds Ui { get; }

    /// <summary>The lists behind F5, F6 and F8: players, maps, friends, and what to do with each.</summary>
    private readonly MenuStack _menus;
    /// <summary>/listening: telling the game how loud your headphones are (docs/EAR_MODEL.md).</summary>
    private readonly ListeningCalibration _listening;

    /// <summary>The audio engine, for a head's settings (devices, interface sounds).</summary>
    public AudioEngineFacade Audio => _audioEngine;
    private readonly InputCommandMapper _bindings = new();

    private long _sequenceId;
    private int _ownEntityId = -1;

    /// <summary>The role the server gave this login. Only decides which menu items are offered; the
    /// server checks every command itself.</summary>
    private UserRole _role = UserRole.Player;
    private int _expectedEntityCount;
    private readonly bool _enableAudio;

    // From the manifest, for when the acoustics are generated.
    private float _voxelResolution = AcousticConstants.DefaultVoxelResolution;
    private float _occlusionFloor = 0.2f;
    private Vector3 _mapMin;
    /// <summary>The map's tiles are streamed (MapManifest.TileMetres), and its first load is complete: every
    /// definition batch from now on is a tile arriving.</summary>
    private bool _streamed, _mapLoaded;

    // A named object is announced once, on coming within its radius.
    private const float InteractionRadius = 3.0f;
    private readonly HashSet<int> _announcedNearby = new();
    private readonly HashSet<int> _currentNearby = new();
    private int _proximityCheckCounter;

    /// <summary>Keyboard state for the in-game window. Each head's key map writes into this.</summary>
    public InputStateBuffer Input { get; } = new();

    /// <summary>-1 before spawn.</summary>
    public int OwnEntityId => _ownEntityId;

    public bool IsInGame => _ownEntityId != -1;

    /// <summary>For a head's diagnostics.</summary>
    public ClientWorldState World => _world;

    /// <summary>For a head's diagnostics.</summary>
    public LocalPlayerState PlayerState => _state;

    /// <summary>Raised on the game-loop thread once the local player has spawned.</summary>
    public event Action? GameJoined;

    /// <summary>Raised on the game-loop thread with the username the server accepted; a head closes
    /// its connect form on it.</summary>
    public event Action<string>? LoginSucceeded;

    /// <summary>Raised on the game-loop thread with the server's reason, already spoken. The head keeps
    /// its connect form open and puts focus back to fix it: a form that closed on Connect moved focus to
    /// the menu, whose speech interrupted the rejection, and a wrong password read as silence.</summary>
    public event Action<string>? LoginFailed;

    public ClientGameSession(
        ClientNetworkService network,
        ISpeechOutput speech,
        IClientShell shell,
        AudioEngineFacade audioEngine,
        IMicrophoneCapture? microphone = null,
        bool enableAudio = true)
    {
        _network = network;
        _speechLog = new SpeechLog(speech);
        _speech = _speechLog;
        _shell = shell;
        _audioEngine = audioEngine;
        _microphone = microphone ?? new NullMicrophoneCapture();
        _enableAudio = enableAudio;

        // Before the audio system starts its acoustic worker, which reads the registry: Initialize is
        // thread-safe, but up front the order is fixed and nothing rebuilds it twice.
        AcousticRegistry.Initialize();
        // The client assembles a car's engine from the name the server sends, so it needs the same names.
        MachineRegistry.EnsureLoaded();
        ModelLibrary.EnsureLoaded();

        _world = new ClientWorldState();
        _state = new LocalPlayerState();
        _physics = new ClientPhysicsSystem(_state, new SpatialService());
        _reconciler = new PredictionReconciler(_state, _physics);
        _controller = new LocalPlayerController(_state);
        _others = new OtherBodies();
        _sounds = new SoundMappingService(_state);
        // Without sound (the tests) no doors are rendered: each session's renders took minutes of every
        // core, and the next test's login waited behind them.
        _audioSystem = new ClientAudioSystem(_audioEngine, _sounds, _state, prewarm: enableAudio);
        _chat = new ChatManager(_speech);
        Ui = new UiSounds(audioEngine);
        _menus = new MenuStack(_speech, Ui);
        _listening = new ListeningCalibration(_speech, Ui,
            (id, gainDb) => _audioSystem.PlayReferenceVoice(id, gainDb), _audioSystem.StopReferenceVoice,
            () => ClientSettings.Load().Save());
        // Each kind of chat has its own sound, heard before the words: UiSounds.CueFor says which,
        // and that somebody coming or going has its own, which a setting can turn off.
        _chat.Incoming += Ui.PlayChat;

        _sounds.Initialize();
        // Your own feet ride with your head (see ClientAudioSystem.OnOwnFootstep); everybody
        // else's are sounds at places in the world.
        _controller.OnStepTriggered += _audioSystem.OnOwnFootstep;
        // Your own foot on the floor under it, off the latest snapshot: a tread on a flight.
        _controller.Footing = (foot, feet, way) =>
        {
            var at = PhysicsUtils.FootOnFloor(_world.GetSnapshot(), foot, feet, way, _ownEntityId, out var m);
            return (at, m);
        };
        _controller.OnLandTriggered += _audioSystem.OnOwnLand;
        // What the road says to a driver — its name, the junction ahead — spoken without cutting off
        // whatever was being said, because two of them can arrive together at a corner.
        _audioSystem.Driving.Announce += text => _speech.Speak(text, interrupt: false);

        // Everybody else's feet arrive through exactly the same two calls as your own. A footstep
        // does not care whose it was, and nothing downstream of here is told.
        _others.OnStepTriggered += _audioSystem.OnPlayerFootstep;
        _others.OnLandTriggered += _audioSystem.OnPlayerLand;

        // Breathing is not played: judged by ear and rejected by Cody ("I don't like the breathing,
        // remove it") after the model and the synthesis were repaired. The model still drives the
        // exertion readout on B. See docs/CLIENT_NOTES.md, "Breathing is not played".

        _shell.CommandEntered += HandleCommandEntered;
        _microphone.PacketReady += OnVoicePacketReady;
        // What the microphone hears goes straight to the player's own room, not round the server.
        _microphone.SamplesCaptured += OpenFPS.Client.AudioEngine.Fmod.OwnVoiceRing.Shared.Write;
        WireConnection();

        RegisterBindings();
    }

    /// <summary>
    /// Starts the audio engine and its preloading on a thread of its own. Never inline: the session
    /// lives on the game-loop thread, which must keep pumping the world-load handshake. The provider's
    /// calls do nothing until it is ready, which is before spawn.
    /// </summary>
    /// <param name="onReady">Invoked on the audio thread once initialization has finished (or failed,
    /// or been skipped). A head that gates its main menu on the sound library uses this.</param>
    public void BeginAudioInit(Action? onReady = null)
    {
        if (!_enableAudio)
        {
            onReady?.Invoke();
            return;
        }

        new Thread(() =>
        {
            try
            {
                _audioEngine.Initialize();
                Serilog.Log.Information("Audio engine initialized (background): {Init}.", _audioEngine.IsInitialized);
                _audioEngine.PreloadAll((msg, pct) => _shell.UpdateLoadingStatus(msg, pct));
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "Audio engine init failed.");
                _speech.Speak("The audio engine failed to start. The game will be silent.", interrupt: true);
            }
            finally
            {
                onReady?.Invoke();
            }
        }) { IsBackground = true, Name = "AudioInit" }.Start();
    }

    // ── Bindings ────────────────────────────────────────────────────────────────

    private void RegisterBindings()
    {
        // The readouts: the game's HUD, spoken.
        _bindings.Bind(InputContext.Gameplay, GameKey.C,
            () => Say(OpenFPS.Common.PlayerCoordinates.Format(_state.Position)));
        _bindings.Bind(InputContext.Gameplay, GameKey.F, () => Say(_state.GetCompassDirection()));
        // H is the horn in the driver's seat (held: GatherInput reads it), and your health everywhere
        // else. Shift+H is your health in the driver's seat too.
        _bindings.Bind(InputContext.Gameplay, GameKey.H, () => { if (!_state.RidingControls) Say($"{_state.Health} percent"); });
        _bindings.Bind(InputContext.Gameplay, GameKey.H, KeyModifiers.Shift, () => Say($"{_state.Health} percent"));
        // Driving, Z is the road: which one, which way, which lane, how fast. On foot it is the area.
        _bindings.Bind(InputContext.Gameplay, GameKey.Z, () =>
            Say(_state.RidingControls && _audioSystem.Driving.Readout is { } road ? road : _state.CurrentRegion));
        _bindings.Bind(InputContext.Gameplay, GameKey.B, () => Say(ExertionReadout()));
        // N: the narration as you turn, on or off, and saved.
        _bindings.Bind(InputContext.Gameplay, GameKey.N, ToggleTurnNarration);

        // Interaction.
        _bindings.Bind(InputContext.Gameplay, GameKey.E, Interact);
        // Shift+E: knock on the nearest door instead of opening it.
        _bindings.Bind(InputContext.Gameplay, GameKey.E, KeyModifiers.Shift,
            () => _network.Send(new TextCommand { Command = "knock" }));

        // P is "what am I looking at", answered here from the geometry the client has. Shift+P is the
        // server's scan, a different question: the five nearest things in any direction.
        _bindings.Bind(InputContext.Gameplay, GameKey.P, LookAhead);
        _bindings.Bind(InputContext.Gameplay, GameKey.P, KeyModifiers.Shift,
            () => _network.Send(new TextCommand { Command = "scan" }));
        // I opens what you carry as a list to choose from; Shift+I says it in one sentence, as before.
        _bindings.Bind(InputContext.Gameplay, GameKey.I, () => _network.Send(new InventoryRequest()));
        _bindings.Bind(InputContext.Gameplay, GameKey.I, KeyModifiers.Shift, () => _network.Send(new TextCommand { Command = "inv" }));

        // Carrying: G takes what is within reach, Q puts down what is in your hand, R (no gun) slings
        // it on your back, all reachable without letting go of the movement keys.
        _bindings.Bind(InputContext.Gameplay, GameKey.G, () => _network.Send(new TextCommand { Command = "take" }));
        // T starts the engine and Shift+T stops it; on foot T claps. Two keys, not a toggle: somebody
        // who cannot tell whether the engine runs switches it off half the time, as on the first drive.
        _bindings.Bind(InputContext.Gameplay, GameKey.T, () => _network.Send(_state.IsRiding
            ? new TextCommand { Command = "ignition", Args = new[] { "on" } }
            : new TextCommand { Command = "clap" }));
        // K: lane assist on or off, while driving. (On foot K looks down; that is read as a held key,
        // not through this binding, so the two never meet.)
        _bindings.Bind(InputContext.Gameplay, GameKey.K, () =>
        {
            if (!_state.RidingControls) return;
            _audioSystem.Driving.AssistEnabled = !_audioSystem.Driving.AssistEnabled;
            Say(_audioSystem.Driving.AssistEnabled ? "Lane assist on." : "Lane assist off.");
        });
        _bindings.Bind(InputContext.Gameplay, GameKey.T, KeyModifiers.Shift,
            () => _network.Send(new TextCommand { Command = "ignition", Args = new[] { "off" } }));
        // Shift+K: every driving sound on or off at once (the spoken road stays). Saved.
        _bindings.Bind(InputContext.Gameplay, GameKey.K, KeyModifiers.Shift, () =>
        {
            if (!_state.RidingControls) return;
            DrivingCues.Enabled = !DrivingCues.Enabled;
            SaveSettings();
            Say(DrivingCues.Enabled ? "Driving sounds on." : "Driving sounds off.");
        });
        // J and L: the indicators in the driver's seat (on foot they turn you, read as held keys). The
        // guide and the brake cue plan the junction ahead by them.
        _bindings.Bind(InputContext.Gameplay, GameKey.J, () => { if (_state.RidingControls) Say(_audioSystem.Driving.ToggleIndicator(-1)); });
        _bindings.Bind(InputContext.Gameplay, GameKey.L, () => { if (_state.RidingControls) Say(_audioSystem.Driving.ToggleIndicator(+1)); });
        // U: the siren on or off, on a vehicle that has one; Shift+U its next tone (wail, yelp,
        // phaser). The server holds the switch and says what it did.
        _bindings.Bind(InputContext.Gameplay, GameKey.U, () =>
        {
            if (_state.RidingControls) _network.Send(new TextCommand { Command = "siren" });
        });
        _bindings.Bind(InputContext.Gameplay, GameKey.U, KeyModifiers.Shift, () =>
        {
            if (_state.RidingControls) _network.Send(new TextCommand { Command = "siren", Args = new[] { "next" } });
        });
        _bindings.Bind(InputContext.Gameplay, GameKey.Q, () => _network.Send(new TextCommand { Command = "drop" }));
        // R: the window in a seat, reload with a gun, otherwise sling what you hold. One key, since the
        // three never apply at once.
        _bindings.Bind(InputContext.Gameplay, GameKey.R, () => _network.Send(new TextCommand { Command = RKeyCommand(_state) }));
        // Shift+R: the reverse, the first thing on your back into your hand. Without a name the server
        // takes whatever was slung first.
        _bindings.Bind(InputContext.Gameplay, GameKey.R, KeyModifiers.Shift, () => _network.Send(new TextCommand { Command = "draw" }));
        _bindings.Bind(InputContext.Gameplay, GameKey.V, ToggleVoiceTransmission);

        // Firing is on Enter, never on Control: every screen reader silences speech on Control, so a
        // blind player presses it constantly, and bound to firing it shot a rifle each time (heard as
        // random banging). Enter is found by touch, sits by J K L O, and no
        // reader claims it in a game window. See ScreenReaderKeys. With empty hands Enter interacts.
        _bindings.Bind(InputContext.Gameplay, GameKey.Enter, () => { if (EnterFires(_state)) Fire(); else Interact(); });

        // X: the fire selector, a detent on; Shift+X back. The server holds where it sits and says it.
        // On the admin gun the selector is its mode. Y and Shift+Y: the admin gun's calibre.
        _bindings.Bind(InputContext.Gameplay, GameKey.X, () => SelectorKey(1));
        _bindings.Bind(InputContext.Gameplay, GameKey.X, KeyModifiers.Shift, () => SelectorKey(-1));
        _bindings.Bind(InputContext.Gameplay, GameKey.Y, () => CalibreKey(1));
        _bindings.Bind(InputContext.Gameplay, GameKey.Y, KeyModifiers.Shift, () => CalibreKey(-1));

        // The plain key asks about the server, Shift narrows it to here (or to yours).
        _bindings.Bind(GameKey.F5, () => _network.Send(new PlayerListRequest { Scope = PlayerListScope.Server }));
        _bindings.Bind(GameKey.F5, KeyModifiers.Shift, () => _network.Send(new PlayerListRequest { Scope = PlayerListScope.Map }));
        _bindings.Bind(GameKey.F6, () => _network.Send(new MapListRequest { Scope = MapListScope.Server }));
        _bindings.Bind(GameKey.F6, KeyModifiers.Shift, () => _network.Send(new MapListRequest { Scope = MapListScope.Mine }));
        _bindings.Bind(GameKey.F8, () => _network.Send(new FriendListRequest()));
        // The world editor (docs/WORLD_EDITOR.md): the server says whether you may, and builds the menu.
        _bindings.Bind(GameKey.F12, OpenWorldEditor);

        // Chat scrollback: brackets step through messages, shift-brackets through buffers.
        _bindings.Bind(GameKey.BracketLeft, () => CycleChat(-1));
        _bindings.Bind(GameKey.BracketRight, () => CycleChat(1));

        // Shell.
        _bindings.Bind(GameKey.Slash, _shell.OpenCommandConsole);
        // Numpad slash is the console's second key, and the scope's trigger while the scope is up:
        // the one hand on the keypad fires without leaving it.
        _bindings.Bind(GameKey.NumpadDivide, () =>
        {
            if (_scope.Raised && _shell.IsGameInputActive && EnterFires(_state)) Fire();
            else _shell.OpenCommandConsole();
        });
        _bindings.Bind(GameKey.Escape, ShowGameMenu);

        RegisterScopeBindings();
        // Comma and period: the doors, entrances, stairs, items, people, vehicles or places near you.
        RegisterTrackerBindings();
    }

    /// <summary>
    /// Keys nothing in gameplay may be bound to. Control silences every screen reader and Alt is the
    /// window manager's; a blind player presses both dozens of times a minute, so an action on either
    /// fires by itself. A chord with a modifier (Shift+F5) is fine: the key itself may not be the action.
    /// </summary>
    public static readonly GameKey[] ScreenReaderKeys =
        { GameKey.ControlLeft, GameKey.ControlRight, GameKey.AltLeft, GameKey.AltRight };

    /// <summary>What R sends: wind the window in a seat, reload a gun in your hands, and otherwise
    /// put what you hold on your back.</summary>
    internal static string RKeyCommand(LocalPlayerState state)
        => state.IsRiding ? "window"
         : HoldsGun(state) ? "reload"
         : "stow";

    /// <summary>Whether Enter fires (a gun in your hands) or interacts (anything else).</summary>
    internal static bool EnterFires(LocalPlayerState state) => HoldsGun(state);

    private static bool HoldsGun(LocalPlayerState state)
        => !string.IsNullOrEmpty(state.HeldWeaponId)
           && (OpenFPS.Common.WeaponRegistry.TryGet(state.HeldWeaponId, out _) || HoldsAdminGun(state));

    private static bool HoldsAdminGun(LocalPlayerState state)
        => string.Equals(state.HeldWeaponId, OpenFPS.Common.AdminGun.WeaponId, StringComparison.OrdinalIgnoreCase);

    /// <summary>X and Shift+X: a gun in your hands has its selector moved; anything else is told why not.</summary>
    private void SelectorKey(int direction)
    {
        if (!HoldsGun(_state)) { Say("You are not holding a gun."); return; }
        _network.Send(new TextCommand { Command = "selector", Args = new[] { direction < 0 ? "back" : "next" } });
    }

    /// <summary>Y and Shift+Y: the admin gun's calibre, on and back.</summary>
    private void CalibreKey(int direction)
    {
        if (!HoldsAdminGun(_state)) { Say("Y changes the admin gun's calibre."); return; }
        _network.Send(new TextCommand { Command = "calibre", Args = new[] { direction < 0 ? "back" : "next" } });
    }

    /// <summary>Whether the trigger is held down: Enter fired a gun and has not come up yet. When it
    /// does, "cease" lets go of the trigger, which stops a gun on automatic (the server holds the
    /// selector and fires at the gun's rate meanwhile).</summary>
    private bool _triggerDown;

    private void ReleaseTrigger(HashSet<GameKey> held)
    {
        if (!_triggerDown || held.Contains(GameKey.Enter)) return;
        _triggerDown = false;
        _network.Send(new TextCommand { Command = "cease" });
    }

    /// <summary>Runs a gameplay key as if pressed, for tests.</summary>
    internal bool Press(GameKey key, KeyModifiers modifiers = KeyModifiers.None)
        => _bindings.Execute(InputContext.Gameplay, key, modifiers);

    /// <summary>The keys, as the in-game window shows them. One text for both heads.</summary>
    public static readonly string KeyHelp = string.Join(Environment.NewLine,
        "In game. W A S D to move, J / L turn, O / K look up and down, Space jump.",
        "Enter fires the gun in your hands; with no gun it interacts, like E. X moves the fire selector, Shift X back; held Enter on auto keeps firing.",
        "Admin gun: X changes its mode (kill, vaporize, freeze, inspect), Y and Shift Y its calibre.",
        "C coordinates, F facing, H health, Z area, P look ahead, Shift P scan, E interact or pick up, I inventory list, Shift I what you carry.",
        "Comma and period step through the nearest things of one kind, nearest first; Shift comma and Shift period change the kind:",
        "doors, entrances, stairs, items, people, vehicles, places.",
        "N turns the narration of what is ahead, as you turn and move and as things pass, on or off.",
        "G take, Q drop, Shift+R draw, T clap or ignition.",
        "R: with a gun, reload; in a vehicle, the window; otherwise put what you hold on your back.",
        "Driving: T engine on, Shift T off, H held the horn, U siren, Shift U its tone, J and L indicators, K lane assist,",
        "Shift K all driving sounds, Z the road, Shift H your health.",
        "Scope, on the keypad with Num Lock on: star raises it, 8 2 4 6 aim, 5 what is on the crosshair, 7 and 9 the targets in view,",
        "plus and minus zoom, 1 and 3 the turret, period the rangefinder, 0 held to hold your breath, slash or Enter to fire.",
        "V voice, F5 players, F6 maps, F8 friends, F12 the world editor, brackets to read chat, slash for the command console.",
        "Escape for the game menu: keep playing, main menu, or quit.");

    /// <summary>Whether anything is bound to a key in a context. For a settings screen, and for the
    /// test that keeps a screen reader's keys free of game actions (see <see cref="ScreenReaderKeys"/>).</summary>
    public bool IsBound(InputContext context, GameKey key) => _bindings.IsBound(context, key);

    private void Say(string text) => _speech.Speak(text, interrupt: true);

    /// <summary>
    /// /reverb: how the trace is doing. The tail is the place's own impulse response traced through
    /// the map, everywhere; there is no other mode to switch to.
    /// </summary>
    internal static string ReverbCommand(string[] args)
    {
        if (args.Length > 0) return "Traced everywhere now; there is no room mode. /reflections sets the level.";
        return OpenFPS.Client.AudioEngine.Fmod.FmodAudioProvider.TracedReverbStatus();
    }

    /// <summary>
    /// /valveflow on | off: the broadband rush of gas through each exhaust valve as it opens
    /// (EngineSynth.BlowdownJet). It changes every engine, the loud V8s most, so it can be switched
    /// live to hear what it does. Plain /valveflow says which.
    /// </summary>
    internal static string ValveFlowCommand(string[] args)
    {
        if (args.Length > 0)
        {
            string a = args[0].ToLowerInvariant();
            if (a is "on") OpenFPS.Client.AudioEngine.Core.Engine.EngineSynth.ValveJetNoise = true;
            else if (a is "off") OpenFPS.Client.AudioEngine.Core.Engine.EngineSynth.ValveJetNoise = false;
            else return "Say /valveflow on or /valveflow off.";
        }
        return OpenFPS.Client.AudioEngine.Core.Engine.EngineSynth.ValveJetNoise
            ? "Valve flow on: the exhaust valves rush."
            : "Valve flow off: the exhaust is pulses only, as before.";
    }

    /// <summary>
    /// /levels, /levels default, /levels 0.7 (or 70): how much of the real loudness differences reach
    /// the mix. No "real" setting (Cody, 2026-10-05): 100 percent is literal source levels, which on
    /// headphones made a parked car's idle inaudible and footsteps vanish, and the word invited it.
    /// </summary>
    internal static string LevelsCommand(string[] args, Action? save = null)
    {
        string Now()
        {
            float c = OpenFPS.Common.Loudness.DynamicRangeCompression;
            string tag = MathF.Abs(c - OpenFPS.Common.Loudness.DefaultCompression) < 0.005f ? ", the default" : "";
            return $"{MathF.Round(c * 100f)} percent{tag}";
        }
        if (args.Length == 0)
            return $"Levels {Now()}: how much of the real loudness differences you hear. Say slash levels and a number from "
                 + $"{OpenFPS.Common.Loudness.MinCompression * 100f:F0} to 100, or default.";
        float value;
        string a = args[0].Trim().TrimEnd('%');
        if (a.Equals("default", StringComparison.OrdinalIgnoreCase)) value = OpenFPS.Common.Loudness.DefaultCompression;
        else if (float.TryParse(a, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v))
            value = v > 1f ? v / 100f : v;
        else return $"{args[0]} is not a level. Say a number from {OpenFPS.Common.Loudness.MinCompression * 100f:F0} to 100, or default.";
        OpenFPS.Common.Loudness.DynamicRangeCompression = value;
        (save ?? (() => ClientSettings.Load().Save()))();
        string note = OpenFPS.Common.Loudness.CompressionFromEnvironment ? " For this run only: the environment sets it." : "";
        return $"Levels set to {Now()}.{note}";
    }

    private bool _shiftHeldThisStep;
    private void CycleChat(int direction)
    {
        if (_shiftHeldThisStep) _chat.CycleBuffer(direction);
        else _chat.CycleMessage(direction);
    }

    // ── Per-frame simulation (game-loop thread) ─────────────────────────────────

    /// <summary>One fixed-timestep step: gather input, run the bindings, predict, reconcile, send.</summary>
    public void SimStep(float dt)
    {
        if (!IsInGame) return;

        var (held, justPressed) = Input.GetSnapshot();
        _shiftHeldThisStep = InputStateBuffer.HasShift(held);
        var modifiers = InputStateBuffer.ModifiersIn(held);

        // With the console open gameplay bindings must not fire; the global ones (chat, quit) still do.
        bool gameplayActive = _shell.IsGameInputActive;
        var context = gameplayActive ? InputContext.Gameplay : InputContext.UI;
        // An open list has the keyboard, except the F keys, which swap one list for another.
        bool menuOpen = _menus.IsOpen && gameplayActive;
        // The listening-level calibration has the keyboard while it is open, as a list does.
        bool calibrating = _listening.IsOpen && gameplayActive;
        foreach (var key in justPressed)
        {
            if (calibrating) { _listening.HandleKey(key, _shiftHeldThisStep, _simTime); continue; }
            // The world editor's direct keys, if a player turned them on (off by default; section 11.8).
            if (menuOpen && EditorKeys.Enabled && _shiftHeldThisStep
                && _menus.Current?.Tag.StartsWith(EditorTagPrefix, StringComparison.Ordinal) == true
                && EditorKeys.CommandFor(key) is { } editorCommand) { SendTyped(editorCommand); continue; }
            if (menuOpen && key is not (GameKey.F5 or GameKey.F6 or GameKey.F8 or GameKey.F12)) { _menus.HandleKey(key); continue; }
            _bindings.Execute(context, key, modifiers);
        }
        _listening.Tick(_simTime);
        if (calibrating) menuOpen = true;   // stand still while listening
        ReleaseTrigger(held);
        if (menuOpen) gameplayActive = false;   // stand still while choosing

        _simTime += dt;
        UpdateScope(held, dt, gameplayActive);
        var input = GatherInput(held, justPressed, dt);
        if (!gameplayActive || _reconciler.Held)
        {
            input.MoveDirection = Vector3.Zero;
            input.Jump = false;
            input.LookDelta = Vector2.Zero;
        }

        // Interpolation first: it is the only step that changes the world (remote entities only), so
        // everything after it shares one snapshot build.
        _world.UpdateInterpolation(dt, _ownEntityId);
        var snapshot = _world.GetSnapshot();

        _reconciler.Step(input, snapshot, dt);
        UpdateWallBump(snapshot, input);
        UpdateTurnNarration(snapshot, input, gameplayActive);

        UpdateShelterFactor(dt, snapshot);

        // Localize atmospheric effects: a roof over your head is most of what stops the rain.
        _state.PrecipitationIntensity = _world.CurrentPrecipitation * (1.0f - _state.ShelterFactor);

        // The correction's visual offset bleeds away over about 0.2 s.
        if (_state.VisualOffset.LengthSquared() > 0.0001f)
            _state.VisualOffset = Vector3.Lerp(_state.VisualOffset, Vector3.Zero, 5.0f * dt);
        else
            _state.VisualOffset = Vector3.Zero;

        if (input.MoveDirection != Vector3.Zero || input.LookDelta != Vector2.Zero || input.Jump || _sequenceId % 10 == 0)
            _network.Send(input);

        CheckInteractableProximity(snapshot);
    }

    /// <summary>
    /// Getting in and out, as the server reports it. Either is a teleport to prediction: the input
    /// history and the stride are thrown away, as on a spawn.
    /// </summary>
    private void NoteRiding(int ridingEntityId)
    {
        if (ridingEntityId == _state.RidingEntityId) return;

        bool wasRiding = _state.IsRiding;
        _state.RidingEntityId = ridingEntityId;
        _reconciler.Riding = _state.IsRiding;
        _reconciler.Reset();
        _controller.Teleported();
        _bumps.Reset();
        _sight.Reset();
        _state.VisualOffset = Vector3.Zero;

        if (_state.IsRiding && !wasRiding) Serilog.Log.Information("Riding entity {Id}.", ridingEntityId);
        else if (!_state.IsRiding) Serilog.Log.Information("No longer riding.");
    }

    /// <summary>
    /// Riding, your heading is the vehicle's, set every frame and never corrected (PredictionReconciler
    /// skips the look while Riding): the server's copy is a network trip late, and correcting toward it
    /// swung the bus round your head on every corner. See docs/CLIENT_NOTES.md, "Facing while riding".
    /// </summary>
    private void FollowRide(WorldSnapshot snapshot)
    {
        if (!_state.IsRiding || !snapshot.Entities.TryGetValue(_state.RidingEntityId, out var ride)) return;
        MathHelper.ToYawPitch(ride.Transform.Rotation, out float yaw, out _);
        _state.Yaw = MathHelper.WrapAngle(yaw);
        _state.Rotation = Quaternion.CreateFromYawPitchRoll(_state.Yaw, _state.Pitch, 0f);
    }

    /// <summary>Render rate: footsteps, the listener and the emitters, then the announcements.</summary>
    public void ContinuousUpdate()
    {
        if (!IsInGame) return;
        // A passenger does not walk: the road would be a footstep every stride, a machine gun at speed.
        if (_state.IsRiding) _controller.Teleported();
        // Where the body is, never the smoothed position: the slide that hides a correction was heard
        // as footsteps after a /tp. See docs/CLIENT_NOTES.md, "Footsteps and the smoothed position".
        else _controller.Update(_state.Position, _state.Velocity);

        var snapshot = _world.GetSnapshot();
        FollowRide(snapshot);

        // Everybody else, off the same snapshot; a seated body makes no steps there either (OtherBodies).
        _others.Update(snapshot, _ownEntityId);

        // Capped to 60 Hz inside (ClientAudioSystem.UpdateHz); this loop spins faster for the socket.
        _audioSystem.Update(snapshot);
        UpdateGuidance();

        // After the audio update: the region it just worked out is the one to say.
        AnnounceStairs(snapshot);
        AnnounceZoneChanges(snapshot);
        AnnounceMapEdge();
    }

    /// <summary>
    /// The edge of the map, said: nothing there to hear or touch, so stopping at it silently felt like
    /// the keys had stopped. Again only after a metre back from it.
    /// </summary>
    private void AnnounceMapEdge()
    {
        if (_state.IsRiding) return;
        var p = _state.Position;
        Vector3 lo = _state.MapMin, hi = _state.MapMax;
        float margin = OpenFPS.Common.PhysicsConstants.PlayerRadius + 0.05f;
        float nearest = MathF.Min(MathF.Min(p.X - lo.X, hi.X - p.X), MathF.Min(p.Z - lo.Z, hi.Z - p.Z));
        if (!_atMapEdge && nearest <= margin)
        {
            _atMapEdge = true;
            _speech.Speak("Edge of the map.", interrupt: false);
        }
        else if (_atMapEdge && nearest > margin + 1f) _atMapEdge = false;
    }

    private bool _atMapEdge;

    // ── Turning ─────────────────────────────────────────────────────────────────────────────────
    //
    // The signs, from the physics: Yaw -= LookDelta.X (forward is (sin yaw, 0, cos yaw), so a positive
    // X turns left) and Pitch += LookDelta.Y (CreateFromYawPitchRoll's pitch takes +Z toward -Y, so
    // increasing pitch looks down). J/L and K/O were each the wrong way round once, every step of the
    // reasoning plausible; TurnKeyTests holds them. See docs/CLIENT_NOTES.md, "Turn key signs".
    internal static readonly (GameKey Key, float X, float Y)[] TurnKeys =
    {
        (GameKey.J, +1f,  0f),   // left
        (GameKey.L, -1f,  0f),   // right
        (GameKey.K,  0f, +1f),   // down  (increasing pitch tilts forward toward -Y)
        (GameKey.O,  0f, -1f),   // up
    };

    /// <summary>A tap turns this far: four face you the other way.</summary>
    private const float TurnStepDegrees = 45f;
    /// <summary>With Shift, for lining something up by ear.</summary>
    private const float TurnFineDegrees = 1f;
    /// <summary>How long a key must be down before a tap becomes a sweep.</summary>
    private const double TurnHoldBeforeSweep = 0.35;
    private const float SweepDegreesPerSecond = 180f;
    private const float FineSweepDegreesPerSecond = 20f;

    private readonly Dictionary<GameKey, double> _turnDownAt = new();
    private double _simTime;

    /// <summary>
    /// Degrees to the next forty-five degree mark in the direction a turn key was pressed, as a
    /// magnitude: the key's axis sign carries the direction (a positive x, J, decreases yaw; a positive
    /// y, K, increases pitch).
    /// </summary>
    private float SnapDegrees(float ax, float ay)
    {
        // Which way the angle itself moves, as against which way the key's axis points.
        float currentDeg, dir;
        if (ax != 0f) { currentDeg = _state.Yaw * (180f / MathF.PI); dir = -MathF.Sign(ax); }
        else { currentDeg = _state.Pitch * (180f / MathF.PI); dir = MathF.Sign(ay); }

        float step = TurnStepDegrees;
        // A heading already on the grid gets a whole step, or the key would do nothing.
        float grid = dir > 0f ? MathF.Ceiling(currentDeg / step) * step
                              : MathF.Floor(currentDeg / step) * step;
        float delta = MathF.Abs(grid - currentDeg);
        // A float yaw is never exactly on a mark after a few turns.
        const float OnGrid = 0.25f;
        return delta < OnGrid ? step : delta;
    }

    /// <summary>
    /// The look keys as discrete steps: a tap is one snap whatever the frame rate, and holding sweeps.
    /// A key that turned while down could not be aimed (a tenth of a second was twenty-six degrees;
    /// "sometimes I overshoot"). The step is in one tick's units, so prediction and the server agree.
    /// </summary>
    private Vector2 GatherLook(HashSet<GameKey> held, IReadOnlyCollection<GameKey> justPressed, bool fine, float dt)
    {
        // Riding, the head faces where the vehicle points: every driving cue is placed relative to it.
        if (_state.IsRiding) { _turnDownAt.Clear(); return Vector2.Zero; }
        // Through a scope every look key is a fine one, scaled by the power.
        if (_scope.Raised) return GatherScopeLook(held, justPressed, dt);
        Vector2 look = Vector2.Zero;
        float perTick = PhysicsConstants.RotationSpeed * MathF.Max(dt, 1e-4f);

        foreach (var (key, ax, ay) in TurnKeys)
        {
            if (!held.Contains(key)) { _turnDownAt.Remove(key); continue; }

            float degrees;
            if (justPressed.Contains(key))
            {
                _turnDownAt[key] = _simTime;
                // A coarse tap snaps to the next multiple of forty-five: added to an off-grid heading
                // it stayed off-grid, and walking "north" moved both coordinates. Shift stays one
                // degree, off the grid on purpose.
                degrees = fine ? TurnFineDegrees : SnapDegrees(ax, ay);
            }
            else if (_simTime - _turnDownAt.GetValueOrDefault(key, _simTime) >= TurnHoldBeforeSweep)
            {
                degrees = (fine ? FineSweepDegreesPerSecond : SweepDegreesPerSecond) * dt;
            }
            else continue;   // still inside the tap the press already paid for

            float mag = degrees * (MathF.PI / 180f) / perTick;
            look.X += ax * mag;
            look.Y += ay * mag;
        }
        return look;
    }

    private ClientInputUpdate GatherInput(HashSet<GameKey> held, IReadOnlyCollection<GameKey> justPressed, float dt)
    {
        var input = new ClientInputUpdate { SequenceId = ++_sequenceId, DeltaTime = dt };

        // Shift modifies the key it is pressed with: a one-degree turn, a run on a movement key.
        bool fine = InputStateBuffer.HasShift(held);
        // Alt (and the console keys) suppress everything: it is the window manager's and the screen
        // reader's. Control does not: readers silence speech on it while the player keeps moving.
        bool chord = InputStateBuffer.HasAlt(held)
                   || held.Contains(GameKey.Slash) || held.Contains(GameKey.NumpadDivide);
        if (chord) return input;

        // A key that went down and up between two ticks (33 ms) was never in `held` and did nothing:
        // such a press is worth one tick of movement, paid once since the drain consumes it. See
        // docs/CLIENT_NOTES.md, "Shift, Control and taps in GatherInput".
        bool Pressed(GameKey k) => held.Contains(k) || justPressed.Contains(k);

        // The arrows are the same four; held with WASD each direction still counts once.
        Vector3 move = Vector3.Zero;
        if (Pressed(GameKey.W) || Pressed(GameKey.Up)) move.Z += 1;
        if (Pressed(GameKey.S) || Pressed(GameKey.Down)) move.Z -= 1;
        if (Pressed(GameKey.A) || Pressed(GameKey.Left)) move.X -= 1;
        if (Pressed(GameKey.D) || Pressed(GameKey.Right)) move.X += 1;
        // Lane assist holds the middle of the lane until you steer: your A or D always wins.
        if (_state.RidingControls && move.X == 0f && _audioSystem.Driving.AssistSteer is { } assist)
            move.X = assist;
        // Normalised on foot so a diagonal is not faster. Not driving: the axes are throttle and
        // steering, and lane assist leaning on the wheel would halve the throttle.
        if (move != Vector3.Zero)
            input.MoveDirection = _state.RidingControls
                ? new Vector3(Math.Clamp(move.X, -1f, 1f), 0f, Math.Clamp(move.Z, -1f, 1f))
                : Vector3.Normalize(move);

        if (held.Contains(GameKey.Space)) input.Jump = true;
        // The horn while H is down in the driver's seat; the server lets go a few ticks after.
        input.Horn = _state.RidingControls && !fine && Pressed(GameKey.H);

        // A claim about a key, not a speed: the server applies it, and prediction reads the same flag.
        input.Sprint = fine && move != Vector3.Zero;

        input.LookDelta = GatherLook(held, justPressed, fine, dt);
        return input;
    }

    // ── Network message handling (game-loop thread) ─────────────────────────────

    public void HandleMessage(IMessage msg)
    {
        switch (msg)
        {
            case LoginResponse login:
                if (login.Success)
                {
                    _role = login.Role;
                    NoteLoggedIn();
                    _shell.ShowLoading("Logging in...", speak: false);
                    LoginSucceeded?.Invoke(login.Username);
                }
                else
                {
                    Serilog.Log.Warning("Login rejected by the server: {Reason}", login.Message);
                    _speech.Speak($"Login failed. {login.Message}", interrupt: true);
                    NoteLoginRejected();
                    LoginFailed?.Invoke(login.Message);
                }
                break;

            case RegisterResponse reg:
                HandleRegisterResponse(reg);
                break;

            case MapManifest manifest:
                Serilog.Log.Information("MapManifest: {Map}, expecting {Count} entities, spawn {Spawn}.",
                    manifest.MapName, manifest.ExpectedEntityCount, manifest.SpawnPoint.Position);
                _mapName = manifest.MapName;
                _arrived = false;                  // the next spawn is an arrival, said out loud
                _lastAnnouncedRegion = null;
                // A second manifest is travel to another map: the old map's sounds and the body go
                // before the new one arrives.
                if (IsInGame)
                {
                    _menus.Close();
                    foreach (int id in _world.GetSnapshot().Entities.Keys.ToList()) _audioSystem.ForgetEntity(id);
                    _ownEntityId = -1;
                    _physics.OwnEntityId = -1;
                    _physics.Spatial.OwnEntityId = -1;
                    _audioSystem.OwnEntityId = -1;
                    // The server got us out of any seat before we left; the old map's bus is gone.
                    _state.RidingEntityId = -1;
                    _state.RidingControls = false;
                    _shell.ShowLoading($"Travelling to {manifest.MapName}...");
                }
                _loadToneStep = -1;
                LoadProgress($"Loading {manifest.MapName}...", 10);
                _world.Clear(manifest.WorldSize);
                // A new map's regions are numbered from scratch, so the last id announced describes
                // nowhere. Arriving somewhere is not crossing into it.
                _lastAnnouncedRegionId = int.MinValue;
                _zoneSettle.Reset();
                _others.Clear();
                // At once: the world-state broadcast is once a second, and until then the acoustics
                // would use the previous map's air.
                _world.ApplyManifestAtmosphere(manifest);
                // Ducked by shelter, never switched off: a doorway is a change in the world, not its end.
                _audioSystem.SetMapAmbience(manifest.AmbienceId);
                _audioSystem.Beacons.SetMapPolicy(manifest.BeaconPolicy);
                // A new map's roads come with its data; until then there are none.
                _audioSystem.Driving.SetRoads(null);
                _audioSystem.SetCrossings(null);

                _expectedEntityCount = manifest.ExpectedEntityCount;
                _voxelResolution = manifest.VoxelResolution;
                _occlusionFloor = manifest.OcclusionFloor;
                _mapMin = manifest.MapMin;
                _streamed = manifest.TileMetres > 0f;
                _mapLoaded = false;
                _world.ConfigureAcoustics(manifest.MapMin, manifest.VoxelResolution, manifest.OcclusionFloor, manifest.TileMetres);

                _physics.MapMin = manifest.PlayMin;
                _physics.MapMax = manifest.PlayMax;
                _physics.Gravity = manifest.Gravity;

                _state.Position = manifest.SpawnPoint.Position;
                _state.Rotation = manifest.SpawnPoint.Rotation;
                _state.MapMin = manifest.PlayMin;
                _state.MapMax = manifest.PlayMax;

                // With how far round us a streamed map should be sent: the world detail setting.
                var radii = WorldDetail.Radii;
                _network.Send(new MapDataRequest { MapName = manifest.MapName, FullDetailMetres = radii.FullMetres, FarMetres = radii.FarMetres });
                break;

            case EntityDefinition def:
                _world.RegisterDefinition(def);
                ReportEntityProgress();
                break;

            case EntityDefinitionBatch batch:
                {
                    // After the first load of a streamed map, a batch is a tile arriving: its rooms and
                    // doorways wait for the tile's TileStreamUpdate and go on the acoustic map together.
                    bool tile = _streamed && _mapLoaded;
                    foreach (var d in batch.Definitions) _world.RegisterDefinition(d, deferAcoustics: tile);
                    if (!tile) ReportEntityProgress();
                }
                break;

            case EntityDefinitionPack pack:
                // A batch, compressed: unpacked here and filed as the batch it was.
                if (pack.Unpack() is { } unpacked) HandleMessage(unpacked);
                else Serilog.Log.Warning("A pack of {Count} definitions could not be unpacked; dropped.", pack.Count);
                break;

            case TileStreamUpdate tiles:
                _world.NoteTiles(tiles.Tiles);
                // The join's own update says how many definitions it sent: that is the count to wait for,
                // whatever detail the manifest guessed.
                if (!_mapLoaded) _expectedEntityCount = tiles.Definitions;
                else
                {
                    _world.RequestAcousticRefresh();
                    // The trees that came or went change the woods heard as one (WoodChorus).
                    foreach (int gone in _world.RefreshWoods()) _audioSystem.ForgetEntity(gone);
                }
                Serilog.Log.Information("Tiles: {Changed} changed ({Defs} definitions, {Removed} removed); holding {Held}, {Count} entities.",
                    tiles.Tiles.Count, tiles.Definitions, tiles.Removed, _world.Tiles.Count, _world.EntityCount);
                break;

            case EntityRemoved removed:
                foreach (int goneId in _world.RemoveEntities(removed.EntityIds))
                {
                    _audioSystem.ForgetEntity(goneId);
                    _announcedNearby.Remove(goneId);
                }
                break;

            case MapRoads roads:
                // The roads the driving cues plan from (docs/DRIVING_AIDS.md), and the rails the tyres
                // strike at a level crossing.
                {
                    var data = OpenFPS.Common.RoadMapData.FromJson(roads.Json);
                    _audioSystem.Driving.SetRoads(data);
                    _audioSystem.SetCrossings(data?.Crossings);
                    Serilog.Log.Information("MapRoads for {Map}: {Roads} roads, {Crossings} level crossings, {Tracks} tracks.",
                                            roads.MapName, data?.Roads.Count ?? 0, data?.Crossings.Count ?? 0, data?.Tracks.Count ?? 0);
                }
                break;

            case MapLoadComplete:
                _mapLoaded = true;
                // The woods the map's trees make, heard as one past the hand-over (WoodChorus).
                foreach (int gone in _world.RefreshWoods()) _audioSystem.ForgetEntity(gone);
                Serilog.Log.Information("MapLoadComplete: {Count} entity definitions received.", _world.EntityCount);
                LoadProgress("Geometry ready. Finalizing acoustics...", 80);
                // Niced and off the shared pool: the bake is seconds of CPU while thirty engine voices
                // are primed, and at equal priority it takes their cores (see BackgroundPriority).
                _audioSystem.NoteSceneLoading();
                BackgroundPriority.RunLowered("AcousticBake", GenerateAcoustics);
                break;

            case PlayerSpawned spawn:
                // The budget hold again: the cars start now, and their first seconds cost the most.
                _audioSystem.NoteSceneLoading();
                _ownEntityId = spawn.EntityId;
                _physics.OwnEntityId = spawn.EntityId;
                _physics.Spatial.OwnEntityId = spawn.EntityId;
                _audioSystem.OwnEntityId = spawn.EntityId;

                _state.Position = spawn.SpawnTransform.Position;
                _state.Rotation = spawn.SpawnTransform.Rotation;
                _state.Velocity = Vector3.Zero;
                // A spawn is a teleport: no replay, no stride (the spawn would walk for you), no stairs
                // you were at, no wall you were against.
                _reconciler.Reset();
                _controller.Teleported();
                _stairs.Reset();
                _bumps.Reset();
                _sight.Reset();

                Serilog.Log.Information("PlayerSpawned: entity {Id} at {Pos}.", spawn.EntityId, spawn.SpawnTransform.Position);
                // A spawn after arriving is a /tp, and its zone is announced as you land in it.
                if (_arrived) break;
                _arrived = true;
                LoadProgress("Entering World...", 100);
                _shell.EnterGame();
                GameJoined?.Invoke();
                Ui.Play(UiCue.EnterWorld);
                // The world fades up over a second; the chord is an interface sound and is not faded.
                FadeWorldIn();
                // Arriving, a player is told where: once the body is placed in a zone, so the map and the
                // zone are one sentence (AnnounceZoneChanges).
                _arrivalPendingSince = DateTime.UtcNow;
                break;

            case ServerStateUpdate update:
                _world.SyncState(update);
                NoteRiding(update.RidingEntityId);
                _state.RidingControls = update.RidingEntityId >= 0 && update.RidingControls;
                _reconciler.Held = update.Held;
                foreach (var s in update.States)
                    if (s.EntityId == _ownEntityId)
                        if (_reconciler.ApplyServerCorrection(s, update.LastProcessedSequenceId, _world.GetSnapshot()))
                        {
                            _controller.Teleported();   // moved, not walked: forget the stride
                            _bumps.Reset();             // ...and the wall you were against
                        }
                break;

            case WorldAudioEvent audioEvent:
                // Rendered on arrival, each part queued for its own moment: a latch precedes its impact,
                // and a pane's glass lands a second and a half after it broke.
                _audioSystem.WorldAudio.Receive(audioEvent, OpenFPS.Common.AudioClock.Now);
                break;

            case WorldStateUpdate wsu:
                _world.UpdateAtmosphere(wsu);
                // The one wind the trees, the fires and your ears read.
                OpenFPS.Common.WindField.Weather = _world.Wind;
                break;

            case HitConfirm confirm:
                // A chime and no words: a word on every hit would talk over the fight.
                Ui.Play(confirm.Killed ? UiCue.Kill : UiCue.Hit);
                break;

            case StatsUpdate stats:
                _state.Health = stats.Health;
                _state.HeldWeaponId = stats.HeldWeaponId ?? "";
                _state.HeldScopeId = stats.HeldScopeId ?? "";
                _state.SpeedLimit = stats.SpeedLimit;
                _state.CurrentMaterial = stats.CurrentMaterial;
                _state.CurrentVariant = stats.CurrentVariant;
                // The floor underfoot, kept current for the region's material.
                _audioSystem.NotifyMaterialChange(stats.CurrentMaterial);
                break;

            case InventoryList inventory:
                _menus.Show(InventoryMenu(inventory));
                break;

            case PlayerListResponse pList:
                _menus.Show(PlayersMenu(pList));
                break;

            case FriendListResponse fList:
                _menus.Show(FriendsMenu(fList));
                break;

            case MapListResponse mList:
                _menus.Show(MapsMenu(mList));
                break;

            case EditorMenu editorMenu:
                ShowEditorMenu(editorMenu);
                break;

            case ModelUpdate model:
                ApplyModelUpdate(model);
                break;

            case MapSettingsUpdate mapSettings:
                ApplyMapSettings(mapSettings);
                break;

            case TextEvent tEvent:
                _chat.AddServerMessage(tEvent.Text);
                break;

            case ChatMessage cMsg:
                _chat.AddMessage(cMsg);
                break;

            case VoiceData voice:
                // Never our own: the server does not send it back, and we already hear ourselves.
                if (voice.SenderId != _ownEntityId && voice.OpusData.Length > 0)
                    _audioSystem.ReceiveVoice(voice.SenderId, voice.Sequence, voice.OpusData);
                break;
        }
    }

    private void GenerateAcoustics()
    {
        int timeout = 0;
        while (_world.EntityCount < _expectedEntityCount && timeout < 40)
        {
            Thread.Sleep(100);
            timeout++;
        }
        var snapshot = _world.GetSnapshot();

        try
        {
            var map = ClientWorldState.BuildAcousticMap(snapshot.Entities.Values.Select(e => e.Definition),
                _world.CurrentMapSize, _mapMin, _voxelResolution, _occlusionFloor, streamed: _streamed, report: true);
            _world.SetAcousticMap(map);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Acoustic generation failed.");
            _speech.Speak("Acoustic generation failed.", interrupt: false);
        }

        // "ready" must be sent here: the spawn is the server's answer to it, so waiting for the spawn
        // to send it would deadlock the handshake.
        Serilog.Log.Information("Acoustics done; sending ready.");
        _network.Send(new TextCommand { Command = "ready" });
        // The saved aim assistance, which the server keeps per session and starts on.
        SendAimAssist();
    }

    /// <summary>Tells the server the aim assistance in force here, without an answer: on entering the
    /// world, and when a settings dialog changes it.</summary>
    public void SendAimAssist()
        => _network.Send(new TextCommand { Command = "aimassist", Args = new[] { NavigationAids.AimAssist ? "on" : "off", "quiet" } });

    // ── Actions ─────────────────────────────────────────────────────────────────

    private void Interact()
    {
        // Something on the ground within reach comes first, and the client chooses which (PickUp).
        // In a seat E is the door and getting out, as it always was.
        if (!_state.IsRiding && TryPickUp()) return;
        var targetId = _world.GetClosestEntityId(_state.Position);
        if (targetId.HasValue)
            _network.Send(new InteractRequest { Action = "interact", TargetEntityId = targetId.Value });
        else
            Say("Nothing within reach.");
    }

    /// <summary>
    /// P: what is ahead, what it is made of (what it will sound like) and how far. With nothing ahead,
    /// where you are and which way you face.
    /// </summary>
    private void LookAhead()
    {
        var snapshot = _world.GetSnapshot();
        Vector3 forward = Vector3.Transform(new Vector3(0, 0, 1), _state.Rotation);
        Vector3 feet = _state.Position;
        Vector3 eyePos = feet + new Vector3(0, _state.EyeHeight, 0);

        // The ground is never the answer: Z says where you stand. A floor you are looking down at
        // hides whatever is under it, so it is "nothing ahead" rather than the floor. See Sightline.
        ReadOnlySpan<Vector3> eye = stackalloc Vector3[] { eyePos };
        if (Sightline.Ahead(_physics.Spatial, snapshot, eye, forward, Sightline.NarrationRange,
                            feet.Y, eyePos.Y, _ownEntityId) is { } seen)
        {
            string material = seen.Entity.Definition.Material.Material;
            string made = string.IsNullOrEmpty(material) || material is "Generic" or "None" ? "" : $", {material.ToLowerInvariant()}";
            Say($"{seen.Name}{made}, {seen.Distance:F1} metres ahead.");
        }
        else
        {
            Say($"Nothing ahead. {_state.CurrentRegion}, facing {_state.GetCompassDirection()}.");
        }
    }

    // ── Bumping into things ─────────────────────────────────────────────────────────────────────

    private readonly WallBumps _bumps = new();
    private int _bumpSeed;

    /// <summary>
    /// After a fresh step: pressing into something not already met gives a knock where it touched and
    /// the thing's name (<see cref="WallBumps"/>). Local only: others would need the server to find the
    /// contact itself.
    /// </summary>
    private void UpdateWallBump(WorldSnapshot snapshot, ClientInputUpdate input)
    {
        if (_state.IsRiding) { _bumps.Reset(); return; }
        // Only faces that stand up, and never the ground: a floor's edge, a kerb, a step you could
        // take, a ceiling met on a jump.
        var contact = WallBumps.Resolve(_reconciler.LastContact, snapshot, _state.EyeHeight, out var struck);
        if (!_bumps.Update(contact, _state.Position) || contact is not { } hit) return;
        if (!NavigationAids.WallBumps) return;

        string name = Sightline.NameOf(struck);
        Vector3 where = WallBumps.TouchPoint(struck, hit);
        var sounds = WallBumps.Sound(struck, hit, where, input.Sprint);
        Serilog.Log.Information("[BUMP] '{Name}' e{Id} {Material} at {Pos}, intent {Intent:F2}, {Count} sounds {Db:F0} dB",
            name, hit.EntityId, struck.Definition.Material.Material, where, hit.Intent, sounds.Count,
            sounds.Count > 0 ? sounds[0].LevelDb : 0f);
        if (sounds.Count > 0)
            _audioSystem.WorldAudio.Receive(new WorldAudioEvent
            {
                // The thing struck, so the knock is not heard through it.
                SourceEntityId = hit.EntityId,
                Label = "bump",
                Seed = unchecked(++_bumpSeed),
                Sounds = sounds,
            }, OpenFPS.Common.AudioClock.Now);
        _speech.Speak(name, interrupt: true);
    }

    // ── Saying what is ahead: as you turn, look, walk, and as things pass ─────────────────────

    private readonly SightWatch _sight = new();
    private readonly System.Diagnostics.Stopwatch _sightClock = new();
    private double _sightCostMax, _sightCostSum;
    private int _sightCostCount;
    private double _sightCostLoggedAt;

    /// <summary>
    /// After each step, what your eyes would tell you: what is ahead once a turn settles, what comes in
    /// front as you move, and what crosses in front. See <see cref="SightWatch"/>.
    /// </summary>
    private void UpdateTurnNarration(WorldSnapshot snapshot, ClientInputUpdate input, bool gameplayActive)
    {
        // Not riding (the vehicle faces for you), not through a scope (it has its own readout), not
        // while a list or the console has the keyboard.
        if (_state.IsRiding || _scope.Raised || !gameplayActive || !NavigationAids.TurnNarration) { _sight.Reset(); return; }
        // A held look key counts as looking: between a tap and its sweep no look arrives for a third
        // of a second, longer than the settle.
        bool looking = input.LookDelta != Vector2.Zero
                    || _turnDownAt.ContainsKey(GameKey.J) || _turnDownAt.ContainsKey(GameKey.L)
                    || _turnDownAt.ContainsKey(GameKey.K) || _turnDownAt.ContainsKey(GameKey.O);
        bool current = _sight.LastLine != null && _speechLog.LastText == _sight.LastLine;

        _sightClock.Restart();
        var result = _sight.Update(snapshot,
            new SightWatch.Pose(_state.Position, _state.Yaw, _state.Pitch, _state.EyeHeight),
            _ownEntityId, _simTime, looking, _state.GetCardinal(), current);
        _sightClock.Stop();
        double ms = _sightClock.Elapsed.TotalMilliseconds;
        _sightCostSum += ms;
        _sightCostMax = Math.Max(_sightCostMax, ms);
        _sightCostCount++;
        // What it costs the game loop, in the log every five minutes: the budget is half a millisecond.
        if (_simTime - _sightCostLoggedAt >= 300.0)
        {
            Serilog.Log.Information("[SIGHT] cost per tick: mean {Mean:F3} ms, max {Max:F3} ms over {Count} ticks",
                _sightCostSum / Math.Max(1, _sightCostCount), _sightCostMax, _sightCostCount);
            _sightCostLoggedAt = _simTime;
            _sightCostSum = _sightCostMax = 0;
            _sightCostCount = 0;
        }

        switch (result.Act)
        {
            case SightWatch.Act.Interrupt:
                _speech.Interrupt();
                break;
            // Climbing a flight, the treads are ground and nothing else is ahead, so the walk up read out
            // "Open, South"; the stairs say themselves, and the landing is said when you step off.
            case SightWatch.Act.Say when result.Line != null && _stairs.OnFlight && result.Cause == "move"
                                         && result.Line.StartsWith("Open, ", StringComparison.Ordinal):
                break;
            case SightWatch.Act.Say when result.Line != null:
                Serilog.Log.Information("[NARRATE] '{Line}' ({Cause}) facing {Deg:F0} at {Pos}", result.Line, result.Cause,
                    MathHelper.WrapAngle(_state.Yaw) * 180f / MathF.PI, _state.Position);
                _speech.Speak(result.Line, interrupt: result.Interrupt);
                break;
        }
    }

    private void ToggleTurnNarration()
    {
        NavigationAids.TurnNarration = !NavigationAids.TurnNarration;
        _sight.Reset();
        SaveSettings();
        Say(NavigationAids.TurnNarration ? "Turn narration on." : "Turn narration off.");
    }

    /// <summary>Writes the settings file with what is live now (see ClientSettings.Save).</summary>
    private static void SaveSettings()
    {
        try { ClientSettings.Load().Save(); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            Serilog.Log.Warning("Settings not saved: {Error}", ex.Message);
        }
    }

    /// <summary>
    /// /aimassist [on|off]: switched and saved here, and the command for the server, which does the
    /// assisting and says what it now does. A word that is neither is refused here.
    /// </summary>
    internal static string? AimAssistCommand(string[] args, out TextCommand? send, Action? save = null)
    {
        send = null;
        if (args.Length > 0)
        {
            string a = args[0].ToLowerInvariant();
            if (a is not ("on" or "off")) return "Say /aimassist on or /aimassist off.";
            NavigationAids.AimAssist = a == "on";
            (save ?? SaveSettings)();
        }
        send = new TextCommand { Command = "aimassist", Args = new[] { NavigationAids.AimAssist ? "on" : "off" } };
        return null;
    }

    /// <summary>
    /// /detail [low|medium|high]: how far round you a streamed map is loaded, switched and saved here, and
    /// the command for the server, which sends or takes away tiles to match and says what it now does.
    /// </summary>
    internal static string? DetailCommand(string[] args, out TextCommand? send, Action? save = null)
    {
        send = null;
        if (args.Length > 0)
        {
            if (StreamRadii.Named(args[0]) == null) return "Say /detail low, /detail medium or /detail high.";
            WorldDetail.Level = args[0].Trim().ToLowerInvariant() == "med" ? "medium" : args[0].Trim().ToLowerInvariant();
            (save ?? SaveSettings)();
        }
        send = new TextCommand { Command = "detail", Args = new[] { WorldDetail.Level } };
        return null;
    }

    /// <summary>
    /// /drivecues [NAME on|off | on|off]: the driving sounds. On its own, which are on; with a name
    /// (guide, lines, clicks, brake, speed) that one; with only on or off, all of them (Shift+K). Saved.
    /// </summary>
    internal static string DriveCuesCommand(string[] args, Action? save = null)
    {
        string Each() => string.Join(", ", DrivingCues.Names.Select(n => $"{n.What} {(DrivingCues.Get(n.Name) ? "on" : "off")}"));
        if (args.Length == 0)
            return $"Driving sounds {(DrivingCues.Enabled ? "on" : "off")}: {Each()}.";
        string first = args[0].ToLowerInvariant();
        if (first is "on" or "off")
        {
            DrivingCues.Enabled = first == "on";
            (save ?? SaveSettings)();
            return DrivingCues.Enabled ? "Driving sounds on." : "Driving sounds off.";
        }
        var named = DrivingCues.Names.FirstOrDefault(n => n.Name == first);
        if (named.Name == null)
            return "Say /drivecues, /drivecues on or off, or /drivecues guide, lines, clicks, brake or speed, then on or off.";
        bool now = args.Length > 1 ? args[1].ToLowerInvariant() == "on" : !DrivingCues.Get(first);
        DrivingCues.Set(first, now);
        (save ?? SaveSettings)();
        return $"{char.ToUpperInvariant(named.What[0])}{named.What[1..]} {(now ? "on" : "off")}.";
    }

    /// <summary>/narrate on|off and /bumps on|off: the two navigation aids, switched and saved.</summary>
    internal static string NavigationAidCommand(string which, string[] args, Action? save = null)
    {
        bool narrate = which == "narrate";
        bool now = narrate ? NavigationAids.TurnNarration : NavigationAids.WallBumps;
        if (args.Length > 0)
        {
            string a = args[0].ToLowerInvariant();
            if (a is "on") now = true;
            else if (a is "off") now = false;
            else return $"Say /{which} on or /{which} off.";
            if (narrate) NavigationAids.TurnNarration = now; else NavigationAids.WallBumps = now;
            (save ?? SaveSettings)();
        }
        return narrate
            ? (now ? "Turn narration on: what is ahead is said as you turn, look and move, and what passes in front." : "Turn narration off.")
            : (now ? "Bumps on: walking into something knocks and names it." : "Bumps off.");
    }

    /// <summary>A text command on the wire: the server spends the round and answers a hit with HitConfirm.</summary>
    private void Fire()
    {
        // Through a scope the shot carries its own aim and is flown; see FireScoped.
        if (_scope.Raised) FireScoped();
        else
        {
            _network.Send(new TextCommand { Command = "fire" });
            // Only a gun that can fire on automatic needs to hear the trigger let go.
            _triggerDown = OpenFPS.Common.WeaponRegistry.TryGet(_state.HeldWeaponId, out var w) && OpenFPS.Common.FireSelector.HasAuto(w);
        }
    }

    /// <summary>How hard you have been working, in words rather than a number.</summary>
    private string ExertionReadout()
    {
        float e = _controller.Exertion;
        string effort = e switch
        {
            < 0.15f => "Breathing easily",
            < 0.35f => "Breathing a little hard",
            < 0.6f  => "Breathing hard",
            < 0.85f => "Winded",
            _       => "Badly winded",
        };
        return $"{effort}.";
    }

    /// <summary>
    /// Says the zone as you cross into it. Keyed on the region id, not the name (two rooms may share
    /// one, and the outdoor name flips with shelter), and spoken without interrupting: a doorway must
    /// not cut off what you were being told.
    /// </summary>
    private void AnnounceZoneChanges(WorldSnapshot snapshot)
    {
        // Held long enough, the body moving as a body moves, to be walked into (ZoneSettle).
        double now = (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds;
        bool settled = _zoneSettle.Update(_state.CurrentRegionId, _state.Position, now);

        if (_arrivalPendingSince is { } since)
        {
            string here = _state.CurrentRegion;
            var waited = DateTime.UtcNow - since;
            // The audio update places the zone a few frames after the spawn; past 1.5 s it is not coming.
            bool placed = waited > TimeSpan.FromSeconds(0.25) && here != LocalPlayerState.UnknownArea;
            if (!placed && waited < TimeSpan.FromSeconds(1.5)) return;
            // "at outside" and "at under shelter" name no place: the map alone is said.
            if (here is LocalPlayerState.UnknownArea or ClientAudioSystem.UnderShelter or ClientAudioSystem.Outside
                || here.StartsWith(ClientAudioSystem.DoorwayPrefix)) here = "";
            _arrivalPendingSince = null;
            _lastAnnouncedRegionId = _state.CurrentRegionId;
            _lastAnnouncedRegion = here;
            _speech.Speak(ArrivalLine(_mapName, here), interrupt: true);
            return;
        }

        // A flight or a landing the map has named: a part of a room, not a room (see NamedPlaces).
        int region = _state.CurrentRegionId;
        bool part = NamedPlaces.NameOf(snapshot, region) != null;

        // On an unnamed flight a room's name waits for the landing: at eye height the next storey's
        // zone begins halfway up ("floor 3" with five steps to climb).
        if (_stairs.OnFlight && !part) return;

        if (region == _lastAnnouncedRegionId) return;
        // ...and not until it has held: a zone that changes back within a breath, or while the body is
        // thrown about faster than it walks, was never crossed into (Cody on Brandt Court's roof,
        // 2026-10-04: "Brandt Court roof", "sidewalk", eighteen times in three seconds).
        if (!settled) return;
        _lastAnnouncedRegionId = region;
        Serilog.Log.Information("[ZONE] {Id} '{Name}' at {Pos}", region, _state.CurrentRegion, _state.Position);

        // ...and the name must change too: a place is often several boxes ("Turn one and two" was
        // announced four times walking through it).
        string name = _state.CurrentRegion;
        if (string.IsNullOrWhiteSpace(name) || name == _lastAnnouncedRegion) return;
        // A doorway is roofed and in no zone: passing through it is not "Under Shelter".
        if (name == ClientAudioSystem.UnderShelter || name.StartsWith(ClientAudioSystem.DoorwayPrefix)) return;

        _lastAnnouncedRegion = name;
        // A stair cue just said ("Stairs up, 17 steps, to floor 3") speaks for the landing and flight
        // after it; their names are said when it did not speak. See StairCues.CoversZone.
        if (part && StairCues.CoversZone(_stairs.FlightAnnounced, _stairCueAt, _zoneSettle.HeldSince))
        {
            Serilog.Log.Information("[ZONE] '{Name}' not said: the stair cue spoke for it", name);
            return;
        }
        _speech.Speak(name, interrupt: false);
    }

    /// <summary>When a stair cue was last said, on the zone announcer's clock.</summary>
    private double _stairCueAt = double.NegativeInfinity;

    /// <summary>
    /// The foot or top of a flight, said once as you reach it facing along it, without interrupting.
    /// Not while riding.
    /// </summary>
    private void AnnounceStairs(WorldSnapshot snapshot)
    {
        if (_state.IsRiding) { _stairs.Reset(); return; }
        // The room, not a flight's or landing's name: stepping off a landing and back must not say the
        // flight again.
        string? line = _stairs.Update(snapshot, _state.Position, _state.Rotation, _state.CurrentRoomId);
        if (line == null) return;
        Serilog.Log.Information("[STAIRS] '{Line}' at {Pos}", line, _state.Position);
        _stairCueAt = (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds;
        _speech.Speak(line, interrupt: false);
    }

    /// <summary>"You're in city, at sidewalk." The zone is left out when there is none.</summary>
    internal static string ArrivalLine(string map, string? zone) =>
        string.IsNullOrWhiteSpace(zone) ? $"You're in {map}." : $"You're in {map}, at {zone}.";

    /// <summary>Set on arriving on a map, until the arrival line has been said.</summary>
    private DateTime? _arrivalPendingSince;
    private int _lastAnnouncedRegionId = int.MinValue;
    private readonly ZoneSettle _zoneSettle = new();
    private string _mapName = "";
    /// <summary>Spawned on this map already: a further spawn is a teleport, not an arrival.</summary>
    private bool _arrived;
    private string? _lastAnnouncedRegion;

    // ── The lists behind F5, F6 and F8 ──────────────────────────────────────────────────────────

    private ListMenu PlayersMenu(PlayerListResponse list)
    {
        var items = new List<MenuItem>();
        for (int i = 0; i < list.Players.Length; i++)
        {
            string name = i < list.Usernames.Length ? list.Usernames[i] : list.Players[i].Split(',')[0].Trim();
            items.Add(new MenuItem(list.Players[i], Opens: () => PersonMenu(name, isFriend: false)));
        }
        return new ListMenu("Players", items);
    }

    /// <summary>
    /// What you carry, one line each ("AKM, 30 rounds, on your back"), and for each what you can do with
    /// it. Each acts on that one thing by its own id, so ten rifles of the same name are ten choices.
    /// </summary>
    private ListMenu InventoryMenu(InventoryList list)
    {
        var items = new List<MenuItem>();
        for (int i = 0; i < list.Ids.Length; i++)
        {
            string id = "#" + list.Ids[i];
            string label = i < list.Labels.Length ? list.Labels[i] : "thing";
            string place = i < list.Places.Length ? list.Places[i] : "";
            bool onBack = place == "back";
            string where = onBack ? "on your back" : $"in your {place}";
            items.Add(new MenuItem($"{label}, {where}", Opens: () =>
            {
                var actions = new List<MenuItem>();
                if (onBack) actions.Add(new("Take in your hands", () => Command("draw", id)));
                else actions.Add(new("Sling on your back", () => Command("stow", id)));
                actions.Add(new("Drop", () => Command("drop", id)));
                return new ListMenu(label, actions);
            }));
        }
        return new ListMenu(items.Count == 0 ? "You are carrying nothing" : "Inventory", items);
    }

    private ListMenu FriendsMenu(FriendListResponse list)
    {
        var items = new List<MenuItem>();
        for (int i = 0; i < list.Friends.Length; i++)
        {
            string name = list.Friends[i];
            bool online = i < list.Online.Length && list.Online[i];
            items.Add(new MenuItem($"{name}, {(online ? "online" : "offline")}", Opens: () => PersonMenu(name, isFriend: true)));
        }
        return new ListMenu("Friends", items);
    }

    /// <summary>
    /// What you can do with a person, each a command the server answers. "Where is" is offered only to
    /// moderators and the administrator, whom the server allows /where (roles of 2026-10-05).
    /// </summary>
    private ListMenu PersonMenu(string name, bool isFriend)
    {
        var items = new List<MenuItem> { new("Private message", () => _shell.OpenCommandConsole($"/pm {name} ")) };
        if (_role is UserRole.Admin or UserRole.Moderator) items.Add(new("Where is", () => Command("where", name)));
        items.Add(new("View profile", () => Command("profile", name)));
        items.Add(isFriend ? new("Remove friend", () => Command("friend", "remove", name))
                           : new("Add friend", () => Command("friend", "add", name)));
        return new ListMenu(name, items);
    }

    private ListMenu MapsMenu(MapListResponse response)
    {
        var items = new List<MenuItem>();
        foreach (var map in response.Maps)
        {
            string people = map.PlayerCount switch { 0 => "empty", 1 => "1 player", _ => $"{map.PlayerCount} players" };
            // A map of a real place has a name to say ("magnolia tx"); the join still goes by its id.
            string spoken = string.IsNullOrWhiteSpace(map.Name) ? map.Id : map.Name;
            string label = $"{spoken}, {people}{(map.IsPublic ? "" : ", private")}{(map.IsCurrent ? ", you are here" : "")}";
            string id = map.Id;
            bool here = map.IsCurrent;
            items.Add(new MenuItem(label, () =>
            {
                if (here) { Say($"You are already on {spoken}."); return; }
                Say($"Going to {spoken}.");
                Command("join", id);
            }));
        }
        return new ListMenu(response.Scope == MapListScope.Mine ? "Your maps" : "Maps", items);
    }

    private void Command(string name, params string[] args)
        => _network.Send(new TextCommand { Command = name, Args = args });

    private void ToggleVoiceTransmission()
    {
        if (!_microphone.IsAvailable)
        {
            Say(_microphone.UnavailableReason);
            return;
        }

        if (!_microphone.IsCapturing)
        {
            _microphone.Start();
            Ui.Play(UiCue.VoiceOn);
            _audioSystem.OwnVoiceLive = _microphone.IsCapturing;
            Serilog.Log.Information("Voice chat: V pressed, transmitting {On}.", _microphone.IsCapturing ? "on" : "FAILED to start");
        }
        else
        {
            _microphone.Stop();
            Ui.Play(UiCue.VoiceOff);
            _audioSystem.OwnVoiceLive = false;
            Serilog.Log.Information("Voice chat: V pressed, transmitting off.");
        }
    }

    /// <summary>The frames sent so far, wrapping; never zero on the wire, which means "not numbered".</summary>
    private int _voiceSequence;

    /// <summary>Capture thread. Unreliable on purpose: a resent frame arrives after the listener has
    /// done without it, and waiting for it would hold up every frame behind.</summary>
    private void OnVoicePacketReady(byte[] opusData)
    {
        ushort sequence = (ushort)System.Threading.Interlocked.Increment(ref _voiceSequence);
        if (sequence == 0) sequence = (ushort)System.Threading.Interlocked.Increment(ref _voiceSequence);
        _network.Send(new VoiceData { SenderId = _ownEntityId, OpusData = opusData, Sequence = sequence },
            LiteNetLib.DeliveryMethod.Unreliable);
        _network.Flush();
    }

    /// <summary>A line from the command console: a slash command (some answered here) or map chat.</summary>
    public void HandleCommandEntered(string text)
    {
        string input = text.Trim();
        if (string.IsNullOrEmpty(input)) return;

        if (input.StartsWith('/'))
        {
            var parts = input[1..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return;
            // The settings this client answers itself, which the server's /help cannot know about.
            if (parts[0].Equals("help", StringComparison.OrdinalIgnoreCase) && parts.Length > 1
                && parts[1].Equals("settings", StringComparison.OrdinalIgnoreCase))
            {
                Say("Your own settings: /levels, how much of the real loudness differences you hear, or /levels default. "
                  + "/beacons, which beacons you hear. /reverb, how the traced reverberation is doing. /echoes on or off. "
                  + "/tail and /copies, the reflections' level in decibels, zero is physical. /cabin, the inside of a vehicle. "
                  + "/narrate on or off, saying what is ahead as you turn and move and what passes in front, also N. /bumps on or off, the knock and name when you walk into something. "
                  + "/aimassist on or off, a shot from the hip near somebody in plain view turned onto them. "
                  + "/track and a kind, what comma and period step through: doors, entrances, stairs, items, people, vehicles or places; also Shift comma and Shift period. "
                  + "Each on its own says where it is set now.");
                return;
            }
            // Answered here, not by the server: which beacons YOU hear is yours, and nobody else's.
            if (parts[0].Equals("beacons", StringComparison.OrdinalIgnoreCase)
                || parts[0].Equals("beacon", StringComparison.OrdinalIgnoreCase))
            {
                Say(_audioSystem.Beacons.Command(parts.Skip(1).ToArray()));
                return;
            }
            // How the traced tail is doing.
            if (parts[0].Equals("reverb", StringComparison.OrdinalIgnoreCase))
            {
                Say(ReverbCommand(parts.Skip(1).ToArray()));
                return;
            }
            if (parts[0].Equals("echoes", StringComparison.OrdinalIgnoreCase))
            {
                var a = parts.Skip(1).FirstOrDefault()?.ToLowerInvariant();
                if (a is "on") OpenFPS.Client.AudioEngine.Fmod.FmodAudioProvider.TracedEchoesOn = true;
                else if (a is "off") OpenFPS.Client.AudioEngine.Fmod.FmodAudioProvider.TracedEchoesOn = false;
                else if (a != null && float.TryParse(a, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float db))
                {
                    // /echoes -12: on, at that level. They are traced, so theirs is the tail level.
                    OpenFPS.Client.AudioEngine.Fmod.FmodAudioProvider.TailDb = Math.Clamp(db, -80f, 6f);
                    OpenFPS.Client.AudioEngine.Fmod.FmodAudioProvider.TracedEchoesOn = true;
                }
                else if (a != null) { Say("Say /echoes on or /echoes off; /tail sets the level."); return; }
                Say(OpenFPS.Client.AudioEngine.Fmod.FmodAudioProvider.TracedEchoesStatus());
                return;
            }
            // Reflections against the direct sound, in two kinds (FmodAudioProvider.TailDb): /tail for
            // everything traced, /copies for everything placed as a copy, /reflections for both.
            // (Not /room: that is the server's "do the walls round you make a room".)
            if (parts[0].Equals("reflections", StringComparison.OrdinalIgnoreCase)
                || parts[0].Equals("tail", StringComparison.OrdinalIgnoreCase)
                || parts[0].Equals("copies", StringComparison.OrdinalIgnoreCase))
            {
                string which = parts[0].ToLowerInvariant();
                var a = parts.Skip(1).FirstOrDefault();
                if (a != null && float.TryParse(a, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float db))
                {
                    db = Math.Clamp(db, -80f, 6f);
                    if (which != "copies") OpenFPS.Client.AudioEngine.Fmod.FmodAudioProvider.TailDb = db;
                    if (which != "tail") OpenFPS.Client.AudioEngine.Fmod.FmodAudioProvider.CopiesDb = db;
                }
                else if (a != null) { Say($"Say a level in decibels, such as /{which} -12. Zero is the physical level, -80 is off."); return; }
                Say($"Tail {OpenFPS.Client.AudioEngine.Fmod.FmodAudioProvider.TailDb:F0} dB, the traced rooms and echoes. "
                  + $"Copies {OpenFPS.Client.AudioEngine.Fmod.FmodAudioProvider.CopiesDb:F0} dB, the placed reflections. Both against the direct sound; zero is physical.");
                return;
            }
            if (parts[0].Equals("cabin", StringComparison.OrdinalIgnoreCase))
            {
                var a = parts.Skip(1).FirstOrDefault();
                if (a != null && float.TryParse(a, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float db))
                    OpenFPS.Client.AudioEngine.Fmod.FmodAudioProvider.CabinDb = Math.Clamp(db, -80f, 6f);
                else if (a != null) { Say("Say a level in decibels, such as /cabin -12. Zero is the traced level, -80 is off."); return; }
                Say($"{OpenFPS.Client.AudioEngine.Fmod.FmodAudioProvider.CabinDb:F0} dB against the traced level, on the response of the vehicle you are sitting in.");
                return;
            }
            if (parts[0].Equals("valveflow", StringComparison.OrdinalIgnoreCase))
            {
                Say(ValveFlowCommand(parts.Skip(1).ToArray()));
                return;
            }
            // The scope is yours: what it sees is worked out here, from the world this client holds.
            if (parts[0].ToLowerInvariant() is "scope" or "zoom" or "range" or "zero")
            {
                if (ScopeCommand(parts[0].ToLowerInvariant(), parts.Skip(1).ToArray()) is { } answer) Say(answer);
                return;
            }
            // So are the driving sounds.
            if (parts[0].Equals("drivecues", StringComparison.OrdinalIgnoreCase))
            {
                Say(DriveCuesCommand(parts.Skip(1).ToArray()));
                return;
            }
            // So are the world editor's direct keys (off unless turned on; docs/WORLD_EDITOR.md 11.8).
            if (parts[0].Equals("editorkeys", StringComparison.OrdinalIgnoreCase))
            {
                Say(EditorKeysCommand(parts.Skip(1).ToArray()));
                return;
            }
            // So are the navigation aids.
            if (parts[0].ToLowerInvariant() is "narrate" or "bumps")
            {
                Say(NavigationAidCommand(parts[0].ToLowerInvariant(), parts.Skip(1).ToArray()));
                return;
            }
            // Aim assistance is the server's to apply and the player's to keep: saved here, and the
            // server told (it answers with what it now does).
            if (parts[0].Equals("aimassist", StringComparison.OrdinalIgnoreCase))
            {
                if (AimAssistCommand(parts.Skip(1).ToArray(), out var send) is { } refusal) Say(refusal);
                else _network.Send(send!);
                return;
            }
            // So is how far round you a large map is loaded: saved here, and the server told.
            if (parts[0].Equals("detail", StringComparison.OrdinalIgnoreCase))
            {
                if (DetailCommand(parts.Skip(1).ToArray(), out var send) is { } refusal) Say(refusal);
                else _network.Send(send!);
                return;
            }
            // So is what comma and period step through.
            if (parts[0].Equals("track", StringComparison.OrdinalIgnoreCase))
            {
                Say(TrackCommand(parts.Skip(1).ToArray()));
                return;
            }
            // So is how loud the world is: yours, and saved.
            if (parts[0].Equals("levels", StringComparison.OrdinalIgnoreCase))
            {
                Say(LevelsCommand(parts.Skip(1).ToArray()));
                return;
            }
            // ...and how loud your headphones are: set by ear, or by a number. Saved.
            if (parts[0].Equals("listening", StringComparison.OrdinalIgnoreCase))
            {
                if (ListeningCalibration.Command(parts.Skip(1).ToArray(), () => ClientSettings.Load().Save()) is { } said) Say(said);
                else _listening.Open(_simTime);
                return;
            }
            // The ear model, on and off, to hear what it does. Not saved.
            if (parts[0].Equals("ear", StringComparison.OrdinalIgnoreCase))
            {
                Say(ListeningCalibration.EarCommand(parts.Skip(1).ToArray()));
                return;
            }
            _network.Send(new TextCommand { Command = parts[0].ToLowerInvariant(), Args = parts.Skip(1).ToArray() });
        }
        else
        {
            // Plain typing is heard by your map; /all is for everyone on the server.
            _network.Send(new ChatMessage { Text = input, Channel = ChatChannel.Map });
        }
    }

    /// <summary>
    /// Named things as the player comes within range, once a second so the screen reader is not
    /// flooded, off the tick's snapshot.
    /// </summary>
    private void CheckInteractableProximity(WorldSnapshot snap)
    {
        if (++_proximityCheckCounter % PhysicsConstants.TickRate != 0) return;

        _currentNearby.Clear();
        foreach (var entity in snap.Entities.Values)
        {
            if (entity.Id == _ownEntityId) continue;
            if (entity.Definition.Type == EntityType.Player) continue;

            // Only what the prefab says to announce: every wall, floor and portal has a name, and a
            // doorway once read the portal's authoring notes aloud.
            if (!entity.Definition.Identity.Announce) continue;

            string name = entity.Definition.Identity.Name;
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (Vector3.Distance(_state.Position, entity.Transform.Position) > InteractionRadius) continue;

            _currentNearby.Add(entity.Id);
            if (_announcedNearby.Contains(entity.Id)) continue;

            string desc = entity.Definition.Identity.Description;
            _speech.Speak(string.IsNullOrWhiteSpace(desc) ? name : $"{name}. {desc}", interrupt: false);
        }

        _announcedNearby.Clear();
        foreach (int id in _currentNearby) _announcedNearby.Add(id);
    }

    /// <summary>
    /// ShelterFactor, 0 to 1: an indoor region, else the share of 16 upward rays that hit something,
    /// slewed. Shelter keeps the rain off and ducks the map's ambience.
    /// </summary>
    private void UpdateShelterFactor(float dt, WorldSnapshot snap)
    {
        bool isSheltered = false;

        // Same eye position as the audio system uses for the listener, so the two agree.
        Vector3 visualEyePos = _state.VisualPosition + new Vector3(0, _state.EyeHeight, 0);

        if (snap.AcousticMap != null)
        {
            int regionId = _physics.Spatial.GetRegionAt(snap, visualEyePos);
            if (regionId != AcousticConstants.GlobalRegionId && snap.AcousticMap.Regions.TryGetValue(regionId, out var region))
                isSheltered = region.IsIndoor;
        }

        float targetShelter = isSheltered ? 1.0f : 0.0f;

        // A Fibonacci spread over the upper hemisphere.
        if (!isSheltered)
        {
            const int rays = 16;
            int hits = 0;
            float goldenRatio = (1 + MathF.Sqrt(5)) / 2;

            for (int i = 0; i < rays; i++)
            {
                float theta = 2 * MathF.PI * i / goldenRatio;
                float phi = MathF.Acos(1 - (float)i / rays);

                var dir = new Vector3(
                    MathF.Cos(theta) * MathF.Sin(phi),
                    MathF.Cos(phi),
                    MathF.Sin(theta) * MathF.Sin(phi));

                if (_physics.Spatial.RaycastSingle(snap, visualEyePos, dir, AcousticConstants.ShelterRayDistance, out _, out _))
                    hits++;
            }

            targetShelter = (float)hits / rays;
        }

        // Slewed, so the audio does not pop at a threshold.
        _state.ShelterFactor += (targetShelter - _state.ShelterFactor)
            * Math.Clamp(AcousticConstants.ShelterFadeSpeed * dt, 0f, 1f);
    }

    public void Dispose()
    {
        // The window was closed under us: tell the server now, not at its timeout.
        if (_network.IsConnected) { _network.Send(new LogoutRequest()); _network.Disconnect(); }
        _shell.CommandEntered -= HandleCommandEntered;
        _microphone.PacketReady -= OnVoicePacketReady;
        _microphone.SamplesCaptured -= OpenFPS.Client.AudioEngine.Fmod.OwnVoiceRing.Shared.Write;
        _microphone.Dispose();
        _audioEngine.Dispose();
    }
}

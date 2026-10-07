using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.Core.Input;
using OpenFPS.Client.Core.Platform;
using OpenFPS.Client.Services;

namespace OpenFPS.Client.Core.Session;

/// <summary>
/// The client's game logic — the whole of it, for every head.
///
/// This class is the answer to the question the two heads used to answer separately: the Windows
/// <c>ClientSimulationSystem</c> and the GTK <c>GameSession</c> were two implementations of one job,
/// and they had already drifted. The Linux client had no chat buffers, no interactable proximity
/// announcements, no voice key and no loading progress; the Windows client had no look-ahead binding
/// in the same place, spoke different sentences on spawn, and reached the world through a Win32
/// global keyboard hook. Every difference between them was an accident of which file was edited, not
/// a decision about the platform.
///
/// What is genuinely platform-specific is now behind four interfaces, and only those:
/// <see cref="ISpeechOutput"/> (what the game says), <see cref="IClientShell"/> (windows and the
/// command console), <see cref="IMicrophoneCapture"/> (voice), and each head's key map feeding
/// <see cref="Input"/>. Everything else — netcode, prediction, reconciliation, acoustics, shelter,
/// bindings, the announcements themselves — lives here once.
///
/// Threading: <see cref="HandleMessage"/>, <see cref="SimStep"/> and <see cref="ContinuousUpdate"/>
/// are all called from the single game-loop thread that also pumps the network, so player state needs
/// no extra locking. A head's UI thread only ever touches <see cref="Input"/> (thread-safe) and the
/// shell it implements itself.
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
    private readonly PredictionReconciler _reconciler;
    private readonly ChatManager _chat;

    /// <summary>The interface's sounds — menus, chat, arriving in the world. Shared with the head's
    /// menus so there is one switch for all of them.</summary>
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

    // Map metadata captured from the manifest, needed when acoustics are generated later.
    private float _voxelResolution = AcousticConstants.DefaultVoxelResolution;
    private float _occlusionFloor = 0.2f;
    private Vector3 _mapMin;
    /// <summary>The map's tiles are streamed (MapManifest.TileMetres), and its first load is complete: every
    /// definition batch from now on is a tile arriving.</summary>
    private bool _streamed, _mapLoaded;

    // Interactable proximity tracking — announce a named object once, on entering its radius.
    private const float InteractionRadius = 3.0f;
    private readonly HashSet<int> _announcedNearby = new();
    private readonly HashSet<int> _currentNearby = new();
    private int _proximityCheckCounter;

    /// <summary>Keyboard state for the in-game window. Each head's key map writes into this.</summary>
    public InputStateBuffer Input { get; } = new();

    /// <summary>The local player's entity id, or -1 before spawn.</summary>
    public int OwnEntityId => _ownEntityId;

    /// <summary>True once the local player has spawned into the world.</summary>
    public bool IsInGame => _ownEntityId != -1;

    /// <summary>True once the audio engine has finished initializing.</summary>
    public bool AudioReady => _audioEngine.IsInitialized;

    /// <summary>The shared world state, for heads that want to inspect it (diagnostics).</summary>
    public ClientWorldState World => _world;

    /// <summary>The local player's state, for heads that want to inspect it (diagnostics).</summary>
    public LocalPlayerState PlayerState => _state;

    /// <summary>Raised on the game-loop thread once the local player has spawned.</summary>
    public event Action? GameJoined;

    /// <summary>Raised on the game-loop thread when the server ACCEPTS a login; the argument is the
    /// username it accepted. A head uses it to dismiss its connect form.</summary>
    public event Action<string>? LoginSucceeded;

    /// <summary>Raised on the game-loop thread when the server REJECTS a login; the argument is the
    /// server's reason, already spoken by the session.
    ///
    /// The head needs to know because the outcome is a UI event as much as a spoken one: a connect form
    /// that closes the moment the button is pressed drops the player back on the menu, and the focus
    /// change there speaks over the rejection with interrupt — which is why a wrong password read as
    /// total silence. Keep the form open, and put focus back where the player can fix it.</summary>
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

        // Initialize the material registry FIRST — before the audio system starts its acoustic worker
        // thread (which reads the registry). Initialize() is thread-safe, but doing it up front keeps
        // ordering deterministic and avoids redundant concurrent rebuilds.
        AcousticRegistry.Initialize();
        // ...and the machines, for the same reason: the client assembles a car's engine itself from
        // the name the server sends, so it has to know the same names the server does.
        MachineRegistry.EnsureLoaded();
        ModelLibrary.EnsureLoaded();

        _world = new ClientWorldState();
        _state = new LocalPlayerState();
        _physics = new ClientPhysicsSystem(_state, new SpatialService());
        _reconciler = new PredictionReconciler(_state, _physics);
        _controller = new LocalPlayerController(_state);
        _others = new OtherBodies();
        _sounds = new SoundMappingService(_state);
        _audioSystem = new ClientAudioSystem(_audioEngine, _sounds, _state);
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

        // Breathing, which is the only sound a body still makes once it has stopped moving — and
        // therefore the only way to find somebody who has stopped to listen for you.
        // ── BREATHING IS NOT PLAYED ─────────────────────────────────────────────────────────────
        //
        // Judged by ear and rejected: "I don't like the breathing, remove it." Not a bug — the model
        // and the synthesis were both repaired first (see BreathTests and --breath), and what was
        // left was a breath that sounded like a breath and was still not wanted. A sound nobody wants
        // to hear is not information, however correct it is.
        //
        // The MODEL stays and keeps running: Breathing drives the exertion readout ("Breathing hard",
        // "Winded") that the player asks for on a key, and that readout is the useful half. Only the
        // voice is gone, and bringing it back is these two lines.
        //
        //   _controller.OnBreath += (pos, breath) => _audioSystem.OnBreath(_ownEntityId, pos, breath);
        //   _others.OnBreath += _audioSystem.OnBreath;

        _shell.CommandEntered += HandleCommandEntered;
        _microphone.PacketReady += OnVoicePacketReady;
        // What the microphone hears goes straight to the player's own room, not round the server.
        _microphone.SamplesCaptured += OpenFPS.Client.AudioEngine.Fmod.OwnVoiceRing.Shared.Write;
        WireConnection();

        RegisterBindings();
    }

    /// <summary>
    /// Starts audio-engine initialization and asset preloading on a dedicated background thread.
    ///
    /// It must NOT run inline: the session is constructed on the network/game-loop thread, and a
    /// synchronous audio init would stop that thread pumping the messages the world-load handshake is
    /// made of. Audio is only used after spawn, by which time this has completed; until then the
    /// provider's calls no-op.
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
    // One table, both heads. A head that wants a different layout rebinds; it does not reimplement.

    private void RegisterBindings()
    {
        // Accessibility readouts — the game's HUD, spoken.
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
        // N: the narration as you turn, on or off, and remembered. Not a screen reader's key, and
        // nothing else in gameplay had it.
        _bindings.Bind(InputContext.Gameplay, GameKey.N, ToggleTurnNarration);

        // Interaction.
        _bindings.Bind(InputContext.Gameplay, GameKey.E, Interact);
        // Shift+E: knock on the nearest door instead of opening it.
        _bindings.Bind(InputContext.Gameplay, GameKey.E, KeyModifiers.Shift,
            () => _network.Send(new TextCommand { Command = "knock" }));

        // P is "what am I looking at", answered HERE rather than by the server. It used to send
        // `scan`, which is a different question — the five nearest things in any direction, most of
        // them the floor — and it had to cross the network to answer a question the client can answer
        // instantly from geometry it already has. `scan` is still a command for when you want it.
        _bindings.Bind(InputContext.Gameplay, GameKey.P, LookAhead);
        _bindings.Bind(InputContext.Gameplay, GameKey.P, KeyModifiers.Shift,
            () => _network.Send(new TextCommand { Command = "scan" }));
        // I opens what you carry as a list to choose from; Shift+I says it in one sentence, as before.
        _bindings.Bind(InputContext.Gameplay, GameKey.I, () => _network.Send(new InventoryRequest()));
        _bindings.Bind(InputContext.Gameplay, GameKey.I, KeyModifiers.Shift, () => _network.Send(new TextCommand { Command = "inv" }));

        // Carrying things. G takes whatever is within reach, Q puts down what is in your hand, and
        // R (with no gun in hand) swaps a hand for your back — the three verbs you use while moving, on keys you can find
        // without letting go of the movement ones. Naming a particular thing is what the console is
        // for; these are the ones you want under a finger.
        _bindings.Bind(InputContext.Gameplay, GameKey.G, () => _network.Send(new TextCommand { Command = "take" }));
        // T starts the engine; Shift+T switches it off. Two keys, not one toggle: a toggle pressed by
        // somebody who cannot tell whether the engine is already running switches it OFF half the
        // time — which is exactly what happened on the first drive with a key.
        // T: the key in a vehicle; on foot, clap your hands.
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
        // J and L: the left and right indicator in the driver's seat (they turn you on foot, which is
        // read as a held key and does nothing in a seat). Which way you mean to turn is what the
        // guide and the brake cue plan the junction ahead by.
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
        // R does what the moment calls for: in a seat it winds the window, with a gun in your hands it
        // reloads it, and otherwise it slings what you hold onto your back. One key for the three,
        // because they never apply at once, and a player should not have to remember which mode
        // they are in to find the one that does.
        _bindings.Bind(InputContext.Gameplay, GameKey.R, () => _network.Send(new TextCommand { Command = RKeyCommand(_state) }));
        // Shift+R: the reverse, the first thing on your back into your hand. Without a name the server
        // takes whatever was slung first.
        _bindings.Bind(InputContext.Gameplay, GameKey.R, KeyModifiers.Shift, () => _network.Send(new TextCommand { Command = "draw" }));
        _bindings.Bind(InputContext.Gameplay, GameKey.V, ToggleVoiceTransmission);

        // ── Firing, on ENTER, and NEVER on a screen reader's key ────────────────────────────────
        //
        // Not on control. CONTROL IS HOW A SCREEN READER USER SILENCES SPEECH: every reader there is
        // (NVDA, JAWS, Orca, VoiceOver) stops talking when you press it, so a blind player presses it
        // constantly, reflexively, without thinking of it as input. Bound to firing, it fires a rifle
        // every time they hush the reader, and the shots sound like random banging from nowhere.
        //
        // Enter, because a blind player finds it by touch without counting keys from a landmark, it
        // is under the right hand that is already on J K L O for turning, and no reader claims it in
        // a focused game window. See ScreenReaderKeys: nothing in gameplay may be bound to one.
        //
        // Only with a gun in your hands. Without one, Enter is the interact key it always was: a
        // player reaching for a door with empty hands must not be told they have nothing to fire.
        _bindings.Bind(InputContext.Gameplay, GameKey.Enter, () => { if (EnterFires(_state)) Fire(); else Interact(); });

        // X: the fire selector, a detent on; Shift+X back. The server holds where it sits and says it.
        // On the admin gun the selector is its mode. Y and Shift+Y: the admin gun's calibre.
        _bindings.Bind(InputContext.Gameplay, GameKey.X, () => SelectorKey(1));
        _bindings.Bind(InputContext.Gameplay, GameKey.X, KeyModifiers.Shift, () => SelectorKey(-1));
        _bindings.Bind(InputContext.Gameplay, GameKey.Y, () => CalibreKey(1));
        _bindings.Bind(InputContext.Gameplay, GameKey.Y, KeyModifiers.Shift, () => CalibreKey(-1));

        // Social / discovery. The plain key is the wider question and shift narrows it to here —
        // the same relationship on both, so there is one thing to remember rather than two.
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
        // Comma was a second look-ahead key; P is that.
        RegisterTrackerBindings();
    }

    /// <summary>
    /// Keys a screen reader owns, which nothing in gameplay may ever be bound to.
    ///
    /// CONTROL silences speech in every screen reader there is. ALT is the window manager's and opens
    /// menus. Both are pressed by a blind player dozens of times a minute as punctuation, not as
    /// input — so a game action on either is not a key that is hard to use, it is a key that fires by
    /// itself.
    ///
    /// Modified bindings are a different thing and are fine: shift-F5 is a chord somebody chose to
    /// press. What is forbidden is a screen reader's key AS the action.
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
        "Scope, on the keypad with Num Lock on: star raises it, 8 2 4 6 aim, 5 what is on the crosshair, 7 and 9 the targets in view,",
        "plus and minus zoom, 1 and 3 the turret, period the rangefinder, 0 held to hold your breath, slash or Enter to fire.",
        "V voice, F5 players, F6 maps, F8 friends, F12 the world editor, brackets to read chat, slash for the command console.",
        "Escape for the game menu: keep playing, main menu, or quit.");

    /// <summary>Rebinds a key. Exposed so a head (or a future settings screen) can re-map without
    /// touching the session.</summary>
    public void Bind(InputContext context, GameKey key, Action action) => _bindings.Bind(context, key, action);

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
        return OpenFPS.Client.AudioEngine.Fmod.FmodAudioProvider.TracedReverbStatus(null);
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
    /// /levels, /levels default, /levels 0.7 (or 70): how much of the real difference in loudness
    /// between sounds reaches the mix. Everything is placed by it — how far a thing carries, how much
    /// louder a hot rod is than a hatchback, how much a car rises when it is floored.
    /// There is no "real" any more (Cody, 2026-10-05): 100 percent is literal source levels, which on
    /// headphones made a parked car's idle inaudible and footsteps vanish; the word invited it.
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

        // Bindings run in the context the shell reports: with a modal console open, gameplay bindings
        // must not fire, but the global ones (chat navigation, quit) still should.
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

        // Remote interpolation FIRST, then everything that reads the world reads one snapshot.
        // Interpolation is the only step in the tick that mutates the world, and it touches only remote
        // entities (it skips the local player, whose position prediction owns). Advancing it before the
        // readers rather than between two of them means prediction, the shelter raycast, the proximity
        // scan and the audio system all share a single snapshot build instead of forcing a second one.
        _world.UpdateInterpolation(dt, _ownEntityId);
        var snapshot = _world.GetSnapshot();

        _reconciler.Step(input, snapshot, dt);
        UpdateWallBump(snapshot, input);
        UpdateTurnNarration(snapshot, input, gameplayActive);

        UpdateShelterFactor(dt, snapshot);

        // Localize atmospheric effects: a roof over your head is most of what stops the rain.
        _state.PrecipitationIntensity = _world.CurrentPrecipitation * (1.0f - _state.ShelterFactor);

        // Bleed the reconciliation visual offset toward zero so server snaps don't pop.
        if (_state.VisualOffset.LengthSquared() > 0.0001f)
            _state.VisualOffset = Vector3.Lerp(_state.VisualOffset, Vector3.Zero, 5.0f * dt); // ~0.2 s
        else
            _state.VisualOffset = Vector3.Zero;

        if (input.MoveDirection != Vector3.Zero || input.LookDelta != Vector2.Zero || input.Jump || _sequenceId % 10 == 0)
            _network.Send(input);

        CheckInteractableProximity(snapshot);
    }

    /// <summary>
    /// Getting in and out, as the server reports it.
    ///
    /// The transition is what matters, not the state: sitting down and standing up are both teleports
    /// as far as prediction is concerned, so the unacknowledged input history is meaningless across
    /// either and the stride accumulator is holding distance walked by somebody who is now sitting in
    /// a car. Both are thrown away, exactly as they are on a spawn.
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
    /// Sitting in something, you face the way it faces.
    ///
    /// Reported: "when the bus turns, the bus turns around my head, which is wrong. My head should
    /// stay facing the direction of the bus, and when I press F it should tell me the correct
    /// direction." A first attempt carried your own heading round with the vehicle's turns and let
    /// the server's copy of it correct yours; but the server's copy arrives a network trip late, so
    /// half way through every corner the two disagreed by more than the correction threshold and your
    /// head was snapped back to where the bus had been — the bus swinging round you. Now, while you
    /// ride, your heading IS the vehicle's, set every frame from the vehicle itself and never
    /// corrected (PredictionReconciler skips the look while Riding). The ears, the compass on F and
    /// the way you face when you step off all read the same number.
    /// </summary>
    private void FollowRide(WorldSnapshot snapshot)
    {
        if (!_state.IsRiding || !snapshot.Entities.TryGetValue(_state.RidingEntityId, out var ride)) return;
        MathHelper.ToYawPitch(ride.Transform.Rotation, out float yaw, out _);
        _state.Yaw = MathHelper.WrapAngle(yaw);
        _state.Rotation = Quaternion.CreateFromYawPitchRoll(_state.Yaw, _state.Pitch, 0f);
    }


    /// <summary>Render-rate update: footstep generation + spatial audio listener/emitters.</summary>
    public void ContinuousUpdate()
    {
        if (!IsInGame) return;
        // A passenger travels without walking. Feeding the vehicle's motion to the stride generator
        // would produce a footstep every stride-length of ROAD — at sixty miles an hour, a machine gun.
        if (_state.IsRiding) _controller.Teleported();
        // ── Where the body IS, not where the camera is being eased to ───────────────────────────
        //
        // VisualOffset is a rendering term: when the server corrects the prediction, the listener is
        // slid to the new position over about two tenths of a second instead of being snapped, so the
        // world does not jump. Feeding it to the stride generator made that slide into WALKING. A
        // correction of a few metres decays at up to twenty-five metres a second, in steps small
        // enough to look plausible, and if the player's own velocity is over the walking threshold at
        // the time — which it is, if they were moving when it landed — every one of those steps banks
        // distance. Heard, and reported, as "when I /tp myself or land in the map, I hear a few
        // footsteps before it settles".
        //
        // The accumulator's own rule is that a stride is something a body DID. The smoothing is
        // something done to the camera, so it has no business here at all.
        else _controller.Update(_state.Position, _state.Velocity);

        var snapshot = _world.GetSnapshot();
        FollowRide(snapshot);

        // ...and everybody else, off the same snapshot. A passenger needs the same exemption as the
        // local player above, and has it there: a seated body carries its vehicle's velocity, and the
        // definition says it is seated (OtherBodies).
        _others.Update(snapshot, _ownEntityId);

        // Internally capped to 60 Hz; the loop this hangs off spins far faster to keep the socket
        // serviced. See ClientAudioSystem.UpdateHz.
        _audioSystem.Update(snapshot);
        UpdateGuidance();

        // ...and only then, because the region the audio system just worked out is the one to say.
        AnnounceStairs(snapshot);
        AnnounceZoneChanges(snapshot);
        AnnounceMapEdge();
    }

    /// <summary>
    /// Says so when you walk into the edge of the map. The edge is not a wall — nothing is there to
    /// hear or touch — so stopping at it silently felt like the keys had stopped working. Said once
    /// on arriving, and again only after you have stepped a metre back from it.
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
    // Which way each key turns you, as a sign on the values the physics applies:
    //   Yaw   -= LookDelta.X * RotationSpeed * dt      (and forward = (sin yaw, 0, cos yaw), so
    //                                                   INCREASING yaw swings forward toward +X,
    //                                                   which is right — therefore a POSITIVE
    //                                                   LookDelta.X decreases yaw and turns LEFT)
    //   Pitch += LookDelta.Y * RotationSpeed * dt      (and Rotation is built by
    //                                                   Quaternion.CreateFromYawPitchRoll(yaw, pitch, 0),
    //                                                   whose pitch is a RIGHT-HANDED rotation about
    //                                                   +X — which takes forward (+Z) toward -Y.
    //                                                   Therefore INCREASING pitch looks DOWN.)
    //
    // J and L were the wrong way round, and the derivation above is why: J emitted a NEGATIVE X,
    // which increases yaw, which turns right. Reported as "turning left seems to turn me right", and
    // it is only findable by following the sign all the way to the forward vector, because every
    // step of it is individually plausible.
    //
    // K and O were wrong for the same reason and the comment here was part of it: it asserted that a
    // positive pitch looks up, which is the intuitive reading of the word and the opposite of what
    // CreateFromYawPitchRoll does. So K, written to look down, emitted a negative Y, which decreased
    // pitch, which looked UP. Reported as "k and o seem to be swapped". The sign now comes from the
    // rotation, not from the word, and TurnKeyTests holds it there.
    private static readonly (GameKey Key, float X, float Y)[] TurnKeys =
    {
        (GameKey.J, +1f,  0f),   // left
        (GameKey.L, -1f,  0f),   // right
        (GameKey.K,  0f, +1f),   // down  (increasing pitch tilts forward toward -Y)
        (GameKey.O,  0f, -1f),   // up
    };

    /// <summary>A tap turns this far. A quarter turn: four presses face you the other way.</summary>
    private const float TurnStepDegrees = 45f;
    /// <summary>...and with shift, this far, for lining something up by ear.</summary>
    private const float TurnFineDegrees = 1f;
    /// <summary>How long a key must be down before a tap becomes a sweep.</summary>
    private const double TurnHoldBeforeSweep = 0.35;
    private const float SweepDegreesPerSecond = 180f;
    private const float FineSweepDegreesPerSecond = 20f;

    private readonly Dictionary<GameKey, double> _turnDownAt = new();
    private double _simTime;

    /// <summary>
    /// How far to the next forty-five degree mark in the direction a turn key was pressed.
    ///
    /// Sign conventions, because they are the whole of it and each one is individually plausible:
    /// the physics applies <c>Yaw -= LookDelta.X * ...</c>, so a POSITIVE x (J) DECREASES yaw, and
    /// <c>Pitch += LookDelta.Y * ...</c>, so a positive y (K) increases pitch. The step returned
    /// here is always a positive magnitude; the key's own axis sign carries the direction.
    /// </summary>
    private float SnapDegrees(float ax, float ay)
    {
        // Which way the angle itself moves, as against which way the key's axis points.
        float currentDeg, dir;
        if (ax != 0f) { currentDeg = _state.Yaw * (180f / MathF.PI); dir = -MathF.Sign(ax); }
        else { currentDeg = _state.Pitch * (180f / MathF.PI); dir = MathF.Sign(ay); }

        float step = TurnStepDegrees;
        // Where the grid line is, in the direction of travel. A heading already ON the grid gets a
        // whole step — otherwise the key would do nothing at all, which is worse than overshooting.
        float grid = dir > 0f ? MathF.Ceiling(currentDeg / step) * step
                              : MathF.Floor(currentDeg / step) * step;
        float delta = MathF.Abs(grid - currentDeg);
        // A tolerance, because a float yaw is never exactly on a mark after a few turns and a
        // hundredth of a degree of "snap" is a key that did nothing.
        const float OnGrid = 0.25f;
        return delta < OnGrid ? step : delta;
    }

    /// <summary>
    /// Turns the four look keys into a LookDelta, as DISCRETE steps rather than a continuous push.
    ///
    /// A key that turns for as long as it is down cannot be aimed: at the old rate a press held for
    /// a tenth of a second swung you twenty-six degrees, so listening to something and then asking
    /// which way you were facing gave a different answer every time — "it seems jumpy when I turn and
    /// then press f, it's like sometimes I overshoot". A tap is now exactly forty-five degrees,
    /// whatever the frame rate and however fast the key was released, so four of them face you the
    /// other way and eight bring you back. Holding still sweeps, for when you want to scan.
    ///
    /// The step is converted into the units the physics expects for ONE tick, so the client's
    /// prediction and the server's authoritative copy apply exactly the same arithmetic and agree.
    /// </summary>
    private Vector2 GatherLook(HashSet<GameKey> held, IReadOnlyCollection<GameKey> justPressed, bool fine, float dt)
    {
        // In the driver's seat your head faces where the car points, and stays there. Every cue —
        // the guide ahead, the centre line on your left — is placed relative to the car, and a head
        // turned away with J or L would put them all somewhere else. A and D steer the car.
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
                // A coarse tap SNAPS TO THE GRID rather than adding to wherever you happen to be.
                //
                // Adding forty-five degrees to an off-angle heading keeps it off-angle for ever.
                // Once a fine nudge or a sweep has left you at, say, 47 degrees, every coarse tap
                // after it lands on 92, 137, 182 — and walking "straight" then changes BOTH
                // coordinates, which is the whole complaint: "if I press j or l to go facing north
                // and I walk straight, both the x and the y change when they shouldn't."
                //
                // Heading north, east, south or west and having exactly one coordinate move is the
                // thing this key is for. So a coarse tap goes to the next multiple of forty-five in
                // the direction pressed — which is a full step when you are already on the grid,
                // and less than one when you are not. Shift is unchanged: one degree, off-grid on
                // purpose, for lining something up by ear.
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

        // Shift is a TURN modifier now, not just a suppressor, so it has to be told apart from the
        // window-manager and screen-reader chords that must never move the player.
        bool fine = InputStateBuffer.HasShift(held);
        // Alt still suppresses everything — it is the window manager's and the screen reader's. Control
        // no longer does, because control is the trigger now, and a player must be able to fire while
        // they are moving.
        bool chord = InputStateBuffer.HasAlt(held)
                   || held.Contains(GameKey.Slash) || held.Contains(GameKey.NumpadDivide);
        if (chord) return input;

        // Shift modifies the key it is pressed WITH, rather than suppressing everything.
        //
        // It used to stop movement dead, so that a shift chord could never walk the player somewhere.
        // That also made shift+W unusable, and shift+W is where a run belongs — it is the key every
        // other game puts it on and the one a hand finds without looking. The two meanings do not
        // collide, because they are on different keys: shift with a turn key is still a one-degree
        // nudge, shift with a movement key is a run, and holding both does both.
        // A key that went down AND back up between two drains still moved the player.
        //
        // Held state is sampled once per fixed tick, 33 ms apart, and a quick tap is shorter than
        // that: pressed and released inside one interval, the key was never in `held` when the tick
        // looked, so the press did nothing at all — no movement, no footstep, no packet. A press is a
        // player asking to move, and the smallest amount of movement this simulation can express is
        // one tick of it, so that is what a press that is already over is worth. The just-pressed set
        // is consumed by the same drain, so it is paid exactly once however the two rates line up.
        bool Pressed(GameKey k) => held.Contains(k) || justPressed.Contains(k);

        // The arrow keys are the same four, for anyone whose hand goes there first — which is most
        // people the moment they are behind a wheel. Held together with WASD they do not add up to a
        // double step: each direction counts once.
        Vector3 move = Vector3.Zero;
        if (Pressed(GameKey.W) || Pressed(GameKey.Up)) move.Z += 1;
        if (Pressed(GameKey.S) || Pressed(GameKey.Down)) move.Z -= 1;
        if (Pressed(GameKey.A) || Pressed(GameKey.Left)) move.X -= 1;
        if (Pressed(GameKey.D) || Pressed(GameKey.Right)) move.X += 1;
        // Lane assist, when you are driving and not steering yourself: the car holds the middle of
        // the lane. Your own A or D always wins — the moment you steer, it lets go.
        if (_state.RidingControls && move.X == 0f && _audioSystem.Driving.AssistSteer is { } assist)
            move.X = assist;
        // Normalised on foot, so diagonal walking is not faster. Not while driving: there the two
        // axes are throttle and steering, and shrinking one because the other is held would halve
        // the throttle every time lane assist leaned on the wheel.
        if (move != Vector3.Zero)
            input.MoveDirection = _state.RidingControls
                ? new Vector3(Math.Clamp(move.X, -1f, 1f), 0f, Math.Clamp(move.Z, -1f, 1f))
                : Vector3.Normalize(move);

        if (held.Contains(GameKey.Space)) input.Jump = true;
        // The horn, for as long as H is down, in the driver's seat. Every packet says so; the server
        // lets go of it a few ticks after they stop saying it.
        input.Horn = _state.RidingControls && !fine && Pressed(GameKey.H);

        // Running is a claim about a key, not about a speed: the speed is the server's to apply, and
        // prediction reads the same flag so a stride does not mispredict.
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
                // A second manifest is a journey to another map (/join, or a map chosen from F6). Everything
                // the last map was making a sound for goes before the new one arrives, and the body goes
                // too: until the server spawns us again there is nobody here to move.
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
                // The map's authored atmosphere applies immediately: the world-state broadcast only
                // arrives once a second, and until it does the acoustics would otherwise be computed for
                // the previous map's air.
                _world.ApplyManifestAtmosphere(manifest);
                // The map's outdoor soundfield. It plays for as long as the map is loaded and is
                // ducked by shelter rather than switched off, so a doorway is a change in the world
                // rather than a boundary the world stops at.
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
                _state.MapSize = manifest.WorldSize;

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
                // Niced, and off the shared pool. The voxel bake is seconds of solid CPU that lands
                // at the exact moment thirty engine voices are being created and primed; at equal
                // priority on a pool that is also decoding samples, it takes its cores from the
                // audio. See BackgroundPriority for why lowering this is the only lever that works.
                _audioSystem.NoteSceneLoading();
                BackgroundPriority.RunLowered("AcousticBake", GenerateAcoustics);
                break;

            case PlayerSpawned spawn:
                // The other half of the budget hold: the bake is over, but the cars only start
                // sounding now, and their first seconds are the expensive ones.
                _audioSystem.NoteSceneLoading();
                _ownEntityId = spawn.EntityId;
                _physics.OwnEntityId = spawn.EntityId;
                _physics.Spatial.OwnEntityId = spawn.EntityId; // ignore self in prediction/raycasts
                _audioSystem.OwnEntityId = spawn.EntityId;

                _state.Position = spawn.SpawnTransform.Position;
                _state.Rotation = spawn.SpawnTransform.Rotation;
                _state.Velocity = Vector3.Zero;
                _reconciler.Reset(); // CRITICAL: reset the prediction buffer on teleport/spawn
                _controller.Teleported(); // ...and the stride accumulator, or the spawn walks for you
                _stairs.Reset();          // ...and which stairs you were at: landing beside some is not walking up to them
                _bumps.Reset();           // ...and the wall you were last against
                _sight.Reset();

                Serilog.Log.Information("PlayerSpawned: entity {Id} at {Pos}.", spawn.EntityId, spawn.SpawnTransform.Position);
                // A spawn after arriving is a teleport (/tp): the server says where to, and the zone is
                // announced as you land in it. Only arriving on a map is an entry into the world.
                if (_arrived) break;
                _arrived = true;
                LoadProgress("Entering World...", 100);
                _shell.EnterGame();
                GameJoined?.Invoke();
                Ui.Play(UiCue.EnterWorld);
                // The world comes up over a second rather than starting mid-sentence; the chord above
                // is an interface sound and is not faded.
                FadeWorldIn();
                // What a player needs on arriving, and nothing else: where they are. Said once the body
                // is placed in a zone (AnnounceZoneChanges), so the map and the zone are one sentence.
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
                // Something happened somewhere and made a noise. Rendered on arrival and queued for
                // its own moment, because the parts of one event do not all happen at once: a latch
                // precedes its own impact, and a pane's glass lands a second and a half after it broke.
                _audioSystem.WorldAudio.Receive(audioEvent, OpenFPS.Common.AudioClock.Now);
                break;

            case WorldStateUpdate wsu:
                _world.UpdateAtmosphere(wsu);
                // The one wind the trees, the fires and your ears read.
                OpenFPS.Common.WindField.Weather = _world.Wind;
                break;

            case HitConfirm confirm:
                // Your shot landed. A chime and no words: the chime is the information, and a word
                // on every hit would talk over the fight. A kill has its own.
                Ui.Play(confirm.Killed ? UiCue.Kill : UiCue.Hit);
                break;

            case StatsUpdate stats:
                _state.Health = stats.Health;
                _state.HeldWeaponId = stats.HeldWeaponId ?? "";
                _state.HeldRounds = stats.HeldRounds;
                _state.HeldScopeId = stats.HeldScopeId ?? "";
                _state.SpeedLimit = stats.SpeedLimit;
                _state.CurrentMaterial = stats.CurrentMaterial;
                _state.CurrentVariant = stats.CurrentVariant;
                // CurrentMaterial feeds the reverb bus material calculation via LocalPlayerState: when a
                // region's floor material (index 0) was not explicitly authored, this is the runtime
                // override for the underfoot surface absorption.
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

        // Tell the server we're done loading; it responds by spawning us (-> PlayerSpawned). This MUST
        // be sent here, not in the PlayerSpawned handler — the spawn is the server's reply to "ready",
        // so sending it later would deadlock the handshake.
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
    /// What you are looking at, answered here and now.
    ///
    /// Three facts in the order a player wants them: what it is, what it is made of, and how far. The
    /// material is worth saying because it is what the thing will SOUND like when anything happens to
    /// it, so it is the difference between "a wall" and a wall you now expect to ring.
    ///
    /// With nothing in front of you the answer is where you are, because "nothing directly ahead" is
    /// a non-answer to a player who pressed a key to find out where they were pointing. The zone is
    /// the fact underneath that question.
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
    /// After each fresh movement step: if the body pressed into something it had not already met, a
    /// knock from where it touched and the thing's name. See <see cref="WallBumps"/> for what counts.
    ///
    /// Local only: heard by you, through the ordinary world-sound path (placed, occluded, in the
    /// room), but not sent to the server, so nobody else hears you meet a wall. The prediction that
    /// finds the contact is the client's; making it a world sound for others would mean the server
    /// finding the same contact in its own step and sending it out, which is a feature of its own.
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
    /// After each step: what your eyes would tell you. Once your own turning or looking has settled,
    /// what is in front of you; as you walk or strafe, what has come in front of you; and anything
    /// crossing in front. See <see cref="SightWatch"/> for when and what, and <see cref="Sightline"/>
    /// for the ground never being named.
    /// </summary>
    private void UpdateTurnNarration(WorldSnapshot snapshot, ClientInputUpdate input, bool gameplayActive)
    {
        // Not riding (the vehicle faces for you), not through a scope (it has its own readout), not
        // while a list or the console has the keyboard.
        if (_state.IsRiding || _scope.Raised || !gameplayActive || !NavigationAids.TurnNarration) { _sight.Reset(); return; }
        // A look key still held counts as looking: between a tap's step and the sweep that follows
        // when it is held, no look arrives for a third of a second, which is longer than the settle.
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

    /// <summary>
    /// Firing what is in your hands, on a key rather than a typed command.
    ///
    /// Still a text command on the wire: the server spends the round, finds what it hit and takes the
    /// health, and says back a HitConfirm when it was somebody.
    /// </summary>
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
    /// Says the zone as you cross into it, without being asked.
    ///
    /// Keyed on the region ID and not on its name: two rooms may share a name, and the outdoor
    /// fallback name flips between "Outside" and "Under Shelter" on a continuous shelter value, which
    /// would announce itself every time a bridge passed overhead. The id changes exactly when you
    /// cross a boundary, which is exactly when a player wants to be told.
    ///
    /// Spoken WITHOUT interrupting, because crossing a doorway must not cut off whatever you were
    /// already being told — very often the thing that made you walk through it.
    /// </summary>
    private void AnnounceZoneChanges(WorldSnapshot snapshot)
    {
        // Whether the zone you are in has held long enough, with the body moving as a body moves, to
        // be a place you have walked into rather than a flicker or a bounce. See ZoneSettle.
        double now = (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds;
        bool settled = _zoneSettle.Update(_state.CurrentRegionId, _state.Position, now);

        if (_arrivalPendingSince is { } since)
        {
            string here = _state.CurrentRegion;
            var waited = DateTime.UtcNow - since;
            // The zone is placed by the audio update a few frames after the spawn, and until then the
            // name is the previous map's or the default. Past a second and a half it is not coming.
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

        // Not a room's name while you are on the stairs. Where the map has not named its flights, a
        // storey's zone ends at its ceiling and the next begins at its floor, so at eye height the next
        // floor's name comes halfway up the flight — "floor 3" with five steps still to climb, and on
        // the way down "floor 2" two treads from the top. Held until your feet are off the treads, it
        // is said on the landing, which is where you arrive. A named flight is the stairs' own name.
        if (_stairs.OnFlight && !part) return;

        if (region == _lastAnnouncedRegionId) return;
        // ...and not until it has held: a zone that changes back within a breath, or while the body is
        // thrown about faster than it walks, was never crossed into (Cody on Brandt Court's roof,
        // 2026-10-04: "Brandt Court roof", "sidewalk", eighteen times in three seconds).
        if (!settled) return;
        _lastAnnouncedRegionId = region;
        Serilog.Log.Information("[ZONE] {Id} '{Name}' at {Pos}", region, _state.CurrentRegion, _state.Position);

        // ...and the NAME has to have changed too, which is the other half of it.
        //
        // A place worth naming is rarely one box. A banked turn is a curve and a straight is four
        // hundred metres, so either is tiled out of several region volumes that are all the same
        // PLACE — and keying on the id alone announced "Turn one and two" four times while you
        // walked through it. Requiring the name to change as well makes crossing between two boxes
        // of one region silent, which is what a player means by not having moved.
        string name = _state.CurrentRegion;
        if (string.IsNullOrWhiteSpace(name) || name == _lastAnnouncedRegion) return;
        // A doorway is roofed and in no zone, so stepping from a flat into its corridor passed through
        // "Under Shelter" on the way. Where you are is the zone on either side of it; the where-am-I
        // key still says it.
        if (name == ClientAudioSystem.UnderShelter || name.StartsWith(ClientAudioSystem.DoorwayPrefix)) return;

        _lastAnnouncedRegion = name;
        // The stair cue speaks for a flight or a landing it has just told you about: walking up to a
        // flight facing it, "Stairs up, 17 steps, to floor 3" is said half a metre short of the first
        // riser, and the landing you are on and the flight you step onto a moment later would each say
        // their names straight after it. The cue says more — which way, how many, to where — and comes
        // first. Their names are said when it did not speak: onto the stairs from the side or
        // backwards, or off a flight onto a landing. See StairCues.CoversZone.
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
    /// The foot or the top of a flight, said once as you reach it facing along it: "Stairs up, 17 steps,
    /// to floor 3". Without interrupting, like a zone: it is where you are, not an alarm. Not while you
    /// ride anything, which carries you past stairs rather than up them. The zone is passed so that
    /// leaving the stairwell and coming back is a new arrival and walking about inside it is not.
    /// </summary>
    private void AnnounceStairs(WorldSnapshot snapshot)
    {
        if (_state.IsRiding) { _stairs.Reset(); return; }
        // The ROOM, not a flight's or a landing's name: stepping off a landing onto the floor beside it
        // and back is not having been somewhere else, and must not say the flight again.
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

    /// <summary>
    /// The map list, as a sentence rather than a grid.
    ///
    /// Ordered by how many people are on each, because that is the fact a player is actually asking
    /// for: a list of names tells you what exists, and the population tells you where the game is.
    /// </summary>
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
    /// What you can do with a person: all of it is a command the server answers aloud. "Where is" is
    /// for moderators and the administrator (the server refuses /where to anyone else, developers
    /// included since the roles of 2026-10-05), so nobody else is offered it.
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

    /// <summary>Capture thread. Unreliable and unordered on purpose: a frame resent late is a frame the
    /// listener has already had to do without, and waiting for it would hold up every frame behind it.</summary>
    private void OnVoicePacketReady(byte[] opusData)
    {
        ushort sequence = (ushort)System.Threading.Interlocked.Increment(ref _voiceSequence);
        if (sequence == 0) sequence = (ushort)System.Threading.Interlocked.Increment(ref _voiceSequence);
        _network.Send(new VoiceData { SenderId = _ownEntityId, OpusData = opusData, Sequence = sequence },
            LiteNetLib.DeliveryMethod.Unreliable);
        _network.Flush();
    }

    /// <summary>
    /// Turns a line typed into the command console into either a slash command or public chat.
    /// </summary>
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
            // Which tail the open air has — traced through the map's geometry, or the room algorithm.
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
    /// Announces named interactables as the player walks into range, at roughly 1 Hz so the screen
    /// reader is not flooded. Reads the snapshot the tick already built rather than forcing another.
    /// </summary>
    private void CheckInteractableProximity(WorldSnapshot snap)
    {
        if (++_proximityCheckCounter % PhysicsConstants.TickRate != 0) return;

        _currentNearby.Clear();
        foreach (var entity in snap.Entities.Values)
        {
            if (entity.Id == _ownEntityId) continue;
            if (entity.Definition.Type == EntityType.Player) continue;

            // Only things that earn an announcement. Every entity carries a name — the walls, the floor,
            // the auto-injected foundation, the acoustic region volumes and the portals all have one so
            // that authors and logs can refer to them. Announcing all of them meant that stepping through
            // a doorway read the portal prefab's authoring notes aloud. The server decides (prefab
            // `Announce`); the client just obeys.
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
    /// Calculates the ShelterFactor (0 to 1) from regional data and vertical raycasting. Shelter
    /// reduces precipitation, damps environmental ambience, and stills the wind.
    /// </summary>
    private void UpdateShelterFactor(float dt, WorldSnapshot snap)
    {
        bool isSheltered = false;

        // Same eye position as the audio system uses for the listener, so the two agree.
        Vector3 visualEyePos = _state.VisualPosition + new Vector3(0, _state.EyeHeight, 0);

        // 1. Regional check: are we in an explicitly marked "indoor" region?
        if (snap.AcousticMap != null)
        {
            int regionId = _physics.Spatial.GetRegionAt(snap, visualEyePos);
            if (regionId != AcousticConstants.GlobalRegionId && snap.AcousticMap.Regions.TryGetValue(regionId, out var region))
                isSheltered = region.IsIndoor;
        }

        float targetShelter = isSheltered ? 1.0f : 0.0f;

        // 2. Volumetric sky-visibility check: a 16-ray Fibonacci hemisphere cast upward.
        if (!isSheltered)
        {
            const int rays = 16;
            int hits = 0;
            float goldenRatio = (1 + MathF.Sqrt(5)) / 2;

            for (int i = 0; i < rays; i++)
            {
                float theta = 2 * MathF.PI * i / goldenRatio;
                float phi = MathF.Acos(1 - (float)i / rays); // upper hemisphere only

                var dir = new Vector3(
                    MathF.Cos(theta) * MathF.Sin(phi),
                    MathF.Cos(phi),
                    MathF.Sin(theta) * MathF.Sin(phi));

                if (_physics.Spatial.RaycastSingle(snap, visualEyePos, dir, AcousticConstants.ShelterRayDistance, out _, out _))
                    hits++;
            }

            targetShelter = (float)hits / rays;
        }

        // 3. Smooth the transition so the audio does not pop at a threshold.
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

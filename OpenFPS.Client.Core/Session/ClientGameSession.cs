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
public sealed class ClientGameSession : IDisposable
{
    private readonly ClientNetworkService _network;
    private readonly ISpeechOutput _speech;
    private readonly IClientShell _shell;
    private readonly IMicrophoneCapture _microphone;

    private readonly ClientWorldState _world;
    private readonly LocalPlayerState _state;
    private readonly ClientPhysicsSystem _physics;
    private readonly LocalPlayerController _controller;
    private readonly OtherBodies _others;
    private readonly AudioEngineFacade _audioEngine;
    private readonly SoundMappingService _sounds;
    private readonly ClientAudioSystem _audioSystem;
    private readonly PredictionReconciler _reconciler;
    private readonly ChatManager _chat;
    private readonly InputCommandMapper _bindings = new();

    private long _sequenceId;
    private int _ownEntityId = -1;
    private int _expectedEntityCount;
    private readonly bool _enableAudio;

    // Map metadata captured from the manifest, needed when acoustics are generated later.
    private float _voxelResolution = AcousticConstants.DefaultVoxelResolution;
    private float _occlusionFloor = 0.2f;
    private Vector3 _mapMin;

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
        _speech = speech;
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

        _sounds.Initialize();
        // Your own feet ride with your head (see ClientAudioSystem.OnOwnFootstep); everybody
        // else's are sounds at places in the world.
        _controller.OnStepTriggered += _audioSystem.OnOwnFootstep;
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
                _speech.Speak("Sound library ready.", interrupt: false);
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
            () => Say($"Coordinates: {OpenFPS.Common.PlayerCoordinates.Format(_state.Position)}"));
        _bindings.Bind(InputContext.Gameplay, GameKey.F, () => Say($"Facing: {_state.GetCompassDirection()}"));
        _bindings.Bind(InputContext.Gameplay, GameKey.H, () => Say($"Health: {_state.Health} percent"));
        // Driving, Z is the road: which one, which way, which lane, how fast. On foot it is the area.
        _bindings.Bind(InputContext.Gameplay, GameKey.Z, () =>
            Say(_state.RidingControls && _audioSystem.Driving.Readout is { } road ? road : $"Area: {_state.CurrentRegion}"));
        _bindings.Bind(InputContext.Gameplay, GameKey.Comma, LookAhead);
        _bindings.Bind(InputContext.Gameplay, GameKey.B, () => Say(ExertionReadout()));

        // Interaction.
        _bindings.Bind(InputContext.Gameplay, GameKey.E, Interact);
        _bindings.Bind(InputContext.Gameplay, GameKey.Enter, Interact);

        // P is "what am I looking at", answered HERE rather than by the server. It used to send
        // `scan`, which is a different question — the five nearest things in any direction, most of
        // them the floor — and it had to cross the network to answer a question the client can answer
        // instantly from geometry it already has. `scan` is still a command for when you want it.
        _bindings.Bind(InputContext.Gameplay, GameKey.P, LookAhead);
        _bindings.Bind(InputContext.Gameplay, GameKey.P, KeyModifiers.Shift,
            () => _network.Send(new TextCommand { Command = "scan" }));
        _bindings.Bind(InputContext.Gameplay, GameKey.I, () => _network.Send(new TextCommand { Command = "inv" }));

        // Carrying things. G takes whatever is within reach, Q puts down what is in your hand, and
        // R swaps a hand for your back — the three verbs you use while moving, on keys you can find
        // without letting go of the movement ones. Naming a particular thing is what the console is
        // for; these are the ones you want under a finger.
        _bindings.Bind(InputContext.Gameplay, GameKey.G, () => _network.Send(new TextCommand { Command = "take" }));
        // T starts the engine; Shift+T switches it off. Two keys, not one toggle: a toggle pressed by
        // somebody who cannot tell whether the engine is already running switches it OFF half the
        // time — which is exactly what happened on the first drive with a key.
        _bindings.Bind(InputContext.Gameplay, GameKey.T, () => _network.Send(new TextCommand { Command = "ignition", Args = new[] { "on" } }));
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
        _bindings.Bind(InputContext.Gameplay, GameKey.Q, () => _network.Send(new TextCommand { Command = "drop" }));
        _bindings.Bind(InputContext.Gameplay, GameKey.R, () => _network.Send(new TextCommand { Command = "stow" }));
        _bindings.Bind(InputContext.Gameplay, GameKey.V, ToggleVoiceTransmission);

        // ── Firing, on ENTER, and NEVER on a screen reader's key ────────────────────────────────
        //
        // This was on both control keys, and the reasoning written here was that a player must be
        // able to fire while they are moving. True, and the wrong key: CONTROL IS HOW A SCREEN READER
        // USER SILENCES SPEECH. Every reader there is — NVDA, JAWS, Orca, VoiceOver — stops talking
        // when you press it, so a blind player presses control constantly, reflexively, without ever
        // thinking of it as input to anything.
        //
        // What that did was fire a rifle. Found in an audio log while chasing a report of "random
        // banging... bang, wait a few seconds, bang, like someone closing a cabinet, I have no clue
        // what the noise is": twenty-six `recv 'AKM' ... 159 dB` events in six minutes, in bursts
        // minutes apart, each with its bullet's impact echoing off the buildings. Five sessions were
        // spent looking for it in the acoustics. It was the trigger.
        //
        // Enter, because a blind player finds it by touch without counting keys from a landmark, it
        // is under the right hand that is already on J K L O for turning, and no reader claims it in
        // a focused game window. See ScreenReaderKeys: nothing in gameplay may be bound to one.
        _bindings.Bind(InputContext.Gameplay, GameKey.Enter, Fire);

        // Social / discovery. The plain key is the wider question and shift narrows it to here —
        // the same relationship on both, so there is one thing to remember rather than two.
        _bindings.Bind(GameKey.F5, () => _network.Send(new PlayerListRequest { Scope = PlayerListScope.Server }));
        _bindings.Bind(GameKey.F5, KeyModifiers.Shift, () => _network.Send(new PlayerListRequest { Scope = PlayerListScope.Map }));
        _bindings.Bind(GameKey.F6, () => _network.Send(new MapListRequest { Scope = MapListScope.Server }));
        _bindings.Bind(GameKey.F6, KeyModifiers.Shift, () => _network.Send(new MapListRequest { Scope = MapListScope.Mine }));
        _bindings.Bind(GameKey.F8, () => _network.Send(new FriendListRequest()));

        // Chat scrollback: brackets step through messages, shift-brackets through buffers.
        _bindings.Bind(GameKey.BracketLeft, () => CycleChat(-1));
        _bindings.Bind(GameKey.BracketRight, () => CycleChat(1));

        // Shell.
        _bindings.Bind(GameKey.Slash, _shell.OpenCommandConsole);
        _bindings.Bind(GameKey.NumpadDivide, _shell.OpenCommandConsole);
        _bindings.Bind(GameKey.Escape, _shell.RequestQuit);
    }

    /// <summary>
    /// Keys a screen reader owns, which nothing in gameplay may ever be bound to.
    ///
    /// CONTROL silences speech in every screen reader there is. ALT is the window manager's and opens
    /// menus. Both are pressed by a blind player dozens of times a minute as punctuation, not as
    /// input — so a game action on either is not a key that is hard to use, it is a key that fires by
    /// itself. Control was the trigger, and it cost five sessions of hunting a "random banging" that
    /// was the player's own rifle.
    ///
    /// Modified bindings are a different thing and are fine: shift-F5 is a chord somebody chose to
    /// press. What is forbidden is a screen reader's key AS the action.
    /// </summary>
    public static readonly GameKey[] ScreenReaderKeys =
        { GameKey.ControlLeft, GameKey.ControlRight, GameKey.AltLeft, GameKey.AltRight };

    /// <summary>Rebinds a key. Exposed so a head (or a future settings screen) can re-map without
    /// touching the session.</summary>
    public void Bind(InputContext context, GameKey key, Action action) => _bindings.Bind(context, key, action);

    /// <summary>Whether anything is bound to a key in a context. For a settings screen, and for the
    /// test that keeps a screen reader's keys free of game actions (see <see cref="ScreenReaderKeys"/>).</summary>
    public bool IsBound(InputContext context, GameKey key) => _bindings.IsBound(context, key);

    private void Say(string text) => _speech.Speak(text, interrupt: true);

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
        foreach (var key in justPressed) _bindings.Execute(context, key, modifiers);

        _simTime += dt;
        var input = GatherInput(held, justPressed, dt);
        if (!gameplayActive)
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
        _state.VisualOffset = Vector3.Zero;

        if (_state.IsRiding && !wasRiding) Serilog.Log.Information("Riding entity {Id}.", ridingEntityId);
        else if (!_state.IsRiding) Serilog.Log.Information("No longer riding.");
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

        // ...and everybody else, off the same snapshot. A passenger needs no exemption here the way
        // the local player does above: the server zeroes an occupant's velocity and its movement
        // system leaves their body to the seat, so a rider is a body at rest being carried, which is
        // the one thing the stride rules already refuse to call walking.
        _others.Update(snapshot, _ownEntityId);

        // Internally capped to 60 Hz; the loop this hangs off spins far faster to keep the socket
        // serviced. See ClientAudioSystem.UpdateHz.
        _audioSystem.Update(snapshot);

        // ...and only then, because the region the audio system just worked out is the one to say.
        AnnounceZoneChanges();
    }

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
    private float SnapDegrees(GameKey key, float ax, float ay)
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
                degrees = fine ? TurnFineDegrees : SnapDegrees(key, ax, ay);
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
                    _speech.Speak($"Logged in as {login.Username}. Loading world.", interrupt: true);
                    _shell.ShowLoading("Authenticated. Preparing manifest...");
                    LoginSucceeded?.Invoke(login.Username);
                }
                else
                {
                    Serilog.Log.Warning("Login rejected by the server: {Reason}", login.Message);
                    _speech.Speak($"Login failed. {login.Message}", interrupt: true);
                    LoginFailed?.Invoke(login.Message);
                }
                break;

            case MapManifest manifest:
                Serilog.Log.Information("MapManifest: {Map}, expecting {Count} entities, spawn {Spawn}.",
                    manifest.MapName, manifest.ExpectedEntityCount, manifest.SpawnPoint.Position);
                _shell.UpdateLoadingStatus($"Loading {manifest.MapName}...", 10);
                _world.Clear(manifest.WorldSize, manifest.MapMin, manifest.MapMax);
                // A new map's regions are numbered from scratch, so the last id announced describes
                // nowhere. Arriving somewhere is not crossing into it.
                _lastAnnouncedRegionId = int.MinValue;
                _others.Clear();
                // The map's authored atmosphere applies immediately: the world-state broadcast only
                // arrives once a second, and until it does the acoustics would otherwise be computed for
                // the previous map's air.
                _world.ApplyManifestAtmosphere(manifest);
                // The map's outdoor soundfield. It plays for as long as the map is loaded and is
                // ducked by shelter rather than switched off, so a doorway is a change in the world
                // rather than a boundary the world stops at.
                _audioSystem.SetMapAmbience(manifest.AmbienceId);

                _expectedEntityCount = manifest.ExpectedEntityCount;
                _voxelResolution = manifest.VoxelResolution;
                _occlusionFloor = manifest.OcclusionFloor;
                _mapMin = manifest.MapMin;

                _physics.MapMin = manifest.MapMin;
                _physics.MapMax = manifest.MapMax;
                _physics.Gravity = manifest.Gravity;

                _state.Position = manifest.SpawnPoint.Position;
                _state.Rotation = manifest.SpawnPoint.Rotation;
                _state.MinimumY = manifest.MinimumY;
                _state.MapMin = manifest.MapMin;
                _state.MapMax = manifest.MapMax;
                _state.MapSize = manifest.WorldSize;

                _network.Send(new MapDataRequest { MapName = manifest.MapName });
                break;

            case EntityDefinition def:
                _world.RegisterDefinition(def);
                if (_expectedEntityCount > 0)
                {
                    int count = _world.EntityCount;
                    int pct = 10 + (int)((float)count / _expectedEntityCount * 60);
                    _shell.UpdateLoadingStatus($"Receiving entities: {count}/{_expectedEntityCount}", pct);
                }
                break;

            case EntityRemoved removed:
                foreach (int goneId in _world.RemoveEntities(removed.EntityIds))
                {
                    _audioSystem.ForgetEntity(goneId);
                    _announcedNearby.Remove(goneId);
                }
                break;

            case MapLoadComplete:
                Serilog.Log.Information("MapLoadComplete: {Count} entity definitions received.", _world.EntityCount);
                _shell.UpdateLoadingStatus("Geometry ready. Finalizing acoustics...", 80);
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

                Serilog.Log.Information("PlayerSpawned: entity {Id} at {Pos}.", spawn.EntityId, spawn.SpawnTransform.Position);
                _shell.UpdateLoadingStatus("Entering World...", 100);
                _shell.EnterGame();
                GameJoined?.Invoke();
                _speech.Speak("You have entered the world. Use W A S D to move, J and L to turn.", interrupt: true);
                break;

            case ServerStateUpdate update:
                _world.SyncState(update);
                NoteRiding(update.RidingEntityId);
                _state.RidingControls = update.RidingEntityId >= 0 && update.RidingControls;
                foreach (var s in update.States)
                    if (s.EntityId == _ownEntityId)
                        if (_reconciler.ApplyServerCorrection(s, update.LastProcessedSequenceId, _world.GetSnapshot()))
                            _controller.Teleported();   // moved, not walked: forget the stride
                break;

            case WorldAudioEvent audioEvent:
                // Something happened somewhere and made a noise. Rendered on arrival and queued for
                // its own moment, because the parts of one event do not all happen at once: a latch
                // precedes its own impact, and a pane's glass lands a second and a half after it broke.
                _audioSystem.WorldAudio.Receive(audioEvent, OpenFPS.Common.AudioClock.Now);
                break;

            case WorldStateUpdate wsu:
                _world.UpdateAtmosphere(wsu);
                break;

            case StatsUpdate stats:
                _state.Health = stats.Health;
                _state.MaxHealth = stats.MaxHealth;
                _state.CurrentMaterial = stats.CurrentMaterial;
                _state.CurrentVariant = stats.CurrentVariant;
                // CurrentMaterial feeds the reverb bus material calculation via LocalPlayerState: when a
                // region's floor material (index 0) was not explicitly authored, this is the runtime
                // override for the underfoot surface absorption.
                _audioSystem.NotifyMaterialChange(stats.CurrentMaterial);
                break;

            case PlayerListResponse pList:
                Say("Players online: " + (pList.Players.Length > 0 ? string.Join(", ", pList.Players) : "none"));
                break;

            case FriendListResponse fList:
                Say("Friends: " + (fList.Friends.Length > 0 ? string.Join(", ", fList.Friends) : "none"));
                break;

            case MapListResponse mList:
                Say(DescribeMaps(mList));
                break;

            case TextEvent tEvent:
                _chat.AddServerMessage(tEvent.Text);
                break;

            case ChatMessage cMsg:
                _chat.AddMessage(cMsg);
                break;

            case VoiceData voice:
                // Don't play back our own transmission.
                if (voice.SenderId != _ownEntityId && voice.OpusData.Length > 0)
                    _audioSystem.PlayReceivedVoice(voice.SenderId, voice.OpusData, _world.GetSnapshot());
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

        _speech.Speak($"Generating acoustics for {snapshot.Entities.Count} entities.", interrupt: false);
        try
        {
            var map = OpenFPS.Common.Systems.AcousticVolumeGenerator.GenerateRegions(
                snapshot.Entities.Values.Select(e => e.Definition),
                _world.CurrentMapSize, _mapMin, _voxelResolution, _occlusionFloor);
            _world.SetAcousticMap(map);
            _speech.Speak("Acoustics ready.", interrupt: false);
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
    }

    // ── Actions ─────────────────────────────────────────────────────────────────

    private void Interact()
    {
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
        Vector3 eyePos = _state.Position + new Vector3(0, _state.EyeHeight, 0);

        if (_physics.Spatial.RaycastSingle(snapshot, eyePos, forward, 20.0f, out var hit, out float dist))
        {
            string name = hit.Definition.Identity.Name;
            if (string.IsNullOrEmpty(name)) name = "an object";

            string material = hit.Definition.Material.Material;
            string made = string.IsNullOrEmpty(material) || material == "Generic" ? "" : $", {material.ToLowerInvariant()}";

            Say($"{name}{made}, {dist:F1} metres ahead.");
        }
        else
        {
            Say($"Nothing ahead. {_state.CurrentRegion}, facing {_state.GetCompassDirection()}.");
        }
    }

    /// <summary>
    /// Firing what is in your hands, on a key rather than a typed command.
    ///
    /// It is still a text command on the wire, and that is the honest state of it: there is no shot
    /// message in the protocol yet, no round is resolved against what it hit, and nothing has ever
    /// decremented a health component. What this changes is only that the trigger is a trigger.
    /// </summary>
    private void Fire() => _network.Send(new TextCommand { Command = "fire" });

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
    private void AnnounceZoneChanges()
    {
        int region = _state.CurrentRegionId;
        if (region == _lastAnnouncedRegionId) return;
        _lastAnnouncedRegionId = region;

        // ...and the NAME has to have changed too, which is the other half of it.
        //
        // A place worth naming is rarely one box. A banked turn is a curve and a straight is four
        // hundred metres, so either is tiled out of several region volumes that are all the same
        // PLACE — and keying on the id alone announced "Turn one and two" four times while you
        // walked through it. Requiring the name to change as well makes crossing between two boxes
        // of one region silent, which is what a player means by not having moved.
        string name = _state.CurrentRegion;
        if (string.IsNullOrWhiteSpace(name) || name == _lastAnnouncedRegion) return;

        // The first region after arriving on a map is where you spawned, not somewhere you walked
        // into; the loading announcement has already said where you are.
        bool first = _lastAnnouncedRegion == null;
        _lastAnnouncedRegion = name;
        if (first) return;

        _speech.Speak(name, interrupt: false);
    }

    private int _lastAnnouncedRegionId = int.MinValue;
    private string? _lastAnnouncedRegion;

    /// <summary>
    /// The map list, as a sentence rather than a grid.
    ///
    /// Ordered by how many people are on each, because that is the fact a player is actually asking
    /// for: a list of names tells you what exists, and the population tells you where the game is.
    /// </summary>
    private static string DescribeMaps(MapListResponse response)
    {
        string what = response.Scope == MapListScope.Mine ? "Your maps" : "Maps on this server";
        if (response.Maps.Length == 0)
            return response.Scope == MapListScope.Mine ? "You have no maps of your own." : "No maps available.";

        var parts = new List<string>(response.Maps.Length);
        foreach (var map in response.Maps)
        {
            string people = map.PlayerCount switch
            {
                0 => "empty",
                1 => "1 player",
                _ => $"{map.PlayerCount} players",
            };
            string here = map.IsCurrent ? ", where you are" : "";
            string visibility = map.IsPublic ? "" : ", private";
            parts.Add($"{map.Id}, {people}{visibility}{here}");
        }

        return $"{what}: {string.Join("; ", parts)}.";
    }

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
            _audioSystem.PlayVoiceIndicator();
        }
        else
        {
            _microphone.Stop();
        }
    }

    private void OnVoicePacketReady(byte[] opusData) =>
        _network.Send(new VoiceData { SenderId = _ownEntityId, OpusData = opusData },
            LiteNetLib.DeliveryMethod.Unreliable);

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
            _network.Send(new TextCommand { Command = parts[0].ToLowerInvariant(), Args = parts.Skip(1).ToArray() });
        }
        else
        {
            _network.Send(new ChatMessage { Text = input });
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
        _shell.CommandEntered -= HandleCommandEntered;
        _microphone.PacketReady -= OnVoicePacketReady;
        _microphone.Dispose();
        _audioEngine.Dispose();
    }
}

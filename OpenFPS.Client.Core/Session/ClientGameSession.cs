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

        _world = new ClientWorldState();
        _state = new LocalPlayerState();
        _physics = new ClientPhysicsSystem(_state, new SpatialService());
        _reconciler = new PredictionReconciler(_state, _physics);
        _controller = new LocalPlayerController(_state);
        _sounds = new SoundMappingService(_state);
        _audioSystem = new ClientAudioSystem(_audioEngine, _sounds, _state);
        _chat = new ChatManager(_speech);

        _sounds.Initialize();
        _controller.OnStepTriggered += _audioSystem.OnPlayerFootstep;
        _controller.OnLandTriggered += _audioSystem.OnPlayerLand;

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
            () => Say($"Coordinates: {_state.Position.X:F1}, {_state.Position.Y:F1}, {_state.Position.Z:F1}"));
        _bindings.Bind(InputContext.Gameplay, GameKey.F, () => Say($"Facing: {_state.GetCompassDirection()}"));
        _bindings.Bind(InputContext.Gameplay, GameKey.H, () => Say($"Health: {_state.Health} percent"));
        _bindings.Bind(InputContext.Gameplay, GameKey.Z, () => Say($"Area: {_state.CurrentRegion}"));
        _bindings.Bind(InputContext.Gameplay, GameKey.Comma, LookAhead);

        // Interaction.
        _bindings.Bind(InputContext.Gameplay, GameKey.E, Interact);
        _bindings.Bind(InputContext.Gameplay, GameKey.Enter, Interact);
        _bindings.Bind(InputContext.Gameplay, GameKey.P, () => _network.Send(new TextCommand { Command = "scan" }));
        _bindings.Bind(InputContext.Gameplay, GameKey.I, () => _network.Send(new TextCommand { Command = "inv" }));
        _bindings.Bind(InputContext.Gameplay, GameKey.V, ToggleVoiceTransmission);

        // Social / discovery.
        _bindings.Bind(GameKey.F5, () => _network.Send(new PlayerListRequest { Scope = PlayerListScope.Server }));
        _bindings.Bind(GameKey.F6, () => _network.Send(new PlayerListRequest { Scope = PlayerListScope.Map }));
        _bindings.Bind(GameKey.F7, () => _network.Send(new FriendListRequest()));

        // Chat scrollback: brackets step through messages, shift-brackets through buffers.
        _bindings.Bind(GameKey.BracketLeft, () => CycleChat(-1));
        _bindings.Bind(GameKey.BracketRight, () => CycleChat(1));

        // Shell.
        _bindings.Bind(GameKey.Slash, _shell.OpenCommandConsole);
        _bindings.Bind(GameKey.NumpadDivide, _shell.OpenCommandConsole);
        _bindings.Bind(GameKey.Escape, _shell.RequestQuit);
    }

    /// <summary>Rebinds a key. Exposed so a head (or a future settings screen) can re-map without
    /// touching the session.</summary>
    public void Bind(InputContext context, GameKey key, Action action) => _bindings.Bind(context, key, action);

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

        // Bindings run in the context the shell reports: with a modal console open, gameplay bindings
        // must not fire, but the global ones (chat navigation, quit) still should.
        bool gameplayActive = _shell.IsGameInputActive;
        var context = gameplayActive ? InputContext.Gameplay : InputContext.UI;
        foreach (var key in justPressed) _bindings.Execute(context, key);

        var input = GatherInput(held, dt);
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

    /// <summary>Render-rate update: footstep generation + spatial audio listener/emitters.</summary>
    public void ContinuousUpdate()
    {
        if (!IsInGame) return;
        _controller.Update(_state.Position + _state.VisualOffset, _state.Velocity);
        // Internally capped to 60 Hz; the loop this hangs off spins far faster to keep the socket
        // serviced. See ClientAudioSystem.UpdateHz.
        _audioSystem.Update(_world.GetSnapshot());
    }

    private ClientInputUpdate GatherInput(HashSet<GameKey> held, float dt)
    {
        var input = new ClientInputUpdate { SequenceId = ++_sequenceId, DeltaTime = dt };

        // Suppress movement while a modifier or the console key is held: window-manager and screen-reader
        // chords must never walk the player across the map.
        if (InputStateBuffer.HasModifier(held) || held.Contains(GameKey.Slash) || held.Contains(GameKey.NumpadDivide))
            return input;

        Vector3 move = Vector3.Zero;
        if (held.Contains(GameKey.W)) move.Z += 1;
        if (held.Contains(GameKey.S)) move.Z -= 1;
        if (held.Contains(GameKey.A)) move.X -= 1;
        if (held.Contains(GameKey.D)) move.X += 1;
        if (move != Vector3.Zero) input.MoveDirection = Vector3.Normalize(move);

        if (held.Contains(GameKey.Space)) input.Jump = true;

        Vector2 look = Vector2.Zero;
        if (held.Contains(GameKey.J)) look.X -= 3;
        if (held.Contains(GameKey.L)) look.X += 3;
        if (held.Contains(GameKey.K)) look.Y -= 3;
        if (held.Contains(GameKey.O)) look.Y += 3;
        input.LookDelta = look;

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
                }
                else
                {
                    _speech.Speak($"Login failed. {login.Message}", interrupt: true);
                }
                break;

            case MapManifest manifest:
                Serilog.Log.Information("MapManifest: {Map}, expecting {Count} entities, spawn {Spawn}.",
                    manifest.MapName, manifest.ExpectedEntityCount, manifest.SpawnPoint.Position);
                _shell.UpdateLoadingStatus($"Loading {manifest.MapName}...", 10);
                _world.Clear(manifest.WorldSize, manifest.MapMin, manifest.MapMax);
                // The map's authored atmosphere applies immediately: the world-state broadcast only
                // arrives once a second, and until it does the acoustics would otherwise be computed for
                // the previous map's air.
                _world.ApplyManifestAtmosphere(manifest);

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
                Task.Run(GenerateAcoustics);
                break;

            case PlayerSpawned spawn:
                _ownEntityId = spawn.EntityId;
                _physics.OwnEntityId = spawn.EntityId;
                _physics.Spatial.OwnEntityId = spawn.EntityId; // ignore self in prediction/raycasts
                _audioSystem.OwnEntityId = spawn.EntityId;

                _state.Position = spawn.SpawnTransform.Position;
                _state.Rotation = spawn.SpawnTransform.Rotation;
                _state.Velocity = Vector3.Zero;
                _reconciler.Reset(); // CRITICAL: reset the prediction buffer on teleport/spawn

                Serilog.Log.Information("PlayerSpawned: entity {Id} at {Pos}.", spawn.EntityId, spawn.SpawnTransform.Position);
                _shell.UpdateLoadingStatus("Entering World...", 100);
                _shell.EnterGame();
                GameJoined?.Invoke();
                _speech.Speak("You have entered the world. Use W A S D to move, J and L to turn.", interrupt: true);
                break;

            case ServerStateUpdate update:
                _world.SyncState(update);
                foreach (var s in update.States)
                    if (s.EntityId == _ownEntityId)
                        _reconciler.ApplyServerCorrection(s, update.LastProcessedSequenceId, _world.GetSnapshot());
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

    private void LookAhead()
    {
        var snapshot = _world.GetSnapshot();
        Vector3 forward = Vector3.Transform(new Vector3(0, 0, 1), _state.Rotation);
        Vector3 eyePos = _state.Position + new Vector3(0, 1.7f, 0);

        if (_physics.Spatial.RaycastSingle(snapshot, eyePos, forward, 20.0f, out var hit, out float dist))
        {
            string name = hit.Definition.Identity.Name;
            if (string.IsNullOrEmpty(name)) name = "an object";
            Say($"{name}, {dist:F1} meters ahead.");
        }
        else
        {
            Say("Nothing directly ahead.");
        }
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
        Vector3 visualEyePos = _state.VisualPosition + new Vector3(0, 1.7f, 0);

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

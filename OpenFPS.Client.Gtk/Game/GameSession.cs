using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Client.Core;
using OpenFPS.Client.Core.Platform;
using OpenFPS.Client.Services;            // SoundMappingService (shared, in Core)
using OpenFPS.Client.AudioEngine.Core;    // AudioEngineFacade

namespace OpenFPS.Client.Gtk.Game;

/// <summary>
/// The GTK head's client-side game logic — the Linux counterpart to the Windows
/// <c>ClientSimulationSystem</c>. It owns the shared Core systems (world state, prediction,
/// footstep controller, audio) and drives them from network messages + keyboard input, speaking
/// status and accessibility readouts through <see cref="ISpeechOutput"/>.
///
/// Threading: <see cref="HandleMessage"/> and <see cref="Tick"/> are both called from the single
/// game-loop thread (which also pumps <c>ClientNetworkService.Poll</c>), so player state needs no
/// extra locking. The GTK main thread only touches <see cref="Input"/> (thread-safe) and never
/// reads game state directly.
/// </summary>
public sealed class GameSession
{
    private readonly ClientNetworkService _network;
    private readonly ISpeechOutput _speech;

    private readonly ClientWorldState _world = new();
    private readonly LocalPlayerState _state = new();
    private readonly ClientPhysicsSystem _physics;
    private readonly LocalPlayerController _controller;
    private readonly AudioEngineFacade _audioEngine;
    private readonly SoundMappingService _sounds;
    private readonly ClientAudioSystem _audioSystem;

    private readonly PredictionReconciler _reconciler;
    private long _sequenceId = 0;
    private int _ownEntityId = -1;
    private int _expectedEntityCount = 0;

    // Map metadata captured from the manifest, needed when acoustics are generated later.
    private float _voxelResolution = 0.5f;
    private float _occlusionFloor = 0.2f;
    private Vector3 _mapMin;

    /// <summary>Raised (on the game-loop thread) once the local player has spawned into the world.</summary>
    public event Action? GameJoined;

    public bool IsInGame => _ownEntityId != -1;
    public GameInput Input { get; } = new();
    public bool AudioReady => _audioEngine.IsInitialized;

    private readonly bool _enableAudio;

    public GameSession(ClientNetworkService network, ISpeechOutput speech, bool enableAudio)
    {
        _network = network;
        _speech = speech;
        _enableAudio = enableAudio;

        // Initialize the material registry FIRST — before the audio system starts its acoustic worker
        // thread (which reads/uses the registry). Initialize() is thread-safe, but doing it up front keeps
        // ordering deterministic and avoids redundant concurrent rebuilds.
        AcousticRegistry.Initialize();

        _physics = new ClientPhysicsSystem(_state, new SpatialService());
        _reconciler = new PredictionReconciler(_state, _physics);
        _controller = new LocalPlayerController(_state);
        _audioEngine = new AudioEngineFacade();
        _sounds = new SoundMappingService(_audioEngine, _state);
        _audioSystem = new ClientAudioSystem(_audioEngine, _sounds, _state);

        _sounds.Initialize();
        // NOTE: audio engine init (FMOD + voices + asset bank) is deferred to BeginAudioInit() on a
        // background thread. It must NOT run here: GameSession is constructed on the network/game-loop
        // thread inside the LoginResponse handler, and a synchronous audio init would block that thread
        // from pumping the next messages (MapManifest, …) — stalling the world-load handshake.
        Serilog.Log.Information("GameSession created. Audio requested={Req}.", enableAudio);

        _controller.OnStepTriggered += _audioSystem.OnPlayerFootstep;
        _controller.OnLandTriggered += _audioSystem.OnPlayerLand;
    }

    /// <summary>Starts audio-engine initialization on a dedicated background thread so FMOD / asset
    /// loading never blocks the network/game-loop thread. Audio is only used after spawn, by which time
    /// this has completed; until then the provider's calls no-op. Safe to call once, right after construction.</summary>
    public void BeginAudioInit()
    {
        if (!_enableAudio) return;
        new Thread(() =>
        {
            try { _audioEngine.Initialize(); Serilog.Log.Information("Audio engine initialized (background): {Init}.", _audioEngine.IsInitialized); }
            catch (Exception ex) { Serilog.Log.Error(ex, "Audio engine init failed."); }
        }) { IsBackground = true, Name = "AudioInit" }.Start();
    }

    // ── Network message handling (game-loop thread) ─────────────────────────────
    public void HandleMessage(IMessage msg)
    {
        switch (msg)
        {
            case MapManifest manifest:
                Serilog.Log.Information("MapManifest: {Map}, expecting {Count} entities, spawn {Spawn}.",
                    manifest.MapName, manifest.ExpectedEntityCount, manifest.SpawnPoint.Position);
                _speech.Speak($"Loading map {manifest.MapName}.");
                _world.Clear(manifest.WorldSize, manifest.MapMin, manifest.MapMax);
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
                break;

            case EntityRemoved removed:
                foreach (int goneId in _world.RemoveEntities(removed.EntityIds))
                    _audioSystem.ForgetEntity(goneId);
                break;

            case MapLoadComplete:
                Serilog.Log.Information("MapLoadComplete: {Count} entity definitions received.", _world.GetSnapshot().Entities.Count);
                _speech.Speak("Geometry received. Generating acoustics.");
                Task.Run(GenerateAcoustics);
                break;

            case PlayerSpawned spawn:
                _ownEntityId = spawn.EntityId;
                _physics.OwnEntityId = spawn.EntityId;
                _physics.Spatial.OwnEntityId = spawn.EntityId;
                _audioSystem.OwnEntityId = spawn.EntityId;

                _state.Position = spawn.SpawnTransform.Position;
                _state.Rotation = spawn.SpawnTransform.Rotation;
                _state.Velocity = Vector3.Zero;
                _reconciler.Reset();

                Serilog.Log.Information("PlayerSpawned: entity {Id} at {Pos}.", spawn.EntityId, spawn.SpawnTransform.Position);
                GameJoined?.Invoke();
                _speech.Speak("You have entered the world. Use W A S D to move, J and L to turn.", true);
                break;

            case ServerStateUpdate update:
                _world.SyncState(update);
                foreach (var s in update.States)
                    if (s.EntityId == _ownEntityId) ApplyServerCorrection(s, update.LastProcessedSequenceId);
                break;

            case WorldStateUpdate wsu:
                _world.UpdateAtmosphere(wsu);
                break;

            case StatsUpdate stats:
                _state.Health = stats.Health;
                _state.MaxHealth = stats.MaxHealth;
                _state.CurrentMaterial = stats.CurrentMaterial;
                _state.CurrentVariant = stats.CurrentVariant;
                _audioSystem.NotifyMaterialChange(stats.CurrentMaterial);
                break;

            case PlayerListResponse pList:
                _speech.Speak("Players online: " + (pList.Players.Length > 0 ? string.Join(", ", pList.Players) : "none"));
                break;

            case TextEvent tEvent:
                _speech.Speak(tEvent.Text);
                break;

            case ChatMessage cMsg:
                _speech.Speak($"{cMsg.Sender} says: {cMsg.Text}");
                break;
        }
    }

    private void GenerateAcoustics()
    {
        var snapshot = _world.GetSnapshot();
        int timeout = 0;
        while (snapshot.Entities.Count < _expectedEntityCount && timeout < 40)
        {
            Thread.Sleep(100);
            snapshot = _world.GetSnapshot();
            timeout++;
        }

        try
        {
            var map = OpenFPS.Common.Systems.AcousticVolumeGenerator.GenerateRegions(
                snapshot.Entities.Values.Select(e => e.Definition),
                _world.CurrentMapSize, _mapMin, _voxelResolution, _occlusionFloor);
            _world.SetAcousticMap(map);
            _speech.Speak("Acoustics ready.");
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Acoustic generation failed.");
            _speech.Speak("Acoustic generation failed.");
        }

        // Tell the server we're done loading; it responds by spawning us (-> PlayerSpawned).
        // This MUST be sent here, not in the PlayerSpawned handler — the spawn is the server's
        // reply to "ready", so sending it later would deadlock the handshake.
        Serilog.Log.Information("Acoustics done; sending ready.");
        _network.Send(new TextCommand { Command = "ready" });
    }

    // ── Per-frame simulation (game-loop thread) ─────────────────────────────────

    /// <summary>One fixed-timestep simulation step: gather input, predict, reconcile-driven send.</summary>
    public void SimStep(float dt)
    {
        if (!IsInGame) return;

        var (held, justPressed) = Input.GetSnapshot();
        HandleActionKeys(justPressed);

        var input = GatherInput(held, dt);
        _reconciler.Step(input, _world.GetSnapshot(), dt);

        _world.UpdateInterpolation(dt, _ownEntityId);

        // Bleed the reconciliation visual offset toward zero so server snaps don't pop.
        if (_state.VisualOffset.LengthSquared() > 0.0001f)
            _state.VisualOffset = Vector3.Lerp(_state.VisualOffset, Vector3.Zero, 5.0f * dt);
        else
            _state.VisualOffset = Vector3.Zero;

        if (input.MoveDirection != Vector3.Zero || input.LookDelta != Vector2.Zero || input.Jump || _sequenceId % 10 == 0)
            _network.Send(input);
    }

    /// <summary>Render-rate update: footstep generation + spatial audio listener/emitters.</summary>
    public void ContinuousUpdate()
    {
        if (!IsInGame) return;
        _controller.Update(_state.Position + _state.VisualOffset, _state.Velocity);
        _audioSystem.Update(_world.GetSnapshot());
    }

    private ClientInputUpdate GatherInput(HashSet<GameKey> held, float dt)
    {
        var input = new ClientInputUpdate { SequenceId = ++_sequenceId, DeltaTime = dt };

        // Suppress movement while a modifier or the command key is held (parity with Windows head).
        if (HasModifier(held) || held.Contains(GameKey.Slash)) return input;

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

    private static bool HasModifier(HashSet<GameKey> held) =>
        held.Contains(GameKey.ShiftLeft) || held.Contains(GameKey.ShiftRight) ||
        held.Contains(GameKey.ControlLeft) || held.Contains(GameKey.ControlRight) ||
        held.Contains(GameKey.AltLeft) || held.Contains(GameKey.AltRight);

    // Accessibility / interaction keys — single-shot on press.
    private void HandleActionKeys(HashSet<GameKey> justPressed)
    {
        if (justPressed.Count == 0) return;

        if (justPressed.Contains(GameKey.C))
            _speech.Speak($"Coordinates: {_state.Position.X:F1}, {_state.Position.Y:F1}, {_state.Position.Z:F1}");
        if (justPressed.Contains(GameKey.F))
            _speech.Speak($"Facing: {_state.GetCompassDirection()}");
        if (justPressed.Contains(GameKey.H))
            _speech.Speak($"Health: {_state.Health} percent");
        if (justPressed.Contains(GameKey.Z))
            _speech.Speak($"Area: {_state.CurrentRegion}");
        if (justPressed.Contains(GameKey.Comma))
            LookAhead();
        if (justPressed.Contains(GameKey.E) || justPressed.Contains(GameKey.Enter))
        {
            var targetId = _world.GetClosestEntityId(_state.Position);
            if (targetId.HasValue)
                _network.Send(new InteractRequest { Action = "interact", TargetEntityId = targetId.Value });
        }
        if (justPressed.Contains(GameKey.P)) _network.Send(new TextCommand { Command = "scan" });
        if (justPressed.Contains(GameKey.I)) _network.Send(new TextCommand { Command = "inv" });
        if (justPressed.Contains(GameKey.F5)) _network.Send(new PlayerListRequest { Scope = PlayerListScope.Server });
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
            _speech.Speak($"{name}, {dist:F1} meters ahead.");
        }
        else
        {
            _speech.Speak("Nothing directly ahead.");
        }
    }

    private void ApplyServerCorrection(EntityState serverState, long lastProcessedId) =>
        _reconciler.ApplyServerCorrection(serverState, lastProcessedId, _world.GetSnapshot());

    public void Shutdown() => _audioEngine.Dispose();
}

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

    private readonly List<ClientInputUpdate> _history = new();
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

    public GameSession(ClientNetworkService network, ISpeechOutput speech, bool enableAudio)
    {
        _network = network;
        _speech = speech;
        _physics = new ClientPhysicsSystem(_state, new SpatialService());
        _controller = new LocalPlayerController(_state);
        _audioEngine = new AudioEngineFacade();
        _sounds = new SoundMappingService(_audioEngine, _state);
        _audioSystem = new ClientAudioSystem(_audioEngine, _sounds, _state);

        AcousticRegistry.Initialize();
        _sounds.Initialize();
        if (enableAudio) _audioEngine.Initialize();

        _controller.OnStepTriggered += _audioSystem.OnPlayerFootstep;
        _controller.OnLandTriggered += _audioSystem.OnPlayerLand;
    }

    // ── Network message handling (game-loop thread) ─────────────────────────────
    public void HandleMessage(IMessage msg)
    {
        switch (msg)
        {
            case MapManifest manifest:
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

            case MapLoadComplete:
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
                _history.Clear();

                _network.Send(new TextCommand { Command = "ready" });
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
    }

    // ── Per-frame simulation (game-loop thread) ─────────────────────────────────

    /// <summary>One fixed-timestep simulation step: gather input, predict, reconcile-driven send.</summary>
    public void SimStep(float dt)
    {
        if (!IsInGame) return;

        var (held, justPressed) = Input.GetSnapshot();
        HandleActionKeys(justPressed);

        var input = GatherInput(held, dt);
        _physics.Predict(input, _world.GetSnapshot(), dt);
        _history.Add(input);

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

    private void ApplyServerCorrection(EntityState serverState, long lastProcessedId)
    {
        Vector3 predictedPos = _state.Position;

        var transform = serverState.Transform.ToTransform();
        _state.Position = transform.Position;
        _state.Velocity = serverState.LinearVelocity;

        _history.RemoveAll(i => i.SequenceId <= lastProcessedId);

        var snap = _world.GetSnapshot();
        foreach (var input in _history)
            _physics.Predict(input, snap, input.DeltaTime);

        _state.VisualOffset = predictedPos - _state.Position;
        if (_state.VisualOffset.Length() > 5.0f) _state.VisualOffset = Vector3.Zero;
    }

    public void Shutdown() => _audioEngine.Dispose();
}

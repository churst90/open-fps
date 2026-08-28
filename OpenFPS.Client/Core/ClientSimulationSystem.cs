using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Windows.Forms;
using System.Threading.Tasks;
using System.Threading;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Client.Services;
using OpenFPS.Client.UI;

namespace OpenFPS.Client.Core;

/// <summary>
/// Responsibility: The heart of the client-side game logic. 
/// It manages network messages, physics prediction, and coordinates the high-level systems.
/// </summary>
public class ClientSimulationSystem
{
    private readonly ClientNetworkService _network;
    private readonly ClientWorldState _world;
    private readonly LocalPlayerState _state;
    private readonly ClientPhysicsSystem _physics;
    private readonly TolkService _tts;
    private readonly InputHandler _inputHandler;
    
    private ClientAudioSystem? _audioSystem;
    private ClientNavigationService? _navigation;
    private readonly OpenFPS.Client.Services.VoiceCapture _voiceCapture = new();

    private int _ownEntityId = -1;
    private long _sequenceId = 0;
    private int _expectedEntityCount = 0;

    // Prediction history + reconciliation (shared with the GTK head).
    private readonly PredictionReconciler _reconciler;

    // Interaction proximity tracking
    private readonly HashSet<int> _announcedNearby = new();
    private int _proximityCheckCounter = 0;
    private const float InteractionRadius = 3.0f;

    public int OwnEntityId => _ownEntityId;
    public bool IsInGame => _ownEntityId != -1;

    public event Action? OnGameJoined;

    public ClientSimulationSystem(ClientNetworkService net, ClientWorldState world, LocalPlayerState state, TolkService tts)
    {
        _network = net;
        _world = world;
        _state = state;
        _tts = tts;
        _physics = new ClientPhysicsSystem(state, new SpatialService());
        _reconciler = new PredictionReconciler(state, _physics);
        _inputHandler = new InputHandler(tts, net, state, world);
    }

    public void SetNavigation(ClientNavigationService nav) => _navigation = nav;
    public InputHandler GetInputHandler() => _inputHandler;

    public void SetAudioSystem(ClientAudioSystem audio)
    {
        _audioSystem = audio;
        // Wire voice capture → encode → send to server
        _voiceCapture.OnPacketReady += opusData =>
            _network.Send(new VoiceData { SenderId = _ownEntityId, OpusData = opusData },
                LiteNetLib.DeliveryMethod.Unreliable);
    }

    public void Update(HashSet<Keys> heldKeys, HashSet<Keys> justPressed, float dt)
    {
        if (!IsInGame) return;

        // 1. Process Functional Keys (Social, UI, etc)
        _inputHandler.ProcessInput(heldKeys, justPressed);

        // 2. Gather Movement Input (Suppress if not focused)
        var cleanKeys = new HashSet<Keys>(heldKeys.Select(k => k & Keys.KeyCode));
        var input = GatherInput(cleanKeys, dt);

        if (!_inputHandler.IsFocused())
        {
            input.MoveDirection = Vector3.Zero;
            input.Jump = false;
            input.LookDelta = Vector2.Zero;
        }

        // 3. Remote Interpolation FIRST, then everything that reads the world reads one snapshot.
        // Interpolation is the only step in the tick that mutates the world, and it touches only remote
        // entities (it skips the local player, whose position prediction owns). Advancing it before the
        // readers rather than between two of them means prediction, the shelter raycast, the proximity
        // scan and the audio system all share a single snapshot build instead of forcing a second one.
        _world.UpdateInterpolation(dt, _ownEntityId);
        var snapshot = _world.GetSnapshot();

        // 3.5. Client-Side Prediction (CSP)
        _reconciler.Step(input, snapshot, dt);

        // 3.6. Update Environmental Shelter (Roof check + Indoor Region check)
        UpdateShelterFactor(dt, snapshot);
        
        // 3.7. Localize Atmospheric Effects (Scale precipitation by shelter)
        _state.PrecipitationIntensity = _world.CurrentPrecipitation * (1.0f - _state.ShelterFactor);

        // 4. Smoothing: Gently reduce the visual position offset over time
        if (_state.VisualOffset.LengthSquared() > 0.0001f)
        {
            float smoothingFactor = 5.0f * dt; // Converge over roughly 0.2s
            _state.VisualOffset = Vector3.Lerp(_state.VisualOffset, Vector3.Zero, smoothingFactor);
        }
        else
        {
            _state.VisualOffset = Vector3.Zero;
        }

        // 5. Network sync
        if (input.MoveDirection != Vector3.Zero || input.LookDelta != Vector2.Zero || input.Jump || _sequenceId % 10 == 0)
            _network.Send(input);

        // 6. Interactable proximity scan (~1 per second)
        CheckInteractableProximity();

        // 7. Voice transmission toggle (V key — press once to start, press again to stop)
        bool vJustPressed = justPressed.Any(k => (k & Keys.KeyCode) == Keys.V) && _inputHandler.IsFocused();
        if (vJustPressed)
        {
            if (!_voiceCapture.IsCapturing)
            {
                _voiceCapture.StartCapture();
                _audioSystem?.PlayVoiceIndicator();
            }
            else
            {
                _voiceCapture.StopCapture();
            }
        }
    }

    /// <summary>
    /// Scans nearby entities and announces newly-entered interactables via TTS.
    /// Runs at roughly 1Hz to avoid spamming the screen reader.
    /// </summary>
    private void CheckInteractableProximity()
    {
        if (++_proximityCheckCounter % PhysicsConstants.TickRate != 0) return;

        var snap = _world.GetSnapshot();
        var currentNearby = new HashSet<int>();

        foreach (var entity in snap.Entities.Values)
        {
            if (entity.Id == _ownEntityId) continue;
            if (entity.Definition.Type == EntityType.Player) continue;

            string name = entity.Definition.Identity.Name;
            if (string.IsNullOrWhiteSpace(name)) continue;

            float dist = Vector3.Distance(_state.Position, entity.Transform.Position);
            if (dist > InteractionRadius) continue;

            currentNearby.Add(entity.Id);

            if (_announcedNearby.Contains(entity.Id)) continue;

            // Announce on first entry into range
            string desc = entity.Definition.Identity.Description;
            string announcement = string.IsNullOrWhiteSpace(desc)
                ? name
                : $"{name}. {desc}";
            _tts.Speak(announcement);
        }

        _announcedNearby.Clear();
        foreach (var id in currentNearby) _announcedNearby.Add(id);
    }

    private ClientInputUpdate GatherInput(HashSet<Keys> held, float dt)
    {
        var input = new ClientInputUpdate { SequenceId = ++_sequenceId, DeltaTime = dt };
        
        // Suppress movement if window not focused or command mode active
        if (Control.ModifierKeys != Keys.None || held.Contains(Keys.OemQuestion) || held.Contains(Keys.Divide))
            return input;

        Vector3 move = Vector3.Zero;
        if (held.Contains(Keys.W)) move.Z += 1;
        if (held.Contains(Keys.S)) move.Z -= 1;
        if (held.Contains(Keys.A)) move.X -= 1;
        if (held.Contains(Keys.D)) move.X += 1;
        
        if (move != Vector3.Zero) input.MoveDirection = Vector3.Normalize(move);
        if (held.Contains(Keys.Space)) input.Jump = true;

        Vector2 look = Vector2.Zero;
        if (held.Contains(Keys.J)) look.X -= 3;
        if (held.Contains(Keys.L)) look.X += 3;
        if (held.Contains(Keys.K)) look.Y -= 3;
        if (held.Contains(Keys.O)) look.Y += 3;
        input.LookDelta = look;

        return input;
    }

    private float _currentVoxelResolution = 0.5f;
    private float _currentOcclusionFloor = 0.2f;
    private Vector3 _currentMapMin;

    public void HandleMessage(IMessage msg)
    {
        switch (msg)
        {
            case LoginResponse login: 
                if (login.Success) _navigation?.ShowLoading("Authenticated. Preparing manifest...");
                else _tts.Speak(login.Message);
                break;

            case MapManifest manifest:
                _navigation?.UpdateLoadingStatus($"Loading {manifest.MapName}...", 10);
                _world.Clear(manifest.WorldSize, manifest.MapMin, manifest.MapMax);
                _expectedEntityCount = manifest.ExpectedEntityCount;
                _currentVoxelResolution = manifest.VoxelResolution;
                _currentOcclusionFloor = manifest.OcclusionFloor;
                _currentMapMin = manifest.MapMin;

                _physics.MapMin = manifest.MapMin;
                _physics.MapMax = manifest.MapMax;
                _physics.Gravity = manifest.Gravity;

                _state.Position = manifest.SpawnPoint.Position;                _state.Rotation = manifest.SpawnPoint.Rotation;
                _state.MinimumY = manifest.MinimumY;
                _state.MapMin = manifest.MapMin;
                _state.MapMax = manifest.MapMax;
                _state.MapSize = manifest.WorldSize;
                
                _network.Send(new MapDataRequest { MapName = manifest.MapName });
                break;

            case EntityDefinition def: 
                _world.RegisterDefinition(def); 
                int count = _world.EntityCount;
                if (_expectedEntityCount > 0)
                {
                    int pct = 10 + (int)((float)count / _expectedEntityCount * 60);
                    _navigation?.UpdateLoadingStatus($"Receiving entities: {count}/{_expectedEntityCount}", pct);
                }
                break;

            case EntityRemoved removed:
                foreach (int goneId in _world.RemoveEntities(removed.EntityIds))
                    _audioSystem?.ForgetEntity(goneId);
                break;

            case MapLoadComplete:
                _navigation?.UpdateLoadingStatus("Geometry ready. Finalizing acoustics...", 80);
                Task.Run(() => {
                    int timeout = 0;
                    while (_world.EntityCount < _expectedEntityCount && timeout < 40) 
                    {
                        Thread.Sleep(100);
                        timeout++;
                    }
                    var snapshot = _world.GetSnapshot();

                    _tts.Speak($"Generating acoustics for {snapshot.Entities.Count} entities.");
                    
                    try {
                        var map = OpenFPS.Common.Systems.AcousticVolumeGenerator.GenerateRegions(
                            snapshot.Entities.Values.Select(e => e.Definition), 
                            _world.CurrentMapSize, 
                            _currentMapMin,
                            _currentVoxelResolution, 
                            _currentOcclusionFloor);
                        
                        _world.SetAcousticMap(map);
                        _tts.Speak("Acoustics ready.");
                    } catch (Exception ex) {
                        _tts.Speak("Acoustic generation failed.");
                        Serilog.Log.Error(ex, "Acoustic generation failed.");
                    }
                    
                    _network.Send(new TextCommand { Command = "ready" });
                });
                break;

            case PlayerSpawned spawn:
                _ownEntityId = spawn.EntityId;
                _physics.OwnEntityId = spawn.EntityId; 
                _physics.Spatial.OwnEntityId = spawn.EntityId; // Set this to ignore self in prediction/raycasts
                if (_audioSystem != null) _audioSystem.OwnEntityId = spawn.EntityId; 
                
                _state.Position = spawn.SpawnTransform.Position;
                _state.Rotation = spawn.SpawnTransform.Rotation;
                _state.Velocity = Vector3.Zero;
                _reconciler.Reset(); // CRITICAL: Reset prediction buffer on teleport/spawn
                
                _navigation?.UpdateLoadingStatus("Entering World...", 100);
                OnGameJoined?.Invoke();
                break;

            case ServerStateUpdate update:
                _world.SyncState(update);
                foreach (var s in update.States)
                {
                    if (s.EntityId == _ownEntityId) ApplyServerCorrection(s, update.LastProcessedSequenceId);
                }
                break;                
            case WorldStateUpdate wsu:
                _world.UpdateAtmosphere(wsu);
                break;

            case PlayerListResponse pList:
                _tts.Speak("Players online: " + (pList.Players.Length > 0 ? string.Join(", ", pList.Players) : "None"));
                break;
                
            case FriendListResponse fList:
                _tts.Speak("Friends: " + (fList.Friends.Length > 0 ? string.Join(", ", fList.Friends) : "None"));
                break;
                
            case StatsUpdate stats:
                _state.Health = stats.Health;
                _state.MaxHealth = stats.MaxHealth;
                _state.CurrentMaterial = stats.CurrentMaterial;
                _state.CurrentVariant = stats.CurrentVariant;
                // CurrentMaterial feeds into the reverb bus material calculation via LocalPlayerState.
                // FmodAudioProvider reads RoomMaterials[] from the acoustic region — if the region's
                // floor material (index 0) was not explicitly authored, CurrentMaterial acts as the
                // runtime override for the underfoot surface absorption.
                if (_audioSystem != null)
                    _audioSystem.NotifyMaterialChange(stats.CurrentMaterial);
                break;

            case TextEvent tEvent:
                _inputHandler.Chat.AddServerMessage(tEvent.Text);
                break;
                
            case ChatMessage cMsg:
                _inputHandler.Chat.AddMessage(cMsg);
                break;

            case VoiceData voice:
                // Don't play back our own voice transmission
                if (voice.SenderId != _ownEntityId && voice.OpusData.Length > 0)
                    _audioSystem?.PlayReceivedVoice(voice.SenderId, voice.OpusData, _world.GetSnapshot());
                break;
        }
    }

    /// <summary>
    /// Calculates the ShelterFactor (0.0 to 1.0) based on regional data and vertical raycasting.
    /// ShelterFactor reduces precipitation rendering and environmental ambient loops.
    /// </summary>
    private void UpdateShelterFactor(float dt, WorldSnapshot snap)
    {
        bool isSheltered = false;
        
        // Use the same eye position logic as the audio system for consistency
        Vector3 visualEyePos = _state.VisualPosition + new Vector3(0, 1.7f, 0);

        // 1. Regional Check: Are we in an explicitly marked "Indoor" region?
        if (snap.AcousticMap != null)
        {
            int regionId = _physics.Spatial.GetRegionAt(snap, visualEyePos);
            if (regionId != AcousticConstants.GlobalRegionId && snap.AcousticMap.Regions.TryGetValue(regionId, out var region))
            {
                isSheltered = region.IsIndoor;
            }
        }

        float targetShelter = isSheltered ? 1.0f : 0.0f;

        // 2. Volumetric Sky-Visibility Check: Cast a 16-ray hemisphere upward
        if (!isSheltered)
        {
            int rays = 16;
            int hits = 0;
            float goldenRatio = (1 + MathF.Sqrt(5)) / 2;
            
            for (int i = 0; i < rays; i++)
            {
                // Fibonacci spiral on hemisphere
                float theta = 2 * MathF.PI * i / goldenRatio;
                float phi = MathF.Acos(1 - (float)i / rays); // Only upper hemisphere

                Vector3 dir = new Vector3(
                    MathF.Cos(theta) * MathF.Sin(phi),
                    MathF.Cos(phi), // Upward Y
                    MathF.Sin(theta) * MathF.Sin(phi)
                );

                if (_physics.Spatial.RaycastSingle(snap, visualEyePos, dir, AcousticConstants.ShelterRayDistance, out _, out _))
                {
                    hits++;
                }
            }
            
            targetShelter = (float)hits / rays;
        }

        // 3. Smooth the transition to avoid audio/visual popping
        _state.ShelterFactor = MathHelper.Lerp(_state.ShelterFactor, targetShelter, AcousticConstants.ShelterFadeSpeed * dt);
    }

    /// <summary>
    /// Re-simulates the player's path starting from a verified server state, reconciling both the
    /// position and the look angles. See <see cref="PredictionReconciler"/>.
    /// </summary>
    private void ApplyServerCorrection(EntityState serverState, long lastProcessedId) =>
        _reconciler.ApplyServerCorrection(serverState, lastProcessedId, _world.GetSnapshot());
}

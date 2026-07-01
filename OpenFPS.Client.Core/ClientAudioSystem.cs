using System;
using System.Numerics;
using System.Collections.Generic;
using System.Linq;
using Concentus;
using Concentus.Enums;
using OpenFPS.Client.Services;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Client.AudioEngine.Acoustics;

namespace OpenFPS.Client.Core;

/// <summary>
/// Responsibility: The bridge between the high-level Game World and the low-level Audio Engine.
/// It translates physical entity state (positions, materials) into acoustic emitters.
/// </summary>
public class ClientAudioSystem
{
    private readonly AudioEngineFacade _audio;
    private readonly SoundMappingService _sounds;
    private readonly LocalPlayerState _state;
    private readonly SpatialService _spatial; 
    private readonly SpatialAcoustics _acoustics;
    private readonly AsyncAcousticWorker _acousticWorker;
    private readonly HashSet<string> _preloadedSounds = new();

    /// <summary>
    /// Set by the simulation system when the player spawns.
    /// Propagates to the shared SpatialService so self-entity is excluded from occlusion raycasts.
    /// </summary>
    private int _ownEntityId = -1;
    public int OwnEntityId
    {
        get => _ownEntityId;
        set { _ownEntityId = value; _spatial.OwnEntityId = value; }
    }
    
    private WorldSnapshot? _lastSnapshot;
    private AcousticMap? _lastAcousticMap;
    private int _frameCount = 0;

    public ClientAudioSystem(AudioEngineFacade audio, SoundMappingService sounds, LocalPlayerState state)
    {
        _audio = audio;
        _sounds = sounds;
        _state = state;
        _spatial = new SpatialService();
        _acoustics = new SpatialAcoustics(_spatial); // Share the same SpatialService instance
        _acousticWorker = new AsyncAcousticWorker(_acoustics);
        _acousticWorker.Start();
    }

    /// <summary>
    /// Primary entry point called every frame from the Game Loop.
    /// Uses the VisualPosition for the listener to ensure smooth audio during server corrections.
    /// </summary>
    public void Update(WorldSnapshot world)
    {
        _frameCount++;
        _lastSnapshot = world;
        _acousticWorker.UpdateWorld(world);
        
        // --- Use smoothed VisualPosition for the listener ---
        Vector3 visualEyePos = _state.VisualPosition + new Vector3(0, 1.7f, 0);

        // 1. Resolve high-precision listener region (OBB check)
        int listenerRegionId = _acoustics.GetRegionAt(world, visualEyePos);

        // 2. Synchronize the listener's smoothed physical state.
        // Include a fraction of wind velocity so moving air produces subtle Doppler on distant sounds.
        Vector3 listenerVelocity = _state.Velocity + _state.WindVelocity * 0.1f;
        _audio.UpdateListener(visualEyePos, _state.Rotation, listenerVelocity, listenerRegionId);
        _audio.UpdateShelter(_state.ShelterFactor);

        // Geometry-driven reverb (Phase 4d): drive the listener-region reverb decay from the Steam Audio
        // reflection sim when available (replaces the Sabine estimate for the room the listener is in).
        if (_acousticWorker.TryGetListenerReverbDecayMs(out float simReverbMs))
            _audio.SetSimulatedReverbDecay(simReverbMs);
        
        // 3. Synchronize the acoustic map ONLY if it changed (optimization)
        if (world.AcousticMap != null)
        {
            if (world.AcousticMap != _lastAcousticMap)
            {
                _audio.SetAcousticMap(world.AcousticMap);
                _lastAcousticMap = world.AcousticMap;
            }
            else
            {
                // 3.5 Check for moving regions within the same map (Dynamic Geometry Updates)
                foreach(var snap in world.Entities.Values)
                {
                    if (snap.Definition.Region.RoomSize.X > 0)
                    {
                        if (_lastSnapshot != null && _lastSnapshot.Entities.TryGetValue(snap.Id, out var oldSnap))
                        {
                            if (Vector3.Distance(snap.Transform.Position, oldSnap.Transform.Position) > 0.1f || 
                                Math.Abs(Quaternion.Dot(snap.Transform.Rotation, oldSnap.Transform.Rotation)) < 0.999f)
                            {
                                OpenFPS.Common.Systems.AcousticVolumeGenerator.UpdateRegion(world.AcousticMap, snap.Id, snap.Transform.Position, snap.Transform.Rotation, snap.Definition.Region);
                            }
                        }
                    }
                }
            }
        }
        
        // 4. Update the local player's environmental state
        UpdateAcousticState(world, visualEyePos, listenerRegionId);

        // 4.5. Near-field proximity radar (Head-to-wall pressure simulation)
        float closestWallDist = 2.0f;
        Vector3[] radarDirs = { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ };
        _spatial.RaycastAll(world, visualEyePos, radarDirs, 2.0f, out float[] pDists, out _, out _);
        
        for (int i = 0; i < radarDirs.Length; i++)
        {
            if (pDists[i] < closestWallDist) closestWallDist = pDists[i];
        }
        
        _spatial.RaycastSingle(world, visualEyePos, -Vector3.UnitY, 2.0f, out _, out float floorDist);
        if (floorDist < 0.5f && floorDist < closestWallDist) closestWallDist = floorDist;

        _audio.UpdateProximity(closestWallDist);

        // 5. Update the acoustic path (occlusion/diffraction) for ALL active sounds in FMOD
        var activeIds = _audio.GetActiveSpatialSoundIds();
        foreach (var id in activeIds)
        {
            // --- CRITICAL FIX: Reflection Termination ---
            // IDs less than -10000 are reserved for synthesized reflections.
            // We MUST NOT calculate reflections for reflections, or we get an infinite feedback loop.
            if (id < -5000)
            {
                // Simple distance update for existing reflections to maintain panning, 
                // but no recursive ray-tracing.
                continue; 
            }

            Vector3 sourcePos;
            if (world.Entities.TryGetValue(id, out var snap))
            {
                sourcePos = snap.Transform.Position;
            }
            else
            {
                sourcePos = _audio.GetSoundPosition(id);
                if (sourcePos == Vector3.Zero) continue;
            }
            
            float dist = Vector3.Distance(visualEyePos, sourcePos);
            int updateRate = 1;
            if (dist > 50.0f) updateRate = 10;
            else if (dist > 15.0f) updateRate = 2;
            
            bool isImportant = false;
            if (world.Entities.TryGetValue(id, out var sourceSnap))
            {
                isImportant = sourceSnap.Definition.SoundEmitter.Volume >= 0.8f && sourceSnap.Definition.SoundEmitter.Range >= 20.0f;
            }

            if (_frameCount % updateRate == Math.Abs(id) % updateRate)
            {
                _acousticWorker.EnqueueRequest(new AcousticRequest
                {
                    EntityId = id,
                    ListenerPos = visualEyePos,
                    SourcePos = sourcePos,
                    IsImportant = isImportant
                });
            }
            
            if (_acousticWorker.TryGetResult(id, out var paths))
            {
                foreach (var path in paths)
                {
                    if (!path.IsReflection)
                    {
                        _audio.SetAcousticPath(id, path);
                    }
                    // Hand-rolled discrete reflection emitters are retired once Steam Audio simulation is
                    // active — geometry-driven reverb (4d) covers reflected energy. They still run as the
                    // fallback when SA sim is unavailable (OPENFPS_STEAMAUDIO_SIM=0 / no libphonon).
                    else if (!_acousticWorker.SteamAudioActive)
                    {
                    // OUTDOOR BUILDING REFLECTIONS
                    if (world.Entities.TryGetValue(id, out var originalSnap))
                    {
                        // --- PHASE 2: Stable Reflection Identity ---
                        // Use the hashed ReflectionId to ensure a specific wall reflection 
                        // persists even if the ray-tracer's scan order changes.
                        int reflectId = -30000 - (id * 100) - (path.ReflectionId % 100);
                        
                        var reflectEmitter = new SpatialEmitter
                        {
                            EntityId = reflectId,
                            SoundId = _sounds.ResolvePath(originalSnap.Definition.SoundEmitter.SoundId),
                            Mode = originalSnap.Definition.SoundEmitter.Mode,
                            Position = path.ApparentPosition,
                            ApparentPosition = path.ApparentPosition,
                            Volume = originalSnap.Definition.SoundEmitter.Volume * (1.0f - path.Occlusion) * 0.8f,
                            Range = originalSnap.Definition.SoundEmitter.Range * 0.8f,
                            IsReflection = true,
                            DelayMs = path.ReflectionDelayMs,
                            Type = EmitterType.WorldLocked,
                            EqHigh = path.EqHigh,
                            ReflectionSpread = path.Spread,
                            // Feed leftover energy into the reverb bus
                            TransmissionBleed = path.MaterialAbsorption * 0.5f 
                        };

                        if (_audio.IsPlaying(reflectId)) _audio.UpdateSpatialAttributes(reflectEmitter);
                        else _audio.PlayPhysicalSoundDirect(reflectEmitter);
                    }
                    }
                }
            }
        }

        // 6. Process persistent audio emitters attached to world entities (NPCs, Beacons, Machines)
        foreach (var entityId in world.AudioEntityIds)
        {
            if (entityId == OwnEntityId) continue;
            if (world.Entities.TryGetValue(entityId, out var snap))
            {
                ProcessAudioEmitter(world, snap, visualEyePos);
            }
        }

        // 7. Execute the audio engine tick (mixing, DSP updates)
        _audio.Update();
    }

    private void UpdateAcousticState(WorldSnapshot world, Vector3 eyePos, int regId)
    {
        _state.Temperature = world.Temperature;
        _state.Humidity = world.Humidity;
        _state.AirPressure = world.AirPressure;
        _state.WindVelocity = world.WindVelocity;
        _state.WindGustiness = world.WindGustiness;

        // Scale down precipitation intensity based on local shelter
        _state.PrecipitationIntensity = world.PrecipitationIntensity * (1.0f - _state.ShelterFactor);

        // Update readable region for accessibility
        if (world.AcousticMap != null && world.AcousticMap.Regions.TryGetValue(regId, out var reg))
        {
            _state.CurrentRegion = reg.FriendlyName;
            _state.IsIndoor = reg.IsIndoor;
            _state.RoomSize = reg.RoomSize;
            _state.RoomMaterials = reg.Materials;
            _state.ReverbTimeScale = reg.ReverbTimeScale;
            _state.RoomCenter = world.AcousticMap.RegionPositions.GetValueOrDefault(regId, eyePos);
            _state.RoomRotation = world.AcousticMap.RegionRotations.GetValueOrDefault(regId, Quaternion.Identity);
        }
        else
        {
            _state.CurrentRegion = _state.ShelterFactor > 0.8f ? "Under Shelter" : "Outside";
            _state.IsIndoor = false;
        }
    }
    private void ProcessAudioEmitter(WorldSnapshot world, EntitySnapshot snap, Vector3 eyePos)
    {
        var def = snap.Definition;

        // Use the async worker's last computed result rather than a synchronous per-frame calculation.
        // On the first frame before the worker has a result, fall back to an unoccluded direct path.
        AcousticPathData acousticPath;
        if (_acousticWorker.TryGetResult(snap.Id, out var cachedPaths))
        {
            acousticPath = cachedPaths.FirstOrDefault(p => !p.IsReflection);
        }
        else
        {
            float directDist = Vector3.Distance(eyePos, snap.Transform.Position);
            acousticPath = new AcousticPathData(0f, snap.Transform.Position, directDist);
        }

        string resolvedSoundId = "";
        if (def.SoundEmitter.IsSynth)
        {
            resolvedSoundId = def.SoundEmitter.SoundId;
            if (string.IsNullOrEmpty(resolvedSoundId)) resolvedSoundId = "SYNTH"; // Last resort dummy
        }
        else
        {
            resolvedSoundId = _sounds.ResolvePath(def.SoundEmitter.SoundId);
            if (string.IsNullOrEmpty(resolvedSoundId)) return;

            // --- Phase 2: Granular Finalization ---
            // If this is a granular emitter, ensure the sample is pre-decoded into RAM.
            if (def.SoundEmitter.IsGranular && !_preloadedSounds.Contains(resolvedSoundId))
            {
                _audio.Preload(resolvedSoundId);
                _preloadedSounds.Add(resolvedSoundId);
            }
        }

        var emitter = new SpatialEmitter
        {
            EntityId = snap.Id,
            SoundId = resolvedSoundId,
            Mode = def.SoundEmitter.Mode,
            Position = snap.Transform.Position,
            ApparentPosition = acousticPath.ApparentPosition,
            EffectiveDistance = acousticPath.EffectiveDistance,
            Occlusion = acousticPath.Occlusion,
            ApertureFactor = acousticPath.ApertureFactor,
            TransmissionBleed = acousticPath.TransmissionBleed,
            Velocity = snap.Velocity,
            Direction = Vector3.Transform(Vector3.UnitZ, snap.Transform.Rotation),
            Volume = def.SoundEmitter.Volume,
            Range = Math.Max(1.0f, def.SoundEmitter.Range),
            Pitch = 1.0f,
            Type = EmitterType.EntityAttached,
            Priority = 1,
            IsReflection = false,
            TargetRegionId = acousticPath.RegionId,
            EnableReverb = true,
            ConeInside = def.SoundEmitter.ConeInsideAngle,
            ConeOutside = def.SoundEmitter.ConeOutsideAngle,
            ConeOutsideVolume = def.SoundEmitter.ConeOutsideVolume,
            MinDistance = def.SoundEmitter.MinDistance,

            // Synthesis mapping
            IsGranular = def.SoundEmitter.IsGranular,
            GranularPosition = def.SoundEmitter.GranularPosition,
            GranularGrainSizeMs = def.SoundEmitter.GranularGrainSizeMs,
            GranularDensity = def.SoundEmitter.GranularDensity,
            GranularPitch = def.SoundEmitter.GranularPitch,
            GranularPositionJitter = def.SoundEmitter.GranularPositionJitter,
            GranularPitchJitter = def.SoundEmitter.GranularPitchJitter,

            IsSynth = def.SoundEmitter.IsSynth,
            SynthWave = (SynthWaveType)def.SoundEmitter.SynthWave,
            SynthFrequency = def.SoundEmitter.SynthFrequency,
            SynthLfoRate = def.SoundEmitter.SynthLfoRate,
            SynthLfoDepth = def.SoundEmitter.SynthLfoDepth,
            SynthFilterCutoff = def.SoundEmitter.SynthFilterCutoff,
            SynthFilterResonance = def.SoundEmitter.SynthFilterResonance,
            SynthPulseWidth = def.SoundEmitter.SynthPulseWidth
        };

        _audio.Submit(emitter);

        // 6.5. Dynamic Height Reflections (Floor Slapback)
        // If sound is significantly below eye level, synthesize a floor reflection
        if (!emitter.IsReflection && emitter.Position.Y < (eyePos.Y - 1.0f) && emitter.Volume > 0.3f)
        {
            if (_frameCount % 20 == Math.Abs(snap.Id) % 20)
            {
                if (_spatial.RaycastSingle(world, snap.Transform.Position, -Vector3.UnitY, 5.0f, out var floorHit, out float hitDist))
                {
                    Vector3 floorHitPos = snap.Transform.Position - new Vector3(0, hitDist, 0);
                    float delayMs = (hitDist / 343.0f) * 1000.0f;
                    float absorption = floorHit.Definition.Acoustics.Absorption;

                    int reflectId = -10000 - snap.Id;
                    var floorReflect = new SpatialEmitter
                    {
                        EntityId = reflectId,
                        SoundId = resolvedSoundId,
                        Mode = emitter.Mode,
                        Position = floorHitPos,
                        ApparentPosition = floorHitPos,
                        Volume = emitter.Volume * 0.3f * (1.0f - absorption),
                        Range = emitter.Range * 0.5f,
                        Pitch = emitter.Pitch * 0.98f, // Slightly lower pitch for reflection
                        Type = EmitterType.WorldLocked,
                        IsReflection = true,
                        DelayMs = delayMs,
                        EqHigh = 0.7f // Muffle high-end of reflections
                    };
                    
                    if (_audio.IsPlaying(reflectId)) _audio.UpdateSpatialAttributes(floorReflect);
                    else _audio.PlayPhysicalSoundDirect(floorReflect);
                }
            }
        }

        if (def.SoundEmitter.ConeInsideAngle < 360f && def.SoundEmitter.Volume > 0.5f)
        {
            if (_frameCount % 10 == Math.Abs(snap.Id) % 10)
            {
                Vector3 forward = Vector3.Transform(Vector3.UnitZ, snap.Transform.Rotation);
                if (_spatial.RaycastSingle(world, snap.Transform.Position, forward, def.SoundEmitter.Range, out _, out float hitDist))
                {
                    Vector3 hitPos = snap.Transform.Position + (forward * hitDist);
                    int hitRegion = _acoustics.GetRegionAt(world, hitPos);
                    float delayMs = (hitDist / 343.0f) * 1000.0f;
                    
                    int reflectId = -5000 - snap.Id;
                    var reflectEmitter = new SpatialEmitter
                    {
                        EntityId = reflectId,
                        SoundId = resolvedSoundId,
                        Mode = def.SoundEmitter.Mode,
                        Position = hitPos,
                        ApparentPosition = hitPos,
                        EffectiveDistance = Vector3.Distance(eyePos, hitPos),
                        Occlusion = 0.0f,
                        Volume = def.SoundEmitter.Volume * 0.4f * (1.0f - (hitDist / def.SoundEmitter.Range)),
                        Range = def.SoundEmitter.Range * 0.5f,
                        Pitch = 1.0f,
                        Type = EmitterType.WorldLocked,
                        Priority = 2,
                        IsReflection = true,
                        DelayMs = delayMs,
                        TargetRegionId = hitRegion,
                        EnableReverb = true
                    };
                    
                    if (_audio.IsPlaying(reflectId)) _audio.UpdateSpatialAttributes(reflectEmitter);
                    else _audio.PlayPhysicalSoundDirect(reflectEmitter);
                }
            }
        }
    }

    // One Opus decoder per sender — decoders are stateful (track packet loss continuity).
    private readonly Dictionary<int, IOpusDecoder> _voiceDecoders = new();
    private const int VoiceSampleRate = 48000;
    private const int VoiceFrameSamples = 960; // 20ms at 48kHz

    /// <summary>
    /// Decodes an incoming Opus voice packet and plays it at the sender's current world position.
    /// </summary>
    public void PlayReceivedVoice(int senderId, byte[] opusData, WorldSnapshot world)
    {
        if (!_voiceDecoders.TryGetValue(senderId, out var decoder))
        {
            decoder = OpusCodecFactory.CreateDecoder(VoiceSampleRate, 1);
            _voiceDecoders[senderId] = decoder;
        }

        var pcmShort = new short[VoiceFrameSamples];
        // Concentus 2.x Span-based API: Decode(ReadOnlySpan<byte>, Span<short>, int frameSize, bool decodeFec)
        int decoded = decoder.Decode(opusData.AsSpan(), pcmShort.AsSpan(), VoiceFrameSamples, false);
        if (decoded <= 0) return;

        // Convert short PCM to byte array
        var pcmBytes = new byte[decoded * 2];
        Buffer.BlockCopy(pcmShort, 0, pcmBytes, 0, pcmBytes.Length);

        Vector3 pos = world.Entities.TryGetValue(senderId, out var snap)
            ? snap.Transform.Position
            : _state.Position; // fallback: play at local position if sender unknown

        _audio.PlayVoice(senderId, pos, pcmBytes);
    }

    /// <summary>
    /// Plays a short tone to indicate voice transmission has started.
    /// </summary>
    public void PlayVoiceIndicator() => _audio.PlayUiBeep(880f, 80f);

    private int _footstepPoolIndex = 0;
    // Larger pool so rapid footsteps rarely reuse an ID while the previous step is still playing — reusing
    // an active voice hard-cuts it (click). 12 IDs gives plenty of headroom at running cadence.
    private const int FOOTSTEP_POOL_SIZE = 12;
    private const int FOOTSTEP_BASE_ID = -100;

    public void OnPlayerFootstep(Vector3 pos, string mat, string var)
    {
        int id = FOOTSTEP_BASE_ID - (_footstepPoolIndex % FOOTSTEP_POOL_SIZE);
        _footstepPoolIndex++;
        Vector3 nudgePos = pos + new Vector3(0, 0.1f, 0);
        
        string resolvedSoundId = _sounds.ResolvePath(_sounds.GetImpactSoundId(mat, 0f));
        if (string.IsNullOrEmpty(resolvedSoundId)) return;

        // 1. Direct Sound (Will now undergo full acoustic pathing)
        var footstep = new SpatialEmitter
        {
            EntityId = id,
            SoundId = resolvedSoundId,
            Position = nudgePos,
            Type = EmitterType.WorldLocked,
            Volume = 1.0f,
            Range = 15.0f,
            Priority = 3,
            IsEvent = true,
            MinDistance = 1.0f
        };
        _audio.Submit(footstep);

        // NOTE: footsteps deliberately do NOT spawn reflection/echo emitters. Bouncing each step off
        // the surrounding walls scattered the sound "all over the place" in enclosed rooms instead of
        // staying localized at the player's feet. The room's reverb bus still gives footsteps their
        // indoor character; per-step geometric reflections are reserved for world emitters.
    }

    /// <summary>
    /// Called when the server reports a material change underfoot via StatsUpdate.
    /// Passes the material's absorption index into the current region for reverb correction.
    /// </summary>
    public void NotifyMaterialChange(string material)
    {
        if (_lastAcousticMap == null) return;
        var snap = _lastSnapshot ?? _acousticWorker.GetLastWorld();
        if (snap == null) return;
        int listenerRegionId = _acoustics.GetRegionAt(snap, _state.VisualPosition + new Vector3(0, 1.7f, 0));
        if (listenerRegionId == AcousticConstants.GlobalRegionId) return;
        if (!_lastAcousticMap.Regions.TryGetValue(listenerRegionId, out var region)) return;

        // Override the floor material (index 0) with the server-reported underfoot material.
        int resonanceIndex = AcousticRegistry.GetProperties(material).ResonanceIndex;
        if (region.Materials != null && region.Materials.Length > 0 && region.Materials[0] != resonanceIndex)
        {
            region.Materials[0] = resonanceIndex;
            _lastAcousticMap.Regions[listenerRegionId] = region;
            // Signal the audio engine to recreate the reverb bus with updated material data.
            _audio.SetAcousticMap(_lastAcousticMap);
        }
    }

    public void OnPlayerLand(Vector3 pos, string mat, string var)
    {
        Vector3 nudgePos = pos + new Vector3(0, 0.1f, 0);
        string impactSound = _sounds.GetImpactSoundId(mat, 0f);
        string resolved = _sounds.ResolvePath(impactSound);
        
        if (!string.IsNullOrEmpty(resolved))
        {
            var landEmitter = new SpatialEmitter
            {
                EntityId = -50,
                SoundId = resolved,
                Position = nudgePos,
                Type = EmitterType.WorldLocked,
                Volume = 1.0f,
                Range = 20.0f,
                Priority = 2,
                IsEvent = true,
                MinDistance = 1.0f
            };
            _audio.Submit(landEmitter);
        }
    }
}

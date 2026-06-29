using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Threading;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Common;

namespace OpenFPS.Client.AudioEngine.Core;

/// <summary>
/// Responsibility: The primary "Public API" for the Audio Engine.
/// It wraps the FMOD provider, the Voice Manager, and the Audio Bank into a single clean interface.
/// Now uses lock-free queues and zero-allocation state passing.
/// </summary>
public class AudioEngineFacade : IDisposable
{
    private IAudioProvider _provider;
    private bool _isInitialized = false;
    private VoiceManager? _voiceManager;
    private readonly AudioBank _bank = new();

    // Lock-free command queues (Game Thread -> Audio Thread)
    private readonly ConcurrentQueue<SpatialEmitter> _submissionQueue = new();
    private readonly ConcurrentQueue<int> _stopRequests = new();
    private readonly ConcurrentQueue<KeyValuePair<int, AcousticPathData>> _acousticPaths = new();
    private readonly ConcurrentQueue<SpatialEmitter> _directPlayQueue = new();
    private readonly ConcurrentQueue<SpatialEmitter> _updateAttributesQueue = new();

    // Thread-safe scalar state
    private Vector3 _listenerPos;
    private Quaternion _listenerRot = Quaternion.Identity;
    private Vector3 _listenerVel;
    private int _listenerRegionId = -1;
    private float _listenerProximity = 2.0f;
    private float _listenerShelter = 0.0f;
    private AcousticMap? _acousticMap;
    private readonly object _stateLock = new();

    private Thread? _audioThread;
    private volatile bool _isRunning = false;

    /// <summary>True once the underlying provider (FMOD) initialized successfully.</summary>
    public bool IsInitialized => _isInitialized;

    public AudioEngineFacade() : this(new FmodAudioProvider()) { }

    public AudioEngineFacade(IAudioProvider provider)
    {
        _provider = provider;
    }

    /// <summary>
    /// Bootstraps the engine and loads the sound assets.
    /// </summary>
    public void Initialize()
    {
        if (_provider.Initialize())
        {
            string audioPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ASSETS", "SOUNDS");
            _bank.Initialize(audioPath);
            _isInitialized = true;
            _voiceManager = new VoiceManager(this, _bank, 256);
            Serilog.Log.Information("AudioEngine: Initialized with {MaxVoices} voices.", 256);
            _isRunning = true;
            _audioThread = new Thread(AudioLoop) 
            { 
                IsBackground = true, 
                Priority = ThreadPriority.AboveNormal,
                Name = "AudioEngineThread"
            };
            _audioThread.Start();
        }
    }

    private void AudioLoop()
    {
        while (_isRunning)
        {
            var start = DateTime.Now;
            
            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "AudioEngine: Error in audio loop.");
            }

            // Throttle to target framerate (~60fps / 16ms)
            int elapsed = (int)(DateTime.Now - start).TotalMilliseconds;
            int sleep = Math.Max(1, 16 - elapsed);
            Thread.Sleep(sleep);
        }
    }

    /// <summary>
    /// Synchronous "Pulse" that processes the audio frame.
    /// Called internally by the AudioThread.
    /// </summary>
    private void Tick()
    {
        if (!_isInitialized) return;

        // 1. Snapshot scalar state
        Vector3 lPos, lVel;
        Quaternion lRot;
        int lRegion;
        float lProx, lShelter;
        AcousticMap? aMap;

        lock (_stateLock)
        {
            lPos = _listenerPos;
            lRot = _listenerRot;
            lVel = _listenerVel;
            lRegion = _listenerRegionId;
            lProx = _listenerProximity;
            lShelter = _listenerShelter;
            aMap = _acousticMap;
        }

        // 2. Synchronize listener
        _provider.UpdateListener(lPos, lRot, lVel, lRegion);
        _provider.UpdateShelter(lShelter);
        _provider.UpdateProximity(lProx);
        _provider.SetSimulatedReverbDecay(_simReverbMs);
        
        // 3. Synchronize acoustic map
        if (aMap != null) _provider.SetAcousticMap(aMap);

        // 4. Process incoming commands from Game Thread
        while (_stopRequests.TryDequeue(out int id)) 
        {
            _voiceManager?.RequestStop(id);
        }
        
        while (_acousticPaths.TryDequeue(out var kvp)) 
        {
            _provider.SetAcousticPath(kvp.Key, kvp.Value);
        }

        while (_submissionQueue.TryDequeue(out var emitter))
        {
            _voiceManager?.Submit(emitter);
        }

        while (_directPlayQueue.TryDequeue(out var emitter))
        {
            _provider.PlaySpatialSound(emitter);
        }

        while (_updateAttributesQueue.TryDequeue(out var emitter))
        {
            _provider.UpdateSpatialAttributes(emitter);
        }

        // 5. Score and manage active voices
        _voiceManager?.Process(lPos);
        
        // 6. Tick the low-level provider
        _provider.Update();
    }

    /// <summary>
    /// Public entry point for the Game Thread to signal it has finished submitting state.
    /// In the threaded model, this is mostly a heartbeat or no-op as the background thread pulls state.
    /// </summary>
    public void Update()
    {
        // No-op in asynchronous mode, but kept for interface compatibility.
    }

    public void UpdateListener(Vector3 pos, Quaternion rot, Vector3 vel, int regionId)
    {
        lock (_stateLock)
        {
            _listenerPos = pos;
            _listenerRot = rot;
            _listenerVel = vel;
            _listenerRegionId = regionId;
        }
    }

    public void UpdateProximity(float distance)
    {
        lock (_stateLock)
        {
            _listenerProximity = distance;
        }
    }

    public void UpdateShelter(float shelterFactor)
    {
        lock (_stateLock)
        {
            _listenerShelter = shelterFactor;
        }
    }

    public void SetAcousticMap(AcousticMap map)
    {
        lock (_stateLock)
        {
            _acousticMap = map;
        }
    }

    // Geometry-driven reverb decay (ms) for the listener's room, supplied by the acoustic worker's
    // reflection sim. Volatile scalar — read once per flush; 0 means "no override".
    private volatile float _simReverbMs;
    public void SetSimulatedReverbDecay(float decayMs) => _simReverbMs = decayMs;

    /// <summary>
    /// Submits a spatial emitter for playback. 
    /// The VoiceManager will decide if it's audible enough to deserve a physical FMOD channel.
    /// </summary>
    public void Submit(SpatialEmitter emitter)
    {
        if (!_isInitialized) return;
        
        // Apply Speed of Sound delay (Wavefront delay)
        // 343 m/s is standard speed of sound at sea level.
        Vector3 lPos;
        lock (_stateLock) { lPos = _listenerPos; }
        
        float dist = Vector3.Distance(lPos, emitter.Position);
        emitter.DelayMs += (dist / 343.0f) * 1000.0f;

        _submissionQueue.Enqueue(emitter);
    }

    /// <summary>
    /// Bypasses the VoiceManager scoring logic and forces immediate playback.
    /// Used for critical sounds or transient events like echoes.
    /// </summary>
    public void PlayPhysicalSoundDirect(SpatialEmitter emitter)
    {
        if (!_isInitialized) return;
        if (Thread.CurrentThread == _audioThread)
        {
            _provider.PlaySpatialSound(emitter);
        }
        else
        {
            _directPlayQueue.Enqueue(emitter);
        }
    }

    /// <summary>
    /// Updates the attributes of an existing physical sound channel.
    /// </summary>
    public void UpdateSpatialAttributes(SpatialEmitter emitter)
    {
        if (!_isInitialized) return;
        if (Thread.CurrentThread == _audioThread)
        {
            _provider.UpdateSpatialAttributes(emitter);
        }
        else
        {
            _updateAttributesQueue.Enqueue(emitter);
        }
    }

    /// <summary>
    /// Requests a sound to stop. This may trigger a sequential "Shutdown" sound
    /// if the emitter is configured as a machine.
    /// </summary>
    public void StopSound(int entityId) 
    {
        _stopRequests.Enqueue(entityId);
    }
    
    /// <summary>
    /// Immediate, hard cutoff of a sound channel.
    /// </summary>
    internal void StopSoundImmediate(int entityId) => _provider.StopSound(entityId);

    /// <summary>
    /// Sets the real-time physical path data (occlusion, bleed) for an entity.
    /// </summary>
    public void SetAcousticPath(int entityId, AcousticPathData path)
    {
        _acousticPaths.Enqueue(new KeyValuePair<int, AcousticPathData>(entityId, path));
    }

    public bool IsPlaying(int entityId) => _isInitialized && _provider.IsPlaying(entityId);
    public Vector3 GetSoundPosition(int entityId) => _isInitialized ? _provider.GetSoundPosition(entityId) : Vector3.Zero;
    public float GetPlaybackProgress(int entityId) => _isInitialized ? _provider.GetPlaybackProgress(entityId) : 0f;
    public bool HasCategory(string category) => _isInitialized && _bank.HasCategory(category);
    public IEnumerable<int> GetActiveSpatialSoundIds() => _isInitialized ? _provider.GetActiveSpatialSoundIds() : Array.Empty<int>();

    /// <summary>
    /// Pre-decodes a sound into the granular buffer cache.
    /// </summary>
    public void Preload(string soundId)
    {
        if (!_isInitialized) return;
        _provider.Preload(soundId);
    }

    public void PreloadAll(Action<string, int> progressCallback)
    {
        if (!_isInitialized) return;
        
        var allIds = _bank.GetAllSoundIds().ToList();
        for (int i = 0; i < allIds.Count; i++)
        {
            var id = allIds[i];
            _provider.Preload(id);
            progressCallback?.Invoke($"Preloading: {id}", (int)((float)i / allIds.Count * 100));
        }
    }

    /// <summary>
    /// Plays a received voice packet as a one-shot 3D sound at the sender's world position.
    /// </summary>
    public void PlayVoice(int senderId, Vector3 position, byte[] pcmData)
    {
        if (_isInitialized) _provider.PlayVoice(senderId, position, pcmData);
    }

    /// <summary>
    /// Plays a non-spatial UI beep. Thread-safe.
    /// </summary>
    public void PlayUiBeep(float frequencyHz, float durationMs)
    {
        if (_isInitialized) _provider.PlayUiBeep(frequencyHz, durationMs);
    }

    // --- Step 1a diagnostics: drive an isolated mono source (see AudioDiagnostics). ---
    public void StartDiagnosticSound() { if (_isInitialized) _provider.StartDiagnosticSound(); }
    public void SetDiagnosticPosition(Vector3 position) { if (_isInitialized) _provider.SetDiagnosticPosition(position); }
    public void StopDiagnosticSound() { if (_isInitialized) _provider.StopDiagnosticSound(); }

    public void Dispose()
    {
        _isRunning = false;
        _audioThread?.Join(500);
        _provider?.Dispose();
    }
}
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
public class AudioEngineFacade : IDisposable, IVoiceSink
{
    private IAudioProvider _provider;
    private bool _isInitialized = false;
    private VoiceManager? _voiceManager;

    /// <summary>However short the mixer runs, this many voices are still placed.</summary>
    private const int MinBudgetVoices = 8;
    /// <summary>And no more than this, whatever the pool says — an upper bound on the sort, not a
    /// statement about the hardware.</summary>
    private const int MaxBudgetVoices = 256;
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
    // The surfaces around the listener's head, double-buffered: the game thread fills one while the
    // audio thread reads the other, so the per-frame probe never allocates and never tears.
    private readonly BoundaryProbe[] _boundariesIn = new BoundaryProbe[BoundaryVoiceState.MaxTaps];
    private readonly BoundaryProbe[] _boundariesOut = new BoundaryProbe[BoundaryVoiceState.MaxTaps];
    private int _boundaryCount;

    /// <summary>Ambience bed commands from the game thread, applied on the audio thread with everything
    /// else. Starting a bed decodes a file and creates a Steam Audio effect — not work to do on the
    /// thread that is trying to simulate the world.</summary>
    private readonly ConcurrentQueue<(string Id, AmbisonicLayout Layout, float Volume, bool Loop, bool Stop)> _ambientBedCommands = new();
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

            // Throttle. Faster than a display frame ON PURPOSE.
            //
            // Doppler is applied as a channel pitch, and a channel pitch changes the instant it is
            // set — FMOD ramps volume, not rate. So the update period is the resolution of every
            // pass-by in the game. A car at 280 km/h going past eleven metres away swings its radial
            // speed at about v^2/d = 550 m/s^2, which at 60 Hz is a 2.7% pitch step sixty times a
            // second: not a swoop but a staircase, and clearly audible as one on anything with a
            // high, pure note — which is exactly where it was first noticed, on the V10s.
            //
            // At 250 Hz the same pass steps by 0.65%, which is under the threshold where a pitch
            // change is heard as a step rather than a glide. The loop itself is cheap — it updates
            // voice attributes, it does not mix — so this costs a few per cent of one thread and buys
            // the single most audible cue in the game.
            const int PeriodMs = 4;
            int elapsed = (int)(DateTime.Now - start).TotalMilliseconds;
            int sleep = Math.Max(1, PeriodMs - elapsed);
            Thread.Sleep(sleep);
            ReportTickRate();
        }
    }

    private readonly System.Diagnostics.Stopwatch _rateClock = System.Diagnostics.Stopwatch.StartNew();
    private int _ticks;

    /// <summary>
    /// Says out loud whether the 250 Hz above is actually being achieved.
    ///
    /// It is not a setting, it is an OUTCOME: the period is `max(1 ms, 4 ms - however long the tick
    /// took)`, and the tick walks every active voice. Eight cars is seventeen voices; thirty cars is
    /// sixty. Nobody would notice the rate sagging, because the symptom is not a dropout or a warning
    /// — it is that pass-bys go back to sounding like a staircase, which is the exact thing the
    /// 250 Hz was chosen to prevent. So it is measured, and it complains.
    /// </summary>
    private void ReportTickRate()
    {
        _ticks++;
        double since = _rateClock.Elapsed.TotalSeconds;
        if (since < 5.0) return;
        _rateClock.Restart();
        double hz = _ticks / since;
        _ticks = 0;
        var cost = _provider.TakeUpdateCost();
        // The pitch step a pass-by gets at this rate. 0.65% at 250 Hz is the design point — under the
        // threshold where a change in pitch is heard as a step rather than a glide.
        double stepPct = 0.65 * (250.0 / Math.Max(1.0, hz));
        if (hz < 200)
            Serilog.Log.Warning("Audio thread: {Hz:F0} Hz of 250 — pass-bys step by {Step:F1}% (0.65% is the target). "
                              + "Attribute pass {Mean:F2} ms mean, {Max:F1} ms worst over {Voices} voice(s).",
                                hz, stepPct, cost.MeanMs, cost.MaxMs, cost.Voices);
        else
            Serilog.Log.Information("Audio thread: {Hz:F0} Hz, attribute pass {Mean:F2} ms mean, {Max:F1} ms worst over {Voices} voice(s).",
                                    hz, cost.MeanMs, cost.MaxMs, cost.Voices);
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
        float lShelter;
        int lBoundaries;
        AcousticMap? aMap;

        lock (_stateLock)
        {
            lPos = _listenerPos;
            lRot = _listenerRot;
            lVel = _listenerVel;
            lRegion = _listenerRegionId;
            lBoundaries = _boundaryCount;
            Array.Copy(_boundariesIn, _boundariesOut, lBoundaries);
            lShelter = _listenerShelter;
            aMap = _acousticMap;
        }

        // 2. Synchronize listener
        _provider.UpdateListener(lPos, lRot, lVel, lRegion);
        _provider.UpdateShelter(lShelter);
        _provider.UpdateBoundaries(new ReadOnlySpan<BoundaryProbe>(_boundariesOut, 0, lBoundaries));
        _provider.SetSimulatedReverbDecay(_simReverbMs, _simEnclosure, _simHf, _simLf);
        Vector3 field; float anis, mfp, surface;
        lock (_stateLock) { field = _reverbFieldDir; anis = _reverbFieldAnisotropy; mfp = _reverbFieldMfp; surface = _reverbFieldSurface; }
        _provider.SetListenerReverbField(field, anis, mfp, surface);
        _provider.SetAirTemperature(_airTemperatureC);
        
        // 3. Synchronize acoustic map
        if (aMap != null) _provider.SetAcousticMap(aMap);

        // 4. Process incoming commands from Game Thread
        while (_stopRequests.TryDequeue(out int id))
        {
            // A voice the manager does not own — anything started through PlayPhysicalSoundDirect —
            // has to be stopped at the provider, or it never stops at all.
            bool owned = _voiceManager?.RequestStop(id) ?? false;
            if (!owned) _provider.StopSound(id);
        }
        
        while (_acousticPaths.TryDequeue(out var kvp)) 
        {
            _provider.SetAcousticPath(kvp.Key, kvp.Value);
        }

        while (_submissionQueue.TryDequeue(out var emitter))
        {
            _voiceManager?.Submit(emitter);
        }

        while (_ambientBedCommands.TryDequeue(out var cmd))
        {
            if (cmd.Stop) _provider.StopAmbientBed(cmd.Id);
            else _provider.PlayAmbientBed(cmd.Id, cmd.Layout, cmd.Volume, cmd.Loop);
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
        // The budget is what the mixer HAS, asked every frame rather than declared once. See
        // VoiceManager.MaxVoices: the floor keeps a handful of voices alive while the pool is
        // recovering, and the ceiling is only there so the list does not grow without bound.
        if (_voiceManager != null)
            _voiceManager.MaxVoices = Math.Clamp(
                _voiceManager.PlayingCount + _provider.SpatialVoicesFree, MinBudgetVoices, MaxBudgetVoices);
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

    /// <summary>
    /// Brings the facade up WITHOUT its audio thread, and pumps it by hand.
    ///
    /// For tests only. The voice lifecycle is queue-driven, so asserting on it means draining the
    /// queues deterministically; with the real thread running as well, the test and the engine race
    /// each other through the same state and the result is noise rather than a verdict.
    /// </summary>
    internal void InitializeForTest()
    {
        if (!_provider.Initialize()) return;
        _isInitialized = true;
        _voiceManager = new VoiceManager(this, _bank, 256);
    }

    /// <summary>Runs one audio frame synchronously. Pair with <see cref="InitializeForTest"/>.</summary>
    internal void PumpForTest() => Tick();

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

    /// <summary>Starts an ambisonic ambience bed, or re-aims a playing one. See
    /// <see cref="IAudioProvider.PlayAmbientBed"/>; the work happens on the audio thread.</summary>
    public void PlayAmbientBed(string soundId, AmbisonicLayout layout, float volume, bool loop = true)
        => _ambientBedCommands.Enqueue((soundId, layout, volume, loop, false));

    /// <summary>Re-aims a playing bed's level. Implemented as a play command, which is what the provider
    /// treats a repeat start as — so this cannot race a start that has not been applied yet.</summary>
    public void SetAmbientBedVolume(string soundId, float volume)
        => _ambientBedCommands.Enqueue((soundId, AmbisonicLayout.AmbiX, volume, true, false));

    public void StopAmbientBed(string soundId)
        => _ambientBedCommands.Enqueue((soundId, AmbisonicLayout.AmbiX, 0f, false, true));

    public void UpdateBoundaries(ReadOnlySpan<BoundaryProbe> probes)
    {
        lock (_stateLock)
        {
            _boundaryCount = Math.Min(probes.Length, _boundariesIn.Length);
            for (int i = 0; i < _boundaryCount; i++) _boundariesIn[i] = probes[i];
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
    public void SetSimulatedReverbDecay(float decayMs, float enclosure, float hfDecayRatio, float lfDecayRatio)
    { _simReverbMs = decayMs; _simEnclosure = enclosure; _simHf = hfDecayRatio; _simLf = lfDecayRatio; }

    private float _simEnclosure;
    private float _simHf = 1f;
    private float _simLf = 1f;

    private Vector3 _reverbFieldDir;
    private float _reverbFieldAnisotropy, _reverbFieldMfp, _reverbFieldSurface;
    /// <summary>See IAudioProvider.SetListenerReverbField. Forwarded on the audio thread's flush.</summary>
    public void SetListenerReverbField(Vector3 returnDirection, float anisotropy, float meanFreePathMetres,
                                       float surfaceAreaSquareMetres = 0f)
    {
        lock (_stateLock)
        {
            _reverbFieldDir = returnDirection; _reverbFieldAnisotropy = anisotropy;
            _reverbFieldMfp = meanFreePathMetres; _reverbFieldSurface = surfaceAreaSquareMetres;
        }
    }

    // The world's air temperature (°C), supplied by the client audio system from the server's weather.
    // Volatile scalar, read once per flush, like the reverb decay above.
    private volatile float _airTemperatureC = 20.0f;
    public void SetAirTemperature(float celsius) => _airTemperatureC = celsius;

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
    /// <summary>The mixer's DSP load, 0..1+. See IAudioProvider.MixerLoad.</summary>
    public float MixerLoad => _isInitialized ? _provider.MixerLoad : 0f;

    /// <summary>Brings a live engine voice back to full after a fade-out was started.</summary>
    public void ReviveEngine(int entityId) { if (_isInitialized) _provider.ReviveEngine(entityId); }

    /// <summary>Asks a live engine voice to fade out; true once it is silent and safe to stop.
    /// Called from the game thread, and it only writes a float the mixer reads.</summary>
    public bool FadeOutEngine(int entityId) => !_isInitialized || _provider.FadeOutEngine(entityId);

    /// <summary>See IAudioProvider.TryGetEngineTelemetry — the four numbers that tell apart the four
    /// different reasons a field of cars can sound like it is slowing down.</summary>
    public bool TryGetEngineTelemetry(int entityId, out float toldSpeed, out float ownSpeed, out float rpm, out int gear)
    {
        toldSpeed = ownSpeed = rpm = 0f; gear = 0;
        return _isInitialized && _provider.TryGetEngineTelemetry(entityId, out toldSpeed, out ownSpeed, out rpm, out gear);
    }

    public void StopSound(int entityId) 
    {
        _stopRequests.Enqueue(entityId);
    }
    
    /// <summary>
    /// Immediate, hard cutoff of a sound channel.
    /// </summary>
    public void StopSoundImmediate(int entityId) => _provider.StopSound(entityId);

    /// <summary>Takes a voice down to silence; true once it is there. The budget's way of letting go
    /// of a CONTINUOUS source, which is still there and still making a noise. See IVoiceSink.</summary>
    public bool FadeOut(int entityId) => !_isInitialized || _provider.FadeOutVoice(entityId);

    /// <summary>...and the other half, for one that won its slot back.</summary>
    public void CancelFade(int entityId) { if (_isInitialized) _provider.CancelVoiceFade(entityId); }

    /// <summary>
    /// Sets the real-time physical path data (occlusion, bleed) for an entity.
    /// </summary>
    public void SetAcousticPath(int entityId, AcousticPathData path)
    {
        _acousticPaths.Enqueue(new KeyValuePair<int, AcousticPathData>(entityId, path));
    }

    public bool IsPlaying(int entityId) => _isInitialized && _provider.IsPlaying(entityId);

    /// <summary>How many submissions the budget is holding. A number that climbs and does not come
    /// back down is one-shots being kept after their moment — see VoiceManager.Process. It has been
    /// heard twice as "reflections piling up where nothing is happening", and both times there was no
    /// gauge to look at.</summary>
    public int PendingSubmissions => _voiceManager?.SubmissionCount ?? 0;

    /// <summary>
    /// Makes a buffer the game synthesised available under a sound id.
    ///
    /// The bridge between physical modelling and the rest of the engine. After this call the id is an
    /// ordinary sound: placed, attenuated, occluded, reverberated and voice-budgeted by exactly the
    /// paths that handle recordings, none of which needs to know that nobody recorded it.
    /// </summary>
    public bool RegisterSynthesisedSound(string soundId, byte[] pcm16Mono, int sampleRate)
        => _isInitialized && _provider.RegisterSynthesisedSound(soundId, pcm16Mono, sampleRate);
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
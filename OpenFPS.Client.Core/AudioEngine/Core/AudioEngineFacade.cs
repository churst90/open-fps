using System.Collections.Concurrent;
using System.Numerics;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Common;

namespace OpenFPS.Client.AudioEngine.Core;

/// <summary>
/// The audio engine's public face: the provider, the voice budget and the bank behind one interface.
/// The game thread queues; the engine's own thread (AudioLoop) applies.
/// </summary>
public class AudioEngineFacade : IDisposable, IVoiceSink
{
    private IAudioProvider _provider;
    private bool _isInitialized = false;
    private VoiceManager? _voiceManager;

    /// <summary>However short the mixer runs, this many voices are still placed.</summary>
    private const int MinBudgetVoices = 8;
    /// <summary>An upper bound on the sort, not a statement about the hardware.</summary>
    private const int MaxBudgetVoices = 256;
    private readonly AudioBank _bank = new();

    // Game thread to audio thread.
    private readonly ConcurrentQueue<SpatialEmitter> _submissionQueue = new();
    private readonly ConcurrentQueue<int> _stopRequests = new();
    private readonly ConcurrentQueue<KeyValuePair<int, AcousticPathData>> _acousticPaths = new();
    private readonly ConcurrentQueue<SpatialEmitter> _directPlayQueue = new();
    private readonly ConcurrentQueue<SpatialEmitter> _updateAttributesQueue = new();

    // Under _stateLock.
    private Vector3 _listenerPos;
    private Quaternion _listenerRot = Quaternion.Identity;
    private Vector3 _listenerVel;
    private int _listenerRegionId = -1;
    // The surfaces round the listener's head, double-buffered so the per-frame probe never allocates
    // and never tears.
    private readonly BoundaryProbe[] _boundariesIn = new BoundaryProbe[BoundaryVoiceState.MaxTaps];
    private readonly BoundaryProbe[] _boundariesOut = new BoundaryProbe[BoundaryVoiceState.MaxTaps];
    private int _boundaryCount;

    /// <summary>Ambience bed commands, applied on the audio thread: starting a bed decodes a file and
    /// creates a Steam Audio effect, not work for the game thread.</summary>
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

    /// <summary>Brings up the provider, loads the bank and starts the audio thread.</summary>
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

            // 250 Hz, faster than a display frame on purpose. Doppler is a channel pitch and FMOD does
            // not ramp rate, so this period is every pass-by's resolution: a car at 280 km/h passing
            // 11 m away steps 2.7 % at 60 Hz (a staircase on a V10's note), 0.65 % at 250 Hz, under the
            // threshold where a pitch change is heard as a step.
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
    /// Logs whether the 250 Hz is achieved: it is an outcome, not a setting (the tick walks every voice,
    /// thirty cars is sixty), and a sagging rate is heard only as pass-bys turning back into staircases.
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
        double stepPct = 0.65 * (250.0 / Math.Max(1.0, hz));
        if (hz < 200)
            Serilog.Log.Warning("Audio thread: {Hz:F0} Hz of 250 — pass-bys step by {Step:F1}% (0.65% is the target). "
                              + "Attribute pass {Mean:F2} ms mean, {Max:F1} ms worst over {Voices} voice(s).",
                                hz, stepPct, cost.MeanMs, cost.MaxMs, cost.Voices);
        else
            Serilog.Log.Information("Audio thread: {Hz:F0} Hz, attribute pass {Mean:F2} ms mean, {Max:F1} ms worst over {Voices} voice(s).",
                                    hz, cost.MeanMs, cost.MaxMs, cost.Voices);
    }

    /// <summary>One audio frame, on the audio thread.</summary>
    private void Tick()
    {
        if (!_isInitialized) return;

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

        _provider.UpdateListener(lPos, lRot, lVel, lRegion);
        _provider.UpdateShelter(lShelter);
        _provider.UpdateBoundaries(new ReadOnlySpan<BoundaryProbe>(_boundariesOut, 0, lBoundaries));
        _provider.SetSimulatedReverbDecay(_simReverbMs, _simEnclosure, _simHf, _simLf);
        _provider.SetAirTemperature(_airTemperatureC);

        if (aMap != null) _provider.SetAcousticMap(aMap);

        while (_stopRequests.TryDequeue(out int id))
        {
            // A voice the manager does not own (started directly) is stopped at the provider, or never.
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

        // The budget is what the mixer has, asked every frame (VoiceManager.MaxVoices).
        if (_voiceManager != null)
            _voiceManager.MaxVoices = Math.Clamp(
                _voiceManager.PlayingCount + _provider.SpatialVoicesFree, MinBudgetVoices, MaxBudgetVoices);
        _voiceManager?.Process(lPos);

        _provider.Update();
    }

    /// <summary>Does nothing: the audio thread pulls state on its own.</summary>
    public void Update()
    {
    }

    /// <summary>
    /// Brings the facade up without its audio thread, to be pumped by hand. Tests only: with the real
    /// thread running too, the test and the engine race through the same queues.
    /// </summary>
    internal void InitializeForTest(string? soundsPath = null)
    {
        if (!_provider.Initialize()) return;
        if (soundsPath != null) _bank.Initialize(soundsPath);
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

    public bool EngineDoorsOpen(int entityId) => _isInitialized && _provider.EngineDoorsOpen(entityId);

    /// <summary>See IAudioProvider.SignalTrain.</summary>
    public void SignalTrain(string train, float[] warning, float bellSeconds, double secondsAgo)
    {
        if (_isInitialized) _provider.SignalTrain(train, warning, bellSeconds, secondsAgo);
    }

    public void SetListenerEnclosure(float lowDb, float midDb, float highDb)
    {
        if (_isInitialized) _provider.SetListenerEnclosure(lowDb, midDb, highDb);
    }

    /// <summary>Where the listener is, for the wind at their ears. Thread-safe: the provider swaps it
    /// in whole.</summary>
    public void SetEarWind(OpenFPS.Common.EarWindListener? listener)
    {
        if (_isInitialized) _provider.SetEarWind(listener);
    }

    public void UpdateShelter(float shelterFactor)
    {
        lock (_stateLock)
        {
            _listenerShelter = shelterFactor;
        }
    }

    /// <summary>The routes through openings, for the reverberant fields of the places beyond them.</summary>
    public Func<OpenFPS.Common.OpeningRoutes?>? RoutesSource
    {
        set { if (_provider is OpenFPS.Client.AudioEngine.Fmod.FmodAudioProvider fmod) fmod.RoutesSource = value; }
    }

    public void SetAcousticMap(AcousticMap map)
    {
        lock (_stateLock)
        {
            _acousticMap = map;
        }
    }

    // The traced reverb decay for the listener's room, ms, from the acoustic worker; 0 is none.
    private volatile float _simReverbMs;
    public void SetSimulatedReverbDecay(float decayMs, float enclosure, float hfDecayRatio, float lfDecayRatio)
    { _simReverbMs = decayMs; _simEnclosure = enclosure; _simHf = hfDecayRatio; _simLf = lfDecayRatio; }

    private float _simEnclosure;
    private float _simHf = 1f;
    private float _simLf = 1f;

    // The world's air temperature, °C, from the server's weather.
    private volatile float _airTemperatureC = 20.0f;
    public void SetAirTemperature(float celsius) => _airTemperatureC = celsius;

    /// <summary>Submits an emitter to the voice budget, with its flight time added to its delay.</summary>
    public void Submit(SpatialEmitter emitter)
    {
        if (!_isInitialized) return;

        Vector3 lPos;
        lock (_stateLock) { lPos = _listenerPos; }
        
        // A cue in the head has no flight time.
        if (emitter.Type != EmitterType.UI)
        {
            float dist = Vector3.Distance(lPos, emitter.Position);
            emitter.DelayMs += dist / AudioPhysics.CurrentSpeedOfSound * 1000.0f;
        }

        _submissionQueue.Enqueue(emitter);
    }

    /// <summary>Plays outside the voice budget (echoes and the like); such a voice must be stopped by
    /// its caller (VoiceManager.RequestStop).</summary>
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

    /// <summary>The mixer's DSP load, 0..1+. See IAudioProvider.MixerLoad.</summary>
    public float MixerLoad => _isInitialized ? _provider.MixerLoad : 0f;

    /// <summary>Brings a live engine voice back to full after a fade-out was started.</summary>
    public void ReviveEngine(int entityId) { if (_isInitialized) _provider.ReviveEngine(entityId); }

    /// <summary>Asks a live engine voice to fade out; true once it is silent and safe to stop.
    /// Called from the game thread, and it only writes a float the mixer reads.</summary>
    public bool FadeOutEngine(int entityId) => !_isInitialized || _provider.FadeOutEngine(entityId);

    /// <summary>See IAudioProvider.EngineVoiceDetail.</summary>
    public string EngineVoiceDetail(int entityId) => _isInitialized ? _provider.EngineVoiceDetail(entityId) : "";

    /// <summary>See IAudioProvider.TryGetEngineTelemetry: the four numbers that tell apart the four
    /// reasons a field of cars can sound like it is slowing down.</summary>
    public bool TryGetEngineTelemetry(int entityId, out float toldSpeed, out float ownSpeed, out float rpm, out int gear)
    {
        toldSpeed = ownSpeed = rpm = 0f; gear = 0;
        return _isInitialized && _provider.TryGetEngineTelemetry(entityId, out toldSpeed, out ownSpeed, out rpm, out gear);
    }

    /// <summary>Asks a sound to stop, on the audio thread; a machine with a stop sound plays it first.</summary>
    public void StopSound(int entityId)
    {
        _stopRequests.Enqueue(entityId);
    }

    /// <summary>A hard cut, on the calling thread.</summary>
    public void StopSoundImmediate(int entityId) => _provider.StopSound(entityId);

    /// <summary>See <see cref="IVoiceSink.FadeOut"/>.</summary>
    public bool FadeOut(int entityId) => !_isInitialized || _provider.FadeOutVoice(entityId);

    /// <summary>See <see cref="IVoiceSink.CancelFade"/>.</summary>
    public void CancelFade(int entityId) { if (_isInitialized) _provider.CancelVoiceFade(entityId); }

    /// <summary>An entity's path (occlusion, bleed), applied on the audio thread.</summary>
    public void SetAcousticPath(int entityId, AcousticPathData path)
    {
        _acousticPaths.Enqueue(new KeyValuePair<int, AcousticPathData>(entityId, path));
    }

    public bool IsPlaying(int entityId) => _isInitialized && _provider.IsPlaying(entityId);

    /// <summary>Submissions the budget is holding: a number that climbs and stays is one-shots kept
    /// after their moment (VoiceManager.Process).</summary>
    public int PendingSubmissions => _voiceManager?.SubmissionCount ?? 0;

    /// <summary>Makes a synthesised buffer an ordinary sound id, handled by the same paths as a
    /// recording.</summary>
    public bool RegisterSynthesisedSound(string soundId, byte[] pcm16Mono, int sampleRate)
        => _isInitialized && _provider.RegisterSynthesisedSound(soundId, pcm16Mono, sampleRate);
    /// <summary>Lets go of a one-off synthesised buffer once it has played (IAudioProvider.ReleaseSynthesisedSound).</summary>
    public bool ReleaseSynthesisedSound(string soundId) => _isInitialized && _provider.ReleaseSynthesisedSound(soundId);
    /// <summary>A synthesised buffer kept in float (IAudioProvider.RegisterSynthesisedSoundFloat).</summary>
    public bool RegisterSynthesisedSoundFloat(string soundId, float[] pcm, int sampleRate)
        => _isInitialized && _provider.RegisterSynthesisedSoundFloat(soundId, pcm, sampleRate);
    public Vector3 GetSoundPosition(int entityId) => _isInitialized ? _provider.GetSoundPosition(entityId) : Vector3.Zero;
    /// <summary>The sounds in one folder of the bank, e.g. BIRDS/SPARROW.</summary>
    public IReadOnlyList<string> SoundsIn(string category)
        => _isInitialized ? _bank.Members(category) : Array.Empty<string>();
    public IEnumerable<int> GetActiveSpatialSoundIds() => _isInitialized ? _provider.GetActiveSpatialSoundIds() : Array.Empty<int>();

    /// <summary>A recorded sound as mono float PCM, decoded on the calling thread and not cached.</summary>
    public bool TryDecodeMono(string soundId, out float[] mono, out int sampleRate)
    {
        mono = Array.Empty<float>(); sampleRate = 0;
        if (!_isInitialized || !_provider.TryDecode(soundId, out var pcm, out int channels, out sampleRate)
            || channels < 1 || sampleRate <= 0) return false;
        if (channels == 1) { mono = pcm; return true; }
        mono = new float[pcm.Length / channels];
        for (int i = 0; i < mono.Length; i++)
        {
            float sum = 0f;
            for (int c = 0; c < channels; c++) sum += pcm[i * channels + c];
            mono[i] = sum / channels;
        }
        return true;
    }

    /// <summary>Pre-decodes a sound into the granular buffer cache.</summary>
    public void Preload(string soundId)
    {
        if (!_isInitialized) return;
        _provider.Preload(soundId);
    }

    /// <summary>A take in the footstep bank below a material's own folder: a shoe's or a gait's.</summary>
    internal static bool IsFootstepVariant(string id)
        => (id.StartsWith("FOOTSTEPS/", StringComparison.OrdinalIgnoreCase) || id.StartsWith("LANDING/", StringComparison.OrdinalIgnoreCase))
           && id.Count(c => c == '/') > 2;

    public void PreloadAll(Action<string, int> progressCallback)
    {
        if (!_isInitialized) return;
        
        // Not the spoken lines (1,400 of them, some 350 MB decoded; WorldAudioPlayer.SpokenLine decodes
        // one when said), nor the footstep bank's per-shoe and per-gait folders (11,000 takes, some
        // 800 MB): those decode on first use.
        var allIds = _bank.GetAllSoundIds()
            .Where(id => !id.StartsWith("VOICES/", StringComparison.OrdinalIgnoreCase))
            .Where(id => !IsFootstepVariant(id))
            .ToList();
        for (int i = 0; i < allIds.Count; i++)
        {
            var id = allIds[i];
            _provider.Preload(id);
            progressCallback?.Invoke($"Preloading: {id}", (int)((float)i / allIds.Count * 100));
        }
    }

    public IReadOnlyList<VoiceLevel> LoudestVoices(int count) =>
        _isInitialized ? _provider.LoudestVoices(count) : Array.Empty<VoiceLevel>();

    public IReadOnlyList<string> OutputDevices() => _isInitialized ? _provider.OutputDevices() : Array.Empty<string>();
    public IReadOnlyList<string> InputDevices() => _isInitialized ? _provider.InputDevices() : Array.Empty<string>();
    public bool SetOutputDevice(string name) => _isInitialized && _provider.SetOutputDevice(name);

    /// <summary>A short interface sound in both ears. See IAudioProvider.PlayUiSound.</summary>
    public void PlayUiSound(string id, Func<float[]> render, int sampleRate, float volume)
    {
        if (_isInitialized) _provider.PlayUiSound(id, render, sampleRate, volume);
    }

    /// <summary>An interface loop in a named slot. See IAudioProvider.SetUiLoop.</summary>
    public void SetUiLoop(string slot, string id, Func<float[]> render, int sampleRate, float volume, float pitch)
    {
        if (_isInitialized) _provider.SetUiLoop(slot, id, render, sampleRate, volume, pitch);
    }

    /// <summary>Stops the interface loop in a slot. See IAudioProvider.StopUiLoop.</summary>
    public void StopUiLoop(string slot) { if (_isInitialized) _provider.StopUiLoop(slot); }

    /// <summary>Fades the world and not the interface sounds. See IAudioProvider.SetWorldFade.</summary>
    public void SetWorldFade(float gain) { if (_isInitialized) _provider.SetWorldFade(gain); }

    // The microphone, recorded through the same FMOD system. See IAudioProvider.StartRecording.
    public bool HasRecordingDevice => _isInitialized && _provider.HasRecordingDevice;
    public bool StartRecording(string deviceName, out int sampleRate)
    {
        sampleRate = 0;
        return _isInitialized && _provider.StartRecording(deviceName, out sampleRate);
    }
    public int ReadRecording(List<float> mono) => _isInitialized ? _provider.ReadRecording(mono) : 0;
    public void StopRecording() { if (_isInitialized) _provider.StopRecording(); }

    /// <summary>Stops every voice in the world at once, for leaving it.</summary>
    public void StopAllWorldSounds()
    {
        if (!_isInitialized) return;
        foreach (int id in _provider.GetActiveSpatialSoundIds().ToList()) _provider.StopSound(id);
    }

    // An isolated mono source for AudioDiagnostics.
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
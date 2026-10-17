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

    // Game thread to audio thread, each call numbered in the order it was made (Tick).
    private readonly ConcurrentQueue<(long Seq, SpatialEmitter E)> _submissionQueue = new();
    private readonly ConcurrentQueue<(long Seq, int Id)> _stopRequests = new();
    private readonly ConcurrentQueue<(long Seq, KeyValuePair<int, AcousticPathData> Path)> _acousticPaths = new();
    private readonly ConcurrentQueue<(long Seq, SpatialEmitter E)> _directPlayQueue = new();
    private readonly ConcurrentQueue<(long Seq, SpatialEmitter E)> _updateAttributesQueue = new();
    private readonly ConcurrentQueue<(long Seq, SpatialEmitter E)> _refreshQueue = new();
    private readonly ConcurrentQueue<(long Seq, SpatialEmitter E)> _soundQueue = new();
    private readonly ConcurrentQueue<(long Seq, SpatialEmitter E)> _sustainQueue = new();
    private long _seq;
    private long Next() => Interlocked.Increment(ref _seq);
    /// <summary>The last stop asked for each id this tick, by number: anything asked of that voice before it
    /// is not done (Tick).</summary>
    private readonly Dictionary<int, long> _stoppedAt = new();
    private readonly List<(long Seq, SpatialEmitter E)> _placings = new();
    private bool StoppedAfter(int id, long seq) => _stoppedAt.TryGetValue(id, out long at) && at > seq;

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

    private float _listenerShelter = 0.0f;
    private AcousticMap? _acousticMap;
    private readonly object _stateLock = new();

    private Thread? _audioThread;
    private volatile bool _isRunning = false;

    /// <summary>True once the underlying provider (FMOD) initialized successfully.</summary>
    public bool IsInitialized => _isInitialized;

    public AudioEngineFacade() : this(new FmodAudioProvider()) { }

    /// <summary>The FMOD mixer, reading its data where <paramref name="paths"/> says: the game's.</summary>
    public AudioEngineFacade(AudioDataPaths paths) : this(new FmodAudioProvider(), paths: paths) { }

    /// <param name="clock">The clock when the provider has none of its own (a test's mixer): the test's stepped
    /// one, or none for a stopwatch.</param>
    /// <param name="paths">Where this world's data is (the host's); none: what ships beside the program, and no
    /// door renders kept (<see cref="AudioDataPaths.Beside"/>).</param>
    /// <param name="options">The world's options when the provider has none of its own (a test's or the lab's
    /// mixer); none: the shipped world.</param>
    public AudioEngineFacade(IAudioProvider provider, IAudioClock? clock = null, AudioDataPaths? paths = null,
                             AudioWorldOptions? options = null)
    {
        _provider = provider;
        Paths = paths ?? AudioDataPaths.Beside(AppContext.BaseDirectory);
        DoorRenders = new DoorRenderCache(Paths);
        Options = (provider as FmodAudioProvider)?.Options ?? options ?? new AudioWorldOptions();
        Clock = (provider as FmodAudioProvider)?.Clock ?? clock ?? new StopwatchAudioClock();
        Weather = (provider as FmodAudioProvider)?.Weather ?? new WorldWeather { Clock = Clock };
        Atmosphere = (provider as FmodAudioProvider)?.Atmosphere ?? new Atmosphere();
        Hearing = (provider as FmodAudioProvider)?.Hearing ?? new OpenFPS.Common.Hearing.HearingSettings();
        Mix = (provider as FmodAudioProvider)?.Mix ?? new MixSettings();
        OwnVoice = (provider as FmodAudioProvider)?.OwnVoice ?? new OwnVoiceRing();
        TalkerStreams = (provider as FmodAudioProvider)?.TalkerStreams ?? new TalkerStreams();
        Lab = (provider as FmodAudioProvider)?.Lab ?? new LabLevers();
    }

    /// <summary>The clock this world tells time by, for the host to read wherever it stamps something the mixer
    /// will age (a position, a strike, a sound's moment): the mixer's when there is one (FmodAudioProvider.Clock).</summary>
    public IAudioClock Clock { get; }

    /// <summary>The weather this world hears, for the host to fill: the mixer's when there is one
    /// (FmodAudioProvider.Weather), so its voices read what the host writes.</summary>
    public WorldWeather Weather { get; }

    /// <summary>The air this world is in, for the host's flight times, echoes and reflections: the mixer's when
    /// there is one (FmodAudioProvider.Atmosphere, set from the weather the host gives it); otherwise one of the
    /// facade's own, in the air the heard laws are written in until a test sets it.</summary>
    public Atmosphere Atmosphere { get; }

    /// <summary>How this world is heard (the law's compression, the ear model, the listening level), for the host
    /// to place by and to set from the player's settings: the mixer's when there is one (FmodAudioProvider.Hearing);
    /// otherwise one of the facade's own, the shipped hearing, which a test sets as it needs.</summary>
    public OpenFPS.Common.Hearing.HearingSettings Hearing { get; }

    /// <summary>The mix's live levers, for the host's console and its placed copies: the mixer's when there is
    /// one (FmodAudioProvider.Mix); otherwise one of the facade's own, the shipped mix.</summary>
    public MixSettings Mix { get; }

    /// <summary>The ring the player's microphone writes into, which their room's answer reads: the mixer's
    /// (FmodAudioProvider.OwnVoice), or one of the facade's own.</summary>
    public OwnVoiceRing OwnVoice { get; }

    /// <summary>Everybody whose voice this world has heard, which the host fills as voices arrive: the mixer's
    /// (FmodAudioProvider.TalkerStreams), or one of the facade's own.</summary>
    public TalkerStreams TalkerStreams { get; }

    /// <summary>The lab's levers on this world, which the host hands its acoustic worker: the mixer's
    /// (FmodAudioProvider.Lab), or one of the facade's own. Nothing in the game sets one.</summary>
    public LabLevers Lab { get; }

    /// <summary>This world's options (the wide sources, the cabins' paths), for the host's placing to read as the
    /// mixer's voices do: the mixer's when there is one (FmodAudioProvider.Options); otherwise one of the facade's
    /// own, the shipped world.</summary>
    public AudioWorldOptions Options { get; }

    /// <summary>Where this world's data is, as the host said (or what ships beside the program).</summary>
    public AudioDataPaths Paths { get; }

    /// <summary>The door models' renders kept on disk for this world, in the folders <see cref="Paths"/> names.</summary>
    public DoorRenderCache DoorRenders { get; }

    /// <summary>Whether the mixer traces this entity's echoes now, so the host stops making its mirror images
    /// (FmodAudioProvider.HasTracedEchoes). Never without an FMOD mixer.</summary>
    public bool HasTracedEchoes(int entityId)
        => (_provider as OpenFPS.Client.AudioEngine.Fmod.FmodAudioProvider)?.HasTracedEchoes(entityId) ?? false;

    /// <summary>The rate the mixer runs at, Hz, for what the host renders for it to play without a resampler:
    /// the FMOD mixer's (FmodAudioProvider.MixerRate), or MixerQuality.DefaultRate without one.</summary>
    public int MixerRate => (_provider as FmodAudioProvider)?.MixerRate ?? MixerQuality.DefaultRate;

    /// <summary>Brings up the provider, loads the bank and starts the audio thread.</summary>
    public void Initialize()
    {
        if (_provider.Initialize())
        {
            _bank.Initialize(Paths.Sounds);
            _isInitialized = true;
            _voiceManager = new VoiceManager(this, _bank, 256) { Hearing = Hearing };
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
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            
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
            int elapsed = (int)System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
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
        // Every voice is started and placed here: nothing that takes longer than a frame belongs in it.
        using var placing = PlacementThread.Enter();

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

        // The queues are drained one kind at a time, but what was asked of one voice is done in the order it was
        // asked: a placing, a start or a submission asked before the voice's stop is not done after it. Drained
        // kind by kind, a voice placed and then stopped in one frame was placed after its stop (a held horn let
        // go), and a submission queued before a stop took the stop back (the budget placed a stopped voice once
        // more).
        _stoppedAt.Clear();
        while (_stopRequests.TryDequeue(out var stop))
        {
            _stoppedAt[stop.Id] = stop.Seq;
            // A voice the manager does not own (started directly) is stopped at the provider, or never.
            bool owned = _voiceManager?.RequestStop(stop.Id) ?? false;
            if (!owned) _provider.StopSound(stop.Id);
        }

        while (_acousticPaths.TryDequeue(out var kvp))
        {
            if (StoppedAfter(kvp.Path.Key, kvp.Seq)) continue;
            _provider.SetAcousticPath(kvp.Path.Key, kvp.Path.Value);
        }

        while (_submissionQueue.TryDequeue(out var s))
        {
            if (StoppedAfter(s.E.EntityId, s.Seq)) continue;
            _voiceManager?.Submit(s.E);
        }

        while (_refreshQueue.TryDequeue(out var r))
        {
            if (StoppedAfter(r.E.EntityId, r.Seq)) continue;
            _voiceManager?.Refresh(r.E);
        }

        while (_sustainQueue.TryDequeue(out var h))
        {
            if (StoppedAfter(h.E.EntityId, h.Seq)) continue;
            if (_provider.IsPlaying(h.E.EntityId)) _voiceManager?.Refresh(h.E);
            else _voiceManager?.Submit(h.E);
        }

        while (_directPlayQueue.TryDequeue(out var d))
        {
            if (StoppedAfter(d.E.EntityId, d.Seq)) continue;
            _provider.PlaySpatialSound(d.E);
        }

        // A voice sounded (Sound) is started if it is not sounding, here and now, and otherwise placed with the
        // rest, in the order the placings were asked for.
        _placings.Clear();
        while (_soundQueue.TryDequeue(out var o))
        {
            if (StoppedAfter(o.E.EntityId, o.Seq)) continue;
            if (_provider.IsPlaying(o.E.EntityId)) _placings.Add(o);
            else _provider.PlaySpatialSound(o.E);
        }
        while (_updateAttributesQueue.TryDequeue(out var u))
        {
            if (StoppedAfter(u.E.EntityId, u.Seq)) continue;
            _placings.Add(u);
        }
        if (_placings.Count > 1) _placings.Sort(static (a, b) => a.Seq.CompareTo(b.Seq));
        foreach (var p in _placings) _provider.UpdateSpatialAttributes(p.E);

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
        _voiceManager = new VoiceManager(this, _bank, 256) { Hearing = Hearing };
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

    /// <summary>Binaural voices free (IAudioProvider.SpatialVoicesFree); without a mixer, no limit.</summary>
    public int SpatialVoicesFree => _isInitialized ? _provider.SpatialVoicesFree : int.MaxValue / 4;

    /// <summary>See IAudioProvider.PlanTrainSlot.</summary>
    public void PlanTrainSlot(string preset, string train, int slot, OpenFPS.Client.AudioEngine.Fmod.TrainSlotPlan plan)
    {
        if (_isInitialized) _provider.PlanTrainSlot(preset, train, slot, plan);
    }

    /// <summary>See IAudioProvider.PlanPieces.</summary>
    public void PlanPieces(int voiceId, OpenFPS.Client.AudioEngine.Core.Nature.ExtendedSources.LineCut cut)
    {
        if (_isInitialized) _provider.PlanPieces(voiceId, cut);
    }

    /// <summary>See IAudioProvider.PrepareTrain.</summary>
    public void PrepareTrain(string preset, string train)
    {
        if (_isInitialized) _provider.PrepareTrain(preset, train);
    }

    /// <summary>See IAudioProvider.PrepareVoice.</summary>
    public void PrepareVoice(string physicalKey, int seed)
    {
        if (_isInitialized) _provider.PrepareVoice(physicalKey, seed);
    }

    public void SetListenerEnclosure(float lowDb, float midDb, float highDb)
    {
        if (_isInitialized) _provider.SetListenerEnclosure(lowDb, midDb, highDb);
    }

    /// <summary>The ground under the listener (IAudioProvider.SetListenerGround). Thread-safe: one float.</summary>
    public void SetListenerGround(float groundY)
    {
        if (_isInitialized) _provider.SetListenerGround(groundY);
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

    /// <summary>The places the mixer plays traced, for the acoustic worker to configure on its context.
    /// Null without FMOD: the worker then traces into a set of its own that nothing plays.</summary>
    internal OpenFPS.Client.Core.AudioEngine.SteamAudio.TracedReverbSet? Traced
        => (_provider as OpenFPS.Client.AudioEngine.Fmod.FmodAudioProvider)?.Traced;

    /// <summary>The /reverb readout (FmodAudioProvider.TracedReverbStatus).</summary>
    public string TracedReverbStatus()
        => (_provider as OpenFPS.Client.AudioEngine.Fmod.FmodAudioProvider)?.TracedReverbStatus()
           ?? "No trace yet — the scene is still being built.";

    /// <summary>The /echoes readout (FmodAudioProvider.TracedEchoesStatus).</summary>
    public string TracedEchoesStatus()
        => (_provider as OpenFPS.Client.AudioEngine.Fmod.FmodAudioProvider)?.TracedEchoesStatus()
           ?? $"Echoes {(Mix.TracedEchoesOn ? "on" : "off")}. No tracer yet — the scene is still being built.";

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
            emitter.DelayMs += dist / Atmosphere.SpeedOfSound * 1000.0f;
        }

        _submissionQueue.Enqueue((Next(), emitter));
    }

    /// <summary>
    /// A fresh placement for a voice the budget already holds, which never starts one: a repeating
    /// one-shot between its firings (a PA mid-announcement) is placed and occluded from where it is now,
    /// and a play that has ended is not begun again. Without it the budget re-applied the submission it
    /// started with, and its placement stamp aged by the length of the line.
    /// </summary>
    public void Refresh(SpatialEmitter emitter)
    {
        if (!_isInitialized) return;
        _refreshQueue.Enqueue((Next(), emitter));
    }

    /// <summary>A sustained voice in the budget: submitted while it is not sounding, refreshed while it is
    /// (<see cref="Refresh"/>), decided on the audio thread. Its flight time is added only when submitted.</summary>
    public void Sustain(SpatialEmitter emitter)
    {
        if (!_isInitialized) return;
        if (emitter.Type != EmitterType.UI)
        {
            Vector3 lPos;
            lock (_stateLock) { lPos = _listenerPos; }
            emitter.DelayMs += Vector3.Distance(lPos, emitter.Position) / Atmosphere.SpeedOfSound * 1000.0f;
        }
        _sustainQueue.Enqueue((Next(), emitter));
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
            _directPlayQueue.Enqueue((Next(), emitter));
        }
    }

    /// <summary>
    /// A voice outside the budget, sounded: started if it is not sounding when the audio thread comes to it,
    /// and otherwise placed. The one call for a voice its owner sounds every frame (VoiceRegistry.Sound): decided
    /// on the audio thread, in the order asked, so a stop asked in the same frame is never undone by a start
    /// asked before it, nor a start lost to a voice that ended meanwhile.
    /// </summary>
    public void Sound(SpatialEmitter emitter)
    {
        if (!_isInitialized) return;
        if (Thread.CurrentThread == _audioThread)
        {
            if (_provider.IsPlaying(emitter.EntityId)) _provider.UpdateSpatialAttributes(emitter);
            else _provider.PlaySpatialSound(emitter);
        }
        else
        {
            _soundQueue.Enqueue((Next(), emitter));
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
            _updateAttributesQueue.Enqueue((Next(), emitter));
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
        _stopRequests.Enqueue((Next(), entityId));
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
        _acousticPaths.Enqueue((Next(), new KeyValuePair<int, AcousticPathData>(entityId, path)));
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
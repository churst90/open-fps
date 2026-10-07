using System.Numerics;
using OpenFPS.Client.Services;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Client.AudioEngine.Acoustics;
using Serilog;

namespace OpenFPS.Client.Core;

/// <summary>
/// Turns the world's entities into sounds: emitters, their acoustic paths, and the budgets that decide
/// which of them get a voice.
/// </summary>
public class ClientAudioSystem
{
    private readonly AudioEngineFacade _audio;
    private readonly SoundMappingService _sounds;
    private readonly LocalPlayerState _state;
    private readonly DrivingAids _drivingAids;
    private readonly BeaconAids _beacons;
    /// <summary>Beacon categories, policies and the player's switches. See BeaconAids.</summary>
    public BeaconAids Beacons => _beacons;
    /// <summary>The driver's cues and readout. See DrivingAids.</summary>
    public DrivingAids Driving => _drivingAids;
    private readonly SpatialService _spatial; 
    private readonly SpatialAcoustics _acoustics;

    /// <summary>
    /// The short sounds the world reports: a door shutting, glass landing, a round striking a wall.
    /// Here rather than beside the network code because a transient takes the same occlusion, reverb
    /// and placement as everything else.
    /// </summary>
    public WorldAudioPlayer WorldAudio { get; }
    private readonly AsyncAcousticWorker _acousticWorker;
    private readonly HashSet<string> _preloadedSounds = new();

    private int _ownEntityId = -1;
    /// <summary>The player's own entity, set at spawn; passed to the SpatialService so occlusion rays
    /// ignore it.</summary>
    public int OwnEntityId
    {
        get => _ownEntityId;
        set { _ownEntityId = value; _spatial.OwnEntityId = value; }
    }
    
    private WorldSnapshot? _lastSnapshot;
    private AcousticMap? _lastAcousticMap;
    private int _frameCount = 0;

    /// <summary>The walls answering the live engines. See EngineReflections.</summary>
    private readonly EngineReflections _engineEchoes = new();

    /// <summary>The voice id of one image-source reflection slot of a source. A slot keeps its wall
    /// (<see cref="AssignReflectionSlots"/>).</summary>
    private static int ReflectionVoiceId(int sourceId, int slot)
        => -30000 - (sourceId * (EarlyReflections.MaxArrivals + 1)) - slot;

    /// <summary>Which of a source's reflection slots answered this frame; the rest are retired.</summary>
    private readonly bool[] _slotLive = new bool[EarlyReflections.MaxArrivals];

    /// <summary>The surface (AcousticPathData.ReflectionId) each reflection slot of a source plays, -1
    /// for none yet. Kept after the wall stops answering, so it gets its own voice back if it returns.</summary>
    private readonly Dictionary<int, int[]> _reflectionSlots = new();

    /// <summary>This frame's slot for each path of a source (-1: none), by <see cref="AssignReflectionSlots"/>.</summary>
    private int[] _pathSlot = new int[8];

    /// <summary>Whether a source's reflection slot still has a voice (fading out counts), made once.</summary>
    private Func<int, int, bool> _reflectionSounding => _reflectionSoundingCached ??= (source, slot) => _audio.IsPlaying(ReflectionVoiceId(source, slot));
    private Func<int, int, bool>? _reflectionSoundingCached;

    /// <summary>
    /// Whether a source's reflections are played as copies of it. A synthesised source has no file to play
    /// a delayed copy of: its id names a model, and playing it as a sample failed every frame per source
    /// (121 window units retrying a file load is most of a game loop). Decided by IsSynth, not by a prefix
    /// list that misses new kinds. A rendered source is answered by its own path (EngineReflections) or by
    /// nothing.
    /// </summary>
    private static bool PlaysCopies(WorldSnapshot world, int id)
    {
        if (!world.Entities.TryGetValue(id, out var snap)) return false;
        var emitter = snap.Definition.SoundEmitter;
        string sound = emitter.SoundId ?? "";
        return !emitter.IsSynth
            && !sound.StartsWith("engine:", StringComparison.OrdinalIgnoreCase)
            && !sound.StartsWith("ENGINE/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gives each reflection a slot by its surface: a wall keeps the slot it had, and a new wall takes a
    /// slot that is free and silent, or waits. By position in the list instead, a wall joining or leaving
    /// the four loudest moved every wall after it to the next voice, which jumped from one image to
    /// another while it played. <paramref name="slotOf"/> gets each path's slot, -1 for none.
    /// </summary>
    internal static void AssignReflectionSlots(int[] surfaceBySlot, IReadOnlyList<AcousticPathData> paths,
                                               int[] slotOf, int sourceId, Func<int, int, bool> sounding)
    {
        Span<bool> taken = stackalloc bool[surfaceBySlot.Length];
        for (int i = 0; i < paths.Count; i++)
        {
            slotOf[i] = -1;
            if (!paths[i].IsReflection) continue;
            int s = Array.IndexOf(surfaceBySlot, paths[i].ReflectionId);
            if (s >= 0 && !taken[s]) { slotOf[i] = s; taken[s] = true; }
        }
        for (int i = 0; i < paths.Count; i++)
        {
            if (!paths[i].IsReflection || slotOf[i] >= 0) continue;
            for (int s = 0; s < surfaceBySlot.Length; s++)
            {
                if (taken[s] || sounding(sourceId, s)) continue;
                surfaceBySlot[s] = paths[i].ReflectionId;
                slotOf[i] = s; taken[s] = true;
                break;
            }
        }
    }

    /// <summary>How far a result's source may be from where a non-entity voice is now and still be
    /// taken as that voice's own. A pooled one-shot id reused a few seconds later is almost always
    /// further than this from its previous occupant; a car between two ticks is not a non-entity.</summary>
    internal const float StaleResultMetres = 1.0f;

    /// <summary>Is this cached result an answer about THIS voice? An entity keeps its last result
    /// between ticks; a voice that is not an entity — a footstep, a shot, anything from an id pool —
    /// takes a result only if it was computed for roughly where the voice is now, because the
    /// worker's cache is keyed by id and the id's previous occupant was somewhere else.</summary>
    internal static bool ResultIsForThisVoice(WorldSnapshot world, int id, Vector3 sourcePos, List<AcousticPathData> paths)
    {
        if (paths.Count == 0 || world.Entities.ContainsKey(id)) return true;
        return Vector3.DistanceSquared(paths[0].SourcePosition, sourcePos) <= StaleResultMetres * StaleResultMetres;
    }
    private readonly List<(int Id, float Key, float D2)> _engineDistances = new();
    private double _lastEngineTime;

    /// <summary>
    /// A sounding car ranks as if this much nearer, so a silent one must be decisively closer to take its
    /// voice. Ranking by distance alone churns: on an oval two cars swap order several times a lap, and
    /// each swap restarts a synth with cold pipes and a stopped crank, heard as a car stuttering.
    /// </summary>
    private const float EngineKeepBias = 0.75f;

    /// <summary>No engine is dropped within this long of being started, whatever the ranking says, so
    /// a fast car crossing the boundary is not started and stopped inside a second.</summary>
    private const double EngineMinimumHoldSeconds = 2.5;

    /// <summary>
    /// The audio update's cap, here so both heads share it. Their loop polls the network at about 200 Hz
    /// (a 5 ms sleep); nothing in the update resolves faster than a frame, so uncapped two updates in
    /// three would be unheard work on the thread that services the socket.
    /// </summary>
    public const double UpdateHz = 60.0;
    /// <summary>
    /// A loud exhaust at one metre under load: the fallback for a vehicle that declares no level. Every
    /// preset declares one (<c>VehicleProfile.SourceLevelDb</c>, measured with `--engine-levels`), from
    /// 91 dB for a diesel pickup to 133 for an unsilenced V10.
    /// </summary>
    public const float EngineSourceLevelDb = 116f;

    /// <summary>
    /// How many vehicles may run a live engine at once (OPENFPS_ENGINE_VOICES overrides). Each is a whole
    /// engine integrated sample by sample on the render pool; a V8 renders about nine seconds of audio per
    /// core-second in a release build (`--engine-cost`). The rest borrow a near car's ring (distant voices).
    /// </summary>
    public static readonly int EngineVoiceBudget =
        int.TryParse(Environment.GetEnvironmentVariable("OPENFPS_ENGINE_VOICES"), out int budget) && budget > 0
            ? budget : 32;

    /// <summary>
    /// The engine budget in force, steered by the measured mixer load: what an engine costs depends on
    /// everything else in the map, and a mixer at 100 % misses its deadline and crackles. It gives things
    /// up in order, a car's second reflection, then its first, and only then a car: shedding cars first
    /// sounds like voices being swapped, while nobody notices a wall stop answering. The floor is two
    /// cars; one engine on a racetrack is not a race.
    /// </summary>
    private int _adaptiveBudget = EngineVoiceBudget;

    /// <summary>Producer starves per second above which live engines are given up.</summary>
    private const float StarveCeilingPerSecond = 5f;
    private float _starveRate;
    private int _starvesSeen;
    private double _starveSampledAt = -1;
    /// <summary>
    /// Reflections per engine (OPENFPS_ENGINE_ECHOES: 0 none, 1 one; a diagnostic lever). Echoes churn
    /// more than anything else in the audio system (about 256 created and released in 98 s on the city)
    /// and are the only voices that read another voice's buffers, so taking them out is the quickest way
    /// to clear or implicate them in a mixer-thread fault.
    /// </summary>
    /// <remarks>
    /// An unsmeared echo is a coherent copy of the engine: against the direct sound a comb that sweeps as
    /// either moves (a car heard inside out), alone a point beamed from a wall. So each echo is smeared by
    /// the roughness of its wall (EngineEchoState.Scattering) and swells and fades over a few hundred
    /// milliseconds instead of switching.
    /// </remarks>
    public static readonly int EchoCeiling =
        int.TryParse(Environment.GetEnvironmentVariable("OPENFPS_ENGINE_ECHOES"), out int ec) && ec >= 0
            ? Math.Min(ec, EngineReflections.MaxEchoesPerEngine)
            : EngineReflections.MaxEchoesPerEngine;

    private int _adaptiveEchoes = EchoCeiling;
    private double _lastBudgetChange;

    /// <summary>However short the mixer runs, this many cars are fully SYNTHESIZED.</summary>
    private const int MinEngineVoices = 2;

    /// <summary>Which source each distant voice is currently bound to, so a change can rebind it.</summary>
    private readonly Dictionary<int, int> _distantBoundTo = new();

    /// <summary>Distant car voices live in their own id band.</summary>
    internal const int DistantVoiceBase = -700000;

    /// <summary>...and a machine's front outlet in another one.</summary>
    internal const int IntakeVoiceBase = -800000;
    /// <summary>Voice ids for a vehicle's siren head. Its own voice; see SirenVoiceState.</summary>
    internal const int SirenVoiceBase = -900000;

    /// <summary>
    /// How many machines may have their front outlet voiced separately (OPENFPS_FRONT_VOICES=0 keeps
    /// each on one voice, for an A/B). A car's ends are told apart only inside about 20 m, so few are
    /// needed except on a start grid. The first thing given up when the mixer runs short, before
    /// reflections: the machine stays fully audible without it.
    /// </summary>
    private static readonly int FrontVoiceBudget =
        int.TryParse(Environment.GetEnvironmentVariable("OPENFPS_FRONT_VOICES"), out int fv) && fv >= 0 ? fv : 6;
    private int _adaptiveFront = FrontVoiceBudget;

    /// <summary>
    /// How many outer places of trees, fires and fountain taps may have voices at once (ExtendedSources),
    /// nearest source first; OPENFPS_PLACE_VOICES=0 keeps each on its middle. A place given up costs only
    /// geometry (the source merges to its middle at the same level), so places are the first thing the
    /// mixer gives up and the last it takes back, a source's worth at a time.
    /// </summary>
    private static readonly int PlaceVoiceBudget =
        int.TryParse(Environment.GetEnvironmentVariable("OPENFPS_PLACE_VOICES"), out int pv) && pv >= 0 ? pv : 36;
    private int _adaptivePlaces = PlaceVoiceBudget;
    private const int PlaceBudgetStep = 6;

    /// <summary>Which machines currently have their front outlet on a voice of its own.</summary>
    private readonly HashSet<int> _frontVoiced = new();
    /// <summary>Which vehicles currently have a siren voice running.</summary>
    private readonly HashSet<int> _sirenVoiced = new();
    private readonly List<int> _frontRetiring = new();

    /// <summary>How many engines may be BUILT in one audio update. See ChooseLiveEngines.</summary>
    private const int NewEnginesPerUpdate = 2;

    /// <summary>
    /// How many standing machines (air conditioners, mowers, plant) may run live at once. Its own budget:
    /// a map carries tens of vehicles but hundreds of small machines (the city has 114 window units).
    /// Ranked not by distance but by how loud each would be here, at the ear (Loudness.RenderedGain, then
    /// Loudness.HeardGain): a rooftop condenser at 65 dB 80 m away beats a 59 dB window unit behind a hedge.
    /// OPENFPS_MACHINE_VOICES overrides, and 0 is a valid answer: every machine and aircraft voice off.
    /// </summary>
    public static readonly int MachineVoiceBudget =
        int.TryParse(Environment.GetEnvironmentVariable("OPENFPS_MACHINE_VOICES"), out int mbudget) && mbudget >= 0
            ? mbudget : 10;
    private int _adaptiveMachines = MachineVoiceBudget;

    /// <summary>However short the mixer runs, this many standing machines keep a voice. One, because
    /// the one you are standing next to is the one you would notice going silent.</summary>
    private const int MinMachineVoices = 1;

    /// <summary>Nothing at all, when the budget is zero: the adaptive floor must not put one back.</summary>
    private int MachineFloor => MachineVoiceBudget == 0 ? 0 : MinMachineVoices;

    /// <summary>Which physical models have a live voice now (and, below, when each started).</summary>
    private readonly HashSet<int> _liveMachines = new();
    /// <summary>The ids whose acoustic path is asked for this frame. See step 5.</summary>
    private readonly HashSet<int> _pathIds = new();
    private readonly Dictionary<int, double> _machineStarted = new();
    private readonly List<int> _machineRetiring = new();
    private readonly List<(int Id, float Key, float Level)> _machineOrder = new();

    /// <summary>
    /// How many borrowed voices may sound at once, beyond the synthesized ones. A borrowed voice runs no
    /// engine but is still placed (HRTF, filters, a channel), so this second budget is what lets a map
    /// carry any number of cars: the nearest handful are voiced, the rest are too far to pick out.
    /// </summary>
    private int _adaptiveDistant = MaxDistantVoices;
    private const int MaxDistantVoices = 12;
    private const int MinDistantVoices = 4;

    /// <summary>Which cars currently hold a borrowed voice, so the rest can be let go.</summary>
    private readonly HashSet<int> _distantVoiced = new();

    /// <summary>Past this the callback is close enough to its deadline to start shedding work.</summary>
    private const float MixerLoadCeiling = 0.70f;
    /// <summary>And under this there is room to take a voice back.</summary>
    private const float MixerLoadFloor = 0.45f;
    private const double BudgetSettleSeconds = 1.0;

    /// <summary>
    /// The budget is left alone this long after a map load, and after that the load must stay over the
    /// ceiling this long together before anything is given up. During a load FMOD's dsp percentage pins at
    /// 100 % while the mixer is stalled (a GC, a producer it waits on), which is not a shortage: shedding
    /// then and re-adding a second later builds new engines at the worst moment, heard as cars appearing
    /// and vanishing on the first lap.
    /// </summary>
    private const double BudgetHoldSeconds = 3.0;
    private const double OverCeilingSeconds = 0.75;
    private double _budgetHeldUntil;
    private double _overCeilingSince = -1;

    /// <summary>Called when a map starts loading and again at spawn: the mixer's load reading cannot be
    /// trusted for a few seconds (BudgetHoldSeconds).</summary>
    public void NoteSceneLoading() => _budgetHeldUntil = _now() + BudgetHoldSeconds;
    private readonly UpdateThrottle _throttle = new(UpdateHz);
    /// <summary>Seconds since this system was built. A stopwatch in the game; a test hands in its
    /// own, so it can tick the throttled update and step past the engine hold without sleeping.</summary>
    private readonly Func<double> _now;

    // Near-field boundary probes: rebuilt each frame from the listener's rotation (ProbeDirections is
    // head space) into buffers owned and reused here, since this runs every audio frame.
    private readonly Vector3[] _boundaryRays = new Vector3[BoundaryModel.ProbeDirections.Length];
    private readonly float[] _boundaryDistances = new float[BoundaryModel.ProbeDirections.Length];
    private readonly float[] _boundaryAbsorptions = new float[BoundaryModel.ProbeDirections.Length];
    private readonly string[] _boundaryMaterials = new string[BoundaryModel.ProbeDirections.Length];
    private readonly BoundaryProbe[] _boundaryProbes = new BoundaryProbe[BoundaryModel.ProbeDirections.Length];

    // Ambience beds. The map's outdoor bed plays for the whole session and is ducked by shelter; a
    // region that declares one of its own plays on top while the listener is inside it.
    private string _mapAmbienceId = "";
    private string _regionAmbienceId = "";
    private int _ambienceRegionId = int.MinValue;

    /// <summary>With <paramref name="prewarm"/> false the city's doors are not rendered in the background
    /// at start: a session with no sound has nothing to play them on.</summary>
    public ClientAudioSystem(AudioEngineFacade audio, SoundMappingService sounds, LocalPlayerState state, bool prewarm = true)
        : this(audio, sounds, state, StopwatchClock(), prewarm: prewarm) { }

    /// <summary>For tests: the same system on a clock the caller controls.</summary>
    /// <param name="audio">The engine to play through.</param>
    /// <param name="sounds">The recorded sounds by material and action.</param>
    /// <param name="state">The local player.</param>
    /// <param name="clock">Seconds, as the test steps them.</param>
    /// <param name="manualAcoustics">The acoustic worker runs no thread: the test steps it between
    /// updates (<see cref="AsyncAcousticWorker.StepForTest"/>), the rain survey runs in place, and no
    /// door or thunder renders start in the background. For the emitter-stream replay, which must come
    /// out the same on every run.</param>
    /// <param name="seed">Seeds what the game leaves to chance (birds, near rain drops, a footstep's
    /// jitter). Null: unseeded, as in the game.</param>
    /// <param name="prewarm">Render the city's doors in the background at start (<see cref="WorldAudioPlayer"/>).
    /// A test that plays no door has no use for minutes of a core.</param>
    internal ClientAudioSystem(AudioEngineFacade audio, SoundMappingService sounds, LocalPlayerState state,
                               Func<double> clock, bool manualAcoustics = false, int? seed = null, bool prewarm = true)
    {
        _now = clock;
        _audio = audio;
        _sounds = sounds;
        _state = state;
        _drivingAids = new DrivingAids(audio);
        _spatial = new SpatialService();
        _acoustics = new SpatialAcoustics(_spatial);
        // After the acoustics, which it needs: a beacon behind a wall is not blipped.
        _beacons = new BeaconAids(audio, acoustics: _acoustics);
        // Shares this system's acoustics, so a rendered latch takes exactly the path a recorded one would.
        WorldAudio = new WorldAudioPlayer(_audio, _acoustics, prewarm: prewarm && !manualAcoustics);
        WorldAudio.HornReceived = StartHorn;
        WorldAudio.TrainSignalReceived = StartTrainSignal;
        WorldAudio.Ground = ApplyRecordedGround;
        _birds = new BirdLife(audio, _acoustics, seed);
        _rain = new RainField(audio, _acoustics, seed) { SurveyInPlace = manualAcoustics };
        _stepRandom = seed is int s ? new Random(s) : Random.Shared;
        WorldAudio.Received = message => _birds.Heard(message, OpenFPS.Common.AudioClock.Now);
        _audio.RoutesSource = () => _acoustics.Routes;
        _acousticWorker = new AsyncAcousticWorker(_acoustics) { Manual = manualAcoustics };
        WorldAudio.Worker = _acousticWorker;
        _acousticWorker.Start();
    }

    /// <summary>The acoustic worker, for a test that steps it (<see cref="AsyncAcousticWorker.Manual"/>).</summary>
    internal AsyncAcousticWorker AcousticWorker => _acousticWorker;

    /// <summary>A footstep's level and pitch jitter: the shared generator in the game, seeded in a replay.</summary>
    private readonly Random _stepRandom;

    private static Func<double> StopwatchClock()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        return () => clock.Elapsed.TotalSeconds;
    }

    /// <summary>
    /// A model was changed in the world editor (ModelUpdate): every voice playing it is stopped, and the
    /// next update starts it again from the new model, so the change is heard at once. The levels and
    /// headroom read from models are forgotten too, since they are the old model's. Returns the
    /// entities whose voices were restarted.
    /// </summary>
    public List<int> ModelChanged(string kind, string id, WorldSnapshot snapshot)
    {
        _physicalLevels.Clear();
        _physicalHeadroom.Clear();
        var restarted = new List<int>();
        foreach (var (entityId, entity) in snapshot.Entities)
        {
            string sound = entity.Definition?.SoundEmitter.SoundId ?? "";
            if (!OpenFPS.Common.Editing.ModelKinds.TryModelOfSound(sound, out var k, out var i)) continue;
            if (k != kind || !i.Equals(id, StringComparison.OrdinalIgnoreCase)) continue;
            ForgetEntity(entityId);
            restarted.Add(entityId);
        }
        return restarted;
    }

    /// <summary>
    /// Silences everything an entity made sound with, once the server says it is gone. A voice keeps
    /// playing on its own once started, so a looping emitter on a despawned object would otherwise stay
    /// for the session; the voices derived from it (reflections, outlets, places) carry ids of their own.
    /// </summary>
    public void ForgetEntity(int entityId)
    {
        _groundCache.Remove(entityId);
        _audio.StopSound(entityId);
        _liveEngines.Remove(entityId);
        _engineStarted.Remove(entityId);
        _engineRetiring.Remove(entityId);
        _engineEchoes.Forget(entityId, _audio);
        _reflectionSlots.Remove(entityId);
        int distant = DistantVoiceBase - Math.Abs(entityId);
        if (_distantBoundTo.Remove(distant)) _audio.StopSound(distant);
        if (_frontVoiced.Remove(entityId)) _audio.StopSound(IntakeVoiceBase - Math.Abs(entityId));
        if (_cabinVoiced == entityId)
        {
            for (int p = 1; p < _cabinVoicedPaths; p++) _audio.StopSound(CabinVoiceId(p));
            _cabinVoiced = int.MinValue; _cabinVoicedPaths = 0;
        }
        if (_spreading.Remove(entityId, out var spreading) && spreading.Voiced) StopOuter(entityId, spreading, now: true);
        if (_sirenVoiced.Remove(entityId)) _audio.StopSound(SirenVoiceBase - Math.Abs(entityId));
        _sirenControl.Remove(entityId);
        if (_heldHorns.Remove(entityId)) _audio.StopSound(HeldHornVoiceBase - Math.Abs(entityId));
        _frontRetiring.Remove(entityId);
        _placed.Remove(entityId);
        for (int slot = 0; slot < EarlyReflections.MaxArrivals; slot++)
            _audio.StopSound(ReflectionVoiceId(entityId, slot));

        _acousticWorker.Forget(entityId);
    }

    /// <summary>
    /// Silences the whole world, for leaving it. Nothing here runs again until the next map, because
    /// <see cref="Update"/> is only called in the world.
    /// </summary>
    public void LeaveWorld(IEnumerable<int> entityIds)
    {
        foreach (int id in entityIds) ForgetEntity(id);
        WorldAudio.Clear();
        if (_mapAmbienceId.Length > 0) _audio.StopAmbientBed(_mapAmbienceId);
        if (_regionAmbienceId.Length > 0) _audio.StopAmbientBed(_regionAmbienceId);
        _mapAmbienceId = "";
        _regionAmbienceId = "";
        _ambienceRegionId = int.MinValue;
        _rain.Stop();
        _audio.StopAllWorldSounds();
        // Nobody is standing anywhere: no wind at the ears until the next map.
        _audio.SetEarWind(null);
        // Entity ids belong to the map just left: whoever had one there is nobody here.
        _talkersVoiced.Clear();
        _talkerRooms.Clear();
        OpenFPS.Client.AudioEngine.Fmod.Talkers.Clear();
    }

    /// <summary>The water in a wheel path of the roads, mm (WorldSnapshot.RoadWaterMm).</summary>
    private float _roadWaterMm;

    /// <summary>
    /// Called every frame from the game loop (throttled to <see cref="UpdateHz"/>). The listener is at the
    /// smoothed VisualPosition, so a server correction is not heard as a jump.
    /// </summary>
    public void Update(WorldSnapshot world)
    {
        if (!_throttle.ShouldRun(_now())) return;

        using var _perf = PerfProbe.Measure("audio.update");

        // This is the only place an emitter's position is resubmitted, so the gap between two runs is how
        // long a car's engine sits still in the air while the car drives on. Measured here for that.
        double nowSec = _now();
        if (_lastUpdateAt > 0)
        {
            double gapMs = (nowSec - _lastUpdateAt) * 1000.0;
            if (gapMs > _worstGapMs) _worstGapMs = gapMs;
        }
        _lastUpdateAt = nowSec;
        long startTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        long stageTicks = startTicks;
        try
        {

        _frameCount++;
        // The previous snapshot, kept before assigning: comparing `world` with `_lastSnapshot` after it
        // compared it with itself and silently disabled the moving-region check below. The two may also be
        // the same object (an unchanged world), and a zero delta is then right.
        var previous = _lastSnapshot;
        _lastSnapshot = world;
        _acousticWorker.UpdateWorld(world);
        
        Vector3 visualEyePos = _state.VisualPosition + new Vector3(0, _state.EyeHeight, 0);
        _groundEar = visualEyePos;
        // The cabin you sit in, for the traced reverb: which vehicle, and your ear in its frame.
        {
            string? preset = null; Vector3 local = default;
            if (_state.IsRiding && world.Entities.TryGetValue(_state.RidingEntityId, out var ride)
                && ride.Definition.SoundEmitter.SoundId is { } rideSound
                && rideSound.StartsWith("engine:", StringComparison.OrdinalIgnoreCase))
            {
                preset = rideSound[7..];
                local = Vector3.Transform(visualEyePos - ride.Transform.Position, Quaternion.Inverse(ride.Transform.Rotation));
            }
            OpenFPS.Client.Core.AudioEngine.SteamAudio.TracedReverbSet.RideIn(preset, local);
        }
        _groundWorld = world;

        int listenerRegionId = _acoustics.GetRegionAt(world, visualEyePos);
        // Kept: your own feet are submitted from the game thread between updates and need the room to
        // reverberate in (SubmitFootstep).
        _listenerRegion = listenerRegionId;

        // Not the wind: a uniform wind moves the source, the listener and the air together and shifts
        // no pitch. A share of it added here bent every pitch in the world with each gust.
        Vector3 listenerVelocity = _state.Velocity;
        // Sitting in something, you face the way it faces: the session sets your heading from the
        // vehicle every frame (ClientGameSession.FollowRide), so the ears and the compass agree.
        var listenerRotation = _state.Rotation;
        if (_state.IsRiding && world.Entities.TryGetValue(_state.RidingEntityId, out var carrying))
        {
            // ...and you move at its speed. A passenger is not predicted, so their own velocity reads
            // zero, which against the vehicle's moving voice is a Doppler shift on your own bus.
            listenerVelocity = carrying.Velocity;
        }

        // The mixer reads the wind field itself once a block (EarWindVoice); this is where the head is,
        // which way it faces and what is round it.
        var earListener = EarListener(world, visualEyePos, listenerVelocity, listenerRotation);
        _audio.SetEarWind(earListener);
        _audio.UpdateListener(visualEyePos, listenerRotation, listenerVelocity, listenerRegionId);
        _audio.UpdateShelter(_state.ShelterFactor);
        WorldAudio.ListenerVehicleId = _state.RidingEntityId;
        WorldAudio.Cabins = _cabins;
        WorldAudio.SelfId = OwnEntityId;
        WorldAudio.Self ??= () => (_state.Position, _state.Rotation);
        _beacons.Update(world, visualEyePos, _now(), OwnEntityId);
        _drivingAids.Update(world, _state, _now());
        // The rest of the world through the glass, if you sit in anything with a roof.
        var (encLow, encMid, encHigh) = CabinEnclosure(world);
        // Now and then, forget the windows of vehicles that have gone.
        if (_frameCount % 600 == 0) _cabins.Forget(world.Entities.ContainsKey);
        _audio.SetListenerEnclosure(encLow, encMid, encHigh);
        // Temperature reaches the mix as the speed of sound: c = 331.3 + 0.606·T.
        _audio.SetAirTemperature(world.Temperature);

        // The listener's reverb decay from the Steam Audio reflection sim, when there is one, in place of
        // the Sabine estimate.
        if (_acousticWorker.TryGetListenerReverbDecayMs(out float simReverbMs))
        {
            _audio.SetSimulatedReverbDecay(simReverbMs, _acousticWorker.ListenerEnclosure,
                                           _acousticWorker.ListenerHfDecayRatio,
                                           _acousticWorker.ListenerLfDecayRatio);
        }
        
        if (world.AcousticMap != null)
        {
            if (world.AcousticMap != _lastAcousticMap)
            {
                _audio.SetAcousticMap(world.AcousticMap);
                _lastAcousticMap = world.AcousticMap;
            }
            else
            {
                // Regions that moved. Over the region list, not the world: walking every entity is six
                // thousand struct copies a frame on the city for six hundred regions, almost none movable.
                foreach (int regionId in world.RegionEntityIds)
                {
                    if (world.Entities.TryGetValue(regionId, out var snap))
                    {
                        if (previous != null && previous.Entities.TryGetValue(snap.Id, out var oldSnap))
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
        
        UpdateAcousticState(world, visualEyePos, listenerRegionId);
        // Through the echo system: the grandstand that answers a car answers the people on it too, on the
        // same terms (obstruction-tested).
        WorldAudio.Update(world, visualEyePos, OpenFPS.Common.AudioClock.Now, _engineEchoes);

        // Six rays out of the head, turned by its rotation, so the answer is "concrete half a metre to my
        // left"; each becomes its own early reflection (BoundaryModel, BoundaryProximityProcessor). This is
        // what tells a corridor from a doorway from open air, and changes as you turn.
        UpdateBoundaryProbes(world, visualEyePos);

        UpdateAmbience(world, listenerRegionId);

        Stage(0, ref stageTicks);     // everything up to here: region, listener, map, probes, ambience

        // The acoustic path of every playing sound, and of every car and machine holding a live slot even
        // when the mixer is not playing it. Asking only about playing voices let an occluded-silent voice's
        // result expire (the worker's 5 s TTL), fall back to an unoccluded path and burst back every five
        // seconds: ambience that "stutters and cuts out".
        _pathIds.Clear();
        _pathIds.UnionWith(_audio.GetActiveSpatialSoundIds());
        _pathIds.UnionWith(_liveEngines);
        _pathIds.UnionWith(_liveMachines);
        _pathIds.UnionWith(_horns.Keys);      // a horn takes its vehicle's path, borrowed voice or not
        _pathIds.UnionWith(_sirenCars);       // and so does a siren
        // ...and a car voiced from afar, whose borrowed id sits below the reflection range and gets no path
        // of its own. Without this, twelve city cars were never occluded or darkened by the air: the
        // white-noise wash from the map's edge (high band 10 dB under the low, against 42 for the rest).
        _pathIds.UnionWith(_distantVoiced);
        foreach (var id in _pathIds)
        {
            // Ids below -5000 are copies and voices that work out their own paths (a wall's copy, your
            // voice and your room's answer, a talker, a horn, a siren). Never reflections of reflections:
            // that feeds back for ever, and a copy asked about from its image is traced through its own wall.
            if (id < -5000)
            {
                continue; 
            }

            // Asked about the point the sound comes out of, not the entity's origin: for anything that
            // drives the origin is its contact patch, and a probe there was half buried (about half the
            // samples blocked on open road).
            Vector3 sourcePos;
            float sourceRadius = OpenFPS.Common.AudioEmission.DefaultOcclusionRadius;
            if (world.Entities.TryGetValue(id, out var snap))
            {
                sourcePos = OpenFPS.Common.AudioEmission.PointFor(snap);
                sourceRadius = OpenFPS.Common.AudioEmission.OcclusionRadiusFor(snap);
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
            
            if (_frameCount % updateRate == Math.Abs(id) % updateRate)
            {
                _acousticWorker.EnqueueRequest(new AcousticRequest
                {
                    EntityId = id,
                    ListenerPos = visualEyePos,
                    SourcePos = sourcePos,
                    SourceRadius = sourceRadius,
                });
            }
            
            if (_acousticWorker.TryGetResult(id, out var paths) && ResultIsForThisVoice(world, id, sourcePos, paths))
            {
                Array.Clear(_slotLive);
                if (_pathSlot.Length < paths.Count) _pathSlot = new int[paths.Count * 2];
                if (PlaysCopies(world, id))
                {
                    if (!_reflectionSlots.TryGetValue(id, out var slots))
                        _reflectionSlots[id] = slots = Enumerable.Repeat(-1, EarlyReflections.MaxArrivals).ToArray();
                    AssignReflectionSlots(slots, paths, _pathSlot, id, _reflectionSounding);
                }
                else Array.Fill(_pathSlot, -1);
                for (int pi = 0; pi < paths.Count; pi++)
                {
                    var path = paths[pi];
                    if (!path.IsReflection)
                    {
                        // What moves is not in the acoustic scene, so a bus between you and a car
                        // is put back here as the barrier it is. See VehicleShadow.
                        var shadowed = path;
                        OpenFPS.Client.AudioEngine.Acoustics.VehicleShadow.Apply(
                            ref shadowed, world, id, sourcePos, visualEyePos, _state.RidingEntityId);
                        _audio.SetAcousticPath(id, shadowed);
                        // A horn or a siren on this vehicle is behind the same bus.
                        if (_horns.ContainsKey(id)) _audio.SetAcousticPath(HornVoiceBase - Math.Abs(id), shadowed);
                        if (_heldHorns.ContainsKey(id)) _audio.SetAcousticPath(HeldHornVoiceBase - Math.Abs(id), shadowed);
                        // Placed where the siren's own update places it, or the two writers pull
                        // the image between two bearings every frame (see SirenApparent).
                        if (_sirenVoiced.Contains(id) && world.Entities.TryGetValue(id, out var sirenCar))
                        {
                            var sirenPath = shadowed;
                            sirenPath.ApparentPosition = SirenApparent(shadowed, SirenMouth(sirenCar), visualEyePos);
                            _audio.SetAcousticPath(SirenVoiceBase - Math.Abs(id), sirenPath);
                        }
                        // And so is its borrowed engine, if it is voiced from afar.
                        if (_distantVoiced.Contains(id)) _audio.SetAcousticPath(DistantVoiceBase - Math.Abs(id), shadowed);
                        // And a tree's, a fire's or a fountain tap's other places: behind the same wall,
                        // each heard from its own offset round the same edge (ExtendedSources).
                        if (_spreading.TryGetValue(id, out var spread) && spread.Voiced)
                            for (int k = 1; k < spread.At.Length; k++)
                            {
                                var placePath = shadowed;
                                placePath.ApparentPosition = shadowed.ApparentPosition + (spread.At[k] - spread.At[0]);
                                _audio.SetAcousticPath(PlaceVoiceId(id, k), placePath);
                            }
                        continue;
                    }

                    // A reflection: an image source off the scene's surfaces (EarlyReflections) under Steam
                    // Audio, the hand-rolled tracer's otherwise. The tail does not carry this energy: without
                    // these a room answers from everywhere at once and a doorway is inaudible from outside.
                    int slot = _pathSlot[pi];
                    if (slot < 0 || !world.Entities.TryGetValue(id, out var originalSnap)) continue;
                    // Everywhere: the listener's traced stage plays the late tail alone, so a sustained
                    // source's first bounces are these, indoors as out.
                    _slotLive[slot] = true;

                    // One voice per slot and one wall per slot (AssignReflectionSlots). Keyed by slot, which
                    // cannot collide, not by a hash of the surface id, which can: one voice flickering
                    // between two opposite walls.
                    int reflectId = ReflectionVoiceId(id, slot);
                    var placed = _placed.TryGetValue(id, out var p)
                        ? p : (originalSnap.Definition.SoundEmitter.Volume, originalSnap.Definition.SoundEmitter.MinDistance);
                    var echo = LoopEchoLevel(path, placed.Volume, placed.MinDistance, dist);

                    var reflectEmitter = new SpatialEmitter
                    {
                        EntityId = reflectId,
                        SoundId = _sounds.ResolvePath(originalSnap.Definition.SoundEmitter.SoundId),
                        Mode = originalSnap.Definition.SoundEmitter.Mode,
                        Position = path.ApparentPosition,
                        ApparentPosition = path.ApparentPosition,
                        // What the surface and the extra distance left of it, per band (LoopEchoLevel).
                        Volume = echo.Volume,
                        MinDistance = echo.MinDistance,
                        Range = originalSnap.Definition.SoundEmitter.Range * 0.8f,
                        IsReflection = true,
                        // Played from the source voice's own position, so it is what is being heard,
                        // arriving later, not the file again from the top.
                        ReflectionOf = id,
                        DelayMs = path.ReflectionDelayMs,
                        Type = EmitterType.WorldLocked,
                        EqLow = echo.EqLow,
                        EqMid = echo.EqMid,
                        EqHigh = echo.EqHigh,
                        AirLowDb = path.AirLowDb, AirMidDb = path.AirMidDb, AirHighDb = path.AirHighDb,
                        ReflectionSpread = path.Spread,
                        // Not a reverb send: the provider uses bleed only to pull an apparent position
                        // toward the real one, and a copy's two are the same.
                        TransmissionBleed = path.MaterialAbsorption * 0.5f 
                    };

                    if (_audio.IsPlaying(reflectId))
                    {
                        // A surface that stopped answering and started again keeps its voice.
                        _audio.CancelFade(reflectId);
                        _audio.UpdateSpatialAttributes(reflectEmitter);
                    }
                    else _audio.PlayPhysicalSoundDirect(reflectEmitter);
                }

                // A surface that stopped answering fades out (a cut is a click) rather than playing on
                // from where the wall was after the listener has walked out of its reach.
                for (int slot = 0; slot < EarlyReflections.MaxArrivals; slot++)
                {
                    if (_slotLive[slot]) continue;
                    int reflectId = ReflectionVoiceId(id, slot);
                    if (_audio.IsPlaying(reflectId) && _audio.FadeOut(reflectId)) _audio.StopSound(reflectId);
                }
            }
        }

        Stage(1, ref stageTicks);     // 5: the acoustic paths of every active voice

        // Once, outside the per-entity pass: who gets a voice is a decision about the set.
        ChooseLiveEngines(world, visualEyePos);
        // The rain running off (gutters, drains, downpipes), fed before the machines are ranked so a dry
        // gutter is not given a voice. Snow stays where it falls.
        {
            var p = world.Precipitation;
            float wet = p.Falling ? (p.Kind == OpenFPS.Common.PrecipitationKind.Snow ? 0f : p.RateMmPerHour) : world.RainRateMmPerHour;
            OpenFPS.Common.Runoff.Update(wet, OpenFPS.Common.AudioClock.Now);
        }
        ChooseLiveMachines(world, visualEyePos);
        ChoosePlaces(world, visualEyePos);
        _engineEchoes.EchoesPerEngine = _adaptiveEchoes;
        _engineEchoes.SyncGeometry(world, _acoustics.ReflectionWorldFor(world));
        float engineDt = (float)Math.Max(1e-3, _now() - _lastEngineTime);
        _lastEngineTime = _now();

        Stage(2, ref stageTicks);     // 5.5: who gets a voice

        foreach (var entityId in world.AudioEntityIds)
        {
            if (entityId == OwnEntityId) continue;
            if (world.Entities.TryGetValue(entityId, out var snap))
            {
                // A placed beacon whose category is off (by the map or by you). Doors, items and cars
                // are BeaconAids'.
                if (snap.Definition.Type == EntityType.Beacon && !_beacons.IsOn(snap.Definition.Identity.BeaconCategory))
                {
                    if (_audio.IsPlaying(entityId)) _audio.StopSound(entityId);
                    continue;
                }
                ProcessAudioEmitter(world, snap, visualEyePos, engineDt);
            }
        }
        RetireSpreading();
        UpdateCabinVoices();

        long partAt = System.Diagnostics.Stopwatch.GetTimestamp();
        UpdateHorns(world, visualEyePos, OpenFPS.Common.AudioClock.Now);
        UpdateSirens(world, visualEyePos);
        UpdateOwnVoice(visualEyePos, OpenFPS.Common.AudioClock.Now);
        UpdateTalkers(world, visualEyePos, OpenFPS.Common.AudioClock.Now);
        _partMs[3] += Ms(partAt);
        partAt = System.Diagnostics.Stopwatch.GetTimestamp();
        _birds.Update(world, visualEyePos, OpenFPS.Common.AudioClock.Now);
        _rain.Update(world, visualEyePos, OpenFPS.Common.AudioClock.Now, listenerRegionId, OwnEntityId, _state.RidingEntityId);
        _partMs[2] += Ms(partAt);

        // Every source has been offered to the reflection system: it sets what next frame's reflections
        // must reach to be worth a voice.
        _engineEchoes.EndFrame();

        {
            double pass = 0; foreach (var v in _partMs) pass += v;
            if (pass > _partWorstPass) { _partWorstPass = pass; Array.Copy(_partMs, _partWorstMs, _partMs.Length); }
            Array.Clear(_partMs);
        }
        Stage(3, ref stageTicks);     // 6: building an emitter for everything that has a voice

        _audio.Update();
        Stage(4, ref stageTicks);     // 7: handing it all to the mixer
        }
        finally
        {
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - startTicks) * 1000.0
                      / System.Diagnostics.Stopwatch.Frequency;
            if (ms > _worstUpdateMs) _worstUpdateMs = ms;
        }
    }

    private double _lastUpdateAt, _worstGapMs, _worstUpdateMs;

    /// <summary>
    /// Where the audio update's time went, by stage (<see cref="StageNames"/>), worst case over the
    /// reporting interval: "the pass took 74 ms" names nothing to act on.
    /// </summary>
    private readonly double[] _stageWorstMs = new double[5];

    /// <summary>
    /// Inside the emitter stage, the costliest pass by part (<see cref="PartNames"/>). The stage alone
    /// can run 150-250 ms on the city, and every source freezes that long (reflections stepping away and
    /// catching up late).
    /// </summary>
    private readonly double[] _partMs = new double[4], _partWorstMs = new double[4];
    private double _partWorstPass;
    private static readonly string[] PartNames = { "echoes", "ground", "birds", "horns+sirens" };
    private static double Ms(long from) => (System.Diagnostics.Stopwatch.GetTimestamp() - from) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    private static readonly string[] StageNames =
        { "listener+map", "paths", "budget", "emitters", "mixer" };

    private void Stage(int stage, ref long since)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        double ms = (now - since) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        if (ms > _stageWorstMs[stage]) _stageWorstMs[stage] = ms;
        since = now;
    }

    private void UpdateAcousticState(WorldSnapshot world, Vector3 eyePos, int regId)
    {
        _state.Temperature = world.Temperature;

        _state.PrecipitationIntensity = world.PrecipitationIntensity * (1.0f - _state.ShelterFactor);
        // The water on the roads, for the vehicles whose wheels the server does not send.
        _roadWaterMm = world.RoadWaterMm;

        // The place's name, by the boxes: in a doorway (the room on your side of it, for sound) you are
        // told you are in the doorway.
        int zone = _acoustics.GetZoneAt(world, eyePos);
        _state.CurrentRegionId = zone;
        _state.CurrentRoomId = _acoustics.GetRoomAt(world, eyePos);
        if (world.AcousticMap != null && world.AcousticMap.Regions.TryGetValue(regId, out var reg))
        {
            _state.IsIndoor = reg.IsIndoor;
        }
        else
        {
            _state.IsIndoor = false;
        }
        string name = NamedPlaces.NameOf(world, zone)
                      ?? NameOfPlace(world.AcousticMap, zone, _state.CurrentMaterial, _state.ShelterFactor);
        // A zone stops at its walls, so a doorway, the wall's own thickness, is in none: it is named
        // from the zones either side of it.
        if (name == UnderShelter) name = DoorwayName(world, eyePos) ?? name;
        _state.CurrentRegion = name;
    }

    /// <summary>Start of the name of a roofed gap between zones, such as a doorway.</summary>
    internal const string DoorwayPrefix = "doorway";

    private Vector3 _doorwayAt = new(float.NaN);
    private string? _doorwayName;

    /// <summary>
    /// "doorway between A and B" when named zones lie within a doorway's depth either side of the
    /// listener, "doorway to A" for one, null for none. Looked up again only after a step.
    /// </summary>
    private string? DoorwayName(WorldSnapshot world, Vector3 at)
    {
        if (Vector3.DistanceSquared(at, _doorwayAt) < 0.01f) return _doorwayName;
        _doorwayAt = at;
        return _doorwayName = NameOfGap(world, at, p => _acoustics.GetZoneAt(world, p));
    }

    internal static string? NameOfGap(WorldSnapshot world, Vector3 at, Func<Vector3, int> regionAt)
    {
        if (world.AcousticMap == null) return null;
        var names = new List<string>(2);
        foreach (float reach in DoorwayProbeMetres)
            foreach (var d in DoorwayProbeDirections)
            {
                int id = regionAt(at + d * reach);
                string? name = NamedPlaces.NameOf(world, id);
                if (name == null && id != AcousticConstants.GlobalRegionId && world.AcousticMap.Regions.TryGetValue(id, out var r)
                    && !string.IsNullOrWhiteSpace(r.FriendlyName)) name = r.FriendlyName;
                if (name != null && !names.Contains(name)) names.Add(name);
            }
        return names.Count switch
        {
            0 => null,
            1 => $"{DoorwayPrefix} to {names[0]}",
            _ => $"{DoorwayPrefix} between {names[0]} and {names[1]}",
        };
    }

    // A door frame and the wall it is in are under half a metre deep; a vestibule a little more.
    private static readonly float[] DoorwayProbeMetres = { 0.5f, 1.0f };
    private static readonly Vector3[] DoorwayProbeDirections =
        { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitZ, -Vector3.UnitZ };

    /// <summary>The name of a roofed spot no zone covers: a doorway between two rooms, a gap between
    /// two zones' boxes. A condition, not a place, so it is never announced on its own.</summary>
    internal const string UnderShelter = "Under Shelter";

    /// <summary>
    /// What to call where the listener stands. A named place says its name; everywhere else is named
    /// from the ground underfoot (the footstep probe's material), because stepping off the kerb is the
    /// most navigationally important fact on a city street and "outside" says nothing. The map-wide
    /// outdoor region is always in the acoustic map and is not a named place: its name is used only for
    /// ground the material table does not know.
    /// </summary>
    internal static string NameOfPlace(AcousticMap? map, int regionId, string material, float shelter)
    {
        RegionComponent? global = null;
        if (map != null && map.Regions.TryGetValue(regionId, out var reg))
        {
            if (regionId != AcousticConstants.GlobalRegionId && !string.IsNullOrWhiteSpace(reg.FriendlyName))
                return reg.FriendlyName;
            if (regionId == AcousticConstants.GlobalRegionId) global = reg;
        }
        if (shelter > 0.8f) return UnderShelter;
        string ground = OutdoorNameFor(material);
        if (ground == Outside && !string.IsNullOrWhiteSpace(global?.FriendlyName)) return global.Value.FriendlyName;
        return ground;
    }

    /// <summary>What to call open ground by its surface; <see cref="Outside"/> for anything unrecognised.</summary>
    internal static string OutdoorNameFor(string material) => material switch
    {
        "Concrete" => "sidewalk",
        "Asphalt" => "road",
        "Grass" => "grass",
        "Dirt" => "dirt",
        "Gravel" => "gravel",
        "Sand" => "sand",
        "Wood" => "boardwalk",
        "Metal" => "metal grating",
        "Water" => "water",
        _ => Outside,
    };

    /// <summary>Open ground of no recognised surface, outside every zone.</summary>
    internal const string Outside = "outside";

    /// <summary>
    /// A physical model's level at a metre and its size, memoised by sound id. One lookup for both the
    /// ranking and the emitter, so a source is not picked on one set of numbers and played at another.
    /// The memo is not optional: behind ByName, ModelLibrary.Get builds the whole model each call, and
    /// asked per machine per update on the city (121 machines) that was gen2 pauses of 57 ms and the
    /// placement pass holding every source still for 106 ms, heard as the client hanging.
    /// </summary>
    private readonly Dictionary<string, (float LevelDb, float Extent)?> _physicalLevels =
        new(StringComparer.OrdinalIgnoreCase);

    private bool PhysicalLevel(string soundId, out float sourceLevelDb, out float extentMetres)
    {
        if (!_physicalLevels.TryGetValue(soundId, out var cached))
        {
            cached = LookUpPhysicalLevel(soundId);
            _physicalLevels[soundId] = cached;
        }
        if (cached is null) { sourceLevelDb = 0f; extentMetres = 1f; return false; }
        (sourceLevelDb, extentMetres) = cached.Value;
        return true;
    }

    /// <summary>The headroom a physical voice renders with, dB: its spec's own for water, fire, foliage
    /// and bells, the shared one for the rest. Memoised like the level.</summary>
    private readonly Dictionary<string, float> _physicalHeadroom = new(StringComparer.OrdinalIgnoreCase);

    private float PhysicalHeadroom(string soundId)
    {
        if (_physicalHeadroom.TryGetValue(soundId, out float h)) return h;
        h = OpenFPS.Common.VehicleProfile.PeakHeadroomDb;
        try
        {
            if (OpenFPS.Client.AudioEngine.Fmod.WaterFeatureVoice.ParseKey(soundId, out string waterPreset, out _, out _))
                h = OpenFPS.Common.WaterFeatureSpec.ByName(waterPreset).PeakHeadroomDb;
            else if (soundId.StartsWith("water:", StringComparison.OrdinalIgnoreCase))
                h = OpenFPS.Common.WaterFeatureSpec.ByName(soundId[6..]).PeakHeadroomDb;
            else if (soundId.StartsWith("fire:", StringComparison.OrdinalIgnoreCase))
                h = OpenFPS.Common.FireSpec.ByName(soundId[5..]).PeakHeadroomDb;
            else if (soundId.StartsWith("foliage:", StringComparison.OrdinalIgnoreCase))
                h = OpenFPS.Common.FoliageSpec.ByName(soundId[8..]).PeakHeadroomDb;
            else if (OpenFPS.Common.WoodChorus.ParseKey(soundId, out string woodPreset, out _, out _))
                h = OpenFPS.Common.FoliageSpec.ByName(woodPreset).PeakHeadroomDb;
            else if (soundId.StartsWith("flow:", StringComparison.OrdinalIgnoreCase))
                h = OpenFPS.Common.RunningWaterSpec.ByName(soundId[5..]).PeakHeadroomDb;
            else if (soundId.StartsWith("shore:", StringComparison.OrdinalIgnoreCase))
                h = OpenFPS.Common.ShoreSpec.ByName(soundId).PeakHeadroomDb;
            else if (soundId.StartsWith("bell:", StringComparison.OrdinalIgnoreCase))
                h = OpenFPS.Common.ModelLibrary.Bell(soundId[5..]).PeakHeadroomDb;
            else if (OpenFPS.Client.AudioEngine.Fmod.TrainVoiceState.ParseKey(soundId, out string railPreset, out _, out int railIndex))
            {
                // A train's bell renders with the bell's own room, as a crossing's does (TrainLayout).
                var layout = OpenFPS.Common.TrainLayout.Sources(OpenFPS.Common.TrainProfile.ByName(railPreset));
                if (railIndex >= 0 && railIndex < layout.Count) h = layout[railIndex].HeadroomDb;
            }
        }
        catch (Exception) { }
        _physicalHeadroom[soundId] = h;
        return h;
    }

    private static (float LevelDb, float Extent)? LookUpPhysicalLevel(string soundId)
    {
        try
        {
            if (soundId.StartsWith("machine:", StringComparison.OrdinalIgnoreCase))
            {
                var spec = OpenFPS.Common.SmallMachineSpec.ByName(soundId[8..]);
                return (spec.SourceLevelDb, spec.ExtentMetres);
            }
            if (soundId.StartsWith("rail:", StringComparison.OrdinalIgnoreCase))
            {
                // "rail:<preset>/<train>/<source index>" — the server places one entity per source
                // in TrainLayout order; the level is that source's own.
                if (OpenFPS.Client.AudioEngine.Fmod.TrainVoiceState.ParseKey(soundId, out string preset, out _, out int index))
                {
                    var layout = OpenFPS.Common.TrainLayout.Sources(OpenFPS.Common.TrainProfile.ByName(preset));
                    if (index >= 0 && index < layout.Count)
                        return (layout[index].LevelDb, layout[index].ExtentMetres);
                }
                return null;
            }
            if (soundId.StartsWith("bell:", StringComparison.OrdinalIgnoreCase))
            {
                // A level crossing's gong. Without a level here a source is dropped by both the ranking
                // and the submit path, and the bell was silent on the client. Its size is the bell.
                var bell = OpenFPS.Common.ModelLibrary.Bell(soundId[5..]);
                return (bell.ReferenceDb, MathF.Max(0.5f, bell.DiameterMetres));
            }
            if (soundId.StartsWith("gate:", StringComparison.OrdinalIgnoreCase))
            {
                // A level crossing's gate mechanism, placed by its motor's level; the case at the
                // foot of the mast is half a metre of steel.
                var gate = OpenFPS.Common.CrossingGateSpec.ByName(soundId[5..]);
                return (gate.SourceLevelDb, 0.5f);
            }
            // Water, fire and trees are placed like machines, by their declared level and size (basin,
            // hearth, crown). One tap of a water feature ("water:<preset>/<feature>/<tap>") takes the
            // whole feature's level and its own landing's size: each renders its share against the
            // whole's scale, so the taps sum to the feature at any distance.
            if (OpenFPS.Client.AudioEngine.Fmod.WaterFeatureVoice.ParseKey(soundId, out string waterPreset, out _, out int waterTap))
            {
                var feature = OpenFPS.Common.WaterFeatureSpec.ByName(waterPreset);
                if (waterTap >= feature.Taps.Length) return null;
                return (feature.SourceLevelDb, feature.Taps[waterTap].ExtentMetres);
            }
            if (soundId.StartsWith("water:", StringComparison.OrdinalIgnoreCase))
            {
                var water = OpenFPS.Common.WaterFeatureSpec.ByName(soundId[6..]);
                return (water.SourceLevelDb, water.ExtentMetres);
            }
            if (soundId.StartsWith("fire:", StringComparison.OrdinalIgnoreCase))
            {
                var fire = OpenFPS.Common.FireSpec.ByName(soundId[5..]);
                return (fire.SourceLevelDb, fire.ExtentMetres);
            }
            if (soundId.StartsWith("foliage:", StringComparison.OrdinalIgnoreCase))
            {
                var tree = OpenFPS.Common.FoliageSpec.ByName(soundId[8..]);
                return (tree.SourceLevelDb, tree.ExtentMetres);
            }
            // A wood heard as one (WoodChorus): one tree's level (its synth renders its trees), the
            // wood's size.
            if (OpenFPS.Common.WoodChorus.ParseKey(soundId, out string woodPreset, out float woodX, out float woodZ))
            {
                var tree = OpenFPS.Common.FoliageSpec.ByName(woodPreset);
                return (tree.SourceLevelDb, MathF.Max(woodX, woodZ));
            }
            // Running water: a creek, a gutter, a drain, a downpipe (RunningWaterSynth). Its size is its
            // length or its opening; its level is declared at its base flow or its reference rain.
            if (soundId.StartsWith("flow:", StringComparison.OrdinalIgnoreCase))
            {
                var flow = OpenFPS.Common.RunningWaterSpec.ByName(soundId[5..]);
                return (flow.SourceLevelDb, flow.ExtentMetres);
            }
            // Waves at an edge (ShoreSynth): declared at its reference wind straight onshore; its size is a
            // stretch of the edge.
            if (soundId.StartsWith("shore:", StringComparison.OrdinalIgnoreCase))
            {
                var shore = OpenFPS.Common.ShoreSpec.ByName(soundId);
                return (shore.SourceLevelDb, shore.ExtentMetres);
            }
            if (soundId.StartsWith("aircraft:", StringComparison.OrdinalIgnoreCase))
            {
                var p = OpenFPS.Common.AircraftProfile.ByName(soundId[9..]);
                // The size is what radiates (the disc or the nozzle), which only has to stop the inverse
                // law running away directly underneath; with more than one engine it is their span
                // (AircraftProfile.EngineSpanMetres, 11.5 m on a twin), as a bus's length is its size.
                float span = p.EngineSpanMetres > 0f
                    ? p.EngineSpanMetres
                    : MathF.Max(2f, p.Propeller?.DiameterMetres
                                 ?? p.Turbine?.Fan?.DiameterMetres
                                 ?? p.Turbine?.BypassNozzleDiameterMetres
                                 ?? 2f);
                return (p.SourceLevelDb, span);
            }
        }
        catch { }
        return null;
    }

    private readonly Dictionary<int, (float Speed, double At)> _lastRailSpeed = new();
    private readonly Dictionary<int, (float Speed, double At)> _lastAirSpeed = new();

    /// <summary>
    /// The power lever of an aeroplane on its wheels, from what it is doing (AircraftGroundRun): pulling
    /// away down the runway is take-off power; slowing hard from speed after touchdown is the reversers,
    /// which run the engines up again; taxiing is idle with a little breakaway thrust; standing is idle.
    /// </summary>
    internal static float GroundPower(float speed, float accel)
    {
        if (accel > 0.4f && speed > 3f) return 1f;
        if (accel < -0.8f && speed > 30f) return 0.55f;
        if (speed > 0.5f) return 0.3f;
        return 0.25f;
    }
    /// <summary>A piston aeroplane's lever rolling out or taxiing: idle, FlightPower's own floor. A piston
    /// engine has no reverser, so after touchdown it idles and its tyres are heard.</summary>
    internal const float GroundIdleLever = 0.06f;

    /// <summary>
    /// The power lever and a rotor's wake from what the aircraft is doing, which nothing on the wire
    /// carries: climbing is full power, level is cruise, descending is idle, so a map's descending path
    /// is an approach without saying so. A rotor slaps descending into its downwash or fast forward.
    /// </summary>
    private static (float Lever, float Wake) FlightPower(Vector3 velocity)
    {
        float speed = velocity.Length();
        if (speed < 0.5f) return (0.25f, 0f);                  // sitting on the apron at idle
        float climb = velocity.Y / speed;                      // sine of the flight path angle
        // Full power by about six degrees up, idle by about four down: a climb needs everything and a
        // descent nothing, so the lever falls faster than it rises.
        float lever = climb >= 0f
            ? Math.Clamp(0.62f + climb * 3.6f, 0f, 1f)
            : Math.Clamp(0.62f + climb * 8.0f, 0.06f, 1f);
        float wake = Math.Clamp(-climb * 6f, 0f, 1f) * 0.7f
                   + Math.Clamp((speed - 25f) / 45f, 0f, 1f) * 0.3f;
        return (lever, Math.Clamp(wake, 0f, 1f));
    }

    /// <summary>
    /// The listener's head for the wind at the ears: place, motion, facing, exposure and cover. Exposure
    /// is one minus the survey's enclosure (a street canyon takes a third off the wind, a walled yard
    /// half) and zero in a room. A cabin lets in what its open windows do; a vehicle without one (a
    /// motorcycle, a formula car) puts a helmet on the rider.
    /// </summary>
    private EarWindListener EarListener(WorldSnapshot world, Vector3 head, Vector3 velocity, Quaternion rotation)
    {
        Vector3 forward = Vector3.Transform(Vector3.UnitZ, rotation);
        float facing = MathF.Atan2(forward.X, forward.Z) * 180f / MathF.PI;
        float exposure = _state.IsIndoor ? 0f : 1f - Math.Clamp(_acousticWorker.ListenerEnclosure, 0f, 1f);
        var cover = EarCover.None;
        float windows = 0f;
        if (_state.IsRiding)
        {
            cover = EarCover.Cabin;
            if (world.Entities.TryGetValue(_state.RidingEntityId, out var ride)
                && OpenFPS.Client.AudioEngine.Acoustics.CabinWalls.Vehicle(ride) is { } vehicle)
            {
                if (OpenFPS.Client.AudioEngine.Acoustics.CabinWalls.HasCabin(vehicle))
                    windows = _cabins.WindowsOpen(ride, _now());
                else cover = EarCover.Helmet;
            }
        }
        return new EarWindListener(head, _state.IsRiding ? 1.3f : _state.EyeHeight, new Vector2(velocity.X, velocity.Z),
                                   facing, exposure, cover, windows);
    }

    /// <summary>An open bus doorway's share of the cabin's wall area, as transmitted power.</summary>
    private const float DoorwayPowerFraction = 0.0225f;

    /// <summary>Every vehicle's windows as this client has them, and what a cabin's walls take off a
    /// sound crossing them.</summary>
    private readonly OpenFPS.Client.AudioEngine.Acoustics.CabinWalls _cabins = new();

    /// <summary>
    /// What the body of the vehicle you sit in takes off everything outside it, dB per band: the interior
    /// engine voice's paths the other way round, the glass by its mass (transmission ~ rho*c / (pi*f*m)),
    /// the seals, and whatever is open (CarWindow.CabinLossDb). At 150 Hz a hatchback's glass passes about
    /// a hundredth of the power; by 1 kHz the seals dominate and the top sits about 30 dB down. Windows
    /// down, a tenth of the wall is a hole. Nothing on foot or on something with no cabin.
    /// </summary>
    private (float Low, float Mid, float High) CabinEnclosure(WorldSnapshot world)
    {
        if (!_state.IsRiding || !world.Entities.TryGetValue(_state.RidingEntityId, out var ride)) return (0f, 0f, 0f);
        if (OpenFPS.Client.AudioEngine.Acoustics.CabinWalls.Vehicle(ride) is not { } vehicle) return (0f, 0f, 0f);
        // A bus's open doors: about 2.4 m^2 in a hundred-odd m^2 of wall, the street at a couple of per
        // cent of its power. The voice decides when the doors are open.
        float doorway = _audio.EngineDoorsOpen(_state.RidingEntityId) ? DoorwayPowerFraction : 0f;
        return OpenFPS.Client.AudioEngine.Acoustics.CabinWalls.LossDb(vehicle, _cabins.WindowsOpen(ride, _now()), doorway);
    }

    /// <summary>
    /// Whether an aeroplane is on its wheels, and its height over the ground (infinity when not probed):
    /// its belly within 15 % of its own length of the ground. The transition is the touchdown, which the
    /// voice turns into wheels spinning up (AircraftSynth.Touchdown). Not probed at all more than 60 m
    /// over the listener's head, since the ground probe is a grid search.
    /// </summary>
    private bool OnTheWheels(EntitySnapshot snap, WorldSnapshot world, Vector3 eyePos, out float height)
    {
        var p = snap.Transform.Position;
        height = float.PositiveInfinity;
        if (p.Y - eyePos.Y > 60f) return false;
        float groundY = OpenFPS.Common.PhysicsUtils.GetGroundHeight(world, p, snap.Id, out _);
        height = p.Y - groundY;
        // A jet sits five metres up on its gear and a light single one: a fraction of length serves both.
        float sitsAt = 0.15f * (PhysicalAircraft(snap)?.LengthMetres ?? 8f);
        return p.Y - groundY <= sitsAt;
    }

    /// <summary>The aircraft profile behind an entity, or null if it is not an aeroplane.</summary>
    private static OpenFPS.Common.AircraftProfile? PhysicalAircraft(EntitySnapshot snap)
    {
        string? sid = snap.Definition.SoundEmitter.SoundId;
        if (sid == null || !sid.StartsWith("aircraft:", StringComparison.OrdinalIgnoreCase)) return null;
        try { return OpenFPS.Common.AircraftProfile.ByName(sid[9..]); } catch { return null; }
    }

    private readonly Dictionary<string, OpenFPS.Common.RunningWaterSpec> _flowSpecs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>When a tap was last seen on, by entity.</summary>
    private readonly Dictionary<int, double> _tapOnAt = new();

    /// <summary>How long a basin is heard after its tap is shut, s: the longest a full sink takes to drain.</summary>
    private const double TapDrainSeconds = 90.0;

    /// <summary>Running water fed only by the rain, with less than a trickle in it now (Runoff): it makes
    /// no sound and is given no voice. A creek or a fountain's overflow, with a flow of its own, never is.</summary>
    private bool Dry(string soundId, int entityId, bool running, double now)
    {
        // Still water: no wind to raise waves, no swell, no current. It makes no sound.
        if (soundId.StartsWith("shore:", StringComparison.OrdinalIgnoreCase))
        {
            try { return OpenFPS.Common.ShoreSpec.ByName(soundId).CalmAt(OpenFPS.Common.WindField.MeanSpeed); }
            catch (Exception) { return false; }
        }
        if (!soundId.StartsWith("flow:", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            // Once per sound id: this runs for every sink in every flat, every frame.
            if (!_flowSpecs.TryGetValue(soundId, out var spec))
                _flowSpecs[soundId] = spec = OpenFPS.Common.RunningWaterSpec.ByName(soundId[5..]);
            // A tap is heard while on and for as long after as its basin takes to empty. A shut tap nobody
            // opened since you arrived is an empty sink; a leaking one drips.
            if (spec.Tap is { } tap)
            {
                if (running) { _tapOnAt[entityId] = now; return false; }
                if (tap.LeakLitresPerSecond > 0f) return false;
                return !_tapOnAt.TryGetValue(entityId, out double on) || now - on > TapDrainSeconds;
            }
            if (spec.BaseFlowLitresPerSecond > 0f || spec.CatchmentSquareMetres <= 0f) return false;
            return spec.FlowNow() < OpenFPS.Common.RunningWaterSpec.DryLitresPerSecond;
        }
        catch (Exception) { return false; }
    }

    // ── Woods heard as one (WoodChorus) ──────────────────────────────────────────────────────────
    private readonly OpenFPS.Common.WoodChorus.Weights _woodWeights = new();
    private Func<int, bool>? _isLiveMachine;

    /// <summary>A tree's or a wood's share this frame: the amplitude its voice is played at (0: no voice
    /// of its own), and for a wood how many trees its synth stands for. Everything else: 1, 1.</summary>
    private float ChorusShare(int entityId, out float trees)
    {
        trees = 1f;
        if (OpenFPS.Client.Core.ClientWorldState.IsWood(entityId))
        {
            if (!_woodWeights.Woods.TryGetValue(entityId, out var w)) return 0f;
            trees = w.Trees;
            return w.Gain;
        }
        return _woodWeights.Individual.TryGetValue(entityId, out float g) ? g : 1f;
    }

    /// <summary>
    /// Decides which physical models (standing machines and aircraft) run live: the loudest here, not
    /// the nearest. A 92 dB mower three gardens away is plain where a 59 dB window unit is not. Aircraft
    /// share the budget so one measure answers both an airliner overhead (beats every air conditioner)
    /// and the same airliner idling far off (does not). As for engines: a held slot keeps a bias, a new
    /// one is held a couple of seconds, and a lost one fades, since a running synth has no zero to stop at.
    /// </summary>
    private void ChooseLiveMachines(WorldSnapshot world, Vector3 eyePos)
    {
        double now = _now();
        _machineOrder.Clear();
        _machineGroups.Clear();
        // Trees the budget left without a voice last frame are heard in their wood (WoodChorus.Weigh).
        if (world.Woods != null) world.Woods.Weigh(eyePos, _woodWeights, _isLiveMachine ??= id => _liveMachines.Contains(id));
        else { _woodWeights.Individual.Clear(); _woodWeights.Woods.Clear(); }

        // A train is one machine: up to nine taps on one synth, ranked by its loudest and admitted
        // together. Ranked tap by tap, its taps dropped and came back all along it as it passed.
        foreach (int entityId in world.AudioEntityIds)
        {
            if (entityId == OwnEntityId) continue;
            if (!world.Entities.TryGetValue(entityId, out var snap)) continue;
            var em = snap.Definition.SoundEmitter;
            if (!em.IsSynth || em.SoundId == null) continue;
            if (!PhysicalLevel(em.SoundId, out float levelDb, out float extent)) continue;
            if (Dry(em.SoundId, entityId, em.SynthRunning, now)) continue;
            // A train's horn or bell that nobody is sounding.
            if (SilentSignal(em.SoundId, now)) continue;
            // A tree past the hand-over is heard in its wood, an empty wood not at all; ranked by what each
            // plays (a wood's trees add in power, so its amplitude by their root).
            float chorus = ChorusShare(entityId, out float chorusTrees);
            if (chorus <= 0f || chorusTrees <= 1e-3f) continue;
            chorus *= MathF.Sqrt(chorusTrees);

            float d = Vector3.Distance(OpenFPS.Common.AudioEmission.PointFor(snap), eyePos);
            var (gain, reference) = OpenFPS.Common.Loudness.Place(levelDb, extent);
            float range = MathF.Max(em.Range, OpenFPS.Common.Loudness.AudibleRange(levelDb));
            // Ranked by loudness at the ear (docs/EAR_MODEL.md, Ranking), as VoiceManager.Audibility ranks
            // every voice: the gain the mixer plays it at, turned into loudness by its measured spectrum.
            // Not the corrected gain itself, which plays what the ear hears less of louder.
            var timbre = OpenFPS.Client.AudioEngine.Core.EarTimbres.Find(em.SoundId);
            gain *= MathF.Pow(10f, OpenFPS.Common.Loudness.TimbreCorrectionDb(levelDb, timbre) / 20f);
            float level = OpenFPS.Common.Loudness.HeardGain(
                OpenFPS.Common.Loudness.RenderedGain(gain * em.Volume * chorus, reference, range, d), timbre, physical: true);

            // A water feature's taps are one fountain the same way, and come and go together.
            string group = OpenFPS.Client.AudioEngine.Fmod.TrainVoiceState.ParseKey(em.SoundId, out string preset, out string train, out _)
                ? "rail:" + preset + "/" + train
                : OpenFPS.Client.AudioEngine.Fmod.WaterFeatureVoice.ParseKey(em.SoundId, out string waterPreset, out string feature, out _)
                ? "water:" + waterPreset + "/" + feature : "#" + entityId;
            if (!_machineGroups.TryGetValue(group, out var g)) _machineGroups[group] = g = new MachineGroup();
            g.Members.Add(entityId);
            g.Level = MathF.Max(g.Level, level);
            if (_liveMachines.Contains(entityId)) g.Live = true;
            if (_machineStarted.TryGetValue(entityId, out double began) && now - began < EngineMinimumHoldSeconds) g.Held = true;
        }
        foreach (var g in _machineGroups.Values)
        {
            // Louder sorts first, so negated; the hold and the keep bias as for a car.
            float key = g.Live ? -g.Level / (EngineKeepBias * EngineKeepBias) : -g.Level;
            if (g.Held) key = float.NegativeInfinity;
            g.Key = key;
        }
        _groupOrder.Clear();
        _groupOrder.AddRange(_machineGroups.Values);
        _groupOrder.Sort((x, y) => x.Key.CompareTo(y.Key));

        int keepGroups = Math.Min(_adaptiveMachines, _groupOrder.Count);
        _wantedMachines.Clear();
        for (int i = 0; i < keepGroups; i++) foreach (int id in _groupOrder[i].Members) _wantedMachines.Add(id);

        foreach (int id in _liveMachines)
        {
            if (_wantedMachines.Contains(id)) continue;
            _machineStarted.Remove(id);
            if (!_machineRetiring.Contains(id)) _machineRetiring.Add(id);
        }
        for (int i = _machineRetiring.Count - 1; i >= 0; i--)
        {
            int id = _machineRetiring[i];
            if (_wantedMachines.Contains(id)) { _machineRetiring.RemoveAt(i); continue; }
            if (_audio.FadeOutEngine(id)) { _audio.StopSound(id); _machineRetiring.RemoveAt(i); }
        }

        int admittedGroups = 0;
        _liveMachines.Clear();
        for (int i = 0; i < keepGroups; i++)
        {
            var g = _groupOrder[i];
            bool isNew = false;
            foreach (int id in g.Members) if (!_machineStarted.ContainsKey(id)) { isNew = true; break; }
            if (isNew)
            {
                // A map load presents every machine at once; building one is a set of waveguides and
                // resonators. Counted per machine: a train's taps share one synth.
                if (admittedGroups >= NewEnginesPerUpdate) continue;
                admittedGroups++;
            }
            foreach (int id in g.Members)
            {
                _liveMachines.Add(id);
                _audio.ReviveEngine(id);
                if (!_machineStarted.ContainsKey(id)) _machineStarted[id] = now;
            }
        }
    }

    private sealed class MachineGroup
    {
        public readonly List<int> Members = new();
        public float Level, Key;
        public bool Live, Held;
    }
    private readonly Dictionary<string, MachineGroup> _machineGroups = new();
    private readonly List<MachineGroup> _groupOrder = new();
    private readonly HashSet<int> _wantedMachines = new();

    /// <summary>
    /// What the engines' reflections cost the game thread, ms a pass: this pass's total and a smoothed
    /// average. Over <see cref="EchoPassCeilingMs"/> reflections give way. Watching only the mixer and the
    /// engine threads, a machine with room in its mixer spent 42 ms a pass on 32 cars' reflections: its
    /// game loop fell to 22 Hz and every sound lagged a tenth of a second (Sean's client, 2026-10-04).
    /// </summary>
    private double _echoPassMs, _echoMsSmoothed;
    private const double EchoPassCeilingMs = 6.0;

    /// <summary>
    /// Decides which cars get their own engine and which borrow one. Engines render ahead on a worker
    /// pool across the machine's cores (EngineRenderPool), not in the mixer callback, so every car in
    /// earshot can have one; the budget is a safety net for the placement (HRTF and filters per voice),
    /// giving up reflections, then borrowed voices, then engines, never the cars first.
    /// </summary>
    private void ChooseLiveEngines(WorldSnapshot world, Vector3 eyePos)
    {
        double now = _now();
        _echoMsSmoothed += (_echoPassMs - _echoMsSmoothed) * 0.1;
        _echoPassMs = 0;
        if (_echoMsSmoothed > EchoPassCeilingMs && _adaptiveEchoes > 0
            && now >= _budgetHeldUntil && now - _lastBudgetChange >= BudgetSettleSeconds)
        {
            _adaptiveEchoes--;
            _lastBudgetChange = now;
            Log.Information("Audio: engine reflections cost {Ms:F1} ms a pass; {Echoes} reflection(s) each.", _echoMsSmoothed, _adaptiveEchoes);
        }

        float load = _audio.MixerLoad;
        if (load > MixerLoadCeiling) { if (_overCeilingSince < 0) _overCeilingSince = now; }
        else _overCeilingSince = -1;

        // The producers as well as the mixer: a render pool that cannot keep up starves voices (blocks
        // ramped to silence), which the mixer's load never sees. On the city a pool a core or two short
        // gave 50-190 starves a second and the nearest car came out chopped. Starving gives up machines,
        // then cars; reflections and front taps cost the producers nothing.
        int starves = OpenFPS.Client.AudioEngine.Fmod.EngineVoiceState.GlobalStarves + OpenFPS.Client.AudioEngine.Fmod.PhysicalVoiceState.GlobalStarves;
        if (_starveSampledAt > 0 && now > _starveSampledAt)
            _starveRate += ((starves - _starvesSeen) / (float)(now - _starveSampledAt) - _starveRate) * 0.3f;
        _starvesSeen = starves; _starveSampledAt = now;
        if (_starveRate > StarveCeilingPerSecond && now >= _budgetHeldUntil && now - _lastBudgetChange >= BudgetSettleSeconds)
        {
            if (_adaptiveMachines > MachineFloor) _adaptiveMachines--;
            else if (_adaptiveBudget > MinEngineVoices) _adaptiveBudget--;
            _lastBudgetChange = now;
            Log.Information("Audio: engines starving at {Rate:F0}/s; {Cars} engine(s), {Machines} machine(s).",
                            _starveRate, _adaptiveBudget, _adaptiveMachines);
        }

        if (load > 0f && now >= _budgetHeldUntil && now - _lastBudgetChange >= BudgetSettleSeconds)
        {
            if (load > MixerLoadCeiling && now - _overCeilingSince >= OverCeilingSeconds)
            {
                // Places of extended sources first, then a machine's second outlet: their loss costs only
                // geometry, since what they carried slews back into the voice still playing.
                if (_adaptivePlaces > 0) _adaptivePlaces = Math.Max(0, _adaptivePlaces - PlaceBudgetStep);
                else if (_adaptiveFront > 0) _adaptiveFront--;
                // Then a standing machine, before a reflection: one fewer air conditioner in a street of
                // forty is not missed, the wall you walk beside answering a car is.
                else if (_adaptiveMachines > MachineFloor) _adaptiveMachines--;
                else if (_adaptiveEchoes > 0) _adaptiveEchoes--;
                else if (_adaptiveDistant > MinDistantVoices) _adaptiveDistant--;
                else if (_adaptiveBudget > MinEngineVoices) _adaptiveBudget--;
                else goto settled;
                _lastBudgetChange = now;
                Log.Information("Audio: mixer at {Load:P0}; {Cars} engine(s), {Distant} borrowed, {Echoes} reflection(s) each.",
                                load, _adaptiveBudget, _adaptiveDistant, _adaptiveEchoes);
            }
            else if (load < MixerLoadFloor && _starveRate < 1f)
            {
                if (_adaptiveBudget < EngineVoiceBudget) _adaptiveBudget++;
                else if (_adaptiveMachines < MachineVoiceBudget) _adaptiveMachines++;
                else if (_adaptiveDistant < MaxDistantVoices) _adaptiveDistant++;
                else if (_adaptiveFront < FrontVoiceBudget) _adaptiveFront++;
                else if (_adaptivePlaces < PlaceVoiceBudget) _adaptivePlaces = Math.Min(PlaceVoiceBudget, _adaptivePlaces + PlaceBudgetStep);
                else if (_adaptiveEchoes < EchoCeiling && _echoMsSmoothed < EchoPassCeilingMs / 3) _adaptiveEchoes++;
                else goto settled;
                _lastBudgetChange = now;
                Log.Information("Audio: mixer at {Load:P0}; {Cars} engine(s), {Distant} borrowed, {Echoes} reflection(s) each.",
                                load, _adaptiveBudget, _adaptiveDistant, _adaptiveEchoes);
            }
            settled: ;
        }

        _engineDistances.Clear();
        _carPreset.Clear();
        foreach (var entityId in world.AudioEntityIds)
        {
            if (entityId == OwnEntityId) continue;
            if (!world.Entities.TryGetValue(entityId, out var snap)) continue;
            var em = snap.Definition.SoundEmitter;
            if (!em.IsSynth || em.SoundId == null) continue;
            if (!em.SoundId.StartsWith("engine:", StringComparison.OrdinalIgnoreCase)) continue;
            string preset = em.SoundId[7..];
            if (!OpenFPS.Common.MachineRegistry.Knows(preset)) continue;
            _carPreset[entityId] = preset;

            float d2 = Vector3.DistanceSquared(snap.Transform.Position, eyePos);
            // The keep bias and the hold stop the set churning as cars trade places (EngineKeepBias).
            float key = _liveEngines.Contains(entityId) ? d2 * (EngineKeepBias * EngineKeepBias) : d2;
            if (_engineStarted.TryGetValue(entityId, out double began) && now - began < EngineMinimumHoldSeconds)
                key = -1f;
            // The one you are sitting in is never ranked out: it is the loudest thing in your world.
            if (entityId == _state.RidingEntityId) key = float.NegativeInfinity;
            _engineDistances.Add((entityId, key, d2));
        }
        _engineDistances.Sort((a, b) => a.Key.CompareTo(b.Key));

        int keep = Math.Min(_adaptiveBudget, _engineDistances.Count);

        // The cars kept outside the budget as their preset's only engine (below), found before anything
        // is let go so a donor keeps its engine and start time. Let go and re-admitted, a donor was held
        // as new, took a slot inside the budget and turned out the car at its edge: that engine and every
        // borrowed voice rebuilt from nothing every 2.5 s while the field stood still.
        _presetsKept.Clear();
        _presetDonors.Clear();
        for (int i = 0; i < _engineDistances.Count; i++)
        {
            if (!_carPreset.TryGetValue(_engineDistances[i].Id, out string? kept)) continue;
            if (_presetsKept.Add(kept) && i >= keep) _presetDonors.Add(_engineDistances[i].Id);
        }

        // A car that lost its engine fades it out rather than being cut mid-waveform.
        foreach (int id in _liveEngines)
        {
            bool survives = _presetDonors.Contains(id);
            for (int i = 0; i < keep && !survives; i++) if (_engineDistances[i].Id == id) { survives = true; break; }
            if (survives) continue;
            _engineEchoes.Forget(id, _audio);
            _engineStarted.Remove(id);
            if (!_engineRetiring.Contains(id)) _engineRetiring.Add(id);
        }
        for (int i = _engineRetiring.Count - 1; i >= 0; i--)
        {
            int id = _engineRetiring[i];
            bool wanted = _presetDonors.Contains(id);
            for (int k = 0; k < keep && !wanted; k++) if (_engineDistances[k].Id == id) { wanted = true; break; }
            if (wanted) { _engineRetiring.RemoveAt(i); continue; }
            if (_audio.FadeOutEngine(id)) { _audio.StopSound(id); _engineRetiring.RemoveAt(i); }
        }

        // New engines are let in a few at a time. A map load presents thirty cars at once, and thirty
        // constructions (cylinders, waveguides, buffers) on the thread that services the mixer's queues,
        // while everything else loads, dropped out for a second or two. A car a sixteenth of a second
        // late is a car that arrived.
        int admitted = 0;
        _liveEngines.Clear();
        _engineSourceByPreset.Clear();
        for (int i = 0; i < keep; i++)
        {
            int id = _engineDistances[i].Id;
            if (!_engineStarted.ContainsKey(id))
            {
                if (admitted >= NewEnginesPerUpdate) continue;
                admitted++;
            }
            _liveEngines.Add(id);
            // A car that holds a slot is audible: cheap, idempotent, and the only undo for an abandoned fade.
            _audio.ReviveEngine(id);
            if (!_engineStarted.ContainsKey(id)) _engineStarted[id] = now;
            _engineSourceByPreset.TryAdd(_carPreset[id], id);
            // Anything that has just earned a real engine gives its borrowed voice back.
            int lent = DistantVoiceBase - Math.Abs(id);
            if (_distantBoundTo.Remove(lent)) _audio.StopSound(lent);
        }

        // Every preset on the map keeps one live engine, whatever the budget said. A car outside the
        // budget borrows the ring of the nearest car of its own preset, and with none live it is silent
        // (DistantEngine): the city's two slip-on motorcycles, the loudest thing on their street, went
        // silent together. At most one engine per preset in use (eleven on the city, budget 32).
        for (int i = keep; i < _engineDistances.Count; i++)
        {
            int id = _engineDistances[i].Id;
            if (!_carPreset.TryGetValue(id, out string? preset)) continue;
            if (_engineSourceByPreset.ContainsKey(preset)) continue;     // already has a donor
            _liveEngines.Add(id);
            _audio.ReviveEngine(id);
            if (!_engineStarted.ContainsKey(id)) _engineStarted[id] = now;
            _engineSourceByPreset[preset] = id;
            _engineRetiring.Remove(id);
            int borrowed = DistantVoiceBase - Math.Abs(id);
            if (_distantBoundTo.Remove(borrowed)) _audio.StopSound(borrowed);
        }

        ChooseFrontVoices();

        // Whatever is left over, nearest first, borrows.
        _distantVoiced.Clear();
        for (int i = keep; i < _engineDistances.Count && i < keep + _adaptiveDistant; i++)
            _distantVoiced.Add(_engineDistances[i].Id);

        foreach (int voiceId in new List<int>(_distantBoundTo.Keys))
        {
            bool wanted = false;
            foreach (int car in _distantVoiced) if (DistantVoiceBase - Math.Abs(car) == voiceId) { wanted = true; break; }
            if (wanted) continue;
            _audio.StopSound(voiceId);
            _distantBoundTo.Remove(voiceId);
        }

        // A census every five seconds: how many cars this machine synthesizes, how many borrow, and how
        // many are past both budgets and silent. Nobody should have to count engines by ear.
        if (now - _lastCensus >= 5.0)
        {
            _lastCensus = now;
            int cars = _engineDistances.Count;
            // Sorted by the keep-biased key, so [0] is not necessarily the nearest.
            float nearestD2 = float.MaxValue;
            foreach (var e in _engineDistances) if (e.D2 < nearestD2) nearestD2 = e.D2;
            float nearest = cars > 0 ? MathF.Sqrt(nearestD2) : 0f;
            Log.Information("Cars: {Cars} on the map — {Live} synthesized, {Borrowed} borrowed, {Silent} out of budget; "
                          + "nearest {Nearest:F0} m; {Echoes} reflection(s) per engine.",
                            cars, _liveEngines.Count, _distantVoiced.Count,
                            Math.Max(0, cars - _liveEngines.Count - _distantVoiced.Count),
                            nearest, _adaptiveEchoes);
            Log.Information("  {Voices} reflection voice(s) of a {Budget} budget, floor {Floor:G3}; "
                          + "{Pending} submission(s) queued, {Transients} transient(s) waiting to be heard.",
                            _engineEchoes.VoiceCount, _engineEchoes.MaxReflectionVoices, _engineEchoes.AudibilityFloor,
                            _audio.PendingSubmissions, WorldAudio.Pending_Count);

            // The longest every source stood still. Target one 60 Hz period, 17 ms; over about 100 ms a
            // car passing in front of you is heard to stop dead and carry on.
            string stages = string.Join(", ",
                StageNames.Select((n, i) => $"{n} {_stageWorstMs[i]:F0}"))
                + "; in the emitters' worst pass " + string.Join(", ", PartNames.Select((n, i) => $"{n} {_partWorstMs[i]:F0}"));
            if (_worstGapMs > 100)
                Log.Warning("Audio placement stalled: every source held its position for up to {Gap:F0} ms "
                          + "in the last 5 s (the pass itself took at most {Work:F0} ms — {Stages}). A car in "
                          + "front of you stops for that long.", _worstGapMs, _worstUpdateMs, stages);
            else
                Log.Information("Audio placement: worst gap {Gap:F0} ms between position refreshes, "
                              + "worst pass {Work:F0} ms ({Stages}).", _worstGapMs, _worstUpdateMs, stages);
            _worstGapMs = 0; _worstUpdateMs = 0;
            Array.Clear(_stageWorstMs);
            Array.Clear(_partWorstMs); _partWorstPass = 0;

            // What the three nearest engines are doing, to tell apart four things that sound the same: the
            // car slowing, the world reporting too low a speed, the driver not holding it, or a shift up.
            // "told" steady and "own" or "rpm" sagging: the synthesis; "told" falling: the car really is.
            _censusOrder.Clear();
            foreach (var e in _engineDistances) if (_liveEngines.Contains(e.Id)) _censusOrder.Add((e.D2, e.Id));
            _censusOrder.Sort(static (x, y) => x.D2.CompareTo(y.D2));
            for (int i = 0; i < Math.Min(3, _censusOrder.Count); i++)
            {
                int id = _censusOrder[i].Id;
                if (!_audio.TryGetEngineTelemetry(id, out float told, out float own, out float rpm, out int gear)) continue;
                Log.Information("  car {Id} ({Preset}) at {Dist:F0} m: told {Told:F0} km/h, driveline {Own:F0} km/h, {Rpm:F0} rpm, gear {Gear}",
                                id, _carPreset.GetValueOrDefault(id, "?"), MathF.Sqrt(_censusOrder[i].D2),
                                told * 3.6f, own * 3.6f, rpm, gear);
                string detail = _audio.EngineVoiceDetail(id);
                if (detail.Length > 0) Log.Information("    car {Id}: {Detail}", id, detail);
            }

            // What reaches you loudest and by which route ("why can I still hear that bus two streets
            // over"): dBFS at the mixer with distance, occlusion, air, shelter and cone applied. "round an
            // edge": the bearing was moved to where the sound bends round something.
            foreach (var v in _audio.LoudestVoices(5))
            {
                string name = _carPreset.TryGetValue(v.EntityId, out var preset) ? preset : v.SoundId;
                Log.Information("  loudest: {Name} ({Id}) at {Dist:F0} m: {Db:F1} dBFS, blocked {Occ:P0}, low/mid/high {Low:F0}/{Mid:F0}/{High:F0} dBFS{Refl}{Edge}",
                                name, v.EntityId, v.Distance, v.Db, v.Occlusion, v.Low, v.Mid, v.High,
                                v.Reflection ? ", a reflection" : "", v.Redirected ? ", round an edge" : "");
            }
        }
    }

    private double _lastCensus;
    private readonly List<(float D2, int Id)> _censusOrder = new();

    /// <summary>When each repeating emitter is next due to speak. See RepeatIntervalSeconds.</summary>
    private readonly Dictionary<int, double> _repeatDue = new();

    /// <summary>Each entity voice's volume and reference distance as last submitted, which its walls'
    /// copies are placed against.</summary>
    private readonly Dictionary<int, (float Volume, float MinDistance)> _placed = new();

    /// <summary>
    /// A wall's copy of a recorded loop: the copy law at the loop's own reference distance
    /// (EarlyReflections.PlacedCopyGain), so the extra spreading is counted once, by the renderer at the
    /// image; and its colour relative to its middle band, so the middle is counted once, in its volume.
    /// </summary>
    internal static (float Volume, float MinDistance, float EqLow, float EqMid, float EqHigh) LoopEchoLevel(
        in AcousticPathData path, float sourceVolume, float sourceMinDistance, float directDistance)
    {
        float mid = Math.Clamp(path.EqMid, 1e-4f, 1f);
        float volume = sourceVolume * EarlyReflections.PlacedCopyGain(Math.Clamp(path.EqMid, 0f, 1f), path.EffectiveDistance,
                                                                      directDistance, sourceMinDistance);
        return (volume, sourceMinDistance, path.EqLow / mid, 1f, path.EqHigh / mid);
    }


    private readonly Dictionary<int, string> _carPreset = new();
    private readonly Dictionary<string, int> _engineSourceByPreset = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Presets met so far in this pass's ranking, and the cars outside the budget kept as their
    /// preset's only engine. See ChooseLiveEngines.</summary>
    private readonly HashSet<string> _presetsKept = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<int> _presetDonors = new();
    private readonly HashSet<int> _liveEngines = new();
    private readonly Dictionary<int, double> _engineStarted = new();
    private readonly List<int> _engineRetiring = new();

    /// <summary>
    /// Which machines are close enough for their two ends to be heard as two: the angle their outlets'
    /// separation subtends from here (Localisation). A car (3.5 m) separates inside about 20 m, a
    /// motorcycle (1 m) inside 6. One extra voice and no extra synthesis (the engine writes both taps),
    /// at the same total level, so crossing the threshold moves the sound and does not change it.
    /// </summary>
    private void ChooseFrontVoices()
    {
        _frontCandidates.Clear();
        foreach (var (id, _, d2) in _engineDistances)
        {
            if (!_liveEngines.Contains(id)) continue;
            // From inside, the two ends of the car are not two sources: both come through the body.
            if (id == _state.RidingEntityId) continue;
            if (!_carPreset.TryGetValue(id, out string? preset)) continue;
            float separation = OutletSeparation(preset);
            if (separation <= 0f) continue;
            if (!OpenFPS.Common.Localisation.Resolvable(separation, MathF.Sqrt(d2))) continue;
            _frontCandidates.Add((id, d2));
        }
        _frontCandidates.Sort((a, b) => a.D2.CompareTo(b.D2));

        // A front voice given back fades, not cut: the tap is a running waveform.
        foreach (int id in _frontVoiced)
        {
            bool survives = false;
            for (int i = 0; i < _frontCandidates.Count && i < _adaptiveFront; i++)
                if (_frontCandidates[i].Id == id) { survives = true; break; }
            if (!survives && !_frontRetiring.Contains(id)) _frontRetiring.Add(id);
        }
        for (int i = _frontRetiring.Count - 1; i >= 0; i--)
        {
            int id = _frontRetiring[i];
            int voice = IntakeVoiceBase - Math.Abs(id);
            bool wanted = false;
            for (int k = 0; k < _frontCandidates.Count && k < _adaptiveFront; k++)
                if (_frontCandidates[k].Id == id) { wanted = true; break; }
            if (wanted) { _frontRetiring.RemoveAt(i); continue; }
            if (_audio.FadeOutEngine(voice)) { _audio.StopSound(voice); _frontRetiring.RemoveAt(i); }
        }

        _frontVoiced.Clear();
        for (int i = 0; i < _frontCandidates.Count && i < _adaptiveFront; i++)
            _frontVoiced.Add(_frontCandidates[i].Id);
    }

    private readonly List<(int Id, float D2)> _frontCandidates = new();

    /// <summary>How far apart a machine's outlets are, metres; memoised, asked per car per frame.</summary>
    private static float OutletSeparation(string preset)
    {
        if (_outletSeparation.TryGetValue(preset, out float cached)) return cached;
        var v = OpenFPS.Common.MachineRegistry.VehicleFor(preset);
        float sep = Vector3.Distance(ExhaustSlot(v), IntakeSlot(v));
        _outletSeparation[preset] = sep;
        return sep;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, float> _outletSeparation = new();

    /// <summary>Where the gas leaves, machine frame: the true tailpipe, not where a single voice sits
    /// (VehicleProfile.ExhaustEmitterBias).</summary>
    private static Vector3 ExhaustSlot(OpenFPS.Common.VehicleProfile v) => v.ExhaustSlot;

    /// <summary>...and the other end: where it breathes, or its nose when the engine is in the back
    /// (VehicleProfile.FrontTapZ).</summary>
    private static Vector3 IntakeSlot(OpenFPS.Common.VehicleProfile v)
        => new(0f, v.FrontTapHeight, v.FrontTapZ);

    /// <summary>
    /// A machine's front outlet, placed: it reads the front tap of the entity's live engine
    /// (EngineTapState), so it costs a buffer read and an HRTF. All but the position is the machine's
    /// other voice's: level reference, range, region, sampled time.
    /// </summary>
    private void FrontVoice(EntitySnapshot snap, OpenFPS.Common.VehicleProfile profile, in AcousticPathData path,
                            float volume, float minDistance, float range, double sampledAt)
    {
        int voiceId = IntakeVoiceBase - Math.Abs(snap.Id);
        Vector3 pos = snap.Transform.Position
                    + Vector3.Transform(IntakeSlot(profile), snap.Transform.Rotation);

        var e = new SpatialEmitter
        {
            EntityId = voiceId,
            SoundId = "engine-intake",
            IsSynth = true,
            IntakeOfEntity = snap.Id,
            EngineKey = "",
            Mode = PlaybackMode.LoopOne,
            Type = EmitterType.EntityAttached,
            Position = pos,
            ApparentPosition = pos,
            Velocity = snap.Velocity,
            PositionSampledAt = sampledAt,
            // The exhaust voice's level reference: the taps already carry the difference between intake
            // and tailpipe, and a second, guessed one here would be a taste constant.
            Volume = volume,
            MinDistance = minDistance,
            ExtentMetres = minDistance,
            Range = range,
            Pitch = 1f,
            Occlusion = path.Occlusion,
            EqLow = path.EqLow, EqMid = path.EqMid, EqHigh = path.EqHigh,
            AirLowDb = path.AirLowDb, AirMidDb = path.AirMidDb, AirHighDb = path.AirHighDb,
            ApertureFactor = path.ApertureFactor,
            TransmissionBleed = path.TransmissionBleed,
            EffectiveDistance = path.EffectiveDistance,
            TargetRegionId = path.RegionId,
        };
        ApplyGround(ref e, _groundWorld);
        if (_audio.IsPlaying(voiceId)) _audio.UpdateSpatialAttributes(e);
        else _audio.PlayPhysicalSoundDirect(e);
    }

    // ── The cabin you are sitting in, path by path (CabinPaths) ────────────────────────────────────

    internal const int CabinVoiceBase = -1_600_000;
    /// <summary>The vehicle whose cabin paths have voices, and how many paths it has.</summary>
    private int _cabinVoiced = int.MinValue, _cabinVoicedPaths;
    /// <summary>Cabin path voices fading out, by voice id.</summary>
    private readonly List<int> _cabinRetiring = new();
    private int _cabinSeenFrame = -1;

    private static int CabinVoiceId(int path) => CabinVoiceBase - path;

    /// <summary>
    /// Every path into the cabin but the bulkhead (which is the engine's own voice): a tap each on the
    /// engine's ring for it, riding with the head, turned to where the path comes in. Everything about
    /// each that is not the direction is the interior voice's: the same level, the same distance, the
    /// same room. See CabinPaths and EngineTapState.
    /// </summary>
    private void CabinVoices(EntitySnapshot snap, OpenFPS.Client.AudioEngine.Core.Engine.CabinPaths.Layout layout,
                             Vector3 ear, in SpatialEmitter engine, Vector3 eyePos)
    {
        if (_cabinVoiced != snap.Id) RetireCabinVoices();
        _cabinVoiced = snap.Id;
        _cabinVoicedPaths = layout.Count;
        _cabinSeenFrame = _frameCount;
        for (int p = 1; p < layout.Count; p++)
        {
            int voiceId = CabinVoiceId(p);
            _cabinRetiring.Remove(voiceId);
            var offset = Vector3.Transform(OpenFPS.Client.AudioEngine.Core.Engine.CabinPaths.Offset(layout, p, ear), snap.Transform.Rotation);
            var e = engine;
            e.EntityId = voiceId;
            e.SoundId = "engine-cabin";
            e.EngineKey = "";
            e.PhysicalKey = "";
            e.CabinOfEntity = snap.Id;
            e.CabinPath = p;
            e.IntakeOfEntity = 0;
            e.EchoOfEntity = 0;
            e.Interior = false;
            e.FollowsListener = true;
            e.ListenerOffset = offset;
            // A voice on the head still needs a world position: the budget culls by it.
            e.Position = eyePos + offset;
            e.ApparentPosition = e.Position;
            if (_audio.IsPlaying(voiceId)) _audio.UpdateSpatialAttributes(e);
            else _audio.PlayPhysicalSoundDirect(e);
        }
    }

    /// <summary>Once a frame: the cabin's voices fade and stop when you leave that vehicle or its engine
    /// loses its voice.</summary>
    private void UpdateCabinVoices()
    {
        if (_cabinVoiced != int.MinValue && _cabinSeenFrame != _frameCount) RetireCabinVoices();
        for (int i = _cabinRetiring.Count - 1; i >= 0; i--)
        {
            int voice = _cabinRetiring[i];
            if (_audio.FadeOutEngine(voice)) { _audio.StopSound(voice); _cabinRetiring.RemoveAt(i); }
        }
    }

    private void RetireCabinVoices()
    {
        if (_cabinVoiced == int.MinValue) return;
        for (int p = 1; p < _cabinVoicedPaths; p++)
            if (!_cabinRetiring.Contains(CabinVoiceId(p))) _cabinRetiring.Add(CabinVoiceId(p));
        _cabinVoiced = int.MinValue;
        _cabinVoicedPaths = 0;
    }

    // ── Extended sources: a tree's crown, a fire's bed (ExtendedSources) ───────────────────────────

    /// <summary>Voice ids for the outer places of an extended source, place 1 up,
    /// <see cref="OpenFPS.Client.AudioEngine.Core.Nature.ExtendedSources.MaxPlaces"/> a source. At eight a
    /// source a ten-place surf beach took the next source's first two ids (2026-10-06).</summary>
    internal const int PlaceVoiceBase = -5_000_000;
    internal const int PlaceIdsPerSource = OpenFPS.Client.AudioEngine.Core.Nature.ExtendedSources.MaxPlaces;
    internal static int PlaceVoiceId(int sourceId, int place) => PlaceVoiceBase - Math.Abs(sourceId) * PlaceIdsPerSource - place;

    /// <summary>After a source merges to its middle its outer voices are kept this long, s, so what was
    /// already rendered into them (up to the render lead ahead) rings out rather than being cut.</summary>
    private const double MergeHoldSeconds = 2.0;

    private sealed class Spreading
    {
        public Vector3[] Layout = Array.Empty<Vector3>();
        public Vector3[] At = Array.Empty<Vector3>();
        public float Spread;
        public bool Voiced;
        public double MergedSince = double.NaN;
        public long Frame;
    }
    private readonly Dictionary<int, Spreading> _spreading = new();
    private readonly HashSet<int> _placesGranted = new();
    private readonly List<(float D2, int Id, int Outer)> _placeCandidates = new();

    /// <summary>Which live extended sources may spread this frame: nearest first, while their outer
    /// places fit in <see cref="_adaptivePlaces"/>. One that wants none (too far to be heard as wide)
    /// asks for nothing.</summary>
    private void ChoosePlaces(WorldSnapshot world, Vector3 eyePos)
    {
        _placesGranted.Clear();
        _placeCandidates.Clear();
        foreach (int id in _liveMachines)
        {
            if (!world.Entities.TryGetValue(id, out var snap)) continue;
            var layout = OpenFPS.Client.AudioEngine.Core.Nature.ExtendedSources.Layout(snap.Definition.SoundEmitter.SoundId);
            if (layout == null) continue;
            var at = OpenFPS.Common.AudioEmission.PointFor(snap);
            float d2 = Vector3.DistanceSquared(at, eyePos);
            if (OpenFPS.Client.AudioEngine.Core.Nature.ExtendedSources.SpreadFor(
                    OpenFPS.Client.AudioEngine.Core.Nature.ExtendedSources.Reach(layout), MathF.Sqrt(d2)) <= 0f) continue;
            _placeCandidates.Add((d2, id, layout.Length - 1));
        }
        _placeCandidates.Sort(static (a, b) => a.D2.CompareTo(b.D2));
        int left = _adaptivePlaces;
        foreach (var (_, id, outer) in _placeCandidates)
        {
            if (outer > left) continue;
            left -= outer;
            _placesGranted.Add(id);
        }
    }
    private readonly List<int> _spreadingGone = new();
    private long _spreadFrame;

    /// <summary>
    /// How much of a tree or a fire its outer places carry from here, slewed, and the one gain that keeps
    /// the places together as loud as the source from its middle (ExtendedSources.Balance), applied to its
    /// own voice's emitter before it is submitted.
    /// </summary>
    private Spreading SpreadOf(EntitySnapshot snap, Vector3[] layout, ref SpatialEmitter e, Vector3 eyePos, float dt, double now)
    {
        if (!_spreading.TryGetValue(snap.Id, out var sp) || sp.Layout != layout)
            _spreading[snap.Id] = sp = new Spreading { Layout = layout, At = new Vector3[layout.Length] };
        sp.Frame = _spreadFrame;
        for (int i = 0; i < layout.Length; i++) sp.At[i] = e.Position + Vector3.Transform(layout[i], snap.Transform.Rotation);
        float target = !_placesGranted.Contains(snap.Id) ? 0f : OpenFPS.Client.AudioEngine.Core.Nature.ExtendedSources.SpreadFor(
            OpenFPS.Client.AudioEngine.Core.Nature.ExtendedSources.Reach(layout), Vector3.Distance(eyePos, e.Position));
        sp.Spread = OpenFPS.Client.AudioEngine.Core.Nature.ExtendedSources.Slew(sp.Spread, target, MathF.Min(dt, 0.25f));
        Span<float> shares = stackalloc float[layout.Length];
        OpenFPS.Client.AudioEngine.Core.Nature.ExtendedSources.Shares(sp.Spread, shares);
        e.Volume *= OpenFPS.Client.AudioEngine.Core.Nature.ExtendedSources.Balance(eyePos, e.Position, sp.At, shares, e.MinDistance, e.Range);
        e.Spread = sp.Spread;
        if (sp.Spread > 0f) sp.MergedSince = double.NaN;
        else if (double.IsNaN(sp.MergedSince)) sp.MergedSince = now;
        return sp;
    }

    /// <summary>
    /// The outer places of a tree or a fire, each a voice at its own point reading its own stream of the
    /// one synth. Everything but the point is the source's own emitter: the same level reference, range,
    /// path, region and declared level, and the same balance gain, so the places and the middle are one
    /// source. Kept while any of it is spread, and <see cref="MergeHoldSeconds"/> after.
    /// </summary>
    private void PlaceOuter(int sourceId, Spreading sp, in SpatialEmitter middle, WorldSnapshot world, double now)
    {
        // A source that has never spread (out of the angle, or out of the budget) asks for nothing.
        bool want = sp.Spread > 0f || (sp.Voiced && !double.IsNaN(sp.MergedSince) && now - sp.MergedSince < MergeHoldSeconds);
        if (!want)
        {
            if (sp.Voiced) StopOuter(sourceId, sp);
            return;
        }
        sp.Voiced = true;
        for (int k = 1; k < sp.Layout.Length; k++)
        {
            var e = middle;
            e.EntityId = PlaceVoiceId(sourceId, k);
            e.Position = sp.At[k];
            e.ApparentPosition = middle.ApparentPosition + (sp.At[k] - middle.Position);
            e.PlaceOfEntity = sourceId;
            e.Place = k;
            e.Spread = 0f;
            e.StartSoundId = "";
            e.StopSoundId = "";
            ApplyGround(ref e, world);
            if (_placesRetiring.Remove(e.EntityId)) _audio.ReviveEngine(e.EntityId);
            if (_audio.IsPlaying(e.EntityId)) _audio.UpdateSpatialAttributes(e);
            else _audio.PlayPhysicalSoundDirect(e);
        }
    }

    /// <summary>Lets a source's outer places go: faded, as any running synth is (they may still be
    /// sounding when the source leaves the budget), or at once when the source itself is gone.</summary>
    private void StopOuter(int sourceId, Spreading sp, bool now = false)
    {
        for (int k = 1; k < sp.Layout.Length; k++)
        {
            int id = PlaceVoiceId(sourceId, k);
            _groundCache.Remove(id);
            if (now) { _placesRetiring.Remove(id); _audio.StopSound(id); }
            else if (!_placesRetiring.Contains(id)) _placesRetiring.Add(id);
        }
        sp.Voiced = false;
    }
    private readonly List<int> _placesRetiring = new();

    /// <summary>Lets go the places of every source not placed this frame: out of the machine budget, or gone.</summary>
    private void RetireSpreading()
    {
        _spreadingGone.Clear();
        foreach (var (id, sp) in _spreading)
            if (sp.Frame != _spreadFrame) _spreadingGone.Add(id);
        foreach (int id in _spreadingGone)
        {
            if (_spreading.Remove(id, out var sp) && sp.Voiced) StopOuter(id, sp);
        }
        for (int i = _placesRetiring.Count - 1; i >= 0; i--)
        {
            int id = _placesRetiring[i];
            if (_audio.FadeOutEngine(id)) { _audio.StopSound(id); _placesRetiring.RemoveAt(i); }
        }
        _spreadFrame++;
    }

    /// <summary>The birds: found from the map's foliage and roofs, not placed. See BirdLife.</summary>
    private readonly BirdLife _birds;

    /// <summary>The rain round the listener: the surfaces it lands on, as a few voices. See RainField.</summary>
    private readonly RainField _rain;

    /// <summary>Vehicles carrying a siren, found this frame.</summary>
    private readonly HashSet<int> _sirenCars = new();
    private readonly List<int> _sirensGone = new();

    /// <summary>
    /// Every siren on the map, placed every frame whatever its car's engine is doing. Not from the car's
    /// emitter pass, which runs only for an engine that won a voice: a siren is 35 dB louder than its
    /// engine, and a car dropped from the budget left its siren wailing where it had been.
    /// </summary>
    private void UpdateSirens(WorldSnapshot world, Vector3 eyePos)
    {
        _sirenCars.Clear();
        foreach (var snap in world.DynamicEntities)
        {
            string? sid = snap.Definition.SoundEmitter.SoundId;
            if (sid == null || !sid.StartsWith("engine:", StringComparison.OrdinalIgnoreCase)) continue;
            string preset = sid[7..];
            if (!OpenFPS.Common.MachineRegistry.Knows(preset)
                || OpenFPS.Common.MachineRegistry.VehicleFor(preset).Siren is not { } sirenKey) continue;
            _sirenCars.Add(snap.Id);
            var path = VehiclePath(world, snap, eyePos);
            SirenVoice(snap, sirenKey, path, world.PositionsSampledAt, eyePos);
        }
        _sirensGone.Clear();
        foreach (int id in _sirenVoiced) if (!_sirenCars.Contains(id)) _sirensGone.Add(id);
        foreach (int id in _sirensGone)
        {
            _sirenVoiced.Remove(id);
            _audio.StopSound(SirenVoiceBase - Math.Abs(id));
        }
    }

    /// <summary>
    /// The path for a horn or a siren: the occlusion worker's answer for the vehicle, asked here since a
    /// vehicle whose engine has no voice is not asked by the per-voice pass. Until it answers, the
    /// one-shots' path stands in: "no answer" read as "nothing in the way" played a horn behind three
    /// buildings at full level.
    /// </summary>
    private AcousticPathData VehiclePath(WorldSnapshot world, EntitySnapshot snap, Vector3 eyePos)
    {
        var at = OpenFPS.Common.AudioEmission.PointFor(snap);
        if (!_audio.IsPlaying(snap.Id))
            _acousticWorker.EnqueueRequest(new AcousticRequest
            {
                EntityId = snap.Id, ListenerPos = eyePos, SourcePos = at,
                SourceRadius = OpenFPS.Common.AudioEmission.OcclusionRadiusFor(snap),
            });
        if (_acousticWorker.TryGetResult(snap.Id, out var paths))
            foreach (var p in paths)
                if (!p.IsReflection) return p;
        return _acoustics.CalculateAcousticPath(world, snap.Id, eyePos, at);
    }

    /// <summary>
    /// The path for a sounding entity the worker has no answer for yet (a car that just won a voice): the
    /// worker's answer for a source near this one, moved here, or failing that the one-shots' path; and
    /// the worker is asked now. Started on an unoccluded line, a car behind a building came in at full
    /// level for a fifth of a second (2026-10-03).
    /// </summary>
    private AcousticPathData FirstAnswer(WorldSnapshot world, EntitySnapshot snap, Vector3 eyePos)
    {
        var at = OpenFPS.Common.AudioEmission.PointFor(snap);
        _acousticWorker.EnqueueRequest(new AcousticRequest
        {
            EntityId = snap.Id, ListenerPos = eyePos, SourcePos = at,
            SourceRadius = OpenFPS.Common.AudioEmission.OcclusionRadiusFor(snap),
        });
        var path = _acoustics.CalculateAcousticPath(world, snap.Id, eyePos, at);
        if (_acousticWorker.TryGetNearby(eyePos, at, out var near))
        {
            Vector3 moved = at - near.SourcePosition;
            float nearDist = MathF.Max(0.1f, Vector3.Distance(eyePos, near.SourcePosition));
            path = path with
            {
                Occlusion = near.Occlusion, EqLow = near.EqLow, EqMid = near.EqMid, EqHigh = near.EqHigh,
                TransmissionBleed = near.TransmissionBleed, ApertureFactor = near.ApertureFactor,
                ApparentPosition = near.ApparentPosition + moved,
                EffectiveDistance = near.EffectiveDistance * Vector3.Distance(eyePos, at) / nearDist,
            };
        }
        return path;
    }

    /// <summary>Voice ids for a vehicle's horn, one per vehicle.</summary>
    internal const int HornVoiceBase = -1_200_000;

    /// <summary>Trains sounding their horn or bell ("preset/train"), until when. Signal sources get a
    /// voice only meanwhile: ranked at a horn's 139 dB, a silent one would hold a voice from kilometres off.</summary>
    private readonly Dictionary<string, double> _trainSignals = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A train sounds for a crossing on its own horn (or whistle) and bell (TrainSignal); this keeps the
    /// signal sources ranked while it lasts. A synth made later takes the signal, less the time gone.
    /// </summary>
    private void StartTrainSignal(string train, float[] warning, float bellSeconds)
    {
        _trainSignals[train] = _now() + OpenFPS.Common.TrainSignal.Duration(warning, bellSeconds) + 1.0;
        _audio.SignalTrain(train, warning, bellSeconds, 0.0);
    }

    /// <summary>A train's horn, whistle or bell source, while the train is not sounding it.</summary>
    private bool SilentSignal(string soundId, double now)
    {
        if (!soundId.StartsWith("rail:", StringComparison.OrdinalIgnoreCase)) return false;
        if (!_signalTrains.TryGetValue(soundId, out string? train))
        {
            train = null;
            if (OpenFPS.Client.AudioEngine.Fmod.TrainVoiceState.ParseKey(soundId, out string preset, out string name, out int index))
            {
                try
                {
                    var layout = OpenFPS.Common.TrainLayout.Sources(OpenFPS.Common.TrainProfile.ByName(preset));
                    if (index >= 0 && index < layout.Count && layout[index].IsSignal) train = preset + "/" + name;
                }
                catch (Exception) { }
            }
            _signalTrains[soundId] = train;
        }
        return train != null && (!_trainSignals.TryGetValue(train, out double until) || now > until);
    }

    /// <summary>Which train a signal source belongs to, by SoundId; null for every other source.</summary>
    private readonly Dictionary<string, string?> _signalTrains = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Horns being sounded: the vehicle, the key its voice was built from, and when the
    /// voice may be let go.</summary>
    private readonly Dictionary<int, (string Key, double Until)> _horns = new();
    private readonly List<int> _hornsDone = new();

    /// <summary>
    /// Somebody on the street sounded their horn: the voice is built next frame on the vehicle and lasts
    /// the rhythm. A second honk from the same car replaces the first (one horn, one hand).
    /// </summary>
    private void StartHorn(int entityId, string horn, float[] rhythm)
    {
        int voiceId = HornVoiceBase - Math.Abs(entityId);
        if (_horns.Remove(entityId)) _audio.StopSound(voiceId);
        _horns[entityId] = (OpenFPS.Common.Honk.Key(horn, rhythm),
                            OpenFPS.Common.AudioClock.Now + OpenFPS.Common.Honk.Duration(rhythm) + 1.0);
    }

    /// <summary>Places every sounding horn at the front of its vehicle, through the vehicle's own path.</summary>
    private void UpdateHorns(WorldSnapshot world, Vector3 eyePos, double now)
    {
        UpdateHeldHorns(world, eyePos, _now());
        if (_horns.Count == 0) return;
        _hornsDone.Clear();
        foreach (var (id, horn) in _horns)
        {
            int voiceId = HornVoiceBase - Math.Abs(id);
            if (now > horn.Until || !world.Entities.TryGetValue(id, out var snap))
            {
                _hornsDone.Add(id);
                continue;
            }
            var path = VehiclePath(world, snap, eyePos);
            // Behind a car's grille, a little above the bumper; on a locomotive's cab roof.
            bool rail = snap.Definition.SoundEmitter.SoundId?.StartsWith("rail:", StringComparison.OrdinalIgnoreCase) == true;
            Vector3 pos = snap.Transform.Position
                        + Vector3.Transform(rail ? new Vector3(0f, 4.2f, 0f) : new Vector3(0f, 0.6f, Grille(snap)),
                                            snap.Transform.Rotation);
            float levelDb = OpenFPS.Common.Honk.LevelDb(horn.Key[OpenFPS.Common.Honk.Prefix.Length..horn.Key.LastIndexOf(':')]);
            var (gain, reference) = OpenFPS.Common.Loudness.Place(levelDb);
            var e = new SpatialEmitter
            {
                EntityId = voiceId,
                SoundId = "horn",
                IsSynth = true,
                PhysicalKey = horn.Key,
                EngineKey = "",
                Mode = PlaybackMode.LoopOne,
                Type = EmitterType.EntityAttached,
                Position = pos,
                // From the horn where the car is now, turned toward the path's edge only when blocked. The
                // path's own point is the exhaust as of the worker's last answer (up to ten frames old):
                // a moving car's horn would trail behind it, from its tailpipe.
                ApparentPosition = SirenApparent(path, pos, eyePos),
                Velocity = snap.Velocity,
                PositionSampledAt = world.PositionsSampledAt,
                Direction = Vector3.Transform(Vector3.UnitZ, snap.Transform.Rotation),
                Volume = gain,
                EarLevelDb = levelDb,
                MinDistance = reference,
                Range = OpenFPS.Common.Loudness.AudibleRange(levelDb),
                Pitch = 1f,
                EngineRunning = true,
                Occlusion = path.Occlusion,
                EqLow = path.EqLow, EqMid = path.EqMid, EqHigh = path.EqHigh,
                AirLowDb = path.AirLowDb, AirMidDb = path.AirMidDb, AirHighDb = path.AirHighDb,
                ApertureFactor = path.ApertureFactor,
                TransmissionBleed = path.TransmissionBleed,
                EffectiveDistance = path.EffectiveDistance,
                TargetRegionId = path.RegionId,
            };
            ApplyGround(ref e, _groundWorld);
            if (_audio.IsPlaying(voiceId)) _audio.UpdateSpatialAttributes(e);
            else _audio.PlayPhysicalSoundDirect(e);
        }
        foreach (int id in _hornsDone)
        {
            _horns.Remove(id);
            _audio.StopSound(HornVoiceBase - Math.Abs(id));
        }
    }

    // ── Tyres over the rails ────────────────────────────────────────────────────────────────
    //
    // A level crossing's rails, from the map's roads. A voiced vehicle about to roll a wheel over one has
    // the strike scheduled in its voice for the moment the wheel reaches the rail (WheelStrikes), once
    // the wheel is under a second away (longer than any voice renders ahead); let go once well past.

    private IReadOnlyList<OpenFPS.Common.CrossingRails> _crossings = Array.Empty<OpenFPS.Common.CrossingRails>();
    private readonly Dictionary<(int Id, int Wheel, int Rail), double> _struck = new();
    private readonly Dictionary<string, (Vector3[] At, float[] Load, float[] Radius)> _wheelLayouts = new();
    private readonly List<OpenFPS.Client.AudioEngine.Core.WheelStrike> _strikeBatch = new();
    private readonly List<(int, int, int)> _unstruck = new();

    /// <summary>The map's level crossings, as the rails lie (null on a map without).</summary>
    public void SetCrossings(IReadOnlyList<OpenFPS.Common.CrossingRails>? crossings)
    {
        _crossings = crossings ?? Array.Empty<OpenFPS.Common.CrossingRails>();
        _struck.Clear();
    }

    /// <summary>How far ahead of a rail a wheel's strike is scheduled, seconds: more than a voice ever
    /// renders ahead (PhysicalVoiceState.MaxLeadSeconds 0.7).</summary>
    private const double StrikeWithinSeconds = 0.9;

    /// <summary>A preset's wheels where the server's model puts them (WheelDynamics), from the entity's middle.</summary>
    private (Vector3[] At, float[] Load, float[] Radius) WheelLayout(string preset)
    {
        if (_wheelLayouts.TryGetValue(preset, out var l)) return l;
        var v = OpenFPS.Common.MachineRegistry.VehicleFor(preset);
        var body = new OpenFPS.Common.WheelDynamics(v);
        float cog = v.Running.CentreOfGravityZ;
        int n = body.Wheels.Length;
        l = (new Vector3[n], new float[n], new float[n]);
        for (int i = 0; i < n; i++)
        {
            l.At[i] = new Vector3(body.Wheels[i].Y, 0f, body.Wheels[i].X + cog);
            l.Load[i] = body.Wheels[i].StaticLoad;
            l.Radius[i] = body.Wheels[i].Radius;
        }
        _wheelLayouts[preset] = l;
        return l;
    }

    /// <summary>The strikes to hand this vehicle's voice this frame, or null for none new.</summary>
    internal OpenFPS.Client.AudioEngine.Core.WheelStrike[]? RailStrikes(EntitySnapshot snap, string preset, double sampledAt)
    {
        if (_crossings.Count == 0) return null;
        var vel = new Vector3(snap.Velocity.X, 0f, snap.Velocity.Z);
        float speed = vel.Length();
        var pos = snap.Transform.Position;
        _strikeBatch.Clear();
        _unstruck.Clear();
        (Vector3[] At, float[] Load, float[] Radius)? layout = null;
        for (int ci = 0; ci < _crossings.Count; ci++)
        {
            var c = _crossings[ci];
            var off = new Vector3(pos.X - c.Centre.X, 0f, pos.Z - c.Centre.Z);
            if (off.LengthSquared() > 40f * 40f) continue;
            layout ??= WheelLayout(preset);
            var across = new Vector3(c.Along.Z, 0f, -c.Along.X);
            float u = Vector3.Dot(vel, across);
            var (left, right) = c.RailPoints();
            for (int r = 0; r < 2; r++)
            {
                var rail = r == 0 ? left : right;
                for (int i = 0; i < layout.Value.At.Length; i++)
                {
                    var p = pos + Vector3.Transform(layout.Value.At[i], snap.Transform.Rotation);
                    var d = new Vector3(p.X - rail.X, 0f, p.Z - rail.Z);
                    float s = Vector3.Dot(d, across);
                    var key = (snap.Id, i, ci * 2 + r);
                    bool known = _struck.ContainsKey(key);
                    if (MathF.Abs(Vector3.Dot(new Vector3(p.X - c.Centre.X, 0f, p.Z - c.Centre.Z), c.Along)) > c.HalfLengthMetres + 1f)
                    {
                        if (known) _unstruck.Add(key);
                        continue;
                    }
                    if (speed < 0.3f || MathF.Abs(u) < 0.2f)
                    {
                        if (known && MathF.Abs(s) > 2f) _unstruck.Add(key);
                        continue;
                    }
                    double t = -s / u;
                    if (t < 0)
                    {
                        // Past it: forget it once well clear, so the next pass strikes again.
                        if (known && MathF.Abs(s) > 2f) _unstruck.Add(key);
                        continue;
                    }
                    if (known || t > StrikeWithinSeconds) continue;
                    double at = sampledAt + t;
                    _struck[key] = at;
                    float radius = layout.Value.Radius[i];
                    _strikeBatch.Add(new OpenFPS.Client.AudioEngine.Core.WheelStrike(
                        i, at,
                        OpenFPS.Client.AudioEngine.Core.WheelStrikes.PeakPascals(speed, layout.Value.Load[i], OpenFPS.Client.AudioEngine.Core.WheelStrikes.CrossingStepMetres),
                        OpenFPS.Client.AudioEngine.Core.WheelStrikes.ContactSeconds(speed, radius)));
                }
            }
        }
        foreach (var k in _unstruck) _struck.Remove(k);
        return _strikeBatch.Count == 0 ? null : _strikeBatch.ToArray();
    }

    /// <summary>Voice ids for a horn a driver is holding down, one per vehicle.</summary>
    internal const int HeldHornVoiceBase = -2_100_000;

    /// <summary>Held horns with a voice: the vehicle, and when its key came up (NaN while held).</summary>
    private readonly Dictionary<int, double> _heldHorns = new();
    private readonly List<int> _heldDone = new();

    /// <summary>How long a released horn's voice is kept for its own valve or relay to close, seconds.</summary>
    private const double HornReleaseSeconds = 0.8;

    /// <summary>
    /// The horn of every vehicle whose driver holds H (EntityState.Signals): its own horn model
    /// (VehicleProfile.HornFor) at the grille for as long as the wire says. Let go, the model's valve or
    /// relay ends the note and the voice goes a moment later.
    /// </summary>
    private void UpdateHeldHorns(WorldSnapshot world, Vector3 eyePos, double now)
    {
        foreach (var snap in world.DynamicEntities)
        {
            if (!OpenFPS.Common.VehicleSignalBits.IsManual(snap.Signals)) continue;
            bool held = OpenFPS.Common.VehicleSignalBits.HornHeld(snap.Signals);
            if (!held && !_heldHorns.ContainsKey(snap.Id)) continue;
            string? sid = snap.Definition.SoundEmitter.SoundId;
            if (sid == null || !sid.StartsWith("engine:", StringComparison.OrdinalIgnoreCase)
                || !OpenFPS.Common.MachineRegistry.Knows(sid[7..])) continue;
            string horn = OpenFPS.Common.VehicleProfile.HornFor(OpenFPS.Common.MachineRegistry.VehicleFor(sid[7..]));
            if (held) _heldHorns[snap.Id] = double.NaN;
            else if (double.IsNaN(_heldHorns[snap.Id])) _heldHorns[snap.Id] = now;
            HeldHornVoice(world, snap, horn, held, eyePos);
        }
        _heldDone.Clear();
        foreach (var (id, releasedAt) in _heldHorns)
            if (!world.Entities.ContainsKey(id) || (!double.IsNaN(releasedAt) && now - releasedAt > HornReleaseSeconds))
                _heldDone.Add(id);
        foreach (int id in _heldDone)
        {
            _heldHorns.Remove(id);
            _audio.StopSound(HeldHornVoiceBase - Math.Abs(id));
        }
    }

    private void HeldHornVoice(WorldSnapshot world, EntitySnapshot snap, string horn, bool blowing, Vector3 eyePos)
    {
        int voiceId = HeldHornVoiceBase - Math.Abs(snap.Id);
        var path = VehiclePath(world, snap, eyePos);
        Vector3 pos = snap.Transform.Position + Vector3.Transform(new Vector3(0f, 0.6f, Grille(snap)), snap.Transform.Rotation);
        float levelDb = OpenFPS.Common.Honk.LevelDb(horn);
        var (gain, reference) = OpenFPS.Common.Loudness.Place(levelDb);
        var e = new SpatialEmitter
        {
            EntityId = voiceId,
            SoundId = "horn",
            IsSynth = true,
            PhysicalKey = OpenFPS.Common.Honk.HoldKey(horn),
            EngineKey = "",
            Mode = PlaybackMode.LoopOne,
            Type = EmitterType.EntityAttached,
            Position = pos,
            ApparentPosition = SirenApparent(path, pos, eyePos),
            Velocity = snap.Velocity,
            PositionSampledAt = world.PositionsSampledAt,
            Direction = Vector3.Transform(Vector3.UnitZ, snap.Transform.Rotation),
            Volume = gain,
            EarLevelDb = levelDb,
            MinDistance = reference,
            Range = OpenFPS.Common.Loudness.AudibleRange(levelDb),
            Pitch = 1f,
            // The hand on the horn: the voice blows while this is set.
            EngineRunning = blowing,
            Occlusion = path.Occlusion,
            EqLow = path.EqLow, EqMid = path.EqMid, EqHigh = path.EqHigh,
            AirLowDb = path.AirLowDb, AirMidDb = path.AirMidDb, AirHighDb = path.AirHighDb,
            ApertureFactor = path.ApertureFactor,
            TransmissionBleed = path.TransmissionBleed,
            EffectiveDistance = path.EffectiveDistance,
            TargetRegionId = path.RegionId,
        };
        ApplyGround(ref e, _groundWorld);
        if (_audio.IsPlaying(voiceId)) _audio.UpdateSpatialAttributes(e);
        else if (blowing) _audio.PlayPhysicalSoundDirect(e);
    }

    /// <summary>
    /// A vehicle's siren: its own voice at its own level, at the grille. A siren head is 130 dB at a metre
    /// where the car is 95, so sharing the engine's reference would square the siren or bury the car
    /// (SirenVoiceState). A driven vehicle sounds as switched; traffic's mode is read off what the car
    /// does (<see cref="SirenModeFor"/>).
    /// </summary>
    private void SirenVoice(EntitySnapshot snap, string sirenKey, in AcousticPathData path, double sampledAt, Vector3 eyePos)
    {
        OpenFPS.Common.SirenSpec spec;
        try { spec = OpenFPS.Common.SirenSpec.ByName(sirenKey); }
        catch { return; }

        float speed = snap.Velocity.Length();
        // A vehicle somebody can drive sounds its siren as the driver switched it; traffic, as it drives.
        var mode = OpenFPS.Common.VehicleSignalBits.IsManual(snap.Signals)
            ? OpenFPS.Common.VehicleSignalBits.Siren(snap.Signals)
            : SirenModeFor(snap.Id, speed, sampledAt);
        int voiceId = SirenVoiceBase - Math.Abs(snap.Id);
        if (mode == OpenFPS.Common.SirenMode.Off)
        {
            if (_sirenVoiced.Remove(snap.Id)) _audio.StopSound(voiceId);
            return;
        }

        Vector3 pos = SirenMouth(snap);
        var (gain, reference) = OpenFPS.Common.Loudness.Place(spec.SourceLevelDb, spec.HornMouthMetres);

        var e = new SpatialEmitter
        {
            EntityId = voiceId,
            SoundId = "siren",
            IsSynth = true,
            PhysicalKey = "siren:" + sirenKey,
            EngineKey = "",
            Mode = PlaybackMode.LoopOne,
            Type = EmitterType.EntityAttached,
            Position = pos,
            ApparentPosition = SirenApparent(path, pos, eyePos),
            Velocity = snap.Velocity,
            PositionSampledAt = sampledAt,
            // Which way the horn points. Without it the frame falls back to the velocity, which is no
            // answer when the car slows for a junction, exactly when a siren matters most.
            Direction = Vector3.Transform(Vector3.UnitZ, snap.Transform.Rotation),
            Volume = gain,
            EarLevelDb = spec.SourceLevelDb,
            MinDistance = reference,
            ExtentMetres = spec.HornMouthMetres,
            Range = OpenFPS.Common.Loudness.AudibleRange(spec.SourceLevelDb),
            Pitch = 1f,
            // The mode rides in the lever slot, as a train's notch does.
            PowerLever = (float)(int)mode,
            EngineRunning = true,
            Occlusion = path.Occlusion,
            EqLow = path.EqLow, EqMid = path.EqMid, EqHigh = path.EqHigh,
            AirLowDb = path.AirLowDb, AirMidDb = path.AirMidDb, AirHighDb = path.AirHighDb,
            ApertureFactor = path.ApertureFactor,
            TransmissionBleed = path.TransmissionBleed,
            EffectiveDistance = path.EffectiveDistance,
            TargetRegionId = path.RegionId,
        };
        ApplyGround(ref e, _groundWorld);
        if (_audio.IsPlaying(voiceId)) _audio.UpdateSpatialAttributes(e);
        else { _audio.PlayPhysicalSoundDirect(e); _sirenVoiced.Add(snap.Id); }
    }

    /// <summary>A siren head: at the grille, which is where the horn is.</summary>
    internal static Vector3 SirenMouth(in EntitySnapshot snap)
        => snap.Transform.Position + Vector3.Transform(new Vector3(0f, 0.4f, Grille(snap)), snap.Transform.Rotation);

    /// <summary>
    /// How far ahead of a vehicle's middle its grille is, metres (where horns and a siren head sit): a
    /// quarter of a metre in from the nose of its declared body, or 1.9 m for a vehicle the client cannot
    /// name.
    /// </summary>
    internal static float Grille(in EntitySnapshot snap)
    {
        string? sid = snap.Definition.SoundEmitter.SoundId;
        if (sid != null && sid.StartsWith("engine:", StringComparison.OrdinalIgnoreCase)
            && OpenFPS.Common.MachineRegistry.Knows(sid[7..]))
            return MathF.Max(0.5f, OpenFPS.Common.MachineRegistry.VehicleFor(sid[7..]).LengthMetres * 0.5f - 0.25f);
        return 1.9f;
    }

    /// <summary>
    /// Where a siren (or horn) is heard from: its own head, or, when the car's path arrives round
    /// something, that bearing at the head's distance. One answer for both writers (the siren's update and
    /// the car's path): with two, the image swung between bearings every frame (3.6 degrees for a car
    /// crossing 100 m out) and a far siren fluttered.
    /// </summary>
    internal static Vector3 SirenApparent(in AcousticPathData path, Vector3 mouth, Vector3 ear)
    {
        // A path with no source recorded is a stand-in (no result yet): nothing to redirect by.
        if (path.SourcePosition == Vector3.Zero || path.ApparentPosition == Vector3.Zero) return mouth;
        if (Vector3.DistanceSquared(path.ApparentPosition, path.SourcePosition) < 1f) return mouth;
        var toApparent = path.ApparentPosition - ear;
        if (toApparent.LengthSquared() < 1e-6f) return mouth;
        return ear + Vector3.Normalize(toApparent) * Vector3.Distance(ear, mouth);
    }

    /// <summary>One mode decision per vehicle, kept between frames: it has state (SirenController).</summary>
    private readonly Dictionary<int, (OpenFPS.Common.SirenController C, double At)> _sirenControl = new();

    /// <summary>
    /// What a patrol car's siren is doing, from what the car does (nothing on the wire carries it): off
    /// standing or with the traffic, wail moving with purpose, yelp braking hard into a junction. The
    /// decision has hysteresis and lives in <see cref="OpenFPS.Common.SirenController"/>, where a test can
    /// drive it; deceleration alone flipped wail and yelp at every corner.
    /// </summary>
    private OpenFPS.Common.SirenMode SirenModeFor(int entityId, float speed, double at)
    {
        if (!_sirenControl.TryGetValue(entityId, out var held))
            held = (new OpenFPS.Common.SirenController(entityId), at);
        float dt = (float)Math.Clamp(at - held.At, 1.0 / 240.0, 0.25);
        var mode = held.C.Update(speed, dt);
        _sirenControl[entityId] = (held.C, at);
        return mode;
    }

    /// <summary>
    /// A car too far away for its own engine, voiced by borrowing a near one: it reads the ring of the
    /// nearest live car of its preset at an offset of its own, placed at its own position with its own
    /// velocity, for a buffer read and an HRTF instead of a tenth of a core. Past a couple of hundred
    /// metres nobody can tell one engine of a kind from another. The per-car offset matters: without it
    /// the borrowed voices are one waveform and sum into one loud car instead of traffic.
    /// </summary>
    private void DistantEngine(EntitySnapshot snap, OpenFPS.Common.Networking.EntityDefinition def, string preset, double sampledAt)
    {
        if (!_distantVoiced.Contains(snap.Id)) return;
        if (!_engineSourceByPreset.TryGetValue(preset, out int sourceId)) return;
        if (sourceId == snap.Id) return;

        int voiceId = DistantVoiceBase - Math.Abs(snap.Id);

        // Rebind when the donor changes: a voice pointed at a dead source is silence that never recovers.
        if (_distantBoundTo.TryGetValue(voiceId, out int bound) && bound != sourceId)
            _audio.StopSound(voiceId);
        _distantBoundTo[voiceId] = sourceId;

        var profile = OpenFPS.Common.MachineRegistry.VehicleFor(preset);
        float level = profile.SourceLevelDb > 0f ? profile.SourceLevelDb : EngineSourceLevelDb;
        float extent = OutletSeparation(preset);
        var (gain, reference) = OpenFPS.Common.Loudness.Place(level, extent);
        Vector3 pos = OpenFPS.Common.AudioEmission.PointFor(snap);

        // A stable, arbitrary offset per car, spread across most of the ring buffer.
        float offset = 0.08f + (Math.Abs(snap.Id) % 17) * 0.045f;

        var e = new SpatialEmitter
        {
            EntityId = voiceId,
            SoundId = "engine-distant",
            IsSynth = true,
            EchoOfEntity = sourceId,
            EchoDelaySeconds = offset,
            EchoGain = 1f,
            EngineKey = "",
            Mode = PlaybackMode.LoopOne,
            Type = EmitterType.WorldLocked,
            Position = pos,
            ApparentPosition = pos,
            // Its own velocity, so it Dopplers as itself, not as its donor.
            Velocity = snap.Velocity,
            PositionSampledAt = sampledAt,
            Volume = gain * def.SoundEmitter.Volume,
            Range = OpenFPS.Common.Loudness.AudibleRange(level),
            MinDistance = reference,
            ExtentMetres = extent,
            Pitch = 1f,
            TargetRegionId = AcousticConstants.GlobalRegionId,
        };
        if (_audio.IsPlaying(voiceId)) _audio.UpdateSpatialAttributes(e);
        else _audio.PlayPhysicalSoundDirect(e);
    }

    private void ProcessAudioEmitter(WorldSnapshot world, EntitySnapshot snap, Vector3 eyePos, float engineDt = 0f)
    {
        double now = _now();
        var def = snap.Definition;

        // A physical model outside the budget (ChooseLiveMachines, this frame) is not worked out either:
        // this bails before the path is read, or the city's 116 unvoiced machines cost a frame's work each.
        if (def.SoundEmitter.IsSynth
            && def.SoundEmitter.SoundId is { } sid
            && PhysicalLevel(sid, out _, out _)
            && !_liveMachines.Contains(snap.Id))
            return;

        // The worker's last answer; until it has one, FirstAnswer. Never "nothing in the way".
        AcousticPathData acousticPath = _acousticWorker.TryGetResult(snap.Id, out var cachedPaths)
            ? cachedPaths.FirstOrDefault(p => !p.IsReflection)
            : FirstAnswer(world, snap, eyePos);

        string resolvedSoundId = "";
        bool interior = false;
        // Sitting in it: the paths into its cabin, and where the ear is in its frame.
        OpenFPS.Client.AudioEngine.Core.Engine.CabinPaths.Layout? cabinLayout = null;
        Vector3 cabinEar = default;
        string engineKey = "";
        string physicalKey = "";
        float powerLever = 1f, rotorWake = 0f;
        bool onGround = false;
        // Where the sound comes out: the same point the occlusion probe in Update asks about.
        Vector3 emitterPosition = OpenFPS.Common.AudioEmission.PointFor(snap);
        float engineVolume = def.SoundEmitter.Volume;
        float engineMinDistance = def.SoundEmitter.MinDistance;
        float engineRange = def.SoundEmitter.Range;
        float engineExtent = def.SoundEmitter.ExtentMetres;
        // A tree or a fire: the places it is heard from across its extent (ExtendedSources).
        Vector3[]? extentLayout = null;
        float chorusTrees = 1f;
        // The declared level, for the ear model; 0 for an authored source with only a volume.
        float earLevel = 0f;
        // An authored source with a size (a fountain, a grille): as for a machine, the reference widens to
        // its radius and the gain is paid down, so only the near field changes.
        if (engineExtent > 0f && !def.SoundEmitter.IsSynth)
            (engineVolume, engineMinDistance) =
                OpenFPS.Common.Loudness.Widen(engineVolume, engineMinDistance, engineExtent);
        if (def.SoundEmitter.IsSynth)
        {
            resolvedSoundId = def.SoundEmitter.SoundId;
            if (string.IsNullOrEmpty(resolvedSoundId)) resolvedSoundId = "SYNTH";
            // The ranking's own lookup, not a second prefix list: with two, the crossing bell won a voice
            // and then fell through here as a nameless synth, placed every frame and rendered by nothing.
            if (PhysicalLevel(resolvedSoundId, out _, out _))
            {
                // Not a vehicle, so no borrowed voice: outside the budget it is not heard (forty air
                // conditioners are not one heard from further away).
                if (!_liveMachines.Contains(snap.Id)) return;
                // The ranking's memoised numbers, so the two cannot disagree.
                if (!PhysicalLevel(resolvedSoundId, out float levelDb, out float extent)) return;
                physicalKey = resolvedSoundId;
                // A stretch of shore carries its geometry (fetch, which way its water lies) in its box
                // (tools/gen_osm.py); the voice reads it from the key.
                if (physicalKey.StartsWith("shore:", StringComparison.OrdinalIgnoreCase))
                    physicalKey = OpenFPS.Common.ShoreSpec.KeyFor(physicalKey, def.Collider.Size, snap.Transform.Rotation);
                // Placed by its declared level and size: inside its extent the level is flat, and the
                // gain is paid down so the far field is unchanged (Loudness.Widen; widening without
                // paying once handed every quiet vehicle eight decibels).
                var (gain, reference) = OpenFPS.Common.Loudness.Place(levelDb, extent);
                // A voice rendered with more headroom than the shared one (a fire's crackles) gets the
                // difference back, so it is placed by its level and not its peaks.
                engineVolume = gain * def.SoundEmitter.Volume
                             * OpenFPS.Client.AudioEngine.Fmod.PhysicalVoiceState.HeadroomGain(PhysicalHeadroom(resolvedSoundId));
                // A tree's share of itself, or a wood's gain (its synth renders its trees: WoodChorus).
                engineVolume *= ChorusShare(snap.Id, out chorusTrees);
                engineMinDistance = reference;
                engineExtent = extent;
                engineRange = MathF.Max(engineRange, OpenFPS.Common.Loudness.AudibleRange(levelDb));
                earLevel = levelDb;
                extentLayout = OpenFPS.Client.AudioEngine.Core.Nature.ExtendedSources.Layout(physicalKey);
                if (physicalKey.StartsWith("aircraft:", StringComparison.OrdinalIgnoreCase))
                {
                    (powerLever, rotorWake) = FlightPower(snap.Velocity);
                    onGround = OnTheWheels(snap, world, eyePos, out float height);
                    // On its wheels the climb angle says nothing: the lever comes from the speed's change.
                    float groundSpeed = snap.Velocity.Length();
                    float accel = 0f;
                    if (_lastAirSpeed.TryGetValue(snap.Id, out var was) && world.PositionsSampledAt > was.At)
                        accel = (groundSpeed - was.Speed) / (float)Math.Max(0.02, world.PositionsSampledAt - was.At);
                    _lastAirSpeed[snap.Id] = (groundSpeed, world.PositionsSampledAt);
                    if (onGround) powerLever = GroundPower(groundSpeed, accel);
                    // A piston aeroplane has no reversers: on the ground it idles unless taking off, and in
                    // the flare (within a wingspan of the ground) its throttle is closed, so its tyres are
                    // heard. At approach power its exhaust stood within 5 dB of a 109 dB touchdown in the
                    // squeal's band; at idle, 10 to 15 dB under. Jets and turboprops keep GroundPower.
                    if (PhysicalAircraft(snap) is { Power: OpenFPS.Common.AircraftPower.Piston } piston)
                    {
                        if (onGround) powerLever = powerLever >= 1f ? 1f : GroundIdleLever;
                        else if (height < piston.WingspanMetres && snap.Velocity.Y < -0.2f) powerLever = GroundIdleLever;
                    }
                }
                else if (physicalKey.StartsWith("rail:", StringComparison.OrdinalIgnoreCase))
                {
                    // The notch from what the speed does (pulling away full, holding a little, braking
                    // none), carried in the lever slot; the wake slot carries the speed, for rolling noise.
                    float speed = snap.Velocity.Length();
                    float accel = 0f;
                    if (_lastRailSpeed.TryGetValue(snap.Id, out var prev) && world.PositionsSampledAt > prev.At)
                        accel = (speed - prev.Speed) / (float)Math.Max(0.02, world.PositionsSampledAt - prev.At);
                    _lastRailSpeed[snap.Id] = (speed, world.PositionsSampledAt);
                    powerLever = accel > 0.08f ? 0.9f : accel < -0.15f ? 0f : speed > 0.5f ? 0.3f : 0f;
                    rotorWake = speed;
                }
            }
            else if (resolvedSoundId.StartsWith("engine:", StringComparison.OrdinalIgnoreCase)
                && OpenFPS.Common.MachineRegistry.Knows(resolvedSoundId[7..]))
            {
                // A vehicle: its own live engine, or a borrowed voice (DistantEngine).
                if (!_liveEngines.Contains(snap.Id))
                {
                    DistantEngine(snap, def, resolvedSoundId[7..], world.PositionsSampledAt);
                    return;
                }
                engineKey = resolvedSoundId[7..];
                var profile = OpenFPS.Common.MachineRegistry.VehicleFor(engineKey);
                // Its own measured level: a stock car is 14 dB over a road car, a diesel pickup 30 under.
                float level = profile.SourceLevelDb > 0f ? profile.SourceLevelDb : EngineSourceLevelDb;
                // Its acoustic size is the distance between the ends it radiates from (a car 3.5 m, a bus
                // 10, a motorcycle 1): inside it the level is flat, since a metre nearer the intake is a
                // metre further from the exhaust. Widened by Loudness.Place, which pays the gain back;
                // a bare MathF.Max(reference, 3f) handed a quiet vehicle up to 8 dB.
                float extent = OutletSeparation(engineKey);
                var (gain, reference) = OpenFPS.Common.Loudness.Place(level, extent);
                engineVolume = gain * def.SoundEmitter.Volume;
                engineMinDistance = reference;
                engineExtent = extent;
                engineRange = MathF.Max(engineRange, OpenFPS.Common.Loudness.AudibleRange(level));
                earLevel = level;

                // Close enough to tell the ends apart: this voice moves to the tailpipe and the front gets
                // its own (FrontVoice); further off it sits between them, biased to the exhaust
                // (VehicleProfile.ExhaustEmitterBias). Two voices are each placed as points: widening them
                // too counted the length twice (a hatchback's tailpipe 4 dB short at 2 m, 9 dB at 1 m).
                // Beyond the extent both placements agree (Widen holds gain times reference).
                if (_frontVoiced.Contains(snap.Id))
                {
                    emitterPosition = snap.Transform.Position
                                    + Vector3.Transform(ExhaustSlot(profile), snap.Transform.Rotation);
                    (gain, reference) = OpenFPS.Common.Loudness.Place(level);
                    engineVolume = gain * def.SoundEmitter.Volume;
                    engineMinDistance = reference;
                    engineExtent = 0f;
                }

                // Sitting in it, the machine arrives through the floor and firewall and the voice renders
                // what gets through the body (EngineVoiceState.Interior). Its level is already pressure at
                // the ear: unwidened, at the gain its reference distance would have had.
                if (snap.Id == _state.RidingEntityId)
                {
                    interior = true;
                    var (g0, r0) = OpenFPS.Common.Loudness.Place(level);
                    engineVolume = MathF.Min(1f, g0 * r0) * def.SoundEmitter.Volume;
                    engineMinDistance = 1f;
                    engineExtent = 0f;
                    // With a cabin (CabinPaths) this voice is the bulkhead; every other path gets its own.
                    cabinLayout = OpenFPS.Client.AudioEngine.Core.Engine.CabinPaths.For(profile);
                    cabinEar = Vector3.Transform(eyePos - snap.Transform.Position, Quaternion.Inverse(snap.Transform.Rotation));
                }
            }
        }
        else
        {
            resolvedSoundId = _sounds.ResolvePath(def.SoundEmitter.SoundId);
            if (string.IsNullOrEmpty(resolvedSoundId)) return;

            // A granular emitter needs its sample decoded into memory.
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
            // Spin-up and spin-down sounds, which VoiceManager plays.
            StartSoundId = def.SoundEmitter.StartSoundId ?? "",
            StopSoundId = def.SoundEmitter.StopSoundId ?? "",
            Mode = def.SoundEmitter.Mode,
            Position = emitterPosition,
            ApparentPosition = engineKey.Length > 0 || physicalKey.Length > 0
                             ? emitterPosition : acousticPath.ApparentPosition,
            // Inside, the voice has already rendered what gets through the body: the path would count
            // the panels twice.
            EffectiveDistance = interior ? 0.7f : acousticPath.EffectiveDistance,
            Occlusion = interior ? 0f : acousticPath.Occlusion,
            EqLow = interior ? 1f : acousticPath.EqLow, EqMid = interior ? 1f : acousticPath.EqMid, EqHigh = interior ? 1f : acousticPath.EqHigh,
            AirLowDb = interior ? 0f : acousticPath.AirLowDb, AirMidDb = interior ? 0f : acousticPath.AirMidDb, AirHighDb = interior ? 0f : acousticPath.AirHighDb,
            ApertureFactor = interior ? 1f : acousticPath.ApertureFactor,
            TransmissionBleed = interior ? 0f : acousticPath.TransmissionBleed,
            Velocity = snap.Velocity,
            PositionSampledAt = world.PositionsSampledAt,
            // Its local aim turned by the entity; zero means straight ahead.
            Direction = Vector3.Transform(
                def.SoundEmitter.Direction.LengthSquared() > 0f
                    ? Vector3.Normalize(def.SoundEmitter.Direction)
                    : Vector3.UnitZ,
                snap.Transform.Rotation),
            Volume = engineVolume,
            EarLevelDb = earLevel,
            Range = Math.Max(1.0f, engineRange),
            Pitch = 1.0f,
            Type = EmitterType.EntityAttached,
            IsReflection = false,
            // Inside, its room is yours (the cabin), whatever room the car's middle is in.
            TargetRegionId = interior ? _listenerRegion : acousticPath.RegionId,
            ConeInside = def.SoundEmitter.ConeInsideAngle,
            ConeOutside = def.SoundEmitter.ConeOutsideAngle,
            ConeOutsideVolume = def.SoundEmitter.ConeOutsideVolume,
            MinDistance = engineMinDistance,
            ExtentMetres = engineExtent,
            EngineKey = engineKey,
            Interior = interior,
            // Inside, it rides with your head, ahead and below (firewall and floor, or the bulkhead with
            // a cabin), turned with the car so the engine stays in front through a corner.
            FollowsListener = interior,
            ListenerOffset = !interior ? Vector3.Zero
                           : cabinLayout != null ? Vector3.Transform(OpenFPS.Client.AudioEngine.Core.Engine.CabinPaths.Offset(cabinLayout, 0, cabinEar), snap.Transform.Rotation)
                           : Vector3.Transform(new Vector3(0f, -0.4f, 0.6f), snap.Transform.Rotation),
            CabinEarX = cabinEar.X,
            PhysicalKey = physicalKey,
            Trees = chorusTrees,
            PowerLever = powerLever,
            RotorWake = rotorWake,
            OnGround = onGround,
            EngineSpeed = snap.Velocity.Length(),
            // Whether a synthesised source is sounding. Nearly everything decides that from what the
            // client sees; a crossing's bell rings for a train the listener may be a kilometre from, so
            // it comes down the wire (true for everything else).
            EngineRunning = def.SoundEmitter.SynthRunning,
            ServingStop = def.SoundEmitter.ServingStop,
            WindowsOpen = _cabins.WindowsOpen(snap, _now()),
            // From the server, which knows the corner's banking. Differentiated here from the velocity
            // against flat-ground grip, the speedway's banked corners read 1.43 to 1.59 against a
            // full-slide threshold of 1.45: every car rendered a broadband skid in every corner.
            TyreSlip = snap.TyreDemand,
            Wheels = snap.Wheels,
            WheelStrikes = engineKey.Length > 0 ? RailStrikes(snap, engineKey, world.PositionsSampledAt) : null,
            RoadWaterMm = _roadWaterMm,

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

        // A repeating one-shot (RepeatIntervalSeconds: a PA, a foghorn, a station bell) is built as
        // normal and handed over only when its interval comes round.
        float repeat = def.SoundEmitter.RepeatIntervalSeconds;
        if (repeat > 0f)
        {
            double due = _repeatDue.GetValueOrDefault(snap.Id, double.NegativeInfinity);
            if (double.IsNegativeInfinity(due))
            {
                // Staggered by id, so two announcers on one map do not talk over each other for ever.
                _repeatDue[snap.Id] = now + (Math.Abs(snap.Id) % 7) * 0.9;
                return;
            }
            if (now < due) return;
            _repeatDue[snap.Id] = now + repeat;
            if (_audio.IsPlaying(snap.Id)) return;    // still saying the last one
        }

        // The road under a machine hands its sound back a moment later; see GroundReflection. Engines and
        // physical models only: a sustained recording (a PA's speech) gets none, as speech flanged with
        // one (docs/CLIENT_NOTES.md, "Speech has no ground reflection").
        long groundAt = System.Diagnostics.Stopwatch.GetTimestamp();
        if (engineKey.Length > 0 || physicalKey.Length > 0) ApplyGround(ref emitter, world);
        _partMs[1] += Ms(groundAt);
        Spreading? spreading = extentLayout != null ? SpreadOf(snap, extentLayout, ref emitter, eyePos, engineDt, now) : null;
        _placed[snap.Id] = (emitter.Volume, emitter.MinDistance);
        _audio.Submit(emitter);
        // ...and its other places, after its middle, so the synth they read already exists.
        if (spreading != null) PlaceOuter(snap.Id, spreading, emitter, world, now);

        // The front outlet, after the machine's own voice so a just-built engine exists for the tap.
        if (engineKey.Length > 0 && _frontVoiced.Contains(snap.Id))
            FrontVoice(snap, OpenFPS.Common.MachineRegistry.VehicleFor(engineKey), acousticPath,
                       engineVolume, engineMinDistance, Math.Max(1.0f, engineRange), world.PositionsSampledAt);

        if (interior && cabinLayout != null && engineKey.Length > 0)
            CabinVoices(snap, cabinLayout, cabinEar, emitter, eyePos);

        // The siren is placed in UpdateSirens, not here.

        // The walls answering this engine, read back out of its own ring at each mirrored path's delay
        // (EngineReflections).
        if (engineKey.Length > 0)
        {
            long echoAt = System.Diagnostics.Stopwatch.GetTimestamp();
            _engineEchoes.Update(snap.Id, emitter, acousticPath, eyePos, AudioPhysics.CurrentSpeedOfSound, engineDt, _audio,
                                 traced: OpenFPS.Client.AudioEngine.Fmod.FmodAudioProvider.HasTracedEchoes(snap.Id));
            double echoMs = Ms(echoAt);
            _partMs[0] += echoMs;
            _echoPassMs += echoMs;
        }

        // No floor slapback or "cone reflection" here, on purpose: a copy started at a ray hit is another
        // read of the file (a looping announcement, again), and a ray from inside the emitter's own box
        // hits it at distance zero, heard as the sound repeating softer in place. The image-source pass
        // already mirrors the source through the floor and the wall.
    }

    /// <summary>
    /// A 20 ms packet of somebody talking, into their stream (TalkerStream), which a voice at their mouth
    /// reads (UpdateTalkers).
    /// </summary>
    public void ReceiveVoice(int senderId, ushort sequence, byte[] opusData)
        => OpenFPS.Client.AudioEngine.Fmod.Talkers.For(senderId).Receive(sequence, opusData, OpenFPS.Common.AudioClock.Now);

    internal const int TalkerVoiceBase = -1_400_000;
    /// <summary>Where a seated person's mouth is above the floor under their feet, metres: a tenth below
    /// a seated listener's eye (LocalPlayerState.EyeHeight).</summary>
    internal const float SeatedMouthHeight = 0.9f;
    /// <summary>How long a talker's voice is kept after their last packet: the jitter buffer's longest
    /// margin and its fade, so the end of what they said is played out.</summary>
    private const double TalkerHoldSeconds = 1.0;
    /// <summary>How long a stream nobody has talked into is kept, decoder and all.</summary>
    private const double TalkerForgetSeconds = 120;
    private readonly HashSet<int> _talkersVoiced = new();
    private readonly List<int> _talkersGone = new();
    private double _talkersNextLog;

    /// <summary>
    /// Everybody talking on voice chat, from their own mouth: facing their way, through what is between
    /// you, into their room. Their usual microphone level is taken as normal conversation
    /// (OwnVoiceRing.SpeechRmsDbfs), so a quiet microphone is not a quiet person and a shout is still
    /// louder. No ground reflection: a voice's ground copy flanged, as the NPC speech's did.
    /// </summary>
    private void UpdateTalkers(WorldSnapshot world, Vector3 eyePos, double now)
    {
        _talkersGone.Clear();
        _talkerSearchedThisFrame = false;
        foreach (var stream in OpenFPS.Client.AudioEngine.Fmod.Talkers.All)
        {
            stream.Pump(now);
            int id = stream.SenderId;
            int voiceId = TalkerVoiceBase - Math.Abs(id);
            double quiet = now - stream.LastArrival;
            if (quiet > TalkerForgetSeconds) { _talkersGone.Add(id); continue; }
            bool talking = quiet < TalkerHoldSeconds && id != OwnEntityId;
            if (!talking || !world.Entities.TryGetValue(id, out var snap))
            {
                if (_talkersVoiced.Remove(id)) _audio.StopSound(voiceId);
                StopTalkerRoom(id);
                continue;
            }
            var facing = Vector3.Transform(Vector3.UnitZ, snap.Transform.Rotation);
            facing.Y = 0f;
            facing = facing.LengthSquared() > 1e-6f ? Vector3.Normalize(facing) : Vector3.UnitZ;
            // Seated in a vehicle: a seated mouth, heard outside through the glass or open windows.
            bool seated = OpenFPS.Client.AudioEngine.Acoustics.CabinWalls.TryFind(world, snap.Transform.Position,
                                                                                out var cabin, out var cabinVehicle);
            float mouthHeight = seated ? SeatedMouthHeight : OpenFPS.Common.Speech.MouthHeight;
            Vector3 mouth = snap.Transform.Position + new Vector3(0f, mouthHeight, 0f) + facing * 0.1f;

            float levelDb = OpenFPS.Common.Speech.NormalDb - stream.Ring.SpeechRmsDbfs;
            var (gain, reference) = OpenFPS.Common.Loudness.Place(levelDb);

            _acousticWorker.EnqueueRequest(new AcousticRequest
            {
                EntityId = id, ListenerPos = eyePos, SourcePos = mouth, SourceRadius = 0.15f,
            });
            AcousticPathData path = default;
            bool found = false;
            if (_acousticWorker.TryGetResult(id, out var paths))
                foreach (var p in paths)
                    if (!p.IsReflection) { path = p; found = true; break; }
            if (!found) path = _acoustics.CalculateAcousticPath(world, id, eyePos, mouth);

            var e = new SpatialEmitter
            {
                EntityId = voiceId,
                SoundId = "voice chat",
                IsSynth = true,
                PhysicalKey = OpenFPS.Client.AudioEngine.Fmod.Talkers.Key(id),
                EngineKey = "",
                Mode = PlaybackMode.LoopOne,
                Type = EmitterType.EntityAttached,
                Position = mouth,
                ApparentPosition = path.ApparentPosition,
                Velocity = snap.Velocity,
                PositionSampledAt = world.PositionsSampledAt,
                Direction = facing,
                Volume = gain,
                EarLevelDb = levelDb,
                MinDistance = reference,
                Range = MathF.Max(reference, OpenFPS.Common.Loudness.AudibleRange(levelDb)),
                Pitch = 1f,
                EngineRunning = true,
                Occlusion = path.Occlusion,
                EqLow = path.EqLow, EqMid = path.EqMid, EqHigh = path.EqHigh,
                AirLowDb = path.AirLowDb, AirMidDb = path.AirMidDb, AirHighDb = path.AirHighDb,
                ApertureFactor = path.ApertureFactor,
                TransmissionBleed = path.TransmissionBleed,
                EffectiveDistance = path.EffectiveDistance,
                TargetRegionId = path.RegionId,
                // Worked out afresh every frame, so the path and the cabin round them follow them.
                CarriesPath = true,
            };
            if (seated)
            {
                // In the car you are in, nothing is between you; in any other, its walls are.
                if (cabin.Id == _state.RidingEntityId) e.InsideListenersVehicle = true;
                else
                {
                    var (low, mid, high) = OpenFPS.Client.AudioEngine.Acoustics.CabinWalls.LossDb(
                        cabinVehicle, _cabins.WindowsOpen(cabin, now));
                    e.EqLow *= OpenFPS.Client.AudioEngine.Acoustics.CabinWalls.Gain(low);
                    e.EqMid *= OpenFPS.Client.AudioEngine.Acoustics.CabinWalls.Gain(mid);
                    e.EqHigh *= OpenFPS.Client.AudioEngine.Acoustics.CabinWalls.Gain(high);
                }
            }
            if (_talkersVoiced.Contains(id) && _audio.IsPlaying(voiceId)) _audio.UpdateSpatialAttributes(e);
            else { _audio.PlayPhysicalSoundDirect(e); _talkersVoiced.Add(id); }

            // The surfaces round them answering; not from inside a cabin, where only its glass hears them.
            if (seated) StopTalkerRoom(id);
            else AnswerTalker(world, id, mouth, eyePos, gain, reference, e.Range, path.RegionId, now);
        }
        foreach (int id in _talkersGone)
        {
            if (_talkersVoiced.Remove(id)) _audio.StopSound(TalkerVoiceBase - Math.Abs(id));
            StopTalkerRoom(id);
            OpenFPS.Client.AudioEngine.Fmod.Talkers.Remove(id);
        }
        if (_talkersVoiced.Count > 0 && now >= _talkersNextLog)
        {
            _talkersNextLog = now + 5;
            foreach (int id in _talkersVoiced)
                if (OpenFPS.Client.AudioEngine.Fmod.Talkers.TryGet(id, out var t))
                    Log.Information("Voice chat: e{Id} talking at {Db:F0} dBFS, buffer {Margin:F0} ms, " +
                                    "{Received} frames in, {Lost} lost ({Rebuilt} rebuilt), {Late} late, {Corrupt} bad, ran dry {Dry} time(s); " +
                                    "room {Region}, {Copies} surface(s) answering",
                                    id, t.Ring.SpeechRmsDbfs, t.Ring.MarginSeconds * 1000, t.Received, t.Lost, t.Rebuilt,
                                    t.Late, t.Corrupt, t.RanDry,
                                    _talkerRooms.TryGetValue(id, out var tr) ? tr.Region : AcousticConstants.GlobalRegionId,
                                    _talkerRooms.TryGetValue(id, out tr) ? tr.Answering : 0);
        }
    }

    // ── The room answering somebody talking ─────────────────────────────────────────────────

    /// <summary>
    /// The first voice id of the copies of other players' voices: a block of <see cref="OwnVoiceCopies"/>
    /// ids per talker from here down, below -5000 so the acoustic-path pass never traces one from its image.
    /// </summary>
    internal const int TalkerCopyBase = -1_700_000;
    /// <summary>How many talkers the surfaces answer at once. A search is a few milliseconds on the game
    /// thread; past this many voices at once, the room is the tail alone.</summary>
    private const int MaxTalkerRooms = 8;

    private sealed class TalkerRoom
    {
        public int Block;
        public double NextSearch;
        public int Answering;
        public int Region = AcousticConstants.GlobalRegionId;
        public readonly bool[] On = new bool[OwnVoiceCopies];
        public readonly List<OpenFPS.Common.EarlyReflections.Arrival> Arrivals = new();
        public int FirstId => TalkerCopyBase - Block * OwnVoiceCopies;
    }
    private readonly Dictionary<int, TalkerRoom> _talkerRooms = new();
    private bool _talkerSearchedThisFrame;

    /// <summary>
    /// The surfaces round somebody talking, answering at your ear as your own room answers you
    /// (PlaceVoiceCopies): searched from their mouth a few times a second, at most one search a frame
    /// among the talkers. Without it a voice was direct sound and late tail only (Cody, 2026-10-04: "I can
    /// hear him but it's dry, his reflections don't follow him").
    /// </summary>
    private void AnswerTalker(WorldSnapshot world, int id, Vector3 mouth, Vector3 ear, float gain, float reference,
                              float range, int region, double now)
    {
        if (!_talkerRooms.TryGetValue(id, out var room))
        {
            int block = -1;
            for (int b = 0; b < MaxTalkerRooms && block < 0; b++)
            {
                block = b;
                foreach (var other in _talkerRooms.Values) if (other.Block == b) { block = -1; break; }
            }
            if (block < 0) return;
            room = new TalkerRoom { Block = block };
            _talkerRooms[id] = room;
        }
        room.Region = region;
        if (now < room.NextSearch || _talkerSearchedThisFrame) return;
        _talkerSearchedThisFrame = true;
        room.NextSearch = now + 0.1;
        // Their voice is played without its flight time, so a copy is its EXTRA path behind it.
        float flight = Vector3.Distance(mouth, ear) / AudioPhysics.CurrentSpeedOfSound;
        room.Answering = PlaceVoiceCopies(world, mouth, ear, room.Arrivals, gain, reference, flight,
                                          room.FirstId, room.On,
                                          OpenFPS.Client.AudioEngine.Fmod.Talkers.CopyKey(id), "voice chat", region, range);
    }

    private void StopTalkerRoom(int id)
    {
        if (!_talkerRooms.Remove(id, out var room)) return;
        StopVoiceCopies(room.FirstId, room.On);
    }

    private int _footstepPoolIndex = 0;
    // Reusing a playing voice's id hard-cuts it (a click); 12 ids is headroom at a running cadence.
    private const int FOOTSTEP_POOL_SIZE = 12;
    /// <summary>
    /// The room the listener was last found in, for the sounds the body makes. A footstep is built on
    /// the game thread between updates, and left at TargetRegionId's default (-1, outdoors) every footfall
    /// reverberated on the outdoor bus, the same tail wherever you were.
    /// </summary>
    private int _listenerRegion = AcousticConstants.GlobalRegionId;

    private const int FOOTSTEP_BASE_ID = -100;

    /// <summary>
    /// Everybody else's steps have a pool apart from yours: a submission replaces whatever waits under its
    /// id, and in one pool three hundred walkers came round it faster than your own steps could start.
    /// </summary>
    private const int OTHERS_FOOTSTEP_BASE_ID = -300;
    private const int OTHERS_FOOTSTEP_POOL_SIZE = 64;
    private int _othersFootstepIndex;

    /// <summary>How far away another body's step can be heard at all, metres: a footstep's range.</summary>
    private const float FootstepRange = 15f;

    /// <summary>Somebody else's step: a sound left where it fell, and only one close enough to hear is
    /// made at all.</summary>
    public void OnPlayerFootstep(Vector3 pos, string mat, string var, StepSlope slope = StepSlope.Level)
        => OnPlayerFootstep(pos, mat, var, slope, bodyId: -1);

    /// <summary>...with <paramref name="bodyId"/>, whose foot it was: their own body is not a wall between
    /// their foot and you (CarryThePath).</summary>
    public void OnPlayerFootstep(Vector3 pos, string mat, string var, StepSlope slope, int bodyId)
    {
        if (Vector3.Distance(pos, _state.VisualPosition) > FootstepRange) return;
        SubmitFootstep(pos + new Vector3(0, 0.1f, 0), mat, follows: false, offset: Vector3.Zero, boostDb: 0f, slope: slope,
                       bodyId: bodyId);
    }

    /// <summary>
    /// How much louder your own footstep is to you than to a bystander where your ears are: you also hear
    /// it through your skeleton, the heel strike up the leg and spine into the skull. Your own body only.
    /// </summary>
    private const float OwnFootstepBoneConductionDb = 8f;

    /// <summary>OPENFPS_AUDIO_DEBUG=1 logs what your own feet did and when: a landing is heavier than a
    /// step, so this answers "periodic bangs" outright.</summary>
    private static readonly bool _footTrace = Environment.GetEnvironmentVariable("OPENFPS_AUDIO_DEBUG") == "1";

    // ── The listening-level calibration's voice (ListeningCalibration) ────────────────────────
    //
    // A person one step in front at a gain the calibration chooses: placed directly, not by the loudness
    // law, and with no ear stage, to play at exactly a real voice's level and tone there. Two ids in turn,
    // so a new saying never waits for the old one's voice to be released.
    private const int ReferenceVoiceBase = -7_900_000;
    private int _referenceVoice;

    public void PlayReferenceVoice(string soundId, float gainDb)
    {
        StopReferenceVoice();
        _referenceVoice = (_referenceVoice + 1) & 1;
        var forward = Vector3.Transform(Vector3.UnitZ, Quaternion.CreateFromYawPitchRoll(_state.Yaw, 0f, 0f));
        Vector3 ear = _state.VisualPosition + new Vector3(0f, _state.EyeHeight, 0f);
        Vector3 mouth = ear + forward * 1f - new Vector3(0f, _state.EyeHeight - OpenFPS.Common.Speech.MouthHeight, 0f);
        _audio.Submit(new SpatialEmitter
        {
            EntityId = ReferenceVoiceBase - _referenceVoice,
            SoundId = soundId,
            Mode = PlaybackMode.Single,
            Type = EmitterType.WorldLocked,
            Position = mouth,
            ApparentPosition = mouth,
            Direction = -forward,
            Volume = MathF.Pow(10f, gainDb / 20f),
            // Flat out to the speaker, so the gain is the level at the ear.
            MinDistance = 1f,
            Range = 20f,
            Pitch = 1f,
            Essential = true,
            IsEvent = true,
            EqLow = 1f, EqMid = 1f, EqHigh = 1f,
            ApertureFactor = 1f,
            CarriesPath = true,
            TargetRegionId = _listenerRegion,
        });
    }

    public void StopReferenceVoice()
    {
        _audio.StopSoundImmediate(ReferenceVoiceBase);
        _audio.StopSoundImmediate(ReferenceVoiceBase - 1);
    }

    /// <summary>
    /// Your own step, which rides with you at a fixed offset from your head. Placed world-locked at the
    /// physics position (the listener being at the smoothed VisualPosition), a step's bearing swung from
    /// straight down to thirty degrees behind while it played, and the steps trailed and slid round you.
    /// </summary>
    public void OnOwnFootstep(Vector3 pos, string mat, string var, StepSlope slope = StepSlope.Level)
    {
        if (_footTrace) Log.Information("[FOOT] step {Slope} on {Mat} at {Pos}", slope, mat, pos);
        Vector3 offset = (pos - _state.Position) + new Vector3(0, 0.1f - _state.EyeHeight, 0);   // the foot, from the eye
        SubmitFootstep(_state.VisualPosition + new Vector3(0, _state.EyeHeight, 0) + offset, mat, follows: true, offset: offset,
                       boostDb: OwnFootstepBoneConductionDb, slope: slope);
    }

    /// <summary>How far a footstep take's pitch and level wander from one play to the next.</summary>
    private const float FootstepPitchJitter = 0.03f, FootstepLevelJitterDb = 1f;


    private void SubmitFootstep(Vector3 nudgePos, string mat, bool follows, Vector3 offset, float boostDb,
                                StepSlope slope = StepSlope.Level, int bodyId = -1)
    {
        // Your own feet ride with you (follows); anybody else's stay where they fell.
        bool own = follows;
        int id = own ? FOOTSTEP_BASE_ID - (_footstepPoolIndex++ % FOOTSTEP_POOL_SIZE)
                     : OTHERS_FOOTSTEP_BASE_ID - (_othersFootstepIndex++ % OTHERS_FOOTSTEP_POOL_SIZE);

        string resolvedSoundId = _sounds.ResolvePath(_sounds.GetImpactSoundId(mat, 0f));
        if (string.IsNullOrEmpty(resolvedSoundId)) return;

        // Placed by Loudness.Place like every other sound. At full scale (a 112 dB source a metre away;
        // a step is about 55) a dry step peaked at -1.6 dBFS with no makeup (--room-walk), hit the limiter
        // by 9 dB with it and pumped the mix: "the footsteps are loud", a pop on each. On stairs a toe going
        // up is lighter and a heel going down heavier (StrideAccumulator.SlopeDb).
        float slopeDb = StrideAccumulator.SlopeDb(slope), slopePitch = StrideAccumulator.SlopePitch(slope);
        var (stepGain, stepReference) = OpenFPS.Common.Loudness.Place(OpenFPS.Common.Loudness.FootstepDb + boostDb + slopeDb);

        var footstep = new SpatialEmitter
        {
            EntityId = id,
            SoundId = resolvedSoundId,
            Position = nudgePos,
            FollowsListener = follows,
            ListenerOffset = offset,
            Type = EmitterType.WorldLocked,
            // No two steps alike: a hair of pitch and a touch of level, at random.
            Volume = stepGain * MathF.Pow(10f, (float)(_stepRandom.NextDouble() * 2.0 - 1.0) * FootstepLevelJitterDb / 20f),
            Pitch = slopePitch * (1f + (float)(_stepRandom.NextDouble() * 2.0 - 1.0) * FootstepPitchJitter),
            Range = FootstepRange,
            // Your own feet are pinned (they are how you know you are moving); everybody else's compete
            // by loudness like any other sound.
            Essential = own,
            IsEvent = true,
            MinDistance = stepReference,
            EarLevelDb = OpenFPS.Common.Loudness.FootstepDb + boostDb + slopeDb,
            TargetRegionId = _listenerRegion,
        };
        if (!own) CarryThePath(ref footstep, nudgePos, bodyId);
        _audio.Submit(footstep);

        // The walls answering YOUR footfalls. Other people's steps have none: their pool is yours.
        if (own) SubmitStepReflections(nudgePos, resolvedSoundId, stepGain, stepReference, OpenFPS.Common.Loudness.FootstepDb + boostDb + slopeDb);
    }

    /// <summary>
    /// Somebody else's step starts with the wall between you already on it: a step is over before the
    /// worker's answer arrives, so it played its attack through the brick ("I still hear people walking
    /// outside through the wall", 2026-09-29). It takes the worker's answer for the nearest source heard a
    /// moment ago, moved here, or the tracer's, and reverberates in the room the foot is in. The tracer
    /// leaves the walker's own solid body out: the foot is inside it, and every other player's step came
    /// out 8 dB down at occlusion 0.6 (Cody and Sean, 2026-10-05: "I cannot hear his footsteps while he's
    /// walking, only his beacon").
    /// </summary>
    private void CarryThePath(ref SpatialEmitter step, Vector3 at, int bodyId = -1)
    {
        if (_groundWorld is not { } world) return;
        Vector3 ear = _state.VisualPosition + new Vector3(0, _state.EyeHeight, 0);
        var path = _acoustics.CalculateAcousticPath(world, bodyId >= 0 ? bodyId : step.EntityId, ear, at);
        if (_acousticWorker.TryGetNearby(ear, at, out var near))
        {
            Vector3 moved = at - near.SourcePosition;
            float nearDist = MathF.Max(0.1f, Vector3.Distance(ear, near.SourcePosition));
            float dist = Vector3.Distance(ear, at);
            path = path with
            {
                Occlusion = near.Occlusion, EqLow = near.EqLow, EqMid = near.EqMid, EqHigh = near.EqHigh,
                TransmissionBleed = near.TransmissionBleed, ApertureFactor = near.ApertureFactor,
                ApparentPosition = near.ApparentPosition + moved,
                EffectiveDistance = near.EffectiveDistance * dist / nearDist,
            };
        }
        step.Occlusion = path.Occlusion;
        step.EqLow = path.EqLow; step.EqMid = path.EqMid; step.EqHigh = path.EqHigh;
        step.AirLowDb = path.AirLowDb; step.AirMidDb = path.AirMidDb; step.AirHighDb = path.AirHighDb;
        step.ApertureFactor = path.ApertureFactor;
        step.TransmissionBleed = path.TransmissionBleed;
        step.ApparentPosition = path.ApparentPosition;
        step.EffectiveDistance = path.EffectiveDistance;
        if (path.RegionId >= 0) step.TargetRegionId = path.RegionId;
        step.CarriesPath = true;
    }

    /// <summary>
    /// Voices for the surfaces answering your own footfalls: their own pool, so a copy never takes its
    /// step's slot, and below -5000, where the acoustic-path pass leaves them alone. Each carries its whole
    /// path from its image. Asked about from the image, the worker traced it through its own wall (-40 to
    /// -100 dB) or round by the stairwell (Cody, 2026-10-04, Marlow Tower: "it's like they're left on the
    /// first floor").
    /// </summary>
    internal const int STEP_ECHO_BASE_ID = -1_800_000;
    private const int STEP_ECHO_POOL_SIZE = 48;
    private readonly List<OpenFPS.Common.EarlyReflections.Arrival> _stepArrivals = new();
    private int _stepEchoIndex;

    // ── Your own voice, as your room answers it ─────────────────────────────────────────────────

    /// <summary>True while the microphone is open (V). The session sets it.</summary>
    public bool OwnVoiceLive { get; set; }

    internal const int OwnVoiceBase = -1_300_000;
    /// <summary>How many surfaces answer your voice at once: the room's first answers, as for a step.</summary>
    private const int OwnVoiceCopies = 8;
    /// <summary>
    /// What the microphone path already adds before a copy can start (capture buffer, 20 ms frame, ring
    /// margin): about 60 ms. A copy's delay is its extra path less this, so a surface over about ten metres
    /// of path away answers on time and a nearer one as soon as it can (Cody, 2026-10-03: "I want to hear
    /// myself in the room I'm actually in").
    /// </summary>
    private const float MicrophoneLatency = 0.06f;
    private readonly List<OpenFPS.Common.EarlyReflections.Arrival> _voiceArrivals = new();
    private readonly bool[] _voiceCopyOn = new bool[OwnVoiceCopies];
    private bool _voiceRoomOn;
    private double _voiceNextSearch, _voiceNextLog;

    /// <summary>
    /// Your own voice while the microphone is open, into your room and never dry: the surfaces answering
    /// from their own directions (found as your footsteps' are) and the reverberation fed from you. Your
    /// talking level on the microphone is normal conversation (OwnVoiceRing.SpeechRmsDbfs), so a shout
    /// fills the room more.
    /// </summary>
    private void UpdateOwnVoice(Vector3 ear, double now)
    {
        if (!OwnVoiceLive || _groundWorld is not { } world)
        {
            StopOwnVoice();
            return;
        }
        var facing = Vector3.Transform(Vector3.UnitZ, Quaternion.CreateFromYawPitchRoll(_state.Yaw, 0f, 0f));
        Vector3 mouth = ear + facing * 0.08f - new Vector3(0f, 0.1f, 0f);
        // Full scale sits as far above normal talking as the microphone's talking level sits below it.
        float levelDb = OpenFPS.Common.Speech.NormalDb - OpenFPS.Client.AudioEngine.Fmod.OwnVoiceRing.Shared.SpeechRmsDbfs;
        var (gain, reference) = OpenFPS.Common.Loudness.Place(levelDb);

        // The room's reverberation; its own sound is silent (FmodAudioProvider._ownVoiceRoomGroup). At its
        // reference distance, where the send to the room is a source of that level's.
        var room = new SpatialEmitter
        {
            EntityId = OwnVoiceBase,
            SoundId = "own voice",
            IsSynth = true,
            PhysicalKey = OpenFPS.Client.AudioEngine.Fmod.FmodAudioProvider.OwnVoiceRoomKey,
            EngineKey = "",
            Mode = PlaybackMode.LoopOne,
            Type = EmitterType.WorldLocked,
            Position = ear + facing * reference,
            ApparentPosition = ear + facing * reference,
            Volume = gain,
            MinDistance = reference,
            Range = 30f,
            Pitch = 1f,
            EngineRunning = true,
            CarriesPath = true,
            Occlusion = 0f, ApertureFactor = 1f, TransmissionBleed = 0f,
            EqLow = 1f, EqMid = 1f, EqHigh = 1f,
            EchoDelaySeconds = 0f,
            TargetRegionId = _listenerRegion,
        };
        if (_voiceRoomOn && _audio.IsPlaying(OwnVoiceBase)) _audio.UpdateSpatialAttributes(room);
        else { _audio.PlayPhysicalSoundDirect(room); _voiceRoomOn = true; }

        // The surfaces, a few times a second: the room only changes as you move.
        if (now < _voiceNextSearch) return;
        _voiceNextSearch = now + 0.1;
        float c = AudioPhysics.CurrentSpeedOfSound;
        // Its whole path, less what the microphone has already cost.
        int slot = PlaceVoiceCopies(world, mouth, ear, _voiceArrivals, gain, reference, MicrophoneLatency,
                                    OwnVoiceBase - 1, _voiceCopyOn,
                                    OpenFPS.Client.AudioEngine.Fmod.FmodAudioProvider.OwnVoiceCopyKey, "own voice",
                                    _listenerRegion);
        if (now >= _voiceNextLog)
        {
            _voiceNextLog = now + 5;
            var ring = OpenFPS.Client.AudioEngine.Fmod.OwnVoiceRing.Shared;
            Serilog.Log.Information("Own voice: talking at {Db:F0} dBFS on the microphone ({Samples} samples in), room {Region}, " +
                                    "{Copies} surface(s) answering, nearest {Near:F0} ms of path",
                                    ring.SpeechRmsDbfs, ring.Written, _listenerRegion, slot,
                                    _voiceArrivals.Count > 0 ? _voiceArrivals.Min(a => a.PathLength) / c * 1000 : 0);
        }
    }

    /// <summary>
    /// The surfaces round a talking mouth answering it at an ear, for your own voice and anybody else's:
    /// image sources to second order inside the window before the tail, the loudest
    /// <see cref="OwnVoiceCopies"/> placed and coloured as a footstep's copies are (SubmitRoomStepEchoes),
    /// each the voice's ring read back at its own path. Returns how many are answering.
    /// </summary>
    /// <param name="world">The world to search.</param>
    /// <param name="mouth">Where the voice comes from.</param>
    /// <param name="ear">The listener's ear.</param>
    /// <param name="arrivals">Scratch for the search, refilled.</param>
    /// <param name="gain">The voice's placed gain.</param>
    /// <param name="reference">The voice's reference distance.</param>
    /// <param name="alreadyLateSeconds">What the voice's own playback has already cost the copies: the
    /// microphone's latency for yours; for somebody else, the direct voice's flight time, which it is
    /// played without, so each copy comes its extra path behind it.</param>
    /// <param name="firstId">The first copy's voice id; the rest count down from it.</param>
    /// <param name="on">Which slots have a voice, kept between calls.</param>
    /// <param name="key">The physical key the copies read the voice's ring by.</param>
    /// <param name="soundId">The copies' sound id, for the logs.</param>
    /// <param name="regionId">The room the copies reverberate in.</param>
    /// <param name="range">How far the copies are heard.</param>
    private int PlaceVoiceCopies(WorldSnapshot world, Vector3 mouth, Vector3 ear,
                                 List<OpenFPS.Common.EarlyReflections.Arrival> arrivals,
                                 float gain, float reference, float alreadyLateSeconds,
                                 int firstId, bool[] on, string key, string soundId, int regionId, float range = 30f)
    {
        arrivals.Clear();
        _acoustics.FindReflections(world, mouth, ear, arrivals, AudioPhysics.CurrentSpeedOfSound,
                                                 maxOrder: 2, keep: OwnVoiceCopies * 2,
                                                 maxExtraPathMetres: WorldAudioPlayer.RoomEchoWindowSeconds * AudioPhysics.CurrentSpeedOfSound);
        arrivals.Sort(static (a, b) => b.GainMid.CompareTo(a.GainMid));
        float direct = Vector3.Distance(mouth, ear);
        float c = AudioPhysics.CurrentSpeedOfSound;
        int slot = 0, secondOrder = 0;
        foreach (var a in arrivals)
        {
            if (slot >= OwnVoiceCopies) break;
            if (a.ExtraDelaySeconds > WorldAudioPlayer.RoomEchoWindowSeconds) continue;
            if (a.Order >= 2 && ++secondOrder > WorldAudioPlayer.MaxSecondOrderCopies) continue;
            float copyGain = OpenFPS.Common.EarlyReflections.PlacedCopyGain(a.GainMid, a.PathLength, direct, reference)
                           * WorldAudioPlayer.MirrorShare(a.Scattering, a.Order)
                           * OpenFPS.Client.AudioEngine.Fmod.FmodAudioProvider.CopiesTrim;
            if (copyGain < OpenFPS.Common.ImageSource.MinGain) continue;
            var loss = WorldAudioPlayer.SpecularLoss(a.Scattering, a.Order);
            float lowDb = 20f * MathF.Log10(MathF.Max(1e-4f, a.GainLow) / MathF.Max(1e-4f, a.GainMid));
            float highDb = 20f * MathF.Log10(MathF.Max(1e-4f, a.GainHigh) / MathF.Max(1e-4f, a.GainMid));
            int id = firstId - slot;
            var copy = new SpatialEmitter
            {
                EntityId = id,
                SoundId = soundId,
                IsSynth = true,
                PhysicalKey = key,
                EngineKey = "",
                Mode = PlaybackMode.LoopOne,
                Type = EmitterType.WorldLocked,
                Position = a.ImagePosition,
                ApparentPosition = a.ImagePosition,
                Volume = gain * copyGain,
                MinDistance = reference,
                Range = range,
                Pitch = 1f,
                EngineRunning = true,
                IsReflection = true,
                CarriesPath = true,
                Occlusion = 0f, ApertureFactor = 1f, TransmissionBleed = 0f,
                EqLow = MathF.Pow(10f, (loss.LowDb + lowDb) / 20f), EqMid = 1f, EqHigh = MathF.Pow(10f, (loss.HighDb + highDb) / 20f),
                EchoDelaySeconds = MathF.Max(0f, a.PathLength / c - alreadyLateSeconds),
                TargetRegionId = regionId,
            };
            if (on[slot] && _audio.IsPlaying(id)) _audio.UpdateSpatialAttributes(copy);
            else { _audio.PlayPhysicalSoundDirect(copy); on[slot] = true; }
            slot++;
        }
        StopVoiceCopies(firstId, on, slot);
        return slot;
    }

    /// <summary>Stops the copies from slot <paramref name="from"/> on.</summary>
    private void StopVoiceCopies(int firstId, bool[] on, int from = 0)
    {
        for (int k = from; k < on.Length; k++)
            if (on[k]) { _audio.StopSound(firstId - k); on[k] = false; }
    }

    private void StopOwnVoice()
    {
        if (_voiceRoomOn) { _audio.StopSound(OwnVoiceBase); _voiceRoomOn = false; }
        for (int k = 0; k < OwnVoiceCopies; k++)
            if (_voiceCopyOn[k]) { _audio.StopSound(OwnVoiceBase - 1 - k); _voiceCopyOn[k] = false; }
    }

    /// <summary>
    /// The walls answering your own footsteps from their own directions. The tail is diffuse, so with
    /// only it a garage is a box reverberant all round; the near-field probes give the last three metres,
    /// and everything from there to the room's size is these image sources, each delayed by its extra path
    /// (a ceiling 0.9 m up answers in 5 ms, a wall 10 m off in 55). Kept to a handful, at the surface's
    /// loss alone: the renderer applies the distance at the image.
    /// </summary>
    private void SubmitStepReflections(Vector3 stepPos, string soundId, float stepGain, float stepReference, float stepLevelDb)
    {
        // Everywhere, traced mode included: the listener's traced stage plays only the late tail, so
        // without these your steps had a direct sound, a tail 50 ms later and nothing between.
        Vector3 ear = _state.VisualPosition + new Vector3(0, _state.EyeHeight, 0);
        if (_groundWorld is not { } world) return;
        SubmitRoomStepEchoes(stepPos, ear, soundId, stepGain, stepReference, stepLevelDb, world);
    }

    /// <summary>The room's first answers to your own footfall, placed as WorldAudioPlayer.QueueRoomEchoes
    /// places a clap's: image sources to second order, loudest first, inside the window before the tail,
    /// each from its own wall with that wall's colour. The floor under the foot is skipped.</summary>
    private void SubmitRoomStepEchoes(Vector3 stepPos, Vector3 ear, string soundId, float stepGain, float stepReference, float stepLevelDb, WorldSnapshot world)
    {
        _acoustics.FindReflections(world, stepPos, ear, _stepArrivals, AudioPhysics.CurrentSpeedOfSound,
                                             maxOrder: 2, keep: WorldAudioPlayer.MaxRoomEchoes * 2,
                                             maxExtraPathMetres: WorldAudioPlayer.RoomEchoWindowSeconds * AudioPhysics.CurrentSpeedOfSound);
        _stepArrivals.Sort(static (a, b) => b.GainMid.CompareTo(a.GainMid));
        float direct = Vector3.Distance(stepPos, ear);
        int added = 0, secondOrder = 0;
        foreach (var a in _stepArrivals)
        {
            if (a.ExtraDelaySeconds > WorldAudioPlayer.RoomEchoWindowSeconds) continue;
            // The floor the foot is on: the step is made of it already.
            if (a.Order == 1 && a.HitPoint.Y < MathF.Min(stepPos.Y, ear.Y) - 0.2f) continue;
            if (a.Order >= 2 && ++secondOrder > WorldAudioPlayer.MaxSecondOrderCopies) continue;
            // The mirror share only (WorldAudioPlayer.MirrorShare): a step is a bank sample, so its
            // scattered share has no wash to go to yet.
            float gain = OpenFPS.Common.EarlyReflections.PlacedCopyGain(a.GainMid, a.PathLength, direct, stepReference)
                       * WorldAudioPlayer.MirrorShare(a.Scattering, a.Order);
            if (gain < OpenFPS.Common.ImageSource.MinGain) continue;
            gain *= OpenFPS.Client.AudioEngine.Fmod.FmodAudioProvider.CopiesTrim;   // /copies
            var loss = WorldAudioPlayer.SpecularLoss(a.Scattering, a.Order);
            float lowDb = 20f * MathF.Log10(MathF.Max(1e-4f, a.GainLow) / MathF.Max(1e-4f, a.GainMid));
            float highDb = 20f * MathF.Log10(MathF.Max(1e-4f, a.GainHigh) / MathF.Max(1e-4f, a.GainMid));
            int echoId = STEP_ECHO_BASE_ID - (_stepEchoIndex % STEP_ECHO_POOL_SIZE);
            _stepEchoIndex++;
            _audio.Submit(new SpatialEmitter
            {
                EntityId = echoId,
                SoundId = soundId,
                Position = a.ImagePosition,
                ApparentPosition = a.ImagePosition,
                Type = EmitterType.WorldLocked,
                // Scaled by what the surfaces and the longer path kept; the renderer's 1/r at the image
                // is undone in `gain`, as for every copy.
                Volume = stepGain * gain,
                MinDistance = stepReference,
                EarLevelDb = stepLevelDb,
                EarCopyDb = 20f * MathF.Log10(MathF.Max(1e-6f, gain)),
                Range = 25f,
                // No DelayMs: the facade delays every submission by its distance, and the image is the
                // whole path away.
                IsReflection = true,
                IsEvent = true,
                // The path is this: clear both legs (checked when it was found), coloured by the walls.
                CarriesPath = true,
                Occlusion = 0f, ApertureFactor = 1f, TransmissionBleed = 0f,
                EqLow = MathF.Pow(10f, (loss.LowDb + lowDb) / 20f), EqMid = 1f, EqHigh = MathF.Pow(10f, (loss.HighDb + highDb) / 20f),
                TargetRegionId = _listenerRegion,
            });
            if (++added >= WorldAudioPlayer.MaxRoomEchoes) break;
        }
    }

    /// <summary>The map's outdoor ambience bed, from the manifest; started by the next audio update.</summary>
    public void SetMapAmbience(string ambienceId)
    {
        _mapAmbienceId = ambienceId ?? "";
        _ambienceRegionId = int.MinValue;   // force the next update to reconsider
    }

    /// <summary>
    /// Keeps the ambience beds in step with the listener. The outdoor bed is only ducked indoors, never
    /// stopped, by <see cref="LocalPlayerState.ShelterFactor"/> (sky rays and the indoor flag): a room
    /// with a door is still connected to outside. A region's own AmbienceId plays on top inside it, and
    /// both beds glide to their levels, so a doorway cross-fades.
    /// </summary>
    private void UpdateAmbience(WorldSnapshot world, int listenerRegionId)
    {
        if (_mapAmbienceId.Length > 0)
        {
            float outdoor = AcousticConstants.OutdoorAmbienceLevel *
                            (1f - _state.ShelterFactor * AcousticConstants.ShelteredAmbienceDuck);
            _audio.PlayAmbientBed(_mapAmbienceId, AmbisonicFormat.GuessLayout(_mapAmbienceId), outdoor);
        }

        if (listenerRegionId == _ambienceRegionId) return;
        _ambienceRegionId = listenerRegionId;

        string wanted = "";
        if (world.AcousticMap != null &&
            listenerRegionId != AcousticConstants.GlobalRegionId &&
            world.AcousticMap.Regions.TryGetValue(listenerRegionId, out var region))
        {
            wanted = region.AmbienceId ?? "";
        }

        if (wanted == _regionAmbienceId) return;

        if (_regionAmbienceId.Length > 0) _audio.StopAmbientBed(_regionAmbienceId);
        _regionAmbienceId = wanted;
        if (_regionAmbienceId.Length > 0)
            _audio.PlayAmbientBed(_regionAmbienceId, AmbisonicFormat.GuessLayout(_regionAmbienceId),
                                  AcousticConstants.RegionAmbienceLevel);
    }

    // ── The ground ─────────────────────────────────────────────────────────────────────────────

    private Vector3 _groundEar;
    private struct GroundCache { public Vector3 Src, Ear; public double At; public bool Found; public float Height; public string Material; }
    private readonly Dictionary<int, GroundCache> _groundCache = new();
    private WorldSnapshot? _groundWorld;
    private readonly Vector3[] _groundRay = { -Vector3.UnitY };
    private readonly float[] _groundDist = new float[1], _groundAbs = new float[1];
    private readonly string[] _groundMat = new string[1];

    /// <summary>
    /// The ground reflection for one live voice (GroundReflection has the why): the source mirrored in the
    /// surface under its bounce point. One ray down finds the ground under the source, fixing the bounce
    /// point in the ratio of the two heights; a second, from the direct path above that point, finds what
    /// is really there to reflect off (a bonnet, a kerb, a shelter roof) and its material.
    /// </summary>
    private void ApplyGround(ref SpatialEmitter e, WorldSnapshot? world) => ApplyGround(ref e, world, true, 0f);

    /// <summary>
    /// The same for a recorded sound, under a fresh voice id each time, so nothing is cached. A recording
    /// made at the ground already has its bounce in it (adding it again lifts it 6 dB), so one within
    /// <see cref="RecordedGroundMinHeight"/> of the surface gets none.
    /// </summary>
    internal void ApplyRecordedGround(ref SpatialEmitter e, WorldSnapshot world)
        => ApplyGround(ref e, world, false, RecordedGroundMinHeight);

    /// <summary>Below this a recorded sound's own bounce is already in the recording, metres.</summary>
    internal const float RecordedGroundMinHeight = 0.15f;

    private void ApplyGround(ref SpatialEmitter e, WorldSnapshot? world, bool keep, float minHeight)
    {
        e.GroundDelaySeconds = 0f; e.GroundLowGain = 0f; e.GroundHighGain = 0f;
        if (world == null || _state.IsRiding) return;          // from inside a vehicle there is no road to hear
        Vector3 src = e.Position, ear = _groundEar;
        const float Reach = 30f;

        // The rays are the cost: cast again only once the source or the ear has moved a metre, or after
        // a quarter of a second.
        double now = OpenFPS.Common.AudioClock.Now;
        if (!_groundCache.TryGetValue(e.EntityId, out var c)
            || Vector3.DistanceSquared(c.Src, src) > 1f || Vector3.DistanceSquared(c.Ear, ear) > 1f || now - c.At > 0.25)
        {
            c = new GroundCache { Src = src, Ear = ear, At = now, Found = false };
            _spatial.RaycastAll(world, src + new Vector3(0f, 0.05f, 0f), _groundRay, Reach, _groundDist, _groundAbs, _groundMat, staticOnly: true);
            if (_groundDist[0] < Reach)
            {
                float g0 = src.Y + 0.05f - _groundDist[0];
                float hs0 = MathF.Max(0.02f, src.Y - g0), hr0 = MathF.Max(0.02f, ear.Y - g0);
                var above0 = Vector3.Lerp(src, ear, hs0 / (hs0 + hr0));
                _spatial.RaycastAll(world, above0 + new Vector3(0f, 0.02f, 0f), _groundRay, Reach, _groundDist, _groundAbs, _groundMat, staticOnly: true);
                if (_groundDist[0] < Reach)
                {
                    c.Found = true;
                    c.Height = above0.Y + 0.02f - _groundDist[0];
                    c.Material = _groundMat[0] ?? "Generic";
                }
            }
            if (keep) _groundCache[e.EntityId] = c;
        }
        if (!c.Found) return;
        float gb = c.Height;
        _groundMat[0] = c.Material;
        if (gb > MathF.Min(src.Y, ear.Y)) return;                // nothing to bounce off below both
        if (src.Y - gb < minHeight) return;                      // the recording has it already

        var image = new Vector3(src.X, 2f * gb - src.Y, src.Z);
        float direct = MathF.Max(0.1f, Vector3.Distance(src, ear));
        float mirrored = Vector3.Distance(image, ear);
        var m = OpenFPS.Common.AcousticRegistry.GetProperties(_groundMat[0] ?? "Generic");
        float spread = direct / MathF.Max(direct, mirrored);
        float low = MathF.Sqrt(Math.Clamp(1f - 0.5f * (m.AbsorptionLow + m.AbsorptionMid), 0f, 1f));
        float high = MathF.Sqrt(Math.Clamp(1f - m.AbsorptionHigh, 0f, 1f));
        // At grazing incidence even asphalt is not a mirror at the top: its spherical-wave reflection
        // falls with range (Delany-Bazley, 20,000 kPa s/m2, source 0.3 m: 0.88 at 10 m and 0.64 at
        // 50 m at 4 kHz). And the air is never still: turbulence decorrelates the two paths, the
        // Clifford-Lataitis coherence Tc = exp(-sigma2 (1 - Csp)) with a moderate <mu2> of 5e-6 and
        // an outer scale of 1.1 m (Rietdijk, Chalmers 2017, eqs 2.55-2.58).
        high *= Math.Clamp(0.94f - 0.0055f * direct, 0.45f, 1f);
        float rho = 1f / (0.5f * (1f / MathF.Max(0.02f, src.Y - gb) + 1f / MathF.Max(0.02f, ear.Y - gb)));
        low *= Coherence(250f, direct, rho);
        high *= Coherence(2500f, direct, rho);
        e.GroundHeight = gb;
        e.GroundDelaySeconds = (mirrored - direct) / AudioPhysics.CurrentSpeedOfSound;
        e.GroundLowGain = low * spread;
        e.GroundHighGain = high * spread;
    }

    /// <summary>Turbulent coherence between the direct and ground paths (Clifford-Lataitis).</summary>
    internal static float Coherence(float hz, float distance, float rho)
    {
        const float Mu2 = 5e-6f, OuterScale = 1.1f;
        float k = 2f * MathF.PI * hz / AudioPhysics.CurrentSpeedOfSound;
        float sigma2 = MathF.Sqrt(MathF.PI) * Mu2 * k * k * distance * OuterScale;
        float x = MathF.Max(1e-4f, rho / OuterScale);
        float csp = MathF.Sqrt(MathF.PI) * 0.5f * Erf(x) / x;
        return MathF.Exp(-sigma2 * MathF.Max(0f, 1f - csp));
    }

    private static float Erf(float x)
    {
        // Abramowitz and Stegun 7.1.26, good to 1.5e-7.
        float t = 1f / (1f + 0.3275911f * MathF.Abs(x));
        float y = 1f - (((((1.061405429f * t - 1.453152027f) * t) + 1.421413741f) * t - 0.284496736f) * t + 0.254829592f) * t * MathF.Exp(-x * x);
        return x < 0 ? -y : y;
    }

    /// <summary>Probes the space round the listener's head for the mixer, head-relative so the picture
    /// turns with the player.</summary>
    private void UpdateBoundaryProbes(WorldSnapshot world, Vector3 visualEyePos)
    {
        var head = _state.Rotation;
        var directions = BoundaryModel.ProbeDirections;

        // Nothing is near while you ride: a lamp post the bus brushes is half a metre from the glass, not
        // your ear, and the cabin is the interior model's ("the proximity to me from the outside objects
        // isn't relevant").
        if (_state.IsRiding)
        {
            for (int i = 0; i < directions.Length; i++)
                _boundaryProbes[i] = new BoundaryProbe(directions[i], BoundaryModel.MaxDistance, "");
            _audio.UpdateBoundaries(_boundaryProbes);
            return;
        }

        for (int i = 0; i < directions.Length; i++)
            _boundaryRays[i] = Vector3.Transform(directions[i], head);

        _spatial.RaycastAll(world, visualEyePos, _boundaryRays, BoundaryModel.MaxDistance,
                            _boundaryDistances, _boundaryAbsorptions, _boundaryMaterials);

        for (int i = 0; i < directions.Length; i++)
            _boundaryProbes[i] = new BoundaryProbe(directions[i], _boundaryDistances[i], _boundaryMaterials[i]);

        _audio.UpdateBoundaries(_boundaryProbes);
    }

    /// <summary>The server reports a new material underfoot (StatsUpdate): it becomes the listener
    /// region's floor material.</summary>
    public void NotifyMaterialChange(string material)
    {
        if (_lastAcousticMap == null) return;
        var snap = _lastSnapshot ?? _acousticWorker.GetLastWorld();
        if (snap == null) return;
        int listenerRegionId = _acoustics.GetRegionAt(snap, _state.VisualPosition + new Vector3(0, _state.EyeHeight, 0));
        if (listenerRegionId == AcousticConstants.GlobalRegionId) return;
        if (!_lastAcousticMap.Regions.TryGetValue(listenerRegionId, out var region)) return;

        int resonanceIndex = AcousticRegistry.GetProperties(material).ResonanceIndex;
        if (region.Materials == null || region.Materials.Length == 0 || region.Materials[0] == resonanceIndex)
            return;

        region.Materials[0] = resonanceIndex;
        _lastAcousticMap.Regions[listenerRegionId] = region;

        // The acoustic map is not rebuilt: that tears down every reverb bus and its sends mid-tail, the
        // loudest discontinuity the engine can make, on a footstep. The tail is surveyed from the boxes
        // round the listener (Enclosure.Look), the floor among them; the material is kept for whatever
        // still reads the Sabine estimate at bus creation.
    }

    /// <summary>Your own landing: under your own head, and it stays there. See OnOwnFootstep.</summary>
    public void OnOwnLand(Vector3 pos, string mat, string var)
    {
        if (_footTrace) Log.Information("[FOOT] LANDED on {Mat} at {Pos}", mat, pos);
        Vector3 offset = (pos - _state.Position) + new Vector3(0, 0.1f - _state.EyeHeight, 0);
        SubmitLanding(_state.VisualPosition + new Vector3(0, _state.EyeHeight, 0) + offset, mat, follows: true, offset: offset);
    }

    public void OnPlayerLand(Vector3 pos, string mat, string var)
        => SubmitLanding(pos + new Vector3(0, 0.1f, 0), mat, follows: false, offset: Vector3.Zero);

    private void SubmitLanding(Vector3 nudgePos, string mat, bool follows, Vector3 offset)
    {
        string resolved = _sounds.ResolvePath(_sounds.GetLandingSoundId(mat));

        if (!string.IsNullOrEmpty(resolved))
        {
            // Placed like a step (SubmitFootstep), at a step's level; the landing takes are recorded 7 to
            // 10 dB over the walk banks' medians, and that difference is kept.
            var (landGain, landReference) = OpenFPS.Common.Loudness.Place(OpenFPS.Common.Loudness.FootstepDb);
            var landEmitter = new SpatialEmitter
            {
                EntityId = -50,
                SoundId = resolved,
                Position = nudgePos,
                FollowsListener = follows,
                ListenerOffset = offset,
                Type = EmitterType.WorldLocked,
                Volume = landGain,
                EarLevelDb = OpenFPS.Common.Loudness.FootstepDb,
                Range = 20.0f,
                Essential = true,
                IsEvent = true,
                MinDistance = landReference,
                TargetRegionId = _listenerRegion,
            };
            _audio.Submit(landEmitter);
        }
    }
}

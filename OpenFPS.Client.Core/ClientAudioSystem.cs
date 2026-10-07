using System;
using System.Numerics;
using System.Collections.Generic;
using System.Linq;
using OpenFPS.Client.Services;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Client.AudioEngine.Acoustics;
using Serilog;

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
    private readonly DrivingAids _drivingAids;
    private readonly BeaconAids _beacons;
    /// <summary>Beacon categories, policies and the player's switches. See BeaconAids.</summary>
    public BeaconAids Beacons => _beacons;
    /// <summary>The driver's cues and readout. See DrivingAids.</summary>
    public DrivingAids Driving => _drivingAids;
    private readonly SpatialService _spatial; 
    private readonly SpatialAcoustics _acoustics;

    /// <summary>
    /// The short sounds the world reports — a door shutting, glass landing, a round striking a wall.
    ///
    /// Lives here rather than beside the network code because it needs this system's acoustics: a
    /// transient has to be occluded, reverberated and placed by the same path as everything else, or
    /// a door heard through a wall would be the one sound in the game that is not muffled by it.
    /// </summary>
    public WorldAudioPlayer WorldAudio { get; }
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

    /// <summary>The walls answering the live engines. See EngineReflections.</summary>
    private readonly EngineReflections _engineEchoes = new();

    /// <summary>The voice id of one image-source reflection slot of a source. Slots are ordered by
    /// surface (see EarlyReflections), so a slot is the same wall from tick to tick.</summary>
    private static int ReflectionVoiceId(int sourceId, int slot)
        => -30000 - (sourceId * (EarlyReflections.MaxArrivals + 1)) - slot;

    /// <summary>Which of a source's reflection slots answered this frame; the rest are retired.</summary>
    private readonly bool[] _slotLive = new bool[EarlyReflections.MaxArrivals];

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
    /// How much nearer a silent car has to be than a sounding one before it takes its voice.
    ///
    /// Pure ranking by distance churns: on an oval, two cars swap order several times a lap, and
    /// every swap stops one engine and starts another — which is not a cross-fade, it is a synthesis
    /// thrown away and restarted with cold pipes and a stopped crank, heard as a car stuttering in
    /// and out. Ranking a car that is already sounding as if it were a quarter nearer
    /// than it is makes it keep its voice until the challenger is decisively closer.
    /// </summary>
    private const float EngineKeepBias = 0.75f;

    /// <summary>And no engine is dropped within this long of being started, whatever the ranking
    /// says, so a car that crosses the boundary at three hundred kilometres an hour cannot be
    /// started and stopped inside the same second.</summary>
    private const double EngineMinimumHoldSeconds = 2.5;

    /// <summary>
    /// The audio update is capped here rather than in each head's game loop, so both are capped by the same
    /// rule. The loop it hangs off polls the network as fast as it can — a 5 ms sleep, so roughly 200 Hz.
    /// Uncapped, it would run the entire audio update at that rate: listener sync, region resolution, the
    /// near-field radar's six raycasts, an acoustic request per active voice, and the FMOD tick. None of
    /// that resolves faster than a frame, so two updates in three would be work nobody could hear, taken
    /// from the thread that has to service the socket.
    /// </summary>
    public const double UpdateHz = 60.0;
    /// <summary>
    /// What a loud exhaust measures at one metre under load — the fallback for a vehicle that does
    /// not declare its own level.
    ///
    /// Every preset does declare one (<c>VehicleProfile.SourceLevelDb</c>, measured with
    /// `--engine-levels`), and they run from 91 dB for a diesel pickup to 133 for an unsilenced V10.
    /// Placing all of them at this one number put the race cars forty decibels of dynamic range out
    /// and, worse, was also being used as the synthesis's clipping reference.
    /// </summary>
    public const float EngineSourceLevelDb = 116f;

    /// <summary>
    /// How many vehicles may run a LIVE engine at once.
    ///
    /// Every one of them is a whole engine — cylinders, valves, waveguides — integrated sample by
    /// sample on the engine render pool, and measured (`--engine-cost`) a V8 renders about nine
    /// seconds of audio per second of one core in a release build. A field of cars can be any size
    /// it likes on the server; the nearest few are the ones a listener can pick out anyway, and the
    /// rest are voiced by borrowing a near car's ring (see the distant voices below).
    ///
    /// Overridable with OPENFPS_ENGINE_VOICES for anyone with a machine to spend.
    /// </summary>
    public static readonly int EngineVoiceBudget =
        int.TryParse(Environment.GetEnvironmentVariable("OPENFPS_ENGINE_VOICES"), out int budget) && budget > 0
            ? budget : 32;

    /// <summary>
    /// The budget actually in force, which follows the MEASURED mixer load rather than a guess.
    ///
    /// A fixed number cannot be right. What one engine costs depends on the engine, and what is left
    /// over depends on everything else in the map — reverb, reflections, footsteps, however many
    /// sources a map nobody has seen yet decides to carry. What measures half a core on the bench can
    /// peg the mixer in the game, because the game also has all of that. And a mixer at a hundred per
    /// cent does not sound busy, it sounds broken: the callback
    /// misses its deadline and the output tears, which is heard as crackling and is easy to mistake
    /// for distortion.
    ///
    /// So the budget is a control loop, and it gives things up IN ORDER. Reflections first — a car's
    /// second reflection, then its first — and only then a car. That order is not a detail: shedding
    /// cars first makes the field sound like voices being swapped, because a car arriving pushes out
    /// one that has not finished going past, and then the one that left comes back. A listener notices
    /// a car vanishing and does not notice a wall stopping answering.
    ///
    /// The floor is two cars, not one. One engine on a racetrack is not a race.
    /// </summary>
    private int _adaptiveBudget = EngineVoiceBudget;

    /// <summary>Producer starves per second above which live engines are given up.</summary>
    private const float StarveCeilingPerSecond = 5f;
    private float _starveRate;
    private int _starvesSeen;
    private double _starveSampledAt = -1;
    /// <summary>
    /// The ceiling on engine reflections, overridable with OPENFPS_ENGINE_ECHOES.
    ///
    /// It exists as a switch because echoes are BY FAR the highest-churn object in the audio system
    /// and the only one that reaches into another voice's buffers: the city puts about 256 of them
    /// through create-and-release in 98 seconds, each holding a reference to the engine voice it is an
    /// echo of. When a fault lands on the mixer thread, taking the busiest subsystem out in one
    /// run is the quickest way to clear or implicate it.
    ///
    ///     OPENFPS_ENGINE_ECHOES=0   no reflections of engines at all
    ///     OPENFPS_ENGINE_ECHOES=1   one per engine instead of two
    ///
    /// Unset is the normal behaviour. This is a diagnostic lever, not a setting anybody should need.
    /// </summary>
    /// <remarks>
    /// An unsmeared engine echo would be a COHERENT copy of the engine — the same waveform, read
    /// later, placed at a mirror point through the full HRTF. Against the direct sound that is a comb
    /// filter that sweeps as either moves (phasing, a car heard inside out); on its own it is a point
    /// source beamed from a wall, carrying, say, a bus's air hiss to wherever the mirror is. What a
    /// street really sends back off a steady engine is many surfaces at once, incoherent. So each
    /// echo is smeared by the roughness of the wall it came off (EngineEchoState.Scattering): the
    /// same sound but not the same waveform, and it swells and fades over a few hundred milliseconds
    /// instead of switching. OPENFPS_ENGINE_ECHOES=0 takes them out for an A/B.
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
    /// How many machines may have their front outlet voiced separately.
    ///
    /// A car close enough for its two ends to be told apart is a car going past you, and there are
    /// never many of those at once — the geometry says so: inside about twenty metres for a car, and
    /// a listener has one of those either side of them at worst. The budget exists for the case the
    /// geometry does not cover, which is standing in the middle of a grid on a start line, and it is
    /// the FIRST thing given up when the mixer runs short, before reflections: a second outlet is the
    /// most expendable voice in the world, because the machine is still fully audible without it.
    /// </summary>
    /// <remarks>OPENFPS_FRONT_VOICES=0 keeps every machine on one voice, for an A/B of the split.</remarks>
    private static readonly int FrontVoiceBudget =
        int.TryParse(Environment.GetEnvironmentVariable("OPENFPS_FRONT_VOICES"), out int fv) && fv >= 0 ? fv : 6;
    private int _adaptiveFront = FrontVoiceBudget;

    /// <summary>
    /// How many outer places of trees, fires and fountain taps may have voices at once (ExtendedSources),
    /// nearest source first. Like a machine's front outlet, a place costs nothing but geometry when it is
    /// given up: the source merges to its middle at the same level. So places are the first thing the
    /// mixer gives up when it runs short, a source's worth at a time, and the last it takes back.
    /// </summary>
    /// <remarks>OPENFPS_PLACE_VOICES=0 keeps every extended source on its middle alone.</remarks>
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
    /// How many STANDING machines may run live at once — air conditioners, mowers, plant.
    ///
    /// Its own budget rather than a share of the engines', because the two are authored at completely
    /// different densities. A map carries tens of vehicles and it carries HUNDREDS of small machines:
    /// the city has a hundred and fourteen window units, one in the window of every street-side flat
    /// on the lower five floors of five towers, which is what a city has. Authoring five of them and
    /// calling it a city would hide the problem this budget exists to solve rather than pose it.
    ///
    /// The ranking underneath it is the part that matters, and it is NOT distance: it is what each
    /// machine would actually SOUND like here (Loudness.RenderedGain). A rooftop condenser at 65 dB
    /// eighty metres up the street beats a window unit at 59 dB behind a hedge, and a distance
    /// ranking gets that backwards. See the audibility note on EngineReflections for the same rule
    /// applied to reflections.
    ///
    /// Overridable with OPENFPS_MACHINE_VOICES, and ZERO IS A VALID ANSWER: 0 switches every machine
    /// and aircraft voice off, which is what the lever is for (taking a subsystem out to see whether
    /// it is the one at fault). Treating 0 as unset would make the lever useless.
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

    /// <summary>Which physical models currently have a live voice, and when each started.</summary>
    private readonly HashSet<int> _liveMachines = new();
    /// <summary>The ids whose acoustic path is asked for this frame. See step 5.</summary>
    private readonly HashSet<int> _pathIds = new();
    private readonly Dictionary<int, double> _machineStarted = new();
    private readonly List<int> _machineRetiring = new();
    private readonly List<(int Id, float Key, float Level)> _machineOrder = new();

    /// <summary>
    /// How many borrowed voices may sound at once, beyond the synthesized ones.
    ///
    /// Borrowing makes a car cheap; it does not make it free. The engine is not run, but the voice is
    /// still placed — HRTF, filtering, a channel — so thirty of them is thirty spatialised sources
    /// and the saving is thrown away again. This is the second budget, and it is what actually lets a
    /// map carry any number of cars: the field can be thirty or three hundred, a few are synthesized,
    /// the nearest handful of the rest are voiced, and everything beyond that is genuinely too far
    /// away to pick out of the pack.
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
    /// How long after a map load the budget is left alone, and how long the mixer must stay over the
    /// ceiling before anything is given up.
    ///
    /// The control loop steers on FMOD's dsp percentage, and during a load that reading is a LIE. It
    /// pins at a hundred per cent while the mixer thread is stalled — by a GC suspension, by a
    /// producer it was waiting on — and a stall is not a mixer that is short of capacity, it is a
    /// mixer that is not running. A loop that cannot tell the difference sheds echoes, then borrowed
    /// voices, then cars, and a second later, the stall over and the load back to normal, adds them
    /// all back. Every re-added engine is a NEW voice — a new synth, a tenth of a second of warm-up, a
    /// quarter of a second of fill, an FMOD graph rebuild — so its response to a busy machine is to
    /// make more work for it at the worst possible moment, heard as cars appearing and vanishing on
    /// the first lap.
    ///
    /// So: nothing is given up for the first few seconds in a map, and after that the ceiling has to
    /// be exceeded for three quarters of a second together, not in one sample.
    /// </summary>
    private const double BudgetHoldSeconds = 3.0;
    private const double OverCeilingSeconds = 0.75;
    private double _budgetHeldUntil;
    private double _overCeilingSince = -1;

    /// <summary>
    /// Called when a map starts loading and again when the player is spawned into it. See
    /// BudgetHoldSeconds: for the next few seconds the mixer's load reading cannot be trusted.
    /// </summary>
    public void NoteSceneLoading() => _budgetHeldUntil = _now() + BudgetHoldSeconds;
    private readonly UpdateThrottle _throttle = new(UpdateHz);
    /// <summary>Seconds since this system was built. A stopwatch in the game; a test hands in its
    /// own, so it can tick the throttled update and step past the engine hold without sleeping.</summary>
    private readonly Func<double> _now;

    /// <summary>Audio updates performed / skipped by the rate cap. Diagnostic.</summary>
    public (long Ran, long Skipped) UpdateCounts => (_throttle.Runs, _throttle.Skipped);

    // Near-field boundary probing. The directions are rebuilt each frame from the listener's rotation
    // (BoundaryModel.ProbeDirections is head space), and every buffer here is owned and reused — this
    // runs on every audio frame.
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

    public ClientAudioSystem(AudioEngineFacade audio, SoundMappingService sounds, LocalPlayerState state)
        : this(audio, sounds, state, StopwatchClock()) { }

    /// <summary>For tests: the same system on a clock the caller controls.</summary>
    internal ClientAudioSystem(AudioEngineFacade audio, SoundMappingService sounds, LocalPlayerState state,
                               Func<double> clock)
    {
        _now = clock;
        _audio = audio;
        _sounds = sounds;
        _state = state;
        _drivingAids = new DrivingAids(audio);
        _spatial = new SpatialService();
        _acoustics = new SpatialAcoustics(_spatial); // Share the same SpatialService instance
        // After the acoustics, which it needs: a beacon behind a wall is not blipped.
        _beacons = new BeaconAids(audio, acoustics: _acoustics);
        // The short sounds the world reports. Shares this system's acoustics so a rendered latch
        // takes exactly the path a recorded one would.
        WorldAudio = new WorldAudioPlayer(_audio, _acoustics);
        WorldAudio.HornReceived = StartHorn;
        WorldAudio.TrainSignalReceived = StartTrainSignal;
        WorldAudio.Ground = ApplyRecordedGround;
        _birds = new BirdLife(audio, _acoustics);
        _rain = new RainField(audio, _acoustics);
        WorldAudio.Received = message => _birds.Heard(message, OpenFPS.Common.AudioClock.Now);
        _audio.RoutesSource = () => _acoustics.Routes;
        _acousticWorker = new AsyncAcousticWorker(_acoustics);
        WorldAudio.Worker = _acousticWorker;
        _acousticWorker.Start();
    }

    private static Func<double> StopwatchClock()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        return () => clock.Elapsed.TotalSeconds;
    }

    /// <summary>
    /// Silences everything an entity was making sound with, after the server said it is gone.
    /// A voice is keyed by entity id and keeps playing on its own once started, so a looping emitter on a
    /// despawned object would otherwise sit in the world for the rest of the session. The reflection
    /// voices derived from that id are stopped too — they carry synthetic ids, not the entity's own.
    /// </summary>
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

    public void ForgetEntity(int entityId)
    {
        _groundCache.Remove(entityId);
        _audio.StopSound(entityId);
        _liveEngines.Remove(entityId);
        _engineStarted.Remove(entityId);
        _motionHistory.Remove(entityId);
        _tyreDemand.Remove(entityId);
        _engineRetiring.Remove(entityId);
        _engineEchoes.Forget(entityId, _audio);
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
        // Its image-source reflections: one voice per slot (see ReflectionVoiceId).
        for (int slot = 0; slot < EarlyReflections.MaxArrivals; slot++)
            _audio.StopSound(ReflectionVoiceId(entityId, slot));

        _acousticWorker.Forget(entityId);
    }

    /// <summary>
    /// Silences the whole world, for leaving it: every entity forgotten, the ambience beds stopped,
    /// and anything still playing stopped. Nothing here runs again until the next map, because
    /// <see cref="Update"/> is only called in the world.
    /// </summary>
    public void LeaveWorld(IEnumerable<int> entityIds)
    {
        foreach (int id in entityIds) ForgetEntity(id);
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
    /// Primary entry point called every frame from the Game Loop.
    /// Uses the VisualPosition for the listener to ensure smooth audio during server corrections.
    /// </summary>
    public void Update(WorldSnapshot world)
    {
        if (!_throttle.ShouldRun(_now())) return;

        using var _perf = PerfProbe.Measure("audio.update");

        // ── How long every source's position went unrefreshed ────────────────────────────────
        //
        // This is the only place an emitter's position is resubmitted. Between two runs of it, every
        // sound in the world is placed where it was when the last one finished — so the gap between
        // these calls IS the length of time a car's engine sits still in the air while the car keeps
        // driving. It is not the audio thread (which holds 240 Hz and only re-reads what it was last
        // given) and it is not the interpolator (which has already moved the car); it is this, so
        // it is measured here.
        //
        // Thirty cars is roughly four times the per-source work of eight — acoustic paths, occlusion,
        // reflections, emitter submission — all of it inside one game-loop iteration.
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
        // Keep the PREVIOUS snapshot to compare against: assigning first and then comparing `world` with
        // `_lastSnapshot` compares it with itself, which is what silently disabled the moving-region check
        // below. With the snapshot cache in place the two can also legitimately be the same object — a
        // world that has not changed — and a zero delta is then exactly the right answer.
        var previous = _lastSnapshot;
        _lastSnapshot = world;
        _acousticWorker.UpdateWorld(world);
        
        // --- Use smoothed VisualPosition for the listener ---
        Vector3 visualEyePos = _state.VisualPosition + new Vector3(0, _state.EyeHeight, 0);
        _groundEar = visualEyePos;
        // The cabin you are sitting in, for the traced reverb: which vehicle, and where your ear is in
        // its own frame.
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

        // 1. Resolve high-precision listener region (OBB check)
        int listenerRegionId = _acoustics.GetRegionAt(world, visualEyePos);
        // Kept, because your own feet are submitted from the game thread between updates and have to
        // know which room to reverberate in. See SubmitFootstep.
        _listenerRegion = listenerRegionId;

        // 2. Synchronize the listener's smoothed physical state.

        // Not the wind: a uniform wind moves the source, the listener and the air together and shifts
        // no pitch. A share of it added here bent every pitch in the world with each gust.
        Vector3 listenerVelocity = _state.Velocity;
        // Sitting in something, you face the way it faces: the session sets your heading from the
        // vehicle every frame (ClientGameSession.FollowRide), so the ears and the compass agree.
        var listenerRotation = _state.Rotation;
        if (_state.IsRiding && world.Entities.TryGetValue(_state.RidingEntityId, out var carrying))
        {
            // ...and you move at its speed. A passenger is not predicted, so their own velocity reads
            // zero — which against the vehicle's moving voice is a Doppler shift on your own bus.
            listenerVelocity = carrying.Velocity;
        }

        // The wind at your ears: the weather's wind where your head is, less your own movement through
        // it, from the side it comes from. The mixer reads the field itself once a block (EarWindVoice);
        // this is where the head is, which way it faces, and what is round it.
        var earListener = EarListener(world, visualEyePos, listenerVelocity, listenerRotation);
        _audio.SetEarWind(earListener);
        {
            double windNow = WindField.Now();
            var weather = WindField.Weather;
            var air = WindField.VelocityAt(weather, visualEyePos.X, earListener.HeightMetres, visualEyePos.Z, windNow);
            var felt = EarWind.Relative(air, earListener);
            _state.FeltWind = new Vector3(felt.X, 0f, felt.Y);
        }
        _audio.UpdateListener(visualEyePos, listenerRotation, listenerVelocity, listenerRegionId);
        _audio.UpdateShelter(_state.ShelterFactor);
        WorldAudio.ListenerVehicleId = _state.RidingEntityId;
        WorldAudio.Cabins = _cabins;
        WorldAudio.SelfId = OwnEntityId;
        WorldAudio.Self ??= () => (_state.Position, _state.Rotation);
        // The doors, the things to pick up, the cars to get into and the people around you.
        _beacons.Update(world, visualEyePos, _now(), OwnEntityId);
        // The lane lines, if you are the one driving.
        _drivingAids.Update(world, _state, _now());
        // ...and the rest of the world through the glass, if you are sitting in anything with a roof.
        var (encLow, encMid, encHigh) = CabinEnclosure(world);
        // Now and then, the windows of vehicles that have gone.
        if (_frameCount % 600 == 0) _cabins.Forget(world.Entities.ContainsKey);
        _audio.SetListenerEnclosure(encLow, encMid, encHigh);
        // Temperature reaches the mix as the speed of sound: c = 331.3 + 0.606·T.
        _audio.SetAirTemperature(world.Temperature);

        // Geometry-driven reverb (Phase 4d): drive the listener-region reverb decay from the Steam Audio
        // reflection sim when available (replaces the Sabine estimate for the room the listener is in).
        if (_acousticWorker.TryGetListenerReverbDecayMs(out float simReverbMs))
        {
            _audio.SetSimulatedReverbDecay(simReverbMs, _acousticWorker.ListenerEnclosure,
                                           _acousticWorker.ListenerHfDecayRatio,
                                           _acousticWorker.ListenerLfDecayRatio);
        }
        
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
                //
                // Over the REGIONS, not over the world. Walking every entity to find the ones that
                // declare a room is six thousand struct copies a frame on the city, for six hundred
                // regions, almost none of which can move at all.
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
        
        // 4. Update the local player's environmental state
        UpdateAcousticState(world, visualEyePos, listenerRegionId);
        // Through the echo system: a wall does not care what made the sound, so the grandstand that
        // answers a car answers the people sitting on it too — and on the same terms, which means
        // obstruction-tested.
        WorldAudio.Update(world, visualEyePos, OpenFPS.Common.AudioClock.Now, _engineEchoes);

        // 4.5. Near-field boundary probing.
        //
        // Six rays out of the listener's head — right, left, up, down, forward, back — turned into world
        // space by the listener's own rotation, so what comes back is "there is concrete half a metre to
        // my LEFT", not "there is concrete somewhere near". Each becomes its own early reflection in the
        // mixer (see BoundaryModel / BoundaryProximityProcessor), which is what lets a player hear the
        // difference between a corridor, a doorway and the open air, and hear it change as they turn.
        UpdateBoundaryProbes(world, visualEyePos);

        // 4.6. Ambience beds: the map's outdoor soundfield, ducked by shelter, plus whatever the
        // listener's own region declares.
        UpdateAmbience(world, listenerRegionId);

        Stage(0, ref stageTicks);     // everything up to here: region, listener, map, probes, ambience

        // 5. Update the acoustic path (occlusion/diffraction) for ALL active sounds in FMOD
        //
        // And for every car and machine that holds a live slot, even one the mixer is not playing.
        // The voice manager silences a voice whose OCCLUDED level falls under the silence floor, and
        // asking only about playing voices then stopped asking about it: after the worker's 5 s TTL
        // its result was evicted, the emitter fell back to an unoccluded path, and the voice was
        // rebuilt at full level until the next answer silenced it again. Heard as ambience that
        // "stutters and cuts out" — an air conditioner behind a tower and a car round a corner,
        // each bursting back every five seconds.
        _pathIds.Clear();
        _pathIds.UnionWith(_audio.GetActiveSpatialSoundIds());
        _pathIds.UnionWith(_liveEngines);
        _pathIds.UnionWith(_liveMachines);
        _pathIds.UnionWith(_horns.Keys);      // a horn takes its vehicle's path, borrowed voice or not
        _pathIds.UnionWith(_sirenCars);       // and so does a siren
        // ...and a car voiced from afar, whose borrowed voice id sits below the reflection range
        // and so was never given a path of its own. Twelve of the city's cars, never occluded and
        // never darkened by the air a kilometre away: the white-noise wash heard from the edge of
        // the map, high band 10 dB under the low where every other far source had it 42 under.
        _pathIds.UnionWith(_distantVoiced);
        foreach (var id in _pathIds)
        {
            // --- CRITICAL FIX: Reflection Termination ---
            // IDs below -5000 are the copies and the voices that work out their own paths (a wall's
            // copy, your own voice and your room's answer to it, a talker, a horn, a siren). We MUST NOT
            // calculate reflections for reflections, or we get an infinite feedback loop; and a copy
            // asked about from its image is traced through the very wall that made it.
            if (id < -5000)
            {
                // Simple distance update for existing reflections to maintain panning, 
                // but no recursive ray-tracing.
                continue; 
            }

            // ── Ask about the point the sound comes OUT of ───────────────────────────────────
            //
            // Not the entity's origin. The voice has always been placed at the emission point; the
            // occlusion probe was left behind at the origin, so the engine was answering "what can be
            // heard from here" about a place nothing was radiating from. For anything that drives,
            // that origin is its contact patch on the ground and the probe sphere was half buried —
            // about half the samples reporting blocked on open road, before any wall was considered.
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
                foreach (var path in paths)
                {
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

                    // A reflection path is honoured wherever it came from. Both paths produce them now:
                    // under Steam Audio simulation they are first-order image sources off the scene's own
                    // surfaces (EarlyReflections), and on the fallback path they come from the hand-rolled
                    // tracer. A reverb tail does not cover the same energy: without these a room
                    // answers from everywhere at once and a doorway is inaudible from outside.
                    if (!world.Entities.TryGetValue(id, out var originalSnap)) continue;

                    // A SYNTHESISED source has no file to play a delayed copy of, and this is the
                    // rule rather than a list of names.
                    //
                    // Its sound id names a model, not a sample, so asking the provider to play it as
                    // one fails every frame — "not playing yet — Missing", once a frame per source,
                    // for ever, with a deferred play queued behind each one. A hundred and twenty-one
                    // window units retrying a file load every frame is most of a game loop.
                    //
                    // IsSynth is the property that actually decides it, and it is on the snapshot. A
                    // list of sound-id prefixes is not enough: it misses whatever kind of synthesised
                    // source was added after it was written.
                    // Anything the mixer RENDERS rather than plays is answered by its own reflection
                    // path — EngineReflections reads a car's walls out of the synthesis's own ring —
                    // or by nothing, which is correct until one exists.
                    var sourceEmitter = originalSnap.Definition.SoundEmitter;
                    string sourceSound = sourceEmitter.SoundId ?? "";
                    if (sourceEmitter.IsSynth
                        || sourceSound.StartsWith("engine:", StringComparison.OrdinalIgnoreCase)
                        || sourceSound.StartsWith("ENGINE/", StringComparison.OrdinalIgnoreCase)) continue;
                    if ((uint)path.ReflectionIndex >= (uint)EarlyReflections.MaxArrivals) continue;
                    // Everywhere: the listener's traced stage plays the late tail alone, so a sustained
                    // source's first bounces are these, indoors as out.
                    _slotLive[path.ReflectionIndex] = true;

                    // One voice per slot, and the slots are ordered by the SURFACE each arrival
                    // came off (see EarlyReflections), so a slot means the same wall from tick to
                    // tick. The index cannot collide: it is 0..N-1 among the arrivals that are live
                    // right now. A hash of the surface's identity (`ReflectionId % 100`) can, and a
                    // collision is one voice handed two reflections from opposite sides of a room,
                    // flickering between them.
                    int reflectId = ReflectionVoiceId(id, path.ReflectionIndex);
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
                        // What the copy has left, per band, is the whole of what makes it a
                        // reflection rather than a second source: the surface took some of it and
                        // the extra distance took the rest (LoopEchoLevel).
                        Volume = echo.Volume,
                        MinDistance = echo.MinDistance,
                        Range = originalSnap.Definition.SoundEmitter.Range * 0.8f,
                        IsReflection = true,
                        // The copy starts at the source voice's own playback position, so it is what
                        // is being heard, arriving later — not the file again from the top.
                        ReflectionOf = id,
                        DelayMs = path.ReflectionDelayMs,
                        Type = EmitterType.WorldLocked,
                        EqLow = echo.EqLow,
                        EqMid = echo.EqMid,
                        EqHigh = echo.EqHigh,
                        AirLowDb = path.AirLowDb, AirMidDb = path.AirMidDb, AirHighDb = path.AirHighDb,
                        ReflectionSpread = path.Spread,
                        // Feed leftover energy into the reverb bus
                        TransmissionBleed = path.MaterialAbsorption * 0.5f 
                    };

                    if (_audio.IsPlaying(reflectId))
                    {
                        // Back within the fusion window's reach: a surface that had stopped answering
                        // and started again keeps its voice rather than restarting it.
                        _audio.CancelFade(reflectId);
                        _audio.UpdateSpatialAttributes(reflectEmitter);
                    }
                    else _audio.PlayPhysicalSoundDirect(reflectEmitter);
                }

                // ── A surface that has stopped answering is let go with a fade, not left playing ──
                //
                // Left alone, a wall's copy would play on at its last position for as long as the
                // source did, and a listener who walked out of a slapback's reach would keep hearing
                // it from where the wall had been. A cut is a click, so the voice fades over the
                // budget's own ramp and is stopped only once it is silent.
                for (int slot = 0; slot < EarlyReflections.MaxArrivals; slot++)
                {
                    if (_slotLive[slot]) continue;
                    int reflectId = ReflectionVoiceId(id, slot);
                    if (_audio.IsPlaying(reflectId) && _audio.FadeOut(reflectId)) _audio.StopSound(reflectId);
                }
            }
        }

        Stage(1, ref stageTicks);     // 5: the acoustic paths of every active voice

        // 5.5. Decide which vehicles get a live engine: the nearest EngineVoiceBudget of them.
        //
        // Done here, once, rather than inside the per-entity pass, because it is a decision ABOUT the
        // set: the eighth-nearest car cannot know it is eighth. A car that loses its slot has its
        // engine and its echoes stopped, which is a real cut rather than a fade — but it only ever
        // happens to whichever car is furthest away and being drowned by three nearer ones.
        ChooseLiveEngines(world, visualEyePos);
        // The rain that has landed and is running off (gutters, drains, downpipes): fed before the
        // machines are ranked, so a gutter that has run dry is not given a voice. Snow stays where it falls.
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

        // 6. Process persistent audio emitters attached to world entities (NPCs, Beacons, Machines)
        foreach (var entityId in world.AudioEntityIds)
        {
            if (entityId == OwnEntityId) continue;
            if (world.Entities.TryGetValue(entityId, out var snap))
            {
                // An authored beacon whose category is switched off — by the map or by you — is not
                // heard. (Doors, items and cars are blipped by BeaconAids; this is the placed kind.)
                if (snap.Definition.Type == EntityType.Beacon && !_beacons.IsOn(snap.Definition.Identity.BeaconCategory))
                {
                    if (_audio.IsPlaying(entityId)) _audio.StopSound(entityId);
                    continue;
                }
                ProcessAudioEmitter(world, snap, visualEyePos, engineDt);
            }
        }
        // The outer places of trees and fires that were not placed this frame (out of the budget, gone).
        RetireSpreading();
        // The cabin's paths, if you are no longer sitting in what they were paths of.
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

        // Every source has now been offered to the reflection system; it can work out what the
        // next frame will demand of a reflection to be worth a voice.
        _engineEchoes.EndFrame();

        {
            double pass = 0; foreach (var v in _partMs) pass += v;
            if (pass > _partWorstPass) { _partWorstPass = pass; Array.Copy(_partMs, _partWorstMs, _partMs.Length); }
            Array.Clear(_partMs);
        }
        Stage(3, ref stageTicks);     // 6: building an emitter for everything that has a voice

        // 7. Execute the audio engine tick (mixing, DSP updates)
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
    /// Where the audio update's time went, by stage, worst case over the reporting interval.
    ///
    /// It exists because "the pass itself took at most 74 ms" is a number you cannot act on. The
    /// whole pass being four times its 17 ms budget says something is wrong and nothing about what,
    /// and the candidates are not close together: a loop over every entity in the world, a per-voice
    /// acoustic path, a ranking over every machine on the map, an emitter built per source, and the
    /// mixer's own attribute pass. On a large map, guessing between those wastes a session.
    /// </summary>
    private readonly double[] _stageWorstMs = new double[5];

    /// <summary>
    /// Inside the emitter stage, the pass that cost most, by part: the engines' echoes, the ground
    /// rays, the birds, the horns and sirens, and everything else. The emitter stage alone can run to
    /// 150-250 ms on the city, and every source freezes for that long (heard as reflections stepping
    /// away and catching up late); this names which part.
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
        _state.Humidity = world.Humidity;
        _state.AirPressure = world.AirPressure;
        _state.WindVelocity = world.WindVelocity;
        _state.WindGustiness = world.WindGustiness;

        // Scale down precipitation intensity based on local shelter
        _state.PrecipitationIntensity = world.PrecipitationIntensity * (1.0f - _state.ShelterFactor);
        // The water on the roads, for the vehicles whose wheels the server does not send.
        _roadWaterMm = world.RoadWaterMm;

        // Update readable region for accessibility. Named by the boxes: in a doorway, which for sound is
        // the room on your side of it, you are told you are in the doorway.
        int zone = _acoustics.GetZoneAt(world, eyePos);
        _state.CurrentRegionId = zone;
        _state.CurrentRoomId = _acoustics.GetRoomAt(world, eyePos);
        if (world.AcousticMap != null && world.AcousticMap.Regions.TryGetValue(regId, out var reg))
        {
            _state.IsIndoor = reg.IsIndoor;
            _state.RoomSize = reg.RoomSize;
            _state.RoomCenter = world.AcousticMap.RegionPositions.GetValueOrDefault(regId, eyePos);
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

    /// <summary>
    /// What to call where the listener is standing.
    ///
    /// A NAMED place says its name. Everywhere else is named from WHAT YOU ARE STANDING ON.
    ///
    /// "Outside" is true and useless. A player working out where they are by ear needs to know they
    /// have stepped off the kerb, and the ground already knows — the same probe that chooses the
    /// footstep material. Concrete under the feet in the open air is a pavement; asphalt is the
    /// road; and the difference between them is the single most navigationally important fact on a
    /// city street.
    ///
    /// The map-wide outdoor region is NOT a named place, although it has a name. It is always in
    /// the acoustic map — the generator puts it there so the outdoors has a reverb and a size — so a
    /// region lookup alone always succeeds, and would announce every metre of open ground no author
    /// had boxed as "Outside". Its name is kept only for ground the material table does not recognise.
    ///
    /// Derived, not authored: no map has to label a kerb, and a surface nobody has named still
    /// announces itself correctly.
    /// </summary>
    /// <summary>The name of a roofed spot no zone covers: a doorway between two rooms, a gap between
    /// two zones' boxes. A condition, not a place, so it is never announced on its own.</summary>
    internal const string UnderShelter = "Under Shelter";

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

    /// <summary>
    /// What to call a patch of open ground, from the surface underfoot. Falls back to "outside"
    /// for anything unrecognised.
    /// </summary>
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
    /// Decides which physical models run live — standing machines and aircraft — by picking the ones
    /// that would actually be LOUDEST here.
    ///
    /// Not the nearest, and that is the whole of the decision. A city authors small machines by the
    /// hundred — sixty window air conditioners on five towers, a mower in every third garden, plant
    /// on every roof — and the budget can afford ten of them. Ranking by distance answers "which is
    /// closest", which is not the question a listener asks: a 92 dB mower three gardens away is
    /// plainly audible where a 59 dB window unit at the same distance is not, and a rooftop
    /// condenser eighty metres up the street beats both. What decides it is what each one would
    /// SOUND like at the ear, which is the rendered gain the mixer is about to apply — its own
    /// source level, placed, paid down for its own extent, rolled off over its own distance.
    ///
    /// Which is also why aircraft share this budget rather than getting one of their own. An
    /// airliner at 142 dB half a kilometre up beats every air conditioner in the city and should;
    /// the same airliner parked at the far end of its path at idle should not. One ranking on one
    /// measure answers both, where two budgets would have meant deciding in advance how many
    /// aeroplanes are worth how many machines — a question with no answer that does not depend on
    /// where the listener is standing.
    ///
    /// Everything else here is the engine's discipline, for the engine's reasons: a machine that
    /// holds a slot keeps a bias so the set does not churn as you walk, a new one is held for a
    /// couple of seconds before it can be taken off again, and one that loses its slot FADES rather
    /// than being cut, because a running synthesiser has no zero-crossing to stop at.
    /// </summary>
    /// <summary>
    /// What a "machine:" or "aircraft:" id is worth, in the two numbers placement needs: how loud it
    /// is at a metre, and how big it is.
    ///
    /// One lookup, used by BOTH the ranking and the emitter, because those two disagreeing is a
    /// source that wins a voice on one set of numbers and is then played at another — audible as a
    /// machine that is picked out of a crowd and then cannot be heard.
    ///
    /// MEMOISED BY NAME, and that is not an optimisation, it is the difference between the client
    /// running and not. Behind ByName is ModelLibrary.Get, which CONSTRUCTS the model every call —
    /// a governor, a deck, a blade row, a casing, a compressor, all nested records — and this is
    /// asked once per machine per audio update. A city with a hundred and twenty-one of them on it
    /// is seven thousand whole machines built and thrown away every second, on the thread that also
    /// places every moving sound. Measured, in the game: gen2 collections with 57 ms pauses, the
    /// game loop reporting 0 Hz and 88 ms iterations, and the placement pass holding every source
    /// still for 106 ms — which is not heard as "slow", it is heard as the client hanging.
    ///
    /// VehicleProfile.ByName is memoised for the same reason. A cache here fixes the caller that has
    /// the problem without changing what a model means for anyone who reloads an authored one.
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

    /// <summary>The headroom a physical voice renders with, dB: its spec's own for water, fire,
    /// foliage and a struck bell, the shared one for everything else. Memoised for the same reason the level is.</summary>
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
                // A struck bell — a level crossing's gong. Without this the lookup returns null,
                // PhysicalLevel says false, and the emitter is dropped by BOTH the ranking and the
                // submit path: the bell is never ranked, never voiced, never heard. It rang
                // perfectly on the server and did not exist on the client, which from the pavement
                // is indistinguishable from a crossing that never closes.
                //
                // Its size is the bell itself. A gong is a quarter of a metre of bronze on a post
                // and there is no sense in which you can stand inside one.
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
            // Water, fire and the wind in a tree: nobody made them, and they are placed like a
            // machine all the same, at their declared level and their own size. A fountain's size
            // is its basin, a fire's its hearth, a tree's its crown.
            //
            // One TAP of a water feature ("water:<preset>/<feature>/<tap>", WaterFeatureVoice) is placed
            // by the WHOLE feature's level and its own landing place's size: its voice renders its own
            // share of the water against the whole's full scale, so the taps sum to the feature at
            // any distance, and near one tap that tap is a point you can walk up to.
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
                // What RADIATES is the disc or the nozzle, not the airframe: a listener is never
                // inside an aeroplane's extent anyway, so the number only has to stop the inverse
                // law running away at the one distance it could — directly underneath.
                //
                // Unless there is MORE THAN ONE of them, which for everything with a jet on it
                // there is. A twin's two engines are eleven and a half metres apart under the
                // wings, and that separation is the source's size in exactly the sense a bus's
                // nose-to-tail is: walking a metre towards one walks you a metre away from the
                // other, so the level is flat across the span and the far field is unchanged.
                // Declared as AircraftProfile.EngineSpanMetres, not inferred from the wings.
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

    /// <summary>
    /// The power lever, and a rotor's wake, from what the aircraft is DOING.
    ///
    /// Nothing scripts this and nothing on the wire carries it. An aeroplane climbing is at or near
    /// full power; one holding height is at cruise, which is well under it; one coming down is at
    /// idle with the air doing the work. That is the whole difference between an airliner going over
    /// and the same airliner on approach, and reading it off the climb angle means a map that
    /// declares a descending flight path gets an aeroplane on approach without saying so.
    ///
    /// A helicopter slaps when it is descending into its own downwash or moving fast forward, and
    /// nothing else makes it slap — so that comes from the same two numbers.
    /// </summary>
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

    private static (float Lever, float Wake) FlightPower(Vector3 velocity)
    {
        float speed = velocity.Length();
        if (speed < 0.5f) return (0.25f, 0f);                  // sitting on the apron at idle
        float climb = velocity.Y / speed;                      // sine of the flight path angle
        // Full power by about six degrees up, cruise level, idle by about four degrees down. The
        // asymmetry is real: a climb needs everything the engines have and a descent needs nothing,
        // so the lever falls away much faster than it rises.
        float lever = climb >= 0f
            ? Math.Clamp(0.62f + climb * 3.6f, 0f, 1f)
            : Math.Clamp(0.62f + climb * 8.0f, 0.06f, 1f);
        float wake = Math.Clamp(-climb * 6f, 0f, 1f) * 0.7f
                   + Math.Clamp((speed - 25f) / 45f, 0f, 1f) * 0.3f;
        return (lever, Math.Clamp(wake, 0f, 1f));
    }

    /// <summary>
    /// Is this aeroplane on its wheels?
    ///
    /// Read off the same two things everything else about a flight is — where it is and what it is
    /// doing — and not scripted. An aeroplane whose belly is within a fraction of its own length of
    /// the ground is on the runway; one higher than that is flying. The TRANSITION into it is the
    /// touchdown, and the voice turns that into spinning wheels up from rest (see
    /// AircraftSynth.Touchdown). Nothing has to declare a landing, and a map that draws a flight
    /// path down to a runway gets one.
    ///
    /// The ground probe is a grid search, so it is asked only about an aeroplane that could
    /// plausibly be near the ground at all: one more than sixty metres over the listener's head is
    /// flying, and that is settled without touching the world.
    /// </summary>
    /// <summary>
    /// Where the listener's head is, for the wind at the ears: its place, how it moves, which way it
    /// faces, how much of the weather's wind reaches it, and what covers the ears.
    ///
    /// Exposure is one minus the enclosure the acoustic survey measures round the listener (the same
    /// sphere of rays that decides how much reverb there is), and nothing at all inside a room: a
    /// street between tall buildings takes a third off the wind, a walled yard half, a room all of
    /// it. It takes your own movement off with the weather's: indoors no wind at the ears at all.
    /// In a vehicle: a cabin lets in what its open windows let in, and a vehicle without one (a
    /// motorcycle, a formula car) has a helmet on the rider.
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
    /// What the body of the vehicle you are sitting in takes off everything outside it, dB per band.
    ///
    /// The same paths the interior engine voice uses, the other way round: the windows by their mass
    /// (transmission falls as rho*c / (pi*f*m)), the seals, which have no mass and let a little of
    /// everything through, and whatever is open (CarWindow.CabinLossDb). At 150 Hz a hatchback's glass
    /// passes about a hundredth of the power; by a kilohertz the seals are most of what gets in, so the
    /// top end sits about thirty decibels down — which is the whole of "the traffic outside sounds like
    /// it is outside". With the windows down a tenth of the wall is a hole, and the street comes in at
    /// a tenth of its power, at every frequency.
    /// Nothing when you are on foot, or on something with no cabin: a motorcycle keeps the street.
    /// </summary>
    private (float Low, float Mid, float High) CabinEnclosure(WorldSnapshot world)
    {
        if (!_state.IsRiding || !world.Entities.TryGetValue(_state.RidingEntityId, out var ride)) return (0f, 0f, 0f);
        if (OpenFPS.Client.AudioEngine.Acoustics.CabinWalls.Vehicle(ride) is not { } vehicle) return (0f, 0f, 0f);
        // A bus at a stop with its doors open has a hole in its side: about 2.4 m^2 of doorway in a
        // hundred-odd m^2 of cabin wall, which lets the street in at a couple of per cent of its
        // power, at every frequency. The voice decides when the doors are open; ask it.
        float doorway = _audio.EngineDoorsOpen(_state.RidingEntityId) ? DoorwayPowerFraction : 0f;
        return OpenFPS.Client.AudioEngine.Acoustics.CabinWalls.LossDb(vehicle, _cabins.WindowsOpen(ride, _now()), doorway);
    }

    private bool OnTheWheels(EntitySnapshot snap, WorldSnapshot world, Vector3 eyePos)
    {
        var p = snap.Transform.Position;
        if (p.Y - eyePos.Y > 60f) return false;
        float groundY = OpenFPS.Common.PhysicsUtils.GetGroundHeight(world, p, snap.Id, out _);
        // Its own size sets how close counts: a jet sits five metres up on its gear and a light
        // single one, so the same fraction of length serves both without either being told.
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
            // Looked up once a sound id: this runs for every sink in every flat, every frame.
            if (!_flowSpecs.TryGetValue(soundId, out var spec))
                _flowSpecs[soundId] = spec = OpenFPS.Common.RunningWaterSpec.ByName(soundId[5..]);
            // A tap: heard while it is on, and for as long after as its basin takes to empty. A shut tap
            // nobody has opened since you arrived is an empty sink; a leaking one drips.
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

    private void ChooseLiveMachines(WorldSnapshot world, Vector3 eyePos)
    {
        double now = _now();
        _machineOrder.Clear();
        _machineGroups.Clear();
        // Trees the budget left without a voice last frame are heard in their wood (WoodChorus.Weigh).
        if (world.Woods != null) world.Woods.Weigh(eyePos, _woodWeights, _isLiveMachine ??= id => _liveMachines.Contains(id));
        else { _woodWeights.Individual.Clear(); _woodWeights.Woods.Clear(); }

        // A TRAIN IS ONE MACHINE. A light-rail set is up to nine taps — one per bogie and source
        // along it, all reading one shared synth. Ranked tap by tap against every air conditioner and
        // mower, its near taps would outrank the rest and its far ones fall below a window unit, so
        // taps would drop and re-admit all the way along as it passed: a train cutting in and out
        // beside you. So a train is ranked by its loudest tap, takes ONE place, and its taps come and
        // go together.
        foreach (int entityId in world.AudioEntityIds)
        {
            if (entityId == OwnEntityId) continue;
            if (!world.Entities.TryGetValue(entityId, out var snap)) continue;
            var em = snap.Definition.SoundEmitter;
            if (!em.IsSynth || em.SoundId == null) continue;
            if (!PhysicalLevel(em.SoundId, out float levelDb, out float extent)) continue;
            // A gutter, a drain or a downpipe with no rain running off into it is not there to be heard.
            if (Dry(em.SoundId, entityId, em.SynthRunning, now)) continue;
            // A train's horn or bell that nobody is sounding.
            if (SilentSignal(em.SoundId, now)) continue;
            // A tree past the hand-over is heard in its wood, and a wood with no trees in it now is not
            // heard; ranked by what each plays (a wood's trees in power, so its amplitude by their root).
            float chorus = ChorusShare(entityId, out float chorusTrees);
            if (chorus <= 0f || chorusTrees <= 1e-3f) continue;
            chorus *= MathF.Sqrt(chorusTrees);

            float d = Vector3.Distance(OpenFPS.Common.AudioEmission.PointFor(snap), eyePos);
            var (gain, reference) = OpenFPS.Common.Loudness.Place(levelDb, extent);
            float range = MathF.Max(em.Range, OpenFPS.Common.Loudness.AudibleRange(levelDb));
            // Ranked in loudness: the law's correction for what this machine is made of, once heard.
            gain *= MathF.Pow(10f, OpenFPS.Client.AudioEngine.Core.EarTimbres.CorrectionDb(em.SoundId, levelDb) / 20f);
            float level = OpenFPS.Common.Loudness.RenderedGain(gain * em.Volume * chorus, reference, range, d);

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
            // Louder sorts first, so the key is negated. The hold and the keep bias work on the key
            // exactly as they do for a car — a machine that already has a voice has to be beaten
            // decisively, not merely matched.
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
                // Same reason as an engine: building one is a set of waveguides and resonators, and
                // a map load presents all of them in the same instant. Counted per MACHINE: a train's
                // taps share one synth and arrive together.
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
    /// Decides which cars get their own engine, and which borrow one.
    ///
    /// Every car in earshot gets its OWN engine, because the engines do not run inside the mixer
    /// callback — a worker pool renders them ahead across all the machine's cores (see
    /// EngineRenderPool), so no single thread has to integrate every engine before a deadline.
    ///
    /// The budget is a SAFETY NET. It follows measured mixer load, and what it protects against is
    /// not the synthesis but the placement: thirty cars is still thirty spatialised
    /// voices with HRTF and filtering, and that cost is the mixer's. If it ever runs short the order
    /// of sacrifice is reflections, then borrowed voices, then engines — never the cars first.
    /// </summary>
    /// <summary>
    /// What the engines' reflections cost the game thread, milliseconds a pass: this pass's running total
    /// and a smoothed average of the passes before it. The budget below watched the mixer and the engine
    /// threads and never this, so a machine whose mixer had room ran two reflections for every one of
    /// thirty-two cars and spent 42 ms of each pass finding them: its game loop fell to 22 Hz and every
    /// sound sat a tenth of a second behind what was making it (Sean's client, 2026-10-04). Over
    /// <see cref="EchoPassCeilingMs"/> the reflections give way like anything else in the budget.
    /// </summary>
    private double _echoPassMs, _echoMsSmoothed;
    private const double EchoPassCeilingMs = 6.0;

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

        // THE PRODUCERS, as well as the mixer. Engines render on EngineRenderPool's threads, and when
        // those cannot keep up a voice STARVES — its block is ramped to silence — which the mixer's
        // load never sees. On the city a pool a core or two short gives 50-190 starves a second, and
        // the car nearest you comes out chopped. Starving gives up machines and then cars, the same
        // way an overloaded mixer does; reflections and front taps cost the producers nothing.
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
                // The places of extended sources go first, a source's worth at a time, and then a
                // machine's second outlet: the only voices whose loss costs nothing but geometry —
                // the source stays exactly as loud, because what they carried slews back into the
                // voice that is still playing.
                if (_adaptivePlaces > 0) _adaptivePlaces = Math.Max(0, _adaptivePlaces - PlaceBudgetStep);
                else if (_adaptiveFront > 0) _adaptiveFront--;
                // Then a standing machine, before a reflection. A machine that drops out is one
                // fewer air conditioner in a street of forty and is not missed; a car's first
                // reflection is the wall of the building you are walking beside.
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
            // A car that already has an engine keeps it unless a silent one is decisively nearer;
            // and one that has only just been given an engine is not taken off it at once. Both stop
            // the set churning as cars trade places, which on a full grid happens constantly.
            float key = _liveEngines.Contains(entityId) ? d2 * (EngineKeepBias * EngineKeepBias) : d2;
            if (_engineStarted.TryGetValue(entityId, out double began) && now - began < EngineMinimumHoldSeconds)
                key = -1f;
            // The one you are sitting in is never ranked out: it is the loudest thing in your world.
            if (entityId == _state.RidingEntityId) key = float.NegativeInfinity;
            _engineDistances.Add((entityId, key, d2));
        }
        _engineDistances.Sort((a, b) => a.Key.CompareTo(b.Key));

        int keep = Math.Min(_adaptiveBudget, _engineDistances.Count);

        // Cars that have lost their engine fade it out rather than being cut mid-waveform.
        foreach (int id in _liveEngines)
        {
            bool survives = false;
            for (int i = 0; i < keep; i++) if (_engineDistances[i].Id == id) { survives = true; break; }
            if (survives) continue;
            _engineEchoes.Forget(id, _audio);
            _engineStarted.Remove(id);
            if (!_engineRetiring.Contains(id)) _engineRetiring.Add(id);
        }
        for (int i = _engineRetiring.Count - 1; i >= 0; i--)
        {
            int id = _engineRetiring[i];
            bool wanted = false;
            for (int k = 0; k < keep; k++) if (_engineDistances[k].Id == id) { wanted = true; break; }
            if (wanted) { _engineRetiring.RemoveAt(i); continue; }
            if (_audio.FadeOutEngine(id)) { _audio.StopSound(id); _engineRetiring.RemoveAt(i); }
        }

        // New engines are let in a FEW AT A TIME.
        //
        // Building one is not free — a cylinder set, waveguides for every pipe, buffers for all of
        // it — and a map load presents thirty cars in the same instant. Thirty constructions at once
        // is an allocation spike on the thread that also services the mixer's queues, at the exact
        // moment the scene, the geometry and the sample banks are all loading too: dropouts for the
        // first second or two. Spread over a few frames it is inaudible: a car
        // that arrives a sixteenth of a second late is a car that arrived.
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
            // Whatever happened to it before, a car that holds a slot is audible. Cheap and
            // idempotent, and the only place that can undo a fade that was started and abandoned.
            _audio.ReviveEngine(id);
            if (!_engineStarted.ContainsKey(id)) _engineStarted[id] = now;
            _engineSourceByPreset.TryAdd(_carPreset[id], id);
            // Anything that has just earned a real engine gives its borrowed voice back.
            int lent = DistantVoiceBase - Math.Abs(id);
            if (_distantBoundTo.Remove(lent)) _audio.StopSound(lent);
        }

        // ── Every preset on the map keeps one live engine ──────────────────────────────────────
        //
        // A car outside the budget borrows the ring of the nearest car OF ITS OWN PRESET, and if
        // no car of that preset has a live engine there is nothing to borrow and the car is simply
        // SILENT (DistantEngine returns on exactly that). That is fine for a preset the map has
        // twenty of and fatal for one it has two of: the city carries two slip-on motorcycles, at
        // their closest approach the loudest thing on that street, and the moment both fall out of
        // the budget together they go silent.
        //
        // So the highest-ranked car of each distinct preset is admitted whatever the budget said.
        // It costs at most one engine per preset the map actually uses (eleven on the city, against
        // a budget of thirty-two) and it is what borrowing has always assumed was true.
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

        // Whatever is left over, nearest first, borrows — a fallback now rather than the normal case.
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

        // A census, every five seconds. "Are there really thirty cars out there?" is not a question
        // anybody should have to answer by counting engines with their ears, and the number that
        // matters is not the map's — it is how many of the map's cars this machine is currently
        // SYNTHESIZING, how many are borrowing a voice, and how many are past both budgets and
        // therefore genuinely silent.
        if (now - _lastCensus >= 5.0)
        {
            _lastCensus = now;
            int cars = _engineDistances.Count;
            // The list is sorted by the KEEP-BIASED key, not by distance, so [0] is not necessarily
            // the nearest car. Ask the distances.
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

            // The worst that every source in the world stood still for. Target is one 60 Hz period,
            // 17 ms; anything over about 100 ms is long enough to hear a car passing in front of you
            // stop dead and then carry on.
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

            // And what the three nearest engines are actually DOING, which is the only way to tell
            // apart the four things that sound identical from a chair: the cars really are slowing
            // (an oval makes them lift twice a lap), the world is reporting a speed that is too low,
            // the virtual driver is not holding the speed it was given, or it is shifting up. If
            // "told" is steady and "own" or "rpm" sags, the fault is in the synthesis; if "told"
            // itself falls, the car is genuinely slowing and the audio is right.
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

            // What actually reaches you loudest, and by which route: the answer to "why can I still
            // hear that bus two streets over". Levels are dB full scale at the mixer, with distance,
            // occlusion, air absorption, shelter and cone applied: the loudest band first, then each
            // of low, mid and high. "round an edge" means the bearing has been moved to where the
            // sound bends round something.
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

    /// <summary>Each entity voice's own volume and reference distance as last submitted: what its walls' copies are
    /// placed against.</summary>
    private readonly Dictionary<int, (float Volume, float MinDistance)> _placed = new();

    /// <summary>
    /// A wall's copy of a recorded loop, as loud as the wall sends it back: the copy law at the loop's own reference
    /// distance (EarlyReflections.PlacedCopyGain), so the copy's extra spreading is counted once, by the renderer at the
    /// image; and its colour against its middle band, so the middle is counted once, in its volume.
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
    private readonly HashSet<int> _liveEngines = new();
    private readonly Dictionary<int, double> _engineStarted = new();
    private readonly List<int> _engineRetiring = new();

    /// <summary>
    /// Which machines are close enough for their two ends to be heard as two ends.
    ///
    /// The test is geometric and nothing about it knows what a car is: how far apart this machine's
    /// outlets are, and what angle that separation subtends from here (see Localisation). A car is
    /// three and a half metres from airbox to tailpipe, so it separates inside about twenty metres; a
    /// motorcycle is one metre and separates inside six; an airliner would separate from half a
    /// kilometre away. Nothing had to be authored for any of those.
    ///
    /// It costs one extra voice and NO extra synthesis — the engine is integrated once and its two
    /// outlets are written to their own taps — and the machine's level is identical either way, so
    /// crossing the threshold is a change in where the sound comes from and not in how much of it
    /// there is.
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

        // Anything that had a front voice and no longer wants one gives it back — faded, not cut:
        // the tap is a running waveform like the engine it comes from.
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

    /// <summary>How far apart a machine's outlets are, metres. Memoised: it is a property of the
    /// machine, asked once per car per frame.</summary>
    private static float OutletSeparation(string preset)
    {
        if (_outletSeparation.TryGetValue(preset, out float cached)) return cached;
        var v = OpenFPS.Common.MachineRegistry.VehicleFor(preset);
        float sep = Vector3.Distance(ExhaustSlot(v), IntakeSlot(v));
        _outletSeparation[preset] = sep;
        return sep;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, float> _outletSeparation = new();

    /// <summary>Where the gas leaves, in the machine's own frame — the TRUE tailpipe, not the
    /// compromise position a single voice sits at (VehicleProfile.ExhaustEmitterBias).</summary>
    private static Vector3 ExhaustSlot(OpenFPS.Common.VehicleProfile v) => v.ExhaustSlot;

    /// <summary>...and the other end: where it breathes, or its nose when the engine is in the back
    /// (VehicleProfile.FrontTapZ).</summary>
    private static Vector3 IntakeSlot(OpenFPS.Common.VehicleProfile v)
        => new(0f, v.FrontTapHeight, v.FrontTapZ);

    /// <summary>
    /// The front outlet of a machine, placed and kept up to date.
    ///
    /// It carries no engine of its own: the voice reads the front tap of the entity's live engine
    /// (EngineTapState), so it costs a buffer read and an HRTF. Everything about it that is not the
    /// position is the same as the machine's other voice, because it is the same machine — the same
    /// level reference, the same range, the same region, the same sampled time.
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
            // The SAME level reference as the exhaust voice. The two taps already carry the
            // difference between what an intake radiates and what a tailpipe does — that is what the
            // synthesis is for — and applying a second, guessed difference on top of it here would
            // be the taste constant this design is trying not to have.
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

    /// <summary>Called once a frame: the cabin's voices go when you are no longer sitting in that
    /// vehicle, or its engine has no voice. Faded, then stopped.</summary>
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

    /// <summary>Voice ids for the outer places of an extended source: <see cref="ExtendedSources.MaxPlaces"/> a
    /// source, place 1 up. Eight a source until 2026-10-06, when a surf beach had ten places and its ninth and
    /// tenth took the next source's first two ids.</summary>
    internal const int PlaceVoiceBase = -5_000_000;
    internal const int PlaceIdsPerSource = OpenFPS.Client.AudioEngine.Core.Nature.ExtendedSources.MaxPlaces;
    internal static int PlaceVoiceId(int sourceId, int place) => PlaceVoiceBase - Math.Abs(sourceId) * PlaceIdsPerSource - place;

    /// <summary>After a source merges to its middle, its outer voices are kept this long, s: what was
    /// already written to them (a twig's clatter, a crackle's rattle, up to the render lead ahead) rings
    /// out rather than being cut.</summary>
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
    /// Every siren on the map, placed every frame, whatever its car's engine is doing.
    ///
    /// Not from inside the car's own emitter pass, which only runs for a car whose ENGINE won a voice:
    /// a siren is thirty-five decibels louder than the engine under it, so it is exactly the sound
    /// that must not depend on that. Placed from there, a patrol car that dropped out of the engine
    /// budget would leave its siren wailing where the car had been, with nothing to stop it.
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
    /// The path for a horn or a siren on a vehicle: the occlusion worker's answer for the vehicle. A
    /// vehicle whose engine did not win a voice is not asked about by the per-voice pass, so it is asked
    /// about here; and until an answer comes the one-shots' path stands in, which has the same walls and
    /// the same routes by the openings. "No answer" is never "nothing in the way": that played a horn
    /// behind three buildings at full level until the vehicle happened to be asked about.
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
    /// The path for a sounding entity the occlusion worker has no answer for yet — a car that has just
    /// won a live voice, whose last answer was dropped while nothing asked about it. It was built as an
    /// unoccluded line, and the voice was STARTED with it: a car behind a building came in at full
    /// level for the fifth of a second until the worker answered, and then eased down (2026-10-03).
    /// The worker's answer for a source it heard near this one a moment ago, moved to this one; failing
    /// that, the one-shots' path, which has the same walls and the same routes by the openings. And the
    /// worker is asked now, so the next frame has its own.
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

    /// <summary>Trains sounding their horn or bell ("preset/train"), until when. A train's horn,
    /// whistle and bell sources are given a voice only while it is: silent, they are nothing to hear,
    /// and ranked at a horn's 139 dB they would hold a voice for the whole train from kilometres off.</summary>
    private readonly Dictionary<string, double> _trainSignals = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A train sounds for a crossing. Its synth plays it on the train's own horn (or whistle) and bell
    /// (TrainSignal); this keeps the signal sources ranked while it lasts. A synth that does not exist
    /// yet takes the signal when it is made, the time already gone counted off.
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
    /// Somebody on the street sounded their horn. The voice is built on the next frame, on the
    /// vehicle, where it stays for as long as the rhythm lasts. A second honk from the same car while
    /// the first is still going replaces it — one horn, one hand.
    /// </summary>
    private void StartHorn(int entityId, string horn, float[] rhythm)
    {
        int voiceId = HornVoiceBase - Math.Abs(entityId);
        if (_horns.Remove(entityId)) _audio.StopSound(voiceId);
        _horns[entityId] = (OpenFPS.Common.Honk.Key(horn, rhythm),
                            OpenFPS.Common.AudioClock.Now + OpenFPS.Common.Honk.Duration(rhythm) + 1.0);
    }

    /// <summary>
    /// Places every horn that is sounding, at the front of its vehicle, through the vehicle's own
    /// acoustic path — behind a building, a horn is behind the building too.
    /// </summary>
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
                // Heard from the horn itself, where the car is NOW — only turned toward the edge the
                // path goes round when something is in the way. Not the path's own point, which is
                // the car's exhaust as of the worker's last answer (up to ten frames old at range):
                // a moving car's horn would trail behind it, and come from its tailpipe.
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
    // A level crossing's rails, from the map's roads (MapRoads). Every vehicle with a voice of its own
    // that is about to roll a wheel over one has the strike scheduled in its voice, at the moment the
    // wheel reaches the rail (WheelStrikes): front wheels, a rail-gauge later the second rail, a
    // wheelbase later the back wheels. Scheduled once a wheel is under a second from a rail, which is
    // longer than any voice renders ahead; let go once it is well past, so the next pass strikes again.

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
    /// The horn of every vehicle whose driver is holding H (EntityState.Signals): the vehicle's own horn
    /// model (VehicleProfile.HornFor) blowing for as long as the wire says, at the grille. Let go, the
    /// voice is told to stop and the model's own valve or relay ends the note; the voice goes a moment
    /// later.
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
    /// The siren on a vehicle that carries one — its own voice at its own level, at the grille.
    ///
    /// Everything about it is separate from the engine's voice except where it is, and that is the
    /// point: a siren head is 130 dB at a metre where the car is 95, so it gets its own placement
    /// and its own full-scale reference. Sharing the engine's would either square the siren or
    /// bury the car. See SirenVoiceState.
    ///
    /// WHETHER IT IS SOUNDING is not decided here and is not scripted. A patrol car with its
    /// lights on is one that is going somewhere, and on a track that is a car running above the
    /// speed the rest of the traffic keeps — so the mode is read off what the car is DOING, the
    /// same way an aeroplane's power lever is read off its climb angle. Standing still or rolling
    /// with the traffic: off. Moving with purpose: wail. Hard on the brakes into a junction: yelp,
    /// which is what a real crew switches to, because a fast sweep is far easier to place.
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
            // Which way the horn points. Without it the machine frame falls back to the VELOCITY,
            // which is the right answer while the car is moving and no answer at all when it slows
            // for a junction — exactly when a siren matters most. The car's own rotation always
            // knows.
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
    /// How far ahead of a vehicle's middle its grille is, metres: a quarter of a metre in from the nose
    /// of the body its profile declares. Where the horns and a siren head are mounted. A vehicle the
    /// client cannot name is taken as a car, whose grille is 1.9 m ahead of its middle.
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
    /// Where a siren is heard from: its own head, unless the car's path says the sound arrives round
    /// something, in which case from that bearing at the head's distance.
    ///
    /// ONE answer, used by both things that place the voice: the siren's own update and the car's
    /// acoustic path. With two answers (the grille, and the exhaust as of the last worker request or
    /// the edge a blocked source is redirected to) the audio thread applies whichever arrived last,
    /// and the image swings between two bearings every frame — 3.6 degrees for a car crossing 100 m
    /// out in the open, the whole redirection behind a building. A far siren then flutters.
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

    /// <summary>One mode decision per vehicle, kept between frames because the decision has
    /// state in it — see SirenController.</summary>
    private readonly Dictionary<int, (OpenFPS.Common.SirenController C, double At)> _sirenControl = new();

    /// <summary>
    /// What a patrol car's siren is doing, from what the car is doing. Nothing on the wire carries
    /// it and nothing scripts it, which is the same rule the power lever and the air brakes follow.
    ///
    /// The decision itself lives in <see cref="OpenFPS.Common.SirenController"/> rather than here,
    /// because it has memory and hysteresis in it and a thing with memory is a thing a test can
    /// drive. The instantaneous deceleration alone would flip between wail and yelp at every corner
    /// of a city lap.
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
    /// A car too far away to be worth its own engine, voiced by BORROWING one that is near.
    ///
    /// This is what stops the number of cars a map may carry from being decided by the mixer. A full
    /// engine is cylinders, valves and waveguides integrated per sample — about a tenth of a core —
    /// so simulating fifty of them is five cores, and the answer is not a bigger machine. It is that
    /// past a certain distance nobody can tell one engine from another of the same kind: what reaches
    /// you is the pack.
    ///
    /// So a distant car reads the ring buffer of the NEAREST car of its own preset, at a fixed offset
    /// of its own, and is placed at its own position with its own velocity. It costs a buffer read
    /// and an HRTF, not an engine. Its revs are the lead car's rather than its own, which at two
    /// hundred metres is a difference no listener has ever been able to name — and it means the
    /// field keeps circulating instead of cars dropping out of the world as they go round the back.
    ///
    /// The offset per car matters: without it, every borrowed voice would be the same waveform at the
    /// same instant and they would sum coherently into one loud car rather than spreading into traffic.
    /// </summary>
    private void DistantEngine(EntitySnapshot snap, OpenFPS.Common.Networking.EntityDefinition def, string preset, double sampledAt)
    {
        if (!_distantVoiced.Contains(snap.Id)) return;
        if (!_engineSourceByPreset.TryGetValue(preset, out int sourceId)) return;
        if (sourceId == snap.Id) return;

        int voiceId = DistantVoiceBase - Math.Abs(snap.Id);

        // Rebind if the car it was borrowing from has itself gone: the ring buffer it was reading no
        // longer exists, and a voice pointed at a dead source is silence that never recovers.
        if (_distantBoundTo.TryGetValue(voiceId, out int bound) && bound != sourceId)
            _audio.StopSound(voiceId);
        _distantBoundTo[voiceId] = sourceId;

        var profile = OpenFPS.Common.MachineRegistry.VehicleFor(preset);
        float level = profile.SourceLevelDb > 0f ? profile.SourceLevelDb : EngineSourceLevelDb;
        // The same machine, the same size: a car does not become a point because it is far away and
        // borrowing somebody else's engine.
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
            // Its OWN velocity, so it Dopplers as itself rather than as the car it borrowed from.
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

    // ── How hard the road is working a moving thing's tyres ─────────────────────────────────────
    //
    // Derived from its OWN MOTION, which is the only way this can be general. Nothing here knows what
    // a corner is, or that this map has a track on it: a source whose velocity is changing direction
    // is turning, a source whose speed is changing is accelerating or braking, and the two combine as
    // a vector because a tyre has one friction budget to spend on both. A car, a bus, a runaway
    // trolley and a player-driven vehicle all get it on the same terms, and so will whatever the next
    // map has on it.
    //
    // The lateral term is the interesting one and it needs no curvature, no racing line and no server
    // support: if the velocity vector rotated by an angle in a time, the centripetal acceleration is
    // speed times that rate. That is the definition, and it is true of anything that moves.
    private readonly Dictionary<int, (Vector3 Velocity, double At)> _motionHistory = new();
    private readonly Dictionary<int, float> _tyreDemand = new();

    /// <summary>
    /// The fraction of its tyres' grip an entity is currently using, from two successive samples of
    /// its velocity.
    ///
    /// Held between calls rather than recomputed, because the position is only new thirty times a
    /// second and differentiating the same pair twice would halve the answer. Smoothed on the way out
    /// for the same reason the provider smooths anything else: a 30 Hz staircase in a level is as
    /// audible as one in a pitch.
    /// </summary>
    private float TyreDemand(EntitySnapshot snap, double sampledAt, float gripG)
    {
        float previous = _tyreDemand.GetValueOrDefault(snap.Id, 0f);
        if (_motionHistory.TryGetValue(snap.Id, out var last))
        {
            double dt = sampledAt - last.At;
            // Only when the sample is genuinely new. Asking twice about one pair of snapshots would
            // compute an acceleration from no elapsed time.
            if (dt > 1e-3)
            {
                Vector3 v = snap.Velocity;
                Vector3 a = (v - last.Velocity) / (float)dt;
                float speed = v.Length();
                float aLong = 0f, aLat = a.Length();
                if (speed > 0.5f)
                {
                    Vector3 along = v / speed;
                    aLong = Vector3.Dot(a, along);
                    aLat = (a - along * aLong).Length();
                }
                float demand = OpenFPS.Common.TyreFriction.Demand(aLong, aLat, gripG);
                // Fast to rise, slow to fall — a tyre lets go on the instant and settles over a
                // couple of hundred milliseconds. Matches the DSP's own smoothing of the same number.
                previous += (demand - previous) * (demand > previous ? 0.5f : 0.12f);
                _tyreDemand[snap.Id] = previous;
                _motionHistory[snap.Id] = (v, sampledAt);
            }
        }
        else _motionHistory[snap.Id] = (snap.Velocity, sampledAt);
        return previous;
    }

    private void ProcessAudioEmitter(WorldSnapshot world, EntitySnapshot snap, Vector3 eyePos, float engineDt = 0f)
    {
        double now = _now();
        var def = snap.Definition;

        // A physical model outside the budget is not heard, so it is not WORKED OUT either.
        //
        // This bails before the acoustic path is read, and that is the whole point of where it sits.
        // A city carries a hundred and twenty-six of these and ten of them get a voice; doing the
        // occlusion, the diffraction and the placement for the other hundred and sixteen every
        // frame, to throw all of it away at the branch that builds the emitter, is a hundred and
        // sixteen sources' worth of work per frame for silence. The ranking in ChooseLiveMachines has already decided, on the measure that
        // matters, and it ran this frame.
        if (def.SoundEmitter.IsSynth
            && def.SoundEmitter.SoundId is { } sid
            && PhysicalLevel(sid, out _, out _)
            && !_liveMachines.Contains(snap.Id))
            return;

        // Use the async worker's last computed result rather than a synchronous per-frame calculation.
        // Until it has one, the answer it gave a moment ago for a source near this one, or failing that
        // the one-shots' path (FirstAnswer). Never "nothing in the way".
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
        // Where the sound comes out. One shared answer, so the voice and the occlusion probe in step 5
        // can never again be asking about two different points in space.
        Vector3 emitterPosition = OpenFPS.Common.AudioEmission.PointFor(snap);
        float engineVolume = def.SoundEmitter.Volume;
        float engineMinDistance = def.SoundEmitter.MinDistance;
        float engineRange = def.SoundEmitter.Range;
        float engineExtent = def.SoundEmitter.ExtentMetres;
        // A tree or a fire: the places it is heard from across its extent (ExtendedSources).
        Vector3[]? extentLayout = null;
        float chorusTrees = 1f;
        // The declared level the voice is placed by, for the ear model; an authored source with only a
        // volume has none.
        float earLevel = 0f;
        // An authored source with a SIZE — a fountain, a grille, a waterfall. Same rule as a machine:
        // the reference widens to the thing's own radius and the gain is paid down to match, so the
        // far field is unchanged and only the near field goes flat.
        if (engineExtent > 0f && !def.SoundEmitter.IsSynth)
            (engineVolume, engineMinDistance) =
                OpenFPS.Common.Loudness.Widen(engineVolume, engineMinDistance, engineExtent);
        if (def.SoundEmitter.IsSynth)
        {
            resolvedSoundId = def.SoundEmitter.SoundId;
            if (string.IsNullOrEmpty(resolvedSoundId)) resolvedSoundId = "SYNTH"; // Last resort dummy
            // Asked of the SAME lookup the ranking uses, not a second list of prefixes. There were
            // two lists: LookUpPhysicalLevel learned "bell:" and this one did not, so the crossing
            // bell was ranked, won a voice, and then fell through to here as a nameless synth with
            // no physical key — placed every frame, rendered by nothing. Every kind the lookup
            // knows is a physical voice, and nothing else is.
            if (PhysicalLevel(resolvedSoundId, out _, out _))
            {
                // A physical model that is not a vehicle. Unlike a car it has no borrowed-voice
                // fallback: one outside the budget is simply not heard, because there is no sense in
                // which forty air conditioners are one air conditioner heard from further away —
                // that is what aggregation will be for, and this is not it.
                if (!_liveMachines.Contains(snap.Id)) return;
                // The same memoised numbers the ranking used. Building a fresh spec here as well
                // would be the same fault at a tenth of the scale, and would also let the two
                // disagree if a model were ever reloaded between the two calls.
                if (!PhysicalLevel(resolvedSoundId, out float levelDb, out float extent)) return;
                physicalKey = resolvedSoundId;
                // A stretch of shore carries its own geometry, its fetch and which way its water lies, in
                // its box (tools/gen_osm.py): the voice reads it from the key.
                if (physicalKey.StartsWith("shore:", StringComparison.OrdinalIgnoreCase))
                    physicalKey = OpenFPS.Common.ShoreSpec.KeyFor(physicalKey, def.Collider.Size, snap.Transform.Rotation);
                // Placed on its own declared level and its own size, the same way a vehicle is.
                // The extent is what stops a window unit being a point source you can walk into:
                // inside its own half-metre the level is flat, and the gain is paid down to match so
                // the far field is unchanged. See Loudness.Widen — widening without paying is how
                // the engine once handed every quiet vehicle eight decibels it had not earned.
                var (gain, reference) = OpenFPS.Common.Loudness.Place(levelDb, extent);
                // A voice that renders with more room than the shared headroom — a fire's crackles —
                // gets the difference back here, so it is placed by its level and not its peaks.
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
                    onGround = OnTheWheels(snap, world, eyePos);
                    // On its wheels the climb angle says nothing; what it is doing does. Read off the
                    // speed's change, the same way a train's notch is.
                    float groundSpeed = snap.Velocity.Length();
                    float accel = 0f;
                    if (_lastAirSpeed.TryGetValue(snap.Id, out var was) && world.PositionsSampledAt > was.At)
                        accel = (groundSpeed - was.Speed) / (float)Math.Max(0.02, world.PositionsSampledAt - was.At);
                    _lastAirSpeed[snap.Id] = (groundSpeed, world.PositionsSampledAt);
                    if (onGround) powerLever = GroundPower(groundSpeed, accel);
                }
                else if (physicalKey.StartsWith("rail:", StringComparison.OrdinalIgnoreCase))
                {
                    // A train's speed is the bogie's speed, and its notch is what the speed is
                    // doing: pulling away is full effort, holding speed is a little, braking is none.
                    // The lever carries the notch as a fraction of eight; the wake slot carries the
                    // speed itself, which is what the rolling noise is made from.
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
                // A vehicle: the engine runs live in the mixer and follows the entity's speed. The
                // voice sits toward the tailpipe, since that is where most of the sound comes from,
                // and it is placed at the level a loud exhaust really has.
                // Its own engine if it has one; a borrowed voice if the mixer is short. See DistantEngine.
                if (!_liveEngines.Contains(snap.Id))
                {
                    DistantEngine(snap, def, resolvedSoundId[7..], world.PositionsSampledAt);
                    return;
                }
                engineKey = resolvedSoundId[7..];
                var profile = OpenFPS.Common.MachineRegistry.VehicleFor(engineKey);
                // This car's own measured level, not one number for every car. A stock car is
                // fourteen decibels over a road car and a diesel pickup thirty under it.
                float level = profile.SourceLevelDb > 0f ? profile.SourceLevelDb : EngineSourceLevelDb;
                // How big the machine is, acoustically: the distance between the ends it radiates
                // from. A car is three and a half metres of machine, a bus is ten, a motorcycle is
                // one — and inside that the level is flat, because walking a metre nearer the intake
                // walks you a metre further from the exhaust.
                //
                // This replaces `MathF.Max(reference, 3f)`, which widened the reference by hand and
                // paid nothing back for it. That is not an extended source, it is a louder one: it
                // was worth up to eight decibels to a quiet vehicle, which is most of the measured
                // 3.6 dB error in the crowd-against-motorcycle balance.
                float extent = OutletSeparation(engineKey);
                var (gain, reference) = OpenFPS.Common.Loudness.Place(level, extent);
                engineVolume = gain * def.SoundEmitter.Volume;
                engineMinDistance = reference;
                engineExtent = extent;
                engineRange = MathF.Max(engineRange, OpenFPS.Common.Loudness.AudibleRange(level));
                earLevel = level;

                // Close enough to hear which end is which: this voice moves back to the TAILPIPE and
                // the front of the machine gets a voice of its own at the airbox. Further away the
                // voice stays where it has always been — between the two, biased toward the exhaust
                // (VehicleProfile.ExhaustEmitterBias) — because that is the honest position for a
                // machine being heard as one thing.
                //
                // And once the two ends are two voices, each is placed as the POINT it is. The extent
                // above stands in for "a metre nearer the intake is a metre further from the exhaust"
                // while the machine is one voice; with a voice at each end that geometry is modelled
                // outright, and widening each of them as well counted the car's length twice: inside
                // its 3.3 m a hatchback's tailpipe stopped getting louder as you approached, about
                // 4 dB short at 2 m and 9 dB at 1 m. Beyond the extent the two placements are the same
                // (Widen holds gain times reference), so the switch between them is seamless.
                if (_frontVoiced.Contains(snap.Id))
                {
                    emitterPosition = snap.Transform.Position
                                    + Vector3.Transform(ExhaustSlot(profile), snap.Transform.Rotation);
                    (gain, reference) = OpenFPS.Common.Loudness.Place(level);
                    engineVolume = gain * def.SoundEmitter.Volume;
                    engineMinDistance = reference;
                    engineExtent = 0f;
                }

                // ...unless you are SITTING in it. Then there is no distance and no direction to
                // speak of: the whole machine arrives through the floor and the firewall, a little
                // ahead of you and below, and the voice renders what gets through the body
                // (EngineVoiceState.Interior). Its level is already pressure at the ear, so it is
                // placed unwidened and played at the gain its reference distance would have had.
                if (snap.Id == _state.RidingEntityId)
                {
                    interior = true;
                    var (g0, r0) = OpenFPS.Common.Loudness.Place(level);
                    engineVolume = MathF.Min(1f, g0 * r0) * def.SoundEmitter.Volume;
                    engineMinDistance = 1f;
                    engineExtent = 0f;
                    // ...and from where each way in IS, if it has a cabin (CabinPaths): this voice is the
                    // bulkhead, and every other path gets a voice of its own below.
                    cabinLayout = OpenFPS.Client.AudioEngine.Core.Engine.CabinPaths.For(profile);
                    cabinEar = Vector3.Transform(eyePos - snap.Transform.Position, Quaternion.Inverse(snap.Transform.Rotation));
                }
            }
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
            // Spin-up / spin-down sounds. VoiceManager has always known how to play these; nothing ever
            // handed them to it, so an authored StartSoundId was dropped between the prefab and the voice.
            StartSoundId = def.SoundEmitter.StartSoundId ?? "",
            StopSoundId = def.SoundEmitter.StopSoundId ?? "",
            Mode = def.SoundEmitter.Mode,
            Position = emitterPosition,
            ApparentPosition = engineKey.Length > 0 || physicalKey.Length > 0
                             ? emitterPosition : acousticPath.ApparentPosition,
            // Inside, the body IS the occluder, and the voice has already rendered what gets through
            // it — the acoustic path would count the same panels twice.
            EffectiveDistance = interior ? 0.7f : acousticPath.EffectiveDistance,
            Occlusion = interior ? 0f : acousticPath.Occlusion,
            EqLow = interior ? 1f : acousticPath.EqLow, EqMid = interior ? 1f : acousticPath.EqMid, EqHigh = interior ? 1f : acousticPath.EqHigh,
            AirLowDb = interior ? 0f : acousticPath.AirLowDb, AirMidDb = interior ? 0f : acousticPath.AirMidDb, AirHighDb = interior ? 0f : acousticPath.AirHighDb,
            ApertureFactor = interior ? 1f : acousticPath.ApertureFactor,
            TransmissionBleed = interior ? 0f : acousticPath.TransmissionBleed,
            Velocity = snap.Velocity,
            PositionSampledAt = world.PositionsSampledAt,
            // The emitter aims along its own LOCAL direction, rotated into the world by the entity's
            // rotation. Zero (the default) means "straight ahead".
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
            // Inside, its room is yours — the cabin — whatever room the car's middle is in.
            TargetRegionId = interior ? _listenerRegion : acousticPath.RegionId,
            ConeInside = def.SoundEmitter.ConeInsideAngle,
            ConeOutside = def.SoundEmitter.ConeOutsideAngle,
            ConeOutsideVolume = def.SoundEmitter.ConeOutsideVolume,
            MinDistance = engineMinDistance,
            ExtentMetres = engineExtent,
            EngineKey = engineKey,
            Interior = interior,
            // Inside, the voice rides with your head, just ahead and below: where the firewall and
            // the floor are. Turned with the car, so the engine stays in front of you through a corner.
            FollowsListener = interior,
            // With a cabin, the voice is the bulkhead, from where the firewall is.
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
            // Whether a synthesised source is SOUNDING. Almost everything in this world decides
            // that for itself from what the client can observe — an engine from its speed, a siren
            // from the car's behaviour, an aeroplane's power from its climb angle. A level
            // crossing's bell cannot: it rings because of where a train is on a line the listener
            // may be a kilometre from. So that one comes down the wire, and it defaults to true for
            // every other emitter.
            EngineRunning = def.SoundEmitter.SynthRunning,
            ServingStop = def.SoundEmitter.ServingStop,
            // How far down its windows are: sitting in it, the outside comes in through them.
            WindowsOpen = _cabins.WindowsOpen(snap, _now()),
            // Straight from the server, which is the only thing that knows the corner.
            //
            // Not differentiated here from the interpolated velocity and divided by the tyre's
            // FLAT-ground grip: a banked corner is indistinguishable from a flat one in a velocity,
            // because the bank shows up in the normal load and not in the kinematics. On the speedway
            // that reads 1.43 to 1.59 against a full-slide threshold of 1.45, so every car in every
            // corner would render pure broadband skid, a white-noise tail travelling with the field.
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

        // ── A repeating one-shot: submitted only when its interval comes round ───────────────
        //
        // Any emitter may carry RepeatIntervalSeconds. It is not a mode of playback so much as a
        // decision about WHEN to ask for one: the emitter is built as normal and then simply not
        // handed over until the clock says so, which means everything else about it — placement,
        // occlusion, reverb, the acoustic path — is whatever that emitter would always have got.
        // A PA announcing a racetrack, a foghorn and a station bell are the same object.
        float repeat = def.SoundEmitter.RepeatIntervalSeconds;
        if (repeat > 0f)
        {
            double due = _repeatDue.GetValueOrDefault(snap.Id, double.NegativeInfinity);
            if (double.IsNegativeInfinity(due))
            {
                // First sight of it: stagger the first firing by the entity's own id so that two
                // announcers on one map do not talk over each other for ever.
                _repeatDue[snap.Id] = now + (Math.Abs(snap.Id) % 7) * 0.9;
                return;
            }
            if (now < due) return;
            _repeatDue[snap.Id] = now + repeat;
            if (_audio.IsPlaying(snap.Id)) return;    // still saying the last one
        }

        // The road under a machine hands its sound back a moment later; see GroundReflection.
        long groundAt = System.Diagnostics.Stopwatch.GetTimestamp();
        if (engineKey.Length > 0 || physicalKey != null) ApplyGround(ref emitter, world);
        _partMs[1] += Ms(groundAt);
        Spreading? spreading = extentLayout != null ? SpreadOf(snap, extentLayout, ref emitter, eyePos, engineDt, now) : null;
        _placed[snap.Id] = (emitter.Volume, emitter.MinDistance);
        _audio.Submit(emitter);
        // ...and its other places, after its middle, so the synth they read already exists.
        if (spreading != null) PlaceOuter(snap.Id, spreading, emitter, world, now);

        // The other end of the machine, when it is close enough to be a second thing. Placed after
        // the machine's own voice, so an engine that has only just been built already exists for the
        // tap to read.
        if (engineKey.Length > 0 && _frontVoiced.Contains(snap.Id))
            FrontVoice(snap, OpenFPS.Common.MachineRegistry.VehicleFor(engineKey), acousticPath,
                       engineVolume, engineMinDistance, Math.Max(1.0f, engineRange), world.PositionsSampledAt);

        // ...and sitting in it, every other way into the cabin from where it comes in.
        if (interior && cabinLayout != null && engineKey.Length > 0)
            CabinVoices(snap, cabinLayout, cabinEar, emitter, eyePos);

        // The siren is NOT placed here: see UpdateSirens.

        // 6.1. The walls answering this engine. A live engine has no file to replay, so its
        // reflections are read back out of the synthesis's own ring buffer at the delay the mirrored
        // path implies — see EngineReflections.
        if (engineKey.Length > 0)
        {
            long echoAt = System.Diagnostics.Stopwatch.GetTimestamp();
            _engineEchoes.Update(snap.Id, emitter, acousticPath, eyePos, AudioPhysics.CurrentSpeedOfSound, engineDt, _audio,
                                 traced: OpenFPS.Client.AudioEngine.Fmod.FmodAudioProvider.HasTracedEchoes(snap.Id));
            double echoMs = Ms(echoAt);
            _partMs[0] += echoMs;
            _echoPassMs += echoMs;
        }

        // No floor slapback and no "cone reflection" here, on purpose.
        //
        // A second playback of the sound started at a ray hit (straight down, or along a directional
        // source's beam) is another independent read of the same file at an unrelated position in it
        // — for a looping announcement, the announcement again. And a ray that tests every collider,
        // including the emitter's own, hits it at distance zero when it starts inside the box, so
        // the copy sits AT the source, unoccluded (derived ids are skipped by the acoustic pass):
        // heard as the sound repeating softer in the same place.
        //
        // The floor is a box face and so is the wall the beam points at. The image-source pass
        // already mirrors the source through both, with the material's absorption and the extra
        // path, and renders a copy only when the ear would hear one as a separate event.
    }

    /// <summary>
    /// A packet of somebody talking, off the network: into their stream (TalkerStream), which a voice at
    /// their mouth reads (UpdateTalkers). Nothing is played from here: a packet is 20 ms of a voice, not
    /// a sound of its own.
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
    /// Everybody talking on voice chat, heard from their own mouth: at their head, facing the way they
    /// face, at a person's speaking level, through whatever is between you (the occlusion worker's answer,
    /// as for a siren), into the room they are in. Their usual level on their microphone is taken as normal
    /// conversation (OwnVoiceRing.SpeechRmsDbfs, measured from what arrives), so a quiet microphone is not
    /// a quiet person and a shout is still louder than talking. No ground reflection: a voice's ground
    /// copy flanged (see the NPC speech), and speech has none.
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
            // Sitting in a vehicle, their feet are on its floor and its cabin is round them: their mouth is
            // a seated person's, and what they say reaches anyone outside through the glass, or through
            // the windows if they are down.
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
                // Worked out afresh every frame, so the path, and the cabin round them, follow them.
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

            // ...and the surfaces round them answering, from where they stand to where you do. Not from
            // inside a cabin: what reaches you from there comes through its glass, and the street's
            // surfaces never hear them directly.
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
    /// The first voice id of the copies of another player's voice. Each talker answered at once holds a
    /// block of <see cref="OwnVoiceCopies"/> ids from here down, below -5000 with every other copy, so
    /// the acoustic-path pass never traces one from its image.
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
    /// The surfaces round somebody talking, answering them at your ear, as your own room answers you
    /// (PlaceVoiceCopies): found from their mouth, a few times a second, and played from their own
    /// images, each their voice read back its extra path behind it. Without this their voice was the
    /// direct sound and the room's late tail and nothing between, which is a voice heard dry — and, as
    /// they walked round you, a dry voice moving through a room that did not answer it (Cody, 2026-10-04:
    /// "I can hear him but it's dry, his reflections don't follow him"). At most one search a frame,
    /// shared out among the talkers.
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
    // Larger pool so rapid footsteps rarely reuse an ID while the previous step is still playing — reusing
    // an active voice hard-cuts it (click). 12 IDs gives plenty of headroom at running cadence.
    private const int FOOTSTEP_POOL_SIZE = 12;
    /// <summary>
    /// The room the listener was last found in, for the sounds the BODY makes.
    ///
    /// A SpatialEmitter's TargetRegionId defaults to -1, which is the outdoors, and a footstep is
    /// built between updates on the game thread where nothing has worked out a region. Left at the
    /// default, every footfall would send its reverberation to the OUTDOOR bus and give the room the
    /// player is standing in only the small cross-send meant for a sound in the NEXT room. The tail
    /// would then be the same length wherever you are, because it is the same bus wherever you are.
    /// </summary>
    private int _listenerRegion = AcousticConstants.GlobalRegionId;

    private const int FOOTSTEP_BASE_ID = -100;

    /// <summary>
    /// Everybody else's steps have voices of their own, apart from yours.
    ///
    /// Each step takes the next id round, and a submission under an id replaces whatever was waiting
    /// there, so in one shared pool three hundred people walking the city would come round it faster
    /// than your steps could start, and you would stop hearing your own. Nobody else's step can take
    /// your slot.
    /// </summary>
    private const int OTHERS_FOOTSTEP_BASE_ID = -300;
    private const int OTHERS_FOOTSTEP_POOL_SIZE = 64;
    private int _othersFootstepIndex;

    /// <summary>How far away another body's step can be heard at all, metres: a footstep's range.</summary>
    private const float FootstepRange = 15f;

    /// <summary>Somebody else's step: a sound at a place in the world, left there as they walk on.
    /// Only one close enough to hear is made at all. <paramref name="bodyId"/> is whose foot it was:
    /// their own body is not a wall between their foot and you (see CarryThePath).</summary>
    public void OnPlayerFootstep(Vector3 pos, string mat, string var, StepSlope slope = StepSlope.Level)
        => OnPlayerFootstep(pos, mat, var, slope, bodyId: -1);

    public void OnPlayerFootstep(Vector3 pos, string mat, string var, StepSlope slope, int bodyId)
    {
        if (Vector3.Distance(pos, _state.VisualPosition) > FootstepRange) return;
        SubmitFootstep(pos + new Vector3(0, 0.1f, 0), mat, follows: false, offset: Vector3.Zero, boostDb: 0f, slope: slope,
                       bodyId: bodyId);
    }

    /// <summary>
    /// How much louder your OWN footstep is to you than the same footstep is to a bystander standing
    /// where your ears are. A bystander hears it through the air only. You hear it through the air
    /// AND through your skeleton — the heel strike travels up the leg and the spine into the skull,
    /// which is why a footstep on a hard floor is felt as much as heard, and why your own steps
    /// stay obvious in a street where a stranger's are not. This is that second path. It applies to
    /// nothing but your own body; every other footstep in the world is the air path alone.
    /// </summary>
    private const float OwnFootstepBoneConductionDb = 8f;

    /// <summary>Tracing for OPENFPS_AUDIO_DEBUG=1 — what your own feet did, and when. A landing is a
    /// heavier sound than a step and fires at most twice a second, so "periodic bangs" is a question
    /// this line answers outright.</summary>
    private static readonly bool _footTrace = Environment.GetEnvironmentVariable("OPENFPS_AUDIO_DEBUG") == "1";

    /// <summary>
    /// Your OWN step. It is part of you, so it rides with you.
    ///
    /// Your feet are not somewhere in the world that you then walk away from; they are under your
    /// head, and stay there. Placed as a world-locked sound at the physics position, a step would be
    /// put down wherever the predicted position and the server's disagree at that instant — the
    /// listener stands at the smoothed VisualPosition, the step at Position — and then left behind
    /// as the listener moves on through its two or three hundred milliseconds: a single step's
    /// bearing swings from straight down to thirty degrees behind while it plays, and the steps
    /// trail and slide around you. So an own step is placed at a fixed offset from the listener's
    /// head and follows it, whatever the network is doing to the position underneath.
    /// </summary>
    // ── The listening-level calibration's voice (ListeningCalibration) ────────────────────────
    //
    // A person one step in front, at a digital gain the calibration chooses: placed directly, not by
    // the loudness law, and with no ear stage (no EarLevelDb), because it is meant to play at exactly
    // the level a real voice has there, with its real tone. Two ids taken in turn, so a new saying
    // never has to wait for the old one's voice to be released.
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

    public void OnOwnFootstep(Vector3 pos, string mat, string var, StepSlope slope = StepSlope.Level)
    {
        if (_footTrace) Log.Information("[FOOT] step {Slope} on {Mat} at {Pos}", slope, mat, pos);
        Vector3 offset = (pos - _state.Position) + new Vector3(0, 0.1f - _state.EyeHeight, 0);   // the foot, from the eye
        SubmitFootstep(_state.VisualPosition + new Vector3(0, _state.EyeHeight, 0) + offset, mat, follows: true, offset: offset,
                       boostDb: OwnFootstepBoneConductionDb, slope: slope);
    }

    /// <summary>How far a footstep take's pitch and level wander from one play to the next.</summary>
    private const float FootstepPitchJitter = 0.03f, FootstepLevelJitterDb = 1f;

    /// <summary>The last own footstep's slope, and the level and pitch it was given for it.</summary>
    internal (StepSlope Slope, float Db, float Pitch) LastStepGait { get; private set; }

    private void SubmitFootstep(Vector3 nudgePos, string mat, bool follows, Vector3 offset, float boostDb,
                                StepSlope slope = StepSlope.Level, int bodyId = -1)
    {
        // Your own feet ride with you (follows); anybody else's stay where they fell.
        bool own = follows;
        int id = own ? FOOTSTEP_BASE_ID - (_footstepPoolIndex++ % FOOTSTEP_POOL_SIZE)
                     : OTHERS_FOOTSTEP_BASE_ID - (_othersFootstepIndex++ % OTHERS_FOOTSTEP_POOL_SIZE);

        string resolvedSoundId = _sounds.ResolvePath(_sounds.GetImpactSoundId(mat, 0f));
        if (string.IsNullOrEmpty(resolvedSoundId)) return;

        // ── A footstep is a quiet sound, and it is placed as one ────────────────────────────
        //
        // Every engine and every transient in the world is placed by Loudness.Place from a source
        // level in decibels; the footsteps were not — they played at full scale, which on this
        // scale is a 112 dB source, a metre from the ear. A step is about 55 dB. Measured
        // (AudioLab --room-walk): even with the master maximizer's makeup gain at zero a dry step
        // peaked at −1.6 dBFS, so with the makeup on it hit the brick wall by nine decibels on
        // every step and the limiter pumped everything under it for the next fifty milliseconds —
        // "the footsteps are loud", and a pop on every one. The same law that places a rifle and a
        // car places these, twenty-six decibels down, where a step belongs against a megaphone.
        // ...and on stairs, a toe put down on the tread going up is lighter than a step on the level and
        // a heel dropped onto the tread below is heavier: see StrideAccumulator.SlopeDb.
        float slopeDb = StrideAccumulator.SlopeDb(slope), slopePitch = StrideAccumulator.SlopePitch(slope);
        if (follows) LastStepGait = (slope, slopeDb, slopePitch);
        var (stepGain, stepReference) = OpenFPS.Common.Loudness.Place(OpenFPS.Common.Loudness.FootstepDb + boostDb + slopeDb);

        // 1. Direct Sound (Will now undergo full acoustic pathing)
        var footstep = new SpatialEmitter
        {
            EntityId = id,
            SoundId = resolvedSoundId,
            Position = nudgePos,
            FollowsListener = follows,
            ListenerOffset = offset,
            Type = EmitterType.WorldLocked,
            // No two steps alike: a take is heard a hair higher or lower and a touch louder or softer
            // each time, as the same foot never lands quite the same way twice.
            Volume = stepGain * MathF.Pow(10f, (float)(Random.Shared.NextDouble() * 2.0 - 1.0) * FootstepLevelJitterDb / 20f),
            Pitch = slopePitch * (1f + (float)(Random.Shared.NextDouble() * 2.0 - 1.0) * FootstepPitchJitter),
            Range = FootstepRange,
            // Your own feet, pinned above the physics: they are how you know you are moving, and on
            // a loud map the arithmetic would rightly bury them under everything else. Only yours:
            // everybody else's compete for a voice by how loud they are, like any other sound.
            Essential = own,
            IsEvent = true,
            MinDistance = stepReference,
            EarLevelDb = OpenFPS.Common.Loudness.FootstepDb + boostDb + slopeDb,
            // The room the body is standing in, so its reverberation is THAT room's.
            TargetRegionId = _listenerRegion,
        };
        if (!own) CarryThePath(ref footstep, nudgePos, bodyId);
        _audio.Submit(footstep);

        // The walls answering YOUR footfalls. Other people's steps have none: their pool is yours.
        if (own) SubmitStepReflections(nudgePos, resolvedSoundId, stepGain, stepReference, OpenFPS.Common.Loudness.FootstepDb + boostDb + slopeDb);
    }

    /// <summary>
    /// Somebody else's step starts with the wall between you already on it.
    ///
    /// A step is short. It is submitted with no occlusion, its pooled id is asked about on the
    /// worker's next tick, and the answer comes back a tick or two later and is eased in over the
    /// smoothing time — by which time the step is over. So every footfall outside a flat played its
    /// attack through the brick unoccluded, and only its tail was dimmed: "I still hear people walking
    /// outside through the wall" (2026-09-29), after the same fault had been fixed for every other
    /// one-shot in WorldAudioPlayer, which this path never went through. The same answer as there: the
    /// simulator's result for the nearest source it heard a moment ago (their voice, their last step),
    /// moved to this one; failing that, the hand-rolled tracer. And the step reverberates in the room
    /// the FOOT is in, not the room you are in.
    ///
    /// The tracer is told whose foot it is, so that it leaves their body out. A player's body is a
    /// solid cylinder (it is what you bump into), and the foot is inside it: the rays from your ear
    /// ended in it, three of the five went through a body-sized chord of whatever floor that player
    /// was last standing on (MovementSystem writes it to the body's material), and every step of
    /// every other player came out 8 dB down with occlusion 0.6, at four metres as at nine — lost
    /// under a city street (Cody and Sean, 2026-10-05: "I cannot hear his footsteps while he's walking, only his
    /// beacon"). The people walking the city are not solid, which is why theirs were heard.
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
    /// Voices for the surfaces answering your own footfalls. Their own pool, so a wall's copy can never
    /// take the slot of the step it is a copy of.
    ///
    /// Below -5000, with every other copy, where the acoustic-path pass leaves them alone. Each carries
    /// its whole path, placed at its image behind the surface it came off; at -200 it was asked about
    /// as if it were a source standing at that image, and the worker's answer — the image traced to
    /// your ear THROUGH the wall that made it, -40 to -100 dB in a stairwell's brick and concrete, or
    /// round by the openings, out of the ground-floor door and up the stair openings, heard from the
    /// floor below — replaced it a frame or two after it started (Cody, 2026-10-04, Marlow Tower: "not
    /// hearing my own reflections follow me, it's like they're left on the first floor").
    /// </summary>
    internal const int STEP_ECHO_BASE_ID = -1_800_000;
    private const int STEP_ECHO_POOL_SIZE = 48;
    private readonly List<OpenFPS.Common.EarlyReflections.Arrival> _stepArrivals = new();
    private int _stepEchoIndex;

    /// <summary>Is the listener in a room (a closed boundary) rather than out of doors?</summary>
    private bool ListenerEnclosed(WorldSnapshot world, Vector3 at)
        => world.AcousticMap != null
           && world.AcousticMap.Regions.TryGetValue(_listenerRegion, out var room)
           && RoomAcoustics.IsEnclosure(room);

    /// <summary>
    /// The walls answering your own footsteps.
    ///
    /// The reverb bus cannot give footsteps their indoor character on its own: a bus is DIFFUSE. With
    /// only the direct sound and a diffuse tail, a parking garage sounds like a box that is reverberant
    /// all round you, rather than walls answering from their own directions. The near-field probes
    /// give the last three metres and the bus gives the tail; everything from three metres to the
    /// size of the room comes from here, and in a twenty-one by twenty-eight metre garage that is the
    /// whole room.
    ///
    /// What fills it is the machinery that already answers for world events and engines: image
    /// sources off the surfaces actually there, each played from the mirrored position so it arrives
    /// FROM ITS OWN WALL, delayed by its own extra path. A ceiling nine hundred millimetres over your
    /// head answers in five milliseconds and is most of why a low garage sounds low; a wall ten metres
    /// off answers in fifty-five and is the slap. Neither is a tail.
    ///
    /// Per-step reflections scatter the sound all over an enclosed room unless they are kept to a
    /// handful of taps, with a level that is the SURFACE's loss alone — the distance is applied by
    /// the engine when it places the copy at the image position, so a copy off a far wall is quiet
    /// because it is far, not because anybody scaled it.
    /// </summary>
    // ── Your own voice, as your room answers it ─────────────────────────────────────────────────

    /// <summary>True while the microphone is open (V). The session sets it.</summary>
    public bool OwnVoiceLive { get; set; }

    internal const int OwnVoiceBase = -1_300_000;
    /// <summary>How many surfaces answer your voice at once: the room's first answers, as for a step.</summary>
    private const int OwnVoiceCopies = 8;
    /// <summary>
    /// What the microphone path already adds before a copy can start: the capture's own buffer, its 20 ms
    /// frame and the ring's margin, about 60 ms. A copy's delay is its extra path less this, so a surface
    /// more than about ten metres of path away answers at its true time and a nearer one as soon as the
    /// microphone allows. (Cody, 2026-10-03: "server lag is a given, that's ok. I want to hear myself in
    /// the room I'm actually in.")
    /// </summary>
    private const float MicrophoneLatency = 0.06f;
    private readonly List<OpenFPS.Common.EarlyReflections.Arrival> _voiceArrivals = new();
    private readonly bool[] _voiceCopyOn = new bool[OwnVoiceCopies];
    private bool _voiceRoomOn;
    private double _voiceNextSearch, _voiceNextLog;

    /// <summary>
    /// Your own voice, while the microphone is open, played into the room you are in and never dry: the
    /// surfaces round you sending it back from their own directions after their own extra paths (found as
    /// your footsteps' are, from your mouth to your ears), and the room's reverberation fed from you. Its
    /// level is your voice's: the microphone's level while you talk is taken as normal conversation
    /// (OwnVoiceRing.SpeechRmsDbfs), so shouting fills the room more than talking does.
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
        // A world sound's level is its buffer's full scale at a metre (Speech.LevelDb): the voice's
        // full scale sits as far above normal talking as the microphone's talking level sits below it.
        float levelDb = OpenFPS.Common.Speech.NormalDb - OpenFPS.Client.AudioEngine.Fmod.OwnVoiceRing.Shared.SpeechRmsDbfs;
        var (gain, reference) = OpenFPS.Common.Loudness.Place(levelDb);

        // The room's reverberation. Its own sound is silent (FmodAudioProvider._ownVoiceRoomGroup); it is
        // placed at its reference distance, where a source's send to the room is what a source of that
        // level gives (closer, the send falls with the distance while the level cannot rise).
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
    /// The surfaces round a talking mouth answering it at an ear: image sources to second order, inside
    /// the window before the tail, the loudest <see cref="OwnVoiceCopies"/> placed and coloured exactly as
    /// a footstep's copies are (SubmitRoomStepEchoes), each one the voice's ring read back at its own
    /// path. One rule for your own voice and for anybody else's. Returns how many are answering.
    /// </summary>
    /// <param name="alreadyLateSeconds">What the voice's own playback has already cost the copies: the
    /// microphone's latency for yours; for somebody else, the direct voice's flight time, which it is
    /// played without, so each copy comes its extra path behind it.</param>
    /// <param name="firstId">The first copy's voice id; the rest count down from it.</param>
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

    private void SubmitStepReflections(Vector3 stepPos, string soundId, float stepGain, float stepReference, float stepLevelDb)
    {
        // The surfaces round your own footfall, placed as a clap's are (WorldAudioPlayer.QueueEarlyEchoes):
        // mirrored through the walls to third order, the loudest first, inside the window before the
        // tail. Everywhere: outdoors the floor is skipped (the voice has its own ground) and the
        // facades within 27 m of extra path answer, as they do. In traced mode too: the listener's
        // traced stage plays only the late tail, so without these your own steps would have a direct
        // sound, a tail 50 ms later, and nothing from the walls between.
        Vector3 ear = _state.VisualPosition + new Vector3(0, _state.EyeHeight, 0);
        if (_groundWorld is not { } world) return;
        SubmitRoomStepEchoes(stepPos, ear, soundId, stepGain, stepReference, stepLevelDb, world);
    }

    /// <summary>The room's first answers to your own footfall, placed as WorldAudioPlayer.QueueRoomEchoes
    /// places a clap's: mirrored through the walls to third order, the loudest first, inside the
    /// window before the tail, each from its own wall's direction with that wall's colour.</summary>
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
            // The mirror share only (WorldAudioPlayer.MirrorShare). A step is a bank sample, not a
            // synthesised sound, so its scattered share has no wash to go to yet.
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
                // Placed as the step and scaled by what the surfaces and the longer path kept; the
                // engine's 1/r at the image is undone in `gain`, as for every other copy.
                Volume = stepGain * gain,
                MinDistance = stepReference,
                EarLevelDb = stepLevelDb,
                EarCopyDb = 20f * MathF.Log10(MathF.Max(1e-6f, gain)),
                Range = 25f,
                // No DelayMs: the facade delays every submission by its distance, and the image is
                // the whole path away.
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

    /// <summary>
    /// The map's outdoor ambience bed, from the manifest. Starting it is deferred to the audio update
    /// so it happens on the audio thread with everything else.
    /// </summary>
    public void SetMapAmbience(string ambienceId)
    {
        _mapAmbienceId = ambienceId ?? "";
        _ambienceRegionId = int.MinValue;   // force the next update to reconsider
    }

    /// <summary>
    /// Keeps the ambience beds in step with where the listener is.
    ///
    /// The outdoor bed is never stopped while the map is loaded, only ducked: walking into a building
    /// should take the world outside DOWN, not switch it off, because a room with a door in it is still
    /// connected to outside. <see cref="LocalPlayerState.ShelterFactor"/> already measures exactly that
    /// — it is the sky-visibility raycast plus the indoor-region flag — so it is what drives the duck.
    ///
    /// A region that declares its own AmbienceId (a hum, a machine room, running water) plays on top
    /// while the listener is inside it, and cross-fades out on the way through the door because both
    /// beds glide to their new levels rather than switching.
    /// </summary>
    private void UpdateAmbience(WorldSnapshot world, int listenerRegionId)
    {
        if (_mapAmbienceId.Length > 0)
        {
            // Ducked, not silenced. Even fully sheltered the world outside is still faintly there.
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

    /// <summary>
    /// Probes the space around the listener's head and hands the result to the mixer. Head-relative,
    /// so the picture turns with the player.
    /// </summary>
    // ── The ground ─────────────────────────────────────────────────────────────────────────────

    private Vector3 _groundEar;
    private struct GroundCache { public Vector3 Src, Ear; public double At; public bool Found; public float Height; public string Material; }
    private readonly Dictionary<int, GroundCache> _groundCache = new();
    private WorldSnapshot? _groundWorld;
    private readonly Vector3[] _groundRay = { -Vector3.UnitY };
    private readonly float[] _groundDist = new float[1], _groundAbs = new float[1];
    private readonly string[] _groundMat = new string[1];

    /// <summary>
    /// The ground reflection for one live voice: the source mirrored in the surface under the point
    /// where its sound bounces on the way to the listener (GroundReflection has the why).
    ///
    /// Two rays straight down. The first finds the ground under the source, which fixes where the
    /// bounce lands — the point between the two, in the ratio of their heights. The second is cast
    /// from the direct path above that point, so it finds whatever is actually there to reflect off,
    /// and what it is made of: a surface above the ground and below the line (a bonnet, a kerb, a
    /// shelter roof) is what the sound bounces off, and anything higher would be in the way of the
    /// direct sound, not under it. The surface's own absorption decides how much comes back.
    /// </summary>
    private void ApplyGround(ref SpatialEmitter e, WorldSnapshot? world) => ApplyGround(ref e, world, true, 0f);

    /// <summary>
    /// The same for a recorded sound, which WorldAudioPlayer plays under a fresh voice id each time, so
    /// it asks once and keeps nothing.
    ///
    /// A recording made at the ground already has the ground in it. A footstep, a dropped can, a door
    /// scraping — the microphone heard the bounce as part of the sound, a fraction of a millisecond
    /// behind it, so adding it again would lift the whole thing six decibels. So a recorded sound
    /// within <see cref="RecordedGroundMinHeight"/> of the surface under it gets none of its own.
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

        // The rays are the cost; the geometry after them is not. The ground under a car does not
        // change from one frame to the next, so they are cast again only once the source or the ear
        // has moved a metre, or a quarter of a second has gone.
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

    private void UpdateBoundaryProbes(WorldSnapshot world, Vector3 visualEyePos)
    {
        var head = _state.Rotation;
        var directions = BoundaryModel.ProbeDirections;

        // A passenger has no near field outside the vehicle. "The proximity to me from the outside
        // objects isn't relevant" — a lamp post the bus brushes past is half a metre from the glass,
        // not from your ear, and what the cabin does to sound is the interior model's (EngineVoiceState
        // .Interior and the enclosure filter). So nothing is near while you ride.
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

    /// <summary>
    /// Called when the server reports a material change underfoot via StatsUpdate.
    /// Passes the material's absorption index into the current region for reverb correction.
    /// </summary>
    public void NotifyMaterialChange(string material)
    {
        if (_lastAcousticMap == null) return;
        var snap = _lastSnapshot ?? _acousticWorker.GetLastWorld();
        if (snap == null) return;
        int listenerRegionId = _acoustics.GetRegionAt(snap, _state.VisualPosition + new Vector3(0, _state.EyeHeight, 0));
        if (listenerRegionId == AcousticConstants.GlobalRegionId) return;
        if (!_lastAcousticMap.Regions.TryGetValue(listenerRegionId, out var region)) return;

        // Override the floor material (index 0) with the server-reported underfoot material.
        int resonanceIndex = AcousticRegistry.GetProperties(material).ResonanceIndex;
        if (region.Materials == null || region.Materials.Length == 0 || region.Materials[0] == resonanceIndex)
            return;

        region.Materials[0] = resonanceIndex;
        _lastAcousticMap.Regions[listenerRegionId] = region;

        // And that is all. The acoustic map is NOT rebuilt when the floor's Sabine estimate moves:
        // that tears down every reverb bus on the map, every Steam Audio voice attached to them and
        // every send into them, mid-tail — the loudest discontinuity the engine can make, on a
        // footstep. It is not needed. The tail's time, colour and level are surveyed from the boxes
        // round the listener every few ticks (Enclosure.Look), and the floor underfoot is one of
        // those boxes, so what you are standing on already colours the room. The region's material
        // is kept current here for anything that still reads the Sabine estimate at bus creation.
    }

    private int _breathSeed;

    /// <summary>
    /// A body breathing, through the same channel as every other short sound in the world.
    ///
    /// Nobody recorded any of these. A breath is turbulent air through a narrow opening — a hiss —
    /// and the transient synthesiser already makes those. Routing it
    /// through <see cref="WorldAudioPlayer"/> rather than playing a sample means it is attenuated,
    /// occluded through walls, reverberated by the room and placed in the listener's head by exactly
    /// the code that handles a gunshot, none of which had to learn what breathing is.
    /// </summary>
    public void OnBreath(int entityId, Vector3 position, OpenFPS.Common.Breath breath)
    {
        if (!breath.Taken) return;

        WorldAudio.Receive(new OpenFPS.Common.Networking.WorldAudioEvent
        {
            SourceEntityId = entityId,
            Label = breath.IsInhale ? "breath in" : "breath out",
            Seed = unchecked(++_breathSeed),
            Sounds = new System.Collections.Generic.List<OpenFPS.Common.TransientSound>
            {
                new OpenFPS.Common.TransientSound
                {
                    Character = OpenFPS.Common.SoundCharacter.Hiss,
                    Position = position,
                    LevelDb = breath.LevelDb,
                    Hz = breath.Hz,
                    DecaySeconds = breath.DecaySeconds,
                    Noisiness = 1.0f,
                },
            },
        }, OpenFPS.Common.AudioClock.Now);
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
        string impactSound = _sounds.GetImpactSoundId(mat, 0f);
        string resolved = _sounds.ResolvePath(impactSound);
        
        if (!string.IsNullOrEmpty(resolved))
        {
            // Placed like a step (see SubmitFootstep); a landing is a heavier step, and its extra
            // weight is in the sound the server chose for it, not in a louder scale.
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

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
    /// thrown away and restarted with cold pipes and a stopped crank. It was audible as a car
    /// stuttering in and out. Ranking a car that is already sounding as if it were a quarter nearer
    /// than it is makes it keep its voice until the challenger is decisively closer.
    /// </summary>
    private const float EngineKeepBias = 0.75f;

    /// <summary>And no engine is dropped within this long of being started, whatever the ranking
    /// says, so a car that crosses the boundary at three hundred kilometres an hour cannot be
    /// started and stopped inside the same second.</summary>
    private const double EngineMinimumHoldSeconds = 2.5;

    /// <summary>
    /// The audio update is capped here rather than in each head's game loop, so both are capped by the same
    /// rule. The loop it hangs off polls the network as fast as it can — a 5 ms sleep, so roughly 200 Hz —
    /// and it was driving the entire audio update from that: listener sync, region resolution, the
    /// near-field radar's six raycasts, an acoustic request per active voice, and the FMOD tick. None of
    /// that resolves faster than a frame, so two updates in three were work nobody could hear, taken from
    /// the thread that has to service the socket.
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
    /// sample inside the FMOD mixer callback, and measured (`--engine-cost`) a V8 renders about nine
    /// seconds of audio per second of one core in a release build. Four is therefore already about
    /// half a core, and the mixer callback still has HRTF, reverb and every other voice to do in the
    /// same deadline. A field of cars can be any size it likes on the server; the nearest few are
    /// the ones a listener can pick out anyway, and the rest are the ones the wind takes.
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
    /// sources a map nobody has seen yet decides to carry. Four engines measured half a core on the
    /// bench and pegged the mixer at a hundred per cent in the game, because the game also had all of
    /// that. And a mixer at a hundred per cent does not sound busy, it sounds broken: the callback
    /// misses its deadline and the output tears, which is heard as crackling and is easy to mistake
    /// for distortion.
    ///
    /// So the budget is a control loop, and it gives things up IN ORDER. Reflections first — a car's
    /// second reflection, then its first — and only then a car. That order is not a detail: shedding
    /// cars first made the field sound like voices being swapped, because a car arriving pushed out
    /// one that had not finished going past, and then the one that left came back. A listener notices
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
    /// and the only one that reaches into another voice's buffers: a city put 256 of them through
    /// create-and-release in 98 seconds, each holding a reference to the engine voice it is an echo
    /// of. When a crash lands on the mixer thread and cannot be reproduced headlessly, being able to
    /// take the busiest subsystem out in one run is worth more than another round of reading code.
    ///
    ///     OPENFPS_ENGINE_ECHOES=0   no reflections of engines at all
    ///     OPENFPS_ENGINE_ECHOES=1   one per engine instead of two
    ///
    /// Unset is the normal behaviour. This is a diagnostic lever, not a setting anybody should need.
    /// </summary>
    /// <remarks>
    /// OFF by default since 2026-09-23. An engine echo is a COHERENT copy of the engine — the same
    /// waveform, read later, placed at a mirror point through the full HRTF. Against the direct
    /// sound it is a comb filter that sweeps as either moves (phasing, "inside out"); on its own it is
    /// a point source beamed from a wall; and the budget moves echoes from car to car, so one cuts
    /// out mid-sound. A bus's echo carried its air hiss to wherever the mirror was: "white noise off to
    /// my right, then it suddenly disappears — is that a bus or a reflection?" What a street really
    /// sends back off a steady engine is many surfaces at once, incoherent: a diffuse wash, which is
    /// the reverb. One-off sounds keep their echoes (WorldAudioPlayer), where an echo IS an event.
    /// OPENFPS_ENGINE_ECHOES=2 brings them back for comparison.
    ///
    /// ON again since the same evening, with the cause fixed rather than the effect removed: each echo
    /// is smeared by the roughness of the wall it came off (EngineEchoState.Scattering), so it is the
    /// same sound but no longer the same waveform, and it swells and fades over a few hundred
    /// milliseconds instead of switching. OPENFPS_ENGINE_ECHOES=0 takes them out for an A/B.
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
    /// </summary>
    /// <summary>
    /// ...overridable with OPENFPS_MACHINE_VOICES, and ZERO IS A VALID ANSWER.
    ///
    /// It used to treat 0 as "unset" and hand back the default, which makes the lever useless for the
    /// one thing a lever is for: taking a subsystem out to see whether it is the one at fault. The
    /// physical voices — machines and aircraft — are the newest code in the audio engine and the last
    /// line in the log before several crashes; being able to switch them off in one run is worth more
    /// than a day of reading them.
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
    /// mixer that is not running. The loop could not tell the difference: it shed echoes, then
    /// borrowed voices, then cars, and a second later, the stall over and the load back to normal,
    /// it added them all back. Every re-added engine is a NEW voice — a new synth, a tenth of a
    /// second of warm-up, a quarter of a second of fill, an FMOD graph rebuild — so the loop's
    /// response to a busy machine was to make more work for it, at the worst possible moment, and it
    /// was heard as cars appearing and vanishing on the first lap.
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
        _birds = new BirdLife(audio, _acoustics);
        WorldAudio.Received = message => _birds.Heard(message, OpenFPS.Common.AudioClock.Now);
        _acousticWorker = new AsyncAcousticWorker(_acoustics);
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
        if (_sirenVoiced.Remove(entityId)) _audio.StopSound(SirenVoiceBase - Math.Abs(entityId));
        _sirenControl.Remove(entityId);
        _frontRetiring.Remove(entityId);
        // Its image-source reflections: one voice per slot (see ReflectionVoiceId).
        for (int slot = 0; slot < EarlyReflections.MaxArrivals; slot++)
            _audio.StopSound(ReflectionVoiceId(entityId, slot));

        _acousticWorker.Forget(entityId);
    }

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
        // given) and it is not the interpolator (which had already moved the car); it is this, and
        // nothing was measuring it.
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
        _groundWorld = world;

        // 1. Resolve high-precision listener region (OBB check)
        int listenerRegionId = _acoustics.GetRegionAt(world, visualEyePos);
        // Kept, because your own feet are submitted from the game thread between updates and have to
        // know which room to reverberate in. See SubmitFootstep.
        _listenerRegion = listenerRegionId;

        // 2. Synchronize the listener's smoothed physical state.
        //
        // The wind the listener actually feels is synthesized HERE, not received. The server broadcasts
        // the sustained wind and a single gustiness scalar once a second; sampling a gust at 1 Hz would
        // alias it into a stutter, so the swell is generated locally at audio rate from that scalar and
        // the local clock (see WindModel). Shelter attenuates it: indoors the air is still.
        Vector3 feltWind = WindModel.Felt(
            world.WindVelocity, world.WindGustiness, _now(), _state.ShelterFactor);
        _state.FeltWind = feltWind;

        // A fraction of the moving air rides on the listener velocity, so wind produces a subtle Doppler
        // on distant sounds — and a gust now audibly swells and drops it.
        Vector3 listenerVelocity = _state.Velocity + feltWind * 0.1f;
        // Sitting in something, you face the way it faces: the session sets your heading from the
        // vehicle every frame (ClientGameSession.FollowRide), so the ears and the compass agree.
        var listenerRotation = _state.Rotation;
        if (_state.IsRiding && world.Entities.TryGetValue(_state.RidingEntityId, out var carrying))
        {
            // ...and you move at its speed. A passenger is not predicted, so their own velocity reads
            // zero — which against the vehicle's moving voice is a Doppler shift on your own bus.
            listenerVelocity = carrying.Velocity + feltWind * 0.1f;
        }
        _audio.UpdateListener(visualEyePos, listenerRotation, listenerVelocity, listenerRegionId);
        _audio.UpdateShelter(_state.ShelterFactor);
        WorldAudio.ListenerVehicleId = _state.RidingEntityId;
        // The doors, the things to pick up and the cars to get into around you.
        _beacons.Update(world, visualEyePos, _now());
        // The lane lines, if you are the one driving.
        _drivingAids.Update(world, _state, _now());
        // ...and the rest of the world through the glass, if you are sitting in anything with a roof.
        var (encLow, encMid, encHigh) = CabinEnclosure(world);
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
            _audio.SetListenerReverbField(_acousticWorker.ListenerReturnDirection, _acousticWorker.ListenerAnisotropy,
                                          _acousticWorker.ListenerMeanFreePath,
                                          _acousticWorker.ListenerSurfaceArea);
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
                // Over the REGIONS, not over the world. This used to walk every entity there is to
                // find the ones that declare a room, which on a city block was five hundred struct
                // copies a frame and on a city is six thousand — for the same six hundred regions,
                // almost none of which can move at all.
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
            // IDs less than -10000 are reserved for synthesized reflections.
            // We MUST NOT calculate reflections for reflections, or we get an infinite feedback loop.
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
                        if (_sirenVoiced.Contains(id)) _audio.SetAcousticPath(SirenVoiceBase - Math.Abs(id), shadowed);
                        // And so is its borrowed engine, if it is voiced from afar.
                        if (_distantVoiced.Contains(id)) _audio.SetAcousticPath(DistantVoiceBase - Math.Abs(id), shadowed);
                        continue;
                    }

                    // A reflection path is honoured wherever it came from. Both paths produce them now:
                    // under Steam Audio simulation they are first-order image sources off the scene's own
                    // surfaces (EarlyReflections), and on the fallback path they come from the hand-rolled
                    // tracer. Retiring them under the simulator — on the theory that a parametric reverb
                    // tail covered the same energy — is what left a room answering from everywhere at once
                    // and a doorway inaudible from outside.
                    if (!world.Entities.TryGetValue(id, out var originalSnap)) continue;

                    // A SYNTHESISED source has no file to play a delayed copy of, and this is the
                    // rule rather than a list of names.
                    //
                    // Its sound id names a model, not a sample, so asking the provider to play it as
                    // one fails every frame — "not playing yet — Missing", once a frame per source,
                    // for ever, with a deferred play queued behind each one. It has now caught two
                    // different kinds of source: first engines, where the first version of this line
                    // matched the RESOLVED spelling "ENGINE/" that no snapshot ever holds and so
                    // matched nothing; then air conditioners, where the test was a list of two
                    // prefixes and "machine:" was not on it. A hundred and twenty-one window units
                    // retrying a file load every frame is most of a game loop.
                    //
                    // IsSynth is the property that actually decides it, and it is on the snapshot.
                    // Anything the mixer RENDERS rather than plays is answered by its own reflection
                    // path — EngineReflections reads a car's walls out of the synthesis's own ring —
                    // or by nothing, which is correct until one exists.
                    var sourceEmitter = originalSnap.Definition.SoundEmitter;
                    string sourceSound = sourceEmitter.SoundId ?? "";
                    if (sourceEmitter.IsSynth
                        || sourceSound.StartsWith("engine:", StringComparison.OrdinalIgnoreCase)
                        || sourceSound.StartsWith("ENGINE/", StringComparison.OrdinalIgnoreCase)) continue;
                    if ((uint)path.ReflectionIndex >= (uint)EarlyReflections.MaxArrivals) continue;
                    // Traced, indoors: the room's traced response has these arrivals in it.
                    if (OpenFPS.Client.AudioEngine.Fmod.FmodAudioProvider.TracedActive && ListenerEnclosed(world, visualEyePos)) continue;
                    _slotLive[path.ReflectionIndex] = true;

                    // One voice per slot, and the slots are ordered by the SURFACE each arrival
                    // came off (see EarlyReflections), so a slot means the same wall from tick to
                    // tick. It used to be `ReflectionId % 100` — the surface's identity folded into
                    // a hundred values — which two different surfaces can collide on, and a
                    // collision is one voice being handed two reflections from opposite sides of a
                    // room and flickering between them. The index cannot collide: it is 0..N-1
                    // among the arrivals that are live right now.
                    int reflectId = ReflectionVoiceId(id, path.ReflectionIndex);

                    var reflectEmitter = new SpatialEmitter
                    {
                        EntityId = reflectId,
                        SoundId = _sounds.ResolvePath(originalSnap.Definition.SoundEmitter.SoundId),
                        Mode = originalSnap.Definition.SoundEmitter.Mode,
                        Position = path.ApparentPosition,
                        ApparentPosition = path.ApparentPosition,
                        // What the copy has left, per band, is the whole of what makes it a
                        // reflection rather than a second source: the surface took some of it and
                        // the extra distance took the rest. The level used to be the emitter's own
                        // volume less occlusion, which for an image source is always zero — every
                        // reflection came back at full strength however absorbent the wall was.
                        Volume = originalSnap.Definition.SoundEmitter.Volume
                               * Math.Clamp(path.EqMid, 0f, 1f),
                        Range = originalSnap.Definition.SoundEmitter.Range * 0.8f,
                        IsReflection = true,
                        // The copy starts at the source voice's own playback position, so it is what
                        // is being heard, arriving later — not the file again from the top.
                        ReflectionOf = id,
                        DelayMs = path.ReflectionDelayMs,
                        Type = EmitterType.WorldLocked,
                        EqLow = path.EqLow,
                        EqMid = path.EqMid,
                        EqHigh = path.EqHigh,
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
                // Nothing used to stop these. A wall's copy, once started, played on at its last
                // position for as long as the source did — a listener who walked out of a slapback's
                // reach kept hearing it from where the wall had been. And a cut is a click, so the
                // voice fades over the budget's own ramp and is stopped only once it is silent.
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
        ChooseLiveMachines(world, visualEyePos);
        _engineEchoes.EchoesPerEngine = _adaptiveEchoes;
        _engineEchoes.SyncGeometry(world);
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

        long partAt = System.Diagnostics.Stopwatch.GetTimestamp();
        UpdateHorns(world, visualEyePos, OpenFPS.Common.AudioClock.Now);
        UpdateSirens(world, visualEyePos);
        _partMs[3] += Ms(partAt);
        partAt = System.Diagnostics.Stopwatch.GetTimestamp();
        _birds.Update(world, visualEyePos, OpenFPS.Common.AudioClock.Now);
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
    /// mixer's own attribute pass. On a map fifty times the area of the one this was written for,
    /// guessing between those is how a session gets spent.
    /// </summary>
    private readonly double[] _stageWorstMs = new double[5];

    /// <summary>
    /// Inside the emitter stage, the pass that cost most, by part: the engines' echoes, the ground
    /// rays, the birds, the horns and sirens, and everything else. The emitter stage alone ran to
    /// 150-250 ms on the city on 2026-09-25 and every source froze for that long ("the reflections
    /// step away ... a delay in when the reflections catch up"); this names which part.
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

        // Update readable region for accessibility
        _state.CurrentRegionId = regId;
        if (world.AcousticMap != null && world.AcousticMap.Regions.TryGetValue(regId, out var reg))
        {
            _state.IsIndoor = reg.IsIndoor;
            _state.RoomSize = reg.RoomSize;
            _state.RoomMaterials = reg.Materials;
            _state.ReverbTimeScale = reg.ReverbTimeScale;
            _state.RoomCenter = world.AcousticMap.RegionPositions.GetValueOrDefault(regId, eyePos);
            _state.RoomRotation = world.AcousticMap.RegionRotations.GetValueOrDefault(regId, Quaternion.Identity);
        }
        else
        {
            _state.IsIndoor = false;
        }
        _state.CurrentRegion = NameOfPlace(world.AcousticMap, regId, _state.CurrentMaterial, _state.ShelterFactor);
    }

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
    /// the acoustic map — the generator puts it there so the outdoors has a reverb and a size — so
    /// the lookup that used to guard this fallback always succeeded, and every metre of open ground
    /// no author had boxed was announced as "Outside": the fallback below had never once run. Its
    /// name is kept only for ground the material table does not recognise.
    ///
    /// Derived, not authored: no map has to label a kerb, and a surface nobody has named still
    /// announces itself correctly.
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
        if (shelter > 0.8f) return "Under Shelter";
        string ground = OutdoorNameFor(material);
        if (ground == "outside" && !string.IsNullOrWhiteSpace(global?.FriendlyName)) return global.Value.FriendlyName;
        return ground;
    }

    /// <summary>
    /// What to call a patch of open ground, from the surface underfoot. Falls back to "outside"
    /// for anything unrecognised, which is no worse than what it replaced.
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
        _ => "outside",
    };

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
    /// VehicleProfile.ByName is memoised for exactly this reason and says so; these two are not, and
    /// a cache here fixes the caller that has the problem without changing what a model means for
    /// anyone who reloads an authored one.
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
    /// <summary>Car glass, metres. The windows are most of a cabin's area and all of its weakest
    /// panels, so the airborne path in is theirs.</summary>
    private const float WindowThicknessM = 0.004f;

    /// <summary>An open bus doorway's share of the cabin's wall area, as transmitted power.</summary>
    private const float DoorwayPowerFraction = 0.0225f;

    /// <summary>
    /// What the body of the vehicle you are sitting in takes off everything outside it, dB per band.
    ///
    /// The same two paths the interior engine voice uses, the other way round: the windows by their
    /// mass (transmission falls as rho*c / (pi*f*m)), and the seals, which have no mass and let a
    /// little of everything through. At 150 Hz a hatchback's glass passes about a hundredth of the
    /// power; by a kilohertz the seals are most of what gets in, so the top end sits about thirty
    /// decibels down — which is the whole of "the traffic outside sounds like it is outside".
    /// Nothing when you are on foot, or on something with no cabin: a motorcycle keeps the street.
    /// </summary>
    private (float Low, float Mid, float High) CabinEnclosure(WorldSnapshot world)
    {
        if (!_state.IsRiding || !world.Entities.TryGetValue(_state.RidingEntityId, out var ride)) return (0f, 0f, 0f);
        string? sid = ride.Definition.SoundEmitter.SoundId;
        if (sid == null || !sid.StartsWith("engine:", StringComparison.OrdinalIgnoreCase)
            || !OpenFPS.Common.MachineRegistry.Knows(sid[7..])) return (0f, 0f, 0f);
        var body = OpenFPS.Common.MachineRegistry.VehicleFor(sid[7..]).Body;
        if (body == null || body.CabinLengthM <= 0f) return (0f, 0f, 0f);

        float mass = MathF.Max(1f, OpenFPS.Common.AcousticRegistry.GetProperties("Glass").DensityKgM3 * WindowThicknessM);
        float seal = body.SealLeak * body.SealLeak;
        // A bus at a stop with its doors open has a hole in its side: about 2.4 m^2 of doorway in a
        // hundred-odd m^2 of cabin wall, which lets the street in at a couple of per cent of its
        // power, at every frequency. The voice decides when the doors are open; ask it.
        if (_audio.EngineDoorsOpen(_state.RidingEntityId)) seal += DoorwayPowerFraction;
        float Loss(float hz)
        {
            float t = 415f / (MathF.PI * hz * mass);
            return 10f * MathF.Log10(MathF.Min(1f, t * t + seal));
        }
        return (Loss(150f), Loss(1000f), Loss(4000f));
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

    private void ChooseLiveMachines(WorldSnapshot world, Vector3 eyePos)
    {
        double now = _now();
        _machineOrder.Clear();

        foreach (int entityId in world.AudioEntityIds)
        {
            if (entityId == OwnEntityId) continue;
            if (!world.Entities.TryGetValue(entityId, out var snap)) continue;
            var em = snap.Definition.SoundEmitter;
            if (!em.IsSynth || em.SoundId == null) continue;
            if (!PhysicalLevel(em.SoundId, out float levelDb, out float extent)) continue;

            float d = Vector3.Distance(OpenFPS.Common.AudioEmission.PointFor(snap), eyePos);
            var (gain, reference) = OpenFPS.Common.Loudness.Place(levelDb, extent);
            float range = MathF.Max(em.Range, OpenFPS.Common.Loudness.AudibleRange(levelDb));
            float level = OpenFPS.Common.Loudness.RenderedGain(gain * em.Volume, reference, range, d);

            // Louder sorts first, so the key is negated. The hold and the keep bias work on the key
            // exactly as they do for a car — a machine that already has a voice has to be beaten
            // decisively, not merely matched.
            float key = _liveMachines.Contains(entityId) ? -level / (EngineKeepBias * EngineKeepBias) : -level;
            if (_machineStarted.TryGetValue(entityId, out double began) && now - began < EngineMinimumHoldSeconds)
                key = float.NegativeInfinity;
            _machineOrder.Add((entityId, key, level));
        }
        _machineOrder.Sort((a, b) => a.Key.CompareTo(b.Key));

        int keep = Math.Min(_adaptiveMachines, _machineOrder.Count);

        foreach (int id in _liveMachines)
        {
            bool survives = false;
            for (int i = 0; i < keep; i++) if (_machineOrder[i].Id == id) { survives = true; break; }
            if (survives) continue;
            _machineStarted.Remove(id);
            if (!_machineRetiring.Contains(id)) _machineRetiring.Add(id);
        }
        for (int i = _machineRetiring.Count - 1; i >= 0; i--)
        {
            int id = _machineRetiring[i];
            bool wanted = false;
            for (int k = 0; k < keep; k++) if (_machineOrder[k].Id == id) { wanted = true; break; }
            if (wanted) { _machineRetiring.RemoveAt(i); continue; }
            if (_audio.FadeOutEngine(id)) { _audio.StopSound(id); _machineRetiring.RemoveAt(i); }
        }

        int admittedMachines = 0;
        _liveMachines.Clear();
        for (int i = 0; i < keep; i++)
        {
            int id = _machineOrder[i].Id;
            if (!_machineStarted.ContainsKey(id))
            {
                // Same reason as an engine: building one is a set of waveguides and resonators, and
                // a map load presents all of them in the same instant.
                if (admittedMachines >= NewEnginesPerUpdate) continue;
                admittedMachines++;
            }
            _liveMachines.Add(id);
            _audio.ReviveEngine(id);
            if (!_machineStarted.ContainsKey(id)) _machineStarted[id] = now;
        }
    }

    /// <summary>
    /// Decides which cars get their own engine, and which borrow one.
    ///
    /// Every car in earshot gets its OWN engine now, because the engines no longer run inside the
    /// mixer callback — a worker pool renders them ahead across all the machine's cores (see
    /// EngineRenderPool). What used to be the hard constraint, one thread integrating every engine in
    /// the world one after another before a deadline, is gone, and with it the reason cars had to be
    /// rationed, borrowed, handed over between voices, or dropped.
    ///
    /// The budget survives only as a SAFETY NET. It follows measured mixer load, and what it protects
    /// against now is not the synthesis but the placement: thirty cars is still thirty spatialised
    /// voices with HRTF and filtering, and that cost is the mixer's. If it ever runs short the order
    /// of sacrifice is reflections, then borrowed voices, then engines — never the cars first.
    /// </summary>
    private void ChooseLiveEngines(WorldSnapshot world, Vector3 eyePos)
    {
        double now = _now();

        float load = _audio.MixerLoad;
        if (load > MixerLoadCeiling) { if (_overCeilingSince < 0) _overCeilingSince = now; }
        else _overCeilingSince = -1;

        // THE PRODUCERS, as well as the mixer. Engines render on EngineRenderPool's threads, and when
        // those cannot keep up a voice STARVES — its block is ramped to silence — which the mixer's
        // load never sees. On the city on 2026-09-25 the pool was short by a core or two, 50-190
        // starves a second went unanswered, and the car nearest you came out chopped ("really bad
        // over sampling ... the v8 muscle car"). Starving gives up machines and then cars, the same
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
                // A machine's second outlet goes first: it is the only voice whose loss costs
                // nothing but geometry — the machine stays exactly as loud, because the front tap
                // slews back into the voice that is still playing.
                if (_adaptiveFront > 0) _adaptiveFront--;
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
                else if (_adaptiveEchoes < EchoCeiling) _adaptiveEchoes++;
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
        // moment the scene, the geometry and the sample banks are all loading too, and it was heard
        // as dropouts for the first second or two. Spread over a few frames it is inaudible: a car
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
        // twenty of and fatal for one it has two of: the city carries two slip-on motorcycles, and
        // the moment both fell out of the budget together the loudest vehicle in the city stopped
        // existing. Reported as "the motorcycles are very quiet, I can hardly hear them drive by" —
        // and measured, at their closest approach they are the loudest thing on that street.
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
            // stop dead and then carry on, which is precisely what it was reported as.
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
    private static Vector3 ExhaustSlot(OpenFPS.Common.VehicleProfile v)
        => new(0f, v.ExhaustHeight, v.ExhaustOffsetZ);

    /// <summary>...and where it breathes.</summary>
    private static Vector3 IntakeSlot(OpenFPS.Common.VehicleProfile v)
        => new(0f, v.IntakeHeight, v.IntakeOffsetZ);

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
            EnableReverb = true,
        };
        ApplyGround(ref e, _groundWorld);
        if (_audio.IsPlaying(voiceId)) _audio.UpdateSpatialAttributes(e);
        else _audio.PlayPhysicalSoundDirect(e);
    }

    /// <summary>The birds: found from the map's foliage and roofs, not placed. See BirdLife.</summary>
    private readonly BirdLife _birds;

    /// <summary>Vehicles carrying a siren, found this frame.</summary>
    private readonly HashSet<int> _sirenCars = new();
    private readonly List<int> _sirensGone = new();

    /// <summary>
    /// Every siren on the map, placed every frame, whatever its car's engine is doing.
    ///
    /// It used to be placed from inside the car's own emitter pass, which only runs for a car whose
    /// ENGINE won a voice — and a siren is thirty-five decibels louder than the engine under it, so it
    /// is exactly the sound that must not depend on that. A patrol car that dropped out of the engine
    /// budget left its siren wailing where the car had been, for twenty seconds at a time ("placed at
    /// a position 19,700 ms old"), and nothing ever stopped it.
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
            AcousticPathData path;
            if (_acousticWorker.TryGetResult(snap.Id, out var paths)) path = paths.FirstOrDefault(p => !p.IsReflection);
            else
            {
                var at = OpenFPS.Common.AudioEmission.PointFor(snap);
                path = new AcousticPathData(0f, at, Vector3.Distance(eyePos, at));
            }
            SirenVoice(snap, sirenKey, path, world.PositionsSampledAt);
        }
        _sirensGone.Clear();
        foreach (int id in _sirenVoiced) if (!_sirenCars.Contains(id)) _sirensGone.Add(id);
        foreach (int id in _sirensGone)
        {
            _sirenVoiced.Remove(id);
            _audio.StopSound(SirenVoiceBase - Math.Abs(id));
        }
    }

    /// <summary>Voice ids for a vehicle's horn, one per vehicle.</summary>
    internal const int HornVoiceBase = -1_200_000;

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
            AcousticPathData path;
            if (_acousticWorker.TryGetResult(id, out var paths)) path = paths.FirstOrDefault(p => !p.IsReflection);
            else
            {
                var at = OpenFPS.Common.AudioEmission.PointFor(snap);
                path = new AcousticPathData(0f, at, Vector3.Distance(eyePos, at));
            }
            // Behind a car's grille, a little above the bumper; on a locomotive's cab roof.
            bool rail = snap.Definition.SoundEmitter.SoundId?.StartsWith("rail:", StringComparison.OrdinalIgnoreCase) == true;
            Vector3 pos = snap.Transform.Position
                        + Vector3.Transform(rail ? new Vector3(0f, 4.2f, 0f) : new Vector3(0f, 0.6f, 1.9f),
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
                ApparentPosition = path.ApparentPosition == Vector3.Zero ? pos : path.ApparentPosition,
                Velocity = snap.Velocity,
                PositionSampledAt = world.PositionsSampledAt,
                Direction = Vector3.Transform(Vector3.UnitZ, snap.Transform.Rotation),
                Volume = gain,
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
                EnableReverb = true,
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
    private void SirenVoice(EntitySnapshot snap, string sirenKey, in AcousticPathData path, double sampledAt)
    {
        OpenFPS.Common.SirenSpec spec;
        try { spec = OpenFPS.Common.SirenSpec.ByName(sirenKey); }
        catch { return; }

        float speed = snap.Velocity.Length();
        var mode = SirenModeFor(snap.Id, speed, sampledAt);
        int voiceId = SirenVoiceBase - Math.Abs(snap.Id);
        if (mode == OpenFPS.Common.SirenMode.Off)
        {
            if (_sirenVoiced.Remove(snap.Id)) _audio.StopSound(voiceId);
            return;
        }

        // At the grille, which is where the horn is.
        Vector3 pos = snap.Transform.Position
                    + Vector3.Transform(new Vector3(0f, 0.4f, 1.9f), snap.Transform.Rotation);
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
            ApparentPosition = pos,
            Velocity = snap.Velocity,
            PositionSampledAt = sampledAt,
            // Which way the horn points. Without it the machine frame falls back to the VELOCITY,
            // which is the right answer while the car is moving and no answer at all when it slows
            // for a junction — exactly when a siren matters most. The car's own rotation always
            // knows.
            Direction = Vector3.Transform(Vector3.UnitZ, snap.Transform.Rotation),
            Volume = gain,
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
            EnableReverb = true,
        };
        ApplyGround(ref e, _groundWorld);
        if (_audio.IsPlaying(voiceId)) _audio.UpdateSpatialAttributes(e);
        else { _audio.PlayPhysicalSoundDirect(e); _sirenVoiced.Add(snap.Id); }
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
    /// drive. The first version read the instantaneous deceleration and flipped between wail and
    /// yelp at every corner of a city lap.
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
            EnableReverb = true,
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
        // On the first frame before the worker has a result, fall back to an unoccluded direct path.
        AcousticPathData acousticPath;
        if (_acousticWorker.TryGetResult(snap.Id, out var cachedPaths))
        {
            acousticPath = cachedPaths.FirstOrDefault(p => !p.IsReflection);
        }
        else
        {
            Vector3 fallbackSource = OpenFPS.Common.AudioEmission.PointFor(snap);
            float directDist = Vector3.Distance(eyePos, fallbackSource);
            acousticPath = new AcousticPathData(0f, fallbackSource, directDist);
        }

        string resolvedSoundId = "";
        bool interior = false;
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
                // Placed on its own declared level and its own size, the same way a vehicle is.
                // The extent is what stops a window unit being a point source you can walk into:
                // inside its own half-metre the level is flat, and the gain is paid down to match so
                // the far field is unchanged. See Loudness.Widen — widening without paying is how
                // the engine once handed every quiet vehicle eight decibels it had not earned.
                var (gain, reference) = OpenFPS.Common.Loudness.Place(levelDb, extent);
                engineVolume = gain * def.SoundEmitter.Volume;
                engineMinDistance = reference;
                engineExtent = extent;
                engineRange = MathF.Max(engineRange, OpenFPS.Common.Loudness.AudibleRange(levelDb));
                if (physicalKey.StartsWith("aircraft:", StringComparison.OrdinalIgnoreCase))
                {
                    (powerLever, rotorWake) = FlightPower(snap.Velocity);
                    onGround = OnTheWheels(snap, world, eyePos);
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

                // Close enough to hear which end is which: this voice moves back to the TAILPIPE and
                // the front of the machine gets a voice of its own at the airbox. Further away the
                // voice stays where it has always been — between the two, biased toward the exhaust
                // (VehicleProfile.ExhaustEmitterBias) — because that is the honest position for a
                // machine being heard as one thing.
                if (_frontVoiced.Contains(snap.Id))
                    emitterPosition = snap.Transform.Position
                                    + Vector3.Transform(ExhaustSlot(profile), snap.Transform.Rotation);

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
            // rotation. Zero (the default, and what every prefab produced before the field was authorable)
            // means "straight ahead", which is the old behaviour exactly.
            Direction = Vector3.Transform(
                def.SoundEmitter.Direction.LengthSquared() > 0f
                    ? Vector3.Normalize(def.SoundEmitter.Direction)
                    : Vector3.UnitZ,
                snap.Transform.Rotation),
            Volume = engineVolume,
            Range = Math.Max(1.0f, engineRange),
            Pitch = 1.0f,
            Type = EmitterType.EntityAttached,
            IsReflection = false,
            // Inside, its room is yours — the cabin — whatever room the car's middle is in.
            TargetRegionId = interior ? _listenerRegion : acousticPath.RegionId,
            EnableReverb = true,
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
            ListenerOffset = interior ? Vector3.Transform(new Vector3(0f, -0.4f, 0.6f), snap.Transform.Rotation) : Vector3.Zero,
            PhysicalKey = physicalKey,
            PowerLever = powerLever,
            RotorWake = rotorWake,
            OnGround = onGround,
            EngineSpeed = snap.Velocity.Length(),
            // Whether a synthesised source is SOUNDING. Almost everything in this world decides
            // that for itself from what the client can observe — an engine from its speed, a siren
            // from the car's behaviour, an aeroplane's power from its climb angle. A level
            // crossing's bell cannot: it rings because of where a train is on a line the listener
            // may be a kilometre from. So that one comes down the wire, and it defaults to true, so
            // every other emitter means exactly what it meant before.
            EngineRunning = def.SoundEmitter.SynthRunning,
            ServingStop = def.SoundEmitter.ServingStop,
            // Straight from the server, which is the only thing that knows the corner.
            //
            // It used to be differentiated here from the interpolated velocity and divided by the
            // tyre's FLAT-ground grip — and a banked corner is indistinguishable from a flat one in a
            // velocity, because the bank shows up in the normal load and not in the kinematics. On
            // the speedway that read 1.43 to 1.59 against a full-slide threshold of 1.45, so every
            // car in every corner rendered pure broadband skid for the length of both turns. Heard as
            // a long white-noise tail travelling with the field.
            TyreSlip = snap.TyreDemand,

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
        _audio.Submit(emitter);

        // The other end of the machine, when it is close enough to be a second thing. Placed after
        // the machine's own voice, so an engine that has only just been built already exists for the
        // tap to read.
        if (engineKey.Length > 0 && _frontVoiced.Contains(snap.Id))
            FrontVoice(snap, OpenFPS.Common.MachineRegistry.VehicleFor(engineKey), acousticPath,
                       engineVolume, engineMinDistance, Math.Max(1.0f, engineRange), world.PositionsSampledAt);

        // The siren is NOT placed here: see UpdateSirens.

        // 6.1. The walls answering this engine. A live engine has no file to replay, so its
        // reflections are read back out of the synthesis's own ring buffer at the delay the mirrored
        // path implies — see EngineReflections.
        if (engineKey.Length > 0)
        {
            long echoAt = System.Diagnostics.Stopwatch.GetTimestamp();
            _engineEchoes.Update(snap.Id, emitter, acousticPath, eyePos, AudioPhysics.SpeedOfSound, engineDt, _audio);
            _partMs[0] += Ms(echoAt);
        }

        // No floor slapback and no "cone reflection" here any more, and the absence is the fix.
        //
        // Two generators used to live at this point. One cast a ray straight down from the emitter
        // and started a second playback of its sound at the hit, pitched down two per cent. The other,
        // for a directional source, cast a ray along its beam and started a second playback at
        // whatever that hit. Both were the mechanism EarlyReflections retired for the walls: another
        // independent read of the same file, at an unrelated position in it — for a looping
        // announcement, the announcement again. And both rays tested every collider, solid or not,
        // including the emitter's own: a ray that starts inside a box hits it at distance zero. So
        // the megaphone's "floor" and "wall" reflections both sat AT the megaphone, at 30 and 40 per
        // cent, never occluded (derived ids are skipped by the acoustic pass), one of them drifting
        // slowly against the original because of the pitch shift. Reported exactly: "I hear like 2
        // copies, one latent like it is echoing off something way far away... if I stand by the
        // megaphone I hear it repeat softer but in the same place."
        //
        // The floor is a box face and so is the wall the beam points at. The image-source pass
        // already mirrors the source through both, with the material's absorption and the extra
        // path, and renders a copy only when the ear would hear one as a separate event.
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

    private int _footstepPoolIndex = 0;
    // Larger pool so rapid footsteps rarely reuse an ID while the previous step is still playing — reusing
    // an active voice hard-cuts it (click). 12 IDs gives plenty of headroom at running cadence.
    private const int FOOTSTEP_POOL_SIZE = 12;
    /// <summary>
    /// The room the listener was last found in, for the sounds the BODY makes.
    ///
    /// A SpatialEmitter's TargetRegionId defaults to -1, which is the outdoors, and a footstep is
    /// built between updates on the game thread where nothing has worked out a region. So every
    /// footfall sent its reverberation to the OUTDOOR bus and gave the room the player was standing
    /// in only the small cross-send meant for a sound in the NEXT room — a quarter of it.
    ///
    /// What that produces is a tail that is the same length wherever you are, because it is the same
    /// bus wherever you are. Reported after the rooms had been measured and the measurements shown to
    /// be right: "the tail on the reverb is the same no matter where I am in the stairs, corridor or
    /// parking garage... the walls might be farther away but the decay is short".
    /// </summary>
    private int _listenerRegion = AcousticConstants.GlobalRegionId;

    private const int FOOTSTEP_BASE_ID = -100;

    /// <summary>Somebody else's step: a sound at a place in the world, left there as they walk on.</summary>
    public void OnPlayerFootstep(Vector3 pos, string mat, string var)
        => SubmitFootstep(pos + new Vector3(0, 0.1f, 0), mat, follows: false, offset: Vector3.Zero, boostDb: 0f);

    /// <summary>
    /// How much louder your OWN footstep is to you than the same footstep is to a bystander standing
    /// where your ears are. A bystander hears it through the air only. You hear it through the air
    /// AND through your skeleton — the heel strike travels up the leg and the spine into the skull,
    /// which is why a footstep on a hard floor is felt as much as heard, and why your own steps
    /// stay obvious in a street where a stranger's are not. This is that second path. It applies to
    /// nothing but your own body; every other footstep in the world is the air path alone.
    /// </summary>
    private const float OwnFootstepBoneConductionDb = 8f;

    /// <summary>
    /// Your OWN step. It is part of you, so it rides with you.
    ///
    /// Your feet are not somewhere in the world that you then walk away from; they are under your
    /// head, and stay there. Placed as a world-locked sound at the physics position, a step was put
    /// down wherever the predicted position and the server's disagreed at that instant — the
    /// listener stands at the smoothed VisualPosition, the step was at Position — and then left
    /// behind as the listener moved on through its two or three hundred milliseconds. In the log a
    /// single step's bearing went from straight down to thirty degrees behind while it played.
    /// Reported as "the footsteps slide all around me; if I move right I hear them trailing to the
    /// left", and heard, step by step, as a click from somewhere off to one side. So an own step is
    /// placed at a fixed offset from the listener's head and follows it, whatever the network is
    /// doing to the position underneath.
    /// </summary>
    /// <summary>Tracing for OPENFPS_AUDIO_DEBUG=1 — what your own feet did, and when. A landing is a
    /// heavier sound than a step and fires at most twice a second, so "periodic bangs" is a question
    /// this line answers outright.</summary>
    private static readonly bool _footTrace = Environment.GetEnvironmentVariable("OPENFPS_AUDIO_DEBUG") == "1";

    public void OnOwnFootstep(Vector3 pos, string mat, string var)
    {
        if (_footTrace) Log.Information("[FOOT] step on {Mat} at {Pos}", mat, pos);
        Vector3 offset = (pos - _state.Position) + new Vector3(0, 0.1f - _state.EyeHeight, 0);   // the foot, from the eye
        SubmitFootstep(_state.VisualPosition + new Vector3(0, _state.EyeHeight, 0) + offset, mat, follows: true, offset: offset,
                       boostDb: OwnFootstepBoneConductionDb);
    }

    private void SubmitFootstep(Vector3 nudgePos, string mat, bool follows, Vector3 offset, float boostDb)
    {
        int id = FOOTSTEP_BASE_ID - (_footstepPoolIndex % FOOTSTEP_POOL_SIZE);
        _footstepPoolIndex++;

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
        var (stepGain, stepReference) = OpenFPS.Common.Loudness.Place(OpenFPS.Common.Loudness.FootstepDb + boostDb);

        // 1. Direct Sound (Will now undergo full acoustic pathing)
        var footstep = new SpatialEmitter
        {
            EntityId = id,
            SoundId = resolvedSoundId,
            Position = nudgePos,
            FollowsListener = follows,
            ListenerOffset = offset,
            Type = EmitterType.WorldLocked,
            Volume = stepGain,
            Range = 15.0f,
            // Your own feet, pinned above the physics: they are how you know you are moving, and on
            // a loud map the arithmetic would rightly bury them under everything else.
            Essential = true,
            IsEvent = true,
            MinDistance = stepReference,
            // The room the body is standing in, so its reverberation is THAT room's.
            TargetRegionId = _listenerRegion,
        };
        _audio.Submit(footstep);

        SubmitStepReflections(nudgePos, resolvedSoundId, stepGain, stepReference);
    }

    /// <summary>Voices for the surfaces answering your own footfalls. Their own pool, so a wall's copy
    /// can never take the slot of the step it is a copy of.</summary>
    private const int STEP_ECHO_BASE_ID = -200;
    private const int STEP_ECHO_POOL_SIZE = 16;
    private int _stepEchoIndex;

    /// <summary>
    /// How many surfaces answer one footfall.
    ///
    /// Few on purpose. What a listener needs from a room is the nearest two or three arrivals — the
    /// floor, whatever is over your head, the wall you are walking along — because those are the ones
    /// close enough and loud enough to say where they are. Past that the arrivals are too many and too
    /// close together to have a direction any more, which is what the diffuse bus is for.
    /// </summary>
    private const int StepEchoTaps = 3;

    /// <summary>
    /// The walls answering your own footsteps.
    ///
    /// Footsteps used to spawn none of these. The comment that stood here said per-step geometric
    /// reflections had been tried and "scattered the sound all over the place in enclosed rooms",
    /// and that the reverb bus would give them their indoor character instead. It does not, and it
    /// cannot: a bus is DIFFUSE. Reported from the chair, walking the city's car park —
    ///
    ///   "a parking garage is big, this sounds like a box... rather than reflections being emitted
    ///    from the walls, it's like the whole room is reverby... it surrounds me rather than being
    ///    directional. I should hear reflections from a wall, from the wall's direction, not an all
    ///    around reflection from everywhere at once."
    ///
    /// — which is exactly right, and exactly what direct-plus-diffuse-tail with nothing in between
    /// sounds like. The near-field probes already give the last three metres (and were reported as
    /// working: "only when I get close to walls do I hear the proximity, which is good"); the diffuse
    /// bus gives the tail. Everything from three metres to the size of the room was missing, and in a
    /// twenty-one by twenty-eight metre garage that is the whole room.
    ///
    /// What fills it is the machinery that already answers for world events and engines: first-order
    /// image sources off the surfaces actually there, each played from the mirrored position so it
    /// arrives FROM ITS OWN WALL, delayed by its own extra path. A ceiling nine hundred millimetres
    /// over your head answers in five milliseconds and is most of why a low garage sounds low; a wall
    /// ten metres off answers in fifty-five and is the slap. Neither is a tail.
    ///
    /// The old failure is guarded against by the two things that were missing then rather than by
    /// refusing to do it: only a handful of taps, and a level that is the SURFACE's loss alone — the
    /// distance is applied by the engine when it places the copy at the image position, so a copy off
    /// a far wall is quiet because it is far, not because anybody scaled it.
    /// </summary>
    /// <summary>Is the listener in a room (a closed boundary) rather than out of doors?</summary>
    private bool ListenerEnclosed(WorldSnapshot world, Vector3 at)
        => world.AcousticMap != null
           && world.AcousticMap.Regions.TryGetValue(_listenerRegion, out var room)
           && RoomAcoustics.IsEnclosure(room);

    private void SubmitStepReflections(Vector3 stepPos, string soundId, float stepGain, float stepReference)
    {
        if (_engineEchoes.SurfaceCount == 0) return;
        // Traced: your own step is a sound where you stand, and the traced response from where you
        // stand is exactly its reflections, off every surface round you, with their materials.
        if (OpenFPS.Client.AudioEngine.Fmod.FmodAudioProvider.TracedActive) return;

        Vector3 ear = _state.VisualPosition + new Vector3(0, _state.EyeHeight, 0);
        Span<OpenFPS.Common.Reflection> found = stackalloc OpenFPS.Common.Reflection[OpenFPS.Common.EarlyReflections.MaxArrivals];
        int n = _engineEchoes.FindReflections(stepPos, ear, AudioPhysics.SpeedOfSound, found, diffuseTaps: 1);
        if (n <= 0) return;

        float direct = MathF.Max(0.5f, Vector3.Distance(stepPos, ear));
        int taps = 0;
        for (int i = 0; i < n && taps < StepEchoTaps; i++)
        {
            var r = found[i];
            // What the SURFACE took, with the distance divided back out: the voice is placed at the
            // image position, so the engine applies the path's own falloff. Multiplying both in would
            // count the distance twice and is how a reflection ends up inaudible.
            float surfaceGain = Math.Clamp(r.Gain * r.PathLength / direct, 0f, 1f);
            if (surfaceGain < OpenFPS.Common.ImageSource.MinGain) continue;

            int echoId = STEP_ECHO_BASE_ID - (_stepEchoIndex % STEP_ECHO_POOL_SIZE);
            _stepEchoIndex++;
            taps++;

            _audio.Submit(new SpatialEmitter
            {
                EntityId = echoId,
                SoundId = soundId,
                Position = r.ApparentPosition,
                ApparentPosition = r.ApparentPosition,
                Type = EmitterType.WorldLocked,
                Volume = stepGain * surfaceGain,
                MinDistance = stepReference,
                Range = 25f,
                // From the wall, later than the step, and NOT pinned to the listener: a reflection
                // stays where the wall is while you walk on, which is the whole of what makes it a
                // wall rather than a part of you.
                DelayMs = r.DelaySeconds * 1000f,
                IsReflection = true,
                IsEvent = true,
                ReflectionSpread = r.IsDiffuse ? 1f : 0f,
                TargetRegionId = _listenerRegion,
            });
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
    private void ApplyGround(ref SpatialEmitter e, WorldSnapshot? world)
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
            _groundCache[e.EntityId] = c;
        }
        if (!c.Found) return;
        float gb = c.Height;
        _groundMat[0] = c.Material;
        if (gb > MathF.Min(src.Y, ear.Y)) return;                // nothing to bounce off below both

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
        e.GroundDelaySeconds = (mirrored - direct) / AudioPhysics.SpeedOfSound;
        e.GroundLowGain = low * spread;
        e.GroundHighGain = high * spread;
    }

    /// <summary>Turbulent coherence between the direct and ground paths (Clifford-Lataitis).</summary>
    internal static float Coherence(float hz, float distance, float rho)
    {
        const float Mu2 = 5e-6f, OuterScale = 1.1f;
        float k = 2f * MathF.PI * hz / AudioPhysics.SpeedOfSound;
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

        // And that is all. This used to rebuild the acoustic map when the floor's Sabine estimate
        // moved — which tears down every reverb bus on the map, every Steam Audio voice attached to
        // them and every send into them, mid-tail: the loudest discontinuity the engine can make, on
        // a footstep. It is not needed. The tail's time, colour and level are surveyed from the boxes
        // round the listener every few ticks (Enclosure.Look), and the floor underfoot is one of
        // those boxes, so what you are standing on already colours the room. The region's material
        // is kept current here for anything that still reads the Sabine estimate at bus creation.
    }

    private int _breathSeed;

    /// <summary>
    /// A body breathing, through the same channel as every other short sound in the world.
    ///
    /// Nobody recorded any of these. A breath is turbulent air through a narrow opening — a hiss —
    /// and the transient synthesiser has known how to make one of those since doors did. Routing it
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

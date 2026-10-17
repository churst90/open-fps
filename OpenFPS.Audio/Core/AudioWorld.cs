using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Sound;
using OpenFPS.Sound.Core;
using OpenFPS.Audio.Acoustics;
using OpenFPS.Audio.Data;
using Serilog;
using OpenFPS.Geometry;
using OpenFPS.Acoustics;
using OpenFPS.Materials;
using OpenFPS.Physics;
using OpenFPS.Environment;

namespace OpenFPS.Audio.Core;

/// <summary>
/// An audio world (docs/SOUND_LIBRARY_BOUNDARY.md 7.2, stage 7): turns the host's sources into sounds, their
/// emitters, their acoustic paths, and the budgets that decide which of them get a voice. The host hands it the
/// world input and the sources once a frame (<see cref="Tick"/>), the listener and its own parts of the frame
/// through <see cref="IAudioHost"/>, and the one-off events (a footstep, a horn) as they come.
/// </summary>
public partial class AudioWorld
{
    private readonly AudioEngineFacade _audio;
    private readonly IRecordings _sounds;
    private readonly IAudioHost _host;
    private readonly SpatialAcoustics _acoustics;

    /// <summary>The acoustics this world's paths come from, for the host's features that place sounds on the
    /// same terms (beacons, birds).</summary>
    public SpatialAcoustics Acoustics => _acoustics;

    /// <summary>
    /// The short sounds the world reports: a door shutting, glass landing, a round striking a wall, thunder.
    /// The world's own rather than beside the host's network code because a transient takes the same
    /// occlusion, reverb and placement as everything else.
    /// </summary>
    public OneOffSounds OneOffs => _oneOffs;
    private readonly OneOffSounds _oneOffs;

    /// <summary>The birds: found from the map's foliage and roofs, not placed. See BirdLife.</summary>
    public BirdLife Birds => _birds;
    private readonly BirdLife _birds;
    private readonly AsyncAcousticWorker _acousticWorker;
    private readonly HashSet<string> _preloadedSounds = new();

    private int _ownEntityId = -1;
    /// <summary>The listener's own body, set at spawn; passed to the hand-rolled path so the rays ignore it.</summary>
    public int OwnEntityId
    {
        get => _ownEntityId;
        set { _ownEntityId = value; _acoustics.ListenerBodyId = value; }
    }
    
    /// <summary>The last frame's world input.</summary>
    private IAcousticWorld? _lastWorld;

    /// <summary>The listener as the host has it now: read where it is needed, so a step taken between frames
    /// is placed from where the listener is then.</summary>
    private ListenerState Listener => _host.Listener;

    /// <summary>This frame's sources, as the host filled them.</summary>
    private IAudioSources _sources = new NoSources();
    private AcousticMap? _lastAcousticMap;
    private int _frameCount = 0;

    /// <summary>The walls answering the live engines. See EngineReflections.</summary>
    private readonly EngineReflections _engineEchoes = new();



    /// <summary>The voice id of one image-source reflection slot of a source. A slot keeps its wall
    /// (<see cref="AssignReflectionSlots"/>).</summary>
    private int ReflectionVoiceId(int sourceId, int slot) => _voices.IdFor(VoiceKind.Reflection, sourceId, slot);

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
    private static bool PlaysCopies(AudioSource src)
    {
        string sound = src.Spec.SoundId ?? "";
        return !src.Spec.IsSynth && !OpenFPS.Common.ModelKindTable.IsVehicle(sound);
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

    /// <summary>How fast the ear is going this frame, m/s: how far from here an answer traced a moment ago
    /// may have been asked (<see cref="AcousticPathData.Answers"/>).</summary>
    private float _earSpeed;

    /// <summary>The cars in the order engines go to them: the one you sit in (tier 0), those whose engine has
    /// just started (1), the rest (2); within a tier the loudest at the ear first (the key is its level, negated,
    /// a sounding car's with its keep bias), ties by id.</summary>
    private readonly List<(int Id, int Tier, float Key, float D2)> _engineOrder = new();
    private double _lastEngineTime;

    /// <summary>
    /// A sounding car ranks as if at this share of its distance (2.5 dB up), so a silent one must be decisively
    /// louder to take its voice. Ranking with no bias churns: on an oval two cars swap order several times a
    /// lap, and each swap restarts a synth with cold pipes and a stopped crank, heard as a car stuttering.
    /// </summary>
    private const float EngineKeepBias = 0.75f;

    /// <summary>No engine is dropped within this long of being started, whatever the ranking says, so
    /// a fast car crossing the boundary is not started and stopped inside a second.</summary>
    private const double EngineMinimumHoldSeconds = 2.5;

    /// <summary>
    /// How often the host ticks the world, Hz: what the voice registry counts its frames in. open-fps's heads
    /// cap their audio update here (ClientAudioSystem.UpdateHz). Their loop polls the network at about 200 Hz
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

    /// <summary>The most voices of each kind this world plays (VoiceBudgets), its options'.</summary>
    private readonly VoiceBudgets _budgets;
    private int EngineVoiceBudget => _budgets.Engines;
    private int EchoCeiling => _budgets.Echoes;
    private int FrontVoiceBudget => _budgets.Front;
    private int PlaceVoiceBudget => _budgets.Places;
    private int MachineVoiceBudget => _budgets.Machines;

    /// <summary>
    /// The engine budget in force, steered by the measured mixer load: what an engine costs depends on
    /// everything else in the map, and a mixer at 100 % misses its deadline and crackles. It gives things
    /// up in order, a car's second reflection, then its first, and only then a car: shedding cars first
    /// sounds like voices being swapped, while nobody notices a wall stop answering. The floor is two
    /// cars; one engine on a racetrack is not a race.
    /// </summary>
    private int _adaptiveBudget;

    /// <summary>Producer starves per second above which live engines are given up.</summary>
    private const float StarveCeilingPerSecond = 5f;
    private float _starveRate;
    private int _starvesSeen;
    private double _starveSampledAt = -1;
    private int _adaptiveEchoes;
    private double _lastBudgetChange;

    /// <summary>However short the mixer runs, this many cars are fully SYNTHESIZED.</summary>
    private const int MinEngineVoices = 2;

    /// <summary>Which source each borrowing car's voice is currently bound to, by car, so a change can rebind it.</summary>
    private readonly Dictionary<int, int> _distantBoundTo = new();

    /// <summary>The voice a car too far off for its own engine borrows another's under.</summary>
    private int BorrowedVoiceId(int carId) => _voices.IdFor(VoiceKind.Borrowed, carId);
    /// <summary>A machine's front outlet's voice.</summary>
    private int IntakeVoiceId(int carId) => _voices.IdFor(VoiceKind.Intake, carId);
    /// <summary>A vehicle's siren head's voice. Its own voice; see SirenVoiceState.</summary>
    private int SirenVoiceId(int carId) => _voices.IdFor(VoiceKind.Siren, carId);

    private int _adaptiveFront;
    private int _adaptivePlaces;
    private const int PlaceBudgetStep = 6;

    /// <summary>Which machines currently have their front outlet on a voice of its own.</summary>
    private readonly HashSet<int> _frontVoiced = new();
    /// <summary>Which vehicles currently have a siren voice running.</summary>
    private readonly HashSet<int> _sirenVoiced = new();

    /// <summary>How many engines may be BUILT in one audio update. See ChooseLiveEngines.</summary>
    private const int NewEnginesPerUpdate = 2;

    private int _adaptiveMachines;

    /// <summary>However short the mixer runs, this many machine voices are kept, and the loudest machine
    /// keeps its voice whatever it costs: the one you are standing next to is the one you would notice
    /// going silent.</summary>
    internal const int MinMachineVoices = 6;

    /// <summary>Nothing at all, when the budget is zero: the adaptive floor must not put one back.</summary>
    private int MachineFloor => MachineVoiceBudget == 0 ? 0 : MinMachineVoices;

    /// <summary>Which physical models have a live voice now (and, below, when each started).</summary>
    private readonly HashSet<int> _liveMachines = new();
    /// <summary>The ids whose acoustic path is asked for this frame. See step 5.</summary>
    private readonly HashSet<int> _pathIds = new();
    private readonly Dictionary<int, double> _machineStarted = new();
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
    /// <summary>
    /// And this far under the ceiling, held for <see cref="RestoreAfterSeconds"/>, there is room to take a
    /// voice back: a voice costs the mixer about 0.3 % (60 voices at 60 %, 200 at 100 %). Not an absolute
    /// floor: under what the city costs with little playing, nothing given up comes back
    /// (docs/CLIENT_NOTES.md, "The budgets").
    /// </summary>
    private const float RestoreMargin = 0.08f;
    private const double RestoreAfterSeconds = 3.0;
    private const double BudgetSettleSeconds = 1.0;
    private double _underRestoreSince = -1;
    /// <summary>
    /// A voice taken back and given up again within <see cref="RestoreProbationSeconds"/> was one too
    /// many: the next restore waits twice as long (from 15 s up to four minutes), so the budget settles
    /// instead of a voice coming and going every couple of seconds at the ceiling.
    /// </summary>
    private const double RestoreProbationSeconds = 15.0;
    private double _lastRestoreAt = double.NegativeInfinity;
    private double _restoreBackoff = 15.0;
    private double _restoreAllowedAt;
    /// <summary>A voice was given up: if it was taken back only just now, the next restore waits longer.</summary>
    private void GaveUp(double now)
    {
        if (now - _lastRestoreAt < RestoreProbationSeconds) _restoreBackoff = Math.Min(240.0, _restoreBackoff * 2.0);
        else if (now - _lastRestoreAt > 120.0) _restoreBackoff = 15.0;
        _restoreAllowedAt = now + _restoreBackoff;
    }

    /// <summary>Binaural voices free under which the budgets give a voice up as they do for the mixer:
    /// past the pool, a voice plays flat and in the middle of the head.</summary>
    private const int HrtfLowWater = 6;

    /// <summary>
    /// The budget is left alone this long after a map load, and after that the load must stay over the
    /// ceiling this long together before anything is given up. During a load FMOD's dsp percentage pins at
    /// 100 % while the mixer is stalled (a GC, a producer it waits on), which is not a shortage: shedding
    /// then and re-adding a second later builds new engines at the worst moment, heard as cars appearing
    /// and vanishing on the first lap.
    /// </summary>
    private const double BudgetHoldSeconds = 30.0;
    private const double OverCeilingSeconds = 0.75;
    private double _budgetHeldUntil;
    private double _overCeilingSince = -1;

    /// <summary>Called when a map starts loading and again at spawn: the mixer's load reading cannot be
    /// trusted for a while (BudgetHoldSeconds): on the city the bake, the scene build and the decodes hold
    /// the mixer at 74-108 % for the first twenty-five seconds.</summary>
    public void NoteSceneLoading() => _budgetHeldUntil = _now() + BudgetHoldSeconds;
    /// <summary>Seconds since this system was built. A stopwatch in the game; a test hands in its
    /// own, so it can tick the throttled update and step past the engine hold without sleeping.</summary>
    private readonly Func<double> _now;

    // Near-field boundary probes: rebuilt each frame from the listener's rotation (ProbeDirections is
    // head space) into buffers owned and reused here, since this runs every audio frame.
    private readonly BoundaryProber _boundaryProber = new();
    private readonly BoundaryProbe[] _boundaryProbes = new BoundaryProbe[BoundaryModel.ProbeDirections.Length];


    /// <param name="audio">The engine to play through.</param>
    /// <param name="recordings">The host's recordings: which take a sound id plays, a footfall's category.</param>
    /// <param name="host">The listener, the host's parts of each frame and its answers.</param>
    /// <param name="clock">Seconds, as the host steps them (a stopwatch in the game).</param>
    /// <param name="manualAcoustics">The acoustic worker runs no thread: the caller steps it between
    /// ticks (<see cref="AsyncAcousticWorker.StepForTest"/>), the rain survey runs in place, and no loudspeaker
    /// renders start in the background. For the emitter-stream replay, which must come out the same on every run.</param>
    /// <param name="seed">Seeds what the world leaves to chance (near rain drops, a footstep's jitter). Null:
    /// unseeded, as in the game.</param>
    /// <param name="prewarm">Render the city's doors and a strike of thunder in the background (OneOffSounds): the
    /// game. A test that plays no door has no use for minutes of a core.</param>
    public AudioWorld(AudioEngineFacade audio, IRecordings recordings, IAudioHost host, Func<double> clock,
                      bool manualAcoustics = false, int? seed = null, bool prewarm = false)
    {
        _now = clock;
        _audio = audio;
        _budgets = audio.Options.Budgets;
        _adaptiveBudget = EngineVoiceBudget;
        _adaptiveEchoes = EchoCeiling;
        _adaptiveFront = FrontVoiceBudget;
        _adaptivePlaces = PlaceVoiceBudget;
        _adaptiveMachines = MachineVoiceBudget;
        _sounds = recordings;
        _host = host;
        _acoustics = new SpatialAcoustics { Clock = audio.Clock, Atmosphere = audio.Atmosphere };
        // Shares this world's acoustics, so a rendered latch takes exactly the path a recorded one would.
        _oneOffs = new OneOffSounds(audio, _acoustics, prewarm: prewarm && !manualAcoustics);
        _oneOffs.HornReceived = StartHorn;
        _oneOffs.TrainSignalReceived = StartTrainSignal;
        _oneOffs.Ground = ApplyRecordedGround;
        // Every world voice is started, placed and stopped through the registry, on this mixer.
        _voices.Mixer = audio;
        _oneOffs.Voices = _voices;
        _rain = new RainField(audio, _acoustics, seed) { SurveyInPlace = manualAcoustics };
        // What the library's sources voice, declared as theirs: each carries its own path.
        _rain.Voiced = (id, drop) =>
        {
            if (drop) _voices.DeclareEvent(id, VoiceRegistry.NoEntity, VoiceKind.RainDrop, Vector3.Zero);
            else _voices.Declare(id, VoiceRegistry.NoEntity, VoiceKind.Rain, Vector3.Zero);
        };
        _engineEchoes.Voiced = (id, engine) => _voices.Declare(id, engine, VoiceKind.EngineEcho, Vector3.Zero);
        // The library's voices are given their ids here, by what they are the sound of.
        _rain.SlotVoice = (slot, part) => (_voices.IdFor(VoiceKind.Rain, slot, part), VoiceRegistry.SeedOf(VoiceKind.Rain, slot, part));
        _rain.DropVoice = n => (_voices.IdFor(VoiceKind.RainDrop, VoiceRegistry.NoEntity, n), VoiceRegistry.SeedOf(VoiceKind.RainDrop, VoiceRegistry.NoEntity, n));
        _engineEchoes.VoiceOf = (engine, n) => (_voices.IdFor(VoiceKind.EngineEcho, engine, n), VoiceRegistry.SeedOf(VoiceKind.EngineEcho, engine, n));
        _stepRandom = seed is int s ? new Random(s) : Random.Shared;
        _audio.RoutesSource = () => _acoustics.Routes;
        _loudspeakers = new LoudspeakerVoices(audio) { RenderInPlace = manualAcoustics };
        _acousticWorker = new AsyncAcousticWorker(_acoustics, audio.Traced, audio.Clock) { Manual = manualAcoustics, Lab = audio.Lab };
        _oneOffs.Worker = _acousticWorker;
        // The birds: found from the map's foliage and roofs, heard by the worker's answers (P6), quiet at a bang.
        _birds = new BirdLife(audio, _acoustics, seed) { Voices = _voices, Worker = _acousticWorker };
        _oneOffs.Received = sounds => _birds.Heard(sounds, _audio.Clock.Now);
        // The loudest surface of each rain patch is heard by the worker's answer, as every sustained voice is (P6).
        _rain.Worker = _acousticWorker;
        _rain.SlotProbe = slot => _voices.IdFor(VoiceKind.Probe, VoiceRegistry.NoEntity, VoiceRegistry.RainProbe(slot));
        _acousticWorker.Start();
    }

    /// <summary>The acoustic worker, for the host's features that are heard by its answers (blips, perched birds)
    /// and for a test that steps it (<see cref="AsyncAcousticWorker.Manual"/>).</summary>
    public AsyncAcousticWorker AcousticWorker => _acousticWorker;

    /// <summary>A footstep's level and pitch jitter: the shared generator in the game, seeded in a replay.</summary>
    private readonly Random _stepRandom;

    /// <summary>A model was changed (the host's world editor): the levels and headroom read from models are
    /// the old model's, and forgotten. The host then forgets each source that plays it (<see cref="ForgetEntity"/>).</summary>
    public void ModelsChanged()
    {
        _physicalLevels.Clear();
        _physicalHeadroom.Clear();
    }

    /// <summary>
    /// Silences everything an entity made sound with, once the server says it is gone. A voice keeps
    /// playing on its own once started, so a looping emitter on a despawned object would otherwise stay
    /// for the session; the voices derived from it (reflections, outlets, places) carry ids of their own.
    /// </summary>
    public void ForgetEntity(int entityId)
    {
        _groundCache.Remove(entityId);
        _linePaths.Remove(entityId);
        _voices.Stop(entityId);
        // A wood's voice is under an id of its own.
        if (_voices.TryIdOf(VoiceKind.Wood, entityId, 0, out int wood))
        {
            _groundCache.Remove(wood);
            _voices.Stop(wood);
        }
        _liveEngines.Remove(entityId);
        _engineStarted.Remove(entityId);
        _engineEchoes.Forget(entityId, _audio);
        _reflectionSlots.Remove(entityId);
        if (_distantBoundTo.Remove(entityId)) _voices.Stop(BorrowedVoiceId(entityId));
        if (_frontVoiced.Remove(entityId)) _voices.Stop(IntakeVoiceId(entityId));
        else if (_voices.TryIdOf(VoiceKind.Intake, entityId, 0, out int intake)) _voices.Stop(intake);
        if (_cabinVoiced == entityId)
        {
            for (int p = 1; p < _cabinVoicedPaths; p++) _voices.Stop(CabinVoiceId(p));
            _cabinVoiced = int.MinValue; _cabinVoicedPaths = 0;
        }
        if (_spreading.Remove(entityId, out var spreading) && spreading.Voiced) StopOuter(entityId, spreading, now: true);
        if (_cutting.Remove(entityId, out var cutting)) StopPieces(entityId, cutting, 1, now: true);
        if (_sirenVoiced.Remove(entityId)) _voices.Stop(SirenVoiceId(entityId));
        if (_heldHorns.Remove(entityId)) _voices.Stop(HeldHornVoiceId(entityId));
        _placed.Remove(entityId);
        _speakerOf.Remove(entityId);
        // Only the walls' copies it has: asking for an id gives one out.
        for (int slot = 0; slot < EarlyReflections.MaxArrivals; slot++)
            if (_voices.TryIdOf(VoiceKind.Reflection, entityId, slot, out int copy)) _voices.Stop(copy);

        _acousticWorker.Forget(entityId);
    }

    /// <summary>
    /// Silences the whole world, for leaving it. Nothing here runs again until the next map, because
    /// <see cref="Tick"/> is only called in a world.
    /// </summary>
    public void LeaveWorld(IEnumerable<int> entityIds)
    {
        foreach (int id in entityIds) ForgetEntity(id);
        ForgetTrains();
        _voices.LeaveWorld();
        _ownerPaths.Clear();
        _oneOffs.Clear();
        _rain.Stop();
        _audio.StopAllWorldSounds();
        // Nobody is standing anywhere: no wind at the ears until the next map.
        _audio.SetEarWind(null);
        // Entity ids belong to the map just left: whoever had one there is nobody here.
        _talkersVoiced.Clear();
        _talkerRooms.Clear();
        _audio.TalkerStreams.Clear();
    }

    /// <summary>How far past the audio clock's now a position's stamp may be before it is taken as another
    /// clock's, s: on one clock it is never past it at all.</summary>
    private const double OtherClockSeconds = 0.05;

    /// <summary>Whether a snapshot's positions were found stamped on another clock than the audio's.</summary>
    internal bool WorldOnAnotherClock { get; private set; }

    /// <summary>
    /// One frame (the host's, about <see cref="UpdateHz"/>): every source placed from <paramref name="sources"/> in
    /// <paramref name="world"/>, as the listener is now. The listener is where the host hears from (open-fps: the
    /// smoothed VisualPosition, so a correction from the server is not heard as a jump).
    /// </summary>
    public void Tick(IAcousticWorld world, IAudioSources sources)
    {
        using var _perf = PerfProbe.Measure("audio.update");
        // Every source is placed here: nothing that takes longer than a frame belongs in it (PlacementThread).
        using var _placing = OpenFPS.Audio.Core.PlacementThread.Enter();

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
        _voices.Frame = _frameCount;
        _lastWorld = world;
        _sources = sources;
        _acousticWorker.UpdateWorld(world);

        // A position stamped after now was stamped on another clock: its age, and so where the mixer reckons
        // the thing has got to, is out by the two clocks' difference. Whoever holds the world and this gives the
        // world this one's clock (ClientWorldState.Clock = AudioEngineFacade.Clock). Said once.
        if (!WorldOnAnotherClock && _sources.SampledAt > _audio.Clock.Now + OtherClockSeconds)
        {
            WorldOnAnotherClock = true;
            Log.Warning("Audio: the world's positions are stamped {Ahead:F2} s after the audio's clock reads: the world "
                      + "state was not given the audio's clock (ClientWorldState.Clock), so moving things are placed late or early.",
                        _sources.SampledAt - _audio.Clock.Now);
        }

        Vector3 visualEyePos = Listener.Feet + new Vector3(0, Listener.EyeHeight, 0);
        _groundEar = visualEyePos;
        // The cabin you sit in, for the traced reverb: which vehicle, and your ear in its frame.
        {
            string? preset = null; Vector3 local = default;
            if (Listener.Riding && _sources.TryGet(Listener.RidingId, out var ride)
                && OpenFPS.Common.ModelKindTable.VehiclePreset(ride.Spec.SoundId) is { } ridePreset)
            {
                preset = ridePreset;
                local = Vector3.Transform(visualEyePos - ride.State.Position, Quaternion.Inverse(ride.State.Rotation));
            }
            _acousticWorker.Traced.RideIn(preset, local);
            // The cabin of the nearest vehicle within a few steps, traced before you get in (the order is
            // last frame's): made when you sat down, the street played until it was.
            if (preset == null) PrepareNearestCabin();
        }
        _groundWorld = world;

        int listenerRegionId = _acoustics.GetRegionAt(world, visualEyePos);
        // Kept: your own feet are submitted from the game thread between updates and need the room to
        // reverberate in (SubmitFootstep).
        _listenerRegion = listenerRegionId;

        // Not the wind: a uniform wind moves the source, the listener and the air together and shifts
        // no pitch. A share of it added here bent every pitch in the world with each gust.
        Vector3 listenerVelocity = Listener.Velocity;
        // Sitting in something, you face the way it faces: the session sets your heading from the
        // vehicle every frame (ClientGameSession.FollowRide), so the ears and the compass agree.
        var listenerRotation = Listener.Rotation;
        if (Listener.Riding && _sources.TryGet(Listener.RidingId, out var carrying))
        {
            // ...and you move at its speed, as the interpolated world has it. Your own velocity is the
            // vehicle's too (OccupancySystem), but as of the last server state, not the frame its voice
            // is placed in; any difference is a Doppler shift on your own bus.
            listenerVelocity = carrying.State.Velocity;
        }

        // The mixer reads the wind field itself once a block (EarWindVoice); this is where the head is,
        // which way it faces and what is round it.
        _earSpeed = listenerVelocity.Length();
        var earListener = EarListener(visualEyePos, listenerVelocity, listenerRotation);
        _audio.SetEarWind(earListener);
        _audio.UpdateListener(visualEyePos, listenerRotation, listenerVelocity, listenerRegionId);
        _audio.UpdateShelter(Listener.Shelter);
        _oneOffs.ListenerVehicleId = Listener.RidingId;
        _oneOffs.ListenerSpeed = _earSpeed;
        _oneOffs.Cabins = _cabins;
        _oneOffs.SelfId = OwnEntityId;
        _oneOffs.Self ??= () => (Listener.Body, Listener.Rotation);
        // The host's: the beacons and the driving aids, placed from here.
        _host.ListenerPlaced(visualEyePos, _earSpeed);
        // The rest of the world through the glass, if you sit in anything with a roof.
        var (encLow, encMid, encHigh) = CabinEnclosure();
        // Now and then, forget the windows of vehicles that have gone.
        if (_frameCount % 600 == 0) _cabins.Forget(_sources.Contains);
        _audio.SetListenerEnclosure(encLow, encMid, encHigh);
        // What the air's bending measures heights from where a voice's own bounce found no ground.
        _audio.SetListenerGround(visualEyePos.Y - Listener.EyeHeight);
        // Temperature reaches the mix as the speed of sound: c = 331.3 + 0.606·T.
        _audio.SetAirTemperature(world.Air.Celsius);

        // The listener's reverb decay from the Steam Audio reflection sim, when there is one, in place of
        // the Sabine estimate.
        if (_acousticWorker.TryGetListenerReverbDecayMs(out float simReverbMs))
        {
            _audio.SetSimulatedReverbDecay(simReverbMs, _acousticWorker.ListenerEnclosure,
                                           _acousticWorker.ListenerHfDecayRatio,
                                           _acousticWorker.ListenerLfDecayRatio);
        }
        
        var rooms = world.Rooms;
        bool newRooms = rooms != null && rooms != _lastAcousticMap;
        if (newRooms)
        {
            _audio.SetAcousticMap(rooms!);
            _lastAcousticMap = rooms;
        }
        // The host's: the rooms that moved, the place's name.
        _host.RoomsSet(visualEyePos, listenerRegionId, newRooms);
        // Through the echo system: the grandstand that answers a car answers the people on it too, on the
        // same terms (obstruction-tested).
        _oneOffs.Update(world, visualEyePos, _audio.Clock.Now, _engineEchoes, sources.Outdoors);

        // Six rays out of the head, turned by its rotation, so the answer is "concrete half a metre to my
        // left"; each becomes its own early reflection (BoundaryModel, BoundaryProximityProcessor). This is
        // what tells a corridor from a doorway from open air, and changes as you turn.
        UpdateBoundaryProbes(world, visualEyePos);

        Stage(0, ref stageTicks);     // everything up to here: region, listener, map, probes

        // The acoustic path of every playing sound, and of every car and machine holding a live slot even
        // when the mixer is not playing it. Asking only about playing voices let an occluded-silent voice's
        // result expire (the worker's 5 s TTL), fall back to an unoccluded path and burst back every five
        // seconds: ambience that "stutters and cuts out".
        _pathIds.Clear();
        _ownerPaths.Clear();
        _pathIds.UnionWith(_audio.GetActiveSpatialSoundIds());
        _pathIds.UnionWith(_liveEngines);
        _pathIds.UnionWith(_liveMachines);
        _pathIds.UnionWith(_horns.Keys);      // a horn takes its vehicle's path, borrowed voice or not
        // ...and a car voiced from afar, whose borrowed id sits below the reflection range and gets no path
        // of its own. Without this, twelve city cars were never occluded or darkened by the air: the
        // white-noise wash from the map's edge (high band 10 dB under the low, against 42 for the rest).
        _pathIds.UnionWith(_distantVoiced);
        // ...and the owner of every voice of a moving thing (last frame's: they are placed later on): a siren's
        // car, a held horn's, the sources a train's voices are heard through.
        _pathIds.UnionWith(_voices.Owners);
        foreach (var id in _pathIds)
        {
            // Asked about things in the world only, never about a voice's own id: a copy, a talker, your own
            // voice, rain and the rest work out their own paths (VoiceRegistry.HeardFromOf), and a copy asked
            // about from its image was traced through its own wall.
            if (!_sources.TryGet(id, out var src)) continue;

            // Asked about the point the sound comes out of, not the entity's origin: for anything that
            // drives the origin is its contact patch, and a probe there was half buried (about half the
            // samples blocked on open road).
            Vector3 sourcePos = HeardPoint(src, visualEyePos, out bool alongLine);
            float sourceRadius = src.ProbeRadius;
            // A line's nearest point moves as the ear does.
            float sourceSpeed = alongLine ? MathF.Max(src.State.Velocity.Length(), _earSpeed) : src.State.Velocity.Length();
            
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
            
            // Only an answer for this source and this ear: an entity's from a moment ago, never a pooled id's
            // last occupant's, nor one asked from before a teleport. Refused, the voice keeps what it has.
            if (_acousticWorker.TryGetResultFor(id, sourcePos, sourceSpeed, visualEyePos, _earSpeed, out var paths))
            {
                Array.Clear(_slotLive);
                if (_pathSlot.Length < paths.Count) _pathSlot = new int[paths.Count * 2];
                if (PlaysCopies(src))
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
                        // is put back here as the barrier it is. See VehicleShadow. Every voice of this
                        // entity hears it through this one answer (PathOf).
                        // Every voice of this entity carries this one answer in its own emitter (HandPath):
                        // its own, its outlets, its horn and siren, its places. A second writer here lost to
                        // the emitter on every field they share.
                        _ownerPaths[id] = Shadowed(world, id, path, sourcePos, visualEyePos);
                        continue;
                    }

                    // A reflection: an image source off the scene's surfaces (EarlyReflections) under Steam
                    // Audio, the hand-rolled tracer's otherwise. The tail does not carry this energy: without
                    // these a room answers from everywhere at once and a doorway is inaudible from outside.
                    int slot = _pathSlot[pi];
                    if (slot < 0) continue;
                    // Everywhere: the listener's traced stage plays the late tail alone, so a sustained
                    // source's first bounces are these, indoors as out.
                    _slotLive[slot] = true;

                    // One voice per slot and one wall per slot (AssignReflectionSlots). Keyed by slot, which
                    // cannot collide, not by a hash of the surface id, which can: one voice flickering
                    // between two opposite walls.
                    int reflectId = ReflectionVoiceId(id, slot);
                    var placed = _placed.TryGetValue(id, out var p)
                        ? p : (src.Spec.Volume, src.Spec.MinDistance, src.Spec.Range);
                    var echo = LoopEchoLevel(path, placed.Volume, placed.MinDistance, dist);

                    // A loudspeaker's copy is its render, thrown toward the wall by its beam: the band
                    // gains the mouth gives the way the sound left it for that wall. None before the
                    // render is in, or it would be the bare recording.
                    string copySound = _sounds.ResolvePath(src.Spec.SoundId!);
                    if (!string.IsNullOrEmpty(src.Spec.Loudspeaker))
                    {
                        if (!_speakerOf.TryGetValue(id, out var speakerRender)) continue;
                        copySound = speakerRender.Key;
                        var (bL, bM, bH) = CopyBeam(speakerRender, src, path.ApparentPosition, visualEyePos);
                        float mid = MathF.Max(1e-3f, bM);
                        echo = (echo.Volume * bM, echo.MinDistance, echo.EqLow * bL / mid, echo.EqMid, echo.EqHigh * bH / mid);
                    }

                    var reflectEmitter = new SpatialEmitter
                    {
                        EntityId = reflectId,
                        Seed = VoiceRegistry.SeedOf(VoiceKind.Reflection, id, slot),
                        SoundId = copySound,
                        Mode = src.Spec.Mode,
                        Position = path.ApparentPosition,
                        ApparentPosition = path.ApparentPosition,
                        // What the surface and the extra distance left of it, per band (LoopEchoLevel).
                        Volume = echo.Volume,
                        MinDistance = echo.MinDistance,
                        // Heard as far as its source is: at 0.8 of the authored range a loud source's copies
                        // stopped at 40 m while it played on (S24).
                        Range = placed.Range,
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
                        // A rough wall's scattered share, √s of the pressure, is smeared by its roughness;
                        // the mirror share stays a clean copy (EchoWashState; nothing at all off a smooth wall).
                        EchoScattering = path.Scattering,
                        EchoMirrorShare = MathF.Sqrt(1f - Math.Clamp(path.Scattering, 0f, 1f)),
                        // Not a reverb send: the provider uses bleed only to pull an apparent position
                        // toward the real one, and a copy's two are the same.
                        TransmissionBleed = path.MaterialAbsorption * 0.5f 
                    };

                    _voices.Declare(reflectId, id, VoiceKind.Reflection, path.ApparentPosition);
                    // A surface that stopped answering and started again keeps its voice (its fade is undone).
                    _voices.Sound(reflectEmitter);
                }

                // A surface that stopped answering fades out (a cut is a click) rather than playing on
                // from where the wall was after the listener has walked out of its reach.
                for (int slot = 0; slot < EarlyReflections.MaxArrivals; slot++)
                    if (!_slotLive[slot]) _voices.Retire(ReflectionVoiceId(id, slot), VoiceFade.Voice);
            }
            // No answer this frame: its walls' copies sound on where they were, and stay its.
            else if (_reflectionSlots.ContainsKey(id))
                for (int slot = 0; slot < EarlyReflections.MaxArrivals; slot++) _voices.Keep(ReflectionVoiceId(id, slot));
        }

        Stage(1, ref stageTicks);     // 5: the acoustic paths of every active voice

        // Once, outside the per-entity pass: who gets a voice is a decision about the set.
        ChooseLiveEngines(visualEyePos);
        // The rain running off (gutters, drains, downpipes), fed before the machines are ranked so a dry
        // gutter is not given a voice. Snow stays where it falls.
        {
            var p = _sources.Precipitation;
            float wet = p.Falling ? (p.Kind == OpenFPS.Environment.PrecipitationKind.Snow ? 0f : p.RateMmPerHour) : _sources.RainRateMmPerHour;
            _audio.Weather.Runoff.Update(wet, _audio.Clock.Now);
        }
        ChooseLiveMachines(visualEyePos);
        ChoosePlaces(visualEyePos);
        _engineEchoes.EchoesPerEngine = _adaptiveEchoes;
        _engineEchoes.SyncGeometry(world, _acoustics.ReflectionWorldFor(world));
        float engineDt = (float)Math.Max(1e-3, _now() - _lastEngineTime);
        _lastEngineTime = _now();

        Stage(2, ref stageTicks);     // 5.5: who gets a voice

        foreach (var entityId in _sources.Audible)
        {
            if (entityId == OwnEntityId) continue;
            if (_sources.TryGet(entityId, out var src))
            {
                // Switched off by the host: a placed beacon whose category is off (by the map or by you).
                // Doors, items and cars are BeaconAids'.
                if (src.Silenced)
                {
                    _voices.Stop(entityId);
                    continue;
                }
                ProcessAudioEmitter(world, src, visualEyePos, engineDt);
            }
        }
        RetireSpreading();
        UpdateCabinVoices();

        long partAt = System.Diagnostics.Stopwatch.GetTimestamp();
        UpdateTrains(world, visualEyePos, engineDt);
        UpdateHorns(world, visualEyePos, _audio.Clock.Now);
        UpdateSirens(world, visualEyePos);
        UpdateOwnVoice(visualEyePos, _audio.Clock.Now);
        UpdateTalkers(world, visualEyePos, _audio.Clock.Now);
        _partMs[3] += Ms(partAt);
        partAt = System.Diagnostics.Stopwatch.GetTimestamp();
        _birds.EarSpeed = _earSpeed;
        _birds.Update(world, visualEyePos, _audio.Clock.Now);
        _rain.EarSpeed = _earSpeed;
        _rain.Update(world, _sources.Falling, visualEyePos, _audio.Clock.Now, listenerRegionId, OwnEntityId, Listener.RidingId);
        _partMs[2] += Ms(partAt);

        // Every source has been offered to the reflection system: it sets what next frame's reflections
        // must reach to be worth a voice.
        _engineEchoes.EndFrame();
        // Every voice let go this frame or before is taken down its fade; then a sustained voice not declared
        // this frame is no longer an entry, nor a one-off that has stopped, nor a fade that has ended.
        _voices.Fades();
        _voices.Sweep(_frameCount, _sounding ??= _audio.IsPlaying);

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
    private Func<int, bool>? _sounding;

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
    private readonly string[] PartNames = { "echoes", "ground", "birds", "horns+sirens" };
    private static double Ms(long from) => (System.Diagnostics.Stopwatch.GetTimestamp() - from) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    private readonly string[] StageNames =
        { "listener+map", "paths", "budget", "emitters", "mixer" };

    private void Stage(int stage, ref long since)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        double ms = (now - since) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        if (ms > _stageWorstMs[stage]) _stageWorstMs[stage] = ms;
        since = now;
    }

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

    /// <summary>
    /// The key a placed thing's voice is made and ranked with: its emitter's own, but a fire takes the
    /// shape its collider gives it (FireSpec.KeyForPlaced), so a fire pit scaled up is a bigger fire.
    /// Memoised: asked several times a frame per source.
    /// </summary>
    private string VoiceKey(in SourceSpec spec)
    {
        // A whole shore's outline (its emitter's line, closed): one source as long as the outline (ShoreSpec.OutlineKey).
        if (spec.Line is { Length: >= 3 } line && OpenFPS.Common.ModelKindTable.Is(spec.SoundId, OpenFPS.Common.ModelSynth.Shore))
        {
            if (_outlineKeys.TryGetValue(line, out var known)) return known;
            float perimeter = 0f;
            for (int i = 1; i < line.Length; i++) perimeter += Vector3.Distance(line[i - 1], line[i]);
            if (_outlineKeys.Count > 4096) _outlineKeys.Clear();
            return _outlineKeys[line] = OpenFPS.Sound.ShoreSpec.OutlineKey(spec.SoundId!, perimeter);
        }
        return VoiceKey(spec.SoundId, spec.Shape, spec.Size);
    }
    private readonly Dictionary<Vector3[], string> _outlineKeys = new(ReferenceEqualityComparer.Instance);

    private string VoiceKey(string? soundId, OpenFPS.Geometry.ColliderShape shape, Vector3 size)
    {
        string id = soundId ?? "";
        if (!OpenFPS.Common.ModelKindTable.Is(id, OpenFPS.Common.ModelSynth.Fire)) return id;
        var k = (id, shape, size);
        if (_voiceKeys.TryGetValue(k, out var key)) return key;
        key = OpenFPS.Environment.FireSpec.KeyForPlaced(id, shape.IsRound(), size, ModelLibrary.Presets.Fire);
        if (_voiceKeys.Count > 4096) _voiceKeys.Clear();
        _voiceKeys[k] = key;
        return key;
    }
    private readonly Dictionary<(string, OpenFPS.Geometry.ColliderShape, Vector3), string> _voiceKeys = new();

    /// <summary>The headroom a physical voice renders with, dB: its spec's own for water, fire, foliage
    /// and bells, the shared one for the rest. Memoised like the level.</summary>
    private readonly Dictionary<string, float> _physicalHeadroom = new(StringComparer.OrdinalIgnoreCase);

    private float PhysicalHeadroom(string soundId)
    {
        if (_physicalHeadroom.TryGetValue(soundId, out float h)) return h;
        h = OpenFPS.Common.ModelKindTable.Headroom(soundId);
        _physicalHeadroom[soundId] = h;
        return h;
    }

    private static (float LevelDb, float Extent)? LookUpPhysicalLevel(string soundId)
        => OpenFPS.Common.ModelKindTable.TryLevel(soundId, out float level, out float extent) ? (level, extent) : null;

    /// <summary>
    /// A rotor's wake from what the aircraft is doing: it slaps descending into its own downwash, or fast
    /// forward. Read off the motion, which says it; the power lever is the server's (EntitySnapshot.Lever).
    /// </summary>
    private static float RotorWake(Vector3 velocity)
    {
        float speed = velocity.Length();
        if (speed < 0.5f) return 0f;
        float climb = velocity.Y / speed;                      // sine of the flight path angle
        float wake = Math.Clamp(-climb * 6f, 0f, 1f) * 0.7f
                   + Math.Clamp((speed - 25f) / 45f, 0f, 1f) * 0.3f;
        return Math.Clamp(wake, 0f, 1f);
    }

    /// <summary>
    /// The listener's head for the wind at the ears: place, motion, facing, exposure and cover. Exposure
    /// is one minus the survey's enclosure (a street canyon takes a third off the wind, a walled yard
    /// half) and zero in a room. A cabin lets in what its open windows do; a vehicle without one (a
    /// motorcycle, a formula car) puts a helmet on the rider.
    /// </summary>
    private EarWindListener EarListener(Vector3 head, Vector3 velocity, Quaternion rotation)
    {
        Vector3 forward = Vector3.Transform(Vector3.UnitZ, rotation);
        float facing = MathF.Atan2(forward.X, forward.Z) * 180f / MathF.PI;
        float exposure = Listener.Indoor ? 0f : 1f - Math.Clamp(_acousticWorker.ListenerEnclosure, 0f, 1f);
        var cover = EarCover.None;
        float windows = 0f;
        if (Listener.Riding)
        {
            cover = EarCover.Cabin;
            if (_sources.TryGet(Listener.RidingId, out var ride)
                && OpenFPS.Audio.Acoustics.CabinWalls.Vehicle(ride.Spec.SoundId) is { } vehicle)
            {
                if (OpenFPS.Audio.Acoustics.CabinWalls.HasCabin(vehicle))
                    windows = _cabins.WindowsOpen(ride.Id, ride.Spec.WindowsOpen, _now());
                else cover = EarCover.Helmet;
            }
        }
        return new EarWindListener(head, Listener.Riding ? 1.3f : Listener.EyeHeight, new Vector2(velocity.X, velocity.Z),
                                   facing, exposure, cover, windows);
    }

    /// <summary>An open bus doorway's share of the cabin's wall area, as transmitted power.</summary>
    private const float DoorwayPowerFraction = 0.0225f;

    /// <summary>Every vehicle's windows as this client has them, and what a cabin's walls take off a
    /// sound crossing them.</summary>
    private readonly OpenFPS.Audio.Acoustics.CabinWalls _cabins = new();



    /// <summary>
    /// What the body of the vehicle you sit in takes off everything outside it, dB per band: the interior
    /// engine voice's paths the other way round, the glass by its mass (transmission ~ rho*c / (pi*f*m)),
    /// the seals, and whatever is open (CarWindow.CabinLossDb). At 150 Hz a hatchback's glass passes about
    /// a hundredth of the power; by 1 kHz the seals dominate and the top sits about 30 dB down. Windows
    /// down, a tenth of the wall is a hole. Nothing on foot or on something with no cabin.
    /// </summary>
    private (float Low, float Mid, float High) CabinEnclosure()
    {
        if (!Listener.Riding || !_sources.TryGet(Listener.RidingId, out var ride)) return (0f, 0f, 0f);
        if (OpenFPS.Audio.Acoustics.CabinWalls.Vehicle(ride.Spec.SoundId) is not { } vehicle) return (0f, 0f, 0f);
        // A bus's open doors: about 2.4 m^2 in a hundred-odd m^2 of wall, the street at a couple of per
        // cent of its power. The voice decides when the doors are open.
        float doorway = _audio.EngineDoorsOpen(Listener.RidingId) ? DoorwayPowerFraction : 0f;
        return OpenFPS.Audio.Acoustics.CabinWalls.LossDb(vehicle, _cabins.WindowsOpen(ride.Id, ride.Spec.WindowsOpen, _now()), doorway);
    }

    /// <summary>
    /// Whether an aeroplane is on its wheels, and its height over the ground (infinity when not probed):
    /// its belly within 15 % of its own length of the ground. The transition is the touchdown, which the
    /// voice turns into wheels spinning up (AircraftSynth.Touchdown). Not probed at all more than 60 m
    /// over the listener's head, since the ground probe is a grid search.
    /// </summary>
    private bool OnTheWheels(AudioSource src, IAcousticWorld world, Vector3 eyePos, out float height)
    {
        var p = src.State.Position;
        height = float.PositiveInfinity;
        if (p.Y - eyePos.Y > 60f) return false;
        float groundY = _host.GroundUnder(p, src.Id);
        height = p.Y - groundY;
        // A jet sits five metres up on its gear and a light single one: a fraction of length serves both.
        float sitsAt = 0.15f * (PhysicalAircraft(src.Spec.SoundId)?.LengthMetres ?? 8f);
        return p.Y - groundY <= sitsAt;
    }

    /// <summary>The aircraft profile a sound id names, or null if it is not an aeroplane.</summary>
    private static OpenFPS.Sound.AircraftProfile? PhysicalAircraft(string? sid)
    {
        if (OpenFPS.Common.ModelKindTable.KindOf(sid) is not { Synth: OpenFPS.Common.ModelSynth.Aircraft } kind) return null;
        try { return OpenFPS.Sound.AircraftProfile.ByName(kind.Rest(sid!)); }
        catch (Exception ex) { OpenFPS.Common.ModelKindTable.Fail(sid!, ex.Message); return null; }
    }

    private readonly Dictionary<string, OpenFPS.Sound.RunningWaterSpec> _flowSpecs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>When a tap was last seen on, by entity.</summary>
    private readonly Dictionary<int, double> _tapOnAt = new();

    /// <summary>How long a basin is heard after its tap is shut, s: the longest a full sink takes to drain.</summary>
    private const double TapDrainSeconds = 90.0;

    /// <summary>Running water fed only by the rain, with less than a trickle in it now (Runoff): it makes
    /// no sound and is given no voice. A creek or a fountain's overflow, with a flow of its own, never is.</summary>
    private bool Dry(string soundId, int entityId, bool running, double now)
    {
        // Still water: no wind to raise waves, no swell, no current. It makes no sound.
        if (OpenFPS.Common.ModelKindTable.Is(soundId, OpenFPS.Common.ModelSynth.Shore))
        {
            try { return OpenFPS.Common.ModelLibrary.Presets.Shore(soundId).CalmAt(_audio.Weather.MeanSpeed); }
            catch (Exception) { return false; }
        }
        // A gas hob with every burner off: heard for as long as its last change can still be heard (the
        // flame going out, the safety valve's click), then nothing.
        if (OpenFPS.Sound.HobKey.IsKey(soundId))
        {
            if (!OpenFPS.Sound.HobKey.TryParse(soundId, out var hob) || hob.AnyOn) return false;
            return _audio.Clock.WorldTime - hob.At > OpenFPS.Sound.HobKey.QuietAfterSeconds;
        }
        if (!OpenFPS.Common.ModelKindTable.Is(soundId, OpenFPS.Common.ModelSynth.Flow)) return false;
        try
        {
            // Once per sound id: this runs for every sink in every flat, every frame.
            if (!_flowSpecs.TryGetValue(soundId, out var spec))
                _flowSpecs[soundId] = spec = OpenFPS.Common.ModelLibrary.Presets.Flow(soundId[5..]);
            // A tap is heard while on and for as long after as its basin takes to empty. A shut tap nobody
            // opened since you arrived is an empty sink; a leaking one drips.
            if (spec.Tap is { } tap)
            {
                if (running) { _tapOnAt[entityId] = now; return false; }
                if (tap.LeakLitresPerSecond > 0f) return false;
                return !_tapOnAt.TryGetValue(entityId, out double on) || now - on > TapDrainSeconds;
            }
            if (spec.BaseFlowLitresPerSecond > 0f || spec.CatchmentSquareMetres <= 0f) return false;
            return spec.FlowNow(_audio.Weather) < spec.DryBelowLitresPerSecond;
        }
        catch (Exception) { return false; }
    }

    // ── Woods heard as one (WoodChorus) ──────────────────────────────────────────────────────────
    private readonly OpenFPS.Sound.WoodChorus.Weights _woodWeights = new();
    private Func<int, bool>? _isLiveMachine;

    /// <summary>A tree's or a wood's share this frame: the amplitude its voice is played at (0: no voice
    /// of its own), and for a wood how many trees its synth stands for. Everything else: 1, 1.</summary>
    private float ChorusShare(int entityId, out float trees)
    {
        trees = 1f;
        if (IsWood(entityId))
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
    private void ChooseLiveMachines(Vector3 eyePos)
    {
        double now = _now();
        _machineOrder.Clear();
        _machineGroups.Clear();
        // Trees the budget left without a voice last frame are heard in their wood (WoodChorus.Weigh).
        if (_sources.Woods is { } woods) woods.Weigh(eyePos, _woodWeights, _isLiveMachine ??= id => _liveMachines.Contains(id));
        else { _woodWeights.Individual.Clear(); _woodWeights.Woods.Clear(); }

        // A train is one machine: up to nine taps on one synth, ranked by its loudest and admitted
        // together. Ranked tap by tap, its taps dropped and came back all along it as it passed.
        foreach (int entityId in _sources.Audible)
        {
            if (entityId == OwnEntityId) continue;
            if (!_sources.TryGet(entityId, out var src)) continue;
            ref readonly var em = ref src.Spec;
            if (!em.IsSynth || em.SoundId == null) continue;
            if (!PhysicalLevel(VoiceKey(em), out float levelDb, out float extent)) continue;
            if (Dry(em.SoundId, entityId, em.SynthRunning, now)) continue;
            // A train's horn or bell that nobody is sounding.
            if (SilentSignal(em.SoundId, now)) continue;
            // A tree past the hand-over is heard in its wood, an empty wood not at all; ranked by what each
            // plays (a wood's trees add in power, so its amplitude by their root).
            float chorus = ChorusShare(entityId, out float chorusTrees);
            if (chorus <= 0f || chorusTrees <= 1e-3f) continue;
            chorus *= MathF.Sqrt(chorusTrees);

            // Ranked by how loud it is at the ear (VoiceManager.HeardLevel, the one rank), with the numbers its
            // emitter plays (ProcessAudioEmitter): its placed gain, its range and the key its spectrum is measured
            // under. Not the gain that gives a quieter render its headroom back, which is no louder at the ear.
            string voiceKey = VoiceKey(em);
            // A line from its point nearest the ear: stood beside its near part, it is that far.
            float d = Vector3.Distance(HeardPoint(src, eyePos, out _), eyePos);
            var (gain, reference) = _audio.Hearing.Place(levelDb, extent);
            float range = MathF.Max(1f, MathF.Max(em.Range, _audio.Hearing.AudibleRange(levelDb)));
            // A line cut into pieces is as loud as the law over its whole length, not its middle alone: stood at
            // one end of a stretch you are beside it.
            float line = LineHeard(src, voiceKey, reference, eyePos, d);
            float level = OpenFPS.Audio.Core.VoiceManager.HeardLevel(_audio.Hearing,
                gain * em.Volume * chorus * line, reference, range, d, UnaskedOcclusion, levelDb, PhysicalKeyOf(src, voiceKey), physical: true);
            _heardInRanking[entityId] = level;
            // Switched off by the server (a crossing's bell between trains) and within earshot: the voice it
            // would start with is made now, off the threads that place sounds, so it starts when switched on.
            if (!em.SynthRunning && d < range && !_liveMachines.Contains(entityId)
                && OpenFPS.Audio.Fmod.VoiceBuilds.Makes(OpenFPS.Common.ModelKindTable.KindOf(voiceKey)?.Synth))
                _audio.PrepareVoice(PhysicalKeyOf(src, voiceKey), VoiceRegistry.SeedOf(OwnVoiceKind(em, entityId, true), entityId));

            // A water feature's taps are one fountain the same way, and come and go together.
            bool rail = OpenFPS.Audio.Fmod.TrainVoiceState.ParseKey(em.SoundId, out string preset, out string train, out _);
            string group = rail
                ? preset + "/" + train
                : OpenFPS.Audio.Fmod.WaterFeatureVoice.ParseKey(em.SoundId, out string waterPreset, out string feature, out _)
                ? "water:" + waterPreset + "/" + feature : "#" + entityId;
            if (!_machineGroups.TryGetValue(group, out var g))
            {
                _machineGroups[group] = g = new MachineGroup();
                if (rail) { SeenTrain(preset, train); g.Train = group; g.Live = _liveTrains.Contains(group); g.Held = _trainStarted.TryGetValue(group, out double t0) && now - t0 < EngineMinimumHoldSeconds; }
                else if (group.StartsWith("water:", StringComparison.Ordinal)) g.Water = group;
            }
            if (g.Water != null) g.Taps.Add(src.Emission);
            g.Members.Add(entityId);
            g.FirstId = g.Members.Count == 1 ? entityId : Math.Min(g.FirstId, entityId);
            g.Level = MathF.Max(g.Level, level);
            if (rail) continue;
            if (_liveMachines.Contains(entityId)) g.Live = true;
            if (_machineStarted.TryGetValue(entityId, out double began) && now - began < EngineMinimumHoldSeconds) g.Held = true;
        }
        foreach (var g in _machineGroups.Values)
        {
            // Louder sorts first, so negated; the hold and the keep bias as for a car.
            float key = g.Live ? -g.Level / (EngineKeepBias * EngineKeepBias) : -g.Level;
            if (g.Held) key = float.NegativeInfinity;
            g.Key = key;
            // What it costs in voices: a train its handful (TrainVoicing), a water feature far enough off one (its
            // taps heard as the whole, WholeFeature), anything else one per member.
            if (g.Water != null) g.Whole = WholeFeature(g, eyePos);
            g.Cost = g.Train != null ? TrainVoiceCost(g) : g.Whole ? 1 : g.Members.Count;
        }
        _groupOrder.Clear();
        _groupOrder.AddRange(_machineGroups.Values);
        // Ties (every group still held is -infinity) by the group's lowest id, not the order the world listed
        // them in: the world's dictionaries list in an order that follows the processor count, and a 4-core
        // machine revived a held fountain and air conditioner the other way round.
        _groupOrder.Sort(static (x, y) => (x.Key, x.FirstId).CompareTo((y.Key, y.FirstId)));

        // In voices, the loudest first, the first whatever it costs. A group not yet playing is only
        // started while the binaural pool can take it and still keep a reserve: past that, a voice would
        // play without HRTF, flat and in the middle of the head, which is worse than not playing it.
        int spent = 0, hrtfLeft = _audio.SpatialVoicesFree - HrtfReserve;
        _keptGroups.Clear();
        if (MachineVoiceBudget > 0)
            for (int i = 0; i < _groupOrder.Count; i++)
            {
                var g = _groupOrder[i];
                // One that does not fit leaves room for a smaller one further down.
                if (i > 0 && spent + g.Cost > _adaptiveMachines) continue;
                if (!g.Live && i > 0 && g.Cost > hrtfLeft) continue;
                if (!g.Live) hrtfLeft -= g.Cost;
                spent += g.Cost;
                _keptGroups.Add(g);
            }
        int keepGroups = _keptGroups.Count;
        _wantedMachines.Clear();
        _wantedTrains.Clear();
        for (int i = 0; i < keepGroups; i++)
        {
            if (_keptGroups[i].Train is { } t) { _wantedTrains.Add(t); continue; }
            // A water feature heard whole plays every tap's water from its first.
            if (_keptGroups[i].Whole) { _wantedMachines.Add(_keptGroups[i].FirstId); continue; }
            foreach (int id in _keptGroups[i].Members) _wantedMachines.Add(id);
        }
        foreach (var g in _machineGroups.Values)
            if (g.Water != null && MachineVoiceId(g.FirstId, out int whole)) _audio.WholeFeature(whole, g.Whole);
        _machineVoicesSpent = spent;

        // A machine given up fades out, the registry's to finish; one wanted again before it is silent comes back.
        // Each by its voice's id, which for a wood is not its entity's.
        foreach (int id in _liveMachines)
        {
            if (_wantedMachines.Contains(id)) continue;
            _machineStarted.Remove(id);
            if (MachineVoiceId(id, out int voice)) _voices.Retire(voice);
        }
        foreach (int id in _wantedMachines)
            if (MachineVoiceId(id, out int voice)) _voices.Unretire(voice);

        int admittedGroups = 0;
        _liveMachines.Clear();
        _liveTrains.Clear();
        _trainMembers.Clear();
        for (int i = 0; i < keepGroups; i++)
        {
            var g = _keptGroups[i];
            if (g.Train != null)
            {
                if (!g.Live)
                {
                    if (admittedGroups >= NewEnginesPerUpdate) continue;
                    admittedGroups++;
                    _trainStarted[g.Train] = now;
                }
                _liveTrains.Add(g.Train);
                _trainMembers[g.Train] = g.Members.ToArray();
                continue;
            }
            // A water feature heard whole is its first tap alone.
            var members = g.Whole ? _wholeMembers.Replace(g.FirstId) : g.Members;
            bool isNew = false;
            foreach (int id in members) if (!_machineStarted.ContainsKey(id)) { isNew = true; break; }
            if (isNew)
            {
                // A map load presents every machine at once; building one is a set of waveguides and
                // resonators. Counted per machine: a fountain's taps share one synth.
                if (admittedGroups >= NewEnginesPerUpdate) continue;
                admittedGroups++;
            }
            foreach (int id in members)
            {
                _liveMachines.Add(id);
                // A wood is given its voice's id here if it has none: it is sounded under it later this frame.
                _audio.ReviveEngine(IsWood(id) ? _voices.IdFor(VoiceKind.Wood, id) : id);
                if (!_machineStarted.ContainsKey(id)) _machineStarted[id] = now;
            }
        }
    }

    /// <summary>The id a machine's own voice is under, by its entity: the entity's, or for a wood the id its
    /// voice was given. False for a wood whose voice has none (asking for an id gives one out).</summary>
    private bool MachineVoiceId(int entityId, out int voiceId)
    {
        if (_voices.TryIdOf(VoiceKind.Wood, entityId, 0, out voiceId)) return true;
        voiceId = entityId;
        return !IsWood(entityId);
    }

    /// <summary>How loud each car and each standing model was at the ear in this frame's rankings
    /// (VoiceManager.HeardLevel; the cars are ranked first and clear it), for what is chosen among them afterwards
    /// (ChoosePlaces).</summary>
    private readonly Dictionary<int, float> _heardInRanking = new();

    /// <summary>How loud the ranking took a car or a standing model to be this frame, for a test; NaN, not ranked.</summary>
    internal float HeardInRanking(int entityId) => _heardInRanking.GetValueOrDefault(entityId, float.NaN);

    /// <summary>The key a standing model's voice is built and measured under: its voice key, and for a stretch
    /// of shore its geometry with it (fetch, which way its water lies), which it carries in its box
    /// (tools/gen_osm.py) and its voice reads from the key.</summary>
    private static string PhysicalKeyOf(AudioSource src, string voiceKey)
        => PhysicalKeyOf(src.Spec.Size, src.State.Rotation, voiceKey);

    private static string PhysicalKeyOf(Vector3 size, Quaternion rotation, string voiceKey)
        => OpenFPS.Common.ModelKindTable.KindOf(voiceKey)?.Synth == OpenFPS.Common.ModelSynth.Shore && !OpenFPS.Sound.ShoreSpec.TryOutline(voiceKey, out _, out _)
            ? OpenFPS.Sound.ShoreSpec.KeyFor(voiceKey, size, rotation)
            : voiceKey;

    private sealed class MachineGroup
    {
        public readonly List<int> Members = new();
        public float Level, Key;
        /// <summary>The lowest member's id: what a tie in <see cref="Key"/> is broken by.</summary>
        public int FirstId;
        public bool Live, Held;
        /// <summary>"preset/train" for a train's group, else null.</summary>
        public string? Train;
        /// <summary>Voices it takes.</summary>
        public int Cost = 1;
        /// <summary>A water feature's group ("water:preset/feature"), its taps' places this frame, and whether it is
        /// heard whole from its first tap (<see cref="WholeFeature"/>).</summary>
        public string? Water;
        public readonly List<Vector3> Taps = new();
        public bool Whole;
    }

    /// <summary>A whole feature's one member, without a list a frame.</summary>
    private sealed class OneMember : List<int>
    {
        public OneMember Replace(int id) { Clear(); Add(id); return this; }
    }
    private readonly OneMember _wholeMembers = new();

    /// <summary>Which water features were heard whole last frame, for the hysteresis.</summary>
    private readonly HashSet<string> _wholeFeatures = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether a water feature's taps are heard as one from its first (docs/RUNNING_WATER.md 15.8): the angle its taps
    /// cover at the ear by the line's own cut, as TrainVoicing merges a pair, under its merge angle (4°) together and
    /// over its split angle (7°) apart, held between; a fountain 11 m across is one voice from about 150 m. Its taps
    /// stay where they are: heard whole, its first tap plays all of their water, the rest of its voices let go.
    /// </summary>
    private bool WholeFeature(MachineGroup g, Vector3 ear)
    {
        double widest = 0;
        for (int i = 0; i < g.Taps.Count; i++)
            for (int j = i + 1; j < g.Taps.Count; j++)
            {
                var a = g.Taps[i] - ear;
                var b = g.Taps[j] - ear;
                widest = Math.Max(widest, Math.Atan2(Vector3.Cross(a, b).Length(), Vector3.Dot(a, b)));
            }
        float degrees = (float)(widest * 180.0 / Math.PI);
        bool was = _wholeFeatures.Contains(g.Water!);
        bool whole = g.Taps.Count > 1 && (was ? degrees < OpenFPS.Sound.Core.Rail.TrainVoicing.SplitAboveDegrees
                                               : degrees < OpenFPS.Sound.Core.Rail.TrainVoicing.MergeBelowDegrees);
        if (whole) _wholeFeatures.Add(g.Water!); else _wholeFeatures.Remove(g.Water!);
        return whole;
    }

    /// <summary>Binaural voices kept free when the machine budget starts something new: what the cars,
    /// the footsteps and the speech need in the next second.</summary>
    private const int HrtfReserve = 16;

    /// <summary>Machine voices in use after the last ranking, for the log.</summary>
    private int _machineVoicesSpent;
    private readonly Dictionary<string, MachineGroup> _machineGroups = new();
    private readonly List<MachineGroup> _groupOrder = new();
    private readonly List<MachineGroup> _keptGroups = new();
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
    private void ChooseLiveEngines(Vector3 eyePos)
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
        int starves = OpenFPS.Audio.Fmod.EngineVoiceState.GlobalStarves + OpenFPS.Audio.Fmod.PhysicalVoiceState.GlobalStarves;
        if (_starveSampledAt > 0 && now > _starveSampledAt)
            _starveRate += ((starves - _starvesSeen) / (float)(now - _starveSampledAt) - _starveRate) * 0.3f;
        _starvesSeen = starves; _starveSampledAt = now;
        if (_starveRate > StarveCeilingPerSecond && now >= _budgetHeldUntil && now - _lastBudgetChange >= BudgetSettleSeconds)
        {
            if (_adaptiveMachines > MachineFloor) _adaptiveMachines--;
            else if (_adaptiveBudget > MinEngineVoices) _adaptiveBudget--;
            _lastBudgetChange = now;
            GaveUp(now);
            Log.Information("Audio: engines starving at {Rate:F0}/s; {Cars} engine(s), {Machines} machine voice(s).",
                            _starveRate, _adaptiveBudget, _adaptiveMachines);
        }

        // The binaural pool nearly empty is a shortage like the mixer's: a voice past it plays flat.
        int hrtfFree = _audio.SpatialVoicesFree;
        bool hrtfShort = hrtfFree < HrtfLowWater;
        bool roomToRestore = load > 0f && load < MixerLoadCeiling - RestoreMargin && _starveRate < 1f && hrtfFree > HrtfReserve;
        if (!roomToRestore) _underRestoreSince = -1;
        else if (_underRestoreSince < 0) _underRestoreSince = now;

        if ((load > 0f || hrtfShort) && now >= _budgetHeldUntil && now - _lastBudgetChange >= BudgetSettleSeconds)
        {
            if (hrtfShort || load > MixerLoadCeiling && now - _overCeilingSince >= OverCeilingSeconds)
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
                GaveUp(now);
                Log.Information("Audio: {Why}; {Cars} engine(s), {Machines} machine voice(s), {Places} place(s), {Distant} borrowed, {Echoes} reflection(s) each.",
                                hrtfShort ? $"{hrtfFree} binaural voice(s) free" : $"mixer at {load:P0}",
                                _adaptiveBudget, _adaptiveMachines, _adaptivePlaces, _adaptiveDistant, _adaptiveEchoes);
            }
            else if (_underRestoreSince >= 0 && now - _underRestoreSince >= RestoreAfterSeconds && now >= _restoreAllowedAt)
            {
                if (_adaptiveBudget < EngineVoiceBudget) _adaptiveBudget++;
                else if (_adaptiveMachines < MachineVoiceBudget) _adaptiveMachines++;
                else if (_adaptiveDistant < MaxDistantVoices) _adaptiveDistant++;
                else if (_adaptiveFront < FrontVoiceBudget) _adaptiveFront++;
                else if (_adaptivePlaces < PlaceVoiceBudget) _adaptivePlaces = Math.Min(PlaceVoiceBudget, _adaptivePlaces + PlaceBudgetStep);
                else if (_adaptiveEchoes < EchoCeiling && _echoMsSmoothed < EchoPassCeilingMs / 3) _adaptiveEchoes++;
                else goto settled;
                _lastBudgetChange = now;
                _lastRestoreAt = now;
                // One step at a time, each after the load has stayed low again.
                _underRestoreSince = now;
                Log.Information("Audio: mixer at {Load:P0}, taking a voice back; {Cars} engine(s), {Machines} machine voice(s), {Places} place(s), {Distant} borrowed, {Echoes} reflection(s) each.",
                                load, _adaptiveBudget, _adaptiveMachines, _adaptivePlaces, _adaptiveDistant, _adaptiveEchoes);
            }
            settled: ;
        }

        _engineOrder.Clear();
        _carPreset.Clear();
        _heardInRanking.Clear();
        foreach (var entityId in _sources.Audible)
        {
            if (entityId == OwnEntityId) continue;
            if (!_sources.TryGet(entityId, out var src)) continue;
            ref readonly var em = ref src.Spec;
            if (!em.IsSynth || em.SoundId == null) continue;
            if (OpenFPS.Common.ModelKindTable.KnownVehiclePreset(em.SoundId) is not { } preset) continue;
            _carPreset[entityId] = preset;

            float d2 = Vector3.DistanceSquared(src.State.Position, eyePos);
            // Ranked by how loud it is at the ear (VoiceManager.HeardLevel, the one rank), with the numbers its
            // engine's emitter plays (ProcessAudioEmitter): its declared level placed over the length it radiates
            // from, its range and its spectrum. By distance alone a 132 dB stock car lost its engine to a quiet
            // electric car a metre nearer.
            var profile = OpenFPS.Common.MachineRegistry.VehicleFor(preset);
            float levelDb = DeclaredLevel(preset, profile);
            var (gain, reference) = _audio.Hearing.Place(levelDb, OutletSeparation(preset));
            float heard = OpenFPS.Audio.Core.VoiceManager.HeardLevel(_audio.Hearing,
                gain * em.Volume, reference, MathF.Max(1f, MathF.Max(em.Range, _audio.Hearing.AudibleRange(levelDb))),
                Vector3.Distance(src.Emission, eyePos), UnaskedOcclusion, levelDb, VoiceKey(em), physical: true);
            // Louder sorts first, so negated. The keep bias and the hold stop the set churning as cars trade
            // places (EngineKeepBias).
            _heardInRanking[entityId] = heard;
            float key = _liveEngines.Contains(entityId) ? -heard / EngineKeepBias : -heard;
            int tier = 2;
            // A donor (last pass's) is not held: held, a new donor ranked first, took a slot inside the
            // budget and turned out the car at its edge for 2.5 s.
            if (_engineStarted.TryGetValue(entityId, out double began) && now - began < EngineMinimumHoldSeconds
                && !_presetDonors.Contains(entityId))
                tier = 1;
            // The one you are sitting in is never ranked out: it is the loudest thing in your world.
            if (entityId == Listener.RidingId) tier = 0;
            _engineOrder.Add((entityId, tier, key, d2));
        }
        // The held cars loudest first among themselves too: all alike and so by id, the first of a kind (whose
        // ring the cars borrowing that kind read) was another car while two of a kind were held, and every
        // borrowed voice of the kind was stopped and started on the other ring, and again when the hold ended.
        // Ties (every car out of earshot) by id, so every machine keeps the same cars (VoiceManager.Update).
        _engineOrder.Sort(static (a, b) => (a.Tier, a.Key, a.Id).CompareTo((b.Tier, b.Key, b.Id)));

        int keep = Math.Min(_adaptiveBudget, _engineOrder.Count);

        // The cars kept outside the budget as their preset's only engine (below), found before anything
        // is let go so a donor keeps its engine and start time. Let go and re-admitted, a donor was held
        // as new, took a slot inside the budget and turned out the car at its edge: that engine and every
        // borrowed voice rebuilt from nothing every 2.5 s while the field stood still.
        _presetsKept.Clear();
        _presetDonors.Clear();
        for (int i = 0; i < _engineOrder.Count; i++)
        {
            if (!_carPreset.TryGetValue(_engineOrder[i].Id, out string? kept)) continue;
            if (_presetsKept.Add(kept) && i >= keep) _presetDonors.Add(_engineOrder[i].Id);
        }

        // A car that lost its engine fades it out rather than being cut mid-waveform.
        foreach (int id in _liveEngines)
        {
            bool survives = _presetDonors.Contains(id);
            for (int i = 0; i < keep && !survives; i++) if (_engineOrder[i].Id == id) { survives = true; break; }
            if (survives) continue;
            _engineEchoes.Forget(id, _audio);
            _engineStarted.Remove(id);
            _voices.Retire(id);
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
            int id = _engineOrder[i].Id;
            if (!_engineStarted.ContainsKey(id))
            {
                if (admitted >= NewEnginesPerUpdate) continue;
                admitted++;
            }
            _liveEngines.Add(id);
            // A car that holds a slot is audible: cheap, idempotent, and the only undo for an abandoned fade.
            _voices.Unretire(id);
            _audio.ReviveEngine(id);
            if (!_engineStarted.ContainsKey(id)) _engineStarted[id] = now;
            _engineSourceByPreset.TryAdd(_carPreset[id], id);
            // Anything that has just earned a real engine gives its borrowed voice back.
            if (_distantBoundTo.Remove(id)) _voices.Stop(BorrowedVoiceId(id));
        }

        // Every preset on the map keeps one live engine, whatever the budget said. A car outside the
        // budget borrows the ring of the nearest car of its own preset, and with none live it is silent
        // (DistantEngine): the city's two slip-on motorcycles, the loudest thing on their street, went
        // silent together. At most one engine per preset in use (eleven on the city, budget 32).
        for (int i = keep; i < _engineOrder.Count; i++)
        {
            int id = _engineOrder[i].Id;
            if (!_carPreset.TryGetValue(id, out string? preset)) continue;
            if (_engineSourceByPreset.ContainsKey(preset)) continue;     // already has a donor
            _liveEngines.Add(id);
            _audio.ReviveEngine(id);
            if (!_engineStarted.ContainsKey(id)) _engineStarted[id] = now;
            _engineSourceByPreset[preset] = id;
            _presetDonors.Add(id);
            _voices.Unretire(id);
            if (_distantBoundTo.Remove(id)) _voices.Stop(BorrowedVoiceId(id));
        }

        ChooseFrontVoices();

        // Whatever is left over, loudest first, borrows.
        _distantVoiced.Clear();
        for (int i = keep; i < _engineOrder.Count && i < keep + _adaptiveDistant; i++)
            _distantVoiced.Add(_engineOrder[i].Id);

        foreach (int car in new List<int>(_distantBoundTo.Keys))
        {
            if (_distantVoiced.Contains(car)) continue;
            _voices.Stop(BorrowedVoiceId(car));
            _distantBoundTo.Remove(car);
        }

        // A census every five seconds: how many cars this machine synthesizes, how many borrow, and how
        // many are past both budgets and silent. Nobody should have to count engines by ear.
        if (now - _lastCensus >= 5.0)
        {
            _lastCensus = now;
            int cars = _engineOrder.Count;
            // Sorted by the keep-biased key, so [0] is not necessarily the nearest.
            float nearestD2 = float.MaxValue;
            foreach (var e in _engineOrder) if (e.D2 < nearestD2) nearestD2 = e.D2;
            float nearest = cars > 0 ? MathF.Sqrt(nearestD2) : 0f;
            Log.Information("Cars: {Cars} on the map — {Live} synthesized, {Borrowed} borrowed, {Silent} out of budget; "
                          + "nearest {Nearest:F0} m; {Echoes} reflection(s) per engine; machines {MachineVoices} of {MachineBudget} voice(s), {Trains} train(s) voiced.",
                            cars, _liveEngines.Count, _distantVoiced.Count,
                            Math.Max(0, cars - _liveEngines.Count - _distantVoiced.Count),
                            nearest, _adaptiveEchoes, _machineVoicesSpent, _adaptiveMachines, _liveTrains.Count);
            Log.Information("  {Voices} reflection voice(s) of a {Budget} budget, floor {Floor:G3}; "
                          + "{Pending} submission(s) queued, {Transients} transient(s) waiting to be heard.",
                            _engineEchoes.VoiceCount, _engineEchoes.MaxReflectionVoices, _engineEchoes.AudibilityFloor,
                            _audio.PendingSubmissions, _oneOffs.Pending_Count);

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
            foreach (var e in _engineOrder) if (_liveEngines.Contains(e.Id)) _censusOrder.Add((e.D2, e.Id));
            _censusOrder.Sort(static (x, y) => x.CompareTo(y));
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

    /// <summary>Recordings played through loudspeakers, rendered once each (LoudspeakerVoices).</summary>
    private readonly LoudspeakerVoices _loudspeakers;
    /// <summary>The loudspeaker render each entity voice is playing, for its walls' copies.</summary>
    private readonly Dictionary<int, OpenFPS.Sound.Core.Signals.LoudspeakerRender> _speakerOf = new();

    /// <summary>When each repeating emitter is next due to speak. See RepeatIntervalSeconds.</summary>
    private readonly Dictionary<int, double> _repeatDue = new();

    /// <summary>Each entity voice's volume and reference distance as last submitted, which its walls'
    /// copies are placed against.</summary>
    private readonly Dictionary<int, (float Volume, float MinDistance, float Range)> _placed = new();

    /// <summary>
    /// A loudspeaker's beam toward the wall a copy came off, on the mixer's three bands. The image sits
    /// mirrored through the wall, so the way the sound left the speaker is the image's line to the
    /// listener mirrored back: d - 2 (d·n) n, with n from the speaker to its image.
    /// </summary>
    internal static (float Low, float Mid, float High) CopyBeam(OpenFPS.Sound.Core.Signals.LoudspeakerRender speaker,
                                                               AudioSource source, Vector3 image, Vector3 listener)
    {
        Vector3 at = source.Emission;
        var local = source.Spec.Direction;
        Vector3 axis = Vector3.Transform(local.LengthSquared() > 0f ? Vector3.Normalize(local) : Vector3.UnitZ, source.State.Rotation);
        Vector3 n = image - at, d = listener - image;
        if (n.LengthSquared() < 1e-6f || d.LengthSquared() < 1e-6f) return (1f, 1f, 1f);
        n = Vector3.Normalize(n); d = Vector3.Normalize(d);
        Vector3 left = d - 2f * Vector3.Dot(d, n) * n;
        return speaker.Radiator.ThreeBand(Vector3.Dot(axis, left), speaker.BandEnergy);
    }

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

    /// <summary>
    /// Which machines are close enough for their two ends to be heard as two: the angle their outlets'
    /// separation subtends from here (Localisation). A car (3.5 m) separates inside about 20 m, a
    /// motorcycle (1 m) inside 6. One extra voice and no extra synthesis (the engine writes both taps),
    /// at the same total level, so crossing the threshold moves the sound and does not change it.
    /// </summary>
    private void ChooseFrontVoices()
    {
        _frontCandidates.Clear();
        foreach (var (id, _, _, d2) in _engineOrder)
        {
            if (!_liveEngines.Contains(id)) continue;
            // From inside, the two ends of the car are not two sources: both come through the body.
            if (id == Listener.RidingId) continue;
            if (!_carPreset.TryGetValue(id, out string? preset)) continue;
            float separation = OutletSeparation(preset);
            if (separation <= 0f) continue;
            if (!OpenFPS.Acoustics.Localisation.Resolvable(separation, MathF.Sqrt(d2))) continue;
            _frontCandidates.Add((id, d2));
        }
        _frontCandidates.Sort(static (a, b) => (a.D2, a.Id).CompareTo((b.D2, b.Id)));

        // A front voice given back fades, not cut: the tap is a running waveform.
        foreach (int id in _frontVoiced)
        {
            bool survives = false;
            for (int i = 0; i < _frontCandidates.Count && i < _adaptiveFront; i++)
                if (_frontCandidates[i].Id == id) { survives = true; break; }
            if (!survives) _voices.Retire(IntakeVoiceId(id));
        }

        _frontVoiced.Clear();
        for (int i = 0; i < _frontCandidates.Count && i < _adaptiveFront; i++)
            _frontVoiced.Add(_frontCandidates[i].Id);
    }

    private readonly List<(int Id, float D2)> _frontCandidates = new();

    /// <summary>How far apart a machine's outlets are, metres, from the vehicle as it is now: its profile is
    /// memoised by MachineRegistry.VehicleFor, which a model edit renews (a cache here never forgot).</summary>
    private static float OutletSeparation(string preset)
    {
        var v = OpenFPS.Common.MachineRegistry.VehicleFor(preset);
        return Vector3.Distance(ExhaustSlot(v), IntakeSlot(v));
    }

    /// <summary>Where the gas leaves, machine frame: the true tailpipe, not where a single voice sits
    /// (VehicleProfile.ExhaustEmitterBias).</summary>
    private static Vector3 ExhaustSlot(OpenFPS.Sound.VehicleProfile v) => v.ExhaustSlot;

    /// <summary>...and the other end: where it breathes, or its nose when the engine is in the back
    /// (VehicleProfile.FrontTapZ).</summary>
    private static Vector3 IntakeSlot(OpenFPS.Sound.VehicleProfile v)
        => new(0f, v.FrontTapHeight, v.FrontTapZ);

    /// <summary>
    /// A machine's front outlet, placed: it reads the front tap of the entity's live engine
    /// (EngineTapState), so it costs a buffer read and an HRTF. All but the position is the machine's
    /// other voice's: level reference, range, path, sampled time.
    /// </summary>
    private void FrontVoice(IAcousticWorld world, AudioSource src, OpenFPS.Sound.VehicleProfile profile, Vector3 eyePos,
                            float levelDb, float volume, float minDistance, float range, double sampledAt)
    {
        int voiceId = IntakeVoiceId(src.Id);
        Vector3 pos = src.State.Position
                    + Vector3.Transform(IntakeSlot(profile), src.State.Rotation);

        var e = new SpatialEmitter
        {
            EntityId = voiceId,
            Seed = VoiceRegistry.SeedOf(VoiceKind.Intake, src.Id),
            SoundId = "engine-intake",
            IsSynth = true,
            IntakeOfEntity = src.Id,
            EngineKey = "",
            Mode = PlaybackMode.LoopOne,
            Type = EmitterType.EntityAttached,
            Velocity = src.State.Velocity,
            PositionSampledAt = sampledAt,
            // The exhaust voice's level reference: the taps already carry the difference between intake
            // and tailpipe, and a second, guessed one here would be a taste constant.
            Volume = volume,
            MinDistance = minDistance,
            ExtentMetres = minDistance,
            Range = range,
            Pitch = 1f,
        };
        // Its path every frame: it kept the tone it started with, a car turning behind a wall within 20 m
        // still breathing in the open (S1).
        HandPath(ref e, VoiceKind.Intake, world, src, pos, levelDb, eyePos);
        ApplyGround(ref e, _groundWorld);
        _voices.Sound(e);
    }

    // ── The cabin you are sitting in, path by path (CabinPaths) ────────────────────────────────────

    /// <summary>The vehicle whose cabin paths have voices, and how many paths it has.</summary>
    private int _cabinVoiced = int.MinValue, _cabinVoicedPaths;
    private int _cabinSeenFrame = -1;

    /// <summary>The voice of a path into the cabin you sit in, whichever vehicle that is.</summary>
    /// <summary>How near a vehicle has to be for its cabin to be traced ahead: a few steps from its door.</summary>
    internal const float CabinAheadMetres = 8f;

    private void PrepareNearestCabin()
    {
        int nearest = -1;
        float best = CabinAheadMetres * CabinAheadMetres;
        foreach (var e in _engineOrder)
            if (e.D2 < best) { best = e.D2; nearest = e.Id; }
        if (nearest >= 0 && _carPreset.TryGetValue(nearest, out string? preset))
            _acousticWorker.Traced.Prepare(preset);
    }

    private int CabinVoiceId(int path) => _voices.IdFor(VoiceKind.Cabin, VoiceRegistry.NoEntity, path);

    /// <summary>
    /// Every path into the cabin but the bulkhead (which is the engine's own voice): a tap each on the
    /// engine's ring for it, riding with the head, turned to where the path comes in. Everything about
    /// each that is not the direction is the interior voice's: the same level, the same distance, the
    /// same room. See CabinPaths and EngineTapState.
    /// </summary>
    private void CabinVoices(IAcousticWorld world, AudioSource src, OpenFPS.Sound.Core.Engine.CabinPaths.Layout layout,
                             Vector3 ear, in SpatialEmitter engine, Vector3 eyePos)
    {
        if (_cabinVoiced != src.Id) RetireCabinVoices();
        _cabinVoiced = src.Id;
        _cabinVoicedPaths = layout.Count;
        _cabinSeenFrame = _frameCount;
        for (int p = 1; p < layout.Count; p++)
        {
            int voiceId = CabinVoiceId(p);
            var offset = Vector3.Transform(_audio.Options.CabinOffset(layout, p, ear), src.State.Rotation);
            var e = engine;
            e.EntityId = voiceId;
            e.Seed = VoiceRegistry.SeedOf(VoiceKind.Cabin, VoiceRegistry.NoEntity, p);
            e.SoundId = "engine-cabin";
            e.EngineKey = "";
            e.PhysicalKey = "";
            e.CabinOfEntity = src.Id;
            e.CabinPath = p;
            e.IntakeOfEntity = 0;
            e.EchoOfEntity = 0;
            e.Interior = false;
            e.FollowsListener = true;
            e.ListenerOffset = offset;
            // A voice on the head still needs a world position: the budget culls by it.
            HandPath(ref e, VoiceKind.Cabin, world, src, eyePos + offset, engine.EarLevelDb, eyePos);
            _voices.Sound(e);
        }
    }

    /// <summary>Once a frame: the cabin's voices fade and stop when you leave that vehicle or its engine
    /// loses its voice.</summary>
    private void UpdateCabinVoices()
    {
        if (_cabinVoiced != int.MinValue && _cabinSeenFrame != _frameCount) RetireCabinVoices();
    }

    private void RetireCabinVoices()
    {
        if (_cabinVoiced == int.MinValue) return;
        for (int p = 1; p < _cabinVoicedPaths; p++) _voices.Retire(CabinVoiceId(p));
        _cabinVoiced = int.MinValue;
        _cabinVoicedPaths = 0;
    }

    // ── Extended sources: a tree's crown, a fire's bed (ExtendedSources) ───────────────────────────

    /// <summary>The voice of one outer place of an extended source, place 1 up.</summary>
    internal int PlaceVoiceId(int sourceId, int place) => _voices.IdFor(VoiceKind.Place, sourceId, place);

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
    private readonly List<(float Key, int Id, int Outer)> _placeCandidates = new();

    /// <summary>Which live extended sources may spread this frame: the loudest at the ear first (the level the
    /// machines were ranked by, VoiceManager.HeardLevel), while their outer places fit in
    /// <see cref="_adaptivePlaces"/>. One that wants none (too far to be heard as wide) asks for nothing.</summary>
    private void ChoosePlaces(Vector3 eyePos)
    {
        _placesGranted.Clear();
        _placeCandidates.Clear();
        foreach (int id in _liveMachines)
        {
            if (!_sources.TryGet(id, out var src)) continue;
            string key = VoiceKey(src.Spec);
            var at = src.Emission;
            if (LinePathOf(src, key) is { } path)
            {
                // A line: as many pieces as the angle it covers here asks for, the rest of it merged.
                if (!_cutting.TryGetValue(id, out var c)) _cutting[id] = c = new Cutting();
                c.Wanted = OpenFPS.Sound.Core.Nature.ExtendedSources.WantedPieces(path, eyePos, c.Wanted);
                c.Count = 1;
                if (c.Wanted > 1) _placeCandidates.Add((-_heardInRanking.GetValueOrDefault(id), id, c.Wanted - 1));
                continue;
            }
            var layout = _audio.Options.WideSources.Layout(key, ModelLibrary.Presets);
            if (layout == null) continue;
            float d2 = Vector3.DistanceSquared(at, eyePos);
            if (_audio.Options.WideSources.SpreadFor(
                    OpenFPS.Sound.Core.Nature.ExtendedSources.Reach(layout), MathF.Sqrt(d2)) <= 0f) continue;
            // Louder sorts first, so negated.
            _placeCandidates.Add((-_heardInRanking.GetValueOrDefault(id), id, layout.Length - 1));
        }
        _placeCandidates.Sort(static (a, b) => (a.Key, a.Id).CompareTo((b.Key, b.Id)));
        int left = _adaptivePlaces;
        foreach (var (_, id, outer) in _placeCandidates)
        {
            // A line takes what is left and is cut coarser; a crown or a fire all its places or none.
            if (_cutting.TryGetValue(id, out var line)) line.Count = 1 + OpenFPS.Sound.Core.Nature.ExtendedSources.Grant(outer, cutsCoarser: true, ref left);
            else if (OpenFPS.Sound.Core.Nature.ExtendedSources.Grant(outer, cutsCoarser: false, ref left) > 0) _placesGranted.Add(id);
        }
    }

    /// <summary>
    /// For the ranking: how much louder a line cut into pieces is here than a point at <paramref name="distance"/>,
    /// both by the mixer's law (ExtendedSources.MeanLawPower). One for anything else, and further off than twice its
    /// length, where the two agree within a decibel.
    /// </summary>
    private float LineHeard(AudioSource src, string key, float reference, Vector3 eyePos, float distance)
    {
        if (LinePathOf(src, key) is not { } path || distance > 2f * path.Length) return 1f;
        float point = MathF.Min(1f, MathF.Max(0.1f, reference) / MathF.Max(1e-3f, distance));
        return MathF.Sqrt(OpenFPS.Sound.Core.Nature.ExtendedSources.MeanLawPower(path, eyePos, reference)) / point;
    }

    // ── A line's own path (SoundEmitterComponent.Line, 2026-10-17) ───────────────────────────────

    private sealed record LinePathEntry(Vector3 Position, Quaternion Rotation, Vector3[]? Line, float Length,
                                        OpenFPS.Sound.Core.Nature.LinePath Path);
    private readonly Dictionary<int, LinePathEntry> _linePaths = new();

    /// <summary>The path a line source is heard along (its spec's own line, or straight along its x), kept while it
    /// stands where it stood; null for anything that is not a line this world cuts.</summary>
    private OpenFPS.Sound.Core.Nature.LinePath? LinePathOf(AudioSource src, string key)
    {
        float length = _audio.Options.WideSources.LineLength(key, ModelLibrary.Presets);
        var line = src.Spec.Line;
        // Running water along a line (its own, or straight along its x), or a shore's whole outline.
        if (length <= 0f && !(line is { Length: >= 3 } && _audio.Options.WideSources.IsOutline(key))) return null;
        var at = src.Emission;
        var turn = src.State.Rotation;
        if (_linePaths.TryGetValue(src.Id, out var known) && known.Position == at && known.Rotation == turn
            && ReferenceEquals(known.Line, line) && known.Length == length)
            return known.Path;
        var path = OpenFPS.Sound.Core.Nature.WideSources.PathOf(at, turn, line, length,
                                                                             _audio.Options.WideSources.LineWidth(key, ModelLibrary.Presets));
        _linePaths[src.Id] = new LinePathEntry(at, turn, line, length, path);
        return path;
    }

    /// <summary>Where a source is heard from first, for its path and its distance: a line's point nearest the ear (stood
    /// beside its near part, whatever its middle is behind), anything else its emission point.</summary>
    private Vector3 HeardPoint(AudioSource src, Vector3 ear, out bool alongLine)
    {
        var path = src.Spec.IsSynth ? LinePathOf(src, VoiceKey(src.Spec)) : null;
        alongLine = path != null;
        return path != null ? path.Nearest(ear, out _) : src.Emission;
    }
    private readonly List<int> _spreadingGone = new();
    private long _spreadFrame;

    /// <summary>
    /// How much of a tree or a fire its outer places carry from here, slewed, applied to its own voice's emitter
    /// before it is submitted. Each place plays its share at the source's own gain from where it is, so together
    /// they are what the mixer's law makes of each part of it (ExtendedSources.PlaceRule.Pieces); under the old
    /// rule, one gain held them to the source heard from its middle (ExtendedSources.Balance).
    /// </summary>
    private Spreading SpreadOf(AudioSource src, Vector3[] layout, ref SpatialEmitter e, Vector3 eyePos, float dt, double now)
    {
        if (!_spreading.TryGetValue(src.Id, out var sp) || sp.Layout != layout)
            _spreading[src.Id] = sp = new Spreading { Layout = layout, At = new Vector3[layout.Length] };
        sp.Frame = _spreadFrame;
        for (int i = 0; i < layout.Length; i++) sp.At[i] = e.Position + Vector3.Transform(layout[i], src.State.Rotation);
        float target = !_placesGranted.Contains(src.Id) ? 0f : _audio.Options.WideSources.SpreadFor(
            OpenFPS.Sound.Core.Nature.ExtendedSources.Reach(layout), Vector3.Distance(eyePos, e.Position));
        sp.Spread = OpenFPS.Sound.Core.Nature.ExtendedSources.Slew(sp.Spread, target, MathF.Min(dt, 0.25f));
        if (_audio.Options.WideSources.Rule == OpenFPS.Sound.Core.Nature.ExtendedSources.PlaceRule.Middle)
        {
            Span<float> shares = stackalloc float[layout.Length];
            OpenFPS.Sound.Core.Nature.ExtendedSources.Shares(sp.Spread, shares);
            e.Volume *= OpenFPS.Sound.Core.Nature.ExtendedSources.Balance(eyePos, e.Position, sp.At, shares, e.MinDistance, e.Range);
        }
        e.Spread = sp.Spread;
        if (sp.Spread > 0f) sp.MergedSince = double.NaN;
        else if (double.IsNaN(sp.MergedSince)) sp.MergedSince = now;
        return sp;
    }

    /// <summary>
    /// The outer places of a tree or a fire, each a voice at its own point reading its own stream of the
    /// one synth. Everything but the point is the source's own emitter: the same level reference, range,
    /// path (carried, HandPath), region and declared level, and the same balance gain, so the places and
    /// the middle are one source. Kept while any of it is spread, and <see cref="MergeHoldSeconds"/> after.
    /// </summary>
    private void PlaceOuter(int sourceId, Spreading sp, in SpatialEmitter middle, IAcousticWorld world, double now)
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
            e.Seed = VoiceRegistry.SeedOf(VoiceKind.Place, sourceId, k);
            e.Position = sp.At[k];
            e.ApparentPosition = middle.ApparentPosition + (sp.At[k] - middle.Position);
            // The voice whose synth it reads, by that voice's id.
            e.PlaceOfEntity = middle.EntityId;
            e.Place = k;
            e.Spread = 0f;
            e.StartSoundId = "";
            e.StopSoundId = "";
            ApplyGround(ref e, world);
            // Its middle's path, carried in the copy of its middle's emitter: no second writer.
            _voices.Declare(e.EntityId, sourceId, VoiceKind.Place, sp.Layout[k], e.EarLevelDb);
            _voices.Sound(e);
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
            if (now) _voices.Stop(id);
            else _voices.Retire(id);
        }
        sp.Voiced = false;
    }

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
        _spreadingGone.Clear();
        foreach (var (id, c) in _cutting)
            if (c.Frame != _spreadFrame) _spreadingGone.Add(id);
        foreach (int id in _spreadingGone)
            if (_cutting.Remove(id, out var c)) StopPieces(id, c, 1);
        _spreadFrame++;
    }

    // ── A line cut into pieces at the ear (ExtendedSources.Cut, 2026-10-16) ─────────────────────────

    private sealed class Cutting
    {
        /// <summary>How many pieces the angle it covers asks for (held between the merge and split angles), and
        /// how many the place budget gave it this frame.</summary>
        public int Wanted, Count = 1;
        public OpenFPS.Sound.Core.Nature.ExtendedSources.LineCut? Cut;
        /// <summary>Where the ear was when it was cut: it is cut again when the ear has moved a quarter of its
        /// shortest piece, the one beside you.</summary>
        public Vector3 CutAt;
        /// <summary>The source's own emitter as built, before its piece moves it, and the path its line runs along.</summary>
        public SpatialEmitter Base;
        public OpenFPS.Sound.Core.Nature.LinePath? Path;
        /// <summary>How many of its places have voices, and since when the cut has had fewer.</summary>
        public int Voiced = 1;
        public double FewerSince = double.NaN;
        public long Frame;
    }
    private readonly Dictionary<int, Cutting> _cutting = new();

    /// <summary>
    /// A line cut at the ear: the pieces it is heard as (the place budget's count, ChoosePlaces), handed to its synth
    /// when they change, and its own voice moved to the piece that holds its middle, as loud as everything in it.
    /// A few closed forms a piece (ExtendedSources.Place), so on this thread.
    /// </summary>
    private Cutting CutOf(AudioSource src, OpenFPS.Sound.Core.Nature.LinePath path, ref SpatialEmitter e, Vector3 eyePos)
    {
        if (!_cutting.TryGetValue(src.Id, out var c)) _cutting[src.Id] = c = new Cutting();
        c.Frame = _spreadFrame;
        c.Base = e;
        int n = Math.Max(1, c.Count);
        if (c.Cut == null || c.Cut.Count != n || !ReferenceEquals(c.Path, path)
            || n > 1 && Vector3.Distance(eyePos, c.CutAt) > 0.25f * c.Cut.Shortest)
        {
            c.Cut = n == 1 ? OpenFPS.Sound.Core.Nature.ExtendedSources.Whole(path) : OpenFPS.Sound.Core.Nature.ExtendedSources.Cut(path, eyePos, n);
            c.CutAt = eyePos;
            _audio.PlanPieces(e.EntityId, c.Cut);
        }
        c.Path = path;
        PlacePiece(ref e, c, eyePos, 0);
        e.Spread = 0f;
        return c;
    }

    /// <summary>One place of a cut line: at its piece's centroid, at the gain that makes it as loud as its piece.
    /// Returns where along the line it is, m from the middle.</summary>
    private static float PlacePiece(ref SpatialEmitter e, Cutting c, Vector3 eyePos, int place)
    {
        var cut = c.Cut!;
        int i = cut.PieceOfPlace(place);
        var p = OpenFPS.Sound.Core.Nature.ExtendedSources.Place(c.Path!, eyePos, cut.Edges[i], cut.Edges[i + 1], c.Base.MinDistance);
        var offset = p.At - c.Base.Position;
        e.Position = p.At;
        e.ApparentPosition = c.Base.ApparentPosition + offset;
        e.Volume = c.Base.Volume * p.Gain;
        return p.Along;
    }

    /// <summary>
    /// The other pieces of a cut line, each a voice at its own point reading its own stream of the one synth: every
    /// event in the piece it happens in, never a copy. Places the cut no longer has keep their last placing for
    /// <see cref="MergeHoldSeconds"/>, so what was rendered into them rings out, and are then let go.
    /// </summary>
    private void PlacePieces(int sourceId, Cutting c, in SpatialEmitter middle, IAcousticWorld world, Vector3 eyePos, double now)
    {
        var cut = c.Cut!;
        for (int k = 1; k < cut.Count; k++)
        {
            var e = middle;
            float along = PlacePiece(ref e, c, eyePos, k);
            e.EntityId = PlaceVoiceId(sourceId, k);
            e.Seed = VoiceRegistry.SeedOf(VoiceKind.Place, sourceId, k);
            // The voice whose synth it reads, by that voice's id.
            e.PlaceOfEntity = middle.EntityId;
            e.Place = k;
            e.Spread = 0f;
            e.StartSoundId = "";
            e.StopSoundId = "";
            ApplyGround(ref e, world);
            _voices.Declare(e.EntityId, sourceId, VoiceKind.Place, new Vector3(along, 0f, 0f), e.EarLevelDb);
            _voices.Sound(e);
        }
        if (cut.Count >= c.Voiced) { c.Voiced = cut.Count; c.FewerSince = double.NaN; return; }
        if (double.IsNaN(c.FewerSince)) c.FewerSince = now;
        else if (now - c.FewerSince >= MergeHoldSeconds) StopPieces(sourceId, c, cut.Count);
    }

    /// <summary>What a walk hears of some sources at one point (the tests and AudioLab --extent-walk).</summary>
    internal readonly record struct Together(double Power, float BearingDegrees, float WidthDegrees, int Voices);

    /// <summary>
    /// What the mixer makes at <paramref name="ear"/> of every live voice whose physical key <paramref name="source"/>
    /// takes: each voice's share of its source (its piece's share of a cut line, or its place's share at its middle's
    /// spread) times the mixer's law at its own place and gain (Loudness.RenderedGain, its headroom's return taken
    /// off), with where they come from (the bearing of their power-weighted directions on the flat) and how wide
    /// (twice the arc cosine of their resultant: two equal sources ±θ either side are 2θ). Paths are left out.
    /// The places and lines are those of the world the voices were placed in (<paramref name="wide"/>).
    /// </summary>
    internal static Together HeardTogether(IReadOnlyDictionary<int, SpatialEmitter> latest, IEnumerable<int> live,
                                           IReadOnlyDictionary<int, OpenFPS.Sound.Core.Nature.ExtendedSources.LineCut> pieces,
                                           Func<string, bool> source, Vector3 ear,
                                           OpenFPS.Sound.Core.Nature.WideSources wide)
    {
        double power = 0, x = 0, z = 0;
        int voices = 0;
        foreach (int id in live)
        {
            if (!latest.TryGetValue(id, out var e) || !source(e.PhysicalKey ?? "")) continue;
            float g = OpenFPS.Sound.Loudness.RenderedGain(OpenFPS.Audio.Core.VoiceManager.WithoutHeadroom(e.Volume, e.HeadroomGain),
                                                          e.MinDistance, e.Range, Vector3.Distance(e.Position, ear));
            double p = ShareOfVoice(latest, pieces, e, wide) * (double)g * g;
            voices++;
            power += p;
            var to = e.Position - ear;
            float flat = MathF.Sqrt(to.X * to.X + to.Z * to.Z);
            if (flat > 1e-4f) { x += p * to.X / flat; z += p * to.Z / flat; }
        }
        double r = power > 0 ? Math.Sqrt(x * x + z * z) / power : 1.0;
        float bearing = (float)(Math.Atan2(x, z) * 180.0 / Math.PI);
        if (bearing < 0f) bearing += 360f;
        return new Together(power, bearing, (float)(2.0 * Math.Acos(Math.Clamp(r, 0.0, 1.0)) * 180.0 / Math.PI), voices);
    }

    /// <summary>A voice's share of its source's power; one for a voice that is a source of its own.</summary>
    private static float ShareOfVoice(IReadOnlyDictionary<int, SpatialEmitter> latest,
                                      IReadOnlyDictionary<int, OpenFPS.Sound.Core.Nature.ExtendedSources.LineCut> pieces, in SpatialEmitter e,
                                      OpenFPS.Sound.Core.Nature.WideSources wide)
    {
        var middle = e;
        if (e.PlaceOfEntity != 0 && !latest.TryGetValue(e.PlaceOfEntity, out middle)) return 0f;
        if (pieces.TryGetValue(middle.EntityId, out var cut)) return cut.ShareOfPlace(e.Place);
        if (wide.LineLength(middle.PhysicalKey, ModelLibrary.Presets) > 0f || wide.IsOutline(middle.PhysicalKey))
            return e.Place == 0 ? 1f : 0f;
        var layout = wide.Layout(middle.PhysicalKey, ModelLibrary.Presets);
        if (layout == null) return 1f;
        Span<float> shares = stackalloc float[layout.Length];
        OpenFPS.Sound.Core.Nature.ExtendedSources.Shares(middle.Spread, shares);
        return e.Place >= 0 && e.Place < shares.Length ? shares[e.Place] : 0f;
    }

    /// <summary>Lets go of a cut line's places from <paramref name="from"/> up: faded, or at once when the source is gone.</summary>
    private void StopPieces(int sourceId, Cutting c, int from, bool now = false)
    {
        for (int k = Math.Max(1, from); k < c.Voiced; k++)
        {
            int id = PlaceVoiceId(sourceId, k);
            _groundCache.Remove(id);
            if (now) _voices.Stop(id);
            else _voices.Retire(id);
        }
        c.Voiced = Math.Max(1, Math.Min(c.Voiced, from));
        c.FewerSince = double.NaN;
    }

    /// <summary>The rain round the listener: the surfaces it lands on, as a few voices. See RainField.</summary>
    private readonly RainField _rain;

    /// <summary>The rain, for a test.</summary>
    internal RainField Rain => _rain;

    /// <summary>Paths asked for horns, sirens and trains, for a test.</summary>
    internal long VehiclePathsAsked;

    /// <summary>Vehicles whose siren is sounding, found this frame.</summary>
    private readonly HashSet<int> _sirenCars = new();
    private readonly List<int> _sirensGone = new();

    /// <summary>
    /// Every siren on the map, placed every frame whatever its car's engine is doing. Not from the car's
    /// emitter pass, which runs only for an engine that won a voice: a siren is 35 dB louder than its
    /// engine, and a car dropped from the budget left its siren wailing where it had been.
    /// </summary>
    private void UpdateSirens(IAcousticWorld world, Vector3 eyePos)
    {
        _sirenCars.Clear();
        for (int i = 0, n = _sources.MovingCount; i < n; i++)
        {
            var src = _sources.MovingAt(i);
            string? sid = src.Spec.SoundId;
            if (OpenFPS.Common.ModelKindTable.KnownVehiclePreset(sid) is not { } preset
                || OpenFPS.Common.MachineRegistry.VehicleFor(preset).Siren is not { } sirenKey) continue;
            // Asked about only while it sounds: switched off, every patrol car on the map asked the worker
            // every frame and was added to the path pass, and with no engine voice ran the hand-rolled tracer
            // on this thread for nothing.
            var mode = SirenModeOf(src);
            if (mode == OpenFPS.Sound.SirenMode.Off)
            {
                if (_sirenVoiced.Remove(src.Id)) _voices.Stop(SirenVoiceId(src.Id));
                // Within earshot, its head is made now off the threads that place sounds, so it sounds the
                // moment it is switched on: made then, it held every source still for 70-100 ms.
                else PrepareSiren(src, sirenKey, eyePos);
                continue;
            }
            _sirenCars.Add(src.Id);
            SirenVoice(world, src, sirenKey, mode, eyePos);
        }
        _sirensGone.Clear();
        foreach (int id in _sirenVoiced) if (!_sirenCars.Contains(id)) _sirensGone.Add(id);
        foreach (int id in _sirensGone)
        {
            _sirenVoiced.Remove(id);
            _voices.Stop(SirenVoiceId(id));
        }
    }

    /// <summary>
    /// The path for a sounding entity the worker has no answer for yet (a car that just won a voice): the
    /// worker's answer for a source near this one, moved here, or failing that the one-shots' path; and
    /// the worker is asked now. Started on an unoccluded line, a car behind a building came in at full
    /// level for a fifth of a second (2026-10-03).
    /// </summary>
    private AcousticPathData FirstAnswer(IAcousticWorld world, AudioSource src, Vector3 eyePos)
    {
        var at = HeardPoint(src, eyePos, out _);
        _acousticWorker.EnqueueRequest(new AcousticRequest
        {
            EntityId = src.Id, ListenerPos = eyePos, SourcePos = at,
            SourceRadius = src.ProbeRadius,
        });
        var path = _acoustics.CalculateAcousticPath(world, src.Id, eyePos, at);
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

    /// <summary>A vehicle's honked horn's voice, one per vehicle.</summary>
    private int HornVoiceId(int carId) => _voices.IdFor(VoiceKind.Horn, carId);

    /// <summary>Trains sounding their horn or bell ("preset/train"), until when. Signal sources get a
    /// voice only meanwhile: ranked at a horn's 139 dB, a silent one would hold a voice from kilometres off.</summary>
    private readonly Dictionary<string, double> _trainSignals = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A train sounds for a crossing on its own horn (or whistle) and bell (TrainSignal); this keeps the
    /// signal sources ranked while it lasts. A synth made later takes the signal, less the time gone.
    /// </summary>
    public void StartTrainSignal(string train, float[] warning, float bellSeconds)
    {
        _trainSignals[train] = _now() + OpenFPS.Sound.TrainSignal.Duration(warning, bellSeconds) + 1.0;
        _audio.SignalTrain(train, warning, bellSeconds, 0.0);
    }

    /// <summary>A train's horn, whistle or bell source, while the train is not sounding it.</summary>
    private bool SilentSignal(string soundId, double now)
    {
        if (!OpenFPS.Common.ModelKindTable.Is(soundId, OpenFPS.Common.ModelSynth.Rail)) return false;
        if (!_signalTrains.TryGetValue(soundId, out string? train))
        {
            train = null;
            if (OpenFPS.Audio.Fmod.TrainVoiceState.ParseKey(soundId, out string preset, out string name, out int index))
            {
                try
                {
                    var layout = TrainLayoutOf(preset);
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

    /// <summary>A kind of train's sources, by preset. Once each: worked out per source, the 160 of a freight
    /// built its whole layout 160 times on the frame it was first seen (30 ms).</summary>
    private IReadOnlyList<OpenFPS.Sound.TrainLayout.Entry> TrainLayoutOf(string preset)
    {
        if (!_trainLayouts.TryGetValue(preset, out var layout))
            _trainLayouts[preset] = layout = OpenFPS.Sound.TrainLayout.Sources(OpenFPS.Sound.TrainProfile.ByName(preset), ModelLibrary.Presets).ToArray();
        return layout;
    }
    private readonly Dictionary<string, IReadOnlyList<OpenFPS.Sound.TrainLayout.Entry>> _trainLayouts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Horns being sounded: the vehicle, the key its voice was built from, and when the
    /// voice may be let go.</summary>
    private readonly Dictionary<int, (string Key, double Until)> _horns = new();
    private readonly List<int> _hornsDone = new();

    /// <summary>
    /// Somebody on the street sounded their horn: the voice is built next frame on the vehicle and lasts
    /// the rhythm. A second honk from the same car replaces the first (one horn, one hand).
    /// </summary>
    public void StartHorn(int entityId, string horn, float[] rhythm)
    {
        if (_horns.Remove(entityId)) _voices.Stop(HornVoiceId(entityId));
        _horns[entityId] = (OpenFPS.Sound.Honk.Key(horn, rhythm),
                            _audio.Clock.Now + OpenFPS.Sound.Honk.Duration(rhythm) + 1.0);
    }

    /// <summary>Places every sounding horn at the front of its vehicle, through the vehicle's own path.</summary>
    private void UpdateHorns(IAcousticWorld world, Vector3 eyePos, double now)
    {
        UpdateHeldHorns(world, eyePos, _now());
        if (_horns.Count == 0) return;
        _hornsDone.Clear();
        foreach (var (id, horn) in _horns)
        {
            if (now > horn.Until || !_sources.TryGet(id, out var src))
            {
                _hornsDone.Add(id);
                continue;
            }
            int voiceId = HornVoiceId(id);
            // Where the vehicle's model has its horn: behind a car's grille, on a train its horn source.
            Vector3 pos = HornAt(src);
            float levelDb = OpenFPS.Sound.Honk.LevelDb(horn.Key[OpenFPS.Sound.Honk.Prefix.Length..horn.Key.LastIndexOf(':')], ModelLibrary.Presets);
            var (gain, reference) = _audio.Hearing.Place(levelDb);
            var e = new SpatialEmitter
            {
                EntityId = voiceId,
                Seed = VoiceRegistry.SeedOf(VoiceKind.Horn, id),
                SoundId = "horn",
                IsSynth = true,
                PhysicalKey = horn.Key,
                EngineKey = "",
                Mode = PlaybackMode.LoopOne,
                Type = EmitterType.EntityAttached,
                Velocity = src.State.Velocity,
                PositionSampledAt = _sources.SampledAt,
                Direction = Vector3.Transform(Vector3.UnitZ, src.State.Rotation),
                Volume = gain,
                EarLevelDb = levelDb,
                MinDistance = reference,
                Range = _audio.Hearing.AudibleRange(levelDb),
                Pitch = 1f,
                EngineRunning = true,
            };
            // From the horn where the car is now, turned toward the path's edge only when blocked. The path's
            // own point is the exhaust as of the worker's last answer (up to ten frames old): a moving car's
            // horn would trail behind it, from its tailpipe.
            HandPath(ref e, VoiceKind.Horn, world, src, pos, levelDb, eyePos);
            ApplyGround(ref e, _groundWorld);
            _voices.Sound(e);
        }
        foreach (int id in _hornsDone)
        {
            _horns.Remove(id);
            _voices.Stop(HornVoiceId(id));
        }
    }

    // ── Tyres over the rails ────────────────────────────────────────────────────────────────
    //
    // A level crossing's rails, from the map's roads. A voiced vehicle about to roll a wheel over one has
    // the strike scheduled in its voice for the moment the wheel reaches the rail (WheelStrikes), once
    // the wheel is under a second away (longer than any voice renders ahead); let go once well past.

    private IReadOnlyList<RailCrossing> _crossings = Array.Empty<RailCrossing>();
    private readonly Dictionary<(int Id, int Wheel, int Rail), double> _struck = new();
    private readonly Dictionary<string, (Vector3[] At, float[] Load, float[] Radius)> _wheelLayouts = new();
    private readonly List<OpenFPS.Sound.Core.WheelStrike> _strikeBatch = new();
    private readonly List<(int, int, int)> _unstruck = new();

    /// <summary>The map's level crossings, as the rails lie (null on a map without).</summary>
    public void SetCrossings(IReadOnlyList<RailCrossing>? crossings)
    {
        _crossings = crossings ?? Array.Empty<RailCrossing>();
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
        var body = new WheelDynamics(v.Mechanics);
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
    internal OpenFPS.Sound.Core.WheelStrike[]? RailStrikes(AudioSource src, string preset, double sampledAt)
    {
        if (_crossings.Count == 0) return null;
        var vel = new Vector3(src.State.Velocity.X, 0f, src.State.Velocity.Z);
        float speed = vel.Length();
        var pos = src.State.Position;
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
            var (left, right) = (c.Left, c.Right);
            for (int r = 0; r < 2; r++)
            {
                var rail = r == 0 ? left : right;
                for (int i = 0; i < layout.Value.At.Length; i++)
                {
                    var p = pos + Vector3.Transform(layout.Value.At[i], src.State.Rotation);
                    var d = new Vector3(p.X - rail.X, 0f, p.Z - rail.Z);
                    float s = Vector3.Dot(d, across);
                    var key = (src.Id, i, ci * 2 + r);
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
                    _strikeBatch.Add(new OpenFPS.Sound.Core.WheelStrike(
                        i, at,
                        OpenFPS.Sound.Core.WheelStrikes.PeakPascals(speed, layout.Value.Load[i], OpenFPS.Sound.Core.WheelStrikes.CrossingStepMetres),
                        OpenFPS.Sound.Core.WheelStrikes.ContactSeconds(speed, radius)));
                }
            }
        }
        foreach (var k in _unstruck) _struck.Remove(k);
        return _strikeBatch.Count == 0 ? null : _strikeBatch.ToArray();
    }

    /// <summary>The voice of a horn a driver is holding down, one per vehicle.</summary>
    private int HeldHornVoiceId(int carId) => _voices.IdFor(VoiceKind.HeldHorn, carId);

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
    private void UpdateHeldHorns(IAcousticWorld world, Vector3 eyePos, double now)
    {
        for (int i = 0, n = _sources.MovingCount; i < n; i++)
        {
            var src = _sources.MovingAt(i);
            if (!OpenFPS.Sound.VehicleSignalBits.IsManual(src.State.Signals)) continue;
            bool held = OpenFPS.Sound.VehicleSignalBits.HornHeld(src.State.Signals);
            if (!held && !_heldHorns.ContainsKey(src.Id)) continue;
            if (OpenFPS.Common.ModelKindTable.Vehicle(src.Spec.SoundId) is not { } vehicle) continue;
            string horn = OpenFPS.Sound.VehicleProfile.HornFor(vehicle);
            if (held) _heldHorns[src.Id] = double.NaN;
            else if (double.IsNaN(_heldHorns[src.Id])) _heldHorns[src.Id] = now;
            // Its valve has had its time: stopped below, and not placed once more first.
            if (!held && now - _heldHorns[src.Id] > HornReleaseSeconds) continue;
            HeldHornVoice(world, src, horn, held, eyePos);
        }
        _heldDone.Clear();
        foreach (var (id, releasedAt) in _heldHorns)
            if (!_sources.Contains(id) || (!double.IsNaN(releasedAt) && now - releasedAt > HornReleaseSeconds))
                _heldDone.Add(id);
        foreach (int id in _heldDone)
        {
            _heldHorns.Remove(id);
            _voices.Stop(HeldHornVoiceId(id));
        }
    }

    private void HeldHornVoice(IAcousticWorld world, AudioSource src, string horn, bool blowing, Vector3 eyePos)
    {
        int voiceId = HeldHornVoiceId(src.Id);
        // Let go before it ever sounded: nothing to close.
        if (!blowing && !_voices.IsRegistered(voiceId)) return;
        Vector3 pos = HornAt(src);
        float levelDb = OpenFPS.Sound.Honk.LevelDb(horn, ModelLibrary.Presets);
        var (gain, reference) = _audio.Hearing.Place(levelDb);
        var e = new SpatialEmitter
        {
            EntityId = voiceId,
            Seed = VoiceRegistry.SeedOf(VoiceKind.HeldHorn, src.Id),
            SoundId = "horn",
            IsSynth = true,
            PhysicalKey = OpenFPS.Sound.Honk.HoldKey(horn),
            EngineKey = "",
            Mode = PlaybackMode.LoopOne,
            Type = EmitterType.EntityAttached,
            Velocity = src.State.Velocity,
            PositionSampledAt = _sources.SampledAt,
            Direction = Vector3.Transform(Vector3.UnitZ, src.State.Rotation),
            Volume = gain,
            EarLevelDb = levelDb,
            MinDistance = reference,
            Range = _audio.Hearing.AudibleRange(levelDb),
            Pitch = 1f,
            // The hand on the horn: the voice blows while this is set.
            EngineRunning = blowing,
        };
        // One apparent position, the horn's: the path pass also handed it the exhaust's (S2).
        HandPath(ref e, VoiceKind.HeldHorn, world, src, pos, levelDb, eyePos);
        ApplyGround(ref e, _groundWorld);
        _voices.Sound(e);
    }

    /// <summary>A switched-off siren within earshot of its level: the head its voice would start with, made
    /// ahead (VoiceBuilds).</summary>
    private void PrepareSiren(AudioSource src, string sirenKey, Vector3 eyePos)
    {
        OpenFPS.Sound.SirenSpec spec;
        try { spec = OpenFPS.Sound.SirenSpec.ByName(sirenKey); }
        catch { return; }
        if (Vector3.Distance(SirenMouth(src), eyePos) > _audio.Hearing.AudibleRange(spec.SourceLevelDb)) return;
        if (!_sirenKeys.TryGetValue(sirenKey, out string? key)) _sirenKeys[sirenKey] = key = "siren:" + sirenKey;
        _audio.PrepareVoice(key, VoiceRegistry.SeedOf(VoiceKind.Siren, src.Id));
    }
    private readonly Dictionary<string, string> _sirenKeys = new(StringComparer.Ordinal);

    /// <summary>
    /// A vehicle's siren: its own voice at its own level, at the grille. A siren head is 130 dB at a metre
    /// where the car is 95, so sharing the engine's reference would square the siren or bury the car
    /// (SirenVoiceState). A driven vehicle sounds as switched; traffic's mode is read off what the car
    /// does (<see cref="SirenModeFor"/>).
    /// </summary>
    private void SirenVoice(IAcousticWorld world, AudioSource src, string sirenKey, OpenFPS.Sound.SirenMode mode, Vector3 eyePos)
    {
        OpenFPS.Sound.SirenSpec spec;
        try { spec = OpenFPS.Sound.SirenSpec.ByName(sirenKey); }
        catch { return; }

        int voiceId = SirenVoiceId(src.Id);

        Vector3 pos = SirenMouth(src);
        var (gain, reference) = _audio.Hearing.Place(spec.SourceLevelDb, spec.HornMouthMetres);

        var e = new SpatialEmitter
        {
            EntityId = voiceId,
            Seed = VoiceRegistry.SeedOf(VoiceKind.Siren, src.Id),
            SoundId = "siren",
            IsSynth = true,
            PhysicalKey = "siren:" + sirenKey,
            EngineKey = "",
            Mode = PlaybackMode.LoopOne,
            Type = EmitterType.EntityAttached,
            Velocity = src.State.Velocity,
            PositionSampledAt = _sources.SampledAt,
            // Which way the horn points. Without it the frame falls back to the velocity, which is no
            // answer when the car slows for a junction, exactly when a siren matters most.
            Direction = Vector3.Transform(Vector3.UnitZ, src.State.Rotation),
            Volume = gain,
            EarLevelDb = spec.SourceLevelDb,
            MinDistance = reference,
            ExtentMetres = spec.HornMouthMetres,
            Range = _audio.Hearing.AudibleRange(spec.SourceLevelDb),
            Pitch = 1f,
            // The mode rides in the lever slot, as a train's notch does.
            PowerLever = (float)(int)mode,
            EngineRunning = true,
        };
        HandPath(ref e, VoiceKind.Siren, world, src, pos, spec.SourceLevelDb, eyePos);
        ApplyGround(ref e, _groundWorld);
        _voices.Sound(e);
        _sirenVoiced.Add(src.Id);
    }

    /// <summary>A siren head, where its vehicle's model has it (VehicleProfile.SirenMount): behind the grille,
    /// in the bumper beside the horn.</summary>
    internal static Vector3 SirenMouth(AudioSource src)
        => src.State.Position + Vector3.Transform(
            OpenFPS.Common.ModelKindTable.Vehicle(src.Spec.SoundId)?.SirenMount ?? new Vector3(0f, 0.4f, Grille(src)),
            src.State.Rotation);

    /// <summary>
    /// Where a vehicle's horn is, in the world: where its model has it (VehicleProfile.HornMount); a train's is its
    /// horn source (TrainLayout), wherever on the train the honk was reported from. A train with none, or a
    /// vehicle the client cannot name, is logged once by id.
    /// </summary>
    private Vector3 HornAt(AudioSource src)
    {
        string? sid = src.Spec.SoundId;
        if (OpenFPS.Common.ModelKindTable.BodyOf(sid) == OpenFPS.Common.ModelBody.Rails) return TrainHornAt(src);
        var mount = OpenFPS.Common.ModelKindTable.Vehicle(sid)?.HornMount ?? new Vector3(0f, 0.6f, Grille(src));
        return src.State.Position + Vector3.Transform(mount, src.State.Rotation);
    }

    /// <summary>A train's horn source's point, from the honked source: the train's layout names which of its
    /// sources the horn is, and that source is an entity of its own.</summary>
    private Vector3 TrainHornAt(AudioSource src)
    {
        string sid = src.Spec.SoundId ?? "";
        if (OpenFPS.Common.ModelKindTable.TryRailSource(sid, out string preset, out string train, out int index))
        {
            int horn = -1;
            try { horn = OpenFPS.Sound.TrainSignal.WarningSource(OpenFPS.Sound.TrainLayout.Sources(OpenFPS.Sound.TrainProfile.ByName(preset), ModelLibrary.Presets)); }
            catch (Exception ex) { OpenFPS.Common.ModelKindTable.Fail(sid, ex.Message); }
            if (horn == index) return src.Emission;
            if (horn >= 0)
            {
                string hornId = OpenFPS.Common.ModelKindTable.RailSource(preset, train, horn);
                for (int i = 0, n = _sources.MovingCount; i < n; i++)
                {
                    var other = _sources.MovingAt(i);
                    if (string.Equals(other.Spec.SoundId, hornId, StringComparison.OrdinalIgnoreCase))
                        return other.Emission;
                }
            }
        }
        OpenFPS.Common.ModelKindTable.Fail(sid, "honked, and its train has no horn source on the map: heard from its own point");
        return src.Emission;
    }

    /// <summary>
    /// How far ahead of a vehicle's middle its grille is, metres: its model's (VehicleProfile.GrilleZ). A
    /// vehicle the client cannot name is logged once by id and measured from its collider, or failing that
    /// put 1.9 m ahead.
    /// </summary>
    internal static float Grille(AudioSource src)
    {
        string? sid = src.Spec.SoundId;
        if (OpenFPS.Common.ModelKindTable.Vehicle(sid) is { } vehicle) return vehicle.GrilleZ;
        float length = src.Spec.Size.Z;
        if (length > 0f)
        {
            OpenFPS.Common.ModelKindTable.Fail(sid ?? "", "names no vehicle: its grille is a quarter of a metre in from its collider's nose");
            return MathF.Max(0.5f, length * 0.5f - 0.25f);
        }
        OpenFPS.Common.ModelKindTable.Fail(sid ?? "", "names no vehicle and has no body: its grille is taken as 1.9 m ahead");
        return 1.9f;
    }

    /// <summary>The level a vehicle's model declares at a metre (VehicleProfile.SourceLevelDb). One that declares
    /// none is logged once by id and placed at <see cref="EngineSourceLevelDb"/>.</summary>
    private static float DeclaredLevel(string preset, OpenFPS.Sound.VehicleProfile profile)
    {
        if (profile.SourceLevelDb > 0f) return profile.SourceLevelDb;
        OpenFPS.Common.ModelKindTable.Fail(OpenFPS.Common.ModelKindTable.IdOf(OpenFPS.Common.ModelSynth.Engine, preset),
                                           $"declares no source level: placed at {EngineSourceLevelDb} dB");
        return EngineSourceLevelDb;
    }

    /// <summary>
    /// Where a siren (or horn, or a train's voice) is heard from: its own head, or, when the car's path
    /// arrives round something, that bearing at the head's distance. With two writers placing it by two
    /// rules, the image swung between bearings every frame (3.6 degrees for a car crossing 100 m out) and
    /// a far siren fluttered; the registry's pass is its one writer now (HandPath).
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

    /// <summary>
    /// What a vehicle's siren is doing, as the server sends it (EntityState.Signals): its driver's switch, or for
    /// a patrol car in traffic the mode the server's own SirenController chose from how it is driving (off
    /// standing or with the traffic, wail on a call, yelp braking hard into a junction). Each client decided
    /// that for itself, from the moment it first saw the car, so two players beside one car heard two sirens.
    /// </summary>
    private static OpenFPS.Sound.SirenMode SirenModeOf(AudioSource src)
        => OpenFPS.Sound.VehicleSignalBits.Siren(src.State.Signals);

    /// <summary>
    /// A car too far away for its own engine, voiced by borrowing a near one: it reads the ring of the
    /// nearest live car of its preset at an offset of its own, placed at its own position with its own
    /// velocity, for a buffer read and an HRTF instead of a tenth of a core. Past a couple of hundred
    /// metres nobody can tell one engine of a kind from another. The per-car offset matters: without it
    /// the borrowed voices are one waveform and sum into one loud car instead of traffic.
    /// </summary>
    private void DistantEngine(IAcousticWorld world, AudioSource src, string preset, Vector3 eyePos)
    {
        if (!_distantVoiced.Contains(src.Id)) return;
        if (!_engineSourceByPreset.TryGetValue(preset, out int sourceId)) return;
        if (sourceId == src.Id) return;

        int voiceId = BorrowedVoiceId(src.Id);

        // Rebind when the donor changes: a voice pointed at a dead source is silence that never recovers.
        if (_distantBoundTo.TryGetValue(src.Id, out int bound) && bound != sourceId)
            _voices.Stop(voiceId);
        _distantBoundTo[src.Id] = sourceId;

        var profile = OpenFPS.Common.MachineRegistry.VehicleFor(preset);
        float level = DeclaredLevel(preset, profile);
        float extent = OutletSeparation(preset);
        var (gain, reference) = _audio.Hearing.Place(level, extent);
        Vector3 pos = src.Emission;

        // A stable, arbitrary offset per car, spread across most of the ring buffer.
        float offset = 0.08f + (Math.Abs(src.Id) % 17) * 0.045f;

        var e = new SpatialEmitter
        {
            EntityId = voiceId,
            Seed = VoiceRegistry.SeedOf(VoiceKind.Borrowed, src.Id),
            SoundId = "engine-distant",
            IsSynth = true,
            EchoOfEntity = sourceId,
            EchoDelaySeconds = offset,
            EchoGain = 1f,
            EngineKey = "",
            Mode = PlaybackMode.LoopOne,
            Type = EmitterType.WorldLocked,
            // Its own velocity, so it Dopplers as itself, not as its donor.
            Velocity = src.State.Velocity,
            PositionSampledAt = _sources.SampledAt,
            Volume = gain * src.Spec.Volume,
            Range = _audio.Hearing.AudibleRange(level),
            MinDistance = reference,
            ExtentMetres = extent,
            Pitch = 1f,
        };
        // Placed and pathed as the car it stands for. Re-placed bare, it was never occluded nor told its
        // distance whatever its path said (S3).
        HandPath(ref e, VoiceKind.Borrowed, world, src, pos, level, eyePos);
        _voices.Sound(e);
    }

    private void ProcessAudioEmitter(IAcousticWorld world, AudioSource src, Vector3 eyePos, float engineDt = 0f)
    {
        double now = _now();
        ref readonly var spec = ref src.Spec;

        // A physical model outside the budget (ChooseLiveMachines, this frame) is not worked out either:
        // this bails before the path is read, or the city's 116 unvoiced machines cost a frame's work each.
        if (spec.IsSynth
            && VoiceKey(spec) is { Length: > 0 } sid
            && PhysicalLevel(sid, out _, out _)
            && !_liveMachines.Contains(src.Id))
            return;

        // The worker's last answer; until it has one, FirstAnswer. Never "nothing in the way". The entity's
        // path for the frame, which its horn, siren, outlets and places are heard through too.
        AcousticPathData acousticPath = PathOf(world, src, eyePos).Raw;

        string resolvedSoundId = "";
        bool interior = false;
        // Sitting in it: the paths into its cabin, and where the ear is in its frame.
        OpenFPS.Sound.Core.Engine.CabinPaths.Layout? cabinLayout = null;
        Vector3 cabinEar = default;
        string engineKey = "";
        string physicalKey = "";
        float powerLever = 1f, rotorWake = 0f;
        bool onGround = false;
        // Where the sound comes out: the same point the occlusion probe in Update asks about.
        Vector3 emitterPosition = src.Emission;
        float engineVolume = spec.Volume;
        float engineMinDistance = spec.MinDistance;
        float engineRange = spec.Range;
        float engineExtent = spec.ExtentMetres;
        // A tree or a fire: the places it is heard from across its extent (ExtendedSources).
        Vector3[]? extentLayout = null;
        // Running water along a line: cut into pieces at the ear (ExtendedSources.Cut); the path it runs along, or none.
        OpenFPS.Sound.Core.Nature.LinePath? linePath = null;
        float chorusTrees = 1f;
        // The declared level, for the ear model; 0 for an authored source with only a volume.
        float earLevel = 0f;
        // How much of its volume gives a quieter render its headroom back (SpatialEmitter.HeadroomGain); 0: none.
        float headroomGain = 0f;
        // A recording played through a loudspeaker: its render, level and beam.
        OpenFPS.Sound.Core.Signals.LoudspeakerRender? speaker = null;
        // An authored source with a size (a fountain, a grille): as for a machine, the reference widens to
        // its radius and the gain is paid down, so only the near field changes.
        if (engineExtent > 0f && !spec.IsSynth)
            (engineVolume, engineMinDistance) =
                OpenFPS.Sound.Loudness.Widen(engineVolume, engineMinDistance, engineExtent);
        if (spec.IsSynth)
        {
            resolvedSoundId = VoiceKey(spec);
            if (string.IsNullOrEmpty(resolvedSoundId))
            {
                // A synthesised emitter that names no model: the plain oscillator its fields describe.
                OpenFPS.Common.ModelKindTable.Fail("SYNTH", "an emitter is synthesised with no sound id: played as the plain oscillator");
                resolvedSoundId = "SYNTH";
            }
            // The ranking's own lookup, not a second prefix list: with two, the crossing bell won a voice
            // and then fell through here as a nameless synth, placed every frame and rendered by nothing.
            if (PhysicalLevel(resolvedSoundId, out _, out _))
            {
                // Not a vehicle, so no borrowed voice: outside the budget it is not heard (forty air
                // conditioners are not one heard from further away).
                if (!_liveMachines.Contains(src.Id)) return;
                // The ranking's memoised numbers, so the two cannot disagree.
                if (!PhysicalLevel(resolvedSoundId, out float levelDb, out float extent)) return;
                physicalKey = PhysicalKeyOf(src, resolvedSoundId);
                var physicalSynth = OpenFPS.Common.ModelKindTable.KindOf(resolvedSoundId)!.Synth;
                // Placed by its declared level and size: inside its extent the level is flat, and the
                // gain is paid down so the far field is unchanged (Loudness.Widen; widening without
                // paying once handed every quiet vehicle eight decibels).
                var (gain, reference) = _audio.Hearing.Place(levelDb, extent);
                // A voice rendered with more headroom than the shared one (a fire's crackles) gets the
                // difference back, so it is placed by its level and not its peaks.
                headroomGain = OpenFPS.Audio.Fmod.PhysicalVoiceState.HeadroomGain(PhysicalHeadroom(resolvedSoundId));
                engineVolume = gain * spec.Volume * headroomGain;
                // A tree's share of itself, or a wood's gain (its synth renders its trees: WoodChorus).
                engineVolume *= ChorusShare(src.Id, out chorusTrees);
                engineMinDistance = reference;
                engineExtent = extent;
                engineRange = MathF.Max(engineRange, _audio.Hearing.AudibleRange(levelDb));
                earLevel = levelDb;
                extentLayout = _audio.Options.WideSources.Layout(physicalKey, ModelLibrary.Presets);
                linePath = LinePathOf(src, physicalKey);
                if (physicalSynth == OpenFPS.Common.ModelSynth.Aircraft)
                {
                    // Its power lever as the server that flies it sends it (MachinePower: from its flight path
                    // in the air and its ground run on its wheels, a piston's closed throttle in the flare).
                    powerLever = src.State.Lever;
                    rotorWake = RotorWake(src.State.Velocity);
                    onGround = OnTheWheels(src, world, eyePos, out _);
                }
                else if (physicalSynth == OpenFPS.Common.ModelSynth.Rail)
                {
                    // The notch its driver has it in, as the server sends it (MachinePower.TrainNotch), carried
                    // in the lever slot; the wake slot carries the speed, for rolling noise.
                    powerLever = src.State.Lever;
                    rotorWake = src.State.Velocity.Length();
                }
            }
            else if (OpenFPS.Common.ModelKindTable.KnownVehiclePreset(resolvedSoundId) is { } vehiclePreset)
            {
                // A vehicle: its own live engine, or a borrowed voice (DistantEngine).
                if (!_liveEngines.Contains(src.Id))
                {
                    DistantEngine(world, src, vehiclePreset, eyePos);
                    return;
                }
                engineKey = vehiclePreset;
                var profile = OpenFPS.Common.MachineRegistry.VehicleFor(engineKey);
                // Its own measured level: a stock car is 14 dB over a road car, a diesel pickup 30 under.
                float level = DeclaredLevel(engineKey, profile);
                // Its acoustic size is the distance between the ends it radiates from (a car 3.5 m, a bus
                // 10, a motorcycle 1): inside it the level is flat, since a metre nearer the intake is a
                // metre further from the exhaust. Widened by Loudness.Place, which pays the gain back;
                // a bare MathF.Max(reference, 3f) handed a quiet vehicle up to 8 dB.
                float extent = OutletSeparation(engineKey);
                var (gain, reference) = _audio.Hearing.Place(level, extent);
                engineVolume = gain * spec.Volume;
                engineMinDistance = reference;
                engineExtent = extent;
                engineRange = MathF.Max(engineRange, _audio.Hearing.AudibleRange(level));
                earLevel = level;

                // Close enough to tell the ends apart: this voice moves to the tailpipe and the front gets
                // its own (FrontVoice); further off it sits between them, biased to the exhaust
                // (VehicleProfile.ExhaustEmitterBias). Two voices are each placed as points: widening them
                // too counted the length twice (a hatchback's tailpipe 4 dB short at 2 m, 9 dB at 1 m).
                // Beyond the extent both placements agree (Widen holds gain times reference).
                if (_frontVoiced.Contains(src.Id))
                {
                    emitterPosition = src.State.Position
                                    + Vector3.Transform(ExhaustSlot(profile), src.State.Rotation);
                    (gain, reference) = _audio.Hearing.Place(level);
                    engineVolume = gain * spec.Volume;
                    engineMinDistance = reference;
                    engineExtent = 0f;
                }

                // Sitting in it, the machine arrives through the floor and firewall and the voice renders
                // what gets through the body (EngineVoiceState.Interior). Its level is already pressure at
                // the ear: unwidened, at the gain its reference distance would have had.
                if (src.Id == Listener.RidingId)
                {
                    interior = true;
                    var (g0, r0) = _audio.Hearing.Place(level);
                    engineVolume = MathF.Min(1f, g0 * r0) * spec.Volume;
                    engineMinDistance = 1f;
                    engineExtent = 0f;
                    // With a cabin (CabinPaths) this voice is the bulkhead; every other path gets its own.
                    cabinLayout = _audio.Options.CabinLayout(profile, ModelLibrary.Presets);
                    cabinEar = Vector3.Transform(eyePos - src.State.Position, Quaternion.Inverse(src.State.Rotation));
                }
            }
            else if (OpenFPS.Common.ModelKindTable.KindOf(resolvedSoundId) == null)
                OpenFPS.Common.ModelKindTable.Fail(resolvedSoundId, "is synthesised but names no kind of model");
        }
        else
        {
            resolvedSoundId = _sounds.ResolvePath(spec.SoundId!);
            if (string.IsNullOrEmpty(resolvedSoundId)) return;

            // A recording played through a loudspeaker: its render through the speaker's chain, placed
            // by the level that comes out of it (a paging horn's speech is 106 dB at a metre, not "full scale to 12 m"),
            // beaming as its mouth does. Silent until the render is in.
            if (!string.IsNullOrEmpty(spec.Loudspeaker) && !spec.IsGranular)
            {
                if (!_loudspeakers.TryGet(spec.Loudspeaker, resolvedSoundId,
                                          loop: spec.Mode != PlaybackMode.Single, out var render))
                    return;
                speaker = render;
                resolvedSoundId = render.Key;
                var (gain, reference) = _audio.Hearing.Place(render.LevelDb, engineExtent);
                engineVolume = gain * spec.Volume;
                engineMinDistance = reference;
                engineRange = MathF.Max(engineRange, _audio.Hearing.AudibleRange(render.LevelDb));
                earLevel = render.LevelDb;
            }

            // A granular emitter needs its sample decoded into memory.
            if (spec.IsGranular && !_preloadedSounds.Contains(resolvedSoundId))
            {
                _audio.Preload(resolvedSoundId);
                _preloadedSounds.Add(resolvedSoundId);
            }
        }

        // Its own voice: a moving thing's engine or aeroplane, a wood, a standing model, a recording.
        var ownKind = OwnVoiceKind(spec, src.Id, physicalKey.Length > 0);
        var emitter = new SpatialEmitter
        {
            EntityId = _voices.IdFor(ownKind, src.Id),
            Seed = VoiceRegistry.SeedOf(ownKind, src.Id),
            SoundId = resolvedSoundId,
            // Spin-up and spin-down sounds, which VoiceManager plays.
            StartSoundId = spec.StartSoundId ?? "",
            StopSoundId = spec.StopSoundId ?? "",
            Mode = spec.Mode,
            Position = emitterPosition,
            ApparentPosition = engineKey.Length > 0 || physicalKey.Length > 0
                             ? emitterPosition : acousticPath.ApparentPosition,
            // Inside, the voice has already rendered what gets through the body: the path would count
            // the panels twice.
            EffectiveDistance = interior ? OpenFPS.Sound.Core.Engine.CabinPaths.PlacedMetres : acousticPath.EffectiveDistance,
            Occlusion = interior ? 0f : acousticPath.Occlusion,
            EqLow = interior ? 1f : acousticPath.EqLow, EqMid = interior ? 1f : acousticPath.EqMid, EqHigh = interior ? 1f : acousticPath.EqHigh,
            AirLowDb = interior ? 0f : acousticPath.AirLowDb, AirMidDb = interior ? 0f : acousticPath.AirMidDb, AirHighDb = interior ? 0f : acousticPath.AirHighDb,
            ApertureFactor = interior ? 1f : acousticPath.ApertureFactor,
            TransmissionBleed = interior ? 0f : acousticPath.TransmissionBleed,
            Velocity = src.State.Velocity,
            PositionSampledAt = _sources.SampledAt,
            // Its local aim turned by the entity; zero means straight ahead.
            Direction = Vector3.Transform(
                spec.Direction.LengthSquared() > 0f
                    ? Vector3.Normalize(spec.Direction)
                    : Vector3.UnitZ,
                src.State.Rotation),
            Volume = engineVolume,
            EarLevelDb = earLevel,
            HeadroomGain = headroomGain,
            Range = Math.Max(1.0f, engineRange),
            Pitch = 1.0f,
            Type = EmitterType.EntityAttached,
            IsReflection = false,
            // Inside, its room is yours (the cabin), whatever room the car's middle is in.
            TargetRegionId = interior ? _listenerRegion : acousticPath.RegionId,
            // A loudspeaker's beam is its mouth's, band by band (Radiator), not a flat cone.
            ConeInside = speaker != null ? 360f : spec.ConeInsideAngle,
            ConeOutside = speaker != null ? 360f : spec.ConeOutsideAngle,
            ConeOutsideVolume = speaker != null ? 1f : spec.ConeOutsideVolume,
            Radiator = speaker?.Radiator,
            RadiatorBandEnergy = speaker?.BandEnergy,
            RadiatedGain = speaker?.RadiatedGain ?? 0f,
            MinDistance = engineMinDistance,
            ExtentMetres = engineExtent,
            EngineKey = engineKey,
            Interior = interior,
            // Inside, it rides with your head, ahead and below (firewall and floor, or the bulkhead with
            // a cabin), turned with the car so the engine stays in front through a corner.
            FollowsListener = interior,
            ListenerOffset = !interior ? Vector3.Zero
                           : cabinLayout != null ? Vector3.Transform(_audio.Options.CabinOffset(cabinLayout, 0, cabinEar), src.State.Rotation)
                           : Vector3.Transform(OpenFPS.Sound.Core.Engine.CabinPaths.OnePlace, src.State.Rotation),
            CabinEarX = cabinEar.X,
            PhysicalKey = physicalKey,
            Trees = chorusTrees,
            PowerLever = powerLever,
            RotorWake = rotorWake,
            OnGround = onGround,
            EngineSpeed = src.State.Velocity.Length(),
            // Whether a synthesised source is sounding. Nearly everything decides that from what the
            // client sees; a crossing's bell rings for a train the listener may be a kilometre from, so
            // it comes down the wire (true for everything else).
            EngineRunning = spec.SynthRunning,
            Quench = spec.Quench,
            ServingStop = spec.ServingStop,
            WindowsOpen = _cabins.WindowsOpen(src.Id, spec.WindowsOpen, _now()),
            // From the server, which knows the corner's banking. Differentiated here from the velocity
            // against flat-ground grip, the speedway's banked corners read 1.43 to 1.59 against a
            // full-slide threshold of 1.45: every car rendered a broadband skid in every corner.
            TyreSlip = src.State.TyreDemand,
            Wheels = src.State.Wheels,
            WheelStrikes = engineKey.Length > 0 ? RailStrikes(src, engineKey, _sources.SampledAt) : null,
            // The water on the roads, for the vehicles whose wheels the server does not send.
            RoadWaterMm = _sources.RoadWaterMm,

            // Synthesis mapping
            IsGranular = spec.IsGranular,
            GranularPosition = spec.GranularPosition,
            GranularGrainSizeMs = spec.GranularGrainSizeMs,
            GranularDensity = spec.GranularDensity,
            GranularPitch = spec.GranularPitch,
            GranularPositionJitter = spec.GranularPositionJitter,
            GranularPitchJitter = spec.GranularPitchJitter,

            IsSynth = spec.IsSynth,
            SynthWave = (SynthWaveType)spec.SynthWave,
            SynthFrequency = spec.SynthFrequency,
            SynthLfoRate = spec.SynthLfoRate,
            SynthLfoDepth = spec.SynthLfoDepth,
            SynthFilterCutoff = spec.SynthFilterCutoff,
            SynthFilterResonance = spec.SynthFilterResonance,
            SynthPulseWidth = spec.SynthPulseWidth
        };
        // Its path in the emitter the budget holds, the only writer: heard from its point (a machine, a wood),
        // from the edge its path comes round when blocked (an engine, an aeroplane), or from where the path
        // arrives (a recording).
        HandPath(ref emitter, ownKind, world, src, emitterPosition, earLevel, eyePos, inside: interior);

        // A repeating one-shot (RepeatIntervalSeconds: a PA, a foghorn, a station bell) is built as
        // normal and handed over only when its interval comes round.
        float repeat = spec.RepeatIntervalSeconds;
        if (repeat > 0f)
        {
            double due = _repeatDue.GetValueOrDefault(src.Id, double.NegativeInfinity);
            if (double.IsNegativeInfinity(due))
            {
                // Staggered by id, so two announcers on one map do not talk over each other for ever.
                _repeatDue[src.Id] = now + (Math.Abs(src.Id) % 7) * 0.9;
                return;
            }
            // Between its firings, or still saying the last one: placed from where it is now, never
            // started. The budget had re-applied the submission it started with, and its placement
            // stamp aged the length of the line ("placed at a position 2400 ms old").
            if (now < due || _audio.IsPlaying(emitter.EntityId))
            {
                if (now >= due) _repeatDue[src.Id] = now + repeat;
                // A play the budget holds (under way, or waiting on its file) is placed afresh; nothing
                // else is touched (AudioEngineFacade.Refresh never starts one).
                _voices.Refresh(emitter);
                return;
            }
            _repeatDue[src.Id] = now + repeat;
        }
        if (speaker != null) _speakerOf[src.Id] = speaker; else _speakerOf.Remove(src.Id);

        // The road under a machine hands its sound back a moment later; see GroundReflection. Engines and
        // physical models only: a sustained recording (a PA's speech) gets none, as speech flanged with
        // one (docs/CLIENT_NOTES.md, "Speech has no ground reflection").
        Cutting? cutting = linePath != null ? CutOf(src, linePath, ref emitter, eyePos) : null;
        long groundAt = System.Diagnostics.Stopwatch.GetTimestamp();
        if (engineKey.Length > 0 || physicalKey.Length > 0) ApplyGround(ref emitter, world);
        _partMs[1] += Ms(groundAt);
        Spreading? spreading = extentLayout != null ? SpreadOf(src, extentLayout, ref emitter, eyePos, engineDt, now) : null;
        _placed[src.Id] = (emitter.Volume, emitter.MinDistance, emitter.Range);
        _voices.Submit(emitter);
        // ...and its other places, after its middle, so the synth they read already exists.
        if (spreading != null) PlaceOuter(src.Id, spreading, emitter, world, now);
        if (cutting != null) PlacePieces(src.Id, cutting, emitter, world, eyePos, now);

        // The front outlet, after the machine's own voice so a just-built engine exists for the tap.
        if (engineKey.Length > 0 && _frontVoiced.Contains(src.Id))
            FrontVoice(world, src, OpenFPS.Common.MachineRegistry.VehicleFor(engineKey), eyePos, earLevel,
                       engineVolume, engineMinDistance, Math.Max(1.0f, engineRange), _sources.SampledAt);

        if (interior && cabinLayout != null && engineKey.Length > 0)
            CabinVoices(world, src, cabinLayout, cabinEar, emitter, eyePos);

        // The siren is placed in UpdateSirens, not here.

        // The walls answering this engine, read back out of its own ring at each mirrored path's delay
        // (EngineReflections).
        if (engineKey.Length > 0)
        {
            long echoAt = System.Diagnostics.Stopwatch.GetTimestamp();
            _engineEchoes.Update(src.Id, emitter, acousticPath, eyePos, _audio.Atmosphere.SpeedOfSound, engineDt, _audio,
                                 traced: _audio.HasTracedEchoes(src.Id));
            double echoMs = Ms(echoAt);
            _partMs[0] += echoMs;
            _echoPassMs += echoMs;
        }

        // No floor slapback or "cone reflection" here, on purpose: a copy started at a ray hit is another
        // read of the file (a looping announcement, again), and a ray from inside the emitter's own box
        // hits it at distance zero, heard as the sound repeating softer in place. The image-source pass
        // already mirrors the source through the floor and the wall.
    }

    /// <summary>The voice of somebody talking on voice chat, from their mouth.</summary>
    private int TalkerVoiceId(int playerId) => _voices.IdFor(VoiceKind.Talker, playerId);
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
    private void UpdateTalkers(IAcousticWorld world, Vector3 eyePos, double now)
    {
        _talkersGone.Clear();
        _talkerSearchedThisFrame = false;
        foreach (var stream in _audio.TalkerStreams.All)
        {
            stream.Pump(now);
            int id = stream.SenderId;
            int voiceId = TalkerVoiceId(id);
            double quiet = now - stream.LastArrival;
            if (quiet > TalkerForgetSeconds) { _talkersGone.Add(id); continue; }
            bool talking = quiet < TalkerHoldSeconds && id != OwnEntityId;
            if (!talking || !_sources.TryGet(id, out var talker))
            {
                if (_talkersVoiced.Remove(id)) _voices.Stop(voiceId);
                StopTalkerRoom(id);
                continue;
            }
            var facing = Vector3.Transform(Vector3.UnitZ, talker.State.Rotation);
            facing.Y = 0f;
            facing = facing.LengthSquared() > 1e-6f ? Vector3.Normalize(facing) : Vector3.UnitZ;
            // Seated in a vehicle: a seated mouth, heard outside through the glass or open windows.
            bool seated = OpenFPS.Audio.Acoustics.CabinWalls.TryFind(world, talker.State.Position,
                                                                                out var cabin, out var cabinVehicle);
            float mouthHeight = seated ? SeatedMouthHeight : OpenFPS.Common.Speech.MouthHeight;
            Vector3 mouth = talker.State.Position + new Vector3(0f, mouthHeight, 0f) + facing * 0.1f;

            float levelDb = OpenFPS.Common.Speech.NormalDb - stream.Ring.SpeechRmsDbfs;
            var (gain, reference) = _audio.Hearing.Place(levelDb);

            // Asked under its own probe, as a one-off's source is: under the player's id it shared one answer with
            // anything else of theirs asked about at their feet (S13). Their own body is left out of the rays.
            int probe = _voices.ProbeFor(id);
            _acousticWorker.EnqueueRequest(new AcousticRequest
            {
                EntityId = probe, ListenerPos = eyePos, SourcePos = mouth, SourceRadius = 0.15f, LeaveOut = id,
            });
            if (!_acousticWorker.TryGetDirectFor(probe, mouth, talker.State.Velocity.Length(), eyePos, _earSpeed, out var path))
                path = _acoustics.CalculateAcousticPath(world, id, eyePos, mouth);

            var e = new SpatialEmitter
            {
                EntityId = voiceId,
                Seed = VoiceRegistry.SeedOf(VoiceKind.Talker, id),
                SoundId = "voice chat",
                IsSynth = true,
                PhysicalKey = OpenFPS.Audio.Fmod.Talkers.Key(id),
                EngineKey = "",
                Mode = PlaybackMode.LoopOne,
                Type = EmitterType.EntityAttached,
                Position = mouth,
                ApparentPosition = path.ApparentPosition,
                Velocity = talker.State.Velocity,
                PositionSampledAt = _sources.SampledAt,
                Direction = facing,
                Volume = gain,
                EarLevelDb = levelDb,
                MinDistance = reference,
                Range = MathF.Max(reference, _audio.Hearing.AudibleRange(levelDb)),
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
                if (cabin.Id == Listener.RidingId) e.InsideListenersVehicle = true;
                else
                {
                    var (low, mid, high) = OpenFPS.Audio.Acoustics.CabinWalls.LossDb(
                        cabinVehicle, _cabins.WindowsOpen(cabin, now));
                    e.EqLow *= OpenFPS.Audio.Acoustics.CabinWalls.Gain(low);
                    e.EqMid *= OpenFPS.Audio.Acoustics.CabinWalls.Gain(mid);
                    e.EqHigh *= OpenFPS.Audio.Acoustics.CabinWalls.Gain(high);
                }
            }
            _voices.Declare(voiceId, id, VoiceKind.Talker, mouth - talker.State.Position, levelDb);
            _voices.Sound(e);
            _talkersVoiced.Add(id);

            // The surfaces round them answering; not from inside a cabin, where only its glass hears them.
            if (seated) StopTalkerRoom(id);
            else AnswerTalker(world, id, mouth, eyePos, gain, reference, e.Range, path.RegionId, now);
        }
        foreach (int id in _talkersGone)
        {
            if (_talkersVoiced.Remove(id)) _voices.Stop(TalkerVoiceId(id));
            StopTalkerRoom(id);
            _audio.TalkerStreams.Remove(id);
        }
        if (_talkersVoiced.Count > 0 && now >= _talkersNextLog)
        {
            _talkersNextLog = now + 5;
            foreach (int id in _talkersVoiced)
                if (_audio.TalkerStreams.TryGet(id, out var t))
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

    /// <summary>How many talkers the surfaces answer at once. A search is a few milliseconds on the game
    /// thread; past this many voices at once, the room is the tail alone.</summary>
    private const int MaxTalkerRooms = 8;

    private sealed class TalkerRoom
    {
        /// <summary>Which of the rooms answering talkers this is: what its copies' voices belong to
        /// (VoiceKind.TalkerCopy). Each carries its own path: traced from its image, it would go through its
        /// own wall.</summary>
        public int Block;
        public double NextSearch;
        public int Answering;
        public int Region = AcousticConstants.GlobalRegionId;
        public readonly bool[] On = new bool[OwnVoiceCopies];
        public readonly List<OpenFPS.Acoustics.EarlyReflections.Arrival> Arrivals = new();
    }
    private readonly Dictionary<int, TalkerRoom> _talkerRooms = new();
    private bool _talkerSearchedThisFrame;

    /// <summary>
    /// The surfaces round somebody talking, answering at your ear as your own room answers you
    /// (PlaceVoiceCopies): searched from their mouth a few times a second, at most one search a frame
    /// among the talkers. Without it a voice was direct sound and late tail only (Cody, 2026-10-04: "I can
    /// hear him but it's dry, his reflections don't follow him").
    /// </summary>
    private void AnswerTalker(IAcousticWorld world, int id, Vector3 mouth, Vector3 ear, float gain, float reference,
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
        KeepCopies(VoiceKind.TalkerCopy, room.Block, room.On);
        if (now < room.NextSearch || _talkerSearchedThisFrame) return;
        _talkerSearchedThisFrame = true;
        room.NextSearch = now + 0.1;
        // Their voice is played without its flight time, so a copy is its EXTRA path behind it.
        float flight = Vector3.Distance(mouth, ear) / _audio.Atmosphere.SpeedOfSound;
        room.Answering = PlaceVoiceCopies(world, mouth, ear, room.Arrivals, gain, reference, flight,
                                          room.Block, room.On,
                                          OpenFPS.Audio.Fmod.Talkers.CopyKey(id), "voice chat", region, range,
                                          VoiceKind.TalkerCopy, id);
    }

    private void StopTalkerRoom(int id)
    {
        if (!_talkerRooms.Remove(id, out var room)) return;
        StopVoiceCopies(VoiceKind.TalkerCopy, room.Block, room.On);
    }

    /// <summary>Your own steps so far, and everybody else's: each step is the next of its kind (VoiceRegistry.IdFor).</summary>
    private int _footstepPoolIndex = 0;
    /// <summary>
    /// The room the listener was last found in, for the sounds the body makes. A footstep is built on
    /// the game thread between updates, and left at TargetRegionId's default (-1, outdoors) every footfall
    /// reverberated on the outdoor bus, the same tail wherever you were.
    /// </summary>
    private int _listenerRegion = AcousticConstants.GlobalRegionId;

    /// <summary>
    /// Everybody else's steps are counted apart from yours: a submission replaces whatever waits under its
    /// id, and with one count three hundred walkers came round it faster than your own steps could start.
    /// </summary>
    private int _othersFootstepIndex;

    /// <summary>How far away another body's step can be heard at all, metres: the audible range of a step's
    /// level (S24; it was 15 m, hundreds short of a 68 dB step's).</summary>
    private float FootstepRange(float levelDb) => _audio.Hearing.AudibleRange(levelDb);

    /// <summary>Somebody else's step at <paramref name="pos"/> on <paramref name="mat"/>, a slope's
    /// <paramref name="slopeDb"/> and <paramref name="slopePitch"/> on it (the host's), by <paramref name="bodyId"/>'s
    /// foot: a sound left where it fell, and only one close enough to hear is made at all. Their own body is not a
    /// wall between their foot and you (CarryThePath).</summary>
    public void OtherFootstep(Vector3 pos, string mat, float slopeDb, float slopePitch, int bodyId = -1)
    {
        if (Vector3.Distance(pos, Listener.Feet) > FootstepRange(OpenFPS.Sound.Loudness.FootstepDb + slopeDb)) return;
        SubmitFootstep(pos + new Vector3(0, 0.1f, 0), mat, follows: false, offset: Vector3.Zero, boostDb: 0f, slopeDb, slopePitch,
                       bodyId: bodyId);
    }

    /// <summary>
    /// How much louder your own footstep is to you than to a bystander where your ears are: you also hear
    /// it through your skeleton, the heel strike up the leg and spine into the skull. Your own body only.
    /// </summary>
    private const float OwnFootstepBoneConductionDb = 8f;

    /// <summary>OPENFPS_AUDIO_DEBUG=1 logs what your own feet did and when: a landing is heavier than a
    /// step, so this answers "periodic bangs" outright.</summary>
    private bool _footTrace => _audio.Options.Trace;

    // ── The listening-level calibration's voice (ListeningCalibration) ────────────────────────
    //
    // A person one step in front at a gain the calibration chooses: placed directly, not by the loudness
    // law, and with no ear stage, to play at exactly a real voice's level and tone there. Two ids in turn,
    // so a new saying never waits for the old one's voice to be released.
    private int _referenceVoice;

    public void PlayReferenceVoice(string soundId, float gainDb)
    {
        StopReferenceVoice();
        _referenceVoice = (_referenceVoice + 1) & 1;
        var forward = Vector3.Transform(Vector3.UnitZ, Quaternion.CreateFromYawPitchRoll(Listener.Yaw, 0f, 0f));
        Vector3 ear = Listener.Feet + new Vector3(0f, Listener.EyeHeight, 0f);
        Vector3 mouth = ear + forward * 1f - new Vector3(0f, Listener.EyeHeight - OpenFPS.Common.Speech.MouthHeight, 0f);
        int voiceId = _voices.IdFor(VoiceKind.Reference, VoiceRegistry.NoEntity, _referenceVoice);
        _voices.DeclareEvent(voiceId, VoiceRegistry.NoEntity, VoiceKind.Reference, mouth);
        _voices.Submit(new SpatialEmitter
        {
            EntityId = voiceId,
            Seed = VoiceRegistry.SeedOf(VoiceKind.Reference, VoiceRegistry.NoEntity, _referenceVoice),
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
        _voices.StopNow(_voices.IdFor(VoiceKind.Reference, VoiceRegistry.NoEntity, 0));
        _voices.StopNow(_voices.IdFor(VoiceKind.Reference, VoiceRegistry.NoEntity, 1));
    }

    /// <summary>
    /// Your own step at <paramref name="pos"/>, as the physics has the foot, which rides with you at a fixed offset
    /// from your head. Placed world-locked at the physics position (the listener being at the smoothed feet), a
    /// step's bearing swung from straight down to thirty degrees behind while it played, and the steps trailed and
    /// slid round you.
    /// </summary>
    public void OwnFootstep(Vector3 pos, string mat, float slopeDb, float slopePitch)
    {
        var listener = Listener;
        Vector3 offset = (pos - listener.Body) + new Vector3(0, 0.1f - listener.EyeHeight, 0);   // the foot, from the eye
        SubmitFootstep(listener.Feet + new Vector3(0, listener.EyeHeight, 0) + offset, mat, follows: true, offset: offset,
                       boostDb: OwnFootstepBoneConductionDb, slopeDb, slopePitch);
    }

    /// <summary>How far a footstep take's pitch and level wander from one play to the next.</summary>
    private const float FootstepPitchJitter = 0.03f, FootstepLevelJitterDb = 1f;


    private void SubmitFootstep(Vector3 nudgePos, string mat, bool follows, Vector3 offset, float boostDb,
                                float slopeDb, float slopePitch, int bodyId = -1)
    {
        // Your own feet ride with you (follows); anybody else's stay where they fell.
        bool own = follows;
        var stepKind = own ? VoiceKind.OwnStep : VoiceKind.Footstep;
        int step = own ? _footstepPoolIndex++ : _othersFootstepIndex++;
        int id = _voices.IdFor(stepKind, VoiceRegistry.NoEntity, step);

        string resolvedSoundId = _sounds.ResolvePath(_sounds.GetImpactSoundId(mat, 0f));
        if (string.IsNullOrEmpty(resolvedSoundId)) return;

        // Placed by Loudness.Place like every other sound. At full scale (a 112 dB source a metre away;
        // a step is about 55) a dry step peaked at -1.6 dBFS with no makeup (--room-walk), hit the limiter
        // by 9 dB with it and pumped the mix: "the footsteps are loud", a pop on each. On stairs a toe going
        // up is lighter and a heel going down heavier (the host's slope, slopeDb and slopePitch).
        var (stepGain, stepReference) = _audio.Hearing.Place(OpenFPS.Sound.Loudness.FootstepDb + boostDb + slopeDb);

        var footstep = new SpatialEmitter
        {
            EntityId = id,
            Seed = VoiceRegistry.SeedOf(stepKind, VoiceRegistry.NoEntity, step),
            SoundId = resolvedSoundId,
            Position = nudgePos,
            FollowsListener = follows,
            ListenerOffset = offset,
            Type = EmitterType.WorldLocked,
            // No two steps alike: a hair of pitch and a touch of level, at random.
            Volume = stepGain * MathF.Pow(10f, (float)(_stepRandom.NextDouble() * 2.0 - 1.0) * FootstepLevelJitterDb / 20f),
            Pitch = slopePitch * (1f + (float)(_stepRandom.NextDouble() * 2.0 - 1.0) * FootstepPitchJitter),
            Range = FootstepRange(OpenFPS.Sound.Loudness.FootstepDb + boostDb + slopeDb),
            // Your own feet are pinned (they are how you know you are moving); everybody else's compete
            // by loudness like any other sound.
            Essential = own,
            IsEvent = true,
            MinDistance = stepReference,
            EarLevelDb = OpenFPS.Sound.Loudness.FootstepDb + boostDb + slopeDb,
            TargetRegionId = _listenerRegion,
        };
        if (!own) CarryThePath(ref footstep, nudgePos, bodyId);
        _voices.DeclareEvent(id, own ? OwnEntityId : bodyId >= 0 ? bodyId : VoiceRegistry.NoEntity,
                             stepKind, nudgePos, footstep.EarLevelDb);
        _voices.Submit(footstep);

        // The walls answering YOUR footfalls. Other people's steps have none: their pool is yours.
        if (own) SubmitStepReflections(nudgePos, resolvedSoundId, stepGain, stepReference, OpenFPS.Sound.Loudness.FootstepDb + boostDb + slopeDb);
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
        Vector3 ear = Listener.Feet + new Vector3(0, Listener.EyeHeight, 0);
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
        // OPENFPS_AUDIO_DEBUG=1: the path somebody else's step starts on, on the audio clock.
        if (_footTrace)
            Serilog.Log.Information("[STEP] other {Id} at ({X:F2},{Y:F2},{Z:F2}) occ {Occ:F2} eq {L:F4}/{M:F4}/{H:F4} bleed {B:F2} aperture {A:F2} apparent ({AX:F1},{AY:F1},{AZ:F1}) region {R} t {T:F3}",
                step.EntityId, at.X, at.Y, at.Z, path.Occlusion, path.EqLow, path.EqMid, path.EqHigh, path.TransmissionBleed,
                path.ApertureFactor, path.ApparentPosition.X, path.ApparentPosition.Y, path.ApparentPosition.Z, path.RegionId, _audio.Clock.Now);
    }

    private readonly List<OpenFPS.Acoustics.EarlyReflections.Arrival> _stepArrivals = new();
    /// <summary>
    /// The surfaces answering your own footfalls so far, each a voice of its own kind (VoiceKind.StepEcho), so a
    /// copy never takes its step's. Each carries its whole path from its image, and nothing asks about it
    /// there: asked about from the image, the worker traced it through its own wall (-40 to -100 dB) or round
    /// by the stairwell (Cody, 2026-10-04, Marlow Tower: "it's like they're left on the first floor").
    /// </summary>
    private int _stepEchoIndex;

    // ── Your own voice, as your room answers it ─────────────────────────────────────────────────

    /// <summary>True while the microphone is open (V). The session sets it.</summary>
    public bool OwnVoiceLive { get; set; }

    /// <summary>Your own voice's send into your room, and the surfaces answering it.</summary>
    private int OwnVoiceId => _voices.IdFor(VoiceKind.OwnVoice, VoiceRegistry.NoEntity);
    /// <summary>How many surfaces answer your voice at once: the room's first answers, as for a step.</summary>
    private const int OwnVoiceCopies = 8;
    /// <summary>
    /// What the microphone path already adds before a copy can start (capture buffer, 20 ms frame, ring
    /// margin): about 60 ms. A copy's delay is its extra path less this, so a surface over about ten metres
    /// of path away answers on time and a nearer one as soon as it can (Cody, 2026-10-03: "I want to hear
    /// myself in the room I'm actually in").
    /// </summary>
    private const float MicrophoneLatency = 0.06f;
    private readonly List<OpenFPS.Acoustics.EarlyReflections.Arrival> _voiceArrivals = new();
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
        var facing = Vector3.Transform(Vector3.UnitZ, Quaternion.CreateFromYawPitchRoll(Listener.Yaw, 0f, 0f));
        Vector3 mouth = ear + facing * 0.08f - new Vector3(0f, 0.1f, 0f);
        // Full scale sits as far above normal talking as the microphone's talking level sits below it.
        float levelDb = OpenFPS.Common.Speech.NormalDb - _audio.OwnVoice.SpeechRmsDbfs;
        var (gain, reference) = _audio.Hearing.Place(levelDb);

        // The room's reverberation; its own sound is silent (FmodAudioProvider._ownVoiceRoomGroup). At its
        // reference distance, where the send to the room is a source of that level's.
        int roomId = OwnVoiceId;
        var room = new SpatialEmitter
        {
            EntityId = roomId,
            Seed = VoiceRegistry.SeedOf(VoiceKind.OwnVoice, VoiceRegistry.NoEntity),
            SoundId = "own voice",
            IsSynth = true,
            PhysicalKey = OpenFPS.Audio.Fmod.FmodAudioProvider.OwnVoiceRoomKey,
            EngineKey = "",
            Mode = PlaybackMode.LoopOne,
            Type = EmitterType.WorldLocked,
            Position = ear + facing * reference,
            ApparentPosition = ear + facing * reference,
            Volume = gain,
            MinDistance = reference,
            Range = _audio.Hearing.AudibleRange(levelDb),
            Pitch = 1f,
            EngineRunning = true,
            CarriesPath = true,
            Occlusion = 0f, ApertureFactor = 1f, TransmissionBleed = 0f,
            EqLow = 1f, EqMid = 1f, EqHigh = 1f,
            EchoDelaySeconds = 0f,
            TargetRegionId = _listenerRegion,
        };
        _voices.Declare(roomId, OwnEntityId, VoiceKind.OwnVoice, Vector3.Zero, levelDb);
        _voices.Sound(room);
        _voiceRoomOn = true;

        // The surfaces, a few times a second: the room only changes as you move.
        KeepCopies(VoiceKind.OwnVoiceCopy, VoiceRegistry.NoEntity, _voiceCopyOn);
        if (now < _voiceNextSearch) return;
        _voiceNextSearch = now + 0.1;
        float c = _audio.Atmosphere.SpeedOfSound;
        // Its whole path, less what the microphone has already cost.
        int slot = PlaceVoiceCopies(world, mouth, ear, _voiceArrivals, gain, reference, MicrophoneLatency,
                                    VoiceRegistry.NoEntity, _voiceCopyOn,
                                    OpenFPS.Audio.Fmod.FmodAudioProvider.OwnVoiceCopyKey, "own voice",
                                    _listenerRegion, _audio.Hearing.AudibleRange(levelDb), VoiceKind.OwnVoiceCopy, OwnEntityId);
        if (now >= _voiceNextLog)
        {
            _voiceNextLog = now + 5;
            var ring = _audio.OwnVoice;
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
    /// <param name="copiesOf">What the copies' voices belong to (VoiceRegistry.IdFor): a talker's room, or
    /// nothing's for your own.</param>
    /// <param name="on">Which slots have a voice, kept between calls.</param>
    /// <param name="key">The physical key the copies read the voice's ring by.</param>
    /// <param name="soundId">The copies' sound id, for the logs.</param>
    /// <param name="regionId">The room the copies reverberate in.</param>
    /// <param name="range">How far the copies are heard.</param>
    private int PlaceVoiceCopies(IAcousticWorld world, Vector3 mouth, Vector3 ear,
                                 List<OpenFPS.Acoustics.EarlyReflections.Arrival> arrivals,
                                 float gain, float reference, float alreadyLateSeconds,
                                 int copiesOf, bool[] on, string key, string soundId, int regionId, float range,
                                 VoiceKind kind, int ownerId)
    {
        arrivals.Clear();
        _acoustics.FindReflections(world, mouth, ear, arrivals, _audio.Atmosphere.SpeedOfSound,
                                                 maxOrder: 2, keep: OwnVoiceCopies * 2,
                                                 maxExtraPathMetres: RoomEchoes.WindowSeconds * _audio.Atmosphere.SpeedOfSound);
        arrivals.Sort(static (a, b) => (b.GainMid, a.SurfaceId, a.Order, a.PathLength).CompareTo((a.GainMid, b.SurfaceId, b.Order, b.PathLength)));
        float direct = Vector3.Distance(mouth, ear);
        float c = _audio.Atmosphere.SpeedOfSound;
        int slot = 0, secondOrder = 0;
        foreach (var a in arrivals)
        {
            if (slot >= OwnVoiceCopies) break;
            if (a.ExtraDelaySeconds > RoomEchoes.WindowSeconds) continue;
            if (a.Order >= 2 && ++secondOrder > RoomEchoes.MaxSecondOrderCopies) continue;
            float copyGain = OpenFPS.Acoustics.EarlyReflections.PlacedCopyGain(a.GainMid, a.PathLength, direct, reference)
                           * RoomEchoes.MirrorShare(a.Scattering, a.Order)
                           * _audio.Mix.CopiesTrim;
            if (copyGain < OpenFPS.Acoustics.ImageSource.MinGain) continue;
            var loss = OpenFPS.Acoustics.ImageSource.SpecularBandLossDb(a.Scattering, a.Order);
            float lowDb = 20f * MathF.Log10(MathF.Max(1e-4f, a.GainLow) / MathF.Max(1e-4f, a.GainMid));
            float highDb = 20f * MathF.Log10(MathF.Max(1e-4f, a.GainHigh) / MathF.Max(1e-4f, a.GainMid));
            int id = _voices.IdFor(kind, copiesOf, slot);
            var copy = new SpatialEmitter
            {
                EntityId = id,
                Seed = VoiceRegistry.SeedOf(kind, copiesOf, slot),
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
            _voices.Declare(id, ownerId, kind, a.ImagePosition);
            _voices.Sound(copy);
            on[slot] = true;
            slot++;
        }
        StopVoiceCopies(kind, copiesOf, on, slot);
        return slot;
    }

    /// <summary>The copies still answering between searches stay entries, as they were declared.</summary>
    private void KeepCopies(VoiceKind kind, int copiesOf, bool[] on)
    {
        for (int k = 0; k < on.Length; k++) if (on[k]) _voices.Keep(_voices.IdFor(kind, copiesOf, k));
    }

    /// <summary>Stops the copies from slot <paramref name="from"/> on.</summary>
    private void StopVoiceCopies(VoiceKind kind, int copiesOf, bool[] on, int from = 0)
    {
        for (int k = from; k < on.Length; k++)
            if (on[k]) { _voices.Stop(_voices.IdFor(kind, copiesOf, k)); on[k] = false; }
    }

    private void StopOwnVoice()
    {
        if (_voiceRoomOn) { _voices.Stop(OwnVoiceId); _voiceRoomOn = false; }
        StopVoiceCopies(VoiceKind.OwnVoiceCopy, VoiceRegistry.NoEntity, _voiceCopyOn);
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
        Vector3 ear = Listener.Feet + new Vector3(0, Listener.EyeHeight, 0);
        if (_groundWorld is not { } world) return;
        SubmitRoomStepEchoes(stepPos, ear, soundId, stepGain, stepReference, stepLevelDb, world);
    }

    /// <summary>The room's first answers to your own footfall, placed as WorldAudioPlayer.QueueRoomEchoes
    /// places a clap's: image sources to second order, loudest first, inside the window before the tail,
    /// each from its own wall with that wall's colour. The floor under the foot is skipped.</summary>
    private void SubmitRoomStepEchoes(Vector3 stepPos, Vector3 ear, string soundId, float stepGain, float stepReference, float stepLevelDb, IAcousticWorld world)
    {
        _acoustics.FindReflections(world, stepPos, ear, _stepArrivals, _audio.Atmosphere.SpeedOfSound,
                                             maxOrder: 2, keep: RoomEchoes.MaxEchoes * 2,
                                             maxExtraPathMetres: RoomEchoes.WindowSeconds * _audio.Atmosphere.SpeedOfSound);
        _stepArrivals.Sort(static (a, b) => (b.GainMid, a.SurfaceId, a.Order, a.PathLength).CompareTo((a.GainMid, b.SurfaceId, b.Order, b.PathLength)));
        float direct = Vector3.Distance(stepPos, ear);
        int added = 0, secondOrder = 0;
        foreach (var a in _stepArrivals)
        {
            if (a.ExtraDelaySeconds > RoomEchoes.WindowSeconds) continue;
            // The floor the foot is on: the step is made of it already.
            if (a.Order == 1 && a.HitPoint.Y < MathF.Min(stepPos.Y, ear.Y) - 0.2f) continue;
            if (a.Order >= 2 && ++secondOrder > RoomEchoes.MaxSecondOrderCopies) continue;
            float placed = OpenFPS.Acoustics.EarlyReflections.PlacedCopyGain(a.GainMid, a.PathLength, direct, stepReference);
            float lowDb = 20f * MathF.Log10(MathF.Max(1e-4f, a.GainLow) / MathF.Max(1e-4f, a.GainMid));
            float highDb = 20f * MathF.Log10(MathF.Max(1e-4f, a.GainHigh) / MathF.Max(1e-4f, a.GainMid));
            // A first-order wall's scattered share, √s of the pressure, as its wash: the step smeared by the
            // wall's roughness, coloured by what it absorbs but not by the mirror's loss (a one-off sound's
            // wash, WorldAudioPlayer.QueueWash). Nothing off a smooth wall.
            float s = Math.Clamp(a.Scattering, 0f, 1f);
            float wash = a.Order == 1 && OpenFPS.Audio.Fmod.EchoWashState.Applies(s) ? placed * MathF.Sqrt(s) : 0f;
            if (wash >= OpenFPS.Acoustics.ImageSource.MinGain)
                SubmitStepCopy(a, soundId, stepGain, stepReference, stepLevelDb,
                               wash * _audio.Mix.CopiesTrim, lowDb, highDb, s);
            // The mirror share (RoomEchoes.MirrorShare), a clean copy.
            float gain = placed * RoomEchoes.MirrorShare(a.Scattering, a.Order);
            if (gain < OpenFPS.Acoustics.ImageSource.MinGain) continue;
            gain *= _audio.Mix.CopiesTrim;   // /copies
            var loss = OpenFPS.Acoustics.ImageSource.SpecularBandLossDb(a.Scattering, a.Order);
            SubmitStepCopy(a, soundId, stepGain, stepReference, stepLevelDb, gain, loss.LowDb + lowDb, loss.HighDb + highDb, 0f);
            if (++added >= RoomEchoes.MaxEchoes) break;
        }
    }

    /// <summary>One copy of your step at a wall's image: the mirror (<paramref name="smear"/> 0) or the
    /// wall's wash, smeared by its roughness in the mixer (EchoWashState).</summary>
    private void SubmitStepCopy(in OpenFPS.Acoustics.EarlyReflections.Arrival a, string soundId, float stepGain, float stepReference,
                                float stepLevelDb, float gain, float eqLowDb, float eqHighDb, float smear)
    {
        int echo = _stepEchoIndex++;
        int echoId = _voices.IdFor(VoiceKind.StepEcho, VoiceRegistry.NoEntity, echo);
        _voices.DeclareEvent(echoId, OwnEntityId, VoiceKind.StepEcho, a.ImagePosition, stepLevelDb);
        _voices.Submit(new SpatialEmitter
        {
            EntityId = echoId,
            Seed = VoiceRegistry.SeedOf(VoiceKind.StepEcho, VoiceRegistry.NoEntity, echo),
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
            Range = _audio.Hearing.AudibleRange(stepLevelDb),
            // No DelayMs: the facade delays every submission by its distance, and the image is the
            // whole path away.
            IsReflection = true,
            IsEvent = true,
            // The path is this: clear both legs (checked when it was found), coloured by the walls.
            CarriesPath = true,
            Occlusion = 0f, ApertureFactor = 1f, TransmissionBleed = 0f,
            EqLow = MathF.Pow(10f, eqLowDb / 20f), EqMid = 1f, EqHigh = MathF.Pow(10f, eqHighDb / 20f),
            TargetRegionId = _listenerRegion,
            // All of it smeared: the mirror is its own voice.
            EchoScattering = smear,
            EchoMirrorShare = 0f,
        });
    }

    // ── The ground ─────────────────────────────────────────────────────────────────────────────

    private Vector3 _groundEar;
    private struct GroundCache { public Vector3 Src, Ear; public double At; public bool Found; public float Height; public string Material; }
    private readonly Dictionary<int, GroundCache> _groundCache = new();
    private IAcousticWorld? _groundWorld;
    private readonly Vector3[] _groundRay = { -Vector3.UnitY };
    private readonly float[] _groundDist = new float[1], _groundAbs = new float[1];
    private readonly string[] _groundMat = new string[1];

    /// <summary>
    /// The ground reflection for one live voice (GroundReflection has the why): the source mirrored in the
    /// surface under its bounce point. One ray down finds the ground under the source, fixing the bounce
    /// point in the ratio of the two heights; a second, from the direct path above that point, finds what
    /// is really there to reflect off (a bonnet, a kerb, a shelter roof) and its material.
    /// </summary>
    private void ApplyGround(ref SpatialEmitter e, IAcousticWorld? world) => ApplyGround(ref e, world, true, 0f);

    /// <summary>
    /// The same for a recorded sound, under a fresh voice id each time, so nothing is cached. A recording
    /// made at the ground already has its bounce in it (adding it again lifts it 6 dB), so one within
    /// <see cref="RecordedGroundMinHeight"/> of the surface gets none.
    /// </summary>
    public void ApplyRecordedGround(ref SpatialEmitter e, IAcousticWorld world)
        => ApplyGround(ref e, world, false, RecordedGroundMinHeight);

    /// <summary>Below this a recorded sound's own bounce is already in the recording, metres.</summary>
    internal const float RecordedGroundMinHeight = 0.15f;

    /// <summary>
    /// Two rays straight down (ApplyGround's). The first finds the ground under the source, which fixes where the bounce
    /// lands, between the two in the ratio of their heights (<paramref name="at"/>); the second, from the direct path above
    /// that point, finds what is there to bounce off: a surface above the ground and below the line (a bonnet, a kerb)
    /// is what the sound meets. Buffers the caller's, one ray long.
    /// </summary>
    internal static bool LookForGround(IAcousticWorld world, Vector3 src, Vector3 ear, Vector3[] down,
                                       float[] dist, float[] abs, string[] mat, out float height, out string material, out Vector3 at)
    {
        const float Reach = 30f;
        height = 0f; material = "Generic"; at = src;
        AcousticRays.FirstSurfaces(world, src + new Vector3(0f, 0.05f, 0f), down, Reach, dist, abs, mat, staticOnly: true);
        if (!(dist[0] < Reach)) return false;
        float g0 = src.Y + 0.05f - dist[0];
        float hs0 = MathF.Max(0.02f, src.Y - g0), hr0 = MathF.Max(0.02f, ear.Y - g0);
        at = Vector3.Lerp(src, ear, hs0 / (hs0 + hr0));
        AcousticRays.FirstSurfaces(world, at + new Vector3(0f, 0.02f, 0f), down, Reach, dist, abs, mat, staticOnly: true);
        if (!(dist[0] < Reach)) return false;
        height = at.Y + 0.02f - dist[0];
        material = mat[0] ?? "Generic";
        return true;
    }

    private void ApplyGround(ref SpatialEmitter e, IAcousticWorld? world, bool keep, float minHeight)
    {
        e.GroundDelaySeconds = 0f; e.GroundLowGain = 0f; e.GroundHighGain = 0f;
        e.GroundLowDb = 0f; e.GroundMidDb = 0f; e.GroundHighDb = 0f;
        e.OverWater = false;
        if (world == null || Listener.Riding) return;          // from inside a vehicle there is no road to hear
        Vector3 src = e.Position, ear = _groundEar;

        // The rays are the cost: cast again only once the source or the ear has moved a metre, or after
        // a quarter of a second.
        double now = _audio.Clock.Now;
        if (!_groundCache.TryGetValue(e.EntityId, out var c)
            || Vector3.DistanceSquared(c.Src, src) > 1f || Vector3.DistanceSquared(c.Ear, ear) > 1f || now - c.At > 0.25)
        {
            c = new GroundCache { Src = src, Ear = ear, At = now };
            c.Found = LookForGround(world, src, ear, _groundRay, _groundDist, _groundAbs, _groundMat,
                                    out c.Height, out c.Material, out _);
            if (keep) _groundCache[e.EntityId] = c;
        }
        if (!c.Found) return;
        var m = OpenFPS.Materials.AcousticRegistry.GetProperties(c.Material ?? "Generic");
        // Sound bent down by the wind or the air's layers comes off the ground as if both ends stood higher (Bending).
        bool water = OpenFPS.Materials.AcousticRegistry.IsLiquid(c.Material);
        var bent = OpenFPS.Sound.Core.Bending.Along(_audio.Weather, src, ear, c.Height, water, _audio.Weather.Now, _audio.Atmosphere.SpeedOfSound);
        if (OpenFPS.Acoustics.GroundAnswer.Reflect(c.Height, m, src, ear, minHeight, _audio.Atmosphere.SpeedOfSound,
                                                bent.SourceRaise, bent.EarRaise) is not { } bounce)
            return;
        e.OverWater = water;
        e.GroundHeight = bounce.Height;
        e.GroundDelaySeconds = bounce.DelaySeconds;
        e.GroundLowGain = bounce.LowGain;
        e.GroundHighGain = bounce.HighGain;
        e.GroundLowDb = Taken(bounce.ExcessLow);
        e.GroundMidDb = Taken(bounce.ExcessMid);
        e.GroundHighDb = Taken(bounce.ExcessHigh);

        static float Taken(float gain) => gain >= 1f ? 0f : -20f * MathF.Log10(MathF.Max(1e-4f, gain));
    }

    /// <summary>Probes the space round the listener's head for the mixer, head-relative so the picture
    /// turns with the player.</summary>
    private void UpdateBoundaryProbes(IAcousticWorld world, Vector3 visualEyePos)
    {
        var head = Listener.Rotation;
        var directions = BoundaryModel.ProbeDirections;

        // Nothing is near while you ride: a lamp post the bus brushes is half a metre from the glass, not
        // your ear, and the cabin is the interior model's ("the proximity to me from the outside objects
        // isn't relevant").
        if (Listener.Riding)
        {
            for (int i = 0; i < directions.Length; i++)
                _boundaryProbes[i] = new BoundaryProbe(directions[i], BoundaryModel.MaxDistance, "");
            _audio.UpdateBoundaries(_boundaryProbes);
            return;
        }

        _boundaryProber.Probe(world, visualEyePos, head, _audio.Atmosphere.SpeedOfSound, _boundaryProbes);
        _audio.UpdateBoundaries(_boundaryProbes);
    }

    /// <summary>
    /// The six probes round the head and, for each that meets something it can hear (not the floor), how much
    /// of a wall that something is (<see cref="BoundaryModel.Share"/>): a door's jamb a foot deep met between
    /// the ears was a whole wall's copy of everything at each ear (Resonance, Sean in a shop's doorway,
    /// 2026-10-14). Buffers owned and reused: this runs every audio frame, 6 rays and at most 40 more, all in
    /// one cast so the traffic list is walked twice.
    /// </summary>
    internal sealed class BoundaryProber
    {
        private static readonly int Probes = BoundaryModel.ProbeDirections.Length;
        private readonly Vector3[] _rays = new Vector3[Probes];
        private readonly float[] _distances = new float[Probes];
        private readonly float[] _absorptions = new float[Probes];
        private readonly string[] _materials = new string[Probes];
        private readonly Vector3[] _normals = new Vector3[Probes];
        private readonly int[] _looked = new int[Probes];
        private readonly Vector3[] _looks = new Vector3[Probes * BoundaryModel.LooksPerProbe];
        private readonly float[] _reach = new float[Probes * BoundaryModel.LooksPerProbe];
        private readonly float[] _lookHits = new float[Probes * BoundaryModel.LooksPerProbe];
        private readonly float[] _lookAbsorptions = new float[Probes * BoundaryModel.LooksPerProbe];
        private readonly string[] _lookMaterials = new string[Probes * BoundaryModel.LooksPerProbe];

        public void Probe(IAcousticWorld world, Vector3 ear, Quaternion head, float speedOfSound,
                          Span<BoundaryProbe> into)
        {
            var directions = BoundaryModel.ProbeDirections;
            for (int i = 0; i < Probes; i++) _rays[i] = Vector3.Transform(directions[i], head);
            AcousticRays.FirstSurfaces(world, ear, _rays, BoundaryModel.MaxDistance, _distances, _absorptions, _materials, normals: _normals);

            int looks = 0;
            float reach = 0f;
            for (int i = 0; i < Probes; i++)
            {
                _looked[i] = -1;
                if (!(_distances[i] < BoundaryModel.MaxDistance) || BoundaryModel.IsFloor(directions[i])) continue;
                _looked[i] = looks;
                BoundaryModel.Looks(ear, _rays[i], _distances[i], _normals[i], speedOfSound,
                                    _looks.AsSpan(looks, BoundaryModel.LooksPerProbe), _reach.AsSpan(looks, BoundaryModel.LooksPerProbe));
                for (int k = looks; k < looks + BoundaryModel.LooksPerProbe; k++) reach = MathF.Max(reach, _reach[k]);
                looks += BoundaryModel.LooksPerProbe;
            }
            // Twice the furthest look and a half metre: a look that meets nothing stops well behind its plane.
            if (looks > 0)
                AcousticRays.FirstSurfaces(world, ear, _looks, 2f * reach + 0.5f, _lookHits, _lookAbsorptions, _lookMaterials, count: looks);

            for (int i = 0; i < Probes; i++)
            {
                float share = _looked[i] < 0 ? 1f
                    : BoundaryModel.Share(ear, _rays[i], _distances[i], _normals[i],
                                          _looks.AsSpan(_looked[i], BoundaryModel.LooksPerProbe), _lookHits.AsSpan(_looked[i], BoundaryModel.LooksPerProbe));
                into[i] = new BoundaryProbe(directions[i], _distances[i], _materials[i], share);
            }
        }
    }

    /// <summary>The server reports a new material underfoot (StatsUpdate): it becomes the listener
    /// region's floor material.</summary>
    public void NotifyMaterialChange(string material)
    {
        if (_lastAcousticMap == null) return;
        var snap = _lastWorld ?? _acousticWorker.GetLastWorld();
        if (snap == null) return;
        int listenerRegionId = _acoustics.GetRegionAt(snap, Listener.Feet + new Vector3(0, Listener.EyeHeight, 0));
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
        var listener = Listener;
        Vector3 offset = (pos - listener.Body) + new Vector3(0, 0.1f - listener.EyeHeight, 0);
        SubmitLanding(listener.Feet + new Vector3(0, listener.EyeHeight, 0) + offset, mat, follows: true, offset: offset);
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
            var (landGain, landReference) = _audio.Hearing.Place(OpenFPS.Sound.Loudness.FootstepDb);
            var landKind = follows ? VoiceKind.OwnStep : VoiceKind.Footstep;
            var landEmitter = new SpatialEmitter
            {
                EntityId = _voices.IdFor(landKind, VoiceRegistry.NoEntity, VoiceRegistry.Landing),
                Seed = VoiceRegistry.SeedOf(landKind, VoiceRegistry.NoEntity, VoiceRegistry.Landing),
                SoundId = resolved,
                Position = nudgePos,
                FollowsListener = follows,
                ListenerOffset = offset,
                Type = EmitterType.WorldLocked,
                Volume = landGain,
                EarLevelDb = OpenFPS.Sound.Loudness.FootstepDb,
                Range = _audio.Hearing.AudibleRange(OpenFPS.Sound.Loudness.FootstepDb),
                Essential = true,
                IsEvent = true,
                MinDistance = landReference,
                TargetRegionId = _listenerRegion,
            };
            // Somebody else's lands where it fell, behind whatever is between, as their steps do.
            if (!follows) CarryThePath(ref landEmitter, nudgePos);
            _voices.DeclareEvent(landEmitter.EntityId, follows ? OwnEntityId : VoiceRegistry.NoEntity,
                                 landKind, nudgePos, OpenFPS.Sound.Loudness.FootstepDb);
            _voices.Submit(landEmitter);
        }
    }
}

/// <summary>Sources before the host's first frame: none.</summary>
file sealed class NoSources : IAudioSources
{
    public bool TryGet(int id, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out AudioSource? source) { source = null; return false; }
    public bool Contains(int id) => false;
    public IReadOnlyList<int> Audible => Array.Empty<int>();
    public int MovingCount => 0;
    public AudioSource MovingAt(int index) => throw new ArgumentOutOfRangeException(nameof(index));
    public double SampledAt => 0;
    public WoodChorus? Woods => null;
    public Precipitation Precipitation => Precipitation.None;
    public float RainRateMmPerHour => 0f;
    public Precipitation Falling => Precipitation.None;
    public float RoadWaterMm => 0f;
    public OutdoorAir Outdoors => Thunder.StandardAir;
}

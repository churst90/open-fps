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
    private int _adaptiveEchoes = EngineReflections.MaxEchoesPerEngine;
    private double _lastBudgetChange;

    /// <summary>However short the mixer runs, this many cars are fully SYNTHESIZED.</summary>
    private const int MinEngineVoices = 2;

    /// <summary>Which source each distant voice is currently bound to, so a change can rebind it.</summary>
    private readonly Dictionary<int, int> _distantBoundTo = new();

    /// <summary>Distant car voices live in their own id band.</summary>
    internal const int DistantVoiceBase = -700000;

    /// <summary>...and a machine's front outlet in another one.</summary>
    internal const int IntakeVoiceBase = -800000;

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
    private const int FrontVoiceBudget = 6;
    private int _adaptiveFront = FrontVoiceBudget;

    /// <summary>Which machines currently have their front outlet on a voice of its own.</summary>
    private readonly HashSet<int> _frontVoiced = new();
    private readonly List<int> _frontRetiring = new();

    /// <summary>How many engines may be BUILT in one audio update. See ChooseLiveEngines.</summary>
    private const int NewEnginesPerUpdate = 2;

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
    public void NoteSceneLoading() => _budgetHeldUntil = _clock.Elapsed.TotalSeconds + BudgetHoldSeconds;
    private readonly UpdateThrottle _throttle = new(UpdateHz);
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

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
    {
        _audio = audio;
        _sounds = sounds;
        _state = state;
        _spatial = new SpatialService();
        _acoustics = new SpatialAcoustics(_spatial); // Share the same SpatialService instance
        // The short sounds the world reports. Shares this system's acoustics so a rendered latch
        // takes exactly the path a recorded one would.
        WorldAudio = new WorldAudioPlayer(_audio, _acoustics);
        _acousticWorker = new AsyncAcousticWorker(_acoustics);
        _acousticWorker.Start();
    }

    /// <summary>
    /// Silences everything an entity was making sound with, after the server said it is gone.
    /// A voice is keyed by entity id and keeps playing on its own once started, so a looping emitter on a
    /// despawned object would otherwise sit in the world for the rest of the session. The reflection
    /// voices derived from that id are stopped too — they carry synthetic ids, not the entity's own.
    /// </summary>
    public void ForgetEntity(int entityId)
    {
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
        if (!_throttle.ShouldRun(_clock.Elapsed.TotalSeconds)) return;

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
        double nowSec = _clock.Elapsed.TotalSeconds;
        if (_lastUpdateAt > 0)
        {
            double gapMs = (nowSec - _lastUpdateAt) * 1000.0;
            if (gapMs > _worstGapMs) _worstGapMs = gapMs;
        }
        _lastUpdateAt = nowSec;
        long startTicks = System.Diagnostics.Stopwatch.GetTimestamp();
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
        Vector3 visualEyePos = _state.VisualPosition + new Vector3(0, 1.7f, 0);

        // 1. Resolve high-precision listener region (OBB check)
        int listenerRegionId = _acoustics.GetRegionAt(world, visualEyePos);

        // 2. Synchronize the listener's smoothed physical state.
        //
        // The wind the listener actually feels is synthesized HERE, not received. The server broadcasts
        // the sustained wind and a single gustiness scalar once a second; sampling a gust at 1 Hz would
        // alias it into a stutter, so the swell is generated locally at audio rate from that scalar and
        // the local clock (see WindModel). Shelter attenuates it: indoors the air is still.
        Vector3 feltWind = WindModel.Felt(
            world.WindVelocity, world.WindGustiness, _clock.Elapsed.TotalSeconds, _state.ShelterFactor);
        _state.FeltWind = feltWind;

        // A fraction of the moving air rides on the listener velocity, so wind produces a subtle Doppler
        // on distant sounds — and a gust now audibly swells and drops it.
        Vector3 listenerVelocity = _state.Velocity + feltWind * 0.1f;
        _audio.UpdateListener(visualEyePos, _state.Rotation, listenerVelocity, listenerRegionId);
        _audio.UpdateShelter(_state.ShelterFactor);
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
                                          _acousticWorker.ListenerMeanFreePath);
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
                foreach(var snap in world.Entities.Values)
                {
                    if (snap.Definition.Region.RoomSize.X > 0)
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
                    SourceRadius = sourceRadius,
                    IsImportant = isImportant
                });
            }
            
            if (_acousticWorker.TryGetResult(id, out var paths) && ResultIsForThisVoice(world, id, sourcePos, paths))
            {
                Array.Clear(_slotLive);
                foreach (var path in paths)
                {
                    if (!path.IsReflection)
                    {
                        _audio.SetAcousticPath(id, path);
                        continue;
                    }

                    // A reflection path is honoured wherever it came from. Both paths produce them now:
                    // under Steam Audio simulation they are first-order image sources off the scene's own
                    // surfaces (EarlyReflections), and on the fallback path they come from the hand-rolled
                    // tracer. Retiring them under the simulator — on the theory that a parametric reverb
                    // tail covered the same energy — is what left a room answering from everywhere at once
                    // and a doorway inaudible from outside.
                    if (!world.Entities.TryGetValue(id, out var originalSnap)) continue;

                    // A live engine has no file to play a delayed copy of: its sound id names a
                    // synthesis, not a sample, and asking the provider to play it as one fails every
                    // frame ("not playing yet — Missing", once a frame per car). The walls answering an
                    // engine are EngineReflections' job, read out of the synthesis's own ring buffer.
                    // "engine:" is how the SERVER spells it on a snapshot (VehicleSystem) and the
                    // form tested before anything is resolved — the same test OtherBodies uses. The
                    // first version of this line matched the resolved spelling, "ENGINE/", which no
                    // snapshot ever holds, so it matched nothing and the cars kept their copies.
                    string sourceSound = originalSnap.Definition.SoundEmitter.SoundId ?? "";
                    if (sourceSound.StartsWith("engine:", StringComparison.OrdinalIgnoreCase)
                        || sourceSound.StartsWith("ENGINE/", StringComparison.OrdinalIgnoreCase)) continue;
                    if ((uint)path.ReflectionIndex >= (uint)EarlyReflections.MaxArrivals) continue;
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

        // 5.5. Decide which vehicles get a live engine: the nearest EngineVoiceBudget of them.
        //
        // Done here, once, rather than inside the per-entity pass, because it is a decision ABOUT the
        // set: the eighth-nearest car cannot know it is eighth. A car that loses its slot has its
        // engine and its echoes stopped, which is a real cut rather than a fade — but it only ever
        // happens to whichever car is furthest away and being drowned by three nearer ones.
        ChooseLiveEngines(world, visualEyePos);
        _engineEchoes.EchoesPerEngine = _adaptiveEchoes;
        _engineEchoes.SyncGeometry(world);
        float engineDt = (float)Math.Max(1e-3, _clock.Elapsed.TotalSeconds - _lastEngineTime);
        _lastEngineTime = _clock.Elapsed.TotalSeconds;

        // 6. Process persistent audio emitters attached to world entities (NPCs, Beacons, Machines)
        foreach (var entityId in world.AudioEntityIds)
        {
            if (entityId == OwnEntityId) continue;
            if (world.Entities.TryGetValue(entityId, out var snap))
            {
                ProcessAudioEmitter(world, snap, visualEyePos, engineDt);
            }
        }

        // Every source has now been offered to the reflection system; it can work out what the
        // next frame will demand of a reflection to be worth a voice.
        _engineEchoes.EndFrame();

        // 7. Execute the audio engine tick (mixing, DSP updates)
        _audio.Update();
        }
        finally
        {
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - startTicks) * 1000.0
                      / System.Diagnostics.Stopwatch.Frequency;
            if (ms > _worstUpdateMs) _worstUpdateMs = ms;
        }
    }

    private double _lastUpdateAt, _worstGapMs, _worstUpdateMs;

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
        double now = _clock.Elapsed.TotalSeconds;

        float load = _audio.MixerLoad;
        if (load > MixerLoadCeiling) { if (_overCeilingSince < 0) _overCeilingSince = now; }
        else _overCeilingSince = -1;

        if (load > 0f && now >= _budgetHeldUntil && now - _lastBudgetChange >= BudgetSettleSeconds)
        {
            if (load > MixerLoadCeiling && now - _overCeilingSince >= OverCeilingSeconds)
            {
                // A machine's second outlet goes first: it is the only voice whose loss costs
                // nothing but geometry — the machine stays exactly as loud, because the front tap
                // slews back into the voice that is still playing.
                if (_adaptiveFront > 0) _adaptiveFront--;
                else if (_adaptiveEchoes > 0) _adaptiveEchoes--;
                else if (_adaptiveDistant > MinDistantVoices) _adaptiveDistant--;
                else if (_adaptiveBudget > MinEngineVoices) _adaptiveBudget--;
                else goto settled;
                _lastBudgetChange = now;
                Log.Information("Audio: mixer at {Load:P0}; {Cars} engine(s), {Distant} borrowed, {Echoes} reflection(s) each.",
                                load, _adaptiveBudget, _adaptiveDistant, _adaptiveEchoes);
            }
            else if (load < MixerLoadFloor)
            {
                if (_adaptiveBudget < EngineVoiceBudget) _adaptiveBudget++;
                else if (_adaptiveDistant < MaxDistantVoices) _adaptiveDistant++;
                else if (_adaptiveFront < FrontVoiceBudget) _adaptiveFront++;
                else if (_adaptiveEchoes < EngineReflections.MaxEchoesPerEngine) _adaptiveEchoes++;
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
            if (_worstGapMs > 100)
                Log.Warning("Audio placement stalled: every source held its position for up to {Gap:F0} ms "
                          + "in the last 5 s (the pass itself took at most {Work:F0} ms). A car in front of you "
                          + "stops for that long.", _worstGapMs, _worstUpdateMs);
            else
                Log.Information("Audio placement: worst gap {Gap:F0} ms between position refreshes, "
                              + "worst pass {Work:F0} ms.", _worstGapMs, _worstUpdateMs);
            _worstGapMs = 0; _worstUpdateMs = 0;

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
    private void FrontVoice(EntitySnapshot snap, OpenFPS.Common.Networking.EntityDefinition def,
                            OpenFPS.Common.VehicleProfile profile, in AcousticPathData path,
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
            ApertureFactor = path.ApertureFactor,
            TransmissionBleed = path.TransmissionBleed,
            EffectiveDistance = path.EffectiveDistance,
            TargetRegionId = path.RegionId,
            EnableReverb = true,
        };
        if (_audio.IsPlaying(voiceId)) _audio.UpdateSpatialAttributes(e);
        else _audio.PlayPhysicalSoundDirect(e);
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
    private void DistantEngine(EntitySnapshot snap, OpenFPS.Common.Networking.EntityDefinition def, string preset, Vector3 eyePos, double sampledAt)
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
        double now = _clock.Elapsed.TotalSeconds;
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
            Vector3 fallbackSource = OpenFPS.Common.AudioEmission.PointFor(snap);
            float directDist = Vector3.Distance(eyePos, fallbackSource);
            acousticPath = new AcousticPathData(0f, fallbackSource, directDist);
        }

        string resolvedSoundId = "";
        string engineKey = "";
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
            if (resolvedSoundId.StartsWith("engine:", StringComparison.OrdinalIgnoreCase)
                && OpenFPS.Common.MachineRegistry.Knows(resolvedSoundId[7..]))
            {
                // A vehicle: the engine runs live in the mixer and follows the entity's speed. The
                // voice sits toward the tailpipe, since that is where most of the sound comes from,
                // and it is placed at the level a loud exhaust really has.
                // Its own engine if it has one; a borrowed voice if the mixer is short. See DistantEngine.
                if (!_liveEngines.Contains(snap.Id))
                {
                    DistantEngine(snap, def, resolvedSoundId[7..], eyePos, world.PositionsSampledAt);
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
            ApparentPosition = engineKey.Length > 0 ? emitterPosition : acousticPath.ApparentPosition,
            EffectiveDistance = acousticPath.EffectiveDistance,
            Occlusion = acousticPath.Occlusion,
            ApertureFactor = acousticPath.ApertureFactor,
            TransmissionBleed = acousticPath.TransmissionBleed,
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
            TargetRegionId = acousticPath.RegionId,
            EnableReverb = true,
            ConeInside = def.SoundEmitter.ConeInsideAngle,
            ConeOutside = def.SoundEmitter.ConeOutsideAngle,
            ConeOutsideVolume = def.SoundEmitter.ConeOutsideVolume,
            MinDistance = engineMinDistance,
            ExtentMetres = engineExtent,
            EngineKey = engineKey,
            EngineSpeed = snap.Velocity.Length(),
            EngineRunning = true,
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

        _audio.Submit(emitter);

        // The other end of the machine, when it is close enough to be a second thing. Placed after
        // the machine's own voice, so an engine that has only just been built already exists for the
        // tap to read.
        if (engineKey.Length > 0 && _frontVoiced.Contains(snap.Id))
            FrontVoice(snap, def, OpenFPS.Common.MachineRegistry.VehicleFor(engineKey), acousticPath,
                       engineVolume, engineMinDistance, Math.Max(1.0f, engineRange), world.PositionsSampledAt);

        // 6.1. The walls answering this engine. A live engine has no file to replay, so its
        // reflections are read back out of the synthesis's own ring buffer at the delay the mirrored
        // path implies — see EngineReflections.
        if (engineKey.Length > 0)
            _engineEchoes.Update(snap.Id, emitter, acousticPath, eyePos, AudioPhysics.SpeedOfSound, engineDt, _audio);

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

    /// <summary>
    /// Plays a short tone to indicate voice transmission has started.
    /// </summary>
    public void PlayVoiceIndicator() => _audio.PlayUiBeep(880f, 80f);

    private int _footstepPoolIndex = 0;
    // Larger pool so rapid footsteps rarely reuse an ID while the previous step is still playing — reusing
    // an active voice hard-cuts it (click). 12 IDs gives plenty of headroom at running cadence.
    private const int FOOTSTEP_POOL_SIZE = 12;
    private const int FOOTSTEP_BASE_ID = -100;

    /// <summary>Somebody else's step: a sound at a place in the world, left there as they walk on.</summary>
    public void OnPlayerFootstep(Vector3 pos, string mat, string var)
        => SubmitFootstep(pos + new Vector3(0, 0.1f, 0), mat, follows: false, offset: Vector3.Zero);

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
    public void OnOwnFootstep(Vector3 pos, string mat, string var)
    {
        Vector3 offset = (pos - _state.Position) + new Vector3(0, 0.1f - 1.7f, 0);   // the foot, from the eye
        SubmitFootstep(_state.VisualPosition + new Vector3(0, 1.7f, 0) + offset, mat, follows: true, offset: offset);
    }

    private void SubmitFootstep(Vector3 nudgePos, string mat, bool follows, Vector3 offset)
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
        var (stepGain, stepReference) = OpenFPS.Common.Loudness.Place(OpenFPS.Common.Loudness.FootstepDb);

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
            MinDistance = stepReference
        };
        _audio.Submit(footstep);

        // NOTE: footsteps deliberately do NOT spawn reflection/echo emitters. Bouncing each step off
        // the surrounding walls scattered the sound "all over the place" in enclosed rooms instead of
        // staying localized at the player's feet. The room's reverb bus still gives footsteps their
        // indoor character; per-step geometric reflections are reserved for world emitters.
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
    private void UpdateBoundaryProbes(WorldSnapshot world, Vector3 visualEyePos)
    {
        var head = _state.Rotation;
        var directions = BoundaryModel.ProbeDirections;

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
        int listenerRegionId = _acoustics.GetRegionAt(snap, _state.VisualPosition + new Vector3(0, 1.7f, 0));
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
        Vector3 offset = (pos - _state.Position) + new Vector3(0, 0.1f - 1.7f, 0);
        SubmitLanding(_state.VisualPosition + new Vector3(0, 1.7f, 0) + offset, mat, follows: true, offset: offset);
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
                MinDistance = landReference
            };
            _audio.Submit(landEmitter);
        }
    }
}

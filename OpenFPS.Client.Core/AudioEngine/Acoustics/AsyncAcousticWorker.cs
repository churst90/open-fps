using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Common.Components;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Client.Core.AudioEngine.SteamAudio;

namespace OpenFPS.Client.AudioEngine.Acoustics;

public struct AcousticRequest
{
    public int EntityId;
    public Vector3 ListenerPos;
    /// <summary>Where the sound comes OUT — see <see cref="AudioEmission.PointFor"/> — not the
    /// entity's origin. Everything downstream is an answer about this point.</summary>
    public Vector3 SourcePos;
    /// <summary>How big a sphere to probe around it. Per source because it depends on how much room
    /// the emitter has above what it is resting on. See <see cref="AudioEmission.OcclusionRadiusFor"/>.</summary>
    public float SourceRadius;
    public bool IsImportant;
}

public class AsyncAcousticWorker : IDisposable
{
    private readonly SpatialAcoustics _acoustics;
    private readonly ConcurrentQueue<AcousticRequest> _requestQueue = new();
    private readonly ConcurrentDictionary<int, List<AcousticPathData>> _results = new();
    private readonly CancellationTokenSource _cts = new();
    private Thread? _workerThread;

    // We hold a reference to the latest world snapshot to avoid queueing it per-request
    private WorldSnapshot? _latestWorld;
    private readonly object _worldLock = new();

    // --- Steam Audio simulation (Phase 4b) ---------------------------------------------------------
    // Per-source occlusion + transmission computed from real geometry via SteamAudioSimulator, replacing
    // the hand-rolled occlusion/EQ on the DIRECT path. Everything Phonon here lives on THIS worker thread
    // only (created lazily in WorkerLoop, freed in Dispose after the thread joins) — no cross-thread calls,
    // and never on the FMOD mixer thread. The hand-rolled SpatialAcoustics still produces reflections,
    // portal apparent-position, air absorption and room gain; we only override occlusion/EQ/bleed.
    // Set OPENFPS_STEAMAUDIO_SIM=0 to force the legacy hand-rolled occlusion.
    private static readonly bool _saDisabled = Environment.GetEnvironmentVariable("OPENFPS_STEAMAUDIO_SIM") == "0";
    private static readonly bool _saDebug = Environment.GetEnvironmentVariable("OPENFPS_AUDIO_DEBUG") == "1";
    private int _lastSceneBoxes;

    // OPENFPS_AUDIO_DEBUG=1 tracing. The per-source line used to print for EVERY pending source on EVERY
    // worker tick: on the speedway that is twenty-odd sources at the audio update's 60 Hz cap, well over a
    // thousand synchronized Console.WriteLine calls a second, which floods the log the ear test is supposed
    // to be read from and slows the thread it is measuring. Per source it is now one line a second, and the
    // line that is actually useful during a live listen — how many sources the simulator answered for, how
    // many it says are blocked, how many it re-aimed at an opening — is a single summary at the same rate.
    private const long SaDebugIntervalMs = 1000;
    private readonly Dictionary<int, long> _saDebugLastPrint = new();
    private long _saSummaryLastPrint;
    private const int SaMaxSources = 64;
    private const long SaSourceTtlMs = 5000; // release a source whose voice hasn't been requested in 5s

    private bool _saTried;
    private bool _saEnabled;
    private IntPtr _saContext;
    private SteamAudioScene? _saScene;
    private SteamAudioSimulator? _saSim;
    private AcousticMap? _saSceneMap; // the map the current scene was built for (rebuild when it changes)

    private volatile float _listenerReverbMs;  // 0 = no simulated reverb available yet
    private volatile float _listenerEnclosure; // 0..1, how closed-in the listener is; see Enclosure
    private volatile float _listenerHfDecayRatio = 1f;  // RT60(high)/RT60(mid) — how the tail is coloured
    private volatile float _listenerLfDecayRatio = 1f;  // RT60(low)/RT60(mid)
    private int _reverbTick;
    private const int ReverbEveryNTicks = 6;

    /// <summary>True once Steam Audio simulation is running (occlusion/pathing). The client uses this to
    /// stop spawning the hand-rolled discrete reflection emitters, since geometry-driven reverb covers them.</summary>
    public bool SteamAudioActive => _saEnabled;

    /// <summary>The simulated reverb decay (FMOD SFXREVERB ms) for the listener's room, or false if SA
    /// simulation isn't producing one. Read from the game thread to drive the listener-region reverb.</summary>
    public bool TryGetListenerReverbDecayMs(out float ms)
    {
        ms = _listenerReverbMs;
        return _saEnabled && ms > 0f;
    }

    /// <summary>
    /// How enclosed the listener is, 0 (open field) to 1 (sealed box) — the fraction of what leaves
    /// that comes back off geometry near enough to be a room rather than an echo.
    ///
    /// Separate from the decay time on purpose. The decay says how long a tail lasts; this says
    /// whether there is one. See <see cref="Enclosure"/> for why that had to be split apart.
    /// </summary>
    public float ListenerEnclosure => _listenerEnclosure;

    /// <summary>How the listener's room colours its own tail: the ratio of the high band's decay to the
    /// middle's, and the low band's to the middle's. 1 means an even decay across the spectrum.</summary>
    public float ListenerHfDecayRatio => _listenerHfDecayRatio;
    /// <summary>Where the listener's reverberant field comes from, world space, and how one-sided it
    /// is (0 = from everywhere, 1 = from one direction). See Enclosure.ReturnCentroid.</summary>
    public Vector3 ListenerReturnDirection => new(_listenerReturnX, _listenerReturnY, _listenerReturnZ);
    public float ListenerAnisotropy => _listenerAnisotropy;
    /// <summary>The distance between surfaces round the listener, metres — the room's size as the
    /// rays found it. The diffuse tail begins a couple of these after the direct sound.</summary>
    public float ListenerMeanFreePath => _listenerMfp;
    /// <summary>The room's surface area as the rays measured it, m². The room equation needs it and
    /// used to assume a cube instead; see Enclosure.ReverberantToDirectPower.</summary>
    public float ListenerSurfaceArea => _listenerSurface;
    private volatile float _listenerReturnX, _listenerReturnY, _listenerReturnZ, _listenerAnisotropy, _listenerMfp, _listenerSurface;
    public float ListenerLfDecayRatio => _listenerLfDecayRatio;
    private readonly Dictionary<int, IntPtr> _saSources = new();   // entityId -> acquired IPLSource
    private readonly Dictionary<int, long> _saLastSeen = new();     // entityId -> TickCount64 of last request
    private readonly Dictionary<int, AcousticRequest> _pending = new(); // drained-per-tick latest request
    private List<int>? _evictScratch;

    public AsyncAcousticWorker(SpatialAcoustics acoustics)
    {
        _acoustics = acoustics;
    }

    public void Start()
    {
        if (_workerThread != null) return;
        _workerThread = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "AcousticWorkerThread",
            // BELOW the game, and a long way below the engine producers and the mixer.
            //
            // What this thread computes — occlusion, reflection paths, the geometry-driven reverb —
            // is allowed to arrive late. What the mixer computes is not: a block that misses its
            // deadline is not stale, it is a hole. At AboveNormal this competed for cores with the
            // audio at the one moment there are none to spare, which is a map load: the scene is
            // being built from a hundred-odd colliders and thirty engines are starting at the same
            // time. The cost of losing that race is a fraction of a second of slightly stale
            // occlusion. The cost of the mixer losing it is the audio cutting out.
            Priority = ThreadPriority.BelowNormal
        };
        _workerThread.Start();
    }

    public void UpdateWorld(WorldSnapshot world)
    {
        lock (_worldLock)
        {
            _latestWorld = world;
        }
    }

    public void EnqueueRequest(AcousticRequest req)
    {
        _requestQueue.Enqueue(req);
    }

    /// <summary>Files a result under the entity it answers, stamped with where the source was when it
    /// was asked about, so the consumer can tell a result for THIS sound from one left behind by a
    /// previous occupant of a pooled id.</summary>
    private void Store(AcousticRequest req, List<AcousticPathData> paths)
    {
        for (int i = 0; i < paths.Count; i++)
        {
            var p = paths[i];
            p.SourcePosition = req.SourcePos;
            paths[i] = p;
        }
        _results[req.EntityId] = paths;
    }

    public bool TryGetResult(int entityId, out List<AcousticPathData> paths)
    {
        return _results.TryGetValue(entityId, out paths!);
    }

    /// <summary>
    /// Drops a removed entity's cached acoustic result. Its Steam Audio source (if any) is released by
    /// the idle TTL sweep; this stops the stale paths being handed back for an entity that no longer exists.
    /// </summary>
    public void Forget(int entityId) => _results.TryRemove(entityId, out _);

    public WorldSnapshot? GetLastWorld()
    {
        lock (_worldLock) { return _latestWorld; }
    }

    private void WorkerLoop()
    {
        EnsureSteamAudio();

        while (!_cts.Token.IsCancellationRequested)
        {
            // Drain ALL queued requests for this tick; the latest request per entity wins. Batching lets
            // the Steam Audio direct stage run ONCE for every active source instead of once per request.
            bool any = false;
            while (_requestQueue.TryDequeue(out var req)) { _pending[req.EntityId] = req; any = true; }
            if (!any) { Thread.Sleep(1); continue; }

            WorldSnapshot? world;
            lock (_worldLock) { world = _latestWorld; }
            if (world == null) { _pending.Clear(); continue; }

            if (_saEnabled && _saSim != null)
            {
                // Steam Audio is the ACTIVE spatializer: build the acoustic result from the simulator
                // (occlusion/transmission + pathing arrival direction). Any source the simulator did NOT
                // produce a result for — a failed tick, an unbuilt scene, an exhausted source pool — falls
                // back to the hand-rolled ray-tracer for that source. It must NEVER fall back to
                // DirectResult.Clear: "no result" would then be rendered as "nothing is in the way", which
                // is the single worst possible answer in a game played by ear — every wall in the level
                // silently disappears and the player is told a lie about where sounds are.
                Dictionary<int, SaResult>? sim = RunSteamAudio(world);
                int degraded = 0;
                foreach (var kv in _pending)
                {
                    var req = kv.Value;
                    if (sim != null && sim.TryGetValue(req.EntityId, out var r))
                    {
                        Store(req, BuildSimPath(world, req, r));
                    }
                    else
                    {
                        Store(req, HandRolledPath(world, req));
                        degraded++;
                    }
                }
                ReportSimCoverage(degraded, _pending.Count, sim == null);
                ReportRayBudget(_pending.Count);
            }
            else
            {
                // Legacy hand-rolled spatializer — the standing configuration when Steam Audio sim is
                // unavailable or disabled (OPENFPS_STEAMAUDIO_SIM=0 / no phonon library).
                foreach (var kv in _pending)
                    Store(kv.Value, HandRolledPath(world, kv.Value));
            }

            _pending.Clear();
        }
    }

    /// <summary>
    /// The hand-rolled ray-traced acoustic path for one source — the real fallback whenever the Steam Audio
    /// simulator did not answer for it. It is a worse model than the simulator, but it is a MODEL: it still
    /// occludes through walls, still finds portals, still attenuates. Returning it is always better than
    /// returning "clear".
    ///
    /// If even the hand-rolled tracer throws, the source keeps whatever result it last had rather than being
    /// reset to unoccluded; only a source that has never had one gets a clear path, and that is logged.
    /// </summary>
    private List<AcousticPathData> HandRolledPath(WorldSnapshot world, AcousticRequest req)
    {
        try
        {
            return _acoustics.CalculateAcousticPaths(world, req.EntityId, req.ListenerPos, req.SourcePos, req.IsImportant);
        }
        catch (Exception ex)
        {
            ReportTracerFailure(req.EntityId, ex);
            if (_results.TryGetValue(req.EntityId, out var previous) && previous.Count > 0) return previous;
            return new List<AcousticPathData>
            {
                new()
                {
                    Occlusion = 0f, EqLow = 1f, EqMid = 1f, EqHigh = 1f, TransmissionBleed = 0f,
                    ApparentPosition = req.SourcePos,
                    EffectiveDistance = Vector3.Distance(req.ListenerPos, req.SourcePos),
                    ApertureFactor = 1f, RoomGain = 1f, AirAbsorption = 0f, RegionId = -1, IsReflection = false,
                }
            };
        }
    }

    // --- Degradation reporting ------------------------------------------------------------------------
    // A degradation that nobody is told about is a bug that never gets fixed. These log on the TRANSITION
    // (healthy -> degraded and back) rather than every tick, so the log stays readable while never hiding
    // the fact that the geometry-driven acoustics stopped answering.
    private bool _simDegradedLogged;
    private long _lastDegradeLogTicks;
    private const long DegradeLogIntervalMs = 10_000;
    private readonly HashSet<int> _tracerFailuresReported = new();

    /// <summary>True when Steam Audio simulation is enabled but is not currently covering every source, so
    /// some or all of the acoustics are coming from the hand-rolled tracer.</summary>
    public bool IsDegraded { get; private set; }

    private void ReportSimCoverage(int degraded, int total, bool wholeTickFailed)
    {
        bool nowDegraded = degraded > 0;
        IsDegraded = nowDegraded;

        if (!nowDegraded)
        {
            if (_simDegradedLogged)
            {
                Console.WriteLine("[AcousticWorker] Steam Audio simulation recovered; all sources are geometry-simulated again.");
                _simDegradedLogged = false;
            }
            return;
        }

        long now = Environment.TickCount64;
        if (_simDegradedLogged && now - _lastDegradeLogTicks < DegradeLogIntervalMs) return;

        _simDegradedLogged = true;
        _lastDegradeLogTicks = now;
        string cause = wholeTickFailed
            ? "the simulation tick produced no results at all"
            : $"the simulator had no result for some sources (more than {SaMaxSources} wanted in one tick)";
        Console.WriteLine($"[AcousticWorker] DEGRADED: {degraded}/{total} sources fell back to the hand-rolled ray-tracer — {cause}.");
    }

    // --- Ray budget -----------------------------------------------------------------------------------
    // "Measure the Steam Audio ray budget" (audit step 6). The simulation cost is a configuration — rays
    // times bounces times sources — and until it is measured on real hardware every number in it is a
    // guess. The simulator times its own runs; this reports them, and says so loudly when a run costs more
    // than the audio frame it is feeding, because a worker that cannot keep up does not fail, it just
    // silently delivers older and older acoustics.
    private long _lastRayBudgetReportTicks;
    private long _lastRayBudgetWarnTicks;
    private const long RayBudgetReportIntervalMs = 30_000;

    /// <summary>One audio frame at <see cref="ClientAudioSystem"/>'s 60 Hz cap — the wall a run should
    /// stay under to keep the acoustics current with what is being heard.</summary>
    private const double RayBudgetFrameMs = 1000.0 / 60.0;

    /// <summary>The last measured simulation cost, for anything that wants to display it.</summary>
    public string RayBudgetSummary { get; private set; } = "no simulation runs yet";

    private void ReportRayBudget(int sources)
    {
        if (_saSim == null || _saSim.RunCount == 0) return;

        long now = Environment.TickCount64;

        if (_saSim.MaxRunMs > RayBudgetFrameMs && now - _lastRayBudgetWarnTicks >= DegradeLogIntervalMs)
        {
            _lastRayBudgetWarnTicks = now;
            Console.WriteLine($"[AcousticWorker] RAY BUDGET EXCEEDED: a simulation run took {_saSim.MaxRunMs:F1}ms " +
                              $"against a {RayBudgetFrameMs:F1}ms audio frame ({_saSim.RaysPerRun} rays x " +
                              $"{_saSim.BouncesPerRun} bounce(s), {sources} sources). Acoustics are lagging what is heard.");
        }

        if (now - _lastRayBudgetReportTicks < RayBudgetReportIntervalMs) return;
        _lastRayBudgetReportTicks = now;
        RayBudgetSummary = _saSim.DescribeRayBudget();
        if (_saDebug || PerfProbe.Enabled)
            Console.WriteLine($"[AcousticWorker] ray budget: {RayBudgetSummary}");
        _saSim.ResetRayBudgetStats();
    }

    private string? _lastSimTickFailure;
    private long _lastSimTickFailureTicks;

    /// <summary>Records why a whole simulation tick produced nothing. Logged on change of cause, and at
    /// most once per interval thereafter — a persistent failure stays visible without flooding.</summary>
    private void ReportSimTickFailure(string reason)
    {
        long now = Environment.TickCount64;
        if (reason == _lastSimTickFailure && now - _lastSimTickFailureTicks < DegradeLogIntervalMs) return;
        _lastSimTickFailure = reason;
        _lastSimTickFailureTicks = now;
        Console.WriteLine($"[AcousticWorker] Steam Audio simulation tick produced no results — {reason}. Falling back to the hand-rolled ray-tracer.");
    }

    private void ReportTracerFailure(int entityId, Exception ex)
    {
        if (!_tracerFailuresReported.Add(entityId)) return;
        Console.WriteLine($"[AcousticWorker] Hand-rolled acoustic tracer failed for entity {entityId} (reported once): {ex.Message}");
    }

    /// <summary>Per-entity Steam Audio result for one tick: the direct occlusion/transmission, and (when the
    /// source is occluded and pathing found a route) the world-space apparent position to localize the HRTF
    /// to the opening the sound arrives through.</summary>
    /// <summary>What the simulator says about one source this tick. <c>BarrierDelta</c> is how far out of
    /// its way sound had to bend to get past the worst thing in the line (negative = nothing in the way),
    /// measured once here because BOTH the arrival direction and the per-band level are decided by it.</summary>
    private readonly record struct SaResult(SteamAudioSimulator.DirectResult Direct, Vector3 ApparentPosition,
                                            bool HasApparent, float BarrierDelta);

    // Below this direct visibility a source is "occluded enough" that pathing should drive its apparent
    // position to the opening the sound arrives through (rather than the straight-through-wall direction).
    private const float PathRedirectVisibility = 0.5f;

    /// <summary>Runs the Steam Audio direct + pathing stages for all pending sources against the current
    /// listener and returns per-entity results, or null when SA simulation is unavailable/disabled.</summary>
    private Dictionary<int, SaResult>? RunSteamAudio(WorldSnapshot world)
    {
        if (!_saEnabled || _saSim == null) return null;
        try
        {
            RebuildSceneIfNeeded(world);
            if (_saScene == null || !_saScene.IsBuilt)
            {
                ReportSimTickFailure("the Steam Audio scene is not built");
                return null;
            }

            // ── A finished bake joins the simulation HERE, before anything is staged ───────────
            //
            // Before, not after, and that ordering is the whole of it. Attaching the probe batch flips
            // PathingReady true, and PathingReady is what decides BOTH whether a source's inputs carry
            // a probe batch and whether the run executes the pathing stage. Committing it after the
            // sources were staged left the two disagreeing for exactly one tick: thirty sources staged
            // without probes, and then a pathing run over them. Steam Audio dereferenced a probe batch
            // no source had been given and took the process with it — no exception, no log line, the
            // client simply ended a second after the cars started.
            //
            // Between runs, and on the thread that owns the simulator: it is single-threaded by
            // contract, and the bake thread never touches it.
            if (_saSim.CommitPendingProbes())
                Console.WriteLine("[AcousticWorker] Pathing probes are live; occluded sources can now be localized to the opening they arrive through.");

            long now = Environment.TickCount64;
            Vector3 listener = default;
            bool haveListener = false;
            int blocked = 0, viaEdge = 0, viaProbe = 0, reflections = 0;
            foreach (var kv in _pending)
            {
                // The listener is the same for every request this tick, so take it from the first one —
                // NOT only from requests that won a source. Otherwise an exhausted source pool would drop
                // the whole tick instead of just the sources it could not fit.
                if (!haveListener) { listener = kv.Value.ListenerPos; haveListener = true; }

                IntPtr src = GetOrAcquireSource(kv.Key);
                if (src == IntPtr.Zero) continue; // pool exhausted -> that source falls back to hand-rolled
                float radius = kv.Value.SourceRadius > 0f ? kv.Value.SourceRadius : AudioEmission.DefaultOcclusionRadius;
                _saSim.SetSourceInputs(src, kv.Value.SourcePos, radius);
                _saLastSeen[kv.Key] = now;
            }
            if (!haveListener) return null;   // nothing pending; not a degradation

            _saSim.SetListener(listener);
            _saSim.Run();

            var results = new Dictionary<int, SaResult>(_pending.Count);
            foreach (var kv in _pending)
            {
                if (!_saSources.TryGetValue(kv.Key, out var src) || src == IntPtr.Zero) continue;
                var direct = _saSim.GetResult(src);

                if (_saDebug && kv.Key >= 0)
                {
                    _saDebugLastPrint.TryGetValue(kv.Key, out long lastPrint);
                    if (now - lastPrint >= SaDebugIntervalMs)
                    {
                        _saDebugLastPrint[kv.Key] = now;
                        var sp = kv.Value.SourcePos; var lp = kv.Value.ListenerPos;
                        Console.WriteLine($"[SAWORKER] e{kv.Key} vis={direct.Visibility:F2} src=({sp.X:F1},{sp.Y:F1},{sp.Z:F1}) reqLis=({lp.X:F1},{lp.Y:F1},{lp.Z:F1}) runLis=({listener.X:F1},{listener.Y:F1},{listener.Z:F1}) sceneBoxes={_lastSceneBoxes}");
                    }
                }

                // ── Which way did it come: over the thing, or round it? ──────────────────────────
                //
                // Losing the line of sight is not on its own a reason to move a source. Sound past an
                // obstacle takes the better of two routes, and they arrive from DIFFERENT DIRECTIONS:
                // over the top of a barrier, which is essentially still the source's own bearing, or
                // through an opening somewhere else, which is not. BuildSimPath already lets the two
                // compete for the LEVEL — that is what stopped a knee-high pit wall silencing a car.
                // The direction was being decided separately, on visibility alone, so the two halves
                // disagreed: the level said "it came over the wall" and the bearing said "it came from
                // a probe seven metres to your left".
                //
                // On the speedway that is heard, and was reported, as a near car STOPPING. The pathing
                // probes are laid on a uniform floor grid sized to the map — 7.3 m apart over a
                // 900 x 480 m track — so a redirected bearing is quantised to that grid. At a hundred
                // metres that is four degrees and invisible; at fifteen it is nearly thirty, and it
                // holds still while the car crosses the cell and then jumps. The car's sound stops
                // tracking the car.
                //
                // So the routes compete for the bearing on the same terms they compete for the level:
                // whichever delivers more energy decides where it came from. A 0.9 m wall gives a few
                // centimetres of detour, loses about five decibels, and wins — the car keeps its own
                // direction. A grandstand gives a detour the barrier ceiling flattens to 24 dB down,
                // and any real opening beats it. Nothing here knows what a wall or a doorway is.
                float barrierDelta = BarrierPathDifference(kv.Value.SourcePos, kv.Value.ListenerPos,
                                                           out Vector3 edge, out bool edgeVerified);
                Vector3 apparent = default;
                bool hasApparent = false;
                if (direct.Visibility < PathRedirectVisibility)
                {
                    float dist = Vector3.Distance(kv.Value.ListenerPos, kv.Value.SourcePos);
                    if (edgeVerified)
                    {
                        // The edge is the secondary source. Placed along its bearing at the real
                        // source's distance, for the same reason the pathing branch does: the level
                        // has already been decided by the route, and moving the source nearer would
                        // charge it for the detour twice.
                        Vector3 toEdge = edge - kv.Value.ListenerPos;
                        if (toEdge.LengthSquared() > 1e-6f)
                        {
                            apparent = kv.Value.ListenerPos + Vector3.Normalize(toEdge) * dist;
                            hasApparent = true; viaEdge++;
                        }
                    }
                    else
                    {
                        var path = _saSim.GetPathing(src);
                        if (path.Found)
                        {
                            apparent = kv.Value.ListenerPos + path.WorldDirection * dist;
                            hasApparent = true; viaProbe++;
                        }
                    }
                }
                results[kv.Key] = new SaResult(direct, apparent, hasApparent, barrierDelta);
                if (direct.Visibility < PathRedirectVisibility) blocked++;
                reflections += _lastReflectionCount;
            }

            RunListenerReverb(listener);
            EvictStaleSources(now);

            if (_saDebug && now - _saSummaryLastPrint >= SaDebugIntervalMs)
            {
                _saSummaryLastPrint = now;
                Console.WriteLine($"[SASUMMARY] sources={results.Count}/{_pending.Count} blocked={blocked} viaEdge={viaEdge} viaProbe={viaProbe} " +
                                  $"refl={reflections} sceneBoxes={_lastSceneBoxes} pathing={(_saSim.PathingReady ? "ready" : "off")} " +
                                  $"reverb={_listenerReverbMs:F0}ms lis=({listener.X:F1},{listener.Y:F1},{listener.Z:F1})");
            }
            return results;
        }
        catch (Exception ex)
        {
            ReportSimTickFailure($"the simulation threw: {ex.Message}");
            return null;
        }
    }

    /// <summary>Builds the complete acoustic result for one source purely from the Steam Audio simulator —
    /// occlusion/transmission EQ (SA <c>occlusion</c> is a VISIBILITY gain; the engine wants "fraction
    /// blocked" + per-band clarity), and the apparent position redirected to the opening when pathing found a
    /// route around an occluder. Region is looked up from the map (for reverb routing) when one is loaded.
    /// No hand-rolled ray-tracing or reflection entries — those are retired on the SA path.</summary>
    private List<AcousticPathData> BuildSimPath(WorldSnapshot world, AcousticRequest req, SaResult sr)
    {
        var ap = SteamAudioSimulator.ToAcousticParams(sr.Direct);
        float occ = Math.Clamp(ap.Occlusion, 0f, AcousticConstants.OcclusionCap);
        Vector3 apparent = sr.HasApparent ? sr.ApparentPosition : req.SourcePos;
        float dist = Vector3.Distance(req.ListenerPos, req.SourcePos);

        // ── What actually gets past the thing in the way ────────────────────────────────────
        //
        // The simulator's direct stage knows line-of-sight and transmission THROUGH a material. It has
        // no edge diffraction, so a source it cannot see is a source that can only reach the ear by
        // going through the wall — and for a solid wall that is almost nothing. Taken literally, a
        // knee-high pit wall silenced a car twenty metres behind it: twenty-six decibels down with the
        // top three octaves gone. The wall is 0.9 m tall. You can see over it.
        //
        // So the simulator's visibility is an INPUT here, not the answer. What arrives is the better of
        // the two routes sound can take past an obstacle — through it, or round it — and the second is
        // a function of how far out of its way it had to go, which is geometry the simulator does not
        // report and we can measure ourselves. A high wall gives a big detour and stays a wall; a low
        // one gives a few centimetres and costs a handful of decibels, mostly at the top end. Neither
        // outcome is written down anywhere: both fall out of the same measurement.
        if (occ > 0f)
        {
            // Measured once per source per tick, in RunSteamAudio, because the SAME number decides
            // the bearing there and the per-band level here. Two measurements could disagree, and a
            // source whose level says "over the wall" while its bearing says "through a door" is
            // exactly the fault this carrying was introduced to remove.
            float delta = sr.BarrierDelta;
            if (delta >= 0f)
            {
                var (dLow, dMid, dHigh) = Diffraction.BandGains(delta, AudioPhysics.SpeedOfSound);
                // Per band, the better route wins. Transmission is what the material lets through;
                // diffraction is what came round the edge regardless of what the material is.
                ap = new SteamAudioSimulator.AcousticParams(
                    ap.Occlusion,
                    MathF.Max(ap.EqLow, dLow),
                    MathF.Max(ap.EqMid, dMid),
                    MathF.Max(ap.EqHigh, dHigh),
                    ap.Bleed);
                // And the dry level cannot fall below what the loudest band still delivers: a source
                // whose energy is arriving round an edge is quieter, not absent.
                float throughput = MathF.Max(dLow, MathF.Max(dMid, dHigh));
                occ = Math.Clamp(MathF.Min(occ, 1f - throughput), 0f, AcousticConstants.OcclusionCap);
            }
        }

        int region = -1;
        bool listenerEnclosed = false;
        if (world.AcousticMap != null)
        {
            try { region = _acoustics.GetRegionAt(world, req.SourcePos); }
            catch { region = -1; }
            // The LISTENER's boundary, which is what the air absorption model is asking about. This
            // was the SOURCE's region and the test was "is it not the global id", so a source that
            // stood in any named region made the listener indoors — and after the speedway got its
            // sector names, that was every source on the map.
            try
            {
                listenerEnclosed = world.AcousticMap.Regions.TryGetValue(
                                       _acoustics.GetRegionAt(world, req.ListenerPos), out var lr)
                                   && RoomAcoustics.IsEnclosure(lr);
            }
            catch { listenerEnclosed = false; }
        }

        var path = new AcousticPathData
        {
            Occlusion = occ,
            EqLow = ap.EqLow,
            EqMid = ap.EqMid,
            EqHigh = ap.EqHigh,
            TransmissionBleed = ap.Bleed,
            ApparentPosition = apparent,
            EffectiveDistance = dist,
            // The per-band gains above already carry the barrier's frequency dependence, so the
            // provider's aperture low-pass — which models a sound squeezing through a small OPENING,
            // a different phenomenon — stays out of the way and is not a second filter over the top.
            ApertureFactor = 1f,
            RoomGain = 1f,
            // Was a hard zero, described as "a later phenomena pass". The effect of that was that
            // turning the simulator ON turned air absorption OFF for every source in the game, so a
            // shot two streets away arrived with all its high frequency intact — quiet, but bright,
            // which the ear reads as small-and-near rather than big-and-far.
            AirAbsorption = AudioPhysics.AirAbsorptionFor(
                dist, world.Humidity, world.Temperature,
                world.AirPressure, world.AirAbsorptionMultiplier,
                listenerIndoors: listenerEnclosed),
            RegionId = region,
            IsReflection = false,
        };

        var paths = new List<AcousticPathData>(1 + EarlyReflections.MaxArrivals) { path };
        AddEarlyReflections(paths, world, req, region, listenerEnclosed);
        return paths;
    }

    /// <summary>
    /// The copies of this source that the surfaces around it send back.
    ///
    /// The simulator does not produce these and cannot: its reflection stage is parametric, which
    /// yields a decay TIME for the listener's surroundings and no directions at all. A tail with no
    /// direction in it is a blanket — a room answers from everywhere at once, and a doorway cannot be
    /// heard from outside, which is exactly what was reported. So the early part of a room's response
    /// is built here from the same boxes everything else in this file reads, as first-order image
    /// sources: mirror the source through each surface and you have where the copy stands, how far it
    /// travelled, and what the material took out of it.
    ///
    /// These are the LOUD, EARLY, DIRECTIONAL part. What is left after them — the copies of copies,
    /// too many and too close together to have a direction any more — is the reverb bus's job, and its
    /// level comes from how enclosed the place is (see Enclosure).
    /// </summary>
    private void AddEarlyReflections(List<AcousticPathData> into, WorldSnapshot world,
                                     AcousticRequest req, int region, bool listenerEnclosed)
    {
        var solids = ReflectionSolids();
        if (solids.Count == 0) return;

        _reflectionScratch ??= new List<EarlyReflections.Arrival>();
        // FIRST ORDER, and the old ranking, for a sound that goes on. Heard 2026-09-23 with third order
        // and separate-events-first here: an aeroplane's jet and a bus's air hiss mirrored off the
        // hangar and the facades became extra copies of themselves standing still in the distance,
        // cutting in and out as each path came and went — "ghostly washes of white noise that stay in
        // one place" — and a far siren's image put it in front of you. The echo of a SUSTAINED sound is
        // not heard as an event; it is part of the field, which the reverb is. Copies of copies belong
        // to one-off sounds (WorldAudioPlayer), where an echo happens once and is gone.
        EarlyReflections.Find(req.SourcePos, req.ListenerPos, solids, _reflectionScratch, AudioPhysics.SpeedOfSound);

        _lastReflectionCount = 0;
        for (int i = 0; i < _reflectionScratch.Count; i++)
        {
            var a = _reflectionScratch[i];

            // ── Only an arrival the ear hears apart gets a voice of its own ─────────────────────
            //
            // A voice is an independent playback. Two voices of one sound are two reads of it at
            // unrelated positions, so for anything sustained — a siren, a machine, a megaphone
            // repeating an announcement — a second voice is a second copy of the announcement, not a
            // reflection of it. Inside the fusion window the ear would not have heard a separate event
            // anyway; it would have heard one wider, slightly coloured event. Rendering it as a voice
            // buys nothing and costs the repeat that was reported.
            //
            // The energy is not lost. Those surfaces are the same ones the room survey measured, and
            // what they return is the room's tail — which is where a fused reflection belongs.
            if (!EarlyReflections.IsSeparateEvent(a)) continue;

            into.Add(new AcousticPathData
            {
                IsReflection = true,
                // The surface's own identity, so a wall keeps one voice while the listener moves
                // instead of being torn down and started again — which is a click per frame.
                ReflectionId = a.SurfaceId,
                ReflectionIndex = i,
                ApparentPosition = a.ImagePosition,
                EffectiveDistance = a.PathLength,
                ReflectionDelayMs = a.ExtraDelaySeconds * 1000f,
                // A reflection is not occluded — it got here, which is what being found means. What it
                // LOST is carried per band, by the surface and by the extra distance it travelled.
                Occlusion = 0f,
                EqLow = a.GainLow,
                EqMid = a.GainMid,
                EqHigh = a.GainHigh,
                MaterialAbsorption = 1f - a.GainMid,
                Scattering = a.Scattering,
                // A rough surface returns a wider, less pointlike copy. The provider reads this as the
                // arrival's angular width.
                Spread = a.Scattering * 90f,
                ApertureFactor = 1f,
                RoomGain = 1f,
                TransmissionBleed = 0f,
                AirAbsorption = AudioPhysics.AirAbsorptionFor(
                    a.PathLength, world.Humidity, world.Temperature,
                    world.AirPressure, world.AirAbsorptionMultiplier,
                    listenerIndoors: listenerEnclosed),
                RegionId = region,
            });
            _lastReflectionCount++;
        }
    }

    private List<EarlyReflections.Arrival>? _reflectionScratch;

    /// <summary>How many arrivals the last source's reflection search produced, for the summary line.</summary>
    private int _lastReflectionCount;

    /// <summary>The scene's boxes in the shape the reflection model wants. Rebuilt only when the scene
    /// is, because the geometry is static and this runs per source per tick.</summary>
    private IReadOnlyList<EarlyReflections.Solid> ReflectionSolids()
    {
        var boxes = _barrierBoxes;
        if (ReferenceEquals(_reflectionSolidsFor, boxes)) return _reflectionSolids;
        var solids = new List<EarlyReflections.Solid>(boxes.Count);
        for (int i = 0; i < boxes.Count; i++)
            solids.Add(new EarlyReflections.Solid(boxes[i].Center, boxes[i].Size, boxes[i].Rotation, boxes[i].Material));
        _reflectionSolids = solids;
        _reflectionSolidsFor = boxes;
        return solids;
    }

    private object? _reflectionSolidsFor;
    private IReadOnlyList<EarlyReflections.Solid> _reflectionSolids = Array.Empty<EarlyReflections.Solid>();

    /// <summary>The boxes the current scene was built from, for the barrier search. Replaced whole on
    /// a scene rebuild and only ever read afterwards, so the worker needs no lock to walk it.</summary>
    private List<OpenFPS.Client.Core.AudioEngine.SteamAudio.SteamAudioScene.Box> _barrierBoxes = new();

    /// <summary>
    /// How far out of its way sound had to go to get from the source to the listener, metres, or -1 if
    /// nothing is in the way at all.
    ///
    /// Barriers do not add up: two screens in a row are not twice one screen, because the second is
    /// standing in the first one's shadow. What governs is the single worst detour, which is what the
    /// standards use and the only version that does not silence a source merely for having a lot of
    /// scenery near it.
    /// </summary>
    private float BarrierPathDifference(Vector3 source, Vector3 listener)
        => BarrierPathDifference(source, listener, out _, out _);

    /// <summary>
    /// The same search, also reporting the point the sound left on its last leg to the ear — the
    /// diffracting edge, which is where a blocked source is actually heard FROM.
    ///
    /// <paramref name="edgeVerified"/> is a claim about the EDGE alone, and it is deliberately not
    /// allowed to touch the returned path difference. The detour a barrier costs is an estimate either
    /// way and a decent one — the standards' single-worst-screen rule — so a source behind a doorway
    /// keeps the relief that stops it sounding like it is coming through the wall. Where the sound is
    /// COMING FROM is not an estimate: it either is that corner or it is somewhere else entirely, and
    /// a bearing that is wrong by thirty degrees is worse than no bearing at all.
    /// </summary>
    private float BarrierPathDifference(Vector3 source, Vector3 listener, out Vector3 edge, out bool edgeVerified)
    {
        var boxes = _barrierBoxes;
        float worst = -1f;
        int worstBox = -1;
        edge = listener;
        for (int i = 0; i < boxes.Count; i++)
        {
            var b = boxes[i];
            if (!Diffraction.PathDifferenceAroundBox(b.Center, b.Size, b.Rotation, source, listener,
                                                     out float d, out Vector3 p)) continue;
            if (d <= worst) continue;
            worst = d; worstBox = i; edge = p;
        }

        // ── An edge you cannot see is not where you are hearing it from ─────────────────────────
        //
        // The search above is per box: it guarantees the route clears the box it went round, and knows
        // nothing about the rest of the scene. Round the end of one wall and straight into the next is
        // a perfectly good answer to "how far past THIS box" and a completely wrong answer to "which
        // way did it come" — two rooms apart, the nearest silhouette edge is inside the wall between
        // them. So the last leg is checked against everything before its bearing is believed; when it
        // fails, there is a route but we do not know where it runs, and Steam Audio's probe graph —
        // which does know the whole scene — answers instead.
        edgeVerified = worstBox >= 0 && RouteIsClear(source, edge, listener, worstBox);
        return worst;
    }

    /// <summary>Are BOTH legs of a diffracted route — source to edge, edge to ear — clear of every OTHER
    /// barrier? The diffracting box itself is skipped: the edge lies on its surface, so it always reports
    /// a hit.
    ///
    /// Both, not just the arriving one. Round the west end of a doorway's west leaf is the shortest way
    /// past THAT leaf and runs straight into the room's west wall — the leg that fails is the one leaving
    /// the source, and a bearing taken from it points at a corner the sound never reached.</summary>
    private bool RouteIsClear(Vector3 source, Vector3 edge, Vector3 listener, int skipBox)
    {
        var boxes = _barrierBoxes;
        for (int i = 0; i < boxes.Count; i++)
        {
            if (i == skipBox) continue;
            var b = boxes[i];
            if (GeometryUtils.LineIntersectsOBB(edge, listener, b.Center, b.Size, b.Rotation)) return false;
            if (GeometryUtils.LineIntersectsOBB(source, edge, b.Center, b.Size, b.Rotation)) return false;
        }
        return true;
    }

    private void EnsureSteamAudio()
    {
        if (_saTried) return;
        _saTried = true;
        if (_saDisabled) { Console.WriteLine("[AcousticWorker] Steam Audio sim disabled by OPENFPS_STEAMAUDIO_SIM=0; using the hand-rolled ray-tracer for occlusion, portals and reverb."); return; }
        try
        {
            // Idempotent: ensure acoustic materials exist before the scene build queries them. Without this
            // an uninitialized registry makes every scene-material lookup throw, and the sim silently falls
            // back to no-occlusion. The clients already call this, but the SA path shouldn't depend on it.
            AcousticRegistry.Initialize();

            var cs = Phonon.DefaultContextSettings();
            string simd = Phonon.SimdLevelName(cs.simdLevel);
            if (Phonon.iplContextCreate(ref cs, out _saContext) != Phonon.IPL_STATUS_SUCCESS)
            { _saContext = IntPtr.Zero; Console.WriteLine($"[AcousticWorker] DEGRADED: Steam Audio context create failed (SIMD {simd}); using the hand-rolled ray-tracer."); return; }

            _saSim = new SteamAudioSimulator(_saContext, SaMaxSources, enablePathing: true);
            if (!_saSim.IsValid)
            {
                _saSim.Dispose(); _saSim = null;
                Phonon.iplContextRelease(ref _saContext);
                Console.WriteLine("[AcousticWorker] DEGRADED: Steam Audio simulator create failed; using the hand-rolled ray-tracer.");
                return;
            }
            _saScene = new SteamAudioScene(_saContext);


            _saEnabled = true;
            Console.WriteLine($"[AcousticWorker] Steam Audio simulation enabled (SIMD {simd}; per-source occlusion/transmission/pathing). " +
                              "Reflections and room response are measured from the scene's own surfaces.");
        }
        catch (DllNotFoundException)
        {
            Console.WriteLine($"[AcousticWorker] DEGRADED: {OpenFPS.Client.Core.Platform.NativeAudioLibraries.PhononFileName} not found; using the hand-rolled ray-tracer for occlusion, portals and reverb.");
        }
        catch (Exception ex) { Console.WriteLine($"[AcousticWorker] DEGRADED: Steam Audio sim init failed; using the hand-rolled ray-tracer: {ex.Message}"); }
    }

    /// <summary>Rebuilds the simulator scene from the world's solid box colliders when the acoustic map
    /// changes (or on first use). Geometry is mostly static, so this is a per-map-load cost.</summary>
    private void RebuildSceneIfNeeded(WorldSnapshot world)
    {
        if (_saScene == null || _saSim == null) return;
        if (_saScene.IsBuilt && ReferenceEquals(world.AcousticMap, _saSceneMap)) return;

        var boxes = SteamAudioScene.BoxesFromWorld(world);
        _lastSceneBoxes = boxes.Count;
        if (_saDebug)
            foreach (var b in boxes)
                Console.WriteLine($"[SABOX] center=({b.Center.X:F1},{b.Center.Y:F1},{b.Center.Z:F1}) size=({b.Size.X:F1},{b.Size.Y:F1},{b.Size.Z:F1}) mat={b.Material}");
        _saScene.Build(boxes);
        _saSceneMap = world.AcousticMap;
        if (_saScene.IsBuilt)
        {
            _saSim.SetScene(_saScene);
            // Off the worker thread and out of the way. This is the work that used to sit inside
            // SetScene and take a hundred seconds of a single core before ANY source got an occlusion
            // value — a hundred seconds in which the whole world was rendered as if nothing were in
            // the way, ending in every source receiving its first real occlusion in the same frame.
            _saSim.BeginProbeBake(_saScene);
        }
        // The boxes the barrier model bends sound around. The same list the scene was built from, so
        // the diffraction path and the occlusion test can never disagree about what is in the world.
        _barrierBoxes = boxes;
        Console.WriteLine($"[AcousticWorker] Built Steam Audio scene from {boxes.Count} solid box colliders.");
    }

    /// <summary>Throttled: runs the reflections sim for a single probe at the listener to get the room's
    /// geometry-driven RT60, mapped to an FMOD reverb decay (ms) the provider applies to the listener's
    /// reverb. Reflections are the heaviest stage, so this runs only every Nth tick for one source.</summary>
    /// <summary>
    /// What the room around the listener does to sound, measured from its surfaces.
    ///
    /// This used to be Steam Audio's reflection stage — a second simulator, the heaviest of the three,
    /// run on a throttle to fit a single number out of it. That number could not tell a room from a
    /// yard (a roofless box fitted a LONGER tail than the same box with a roof on), had no bands in it,
    /// and knew nothing about what the walls were made of. The stage is retired here; the simulator
    /// still supports it for the spikes.
    ///
    /// What replaces it is a sphere of rays from the listener, which was already being cast to measure
    /// enclosure. The same cast yields the mean free path and the mean absorption per band, and those
    /// are the two things a decay time is made of. So the room's character now comes from its
    /// materials: a carpeted half of a hall answers dull and short, the concrete half beside it bright
    /// and long, and neither is written down anywhere.
    /// </summary>
    private void RunListenerReverb(Vector3 listener)
    {
        if (++_reverbTick % ReverbEveryNTicks != 0) return;

        var survey = Enclosure.Look(listener, EnclosureSolids());
        var (low, mid, high) = Enclosure.DecaySeconds(survey);

        _listenerEnclosure = survey.Enclosure;
        _listenerReturnX = survey.ReturnDirection.X;
        _listenerReturnY = survey.ReturnDirection.Y;
        _listenerReturnZ = survey.ReturnDirection.Z;
        _listenerAnisotropy = survey.Anisotropy;
        _listenerMfp = survey.MeanFreePathMetres;
        _listenerSurface = survey.SurfaceAreaSquareMetres;
        _listenerReverbMs = Math.Clamp(mid * 1000f, AcousticConstants.MinReverbDecayMs,
                                                    AcousticConstants.MaxReverbDecayMs);
        // How much faster the top decays than the middle. This is the audible half of what a material
        // is: carpet takes the high band four times harder than the low, so its tail dies bright-first
        // and sounds like cloth; concrete's barely tilts at all and rings.
        _listenerHfDecayRatio = Math.Clamp(high / MathF.Max(0.01f, mid), 0.1f, 2.0f);
        _listenerLfDecayRatio = Math.Clamp(low / MathF.Max(0.01f, mid), 0.1f, 4.0f);
    }

    /// <summary>The scene's boxes as the enclosure measure wants them. Rebuilt only when the scene is,
    /// because the geometry is static and this runs on the audio worker.</summary>
    private IReadOnlyList<Enclosure.Solid> EnclosureSolids()
    {
        var boxes = _barrierBoxes;
        if (ReferenceEquals(_enclosureSolidsFor, boxes)) return _enclosureSolids;
        var solids = new List<Enclosure.Solid>(boxes.Count);
        for (int i = 0; i < boxes.Count; i++)
            solids.Add(new Enclosure.Solid(boxes[i].Center, boxes[i].Size, boxes[i].Rotation, boxes[i].Material));
        _enclosureSolids = solids;
        _enclosureSolidsFor = boxes;
        return solids;
    }

    private object? _enclosureSolidsFor;
    private IReadOnlyList<Enclosure.Solid> _enclosureSolids = Array.Empty<Enclosure.Solid>();

    private IntPtr GetOrAcquireSource(int entityId)
    {
        if (_saSources.TryGetValue(entityId, out var s)) return s;
        IntPtr src = _saSim!.AcquireSource();
        if (src == IntPtr.Zero)
        {
            // ── A full pool lends out the source nobody is asking about ──────────────────────
            //
            // A source is held for SaSourceTtlMs after its last request, so the pool fills with
            // every id asked about in the last five seconds — on the city that is fifty-odd playing
            // voices plus every live car and machine, and it crossed 64. The pool was first come,
            // first served: whichever sound started LAST (a door, a footstep, the room you just
            // walked into) was the one refused, and it was rendered by the hand-rolled tracer —
            // different occlusion from its neighbours, until you walked out and something expired.
            //
            // Nothing a source carries between runs is needed: its inputs are staged fresh before
            // every run it is read in. So only the sources asked about THIS tick need one, and a
            // held source that is not among them can be handed over without anything losing an answer.
            int victim = PickSourceToReclaim(_saSources, _saLastSeen, _pending);
            if (victim != int.MinValue && _saSources.Remove(victim, out src))
                _saLastSeen.Remove(victim);   // its last result stays in _results until it asks again
            else
                return IntPtr.Zero;
        }
        _saSources[entityId] = src;
        return src;
    }

    /// <summary>The held source that has gone longest without a request and is not wanted this tick,
    /// or <see cref="int.MinValue"/> when every held source is wanted now.</summary>
    internal static int PickSourceToReclaim<TSrc, TReq>(IReadOnlyDictionary<int, TSrc> held,
        IReadOnlyDictionary<int, long> lastSeen, IReadOnlyDictionary<int, TReq> wantedThisTick)
    {
        int victim = int.MinValue;
        long oldest = long.MaxValue;
        foreach (var kv in held)
        {
            if (wantedThisTick.ContainsKey(kv.Key)) continue;
            long seen = lastSeen.TryGetValue(kv.Key, out long t) ? t : long.MinValue;
            if (seen < oldest || victim == int.MinValue) { oldest = seen; victim = kv.Key; }
        }
        return victim;
    }

    private void EvictStaleSources(long now)
    {
        _evictScratch ??= new List<int>();
        _evictScratch.Clear();
        foreach (var kv in _saLastSeen)
            if (now - kv.Value > SaSourceTtlMs) _evictScratch.Add(kv.Key);

        foreach (int id in _evictScratch)
        {
            if (_saSources.TryGetValue(id, out var src))
            {
                _saSim!.ClearSource(src); // stop tracing it
                _saSim.ReleaseSource(src);
                _saSources.Remove(id);
            }
            _saLastSeen.Remove(id);
            _saDebugLastPrint.Remove(id);
            _results.TryRemove(id, out _);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _workerThread?.Join(); // after this, no other thread touches the Phonon sim objects

        if (_saSim != null) { _saSim.Dispose(); _saSim = null; }
        if (_saScene != null) { _saScene.Dispose(); _saScene = null; }
        if (_saContext != IntPtr.Zero) Phonon.iplContextRelease(ref _saContext);

        _cts.Dispose();
    }
}

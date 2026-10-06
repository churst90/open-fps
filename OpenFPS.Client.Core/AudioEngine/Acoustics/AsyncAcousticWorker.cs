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
    /// <summary>The same scene without its open ground, for the trace from the listener's head.</summary>
    private SteamAudioScene? _saListenerScene;
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

    /// <summary>The probe graph has been baked and pathing takes part in every tick. The lab waits on
    /// this: a probe asked before the bake lands measures a different engine from the game's.</summary>
    public bool PathingReady => _saSim?.PathingReady == true;

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

    /// <summary>Lab only (--pop-hunt): records what produced each source's last direct answer in
    /// <see cref="Provenance"/>. Off in the game.</summary>
    public static bool TraceProvenance;
    /// <summary>What produced each source's last direct answer, when <see cref="TraceProvenance"/> is on.</summary>
    public readonly ConcurrentDictionary<int, string> Provenance = new();

    /// <summary>Each source's last Steam Audio answer and when, for a tick the pool had no room for it.</summary>
    private readonly Dictionary<int, (List<AcousticPathData> Paths, long At)> _lastSimPath = new();
    /// <summary>How old a held answer may be: well past a tick, well short of anything moving far.</summary>
    private const long HeldSimPathMs = 1000;
    /// <summary>Requests the pool had no room for this tick, asked first on the next.</summary>
    private readonly List<AcousticRequest> _carried = new();
    /// <summary>The sources given a place in the pool this tick: the first SaMaxSources in line.</summary>
    private readonly HashSet<int> _served = new();

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
        foreach (var p in paths)
            if (!p.IsReflection) { Remember(req, p); break; }
    }

    // ── What was heard near here a moment ago ─────────────────────────────────────────────────
    //
    // A one-shot gets a voice id of its own, so the simulator has never been asked about it when it
    // starts. It used to start on the hand-rolled tracer's guess and slide to the simulator's answer a
    // frame or two later — after the attack, the loudest part, had already played. For a walker on the
    // pavement outside a flat that guess was a route out of the flat's door (-4 dB) where the brick
    // wall's answer was -24: every step's attack came through the wall ("I still hear people walking
    // outside to my right"). The walker's previous step, 0.7 m back, has the real answer; so does the
    // shot before this one. Nothing here knows what a footstep is: it is an answer for a nearby point.
    private const int RecentCapacity = 256;
    private const long RecentMaxAgeMs = 1500;
    private readonly (Vector3 Listener, Vector3 Source, long At, AcousticPathData Path)[] _recent = new (Vector3, Vector3, long, AcousticPathData)[RecentCapacity];
    private int _recentNext;
    private readonly object _recentLock = new();

    private void Remember(AcousticRequest req, AcousticPathData path)
    {
        lock (_recentLock)
        {
            _recent[_recentNext] = (req.ListenerPos, req.SourcePos, Environment.TickCount64, path);
            _recentNext = (_recentNext + 1) % RecentCapacity;
        }
    }

    /// <summary>
    /// The simulator's direct-path answer for the nearest source it was asked about recently, if one
    /// was near enough to stand for this one: the listener within half a metre of where they were, the
    /// source within a metre (or a tenth of its distance, far off). False when nothing qualifies.
    /// </summary>
    public bool TryGetNearby(Vector3 listener, Vector3 source, out AcousticPathData path)
    {
        path = default;
        if (!_saEnabled) return false;
        long now = Environment.TickCount64;
        float tolerance = MathF.Max(1f, 0.1f * Vector3.Distance(listener, source));
        float best = float.MaxValue;
        bool found = false;
        lock (_recentLock)
        {
            foreach (var r in _recent)
            {
                if (r.At == 0 || now - r.At > RecentMaxAgeMs) continue;
                if (Vector3.DistanceSquared(r.Listener, listener) > 0.25f) continue;
                float d = Vector3.Distance(r.Source, source);
                if (d > tolerance || d >= best) continue;
                best = d; path = r.Path; found = true;
            }
        }
        return found;
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
            // What the pool had no room for last tick goes first: it was put back in _pending, ahead of
            // anything asked since, and a newer request for the same source takes its place in the line.
            bool any = false;
            AcousticRequest newest = default;
            while (_requestQueue.TryDequeue(out var req)) { _pending[req.EntityId] = req; newest = req; any = true; }
            if (!any && _pending.Count == 0) { Thread.Sleep(1); continue; }
            // A request carried over was made for where the listener was a tick ago; it is answered for
            // where they are now, as every other source this tick is.
            if (any && _carried.Count > 0)
                foreach (var c in _carried)
                    if (_pending.TryGetValue(c.EntityId, out var held) && held.ListenerPos != newest.ListenerPos)
                        _pending[c.EntityId] = held with { ListenerPos = newest.ListenerPos };

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
                _carried.Clear();
                foreach (var kv in _pending)
                {
                    var req = kv.Value;
                    if (sim != null && sim.TryGetValue(req.EntityId, out var r))
                    {
                        var built = BuildSimPath(world, req, r);
                        Store(req, built);
                        _lastSimPath[req.EntityId] = (built, Environment.TickCount64);
                    }
                    else if (sim != null && _lastSimPath.ContainsKey(req.EntityId))
                    {
                        // ── No room in the pool this tick: asked again first thing next tick ─────
                        //
                        // Not handed to the hand-rolled tracer. That is a different model, and from
                        // the Main Street pavement it put a car behind a building at -15 dB where
                        // Steam Audio and the barrier search say -63, until the source's turn in the
                        // pool came round again (2026-10-03, --pop-hunt extra=40): a pop every time
                        // the pool ran over. A source that has had an answer from the simulator
                        // keeps it — moved with the source, below — and waits one tick for the next.
                        _carried.Add(req);
                        if (_lastSimPath.TryGetValue(req.EntityId, out var held)
                            && Environment.TickCount64 - held.At < HeldSimPathMs)
                        {
                            // Moved with the source: the answer's positions are where it WAS, and a bus
                            // at 15 m/s held for a second would be heard 15 m behind itself.
                            var moved = new List<AcousticPathData>(held.Paths.Count);
                            foreach (var hp in held.Paths)
                            {
                                var m = hp;
                                m.ApparentPosition += req.SourcePos - hp.SourcePosition;
                                moved.Add(m);
                            }
                            Store(req, moved);
                            if (TraceProvenance) Provenance[req.EntityId] = "held";
                        }
                    }
                    else
                    {
                        // Never answered by the simulator: the tracer's answer stands in until it is,
                        // first thing next tick if the pool was what refused it.
                        Store(req, HandRolledPath(world, req));
                        if (TraceProvenance) Provenance[req.EntityId] = "hand-rolled";
                        if (sim != null) _carried.Add(req);
                        degraded++;
                    }
                }
                ReportSimCoverage(degraded, _pending.Count, sim == null);
                // Transient sounds get a new id each time: drop the held answers nobody can use.
                if (_lastSimPath.Count > 512)
                {
                    long stale = Environment.TickCount64 - HeldSimPathMs;
                    foreach (var id in _lastSimPath.Where(e => e.Value.At < stale).Select(e => e.Key).ToList())
                        _lastSimPath.Remove(id);
                }
                ReportRayBudget(_pending.Count);
                _pending.Clear();
                foreach (var c in _carried) _pending[c.EntityId] = c;
                continue;
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
            return _acoustics.CalculateAcousticPaths(world, req.EntityId, req.ListenerPos, req.SourcePos);
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
                    ApertureFactor = 1f, RoomGain = 1f, RegionId = -1, IsReflection = false,
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
    /// <param name="BarrierVerified">The route round the barrier box is clear of everything else. When it
    /// is not, the route does not exist and its level must not be used (BuildSimPath).</param>
    /// <param name="Path">Steam Audio's own route through the scene, when it found one.</param>
    /// <param name="Route">What came by the openings (OpeningRoutes), when the source and the listener are
    /// in different places and a route joins them.</param>
    private readonly record struct SaResult(SteamAudioSimulator.DirectResult Direct, Vector3 ApparentPosition,
                                            bool HasApparent, float BarrierDelta,
                                            bool BarrierVerified = true,
                                            SteamAudioSimulator.PathResult Path = default,
                                            OpeningRoutes.Answer? Route = null);

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
            // ── Who is served this tick when more are asked about than the pool holds ─────────────
            //
            // The first SaMaxSources in line, and the line starts with whoever was turned away last
            // tick (WorkerLoop). A source held by someone further back is lent to someone served. Before,
            // every source asked about this tick kept its own, so when eighty were asked about every
            // tick the same sixteen were turned away every tick, for good, and heard through the
            // hand-rolled tracer: cars behind a building popping between -63 and -15 dB.
            Serve(_pending.Keys, SaMaxSources, _served);
            foreach (var kv in _pending)
            {
                // The listener is the same for every request this tick, so take it from the first one —
                // NOT only from requests that won a source. Otherwise an exhausted source pool would drop
                // the whole tick instead of just the sources it could not fit.
                if (!haveListener) { listener = kv.Value.ListenerPos; haveListener = true; _lastListenerPos = listener; _haveLastListener = true; }

                if (!_served.Contains(kv.Key)) continue;   // asked first next tick (WorkerLoop)
                IntPtr src = GetOrAcquireSource(kv.Key);
                if (src == IntPtr.Zero) continue;
                float radius = kv.Value.SourceRadius > 0f ? kv.Value.SourceRadius : AudioEmission.DefaultOcclusionRadius;
                _saSim.SetSourceInputs(src, kv.Value.SourcePos, radius);
                _saLastSeen[kv.Key] = now;
            }
            if (!haveListener) return null;   // nothing pending; not a degradation
            // The traced reverb follows the ear; it runs its own trace on its own thread. The region
            // tells it when you have gone into another room, so its averaged tail starts again.
            int hereRegion = _acoustics.GetRegionAt(world, listener);
            OpenFPS.Client.Core.AudioEngine.SteamAudio.TracedReverbSet.SetListener(listener, hereRegion);

            _saSim.SetListener(listener);
            _saSim.Run();

            var results = new Dictionary<int, SaResult>(_pending.Count);
            var routes = _routes;
            _routeTicksThisTick = 0;
            int listenerRegion = routes != null ? hereRegion : AcousticConstants.GlobalRegionId;
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
                Vector3 edge = kv.Value.ListenerPos;
                bool edgeVerified = false;
                float barrierDelta = routes != null
                    ? routes.BarrierPathDifference(kv.Value.SourcePos, kv.Value.ListenerPos, out edge, out edgeVerified)
                    : -1f;
                if (_saDebug && edgeVerified)
                    Console.WriteLine($"[ROUTE] {kv.Value.SourcePos} -> {kv.Value.ListenerPos}: {barrierDelta:F2} m round one box, edge {edge}");
                Vector3 apparent = default;
                bool hasApparent = false;
                var route = default(SteamAudioSimulator.PathResult);
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
                    // No verified route: the level is what comes THROUGH the wall (BuildSimPath), so the
                    // bearing is the source's own. The probe graph's direction used to be taken here with
                    // the level from elsewhere, and from a flat on the ground floor the graph's route ran
                    // down to its floor grid: a siren behind the wall was heard from straight below,
                    // where turning the head changes nothing.
                }
                // ── And by the openings ──────────────────────────────────────────────────────
                //
                // Through the walls and round one edge is all the above can find. From the street to a
                // corridor by the front door, the stairwell and its doorway is a route with corners
                // in it, and without it an open front door changed nothing ("only when a loud source
                // passes does it come inside"). Asked whenever the source and the listener are in
                // different places; BuildSimPath lets it compete band by band.
                OpeningRoutes.Answer? viaOpenings = routes != null
                    ? AskRoutes(routes, world, kv.Key, kv.Value.SourcePos, kv.Value.ListenerPos, listenerRegion)
                    : null;
                results[kv.Key] = new SaResult(direct, apparent, hasApparent, barrierDelta, edgeVerified, route, viaOpenings);
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
        string? trace = TraceProvenance
            ? $"sim vis {sr.Direct.Visibility:F2} through {Db(ap.EqLow):F0}/{Db(ap.EqMid):F0}/{Db(ap.EqHigh):F0}" : null;
        if (_saDebug) Console.WriteLine($"[SAPATH] e{req.EntityId} vis {sr.Direct.Visibility:F3} trans {sr.Direct.TransLow:F3}/{sr.Direct.TransMid:F3}/{sr.Direct.TransHigh:F3} barrierDelta {sr.BarrierDelta:F3} verified {sr.BarrierVerified} route {(sr.Path.Found ? $"{sr.Path.EqLow:F3}/{sr.Path.EqMid:F3}/{sr.Path.EqHigh:F3}" : "none")}");
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
            // Only a route that EXISTS. The barrier search goes round one box at a time; round the edge
            // of a shut door is eight centimetres out of the way and straight into the wall the door is
            // hung in, and if that route set the level a shut door between two rooms would pass
            // -7/-11/-19 dB (--path-probe shows it). When the route round is blocked, what arrives is
            // what the wall lets through.
            //
            // NOT Steam Audio's pathing eq. That is the colour of the bend, not the loss: about 1.0 for a
            // route a hundred and fifty metres long, so every siren and walker behind a wall would play
            // at full level, and from below, where the probe grid's route points.
            if (delta >= 0f && !sr.BarrierVerified) delta = -1f;
            if (delta >= 0f)
            {
                var (dLow, dMid, dHigh) = Diffraction.BandGains(delta, AudioPhysics.CurrentSpeedOfSound);
                // And the route round is LONGER, which the barrier's insertion loss does not pay for
                // once it reaches its 24 dB ceiling. A walker on the pavement outside Marlow flat 01F
                // is 8 m from the ear through a brick wall and 164 m round the building: capped, that
                // route would come out at -24 dB in every band and beat the wall's own -24/-30/-36, so
                // every step would be heard through the brick. Spreading over the longer
                // route costs 26 dB there, and under half a decibel for a half-metre kerb.
                float spread = MathF.Max(0.5f, dist) / (MathF.Max(0.5f, dist) + delta);
                dLow *= spread; dMid *= spread; dHigh *= spread;
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
                if (trace != null) trace += $"; over an edge {delta:F1} m {Db(dLow):F0}/{Db(dMid):F0}/{Db(dHigh):F0}";
            }
            else if (trace != null && sr.BarrierDelta >= 0f) trace += $"; edge {sr.BarrierDelta:F1} m not verified";
        }

        // ── By the openings, where that delivers more ───────────────────────────────────────
        //
        // The same rule as the one-shots' path (SpatialAcoustics.CalculateMainPath) and the same graph:
        // per band the better of the straight way and the way by the openings, and the openings decide
        // where it is heard from when they deliver more over all — from the last one, at the source's
        // own distance, the level having already paid for the longer way.
        if (sr.Route is { } viaOpenings)
        {
            var g = OpeningRoutes.Better(new Vector3(ap.EqLow, ap.EqMid, ap.EqHigh), viaOpenings, out bool routeWins);
            if (trace != null)
                trace += $"; openings {Db(viaOpenings.Low):F0}/{Db(viaOpenings.Mid):F0}/{Db(viaOpenings.High):F0} via {viaOpenings.Via}{(routeWins ? " (wins)" : "")}";
            ap = new SteamAudioSimulator.AcousticParams(ap.Occlusion, g.X, g.Y, g.Z, ap.Bleed);
            occ = Math.Clamp(MathF.Min(occ, 1f - MathF.Max(g.X, MathF.Max(g.Y, g.Z))), 0f, AcousticConstants.OcclusionCap);
            Vector3 toOpening = viaOpenings.Apparent - req.ListenerPos;
            if (routeWins && toOpening.LengthSquared() > 1e-6f)
                apparent = req.ListenerPos + Vector3.Normalize(toOpening) * dist;
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
            RegionId = region,
            IsReflection = false,
        };
        // What the air took on the way, per band (ISO 9613-1). Once a hard zero here, described as "a
        // later phenomena pass": turning the simulator on turned air absorption off, and a shot two
        // streets away arrived with its top end intact — quiet but bright, which reads as small and
        // near rather than big and far.
        (path.AirLowDb, path.AirMidDb, path.AirHighDb) = AudioPhysics.AirLossDb(
            dist, world.Humidity, world.Temperature, world.AirPressure, world.AirAbsorptionMultiplier);

        if (trace != null) Provenance[req.EntityId] = trace;
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
        // FIRST ORDER, in EarlyReflections' own order, for a sound that goes on. With third order and
        // separate events first, an aeroplane's jet and a bus's air hiss mirrored off the hangar and
        // the facades become extra copies of themselves standing still in the distance, cutting in
        // and out as each path comes and goes, and a far siren's image puts it in front of you. The
        // echo of a SUSTAINED sound is not heard as an event; it is part of the field, which the
        // reverb is. Copies of copies belong to one-off sounds (WorldAudioPlayer), where an echo
        // happens once and is gone.
        EarlyReflections.Find(req.SourcePos, req.ListenerPos, solids, _reflectionScratch, AudioPhysics.CurrentSpeedOfSound);

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

            var reflected = new AcousticPathData
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
                RegionId = region,
            };
            (reflected.AirLowDb, reflected.AirMidDb, reflected.AirHighDb) = AudioPhysics.AirLossDb(
                a.PathLength, world.Humidity, world.Temperature, world.AirPressure, world.AirAbsorptionMultiplier);
            into.Add(reflected);
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

    // ── Routes by the openings, and what they cost ────────────────────────────────────────────
    //
    // The graph (OpeningRoutes) is built with each scene, so it has the door leaves where the scene has
    // them, and handed to SpatialAcoustics so every other voice asks the same one. A route query is a
    // few Dijkstra steps and a handful of segment tests, but there are dozens of sources a tick: an
    // answer is kept while neither end has moved enough to change it.
    private volatile OpeningRoutes? _routes;
    private readonly Dictionary<int, (OpeningRoutes Model, Vector3 Source, Vector3 Listener, OpeningRoutes.Answer? Answer, long At)> _routeCache = new();
    /// <summary>How far either end may move before a source's route is asked again, metres: well under a
    /// doorway's width, so the crossing it reports cannot be a different opening.</summary>
    private const float RouteReuseMetres = 0.25f;
    private const long RouteReuseMs = 500;
    /// <summary>
    /// How much of a tick route queries may take, milliseconds. A source whose ends are new costs a leg
    /// search through the city (a millisecond or more when the way to a door is blocked); past this, a
    /// source that has an answer keeps it for this tick, however far it has moved, and only a source
    /// that has none is asked. A bound on cost, not on what is heard: the answer comes a tick later.
    /// </summary>
    private const double RouteBudgetMs = 4.0;
    /// <summary>The oldest answer the budget may stand on, milliseconds.</summary>
    private const long RouteHeldMaxMs = 2000;
    private long _routeTicksThisTick;
    private long _routeQueries, _routeTicks, _routeReused;
    private long _lastRouteReport;

    /// <summary>The cost of the route queries so far: how many, and the mean per query.</summary>
    public string RouteCostSummary =>
        _routeQueries == 0 ? "no route queries yet"
        : $"{_routeQueries} route queries, {_routeTicks * 1e6 / System.Diagnostics.Stopwatch.Frequency / _routeQueries:F0} µs each, {_routeReused} reused";

    private static float Db(float gain) => 20f * MathF.Log10(MathF.Max(1e-5f, gain));

    private OpeningRoutes.Answer? AskRoutes(OpeningRoutes routes, WorldSnapshot world, int id, Vector3 source, Vector3 listener, int listenerRegion)
    {
        long now = Environment.TickCount64;
        if (_routeCache.TryGetValue(id, out var held) && ReferenceEquals(held.Model, routes))
        {
            bool still = now - held.At < RouteReuseMs
                         && Vector3.DistanceSquared(held.Source, source) < RouteReuseMetres * RouteReuseMetres
                         && Vector3.DistanceSquared(held.Listener, listener) < RouteReuseMetres * RouteReuseMetres;
            bool overBudget = _routeTicksThisTick * 1000.0 / System.Diagnostics.Stopwatch.Frequency > RouteBudgetMs
                              && now - held.At < RouteHeldMaxMs;
            if (still || overBudget)
            {
                _routeReused++;
                return held.Answer;
            }
        }
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        OpeningRoutes.Answer? answer = null;
        try
        {
            if (routes.Route(source, _acoustics.GetRegionAt(world, source), listener, listenerRegion, out var a)) answer = a;
        }
        catch (Exception ex) { ReportTracerFailure(id, ex); }
        long spent = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        _routeTicks += spent;
        _routeTicksThisTick += spent;
        _routeQueries++;
        _routeCache[id] = (routes, source, listener, answer, now);
        if (_routeCache.Count > 1024)
            foreach (var k in _routeCache.Where(e => now - e.Value.At > RouteReuseMs).Select(e => e.Key).ToList())
                _routeCache.Remove(k);
        if ((_saDebug || PerfProbe.Enabled) && now - _lastRouteReport > 30_000)
        {
            _lastRouteReport = now;
            Console.WriteLine($"[AcousticWorker] routes: {RouteCostSummary}; {routes.Openings.Count} openings.");
        }
        return answer;
    }

    /// <summary>Builds the graph for a scene. What the map's openings disagree with in the geometry is
    /// said once, when the map arrives, not on every door's swing.</summary>
    private OpeningRoutes BuildRoutes(WorldSnapshot world, List<SteamAudioScene.Box> boxes, bool report)
        => BuildRoutes(world, boxes, null, null, report);

    /// <summary>
    /// The routes through openings for a scene. With the scene's acoustic triangle store
    /// (<paramref name="geometry"/>, geometry stage 1) they are built tile by tile: the boxes asked of the
    /// store's trees, each tile's boxes and each opening kept while nothing round it changed
    /// (<paramref name="cache"/>, this map's). Each opening's sides are checked against the places only
    /// when the result is reported: the check only ever wrote the report.
    /// </summary>
    private OpeningRoutes BuildRoutes(WorldSnapshot world, List<SteamAudioScene.Box> boxes,
                                      OpenFPS.Common.Geometry.TriangleWorld? geometry, OpeningRoutes.TileCache? cache, bool report)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Func<Vector3, int> regionAt = p => _acoustics.GetRegionAt(world, p);
        var model = geometry != null && cache != null && OpenFPS.Common.Geometry.TriangleGeometry.Enabled
            ? OpeningGraph.Build(world, geometry, report ? regionAt : null, cache)
            : OpeningGraph.Build(world, boxes, regionAt);
        if (!report) return model;
        Console.WriteLine($"[AcousticWorker] Openings: {model.Openings.Count} from the map, {model.Problems.Count} disagreeing with the geometry ({sw.ElapsedMilliseconds} ms).");
        foreach (var problem in model.Problems.Take(20)) Console.WriteLine($"[AcousticWorker]   opening {problem}");
        if (model.Problems.Count > 20) Console.WriteLine($"[AcousticWorker]   ...and {model.Problems.Count - 20} more.");
        return model;
    }

    private void PublishRoutes(OpeningRoutes model)
    {
        _routes = model;
        _acoustics.Routes = model;
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

            // Pathing off: its probe grid is too coarse on a city (tens of metres) to say where a sound
            // comes from, nothing reads its answer, and the bake cost a core for minutes at every map
            // load. Routes round obstacles come from the barrier search (BarrierPathDifference).
            // Embree, where it starts: the scene is then made of a sub-scene per tile and per door leaf
            // (TileSceneSet), and a tile arriving or a door swinging rebuilds none of the rest. The
            // simulators are made for the context's scene type, so this comes first.
            // OPENFPS_EMBREE=0 keeps the default tracer and whole-scene rebuilds (for comparison, or a
            // machine where Embree misbehaves); OPENFPS_TILE_SCENES=0 keeps Embree with whole scenes.
            _embree = Environment.GetEnvironmentVariable("OPENFPS_EMBREE") != "0"
                      && OpenFPS.Client.Core.AudioEngine.SteamAudio.SteamAudioScene.UseEmbree(_saContext);
            Console.WriteLine(_embree
                ? "[AcousticWorker] Steam Audio scenes use Embree: a sub-scene per tile and per door leaf."
                : "[AcousticWorker] Embree did not start here; Steam Audio scenes use the default tracer and are rebuilt whole.");
            _saSim = new SteamAudioSimulator(_saContext, SaMaxSources, enablePathing: false);
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
    // ── Doors are part of the geometry, where they are now ────────────────────────────────────────
    //
    // Built once per map with every door leaf where it stood at load, an open door would still be a
    // wall to occlusion, diffraction and the traces. The server swings the leaf's real transform, so
    // the world knows where it is. When any leaf has moved, the scene is rebuilt with it there — every
    // simulator takes the new one as it does on a map change, and Steam Audio's reference counting
    // keeps the old alive until each has let go — at most every DoorRebuildSeconds while a door
    // swings, and once more when it settles. The pathing probes are not rebaked for a door.
    private long _lastDoorRebuildTicks;
    private const double DoorRebuildSeconds = 0.3;
    /// <summary>Only doors this near the listener count: on the city walkers open doors all day, and a
    /// scene rebuild for one three hundred metres off is work nobody can hear.</summary>
    private const float DoorNearMetres = 50f;
    private Vector3 _lastListenerPos;
    private bool _haveLastListener;

    /// <summary>Where each door leaf stood when the scene in use was built, by entity.</summary>
    private readonly Dictionary<int, long> _builtDoorPoses = new();

    /// <summary>A door leaf: a solid box that is also a portal. The server gives every door a portal
    /// (PrefabRepository), with both sides the outside when the map names no rooms, so a door is told by
    /// HAVING one, not by its two sides differing — that test missed every door on the city. Movers
    /// are not in the scene at all.</summary>
    private static bool IsDoorLeaf(OpenFPS.Common.Networking.EntityDefinition? def) => OpeningGraph.IsDoorLeaf(def);

    /// <summary>A leaf's pose to the centimetre and the degree, folded into one number.</summary>
    private static long DoorPose(in EntitySnapshot snap)
    {
        var p = snap.Transform.Position; var q = snap.Transform.Rotation;
        long h = 17;
        h = h * 31 + (long)MathF.Round(p.X * 100f); h = h * 31 + (long)MathF.Round(p.Y * 100f); h = h * 31 + (long)MathF.Round(p.Z * 100f);
        h = h * 31 + (long)MathF.Round(q.Y * 100f); h = h * 31 + (long)MathF.Round(q.W * 100f);
        return h;
    }

    /// <summary>True when a leaf near the listener stands somewhere other than where the scene in use has
    /// it. Only a leaf that MOVED counts: a hash of the doors within 50 m would change every time one
    /// crossed that radius as you walked, and rebuild the scene for nothing. A far leaf that moved is
    /// left as it is until you come near it.</summary>
    private bool NearDoorMoved(WorldSnapshot world)
    {
        foreach (var snap in world.Entities.Values)
        {
            if (!IsDoorLeaf(snap.Definition)) continue;
            if (_haveLastListener && Vector3.DistanceSquared(snap.Transform.Position, _lastListenerPos) > DoorNearMetres * DoorNearMetres) continue;
            if (!_builtDoorPoses.TryGetValue(snap.Id, out long was) || was != DoorPose(snap)) return true;
        }
        return false;
    }

    /// <summary>Records where every leaf is, for the scene about to be built from this world.</summary>
    private void RecordDoorPoses(WorldSnapshot world)
    {
        _builtDoorPoses.Clear();
        foreach (var snap in world.Entities.Values)
            if (IsDoorLeaf(snap.Definition)) _builtDoorPoses[snap.Id] = DoorPose(snap);
    }

    // A door's rebuild is built OFF this thread and swapped in here when it is ready: on the city the
    // two scenes take about 120 ms (--scene-cost), and every source's occlusion would stand still for
    // that, several times a swing. Replaced scenes are released a few seconds later, once every
    // simulator has taken the new one.
    private System.Threading.Tasks.Task<(SteamAudioScene Full, SteamAudioScene Listener, List<SteamAudioScene.Box> Boxes, AcousticMap? Map, OpeningRoutes Routes, OpenFPS.Common.Geometry.TriangleWorld? Geometry)>? _doorBuild;
    private readonly List<(SteamAudioScene Scene, long At)> _retiredScenes = new();

    // ── Tiles arriving and leaving ─────────────────────────────────────────────────────────────
    //
    // On a map streamed in tiles the acoustic map object stays the same while its contents follow the
    // player (ClientWorldState.RefreshAcousticsNow), and each refresh bumps the snapshot's
    // GeometryVersion. A change of version rebuilds the scene exactly as a door does: off this thread, on
    // a niced one of its own because a radius of tiles is a bigger scene than a door's, and swapped in
    // when it is ready. Sources keep their last answers meanwhile; nothing waits for it.
    private long _builtGeometryVersion;
    private bool _buildIsForTiles;
    /// <summary>Embree started on this context: scenes are assembled from tiles (TileSceneSet).</summary>
    private bool _embree;
    /// <summary>This map's sub-scenes. Replaced on a new map; the old set is let go once no build uses it.</summary>
    private TileSceneSet? _tileScenes;
    /// <summary>Without a tile set, this map's acoustic triangle store (a new one each map: a build for the
    /// last map may still be using the old one).</summary>
    private AcousticGeometry? _acousticStore;
    /// <summary>The acoustic scene as triangles, swapped in with <see cref="_barrierBoxes"/>: what the
    /// enclosure survey casts against.</summary>
    private OpenFPS.Common.Geometry.TriangleWorld? _enclosureWorld;
    /// <summary>This map's routes kept tile by tile (OpeningRoutes.TileCache): one build at a time uses it.</summary>
    private OpeningRoutes.TileCache? _routeTiles;
    private double _lastScenesMs, _lastRoutesMs;
    private object? _routesPortals;
    private long _routesBuiltTicks;
    /// <summary>The longest the routes go unmade while only walls and roads change, seconds.</summary>
    private const double RoutesEverySeconds = 3.0;
    private readonly List<(TileSceneSet Set, long At)> _retiredTileSets = new();

    /// <summary>The tile sets of earlier maps, let go as replaced scenes are: once no build uses one, no
    /// tracer is still being handed over, and five seconds have passed.</summary>
    private void ReleaseRetiredTileSets()
    {
        if (_doorBuild != null || _retiredTileSets.Count == 0) return;
        if (OpenFPS.Client.Core.AudioEngine.SteamAudio.TracedReverbSet.Reconfiguring) return;
        long cutoff = DateTime.UtcNow.Ticks - 5 * TimeSpan.TicksPerSecond;
        for (int i = _retiredTileSets.Count - 1; i >= 0; i--)
            if (_retiredTileSets[i].At < cutoff) { _retiredTileSets[i].Set.Dispose(); _retiredTileSets.RemoveAt(i); }
    }

    /// <summary>The two scenes for a world: assembled from the tile set's sub-scenes (only the tiles
    /// that changed are built), or, without Embree, built whole.</summary>
    private static (SteamAudioScene Full, SteamAudioScene Listener) ScenesFor(IntPtr ctx, TileSceneSet? set, WorldSnapshot world,
                                                                             List<SteamAudioScene.Box> boxes, ISet<int>? leaves = null)
    {
        if (set != null)
        {
            set.Update(boxes, leaves);
            return set.Assemble();
        }
        var full = new SteamAudioScene(ctx);
        full.Build(boxes);
        var listener = new SteamAudioScene(ctx);
        listener.Build(SteamAudioScene.WithoutOpenGround(boxes));
        return (full, listener);
    }

    /// <summary>
    /// The acoustic scene as triangles (docs/GEOMETRY.md stage 1), the one the scenes were just made from:
    /// the tile set's store, or (without Embree) a store of the map's own brought up to the same boxes.
    /// What the enclosure survey casts its rays against.
    /// </summary>
    private static OpenFPS.Common.Geometry.TriangleWorld? GeometryFor(TileSceneSet? set, AcousticGeometry? store,
                                                                      List<SteamAudioScene.Box> boxes, ISet<int>? leaves = null)
    {
        if (!OpenFPS.Common.Geometry.TriangleGeometry.Enabled) return null;
        if (set != null) return set.Geometry;
        return store?.Update(boxes, leaves);
    }
    private long _buildStartedTicks;

    /// <summary>Tile rebuilds swapped in, and how long the last took, milliseconds. Diagnostic.</summary>
    public int TileSceneBuilds { get; private set; }
    public double LastTileSceneBuildMs { get; private set; }
    /// <summary>Every background scene build so far (tiles and doors), milliseconds of wall time in all.</summary>
    public double SceneBuildMsTotal { get; private set; }
    /// <summary>Of those: making the Steam Audio scenes, and making the routes through openings, ms in all.</summary>
    public double SceneOnlyMsTotal, RoutesMsTotal;

    private static System.Threading.Tasks.Task<T> RunLowered<T>(string name, Func<T> work)
    {
        var done = new System.Threading.Tasks.TaskCompletionSource<T>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        OpenFPS.Client.Core.Platform.BackgroundPriority.RunLowered(name, () =>
        {
            try { done.SetResult(work()); }
            catch (Exception ex) { done.SetException(ex); }
        });
        return done.Task;
    }

    private void SwapInDoorBuild()
    {
        if (_doorBuild is not { IsCompleted: true } t || _saSim == null) return;
        _doorBuild = null;
        if (t.Status != System.Threading.Tasks.TaskStatus.RanToCompletion) return;
        var (full, listener, boxes, map, routes, geometry) = t.Result;
        if (!ReferenceEquals(map, _saSceneMap) || !full.IsBuilt)
        {
            full.Dispose(); listener.Dispose();        // the map changed meanwhile: it is not this map's
            return;
        }
        long now = DateTime.UtcNow.Ticks;
        if (_saScene != null) _retiredScenes.Add((_saScene, now));
        if (_saListenerScene != null) _retiredScenes.Add((_saListenerScene, now));
        _saScene = full; _saListenerScene = listener;
        _saSim.SetScene(full);
        OpenFPS.Client.Core.AudioEngine.SteamAudio.TracedReverbSet.ConfigureInBackground(_saContext, full, listener.IsBuilt ? listener : null);
        _barrierBoxes = boxes;
        _enclosureWorld = geometry;
        _lastSceneBoxes = boxes.Count;
        PublishRoutes(routes);
        SceneBuildMsTotal += (DateTime.UtcNow.Ticks - _buildStartedTicks) / (double)TimeSpan.TicksPerMillisecond;
        if (_buildIsForTiles)
        {
            TileSceneBuilds++;
            LastTileSceneBuildMs = (DateTime.UtcNow.Ticks - _buildStartedTicks) / (double)TimeSpan.TicksPerMillisecond;
            Console.WriteLine($"[AcousticWorker] Tiles changed: the scene now has {boxes.Count} boxes ({LastTileSceneBuildMs:F0} ms, off this thread"
                + $"; scenes {_lastScenesMs:F0} ms, routes {_lastRoutesMs:F0} ms"
                + (_tileScenes != null ? $"; {_tileScenes.LastBuilt} tile(s) built, update {_tileScenes.LastUpdateMs:F0} ms, assembled in {_tileScenes.LastAssembleMs:F1} ms)." : ")."));
        }
        else Console.WriteLine("[AcousticWorker] A door moved: the scene now has the leaves where they are.");
    }

    private void ReleaseRetiredScenes()
    {
        // Not while the tracers are still being handed the new scenes: until then they trace the old.
        if (OpenFPS.Client.Core.AudioEngine.SteamAudio.TracedReverbSet.Reconfiguring) return;
        if (_saSim is { Baking: true }) return;                 // nor while the bake reads the old one
        long cutoff = DateTime.UtcNow.Ticks - 5 * TimeSpan.TicksPerSecond;
        for (int i = _retiredScenes.Count - 1; i >= 0; i--)
            if (_retiredScenes[i].At < cutoff) { _retiredScenes[i].Scene.Dispose(); _retiredScenes.RemoveAt(i); }
    }

    private void RebuildSceneIfNeeded(WorldSnapshot world)
    {
        if (_saScene == null || _saSim == null) return;
        SwapInDoorBuild();
        ReleaseRetiredScenes();
        ReleaseRetiredTileSets();
        bool mapChanged = !_saScene.IsBuilt || !ReferenceEquals(world.AcousticMap, _saSceneMap);
        if (!mapChanged)
        {
            if (_doorBuild != null) return;
            // The tile set assembles into the pair of scenes not in use, which is only free once the last
            // pair handed over has reached every tracer.
            if (_tileScenes != null && OpenFPS.Client.Core.AudioEngine.SteamAudio.TracedReverbSet.Reconfiguring) return;
            bool tiles = world.GeometryVersion != _builtGeometryVersion;
            if (!tiles)
            {
                if ((DateTime.UtcNow.Ticks - _lastDoorRebuildTicks) < DoorRebuildSeconds * TimeSpan.TicksPerSecond) return;
                if (!NearDoorMoved(world)) return;
            }
            _builtGeometryVersion = world.GeometryVersion;
            _lastDoorRebuildTicks = DateTime.UtcNow.Ticks;
            _buildStartedTicks = DateTime.UtcNow.Ticks;
            _buildIsForTiles = tiles;
            RecordDoorPoses(world);
            var doorBoxes = SteamAudioScene.BoxesFromWorld(world);
            var ctx = _saContext; var forMap = _saSceneMap; var set = _tileScenes;
            // The routes through openings are made again when the openings changed (a door moved, rooms
            // came or went) and otherwise at most every few seconds: the coarse ring moving as you drive
            // changes their far barriers, not their doorways, and making them is most of a tile's cost.
            var portals = world.AcousticMap?.Portals;
            bool routesDue = !tiles || _routes == null || !ReferenceEquals(portals, _routesPortals)
                             || DateTime.UtcNow.Ticks - _routesBuiltTicks > RoutesEverySeconds * TimeSpan.TicksPerSecond;
            var keptRoutes = _routes;
            if (routesDue) { _routesPortals = portals; _routesBuiltTicks = DateTime.UtcNow.Ticks; }
            var store = _acousticStore;
            var routeCache = _routeTiles;
            Func<(SteamAudioScene, SteamAudioScene, List<SteamAudioScene.Box>, AcousticMap?, OpeningRoutes, OpenFPS.Common.Geometry.TriangleWorld?)> build = () =>
            {
                var parts = System.Diagnostics.Stopwatch.StartNew();
                var leaves = OpeningGraph.LeavesOf(world);
                var (full, listener) = ScenesFor(ctx, set, world, doorBoxes, leaves);
                var geometry = GeometryFor(set, store, doorBoxes, leaves);
                _lastScenesMs = parts.Elapsed.TotalMilliseconds;
                SceneOnlyMsTotal += _lastScenesMs;
                parts.Restart();
                var routes = routesDue || keptRoutes == null ? BuildRoutes(world, doorBoxes, geometry, routeCache, report: false) : keptRoutes;
                _lastRoutesMs = parts.Elapsed.TotalMilliseconds;
                RoutesMsTotal += _lastRoutesMs;
                return (full, listener, doorBoxes, forMap, routes, geometry);
            };
            _doorBuild = tiles ? RunLowered("TileScene", build) : System.Threading.Tasks.Task.Run(build);
            return;
        }
        _builtGeometryVersion = world.GeometryVersion;
        _lastDoorRebuildTicks = DateTime.UtcNow.Ticks;
        RecordDoorPoses(world);
        var built = System.Diagnostics.Stopwatch.StartNew();

        var boxes = SteamAudioScene.BoxesFromWorld(world);
        var mapLeaves = OpeningGraph.LeavesOf(world);
        _lastSceneBoxes = boxes.Count;
        // A new map gets new scene objects; the old ones are retired, not rebuilt in place. The
        // tracers' threads and the pathing bake may still be running on them, and freeing a native
        // scene under a running trace is a crash.
        if (_saScene.IsBuilt)
        {
            long now = DateTime.UtcNow.Ticks;
            _retiredScenes.Add((_saScene, now));
            _saScene = new SteamAudioScene(_saContext);
            if (_saListenerScene != null) { _retiredScenes.Add((_saListenerScene, now)); _saListenerScene = null; }
        }
        if (_saDebug)
            foreach (var b in boxes)
                Console.WriteLine($"[SABOX] center=({b.Center.X:F1},{b.Center.Y:F1},{b.Center.Z:F1}) size=({b.Size.X:F1},{b.Size.Y:F1},{b.Size.Z:F1}) mat={b.Material}");
        if (_embree)
        {
            // A new map's own set of tiles; the last map's goes once no build is using it.
            if (_tileScenes != null) _retiredTileSets.Add((_tileScenes, DateTime.UtcNow.Ticks));
            _tileScenes = Environment.GetEnvironmentVariable("OPENFPS_TILE_SCENES") == "0" ? null : new TileSceneSet(_saContext, world.TileMetres);
            _acousticStore = _tileScenes == null ? new AcousticGeometry(world.TileMetres) : null;
            var (assembledFull, assembledListener) = ScenesFor(_saContext, _tileScenes, world, boxes, mapLeaves);
            _saScene.Dispose();
            _saScene = assembledFull;
            _saListenerScene?.Dispose();
            _saListenerScene = assembledListener;
            if (_tileScenes != null)
                Console.WriteLine($"[AcousticWorker] {_tileScenes.TileCount} tile sub-scene(s) built in {_tileScenes.LastUpdateMs:F0} ms, assembled in {_tileScenes.LastAssembleMs:F1} ms.");
        }
        else
        {
            _acousticStore = new AcousticGeometry(world.TileMetres);
            _saScene.Build(boxes);
        }
        _enclosureWorld = GeometryFor(_tileScenes, _acousticStore, boxes, mapLeaves);
        _routeTiles = new OpeningRoutes.TileCache();
        _saSceneMap = world.AcousticMap;
        if (_saScene.IsBuilt)
        {
            _saSim.SetScene(_saScene);
            // The places themselves, traced: an impulse response from where the listener stands, and
            // one from the middle of each other room that can be heard (SteamAudio.TracedReverbSet).
            //
            // The listener's own trace gets the scene without its open ground: see
            // SteamAudioScene.WithoutOpenGround for why a trace from the listener's head must not hear
            // the floor under their feet.
            if (!_embree)
            {
                _saListenerScene ??= new SteamAudioScene(_saContext);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var withoutGround = SteamAudioScene.WithoutOpenGround(boxes);
                _saListenerScene.Build(withoutGround);
                if (mapChanged)
                    Console.WriteLine($"[AcousticWorker] Listener trace scene: {boxes.Count - withoutGround.Count} open-ground slab(s) left out "
                                    + $"of {boxes.Count} ({sw.ElapsedMilliseconds} ms).");
            }
            OpenFPS.Client.Core.AudioEngine.SteamAudio.TracedReverbSet.Configure(_saContext, _saScene,
                _saListenerScene is { IsBuilt: true } ? _saListenerScene : null);
            // Off the worker thread and out of the way. This is the work that used to sit inside
            // SetScene and take a hundred seconds of a single core before ANY source got an occlusion
            // value — a hundred seconds in which the whole world was rendered as if nothing were in
            // the way, ending in every source receiving its first real occlusion in the same frame.
            if (mapChanged) _saSim.BeginProbeBake(_saScene);
        }
        // The boxes the barrier model bends sound around and the routes run through. The same list the
        // scene was built from, so the diffraction path and the occlusion test can never disagree about
        // what is in the world.
        _barrierBoxes = boxes;
        PublishRoutes(BuildRoutes(world, boxes, _enclosureWorld, _routeTiles, report: mapChanged));
        Console.WriteLine($"[AcousticWorker] Built Steam Audio scene from {boxes.Count} solid box colliders ({built.ElapsedMilliseconds} ms).");
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

        var geometry = _enclosureWorld;
        var survey = geometry != null && OpenFPS.Common.Geometry.TriangleGeometry.Enabled
            ? Enclosure.Look(listener, geometry)
            : Enclosure.Look(listener, EnclosureSolids());
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
            int victim = PickSourceToReclaim(_saSources, _saLastSeen, _served);
            if (victim != int.MinValue && _saSources.Remove(victim, out src))
                _saLastSeen.Remove(victim);   // its last result stays in _results until it asks again
            else
                return IntPtr.Zero;
        }
        _saSources[entityId] = src;
        return src;
    }

    /// <summary>Who gets a place in the pool this tick: the first <paramref name="capacity"/> in line.
    /// The line is the order sources were asked about in, with whoever was turned away last tick put at
    /// its head (WorkerLoop), so nobody is turned away two ticks running.</summary>
    internal static void Serve(IEnumerable<int> line, int capacity, HashSet<int> served)
    {
        served.Clear();
        foreach (int id in line)
        {
            if (served.Count >= capacity) break;
            served.Add(id);
        }
    }

    /// <summary>The held source that has gone longest without a request and is not wanted this tick,
    /// or <see cref="int.MinValue"/> when every held source is wanted now.</summary>
    internal static int PickSourceToReclaim<TSrc, TReq>(IReadOnlyDictionary<int, TSrc> held,
        IReadOnlyDictionary<int, long> lastSeen, IReadOnlyDictionary<int, TReq> wantedThisTick)
        => PickSourceToReclaim(held, lastSeen, wantedThisTick.ContainsKey);

    /// <summary>The same, for the sources served this tick (RunSteamAudio).</summary>
    internal static int PickSourceToReclaim<TSrc>(IReadOnlyDictionary<int, TSrc> held,
        IReadOnlyDictionary<int, long> lastSeen, IReadOnlySet<int> servedThisTick)
        => PickSourceToReclaim(held, lastSeen, servedThisTick.Contains);

    private static int PickSourceToReclaim<TSrc>(IReadOnlyDictionary<int, TSrc> held,
        IReadOnlyDictionary<int, long> lastSeen, Func<int, bool> wantedThisTick)
    {
        int victim = int.MinValue;
        long oldest = long.MaxValue;
        foreach (var kv in held)
        {
            if (wantedThisTick(kv.Key)) continue;
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

        OpenFPS.Client.Core.AudioEngine.SteamAudio.TracedReverbSet.Dispose();
        if (_saSim != null) { _saSim.Dispose(); _saSim = null; }
        if (_saScene != null) { _saScene.Dispose(); _saScene = null; }
        foreach (var (retired, _) in _retiredScenes) retired.Dispose();
        _retiredScenes.Clear();
        if (_saListenerScene != null) { _saListenerScene.Dispose(); _saListenerScene = null; }
        _tileScenes?.Dispose(); _tileScenes = null;
        foreach (var (set, _) in _retiredTileSets) set.Dispose();
        _retiredTileSets.Clear();
        if (_saContext != IntPtr.Zero) Phonon.iplContextRelease(ref _saContext);

        _cts.Dispose();
    }
}

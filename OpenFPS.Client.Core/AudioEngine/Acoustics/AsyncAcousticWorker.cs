using System.Collections.Concurrent;
using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Client.Core.AudioEngine.SteamAudio;

namespace OpenFPS.Client.AudioEngine.Acoustics;

public struct AcousticRequest
{
    public int EntityId;
    public Vector3 ListenerPos;
    /// <summary>Where the sound comes out (<see cref="AudioEmission.PointFor"/>), not the entity's origin.</summary>
    public Vector3 SourcePos;
    /// <summary>The sphere probed round it: per source, by the room the emitter has above what it rests on
    /// (<see cref="AudioEmission.OcclusionRadiusFor"/>).</summary>
    public float SourceRadius;
}

/// <summary>
/// The acoustic paths of every voice, worked out off the game thread: Steam Audio's direct stage with the
/// barrier search, the routes through openings and image-source early reflections where the phonon library
/// is present, the hand-rolled tracer (SpatialAcoustics) where it is not or has no answer. Every Phonon
/// object lives on this worker's thread only, never on the mixer's.
/// </summary>
public class AsyncAcousticWorker : IDisposable
{
    private readonly SpatialAcoustics _acoustics;
    private readonly ConcurrentQueue<AcousticRequest> _requestQueue = new();
    private readonly ConcurrentDictionary<int, List<AcousticPathData>> _results = new();
    private readonly CancellationTokenSource _cts = new();
    private Thread? _workerThread;

    private WorldSnapshot? _latestWorld;
    private readonly object _worldLock = new();

    // ── Steam Audio ─────────────────────────────────────────────────────────────────────────────
    // Every Phonon object lives on this worker's thread only: made in WorkerLoop, freed in Dispose after
    // the thread joins, never touched from another thread or the mixer. OPENFPS_STEAMAUDIO_SIM=0 forces the
    // hand-rolled tracer.
    private static readonly bool _saDisabled = Environment.GetEnvironmentVariable("OPENFPS_STEAMAUDIO_SIM") == "0";
    private static readonly bool _saDebug = Environment.GetEnvironmentVariable("OPENFPS_AUDIO_DEBUG") == "1";
    private int _lastSceneBoxes;

    // OPENFPS_AUDIO_DEBUG=1: one line a second per source, and one summary. A line per source per tick
    // was over a thousand a second on the speedway, flooding the log and slowing the thread it measured.
    private const long SaDebugIntervalMs = 1000;
    private readonly Dictionary<int, long> _saDebugLastPrint = new();
    private long _saSummaryLastPrint;
    private const int SaMaxSources = 64;
    private const long SaSourceTtlMs = 5000; // a source not asked about for 5 s is released

    private bool _saTried;
    private bool _saEnabled;
    private IntPtr _saContext;
    private SteamAudioScene? _saScene;
    /// <summary>The same scene without its open ground, for the trace from the listener's head.</summary>
    private SteamAudioScene? _saListenerScene;
    private SteamAudioSimulator? _saSim;
    private AcousticMap? _saSceneMap; // the map the current scene was built for

    private volatile float _listenerReverbMs;  // 0 = no simulated reverb available yet
    private volatile float _listenerEnclosure; // 0..1, how closed-in the listener is; see Enclosure
    private volatile float _listenerHfDecayRatio = 1f;  // RT60(high)/RT60(mid)
    private volatile float _listenerLfDecayRatio = 1f;  // RT60(low)/RT60(mid)
    private int _reverbTick;
    private const int ReverbEveryNTicks = 6;

    /// <summary>Steam Audio's simulation is running; the client then makes none of the hand-rolled
    /// reflection emitters.</summary>
    public bool SteamAudioActive => _saEnabled;

    /// <summary>The probe graph is baked and pathing takes part in every tick. The lab waits on this: a
    /// probe asked before the bake measures a different engine from the game's.</summary>
    public bool PathingReady => _saSim?.PathingReady == true;

    /// <summary>The surveyed decay for the listener's room, ms, or false without one. Game thread.</summary>
    public bool TryGetListenerReverbDecayMs(out float ms)
    {
        ms = _listenerReverbMs;
        return _saEnabled && ms > 0f;
    }

    /// <summary>
    /// 0 (open field) to 1 (sealed box): the fraction of what leaves that comes back off geometry near
    /// enough to be a room. The decay says how long a tail lasts; this says whether there is one
    /// (<see cref="Enclosure"/>).
    /// </summary>
    public float ListenerEnclosure => _listenerEnclosure;

    /// <summary>The high band's decay over the middle's, and the low band's: how the listener's room colours
    /// its tail (1, even).</summary>
    public float ListenerHfDecayRatio => _listenerHfDecayRatio;
    public float ListenerLfDecayRatio => _listenerLfDecayRatio;
    private readonly Dictionary<int, IntPtr> _saSources = new();   // entity id -> IPLSource
    private readonly Dictionary<int, long> _saLastSeen = new();     // entity id -> TickCount64 of its last request
    private readonly Dictionary<int, AcousticRequest> _pending = new(); // the latest request per entity
    private List<int>? _evictScratch;

    public AsyncAcousticWorker(SpatialAcoustics acoustics)
    {
        _acoustics = acoustics;
    }

    /// <summary>
    /// For the emitter-stream replay (EmitterStreamReplayTests): no thread; requests are answered when the
    /// test calls <see cref="StepForTest"/>, so an answer lands on the same frame every run. Steam Audio is
    /// never started: the answers are the hand-rolled tracer's.
    /// </summary>
    internal bool Manual { get; init; }

    public void Start()
    {
        if (_workerThread != null || Manual) return;
        _workerThread = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "AcousticWorkerThread",
            // Below the game and well below the engine producers and the mixer: this thread's answers may
            // arrive late, a mixer block may not. At AboveNormal it fought the audio for cores at a map
            // load, and the audio cut out.
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

    /// <summary>Lab only (--pop-hunt): fills <see cref="Provenance"/>. Off in the game.</summary>
    public static bool TraceProvenance;
    /// <summary>What produced each source's last direct answer, when <see cref="TraceProvenance"/> is on.</summary>
    public readonly ConcurrentDictionary<int, string> Provenance = new();

    /// <summary>Each source's last Steam Audio answer and when, for a tick the pool had no room for it.</summary>
    private readonly Dictionary<int, (List<AcousticPathData> Paths, long At)> _lastSimPath = new();
    /// <summary>Well past a tick, well short of anything moving far.</summary>
    private const long HeldSimPathMs = 1000;
    /// <summary>Requests the pool had no room for this tick, asked first on the next.</summary>
    private readonly List<AcousticRequest> _carried = new();
    /// <summary>The sources given a place in the pool this tick: the first SaMaxSources in line.</summary>
    private readonly HashSet<int> _served = new();

    /// <summary>Stamps a result with where the source was when asked, so the consumer can tell a result for
    /// this sound from one left by a previous user of a pooled id.</summary>
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
    // A one-shot's voice id is new, so the simulator has never been asked about it when it starts. On the
    // hand-rolled tracer's guess, a walker outside a flat came through the brick wall at -4 dB where the
    // simulator said -24, for every step's attack ("I still hear people walking outside to my right"). The
    // previous step, 0.7 m back, has the real answer: an answer for a nearby point, whatever made it.
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

    /// <summary>Drops a removed entity's result; its Steam Audio source goes with the idle sweep.</summary>
    public void Forget(int entityId) => _results.TryRemove(entityId, out _);

    public WorldSnapshot? GetLastWorld()
    {
        lock (_worldLock) { return _latestWorld; }
    }

    private void WorkerLoop()
    {
        EnsureSteamAudio();
        Run(once: false);
    }

    /// <summary>One pass of the worker, on the caller's thread: everything asked since the last. Only
    /// for a worker made <see cref="Manual"/>.</summary>
    internal void StepForTest()
    {
        if (!Manual) throw new InvalidOperationException("StepForTest drives a Manual worker only.");
        _saTried = true;
        Run(once: true);
    }

    private void Run(bool once)
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            // Every queued request, the latest per entity, so the direct stage runs once a tick for all of
            // them. What the pool had no room for last tick is still first in _pending.
            bool any = false;
            AcousticRequest newest = default;
            while (_requestQueue.TryDequeue(out var req)) { _pending[req.EntityId] = req; newest = req; any = true; }
            if (!any && _pending.Count == 0) { if (once) return; Thread.Sleep(1); continue; }
            // A carried request is answered for where the listener is now.
            if (any && _carried.Count > 0)
                foreach (var c in _carried)
                    if (_pending.TryGetValue(c.EntityId, out var held) && held.ListenerPos != newest.ListenerPos)
                        _pending[c.EntityId] = held with { ListenerPos = newest.ListenerPos };

            WorldSnapshot? world;
            lock (_worldLock) { world = _latestWorld; }
            if (world == null) { _pending.Clear(); if (once) return; continue; }

            if (_saEnabled && _saSim != null)
            {
                // A source the simulator gave no answer (a failed tick, no scene yet, a full pool) falls back
                // to the hand-rolled tracer, never to DirectResult.Clear: "no result" heard as "nothing in
                // the way" makes every wall disappear, the worst answer in a game played by ear.
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
                        // No room in the pool this tick: it keeps its last answer and is asked first next
                        // tick. Not the hand-rolled tracer, a different model: it put a car behind a
                        // building at -15 dB where the simulator said -63, a pop every time the pool ran
                        // over (2026-10-03, --pop-hunt extra=40).
                        _carried.Add(req);
                        if (_lastSimPath.TryGetValue(req.EntityId, out var held)
                            && Environment.TickCount64 - held.At < HeldSimPathMs)
                        {
                            // Moved with the source: a bus at 15 m/s held for a second would be heard 15 m
                            // behind itself.
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
                        // Never answered by the simulator: the tracer stands in, and a source the pool
                        // refused is asked first next tick.
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
                if (once) return;
                continue;
            }
            else
            {
                // No simulator (no phonon library, or OPENFPS_STEAMAUDIO_SIM=0): the hand-rolled tracer.
                foreach (var kv in _pending)
                    Store(kv.Value, HandRolledPath(world, kv.Value));
            }

            _pending.Clear();
            if (once) return;
        }
    }

    /// <summary>
    /// The hand-rolled tracer's paths for one source: a worse model than the simulator's, but always better
    /// than "clear". If it throws, the source keeps its last result; only one that never had any gets a clear
    /// path, and that is logged.
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

    // ── Degradation reporting ────────────────────────────────────────────────────────────────────
    // Logged on the change (healthy to degraded and back), not every tick: readable, and never hiding that
    // the simulator stopped answering.
    private bool _simDegradedLogged;
    private long _lastDegradeLogTicks;
    private const long DegradeLogIntervalMs = 10_000;
    private readonly HashSet<int> _tracerFailuresReported = new();

    /// <summary>The simulator is on but some sources this tick came from the hand-rolled tracer.</summary>
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

    // ── Ray budget ───────────────────────────────────────────────────────────────────────────────
    // The simulator times its own runs (rays times bounces times sources); this reports them, loudly when a
    // run costs more than an audio frame: a worker that cannot keep up does not fail, it delivers older and
    // older acoustics.
    private long _lastRayBudgetReportTicks;
    private long _lastRayBudgetWarnTicks;
    private const long RayBudgetReportIntervalMs = 30_000;

    /// <summary>One audio frame at ClientAudioSystem's 60 Hz cap: what a run should stay under.</summary>
    private const double RayBudgetFrameMs = 1000.0 / 60.0;

    /// <summary>The last measured simulation cost, for display.</summary>
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

    /// <summary>Why a whole tick produced nothing: logged on a change of cause, then once per interval.</summary>
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

    /// <summary>What the simulator says about one source this tick.</summary>
    /// <param name="Direct">The direct stage's occlusion and transmission.</param>
    /// <param name="ApparentPosition">Where the sound is heard from when it arrives round an edge or through
    /// an opening (world space).</param>
    /// <param name="HasApparent">Whether <paramref name="ApparentPosition"/> is set.</param>
    /// <param name="BarrierDelta">How far out of its way sound bent past the worst thing in the line, metres
    /// (negative: nothing in the way); measured once, since it decides both the bearing and the level.</param>
    /// <param name="BarrierVerified">The route round the barrier box is clear of everything else; when not,
    /// the route does not exist and its level must not be used (BuildSimPath).</param>
    /// <param name="Path">Steam Audio's own route through the scene, when it found one.</param>
    /// <param name="Route">What came by the openings (OpeningRoutes), when source and listener are in
    /// different places and a route joins them.</param>
    private readonly record struct SaResult(SteamAudioSimulator.DirectResult Direct, Vector3 ApparentPosition,
                                            bool HasApparent, float BarrierDelta,
                                            bool BarrierVerified = true,
                                            SteamAudioSimulator.PathResult Path = default,
                                            OpeningRoutes.Answer? Route = null);

    // Below this visibility a source is heard from where it arrives (an edge, an opening), not through the wall.
    private const float PathRedirectVisibility = 0.5f;

    /// <summary>The direct and pathing stages for every pending source, or null with no simulator.</summary>
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

            // A finished bake joins here, before anything is staged, between runs, on the thread that owns
            // the simulator (single-threaded by contract). Committed after staging, it crashed the client
            // natively a second after the cars started (docs/AUDIO_GHOSTS_AND_STUTTERS.md, the probe batch).
            if (_saSim.CommitPendingProbes())
                Console.WriteLine("[AcousticWorker] Pathing probes are live; occluded sources can now be localized to the opening they arrive through.");

            long now = Environment.TickCount64;
            Vector3 listener = default;
            bool haveListener = false;
            int blocked = 0, viaEdge = 0, viaProbe = 0, reflections = 0;
            // More asked about than the pool holds: the first SaMaxSources in line are served, the line
            // starting with last tick's turned-away, and a source held by someone further back is lent.
            // With every source keeping its own, the same sixteen of eighty were turned away for good and
            // popped between -63 and -15 dB through the hand-rolled tracer.
            Serve(_pending.Keys, SaMaxSources, _served);
            foreach (var kv in _pending)
            {
                // The listener from the first request, served or not: else a full pool drops the whole tick.
                if (!haveListener) { listener = kv.Value.ListenerPos; haveListener = true; _lastListenerPos = listener; _haveLastListener = true; }

                if (!_served.Contains(kv.Key)) continue;   // asked first next tick (WorkerLoop)
                IntPtr src = GetOrAcquireSource(kv.Key);
                if (src == IntPtr.Zero) continue;
                float radius = kv.Value.SourceRadius > 0f ? kv.Value.SourceRadius : AudioEmission.DefaultOcclusionRadius;
                _saSim.SetSourceInputs(src, kv.Value.SourcePos, radius);
                _saLastSeen[kv.Key] = now;
            }
            if (!haveListener) return null;   // nothing pending; not a degradation
            // The traced reverb runs its own trace on its own thread; the region tells it when you have gone
            // into another room, so its averaged tail starts again.
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

                // Over the thing or round it: the routes compete for the bearing on the same terms as for
                // the level (BuildSimPath), whichever delivers more energy. Decided on visibility alone, a
                // near car on the speedway was heard to stop: the bearing snapped to the 7.3 m probe grid.
                // See docs/CLIENT_NOTES.md, "The bearing follows the level".
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
                        // The edge is the secondary source, placed on its bearing at the real source's
                        // distance: the level has paid for the detour already.
                        Vector3 toEdge = edge - kv.Value.ListenerPos;
                        if (toEdge.LengthSquared() > 1e-6f)
                        {
                            apparent = kv.Value.ListenerPos + Vector3.Normalize(toEdge) * dist;
                            hasApparent = true; viaEdge++;
                        }
                    }
                    // No verified route: the level is what comes through the wall, so the bearing is the
                    // source's own. The probe graph's direction here put a siren behind a ground-floor
                    // flat's wall straight below, where turning the head changes nothing.
                }
                // And by the openings, a route with corners in it (the street to a corridor by way of the
                // front door): without it an open front door changed nothing ("only when a loud source
                // passes does it come inside"). BuildSimPath lets it compete band by band.
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

    /// <summary>One source's paths from the simulator's answer: Steam Audio's visibility turned into the
    /// engine's "fraction blocked" and band gains, the better of the routes over an edge and by the openings,
    /// air, and the early reflections (<see cref="AddEarlyReflections"/>).</summary>
    private List<AcousticPathData> BuildSimPath(WorldSnapshot world, AcousticRequest req, SaResult sr)
    {
        var ap = SteamAudioSimulator.ToAcousticParams(sr.Direct);
        string? trace = TraceProvenance
            ? $"sim vis {sr.Direct.Visibility:F2} through {Db(ap.EqLow):F0}/{Db(ap.EqMid):F0}/{Db(ap.EqHigh):F0}" : null;
        if (_saDebug) Console.WriteLine($"[SAPATH] e{req.EntityId} vis {sr.Direct.Visibility:F3} trans {sr.Direct.TransLow:F3}/{sr.Direct.TransMid:F3}/{sr.Direct.TransHigh:F3} barrierDelta {sr.BarrierDelta:F3} verified {sr.BarrierVerified} route {(sr.Path.Found ? $"{sr.Path.EqLow:F3}/{sr.Path.EqMid:F3}/{sr.Path.EqHigh:F3}" : "none")}");
        float occ = Math.Clamp(ap.Occlusion, 0f, AcousticConstants.OcclusionCap);
        Vector3 apparent = sr.HasApparent ? sr.ApparentPosition : req.SourcePos;
        float dist = Vector3.Distance(req.ListenerPos, req.SourcePos);

        // What gets past the thing in the way. The direct stage has no edge diffraction: taken as the
        // answer, a 0.9 m pit wall silenced a car twenty metres behind it (26 dB, the top three octaves
        // gone). So what arrives is the better of through it and round it, the second by the detour.
        if (occ > 0f)
        {
            // Measured once, in RunSteamAudio, where the same number decides the bearing: two measurements
            // could have the level "over the wall" and the bearing "through a door".
            float delta = sr.BarrierDelta;
            // Only a route that exists: round a shut door's edge is eight centimetres and straight into the
            // wall it hangs in, and would pass -7/-11/-19 dB (--path-probe). Not Steam Audio's pathing eq
            // either: that is the bend's colour, about 1.0 for a route 150 m long, not its loss.
            if (delta >= 0f && !sr.BarrierVerified) delta = -1f;
            if (delta >= 0f)
            {
                var (dLow, dMid, dHigh) = Diffraction.BandGains(delta, AudioPhysics.CurrentSpeedOfSound);
                // The longer route's spreading, which the barrier loss stops paying at its 24 dB ceiling: a
                // walker 8 m through a brick wall and 164 m round the building (Marlow flat 01F) would beat
                // the wall's -24/-30/-36 and be heard through it. 26 dB there; under half a dB for a kerb.
                float spread = MathF.Max(0.5f, dist) / (MathF.Max(0.5f, dist) + delta);
                dLow *= spread; dMid *= spread; dHigh *= spread;
                // Per band, the better of what the material lets through and what came round the edge.
                ap = new SteamAudioSimulator.AcousticParams(
                    ap.Occlusion,
                    MathF.Max(ap.EqLow, dLow),
                    MathF.Max(ap.EqMid, dMid),
                    MathF.Max(ap.EqHigh, dHigh),
                    ap.Bleed);
                // A source arriving round an edge is quieter, not absent.
                float throughput = MathF.Max(dLow, MathF.Max(dMid, dHigh));
                occ = Math.Clamp(MathF.Min(occ, 1f - throughput), 0f, AcousticConstants.OcclusionCap);
                if (trace != null) trace += $"; over an edge {delta:F1} m {Db(dLow):F0}/{Db(dMid):F0}/{Db(dHigh):F0}";
            }
            else if (trace != null && sr.BarrierDelta >= 0f) trace += $"; edge {sr.BarrierDelta:F1} m not verified";
        }

        // By the openings, the same rule and graph as SpatialAcoustics.CalculateMainPath: per band the
        // better way, and heard from the last opening, at the source's own distance, when it delivers more.
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
        if (world.AcousticMap != null)
        {
            try { region = _acoustics.GetRegionAt(world, req.SourcePos); }
            catch { region = -1; }
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
            // The band gains carry the barrier already; the aperture low-pass is for a small opening.
            ApertureFactor = 1f,
            RoomGain = 1f,
            RegionId = region,
            IsReflection = false,
        };
        // The air, per band (ISO 9613-1). Once zero here, and a shot two streets away arrived quiet but
        // bright, which reads as small and near.
        (path.AirLowDb, path.AirMidDb, path.AirHighDb) = AudioPhysics.AirLossDb(
            dist, world.Humidity, world.Temperature, world.AirPressure, world.AirAbsorptionMultiplier);

        if (trace != null) Provenance[req.EntityId] = trace;
        var paths = new List<AcousticPathData>(1 + EarlyReflections.MaxArrivals) { path };
        AddEarlyReflections(paths, world, req, region);
        return paths;
    }

    /// <summary>
    /// The copies of this source the surfaces send back, as first-order image sources from the scene's
    /// boxes: the loud, early, directional part. The simulator's parametric reflections give a decay time
    /// and no directions, a blanket in which a doorway cannot be heard from outside. The copies of copies
    /// are the reverb's, its level from how enclosed the place is (Enclosure).
    /// </summary>
    private void AddEarlyReflections(List<AcousticPathData> into, WorldSnapshot world,
                                     AcousticRequest req, int region)
    {
        var geometry = _enclosureWorld;
        var solids = geometry != null && OpenFPS.Common.Geometry.TriangleGeometry.Enabled ? null : ReflectionSolids();
        if (solids != null && solids.Count == 0) return;

        _reflectionScratch ??= new List<EarlyReflections.Arrival>();
        // First order only, for a sound that goes on: at third order a jet's and a bus's hiss became
        // copies of themselves standing in the distance, cutting in and out, and a far siren's image
        // stood in front of you. Copies of copies belong to one-off sounds (WorldAudioPlayer).
        if (solids != null) EarlyReflections.Find(req.SourcePos, req.ListenerPos, solids, _reflectionScratch, AudioPhysics.CurrentSpeedOfSound);
        else EarlyReflections.Find(req.SourcePos, req.ListenerPos, geometry!, _reflectionScratch, AudioPhysics.CurrentSpeedOfSound);

        _lastReflectionCount = 0;
        for (int i = 0; i < _reflectionScratch.Count; i++)
        {
            var a = _reflectionScratch[i];

            // Only an arrival the ear hears apart gets a voice: a second voice of a sustained sound is a
            // second copy (a megaphone's announcement heard twice), and inside the fusion window the ear
            // hears one wider event. Its energy is the room's tail, which the survey measured.
            if (!EarlyReflections.IsSeparateEvent(a)) continue;

            var reflected = new AcousticPathData
            {
                IsReflection = true,
                // The surface's identity: a wall keeps one voice as the listener moves.
                ReflectionId = a.SurfaceId,
                ApparentPosition = a.ImagePosition,
                EffectiveDistance = a.PathLength,
                ReflectionDelayMs = a.ExtraDelaySeconds * 1000f,
                // Found means it got here: what it lost is per band, by the surface and the extra distance.
                Occlusion = 0f,
                EqLow = a.GainLow,
                EqMid = a.GainMid,
                EqHigh = a.GainHigh,
                MaterialAbsorption = 1f - a.GainMid,
                Scattering = a.Scattering,
                // A rough surface returns a wider copy: the arrival's angular width.
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

    /// <summary>The last source's arrivals, for the summary line.</summary>
    private int _lastReflectionCount;

    /// <summary>The scene's boxes for the reflection model, rebuilt only with the scene: this runs per
    /// source per tick.</summary>
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

    /// <summary>The current scene's boxes, for the barrier search. Replaced whole on a rebuild and only
    /// read afterwards, so no lock.</summary>
    private List<OpenFPS.Client.Core.AudioEngine.SteamAudio.SteamAudioScene.Box> _barrierBoxes = new();

    // ── Routes by the openings, and what they cost ────────────────────────────────────────────
    //
    // The graph is built with each scene, door leaves where the scene has them, and handed to
    // SpatialAcoustics so every voice asks the same one. Dozens of sources a tick: an answer is kept
    // while neither end has moved enough to change it.
    private volatile OpeningRoutes? _routes;
    private readonly RouteAnswers _routeCache = new();
    /// <summary>Metres either end may move before the route is asked again: well under a doorway's width,
    /// so the crossing cannot be a different opening.</summary>
    private const float RouteReuseMetres = 0.25f;
    private const long RouteReuseMs = 500;
    /// <summary>
    /// Milliseconds of a tick route queries may take (a new query can cost a millisecond or more in the
    /// city). Past it, a source with an answer keeps it this tick and only one with none is asked: the
    /// answer comes a tick later.
    /// </summary>
    private const double RouteBudgetMs = 4.0;
    /// <summary>The oldest answer the budget may stand on, ms.</summary>
    private const long RouteHeldMaxMs = 2000;
    private long _routeTicksThisTick;
    private long _routeQueries, _routeTicks, _routeReused;
    private long _lastRouteReport;

    /// <summary>How many route queries, and the mean per query.</summary>
    public string RouteCostSummary =>
        _routeQueries == 0 ? "no route queries yet"
        : $"{_routeQueries} route queries, {_routeTicks * 1e6 / System.Diagnostics.Stopwatch.Frequency / _routeQueries:F0} µs each, {_routeReused} reused";

    private static float Db(float gain) => 20f * MathF.Log10(MathF.Max(1e-5f, gain));

    private OpeningRoutes.Answer? AskRoutes(OpeningRoutes routes, WorldSnapshot world, int id, Vector3 source, Vector3 listener, int listenerRegion)
    {
        long now = Environment.TickCount64;
        if (_routeCache.TryGet(id, routes, out var held))
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
        _routeCache.Put(id, new RouteAnswers.Held(routes, source, listener, answer, now));
        _routeCache.Trim(now, RouteReuseMs, 1024);
        if ((_saDebug || PerfProbe.Enabled) && now - _lastRouteReport > 30_000)
        {
            _lastRouteReport = now;
            Console.WriteLine($"[AcousticWorker] routes: {RouteCostSummary}; {routes.Openings.Count} openings.");
        }
        return answer;
    }

    /// <summary>
    /// The routes through openings for a scene: with the acoustic triangle store (<paramref name="geometry"/>)
    /// built tile by tile, each tile and opening kept in <paramref name="cache"/> while nothing round it
    /// changed. The openings' sides are checked against the places only when <paramref name="report"/>: the
    /// check only ever wrote the report.
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
        // Answers about the old graph are no use, and each holds that graph and its scene.
        _routeCache.Published(model);
    }

    private void EnsureSteamAudio()
    {
        if (_saTried) return;
        _saTried = true;
        if (_saDisabled) { Console.WriteLine("[AcousticWorker] Steam Audio sim disabled by OPENFPS_STEAMAUDIO_SIM=0; using the hand-rolled ray-tracer for occlusion, portals and reverb."); return; }
        try
        {
            // Idempotent. Uninitialised, every material lookup in the scene build throws and the simulator
            // falls back to no occlusion.
            AcousticRegistry.Initialize();

            var cs = Phonon.DefaultContextSettings();
            string simd = Phonon.SimdLevelName(cs.simdLevel);
            if (Phonon.iplContextCreate(ref cs, out _saContext) != Phonon.IPL_STATUS_SUCCESS)
            { _saContext = IntPtr.Zero; Console.WriteLine($"[AcousticWorker] DEGRADED: Steam Audio context create failed (SIMD {simd}); using the hand-rolled ray-tracer."); return; }

            // Embree, where it starts: a sub-scene per tile and per door leaf (TileSceneSet), so a tile
            // arriving or a door swinging rebuilds none of the rest. First, since the simulators are made
            // for the context's scene type. OPENFPS_EMBREE=0 keeps the default tracer and whole rebuilds;
            // OPENFPS_TILE_SCENES=0 keeps Embree with whole scenes.
            _embree = Environment.GetEnvironmentVariable("OPENFPS_EMBREE") != "0"
                      && OpenFPS.Client.Core.AudioEngine.SteamAudio.SteamAudioScene.UseEmbree(_saContext);
            Console.WriteLine(_embree
                ? "[AcousticWorker] Steam Audio scenes use Embree: a sub-scene per tile and per door leaf."
                : "[AcousticWorker] Embree did not start here; Steam Audio scenes use the default tracer and are rebuilt whole.");
            // Pathing off: its probe grid is too coarse on a city (tens of metres) to say where a sound comes
            // from, nothing reads its answer, and the bake cost a core for minutes at every map load. Routes
            // round obstacles come from the barrier search (BarrierPathDifference).
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

    // ── Doors are part of the geometry, where they are now ────────────────────────────────────────
    //
    // When a leaf near the listener has moved, the scene is rebuilt with it there, at most every
    // DoorRebuildSeconds while it swings and once more when it settles; Steam Audio's reference counting
    // keeps the old scene alive until every simulator has let go. Built once per map, an open door stayed
    // a wall to occlusion, diffraction and the traces.
    private long _lastDoorRebuildTicks;
    private const double DoorRebuildSeconds = 0.3;
    /// <summary>Only doors this near the listener count: on the city walkers open doors all day.</summary>
    private const float DoorNearMetres = 50f;
    private Vector3 _lastListenerPos;
    private bool _haveLastListener;

    /// <summary>Where each door leaf stood when the scene in use was built, by entity.</summary>
    private readonly Dictionary<int, long> _builtDoorPoses = new();

    /// <summary>See <see cref="OpeningGraph.IsDoorLeaf"/>; telling a door by its sides differing missed
    /// every door on the city.</summary>
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

    /// <summary>A leaf near the listener stands somewhere other than the scene in use has it. Only a leaf
    /// that moved: a hash of the doors within 50 m changed whenever one crossed the radius as you walked.
    /// A far leaf that moved waits until you come near.</summary>
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

    // A door's rebuild is made off this thread and swapped in when ready: the city's two scenes take
    // about 120 ms (--scene-cost), and occlusion would stand still that long, several times a swing.
    // Replaced scenes are released a few seconds later, once every simulator has the new one.
    private System.Threading.Tasks.Task<(SteamAudioScene Full, SteamAudioScene Listener, List<SteamAudioScene.Box> Boxes, AcousticMap? Map, OpeningRoutes Routes, OpenFPS.Common.Geometry.TriangleWorld? Geometry)>? _doorBuild;
    private readonly List<(SteamAudioScene Scene, long At)> _retiredScenes = new();

    // ── Tiles arriving and leaving ─────────────────────────────────────────────────────────────
    //
    // On a streamed map the acoustic map object stays while its contents follow the player, and each
    // refresh bumps GeometryVersion (ClientWorldState.RefreshAcousticsNow). A new version rebuilds the
    // scene as a door does, on a niced thread of its own; sources keep their last answers meanwhile.
    private long _builtGeometryVersion;
    private bool _buildIsForTiles;
    /// <summary>Embree started on this context: scenes are assembled from tiles (TileSceneSet).</summary>
    private bool _embree;
    /// <summary>This map's sub-scenes. Replaced on a new map; the old set is let go once no build uses it.</summary>
    private TileSceneSet? _tileScenes;
    /// <summary>Without a tile set, this map's acoustic triangle store: a new one each map, since a build
    /// for the last may still use the old.</summary>
    private AcousticGeometry? _acousticStore;
    /// <summary>The acoustic scene as triangles, swapped in with <see cref="_barrierBoxes"/>: what the
    /// enclosure survey casts against.</summary>
    private OpenFPS.Common.Geometry.TriangleWorld? _enclosureWorld;
    /// <summary>This map's routes kept tile by tile: one build at a time uses it.</summary>
    private OpeningRoutes.TileCache? _routeTiles;
    private double _lastScenesMs, _lastRoutesMs;
    private object? _routesPortals;
    private long _routesBuiltTicks;
    /// <summary>The longest the routes go unmade while only walls and roads change, seconds.</summary>
    private const double RoutesEverySeconds = 3.0;
    private readonly List<(TileSceneSet Set, long At)> _retiredTileSets = new();

    /// <summary>Earlier maps' tile sets go once no build uses one, no tracer is being handed over, and five
    /// seconds have passed.</summary>
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
    private static (SteamAudioScene Full, SteamAudioScene Listener) ScenesFor(IntPtr ctx, TileSceneSet? set,
                                                                             List<SteamAudioScene.Box> boxes, ISet<int>? leaves,
                                                                             List<OpenFPS.Common.Geometry.SolidSpec> terrains)
    {
        if (set != null)
        {
            set.Update(boxes, leaves, terrains);
            return set.Assemble();
        }
        var full = new SteamAudioScene(ctx);
        full.Build(boxes, terrains);
        var listener = new SteamAudioScene(ctx);
        listener.Build(SteamAudioScene.WithoutOpenGround(boxes, new GroundHeights(terrains)));
        return (full, listener);
    }

    /// <summary>
    /// The acoustic scene as triangles (docs/GEOMETRY.md stage 1), what the enclosure survey casts against:
    /// the tile set's store, or without Embree a store of the map's own brought up to the same boxes.
    /// </summary>
    private static OpenFPS.Common.Geometry.TriangleWorld? GeometryFor(TileSceneSet? set, AcousticGeometry? store,
                                                                      List<SteamAudioScene.Box> boxes, ISet<int>? leaves,
                                                                      List<OpenFPS.Common.Geometry.SolidSpec> terrains)
    {
        if (!OpenFPS.Common.Geometry.TriangleGeometry.Enabled) return null;
        if (set != null) return set.Geometry;
        return store?.Update(boxes, leaves, terrains);
    }
    private long _buildStartedTicks;

    /// <summary>Tile rebuilds swapped in. Diagnostic.</summary>
    public int TileSceneBuilds { get; private set; }
    /// <summary>Background rebuilds (doors and tiles) swapped in so far. Diagnostic.</summary>
    public int SceneSwaps { get; private set; }
    /// <summary>The tile scenes' state, or null without them. Diagnostic: read while no build runs
    /// (<see cref="SceneBuildPending"/>).</summary>
    public string? TileScenesState
    {
        get
        {
            try { return _tileScenes?.Describe(); }
            catch (InvalidOperationException) { return "a build is changing them"; }
        }
    }
    /// <summary>A background rebuild (a door, tiles) is being made. Diagnostic.</summary>
    public bool SceneBuildPending => _doorBuild != null;
    /// <summary>How long the last tile rebuild took, ms.</summary>
    public double LastTileSceneBuildMs { get; private set; }
    /// <summary>Every background build so far (tiles and doors), ms of wall time in all.</summary>
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
            full.Dispose(); listener.Dispose();        // the map changed meanwhile
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
        _acoustics.PublishReflectionWorld(geometry, map);
        _lastSceneBoxes = boxes.Count;
        PublishRoutes(routes);
        SceneSwaps++;
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

    /// <summary>A new scene for a new map at once; for a door or tiles, a background build.</summary>
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
            var doorTerrains = world.TerrainSolids();
            var ctx = _saContext; var forMap = _saSceneMap; var set = _tileScenes;
            // The routes are made again when the openings changed, otherwise at most every few seconds: the
            // coarse ring moving as you drive changes their far barriers, not their doorways, and making
            // them is most of a tile's cost.
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
                var (full, listener) = ScenesFor(ctx, set, doorBoxes, leaves, doorTerrains);
                var geometry = GeometryFor(set, store, doorBoxes, leaves, doorTerrains);
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
        var terrains = world.TerrainSolids();
        var mapLeaves = OpeningGraph.LeavesOf(world);
        _lastSceneBoxes = boxes.Count;
        // A new map gets new scene objects; the old are retired, not rebuilt in place: the tracers and
        // the bake may still run on them, and freeing a native scene under a running trace is a crash.
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
            if (_tileScenes != null) _retiredTileSets.Add((_tileScenes, DateTime.UtcNow.Ticks));
            // On unless OPENFPS_TILE_SCENES=0. Off for a day (2026-10-06): the second pair of scenes, handed
            // over at the first door swing, traced as empty and every wall stopped occluding (Cody: traffic
            // heard inside Selby House). TileSceneSet's remarks say why; they now answer as the whole-scene
            // build does through any number of swings and tile changes (AudioLab --path-probe door=
            // swings=, --stream-walk stops=).
            _tileScenes = Environment.GetEnvironmentVariable("OPENFPS_TILE_SCENES") == "0" ? null : new TileSceneSet(_saContext, world.TileMetres);
            _acousticStore = _tileScenes == null ? new AcousticGeometry(world.TileMetres) : null;
            var (assembledFull, assembledListener) = ScenesFor(_saContext, _tileScenes, boxes, mapLeaves, terrains);
            _saScene.Dispose();
            _saScene = assembledFull;
            _saListenerScene?.Dispose();
            _saListenerScene = assembledListener;
            if (_tileScenes != null)
            {
                Console.WriteLine($"[AcousticWorker] {_tileScenes.TileCount} tile sub-scene(s) built in {_tileScenes.LastUpdateMs:F0} ms, assembled in {_tileScenes.LastAssembleMs:F1} ms.");
                var layers = _tileScenes.LayerPlan;
                Console.WriteLine($"[AcousticWorker] {layers.Constructions} construction(s) of layers in contact, {layers.Members.Count} layer(s), "
                                + $"{layers.Faces} face(s) ({layers.Milliseconds:F0} ms).");
            }
        }
        else
        {
            _acousticStore = new AcousticGeometry(world.TileMetres);
            _saScene.Build(boxes, terrains);
        }
        _enclosureWorld = GeometryFor(_tileScenes, _acousticStore, boxes, mapLeaves, terrains);
        _acoustics.PublishReflectionWorld(_enclosureWorld, world.AcousticMap);
        _routeTiles = new OpeningRoutes.TileCache();
        _saSceneMap = world.AcousticMap;
        if (_saScene.IsBuilt)
        {
            _saSim.SetScene(_saScene);
            // The places traced (TracedReverbSet): from where the listener stands, and from the middle of
            // each other room heard. The listener's trace has no open ground (SteamAudioScene.WithoutOpenGround).
            if (!_embree)
            {
                _saListenerScene ??= new SteamAudioScene(_saContext);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var withoutGround = SteamAudioScene.WithoutOpenGround(boxes, new GroundHeights(terrains));
                _saListenerScene.Build(withoutGround);
                if (mapChanged)
                    Console.WriteLine($"[AcousticWorker] Listener trace scene: {boxes.Count - withoutGround.Count} open-ground slab(s) left out "
                                    + $"of {boxes.Count} ({sw.ElapsedMilliseconds} ms).");
            }
            OpenFPS.Client.Core.AudioEngine.SteamAudio.TracedReverbSet.Configure(_saContext, _saScene,
                _saListenerScene is { IsBuilt: true } ? _saListenerScene : null);
            // Off the worker thread: inside SetScene the bake took a hundred seconds of a core before any
            // source had an occlusion value, the world unoccluded until then.
            if (mapChanged) _saSim.BeginProbeBake(_saScene);
        }
        // The scene's own boxes, so diffraction and occlusion cannot disagree about what is there.
        _barrierBoxes = boxes;
        PublishRoutes(BuildRoutes(world, boxes, _enclosureWorld, _routeTiles, report: mapChanged));
        Console.WriteLine($"[AcousticWorker] Built Steam Audio scene from {boxes.Count} solid box colliders ({built.ElapsedMilliseconds} ms).");
    }

    /// <summary>
    /// What the room round the listener does to sound, every few ticks, from the enclosure survey's sphere
    /// of rays: its mean free path and absorption per band make the decay, so a carpeted half of a hall
    /// answers dull and short and the concrete half bright and long. Steam Audio's reflection stage, which
    /// this replaced, fitted a roofless box a longer tail than a roofed one (docs/COMMON_NOTES.md,
    /// "Reverberation").
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
        _listenerReverbMs = Math.Clamp(mid * 1000f, AcousticConstants.MinReverbDecayMs,
                                                    AcousticConstants.MaxReverbDecayMs);
        // The tilt is the audible half of a material: carpet takes the high band four times harder than
        // the low and sounds like cloth; concrete barely tilts and rings.
        _listenerHfDecayRatio = Math.Clamp(high / MathF.Max(0.01f, mid), 0.1f, 2.0f);
        _listenerLfDecayRatio = Math.Clamp(low / MathF.Max(0.01f, mid), 0.1f, 4.0f);
    }

    /// <summary>The scene's boxes for the enclosure measure, rebuilt only with the scene.</summary>
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
            // A full pool lends out a source nobody asks about this tick: inputs are staged fresh every run,
            // so nothing is lost. First come, first served, the last sound to start (a door, a footstep) was
            // refused and heard through the hand-rolled tracer, the city's pool past 64 within five seconds.
            int victim = PickSourceToReclaim(_saSources, _saLastSeen, _served);
            if (victim != int.MinValue && _saSources.Remove(victim, out src))
                _saLastSeen.Remove(victim);   // its last result stays in _results until it asks again
            else
                return IntPtr.Zero;
        }
        _saSources[entityId] = src;
        return src;
    }

    /// <summary>The first <paramref name="capacity"/> in line, the line headed by last tick's turned-away,
    /// so nobody is turned away two ticks running.</summary>
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
            _routeCache.Forget(id);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _workerThread?.Join(); // after this, nothing else touches the Phonon objects

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

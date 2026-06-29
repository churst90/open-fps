using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Client.Core.AudioEngine.SteamAudio;

namespace OpenFPS.Client.AudioEngine.Acoustics;

public struct AcousticRequest
{
    public int EntityId;
    public Vector3 ListenerPos;
    public Vector3 SourcePos;
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
    private const int SaMaxSources = 64;
    private const long SaSourceTtlMs = 5000; // release a source whose voice hasn't been requested in 5s

    private bool _saTried;
    private bool _saEnabled;
    private IntPtr _saContext;
    private SteamAudioScene? _saScene;
    private SteamAudioSimulator? _saSim;
    private AcousticMap? _saSceneMap; // the map the current scene was built for (rebuild when it changes)

    // Phase 4d: a reflections-only simulator with a single listener probe, run on a throttle, that yields
    // geometry-driven reverb decay (RT60) for the room the listener is in. Cheap (one source, every Nth
    // tick) so reflections — the heaviest stage — don't run per-source per-tick.
    private SteamAudioSimulator? _saReverbSim;
    private IntPtr _reverbSource;
    private volatile float _listenerReverbMs;  // 0 = no simulated reverb available yet
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
            Priority = ThreadPriority.AboveNormal
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

    public bool TryGetResult(int entityId, out List<AcousticPathData> paths)
    {
        return _results.TryGetValue(entityId, out paths!);
    }

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
                // Steam Audio is the ACTIVE spatializer: build the acoustic result purely from the
                // simulator (occlusion/transmission + pathing arrival direction). The hand-rolled
                // ray-tracer is NOT run on this path.
                Dictionary<int, SaResult>? sim = RunSteamAudio(world);
                foreach (var kv in _pending)
                {
                    var req = kv.Value;
                    SaResult sr = (sim != null && sim.TryGetValue(req.EntityId, out var r))
                        ? r
                        : new SaResult(SteamAudioSimulator.DirectResult.Clear, default, false);
                    _results[req.EntityId] = BuildSimPath(world, req, sr);
                }
            }
            else
            {
                // Legacy hand-rolled spatializer — only the fallback when Steam Audio sim is unavailable
                // or disabled (OPENFPS_STEAMAUDIO_SIM=0 / no libphonon).
                foreach (var kv in _pending)
                {
                    var req = kv.Value;
                    try
                    {
                        _results[req.EntityId] = _acoustics.CalculateAcousticPaths(world, req.EntityId, req.ListenerPos, req.SourcePos, req.IsImportant);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[AcousticWorker] Error processing acoustic path for {req.EntityId}: {ex.Message}");
                    }
                }
            }

            _pending.Clear();
        }
    }

    /// <summary>Per-entity Steam Audio result for one tick: the direct occlusion/transmission, and (when the
    /// source is occluded and pathing found a route) the world-space apparent position to localize the HRTF
    /// to the opening the sound arrives through.</summary>
    private readonly record struct SaResult(SteamAudioSimulator.DirectResult Direct, Vector3 ApparentPosition, bool HasApparent);

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
            if (_saScene == null || !_saScene.IsBuilt) return null;

            long now = Environment.TickCount64;
            Vector3 listener = default;
            bool haveListener = false;
            foreach (var kv in _pending)
            {
                IntPtr src = GetOrAcquireSource(kv.Key);
                if (src == IntPtr.Zero) continue; // pool exhausted -> that voice stays hand-rolled this tick
                _saSim.SetSourceInputs(src, kv.Value.SourcePos);
                _saLastSeen[kv.Key] = now;
                listener = kv.Value.ListenerPos;
                haveListener = true;
            }
            if (!haveListener) return null;

            _saSim.SetListener(listener);
            _saSim.Run();

            var results = new Dictionary<int, SaResult>(_pending.Count);
            foreach (var kv in _pending)
            {
                if (!_saSources.TryGetValue(kv.Key, out var src) || src == IntPtr.Zero) continue;
                var direct = _saSim.GetResult(src);

                if (_saDebug && kv.Key >= 0)
                {
                    var sp = kv.Value.SourcePos; var lp = kv.Value.ListenerPos;
                    Console.WriteLine($"[SAWORKER] e{kv.Key} vis={direct.Visibility:F2} src=({sp.X:F1},{sp.Y:F1},{sp.Z:F1}) reqLis=({lp.X:F1},{lp.Y:F1},{lp.Z:F1}) runLis=({listener.X:F1},{listener.Y:F1},{listener.Z:F1}) sceneBoxes={_lastSceneBoxes}");
                }

                Vector3 apparent = default;
                bool hasApparent = false;
                if (direct.Visibility < PathRedirectVisibility)
                {
                    var path = _saSim.GetPathing(src);
                    if (path.Found)
                    {
                        // Localize the HRTF to the opening: place the apparent source along the arrival
                        // direction, at the real source's distance.
                        float dist = Vector3.Distance(kv.Value.ListenerPos, kv.Value.SourcePos);
                        apparent = kv.Value.ListenerPos + path.WorldDirection * dist;
                        hasApparent = true;
                    }
                }
                results[kv.Key] = new SaResult(direct, apparent, hasApparent);
            }

            RunListenerReverb(listener);
            EvictStaleSources(now);
            return results;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AcousticWorker] Steam Audio sim tick failed; falling back to hand-rolled: {ex.Message}");
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
            ApertureFactor = 1f,   // sim owns occlusion EQ; bypass the diffraction LPF
            RoomGain = 1f,
            AirAbsorption = 0f,    // distance/humidity air-absorption is a later phenomena pass
            RegionId = region,
            IsReflection = false,
        };
        return new List<AcousticPathData> { path };
    }

    private void EnsureSteamAudio()
    {
        if (_saTried) return;
        _saTried = true;
        if (_saDisabled) { Console.WriteLine("[AcousticWorker] Steam Audio sim disabled (OPENFPS_STEAMAUDIO_SIM=0); using hand-rolled occlusion."); return; }
        try
        {
            // Idempotent: ensure acoustic materials exist before the scene build queries them. Without this
            // an uninitialized registry makes every scene-material lookup throw, and the sim silently falls
            // back to no-occlusion. The clients already call this, but the SA path shouldn't depend on it.
            AcousticRegistry.Initialize();

            var cs = new Phonon.IPLContextSettings { version = Phonon.STEAMAUDIO_VERSION, simdLevel = Phonon.IPL_SIMDLEVEL_AVX2, flags = 0 };
            if (Phonon.iplContextCreate(ref cs, out _saContext) != Phonon.IPL_STATUS_SUCCESS)
            { _saContext = IntPtr.Zero; Console.WriteLine("[AcousticWorker] Steam Audio context create failed; using hand-rolled occlusion."); return; }

            _saSim = new SteamAudioSimulator(_saContext, SaMaxSources, enablePathing: true);
            if (!_saSim.IsValid)
            {
                _saSim.Dispose(); _saSim = null;
                Phonon.iplContextRelease(ref _saContext);
                Console.WriteLine("[AcousticWorker] Steam Audio simulator create failed; using hand-rolled occlusion.");
                return;
            }
            _saScene = new SteamAudioScene(_saContext);

            _saReverbSim = new SteamAudioSimulator(_saContext, maxSources: 4, enableReflections: true, enableDirect: false);
            if (!_saReverbSim.IsValid) { _saReverbSim.Dispose(); _saReverbSim = null; }

            _saEnabled = true;
            Console.WriteLine("[AcousticWorker] Steam Audio simulation enabled (per-source occlusion/transmission + geometry reverb).");
        }
        catch (DllNotFoundException) { Console.WriteLine("[AcousticWorker] libphonon not found; using hand-rolled occlusion."); }
        catch (Exception ex) { Console.WriteLine($"[AcousticWorker] Steam Audio sim init failed; using hand-rolled occlusion: {ex.Message}"); }
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
            _saReverbSim?.SetScene(_saScene);
        }
        Console.WriteLine($"[AcousticWorker] Built Steam Audio scene from {boxes.Count} solid box colliders.");
    }

    /// <summary>Throttled: runs the reflections sim for a single probe at the listener to get the room's
    /// geometry-driven RT60, mapped to an FMOD reverb decay (ms) the provider applies to the listener's
    /// reverb. Reflections are the heaviest stage, so this runs only every Nth tick for one source.</summary>
    private void RunListenerReverb(Vector3 listener)
    {
        if (_saReverbSim == null || !_saReverbSim.IsValid) return;
        if (++_reverbTick % ReverbEveryNTicks != 0) return;
        if (_reverbSource == IntPtr.Zero) _reverbSource = _saReverbSim.AcquireSource();
        if (_reverbSource == IntPtr.Zero) return;

        _saReverbSim.SetSourceInputs(_reverbSource, listener);
        _saReverbSim.SetListener(listener);
        _saReverbSim.Run();
        var rv = _saReverbSim.GetReverb(_reverbSource);
        _listenerReverbMs = SteamAudioSimulator.ReverbDecayMs(rv);
    }

    private IntPtr GetOrAcquireSource(int entityId)
    {
        if (_saSources.TryGetValue(entityId, out var s)) return s;
        IntPtr src = _saSim!.AcquireSource();
        if (src != IntPtr.Zero) _saSources[entityId] = src;
        return src;
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
            _results.TryRemove(id, out _);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _workerThread?.Join(); // after this, no other thread touches the Phonon sim objects

        if (_saSim != null) { _saSim.Dispose(); _saSim = null; }
        if (_saReverbSim != null) { _saReverbSim.Dispose(); _saReverbSim = null; }
        if (_saScene != null) { _saScene.Dispose(); _saScene = null; }
        if (_saContext != IntPtr.Zero) Phonon.iplContextRelease(ref _saContext);

        _cts.Dispose();
    }
}

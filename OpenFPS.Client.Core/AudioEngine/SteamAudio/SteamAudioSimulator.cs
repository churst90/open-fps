using System.Numerics;
using System.Runtime.InteropServices;
using OpenFPS.Common;
using PV = OpenFPS.Client.Core.AudioEngine.SteamAudio.Phonon.IPLVector3;

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// An <c>IPLSimulator</c> and a fixed pool of <c>IPLSource</c> handles, for the acoustic worker: set the
/// scene on map load, acquire a source per voice, then each tick stage every source and the listener,
/// <see cref="Run"/> once (the direct stage batches all sources) and read each with
/// <see cref="GetResult"/>.
///
/// Every source is created and added up front (adding or removing one needs an
/// <c>iplSimulatorCommit</c>), so per-frame work never allocates or changes the simulator; Acquire and
/// Release only move ownership. Not thread-safe: one worker thread drives it. The caller owns the
/// context and the scene. Orientation is identity (+x right, +y up, -z ahead); positions go through
/// Phonon.World, which mirrors z.
/// </summary>
public sealed class SteamAudioSimulator : IDisposable
{
    /// <summary>Direct-stage result for one source. <see cref="Visibility"/> is a gain, 1 a clear line of
    /// sight and 0 fully blocked (Steam Audio's "occlusion" field). Transmission is the per-band gain of
    /// sound passing through the occluder.</summary>
    public readonly record struct DirectResult(float Visibility, float TransLow, float TransMid, float TransHigh)
    {
        /// <summary>Result to assume with no source or scene: unoccluded.</summary>
        public static readonly DirectResult Clear = new(1f, 1f, 1f, 1f);
    }

    /// <summary>Pathing arrival result for one source.</summary>
    /// <param name="Found">Whether a path was found.</param>
    /// <param name="WorldDirection">The game-world direction the sound comes from after routing through
    /// openings.</param>
    /// <param name="Energy">The SH omni term: 0 is no path, and the caller keeps the direct line.</param>
    /// <param name="EqLow">What the route takes per band (Steam Audio's pathing eq): the level of a sound
    /// that has to find its way round, where the direct stage only knows it is blocked.</param>
    /// <param name="EqMid">As <paramref name="EqLow"/>, mid band.</param>
    /// <param name="EqHigh">As <paramref name="EqLow"/>, high band.</param>
    public readonly record struct PathResult(bool Found, Vector3 WorldDirection, float Energy,
                                             float EqLow = 0f, float EqMid = 0f, float EqHigh = 0f);

    /// <summary>The direct result as the engine's acoustic parameters: <see cref="Occlusion"/> the
    /// fraction blocked (0 clear), EqLow/Mid/High per-band clarity (1 clear). The caller still clamps
    /// occlusion to its own cap.</summary>
    public readonly record struct AcousticParams(float Occlusion, float EqLow, float EqMid, float EqHigh, float Bleed);

    /// <summary>Maps a <see cref="DirectResult"/> (SA visibility gain + per-band transmission) to engine
    /// acoustic parameters: occlusion = 1−visibility; per-band clarity blends straight-line visibility with
    /// what transmits through the occluder (eq = v + (1−v)·trans); bleed = low-band transmission.</summary>
    public static AcousticParams ToAcousticParams(DirectResult dr)
    {
        float v = Math.Clamp(dr.Visibility, 0f, 1f);
        return new AcousticParams(
            1f - v,
            Math.Clamp(v + (1f - v) * dr.TransLow, 0f, 1f),
            Math.Clamp(v + (1f - v) * dr.TransMid, 0f, 1f),
            Math.Clamp(v + (1f - v) * dr.TransHigh, 0f, 1f),
            Math.Clamp(dr.TransLow, 0f, 1f));
    }

    /// <summary>
    /// Reflection result for a source: per-band RT60, seconds, and only that. The reflections output's
    /// <c>eq</c> triple comes back zero in every band on the parametric path (Steam Audio fills it only
    /// for the hybrid effect), so how much reverberant field there is comes from the geometry
    /// (OpenFPS.Common.Enclosure).
    /// </summary>
    public readonly record struct ReverbResult(float Rt60Low, float Rt60Mid, float Rt60High)
    {
        public static readonly ReverbResult None = new(0f, 0f, 0f);
        public float Max => MathF.Max(Rt60Low, MathF.Max(Rt60Mid, Rt60High));
    }

    /// <summary>A simulated RT60 as an FMOD SFXREVERB decay time, ms: the longest band, clamped.</summary>
    public static float ReverbDecayMs(ReverbResult r, float minMs = 100f, float maxMs = 20000f)
        => Math.Clamp(r.Max * 1000f, minMs, maxMs);

    private delegate void ProgressCallback(float progress, IntPtr userData);
    // Kept alive so native code can't call a collected delegate during a (synchronous) bake.
    private static readonly ProgressCallback _bakeProgress = (p, u) => { };

    private readonly IntPtr _context;
    private readonly int _maxSources;
    private readonly int _flags;        // any combination of DIRECT | PATHING | REFLECTIONS
    private readonly bool _direct;
    private readonly bool _pathing;
    private readonly bool _reflections;

    private IntPtr _simulator;
    private readonly Stack<IntPtr> _freeSources = new();
    private readonly List<IntPtr> _allSources = new();
    private Vector3 _listener;

    // Pathing probe state (only when _pathing): made by the first bake (BeginProbeBake), re-baked on map change.
    private IntPtr _probeArray;
    private IntPtr _probeBatch;
    private Phonon.IPLBakedDataIdentifier _pathId;

    /// <summary>Steam Audio's order-1 pathing SH (ACN: w, m=-1, m=0, m=+1) as a unit game-world arrival
    /// direction: normalize(-sh[1], sh[2], +sh[3]) in the mirrored world, measured from the simulator and
    /// held by SteamAudioMappingTests. <paramref name="w"/> is not needed for the direction.</summary>
    public static Vector3 PathingWorldDirection(float w, float shYm1, float shZm0, float shXp1)
    {
        // Ambisonic X is Steam Audio's ahead, which is its -z; the game's z runs the other way
        // (Phonon.World), so the game's z component is +X.
        var d = new Vector3(-shYm1, shZm0, Phonon.WorldZ(-shXp1));
        float len = d.Length();
        return len > 1e-6f ? d / len : Vector3.Zero;
    }

    /// <summary>Whether any source staged pathing inputs since the last <see cref="Run"/>. See Run.</summary>
    private bool _pathingStaged;

    /// <summary>Total pooled sources (0 until the first <see cref="SetScene"/>).</summary>
    public int Capacity => _allSources.Count;
    /// <summary>Sources currently available to acquire.</summary>
    public int Available => _freeSources.Count;
    /// <summary>True once the simulator was created successfully.</summary>
    public bool IsValid => _simulator != IntPtr.Zero;

    public SteamAudioSimulator(IntPtr context, int maxSources = 64, bool enablePathing = false,
        bool enableReflections = false, bool enableDirect = true, int samplingRate = 0, int frameSize = 1024)
    {
        if (samplingRate <= 0) samplingRate = OpenFPS.Client.AudioEngine.Fmod.MixerQuality.MixerRate;   // 0: the mixer's
        _context = context;
        _maxSources = maxSources;
        _direct = enableDirect;
        _pathing = enablePathing;
        _reflections = enableReflections;
        _flags = (enableDirect ? Phonon.IPL_SIMULATIONFLAGS_DIRECT : 0)
               | (enablePathing ? Phonon.IPL_SIMULATIONFLAGS_PATHING : 0)
               | (enableReflections ? Phonon.IPL_SIMULATIONFLAGS_REFLECTIONS : 0);

        var s = new Phonon.IPLSimulationSettings
        {
            flags = _flags,
            sceneType = SteamAudioScene.TypeFor(_context),
            reflectionType = enableReflections ? Phonon.IPL_REFLECTIONEFFECTTYPE_PARAMETRIC : Phonon.IPL_REFLECTIONEFFECTTYPE_CONVOLUTION,
            maxNumOcclusionSamples = 16, maxNumRays = enableReflections ? 8192 : 4096, numDiffuseSamples = 32,
            maxDuration = enableReflections ? 2.0f : 1.0f,
            maxOrder = 1, maxNumSources = maxSources, numThreads = 1, rayBatchSize = 16, numVisSamples = 4,
            samplingRate = samplingRate, frameSize = frameSize,
        };
        if (Phonon.iplSimulatorCreate(_context, ref s, out _simulator) != Phonon.IPL_STATUS_SUCCESS)
            _simulator = IntPtr.Zero;
    }

    /// <summary>Points the simulator at a built scene and commits. The source pool is made on the first
    /// call (sources must be added after the scene is set) and kept across rebuilds.</summary>
    public void SetScene(SteamAudioScene scene)
    {
        if (!IsValid || scene is null || !scene.IsBuilt) return;
        Phonon.iplSimulatorSetScene(_simulator, scene.Handle);

        // No pathing bake here (BeginProbeBake): inline it took a hundred seconds on a large map, during
        // which every source played unoccluded. Nothing that takes longer than a frame may gate the
        // direct stage.
        Phonon.iplSimulatorCommit(_simulator);

        if (_allSources.Count == 0)
        {
            for (int i = 0; i < _maxSources; i++)
            {
                var ss = new Phonon.IPLSourceSettings { flags = _flags };
                if (Phonon.iplSourceCreate(_simulator, ref ss, out IntPtr src) != Phonon.IPL_STATUS_SUCCESS) break;
                Phonon.iplSourceAdd(src, _simulator);
                _allSources.Add(src);
                _freeSources.Push(src);
            }
            Phonon.iplSimulatorCommit(_simulator);
        }
    }

    /// <summary>
    /// Starts the pathing bake on a thread of its own and returns at once: it is map-sized work, and
    /// until it is done <see cref="PathingReady"/> is false and every source gets its direct result.
    /// The batch is handed over by <see cref="CommitPendingProbes"/> on the owning thread: the simulator
    /// is single-threaded, and the bake thread only reads the scene.
    /// </summary>
    public void BeginProbeBake(SteamAudioScene scene)
    {
        if (!IsValid || !_pathing || scene is null || !scene.IsBuilt) return;
        if (_bakeThread is { IsAlive: true }) return;
        _bakeThread = new Thread(() =>
        {
            try { BuildOrRebakeProbes(scene); }
            catch (Exception ex) { Console.WriteLine($"[SteamAudio] Pathing bake failed; pathing stays off: {ex.Message}"); }
        })
        {
            IsBackground = true,
            Name = "SaPathingBake",
            // Below everything with a deadline: at a map load nobody is waiting for this.
            Priority = ThreadPriority.Lowest,
        };
        _bakeThread.Start();
    }

    private Thread? _bakeThread;

    /// <summary>True while the pathing bake is still reading the scene it was given.</summary>
    public bool Baking => _bakeThread is { IsAlive: true };
    private IntPtr _bakedBatchPending;

    /// <summary>
    /// Hands a finished bake to the simulator. Call from the thread that owns the simulator, between
    /// runs. Returns true on the tick that pathing actually comes alive, so the caller can say so.
    /// </summary>
    public bool CommitPendingProbes()
    {
        IntPtr batch = Interlocked.Exchange(ref _bakedBatchPending, IntPtr.Zero);
        if (batch == IntPtr.Zero) return false;
        Phonon.iplSimulatorAddProbeBatch(_simulator, batch);
        Phonon.iplSimulatorCommit(_simulator);
        _probeBatch = batch;
        return true;
    }

    /// <summary>
    /// Probes to generate at most, whatever the map's size: the bake grows worse than linearly in the
    /// probe count, and 720 x 420 m at a fixed 2 m is seventy-five thousand. A big map gets coarser
    /// spacing instead of an unbounded bake.
    /// </summary>
    private const int MaxProbes = 8192;
    private const float MinProbeSpacing = 2.0f;

    /// <summary>Generates floor probes over the scene bounds and bakes the visibility graph between them
    /// (pathing finds nothing without it). Built once; re-baked against the new scene on map change
    /// without moving the probes. UNIFORMFLOOR needs floors wound normal-up, or it places no probes and
    /// pathing quietly does nothing.</summary>
    private void BuildOrRebakeProbes(SteamAudioScene scene)
    {
        _pathId = new Phonon.IPLBakedDataIdentifier
        {
            type = Phonon.IPL_BAKEDDATATYPE_PATHING,
            variation = Phonon.IPL_BAKEDDATAVARIATION_DYNAMIC,
            endpointInfluence = new Phonon.IPLSphere { center = new PV { x = 0, y = 0, z = 0 }, radius = 100000f },
        };

        IntPtr batch = _probeBatch;
        bool isNewBatch = batch == IntPtr.Zero;
        int probes = 0;
        float spacing = MinProbeSpacing;

        if (isNewBatch)
        {
            Vector3 min = scene.BoundsMin, max = scene.BoundsMax;
            if (!(max.X > min.X)) return; // empty / invalid bounds -> no probes
            Phonon.iplProbeArrayCreate(_context, out _probeArray);
            float area = MathF.Max(1f, (max.X - min.X) * (max.Z - min.Z));
            spacing = MathF.Max(MinProbeSpacing, MathF.Sqrt(area / MaxProbes));
            var genP = new Phonon.IPLProbeGenerationParams
            {
                type = Phonon.IPL_PROBEGENERATIONTYPE_UNIFORMFLOOR, spacing = spacing, height = 1.5f,
            };
            // The scene's bounds are the game's; the probe volume is in Steam Audio's world (Phonon.World).
            float zA = Phonon.World(min).z, zB = Phonon.World(max).z;
            SetBoxTransform(ref genP.transform, min.X, max.X, min.Y - 0.5f, max.Y, MathF.Min(zA, zB), MathF.Max(zA, zB));
            Phonon.iplProbeArrayGenerateProbes(_probeArray, scene.Handle, ref genP);
            probes = Phonon.iplProbeArrayGetNumProbes(_probeArray);
            if (probes == 0)
            { Phonon.iplProbeArrayRelease(ref _probeArray); _probeArray = IntPtr.Zero; return; }

            Phonon.iplProbeBatchCreate(_context, out batch);
            Phonon.iplProbeBatchAddProbeArray(batch, _probeArray);
            Phonon.iplProbeBatchCommit(batch);
        }

        var bakeP = new Phonon.IPLPathBakeParams
        {
            scene = scene.Handle, probeBatch = batch, identifier = _pathId,
            numSamples = 4, radius = 0.5f, threshold = 0.1f, visRange = 16.0f, pathRange = 100.0f, numThreads = 1,
        };
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        Phonon.iplPathBakerBake(_context, ref bakeP, Marshal.GetFunctionPointerForDelegate(_bakeProgress), IntPtr.Zero);
        double seconds = (System.Diagnostics.Stopwatch.GetTimestamp() - start) / (double)System.Diagnostics.Stopwatch.Frequency;
        Console.WriteLine($"[SteamAudio] Pathing bake finished: {probes} probe(s) at {spacing:F1} m spacing in {seconds:F1} s. "
                        + "Occlusion was live from the first tick; only the direction hint for an occluded source was waiting on this.");

        // Published, not attached: only the simulator's own thread may add to it (CommitPendingProbes).
        // A re-bake of a batch it already holds has nothing to hand over.
        if (isNewBatch) Interlocked.Exchange(ref _bakedBatchPending, batch);
    }

    /// <summary>True when pathing is enabled and a baked probe batch exists (so pathing can find routes).</summary>
    public bool PathingReady => _pathing && _probeBatch != IntPtr.Zero;

    /// <summary>Borrows a source handle from the pool, or <see cref="IntPtr.Zero"/> if exhausted (the
    /// caller then treats that voice as <see cref="DirectResult.Clear"/> rather than failing).</summary>
    public IntPtr AcquireSource() => _freeSources.Count > 0 ? _freeSources.Pop() : IntPtr.Zero;

    /// <summary>Returns a source to the pool. The handle stays added to the simulator for reuse.</summary>
    public void ReleaseSource(IntPtr source)
    {
        if (source != IntPtr.Zero) _freeSources.Push(source);
    }

    /// <summary>Clears a source's per-frame inputs (zeroed flags) so the next <see cref="Run"/> stops
    /// tracing it. Call before releasing a source whose voice has stopped, so an idle pooled source costs
    /// no rays.</summary>
    public void ClearSource(IntPtr source)
    {
        if (source == IntPtr.Zero) return;
        var inputs = default(Phonon.IPLSimulationInputs); // flags = 0, directFlags = 0 -> inert
        Phonon.iplSourceSetInputs(source, _flags, ref inputs);
    }

    /// <summary>Sets the listener position used by the next <see cref="Run"/> (shared across all sources).</summary>
    public void SetListener(Vector3 worldPos) => _listener = worldPos;

    /// <summary>Stages one source's inputs (world position) for the next <see cref="Run"/>. Call once per
    /// active source per tick, before <see cref="Run"/>. Includes pathing inputs when pathing is enabled.</summary>
    public void SetSourceInputs(IntPtr source, Vector3 worldPos, float occlusionRadius = 0.5f)
    {
        if (source == IntPtr.Zero) return;
        // A source claims the pathing stage only once there are probes: a source's flags persist until
        // it is staged again, and the pathing run dereferences the batch of every source that claims it.
        // Staged before the bake finished, a source would claim it with a null batch.
        int flags = PathingReady ? _flags : _flags & ~Phonon.IPL_SIMULATIONFLAGS_PATHING;
        var inputs = new Phonon.IPLSimulationInputs
        {
            flags = flags,
            directFlags = _direct ? (Phonon.IPL_DIRECTSIMULATIONFLAGS_OCCLUSION | Phonon.IPL_DIRECTSIMULATIONFLAGS_TRANSMISSION) : 0,
            source = Coord(worldPos),
            occlusionType = Phonon.IPL_OCCLUSIONTYPE_VOLUMETRIC,
            // Per source (AudioEmission.OcclusionRadiusFor): a fixed half metre put half of every
            // ground-level emitter's probe sphere inside the ground.
            occlusionRadius = MathF.Max(0.01f, occlusionRadius),
            numOcclusionSamples = 16,
            // Every surface up to eight, not the nearest one: a box's loss is split across its two
            // faces (SteamAudioScene.MaterialIndex), and two walls in a row must both be paid for.
            numTransmissionRays = 8,
        };
        if (_reflections)
        {
            inputs.reverbScale0 = 1f; inputs.reverbScale1 = 1f; inputs.reverbScale2 = 1f;
        }
        if (PathingReady)
        {
            inputs.pathingProbes = _probeBatch;
            inputs.bakedDataIdentifier = _pathId;
            inputs.visRadius = 1.0f;
            inputs.visThreshold = 0.1f;
            inputs.visRange = 50.0f;
            inputs.pathingOrder = 1;
            inputs.findAlternatePaths = 1;
            _pathingStaged = true;
        }
        Phonon.iplSourceSetInputs(source, flags, ref inputs);
    }

    // --- Ray budget ------------------------------------------------------------------------------------
    // The simulation is the most expensive thing the client does per audio frame: what was asked for
    // (rays x bounces) and what it cost here, always measured.

    /// <summary>Rays cast per <see cref="Run"/>, as configured.</summary>
    public int RaysPerRun => _reflections ? 8192 : 4096;

    /// <summary>Bounces per ray, as configured. Reflections are the expensive multiplier.</summary>
    public int BouncesPerRun => _reflections ? 16 : 1;

    /// <summary>Completed simulation runs.</summary>
    public long RunCount { get; private set; }

    /// <summary>Milliseconds spent in <see cref="Run"/>, total and worst-case.</summary>
    public double TotalRunMs { get; private set; }
    public double MaxRunMs { get; private set; }

    /// <summary>Mean cost of a run so far, or 0 before the first one.</summary>
    public double AverageRunMs => RunCount > 0 ? TotalRunMs / RunCount : 0.0;

    /// <summary>One line describing the budget and what it is costing. For the periodic perf report.</summary>
    public string DescribeRayBudget() =>
        $"{RaysPerRun} rays x {BouncesPerRun} bounce(s), {Capacity - Available}/{Capacity} sources: " +
        $"avg {AverageRunMs:F2}ms, max {MaxRunMs:F2}ms over {RunCount} runs";

    /// <summary>Zeroes the timing window so each report covers one interval rather than all of history.</summary>
    public void ResetRayBudgetStats()
    {
        RunCount = 0;
        TotalRunMs = 0.0;
        MaxRunMs = 0.0;
    }

    /// <summary>Runs the enabled stages once for all staged sources. Ray-traced: a worker thread, never
    /// the mixer or game thread.</summary>
    public void Run()
    {
        if (!IsValid) return;
        var shared = new Phonon.IPLSimulationSharedInputs
        {
            listener = Coord(_listener),
            numRays = RaysPerRun,
            numBounces = BouncesPerRun,
            duration = _reflections ? 2.0f : 1.0f,
            order = 1, irradianceMinDistance = 1.0f,
        };

        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        Phonon.iplSimulatorSetSharedInputs(_simulator, _flags, ref shared);
        if (_direct) Phonon.iplSimulatorRunDirect(_simulator);
        // Only when a source was staged with a probe batch: otherwise the pathing run is a null
        // dereference inside Steam Audio, a dead process rather than an exception.
        if (PathingReady && _pathingStaged) Phonon.iplSimulatorRunPathing(_simulator);
        if (_reflections) Phonon.iplSimulatorRunReflections(_simulator);
        _pathingStaged = false;
        long elapsed = System.Diagnostics.Stopwatch.GetTimestamp() - start;

        double ms = elapsed * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        RunCount++;
        TotalRunMs += ms;
        if (ms > MaxRunMs) MaxRunMs = ms;
        PerfProbe.Record(_reflections ? "sa.sim.run.reflections" : "sa.sim.run.direct", elapsed);
    }

    /// <summary>Reads the most recent reflection RT60 for a source (valid after <see cref="Run"/> when
    /// reflections are enabled). Returns <see cref="ReverbResult.None"/> otherwise.</summary>
    public ReverbResult GetReverb(IntPtr source)
    {
        if (source == IntPtr.Zero || !_reflections) return ReverbResult.None;
        var outputs = default(Phonon.IPLSimulationOutputs);
        Phonon.iplSourceGetOutputs(source, _flags, ref outputs);
        ref var r = ref outputs.reflections;
        return new ReverbResult(r.reverbTimes0, r.reverbTimes1, r.reverbTimes2);
    }

    /// <summary>Reads the most recent direct result for a source (valid after <see cref="Run"/>).</summary>
    public DirectResult GetResult(IntPtr source)
    {
        if (source == IntPtr.Zero || !_direct) return DirectResult.Clear;
        var outputs = default(Phonon.IPLSimulationOutputs);
        Phonon.iplSourceGetOutputs(source, _flags, ref outputs);
        ref var d = ref outputs.direct;
        return new DirectResult(d.occlusion, d.transmission0, d.transmission1, d.transmission2);
    }

    private static Phonon.IPLCoordinateSpace3 Coord(Vector3 origin) => new()
    {
        right = new PV { x = 1, y = 0, z = 0 },
        up = new PV { x = 0, y = 1, z = 0 },
        ahead = new PV { x = 0, y = 0, z = -1 },
        origin = Phonon.World(origin),
    };

    // Row-major affine transform mapping the unit cube [0,1]^3 onto the given world box (for probe volume).
    private static unsafe void SetBoxTransform(ref Phonon.IPLMatrix4x4 m, float minX, float maxX, float minY, float maxY, float minZ, float maxZ)
    {
        for (int i = 0; i < 16; i++) m.elements[i] = 0;
        m.elements[0] = maxX - minX; m.elements[3] = minX;
        m.elements[5] = maxY - minY; m.elements[7] = minY;
        m.elements[10] = maxZ - minZ; m.elements[11] = minZ;
        m.elements[15] = 1f;
    }

    public void Dispose()
    {
        // The bake reads the scene and writes a probe batch: releasing either under it is a native
        // use-after-free, so wait for it, bounded, and past the wait abandon the batch rather than free
        // something still being written.
        var bake = _bakeThread;
        if (bake is { IsAlive: true } && !bake.Join(TimeSpan.FromSeconds(5)))
        {
            Console.WriteLine("[SteamAudio] Pathing bake did not finish within 5 s of shutdown; leaving its probes to the process exit.");
            _bakedBatchPending = IntPtr.Zero;
            _probeBatch = IntPtr.Zero;
            _probeArray = IntPtr.Zero;
        }
        _bakeThread = null;
        if (_bakedBatchPending != IntPtr.Zero)
        {
            IntPtr pending = Interlocked.Exchange(ref _bakedBatchPending, IntPtr.Zero);
            if (pending != IntPtr.Zero && pending != _probeBatch) Phonon.iplProbeBatchRelease(ref pending);
        }

        for (int i = 0; i < _allSources.Count; i++)
        {
            IntPtr s = _allSources[i];
            Phonon.iplSourceRelease(ref s);
        }
        _allSources.Clear();
        _freeSources.Clear();
        if (_probeBatch != IntPtr.Zero) Phonon.iplProbeBatchRelease(ref _probeBatch);
        if (_probeArray != IntPtr.Zero) Phonon.iplProbeArrayRelease(ref _probeArray);
        _probeBatch = IntPtr.Zero; _probeArray = IntPtr.Zero;
        if (_simulator != IntPtr.Zero) Phonon.iplSimulatorRelease(ref _simulator);
        _simulator = IntPtr.Zero;
    }
}

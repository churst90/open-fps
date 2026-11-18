using System.Numerics;
using OpenFPS.Geometry;
using OpenFPS.Native.SteamAudio;
using PV = OpenFPS.Native.SteamAudio.Phonon.IPLVector3;

namespace OpenFPS.Audio.SteamAudio;

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
    /// (OpenFPS.Acoustics.Enclosure).
    /// </summary>
    public readonly record struct ReverbResult(float Rt60Low, float Rt60Mid, float Rt60High)
    {
        public static readonly ReverbResult None = new(0f, 0f, 0f);
        public float Max => MathF.Max(Rt60Low, MathF.Max(Rt60Mid, Rt60High));
    }

    /// <summary>A simulated RT60 as an FMOD SFXREVERB decay time, ms: the longest band, clamped.</summary>
    public static float ReverbDecayMs(ReverbResult r, float minMs = 100f, float maxMs = 20000f)
        => Math.Clamp(r.Max * 1000f, minMs, maxMs);

    private readonly IntPtr _context;
    private readonly int _maxSources;
    private readonly int _flags;        // any combination of DIRECT | REFLECTIONS
    private readonly bool _direct;
    private readonly bool _reflections;

    private IntPtr _simulator;
    private readonly Stack<IntPtr> _freeSources = new();
    private readonly List<IntPtr> _allSources = new();
    private Vector3 _listener;

    /// <summary>Total pooled sources (0 until the first <see cref="SetScene"/>).</summary>
    public int Capacity => _allSources.Count;
    /// <summary>Sources currently available to acquire.</summary>
    public int Available => _freeSources.Count;
    /// <summary>True once the simulator was created successfully.</summary>
    public bool IsValid => _simulator != IntPtr.Zero;

    public SteamAudioSimulator(IntPtr context, int maxSources = 64,
        bool enableReflections = false, bool enableDirect = true, int samplingRate = 0, int frameSize = 1024)
    {
        if (samplingRate <= 0) samplingRate = OpenFPS.Audio.Fmod.MixerQuality.DefaultRate;   // 0: the default mixer's
        _context = context;
        _maxSources = maxSources;
        _direct = enableDirect;
        _reflections = enableReflections;
        _flags = (enableDirect ? Phonon.IPL_SIMULATIONFLAGS_DIRECT : 0)
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
    /// active source per tick, before <see cref="Run"/>.</summary>
    public void SetSourceInputs(IntPtr source, Vector3 worldPos, float occlusionRadius = 0.5f)
    {
        if (source == IntPtr.Zero) return;
        var inputs = new Phonon.IPLSimulationInputs
        {
            flags = _flags,
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
        Phonon.iplSourceSetInputs(source, _flags, ref inputs);
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
        if (_reflections) Phonon.iplSimulatorRunReflections(_simulator);
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

    public void Dispose()
    {
        for (int i = 0; i < _allSources.Count; i++)
        {
            IntPtr s = _allSources[i];
            Phonon.iplSourceRelease(ref s);
        }
        _allSources.Clear();
        _freeSources.Clear();
        if (_simulator != IntPtr.Zero) Phonon.iplSimulatorRelease(ref _simulator);
        _simulator = IntPtr.Zero;
    }
}

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using PV = OpenFPS.Client.Core.AudioEngine.SteamAudio.Phonon.IPLVector3;

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// Owns an <c>IPLSimulator</c> (DIRECT stage) plus a <see cref="SteamAudioScene"/> reference and a fixed
/// pool of <c>IPLSource</c> handles. This is the runtime engine that Phase 4 wires into the acoustic
/// worker thread to replace the hand-rolled occlusion/transmission computation: build/replace the scene
/// on map load, acquire one source per active voice, then each tick set every active source's position,
/// set the listener, <see cref="Run"/> the simulator ONCE (the direct stage batches all sources), and
/// read each source's occlusion/transmission via <see cref="GetResult"/>.
///
/// Lifetime model mirrors the voice-pool lesson: all <c>IPLSource</c> handles are created and added to
/// the simulator up front (adding/removing a source requires an <c>iplSimulatorCommit</c>), so per-frame
/// work never allocates or mutates the simulator graph. Acquire/Release only toggle logical ownership —
/// a released source stays added but simply isn't fed inputs or read.
///
/// This type is NOT thread-safe; drive it from a single worker thread. It does NOT own the IPLContext
/// or the scene — the caller creates and disposes those.
/// Coordinates: occlusion/transmission are position-only, so source/listener orientation is identity
/// (Steam Audio's +x right / +y up / -z forward); positions are passed through verbatim in world space.
/// </summary>
public sealed class SteamAudioSimulator : IDisposable
{
    /// <summary>Direct-stage result for one source. <see cref="Visibility"/> is a gain factor: 1 = fully
    /// clear line of sight, 0 = fully blocked (this is Steam Audio's "occlusion" field — see the Phase 1
    /// note). Transmission is the per-band gain of sound passing THROUGH the occluder (low/mid/high).</summary>
    public readonly record struct DirectResult(float Visibility, float TransLow, float TransMid, float TransHigh)
    {
        /// <summary>Result to assume when no source/scene is available (unoccluded, full transmission).</summary>
        public static readonly DirectResult Clear = new(1f, 1f, 1f, 1f);
    }

    /// <summary>Pathing arrival result for one source: the direction (world space) the sound comes FROM
    /// after routing through openings, and <see cref="Energy"/> (the SH omni term — 0 means no path found,
    /// so the caller should keep using the direct line to the source).</summary>
    public readonly record struct PathResult(bool Found, Vector3 WorldDirection, float Energy)
    {
        public static readonly PathResult None = new(false, Vector3.Zero, 0f);
    }

    /// <summary>The direct result mapped to the engine's per-band acoustic parameters: <see cref="Occlusion"/>
    /// is "fraction blocked" (0=clear) and EqLow/Mid/High are per-band clarity (1=clear). Pure mapping,
    /// unit-tested. The caller still clamps occlusion to its own cap.</summary>
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

    private delegate void ProgressCallback(float progress, IntPtr userData);
    // Kept alive so native code can't call a collected delegate during a (synchronous) bake.
    private static readonly ProgressCallback _bakeProgress = (p, u) => { };

    private readonly IntPtr _context;
    private readonly int _maxSources;
    private readonly int _samplingRate;
    private readonly int _frameSize;
    private readonly int _flags;        // DIRECT, or DIRECT|PATHING when pathing is enabled
    private readonly bool _pathing;

    private IntPtr _simulator;
    private readonly Stack<IntPtr> _freeSources = new();
    private readonly List<IntPtr> _allSources = new();
    private Vector3 _listener;

    // Pathing probe state (only when _pathing). Built once on the first SetScene; re-baked on map change.
    private IntPtr _probeArray;
    private IntPtr _probeBatch;
    private Phonon.IPLBakedDataIdentifier _pathId;

    /// <summary>Converts Steam Audio's order-1 pathing SH (ACN: w, m=-1, m=0, m=+1) to a unit WORLD
    /// arrival direction (where the sound comes FROM). Convention pinned empirically by SimPathDirSpike:
    /// worldDir = normalize(-sh[1], sh[2], -sh[3]) (world X = -ACN(m=-1), Z = -ACN(m=+1), Y = ACN(m=0)).</summary>
    public static Vector3 PathingWorldDirection(float w, float shYm1, float shZm0, float shXp1)
    {
        var d = new Vector3(-shYm1, shZm0, -shXp1);
        float len = d.Length();
        return len > 1e-6f ? d / len : Vector3.Zero;
    }

    /// <summary>Total pooled sources (0 until the first <see cref="SetScene"/>).</summary>
    public int Capacity => _allSources.Count;
    /// <summary>Sources currently available to acquire.</summary>
    public int Available => _freeSources.Count;
    /// <summary>True once the simulator was created successfully.</summary>
    public bool IsValid => _simulator != IntPtr.Zero;

    public SteamAudioSimulator(IntPtr context, int maxSources = 64, bool enablePathing = false, int samplingRate = 44100, int frameSize = 1024)
    {
        _context = context;
        _maxSources = maxSources;
        _samplingRate = samplingRate;
        _frameSize = frameSize;
        _pathing = enablePathing;
        _flags = Phonon.IPL_SIMULATIONFLAGS_DIRECT | (enablePathing ? Phonon.IPL_SIMULATIONFLAGS_PATHING : 0);

        var s = new Phonon.IPLSimulationSettings
        {
            flags = _flags,
            sceneType = Phonon.IPL_SCENETYPE_DEFAULT,
            reflectionType = Phonon.IPL_REFLECTIONEFFECTTYPE_CONVOLUTION,
            maxNumOcclusionSamples = 16, maxNumRays = 4096, numDiffuseSamples = 32, maxDuration = 1.0f,
            maxOrder = 1, maxNumSources = maxSources, numThreads = 1, rayBatchSize = 16, numVisSamples = 4,
            samplingRate = samplingRate, frameSize = frameSize,
        };
        if (Phonon.iplSimulatorCreate(_context, ref s, out _simulator) != Phonon.IPL_STATUS_SUCCESS)
            _simulator = IntPtr.Zero;
    }

    /// <summary>Points the simulator at a (built) scene and commits. The source pool is created lazily on
    /// the first call — sources must be added AFTER the scene is set (mirrors the working spike ordering) —
    /// and reused across later scene rebuilds. Safe to call again on map change.</summary>
    public void SetScene(SteamAudioScene scene)
    {
        if (!IsValid || scene is null || !scene.IsBuilt) return;
        Phonon.iplSimulatorSetScene(_simulator, scene.Handle);

        if (_pathing) BuildOrRebakeProbes(scene);

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

    /// <summary>Generates floor probes over the scene bounds and bakes the probe-to-probe visibility graph
    /// (pathing finds nothing without the bake). Built once; re-baked against the new scene on map change
    /// (probe positions are not relocated — pathing is tuned for single-map sessions). Requires the scene
    /// to contain floor geometry wound normal-up, or UNIFORMFLOOR places no probes (pathing then silently
    /// no-ops and the caller falls back to the direct line).</summary>
    private void BuildOrRebakeProbes(SteamAudioScene scene)
    {
        _pathId = new Phonon.IPLBakedDataIdentifier
        {
            type = Phonon.IPL_BAKEDDATATYPE_PATHING,
            variation = Phonon.IPL_BAKEDDATAVARIATION_DYNAMIC,
            endpointInfluence = new Phonon.IPLSphere { center = new PV { x = 0, y = 0, z = 0 }, radius = 100000f },
        };

        if (_probeBatch == IntPtr.Zero)
        {
            Vector3 min = scene.BoundsMin, max = scene.BoundsMax;
            if (!(max.X > min.X)) return; // empty / invalid bounds -> no probes
            Phonon.iplProbeArrayCreate(_context, out _probeArray);
            var genP = new Phonon.IPLProbeGenerationParams
            {
                type = Phonon.IPL_PROBEGENERATIONTYPE_UNIFORMFLOOR, spacing = 2.0f, height = 1.5f,
            };
            SetBoxTransform(ref genP.transform, min.X, max.X, min.Y - 0.5f, max.Y, min.Z, max.Z);
            Phonon.iplProbeArrayGenerateProbes(_probeArray, scene.Handle, ref genP);
            if (Phonon.iplProbeArrayGetNumProbes(_probeArray) == 0)
            { Phonon.iplProbeArrayRelease(ref _probeArray); _probeArray = IntPtr.Zero; return; }

            Phonon.iplProbeBatchCreate(_context, out _probeBatch);
            Phonon.iplProbeBatchAddProbeArray(_probeBatch, _probeArray);
            Phonon.iplProbeBatchCommit(_probeBatch);
            Phonon.iplSimulatorAddProbeBatch(_simulator, _probeBatch);
        }

        var bakeP = new Phonon.IPLPathBakeParams
        {
            scene = scene.Handle, probeBatch = _probeBatch, identifier = _pathId,
            numSamples = 4, radius = 0.5f, threshold = 0.1f, visRange = 16.0f, pathRange = 100.0f, numThreads = 1,
        };
        Phonon.iplPathBakerBake(_context, ref bakeP, Marshal.GetFunctionPointerForDelegate(_bakeProgress), IntPtr.Zero);
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
    public void SetSourceInputs(IntPtr source, Vector3 worldPos)
    {
        if (source == IntPtr.Zero) return;
        var inputs = new Phonon.IPLSimulationInputs
        {
            flags = _flags,
            directFlags = Phonon.IPL_DIRECTSIMULATIONFLAGS_OCCLUSION | Phonon.IPL_DIRECTSIMULATIONFLAGS_TRANSMISSION,
            source = Coord(worldPos),
            occlusionType = Phonon.IPL_OCCLUSIONTYPE_VOLUMETRIC,
            occlusionRadius = 0.5f,
            numOcclusionSamples = 16,
            numTransmissionRays = 1,
        };
        if (PathingReady)
        {
            inputs.pathingProbes = _probeBatch;
            inputs.bakedDataIdentifier = _pathId;
            inputs.visRadius = 1.0f;
            inputs.visThreshold = 0.1f;
            inputs.visRange = 50.0f;
            inputs.pathingOrder = 1;
            inputs.findAlternatePaths = 1;
        }
        Phonon.iplSourceSetInputs(source, _flags, ref inputs);
    }

    /// <summary>Runs the direct (and, when enabled, pathing) stage once for ALL staged sources against the
    /// current listener. Expensive (ray-traced) — call on a worker thread, never the mixer/game thread.</summary>
    public void Run()
    {
        if (!IsValid) return;
        var shared = new Phonon.IPLSimulationSharedInputs
        {
            listener = Coord(_listener),
            numRays = 4096, numBounces = 1, duration = 1.0f, order = 1, irradianceMinDistance = 1.0f,
        };
        Phonon.iplSimulatorSetSharedInputs(_simulator, _flags, ref shared);
        Phonon.iplSimulatorRunDirect(_simulator);
        if (PathingReady) Phonon.iplSimulatorRunPathing(_simulator);
    }

    /// <summary>Reads the most recent direct result for a source (valid after <see cref="Run"/>).</summary>
    public DirectResult GetResult(IntPtr source)
    {
        if (source == IntPtr.Zero) return DirectResult.Clear;
        var outputs = default(Phonon.IPLSimulationOutputs);
        Phonon.iplSourceGetOutputs(source, _flags, ref outputs);
        ref var d = ref outputs.direct;
        return new DirectResult(d.occlusion, d.transmission0, d.transmission1, d.transmission2);
    }

    /// <summary>Reads the most recent pathing result for a source — the WORLD direction the sound arrives
    /// from after routing through openings. Returns <see cref="PathResult.None"/> when pathing is off or
    /// no path was found (caller should keep using the direct line to the source).</summary>
    public PathResult GetPathing(IntPtr source)
    {
        if (source == IntPtr.Zero || !PathingReady) return PathResult.None;
        var outputs = default(Phonon.IPLSimulationOutputs);
        Phonon.iplSourceGetOutputs(source, _flags, ref outputs);
        if (outputs.pathing.shCoeffs == IntPtr.Zero) return PathResult.None;

        var sh = new float[4];
        Marshal.Copy(outputs.pathing.shCoeffs, sh, 0, 4);
        float energy = sh[0];
        if (energy <= 0.001f) return PathResult.None;
        Vector3 dir = PathingWorldDirection(sh[0], sh[1], sh[2], sh[3]);
        if (dir == Vector3.Zero) return PathResult.None;
        return new PathResult(true, dir, energy);
    }

    private static Phonon.IPLCoordinateSpace3 Coord(Vector3 origin) => new()
    {
        right = new PV { x = 1, y = 0, z = 0 },
        up = new PV { x = 0, y = 1, z = 0 },
        ahead = new PV { x = 0, y = 0, z = -1 },
        origin = new PV { x = origin.X, y = origin.Y, z = origin.Z },
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

using System;
using System.Collections.Generic;
using System.Numerics;
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

    private const int Flags = Phonon.IPL_SIMULATIONFLAGS_DIRECT;

    private readonly IntPtr _context;
    private readonly int _maxSources;
    private readonly int _samplingRate;
    private readonly int _frameSize;

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

    public SteamAudioSimulator(IntPtr context, int maxSources = 64, int samplingRate = 44100, int frameSize = 1024)
    {
        _context = context;
        _maxSources = maxSources;
        _samplingRate = samplingRate;
        _frameSize = frameSize;

        var s = new Phonon.IPLSimulationSettings
        {
            flags = Flags,
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
        Phonon.iplSimulatorCommit(_simulator);

        if (_allSources.Count == 0)
        {
            for (int i = 0; i < _maxSources; i++)
            {
                var ss = new Phonon.IPLSourceSettings { flags = Flags };
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

    /// <summary>Sets the listener position used by the next <see cref="Run"/> (shared across all sources).</summary>
    public void SetListener(Vector3 worldPos) => _listener = worldPos;

    /// <summary>Stages one source's inputs (world position) for the next <see cref="Run"/>. Call once per
    /// active source per tick, before <see cref="Run"/>.</summary>
    public void SetSourceInputs(IntPtr source, Vector3 worldPos)
    {
        if (source == IntPtr.Zero) return;
        var inputs = new Phonon.IPLSimulationInputs
        {
            flags = Flags,
            directFlags = Phonon.IPL_DIRECTSIMULATIONFLAGS_OCCLUSION | Phonon.IPL_DIRECTSIMULATIONFLAGS_TRANSMISSION,
            source = Coord(worldPos),
            occlusionType = Phonon.IPL_OCCLUSIONTYPE_VOLUMETRIC,
            occlusionRadius = 0.5f,
            numOcclusionSamples = 16,
            numTransmissionRays = 1,
        };
        Phonon.iplSourceSetInputs(source, Flags, ref inputs);
    }

    /// <summary>Runs the direct stage once for ALL staged sources against the current listener. Expensive
    /// (ray-traced) — call on a worker thread, never the mixer/game thread.</summary>
    public void Run()
    {
        if (!IsValid) return;
        var shared = new Phonon.IPLSimulationSharedInputs
        {
            listener = Coord(_listener),
            numRays = 4096, numBounces = 1, duration = 1.0f, order = 1, irradianceMinDistance = 1.0f,
        };
        Phonon.iplSimulatorSetSharedInputs(_simulator, Flags, ref shared);
        Phonon.iplSimulatorRunDirect(_simulator);
    }

    /// <summary>Reads the most recent direct result for a source (valid after <see cref="Run"/>).</summary>
    public DirectResult GetResult(IntPtr source)
    {
        if (source == IntPtr.Zero) return DirectResult.Clear;
        var outputs = default(Phonon.IPLSimulationOutputs);
        Phonon.iplSourceGetOutputs(source, Flags, ref outputs);
        ref var d = ref outputs.direct;
        return new DirectResult(d.occlusion, d.transmission0, d.transmission1, d.transmission2);
    }

    private static Phonon.IPLCoordinateSpace3 Coord(Vector3 origin) => new()
    {
        right = new PV { x = 1, y = 0, z = 0 },
        up = new PV { x = 0, y = 1, z = 0 },
        ahead = new PV { x = 0, y = 0, z = -1 },
        origin = new PV { x = origin.X, y = origin.Y, z = origin.Z },
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

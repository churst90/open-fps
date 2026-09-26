using System;
using System.Numerics;
using System.Threading;

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// The place you are standing in, measured: an impulse response traced through the map's real
/// geometry from the listener's own position, a few times a second, for everything outdoors to be
/// played through (TracedReverbDsp). Steam Audio's "listener-centric reverb".
///
/// Why it exists. Outdoors the reverberation used to be FMOD's room algorithm with its decay set from
/// a ray survey — a statistical ROOM tail, dense and smooth from the first milliseconds. A street is
/// not that: its tail is the flutter between two facades, scatter off windows and cars, and most of
/// the energy leaving through the open sky. Asked "why do we even need reverb at all if that should
/// fall out as a natural consequence of correct physics", the honest answer was that we did not need
/// the ALGORITHM — only something to stand for the thousands of copies of copies nobody can voice one
/// by one. This is that, measured: rays from the listener, bouncing off the scene's own materials
/// (absorption and scattering per band), collected back into a two-second first-order ambisonic IR.
///
/// One simulation for everything, because the tracing is the cost. The approximation is Steam
/// Audio's own: every sound is reverberated as if it were where the listener is. The late field of a
/// place barely depends on where in it the source stands; the early echoes do, and those are voiced
/// separately and exactly (EngineReflections, EarlyReflections' flutter).
/// </summary>
internal sealed class TracedReverb : IDisposable
{
    /// <summary>The one in use, for the provider's reverb buses to read. Null until the scene exists.</summary>
    public static volatile TracedReverb? Current;

    public const float DurationSeconds = 2.0f;
    public const int Order = 1;
    public const int Channels = (Order + 1) * (Order + 1);
    private const int Rays = 8192, Bounces = 16;
    /// <summary>How often the trace is refreshed. Walking is a metre and a half a second; a quarter
    /// second is a third of a metre, and the IR crossfades inside the effect.</summary>
    private const int RefreshMs = 250;

    public IntPtr Context { get; }
    public int SampleRate { get; }
    public int FrameSize { get; }
    public int IrSize => (int)(DurationSeconds * SampleRate);

    private IntPtr _simulator, _source;
    private readonly object _gate = new();
    private Thread? _thread;
    private volatile bool _running;
    private Vector3 _listener;
    private bool _haveScene;

    /// <summary>Diagnostics: runs so far and the last run's cost.</summary>
    public int Runs;
    public double LastRunMs;

    public TracedReverb(IntPtr context, int sampleRate = 44100, int frameSize = 1024)
    {
        Context = context; SampleRate = sampleRate; FrameSize = frameSize;
        var s = new Phonon.IPLSimulationSettings
        {
            flags = Phonon.IPL_SIMULATIONFLAGS_REFLECTIONS,
            sceneType = Phonon.IPL_SCENETYPE_DEFAULT,
            reflectionType = Phonon.IPL_REFLECTIONEFFECTTYPE_CONVOLUTION,
            maxNumOcclusionSamples = 16, maxNumRays = Rays, numDiffuseSamples = 32,
            maxDuration = DurationSeconds, maxOrder = Order, maxNumSources = 1, numThreads = 2,
            rayBatchSize = 16, numVisSamples = 4, samplingRate = sampleRate, frameSize = frameSize,
        };
        if (Phonon.iplSimulatorCreate(context, ref s, out _simulator) != Phonon.IPL_STATUS_SUCCESS)
            _simulator = IntPtr.Zero;
    }

    public bool IsValid => _simulator != IntPtr.Zero;

    /// <summary>The source whose outputs the effect reads. Mixer thread reads it.</summary>
    public IntPtr Source => _source;

    /// <summary>Points the tracer at a (built) scene, and starts tracing.</summary>
    public void SetScene(SteamAudioScene scene)
    {
        if (!IsValid || !scene.IsBuilt) return;
        lock (_gate)
        {
            Phonon.iplSimulatorSetScene(_simulator, scene.Handle);
            Phonon.iplSimulatorCommit(_simulator);
            if (_source == IntPtr.Zero)
            {
                var ss = new Phonon.IPLSourceSettings { flags = Phonon.IPL_SIMULATIONFLAGS_REFLECTIONS };
                if (Phonon.iplSourceCreate(_simulator, ref ss, out _source) == Phonon.IPL_STATUS_SUCCESS)
                {
                    Phonon.iplSourceAdd(_source, _simulator);
                    Phonon.iplSimulatorCommit(_simulator);
                }
            }
            _haveScene = _source != IntPtr.Zero;
        }
        if (_haveScene && _thread == null)
        {
            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "TracedReverb", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }
        Current = this;
    }

    /// <summary>Where the listener is. Game or worker thread.</summary>
    public void SetListener(Vector3 at) { lock (_gate) _listener = at; }

    private void Loop()
    {
        while (_running)
        {
            try
            {
                Vector3 at;
                lock (_gate) at = _listener;
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                lock (_gate)
                {
                    if (!_haveScene) { Thread.Sleep(RefreshMs); continue; }
                    var coord = Coord(at);
                    var shared = new Phonon.IPLSimulationSharedInputs
                    {
                        listener = coord, numRays = Rays, numBounces = Bounces,
                        duration = DurationSeconds, order = Order, irradianceMinDistance = 1.0f,
                    };
                    var inputs = new Phonon.IPLSimulationInputs
                    {
                        flags = Phonon.IPL_SIMULATIONFLAGS_REFLECTIONS,
                        source = coord,
                        reverbScale0 = 1f, reverbScale1 = 1f, reverbScale2 = 1f,
                    };
                    Phonon.iplSourceSetInputs(_source, Phonon.IPL_SIMULATIONFLAGS_REFLECTIONS, ref inputs);
                    Phonon.iplSimulatorSetSharedInputs(_simulator, Phonon.IPL_SIMULATIONFLAGS_REFLECTIONS, ref shared);
                    Phonon.iplSimulatorRunReflections(_simulator);
                }
                LastRunMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                Runs++;
            }
            catch (Exception ex) { Serilog.Log.Warning(ex, "Traced reverb: a trace failed."); }
            Thread.Sleep(RefreshMs);
        }
    }

    /// <summary>The latest traced IR, for the effect. Mixer thread; Steam Audio double-buffers it.</summary>
    public bool TryGetParams(out Phonon.IPLReflectionEffectParams p)
    {
        p = default;
        if (_source == IntPtr.Zero) return false;
        var outs = new Phonon.IPLSimulationOutputs();
        Phonon.iplSourceGetOutputs(_source, Phonon.IPL_SIMULATIONFLAGS_REFLECTIONS, ref outs);
        p = outs.reflections;
        if (p.ir == IntPtr.Zero) return false;
        p.type = Phonon.IPL_REFLECTIONEFFECTTYPE_CONVOLUTION;
        p.numChannels = Channels;
        p.irSize = IrSize;
        return true;
    }

    private static Phonon.IPLCoordinateSpace3 Coord(Vector3 origin) => new()
    {
        right = new Phonon.IPLVector3 { x = 1, y = 0, z = 0 },
        up = new Phonon.IPLVector3 { x = 0, y = 1, z = 0 },
        ahead = new Phonon.IPLVector3 { x = 0, y = 0, z = -1 },
        origin = new Phonon.IPLVector3 { x = origin.X, y = origin.Y, z = origin.Z },
    };

    public void Dispose()
    {
        _running = false;
        _thread?.Join(2000);
        if (ReferenceEquals(Current, this)) Current = null;
        lock (_gate)
        {
            if (_source != IntPtr.Zero) Phonon.iplSourceRelease(ref _source);
            if (_simulator != IntPtr.Zero) Phonon.iplSimulatorRelease(ref _simulator);
        }
    }
}

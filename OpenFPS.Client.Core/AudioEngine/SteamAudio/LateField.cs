using System;
using System.Numerics;
using System.Threading;

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// How much late sound each source raises where the listener stands, and from which way it comes:
/// traced from where each source really is, energy only.
///
/// Why it exists. The tail of the place you stand in is ONE response, traced from your own position
/// (TracedReverb, LateTailIr), and every source is played through it. In a room that is nearly right:
/// a room's late field barely depends on where the sound started (its late energy arrives from
/// everywhere, |I|/E 0.07 in flat 01F). In a tunnel or a street it is wrong. There the late energy
/// falls along the length and comes mostly from the source's side (|I|/E 0.4-0.85), and the send's
/// stand-in for it, the distance raised to the measured enclosure, made a car 45 m down the tunnel
/// ring nearly as loud as one at 5 m (1.8 dB apart where the trace says 7), from all round the head
/// (2026-09-29). Here each source is traced: its late energy against a source at the listener's own
/// position in the same simulation (so the ratio needs nothing else to be calibrated), and the
/// direction that energy arrives from.
///
/// The rays are shot once from the listener and serve every source (as in TracedEchoes), so sixteen
/// sources cost little more than one. Each IR is read back through a private convolution, like the
/// listener's (TracedReverb.ExtractLate).
/// </summary>
internal sealed class LateField : IDisposable
{
    public const int MaxSources = 16;
    public const float DurationSeconds = 1.0f;
    public const int Order = 1, Channels = 4;
    /// <summary>Enough bounces for a second in a long concrete space (a 7 m mean free path).</summary>
    private const int Rays = 4096, Bounces = 48;
    private const int RefreshMs = 400;
    private const int Frame = 1024;

    /// <summary>One source's answer. <see cref="Ratio"/>: its late energy against a source at the
    /// listener's position. <see cref="Direction"/>: the way it arrives, game world, unit.
    /// <see cref="Directivity"/>: |I|/E, 0 from everywhere, 1 from one way.</summary>
    public readonly record struct Answer(int Id, float Ratio, Vector3 Direction, float Directivity, Vector3 At, long When);

    public IntPtr Context { get; }
    public int SampleRate { get; }
    public int IrSize => (int)(DurationSeconds * SampleRate);

    private IntPtr _simulator;
    // Slot 0 is the reference, at the listener; slots 1.. the requested sources.
    private readonly IntPtr[] _sources = new IntPtr[MaxSources + 1];
    private readonly object _gate = new(), _simGate = new();
    private Vector3 _listener;
    private readonly int[] _wantId = new int[MaxSources];
    private readonly Vector3[] _wantAt = new Vector3[MaxSources];
    private int _wantCount;
    private readonly Answer[] _answers = new Answer[MaxSources];
    private readonly int[] _answerId = new int[MaxSources];
    private volatile int _answerCount;
    private Thread? _thread;
    private volatile bool _running;
    private bool _haveScene;
    private IntPtr _effect;
    private Phonon.IPLAudioBuffer _in, _out;
    private float[] _mono = Array.Empty<float>(), _inter = Array.Empty<float>(), _w = Array.Empty<float>(), _y = Array.Empty<float>(), _z = Array.Empty<float>(), _x = Array.Empty<float>();
    /// <summary>First-order channels to a direction in Steam Audio's world, measured through its own
    /// encoder at creation rather than assumed (<see cref="CalibrateAxes"/>).</summary>
    private Vector3 _axisY, _axisZ, _axisX;

    public int Runs;
    public double LastRunMs;

    public LateField(IntPtr context, int sampleRate = 44100)
    {
        Context = context; SampleRate = sampleRate;
        var s = new Phonon.IPLSimulationSettings
        {
            flags = Phonon.IPL_SIMULATIONFLAGS_REFLECTIONS,
            sceneType = Phonon.IPL_SCENETYPE_DEFAULT,
            reflectionType = Phonon.IPL_REFLECTIONEFFECTTYPE_CONVOLUTION,
            maxNumOcclusionSamples = 16, maxNumRays = Rays, numDiffuseSamples = 32,
            maxDuration = DurationSeconds, maxOrder = Order, maxNumSources = MaxSources + 1, numThreads = 1,
            rayBatchSize = 16, numVisSamples = 4, samplingRate = sampleRate, frameSize = Frame,
        };
        if (Phonon.iplSimulatorCreate(context, ref s, out _simulator) != Phonon.IPL_STATUS_SUCCESS)
            _simulator = IntPtr.Zero;
        if (IsValid) CalibrateAxes();
    }

    public bool IsValid => _simulator != IntPtr.Zero;

    public void SetScene(SteamAudioScene scene)
    {
        if (!IsValid || !scene.IsBuilt) return;
        lock (_simGate)
        {
            Phonon.iplSimulatorSetScene(_simulator, scene.Handle);
            for (int i = 0; i <= MaxSources; i++)
            {
                if (_sources[i] != IntPtr.Zero) continue;
                var ss = new Phonon.IPLSourceSettings { flags = Phonon.IPL_SIMULATIONFLAGS_REFLECTIONS };
                if (Phonon.iplSourceCreate(_simulator, ref ss, out IntPtr src) != Phonon.IPL_STATUS_SUCCESS) continue;
                Phonon.iplSourceAdd(src, _simulator);
                _sources[i] = src;
            }
            Phonon.iplSimulatorCommit(_simulator);
            _haveScene = true;
        }
        if (_thread == null)
        {
            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "LateField", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }
    }

    /// <summary>Where the listener is and which sources to trace, loudest first. Game or audio
    /// thread; takes only the request lock.</summary>
    public void Want(Vector3 listener, ReadOnlySpan<int> ids, ReadOnlySpan<Vector3> at)
    {
        lock (_gate)
        {
            _listener = listener;
            _wantCount = Math.Min(MaxSources, Math.Min(ids.Length, at.Length));
            for (int i = 0; i < _wantCount; i++) { _wantId[i] = ids[i]; _wantAt[i] = at[i]; }
        }
    }

    /// <summary>The latest answer for a source, if it was traced.</summary>
    public bool TryGet(int id, out Answer a)
    {
        int n = _answerCount;
        for (int i = 0; i < n; i++)
            if (Volatile.Read(ref _answerId[i]) == id) { a = _answers[i]; return a.When != 0; }
        a = default;
        return false;
    }

    /// <summary>Every current answer, for fitting the place's own law to the sources not traced.</summary>
    public int CopyAnswers(Span<Answer> into)
    {
        int n = Math.Min(_answerCount, into.Length);
        for (int i = 0; i < n; i++) into[i] = _answers[i];
        return n;
    }

    private void Loop()
    {
        var ids = new int[MaxSources];
        var at = new Vector3[MaxSources];
        while (_running)
        {
            try
            {
                Vector3 listener; int count;
                lock (_gate)
                {
                    listener = _listener; count = _wantCount;
                    Array.Copy(_wantId, ids, count); Array.Copy(_wantAt, at, count);
                }
                if (!_haveScene || count == 0) { Thread.Sleep(RefreshMs); continue; }
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                lock (_simGate) Trace(listener, ids, at, count);
                LastRunMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                Runs++;
            }
            catch (Exception ex) { Serilog.Log.Warning(ex, "Late field: a trace failed."); }
            Thread.Sleep(RefreshMs);
        }
    }

    private void Trace(Vector3 listener, int[] ids, Vector3[] at, int count)
    {
        var shared = new Phonon.IPLSimulationSharedInputs
        {
            listener = Coord(listener), numRays = Rays, numBounces = Bounces,
            duration = DurationSeconds, order = Order, irradianceMinDistance = 1.0f,
        };
        for (int i = 0; i <= MaxSources; i++)
        {
            if (_sources[i] == IntPtr.Zero) continue;
            bool live = i == 0 || i - 1 < count;
            var inputs = new Phonon.IPLSimulationInputs
            {
                flags = live ? Phonon.IPL_SIMULATIONFLAGS_REFLECTIONS : 0,
                source = Coord(i == 0 ? listener : at[i - 1]),
                reverbScale0 = 1f, reverbScale1 = 1f, reverbScale2 = 1f,
                hybridReverbTransitionTime = DurationSeconds, hybridReverbOverlapPercent = 0.25f,
            };
            Phonon.iplSourceSetInputs(_sources[i], Phonon.IPL_SIMULATIONFLAGS_REFLECTIONS, ref inputs);
        }
        Phonon.iplSimulatorSetSharedInputs(_simulator, Phonon.IPL_SIMULATIONFLAGS_REFLECTIONS, ref shared);
        Phonon.iplSimulatorRunReflections(_simulator);

        if (!Measure(0, out float eRef, out _, out _) || eRef <= 1e-12f) return;
        long now = DateTime.UtcNow.Ticks;
        int written = 0;
        for (int i = 0; i < count; i++)
        {
            if (!Measure(i + 1, out float e, out Vector3 dir, out float directivity)) continue;
            _answers[written] = new Answer(ids[i], e / eRef, dir, directivity, at[i], now);
            Volatile.Write(ref _answerId[written], ids[i]);
            written++;
        }
        _answerCount = written;
    }

    /// <summary>
    /// One source's late energy (the omnidirectional channel, in the late tail's own window, from
    /// LateTailIr's fade-in on) and the direction of its late intensity, read back through the
    /// private convolution.
    /// </summary>
    private bool Measure(int slot, out float energy, out Vector3 direction, out float directivity)
    {
        energy = 0f; direction = Vector3.Zero; directivity = 0f;
        var src = _sources[slot];
        if (src == IntPtr.Zero) return false;
        var outs = new Phonon.IPLSimulationOutputs();
        Phonon.iplSourceGetOutputs(src, Phonon.IPL_SIMULATIONFLAGS_REFLECTIONS, ref outs);
        var prm = outs.reflections;
        if (prm.ir == IntPtr.Zero) return false;
        prm.type = Phonon.IPL_REFLECTIONEFFECTTYPE_CONVOLUTION; prm.numChannels = Channels; prm.irSize = IrSize;
        if (!ReadBack(prm)) return false;

        int a = (int)(LateTailIr.FadeInStartSeconds * SampleRate), b = (int)(LateTailIr.FadeInEndSeconds * SampleRate);
        double e = 0, iy = 0, iz = 0, ix = 0;
        for (int i = a; i < _w.Length; i++)
        {
            double win = i >= b ? 1.0 : 0.5 - 0.5 * Math.Cos(Math.PI * (i - a) / Math.Max(1, b - a));
            double wv = _w[i] * win;
            e += wv * wv;
            iy += wv * _y[i] * win; iz += wv * _z[i] * win; ix += wv * _x[i] * win;
        }
        energy = (float)e;
        if (e <= 0) return true;
        var saDir = (float)(iy / e) * _axisY + (float)(iz / e) * _axisZ + (float)(ix / e) * _axisX;
        float len = saDir.Length();
        // Steam Audio's world back to the game's (Phonon.World mirrors z).
        var game = new Vector3(saDir.X, saDir.Y, Phonon.WorldZ(saDir.Z));
        direction = len > 1e-6f ? game / len : Vector3.Zero;
        directivity = Math.Clamp(len, 0f, 1f);
        return true;
    }

    private bool ReadBack(Phonon.IPLReflectionEffectParams prm)
    {
        if (_effect == IntPtr.Zero)
        {
            var au = new Phonon.IPLAudioSettings { samplingRate = SampleRate, frameSize = Frame };
            var es = new Phonon.IPLReflectionEffectSettings { type = Phonon.IPL_REFLECTIONEFFECTTYPE_CONVOLUTION, irSize = IrSize, numChannels = Channels };
            if (Phonon.iplReflectionEffectCreate(Context, ref au, ref es, out _effect) != Phonon.IPL_STATUS_SUCCESS) { _effect = IntPtr.Zero; return false; }
            Phonon.iplAudioBufferAllocate(Context, 1, Frame, ref _in);
            Phonon.iplAudioBufferAllocate(Context, Channels, Frame, ref _out);
            _mono = new float[Frame]; _inter = new float[Frame * Channels];
            int n = (IrSize / Frame) * Frame;
            _w = new float[n]; _y = new float[n]; _z = new float[n]; _x = new float[n];
        }
        Phonon.iplReflectionEffectReset(_effect);
        int frames = IrSize / Frame;
        for (int b = -2; b < frames; b++)
        {
            Array.Clear(_mono);
            if (b == 0) _mono[0] = 1f;
            Phonon.iplAudioBufferDeinterleave(Context, _mono, ref _in);
            Phonon.iplReflectionEffectApply(_effect, ref prm, ref _in, ref _out, IntPtr.Zero);
            if (b < 0) continue;
            Phonon.iplAudioBufferInterleave(Context, ref _out, _inter);
            for (int k = 0; k < Frame; k++)
            {
                int i = b * Frame + k, o = k * Channels;
                _w[i] = _inter[o]; _y[i] = _inter[o + 1]; _z[i] = _inter[o + 2]; _x[i] = _inter[o + 3];
            }
        }
        return true;
    }

    /// <summary>
    /// Which world direction each first-order channel stands for, and with what sign, by encoding a
    /// sound from each axis through Steam Audio's own encoder and reading the channels back. The
    /// intensity vector is then sum(channel × its axis); nothing about the channel order or the
    /// handedness of the frame is assumed.
    /// </summary>
    private void CalibrateAxes()
    {
        var au = new Phonon.IPLAudioSettings { samplingRate = SampleRate, frameSize = 64 };
        var es = new Phonon.IPLAmbisonicsEncodeEffectSettings { maxOrder = Order };
        if (Phonon.iplAmbisonicsEncodeEffectCreate(Context, ref au, ref es, out IntPtr enc) != Phonon.IPL_STATUS_SUCCESS) return;
        var inB = new Phonon.IPLAudioBuffer(); var outB = new Phonon.IPLAudioBuffer();
        Phonon.iplAudioBufferAllocate(Context, 1, 64, ref inB);
        Phonon.iplAudioBufferAllocate(Context, Channels, 64, ref outB);
        var ones = new float[64]; Array.Fill(ones, 1f);
        var inter = new float[64 * Channels];
        var axes = new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ };
        var gains = new float[3, 3];   // [axis, channel 1..3]
        for (int a = 0; a < 3; a++)
        {
            var dir = new Phonon.IPLVector3 { x = axes[a].X, y = axes[a].Y, z = axes[a].Z };
            var ep = new Phonon.IPLAmbisonicsEncodeEffectParams { direction = dir, order = Order };
            for (int rep = 0; rep < 3; rep++)   // past any gain ramp
            {
                Phonon.iplAudioBufferDeinterleave(Context, ones, ref inB);
                Phonon.iplAmbisonicsEncodeEffectApply(enc, ref ep, ref inB, ref outB);
            }
            Phonon.iplAudioBufferInterleave(Context, ref outB, inter);
            float w = inter[(63) * Channels];
            for (int c = 1; c < 4; c++) gains[a, c - 1] = w != 0 ? inter[63 * Channels + c] / w : 0f;
        }
        // Channel c's axis is the one it answers; normalised so a source straight down that axis
        // reads as |I|/E = 1.
        Vector3 Axis(int c)
        {
            var v = new Vector3(gains[0, c], gains[1, c], gains[2, c]);
            float l2 = v.LengthSquared();
            return l2 > 1e-9f ? v / l2 : Vector3.Zero;
        }
        _axisY = Axis(0); _axisZ = Axis(1); _axisX = Axis(2);
        Phonon.iplAmbisonicsEncodeEffectRelease(ref enc);
        Phonon.iplAudioBufferFree(Context, ref inB); Phonon.iplAudioBufferFree(Context, ref outB);
    }

    private static Phonon.IPLCoordinateSpace3 Coord(Vector3 origin) => new()
    {
        right = new Phonon.IPLVector3 { x = 1, y = 0, z = 0 },
        up = new Phonon.IPLVector3 { x = 0, y = 1, z = 0 },
        ahead = new Phonon.IPLVector3 { x = 0, y = 0, z = -1 },
        origin = Phonon.World(origin),
    };

    public void Dispose()
    {
        _running = false;
        _thread?.Join(2000);
        lock (_simGate)
        {
            for (int i = 0; i <= MaxSources; i++)
                if (_sources[i] != IntPtr.Zero) { Phonon.iplSourceRelease(ref _sources[i]); _sources[i] = IntPtr.Zero; }
            if (_effect != IntPtr.Zero)
            {
                Phonon.iplReflectionEffectRelease(ref _effect);
                Phonon.iplAudioBufferFree(Context, ref _in); Phonon.iplAudioBufferFree(Context, ref _out);
            }
            if (_simulator != IntPtr.Zero) Phonon.iplSimulatorRelease(ref _simulator);
        }
    }
}

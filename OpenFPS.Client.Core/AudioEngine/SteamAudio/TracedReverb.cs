using System.Numerics;
using OpenFPS.Common;

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// The place you are standing in, measured: an impulse response traced through the map's geometry and
/// materials from the listener's own position a few times a second, played through TracedReverbDsp
/// (Steam Audio's "listener-centric reverb"). A street's tail is flutter between facades, scatter and
/// energy lost to the sky, not an algorithmic room's smooth tail; this stands for the copies of copies
/// nobody can voice one by one.
///
/// One simulation for everything, because tracing is the cost: every sound is reverberated as if it
/// were where the listener is. The late field barely depends on where the source stands; the early
/// echoes do, and are voiced separately (EngineReflections, EarlyReflections, TracedEchoes).
/// </summary>
internal sealed class TracedReverb : IDisposable
{
    /// <summary>
    /// The block the traced response is convolved in, samples. The convolution answers one block late,
    /// so this is the room's pre-delay: at the mixer's 1,024 every room's first reflection, a cabin's
    /// included, came 20-23 ms late and the room sounded like a separate space to one side; at 256 it is
    /// 5-6 ms. The mixer's block is run through in pieces of this size (TracedReverbDsp).
    /// </summary>
    public const int TracedFrame = 256;

    public const float DurationSeconds = 2.0f;

    /// <summary>What the simulator produces: HYBRID gives the convolution IR and the reverb times;
    /// CONVOLUTION the IR alone.</summary>
    public static int SimulatedType = Phonon.IPL_REFLECTIONEFFECTTYPE_HYBRID;
    public const int Order = 2;
    public const int Channels = (Order + 1) * (Order + 1);
    /// <summary>Rays and bounces per trace. Sixty-four bounces, not sixteen: in a twelve-metre room
    /// sixteen bounces is a quarter of a second of travel, and the tail stopped dead at half a second
    /// (--traced-reverb) where a hard room rings on.</summary>
    private const int Rays = 8192, Bounces = 64;
    /// <summary>How often the trace is refreshed. Walking is a metre and a half a second; a quarter
    /// second is a third of a metre, and the IR crossfades inside the effect.</summary>
    private const int DefaultRefreshMs = 250;
    private readonly int _refreshMs;

    public IntPtr Context { get; }
    public int SampleRate { get; }
    public int FrameSize { get; }
    public int IrSize => (int)(DurationSeconds * SampleRate);

    private IntPtr _simulator, _source;
    /// <summary>
    /// One source per stage that plays this trace, all at the same point. A Steam Audio IR update is
    /// taken by the first effect that reads it after the trace (--traced-echoes: a second effect stayed
    /// silent), so with one source a car's reverberation came and went as it crossed from one region's
    /// bus to another's, the reflections "cutting out".
    /// </summary>
    public const int MaxReaders = 24;
    private readonly IntPtr[] _readers = new IntPtr[MaxReaders];
    private bool _readersDirty;
    private readonly object _gate = new();
    private Thread? _thread;
    private volatile bool _running;
    private Vector3 _listener;
    private bool _haveScene;

    /// <summary>Diagnostics: runs so far and the last run's cost.</summary>
    public int Runs;
    public double LastRunMs;

    /// <summary>
    /// Read the traced IR back after every trace and publish the tail of the place you stand in
    /// (<see cref="Late"/>, through SmoothTail). The SDK keeps the IR opaque, so an impulse is pushed
    /// through a private convolution on this thread, on a reader of its own (<see cref="ExtractReader"/>)
    /// so the mixer's stages do not lose their update. Set before <see cref="SetScene"/>.
    /// </summary>
    public bool ExtractLate;
    /// <summary>The late part to play, time zero at the direct sound, from the traces' averaged energy
    /// (SmoothTail). Null until the first trace.</summary>
    public volatile LateTailIr? Late;
    /// <summary>The last trace's omnidirectional channel as read back, whole and unwindowed: for the lab.</summary>
    public volatile float[]? LastReadBack;
    /// <summary>The tail's directional part (SdmTailIr), from the first reflection to 0.25-0.35 s;
    /// <see cref="Late"/> is then only the remainder after it. Until the axes are known, Late is all of
    /// it.</summary>
    public volatile SdmTailIr? LateSdm;
    /// <summary>The late part after the directional one as a field, one independent noise per
    /// direction (DiffuseLate), from the traces' averaged energy. Null without directions or with
    /// <see cref="RawTail"/>: then <see cref="Late"/> is played as one channel.</summary>
    public volatile DiffuseLateIr? DiffuseLate;
    private System.Numerics.Vector3 _ax1, _ax2, _ax3;
    private bool _axesKnown;

    /// <summary>The trace's tail as energy, averaged over traces (SmoothTail). Made on the first
    /// read-back.</summary>
    private SmoothTail? _smooth;
    /// <summary>The lab's A/B: play each trace's own samples, as before SmoothTail. Never set in the game.</summary>
    public static bool RawTail;
    /// <summary>The lab's A/B: the late part as one channel through the velvet branches, as before
    /// DiffuseLate. Never set in the game.</summary>
    public static bool OneChannelLate;
    /// <summary>
    /// The lab's latency probe: past zero, every trace publishes instead one click this many seconds
    /// into the response, from one direction, and nothing else. Where it lands in a capture against
    /// the dry sound is what the bus path adds. Never set in the game.
    /// </summary>
    public static float LabProbeSeconds;
    /// <summary>The scene's boxes, for the placed copies' share of the early energy (EarlyCopies).</summary>
    private IReadOnlyList<EarlyReflections.Solid> _solids = Array.Empty<EarlyReflections.Solid>();

    private void PublishProbe(float seconds)
    {
        int at = (int)(seconds * SampleRate);
        int k = DiffuseBranch.Count, maxP = SdmTailIr.PartitionsFor(SampleRate, FrameSize);
        var parts = new float[k][];
        for (int d = 0; d < k; d++) parts[d] = new float[maxP * FrameSize];
        if (at < parts[0].Length) parts[0][at] = 1f;
        LateSdm = SdmTailIr.FromParts(parts, FrameSize, maxP);
        Late = LateTailIr.FromWindowed(new float[FrameSize], FrameSize, MaxLatePartitions);
        DiffuseLate = null;
    }
    /// <summary>The lab: also publish each trace's raw parts (<see cref="RawLate"/>, <see cref="RawLateSdm"/>).</summary>
    public bool KeepRaw;
    public volatile LateTailIr? RawLate;
    public volatile SdmTailIr? RawLateSdm;
    /// <summary>The smoothing, for the lab: the averaged energies and how the last trace counted.</summary>
    public SmoothTail? Smooth => _smooth;
    /// <summary>The scene changed since the last trace (a door moved): the average follows faster.</summary>
    private volatile bool _sceneChanged;
    private float[] _c1 = Array.Empty<float>(), _c2 = Array.Empty<float>(), _c3 = Array.Empty<float>();

    // ── Where the remainder arrives from ─────────────────────────────────────────────────────────
    //
    // Averaged over a stretch of the trace, W times each channel is the coefficient of the energy
    // arriving from each direction in the same spherical harmonics. At first order (the intensity) a
    // corridor's two ends cancel; at second order they add. So the remainder is rebuilt from all nine:
    // f(u) = 1 + sum_c (<W ch_c> / <W W>) g_c(u) / <g_c^2>, g_c(u) each channel's gain from u and <g_c^2>
    // its mean over the sphere, both measured through Steam Audio's encoder. A direction per sample, as
    // the early part uses, put 25 % of a 60 m corridor's late energy along its axis, less than even.
    private readonly double[] _lateCov = new double[Channels];
    private float[,]? _dirGains;          // [direction, channel]
    private float[]? _chanPower;          // mean g_c^2 over the sphere

    private void CalibrateSphere()
    {
        int k = DiffuseBranch.Count;
        var au = new Phonon.IPLAudioSettings { samplingRate = SampleRate, frameSize = 64 };
        var es = new Phonon.IPLAmbisonicsEncodeEffectSettings { maxOrder = Order };
        if (Phonon.iplAmbisonicsEncodeEffectCreate(Context, ref au, ref es, out IntPtr enc) != Phonon.IPL_STATUS_SUCCESS) return;
        var inB = new Phonon.IPLAudioBuffer(); var outB = new Phonon.IPLAudioBuffer();
        Phonon.iplAudioBufferAllocate(Context, 1, 64, ref inB); Phonon.iplAudioBufferAllocate(Context, Channels, 64, ref outB);
        var ones = new float[64]; Array.Fill(ones, 1f); var inter = new float[64 * Channels];
        float[] Gains(System.Numerics.Vector3 gameDir)
        {
            var ep = new Phonon.IPLAmbisonicsEncodeEffectParams { direction = Phonon.World(gameDir), order = Order };
            for (int rep = 0; rep < 3; rep++)
            {
                Phonon.iplAudioBufferDeinterleave(Context, ones, ref inB);
                Phonon.iplAmbisonicsEncodeEffectApply(enc, ref ep, ref inB, ref outB);
            }
            Phonon.iplAudioBufferInterleave(Context, ref outB, inter);
            var g = new float[Channels]; float w = inter[63 * Channels];
            for (int c = 0; c < Channels; c++) g[c] = w != 0 ? inter[63 * Channels + c] / w : 0f;
            return g;
        }
        var dg = new float[k, Channels];
        for (int d = 0; d < k; d++) { var g = Gains(DiffuseTail.Direction(d)); for (int c = 0; c < Channels; c++) dg[d, c] = g[c]; }
        var pw = new double[Channels]; const int N = 200;
        for (int i = 0; i < N; i++)
        {
            double y = 1 - 2 * (i + 0.5) / N, r = Math.Sqrt(1 - y * y), th = i * Math.PI * (3 - Math.Sqrt(5));
            var g = Gains(new System.Numerics.Vector3((float)(r * Math.Cos(th)), (float)y, (float)(r * Math.Sin(th))));
            for (int c = 0; c < Channels; c++) pw[c] += g[c] * (double)g[c] / N;
        }
        _chanPower = new float[Channels]; for (int c = 0; c < Channels; c++) _chanPower[c] = (float)pw[c];
        _dirGains = dg;
        Phonon.iplAmbisonicsEncodeEffectRelease(ref enc);
        Phonon.iplAudioBufferFree(Context, ref inB); Phonon.iplAudioBufferFree(Context, ref outB);
    }

    /// <summary>The remainder's energy at each direction, shares summing to one (see above), from the
    /// channel covariances <paramref name="cov"/>.</summary>
    private void FillLateShares(double[] cov, float[] into)
    {
        int k = into.Length;
        if (_dirGains == null || _chanPower == null || cov[0] <= 0) { for (int d = 0; d < k; d++) into[d] = 1f / k; return; }
        double sum = 0;
        var f = new double[k];
        for (int d = 0; d < k; d++)
        {
            double v = 1.0;
            for (int c = 1; c < Channels; c++)
                if (_chanPower[c] > 1e-9f) v += cov[c] / cov[0] * _dirGains[d, c] / _chanPower[c];
            f[d] = Math.Max(0, v); sum += f[d];
        }
        for (int d = 0; d < k; d++) into[d] = sum > 0 ? (float)(f[d] / sum) : 1f / k;
    }
    /// <summary>What reading it back cost, last time; and of that, measuring, averaging and building
    /// the smoothed tail (SmoothTail).</summary>
    public double LastExtractMs, LastSmoothMs;
    /// <summary>The reader the extraction uses, never a mixer stage's.</summary>
    public const int ExtractReader = MaxReaders - 1;
    /// <summary>Most partitions a late part may have: the whole trace in blocks of the traced frame.</summary>
    public int MaxLatePartitions => IrSize / FrameSize + 1;
    /// <summary>The late field's convolver for a stage of <paramref name="sub"/>-sample pieces (DiffuseLate).</summary>
    public DiffuseLateConvolver NewDiffuseLateConvolver(int sub)
        => new(sub, DiffuseLateNoise.PartitionsFor(SampleRate, IrSize), DiffuseBranch.Count, DiffuseLateNoise.StartFor(SampleRate));
    private IntPtr _extractEffect;
    private Phonon.IPLAudioBuffer _extractIn, _extractOut;
    private float[] _extractMono = Array.Empty<float>(), _extractInter = Array.Empty<float>();

    /// <param name="context">The Steam Audio context; the tracer makes a simulator of its own.</param>
    /// <param name="sampleRate">0: the mixer's (MixerQuality.MixerRate).</param>
    /// <param name="frameSize">The convolution's block (<see cref="TracedFrame"/>).</param>
    /// <param name="refreshMs">How often the trace is redone: a quarter second for the listener,
    /// who walks; a second for a room traced from its middle, which does not move.</param>
    public TracedReverb(IntPtr context, int sampleRate = 0, int frameSize = TracedFrame, int refreshMs = DefaultRefreshMs)
    {
        if (sampleRate <= 0) sampleRate = OpenFPS.Client.AudioEngine.Fmod.MixerQuality.MixerRate;
        _refreshMs = refreshMs;
        Context = context; SampleRate = sampleRate; FrameSize = frameSize;
        var s = new Phonon.IPLSimulationSettings
        {
            flags = Phonon.IPL_SIMULATIONFLAGS_REFLECTIONS,
            sceneType = SteamAudioScene.TypeFor(context),
            reflectionType = SimulatedType,
            maxNumOcclusionSamples = 16, maxNumRays = Rays, numDiffuseSamples = 32,
            maxDuration = DurationSeconds, maxOrder = Order, maxNumSources = MaxReaders, numThreads = 2,
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
            // A new scene is a changed room (a door moved): the averaged tail follows it faster.
            if (_haveScene) _sceneChanged = true;
            _solids = scene.Solids;
            Phonon.iplSimulatorSetScene(_simulator, scene.Handle);
            Phonon.iplSimulatorCommit(_simulator);
            if (_source == IntPtr.Zero)
            {
                var ss = new Phonon.IPLSourceSettings { flags = Phonon.IPL_SIMULATIONFLAGS_REFLECTIONS };
                if (Phonon.iplSourceCreate(_simulator, ref ss, out _source) == Phonon.IPL_STATUS_SUCCESS)
                {
                    Phonon.iplSourceAdd(_source, _simulator);
                    Phonon.iplSimulatorCommit(_simulator);
                    _readers[0] = _source;
                }
            }
            _haveScene = _source != IntPtr.Zero;
            if (_haveScene && ExtractLate && _readers[ExtractReader] == IntPtr.Zero)
            {
                var es = new Phonon.IPLSourceSettings { flags = Phonon.IPL_SIMULATIONFLAGS_REFLECTIONS };
                if (Phonon.iplSourceCreate(_simulator, ref es, out IntPtr xs) == Phonon.IPL_STATUS_SUCCESS)
                {
                    Phonon.iplSourceAdd(xs, _simulator);
                    Phonon.iplSimulatorCommit(_simulator);
                    _readers[ExtractReader] = xs;
                }
            }
        }
        if (_haveScene && _thread == null)
        {
            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "TracedReverb", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }
    }

    /// <summary>Asks for reader <paramref name="index"/> to have its own source. Never waits: a trace
    /// holds the tracer's lock for hundreds of milliseconds in a hard hall, and the audio thread asking
    /// here held every source still that long. The tracer makes the source before its next trace, which
    /// the asking brings forward.</summary>
    public void EnsureReader(int index)
    {
        if (index <= 0 || index >= MaxReaders || _readers[index] != IntPtr.Zero || !IsValid) return;
        int bit = 1 << index;
        if ((Interlocked.Or(ref _wantedReaders, bit) & bit) == 0) _wake.Set();
    }
    private int _wantedReaders;
    private readonly ManualResetEventSlim _wake = new(false);

    /// <summary>Whether a trace has run since reader <paramref name="reader"/> had its source: until then
    /// a stage reading it gets nothing and plays silence.</summary>
    public bool HasTraced(int reader)
        => reader >= 0 && reader < MaxReaders && (Volatile.Read(ref _tracedReaders) & (1 << reader)) != 0;
    private int _tracedReaders;

    /// <summary>Traced once and then left alone until asked again: a cabin made ahead of riding in it.</summary>
    public volatile bool Idle;

    /// <summary>A vehicle's cabin, traced in its own frame (TracedReverbSet.RideIn): played whole, at its
    /// traced level.</summary>
    public bool IsCabin { get; init; }

    /// <summary>Makes the sources asked for (EnsureReader). Tracer thread, under the tracer's lock.</summary>
    private void AddWantedReaders()
    {
        int wanted = Interlocked.Exchange(ref _wantedReaders, 0);
        for (int index = 1; wanted != 0 && index < MaxReaders; index++)
        {
            if ((wanted & (1 << index)) == 0 || _readers[index] != IntPtr.Zero) continue;
            var ss = new Phonon.IPLSourceSettings { flags = Phonon.IPL_SIMULATIONFLAGS_REFLECTIONS };
            if (Phonon.iplSourceCreate(_simulator, ref ss, out IntPtr src) != Phonon.IPL_STATUS_SUCCESS) continue;
            Phonon.iplSourceAdd(src, _simulator);
            _readersDirty = true;
            _readers[index] = src;
        }
    }

    /// <summary>Where the listener is. Game or worker thread, under its own lock, never the tracer's:
    /// a trace holds that for hundreds of milliseconds in a hard hall, and every frame waiting on it
    /// stalled every sound 680 ms at a time in the airport terminal.</summary>
    public void SetListener(Vector3 at) { lock (_listenerGate) _listener = at; }

    /// <summary>Where the listener is and the region they are in. A new region starts the averaged
    /// tail again (SmoothTail): one room must not smear into the next.</summary>
    public void SetListener(Vector3 at, int place) { lock (_listenerGate) { _listener = at; _place = place; } }
    private readonly object _listenerGate = new();
    private int _place = int.MinValue;

    private void Loop()
    {
        // Priority = BelowNormal is a placebo on Linux; nice the thread so tracing loses to the mixer.
        OpenFPS.Client.Core.Platform.BackgroundPriority.LowerThisThread("TracedReverb");
        while (_running)
        {
            try
            {
                if (Idle && Runs > 0 && Volatile.Read(ref _wantedReaders) == 0) { _wake.Wait(_refreshMs); _wake.Reset(); continue; }
                Vector3 at; int place;
                lock (_listenerGate) { at = _listener; place = _place; }
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                lock (_gate)
                {
                    if (!_haveScene) { Thread.Sleep(_refreshMs); continue; }
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
                        hybridReverbTransitionTime = DurationSeconds, hybridReverbOverlapPercent = 0.25f,
                    };
                    AddWantedReaders();
                    if (_readersDirty) { Phonon.iplSimulatorCommit(_simulator); _readersDirty = false; }
                    foreach (var r in _readers)
                        if (r != IntPtr.Zero) Phonon.iplSourceSetInputs(r, Phonon.IPL_SIMULATIONFLAGS_REFLECTIONS, ref inputs);
                    Phonon.iplSimulatorSetSharedInputs(_simulator, Phonon.IPL_SIMULATIONFLAGS_REFLECTIONS, ref shared);
                    Phonon.iplSimulatorRunReflections(_simulator);
                    int traced = 0;
                    for (int r = 0; r < MaxReaders; r++) if (_readers[r] != IntPtr.Zero) traced |= 1 << r;
                    Volatile.Write(ref _tracedReaders, traced);
                }
                LastRunMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                Runs++;
                if (ExtractLate)
                {
                    long x0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    if (ReadBack() is { } w)
                    {
                        LastReadBack = w;
                        System.Numerics.Vector3[]? dirs = null;
                        if (_axesKnown)
                        {
                            dirs = new System.Numerics.Vector3[DiffuseBranch.Count];
                            for (int d = 0; d < dirs.Length; d++) dirs[d] = DiffuseTail.Direction(d);
                        }
                        bool sceneChanged = _sceneChanged;
                        _sceneChanged = false;
                        if (RawTail || KeepRaw)
                        {
                            // Each trace's own samples (the lab's raw tail).
                            SdmTailIr? rawSdm = null;
                            LateTailIr rawLate;
                            if (dirs != null)
                            {
                                rawSdm = SdmTailIr.Build(w, _c1, _c2, _c3, _ax1, _ax2, _ax3, dirs, SampleRate, FrameSize);
                                FillLateShares(_lateCov, rawSdm.LateShare);
                                rawLate = LateTailIr.Build(w, SampleRate, FrameSize, MaxLatePartitions,
                                                           SdmTailIr.EndFadeStart, SdmTailIr.EndFadeEnd);
                            }
                            else rawLate = LateTailIr.Build(w, SampleRate, FrameSize, MaxLatePartitions);
                            RawLateSdm = rawSdm; RawLate = rawLate;
                            if (RawTail) { LateSdm = rawSdm; Late = rawLate; DiffuseLate = null; }
                        }
                        if (!RawTail)
                        {
                            // The tail as energy, averaged, through fixed noise (SmoothTail), from the
                            // first reflection on, less what the placed copies carry (EarlyCopies).
                            long s0 = System.Diagnostics.Stopwatch.GetTimestamp();
                            _smooth ??= new SmoothTail(SampleRate, IrSize, DiffuseBranch.Count);
                            var copies = _solids.Count > 0 ? EarlyCopies.From(_solids, at, SampleRate) : null;
                            _smooth.Add(w, _c1, _c2, _c3, _ax1, _ax2, _ax3, dirs, _lateCov, at, place, sceneChanged, copies);
                            if (dirs != null)
                            {
                                var sdm = _smooth.BuildDirectional(FrameSize);
                                FillLateShares(_smooth.Cov, sdm.LateShare);
                                DiffuseLate = OneChannelLate ? null : _smooth.BuildDiffuseLate(DiffuseLateNoise.Shared(SampleRate, IrSize, DiffuseBranch.Count));
                                LateSdm = sdm;
                                Late = _smooth.BuildLate(FrameSize, MaxLatePartitions, afterDirectional: true);
                            }
                            else { Late = _smooth.BuildLate(FrameSize, MaxLatePartitions, afterDirectional: false); DiffuseLate = null; }
                            LastSmoothMs = (System.Diagnostics.Stopwatch.GetTimestamp() - s0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                        }
                        if (LabProbeSeconds > 0f) PublishProbe(LabProbeSeconds);
                    }
                    LastExtractMs = (System.Diagnostics.Stopwatch.GetTimestamp() - x0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                }
            }
            catch (Exception ex) { Serilog.Log.Warning(ex, "Traced reverb: a trace failed."); }
            // Woken early by a stage asking for its reader (EnsureReader), so a new room is heard within a trace.
            _wake.Wait(_refreshMs);
            _wake.Reset();
        }
    }

    /// <summary>The latest traced IR, for the effect. Mixer thread; Steam Audio double-buffers it.</summary>
    public bool TryGetParams(out Phonon.IPLReflectionEffectParams p) => TryGetParams(0, out p);

    /// <summary>The latest traced IR as reader <paramref name="reader"/> gets it. Each stage reads its
    /// own; see <see cref="MaxReaders"/>.</summary>
    public bool TryGetParams(int reader, out Phonon.IPLReflectionEffectParams p)
    {
        p = default;
        IntPtr src = reader >= 0 && reader < MaxReaders ? _readers[reader] : IntPtr.Zero;
        if (src == IntPtr.Zero) return false;
        var outs = new Phonon.IPLSimulationOutputs();
        Phonon.iplSourceGetOutputs(src, Phonon.IPL_SIMULATIONFLAGS_REFLECTIONS, ref outs);
        p = outs.reflections;
        if (p.ir == IntPtr.Zero) return false;
        p.type = Phonon.IPL_REFLECTIONEFFECTTYPE_CONVOLUTION;
        p.numChannels = Channels;
        p.irSize = IrSize;
        return true;
    }

    /// <summary>
    /// The latest trace's omnidirectional channel, read back through a private convolution: warmed
    /// on silence (an effect crossfades a new IR in over its first block), then one impulse. Tracer
    /// thread only. Null if there is nothing to read yet.
    /// </summary>
    private float[]? ReadBack()
    {
        if (!TryGetParams(ExtractReader, out var prm)) return null;
        int frame = FrameSize, ch = Channels, frames = IrSize / frame;
        if (_extractEffect == IntPtr.Zero)
        {
            var au = new Phonon.IPLAudioSettings { samplingRate = SampleRate, frameSize = frame };
            var es = new Phonon.IPLReflectionEffectSettings { type = Phonon.IPL_REFLECTIONEFFECTTYPE_CONVOLUTION, irSize = IrSize, numChannels = ch };
            if (Phonon.iplReflectionEffectCreate(Context, ref au, ref es, out _extractEffect) != Phonon.IPL_STATUS_SUCCESS)
            { _extractEffect = IntPtr.Zero; return null; }
            Phonon.iplAudioBufferAllocate(Context, 1, frame, ref _extractIn);
            Phonon.iplAudioBufferAllocate(Context, ch, frame, ref _extractOut);
            _extractMono = new float[frame];
            _extractInter = new float[frame * ch];
            (_ax1, _ax2, _ax3) = AmbiAxes.Calibrate(Context, SampleRate);
            CalibrateSphere();
            _axesKnown = _ax1 != System.Numerics.Vector3.Zero || _ax2 != System.Numerics.Vector3.Zero || _ax3 != System.Numerics.Vector3.Zero;
        }
        Phonon.iplReflectionEffectReset(_extractEffect);
        var w = new float[frames * frame];
        _c1 = new float[w.Length]; _c2 = new float[w.Length]; _c3 = new float[w.Length];
        Array.Clear(_lateCov);
        int lateFrom = (int)(SdmTailIr.EndFadeStart * SampleRate);
        for (int b = -4; b < frames; b++)
        {
            Array.Clear(_extractMono);
            if (b == 0) _extractMono[0] = 1f;
            Phonon.iplAudioBufferDeinterleave(Context, _extractMono, ref _extractIn);
            Phonon.iplReflectionEffectApply(_extractEffect, ref prm, ref _extractIn, ref _extractOut, IntPtr.Zero);
            if (b < 0) continue;
            Phonon.iplAudioBufferInterleave(Context, ref _extractOut, _extractInter);
            for (int k = 0; k < frame; k++)
            {
                int i = b * frame + k, o = k * ch;
                w[i] = _extractInter[o];
                _c1[i] = _extractInter[o + 1]; _c2[i] = _extractInter[o + 2]; _c3[i] = _extractInter[o + 3];
                if (i >= lateFrom)
                    for (int c = 0; c < ch; c++) _lateCov[c] += _extractInter[o] * (double)_extractInter[o + c];
            }
        }
        return w;
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
        _wake.Set();
        _thread?.Join(2000);
        lock (_gate)
        {
            for (int i = 1; i < MaxReaders; i++)
                if (_readers[i] != IntPtr.Zero) { Phonon.iplSourceRelease(ref _readers[i]); _readers[i] = IntPtr.Zero; }
            _readers[0] = IntPtr.Zero;
            if (_source != IntPtr.Zero) Phonon.iplSourceRelease(ref _source);
            if (_extractEffect != IntPtr.Zero)
            {
                Phonon.iplReflectionEffectRelease(ref _extractEffect);
                Phonon.iplAudioBufferFree(Context, ref _extractIn);
                Phonon.iplAudioBufferFree(Context, ref _extractOut);
            }
            if (_simulator != IntPtr.Zero) Phonon.iplSimulatorRelease(ref _simulator);
        }
    }
}

/// <summary>
/// Every traced place in use: the listener's own (traced from where they stand, following them) and
/// each other room that can be heard (traced from its own middle, so a sound through a doorway rings
/// with the room it is in, not the one you are in). Built on the acoustic worker's context and scene;
/// asked for by the provider's reverb buses. One per Steam Audio context: the provider holds it
/// (FmodAudioProvider.Traced) and the worker that owns the context is handed it, so two audio worlds
/// can live in one process.
/// </summary>
internal sealed class TracedReverbSet
{
    private readonly object _gate = new();
    private IntPtr _context;
    private SteamAudioScene? _scene;
    /// <summary>Counts every Configure: a room traced against an older scene is handed the new one.</summary>
    private int _sceneVersion;
    private TracedReverb? _listener;
    private TracedEchoes? _echoes;
    private LateField? _late;

    /// <summary>The clock of the world these places are traced for, which whoever holds the set gives it (the
    /// mixer, or a worker with a set of its own): a late answer's age is told on it.</summary>
    public OpenFPS.Common.IAudioClock Clock { get; set; } = new OpenFPS.Common.StopwatchAudioClock();
    private readonly Dictionary<int, (TracedReverb Trace, Vector3 At)> _rooms = new();
    private readonly HashSet<int> _roomsMaking = new();
    /// <summary>At most this many rooms traced besides the listener's; the mixer only ever hears four.</summary>
    private const int MaxRooms = 6;

    // ── Made off the threads that place sounds ───────────────────────────────────────────────
    //
    // A trace's simulator, a cabin's scene: tens of milliseconds, and the audio thread reads this set's
    // lock every tick (Listener, Echoes, LateField, Cabin). Made under that lock on the audio update, a
    // car's cabin held the update 20-45 ms as you got in and the audio thread waited on the lock too
    // (AudioLab --driving, 2026-10-12). Each is made on the thread pool and put in under the lock when it
    // is done; until then the place plays what it played before (FmodAudioProvider.HandOver).
    private readonly List<System.Threading.Tasks.Task> _making = new();

    /// <summary>A trace or a cabin's scene made while this set's lock was held (a fault: the audio thread
    /// waits on that lock), since the set was made.</summary>
    internal int MadeUnderLock => Volatile.Read(ref _madeUnderLock);
    private int _madeUnderLock;

    /// <summary>Tests: called as each trace starts being made, with what it is, on the thread making it.</summary>
    internal Action<string>? MakingForTests;

    private void Making(string what)
    {
        OpenFPS.Client.AudioEngine.Core.PlacementThread.Heavy(what);
        if (Monitor.IsEntered(_gate))
        {
            if (Interlocked.Increment(ref _madeUnderLock) == 1)
                Serilog.Log.Warning("{What} was made under the traced reverb's lock: the audio thread waited for it.", what);
        }
        MakingForTests?.Invoke(what);
    }

    /// <summary>Starts <paramref name="make"/> on the thread pool. Under the gate.</summary>
    private void Start(Action make)
    {
        _making.RemoveAll(t => t.IsCompleted);
        _making.Add(System.Threading.Tasks.Task.Run(() =>
        {
            try { make(); }
            catch (Exception ex) { Serilog.Log.Warning(ex, "Traced reverb: a trace could not be made."); }
        }));
    }

    /// <summary>Waits until every trace being made is in (tests and the lab).</summary>
    internal void WaitForMaking(int timeoutMs = 10000)
    {
        System.Threading.Tasks.Task[] now;
        lock (_gate) now = _making.ToArray();
        System.Threading.Tasks.Task.WaitAll(now, timeoutMs);
    }

    /// <summary>The worker hands over its <paramref name="context"/> and <paramref name="scene"/> once
    /// built and after every rebuild. <paramref name="listenerScene"/> is the same geometry without its
    /// open ground (SteamAudioScene.WithoutOpenGround), for the listener's trace; null uses
    /// <paramref name="scene"/>.</summary>
    public void Configure(IntPtr context, SteamAudioScene scene, SteamAudioScene? listenerScene = null)
    {
        // The tracers are made and handed the scene outside the gate: each SetScene waits for its own
        // trace, and under the gate the game thread and the mixer (Listener, Echoes, LateField, Cabin
        // every frame) waited too: a door swinging within 50 m froze every sound for up to a second
        // (2026-09-30).
        bool missing;
        lock (_gate) missing = _listener == null || _echoes == null || _late == null;
        TracedReverb? madeListener = null; TracedEchoes? madeEchoes = null; LateField? madeLate = null;
        if (missing)
        {
            madeListener = new TracedReverb(context) { ExtractLate = true };
            // On the scene without its open ground too: every voice carries its own ground bounce
            // (GroundReflection), and traced again a car at 30 m had it twice, a comb under a millisecond
            // late at about the direct level.
            madeEchoes = new TracedEchoes(context);
            // Each source's own late energy and its direction, on the listener's scene (LateField).
            madeLate = new LateField(context, clock: Clock);
        }
        TracedReverb listener; TracedEchoes echoes; LateField late; List<TracedReverb> rooms;
        lock (_gate)
        {
            _context = context; _scene = scene; _sceneVersion++;
            if (_listener == null) { _listener = madeListener; madeListener = null; }
            if (_echoes == null) { _echoes = madeEchoes; madeEchoes = null; }
            if (_late == null) { _late = madeLate; madeLate = null; }
            listener = _listener!; echoes = _echoes!; late = _late!;
            rooms = new List<TracedReverb>();
            foreach (var r in _rooms.Values) rooms.Add(r.Trace);
        }
        madeListener?.Dispose(); madeEchoes?.Dispose(); madeLate?.Dispose();
        if (listener.IsValid) listener.SetScene(listenerScene ?? scene);
        if (echoes.IsValid) echoes.SetScene(listenerScene ?? scene);
        foreach (var r in rooms) r.SetScene(scene);
        if (late.IsValid) late.SetScene(listenerScene ?? scene);
    }

    /// <summary>A rebuild's scenes (a door moved), handed to the tracers on a thread of their own, one
    /// rebuild after another, so neither the worker nor anyone else waits out a trace for them.</summary>
    public void ConfigureInBackground(IntPtr context, SteamAudioScene scene, SteamAudioScene? listenerScene)
    {
        lock (_gate)
            _reconfigure = _reconfigure.ContinueWith(_ => Configure(context, scene, listenerScene),
                System.Threading.Tasks.TaskScheduler.Default);
    }
    private System.Threading.Tasks.Task _reconfigure = System.Threading.Tasks.Task.CompletedTask;

    /// <summary>True while a rebuild is still being handed over: the scenes it replaces are in use.</summary>
    public bool Reconfiguring { get { lock (_gate) return !_reconfigure.IsCompleted; } }

    /// <summary>Where the listener is, and the region they are in (a new one starts the averaged tail
    /// again; int.MinValue: not known, and only distance counts).</summary>
    public void SetListener(Vector3 at, int place = int.MinValue)
    {
        List<IDisposable>? due;
        lock (_gate) { _listener?.SetListener(at, place); due = TakeRetired(); }
        DisposeElsewhere(due);
    }

    /// <summary>The per-source tracer, or null before the scene exists.</summary>
    public TracedEchoes? Echoes { get { lock (_gate) return _echoes is { IsValid: true } e ? e : null; } }

    /// <summary>Each source's late energy and direction, traced from where it is. Null before the scene.</summary>
    public LateField? LateField { get { lock (_gate) return _late is { IsValid: true } l ? l : null; } }

    /// <summary>The listener's own trace, or null before the scene exists.</summary>
    public TracedReverb? Listener { get { lock (_gate) return _listener is { IsValid: true } l && l.Source != IntPtr.Zero ? l : null; } }

    /// <summary>A room's trace from a point in it. The first asking starts it being made and returns null,
    /// as does every asking until it is made, before the scene exists, or past the cap: then the room is
    /// played through the listener's trace.</summary>
    public TracedReverb? ForRoom(int regionId, Vector3 at)
    {
        lock (_gate)
        {
            if (_scene == null || _context == IntPtr.Zero) return null;
            if (_rooms.TryGetValue(regionId, out var r))
            {
                if (Vector3.DistanceSquared(r.At, at) > 1f) { r.Trace.SetListener(at); _rooms[regionId] = (r.Trace, at); }
                return r.Trace;
            }
            if (_roomsMaking.Contains(regionId) || _rooms.Count + _roomsMaking.Count >= MaxRooms) return null;
            _roomsMaking.Add(regionId);
            var (context, scene, version) = (_context, _scene, _sceneVersion);
            Start(() => MakeRoom(regionId, at, context, scene, version));
            return null;
        }
    }

    private void MakeRoom(int regionId, Vector3 at, IntPtr context, SteamAudioScene scene, int version)
    {
        Making("A room's trace");
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var t = new TracedReverb(context, refreshMs: 1000);
        if (t.IsValid) { t.SetListener(at); t.SetScene(scene); }
        bool keep;
        SteamAudioScene? newer = null;
        lock (_gate)
        {
            _roomsMaking.Remove(regionId);
            keep = t.IsValid && _context == context && _scene != null;
            if (keep)
            {
                _rooms[regionId] = (t, at);
                if (_sceneVersion != version) newer = _scene;
            }
        }
        if (!keep) { t.Dispose(); return; }
        if (newer != null) t.SetScene(newer);
        Serilog.Log.Debug("Traced reverb: region {Id}'s own trace made in {Ms:F0} ms.", regionId,
                          System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
    }

    // ── The vehicle you are riding in ────────────────────────────────────────────────────────
    //
    // Inside a vehicle the room is its cabin. A vehicle moves, so it is not in the map's traced scene
    // (from a bus seat the world scene answers with the street), but it does not move relative to you:
    // it is traced as a scene of its own in the vehicle's frame, from the shell the server builds
    // (VehicleCabin). One trace per kind of vehicle, kept for the last few kinds, so getting out and in
    // again makes nothing; the kind you stand beside is made before you get in (Prepare).
    private sealed class CabinTrace
    {
        public required SteamAudioScene Scene;
        public required TracedReverb Trace;
    }
    /// <summary>Made, by preset; null: that vehicle has no cabin to trace.</summary>
    private readonly Dictionary<string, CabinTrace?> _cabins = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The presets in _cabins, the one used longest ago first.</summary>
    private readonly List<string> _cabinOrder = new();
    private readonly HashSet<string> _cabinsMaking = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Cabins kept: the one you ride in, the one you stand beside, the one you last rode in.</summary>
    private const int MaxCabins = 3;
    private string? _cabinPreset;
    private Vector3 _cabinLocal;
    private TracedReverb? _cabin;
    private bool _riding;

    /// <summary>The cabin's trace while riding in something that has a cabin and once it is made, else null.</summary>
    public TracedReverb? Cabin { get { lock (_gate) return _riding && _cabin is { IsValid: true } c && c.Source != IntPtr.Zero ? c : null; } }

    /// <summary>Riding in <paramref name="preset"/> (null: on foot), with the ear at
    /// <paramref name="local"/> in the vehicle's own frame. Never makes anything here: a cabin not yet
    /// made is started, and the street is heard until it is.</summary>
    public void RideIn(string? preset, Vector3 local)
    {
        lock (_gate)
        {
            if (preset == null || _context == IntPtr.Zero || !MachineRegistry.Knows(preset))
            {
                if (_cabin != null) _cabin.Idle = true;
                _riding = false; _cabin = null; _cabinPreset = null;
                return;
            }
            if (!string.Equals(preset, _cabinPreset, StringComparison.OrdinalIgnoreCase))
            {
                if (_cabin != null) _cabin.Idle = true;
                _cabinPreset = preset;
                _cabin = null;
                if (_cabins.TryGetValue(preset, out var made)) { _cabin = made?.Trace; Used(preset); }
                else MakeCabin(preset);
            }
            _riding = true;
            _cabinLocal = local;
            if (_cabin != null) { _cabin.Idle = false; _cabin.SetListener(local); }
        }
    }

    /// <summary>The cabin of a vehicle you may get into, made now so it is there when you do.</summary>
    public void Prepare(string preset)
    {
        lock (_gate)
        {
            if (_context == IntPtr.Zero || _cabins.ContainsKey(preset) || _cabinsMaking.Contains(preset)) return;
            if (!MachineRegistry.Knows(preset)) return;
            MakeCabin(preset);
        }
    }

    /// <summary>Starts <paramref name="preset"/>'s cabin being made. Under the gate.</summary>
    private void MakeCabin(string preset)
    {
        if (!_cabinsMaking.Add(preset)) return;
        var context = _context;
        Start(() => BuildCabin(preset, context));
    }

    private void BuildCabin(string preset, IntPtr context)
    {
        Making("A cabin's trace");
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        SteamAudioScene? scene = null;
        TracedReverb? trace = null;
        var v = MachineRegistry.VehicleFor(preset);
        if (VehicleCabin.Measure(v) is { } g)
        {
            var boxes = new List<SteamAudioScene.Box>();
            foreach (var (prefab, at, size) in VehicleCabin.Shell(v, g))
                boxes.Add(new SteamAudioScene.Box(at, size, Quaternion.Identity, VehicleCabin.MaterialOf(prefab)));
            scene = new SteamAudioScene(context);
            scene.Build(boxes);
            if (scene.IsBuilt)
            {
                trace = new TracedReverb(context, refreshMs: 1000) { IsCabin = true };
                // Its first trace from the middle of the cabin, until a seat is known.
                if (trace.IsValid) { trace.SetListener(new Vector3(0f, g.FloorTop + g.Hc * 0.5f, g.Cz)); trace.SetScene(scene); }
                else { trace.Dispose(); trace = null; }
            }
        }
        if (trace == null) { scene?.Dispose(); scene = null; }
        var drop = new List<IDisposable>();
        bool riding = false;
        lock (_gate)
        {
            _cabinsMaking.Remove(preset);
            if (_context != context)
            {
                if (trace != null) drop.Add(trace);
                if (scene != null) drop.Add(scene);
            }
            else
            {
                _cabins[preset] = trace != null ? new CabinTrace { Scene = scene!, Trace = trace } : null;
                Used(preset);
                riding = _riding && string.Equals(_cabinPreset, preset, StringComparison.OrdinalIgnoreCase);
                if (riding) { _cabin = trace; trace?.SetListener(_cabinLocal); }
                // Traced once from the middle and left until you get in.
                else if (trace != null) trace.Idle = true;
                Evict();
            }
        }
        foreach (var d in drop) d.Dispose();
        Serilog.Log.Information("Traced reverb: {Preset}'s cabin {What} in {Ms:F0} ms, off the audio threads{Riding}.", preset,
                                trace != null ? "made" : "has nothing to trace, found", System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds,
                                riding ? "; riding in it, the street heard until its first trace" : "");
    }

    /// <summary>Marks <paramref name="preset"/>'s cabin the last used. Under the gate.</summary>
    private void Used(string preset)
    {
        _cabinOrder.RemoveAll(p => string.Equals(p, preset, StringComparison.OrdinalIgnoreCase));
        _cabinOrder.Add(preset);
    }

    /// <summary>Retires the cabins used longest ago past the cap, never the one ridden. Under the gate.</summary>
    private void Evict()
    {
        long now = OpenFPS.Common.RealTime.Ticks;
        for (int i = 0; _cabins.Count > MaxCabins && i < _cabinOrder.Count; )
        {
            string p = _cabinOrder[i];
            if (string.Equals(p, _cabinPreset, StringComparison.OrdinalIgnoreCase)) { i++; continue; }
            _cabinOrder.RemoveAt(i);
            if (_cabins.Remove(p, out var c) && c != null)
            {
                // Retired, not disposed: a reverb stage on the mixer thread may hold this trace until
                // the audio update next hands it another. Disposed a few seconds on (TakeRetired).
                _retired.Add((c.Trace, now));
                _retired.Add((c.Scene, now));
            }
        }
    }

    private readonly List<(IDisposable Item, long At)> _retired = new();

    /// <summary>Cabin traces and scenes retired more than five seconds ago, taken off the list. Under the gate.</summary>
    private List<IDisposable>? TakeRetired()
    {
        if (_retired.Count == 0) return null;
        long cutoff = OpenFPS.Common.RealTime.Ticks - 5 * TimeSpan.TicksPerSecond;
        List<IDisposable>? due = null;
        for (int i = _retired.Count - 1; i >= 0; i--)
            if (_retired[i].At < cutoff) { (due ??= new()).Add(_retired[i].Item); _retired.RemoveAt(i); }
        return due;
    }

    /// <summary>Disposes on the thread pool: a trace's Dispose waits for its thread, up to a trace long.</summary>
    private static void DisposeElsewhere(List<IDisposable>? items)
    {
        if (items == null) return;
        System.Threading.Tasks.Task.Run(() => { foreach (var d in items) d.Dispose(); });
    }

    /// <summary>Everything traced so far, for the /reverb readout.</summary>
    public (int Rooms, int Runs, double LastMs) Stats()
    {
        lock (_gate)
        {
            int runs = _listener?.Runs ?? 0;
            foreach (var r in _rooms.Values) runs += r.Trace.Runs;
            return (_rooms.Count, runs, _listener?.LastRunMs ?? 0);
        }
    }

    /// <summary>Lets go of every trace before the context they were made on is released, waiting for any
    /// being made. The set can be configured again after, on a new context.</summary>
    public void Release()
    {
        // No new trace is started from here on; those started already find the context gone and drop
        // what they made, and are waited for, so nothing is made on a context after it is released.
        System.Threading.Tasks.Task[] making;
        lock (_gate) { _context = IntPtr.Zero; making = _making.ToArray(); }
        System.Threading.Tasks.Task.WaitAll(making, 10000);
        var all = new List<IDisposable>();
        lock (_gate)
        {
            if (_listener != null) all.Add(_listener);
            if (_echoes != null) all.Add(_echoes);
            if (_late != null) all.Add(_late);
            _listener = null; _echoes = null; _late = null;
            foreach (var c in _cabins.Values) if (c != null) { all.Add(c.Trace); all.Add(c.Scene); }
            _cabins.Clear(); _cabinOrder.Clear(); _cabin = null; _cabinPreset = null; _riding = false;
            foreach (var (item, _) in _retired) all.Add(item);
            _retired.Clear();
            foreach (var r in _rooms.Values) all.Add(r.Trace);
            _rooms.Clear();
            _making.Clear();
            _scene = null;
        }
        foreach (var d in all) d.Dispose();
    }
}

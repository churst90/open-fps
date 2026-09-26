using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using Thread = System.Threading.Thread;
using FMOD;
using OpenFPS.Client.AudioEngine.Fmod;   // DspCallback.UserData

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// The echoes of a few sources, traced from where each one actually is.
///
/// The listener's trace (TracedReverb) plays everything as if it stood at the listener: right for the
/// late field, which barely depends on where in a place a source is, and wrong for the early part,
/// which is all about where it is. Those were voiced as a handful of first-order mirror images per
/// engine, each its own voice — and as a train or a police car moved, the images jumped from one
/// facade to the next: "I hear the reflections bounce around from one building to another as the
/// source of sound moves, but in reality the train's sound is reflecting and those reflections are
/// reflecting as well". A real one is a dense field of copies of copies that slides as the source
/// moves.
///
/// That is what this is: one Steam Audio simulator with a source per traced sound, the rays from the
/// listener collecting every order of reflection back to each source's real position, refreshed
/// several times a second, and each IR crossfaded inside its effect. A traced source's whole
/// reverberation is its own: its send to the shared tail is cut and its mirror-image echoes are not
/// made (see the provider's UpdateTracedEchoes and ClientAudioSystem).
///
/// Only a few: the loudest sustained sources at the ear, ranked by the level they render at, with the
/// number following the mixer's headroom. Everything else keeps the cheap paths.
///
/// TWO banks of sources, traced in turn. A trace is a Monte Carlo estimate: a source that has not moved
/// comes back with the same energy (within a few tenths of a decibel) and a different fine pattern
/// of echoes every time, and the effect swapped one pattern for the next inside a single 23 ms
/// block — heard as the reflections "jumping and cutting out a little bit ... they aren't a smooth
/// transition". With two, each IR is replaced only while the mix has faded it out, and what is heard
/// is a crossfade from the older trace to the newer across the whole refresh.
///
/// Both banks are sources in ONE simulator: a second simulator on the same scene traced nothing
/// (--traced-echoes: its IRs came back silent). A trace asks for reflections only from the bank
/// being refreshed; the other bank's sources are given no simulation flags and keep what they had.
/// </summary>
internal sealed class TracedEchoes : IDisposable
{
    public static volatile TracedEchoes? Current;

    public const int MaxSources = 6;
    public const float DurationSeconds = 1.5f;
    public const int Order = 1;
    public const int Channels = (Order + 1) * (Order + 1);
    /// <summary>Rays and bounces per trace. The rays are shot once from the listener and serve every
    /// source; each bounce is then checked for a line of sight to each source.</summary>
    private const int Rays = 4096, Bounces = 32;
    /// <summary>How often: a car at 15 m/s moves two metres in this time, and the effect crossfades
    /// between one IR and the next.</summary>
    private const int RefreshMs = 125;

    public IntPtr Context { get; }
    public int SampleRate { get; }
    public int FrameSize { get; }
    public int IrSize => (int)(DurationSeconds * SampleRate);

    private const int Banks = 2;
    private IntPtr _simulator;
    private readonly IntPtr[,] _sources = new IntPtr[Banks, MaxSources];
    private readonly bool[] _added = new bool[MaxSources];
    private readonly bool[] _dirtyBank = new bool[Banks];
    private int _nextBank;
    /// <summary>The bank traced most recently: the mix crossfades towards it. -1 before the first.</summary>
    public volatile int NewestBank = -1;
    /// <summary>
    /// How many times each bank has been traced. A Steam Audio IR update goes to the first effect that
    /// reads it after the trace — measured: a second effect on the same IR never saw one — so an
    /// effect newly given a slot has nothing from a bank until that bank is traced again. A rig
    /// notes these when it is attached and fades to a bank only once it has moved on.
    /// </summary>
    public readonly int[] BankGeneration = new int[Banks];
    /// <summary>How long the mix takes to cross from one bank to the other, seconds: just under the
    /// refresh, so each crossfade is finished before the bank faded out is traced again.</summary>
    public const float CrossfadeSeconds = RefreshMs * 0.001f * 0.85f;
    private readonly bool[] _inUse = new bool[MaxSources];
    private readonly Vector3[] _at = new Vector3[MaxSources];
    private readonly object _gate = new();
    private Thread? _thread;
    private volatile bool _running;
    private Vector3 _listener;
    private bool _haveScene;

    public int Runs;
    public double LastRunMs;

    public TracedEchoes(IntPtr context, int sampleRate = 44100, int frameSize = 1024)
    {
        Context = context; SampleRate = sampleRate; FrameSize = frameSize;
        var s = new Phonon.IPLSimulationSettings
        {
            flags = Phonon.IPL_SIMULATIONFLAGS_REFLECTIONS,
            sceneType = Phonon.IPL_SCENETYPE_DEFAULT,
            reflectionType = Phonon.IPL_REFLECTIONEFFECTTYPE_CONVOLUTION,
            maxNumOcclusionSamples = 16, maxNumRays = Rays, numDiffuseSamples = 32,
            maxDuration = DurationSeconds, maxOrder = Order, maxNumSources = MaxSources * Banks, numThreads = 2,
            rayBatchSize = 16, numVisSamples = 4, samplingRate = sampleRate, frameSize = frameSize,
        };
        if (Phonon.iplSimulatorCreate(context, ref s, out _simulator) != Phonon.IPL_STATUS_SUCCESS)
            _simulator = IntPtr.Zero;
    }

    public bool IsValid => _simulator != IntPtr.Zero;

    public void SetScene(SteamAudioScene scene)
    {
        if (!IsValid || !scene.IsBuilt) return;
        lock (_gate)
        {
            Phonon.iplSimulatorSetScene(_simulator, scene.Handle);
            for (int b = 0; b < Banks; b++)
                for (int i = 0; i < MaxSources; i++)
                {
                    if (_sources[b, i] != IntPtr.Zero) continue;
                    var ss = new Phonon.IPLSourceSettings { flags = Phonon.IPL_SIMULATIONFLAGS_REFLECTIONS };
                    if (Phonon.iplSourceCreate(_simulator, ref ss, out IntPtr src) == Phonon.IPL_STATUS_SUCCESS)
                        _sources[b, i] = src;
                }
            Phonon.iplSimulatorCommit(_simulator);
            _haveScene = true;
        }
        if (_thread == null)
        {
            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "TracedEchoes", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }
        Current = this;
    }

    /// <summary>A free slot for a source, or -1. Game thread.</summary>
    public int Acquire(Vector3 at)
    {
        lock (_gate)
        {
            for (int i = 0; i < MaxSources; i++)
            {
                if (_inUse[i] || _sources[0, i] == IntPtr.Zero || _sources[1, i] == IntPtr.Zero) continue;
                _inUse[i] = true; _at[i] = at;
                if (!_added[i])
                {
                    for (int b = 0; b < Banks; b++) Phonon.iplSourceAdd(_sources[b, i], _simulator);
                    _dirtyBank[0] = true;
                    _added[i] = true;
                }
                return i;
            }
            return -1;
        }
    }

    /// <summary>Gives a slot back. Its source stays in the simulator — adding and removing sources
    /// needs a commit, and an idle source is only a few visibility checks — but it is parked far
    /// below the map, where nothing reaches it.</summary>
    public void Release(int slot)
    {
        if (slot < 0 || slot >= MaxSources) return;
        lock (_gate) { _inUse[slot] = false; _at[slot] = new Vector3(0f, -10000f, 0f); }
    }

    public void SetSource(int slot, Vector3 at) { if (slot >= 0 && slot < MaxSources) lock (_gate) _at[slot] = at; }
    public void SetListener(Vector3 at) { lock (_gate) _listener = at; }

    private void Loop()
    {
        while (_running)
        {
            try
            {
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                bool ran = false;
                int bank = _nextBank;
                lock (_gate)
                {
                    bool any = false;
                    for (int i = 0; i < MaxSources; i++) any |= _inUse[i];
                    if (_haveScene && any)
                    {
                        IntPtr sim = _simulator;
                        if (_dirtyBank[0]) { Phonon.iplSimulatorCommit(sim); _dirtyBank[0] = false; }
                        for (int i = 0; i < MaxSources; i++)
                        {
                            if (!_added[i]) continue;
                            for (int b = 0; b < Banks; b++)
                            {
                                var inputs = new Phonon.IPLSimulationInputs
                                {
                                    // The bank being refreshed is traced; the other keeps its IR.
                                    flags = b == bank ? Phonon.IPL_SIMULATIONFLAGS_REFLECTIONS : 0,
                                    source = Coord(_at[i]),
                                    reverbScale0 = 1f, reverbScale1 = 1f, reverbScale2 = 1f,
                                };
                                Phonon.iplSourceSetInputs(_sources[b, i], Phonon.IPL_SIMULATIONFLAGS_REFLECTIONS, ref inputs);
                            }
                        }
                        var shared = new Phonon.IPLSimulationSharedInputs
                        {
                            listener = Coord(_listener), numRays = Rays, numBounces = Bounces,
                            duration = DurationSeconds, order = Order, irradianceMinDistance = 1.0f,
                        };
                        Phonon.iplSimulatorSetSharedInputs(sim, Phonon.IPL_SIMULATIONFLAGS_REFLECTIONS, ref shared);
                        Phonon.iplSimulatorRunReflections(sim);
                        ran = true;
                    }
                }
                if (ran)
                {
                    Interlocked.Increment(ref BankGeneration[bank]);
                    NewestBank = bank;
                    _nextBank = bank ^ 1;
                    LastRunMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                    Runs++;
                }
            }
            catch (Exception ex) { Serilog.Log.Warning(ex, "Traced echoes: a trace failed."); }
            Thread.Sleep(RefreshMs);
        }
    }

    /// <summary>The latest IR for a slot. Mixer thread; Steam Audio double-buffers it.</summary>
    public bool TryGetParams(int slot, int bank, out Phonon.IPLReflectionEffectParams p)
    {
        p = default;
        if (slot < 0 || slot >= MaxSources || bank < 0 || bank >= Banks || _sources[bank, slot] == IntPtr.Zero || !_added[slot]) return false;
        var outs = new Phonon.IPLSimulationOutputs();
        Phonon.iplSourceGetOutputs(_sources[bank, slot], Phonon.IPL_SIMULATIONFLAGS_REFLECTIONS, ref outs);
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
            for (int b = 0; b < Banks; b++)
            {
                for (int i = 0; i < MaxSources; i++)
                {
                    IntPtr src = _sources[b, i];
                    if (src == IntPtr.Zero) continue;
                    if (_added[i]) Phonon.iplSourceRemove(src, _simulator);
                    Phonon.iplSourceRelease(ref src);
                    _sources[b, i] = IntPtr.Zero;
                }
            }
            if (_simulator != IntPtr.Zero) Phonon.iplSimulatorRelease(ref _simulator);
        }
    }
}

/// <summary>
/// One traced source's two stages on its own channel, made once and moved from voice to voice:
/// a CAPTURE at the head of the chain, which passes the voice through and keeps a copy of this block
/// at the source's own level, and a MIX at the tail, after the HRTF, which plays that copy through the
/// source's traced IR, decodes it round the head and adds it. Both run inside the one channel's chain
/// in the same block, head first, so the echoes are not a block late — the reason the mirror-image
/// echoes read their source two blocks behind (EngineEchoState.MinDelayBlocks) does not arise.
/// </summary>
internal sealed class TracedEchoRig
{
    public int FrameSize;
    public IntPtr WorkerContext, ProviderContext;
    public IntPtr Effect, EffectB, Decode, Hrtf;
    public Phonon.IPLAudioBuffer Mono, Ambi, AmbiB, Stereo;
    public float[] Capture = Array.Empty<float>(), MonoScratch = Array.Empty<float>(), StereoScratch = Array.Empty<float>();
    public float[] AmbiScratchA = Array.Empty<float>(), AmbiScratchB = Array.Empty<float>();
    /// <summary>Where the crossfade stands: 0 all bank A, 1 all bank B. Mixer thread only.</summary>
    public float Blend = -1f;
    /// <summary>Each bank's generation when the rig was given its slot (TracedEchoes.BankGeneration).</summary>
    public readonly int[] AttachGeneration = new int[2];
    public float SampleRate = 44100f;
    public Phonon.IPLCoordinateSpace3 Orientation;
    public FMOD.DSP CaptureDsp, MixDsp;
    public GCHandle Handle;

    /// <summary>The slot in <see cref="TracedEchoes"/>, -1 when the rig is idle. Game thread writes.</summary>
    public volatile int Slot = -1;
    /// <summary>Gain from the voice's raw samples to the source's level at a metre, the reference the
    /// trace's paths are attenuated from. Game thread writes.</summary>
    public volatile float InputGain;
    /// <summary>Set by the capture, cleared by the mix: the mix only plays a block captured this block.</summary>
    public volatile bool Fresh;
    /// <summary>The effect's convolution tail belongs to the last voice; the mix resets it on its
    /// first block with a new one.</summary>
    public volatile bool NeedsReset;
    /// <summary>Which voice (entity id) it is on, for the readout.</summary>
    public int Owner;
    public volatile float InRms, OutRms;
}

internal static class TracedEchoDsp
{
    private static readonly FMOD.DSP_READ_CALLBACK _capture = CaptureRead, _mix = MixRead;

    public static RESULT Create(FMOD.System system, TracedEchoRig rig)
    {
        rig.Handle = GCHandle.Alloc(rig);
        var c = new FMOD.DSP_DESCRIPTION { pluginsdkversion = FMOD.VERSION.number, numinputbuffers = 1, numoutputbuffers = 1, read = _capture };
        RESULT r = system.createDSP(ref c, out rig.CaptureDsp);
        if (r != RESULT.OK) return r;
        rig.CaptureDsp.setUserData(GCHandle.ToIntPtr(rig.Handle));
        var m = new FMOD.DSP_DESCRIPTION { pluginsdkversion = FMOD.VERSION.number, numinputbuffers = 1, numoutputbuffers = 1, read = _mix };
        r = system.createDSP(ref m, out rig.MixDsp);
        if (r != RESULT.OK) return r;
        rig.MixDsp.setUserData(GCHandle.ToIntPtr(rig.Handle));
        return RESULT.OK;
    }

    private static TracedEchoRig? RigOf(ref DSP_STATE st)
    {
        IntPtr u = DspCallback.UserData(ref st);
        return u != IntPtr.Zero && GCHandle.FromIntPtr(u).Target is TracedEchoRig r ? r : null;
    }

    private static unsafe RESULT CaptureRead(ref DSP_STATE dsp_state, IntPtr inbuffer, IntPtr outbuffer, uint length, int inchannels, ref int outchannels)
    {
        try
        {
            if (outchannels == 0) outchannels = inchannels;
            int n = (int)length, ch = Math.Max(1, inchannels);
            float* i = (float*)inbuffer, o = (float*)outbuffer;
            // Through, untouched.
            for (int k = 0; k < n * ch; k++) o[k] = i[k];
            var rig = RigOf(ref dsp_state);
            if (rig == null || rig.Slot < 0 || n != rig.FrameSize) return RESULT.OK;
            float g = rig.InputGain;
            var cap = rig.Capture;
            double sum = 0;
            for (int k = 0; k < n; k++)
            {
                float v = 0f;
                for (int c = 0; c < ch; c++) v += i[k * ch + c];
                v = v / ch * g;
                cap[k] = v;
                sum += v * (double)v;
            }
            rig.InRms = (float)Math.Sqrt(sum / n);
            rig.Fresh = true;
        }
        catch { }
        return RESULT.OK;
    }

    private static unsafe RESULT MixRead(ref DSP_STATE dsp_state, IntPtr inbuffer, IntPtr outbuffer, uint length, int inchannels, ref int outchannels)
    {
        int n = (int)length;
        try
        {
            if (outchannels == 0) outchannels = inchannels;
            int ch = Math.Max(1, inchannels), outCh = outchannels;
            float* i = (float*)inbuffer, o = (float*)outbuffer;
            for (int k = 0; k < n; k++)
                for (int c = 0; c < outCh; c++) o[k * outCh + c] = c < ch ? i[k * ch + c] : 0f;

            var rig = RigOf(ref dsp_state);
            var echoes = TracedEchoes.Current;
            if (rig == null || echoes == null || rig.Slot < 0 || n != rig.FrameSize || rig.Effect == IntPtr.Zero) return RESULT.OK;
            if (!rig.Fresh) return RESULT.OK;
            rig.Fresh = false;
            int newest = echoes.NewestBank;
            bool haveA = echoes.TryGetParams(rig.Slot, 0, out var pa);
            bool haveB = echoes.TryGetParams(rig.Slot, 1, out var pb);
            if (!haveA && !haveB) return RESULT.OK;
            // A bank not traced since this rig took the slot has given this rig nothing yet.
            bool readyA = haveA && Volatile.Read(ref echoes.BankGeneration[0]) > rig.AttachGeneration[0];
            bool readyB = haveB && Volatile.Read(ref echoes.BankGeneration[1]) > rig.AttachGeneration[1];
            if (rig.NeedsReset)
            {
                Phonon.iplReflectionEffectReset(rig.Effect);
                Phonon.iplReflectionEffectReset(rig.EffectB);
                rig.NeedsReset = false;
                rig.Blend = -1f;
            }
            Phonon.iplAudioBufferDeinterleave(rig.WorkerContext, rig.Capture, ref rig.Mono);
            // Towards the bank traced last; a bank without an IR yet is not faded to.
            float target = newest == 1 ? 1f : 0f;
            if (!readyA && !readyB)
            {
                // Keep both convolutions fed so their histories are whole when they are faded to.
                if (haveA) Phonon.iplReflectionEffectApply(rig.Effect, ref pa, ref rig.Mono, ref rig.Ambi, IntPtr.Zero);
                if (haveB) Phonon.iplReflectionEffectApply(rig.EffectB, ref pb, ref rig.Mono, ref rig.AmbiB, IntPtr.Zero);
                return RESULT.OK;
            }
            if (!readyA) target = 1f; else if (!readyB) target = 0f;
            if (rig.Blend < 0f) rig.Blend = target;

            int ach = TracedEchoes.Channels;
            float step = 1f / MathF.Max(1f, TracedEchoes.CrossfadeSeconds * rig.SampleRate);
            float b0 = rig.Blend;
            bool moving = MathF.Abs(target - b0) > 1e-6f;
            // BOTH banks run every block, faded out or not: a convolution is its input's history, and
            // one left idle while faded out would come back without the last second and a half of
            // tail — a dip in the reverberation at every crossfade.
            float bEnd = moving ? (target > b0 ? MathF.Min(target, b0 + step * n) : MathF.Max(target, b0 - step * n)) : b0;
            bool needA = haveA, needB = haveB;
            if (needA) Phonon.iplReflectionEffectApply(rig.Effect, ref pa, ref rig.Mono, ref rig.Ambi, IntPtr.Zero);
            if (needB) Phonon.iplReflectionEffectApply(rig.EffectB, ref pb, ref rig.Mono, ref rig.AmbiB, IntPtr.Zero);
            if (needA && needB)
            {
                Phonon.iplAudioBufferInterleave(rig.WorkerContext, ref rig.Ambi, rig.AmbiScratchA);
                Phonon.iplAudioBufferInterleave(rig.WorkerContext, ref rig.AmbiB, rig.AmbiScratchB);
                var xa = rig.AmbiScratchA; var xb = rig.AmbiScratchB;
                float bl = b0;
                for (int k = 0; k < n; k++)
                {
                    if (moving) bl = target > bl ? MathF.Min(target, bl + step) : MathF.Max(target, bl - step);
                    for (int c = 0; c < ach; c++) xa[k * ach + c] = xa[k * ach + c] * (1f - bl) + xb[k * ach + c] * bl;
                }
                Phonon.iplAudioBufferDeinterleave(rig.WorkerContext, xa, ref rig.Ambi);
            }
            else if (needB && !needA)
            {
                Phonon.iplAudioBufferInterleave(rig.WorkerContext, ref rig.AmbiB, rig.AmbiScratchB);
                Phonon.iplAudioBufferDeinterleave(rig.WorkerContext, rig.AmbiScratchB, ref rig.Ambi);
            }
            rig.Blend = bEnd;
            var dp = new Phonon.IPLAmbisonicsDecodeEffectParams
            {
                order = TracedEchoes.Order, hrtf = rig.Hrtf, orientation = rig.Orientation, binaural = Phonon.IPL_TRUE,
            };
            Phonon.iplAmbisonicsDecodeEffectApply(rig.Decode, ref dp, ref rig.Ambi, ref rig.Stereo);
            Phonon.iplAudioBufferInterleave(rig.ProviderContext, ref rig.Stereo, rig.StereoScratch);

            var st = rig.StereoScratch;
            double sum = 0;
            for (int k = 0; k < n; k++)
            {
                float l = st[k * 2], r = st[k * 2 + 1];
                sum += l * (double)l + r * (double)r;
                if (outCh >= 2) { o[k * outCh] += l; o[k * outCh + 1] += r; }
                else o[k] += 0.5f * (l + r);
            }
            rig.OutRms = (float)Math.Sqrt(sum / (2 * n));
        }
        catch { }
        return RESULT.OK;
    }
}

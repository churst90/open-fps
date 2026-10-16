using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.Core.AudioEngine.SteamAudio;

namespace OpenFPS.Client.AudioEngine.Fmod;

/// <summary>
/// Reverb buses' traced stages, made ahead and away from the threads that place sounds (PlacementThread).
/// A stage's late tail calibrates its ears on a second of noise when it is made (DiffuseTail.Create,
/// 100-450 ms): made where a new region's bus came in, the audio thread held every source still that long,
/// sitting in a car (AudioLab --driving, 2026-10-12). Every stage is the same when made (the trace it
/// plays, its region and its reader are given when it goes on a bus), so a few are kept made and one is
/// taken in no time. Made the same way on another thread, a stage renders and calibrates exactly as before.
/// </summary>
internal sealed class TracedStages
{
    /// <summary>Stages kept made beyond those in use: a new room and the cabin of a car got into at once.</summary>
    public const int Spare = 2;

    /// <summary>What every stage made for one worker's traces shares.</summary>
    public readonly record struct Shape(IntPtr WorkerContext, int SampleRate, int IrSize, int LatePartitions, int SubFrame);

    private readonly IntPtr _providerContext;
    private readonly int _mixerFrame;
    private readonly object _gate = new();
    private readonly Queue<TracedReverbState> _ready = new();
    private Shape _shape;
    private System.Threading.Tasks.Task _making = System.Threading.Tasks.Task.CompletedTask;
    private bool _released;

    // The stages' own HRTF and the wait that lines them up with the voices, made with the first stage.
    // The HRTF is made for the stage's own block (TracedReverb.TracedFrame): one built for the mixer's 1024
    // and run at another size comes out 2 dB hot and is not the same head (--traced-reverb, SA_HRTF_FRAME).
    // Builder thread writes, before any stage carrying them is handed out.
    private IntPtr _hrtf;
    // The same head again, the builder's own: a Steam Audio HRTF applied from two threads at once gives
    // both garbage, and the mixer applies _hrtf in every stage it plays (TracedPlacementTests).
    private IntPtr _calibrationHrtf;
    private int _preDelay = -1;

    /// <param name="providerContext">The provider's Steam Audio context (the decode, the ears).</param>
    /// <param name="mixerFrame">The mixer's block, which a stage is handed and the voices render in.</param>
    /// <param name="lab">The mixer's lab levers (the stage's wait, the ears' velvet); null is the game.</param>
    public TracedStages(IntPtr providerContext, int mixerFrame, OpenFPS.Client.AudioEngine.Core.LabLevers? lab = null)
    {
        _providerContext = providerContext; _mixerFrame = mixerFrame;
        _lab = lab ?? new OpenFPS.Client.AudioEngine.Core.LabLevers();
    }

    private readonly OpenFPS.Client.AudioEngine.Core.LabLevers _lab;

    private DiffuseTail WithEarVelvet(DiffuseTail tail)
    {
        tail.LateEarVelvet = _lab.LateEarVelvet;
        return tail;
    }

    private static IntPtr NewHrtf(IntPtr context, int rate, int frame)
    {
        var au = new Phonon.IPLAudioSettings { samplingRate = rate, frameSize = frame };
        var hs = new Phonon.IPLHRTFSettings { type = Phonon.IPL_HRTFTYPE_DEFAULT, volume = 1f, normType = Phonon.IPL_HRTFNORMTYPE_NONE };
        return Phonon.iplHRTFCreate(context, ref au, ref hs, out IntPtr hrtf) == Phonon.IPL_STATUS_SUCCESS ? hrtf : IntPtr.Zero;
    }

    /// <summary>Stages made and waiting (the lab and tests).</summary>
    public int Ready { get { lock (_gate) return _ready.Count; } }

    /// <summary>A made stage of <paramref name="shape"/>, or null if none is made yet: one is being made,
    /// and asking again later gets it. Never makes one here. Audio thread.</summary>
    public TracedReverbState? Take(Shape shape)
    {
        List<TracedReverbState>? stale = null;
        TracedReverbState? taken = null;
        lock (_gate)
        {
            if (_released) return null;
            if (shape != _shape)
            {
                // Another worker (a new map's context): the stages made for the old one are of no use.
                while (_ready.TryDequeue(out var old)) (stale ??= new()).Add(old);
                _shape = shape;
            }
            if (_ready.Count > 0) taken = _ready.Dequeue();
            // After a stage Steam Audio would not make, not again every tick: every five seconds.
            if (_making.IsCompleted && _ready.Count < Spare && OpenFPS.Common.RealTime.Milliseconds - _failedAt > 5000)
            {
                // A thread of its own, niced: a stage is a few hundred milliseconds of work that must lose to the mixer.
                var done = new System.Threading.Tasks.TaskCompletionSource();
                _making = done.Task;
                OpenFPS.Client.Core.Platform.BackgroundPriority.RunLowered("TracedStages", () =>
                {
                    try { Fill(); } finally { done.SetResult(); }
                });
            }
        }
        if (stale != null) foreach (var s in stale) Free(s);
        return taken;
    }

    /// <summary>Waits for the stages being made (tests and the lab).</summary>
    public void WaitForMaking(int timeoutMs = 10000)
    {
        System.Threading.Tasks.Task t;
        lock (_gate) t = _making;
        t.Wait(timeoutMs);
    }

    private void Fill()
    {
        try
        {
            while (true)
            {
                Shape shape;
                lock (_gate)
                {
                    if (_released || _ready.Count >= Spare) return;
                    shape = _shape;
                }
                var st = Make(shape);
                if (st == null) { _failedAt = OpenFPS.Common.RealTime.Milliseconds; return; }
                bool keep;
                lock (_gate)
                {
                    keep = !_released && shape == _shape;
                    if (keep) _ready.Enqueue(st);
                }
                if (!keep) { Free(st); return; }
            }
        }
        catch (Exception ex) { _failedAt = OpenFPS.Common.RealTime.Milliseconds; Serilog.Log.Warning(ex, "Traced reverb: a stage could not be made."); }
    }
    private long _failedAt = long.MinValue / 2;

    /// <summary>One stage, as AddTracedStage made it in place before 2026-10-12. Null if Steam Audio will
    /// not make its parts.</summary>
    private TracedReverbState? Make(Shape s)
    {
        PlacementThread.Heavy("A room's traced reverb stage");
        int sub = s.SubFrame;
        var au = new Phonon.IPLAudioSettings { samplingRate = s.SampleRate, frameSize = sub };
        var es = new Phonon.IPLReflectionEffectSettings
        {
            type = Phonon.IPL_REFLECTIONEFFECTTYPE_CONVOLUTION, irSize = s.IrSize, numChannels = TracedReverb.Channels,
        };
        if (Phonon.iplReflectionEffectCreate(s.WorkerContext, ref au, ref es, out IntPtr effect) != Phonon.IPL_STATUS_SUCCESS)
        { Serilog.Log.Warning("Traced reverb: Steam Audio would not make a reflection effect for a stage."); return null; }
        if (_hrtf == IntPtr.Zero) _hrtf = NewHrtf(_providerContext, s.SampleRate, sub);
        if (_calibrationHrtf == IntPtr.Zero) _calibrationHrtf = NewHrtf(_providerContext, s.SampleRate, sub);
        if (_hrtf == IntPtr.Zero || _calibrationHrtf == IntPtr.Zero) { Phonon.iplReflectionEffectRelease(ref effect); return null; }
        // The voices' binaural stage (a frame four times as long) delays a sound more than this one, so
        // its input waits the difference and the room answers after the sound, not 4 ms before it
        // (TracedReverbDsp.StagePreDelay). Measured once, through heads of the builder's own: the voices'
        // and the stages' are being applied by the mixer.
        if (_preDelay < 0)
        {
            IntPtr voiceHead = NewHrtf(_providerContext, s.SampleRate, _mixerFrame);
            int voice = TracedReverbDsp.BinauralOnset(_providerContext, voiceHead, _mixerFrame, s.SampleRate);
            if (voiceHead != IntPtr.Zero) Phonon.iplHRTFRelease(ref voiceHead);
            int stage = TracedReverbDsp.BinauralOnset(_providerContext, _calibrationHrtf, sub, s.SampleRate);
            _preDelay = TracedReverbDsp.StagePreDelay(voice, stage, _lab.NoPreDelay);
            Serilog.Log.Information("Traced reverb: voices render {Voice} samples late, the traced stage {Stage}; its input waits {Wait}.",
                                    voice, stage, _preDelay);
        }
        var ds = new Phonon.IPLAmbisonicsDecodeEffectSettings { speakerLayout = Phonon.StereoLayout(), hrtf = _hrtf, maxOrder = TracedReverb.Order };
        if (Phonon.iplAmbisonicsDecodeEffectCreate(_providerContext, ref au, ref ds, out IntPtr decode) != Phonon.IPL_STATUS_SUCCESS)
        { Phonon.iplReflectionEffectRelease(ref effect); return null; }
        var st = new TracedReverbState
        {
            FrameSize = _mixerFrame, SubFrame = sub, SampleRate = s.SampleRate, WorkerContext = s.WorkerContext, ProviderContext = _providerContext,
            Effect = effect, Decode = decode, Hrtf = _hrtf,
            MonoScratch = new float[sub], StereoScratch = new float[sub * 2],
            AmbiScratch = new float[sub * TracedReverb.Channels],
            LateConv = new LateTailConvolver(sub, s.LatePartitions),
            SdmConv = new SharedInputConvolver(sub, SdmTailIr.PartitionsFor(s.SampleRate, sub), DiffuseBranch.Count),
            LateOut = new float[sub],
            // The room you are in: its late tail as a field round the head, not one channel.
            Diffuse = DiffuseTail.Create(_providerContext, sub, TracedReverb.Channels, _hrtf, s.SampleRate, calibrateWith: _calibrationHrtf) is { } tail
                ? WithEarVelvet(tail) : null,
            Delay = _preDelay > 0 ? new PreDelay(_preDelay) : null,
        };
        Phonon.iplAudioBufferAllocate(s.WorkerContext, 1, sub, ref st.Mono);
        Phonon.iplAudioBufferAllocate(s.WorkerContext, TracedReverb.Channels, sub, ref st.Ambi);
        Phonon.iplAudioBufferAllocate(_providerContext, 2, sub, ref st.Stereo);
        return st;
    }

    /// <summary>Frees a stage's Steam Audio parts. Only for a stage no DSP plays (never attached, or
    /// detached from its bus).</summary>
    public static void Free(TracedReverbState st)
    {
        st.Trace = null;
        if (st.Effect != IntPtr.Zero) Phonon.iplReflectionEffectRelease(ref st.Effect);
        if (st.Decode != IntPtr.Zero) Phonon.iplAmbisonicsDecodeEffectRelease(ref st.Decode);
        st.Diffuse?.Release(); st.Diffuse = null;
        if (st.Mono.data != IntPtr.Zero) Phonon.iplAudioBufferFree(st.WorkerContext, ref st.Mono);
        if (st.Ambi.data != IntPtr.Zero) Phonon.iplAudioBufferFree(st.WorkerContext, ref st.Ambi);
        if (st.Stereo.data != IntPtr.Zero) Phonon.iplAudioBufferFree(st.ProviderContext, ref st.Stereo);
    }

    /// <summary>Frees the stages not handed out and the stages' HRTF, once the mixer has stopped (every
    /// stage handed out shares it). Waits for one being made.</summary>
    public void Release()
    {
        System.Threading.Tasks.Task making;
        lock (_gate) { _released = true; making = _making; }
        making.Wait(10000);
        List<TracedReverbState> left;
        lock (_gate) { left = new(_ready); _ready.Clear(); }
        foreach (var s in left) Free(s);
        if (_hrtf != IntPtr.Zero) Phonon.iplHRTFRelease(ref _hrtf);
        if (_calibrationHrtf != IntPtr.Zero) Phonon.iplHRTFRelease(ref _calibrationHrtf);
    }
}

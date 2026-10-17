using System.Runtime.InteropServices;
using FMOD;
using OpenFPS.Client.AudioEngine.Fmod;   // DspCallback.UserData

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// Per-voice state for the Steam Audio binaural FMOD DSP: the Phonon handles and scratch buffers, all
/// allocated up front (nothing allocates in the callback), and the listener-to-source direction,
/// written by the game thread and read by FMOD's mixer thread, in Steam Audio's frame (+x right,
/// +y up, -z forward).
/// </summary>
internal sealed class SteamAudioVoiceState
{
    public IntPtr Context;
    public IntPtr Hrtf;
    public IntPtr Effect;
    public Phonon.IPLAudioBuffer InBuf;   // 1 channel, FrameSize
    public Phonon.IPLAudioBuffer OutBuf;  // 2 channels, FrameSize
    public float[] MonoScratch = Array.Empty<float>();
    public float[] StereoScratch = Array.Empty<float>();
    public int FrameSize;

    // Live direction (torn reads are inaudible for a single frame).
    public volatile float DirX, DirY = 0f, DirZ = -1f; // default: straight ahead

    /// <summary>How spatialized this voice is: 1 fully placed toward the direction, 0 passed through.
    /// Switching a binaural stage on or off between blocks clicks; ramping this does not.</summary>
    public volatile float SpatialBlend = 1f;

    /// <summary>The blend the last block ended on, mixer thread only; below zero before the first.
    /// The blend is ramped across each block from here, rather than stepped at its start.</summary>
    public float LastBlend = -1f;

    /// <summary>
    /// The ground between a recorded sound and the listener, folded in before the HRTF. Recorded
    /// sounds reach the ear only through this stage, and the synthesised voices carry their own
    /// (EngineVoiceState and the rest), so the provider sets it for recorded sounds only and it stays
    /// silent for everything else. Null when the stage was built without one.
    /// </summary>
    public OpenFPS.Client.AudioEngine.Acoustics.GroundReflection? Ground;

    /// <summary>
    /// The ground's answer, placed at the source's mirror image below the surface through an HRTF of its
    /// own. Summed into the source's direction it is a comb in both ears at once, flanging (heard
    /// 2026-09-27): the ear decolours a reflection only when it arrives from somewhere else (Salomons
    /// 1995, Brueggen 2001).
    /// </summary>
    public IntPtr GroundEffect;
    public Phonon.IPLAudioBuffer GroundInBuf;   // 1 channel, FrameSize
    public Phonon.IPLAudioBuffer GroundOutBuf;  // 2 channels, FrameSize
    public float[] GroundMono = Array.Empty<float>();
    public float[] GroundStereo = Array.Empty<float>();
    /// <summary>Where the image is, listener-relative, in Steam Audio's frame.</summary>
    public volatile float GroundDirX, GroundDirY = -1f, GroundDirZ;

    /// <summary>
    /// For the paths into a cabin (CabinPaths) only: an equaliser ahead of the HRTF (and after the room's
    /// send) that takes this direction's HRTF level at the ears back to the one interior voice's; see
    /// HrtfBands. Swapped in whole from the game thread; its state is the stage's.
    /// </summary>
    public volatile OpenFPS.Client.AudioEngine.Core.Engine.BandEq? PreEq;
    public readonly float[] PreEqState = OpenFPS.Client.AudioEngine.Core.Engine.BandEq.State();

    /// <summary>The sound this stage places, for the [NONFINITE] line (NonFinite); set when it is
    /// handed to a sound. Its flags: reported for its output, for its input.</summary>
    public volatile string? GuardName;
    public int NonFiniteReported, NonFiniteInputReported;

    /// <summary>Forgets what it holds after a non-finite block. Mixer thread; allocation-free.</summary>
    public void ResetAfterFault()
    {
        if (Effect != IntPtr.Zero) Phonon.iplBinauralEffectReset(Effect);
        if (GroundEffect != IntPtr.Zero) Phonon.iplBinauralEffectReset(GroundEffect);
        Ground?.Reset();
        Array.Clear(PreEqState);
    }

    // Diagnostics for the headless smoke test.
    public long CallbackCount;
    public volatile float LastRmsL, LastRmsR; // per-channel RMS — proves L/R binaural separation
}

/// <summary>
/// A custom FMOD DSP that places a mono input binaurally with Steam Audio's HRTF, in place of FMOD's
/// panner; state through GCHandle/UserData, work on FMOD's mixer thread. Attach to a 2D channel, so
/// FMOD does not also pan.
/// </summary>
internal static class SteamAudioDsp
{
    private static readonly FMOD.DSP_PROCESS_CALLBACK _processCallback = ProcessCallback;

    /// <summary>The channels the stage puts out: the binaural pair.</summary>
    internal const int OutputChannels = 2;

    /// <summary>
    /// A process callback, not a read callback: only a process callback can put out two channels while
    /// taking one in. With a read callback the input was made stereo by FMOD's centre pan, so every voice
    /// reached the HRTF 3.01 dB low and the recorded sounds' ground never played (--binaural-input).
    /// </summary>
    internal static FMOD.DSP_DESCRIPTION Description() => new()
    {
        pluginsdkversion = FMOD.VERSION.number,
        numinputbuffers = 1,
        numoutputbuffers = 1,
        process = _processCallback,
    };

    public static RESULT CreateDSP(FMOD.System system, SteamAudioVoiceState state, out FMOD.DSP dsp, out GCHandle handle)
    {
        var desc = Description();
        RESULT res = system.createDSP(ref desc, out dsp);
        if (res == RESULT.OK)
        {
            handle = GCHandle.Alloc(state);
            dsp.setUserData(GCHandle.ToIntPtr(handle));
        }
        else
        {
            handle = default;
        }
        return res;
    }

    /// <summary>
    /// FMOD first queries what the stage puts out (a stereo pair), then runs it. The input is left as it
    /// comes: a point source as its one channel, a reverb bus as its stereo.
    /// </summary>
    internal static RESULT ProcessCallback(ref DSP_STATE dsp_state, uint length, ref DSP_BUFFER_ARRAY inbufferarray,
                                           ref DSP_BUFFER_ARRAY outbufferarray, bool inputsidle, DSP_PROCESS_OPERATION op)
    {
        long profiled = MixerProfile.Start();
        try
        {
            if (op == DSP_PROCESS_OPERATION.PROCESS_QUERY)
            {
                DeclareOutput(ref outbufferarray);
                return RESULT.OK;
            }
            int inchannels = inbufferarray.numchannels, outchannels = OutputChannels;
            IntPtr inbuffer = inbufferarray.buffer, outbuffer = outbufferarray.buffer;
            if (outbuffer == IntPtr.Zero) return RESULT.OK;
            if (inbuffer == IntPtr.Zero || inchannels <= 0)
            {
                DspCallback.Silence(outbuffer, length, outchannels);
                return RESULT.OK;
            }
            // Idle input is silence; the HRTF and the ground still run, so what they hold plays out.
            if (inputsidle) unsafe { new Span<float>((void*)inbuffer, (int)length * inchannels).Clear(); }
            return Render(ref dsp_state, inbuffer, outbuffer, length, inchannels, ref outchannels);
        }
        finally { MixerProfile.Stop(MixerProfile.Kind.Binaural, profiled); }
    }

    /// <summary>The query's answer: a stereo pair out, whatever comes in.</summary>
    internal static void DeclareOutput(ref DSP_BUFFER_ARRAY output)
    {
        if (output.numbuffers == 0) return;
        output.numchannels = OutputChannels;
        if (output.bufferchannelmask != IntPtr.Zero) Marshal.WriteInt32(output.bufferchannelmask, 0);
        output.speakermode = SPEAKERMODE.STEREO;
    }

    /// <summary>
    /// The guard: a managed DSP callback must not throw. FMOD calls it on its native mixer thread, and
    /// an exception unwinding across that boundary takes the whole process down (an index slip in the
    /// boundary DSP killed the client that way). A fault costs one silent block and a line in the log.
    /// </summary>
    private static RESULT Render(ref DSP_STATE dsp_state, IntPtr inbuffer, IntPtr outbuffer,
                                 uint length, int inchannels, ref int outchannels)
    {
        try
        {
            var r = RenderCore(ref dsp_state, inbuffer, outbuffer, length, inchannels, ref outchannels);
            Guard(ref dsp_state, inbuffer, outbuffer, (int)length, inchannels, outchannels > 0 ? outchannels : 2);
            return r;
        }
        catch (Exception ex)
        {
            unsafe
            {
                int ch = outchannels > 0 ? outchannels : (inchannels > 0 ? inchannels : 2);
                if (outbuffer != IntPtr.Zero)
                    new Span<float>((void*)outbuffer, (int)length * ch).Clear();
            }
            // Logged from the game thread (DspFault.TryDrain): the logger allocates and may block.
            DspFault.Record("SteamAudioDsp", ex);
            return RESULT.OK;
        }
    }

    /// <summary>What a voice puts into the mix is always finite (NonFinite). A bad input is named as
    /// the input: the fault is in whatever made the sound.</summary>
    private static unsafe void Guard(ref DSP_STATE dsp_state, IntPtr inbuffer, IntPtr outbuffer, int n, int inCh, int outCh)
    {
        if (outbuffer == IntPtr.Zero) return;
        IntPtr userData = DspCallback.UserData(ref dsp_state);
        if (userData == IntPtr.Zero || GCHandle.FromIntPtr(userData).Target is not SteamAudioVoiceState s) return;
        bool badIn = inbuffer != IntPtr.Zero && !NonFinite.AllFinite((float*)inbuffer, n * Math.Max(1, inCh));
        if (badIn)
        {
            new Span<float>((void*)outbuffer, n * outCh).Clear();
            NonFinite.Report(ref s.NonFiniteInputReported, "voice (what feeds its binaural stage)", s.GuardName);
        }
        if (NonFinite.Scrub((float*)outbuffer, n * outCh, ref s.NonFiniteReported, "voice's binaural stage", s.GuardName) || badIn)
            s.ResetAfterFault();
    }

    private static Phonon.IPLVector3 Dir(System.Numerics.Vector3 v) => new() { x = v.X, y = v.Y, z = v.Z };

    private static RESULT RenderCore(ref DSP_STATE dsp_state, IntPtr inbuffer, IntPtr outbuffer, uint length, int inchannels, ref int outchannels)
    {
        IntPtr userData = DspCallback.UserData(ref dsp_state);
        if (userData == IntPtr.Zero) { DspCallback.Silence(outbuffer, length, outchannels); return RESULT.OK; }

        var state = GCHandle.FromIntPtr(userData).Target as SteamAudioVoiceState;
        if (state == null) { DspCallback.Silence(outbuffer, length, outchannels); return RESULT.OK; }

        if (outchannels == 0) outchannels = 2;
        int outCh = outchannels;
        int n = (int)length;

        // Steam Audio requires buffers of exactly the configured frame size: any other block is silence.
        if (n != state.FrameSize)
        {
            unsafe { float* o = (float*)outbuffer; for (int i = 0; i < n * outCh; i++) o[i] = 0f; }
            return RESULT.OK;
        }

        unsafe
        {
            float* inp = (float*)inbuffer;
            float[] mono = state.MonoScratch;
            if (inchannels == 1)
            {
                for (int i = 0; i < n; i++) mono[i] = inp[i];
            }
            else
            {
                float inv = 1f / inchannels;
                for (int i = 0; i < n; i++)
                {
                    float s = 0f;
                    for (int c = 0; c < inchannels; c++) s += inp[i * inchannels + c];
                    mono[i] = s * inv;
                }
            }
        }

        // A cabin path's direction's HRTF colouring taken back to the one interior voice's.
        if (inchannels == 1 && state.PreEq is { } pre)
        {
            float[] mono = state.MonoScratch, z = state.PreEqState;
            for (int i = 0; i < n; i++) mono[i] = pre.Process(mono[i], z);
        }

        // The ground's answer, on a point source only (a stereo input is a bus, not a place): the
        // reflected part alone, placed at the image below. Fed even while inactive, so a ground that
        // comes back does not replay what it held when it went.
        var ground = state.Ground;
        bool grounded = false;
        if (ground != null && inchannels == 1 && state.GroundEffect != IntPtr.Zero)
        {
            float[] mono = state.MonoScratch, g = state.GroundMono;
            for (int i = 0; i < n; i++) g[i] = ground.Process(mono[i]) - mono[i];
            grounded = ground.Active;
        }

        Phonon.iplAudioBufferDeinterleave(state.Context, state.MonoScratch, ref state.InBuf);

        var prm = new Phonon.IPLBinauralEffectParams
        {
            // Never a zero or a NaN direction: Steam Audio answers those with NaN (Phonon.SafeDirection).
            direction = Dir(Phonon.SafeDirection(new System.Numerics.Vector3(state.DirX, state.DirY, state.DirZ))),
            interpolation = Phonon.IPL_HRTFINTERPOLATION_BILINEAR,
            // Always fully placed here; the blend is applied below against the stage's own input, so a
            // reverb bus at blend 0 passes its stereo through. Bypassing the stage instead clicked at
            // every doorway.
            spatialBlend = 1f,
            hrtf = state.Hrtf,
            peakDelays = IntPtr.Zero
        };
        Phonon.iplBinauralEffectApply(state.Effect, ref prm, ref state.InBuf, ref state.OutBuf);

        Phonon.iplAudioBufferInterleave(state.Context, ref state.OutBuf, state.StereoScratch);

        if (grounded)
        {
            Phonon.iplAudioBufferDeinterleave(state.Context, state.GroundMono, ref state.GroundInBuf);
            var gp = new Phonon.IPLBinauralEffectParams
            {
                direction = Dir(Phonon.SafeDirection(new System.Numerics.Vector3(state.GroundDirX, state.GroundDirY, state.GroundDirZ))),
                interpolation = Phonon.IPL_HRTFINTERPOLATION_BILINEAR,
                spatialBlend = 1f,
                hrtf = state.Hrtf,
                peakDelays = IntPtr.Zero
            };
            Phonon.iplBinauralEffectApply(state.GroundEffect, ref gp, ref state.GroundInBuf, ref state.GroundOutBuf);
            Phonon.iplAudioBufferInterleave(state.Context, ref state.GroundOutBuf, state.GroundStereo);
            float[] st = state.StereoScratch, gs = state.GroundStereo;
            for (int i = 0; i < n * 2; i++) st[i] += gs[i];
        }

        // Out, the placed signal blended with the input. The blend is ramped across the block from where
        // the last one ended: stepped per block it was a 43 Hz staircase while it moved.
        float blend = Math.Clamp(state.SpatialBlend, 0f, 1f);
        float fromBlend = state.LastBlend < 0f ? blend : state.LastBlend;
        state.LastBlend = blend;
        double sumSqL = 0, sumSqR = 0;
        unsafe
        {
            float* o = (float*)outbuffer;
            float* inp = (float*)inbuffer;
            float[] st = state.StereoScratch;
            if (blend < 1f || fromBlend < 1f)
            {
                // What the stage would pass through at blend 0: its own input, stereo kept as
                // stereo, anything else as its mono downmix in both ears.
                float step = (blend - fromBlend) / n;
                if (inchannels == 2)
                    for (int i = 0; i < n; i++)
                    {
                        float b = fromBlend + step * (i + 1), d = 1f - b;
                        st[i * 2] = b * st[i * 2] + d * inp[i * 2];
                        st[i * 2 + 1] = b * st[i * 2 + 1] + d * inp[i * 2 + 1];
                    }
                else
                {
                    float[] mono = state.MonoScratch;
                    for (int i = 0; i < n; i++)
                    {
                        float b = fromBlend + step * (i + 1), d = 1f - b;
                        st[i * 2] = b * st[i * 2] + d * mono[i];
                        st[i * 2 + 1] = b * st[i * 2 + 1] + d * mono[i];
                    }
                }
            }
            if (outCh == 2)
            {
                for (int i = 0; i < n; i++)
                {
                    float l = st[i * 2], r = st[i * 2 + 1];
                    o[i * 2] = l; o[i * 2 + 1] = r;
                    sumSqL += l * (double)l; sumSqR += r * (double)r;
                }
            }
            else
            {
                for (int i = 0; i < n; i++)
                {
                    float l = st[i * 2], r = st[i * 2 + 1];
                    sumSqL += l * (double)l; sumSqR += r * (double)r;
                    for (int c = 0; c < outCh; c++) o[i * outCh + c] = c == 0 ? l : (c == 1 ? r : 0f);
                }
            }
        }

        Interlocked.Increment(ref state.CallbackCount);
        state.LastRmsL = (float)Math.Sqrt(sumSqL / n);
        state.LastRmsR = (float)Math.Sqrt(sumSqR / n);
        return RESULT.OK;
    }
}

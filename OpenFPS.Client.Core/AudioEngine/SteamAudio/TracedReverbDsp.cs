using System;
using System.Runtime.InteropServices;
using System.Threading;
using FMOD;
using OpenFPS.Client.AudioEngine.Fmod;   // DspCallback.UserData

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// One outdoor reverb bus's traced stage: what the bus's sends carry, played through the place's
/// measured impulse response (TracedReverb) and decoded round the listener's head. It sits just
/// after the bus's SFXREVERB, which in traced mode passes its input through dry; in room mode this
/// stage is bypassed and the SFXREVERB is the tail, as it always was.
/// </summary>
internal sealed class TracedReverbState
{
    public int FrameSize;
    public IntPtr WorkerContext;          // the tracer's context: the effect must be of the IR's context
    public IntPtr Effect;                 // IPLReflectionEffect
    public IntPtr Decode;                 // IPLAmbisonicsDecodeEffect (provider context)
    public IntPtr Hrtf;
    public Phonon.IPLAudioBuffer Mono, Ambi, Stereo;
    public float[] MonoScratch = Array.Empty<float>(), StereoScratch = Array.Empty<float>();
    public Phonon.IPLCoordinateSpace3 Orientation;
    public IntPtr ProviderContext;
    /// <summary>Which place this bus is played through: the listener's trace for the room they are
    /// in, the room's own for any other. Game thread writes; the mixer reads it once a block.</summary>
    public volatile TracedReverb? Trace;
    /// <summary>This stage's reader in whatever trace it plays (TracedReverb.MaxReaders).</summary>
    public int Reader;
    /// <summary>Output trim, linear. Game thread writes.</summary>
    public volatile float Gain = 1f;

    // Diagnostics: what goes in and what comes out, for the /reverb line.
    public volatile float InRms, OutRms;
    public volatile int Bailed;
}

internal static class TracedReverbDsp
{
    private static readonly FMOD.DSP_READ_CALLBACK _read = Read;

    public static RESULT Create(FMOD.System system, TracedReverbState state, out FMOD.DSP dsp, out GCHandle handle)
    {
        var desc = new FMOD.DSP_DESCRIPTION
        {
            pluginsdkversion = FMOD.VERSION.number,
            numinputbuffers = 1,
            numoutputbuffers = 1,
            read = _read,
        };
        RESULT res = system.createDSP(ref desc, out dsp);
        if (res == RESULT.OK)
        {
            handle = GCHandle.Alloc(state);
            dsp.setUserData(GCHandle.ToIntPtr(handle));
            dsp.setChannelFormat(0, 0, SPEAKERMODE.STEREO);
        }
        else handle = default;
        return res;
    }

    private static RESULT Read(ref DSP_STATE dsp_state, IntPtr inbuffer, IntPtr outbuffer, uint length, int inchannels, ref int outchannels)
    {
        // A callback on FMOD's mixer thread must never throw: the process aborts.
        try { return ReadCore(ref dsp_state, inbuffer, outbuffer, length, inchannels, ref outchannels); }
        catch
        {
            unsafe
            {
                int ch = outchannels > 0 ? outchannels : 2;
                if (outbuffer != IntPtr.Zero) new Span<float>((void*)outbuffer, (int)length * ch).Clear();
            }
            return RESULT.OK;
        }
    }

    private static unsafe RESULT ReadCore(ref DSP_STATE dsp_state, IntPtr inbuffer, IntPtr outbuffer, uint length, int inchannels, ref int outchannels)
    {
        if (outchannels == 0) outchannels = 2;
        int outCh = outchannels, n = (int)length;
        float* o = (float*)outbuffer;
        float* i = (float*)inbuffer;

        IntPtr userData = DspCallback.UserData(ref dsp_state);
        TracedReverb? reverb = userData != IntPtr.Zero && GCHandle.FromIntPtr(userData).Target is TracedReverbState own ? own.Trace : null;
        if (userData == IntPtr.Zero || GCHandle.FromIntPtr(userData).Target is not TracedReverbState s
            || reverb == null || n != s.FrameSize || s.Effect == IntPtr.Zero
            || !reverb.TryGetParams(s.Reader, out var prm))
        {
            if (userData != IntPtr.Zero && GCHandle.FromIntPtr(userData).Target is TracedReverbState b) b.Bailed++;
            for (int k = 0; k < n * outCh; k++) o[k] = 0f;
            return RESULT.OK;
        }

        // The bus's sends, down to one channel: the tracer's source is a point at the listener.
        var mono = s.MonoScratch;
        double inSum = 0;
        int inCh = Math.Max(1, inchannels);
        for (int k = 0; k < n; k++)
        {
            float v = 0f;
            for (int c = 0; c < inCh; c++) v += i[k * inCh + c];
            v /= inCh;
            mono[k] = v;
            inSum += v * (double)v;
        }
        s.InRms = (float)Math.Sqrt(inSum / n);

        Phonon.iplAudioBufferDeinterleave(s.WorkerContext, mono, ref s.Mono);
        Phonon.iplReflectionEffectApply(s.Effect, ref prm, ref s.Mono, ref s.Ambi, IntPtr.Zero);
        var dp = new Phonon.IPLAmbisonicsDecodeEffectParams
        {
            order = TracedReverb.Order, hrtf = s.Hrtf, orientation = s.Orientation, binaural = Phonon.IPL_TRUE,
        };
        Phonon.iplAmbisonicsDecodeEffectApply(s.Decode, ref dp, ref s.Ambi, ref s.Stereo);
        Phonon.iplAudioBufferInterleave(s.ProviderContext, ref s.Stereo, s.StereoScratch);

        float g = s.Gain;
        double outSum = 0;
        var st = s.StereoScratch;
        for (int k = 0; k < n; k++)
        {
            float l = st[k * 2] * g, r = st[k * 2 + 1] * g;
            outSum += l * (double)l + r * (double)r;
            o[k * outCh] = l;
            if (outCh > 1) o[k * outCh + 1] = r;
            for (int c = 2; c < outCh; c++) o[k * outCh + c] = 0f;
        }
        s.OutRms = (float)Math.Sqrt(outSum / (2 * n));
        return RESULT.OK;
    }
}

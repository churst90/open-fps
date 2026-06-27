using System;
using System.Runtime.InteropServices;
using System.Threading;
using FMOD;

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// Per-voice state for the Steam Audio binaural FMOD DSP. Holds the phonon handles + scratch
/// buffers (all pre-allocated; nothing is allocated in the audio callback) and the current
/// listener-&gt;source direction, written by the game thread and read by FMOD's mixer thread.
/// Direction is in Steam Audio coordinates (+x right, +y up, -z forward).
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

    // Diagnostics for the headless smoke test.
    public long CallbackCount;
    public volatile bool ProducedAudio;
}

/// <summary>
/// A custom FMOD DSP that spatializes a mono input into binaural stereo using Steam Audio's HRTF,
/// replacing FMOD's amplitude panner. Modeled on the existing Granular/Synth DSP processors:
/// created via System.createDSP, state passed through GCHandle/UserData, work done in the read
/// callback on FMOD's mixer thread. Attach to a 2D mono channel (so FMOD does not also pan).
/// </summary>
internal static class SteamAudioDsp
{
    private static readonly FMOD.DSP_READ_CALLBACK _readCallback = ReadCallback;

    public static RESULT CreateDSP(FMOD.System system, SteamAudioVoiceState state, out FMOD.DSP dsp, out GCHandle handle)
    {
        var desc = new FMOD.DSP_DESCRIPTION
        {
            pluginsdkversion = FMOD.VERSION.number,
            numinputbuffers = 1,
            numoutputbuffers = 1,
            read = _readCallback
        };

        RESULT res = system.createDSP(ref desc, out dsp);
        if (res == RESULT.OK)
        {
            handle = GCHandle.Alloc(state);
            dsp.setUserData(GCHandle.ToIntPtr(handle));
            // Force stereo output even though the input is mono — the HRTF produces a binaural pair.
            dsp.setChannelFormat(0, 0, SPEAKERMODE.STEREO);
        }
        else
        {
            handle = default;
        }
        return res;
    }

    private static RESULT ReadCallback(ref DSP_STATE dsp_state, IntPtr inbuffer, IntPtr outbuffer, uint length, int inchannels, ref int outchannels)
    {
        IntPtr userData;
        unsafe
        {
            FMOD.DSP dsp = new FMOD.DSP(dsp_state.instance);
            dsp.getUserData(out userData);
        }
        if (userData == IntPtr.Zero) return RESULT.OK;

        var state = GCHandle.FromIntPtr(userData).Target as SteamAudioVoiceState;
        if (state == null) return RESULT.OK;

        if (outchannels == 0) outchannels = 2;
        int outCh = outchannels;
        int n = (int)length;

        // Steam Audio requires buffers of exactly the configured frame size. If FMOD ever hands us
        // a different block length, output silence for that block rather than misuse the effect.
        if (n != state.FrameSize)
        {
            unsafe { float* o = (float*)outbuffer; for (int i = 0; i < n * outCh; i++) o[i] = 0f; }
            return RESULT.OK;
        }

        // 1. Downmix FMOD's input to the mono scratch buffer.
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

        // 2. mono -> IPL input buffer
        Phonon.iplAudioBufferDeinterleave(state.Context, state.MonoScratch, ref state.InBuf);

        // 3. Spatialize with the current direction.
        var prm = new Phonon.IPLBinauralEffectParams
        {
            direction = new Phonon.IPLVector3 { x = state.DirX, y = state.DirY, z = state.DirZ },
            interpolation = Phonon.IPL_HRTFINTERPOLATION_BILINEAR,
            spatialBlend = 1.0f,
            hrtf = state.Hrtf,
            peakDelays = IntPtr.Zero
        };
        Phonon.iplBinauralEffectApply(state.Effect, ref prm, ref state.InBuf, ref state.OutBuf);

        // 4. IPL stereo (deinterleaved) -> interleaved scratch
        Phonon.iplAudioBufferInterleave(state.Context, ref state.OutBuf, state.StereoScratch);

        // 5. Write to FMOD's (interleaved) output buffer.
        bool nonZero = false;
        unsafe
        {
            float* o = (float*)outbuffer;
            float[] st = state.StereoScratch;
            if (outCh == 2)
            {
                for (int i = 0; i < n * 2; i++) { float v = st[i]; o[i] = v; if (v != 0f) nonZero = true; }
            }
            else
            {
                for (int i = 0; i < n; i++)
                {
                    float l = st[i * 2], r = st[i * 2 + 1];
                    if (l != 0f || r != 0f) nonZero = true;
                    for (int c = 0; c < outCh; c++) o[i * outCh + c] = c == 0 ? l : (c == 1 ? r : 0f);
                }
            }
        }

        Interlocked.Increment(ref state.CallbackCount);
        if (nonZero) state.ProducedAudio = true;
        return RESULT.OK;
    }
}

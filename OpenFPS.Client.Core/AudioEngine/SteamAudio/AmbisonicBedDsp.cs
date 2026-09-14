using System;
using System.Runtime.InteropServices;
using System.Threading;
using FMOD;
using OpenFPS.Client.AudioEngine.Core;

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// One playing ambisonic ambience bed: the decoded soundfield, where the playhead is, and the
/// listener's current orientation. Allocated once; nothing here allocates on the mixer thread.
/// </summary>
internal sealed class AmbisonicBedState
{
    /// <summary>Interleaved N3D/ACN sample data for the whole bed. Converted once at load, never
    /// per block — a 60-second first-order bed is four channels of float and perfectly affordable,
    /// and doing the normalization per block would be pure waste.</summary>
    public float[] Pcm = Array.Empty<float>();
    public int Channels;
    public int Order;
    public int SourceSampleRate;
    public int FrameSize;

    /// <summary>Playhead, in source frames. Fractional so a bed recorded at a different sample rate
    /// than the mixer still plays at the right speed.</summary>
    public double Position;
    public bool Loop = true;

    public IntPtr Context;
    public IntPtr Hrtf;
    public IntPtr Effect;               // IPLAmbisonicsDecodeEffect
    public Phonon.IPLAudioBuffer InBuf;  // (order+1)^2 channels
    public Phonon.IPLAudioBuffer OutBuf; // 2 channels
    public float[] Scratch = Array.Empty<float>();
    public float[] StereoScratch = Array.Empty<float>();

    /// <summary>The listener's frame of reference, written by the game thread every audio update and
    /// read by the mixer. This is the whole point of the exercise: the bed is fixed in the world and
    /// this is what turns underneath it.</summary>
    public Phonon.IPLCoordinateSpace3 Orientation;

    /// <summary>Target and current level, glided per block so starting, stopping and crossfading a bed
    /// never steps the signal.</summary>
    public volatile float TargetVolume = 1f;
    public float CurrentVolume;

    // Diagnostics.
    public long CallbackCount;
    public volatile bool ProducedAudio;
    public volatile float LastRmsL, LastRmsR;
}

/// <summary>
/// An FMOD custom DSP that plays an ambisonic ambience bed through Steam Audio's decode effect.
///
/// It is a GENERATOR (no input buffers), reading its own PCM rather than sitting on an FMOD channel.
/// That is deliberate: FMOD would downmix a four-channel sound to the output speaker mode long before
/// any DSP saw it, destroying the soundfield on the way past. Owning the PCM sidesteps the channel
/// format question entirely and makes looping exact.
///
/// Per block: read (order+1)² channels from the bed, hand them to
/// <c>iplAmbisonicsDecodeEffectApply</c> with the listener's current frame of reference, and take back
/// a binaural stereo pair. The rotation happens inside the decode, which is why the world stays still
/// while the player turns — the thing a binaural RECORDING can never do.
/// </summary>
internal static class AmbisonicBedDsp
{
    private static readonly FMOD.DSP_READ_CALLBACK _readCallback = ReadCallback;

    public static RESULT CreateDSP(FMOD.System system, AmbisonicBedState state, out FMOD.DSP dsp, out GCHandle handle)
    {
        var desc = new FMOD.DSP_DESCRIPTION
        {
            pluginsdkversion = FMOD.VERSION.number,
            numinputbuffers = 0,
            numoutputbuffers = 1,
            read = _readCallback
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

    private static RESULT ReadCallback(ref DSP_STATE dsp_state, IntPtr inbuffer, IntPtr outbuffer,
                                       uint length, int inchannels, ref int outchannels)
    {
        IntPtr userData;
        unsafe
        {
            FMOD.DSP dsp = new FMOD.DSP(dsp_state.instance);
            dsp.getUserData(out userData);
        }
        if (userData == IntPtr.Zero) return RESULT.OK;
        if (GCHandle.FromIntPtr(userData).Target is not AmbisonicBedState s) return RESULT.OK;

        if (outchannels == 0) outchannels = 2;
        int outCh = outchannels;
        int n = (int)length;

        // Steam Audio's effects are built for exactly the frame size they were created with.
        if (n != s.FrameSize || s.Effect == IntPtr.Zero || s.Pcm.Length == 0)
        {
            unsafe { float* o = (float*)outbuffer; for (int i = 0; i < n * outCh; i++) o[i] = 0f; }
            return RESULT.OK;
        }

        int ch = s.Channels;
        int totalFrames = s.Pcm.Length / ch;
        double step = (double)s.SourceSampleRate / 44100.0;

        // 1. Pull one block out of the bed, interleaved, with linear interpolation so a 48 kHz bed
        //    plays correctly through a 44.1 kHz mixer instead of running fast.
        var scratch = s.Scratch;
        double pos = s.Position;
        for (int i = 0; i < n; i++)
        {
            int i0 = (int)pos;
            int i1 = i0 + 1;
            if (i0 >= totalFrames) { i0 = s.Loop ? i0 % totalFrames : totalFrames - 1; }
            if (i1 >= totalFrames) { i1 = s.Loop ? i1 % totalFrames : totalFrames - 1; }
            float frac = (float)(pos - Math.Floor(pos));

            for (int c = 0; c < ch; c++)
            {
                float a = s.Pcm[i0 * ch + c];
                float b = s.Pcm[i1 * ch + c];
                scratch[i * ch + c] = a + (b - a) * frac;
            }

            pos += step;
            if (pos >= totalFrames)
            {
                if (s.Loop) pos -= totalFrames;
                else pos = totalFrames - 1;
            }
        }
        s.Position = pos;

        // 2. Level glide, applied here rather than after the decode so a fade never fights the HRTF.
        float target = s.TargetVolume;
        float vol = s.CurrentVolume;
        if (Math.Abs(target - vol) > 1e-5f)
        {
            float stepPerSample = (target - vol) / n;
            for (int i = 0; i < n; i++)
            {
                vol += stepPerSample;
                for (int c = 0; c < ch; c++) scratch[i * ch + c] *= vol;
            }
        }
        else if (vol != 1f)
        {
            for (int i = 0; i < n * ch; i++) scratch[i] *= vol;
        }
        s.CurrentVolume = target;

        // 3. Interleaved -> Steam Audio's planar buffer -> rotated + decoded binaural pair.
        Phonon.iplAudioBufferDeinterleave(s.Context, scratch, ref s.InBuf);

        var prm = new Phonon.IPLAmbisonicsDecodeEffectParams
        {
            order = s.Order,
            hrtf = s.Hrtf,
            orientation = s.Orientation,
            binaural = Phonon.IPL_TRUE
        };
        Phonon.iplAmbisonicsDecodeEffectApply(s.Effect, ref prm, ref s.InBuf, ref s.OutBuf);
        Phonon.iplAudioBufferInterleave(s.Context, ref s.OutBuf, s.StereoScratch);

        // 4. Out.
        bool nonZero = false;
        double sumL = 0, sumR = 0;
        unsafe
        {
            float* o = (float*)outbuffer;
            float[] st = s.StereoScratch;
            for (int i = 0; i < n; i++)
            {
                float l = st[i * 2], r = st[i * 2 + 1];
                sumL += l * (double)l; sumR += r * (double)r;
                if (l != 0f || r != 0f) nonZero = true;
                o[i * outCh] = l;
                if (outCh > 1) o[i * outCh + 1] = r;
                for (int c = 2; c < outCh; c++) o[i * outCh + c] = 0f;
            }
        }

        Interlocked.Increment(ref s.CallbackCount);
        if (nonZero) s.ProducedAudio = true;
        s.LastRmsL = (float)Math.Sqrt(sumL / n);
        s.LastRmsR = (float)Math.Sqrt(sumR / n);
        return RESULT.OK;
    }
}

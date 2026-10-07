using System.Runtime.InteropServices;
using FMOD;

namespace OpenFPS.Client.AudioEngine.Fmod;

/// <summary>One granular voice: its source PCM, its parameters (from the SpatialEmitter) and its grains.</summary>
public class GranularVoiceState : IGuardedUnit
{
    /// <summary>The non-finite guard's flag and name for this unit (NonFinite).</summary>
    public NonFiniteUnit Guard { get; } = new();

    public float[] PcmData;
    public int Channels;
    public int SampleRate;

    public float Position; // 0..1 through the source
    public float GrainSizeMs; // 10 to 200 ms
    public float Density; // grains per second
    public float Pitch; // playback speed
    public float PositionJitter; // 0..1
    public float PitchJitter;

    public float SamplesSinceLastGrain;
    public Random Rnd = new Random();

    public struct Grain
    {
        public float CurrentSample;
        public float TotalSamples;
        public float StartSample;
        public float Pitch;
        public bool IsActive;
    }

    public Grain[] Grains = new Grain[128]; // at most 128 overlapping, to bound the mixer's cost
    
    /// <summary>Forgets the previous sound's grains, for a pooled voice about to play another. The
    /// DSP is out of the graph when this is called, so the mixer is not reading it.</summary>
    public void Reset()
    {
        Array.Clear(Grains);
        SamplesSinceLastGrain = 0f;
    }

    public GranularVoiceState(float[] pcm, int channels, int sampleRate)
    {
        PcmData = pcm;
        Channels = channels;
        SampleRate = sampleRate;
    }
}

/// <summary>
/// The FMOD DSP for a granular voice: a cloud of overlapping Hann-windowed grains read from a sound.
/// </summary>
public static class GranularProcessor
{
    /// <summary>The non-finite guard's flag for a state that is not an IGuardedUnit.</summary>
    private static int _nonFiniteOther;

    private static readonly FMOD.DSP_READ_CALLBACK _readCallback = ReadCallback;

    public static RESULT CreateDSP(FMOD.System system, GranularVoiceState state, out FMOD.DSP dsp, out GCHandle handle)
    {
        FMOD.DSP_DESCRIPTION desc = new FMOD.DSP_DESCRIPTION();
        desc.pluginsdkversion = FMOD.VERSION.number;
        desc.numinputbuffers = 0;
        desc.numoutputbuffers = 1;
        desc.read = _readCallback;

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
    /// A managed DSP callback must not throw: FMOD calls it on its native mixer thread, and an exception
    /// unwinding across that boundary kills the process (an index slip in the boundary DSP did). A
    /// fault here costs one silent block and one log line.
    /// </summary>
    private static RESULT ReadCallback(ref DSP_STATE dsp_state, IntPtr inbuffer, IntPtr outbuffer,
                                       uint length, int inchannels, ref int outchannels)
    {
        try
        {
            var r = ReadCallbackCore(ref dsp_state, inbuffer, outbuffer, length, inchannels, ref outchannels);
            NonFinite.After(ref dsp_state, outbuffer, length, inchannels, outchannels, "granular voice", ref _nonFiniteOther);
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
            DspFault.Record("GranularProcessor", ex);
            return RESULT.OK;
        }
    }


    private static RESULT ReadCallbackCore(ref DSP_STATE dsp_state, IntPtr inbuffer, IntPtr outbuffer, uint length, int inchannels, ref int outchannels)
    {
        IntPtr userData;
        unsafe
        {
            // Through the callback's own function table (DspCallback.UserData): calling the general
            // API on an FMOD.DSP built from dsp_state.instance re-enters FMOD inside its own mix,
            // which its documentation forbids.
            userData = DspCallback.UserData(ref dsp_state);
        }

        if (userData == IntPtr.Zero) { DspCallback.Silence(outbuffer, length, outchannels); return RESULT.OK; }

        GCHandle handle = GCHandle.FromIntPtr(userData);
        GranularVoiceState state = (GranularVoiceState)handle.Target!;

        if (state == null || state.PcmData == null || state.PcmData.Length == 0) { DspCallback.Silence(outbuffer, length, outchannels); return RESULT.OK; }

        if (outchannels == 0) outchannels = 2;
        int outCh = outchannels;
        int inCh = state.Channels;
        float[] pcm = state.PcmData;
        int totalFrames = pcm.Length / inCh;

        unsafe
        {
            float* outBuf = (float*)outbuffer;

            for (uint i = 0; i < length * outCh; i++)
            {
                outBuf[i] = 0.0f;
            }

            float sampleRateOut = MixerQuality.MixerRate;
            float samplesPerGrain = (state.GrainSizeMs / 1000f) * state.SampleRate;
            float samplesBetweenGrains = sampleRateOut / Math.Max(0.1f, state.Density);

            for (uint frame = 0; frame < length; frame++)
            {
                state.SamplesSinceLastGrain++;
                if (state.SamplesSinceLastGrain >= samplesBetweenGrains)
                {
                    state.SamplesSinceLastGrain -= samplesBetweenGrains;
                    SpawnGrain(state, totalFrames, samplesPerGrain);
                }

                for (int i = 0; i < state.Grains.Length; i++)
                {
                    ref var grain = ref state.Grains[i];
                    if (!grain.IsActive) continue;

                    float progress = grain.CurrentSample / grain.TotalSamples;
                    if (progress >= 1.0f)
                    {
                        grain.IsActive = false;
                        continue;
                    }

                    float window = 0.5f * (1.0f - MathF.Cos(2.0f * MathF.PI * progress));

                    float readPos = grain.StartSample + grain.CurrentSample;
                    // Clamped like idx1: a grain started near the end read past it, threw, and the
                    // guard silenced the whole block.
                    int idx0 = Math.Min((int)readPos, totalFrames - 1);
                    int idx1 = Math.Min(idx0 + 1, totalFrames - 1);
                    float frac = readPos - idx0;

                    for (int c = 0; c < outCh; c++)
                    {
                        int srcChannel = c % inCh; 
                        float s0 = pcm[idx0 * inCh + srcChannel];
                        float s1 = pcm[idx1 * inCh + srcChannel];
                        float sample = s0 + (s1 - s0) * frac;

                        outBuf[frame * outCh + c] += sample * window;
                    }

                    grain.CurrentSample += grain.Pitch * ((float)state.SampleRate / sampleRateOut);
                }
                
                // Overlapping grains sum: scaled and clamped.
                for (int c = 0; c < outCh; c++)
                {
                    outBuf[frame * outCh + c] = Math.Clamp(outBuf[frame * outCh + c] * 0.7f, -1.0f, 1.0f);
                }
            }
        }

        return RESULT.OK;
    }

    private static void SpawnGrain(GranularVoiceState state, int totalFrames, float lengthSamples)
    {
        for (int i = 0; i < state.Grains.Length; i++)
        {
            if (!state.Grains[i].IsActive)
            {
                float basePos = state.Position;
                float jitter = ((float)state.Rnd.NextDouble() * 2.0f - 1.0f) * state.PositionJitter;
                float finalPos = Math.Clamp(basePos + jitter, 0.0f, 0.99f);

                float pitchJitter = ((float)state.Rnd.NextDouble() * 2.0f - 1.0f) * state.PitchJitter;
                float finalPitch = Math.Max(0.1f, state.Pitch + pitchJitter);

                state.Grains[i] = new GranularVoiceState.Grain
                {
                    IsActive = true,
                    CurrentSample = 0,
                    TotalSamples = lengthSamples,
                    StartSample = finalPos * totalFrames,
                    Pitch = finalPitch
                };
                break;
            }
        }
    }
}

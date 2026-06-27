using System;
using System.Runtime.InteropServices;
using FMOD;

namespace OpenFPS.Client.AudioEngine.Fmod;

/// <summary>
/// Responsibility: Holds the internal state for a single Granular Synthesis voice.
/// </summary>
public class GranularVoiceState
{
    public float[] PcmData;
    public int Channels;
    public int SampleRate;

    // Parameters from SpatialEmitter
    public float Position; // 0.0 to 1.0
    public float GrainSizeMs; // 10 to 200 ms
    public float Density; // Grains per second
    public float Pitch; // Base playback speed
    public float PositionJitter; // 0.0 to 1.0 randomness
    public float PitchJitter; // Randomness in pitch

    // Internal execution state
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

    public Grain[] Grains = new Grain[128]; // Max 128 overlapping grains to prevent CPU overload
    
    public GranularVoiceState(float[] pcm, int channels, int sampleRate)
    {
        PcmData = pcm;
        Channels = channels;
        SampleRate = sampleRate;
    }
}

/// <summary>
/// Responsibility: The FMOD Custom DSP that implements Granular Synthesis math.
/// Converts continuous audio buffers into clouds of overlapping windowed grains.
/// </summary>
public static class GranularProcessor
{
    private static readonly FMOD.DSP_READ_CALLBACK _readCallback = ReadCallback;

    /// <summary>
    /// Creates a custom FMOD DSP configured for granular synthesis.
    /// </summary>
    public static RESULT CreateDSP(FMOD.System system, GranularVoiceState state, out FMOD.DSP dsp, out GCHandle handle)
    {
        FMOD.DSP_DESCRIPTION desc = new FMOD.DSP_DESCRIPTION();
        desc.pluginsdkversion = FMOD.VERSION.number;
        desc.numinputbuffers = 0; // We generate audio, no input needed
        desc.numoutputbuffers = 1;
        desc.read = _readCallback;

        // Note: In C#, struct strings need careful marshalling. 
        // We leave name blank or basic to avoid marshalling complex strings manually.
        // The wrapper will handle it if we leave it default.

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

    private static RESULT ReadCallback(ref DSP_STATE dsp_state, IntPtr inbuffer, IntPtr outbuffer, uint length, int inchannels, ref int outchannels)
    {
        // Get the state from the DSP user data
        IntPtr userData;
        unsafe
        {
            // The FMOD wrapper doesn't provide direct access to getUserData from DSP_STATE easily,
            // but we can cast the dsp_state pointer to get the FMOD::DSP pointer and call it, 
            // OR use the functions provided in the FMOD C# wrapper.
            // Wait, we can use dsp_state.instance to get the DSP handle!
            FMOD.DSP dsp = new FMOD.DSP(dsp_state.instance);
            dsp.getUserData(out userData);
        }

        if (userData == IntPtr.Zero) return RESULT.OK;

        GCHandle handle = GCHandle.FromIntPtr(userData);
        GranularVoiceState state = (GranularVoiceState)handle.Target!;

        if (state == null || state.PcmData == null || state.PcmData.Length == 0) return RESULT.OK;

        if (outchannels == 0) outchannels = 2;
        int outCh = outchannels;
        int inCh = state.Channels;
        float[] pcm = state.PcmData;
        int totalFrames = pcm.Length / inCh;

        unsafe
        {
            float* outBuf = (float*)outbuffer;

            // Clear output buffer first
            for (uint i = 0; i < length * outCh; i++)
            {
                outBuf[i] = 0.0f;
            }

            float sampleRateOut = 44100f; // Typical
            float samplesPerGrain = (state.GrainSizeMs / 1000f) * state.SampleRate;
            float samplesBetweenGrains = sampleRateOut / Math.Max(0.1f, state.Density);

            for (uint frame = 0; frame < length; frame++)
            {
                // 1. Spawn new grains
                state.SamplesSinceLastGrain++;
                if (state.SamplesSinceLastGrain >= samplesBetweenGrains)
                {
                    state.SamplesSinceLastGrain -= samplesBetweenGrains;
                    SpawnGrain(state, totalFrames, samplesPerGrain);
                }

                // 2. Process active grains
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

                    // Read sample with basic linear interpolation
                    float readPos = grain.StartSample + grain.CurrentSample;
                    int idx0 = (int)readPos;
                    int idx1 = Math.Min(idx0 + 1, totalFrames - 1);
                    float frac = readPos - idx0;

                    // Mix into output
                    for (int c = 0; c < outCh; c++)
                    {
                        int srcChannel = c % inCh; 
                        float s0 = pcm[idx0 * inCh + srcChannel];
                        float s1 = pcm[idx1 * inCh + srcChannel];
                        float sample = s0 + (s1 - s0) * frac;

                        outBuf[frame * outCh + c] += sample * window;
                    }

                    // Advance grain
                    grain.CurrentSample += grain.Pitch * ((float)state.SampleRate / sampleRateOut);
                }
                
                // 3. Safety Limiter/Normalization
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
        // Find free grain slot
        for (int i = 0; i < state.Grains.Length; i++)
        {
            if (!state.Grains[i].IsActive)
            {
                float basePos = state.Position;
                // Add jitter
                float jitter = ((float)state.Rnd.NextDouble() * 2.0f - 1.0f) * state.PositionJitter; // -jitter to +jitter
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

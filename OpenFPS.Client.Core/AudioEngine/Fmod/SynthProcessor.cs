using System;
using System.Runtime.InteropServices;
using FMOD;

namespace OpenFPS.Client.AudioEngine.Fmod;

/// <summary>
/// Responsibility: Holds the internal state for a single Synthesizer voice.
/// Tracks phase, LFO position, and filter coefficients.
/// </summary>
public class SynthVoiceState
{
    // Parameters from SpatialEmitter
    public OpenFPS.Client.AudioEngine.Data.SynthWaveType WaveType;
    public float Frequency; 
    public float LfoRate; 
    public float LfoDepth; 
    public float FilterCutoff; 
    public float FilterResonance; 
    public float PulseWidth = 0.5f;

    // Internal execution state
    public float Phase;
    public float LfoPhase;
    public float EnvValue = 1.0f;
    public Random Rnd = new Random();

    // Filter state
    public float Filter_v0;
    public float Filter_v1;

    public SynthVoiceState()
    {
    }
}

/// <summary>
/// Responsibility: The FMOD Custom DSP that implements Real-Time Synthesis math.
/// Generates Sine, Square, Triangle, Saw, and Noise waves with resonant filtering.
/// Upgraded: Now supports Percussive Envelopes triggered by LFO.
/// </summary>
public static class SynthProcessor
{
    private static readonly FMOD.DSP_READ_CALLBACK _readCallback = ReadCallback;

    public static RESULT CreateDSP(FMOD.System system, SynthVoiceState state, out FMOD.DSP dsp, out GCHandle handle)
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

    private static RESULT ReadCallback(ref DSP_STATE dsp_state, IntPtr inbuffer, IntPtr outbuffer, uint length, int inchannels, ref int outchannels)
    {
        IntPtr userData;
        unsafe
        {
            FMOD.DSP dsp = new FMOD.DSP(dsp_state.instance);
            dsp.getUserData(out userData);
        }

        if (userData == IntPtr.Zero) return RESULT.OK;

        GCHandle handle = GCHandle.FromIntPtr(userData);
        SynthVoiceState state = (SynthVoiceState)handle.Target!;

        if (state == null) return RESULT.OK;

        if (outchannels == 0) outchannels = 2; 
        int outCh = outchannels;
        float sampleRate = 44100f; 

        unsafe
        {
            float* outBuf = (float*)outbuffer;

            for (uint frame = 0; frame < length; frame++)
            {
                // 1. Process LFO / Trigger
                float prevLfo = state.LfoPhase;
                state.LfoPhase += state.LfoRate / sampleRate;
                if (state.LfoPhase >= 1.0f) 
                {
                    state.LfoPhase -= 1.0f;
                    // RE-TRIGGER ENVELOPE if LFO wraps (Percussive Mode)
                    if (state.LfoRate > 0.01f) state.EnvValue = 1.0f;
                }

                // 2. Decay Envelope (Simple exponential decay)
                // Use PulseWidth as Decay Factor: 0.1 = fast, 0.9 = slow
                float decayCoeff = 1.0f - (1.0f / (sampleRate * Math.Max(0.01f, state.PulseWidth * 2.0f)));
                state.EnvValue *= decayCoeff;

                float lfoVal = MathF.Sin(state.LfoPhase * 2.0f * MathF.PI) * state.LfoDepth;

                // 3. Calculate current frequency with LFO modulation
                float modFreq = state.Frequency * (1.0f + lfoVal); 
                float phaseInc = modFreq / sampleRate;
                state.Phase += phaseInc;
                if (state.Phase >= 1.0f) state.Phase -= 1.0f;

                // 4. Generate Oscillator signal
                float rawSignal = 0.0f;
                switch (state.WaveType)
                {
                    case OpenFPS.Client.AudioEngine.Data.SynthWaveType.Sine:
                        rawSignal = MathF.Sin(state.Phase * 2.0f * MathF.PI);
                        break;
                    case OpenFPS.Client.AudioEngine.Data.SynthWaveType.Square:
                        rawSignal = (state.Phase < 0.5f) ? 1.0f : -1.0f;
                        break;
                    case OpenFPS.Client.AudioEngine.Data.SynthWaveType.Saw:
                        rawSignal = (state.Phase * 2.0f) - 1.0f;
                        break;
                    case OpenFPS.Client.AudioEngine.Data.SynthWaveType.Triangle:
                        rawSignal = 4.0f * MathF.Abs(state.Phase - 0.5f) - 1.0f;
                        break;
                    case OpenFPS.Client.AudioEngine.Data.SynthWaveType.Noise:
                        rawSignal = (float)(state.Rnd.NextDouble() * 2.0 - 1.0);
                        break;
                }

                // 5. Apply Envelope to signal
                float envSignal = rawSignal * state.EnvValue;

                // 6. Apply Resonant Low-Pass Filter
                float cutoffHz = Math.Clamp(state.FilterCutoff * 12000.0f, 40.0f, 18000.0f);
                float g = MathF.Tan(MathF.PI * cutoffHz / sampleRate);
                float k = 2.0f - (2.0f * state.FilterResonance); 

                float v3 = envSignal - state.Filter_v1;
                float v1 = (g * v3 + state.Filter_v0) / (1.0f + g * (g + k));
                float v2 = state.Filter_v1 + g * v1;

                state.Filter_v0 = 2.0f * v1 - state.Filter_v0;
                state.Filter_v1 = 2.0f * v2 - state.Filter_v1;

                float output = Math.Clamp(v2, -1.0f, 1.0f);

                for (int c = 0; c < outCh; c++)
                {
                    outBuf[frame * outCh + c] = output;
                }
            }
        }

        return RESULT.OK;
    }
}

using System;
using System.Linq;
using System.Runtime.InteropServices;
using FMOD;
using OpenFPS.Client.AudioEngine.Core;

namespace OpenFPS.Client.AudioEngine.Fmod;

/// <summary>
/// Live state for the near-field boundary effect: the delay line, and the reflections currently being
/// rendered from it. The game thread writes the targets; the mixer thread reads them and glides toward
/// them. Everything is allocated once, at construction — nothing here allocates in the callback.
/// </summary>
public sealed class BoundaryVoiceState
{
    /// <summary>Six probes: right, left, up, down, forward, back.</summary>
    public const int MaxTaps = 6;

    /// <summary>Longest round trip the line must hold: 2 × <see cref="BoundaryModel.MaxDistance"/> at a
    /// cold-air speed of sound, plus the interaural offset, plus headroom.</summary>
    private const float MaxDelaySeconds = 0.064f;

    public readonly int SampleRate;
    public readonly float[] Line;
    public int Write;

    // Targets, written by the game thread. Torn reads cost one block of a slightly wrong gain, which is
    // inaudible because the mixer glides toward these rather than jumping to them.
    public readonly float[] TargetDelayL = new float[MaxTaps];
    public readonly float[] TargetDelayR = new float[MaxTaps];
    public readonly float[] TargetGainL = new float[MaxTaps];
    public readonly float[] TargetGainR = new float[MaxTaps];
    public readonly float[] LowpassAlpha = new float[MaxTaps];

    // What the mixer is actually rendering right now.
    public readonly float[] CurrentDelayL = new float[MaxTaps];
    public readonly float[] CurrentDelayR = new float[MaxTaps];
    public readonly float[] CurrentGainL = new float[MaxTaps];
    public readonly float[] CurrentGainR = new float[MaxTaps];
    public readonly float[] FilterL = new float[MaxTaps];
    public readonly float[] FilterR = new float[MaxTaps];

    /// <summary>Per-sample glide toward the targets. Fast enough to track a walking player, slow enough
    /// that a 60 Hz update never lands as a step — a step in a gain is a click, and a step in a DELAY is
    /// worse, because it tears the waveform apart mid-cycle.</summary>
    public float Glide;

    /// <summary>Diagnostics: the loudest reflection currently being rendered.</summary>
    public volatile float LoudestGain;

    public BoundaryVoiceState(int sampleRate)
    {
        SampleRate = sampleRate <= 0 ? 44100 : sampleRate;
        Line = new float[(int)(MaxDelaySeconds * SampleRate) + 4];
        // ~30 ms to travel the full range: under a block it would zipper, over a second it would lag.
        Glide = 1f - MathF.Exp(-1f / (0.030f * SampleRate));
        for (int i = 0; i < MaxTaps; i++) LowpassAlpha[i] = 1f;
    }
}

/// <summary>
/// The FMOD custom DSP that renders the near-field boundary reflections onto the master bus.
///
/// It is a multi-tap FEEDFORWARD comb: the dry signal plus one delayed, damped, panned copy per nearby
/// surface. Feedforward matters — a feedback delay is a resonator that rings at a fixed pitch, which is
/// what the old FMOD-echo version did and why it read as a metallic artefact rather than as a wall. One
/// tap per surface matters too: a ceiling at a metre and a wall at thirty centimetres produce two
/// different comb spacings at once, and hearing both is how a corridor sounds different from a stairwell.
///
/// Every tap glides — gains and delays alike — so that a 60 Hz update from the game thread can never put
/// a step into the sample stream. The delays are read with linear interpolation, so a gliding delay
/// sweeps continuously rather than jumping between whole samples.
/// </summary>
public static class BoundaryProximityProcessor
{
    private static readonly FMOD.DSP_READ_CALLBACK _readCallback = ReadCallback;

    public static RESULT CreateDSP(FMOD.System system, BoundaryVoiceState state, out FMOD.DSP dsp, out GCHandle handle)
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
        }
        else handle = default;
        return res;
    }

    private static RESULT ReadCallback(ref DSP_STATE dsp_state, IntPtr inbuffer, IntPtr outbuffer,
                                       uint length, int inchannels, ref int outchannels)
    {
        // THE HANDLE RESOLUTION IS INSIDE THE GUARD TOO, and it was not.
        //
        // The note below says a managed DSP callback must not throw, and the try it describes began
        // AFTER these lines — leaving out the one statement most likely to raise. GCHandle.FromIntPtr
        // throws InvalidOperationException the instant the handle it names is no longer allocated,
        // and this callback runs on the mixer thread, so that exception is a process abort rather
        // than a bad block. Same one-line gap as the engine's four callbacks had.
        IntPtr userData;
        BoundaryVoiceState s;
        try
        {
            userData = DspCallback.UserData(ref dsp_state);
            if (userData == IntPtr.Zero) return RESULT.OK;
            if (GCHandle.FromIntPtr(userData).Target is not BoundaryVoiceState bs) return RESULT.OK;
            s = bs;
        }
        catch
        {
            // Pass the mix through untouched. This unit is on the master bus; silencing it silences
            // the game.
            unsafe
            {
                if (inbuffer != IntPtr.Zero && outbuffer != IntPtr.Zero && inchannels == outchannels)
                    new ReadOnlySpan<float>((void*)inbuffer, (int)length * inchannels)
                        .CopyTo(new Span<float>((void*)outbuffer, (int)length * outchannels));
            }
            return RESULT.OK;
        }

        if (outchannels == 0) outchannels = inchannels > 0 ? inchannels : 2;
        int outCh = outchannels;
        int inCh = inchannels > 0 ? inchannels : outCh;
        int n = (int)length;

        // A MANAGED DSP CALLBACK MUST NOT THROW.
        //
        // This one had no guard and it killed the client outright: an IndexOutOfRangeException in
        // here does not fault a voice, it unwinds into FMOD's native mixer thread, and an exception
        // that crosses that boundary takes the process with it. Every other custom DSP in this
        // engine already catches; this one was the exception, and it is the one that crashed.
        //
        // The fallback is a PASS-THROUGH rather than silence: this unit sits at the tail of the
        // master bus, so everything in the game goes through it. Clearing the buffer would mute the
        // whole mix; copying the input across loses the walls and keeps the game audible.
        try
        {
            unsafe
            {
                if (inbuffer == IntPtr.Zero || outbuffer == IntPtr.Zero) return RESULT.OK;
                var input = new ReadOnlySpan<float>((void*)inbuffer, n * inCh);
                var output = new Span<float>((void*)outbuffer, n * outCh);
                Process(s, input, output, inCh, outCh);
            }
        }
        catch (Exception ex)
        {
            unsafe
            {
                if (outbuffer != IntPtr.Zero)
                {
                    var output = new Span<float>((void*)outbuffer, n * outCh);
                    if (inbuffer != IntPtr.Zero && inCh == outCh)
                        new ReadOnlySpan<float>((void*)inbuffer, n * inCh).CopyTo(output);
                    else output.Clear();
                }
            }
            // RECORDED, NOT LOGGED, and the difference matters more here than anywhere.
            //
            // This unit is on the master bus, so its callback runs for every block of the whole mix.
            // Writing a log line from it stops the mixer; the thread tearing a voice down is inside
            // removeDSP waiting for exactly this callback to return, holding the provider's lock, and
            // everything that wants that lock stops behind it. That is a total freeze, and if the
            // console sink's write is what blocked — a terminal nobody is reading — it never ends.
            //
            // The audio update reports it from the game thread. See DspFault.
            DspFault.Record("BoundaryProximity", ex);
        }
        return RESULT.OK;
    }


    /// <summary>
    /// The whole of the effect, over spans rather than mixer pointers — so it can be rendered offline
    /// and its spectrum checked against the geometry it claims to represent. The read callback is a
    /// three-line wrapper around this.
    /// </summary>
    public static void Process(BoundaryVoiceState s, ReadOnlySpan<float> input, Span<float> output,
                               int inChannels, int outChannels)
    {
        int n = outChannels > 0 ? output.Length / outChannels : 0;
        // ...and never more samples than the INPUT holds. The two spans are sized from the same
        // block length and channel counts FMOD reported, so they should agree; "should" is not a
        // bounds check, and this runs on the thread where being wrong is a crash rather than a bug.
        if (inChannels > 0) n = Math.Min(n, input.Length / inChannels);
        int lineLen = s.Line.Length;
        if (lineLen < 4 || n <= 0) { if (!output.IsEmpty) output.Clear(); return; }
        if ((uint)s.Write >= (uint)lineLen) s.Write = 0;
        float glide = s.Glide;
        float loudest = 0f;

        for (int i = 0; i < n; i++)
        {
            float l = input[i * inChannels];
            float r = inChannels > 1 ? input[i * inChannels + 1] : l;

            // One mono line feeds every tap: the reflection of a room is the room, not one channel.
            int w = s.Write;
            s.Line[w] = (l + r) * 0.5f;

            float addL = 0f, addR = 0f;

            for (int t = 0; t < BoundaryVoiceState.MaxTaps; t++)
            {
                float gL = s.CurrentGainL[t] + (s.TargetGainL[t] - s.CurrentGainL[t]) * glide;
                float gR = s.CurrentGainR[t] + (s.TargetGainR[t] - s.CurrentGainR[t]) * glide;
                s.CurrentGainL[t] = gL;
                s.CurrentGainR[t] = gR;

                // A silent tap still glides (so it can come back smoothly) but costs no delay reads.
                if (gL * gL + gR * gR < 1e-12f) { s.FilterL[t] = 0f; s.FilterR[t] = 0f; continue; }

                float dL = s.CurrentDelayL[t] + (s.TargetDelayL[t] - s.CurrentDelayL[t]) * glide;
                float dR = s.CurrentDelayR[t] + (s.TargetDelayR[t] - s.CurrentDelayR[t]) * glide;
                s.CurrentDelayL[t] = dL;
                s.CurrentDelayR[t] = dR;

                float a = s.LowpassAlpha[t];
                float sampleL = ReadInterpolated(s.Line, lineLen, w, dL * s.SampleRate);
                float sampleR = ReadInterpolated(s.Line, lineLen, w, dR * s.SampleRate);

                s.FilterL[t] += a * (sampleL - s.FilterL[t]);
                s.FilterR[t] += a * (sampleR - s.FilterR[t]);

                addL += gL * s.FilterL[t];
                addR += gR * s.FilterR[t];

                float mag = MathF.Max(MathF.Abs(gL), MathF.Abs(gR));
                if (mag > loudest) loudest = mag;
            }

            s.Write = w + 1 >= lineLen ? 0 : w + 1;

            output[i * outChannels] = l + addL;
            if (outChannels > 1) output[i * outChannels + 1] = r + addR;
            for (int c = 2; c < outChannels; c++)
                output[i * outChannels + c] = inChannels > c ? input[i * inChannels + c] : 0f;
        }

        s.LoudestGain = loudest;
    }

    /// <summary>Reads the line `delaySamples` behind the write head, interpolating between the two
    /// neighbouring samples so a delay that is gliding sweeps smoothly instead of stepping.</summary>
    private static float ReadInterpolated(float[] line, int lineLen, int write, float delaySamples)
    {
        // Written as "is it in range?" rather than "is it out of range?", because NaN answers NO to
        // every comparison: `if (d < 1f) d = 1f;` leaves a NaN untouched, and a NaN delay then walks
        // straight through both clamps and into the index. One NaN in a tap's target delay is
        // permanent, too — the glide carries it forward for ever.
        float maxDelay = lineLen - 2f;
        if (!(delaySamples >= 1f)) delaySamples = 1f;
        if (!(delaySamples <= maxDelay)) delaySamples = maxDelay;

        float readPos = write - delaySamples;
        while (readPos < 0f) readPos += lineLen;

        int i0 = (int)readPos;
        if ((uint)i0 >= (uint)lineLen) return 0f;      // belt and braces: this is the mixer thread
        float frac = readPos - i0;
        int i1 = i0 + 1 >= lineLen ? 0 : i0 + 1;
        return line[i0] + (line[i1] - line[i0]) * frac;
    }
}

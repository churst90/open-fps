using System.Runtime.InteropServices;
using FMOD;
using OpenFPS.Client.AudioEngine.Core;

namespace OpenFPS.Client.AudioEngine.Fmod;

/// <summary>
/// The near-field boundary effect's delay line and reflections. The game thread writes the targets and
/// the mixer glides toward them; everything is allocated at construction, nothing in the callback.
/// </summary>
public sealed class BoundaryVoiceState
{
    /// <summary>Six probes: right, left, up, down, forward, back.</summary>
    public const int MaxTaps = 6;

    /// <summary>2 × <see cref="BoundaryModel.MaxDistance"/> at a cold-air speed of sound, plus the
    /// interaural offset, plus headroom.</summary>
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

    // What the mixer is rendering now.
    public readonly float[] CurrentDelayL = new float[MaxTaps];
    public readonly float[] CurrentDelayR = new float[MaxTaps];
    public readonly float[] CurrentGainL = new float[MaxTaps];
    public readonly float[] CurrentGainR = new float[MaxTaps];
    public readonly float[] FilterL = new float[MaxTaps];
    public readonly float[] FilterR = new float[MaxTaps];

    /// <summary>Per-sample glide toward the targets: a 60 Hz update must never land as a step. A step in
    /// a gain is a click; a step in a delay tears the waveform mid-cycle.</summary>
    public float Glide;

    /// <summary>The non-finite guard's flags: the mix arriving at the master, and this stage's output.</summary>
    public int NonFiniteInputReported, NonFiniteReported;

    /// <summary>Forgets the delay line and the tap filters. Mixer thread; no allocation.</summary>
    public void ResetAfterFault()
    {
        Array.Clear(Line); Array.Clear(FilterL); Array.Clear(FilterR);
    }

    public BoundaryVoiceState(int sampleRate)
    {
        SampleRate = sampleRate <= 0 ? MixerQuality.MixerRate : sampleRate;
        Line = new float[(int)(MaxDelaySeconds * SampleRate) + 4];
        // ~30 ms to travel the full range: under a block it would zipper, over a second it would lag.
        Glide = 1f - MathF.Exp(-1f / (0.030f * SampleRate));
        for (int i = 0; i < MaxTaps; i++) LowpassAlpha[i] = 1f;
    }
}

/// <summary>
/// The near-field boundary reflections on the master bus: a multi-tap feedforward comb, the dry signal
/// plus one delayed, damped, panned copy per nearby surface. Feedforward, because a feedback delay rings
/// at a fixed pitch (the old FMOD-echo version sounded metallic, not like a wall); one tap per surface,
/// because a ceiling at a metre and a wall at thirty centimetres make two comb spacings at once, which
/// is how a corridor differs from a stairwell. Gains and delays glide, and delays are read interpolated.
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
        // The handle resolution is inside a guard too: GCHandle.FromIntPtr throws once the handle is
        // freed, and on the mixer thread that is a process abort (the same one-line gap the engine's four
        // callbacks had).
        IntPtr userData;
        BoundaryVoiceState s;
        try
        {
            userData = DspCallback.UserData(ref dsp_state);
            if (userData == IntPtr.Zero) { DspCallback.PassThrough(inbuffer, outbuffer, length, inchannels, outchannels); return RESULT.OK; }
            if (GCHandle.FromIntPtr(userData).Target is not BoundaryVoiceState bs) { DspCallback.PassThrough(inbuffer, outbuffer, length, inchannels, outchannels); return RESULT.OK; }
            s = bs;
        }
        catch
        {
            // Pass the mix through: this unit is on the master bus, and silencing it silences the game.
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

        // A managed DSP callback must not throw: an IndexOutOfRangeException here, unguarded, unwound
        // into FMOD's mixer thread and killed the client. The fallback passes the mix through rather
        // than silencing it, since everything in the game goes through this unit.
        try
        {
            unsafe
            {
                if (inbuffer == IntPtr.Zero || outbuffer == IntPtr.Zero) return RESULT.OK;
                var input = new ReadOnlySpan<float>((void*)inbuffer, n * inCh);
                var output = new Span<float>((void*)outbuffer, n * outCh);
                // A NaN this far would put the limiter's state out for good and silence the game: the
                // block is silence instead, and the line says something upstream is unguarded.
                if (!NonFinite.AllFinite(input))
                {
                    output.Clear();
                    NonFinite.Report(ref s.NonFiniteInputReported, "the mix arriving at the master bus");
                    s.ResetAfterFault();
                    return RESULT.OK;
                }
                Process(s, input, output, inCh, outCh);
                if (NonFinite.Scrub(output, ref s.NonFiniteReported, "the master bus's boundary stage")) s.ResetAfterFault();
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
            // Recorded, not logged: a log line from the mixer freezes the client (see DspFault).
            DspFault.Record("BoundaryProximity", ex);
        }
        return RESULT.OK;
    }


    /// <summary>
    /// The whole effect over spans rather than mixer pointers, so it can be rendered offline and its
    /// spectrum checked against the geometry.
    /// </summary>
    public static void Process(BoundaryVoiceState s, ReadOnlySpan<float> input, Span<float> output,
                               int inChannels, int outChannels)
    {
        int n = outChannels > 0 ? output.Length / outChannels : 0;
        // Never more samples than the input holds: the spans should agree, and on the mixer thread being
        // wrong is a crash.
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

    }

    /// <summary>Reads the line <paramref name="delaySamples"/> behind the write head, interpolated so a
    /// gliding delay sweeps instead of stepping.</summary>
    private static float ReadInterpolated(float[] line, int lineLen, int write, float delaySamples)
    {
        // "Is it in range?" rather than "is it out?": NaN fails every comparison, so `if (d < 1f)` would
        // let a NaN delay through to the index, and the glide would carry it for ever.
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

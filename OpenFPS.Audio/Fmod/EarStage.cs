using System.Runtime.InteropServices;
using FMOD;
using OpenFPS.Common.Hearing;

namespace OpenFPS.Client.AudioEngine.Fmod;

/// <summary>
/// One world voice's ear stage (docs/EAR_MODEL.md): two shelves set from the ISO 226:2023 contour
/// difference between the voice's real level at the ear and the level it plays at, and, for a live
/// voice, a tap of its signal for its spectrum. First in the voice's chain, so the reverb send is
/// corrected too. Pooled with its DSP and never freed while the mixer runs
/// (docs/THE_MIXER_THREAD_CRASH.md); <see cref="Reset"/> only out of the graph.
/// </summary>
public sealed class EarVoiceState : IGuardedUnit
{
    public NonFiniteUnit Guard { get; } = new();

    /// <summary>Largest channel count a voice brings here.</summary>
    public const int MaxChannels = 8;

    /// <summary>dB a second: no step, and slower than a listener tracks as a change of tone.</summary>
    public const float SlewDbPerSecond = 6f;

    public float SampleRate = 48000f;

    private float _targetLow, _targetHigh;

    /// <summary>Audio update thread.</summary>
    public void SetTarget(float lowDb, float highDb)
    {
        Volatile.Write(ref _targetLow, float.IsFinite(lowDb) ? lowDb : 0f);
        Volatile.Write(ref _targetHigh, float.IsFinite(highDb) ? highDb : 0f);
    }

    /// <summary>For a voice that has not played yet: start the shelves where they should be.</summary>
    public void Snap(float lowDb, float highDb)
    {
        SetTarget(lowDb, highDb);
        Volatile.Write(ref _snap, 1);
    }
    private int _snap;

    // ── Mixer thread ─────────────────────────────────────────────────────────────────────────
    internal float LowDb, HighDb;
    private float _coefLow = float.NaN, _coefHigh = float.NaN;
    private (float B0, float B1, float B2, float A1, float A2) _lo, _hi;
    // Transposed direct form II, two states per filter per channel.
    private readonly float[] _z = new float[MaxChannels * 4];

    /// <summary>The signal tap for a live voice, or null. Set from the update thread while the voice
    /// is out of the graph or before it starts.</summary>
    public LiveBands? Tap;
    private int _tapping;
    public bool Tapping
    {
        get => Volatile.Read(ref _tapping) != 0;
        set => Volatile.Write(ref _tapping, value ? 1 : 0);
    }

    /// <summary>For a pooled state about to serve another voice. Out of the graph only.</summary>
    public void Reset()
    {
        SetTarget(0f, 0f);
        Volatile.Write(ref _snap, 1);
        Array.Clear(_z);
        Tapping = false;
        Tap?.Reset();
    }

    internal void Process(ReadOnlySpan<float> input, Span<float> output, int frames, int channels)
    {
        float tLow = Volatile.Read(ref _targetLow), tHigh = Volatile.Read(ref _targetHigh);
        if (Interlocked.Exchange(ref _snap, 0) != 0) { LowDb = tLow; HighDb = tHigh; }
        else
        {
            float step = SlewDbPerSecond * frames / MathF.Max(1f, SampleRate);
            LowDb += Math.Clamp(tLow - LowDb, -step, step);
            HighDb += Math.Clamp(tHigh - HighDb, -step, step);
        }
        if (Tapping && Tap != null) Tap.WriteInterleaved(input, frames, channels);

        // Always filtered, even at 0 dB (where a shelf is exactly the identity): a stage that skipped
        // itself would resume later from a stale state, a click.
        if (!(MathF.Abs(LowDb - _coefLow) < 0.01f) || !(MathF.Abs(HighDb - _coefHigh) < 0.01f))
        {
            _lo = LoudnessCompensation.Shelf(SampleRate, LoudnessCompensation.LowShelfHz, LowDb, high: false);
            _hi = LoudnessCompensation.Shelf(SampleRate, LoudnessCompensation.HighShelfHz, HighDb, high: true);
            _coefLow = LowDb;
            _coefHigh = HighDb;
        }
        int ch = Math.Min(channels, MaxChannels);
        var lo = _lo; var hi = _hi;
        for (int c = 0; c < ch; c++)
        {
            int k = c * 4;
            float l1 = _z[k], l2 = _z[k + 1], h1 = _z[k + 2], h2 = _z[k + 3];
            for (int i = 0; i < frames; i++)
            {
                int at = i * channels + c;
                float x = input[at];
                float y = lo.B0 * x + l1;
                l1 = lo.B1 * x - lo.A1 * y + l2;
                l2 = lo.B2 * x - lo.A2 * y;
                float v = hi.B0 * y + h1;
                h1 = hi.B1 * y - hi.A1 * v + h2;
                h2 = hi.B2 * y - hi.A2 * v;
                output[at] = v;
            }
            // A filter state that went non-finite would ring for ever: start it again instead.
            if (!float.IsFinite(l1 + l2 + h1 + h2)) { l1 = l2 = h1 = h2 = 0f; }
            _z[k] = l1; _z[k + 1] = l2; _z[k + 2] = h1; _z[k + 3] = h2;
        }
        // Channels past the filters' state pass as they came.
        for (int c = ch; c < channels; c++)
            for (int i = 0; i < frames; i++) output[i * channels + c] = input[i * channels + c];
    }
}

/// <summary>The FMOD unit for <see cref="EarVoiceState"/>.</summary>
public static class EarProcessor
{
    private static readonly DSP_READ_CALLBACK _readCallback = ReadCallback;
    private static int _nonFiniteOther;

    public static RESULT CreateDSP(FMOD.System system, EarVoiceState state, out FMOD.DSP dsp, out GCHandle handle)
    {
        var desc = new DSP_DESCRIPTION
        {
            pluginsdkversion = FMOD.VERSION.number,
            numinputbuffers = 1,
            numoutputbuffers = 1,
            read = _readCallback,
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

    /// <summary>A managed DSP callback must not throw (DspFault): everything is inside the guard, and a
    /// fault passes the voice through untouched.</summary>
    private static RESULT ReadCallback(ref DSP_STATE dsp_state, IntPtr inbuffer, IntPtr outbuffer,
                                       uint length, int inchannels, ref int outchannels)
    {
        long profiled = MixerProfile.Start();
        try
        {
            try
            {
                IntPtr userData = DspCallback.UserData(ref dsp_state);
                if (userData == IntPtr.Zero || GCHandle.FromIntPtr(userData).Target is not EarVoiceState s
                    || inbuffer == IntPtr.Zero || outbuffer == IntPtr.Zero || inchannels <= 0 || inchannels != outchannels)
                {
                    DspCallback.PassThrough(inbuffer, outbuffer, length, inchannels, outchannels);
                    return RESULT.OK;
                }
                unsafe
                {
                    int n = (int)length;
                    var input = new ReadOnlySpan<float>((void*)inbuffer, n * inchannels);
                    var output = new Span<float>((void*)outbuffer, n * outchannels);
                    s.Process(input, output, n, inchannels);
                }
                NonFinite.After(ref dsp_state, outbuffer, length, inchannels, outchannels, "ear stage", ref _nonFiniteOther);
                return RESULT.OK;
            }
            catch (Exception ex)
            {
                DspCallback.PassThrough(inbuffer, outbuffer, length, inchannels, outchannels);
                DspFault.Record("EarProcessor", ex);
                return RESULT.OK;
            }
        }
        finally { MixerProfile.Stop(MixerProfile.Kind.Ear, profiled); }
    }
}

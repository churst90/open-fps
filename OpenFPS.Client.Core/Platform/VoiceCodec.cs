using Concentus;
using Concentus.Enums;

namespace OpenFPS.Client.Core.Platform;

/// <summary>
/// Voice chat on the wire, the same from both heads: Opus, 48 kHz mono, one 20 ms frame per packet.
/// 64 kbit/s full band is transparent for speech, sibilants and breath included (24 kbit/s VOIP gave a
/// telephone band), at 8 kB/s per talker. The AUDIO application, not VOIP: VOIP's high-pass and formant
/// emphasis would come on top of the world's own shaping by distance and walls. In-band FEC rebuilds a
/// lost packet when it can; the decoder conceals the rest (TalkerStream).
/// </summary>
public static class VoiceCodec
{
    public const int Rate = 48000;
    public const int FrameSamples = Rate / 50;          // 20 ms
    public const int Bitrate = 64000;
    /// <summary>The largest Opus packet there is.</summary>
    public const int MaxPacketBytes = 1275;
    /// <summary>The longest frame a decoder can be handed: 120 ms.</summary>
    public const int MaxFrameSamples = Rate * 120 / 1000;

    public static IOpusEncoder CreateEncoder()
    {
        var e = OpusCodecFactory.CreateEncoder(Rate, 1, OpusApplication.OPUS_APPLICATION_AUDIO);
        e.Bitrate = Bitrate;
        e.Complexity = 10;
        e.UseVBR = true;
        e.UseConstrainedVBR = false;
        e.SignalType = OpusSignal.OPUS_SIGNAL_VOICE;
        e.MaxBandwidth = OpusBandwidth.OPUS_BANDWIDTH_FULLBAND;
        e.UseInbandFEC = true;
        e.PacketLossPercent = 5;
        e.UseDTX = false;
        return e;
    }

    public static IOpusDecoder CreateDecoder() => OpusCodecFactory.CreateDecoder(Rate, 1);
}

/// <summary>
/// Gathers 48 kHz samples into 20 ms frames and encodes each one. Not thread-safe: one capture thread.
/// </summary>
public sealed class VoiceFrameEncoder
{
    private readonly IOpusEncoder _encoder = VoiceCodec.CreateEncoder();
    private readonly float[] _frame = new float[VoiceCodec.FrameSamples];
    private readonly byte[] _packet = new byte[VoiceCodec.MaxPacketBytes];
    private int _filled;

    /// <summary>Adds samples; every frame they complete is encoded and handed to <paramref name="packet"/>.</summary>
    public void Push(ReadOnlySpan<float> samples, Action<byte[]> packet)
    {
        foreach (float s in samples)
        {
            _frame[_filled++] = float.IsFinite(s) ? Math.Clamp(s, -1f, 1f) : 0f;
            if (_filled < _frame.Length) continue;
            _filled = 0;
            int len = _encoder.Encode(_frame, VoiceCodec.FrameSamples, _packet, _packet.Length);
            if (len > 0) packet(_packet.AsSpan(0, len).ToArray());
        }
    }
}

/// <summary>
/// A change of sample rate by windowed sinc: a band-limited interpolation, so a microphone that runs at
/// 44.1 kHz comes to 48 kHz without the images and the dulled top a straight line between samples gives.
/// Going down, the cutoff follows the lower rate, so nothing above the new Nyquist folds back in.
/// Carries its history across calls; one capture thread.
/// </summary>
public sealed class SincResampler
{
    private const int HalfTaps = 16;
    private const int Phases = 256;
    private readonly double _step;
    private readonly float[] _table;                     // [phase, tap], Phases + 1 rows for interpolation
    private readonly List<float> _history = new();
    private double _pos = HalfTaps - 1;                   // in _history

    public SincResampler(int fromRate, int toRate)
    {
        _step = (double)fromRate / toRate;
        double cutoff = Math.Min(1.0, (double)toRate / fromRate) * 0.95;
        _table = new float[(Phases + 1) * 2 * HalfTaps];
        double beta = 8.0, i0Beta = BesselI0(beta);
        for (int p = 0; p <= Phases; p++)
        {
            double frac = (double)p / Phases;
            double sum = 0;
            for (int k = 0; k < 2 * HalfTaps; k++)
            {
                double t = k - (HalfTaps - 1) - frac;     // distance from the output point, input samples
                double x = t * cutoff;
                double sinc = Math.Abs(x) < 1e-9 ? 1.0 : Math.Sin(Math.PI * x) / (Math.PI * x);
                double r = t / HalfTaps;
                double w = Math.Abs(r) >= 1 ? 0 : BesselI0(beta * Math.Sqrt(1 - r * r)) / i0Beta;
                double h = cutoff * sinc * w;
                _table[p * 2 * HalfTaps + k] = (float)h;
                sum += h;
            }
            // Unity gain at DC for every phase.
            for (int k = 0; k < 2 * HalfTaps; k++) _table[p * 2 * HalfTaps + k] /= (float)sum;
        }
        for (int i = 0; i < HalfTaps - 1; i++) _history.Add(0f);
    }

    public void Process(IReadOnlyList<float> input, List<float> output)
    {
        for (int i = 0; i < input.Count; i++) _history.Add(input[i]);
        int taps = 2 * HalfTaps;
        while (_pos + HalfTaps < _history.Count)
        {
            int i0 = (int)Math.Floor(_pos);
            double frac = (_pos - i0) * Phases;
            int p = (int)frac;
            float pf = (float)(frac - p);
            int baseIndex = i0 - (HalfTaps - 1);
            float a = 0f, b = 0f;
            int rowA = p * taps, rowB = (p + 1) * taps;
            for (int k = 0; k < taps; k++)
            {
                float x = _history[baseIndex + k];
                a += x * _table[rowA + k];
                b += x * _table[rowB + k];
            }
            output.Add(a + (b - a) * pf);
            _pos += _step;
        }
        // Keep only what the next call's first output can still reach back to.
        int drop = (int)Math.Floor(_pos) - (HalfTaps - 1);
        if (drop > 0)
        {
            _history.RemoveRange(0, drop);
            _pos -= drop;
        }
    }

    private static double BesselI0(double x)
    {
        double sum = 1, term = 1, q = x * x / 4;
        for (int k = 1; k < 30; k++) { term *= q / (k * k); sum += term; }
        return sum;
    }
}

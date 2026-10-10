using System.Runtime.InteropServices;
using FMOD;
using OpenFPS.Common;

namespace OpenFPS.Client.AudioEngine.Fmod;

/// <summary>
/// The radiator's six octave bands (Radiator.CrossoverHz) split off one signal by a Linkwitz-Riley tree:
/// at each crossover a fourth-order low-pass and high-pass (two Butterworth sections each), the bottom
/// band peeled off first, and every band below a crossover passed through that crossover's all-pass so
/// all six stay in phase. They sum to an all-pass of the input: flat, whatever the gains are when they
/// are equal. A first try with one-pole splits leaked a band's neighbours into it at 6 dB an octave, and
/// two units in a row (a horn's power, then its beam over that power) came out 3 dB loud and bright.
/// Single precision, transposed direct form II: the lowest pole pair sits at 0.95 of the circle.
/// </summary>
public sealed class RadiatorBands
{
    private const int Crossovers = Radiator.Bands - 1;
    private readonly Section[] _lp = new Section[Crossovers * 2];
    private readonly Section[] _hp = new Section[Crossovers * 2];
    // Band k below crossover j (j > k) passes its all-pass: 4 + 3 + 2 + 1.
    private readonly Section[] _ap = new Section[10];
    // The same all-passes once each, on a running sum (MixBlock).
    private readonly Section[] _apSum = new Section[Crossovers - 1];

    private struct Section
    {
        public float B0, B1, B2, A1, A2, Z1, Z2;

        public float Process(float x)
        {
            float y = B0 * x + Z1;
            Z1 = B1 * x - A1 * y + Z2;
            Z2 = B2 * x - A2 * y;
            return y;
        }

        public void Set(double b0, double b1, double b2, double a0, double a1, double a2)
        {
            B0 = (float)(b0 / a0); B1 = (float)(b1 / a0); B2 = (float)(b2 / a0);
            A1 = (float)(a1 / a0); A2 = (float)(a2 / a0);
            Z1 = Z2 = 0f;
        }
    }

    /// <summary>Sets the rate and clears the history. Not on the mixer thread.</summary>
    public void Configure(float sampleRate)
    {
        const double Q = 0.70710678;
        for (int j = 0; j < Crossovers; j++)
        {
            double w = 2.0 * Math.PI * Math.Min(Radiator.CrossoverHz[j], sampleRate * 0.45) / sampleRate;
            double c = Math.Cos(w), al = Math.Sin(w) / (2.0 * Q);
            for (int s = 0; s < 2; s++)
            {
                _lp[j * 2 + s].Set((1 - c) / 2, 1 - c, (1 - c) / 2, 1 + al, -2 * c, 1 - al);
                _hp[j * 2 + s].Set((1 + c) / 2, -(1 + c), (1 + c) / 2, 1 + al, -2 * c, 1 - al);
            }
            // The two fourth-order halves sum to the second-order all-pass on the same poles.
            for (int k = 0; k < j; k++)
                _ap[ApIndex(k, j)].Set(1 - al, -2 * c, 1 + al, 1 + al, -2 * c, 1 - al);
            if (j > 0) _apSum[j - 1].Set(1 - al, -2 * c, 1 + al, 1 + al, -2 * c, 1 - al);
        }
    }

    private static int ApIndex(int band, int crossover)
    {
        int offset = band switch { 0 => 0, 1 => 4, 2 => 7, _ => 9 };
        return offset + (crossover - band - 1);
    }

    /// <summary>
    /// A block into its six bands, section by section over the whole block (each section's coefficients
    /// and state stay in registers). <paramref name="rest"/> holds the input and is left holding the top
    /// band; <paramref name="bands"/> gets the other five, not yet in phase: <see cref="MixBlock"/>
    /// puts them back together.
    /// </summary>
    public void SplitBlock(Span<float> rest, float[][] bands, int n)
    {
        for (int j = 0; j < Crossovers; j++) Crossover(ref _lp[j * 2], ref _lp[j * 2 + 1], ref _hp[j * 2], ref _hp[j * 2 + 1], rest[..n], bands[j]);
    }

    /// <summary>One crossover over a block: the low-pass and high-pass pairs in one loop, so their four
    /// recursions overlap in the processor instead of waiting on each other.</summary>
    private static void Crossover(ref Section l1, ref Section l2, ref Section h1, ref Section h2, Span<float> rest, float[] low)
    {
        float lb0 = l1.B0, lb1 = l1.B1, lb2 = l1.B2, la1 = l1.A1, la2 = l1.A2;   // both low sections share these
        float hb0 = h1.B0, hb1 = h1.B1, hb2 = h1.B2;                               // and the poles
        float p1 = l1.Z1, p2 = l1.Z2, q1 = l2.Z1, q2 = l2.Z2, r1 = h1.Z1, r2 = h1.Z2, s1 = h2.Z1, s2 = h2.Z2;
        for (int i = 0; i < rest.Length; i++)
        {
            float x = rest[i];
            float y = lb0 * x + p1; p1 = lb1 * x - la1 * y + p2; p2 = lb2 * x - la2 * y;
            float z = lb0 * y + q1; q1 = lb1 * y - la1 * z + q2; q2 = lb2 * y - la2 * z;
            float u = hb0 * x + r1; r1 = hb1 * x - la1 * u + r2; r2 = hb2 * x - la2 * u;
            float v = hb0 * u + s1; s1 = hb1 * u - la1 * v + s2; s2 = hb2 * u - la2 * v;
            low[i] = z;
            rest[i] = v;
        }
        l1.Z1 = Flush(p1); l1.Z2 = Flush(p2); l2.Z1 = Flush(q1); l2.Z2 = Flush(q2);
        h1.Z1 = Flush(r1); h1.Z2 = Flush(r2); h2.Z1 = Flush(s1); h2.Z2 = Flush(s2);
    }

    private static float Flush(float z) => MathF.Abs(z) < 1e-25f ? 0f : z;

    /// <summary>
    /// The bands from <see cref="SplitBlock"/> weighted and summed, in phase: each crossover's
    /// all-pass applied once to the running sum of the bands below it (Horner's rule), four sections
    /// where one per band below each crossover took ten. <paramref name="gain"/> holds each band's gain
    /// sample by sample; <paramref name="sum"/> gets the result.
    /// </summary>
    public void MixBlock(ReadOnlySpan<float> top, float[][] bands, float[][] gain, Span<float> sum, int n)
    {
        var g0 = gain[0]; var b0 = bands[0];
        for (int i = 0; i < n; i++) sum[i] = g0[i] * b0[i];
        for (int j = 1; j < Crossovers; j++)
        {
            Run(ref _apSum[j - 1], sum[..n]);
            var g = gain[j]; var b = bands[j];
            for (int i = 0; i < n; i++) sum[i] += g[i] * b[i];
        }
        var gt = gain[Crossovers];
        for (int i = 0; i < n; i++) sum[i] += gt[i] * top[i];
    }

    private static void Run(ref Section s, Span<float> x)
    {
        float b0 = s.B0, b1 = s.B1, b2 = s.B2, a1 = s.A1, a2 = s.A2, z1 = s.Z1, z2 = s.Z2;
        for (int i = 0; i < x.Length; i++)
        {
            float v = x[i];
            float y = b0 * v + z1;
            z1 = b1 * v - a1 * y + z2;
            z2 = b2 * v - a2 * y;
            x[i] = y;
        }
        // A tail decaying into denormals is slow on x86: flushed.
        if (MathF.Abs(z1) < 1e-25f) z1 = 0f;
        if (MathF.Abs(z2) < 1e-25f) z2 = 0f;
        s.Z1 = z1; s.Z2 = z2;
    }

    /// <summary>One sample into its six bands. No allocation.</summary>
    public void Split(float x, Span<float> bands)
    {
        float rest = x;
        for (int j = 0; j < Crossovers; j++)
        {
            float lo = _lp[j * 2 + 1].Process(_lp[j * 2].Process(rest));
            rest = _hp[j * 2 + 1].Process(_hp[j * 2].Process(rest));
            for (int k = j + 1; k < Crossovers; k++) lo = _ap[ApIndex(j, k)].Process(lo);
            bands[j] = lo;
        }
        bands[Crossovers] = rest;
    }
}

/// <summary>
/// A loudspeaker's beam toward the listener (Radiator), band by band: the voice split into the
/// radiator's six octave bands (<see cref="RadiatorBands"/>), each at its own gain. Behind a horn the
/// top goes and the low mids and the housing stay, which three bands could not say: the mixer's mid
/// band runs from 400 Hz to 4 kHz, most of what a horn radiates.
///
/// A loudspeaker voice carries two: ahead of its own room's send, the power it radiates all round
/// (Radiator.PowerGains, fixed), so its room is fed band by band with what it really radiates; after
/// the send, its beam toward you over that power (Radiator.BeamOverPower), so the direct sound is the
/// beam. Over unity on the axis: a horn's 8 kHz octave is 22 dB above its all-round power.
///
/// The game thread sets the targets (<see cref="SetTargets"/>); the callback slews to them over 50 ms
/// so a listener walking round the horn hears it turn, not step. Six floats, written whole or not at
/// all: a torn update is one block of a gain half way to its next value. The callback allocates nothing.
/// </summary>
public sealed class RadiatorState
{
    public const int MaxChannels = 2;
    private const float SlewSeconds = 0.05f;
    private readonly RadiatorBands[] _bands = { new(), new() };
    private readonly float[] _target = new float[Radiator.Bands];
    private readonly float[] _gain = new float[Radiator.Bands];
    private float _slew = 1f;
    public int NonFiniteReported;

    /// <summary>The gains being slewed to, for tests and instruments.</summary>
    public ReadOnlySpan<float> Targets => _target;

    /// <summary>Sets the rate and starts at unity. Game thread, while no channel holds the unit.</summary>
    public void Configure(float sampleRate)
    {
        foreach (var b in _bands) b.Configure(sampleRate);
        Array.Fill(_target, 1f);
        Array.Fill(_gain, 1f);
        _slew = MathF.Min(1f, 1f / (sampleRate * SlewSeconds));
    }

    /// <summary>Starts at these gains with no slew: a voice's first block is already aimed.</summary>
    public void Start(ReadOnlySpan<float> gains)
    {
        SetTargets(gains);
        for (int b = 0; b < _gain.Length; b++) _gain[b] = _target[b];
    }

    public void SetTargets(ReadOnlySpan<float> gains)
    {
        for (int b = 0; b < _target.Length && b < gains.Length; b++)
            _target[b] = float.IsFinite(gains[b]) ? Math.Clamp(gains[b], 0f, 100f) : 1f;
    }

    /// <summary>The voice over interleaved spans, block by block. Mixer thread: no allocation.</summary>
    public void Process(ReadOnlySpan<float> input, Span<float> output, int channels)
    {
        if (channels <= 0) { output.Clear(); return; }
        int frames = Math.Min(input.Length, output.Length) / channels;
        for (int at = 0; at < frames; at += Chunk)
        {
            int n = Math.Min(Chunk, frames - at);
            // Each band's gain, sample by sample across the chunk, shared by the channels.
            for (int b = 0; b < Radiator.Bands; b++)
            {
                float g = _gain[b], t = _target[b], k = _slew;
                var track = _track[b];
                for (int i = 0; i < n; i++) { g += (t - g) * k; track[i] = g; }
                _gain[b] = g;
            }
            for (int c = 0; c < channels; c++)
            {
                if (c >= MaxChannels)
                {
                    for (int i = 0; i < n; i++) output[(at + i) * channels + c] = input[(at + i) * channels + c];
                    continue;
                }
                var rest = _rest.AsSpan(0, n);
                for (int i = 0; i < n; i++) rest[i] = input[(at + i) * channels + c];
                _bands[c].SplitBlock(rest, _band, n);
                _bands[c].MixBlock(rest, _band, _track, _sum, n);
                for (int i = 0; i < n; i++) output[(at + i) * channels + c] = _sum[i];
            }
        }
    }

    // Scratch for a chunk, made once: the mixer hands over 1,024 frames, and a longer block goes in pieces.
    private const int Chunk = 1024;
    private readonly float[] _rest = new float[Chunk];
    private readonly float[] _sum = new float[Chunk];
    private readonly float[][] _band = Enumerable.Range(0, Radiator.Bands - 1).Select(_ => new float[Chunk]).ToArray();
    private readonly float[][] _track = Enumerable.Range(0, Radiator.Bands).Select(_ => new float[Chunk]).ToArray();
}

/// <summary>The FMOD unit for <see cref="RadiatorState"/>, on a loudspeaker's chain.</summary>
public static class RadiatorProcessor
{
    private static readonly DSP_READ_CALLBACK _readCallback = ReadCallback;

    public static RESULT CreateDSP(FMOD.System system, RadiatorState state, out FMOD.DSP dsp, out GCHandle handle)
    {
        var desc = new DSP_DESCRIPTION
        {
            pluginsdkversion = VERSION.number,
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

    private static RESULT ReadCallback(ref DSP_STATE dsp_state, IntPtr inbuffer, IntPtr outbuffer,
                                       uint length, int inchannels, ref int outchannels)
    {
        // Never throws into the mixer thread: anything wrong passes the voice through as it is.
        long started = MixerProfile.Start();
        try
        {
            if (outchannels == 0) outchannels = inchannels;
            IntPtr userData = DspCallback.UserData(ref dsp_state);
            if (userData == IntPtr.Zero || inbuffer == IntPtr.Zero || outbuffer == IntPtr.Zero || inchannels != outchannels
                || GCHandle.FromIntPtr(userData).Target is not RadiatorState s)
            {
                DspCallback.PassThrough(inbuffer, outbuffer, length, inchannels, outchannels);
                return RESULT.OK;
            }
            unsafe
            {
                int count = (int)length * inchannels;
                var input = new ReadOnlySpan<float>((void*)inbuffer, count);
                var output = new Span<float>((void*)outbuffer, count);
                s.Process(input, output, inchannels);
                NonFinite.Scrub(output, ref s.NonFiniteReported, "a loudspeaker's beam");
            }
        }
        catch (Exception ex)
        {
            DspCallback.PassThrough(inbuffer, outbuffer, length, inchannels, outchannels);
            DspFault.Record("Radiator", ex);
        }
        MixerProfile.Stop(MixerProfile.Kind.Loudspeaker, started);
        return RESULT.OK;
    }
}

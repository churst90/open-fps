using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace OpenFPS.Client.AudioEngine.Core.Nature;

/// <summary>
/// Where a texture made of many small events is summed: a bubble ringing, a drop's impact, a leaf
/// touching a leaf, a pocket of steam bursting in a log.
///
/// Water, fire and wind in leaves are thousands of short physical events a second, and the ear hears
/// their structure, not just their spectrum: band-filtered noise is what three rounds of footstep
/// synthesis failed with (McDermott and Simoncelli 2011: one event is broadband and hits every band at
/// once). So each event is written whole into a ring at the sample it happens, and the output reads the
/// ring and clears it behind itself. An event may ring on for the ring's length; the longest, a
/// centimetre bubble, rings for about a fifth of a second.
///
/// Every primitive takes its amplitude in pascals at a metre: the owning synth decides how big each
/// event is, and nothing here rescales it.
/// </summary>
public sealed class EventSum
{
    private const int Bits = 15;                 // 32768 samples: 0.68 s at 48 kHz
    private const int Mask = (1 << Bits) - 1;
    private const int RingLength = 1 << Bits;
    private readonly float[] _ring = new float[RingLength];
    private readonly float _rate;
    private long _now;
    private uint _rng;

    private const int PulseTableRes = 32;
    /// <summary>−t exp(−t²/2) from t = −4 to 4, PulseTableRes points per unit of t.</summary>
    private static readonly float[] PulseTable = MakePulseTable();

    // An explicit static constructor, so the table is made when the first sum is (off the audio threads),
    // not whenever a pulse is first placed: without one the runtime may defer it, and a burning car's first
    // pulse 23 minutes into its fire built it on the mixer's thread (1,056 bytes; FireTests.NothingAllocates).
    static EventSum() { }

    private static float[] MakePulseTable()
    {
        var table = new float[8 * PulseTableRes + 2];
        for (int j = 0; j < table.Length; j++)
        {
            float t = j / (float)PulseTableRes - 4f;
            table[j] = -t * MathF.Exp(-0.5f * t * t);
        }
        return table;
    }

    public EventSum(float sampleRate, int seed)
    {
        _rate = sampleRate;
        _rng = (uint)seed * 2654435761u | 1u;
    }

    /// <summary>The sample the next <see cref="Next"/> returns. Events are placed relative to it.</summary>
    public long Now => _now;

    /// <summary>How far ahead an event may reach, offset and all, samples: past it would wrap onto
    /// the present. Every primitive cuts itself off there.</summary>
    public int Horizon => _ring.Length - 4096;

    /// <summary>The next output sample, pascals at a metre.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Next()
    {
        int i = (int)(_now & Mask);
        float v = _ring[i];
        _ring[i] = 0f;
        _now++;
        return v;
    }

    /// <summary>The next <paramref name="into"/>.Length output samples, as that many calls to <see cref="Next"/>.</summary>
    public void Take(Span<float> into)
    {
        for (int i = 0; i < into.Length; i++)
        {
            int at = (int)(_now & Mask);
            into[i] = _ring[at];
            _ring[at] = 0f;
            _now++;
        }
    }

    /// <summary>A uniform number in [0, 1). One generator per sum, so a render is repeatable from its seed.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Uniform()
    {
        _rng ^= _rng << 13; _rng ^= _rng >> 17; _rng ^= _rng << 5;
        return (_rng >> 8) * (1f / 16777216f);
    }

    /// <summary>A uniform number in [-1, 1).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Signed() => 2f * Uniform() - 1f;

    /// <summary>A Poisson count with this mean: exact below thirty, Gaussian above.</summary>
    public int Poisson(float mean)
    {
        if (mean <= 0f) return 0;
        if (mean < 30f)
        {
            float limit = MathF.Exp(-mean), p = 1f;
            int k = 0;
            do { k++; p *= Uniform(); } while (p > limit);
            return k - 1;
        }
        float g = MathF.Sqrt(-2f * MathF.Log(MathF.Max(1e-7f, Uniform()))) * MathF.Cos(MathF.Tau * Uniform());
        return Math.Max(0, (int)MathF.Round(mean + MathF.Sqrt(mean) * g));
    }

    /// <summary>
    /// A gas bubble ringing: a damped sinusoid at its Minnaert frequency, rising as it goes.
    ///
    /// A bubble just under a surface rings at 3.26/R Hz (R in metres) and loses its energy in tens of
    /// cycles — radiation, heat into the water and viscosity, a damping ratio of a few hundredths.
    /// As it rises toward the surface the water above it stiffens it and its note climbs: the
    /// "plink" of a drop and the "bloop" of a stream (van den Doel 2005: f(t) = f0 (1 + ξ d t),
    /// ξ about 0.1, with d the decay rate). It starts from a pressure step as the neck pinches off,
    /// which is the one-cycle onset.
    /// </summary>
    /// <param name="offset">Samples from <see cref="Now"/>.</param>
    /// <param name="hz">The Minnaert frequency at pinch-off.</param>
    /// <param name="damping">The damping ratio δ: the decay rate is π δ f.</param>
    /// <param name="pascals">The first peak, Pa at a metre.</param>
    /// <param name="rise">ξ: how far the note climbs per decay time.</param>
    public void Bubble(int offset, float hz, float damping, float pascals, float rise)
    {
        if (hz <= 0f || pascals <= 0f || hz > 0.45f * _rate) return;
        float decay = MathF.PI * damping * hz;                     // 1/s
        // To -40 dB: a bubble 40 dB down its own decay is under the thousands of others round it.
        int length = Math.Min(Horizon - offset, (int)(4.6f / decay * _rate));
        float perSample = MathF.Exp(-decay / _rate);
        float sigma = rise * decay;                                // relative climb per second
        int start = (int)((_now + offset) & Mask);
        float onset = 1f / MathF.Max(1f, _rate / hz);              // one cycle to full
        ref float ring = ref MemoryMarshal.GetArrayDataReference(_ring);
        // The hottest loop in the water models (a surf beach rings 50 million bubble samples a second), so
        // a vector of samples at a time, each its own sine of the phase: a rotation carried from sample to
        // sample is a chain the core waits on. The phase is kept in double; the note is retuned every
        // sixteen samples. Within 1e-5 of the rotation it replaced (docs/WAVES_AND_SHORES.md 8.2).
        int lanes = Vector<float>.Count;
        var lane = LaneIndex;
        Span<float> fall = stackalloc float[lanes];
        float decayed = 1f;
        for (int l = 0; l < lanes; l++) { fall[l] = decayed; decayed *= perSample; }
        var env = new Vector<float>(fall) * pascals;
        var step = new Vector<float>(decayed);
        double phase = 0;
        for (int i0 = 0; i0 < length; i0 += 16)
        {
            float f = hz * (1f + sigma * i0 / _rate);
            if (f > 0.45f * _rate) break;
            float w = MathF.Tau * f / _rate;
            var from = new Vector<float>((float)phase);
            for (int j = 0; j < 16 && i0 + j < length; j += lanes)
            {
                int i = i0 + j;
                var y = env * Sine(from + (lane + new Vector<float>(j)) * w);
                if (i * onset < 1f) y *= Vector.Min((lane + new Vector<float>(i)) * onset, Vector<float>.One);
                int at = (start + i) & Mask, left = length - i;
                if (left < lanes) y = Vector.ConditionalSelect(Vector.LessThan(lane, new Vector<float>(left)), y, Vector<float>.Zero);
                if (at + lanes <= RingLength)
                {
                    ref float cell = ref Unsafe.Add(ref ring, at);
                    Vector.StoreUnsafe(Vector.LoadUnsafe(ref cell) + y, ref cell);
                }
                else
                    for (int l = 0; l < lanes; l++) Unsafe.Add(ref ring, (at + l) & Mask) += y[l];
                env *= step;
            }
            phase += 16 * (double)w;
            phase -= Math.Tau * Math.Round(phase * (1 / Math.Tau));
        }
    }

    private static readonly Vector<float> LaneIndex = MakeLaneIndex();

    /// <summary>sin x for |x| up to a few dozen radians, within about 2e-7: x less the nearest multiple of
    /// π (in two parts, Cody and Waite, so the reduction keeps what the polynomial needs), then the series
    /// to x^11, whose first term left out is under 6e-8 at π/2. Plain multiplies and adds, so it is the
    /// same on every machine.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector<float> Sine(Vector<float> x)
    {
        // Adding 1.5 * 2^23 rounds to the nearest integer and leaves its parity in the lowest bit.
        var t = x * InversePi + RoundingMagic;
        var k = t - RoundingMagic;
        var r = x - k * PiHigh - k * PiLow;
        var r2 = r * r;
        var p = r + r * r2 * (S3 + r2 * (S5 + r2 * (S7 + r2 * (S9 + r2 * S11))));
        // An odd multiple of π turns the sine over.
        return Vector.AsVectorSingle(Vector.AsVectorInt32(p) ^ Vector.ShiftLeft(Vector.AsVectorInt32(t), 31));
    }

    private static readonly Vector<float> InversePi = new(1f / MathF.PI), RoundingMagic = new(12582912f),
                                          PiHigh = new(3.140625f), PiLow = new(9.676535897932795e-4f),
                                          S3 = new(-1f / 6f), S5 = new(1f / 120f), S7 = new(-1f / 5040f),
                                          S9 = new(1f / 362880f), S11 = new(-1f / 39916800f);

    private static Vector<float> MakeLaneIndex()
    {
        Span<float> v = stackalloc float[Vector<float>.Count];
        for (int l = 0; l < v.Length; l++) v[l] = l;
        return new Vector<float>(v);
    }

    /// <summary>
    /// An impact pulse: the pressure radiated by a force that rises and falls over a contact time,
    /// which is the force's time derivative — a positive lobe then a negative one, with nothing left
    /// over (a monopole cannot pump net air). Its spectrum peaks near 1 / (2π σ): a millimetre drop at
    /// five metres a second is a click at a few kilohertz, a log settling is a thud.
    /// </summary>
    /// <param name="offset">Samples from <see cref="Now"/>.</param>
    /// <param name="sigma">The contact time's standard deviation, seconds.</param>
    /// <param name="pascals">The peak, Pa at a metre.</param>
    public void Pulse(int offset, float sigma, float pascals)
    {
        if (pascals <= 0f) return;
        float s = MathF.Max(0.5f / _rate, sigma) * _rate;      // samples
        int half = (int)MathF.Ceiling(4f * s);
        if (offset + 2 * half >= Horizon) return;
        long at = _now + offset + half;
        // -t/s exp(-t²/2s²) peaks at t = s with value e^-½; scale so the peak is `pascals`.
        float k = pascals * 1.6487213f;
        for (int i = -half; i <= half; i++)
        {
            float t = (i / s + 4f) * PulseTableRes;
            if (t < 0f) continue;
            int j = (int)t;
            if (j >= PulseTable.Length - 1) break;
            float frac = t - j;
            _ring[(int)((at + i) & Mask)] += k * (PulseTable[j] + (PulseTable[j + 1] - PulseTable[j]) * frac);
        }
    }

    /// <summary>
    /// A strike whose force rises almost at once and dies away: a drop meeting water, a pocket of steam
    /// bursting. The pressure radiated is the force's rate of change — a sharp positive spike as the
    /// force arrives, then a slow negative tail as it goes, the two cancelling so no net air is moved.
    /// Above 1 / (2π τ) its energy per octave falls 3 dB an octave, up to the rise: a click with a body,
    /// where a symmetric pulse is a tone-like blip at one frequency.
    /// </summary>
    /// <param name="offset">Samples from <see cref="Now"/>.</param>
    /// <param name="rise">The force's rise time, seconds (a standard deviation).</param>
    /// <param name="tau">How long the force takes to die, seconds.</param>
    /// <param name="pascals">The spike's peak, Pa at a metre.</param>
    public void Impact(int offset, float rise, float tau, float pascals)
    {
        if (pascals <= 0f) return;
        float s = MathF.Max(0.35f, rise * _rate);              // samples
        int half = (int)MathF.Ceiling(3f * s);
        float tauSamples = MathF.Max(1f, tau * _rate);
        int tail = Math.Min((int)(5f * tauSamples), Horizon - offset - 2 * half - 1);
        if (tail <= 0) return;
        long at = _now + offset + half;
        // The spike: a Gaussian of area A = P s √(2π) (in samples).
        float area = 0f;
        for (int i = -half; i <= half; i++)
        {
            float t = i / s;
            float g = pascals * MathF.Exp(-0.5f * t * t);
            area += g;
            _ring[(int)((at + i) & Mask)] += g;
        }
        // The tail takes the same area back, as an exponential of time constant tau.
        float per = MathF.Exp(-1f / tauSamples);
        float amp = -area * (1f - per) / (1f - MathF.Pow(per, tail));
        for (int i = 1; i <= tail; i++)
        {
            _ring[(int)((at + i) & Mask)] += amp;
            amp *= per;
        }
    }

    /// <summary>
    /// A burst of noise in a band: what a crowd of tiny events too fast to tell apart makes — a leaf's
    /// membrane after it is struck, the spray off an impact, a char fragment rattling.
    /// </summary>
    /// <param name="offset">Samples from <see cref="Now"/>.</param>
    /// <param name="rise">The rise, seconds.</param>
    /// <param name="decay">The decay's time constant, seconds.</param>
    /// <param name="pascals">The rms at the top, Pa at a metre.</param>
    /// <param name="lowHz">The band's low edge.</param>
    /// <param name="highHz">The band's high edge.</param>
    /// <param name="steep">A band that is a band: two resonant band-passes (RBJ, 0 dB at the middle)
    /// between <paramref name="lowHz"/> and <paramref name="highHz"/>, falling 12 dB an octave and more
    /// outside it, for a burst that should light its own band and not the ones an octave away (a
    /// splash's spray, whose droplets are of one size; see FallingWaterSynth.SplashCentreHz). The
    /// one-pole edges of the plain burst are a gentle slope, an octave or two wide whatever the edges.</param>
    public void Burst(int offset, float rise, float decay, float pascals, float lowHz, float highHz, bool steep = false)
    {
        if (pascals <= 0f) return;
        if (steep) { ResonantBurst(offset, rise, decay, pascals, lowHz, MathF.Min(highHz, 0.45f * _rate)); return; }
        float a1 = MathF.Exp(-MathF.Tau * MathF.Min(highHz, 0.45f * _rate) / _rate);
        float a2 = MathF.Exp(-MathF.Tau * lowHz / _rate);
        // The band's own gain on white noise (two poles at the top, one at the bottom), so the rms
        // comes out as asked.
        float band = MathF.Max(1e-4f, (MathF.Min(highHz, 0.45f * _rate) - lowHz) / (0.5f * _rate));
        float gain = pascals / MathF.Sqrt(band) * 1.7320508f;
        int length = Math.Min(Horizon - offset, (int)((rise + 6f * decay) * _rate));
        int start = (int)((_now + offset) & Mask);
        float riseSamples = MathF.Max(1f, rise * _rate);
        float down = MathF.Exp(-1f / MathF.Max(1f, decay * _rate));
        _rng = BandNoise(_ring, start, length, _rng, a1, a2, gain, riseSamples, down);
    }

    // On its own for the same reason as ResonantNoise.
    private static uint BandNoise(float[] ringArray, int start, int length, uint rng, float a1, float a2,
                                  float gain, float riseSamples, float down)
    {
        float lp0 = 0f, lp = 0f, hp = 0f;
        float env = 0f, c1 = 1f - a1, c2 = 1f - a2;
        var shape = new Envelope(riseSamples);
        ref float ring = ref MemoryMarshal.GetArrayDataReference(ringArray);
        for (int i = 0; i < length; i++)
        {
            float x = Signed(ref rng);
            lp0 = c1 * x + a1 * lp0;
            lp = c1 * lp0 + a1 * lp;
            hp = c2 * lp + a2 * hp;
            env = shape.At(i, env, down);
            Unsafe.Add(ref ring, (start + i) & Mask) += gain * env * (lp - hp);
        }
        return rng;
    }

    /// <summary>A burst's envelope: a ramp up over <c>rise</c> samples, then a fall by <c>down</c> a sample.</summary>
    private readonly struct Envelope(float rise)
    {
        private readonly int _ramp = (int)MathF.Ceiling(rise), _top = (int)rise;

        /// <summary>The envelope at sample <paramref name="i"/>, from the one before it.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float At(int i, float before, float down)
            => i < _ramp ? i / rise : i == _ramp && i == _top ? 1f : before * down;
    }

    /// <summary><see cref="Signed()"/> on a copy of the generator, for a loop that keeps it in a register.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Signed(ref uint rng)
    {
        rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;
        return 2f * ((rng >> 8) * (1f / 16777216f)) - 1f;
    }

    private void ResonantBurst(int offset, float rise, float decay, float pascals, float lowHz, float highHz)
    {
        if (highHz <= lowHz) return;
        float fc = MathF.Sqrt(lowHz * highHz);
        float q = fc / (highHz - lowHz);
        float w = MathF.Tau * fc / _rate, alpha = MathF.Sin(w) / (2f * q), cw = MathF.Cos(w);
        float a0 = 1f + alpha;
        float b0 = alpha / a0, a1 = -2f * cw / a0, a2 = (1f - alpha) / a0;   // b1 = 0, b2 = -b0
        // White noise of variance 1/3 through two of these: the cascade's noise bandwidth is about
        // π/4 of the band between the edges, so this scale makes the rms at the top `pascals`.
        float enbw = MathF.PI / 4f * (highHz - lowHz);
        float gain = pascals / MathF.Sqrt(enbw * 2f / _rate / 3f);
        int length = Math.Min(Horizon - offset, (int)((rise + 6f * decay) * _rate));
        int start = (int)((_now + offset) & Mask);
        float riseSamples = MathF.Max(1f, rise * _rate);
        float down = MathF.Exp(-1f / MathF.Max(1f, decay * _rate));
        _rng = ResonantNoise(_ring, start, length, _rng, b0, a1, a2, gain, riseSamples, down);
    }

    // On its own, with nothing else live, so the JIT keeps the eight filter states in registers: inside
    // ResonantBurst it kept them on the stack, which doubled the time a sample takes.
    private static uint ResonantNoise(float[] ringArray, int start, int length, uint rng, float b0, float a1, float a2,
                                      float gain, float riseSamples, float down)
    {
        float x1 = 0f, x2 = 0f, y1 = 0f, y2 = 0f, u1 = 0f, u2 = 0f, z1 = 0f, z2 = 0f;
        float env = 0f;
        var shape = new Envelope(riseSamples);
        ref float ring = ref MemoryMarshal.GetArrayDataReference(ringArray);
        for (int i = 0; i < length; i++)
        {
            float x = Signed(ref rng);
            float y = b0 * (x - x2) - a1 * y1 - a2 * y2;
            x2 = x1; x1 = x; y2 = y1; y1 = y;
            float z = b0 * (y - u2) - a1 * z1 - a2 * z2;
            u2 = u1; u1 = y; z2 = z1; z1 = z;
            env = shape.At(i, env, down);
            Unsafe.Add(ref ring, (start + i) & Mask) += gain * env * z;
        }
        return rng;
    }

    /// <summary>
    /// A small hard thing ringing after a tap: a few modes of a piece of charcoal, a pebble, a
    /// twig. Each mode a damped sinusoid with its own decay; the strike is in phase on all of them.
    /// </summary>
    public void Ring(int offset, ReadOnlySpan<float> hz, ReadOnlySpan<float> decaySeconds, ReadOnlySpan<float> pascals)
    {
        long at = _now + offset;
        for (int m = 0; m < hz.Length; m++)
        {
            if (hz[m] <= 0f || hz[m] > 0.45f * _rate) continue;
            int length = Math.Min(Horizon - offset, (int)(6.9f * decaySeconds[m] * _rate));
            float w = MathF.Tau * hz[m] / _rate, cw = MathF.Cos(w), sw = MathF.Sin(w);
            float per = MathF.Exp(-1f / (decaySeconds[m] * _rate));
            float re = 1f, im = 0f, env = pascals[m];
            for (int i = 0; i < length; i++)
            {
                _ring[(int)((at + i) & Mask)] += env * im;
                float nr = re * cw - im * sw;
                im = re * sw + im * cw;
                re = nr;
                env *= per;
                if ((i & 255) == 255) { float mag = 1f / MathF.Sqrt(re * re + im * im); re *= mag; im *= mag; }
            }
        }
    }
}

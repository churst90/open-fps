using System.Numerics;

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// The late part of a traced impulse response, ready to convolve: the trace's own omnidirectional
/// channel, faded in, partitioned in the frequency domain.
///
/// Not Steam Audio's parametric reverb: that takes three decay times from the trace and not its level
/// or envelope, and measured 14-20 dB too loud in the tunnel, 8-11 in a street, 0-3 in flat 01F, silent
/// to 60 ms and then a plateau. The SDK keeps the traced IR opaque, so it is read back through a
/// private convolution (TracedReverb.ExtractLate) and played here.
/// </summary>
internal sealed class LateTailIr
{
    /// <summary>The raised-cosine fade-in of a tail that starts after the placed copies
    /// (WorldAudioPlayer's 80 ms window), so the two meet with no step.</summary>
    public const float FadeInStartSeconds = 0.05f, FadeInEndSeconds = 0.10f;

    public readonly int Block, Bins, Partitions;
    /// <summary>Partition p, bin k at [p * Bins + k].</summary>
    public readonly float[] Re, Im;
    /// <summary>Energy of the windowed late part, for diagnostics.</summary>
    public readonly double Energy;

    /// <summary>
    /// A two-level response (SharedInputConvolver): <see cref="Partitions"/> of <see cref="Block"/>
    /// cover the first <see cref="LongBlock"/> samples, and <see cref="LongPartitions"/> of
    /// <see cref="LongBlock"/> the rest. Zero: one level, all in <see cref="Re"/>.
    /// </summary>
    public readonly int LongBlock, LongBins, LongPartitions;
    /// <summary>Long partition q (samples from (q + 1) * LongBlock), bin k at [q * LongBins + k].</summary>
    public readonly float[] LongRe, LongIm;

    private LateTailIr(int block, int partitions, double energy, int longBlock = 0, int longPartitions = 0)
    {
        Block = block; Bins = block + 1; Partitions = partitions; Energy = energy;
        Re = new float[partitions * Bins]; Im = new float[partitions * Bins];
        LongBlock = longPartitions > 0 ? longBlock : 0; LongBins = LongBlock > 0 ? LongBlock + 1 : 0; LongPartitions = longPartitions;
        LongRe = new float[longPartitions * LongBins]; LongIm = new float[longPartitions * LongBins];
    }

    /// <summary>
    /// The late part of <paramref name="w"/> (the IR's omnidirectional channel, time zero at the
    /// direct sound), windowed and cut where what remains is 70 dB under what was there, into
    /// partitions of <paramref name="block"/> samples. Worker thread: it allocates.
    /// </summary>
    public static LateTailIr Build(float[] w, int sampleRate, int block, int maxPartitions,
                                   float fadeInStart = FadeInStartSeconds, float fadeInEnd = FadeInEndSeconds)
    {
        int n = w.Length;
        int a = (int)(fadeInStart * sampleRate), b = (int)(fadeInEnd * sampleRate);
        var x = new float[n];
        for (int i = a; i < n; i++)
            x[i] = w[i] * (i >= b ? 1f : 0.5f - 0.5f * MathF.Cos(MathF.PI * (i - a) / Math.Max(1, b - a)));
        return FromWindowed(x, block, maxPartitions);
    }

    /// <summary>An IR already windowed as it is to be played, cut 70 dB down, into partitions; past
    /// <paramref name="longBlock"/> samples (a multiple of the block; 0: never) into partitions of that.</summary>
    public static LateTailIr FromWindowed(float[] x, int block, int maxPartitions, int longBlock = 0)
    {
        int n = x.Length;
        double total = 0;
        for (int i = 0; i < n; i++) total += x[i] * (double)x[i];
        // Where the rest is 70 dB down: no partition past it is worth its cost.
        int end = 0;   // silence is one empty partition, not the longest
        double rest = 0;
        for (int i = n - 1; i >= 0 && total > 0; i--)
        {
            rest += x[i] * (double)x[i];
            if (rest > total * 1e-7) { end = i + 1; break; }
        }
        int partitions = Math.Clamp((end + block - 1) / block, 1, maxPartitions);
        // The response is cut at the end of its last partition, whichever level carries it.
        int length = Math.Min(n, partitions * block);
        int head = partitions, longs = 0;
        if (longBlock > block && longBlock % block == 0 && length > longBlock)
        {
            head = longBlock / block;
            longs = (length - longBlock + longBlock - 1) / longBlock;
        }
        var ir = new LateTailIr(block, head, total, longBlock, longs);
        Transform(x, 0, length, block, head, ir.Re, ir.Im);
        if (longs > 0) Transform(x, longBlock, length, longBlock, longs, ir.LongRe, ir.LongIm);
        return ir;
    }

    /// <summary>The response back in time, both levels: for the lab and the tests.</summary>
    public float[] ToTime()
    {
        var y = new float[Partitions * Block + LongPartitions * LongBlock];
        Untransform(Re, Im, Block, Partitions, y, 0);
        if (LongPartitions > 0) Untransform(LongRe, LongIm, LongBlock, LongPartitions, y, LongBlock);
        return y;
    }

    private static void Untransform(float[] specRe, float[] specIm, int block, int count, float[] y, int from)
    {
        int n = 2 * block, bins = block + 1;
        var fft = new Fft(n);
        var re = new float[n]; var im = new float[n];
        for (int p = 0; p < count; p++)
        {
            for (int k = 0; k < bins; k++) { re[k] = specRe[p * bins + k]; im[k] = specIm[p * bins + k]; }
            for (int k = 1; k < block; k++) { re[n - k] = re[k]; im[n - k] = -im[k]; }
            fft.Inverse(re, im);
            Array.Copy(re, 0, y, from + p * block, block);
        }
    }

    /// <summary>Partitions of <paramref name="block"/> samples of x from <paramref name="from"/>,
    /// nothing at or past <paramref name="length"/>, each zero-padded to twice its size and transformed.</summary>
    private static void Transform(float[] x, int from, int length, int block, int count, float[] outRe, float[] outIm)
    {
        int bins = block + 1;
        var fft = new Fft(2 * block);
        var re = new float[2 * block]; var im = new float[2 * block];
        for (int p = 0; p < count; p++)
        {
            Array.Clear(re); Array.Clear(im);
            for (int k = 0; k < block; k++) { int i = from + p * block + k; if (i < length) re[k] = x[i]; }
            fft.Forward(re, im);
            Array.Copy(re, 0, outRe, p * bins, bins);
            Array.Copy(im, 0, outIm, p * bins, bins);
        }
    }
}

/// <summary>
/// Uniformly partitioned overlap-save convolution of one channel through a <see cref="LateTailIr"/>,
/// one block at a time, on the mixer thread: nothing allocates after construction and nothing throws.
/// A new IR takes over across one block, convolved through both and crossfaded, so a new trace is
/// never a click.
/// </summary>
internal sealed class LateTailConvolver
{
    private readonly int _block, _bins, _maxPartitions;
    private readonly Fft _fft;
    private readonly float[] _prev;                  // the previous input block
    private readonly float[] _fdlRe, _fdlIm;          // input spectra, a ring of _maxPartitions
    private int _head;
    private readonly float[] _re, _im, _accRe, _accIm, _out, _outNew;
    private LateTailIr? _ir;
    private volatile LateTailIr? _next;

    public LateTailConvolver(int block, int maxPartitions)
    {
        _block = block; _bins = block + 1; _maxPartitions = maxPartitions;
        _fft = new Fft(2 * block);
        _prev = new float[block];
        _fdlRe = new float[maxPartitions * _bins]; _fdlIm = new float[maxPartitions * _bins];
        _re = new float[2 * block]; _im = new float[2 * block];
        _accRe = new float[_bins]; _accIm = new float[_bins];
        _out = new float[block]; _outNew = new float[block];
    }

    /// <summary>The IR to play from the next block on. Any thread.</summary>
    public void SetIr(LateTailIr? ir)
    {
        if (ir != null && (ir.Block != _block || ir.Partitions > _maxPartitions)) return;
        _next = ir;
    }

    /// <summary>Convolves one block of <paramref name="input"/> into <paramref name="output"/>.</summary>
    public void Process(ReadOnlySpan<float> input, Span<float> output)
    {
        // The input frame: the previous block and this one, transformed once for every partition.
        for (int k = 0; k < _block; k++) { _re[k] = _prev[k]; _re[_block + k] = input[k]; _im[k] = 0f; _im[_block + k] = 0f; }
        input.Slice(0, _block).CopyTo(_prev);
        _fft.Forward(_re, _im);
        _head = (_head + 1) % _maxPartitions;
        Array.Copy(_re, 0, _fdlRe, _head * _bins, _bins);
        Array.Copy(_im, 0, _fdlIm, _head * _bins, _bins);

        var ir = _ir;
        var next = _next;
        bool swap = !ReferenceEquals(next, ir);
        if (ir == null && next == null) { output.Slice(0, _block).Clear(); return; }

        if (ir != null) Convolve(ir, _out); else Array.Clear(_out);
        if (swap)
        {
            if (next != null) Convolve(next, _outNew); else Array.Clear(_outNew);
            for (int k = 0; k < _block; k++)
            {
                float t = (k + 0.5f) / _block;
                float up = 0.5f - 0.5f * MathF.Cos(MathF.PI * t);
                _out[k] = _out[k] * (1f - up) + _outNew[k] * up;
            }
            _ir = next;
        }
        _out.AsSpan(0, _block).CopyTo(output);
    }

    /// <summary>Sum over partitions of the delayed input spectra times the IR's, back to time.</summary>
    private void Convolve(LateTailIr ir, float[] y)
    {
        Array.Clear(_accRe); Array.Clear(_accIm);
        int bins = _bins;
        int vec = Vector<float>.Count;
        for (int p = 0; p < ir.Partitions; p++)
        {
            int slot = _head - p; if (slot < 0) slot += _maxPartitions;
            int xo = slot * bins, ho = p * bins;
            int k = 0;
            for (; k + vec <= bins; k += vec)
            {
                var xr = new Vector<float>(_fdlRe, xo + k); var xi = new Vector<float>(_fdlIm, xo + k);
                var hr = new Vector<float>(ir.Re, ho + k); var hi = new Vector<float>(ir.Im, ho + k);
                var ar = new Vector<float>(_accRe, k); var ai = new Vector<float>(_accIm, k);
                (ar + xr * hr - xi * hi).CopyTo(_accRe, k);
                (ai + xr * hi + xi * hr).CopyTo(_accIm, k);
            }
            for (; k < bins; k++)
            {
                float xr = _fdlRe[xo + k], xi = _fdlIm[xo + k], hr = ir.Re[ho + k], hi = ir.Im[ho + k];
                _accRe[k] += xr * hr - xi * hi;
                _accIm[k] += xr * hi + xi * hr;
            }
        }
        // The full spectrum of a real signal from its first half, then back.
        int n = 2 * _block;
        for (int k = 0; k < bins; k++) { _re[k] = _accRe[k]; _im[k] = _accIm[k]; }
        for (int k = 1; k < _block; k++) { _re[n - k] = _accRe[k]; _im[n - k] = -_accIm[k]; }
        _fft.Inverse(_re, _im);
        // Overlap-save: the second half is the circular-convolution-free part.
        for (int k = 0; k < _block; k++) y[k] = _re[_block + k];
    }

    public void Reset()
    {
        Array.Clear(_prev); Array.Clear(_fdlRe); Array.Clear(_fdlIm);
    }
}

/// <summary>
/// An in-place radix-2 complex FFT of one fixed size. Allocation-free after construction.
///
/// Each stage's twiddles are laid out in a row of their own, so a stage's butterflies run through
/// memory in order and, from eight wide up, a vector at a time: the 8,192-point transforms of the
/// late field (DiffuseLate) took 82 us read from one shared table at a stride.
/// </summary>
internal sealed class Fft
{
    private readonly int _n;
    private readonly int[] _rev;
    // Stage s (butterflies 2^(s+1) wide, half = 2^s) has its twiddles at [half - 1, 2 * half - 1).
    private readonly float[] _twRe, _twIm;

    public Fft(int n)
    {
        if (n < 2 || (n & (n - 1)) != 0) throw new ArgumentException("FFT size must be a power of two.", nameof(n));
        _n = n;
        _rev = new int[n];
        int bits = 0; while ((1 << bits) < n) bits++;
        for (int i = 0; i < n; i++)
        {
            int r = 0; for (int b = 0; b < bits; b++) if ((i & (1 << b)) != 0) r |= 1 << (bits - 1 - b);
            _rev[i] = r;
        }
        _twRe = new float[n]; _twIm = new float[n];
        for (int half = 1; half < n; half <<= 1)
            for (int k = 0; k < half; k++)
            {
                double a = -Math.PI * k / half;
                _twRe[half - 1 + k] = (float)Math.Cos(a); _twIm[half - 1 + k] = (float)Math.Sin(a);
            }
    }

    public void Forward(float[] re, float[] im) => Run(re, im, false);

    /// <summary>The inverse, scaled by 1/n.</summary>
    public void Inverse(float[] re, float[] im)
    {
        Run(re, im, true);
        float s = 1f / _n;
        int i = 0, vec = Vector<float>.Count;
        var vs = new Vector<float>(s);
        for (; i + vec <= _n; i += vec)
        {
            (new Vector<float>(re, i) * vs).CopyTo(re, i);
            (new Vector<float>(im, i) * vs).CopyTo(im, i);
        }
        for (; i < _n; i++) { re[i] *= s; im[i] *= s; }
    }

    private void Run(float[] re, float[] im, bool inverse)
    {
        for (int i = 0; i < _n; i++)
        {
            int j = _rev[i];
            if (j > i) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        float sign = inverse ? -1f : 1f;
        int vec = Vector<float>.Count;
        var vsign = new Vector<float>(sign);
        for (int half = 1; half < _n; half <<= 1)
        {
            int size = half << 1, t0 = half - 1;
            if (half >= vec)
            {
                for (int start = 0; start < _n; start += size)
                    for (int k = 0; k < half; k += vec)
                    {
                        int a = start + k, b = a + half;
                        var wr = new Vector<float>(_twRe, t0 + k); var wi = new Vector<float>(_twIm, t0 + k) * vsign;
                        var br = new Vector<float>(re, b); var bi = new Vector<float>(im, b);
                        var tr = br * wr - bi * wi; var ti = br * wi + bi * wr;
                        var ar = new Vector<float>(re, a); var ai = new Vector<float>(im, a);
                        (ar - tr).CopyTo(re, b); (ai - ti).CopyTo(im, b);
                        (ar + tr).CopyTo(re, a); (ai + ti).CopyTo(im, a);
                    }
            }
            else
            {
                for (int start = 0; start < _n; start += size)
                    for (int k = 0; k < half; k++)
                    {
                        float wr = _twRe[t0 + k], wi = _twIm[t0 + k] * sign;
                        int a = start + k, b = a + half;
                        float tr = re[b] * wr - im[b] * wi, ti = re[b] * wi + im[b] * wr;
                        re[b] = re[a] - tr; im[b] = im[a] - ti;
                        re[a] += tr; im[a] += ti;
                    }
            }
        }
    }
}

/// <summary>
/// The directional part of the tail, by the Spatial Decomposition Method (Tervo et al. 2013): the
/// traced response split sample by sample by the direction its sound arrives from, snapped to the
/// nearest of the tail's directions (DiffuseTail.Direction). The parts sum back to the trace exactly.
///
/// One channel spread over twenty directions is the same whichever way you face and sits in front of
/// you as a mass of reverb; the trace's second and third bounces come off particular walls, so they
/// are played from those walls and move round the head as it turns. Past
/// <see cref="EndFadeStart"/>..<see cref="EndFadeEnd"/> the trace arrives from everywhere and the
/// diffuse rendering takes over, with complementary windows.
///
/// Direction per sample: the intensity W x (first-order channels) over <see cref="DoaWindow"/>
/// samples (about 0.7 ms), through the measured channel axes (AmbiAxes). Snapping to fixed directions
/// keeps each response from being a comb of lone samples (Amengual Garí et al., BinauralSDM).
/// </summary>
internal sealed class SdmTailIr
{
    public const float EndFadeStart = 0.25f, EndFadeEnd = 0.35f;
    public const int DoaWindow = 32;
    /// <summary>The responses' long partitions (SharedInputConvolver), samples: the mixer's block
    /// (FmodAudioProvider's setDSPBufferSize), so their work falls in every mixer callback alike.</summary>
    public const int LongBlock = 1024;

    /// <summary>One response per direction; null where nothing came from that way.</summary>
    public readonly LateTailIr?[] PerDirection;
    public readonly int Block, MaxPartitions;
    /// <summary>Each direction's share of the directional part's energy, for the lab.</summary>
    public readonly float[] Share;
    /// <summary>
    /// Each direction's share of the energy AFTER the directional part (from <see cref="EndFadeStart"/>
    /// to the end of the trace): the remainder's energy distribution to second order (TracedReverb
    /// fills it). The diffuse remainder is weighted by these: even in a room, the two ends of a corridor.
    /// </summary>
    public readonly float[] LateShare;

    private SdmTailIr(int k, int block, int maxPartitions)
    {
        PerDirection = new LateTailIr?[k]; Share = new float[k]; LateShare = new float[k]; Block = block; MaxPartitions = maxPartitions;
    }

    public static int PartitionsFor(int sampleRate, int block) => (int)(EndFadeEnd * sampleRate) / block + 2;

    /// <summary>Splits the trace <paramref name="w"/> (omnidirectional) and <paramref name="c1"/>..
    /// <paramref name="c3"/> (first order, whose axes in Steam Audio's world are <paramref name="a1"/>..
    /// <paramref name="a3"/>) over <paramref name="directions"/> (the game's world), in partitions of
    /// <paramref name="block"/> samples at <paramref name="sampleRate"/>.</summary>
    public static SdmTailIr Build(float[] w, float[] c1, float[] c2, float[] c3,
                                  System.Numerics.Vector3 a1, System.Numerics.Vector3 a2, System.Numerics.Vector3 a3,
                                  System.Numerics.Vector3[] directions, int sampleRate, int block)
    {
        int k = directions.Length;
        int maxP = PartitionsFor(sampleRate, block);
        int full = Math.Min(w.Length, Math.Min(c1.Length, Math.Min(c2.Length, c3.Length)));
        int n = Math.Min(full, maxP * block);
        int s0 = (int)(LateTailIr.FadeInStartSeconds * sampleRate), s1 = (int)(LateTailIr.FadeInEndSeconds * sampleRate);
        int e0 = (int)(EndFadeStart * sampleRate), e1 = (int)(EndFadeEnd * sampleRate);
        var parts = new float[k][];
        for (int d = 0; d < k; d++) parts[d] = new float[n];
        // Running intensity over the last DoaWindow samples, centred.
        int half = DoaWindow / 2;
        double iy = 0, iz = 0, ix = 0;
        for (int i = 0; i < Math.Min(half, n); i++) { iy += w[i] * (double)c1[i]; iz += w[i] * (double)c2[i]; ix += w[i] * (double)c3[i]; }
        for (int i = 0; i < n; i++)
        {
            int add = i + half, drop = i - half - 1;
            if (add < n) { iy += w[add] * (double)c1[add]; iz += w[add] * (double)c2[add]; ix += w[add] * (double)c3[add]; }
            if (drop >= 0) { iy -= w[drop] * (double)c1[drop]; iz -= w[drop] * (double)c2[drop]; ix -= w[drop] * (double)c3[drop]; }
            if (i < s0 || i >= e1) continue;
            float win = i < s1 ? 0.5f - 0.5f * MathF.Cos(MathF.PI * (i - s0) / Math.Max(1, s1 - s0)) : 1f;
            if (i >= e0) win *= 0.5f + 0.5f * MathF.Cos(MathF.PI * (i - e0) / Math.Max(1, e1 - e0));
            var sa = (float)iy * a1 + (float)iz * a2 + (float)ix * a3;
            var dir = AmbiAxes.ToGame(sa);
            int best = 0; float bestDot = float.MinValue;
            if (dir.LengthSquared() > 1e-20f)
                for (int d = 0; d < k; d++) { float dot = System.Numerics.Vector3.Dot(dir, directions[d]); if (dot > bestDot) { bestDot = dot; best = d; } }
            else best = i % k;                      // no direction at all: spread, not piled on one
            parts[best][i] = w[i] * win;
        }
        return FromParts(parts, block, maxP);
    }

    /// <summary>Responses already windowed, one per direction, into partitions (past
    /// <paramref name="longBlock"/> samples, partitions of that; 0: one level); each direction's
    /// share of their energy. A direction with next to nothing is left out.</summary>
    public static SdmTailIr FromParts(float[][] parts, int block, int maxPartitions, int longBlock = LongBlock)
    {
        int k = parts.Length;
        var sdm = new SdmTailIr(k, block, maxPartitions);
        double total = 0;
        var energy = new double[k];
        for (int d = 0; d < k; d++)
        {
            double e = 0; var x = parts[d];
            for (int i = 0; i < x.Length; i++) e += x[i] * (double)x[i];
            energy[d] = e; total += e;
        }
        for (int d = 0; d < k; d++)
        {
            sdm.Share[d] = total > 0 ? (float)(energy[d] / total) : 0f;
            if (energy[d] > total * 1e-6) sdm.PerDirection[d] = LateTailIr.FromWindowed(parts[d], block, maxPartitions, longBlock);
        }
        return sdm;
    }
}

/// <summary>
/// Many responses against one input: the input is transformed once and its spectra shared, and each
/// response is a multiply-add and each pair of them one inverse transform. For the directional tail
/// (SdmTailIr), whose twenty responses all take the same send. Same handover rule as
/// LateTailConvolver: a new set is crossfaded in over one block. Allocation-free after construction.
///
/// Two levels (Gardner 1995): each response's first long block of samples in blocks, worked every
/// block, and the rest in long blocks, worked once a long block and played through the next. The same
/// convolution as one level to float rounding, with the same delay; at 256 and 1,024 a quarter of the
/// multiply-adds, and of the response read from memory, which is what the one level was waiting on.
/// </summary>
internal sealed class SharedInputConvolver
{
    private readonly int _block, _bins, _maxPartitions, _k, _ring;
    private readonly int _long, _longBins, _maxLong;
    private readonly Fft _fft, _longFft;
    private readonly float[] _prev, _fdlRe, _fdlIm, _re, _im, _aRe, _aIm, _bRe, _bIm, _tmpA, _tmpB;
    private int _head;
    // The long level: the previous long block and the one filling, its input spectra, and per
    // direction its answer for the long block now playing (and a new set's, while one takes over).
    private readonly float[] _frame, _lfdlRe, _lfdlIm, _lre, _lim;
    private int _fill, _lhead;
    private float[][] _tail, _tailNew;
    private SdmTailIr? _cur;
    private volatile SdmTailIr? _next;

    /// <param name="maxPartitions">The longest response, in blocks.</param>
    /// <param name="longBlock">The long partitions (SdmTailIr.LongBlock); 0, one level.</param>
    public SharedInputConvolver(int block, int maxPartitions, int k, int longBlock = SdmTailIr.LongBlock)
    {
        _block = block; _bins = block + 1; _maxPartitions = maxPartitions; _k = k;
        bool two = longBlock > block && longBlock % block == 0 && maxPartitions * block > longBlock;
        _long = two ? longBlock : 0;
        _ring = two ? longBlock / block : maxPartitions;
        _fft = new Fft(2 * block);
        _prev = new float[block];
        _fdlRe = new float[_ring * _bins]; _fdlIm = new float[_ring * _bins];
        _re = new float[2 * block]; _im = new float[2 * block];
        _tmpA = new float[block]; _tmpB = new float[block];
        int widest = _bins;
        if (two)
        {
            _longBins = _long + 1;
            _maxLong = (maxPartitions * block - _long + _long - 1) / _long;
            _longFft = new Fft(2 * _long);
            _frame = new float[2 * _long];
            _lfdlRe = new float[_maxLong * _longBins]; _lfdlIm = new float[_maxLong * _longBins];
            _lre = new float[2 * _long]; _lim = new float[2 * _long];
            _tail = new float[k][]; _tailNew = new float[k][];
            for (int d = 0; d < k; d++) { _tail[d] = new float[_long]; _tailNew[d] = new float[_long]; }
            widest = _longBins;
        }
        else
        {
            _longFft = _fft;
            _frame = _lfdlRe = _lfdlIm = _lre = _lim = Array.Empty<float>();
            _tail = _tailNew = Array.Empty<float[]>();
        }
        _aRe = new float[widest]; _aIm = new float[widest]; _bRe = new float[widest]; _bIm = new float[widest];
    }

    public void Set(SdmTailIr? ir)
    {
        if (ir != null && (ir.Block != _block || ir.MaxPartitions > _maxPartitions || ir.PerDirection.Length != _k)) return;
        if (ir != null)
            foreach (var d in ir.PerDirection)
                if (d != null && (d.Partitions > _ring || (d.LongPartitions > 0 && (d.LongBlock != _long || d.LongPartitions > _maxLong)))) return;
        _next = ir;
    }

    /// <summary>One block of input into <paramref name="outputs"/>[d] for every direction d.</summary>
    public void Process(ReadOnlySpan<float> input, float[][] outputs)
    {
        for (int k = 0; k < _block; k++) { _re[k] = _prev[k]; _re[_block + k] = input[k]; _im[k] = 0f; _im[_block + k] = 0f; }
        input.Slice(0, _block).CopyTo(_prev);
        _fft.Forward(_re, _im);
        _head = (_head + 1) % _ring;
        Array.Copy(_re, 0, _fdlRe, _head * _bins, _bins);
        Array.Copy(_im, 0, _fdlIm, _head * _bins, _bins);

        var cur = _cur; var next = _next;
        bool swap = !ReferenceEquals(cur, next);
        int at = _fill;                              // where this block falls in the long block
        // The new set's long-level answer for the long block now playing: its input is all in.
        if (swap && _long > 0) LongAnswer(next, _tailNew);
        for (int da = 0; da < _k; da += 2)
        {
            int db = da + 1 < _k ? da + 1 : -1;
            var oa = outputs[da]; var ob = db >= 0 ? outputs[db] : _tmpB;
            Answer(cur, da, db, oa, ob, _tail, at);
            if (!swap) continue;
            Answer(next, da, db, _tmpA, _tmpB, _tailNew, at);
            for (int i = 0; i < _block; i++)
            {
                float up = 0.5f - 0.5f * MathF.Cos(MathF.PI * (i + 0.5f) / _block);
                oa[i] = oa[i] * (1f - up) + _tmpA[i] * up;
                if (db >= 0) ob[i] = ob[i] * (1f - up) + _tmpB[i] * up;
            }
        }
        if (swap)
        {
            _cur = next;
            (_tail, _tailNew) = (_tailNew, _tail);
        }
        if (_long == 0) return;

        input.Slice(0, _block).CopyTo(_frame.AsSpan(_long + _fill, _block));
        _fill += _block;
        if (_fill < _long) return;
        _fill = 0;
        Array.Copy(_frame, _lre, 2 * _long);
        Array.Clear(_lim);
        _longFft.Forward(_lre, _lim);
        _lhead = (_lhead + 1) % _maxLong;
        Array.Copy(_lre, 0, _lfdlRe, _lhead * _longBins, _longBins);
        Array.Copy(_lim, 0, _lfdlIm, _lhead * _longBins, _longBins);
        Array.Copy(_frame, _long, _frame, 0, _long);
        // The long level's answer to the input so far, played through the next long block.
        LongAnswer(_cur, _tail);
    }

    /// <summary>Directions <paramref name="da"/> and <paramref name="db"/> (none: -1) of
    /// <paramref name="set"/>, this block: the first level's answer and the long level's share.</summary>
    private void Answer(SdmTailIr? set, int da, int db, float[] ya, float[] yb, float[][] tail, int at)
    {
        var a = set?.PerDirection[da]; var b = db >= 0 ? set?.PerDirection[db] : null;
        if (a == null && b == null) { Array.Clear(ya, 0, _block); Array.Clear(yb, 0, _block); }
        else
        {
            Mac(_fdlRe, _fdlIm, _head, _ring, _bins, a?.Re, a?.Im, a?.Partitions ?? 0, _aRe, _aIm);
            Mac(_fdlRe, _fdlIm, _head, _ring, _bins, b?.Re, b?.Im, b?.Partitions ?? 0, _bRe, _bIm);
            InversePair(_fft, _block, _re, _im);
            for (int i = 0; i < _block; i++) { ya[i] = _re[_block + i]; yb[i] = _im[_block + i]; }
        }
        if (_long == 0) return;
        var ta = tail[da];
        for (int i = 0; i < _block; i++) ya[i] += ta[at + i];
        if (db < 0) return;
        var tb = tail[db];
        for (int i = 0; i < _block; i++) yb[i] += tb[at + i];
    }

    /// <summary>The long level's answer for the next long block, every direction of
    /// <paramref name="set"/>, into <paramref name="dest"/>.</summary>
    private void LongAnswer(SdmTailIr? set, float[][] dest)
    {
        for (int da = 0; da < _k; da += 2)
        {
            int db = da + 1 < _k ? da + 1 : -1;
            var a = set?.PerDirection[da]; var b = db >= 0 ? set?.PerDirection[db] : null;
            int pa = a?.LongPartitions ?? 0, pb = b?.LongPartitions ?? 0;
            if (pa == 0 && pb == 0)
            {
                Array.Clear(dest[da]);
                if (db >= 0) Array.Clear(dest[db]);
                continue;
            }
            Mac(_lfdlRe, _lfdlIm, _lhead, _maxLong, _longBins, a?.LongRe, a?.LongIm, pa, _aRe, _aIm);
            Mac(_lfdlRe, _lfdlIm, _lhead, _maxLong, _longBins, b?.LongRe, b?.LongIm, pb, _bRe, _bIm);
            InversePair(_longFft, _long, _lre, _lim);
            Array.Copy(_lre, _long, dest[da], 0, _long);
            if (db >= 0) Array.Copy(_lim, _long, dest[db], 0, _long);
        }
    }

    /// <summary>Two real answers in one inverse transform, C = A + iB, from _a and _b: A's into
    /// <paramref name="re"/>, B's into <paramref name="im"/>; the second half of each is the block's.</summary>
    private void InversePair(Fft fft, int block, float[] re, float[] im)
    {
        int n = 2 * block, bins = block + 1;
        for (int k = 0; k < bins; k++) { re[k] = _aRe[k] - _bIm[k]; im[k] = _aIm[k] + _bRe[k]; }
        for (int k = 1; k < block; k++) { re[n - k] = _aRe[k] + _bIm[k]; im[n - k] = -_aIm[k] + _bRe[k]; }
        fft.Inverse(re, im);
    }

    /// <summary>Sum over <paramref name="partitions"/> of the delayed input spectra (a ring of
    /// <paramref name="ring"/>, newest at <paramref name="newest"/>) times a response's.</summary>
    private static void Mac(float[] xRe, float[] xIm, int newest, int ring, int bins,
                            float[]? hRe, float[]? hIm, int partitions, float[] accRe, float[] accIm)
    {
        Array.Clear(accRe, 0, bins); Array.Clear(accIm, 0, bins);
        if (hRe == null || hIm == null) return;
        int vec = Vector<float>.Count;
        for (int p = 0; p < partitions; p++)
        {
            int slot = newest - p; if (slot < 0) slot += ring;
            int xo = slot * bins, ho = p * bins, k = 0;
            for (; k + vec <= bins; k += vec)
            {
                var xr = new Vector<float>(xRe, xo + k); var xi = new Vector<float>(xIm, xo + k);
                var hr = new Vector<float>(hRe, ho + k); var hi = new Vector<float>(hIm, ho + k);
                (new Vector<float>(accRe, k) + xr * hr - xi * hi).CopyTo(accRe, k);
                (new Vector<float>(accIm, k) + xr * hi + xi * hr).CopyTo(accIm, k);
            }
            for (; k < bins; k++)
            {
                float xr = xRe[xo + k], xi = xIm[xo + k], hr = hRe[ho + k], hi = hIm[ho + k];
                accRe[k] += xr * hr - xi * hi; accIm[k] += xr * hi + xi * hr;
            }
        }
    }

    /// <summary>Forgets the input it holds. Mixer thread; allocation-free.</summary>
    public void Reset()
    {
        Array.Clear(_prev); Array.Clear(_fdlRe); Array.Clear(_fdlIm);
        Array.Clear(_frame); Array.Clear(_lfdlRe); Array.Clear(_lfdlIm);
        foreach (var t in _tail) Array.Clear(t);
        foreach (var t in _tailNew) Array.Clear(t);
    }
}

using System;
using System.Numerics;

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// The late tail of the room you stand in as a field: from every one of the tail's directions
/// (DiffuseTail.Direction) its own independent noise under the room's averaged energy (SmoothTail),
/// each through its own head response. Independent, as the directional part's twenty directions
/// already are (SmoothTail.DirectionalWindowed).
///
/// Why. The late part used to be ONE noise texture (the omnidirectional channel), spread over the
/// twenty directions by twenty velvet filters, and each ear's sum then through a velvet filter of
/// its own. Every one of those is a random spectrum, and the ear heard their product: measured
/// with --tail-steady, 400-900 ms at the ear, 10-11 % of bins 10 dB over their local median and a
/// spectral flatness of 0.18, where noise has 0.1 % and 0.56. That is the "metallic ringing tail"
/// (Cody, 2026-10-02). The directional part, independent noise per direction through the same
/// head responses, measured 0.1-0.2 %.
///
/// Cost, and how it is paid. Twenty full-length convolutions (to 2 s) in the traced reverb's
/// 256-sample blocks would be twenty times the late convolution that was there. But the late part
/// starts 250 ms after the sound (SdmTailIr.EndFadeStart), and a response that starts late can be
/// convolved in long blocks: with blocks of <see cref="Block"/> samples, a block of input's answer
/// is not due until two blocks later, so its work is spread over the next block's 16 mixer pieces
/// and the outputs wait in a ring until they are due. Twenty directions in 4,096-sample blocks cost
/// about what one direction in 256-sample blocks did.
///
/// And the response is not rebuilt each trace. Within a block of the response each direction's
/// noise is fixed, made once (<see cref="DiffuseLateNoise"/>, two spectra per block: the noise faded
/// out across the block and faded in), and each trace only gives the envelope: per block of the
/// response and per frequency bin, its amplitude at the block's start and at its end
/// (<see cref="DiffuseLateIr"/>, built by SmoothTail.BuildDiffuseLate). The convolver multiplies them in
/// as it goes.
/// </summary>
internal sealed class DiffuseLateNoise
{
    /// <summary>The late convolution's block, samples: at most half the late part's start (250 ms,
    /// 11,025 samples), so each block's answer is due two blocks after its input.</summary>
    public const int Block = 4096;
    public readonly int SampleRate, Start, Partitions, Directions, Bins;
    /// <summary>Per direction d and block p of the response, the noise faded out across the block
    /// (N0) and faded in (N1), each zero-padded to 2 * Block and transformed: [(d * Partitions + p) * Bins + k].</summary>
    public readonly float[] N0Re, N0Im, N1Re, N1Im;
    /// <summary>Each of SmoothTail's bands as power per bin, normalised so a unit-variance noise in
    /// that band has this spectrum: [b * Bins + k].</summary>
    public readonly float[] BandShape;

    /// <summary>The first sample of the late part in the response (SdmTailIr.EndFadeStart).</summary>
    public static int StartFor(int sampleRate) => (int)(SdmTailIr.EndFadeStart * sampleRate);
    /// <summary>Blocks of the response from its start to <paramref name="irLength"/>.</summary>
    public static int PartitionsFor(int sampleRate, int irLength) => Math.Max(1, (irLength - StartFor(sampleRate) + Block - 1) / Block);

    public DiffuseLateNoise(int sampleRate, int irLength, int directions, int seed = 5150)
    {
        SampleRate = sampleRate; Directions = directions; Bins = Block + 1;
        Start = StartFor(sampleRate);
        if (Start < 2 * Block) throw new ArgumentException("The late part starts too early for the late field's block.");
        Partitions = PartitionsFor(sampleRate, irLength);
        int count = directions * Partitions * Bins, n = 2 * Block;
        N0Re = new float[count]; N0Im = new float[count]; N1Re = new float[count]; N1Im = new float[count];
        var rng = new Random(seed);
        var fft = new Fft(n);
        var re = new float[n]; var im = new float[n];
        for (int d = 0; d < directions; d++)
            for (int p = 0; p < Partitions; p++)
            {
                // Two real signals in one transform: the faded-out noise as the real part, the
                // faded-in as the imaginary, separated after by their symmetry.
                Array.Clear(re); Array.Clear(im);
                for (int t = 0; t < Block; t++)
                {
                    double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
                    float w = (float)(Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2));
                    float u = (t + 0.5f) / Block;
                    re[t] = w * (1f - u); im[t] = w * u;
                }
                fft.Forward(re, im);
                int o = (d * Partitions + p) * Bins;
                for (int k = 0; k < Bins; k++)
                {
                    int m = (n - k) % n;
                    // X = (Z[k] + conj Z[n-k]) / 2, Y = (Z[k] - conj Z[n-k]) / 2i
                    N0Re[o + k] = 0.5f * (re[k] + re[m]); N0Im[o + k] = 0.5f * (im[k] - im[m]);
                    N1Re[o + k] = 0.5f * (im[k] + im[m]); N1Im[o + k] = -0.5f * (re[k] - re[m]);
                }
            }

        // The bands' power per bin through the same Butterworth tree SmoothTail measures with.
        int nb = SmoothTail.Bands;
        BandShape = new float[nb * Bins];
        for (int b = 0; b < nb; b++)
        {
            double mean = 0;
            var pw = new double[Bins];
            for (int k = 0; k < Bins; k++)
            {
                pw[k] = SmoothTail.BandPower(b, (double)k * sampleRate / n, sampleRate);
                mean += (k == 0 || k == Block ? 1 : 2) * pw[k];
            }
            mean /= n;
            for (int k = 0; k < Bins; k++) BandShape[b * Bins + k] = mean > 0 ? (float)(pw[k] / mean) : 0f;
        }
    }

    private static readonly object Gate = new();
    private static DiffuseLateNoise? _shared;

    /// <summary>The one set of noise, made on first use (about a second; the tracer thread asks).</summary>
    public static DiffuseLateNoise Shared(int sampleRate, int irLength, int directions)
    {
        lock (Gate)
        {
            var s = _shared;
            if (s == null || s.SampleRate != sampleRate || s.Directions != directions || s.Partitions != PartitionsFor(sampleRate, irLength))
                _shared = s = new DiffuseLateNoise(sampleRate, irLength, directions);
            return s;
        }
    }
}

/// <summary>
/// One trace's late field, ready for <see cref="DiffuseLateConvolver"/>: per block of the response and
/// per frequency bin, the amplitude at the block's start (A0) and end (A1). The same for every
/// direction; how much of the field comes from each is the renderer's gain (DiffuseTail's late shares).
/// </summary>
internal sealed class DiffuseLateIr
{
    public readonly DiffuseLateNoise Noise;
    /// <summary>Blocks in use: the response is cut where what remains is 70 dB under the whole.</summary>
    public readonly int Partitions;
    /// <summary>[p * Bins + k].</summary>
    public readonly float[] A0, A1;
    /// <summary>Per band b and block p, the amplitude per sample at the block's start and end:
    /// [b * Partitions + p]. A unit-variance noise in that band times these is what is played.</summary>
    public readonly float[] C0, C1;
    /// <summary>Each direction's energy (every direction carries the same).</summary>
    public readonly double Energy;

    public int Start => Noise.Start;
    public int Bins => Noise.Bins;

    public DiffuseLateIr(DiffuseLateNoise noise, int partitions, float[] c0, float[] c1, double energy)
    {
        Noise = noise; Partitions = Math.Clamp(partitions, 1, noise.Partitions); Energy = energy;
        C0 = c0; C1 = c1;
        int bins = noise.Bins, nb = SmoothTail.Bands;
        A0 = new float[Partitions * bins]; A1 = new float[Partitions * bins];
        var shape = noise.BandShape;
        for (int p = 0; p < Partitions; p++)
            for (int k = 0; k < bins; k++)
            {
                double s0 = 0, s1 = 0;
                for (int b = 0; b < nb; b++)
                {
                    double sh = shape[b * bins + k];
                    double x0 = c0[b * Partitions + p], x1 = c1[b * Partitions + p];
                    s0 += x0 * x0 * sh; s1 += x1 * x1 * sh;
                }
                A0[p * bins + k] = (float)Math.Sqrt(s0); A1[p * bins + k] = (float)Math.Sqrt(s1);
            }
    }

    /// <summary>Direction <paramref name="dir"/>'s response in time, from the response's time zero:
    /// for the lab and the tests. Each block's spill past its end is laid after it, as the
    /// convolution plays it to within the spill's few milliseconds.</summary>
    public float[] ToTime(int dir)
    {
        int B = DiffuseLateNoise.Block, n = 2 * B, bins = Bins;
        var y = new float[Start + (Partitions + 1) * B];
        var fft = new Fft(n);
        var re = new float[n]; var im = new float[n];
        var nz = Noise;
        for (int p = 0; p < Partitions; p++)
        {
            int o = (dir * nz.Partitions + p) * bins, a = p * bins;
            for (int k = 0; k < bins; k++)
            {
                re[k] = A0[a + k] * nz.N0Re[o + k] + A1[a + k] * nz.N1Re[o + k];
                im[k] = A0[a + k] * nz.N0Im[o + k] + A1[a + k] * nz.N1Im[o + k];
            }
            for (int k = 1; k < B; k++) { re[n - k] = re[k]; im[n - k] = -im[k]; }
            fft.Inverse(re, im);
            int at = Start + p * B;
            // [0, 1.5 B) after the block's start; the last half block is before it (the spill of a
            // zero-phase shaping, wrapped).
            for (int t = 0; t < n; t++)
            {
                int i = t < n * 3 / 4 ? at + t : at + t - n;
                if (i >= 0 && i < y.Length) y[i] += re[t];
            }
        }
        return y;
    }
}

/// <summary>
/// Convolves one channel through a <see cref="DiffuseLateIr"/> into one signal per direction, in
/// blocks of <see cref="DiffuseLateNoise.Block"/> fed and read in the mixer's pieces. A block of input
/// is transformed when it is complete; its answer, due two blocks later, is worked out a pair of
/// directions at a time over the next block's pieces and parked in a ring per direction. A new trace
/// takes over across one block: that block is worked out through both and crossfaded (the first
/// starts as it is, there being nothing to fade from). Mixer thread;
/// nothing allocates after construction and nothing throws.
/// </summary>
internal sealed class DiffuseLateConvolver
{
    private readonly int _sub, _block, _bins, _steps, _maxP, _dirs, _pairs, _start, _ring;
    private readonly Fft _fft;
    private readonly float[] _frame;                 // the previous block, then the one filling
    private int _fill;
    private readonly float[] _fdlRe, _fdlIm;         // input spectra, a ring of _maxP blocks
    private int _head;
    private readonly float[][] _out;                 // per direction, a ring of _ring samples
    private long _now;                               // samples taken in so far
    private long _period = -1;                       // the block whose answer is being worked out
    private int _step, _units, _done;
    private bool _fading;
    private DiffuseLateIr? _cur, _latched;
    private volatile DiffuseLateIr? _next;
    private readonly float[] _re, _im, _aRe, _aIm, _bRe, _bIm;

    /// <summary>What the last block's work cost, for the lab: ticks per mixer piece, the largest.</summary>
    public long WorstTicks;

    public DiffuseLateConvolver(int sub, int maxPartitions, int directions, int start)
    {
        _sub = sub; _block = DiffuseLateNoise.Block; _bins = _block + 1;
        if (_block % sub != 0) throw new ArgumentException("The mixer's piece must divide the late block.", nameof(sub));
        if (start < 2 * _block) throw new ArgumentException("The late part starts too early.", nameof(start));
        _steps = _block / sub; _maxP = Math.Max(1, maxPartitions); _dirs = directions; _pairs = (directions + 1) / 2;
        _start = start; _ring = start + 2 * _block;
        _fft = new Fft(2 * _block);
        _frame = new float[2 * _block];
        _fdlRe = new float[_maxP * _bins]; _fdlIm = new float[_maxP * _bins];
        _out = new float[directions][];
        for (int d = 0; d < directions; d++) _out[d] = new float[_ring];
        _re = new float[2 * _block]; _im = new float[2 * _block];
        _aRe = new float[_bins]; _aIm = new float[_bins]; _bRe = new float[_bins]; _bIm = new float[_bins];
        _head = _maxP - 1;
    }

    /// <summary>The field to play from the next block on; null fades it out. Any thread.</summary>
    public void Set(DiffuseLateIr? ir)
    {
        if (ir != null && (ir.Start != _start || ir.Partitions > _maxP || ir.Noise.Directions < _dirs)) return;
        _next = ir;
    }

    public bool HasIr => _cur != null || _next != null;

    /// <summary>One piece of <paramref name="input"/> in; one piece per direction out, into
    /// <paramref name="outputs"/>[d].</summary>
    public void Process(ReadOnlySpan<float> input, float[][] outputs)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        // What is due now: worked out at least a block ago.
        int at = (int)(_now % _ring);
        for (int d = 0; d < _dirs; d++)
        {
            var o = outputs[d]; var r = _out[d];
            for (int i = 0, j = at; i < _sub; i++, j = j + 1 == _ring ? 0 : j + 1) { o[i] = r[j]; r[j] = 0f; }
        }
        input.Slice(0, _sub).CopyTo(_frame.AsSpan(_block + _fill, _sub));
        _fill += _sub;
        _now += _sub;

        // This piece's share of the last block's answer.
        if (_period >= 0)
        {
            int target = (int)((long)(_step + 1) * _units / _steps);
            while (_done < target) Unit(_done++);
            _step++;
        }
        if (_fill == _block)
        {
            if (_period >= 0)
            {
                while (_done < _units) Unit(_done++);
                if (_fading) _cur = _latched;
            }
            Begin();
            _fill = 0;
        }
        long dt = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        if (dt > WorstTicks) WorstTicks = dt;
    }

    /// <summary>A block of input is complete: transform it, and plan its answer.</summary>
    private void Begin()
    {
        Array.Copy(_frame, _re, 2 * _block);
        Array.Clear(_im);
        _fft.Forward(_re, _im);
        _head = (_head + 1) % _maxP;
        Array.Copy(_re, 0, _fdlRe, _head * _bins, _bins);
        Array.Copy(_im, 0, _fdlIm, _head * _bins, _bins);
        Array.Copy(_frame, _block, _frame, 0, _block);
        _period = _now / _block - 1;
        _step = 0; _done = 0;
        _latched = _next;
        // The first field starts as it is: nothing was playing to fade from.
        _fading = !ReferenceEquals(_latched, _cur) && _cur != null;
        if (!_fading) _cur = _latched;
        _units = _cur == null && _latched == null ? 0 : _pairs * (_fading ? 2 : 1);
    }

    /// <summary>One pair of directions' answer to the block, through the current field or (while
    /// one takes over from another) through the old or the new.</summary>
    private void Unit(int u)
    {
        int pair = _fading ? u / 2 : u;
        bool newer = !_fading || u % 2 == 1;
        var ir = _fading ? (newer ? _latched : _cur) : _cur;
        int da = 2 * pair, db = da + 1;
        bool hasB = db < _dirs;
        long t0 = _period * _block + _start;
        int n = 2 * _block;
        if (ir == null) { Array.Clear(_re); Array.Clear(_im); }
        else
        {
            Spectrum(ir, da, hasB ? db : -1);
            // Two real answers in one inverse transform: C = A + iB.
            for (int k = 0; k < _bins; k++) { _re[k] = _aRe[k] - _bIm[k]; _im[k] = _aIm[k] + _bRe[k]; }
            for (int k = 1; k < _block; k++) { _re[n - k] = _aRe[k] + _bIm[k]; _im[n - k] = -_aIm[k] + _bRe[k]; }
            _fft.Inverse(_re, _im);
        }
        // Overlap-save: the second half is the answer to the block.
        Write(_out[da], _re, t0, _fading, newer);
        if (hasB) Write(_out[db], _im, t0, _fading, newer);
    }

    private void Write(float[] ring, float[] y, long t0, bool fading, bool newer)
    {
        int j = (int)(t0 % _ring);
        for (int i = 0; i < _block; i++, j = j + 1 == _ring ? 0 : j + 1)
        {
            float v = y[_block + i];
            if (!fading || !newer) ring[j] = v;
            else
            {
                float up = 0.5f - 0.5f * MathF.Cos(MathF.PI * (i + 0.5f) / _block);
                ring[j] = ring[j] * (1f - up) + v * up;
            }
        }
    }

    /// <summary>Sum over the response's blocks of the delayed input spectra times directions
    /// <paramref name="da"/>'s and <paramref name="db"/>'s noise (none: -1) under the trace's
    /// envelope, into _a and _b. The two in one pass: the input and the envelope are read once.</summary>
    private void Spectrum(DiffuseLateIr ir, int da, int db)
    {
        Array.Clear(_aRe); Array.Clear(_aIm); Array.Clear(_bRe); Array.Clear(_bIm);
        var nz = ir.Noise;
        int bins = _bins, vec = Vector<float>.Count;
        bool two = db >= 0;
        for (int p = 0; p < ir.Partitions; p++)
        {
            int slot = _head - p; if (slot < 0) slot += _maxP;
            int xo = slot * bins, na = (da * nz.Partitions + p) * bins, nb = ((two ? db : da) * nz.Partitions + p) * bins, ao = p * bins, k = 0;
            for (; k + vec <= bins; k += vec)
            {
                var a0 = new Vector<float>(ir.A0, ao + k); var a1 = new Vector<float>(ir.A1, ao + k);
                var xr = new Vector<float>(_fdlRe, xo + k); var xi = new Vector<float>(_fdlIm, xo + k);
                // The envelope times the input once; then each direction's two noises.
                var ur0 = a0 * xr; var ui0 = a0 * xi; var ur1 = a1 * xr; var ui1 = a1 * xi;
                var hr0 = new Vector<float>(nz.N0Re, na + k); var hi0 = new Vector<float>(nz.N0Im, na + k);
                var hr1 = new Vector<float>(nz.N1Re, na + k); var hi1 = new Vector<float>(nz.N1Im, na + k);
                (new Vector<float>(_aRe, k) + ur0 * hr0 - ui0 * hi0 + ur1 * hr1 - ui1 * hi1).CopyTo(_aRe, k);
                (new Vector<float>(_aIm, k) + ur0 * hi0 + ui0 * hr0 + ur1 * hi1 + ui1 * hr1).CopyTo(_aIm, k);
                if (!two) continue;
                hr0 = new Vector<float>(nz.N0Re, nb + k); hi0 = new Vector<float>(nz.N0Im, nb + k);
                hr1 = new Vector<float>(nz.N1Re, nb + k); hi1 = new Vector<float>(nz.N1Im, nb + k);
                (new Vector<float>(_bRe, k) + ur0 * hr0 - ui0 * hi0 + ur1 * hr1 - ui1 * hi1).CopyTo(_bRe, k);
                (new Vector<float>(_bIm, k) + ur0 * hi0 + ui0 * hr0 + ur1 * hi1 + ui1 * hr1).CopyTo(_bIm, k);
            }
            for (; k < bins; k++)
            {
                float a0 = ir.A0[ao + k], a1 = ir.A1[ao + k], xr = _fdlRe[xo + k], xi = _fdlIm[xo + k];
                float ur0 = a0 * xr, ui0 = a0 * xi, ur1 = a1 * xr, ui1 = a1 * xi;
                _aRe[k] += ur0 * nz.N0Re[na + k] - ui0 * nz.N0Im[na + k] + ur1 * nz.N1Re[na + k] - ui1 * nz.N1Im[na + k];
                _aIm[k] += ur0 * nz.N0Im[na + k] + ui0 * nz.N0Re[na + k] + ur1 * nz.N1Im[na + k] + ui1 * nz.N1Re[na + k];
                if (!two) continue;
                _bRe[k] += ur0 * nz.N0Re[nb + k] - ui0 * nz.N0Im[nb + k] + ur1 * nz.N1Re[nb + k] - ui1 * nz.N1Im[nb + k];
                _bIm[k] += ur0 * nz.N0Im[nb + k] + ui0 * nz.N0Re[nb + k] + ur1 * nz.N1Im[nb + k] + ui1 * nz.N1Re[nb + k];
            }
        }
    }

    public void Reset()
    {
        Array.Clear(_frame); Array.Clear(_fdlRe); Array.Clear(_fdlIm);
        foreach (var r in _out) Array.Clear(r);
        _fill = 0; _period = -1;
    }
}

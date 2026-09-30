using System;
using System.Numerics;

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// The late part of a traced impulse response, ready to convolve: the trace's own omnidirectional
/// channel from where the placed early reflections end, faded in, in the frequency domain.
///
/// Why it exists. The tail of the room you stand in used to be Steam Audio's PARAMETRIC reverb, a
/// feedback delay network that takes three decay times from the trace and nothing else — not its
/// level, not its envelope. Measured against traces from where the sources really are, it was
/// 14-20 dB too loud in the tunnel, 8-11 in a street, 0-3 in flat 01F, silent until 60 ms and then a
/// step onto a plateau to 150 ms, decaying slower than the room ("an echo over top of the room", "a
/// mask over where the reflections are coming from", 2026-09-29). The trace itself is none of that:
/// it is dense, its energy falls from the first reflection on, and its level is the room's. The SDK
/// keeps the traced IR opaque, so the tracer pushes an impulse through a private convolution to read
/// it back (TracedReverb.ExtractLate), and this plays it.
/// </summary>
internal sealed class LateTailIr
{
    /// <summary>Where the placed early reflections end and the tail begins (WorldAudioPlayer's
    /// window is 80 ms): a raised-cosine fade-in across this span, so the two meet with no step.</summary>
    public const float FadeInStartSeconds = 0.05f, FadeInEndSeconds = 0.10f;

    public readonly int Block, Bins, Partitions;
    /// <summary>Partition p, bin k at [p * Bins + k].</summary>
    public readonly float[] Re, Im;
    /// <summary>Energy of the windowed late part, for diagnostics.</summary>
    public readonly double Energy;

    private LateTailIr(int block, int partitions, double energy)
    {
        Block = block; Bins = block + 1; Partitions = partitions; Energy = energy;
        Re = new float[partitions * Bins]; Im = new float[partitions * Bins];
    }

    /// <summary>
    /// The late part of <paramref name="w"/> (the IR's omnidirectional channel, time zero at the
    /// direct sound), windowed and cut where what remains is 70 dB under what was there, into
    /// partitions of <paramref name="block"/> samples. Worker thread: it allocates.
    /// </summary>
    public static LateTailIr Build(float[] w, int sampleRate, int block, int maxPartitions)
    {
        int n = w.Length;
        int a = (int)(FadeInStartSeconds * sampleRate), b = (int)(FadeInEndSeconds * sampleRate);
        var x = new float[n];
        double total = 0;
        for (int i = a; i < n; i++)
        {
            float win = i >= b ? 1f : 0.5f - 0.5f * MathF.Cos(MathF.PI * (i - a) / Math.Max(1, b - a));
            x[i] = w[i] * win;
            total += x[i] * (double)x[i];
        }
        // Where the rest is 70 dB down: no partition past it is worth its cost.
        int end = n;
        double rest = 0;
        for (int i = n - 1; i >= 0; i--)
        {
            rest += x[i] * (double)x[i];
            if (rest > total * 1e-7) { end = i + 1; break; }
        }
        int partitions = Math.Clamp((end + block - 1) / block, 1, maxPartitions);
        var ir = new LateTailIr(block, partitions, total);
        var fft = new Fft(2 * block);
        var re = new float[2 * block]; var im = new float[2 * block];
        for (int p = 0; p < partitions; p++)
        {
            Array.Clear(re); Array.Clear(im);
            for (int k = 0; k < block; k++) { int i = p * block + k; if (i < n) re[k] = x[i]; }
            fft.Forward(re, im);
            Array.Copy(re, 0, ir.Re, p * ir.Bins, ir.Bins);
            Array.Copy(im, 0, ir.Im, p * ir.Bins, ir.Bins);
        }
        return ir;
    }
}

/// <summary>
/// Uniformly partitioned overlap-save convolution of one channel through a <see cref="LateTailIr"/>,
/// one block at a time, on the mixer thread. Nothing here allocates after construction and nothing
/// throws. A new IR takes over across one block: that block is convolved through both and crossfaded,
/// so a new trace is never a click.
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

    public bool HasIr => _ir != null || _next != null;

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

/// <summary>An in-place radix-2 complex FFT of one fixed size. Allocation-free after construction.</summary>
internal sealed class Fft
{
    private readonly int _n;
    private readonly float[] _cos, _sin;
    private readonly int[] _rev;

    public Fft(int n)
    {
        if (n < 2 || (n & (n - 1)) != 0) throw new ArgumentException("FFT size must be a power of two.", nameof(n));
        _n = n;
        _cos = new float[n / 2]; _sin = new float[n / 2];
        for (int k = 0; k < n / 2; k++) { _cos[k] = MathF.Cos(2f * MathF.PI * k / n); _sin[k] = -MathF.Sin(2f * MathF.PI * k / n); }
        _rev = new int[n];
        int bits = 0; while ((1 << bits) < n) bits++;
        for (int i = 0; i < n; i++)
        {
            int r = 0; for (int b = 0; b < bits; b++) if ((i & (1 << b)) != 0) r |= 1 << (bits - 1 - b);
            _rev[i] = r;
        }
    }

    public void Forward(float[] re, float[] im) => Run(re, im, false);

    /// <summary>The inverse, scaled by 1/n.</summary>
    public void Inverse(float[] re, float[] im)
    {
        Run(re, im, true);
        float s = 1f / _n;
        for (int i = 0; i < _n; i++) { re[i] *= s; im[i] *= s; }
    }

    private void Run(float[] re, float[] im, bool inverse)
    {
        for (int i = 0; i < _n; i++)
        {
            int j = _rev[i];
            if (j > i) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int size = 2; size <= _n; size <<= 1)
        {
            int half = size >> 1, step = _n / size;
            for (int start = 0; start < _n; start += size)
            {
                for (int k = 0; k < half; k++)
                {
                    float wr = _cos[k * step], wi = inverse ? -_sin[k * step] : _sin[k * step];
                    int a = start + k, b = a + half;
                    float tr = re[b] * wr - im[b] * wi, ti = re[b] * wi + im[b] * wr;
                    re[b] = re[a] - tr; im[b] = im[a] - ti;
                    re[a] += tr; im[a] += ti;
                }
            }
        }
    }
}

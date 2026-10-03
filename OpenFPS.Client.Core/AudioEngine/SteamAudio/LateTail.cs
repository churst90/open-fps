using System;
using System.Numerics;

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// The late part of a traced impulse response, ready to convolve: the trace's own omnidirectional
/// channel from where the placed early reflections end, faded in, in the frequency domain.
///
/// Why it exists. Steam Audio's PARAMETRIC reverb is a feedback delay network that takes three decay
/// times from the trace and nothing else — not its level, not its envelope. Measured against traces
/// from where the sources really are, it is 14-20 dB too loud in the tunnel, 8-11 in a street, 0-3 in
/// flat 01F, silent until 60 ms and then a step onto a plateau to 150 ms, decaying slower than the
/// room: an echo over the room that masks where the reflections come from. The trace itself is none of that:
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

    /// <summary>An IR already windowed as it is to be played, cut 70 dB down, into partitions.</summary>
    public static LateTailIr FromWindowed(float[] x, int block, int maxPartitions)
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

/// <summary>
/// The directional part of the tail, by the Spatial Decomposition Method (Tervo et al. 2013): the
/// traced response split, sample by sample, by the direction its sound arrives from, snapped to the
/// nearest of the tail's directions (DiffuseTail.Direction). Each direction then has its own
/// response — only the samples that came from that way — and they sum back to the trace exactly.
///
/// Why. One channel spread over twenty directions by random filters is a field that is the same
/// whichever way you face, so turning your head tells you nothing and it sits in front of you as a
/// mass of reverb, which is where a generic head response puts anything without a direction. The
/// trace knows better for its first few hundred milliseconds: the second and third
/// bounces arrive off particular walls. From here they come from those walls, fixed in the room, and
/// move round the head as it turns. Past <see cref="EndFadeStart"/>..<see cref="EndFadeEnd"/> the trace
/// itself says the sound arrives from everywhere, and the diffuse rendering takes over; the two
/// windows are complementary, so the parts add back to the trace.
///
/// Direction per sample: the intensity W x (first-order channels) summed over <see cref="DoaWindow"/>
/// samples (about 0.7 ms), turned into the game's world through the measured channel axes
/// (AmbiAxes). Snapping to a fixed set of directions is the published refinement that keeps each
/// direction's response from being a comb of lone samples (Amengual Garí et al., BinauralSDM).
/// </summary>
internal sealed class SdmTailIr
{
    public const float EndFadeStart = 0.25f, EndFadeEnd = 0.35f;
    public const int DoaWindow = 32;

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

    /// <param name="w">The omnidirectional channel; <paramref name="c1"/>..<paramref name="c3"/> the
    /// first-order ones, whose world axes (Steam Audio's) are <paramref name="a1"/>..<paramref name="a3"/>.</param>
    /// <param name="directions">The directions to snap to, in the game's world.</param>
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

    /// <summary>Responses already windowed, one per direction, into partitions; each direction's
    /// share of their energy. A direction with next to nothing is left out.</summary>
    public static SdmTailIr FromParts(float[][] parts, int block, int maxPartitions)
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
            if (energy[d] > total * 1e-6) sdm.PerDirection[d] = LateTailIr.FromWindowed(parts[d], block, maxPartitions);
        }
        return sdm;
    }
}

/// <summary>
/// Many responses against one input: the input is transformed once and its spectra shared, and each
/// response is a multiply-add and one inverse transform. For the directional tail (SdmTailIr), whose
/// twenty responses all take the same send. Same handover rule as LateTailConvolver: a new set is
/// crossfaded in over one block. Allocation-free after construction.
/// </summary>
internal sealed class SharedInputConvolver
{
    private readonly int _block, _bins, _maxPartitions, _k;
    private readonly Fft _fft;
    private readonly float[] _prev, _fdlRe, _fdlIm, _re, _im, _accRe, _accIm, _tmp;
    private int _head;
    private SdmTailIr? _cur;
    private volatile SdmTailIr? _next;

    public SharedInputConvolver(int block, int maxPartitions, int k)
    {
        _block = block; _bins = block + 1; _maxPartitions = maxPartitions; _k = k;
        _fft = new Fft(2 * block);
        _prev = new float[block];
        _fdlRe = new float[maxPartitions * _bins]; _fdlIm = new float[maxPartitions * _bins];
        _re = new float[2 * block]; _im = new float[2 * block];
        _accRe = new float[_bins]; _accIm = new float[_bins];
        _tmp = new float[block];
    }

    public void Set(SdmTailIr? ir)
    {
        if (ir != null && (ir.Block != _block || ir.MaxPartitions > _maxPartitions || ir.PerDirection.Length != _k)) return;
        _next = ir;
    }

    /// <summary>One block of input into <paramref name="outputs"/>[d] for every direction d.</summary>
    public void Process(ReadOnlySpan<float> input, float[][] outputs)
    {
        for (int k = 0; k < _block; k++) { _re[k] = _prev[k]; _re[_block + k] = input[k]; _im[k] = 0f; _im[_block + k] = 0f; }
        input.Slice(0, _block).CopyTo(_prev);
        _fft.Forward(_re, _im);
        _head = (_head + 1) % _maxPartitions;
        Array.Copy(_re, 0, _fdlRe, _head * _bins, _bins);
        Array.Copy(_im, 0, _fdlIm, _head * _bins, _bins);

        var cur = _cur; var next = _next;
        bool swap = !ReferenceEquals(cur, next);
        for (int d = 0; d < _k; d++)
        {
            var o = outputs[d];
            var a = cur?.PerDirection[d];
            if (a != null) Convolve(a, o); else Array.Clear(o, 0, _block);
            if (swap)
            {
                var b = next?.PerDirection[d];
                if (b != null) Convolve(b, _tmp); else Array.Clear(_tmp);
                for (int i = 0; i < _block; i++)
                {
                    float up = 0.5f - 0.5f * MathF.Cos(MathF.PI * (i + 0.5f) / _block);
                    o[i] = o[i] * (1f - up) + _tmp[i] * up;
                }
            }
        }
        if (swap) _cur = next;
    }

    private void Convolve(LateTailIr ir, float[] y)
    {
        Array.Clear(_accRe); Array.Clear(_accIm);
        int bins = _bins, vec = System.Numerics.Vector<float>.Count;
        for (int p = 0; p < ir.Partitions; p++)
        {
            int slot = _head - p; if (slot < 0) slot += _maxPartitions;
            int xo = slot * bins, ho = p * bins, k = 0;
            for (; k + vec <= bins; k += vec)
            {
                var xr = new System.Numerics.Vector<float>(_fdlRe, xo + k); var xi = new System.Numerics.Vector<float>(_fdlIm, xo + k);
                var hr = new System.Numerics.Vector<float>(ir.Re, ho + k); var hi = new System.Numerics.Vector<float>(ir.Im, ho + k);
                (new System.Numerics.Vector<float>(_accRe, k) + xr * hr - xi * hi).CopyTo(_accRe, k);
                (new System.Numerics.Vector<float>(_accIm, k) + xr * hi + xi * hr).CopyTo(_accIm, k);
            }
            for (; k < bins; k++)
            {
                float xr = _fdlRe[xo + k], xi = _fdlIm[xo + k], hr = ir.Re[ho + k], hi = ir.Im[ho + k];
                _accRe[k] += xr * hr - xi * hi; _accIm[k] += xr * hi + xi * hr;
            }
        }
        int n = 2 * _block;
        for (int k = 0; k < bins; k++) { _re[k] = _accRe[k]; _im[k] = _accIm[k]; }
        for (int k = 1; k < _block; k++) { _re[n - k] = _accRe[k]; _im[n - k] = -_accIm[k]; }
        _fft.Inverse(_re, _im);
        for (int k = 0; k < _block; k++) y[k] = _re[_block + k];
    }
}

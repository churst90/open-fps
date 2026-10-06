using System;
using System.Numerics;
using System.Threading;

namespace OpenFPS.Common.Hearing;

/// <summary>
/// One-third-octave band powers of a signal, 25 Hz to 12.5 kHz: the input <see cref="ZwickerLoudness"/>
/// takes, measured from the sound itself.
///
/// An FFT, each bin weighted by the power response of an order-3 Butterworth one-third-octave band
/// filter, |H|^2 = 1 / (1 + (Q (f/fc - fc/f))^6) with Q = 1 / (2^(1/6) - 2^(-1/6)): the class of filter
/// IEC 61260 describes and ISO 532-1 assumes. So a pure tone leaks into the bands beside it as it does
/// through real band filters, rather than landing in one band alone (which reads a 1 kHz tone a phon
/// or two too quiet).
///
/// The bands up to 315 Hz are read from a copy low-passed at 600 Hz (sixth-order Butterworth) and
/// taken every 16th sample: 1024 points there are 3 Hz apart at 48 kHz, enough for the 25 Hz band,
/// for the cost of a 1024-point transform instead of a 16k one. The rest are read at the full rate
/// from 2048-point Hann segments, half overlapped. Both are scaled to mean-square pressure, so the
/// two halves of the spectrum are on one scale.
///
/// One analyser serves one thread: it keeps its work buffers. Nothing allocates after construction.
/// </summary>
public sealed class BandAnalyser
{
    public const int Bands = ZwickerLoudness.BandCount;
    /// <summary>Bands read from the decimated copy: 25 Hz to 315 Hz.</summary>
    private const int LowBands = 12;
    private const int Decimation = 16, LowFft = 1024, HighFft = 2048;

    /// <summary>The samples one analysis reads: the decimated transform's span.</summary>
    public const int WindowSamples = LowFft * Decimation;

    private readonly int _rate;
    private readonly Complex[] _low = new Complex[LowFft], _high = new Complex[HighFft];
    private readonly float[] _hannLow = Hann(LowFft), _hannHigh = Hann(HighFft);
    private readonly float[] _weightsLow, _weightsHigh;
    private readonly int[] _lowFrom, _lowTo, _highFrom, _highTo;
    private readonly (double b0, double b1, double b2, double a1, double a2)[] _lp;

    public BandAnalyser(int sampleRate)
    {
        _rate = sampleRate;
        // The band weights depend only on the rate: built once per rate and shared, read-only.
        var w = _byRate.GetOrAdd(sampleRate, static r =>
        {
            var low = Weights(r / (float)Decimation, LowFft, 0, LowBands);
            var high = Weights(r, HighFft, LowBands, Bands);
            return (low.W, low.From, low.To, high.W, high.From, high.To);
        });
        (_weightsLow, _lowFrom, _lowTo, _weightsHigh, _highFrom, _highTo) = w;
        _lp = new[] { Butter(sampleRate, 600.0, 0.5176), Butter(sampleRate, 600.0, 0.7071), Butter(sampleRate, 600.0, 1.9319) };
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, (float[], int[], int[], float[], int[], int[])> _byRate = new();

    public int SampleRate => _rate;

    private static float[] Hann(int n)
    {
        var w = new float[n];
        for (int i = 0; i < n; i++) w[i] = 0.5f - 0.5f * MathF.Cos(2f * MathF.PI * i / n);
        return w;
    }

    /// <summary>The power response of band b's filter at f.</summary>
    public static double BandWeight(int band, double f)
    {
        const double q = 4.318473; // 1 / (2^(1/6) - 2^(-1/6))
        double fc = ZwickerLoudness.CentresHz[band];
        if (f <= 0) return 0;
        double x = q * (f / fc - fc / f);
        double x2 = x * x;
        return 1.0 / (1.0 + x2 * x2 * x2);
    }

    /// <summary>Per band, the bins it reads (where its weight is over 1e-4) and their weights, packed.</summary>
    private static (float[] W, int[] From, int[] To) Weights(float rate, int n, int first, int last)
    {
        var from = new int[Bands];
        var to = new int[Bands];
        var list = new System.Collections.Generic.List<float>();
        for (int b = first; b < last; b++)
        {
            from[b] = list.Count;
            for (int k = 1; k < n / 2; k++)
            {
                double w = BandWeight(b, k * rate / (double)n);
                if (w > 1e-4) { list.Add((float)w); }
                else list.Add(0f);
            }
            to[b] = list.Count;
        }
        return (list.ToArray(), from, to);
    }

    /// <summary>A Butterworth low-pass section (one pole pair of Q <paramref name="q"/>), bilinear.</summary>
    private static (double, double, double, double, double) Butter(double rate, double fc, double q)
    {
        double w0 = 2 * Math.PI * fc / rate, c = Math.Cos(w0), alpha = Math.Sin(w0) / (2 * q);
        double a0 = 1 + alpha;
        return ((1 - c) / 2 / a0, (1 - c) / a0, (1 - c) / 2 / a0, -2 * c / a0, (1 - alpha) / a0);
    }

    /// <summary>
    /// Adds the band powers (mean-square, in the signal's units squared) of the last
    /// <see cref="WindowSamples"/> of <paramref name="x"/> into <paramref name="bandPower"/>, weighted
    /// by <paramref name="weight"/>. Shorter input is analysed as it is, zero-padded.
    /// </summary>
    public void Accumulate(ReadOnlySpan<float> x, Span<double> bandPower, double weight = 1.0)
    {
        if (x.Length > WindowSamples) x = x[^WindowSamples..];
        int n = x.Length;
        if (n == 0) return;

        // Scaling: a one-sided PSD estimate is 2|X_k|^2 / (fs * sum w^2), and a band's power is that
        // times the bin spacing fs / N times the band's weight; fs cancels. A frame shorter than its
        // transform is windowed over its own length and zero-padded, which changes the bin spacing
        // and nothing else.

        // ── The low bands: low-passed, decimated, one Hann transform ──
        int m = Math.Min(LowFft, n / Decimation);
        if (m >= 8)
        {
            double z0 = 0, z1 = 0, z2 = 0, z3 = 0, z4 = 0, z5 = 0;
            var s0 = _lp[0];
            var s1 = _lp[1];
            var s2 = _lp[2];
            int skip = n - m * Decimation;   // the decimated frame is the LAST m*16 samples, filtered from the start
            int k = 0;
            double sumW2 = 0;
            for (int i = 0; i < n; i++)
            {
                double v = x[i];
                double y0 = s0.b0 * v + z0;
                z0 = s0.b1 * v - s0.a1 * y0 + z1;
                z1 = s0.b2 * v - s0.a2 * y0;
                double ya = s1.b0 * y0 + z2;
                z2 = s1.b1 * y0 - s1.a1 * ya + z3;
                z3 = s1.b2 * y0 - s1.a2 * ya;
                double y1 = s2.b0 * ya + z4;
                z4 = s2.b1 * ya - s2.a1 * y1 + z5;
                z5 = s2.b2 * ya - s2.a2 * y1;
                if (i >= skip && ((i - skip) % Decimation) == Decimation - 1 && k < m)
                {
                    double w = m == LowFft ? _hannLow[k] : 0.5 - 0.5 * Math.Cos(2 * Math.PI * k / m);
                    _low[k] = new Complex(y1 * w, 0);
                    sumW2 += w * w;
                    k++;
                }
            }
            for (int i = k; i < LowFft; i++) _low[i] = Complex.Zero;
            Spectrum.Fft(_low);
            double scale = weight * 2.0 / (LowFft * Math.Max(sumW2, 1e-12));
            for (int b = 0; b < LowBands; b++) bandPower[b] += scale * Sum(_low, _weightsLow, _lowFrom[b], _lowTo[b]);
        }

        // ── The rest: Hann segments at the full rate, half overlapped ──
        Span<double> acc = stackalloc double[Bands];
        acc.Clear();
        int segs = 0;
        double hw2 = 0;
        if (n < HighFft)
        {
            for (int i = 0; i < HighFft; i++)
            {
                double w = i < n ? 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n) : 0;
                _high[i] = new Complex(i < n ? x[i] * w : 0, 0);
                hw2 += w * w;
            }
            Spectrum.Fft(_high);
            for (int b = LowBands; b < Bands; b++) acc[b] += Sum(_high, _weightsHigh, _highFrom[b], _highTo[b]);
            segs = 1;
        }
        else
        {
            for (int i = 0; i < HighFft; i++) hw2 += _hannHigh[i] * _hannHigh[i];
            for (int start = n - HighFft; start >= 0; start -= HighFft / 2)
            {
                for (int i = 0; i < HighFft; i++) _high[i] = new Complex(x[start + i] * _hannHigh[i], 0);
                Spectrum.Fft(_high);
                for (int b = LowBands; b < Bands; b++) acc[b] += Sum(_high, _weightsHigh, _highFrom[b], _highTo[b]);
                segs++;
            }
        }
        double hs = weight * 2.0 / (HighFft * Math.Max(hw2, 1e-12)) / Math.Max(1, segs);
        for (int b = LowBands; b < Bands; b++) bandPower[b] += hs * acc[b];
    }

    private static double Sum(Complex[] spec, float[] weights, int from, int to)
    {
        double s = 0;
        for (int k = 1, w = from; w < to; k++, w++)
        {
            float wt = weights[w];
            if (wt == 0f) continue;
            var c = spec[k];
            s += wt * (c.Real * c.Real + c.Imaginary * c.Imaginary);
        }
        return s;
    }

    /// <summary>
    /// The long-term band powers of a whole recording or rendered buffer (mean square while it
    /// sounds, by energy), up to <paramref name="maxSeconds"/> of it, read in windows across it.
    /// Allocates: for a sound's one-off measurement, not for a mixer.
    /// </summary>
    public static double[] Measure(ReadOnlySpan<float> pcm, int sampleRate, float maxSeconds = 10f)
    {
        var bands = new double[Bands];
        var a = new BandAnalyser(sampleRate);
        int max = (int)Math.Min(pcm.Length, maxSeconds * sampleRate);
        if (max <= 0) return bands;
        int win = WindowSamples;
        for (int start = 0; start < max; start += win)
        {
            int len = Math.Min(win, max - start);
            // Each window's mean square, weighted by its share of the time, so a short tail counts for
            // what it lasts.
            a.Accumulate(pcm.Slice(start, len), bands, len / (double)max);
        }
        return bands;
    }
}

/// <summary>
/// The running spectrum of a live voice: the producer (the voice's own thread, or the mixer) writes
/// its samples into a ring; the owner's thread analyses the latest window when it wants to and keeps a
/// smoothed band shape. The producer only copies, so it is safe on the mixer thread.
/// </summary>
public sealed class LiveBands
{
    private readonly float[] _ring = new float[BandAnalyser.WindowSamples];
    private long _written;
    private long _analysedAt;
    private readonly double[] _smoothed = new double[BandAnalyser.Bands];
    private readonly double[] _scratch = new double[BandAnalyser.Bands];
    private bool _primed;

    // One analyser and one window per analysing thread, not per voice: a voice's tap is its ring.
    [ThreadStatic] private static BandAnalyser? t_analyser;
    [ThreadStatic] private static float[]? t_window;

    public LiveBands(int sampleRate) => SampleRate = sampleRate;

    public int SampleRate { get; }

    /// <summary>Forgets what it heard, for a pooled tap about to serve another voice.</summary>
    public void Reset()
    {
        Volatile.Write(ref _written, 0);
        _analysedAt = 0;
        _primed = false;
        Array.Clear(_smoothed);
    }

    /// <summary>Producer: copies a block in. Never allocates, never throws for a sane block.</summary>
    public void Write(ReadOnlySpan<float> block)
    {
        long w = Volatile.Read(ref _written);
        int n = _ring.Length;
        for (int i = 0; i < block.Length; i++) _ring[(int)((w + i) % n)] = block[i];
        Volatile.Write(ref _written, w + block.Length);
    }

    /// <summary>Producer: one interleaved block, its channels averaged.</summary>
    public void WriteInterleaved(ReadOnlySpan<float> buffer, int frames, int channels)
    {
        long w = Volatile.Read(ref _written);
        int n = _ring.Length;
        float inv = 1f / Math.Max(1, channels);
        for (int i = 0; i < frames; i++)
        {
            float s = 0;
            for (int c = 0; c < channels; c++) s += buffer[i * channels + c];
            _ring[(int)((w + i) % n)] = s * inv;
        }
        Volatile.Write(ref _written, w + frames);
    }

    /// <summary>Samples written so far.</summary>
    public long Written => Volatile.Read(ref _written);

    /// <summary>
    /// Owner: when at least <paramref name="everySeconds"/> of new signal has arrived, analyses the
    /// latest window and folds it into the smoothed shape with time constant
    /// <paramref name="smoothSeconds"/>. True when the shape changed.
    /// </summary>
    public bool Update(float everySeconds, float smoothSeconds)
    {
        long w = Volatile.Read(ref _written);
        long due = (long)(everySeconds * SampleRate);
        if (w - _analysedAt < due || w < BandAnalyser.WindowSamples / 4) return false;
        int n = _ring.Length;
        int take = (int)Math.Min(w, n);
        if (t_analyser == null || t_analyser.SampleRate != SampleRate) t_analyser = new BandAnalyser(SampleRate);
        var window = t_window ??= new float[BandAnalyser.WindowSamples];
        for (int i = 0; i < take; i++) window[i] = _ring[(int)((w - take + i) % n)];
        Array.Clear(_scratch);
        t_analyser.Accumulate(new ReadOnlySpan<float>(window, 0, take), _scratch);
        double since = (w - _analysedAt) / (double)SampleRate;
        _analysedAt = w;
        double total = 0;
        for (int b = 0; b < _scratch.Length; b++) total += _scratch[b];
        if (!(total > 1e-20) || double.IsNaN(total)) return false;   // silence has no shape: keep the last
        double a = _primed ? 1.0 - Math.Exp(-since / Math.Max(1e-3, smoothSeconds)) : 1.0;
        for (int b = 0; b < _smoothed.Length; b++) _smoothed[b] += (_scratch[b] - _smoothed[b]) * a;
        _primed = true;
        return true;
    }

    /// <summary>Whether a shape has been measured yet.</summary>
    public bool HasShape => _primed;

    /// <summary>The smoothed band powers (relative; the shape is what counts).</summary>
    public ReadOnlySpan<double> BandPowers => _smoothed;
}

namespace OpenFPS.Common;

/// <summary>
/// Broadband noise whose power spectrum is set from moment to moment: what turbulent flow past a moving
/// body sounds like at a listener.
///
/// Third-octave bands from 100 Hz to 16 kHz, each its own independent white noise through a resonator
/// one third of an octave wide (the standard constant-peak band-pass), weighted so the band carries the
/// power the spectrum asks for. Independent noise per band, so neighbouring bands add in power and the
/// sum has no comb in it. A band-pass of quality Q passes white noise of unit variance (one-sided
/// density 2/fs) with an equivalent noise bandwidth of (π/2)·fc/Q, and a third octave is fc/Q wide, so
/// a band carrying density S over its width needs a gain of √(S·fs/π), whatever its centre.
///
/// The spectrum is a density in units² per hertz; the output is in those units. Set it as often as it
/// changes (every few dozen samples is plenty): the gains glide to the new value over the next block,
/// so a sweeping spectrum does not step.
///
/// Why not a sum of sine partials: a few dozen sines wandering about a centre frequency are heard as a
/// tone at that centre, however much they wander. That was the subsonic whizz, and it was heard as a
/// laser (Cody, 2026-10-04). Noise of the same spectrum is heard as a rush.
/// </summary>
public sealed class ShapedNoise
{
    /// <summary>The band centres, Hz: 1 kHz · 2^(k/3), 100 Hz to 16 kHz.</summary>
    public static readonly float[] Centres = BuildCentres();

    private static float[] BuildCentres()
    {
        var c = new float[23];
        for (int k = 0; k < c.Length; k++) c[k] = 1000f * MathF.Pow(2f, (k - 10) / 3f);
        return c;
    }

    private readonly int _sampleRate;
    private readonly int _bands;
    private readonly float[] _b0, _a1, _a2;
    private readonly float[] _x1, _x2, _y1, _y2;
    private readonly float[] _gain, _target, _step;
    private uint _state;
    private int _glideLeft;

    public ShapedNoise(int sampleRate, int seed)
    {
        _sampleRate = sampleRate;
        int n = 0;
        while (n < Centres.Length && Centres[n] * 1.13f < 0.45f * sampleRate) n++;
        _bands = n;
        _b0 = new float[n]; _a1 = new float[n]; _a2 = new float[n];
        _x1 = new float[n]; _x2 = new float[n]; _y1 = new float[n]; _y2 = new float[n];
        _gain = new float[n]; _target = new float[n]; _step = new float[n];
        float q = 1f / (MathF.Pow(2f, 1f / 6f) - MathF.Pow(2f, -1f / 6f));
        for (int k = 0; k < n; k++)
        {
            double w0 = 2 * Math.PI * Centres[k] / sampleRate;
            double alpha = Math.Sin(w0) / (2 * q);
            double a0 = 1 + alpha;
            _b0[k] = (float)(alpha / a0);
            _a1[k] = (float)(-2 * Math.Cos(w0) / a0);
            _a2[k] = (float)((1 - alpha) / a0);
        }
        _state = (uint)seed * 2654435761u + 0x9E3779B9u;
        if (_state == 0) _state = 1;
    }

    /// <summary>How many bands this rate can carry.</summary>
    public int BandCount => _bands;

    /// <summary>
    /// The spectrum from now on: <paramref name="density"/> at each band centre, units² per hertz. The
    /// gains glide there over <paramref name="glideSamples"/>.
    /// </summary>
    public void SetSpectrum(Func<float, float> density, int glideSamples = 32)
    {
        int g = Math.Max(1, glideSamples);
        for (int k = 0; k < _bands; k++)
        {
            float s = MathF.Max(0f, density(Centres[k]));
            _target[k] = MathF.Sqrt(s * _sampleRate / MathF.PI);
            _step[k] = (_target[k] - _gain[k]) / g;
        }
        _glideLeft = g;
    }

    /// <summary>The same, with the gains set at once (the first block).</summary>
    public void SetSpectrumNow(Func<float, float> density)
    {
        SetSpectrum(density, 1);
        for (int k = 0; k < _bands; k++) { _gain[k] = _target[k]; _step[k] = 0f; }
        _glideLeft = 0;
    }

    /// <summary>The next sample.</summary>
    public float Next()
    {
        bool gliding = _glideLeft > 0;
        if (gliding) _glideLeft--;
        float sum = 0f;
        for (int k = 0; k < _bands; k++)
        {
            // Unit-variance white noise: uniform on ±√3.
            _state ^= _state << 13; _state ^= _state >> 17; _state ^= _state << 5;
            float x = ((_state >> 8) * (1f / 16777216f) * 2f - 1f) * 1.7320508f;
            float y = _b0[k] * (x - _x2[k]) - _a1[k] * _y1[k] - _a2[k] * _y2[k];
            _x2[k] = _x1[k]; _x1[k] = x; _y2[k] = _y1[k]; _y1[k] = y;
            if (gliding) _gain[k] += _step[k];
            sum += y * _gain[k];
        }
        return sum;
    }
}

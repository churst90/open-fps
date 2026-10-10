namespace OpenFPS.Client.AudioEngine.Core;

/// <summary>One buffer from one rate to another, band-limited: what a render is brought to the mixer's rate by.</summary>
public static class SincResampler
{
    /// <summary>
    /// Brings a buffer to <paramref name="to"/> with a Kaiser-windowed sinc (64 taps a side, about 90 dB
    /// down in the stop band, flat to 95 % of the lower Nyquist), once, on the thread that rendered it.
    /// FMOD's resampler, even its spline, images the top octave: 48 to 44.1 kHz brought a 15 kHz
    /// component back at 11.1 kHz only 24-28 dB under itself (the lab's --quality resampler).
    /// </summary>
    public static float[] Resample(float[] x, int from, int to)
    {
        if (from <= 0 || to <= 0 || from == to || x.Length == 0) return x;
        int g = Gcd(from, to);
        int up = to / g, down = from / g;                      // out[n] sits at input time n * down / up
        const int Half = 64;                                   // taps a side, in input samples
        double fc = 0.5 * Math.Min(from, to) * 0.95 / from;     // cutoff, cycles per INPUT sample
        // Upsampling, the kernel is in input samples and the band is the input's: same table.
        int phases = up <= 4096 ? up : 4096;
        var table = new float[phases * 2 * Half];
        for (int p = 0; p < phases; p++)
        {
            double frac = (double)p / phases;
            double sum = 0;
            for (int j = -Half + 1; j <= Half; j++)
            {
                double t = frac - j;                            // the tap at x[k0 + j] is t away
                double h = 2 * fc * Sinc(2 * fc * t) * Kaiser(t / Half, 9.0);
                table[p * 2 * Half + (j + Half - 1)] = (float)h;
                sum += h;
            }
            // Each phase sums to exactly one: no ripple at the phase rate on a steady level.
            for (int j = 0; j < 2 * Half; j++) table[p * 2 * Half + j] = (float)(table[p * 2 * Half + j] / sum);
        }
        long n = ((long)x.Length * up + down - 1) / down;
        var y = new float[n];
        for (long i = 0; i < n; i++)
        {
            long pos = i * down;
            long k0 = pos / up;
            int p = (int)(pos % up);
            if (phases != up) p = (int)((long)p * phases / up);
            int row = p * 2 * Half;
            double acc = 0;
            for (int j = -Half + 1; j <= Half; j++)
            {
                long k = k0 + j;
                if (k < 0 || k >= x.Length) continue;
                acc += x[k] * table[row + j + Half - 1];
            }
            y[i] = (float)acc;
        }
        return y;
    }

    private static int Gcd(int a, int b) { while (b != 0) (a, b) = (b, a % b); return a; }

    private static double Sinc(double x) => Math.Abs(x) < 1e-12 ? 1.0 : Math.Sin(Math.PI * x) / (Math.PI * x);

    private static double Kaiser(double u, double beta)
    {
        if (Math.Abs(u) >= 1.0) return 0.0;
        return BesselI0(beta * Math.Sqrt(1.0 - u * u)) / BesselI0(beta);
    }

    private static double BesselI0(double x)
    {
        double sum = 1, term = 1, q = x * x / 4;
        for (int k = 1; k < 50; k++)
        {
            term *= q / (k * k);
            sum += term;
            if (term < 1e-12 * sum) break;
        }
        return sum;
    }
}

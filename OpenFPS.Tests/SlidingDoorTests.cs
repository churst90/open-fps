using System;
using System.Linq;
using OpenFPS.Common;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// The patio door (<see cref="SlidingDoor"/>) against the recording Cody named as the patio door
/// (inbox/door-sounds-2026-10-02/patio-slide/ref-goblinjack-slides.wav), measured as the lab's
/// --patio-vs-ref measures it. The recording is a yardstick for the model's physics; nothing of it is played.
/// </summary>
public class SlidingDoorTests
{
    /// <summary>The recording's slides in octaves, 125 Hz to 8 kHz, dB re the loudest (500 Hz).</summary>
    private static readonly double[] Octaves = { 125, 250, 500, 1000, 2000, 4000, 8000 };
    private static readonly double[] Recorded = { -7.4, -1.5, 0.0, -2.4, -9.4, -14.2, -16.2 };

    private static readonly Lazy<(float[] Pcm, double HomeSeconds)> StandardClose = new(() =>
    {
        var report = new SlidingDoor.Report();
        var pcm = SlidingDoor.RenderClose(new SlidingDoor.Door { Kind = SlidingDoor.Kind.Patio, Variant = 1, Seed = 2 }, 48000, 1.4, report);
        double home = report.Events.Where(e => e.EndsWith(" home", StringComparison.Ordinal))
            .Select(e => double.Parse(e.Trim().Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture) / 1000)
            .DefaultIfEmpty(double.NaN).First();
        return (pcm, home);
    });

    [Fact]
    public void APatioSlideHasTheRecordedDoorsBalance()
    {
        // Energy from 250 Hz to 1 kHz, thin below 125 Hz and falling steeply above 2 kHz: each octave within
        // 4 dB of the recording's. (Round 2 was flat to 8 kHz, 10-20 dB over it at both ends.)
        var (pcm, home) = StandardClose.Value;
        Assert.False(double.IsNaN(home));
        var bands = Bands(pcm, 48000, 0.25, home - 0.08);
        for (int o = 0; o < Octaves.Length; o++)
            Assert.True(Math.Abs(bands[o] - Recorded[o]) <= 4,
                $"{Octaves[o]} Hz octave at {bands[o]:F1} dB, the recording's {Recorded[o]:F1}");
        Assert.True(bands[6] <= bands[2] - 12, "the slide is not broadband: 8 kHz well under 500 Hz");
    }

    [Fact]
    public void APatioLeafPushedHomeRingsLow()
    {
        // The leaf meeting the jamb rings the frame and glass low, under 150 Hz, as the recording's stops do.
        var (pcm, home) = StandardClose.Value;
        int a = (int)((home + 0.01) * 48000), b = (int)((home + 0.26) * 48000);
        double best = 0, ring = 0;
        for (double f = 40; f <= 400; f += 1)
        {
            double re = 0, im = 0;
            for (int i = a; i < b; i++)
            {
                double w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * (i - a) / (b - a - 1));
                re += pcm[i] * w * Math.Cos(2 * Math.PI * f * i / 48000);
                im += pcm[i] * w * Math.Sin(2 * Math.PI * f * i / 48000);
            }
            if (re * re + im * im > best) { best = re * re + im * im; ring = f; }
        }
        Assert.InRange(ring, 60, 150);
    }

    /// <summary>Octave bands over [from, to] seconds by Welch's method (8192-point Hann frames), dB re the
    /// loudest of them.</summary>
    private static double[] Bands(float[] x, int rate, double from, double to)
    {
        const int n = 8192;
        var psd = new double[n / 2 + 1];
        int a = (int)(from * rate), b = (int)(to * rate);
        var re = new double[n]; var im = new double[n];
        for (int s = a; s + n <= b; s += n / 2)
        {
            for (int i = 0; i < n; i++) { re[i] = x[s + i] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (n - 1))); im[i] = 0; }
            Fft(re, im);
            for (int k = 0; k < psd.Length; k++) psd[k] += re[k] * re[k] + im[k] * im[k];
        }
        var bands = new double[Octaves.Length];
        for (int o = 0; o < Octaves.Length; o++)
        {
            double sum = 0;
            for (int k = 1; k < psd.Length; k++)
            {
                double f = (double)k * rate / n;
                if (f >= Octaves[o] / Math.Sqrt(2) && f < Octaves[o] * Math.Sqrt(2)) sum += psd[k];
            }
            bands[o] = 10 * Math.Log10(Math.Max(sum, 1e-30));
        }
        double top = bands.Max();
        return bands.Select(v => v - top).ToArray();
    }

    private static void Fft(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len, wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int p = i + k, q = p + len / 2;
                    double tr = re[q] * cr - im[q] * ci, ti = re[q] * ci + im[q] * cr;
                    re[q] = re[p] - tr; im[q] = im[p] - ti; re[p] += tr; im[p] += ti;
                    double nr = cr * wr - ci * wi; ci = cr * wi + ci * wr; cr = nr;
                }
            }
        }
    }
}

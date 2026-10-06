using System;
using System.Runtime.InteropServices;
using FMOD;
using Serilog;

namespace OpenFPS.Client.AudioEngine.Fmod;

/// <summary>
/// Settings of the mixer itself that decide how clean every voice is, whatever the voice is.
///
/// THE RESAMPLER. FMOD resamples a channel whenever its rate is not the mixer's: a buffer rendered
/// at 48 kHz (every TransientSynth sound, the door renders), thunder at 24 kHz, a recording at
/// 48 kHz — and EVERY voice with a pitch that is not exactly one, which here is every voice that
/// moves or that a moving listener hears, because Doppler is applied as a channel pitch. FMOD's
/// default is linear interpolation. Linear interpolation is a two-tap filter whose response depends
/// on where between two samples the read falls: halfway between, it is down 6 dB at 15 kHz and
/// 11 dB at 18 kHz; on a sample it is flat. With a pitch a fraction of a percent off one, that
/// position sweeps round hundreds of times a second, so the top octave of every noisy sound — water,
/// leaves, rain, tyres, wind — is amplitude-modulated at that rate, and everything a 48 kHz source
/// holds above 22 kHz folds back down into the top of the band. Measured with the lab's
/// --quality resampler (docs/AUDIO_QUALITY_2026-10-06.md).
///
/// OPENFPS_RESAMPLER=linear|cubic|spline|none overrides it, for an A/B by ear.
/// </summary>
public static class MixerQuality
{
    /// <summary>The method the mixer is given. See the class summary. Read when a mixer is made, so
    /// the lab can make one of each in a single run.</summary>
    public static DSP_RESAMPLER Resampler => Parse(Environment.GetEnvironmentVariable("OPENFPS_RESAMPLER"));

    /// <summary>Captures (OPENFPS_AUDIO_CAPTURE, _PRE) in 32-bit float rather than 16-bit PCM.</summary>
    public static bool CaptureFloat => Environment.GetEnvironmentVariable("OPENFPS_AUDIO_CAPTURE_FLOAT") == "1";

    /// <summary>The default when nothing is asked for.</summary>
    public const DSP_RESAMPLER Default = DSP_RESAMPLER.SPLINE;

    internal static DSP_RESAMPLER Parse(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "linear" => DSP_RESAMPLER.LINEAR,
        "cubic" => DSP_RESAMPLER.CUBIC,
        "spline" => DSP_RESAMPLER.SPLINE,
        "none" or "nointerp" => DSP_RESAMPLER.NOINTERP,
        _ => Default,
    };

    /// <summary>The mixer's sample rate, once a mixer has been made (FmodAudioProvider.Initialize).</summary>
    public static int MixerRate { get => _mixerRate; set => _mixerRate = value > 0 ? value : 44100; }
    private static volatile int _mixerRate = 44100;

    /// <summary>
    /// A buffer at another rate brought to <paramref name="to"/> properly: band-limited, by a
    /// Kaiser-windowed sinc (64 taps a side, about 90 dB down in the stop band, flat to 95 % of the
    /// lower Nyquist). For the synthesised one-shots, which are rendered at 48 kHz and played by a
    /// 44.1 kHz mixer: FMOD's resampler, even its spline, is a short polynomial, and from 48 kHz it
    /// folds everything above 22 kHz back into the band and images the top octave 3.9 kHz down
    /// (a 15 kHz component came back at 11.1 kHz only 24-28 dB under itself, measured with the lab's
    /// --quality resampler). Done once per buffer, on the thread that rendered it.
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

    /// <summary>
    /// OPENFPS_FMOD_OUTPUT=pulse|alsa|wasapi: which output FMOD drives, for measuring what reaches the
    /// sound server (the lab's --quality output). Unset, FMOD chooses, as it always has.
    /// </summary>
    public static void ApplyOutput(FMOD.System system)
    {
        OUTPUTTYPE? type = Environment.GetEnvironmentVariable("OPENFPS_FMOD_OUTPUT")?.Trim().ToLowerInvariant() switch
        {
            "pulse" or "pulseaudio" => OUTPUTTYPE.PULSEAUDIO,
            "alsa" => OUTPUTTYPE.ALSA,
            "wasapi" => OUTPUTTYPE.WASAPI,
            _ => null,
        };
        if (type == null) return;
        RESULT r = system.setOutput(type.Value);
        if (r == RESULT.OK) Log.Information("FMOD output: {Type} (OPENFPS_FMOD_OUTPUT).", type);
        else Log.Warning("FMOD output {Type} refused: {Result}.", type, r);
    }

    /// <summary>Sets the resampler. BEFORE System.init: FMOD reads it when the mixer is made.</summary>
    public static void ApplyResampler(FMOD.System system)
    {
        DSP_RESAMPLER method = Resampler;
        var adv = new ADVANCEDSETTINGS { cbSize = Marshal.SizeOf<ADVANCEDSETTINGS>() };
        RESULT r = system.getAdvancedSettings(ref adv);
        if (r == RESULT.OK)
        {
            adv.cbSize = Marshal.SizeOf<ADVANCEDSETTINGS>();
            adv.resamplerMethod = method;
            r = system.setAdvancedSettings(ref adv);
        }
        if (r == RESULT.OK) Log.Information("FMOD resampler: {Method}.", method);
        else Log.Warning("FMOD resampler could not be set to {Method}: {Result}; FMOD's default (linear) stands.", method, r);
    }
}

namespace OpenFPS.Client.Core;

/// <summary>
/// The scope's own sounds: the guidance tone and a held breath. Synthesised, quiet, in both ears and
/// not in the world, like the interface sounds.
///
/// The guidance tone is two loops. The first is one PULSE and the silence after it; played faster it
/// rises in pitch and pulses faster together, because both are the same loop played at a higher rate.
/// That is the whole of "pitch and pulse rate rise as the crosshair nears a target": one number, the
/// playback rate, from 1 far off to 2 almost on. The second is the same note held, which the pulses
/// settle into when the crosshair is on a body: it is the pulse's own top pitch, so arriving on target
/// sounds like the pulses running together rather than like a different sound starting.
///
/// The timbre is a soft reed: a fundamental with a little second and third harmonic, so it is not the
/// pure sine of the beacons and not the bell of the interface chimes.
/// </summary>
public static class ScopeSounds
{
    /// <summary>The rate both loops and the breath are rendered at: the mixer's, so they play without a
    /// resampler.</summary>
    public static int SampleRate => OpenFPS.Client.AudioEngine.Fmod.MixerQuality.MixerRate;

    /// <summary>The held note, Hz. About E5. It was thirty cycles in two thousand samples at 44.1 kHz,
    /// and stays that note at any rate: the loop is made of whole cycles instead (<see cref="SteadyLoopSamples"/>).</summary>
    public const float SteadyHz = 661.5f;
    /// <summary>The pulse's note at the slowest rate, an octave under the held note.</summary>
    public const float PulseHz = SteadyHz / 2f;
    /// <summary>One pulse and its silence at the slowest rate, seconds: two and a half a second.</summary>
    public const float PulsePeriodSeconds = 0.4f;
    /// <summary>How long the pulse sounds within that, seconds.</summary>
    public const float PulseOnSeconds = 0.13f;

    /// <summary>The peak of both loops before the interface volume: about -12 dBFS, so with the
    /// interface at its usual half the tone sits near -18, under speech and under the world.</summary>
    public const float Peak = 0.25f;

    private static float Reed(double phase)
        => (float)(Math.Sin(phase) + 0.25 * Math.Sin(2 * phase) + 0.08 * Math.Sin(3 * phase)) / 1.2f;

    /// <summary>One pulse and its silence: a raised-cosine swell up and down, so nothing in it clicks.</summary>
    public static float[] RenderPulse()
    {
        int n = (int)(PulsePeriodSeconds * SampleRate);
        int on = (int)(PulseOnSeconds * SampleRate);
        var x = new float[n];
        double w = 2 * Math.PI * PulseHz / SampleRate;
        for (int i = 0; i < on; i++)
        {
            float env = 0.5f * (1f - MathF.Cos(2f * MathF.PI * i / on));   // up and down, zero at both ends
            x[i] = Peak * env * Reed(w * i);
        }
        return x;
    }

    /// <summary>The fewest samples, at least about 45 ms, that hold a whole number of cycles of
    /// <see cref="SteadyHz"/> (1323/2 Hz) at <paramref name="rate"/>: 2000 at 44.1 kHz, 32000 at 48.</summary>
    public static int SteadyLoopSamples(int rate)
    {
        static int Gcd(int a, int b) { while (b != 0) (a, b) = (b, a % b); return a; }
        int n = 2 * rate / Gcd(2 * rate, 1323);          // cycles = n * 1323 / (2 * rate)
        int want = Math.Max(1, (int)(0.045 * rate));
        return n >= want ? n : n * ((want + n - 1) / n);
    }

    /// <summary>The held note: whole cycles, so it loops without a seam.</summary>
    public static float[] RenderSteady()
    {
        int n = SteadyLoopSamples(SampleRate);
        var x = new float[n];
        double w = 2 * Math.PI * SteadyHz / SampleRate;
        for (int i = 0; i < n; i++) x[i] = Peak * Reed(w * i);
        return x;
    }

    /// <summary>
    /// The pulse loop's playback rate for how near the crosshair is to the nearest target, 0 (at the
    /// edge of the view) to 1 (touching it): 1 to 2, an octave of pitch and a doubling of the pulse.
    /// </summary>
    public static float PulseRate(float closeness) => MathF.Pow(2f, Math.Clamp(closeness, 0f, 1f));

    /// <summary>
    /// A breath for holding still: drawn in (higher, narrower) when the hold starts and let out (lower,
    /// longer) when it ends. Air, not a note: broad noise around a centre, swelling and fading on a
    /// raised cosine so there is no onset to hear as a knock. Quiet, -26 dBFS at its peak before the
    /// interface volume and about -35 as played, because it is the shooter's own and is only there to
    /// be felt.
    /// </summary>
    public static float[] RenderBreath(bool inhale, int seed = 1)
    {
        float seconds = inhale ? 0.55f : 0.8f;
        float hz = inhale ? 1100f : 600f;
        int n = (int)(seconds * SampleRate);
        var x = new float[n];
        var rng = new Random(seed);
        float lowCut = hz / 3f, highCut = MathF.Min(SampleRate * 0.45f, hz * 6f);
        float aHigh = 1f - MathF.Exp(-MathF.Tau * highCut / SampleRate);
        float aLow = 1f - MathF.Exp(-MathF.Tau * lowCut / SampleRate);
        float lp1 = 0f, lp2 = 0f, hp = 0f;
        int rise = (int)(n * (inhale ? 0.45f : 0.3f));
        for (int i = 0; i < n; i++)
        {
            float noise = (float)(rng.NextDouble() * 2.0 - 1.0);
            lp1 += aHigh * (noise - lp1);
            lp2 += aHigh * (lp1 - lp2);
            hp += aLow * (lp2 - hp);
            float env = i < rise
                ? 0.5f * (1f - MathF.Cos(MathF.PI * i / rise))
                : 0.5f * (1f + MathF.Cos(MathF.PI * (i - rise) / (n - rise)));
            x[i] = (lp2 - hp) * env;
        }
        float peak = 1e-9f;
        foreach (float v in x) peak = MathF.Max(peak, MathF.Abs(v));
        float k = 0.05f / peak;
        for (int i = 0; i < n; i++) x[i] *= k;
        return x;
    }
}

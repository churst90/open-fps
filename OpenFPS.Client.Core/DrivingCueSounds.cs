using System;

namespace OpenFPS.Client.Core;

/// <summary>
/// The driving cues' own sounds, made once at the transient rate and registered as buffers
/// (DrivingAids.EnsureSounds). Each is a different kind of sound from every other cue, so none can be
/// mistaken for another: the guide is a pure high beep, the line sensors are buzzy beeps, the turn
/// clicks are clicks; these are a falling note (brake), two falling bell notes (speed), a relay
/// (indicator), and strikes of a tyre on raised markers and on a rumble strip.
/// </summary>
public static class DrivingCueSounds
{
    /// <summary>
    /// The brake cue's note: a glide down by a fifth — "come down" — over 90 ms (60 at the limit), a
    /// sine with a little of its third harmonic so it is rounder than the guide's beep. Higher bands start
    /// higher.
    /// </summary>
    public static float[] Brake(int rate, float hz, int band)
    {
        float seconds = band >= 4 ? 0.06f : 0.09f;
        int n = (int)(rate * seconds);
        var buf = new float[n];
        double phase = 0;
        int attack = Math.Max(1, (int)(rate * 0.005f));
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)n;
            float f = hz * (1.5f - 0.5f * t);
            phase += 2 * Math.PI * f / rate;
            float s = MathF.Sin((float)phase) + MathF.Sin(3f * (float)phase) / 9f;
            float env = MathF.Min(1f, i / (float)attack) * MathF.Min(1f, (n - 1 - i) / (float)(attack * 3));
            buf[i] = 0.5f * s * env;
        }
        return buf;
    }

    /// <summary>Two notes falling a major third, each struck and dying away like a small bell: over the limit.</summary>
    public static float[] OverSpeed(int rate)
    {
        int n = (int)(rate * 0.55f);
        var buf = new float[n];
        void Note(float hz, float at)
        {
            int start = (int)(rate * at);
            for (int i = start; i < n; i++)
            {
                float t = (i - start) / (float)rate;
                float env = MathF.Min(1f, t / 0.004f) * MathF.Exp(-t / 0.12f);
                buf[i] += 0.32f * env * (MathF.Sin(MathF.Tau * hz * t) + 0.2f * MathF.Sin(MathF.Tau * 2.76f * hz * t) * MathF.Exp(-t / 0.04f));
            }
        }
        Note(880f, 0f);
        Note(698.5f, 0.2f);
        return buf;
    }

    /// <summary>
    /// A flasher relay: the armature closing (tick) or opening (tock) against its stop. Two damped modes
    /// of the frame and contact, struck by a millisecond of contact noise; the opening is softer and lower.
    /// </summary>
    public static float[] Relay(int rate, bool closing)
    {
        int n = (int)(rate * 0.035f);
        var buf = new float[n];
        var rng = new Random(closing ? 17 : 23);
        float hiHz = closing ? 3400f : 2600f, loHz = closing ? 1200f : 900f;
        float level = closing ? 0.55f : 0.4f;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)rate;
            float noise = t < 0.001f ? (float)(rng.NextDouble() * 2 - 1) * (1f - t / 0.001f) : 0f;
            float hi = MathF.Sin(MathF.Tau * hiHz * t) * MathF.Exp(-t / 0.003f);
            float lo = MathF.Sin(MathF.Tau * loHz * t) * MathF.Exp(-t / 0.006f);
            buf[i] = level * (0.5f * hi + 0.4f * lo + 0.3f * noise);
        }
        return buf;
    }

    /// <summary>
    /// A second of a tyre rolling over raised pavement markers (Botts' dots) at <paramref name="perSecond"/>
    /// a second: a hard knock of the ceramic dot under the tread and the thump of the wheel taking it.
    /// Looped and pitched to the speed over the markers' spacing.
    /// </summary>
    public static float[] Dots(int rate, float perSecond)
    {
        int n = rate;
        var buf = new float[n];
        var rng = new Random(41);
        int count = (int)MathF.Round(perSecond);
        for (int k = 0; k < count; k++)
        {
            int start = k * n / count;
            float gain = 0.85f + 0.3f * (float)rng.NextDouble();
            int len = (int)(rate * 0.06f);
            for (int j = 0; j < len; j++)
            {
                float t = j / (float)rate;
                float noise = t < 0.002f ? (float)(rng.NextDouble() * 2 - 1) : 0f;
                float knock = MathF.Sin(MathF.Tau * 1800f * t) * MathF.Exp(-t / 0.003f);
                float thump = MathF.Sin(MathF.Tau * 180f * t) * MathF.Exp(-t / 0.015f);
                buf[(start + j) % n] += gain * (0.35f * knock + 0.5f * thump + 0.25f * noise);
            }
        }
        Normalise(buf, 0.6f);
        return buf;
    }

    /// <summary>
    /// A second of a tyre over a milled rumble strip at <paramref name="perSecond"/> grooves a second: each
    /// groove a drop and a thump of the tread band (around 120 Hz) with a little of the tread's slap. At a
    /// town speed the strikes run together into the drone that gives a rumble strip its name.
    /// </summary>
    public static float[] Strip(int rate, float perSecond)
    {
        int n = rate;
        var buf = new float[n];
        var rng = new Random(43);
        int count = (int)MathF.Round(perSecond);
        for (int k = 0; k < count; k++)
        {
            int start = k * n / count;
            float gain = 0.85f + 0.3f * (float)rng.NextDouble();
            int len = (int)(rate * 0.04f);
            for (int j = 0; j < len; j++)
            {
                float t = j / (float)rate;
                float noise = t < 0.003f ? (float)(rng.NextDouble() * 2 - 1) * (1f - t / 0.003f) : 0f;
                float band = MathF.Sin(MathF.Tau * 120f * t) * MathF.Exp(-t / 0.006f);
                float slap = MathF.Sin(MathF.Tau * 420f * t) * MathF.Exp(-t / 0.003f);
                buf[(start + j) % n] += gain * (0.6f * band + 0.25f * slap + 0.2f * noise);
            }
        }
        Normalise(buf, 0.6f);
        return buf;
    }

    private static void Normalise(float[] buf, float peak)
    {
        float max = 0f;
        foreach (float v in buf) max = MathF.Max(max, MathF.Abs(v));
        if (max <= 0f) return;
        float g = peak / max;
        for (int i = 0; i < buf.Length; i++) buf[i] *= g;
    }
}

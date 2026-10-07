namespace OpenFPS.Client.AudioEngine.Core;

/// <summary>
/// Per-sample coefficients chosen at 44.1 kHz, carried to a voice's own rate so each keeps its time
/// constant: `ring *= 0.9955f` is 5.0 ms at 44.1 kHz and 4.6 ms at 48, and through <see cref="Decay"/>
/// stays 5.0 ms. At 44.1 kHz each returns its argument exactly. Work them out once, not per sample:
/// each is a power.
/// </summary>
public static class At44k
{
    /// <summary>The rate the literals were chosen at.</summary>
    public const float Rate = 44100f;

    /// <summary>A per-sample multiplier <c>y *= p</c> (a decay or a pole) chosen at 44.1 kHz.</summary>
    public static float Decay(float p, float rate)
        => rate <= 0f || rate == Rate || p <= 0f ? p : MathF.Pow(p, Rate / rate);

    /// <summary>A one-pole step <c>y += a (x - y)</c> chosen at 44.1 kHz.</summary>
    public static float Step(float a, float rate)
        => rate <= 0f || rate == Rate || a <= 0f || a >= 1f ? a : 1f - MathF.Pow(1f - a, Rate / rate);

    /// <inheritdoc cref="Step(float, float)"/>
    public static double Step(double a, double rate)
        => rate <= 0 || rate == Rate || a <= 0 || a >= 1 ? a : 1 - Math.Pow(1 - a, Rate / rate);

    /// <summary>A per-sample increment (a slew limit, a linear ramp) chosen at 44.1 kHz.</summary>
    public static float Increment(float perSample, float rate)
        => rate <= 0f ? perSample : perSample * (Rate / rate);
}

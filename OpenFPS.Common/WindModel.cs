using System.Numerics;

namespace OpenFPS.Common;

/// <summary>
/// The sustained wind (the server's, broadcast once a second) swung by a gust envelope made from the
/// broadcast gustiness and a clock: a gust sampled once a second would alias into a stutter.
/// Deterministic in the clock. Only the server's bullets use it now (CombatService); everything heard
/// reads <see cref="WindField"/>.
/// </summary>
public static class WindModel
{
    /// <summary>How far a gust can swing the sustained wind at <c>gustiness = 1</c>: a full gale gusts to
    /// 2.2x the sustained speed and can briefly fall to a dead calm.</summary>
    public const float MaxGustFraction = 1.2f;

    // Three incommensurate rates, so the sum never repeats on a hearable period; the weights sum to 1,
    // so the result stays in [-1, 1].
    private const double RateSlow = 0.11;   // Hz — the swell, ~9 s
    private const double RateMid = 0.29;    // Hz — ~3.4 s
    private const double RateFast = 0.73;   // Hz — flutter, ~1.4 s

    /// <summary>The gust envelope, smooth and in [-1, 1]: negative a lull, positive a gust.</summary>
    public static float GustFactor(double seconds)
    {
        double a = Math.Sin(seconds * RateSlow * 2.0 * Math.PI);
        double b = Math.Sin(seconds * RateMid * 2.0 * Math.PI + 1.7);
        double c = Math.Sin(seconds * RateFast * 2.0 * Math.PI + 4.1);
        return (float)(0.5 * a + 0.3 * b + 0.2 * c);
    }

    /// <summary>The sustained wind swung by the gust envelope and cut by shelter: indoors there is none.</summary>
    /// <param name="sustained">The server's broadcast <c>WindVelocity</c>, m/s.</param>
    /// <param name="gustiness">The server's broadcast <c>WindGustiness</c>, 0..1.</param>
    /// <param name="seconds">A monotonically increasing local clock.</param>
    /// <param name="shelterFactor">0 = fully exposed, 1 = fully enclosed.</param>
    public static Vector3 Felt(Vector3 sustained, float gustiness, double seconds, float shelterFactor = 0f)
    {
        float g = Math.Clamp(gustiness, 0f, 1f);
        float exposure = 1f - Math.Clamp(shelterFactor, 0f, 1f);
        if (exposure <= 0f) return Vector3.Zero;

        // A lull can reach a dead calm but never reverses the wind — a gust does not blow backwards.
        float swing = Math.Max(0f, 1f + g * MaxGustFraction * GustFactor(seconds));
        return sustained * swing * exposure;
    }

    /// <summary>The magnitude of <see cref="Felt"/>.</summary>
    public static float FeltSpeed(Vector3 sustained, float gustiness, double seconds, float shelterFactor = 0f)
        => Felt(sustained, gustiness, seconds, shelterFactor).Length();
}

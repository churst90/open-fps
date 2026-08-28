using System;
using System.Numerics;

namespace OpenFPS.Common;

/// <summary>
/// Wind, split into the two parts that travel differently.
///
/// The <b>sustained</b> wind is a vector the server simulates and broadcasts once a second — it is
/// weather, it changes over minutes, and a low rate is exactly right for it. The <b>gust</b> is the
/// part you actually hear: a swell and drop over a couple of seconds. Sending that over the wire at
/// one sample per second would alias it into a stutter, so only its amplitude — a single scalar,
/// <c>WindGustiness</c> — is transmitted, and each client synthesizes the gust locally at audio rate.
///
/// The synthesis is deterministic in the clock it is handed: same seconds in, same factor out. That
/// is what makes it testable, and it means two clients standing together hear the same weather as
/// long as their clocks agree (they do not have to: a gust is not a game-state fact).
/// </summary>
public static class WindModel
{
    /// <summary>How far a gust can swing the sustained wind at <c>gustiness = 1</c>. 1.2 means a full
    /// gale gusts to 2.2x the sustained speed and can briefly fall to a dead calm.</summary>
    public const float MaxGustFraction = 1.2f;

    // Three incommensurate rates so the sum never repeats on a hearable period; weights sum to 1 so
    // the result is bounded by [-1, 1] exactly. Slowest carries the swell, fastest the flutter.
    private const double RateSlow = 0.11;   // Hz — the swell, ~9 s
    private const double RateMid = 0.29;    // Hz — ~3.4 s
    private const double RateFast = 0.73;   // Hz — flutter, ~1.4 s

    /// <summary>
    /// The gust envelope at a moment in time: a smooth, bounded, deterministic value in [-1, 1].
    /// Negative is a lull, positive is a gust.
    /// </summary>
    public static float GustFactor(double seconds)
    {
        double a = Math.Sin(seconds * RateSlow * 2.0 * Math.PI);
        double b = Math.Sin(seconds * RateMid * 2.0 * Math.PI + 1.7);
        double c = Math.Sin(seconds * RateFast * 2.0 * Math.PI + 4.1);
        return (float)(0.5 * a + 0.3 * b + 0.2 * c);
    }

    /// <summary>
    /// The wind actually felt at the listener: the sustained wind, swung by the gust envelope, and
    /// attenuated by shelter — indoors there is no wind, which is half of what makes a doorway
    /// audible as a doorway.
    /// </summary>
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

    /// <summary>
    /// Scalar wind speed felt at the listener — the magnitude of <see cref="Felt"/>, for callers that
    /// only need "how hard is it blowing" (ambience level, gust-triggered creaks).
    /// </summary>
    public static float FeltSpeed(Vector3 sustained, float gustiness, double seconds, float shelterFactor = 0f)
        => Felt(sustained, gustiness, seconds, shelterFactor).Length();
}

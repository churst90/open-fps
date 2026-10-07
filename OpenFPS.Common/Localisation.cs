namespace OpenFPS.Common;

/// <summary>
/// When two sources stop being two: a machine's intake and exhaust are worth separate voices only while
/// a listener can tell them apart, and a street of cars or a grandstand becomes one extended source where
/// its members stop being distinguishable. Decided by the angle they subtend at the listener, not by
/// distance: two tailpipes a metre apart separate at three metres, a hundred-metre grandstand at fifty.
/// </summary>
public static class Localisation
{
    /// <summary>
    /// The angle below which two parts of one moving thing are heard as one, degrees. Not the minimum
    /// audible angle (a degree or two, for separate uncorrelated sources): two outlets of one machine
    /// carry the same firing, and what separates them is their bearings sweeping differently as it
    /// passes. For a car, three and a half metres from airbox to tailpipe, the second voice comes in at
    /// about twenty metres.
    /// </summary>
    public const float MinResolvableDegrees = 10f;

    /// <summary>The angle two points a given distance apart subtend at a listener, degrees.</summary>
    public static float SubtendedDegrees(float separationMetres, float distanceMetres)
    {
        if (separationMetres <= 0f) return 0f;
        if (distanceMetres <= 0.01f) return 180f;
        return 2f * MathF.Atan2(separationMetres * 0.5f, distanceMetres) * (180f / MathF.PI);
    }

    /// <summary>Can a listener at this distance tell these two apart?</summary>
    public static bool Resolvable(float separationMetres, float distanceMetres)
        => SubtendedDegrees(separationMetres, distanceMetres) >= MinResolvableDegrees;

    /// <summary>The distance at which two sources this far apart merge into one.</summary>
    public static float MergingDistance(float separationMetres)
        => separationMetres <= 0f ? 0f
         : separationMetres * 0.5f / MathF.Tan(MinResolvableDegrees * 0.5f * (MathF.PI / 180f));
}

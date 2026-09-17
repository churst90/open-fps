using System;

namespace OpenFPS.Common;

/// <summary>
/// When two sources stop being two.
///
/// One question, asked in both directions, and it is the same question. A machine's outlets — the
/// intake at the front of a car, the exhaust at the back — are worth separate voices only while a
/// listener can tell which is which; past that distance they are one source and paying for two is
/// paying for nothing. And a street of thirty cars, or a flock of birds, or a grandstand of people,
/// collapses into ONE extended source at exactly the distance where its members stop being
/// distinguishable from each other.
///
/// So the rule belongs here rather than in either caller, and it is geometric rather than authored:
/// what matters is the ANGLE two sources subtend at the listener, not how far away they are. Two
/// tailpipes a metre apart are separable at three metres and not at fifty; a hundred-metre grandstand
/// is separable at fifty metres and not at five hundred; the same arithmetic answers both.
/// </summary>
public static class Localisation
{
    /// <summary>
    /// The angle below which two parts of one moving thing are heard as one thing, degrees.
    ///
    /// Not the minimum audible angle, which for a broadband source straight ahead is a degree or
    /// two: that is the threshold for telling two SEPARATE, uncorrelated sources apart under
    /// laboratory conditions. Two outlets of one machine are neither separate nor uncorrelated —
    /// they carry the same firing, moving together at the same speed, one behind the other — and the
    /// cue that actually separates them is the bearing SWEEPING differently as the thing goes past.
    /// Ten degrees is where that sweep stops being a sweep and becomes a single bearing with a wide
    /// source behind it, and it is also, conveniently, where spending a second voice stops buying
    /// anything you could be asked to name.
    ///
    /// For a car — three and a half metres from airbox to tailpipe — it puts the second voice inside
    /// about twenty metres, which is roughly the distance at which a passing car stops being "a car
    /// over there" and becomes something going past you.
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

    /// <summary>
    /// The distance at which two sources this far apart merge into one.
    ///
    /// The inverse of the above, for anything that would rather sort by a distance than test each
    /// candidate — a voice budget spending itself nearest-first, or an aggregation deciding how wide
    /// its one source should be.
    /// </summary>
    public static float MergingDistance(float separationMetres)
        => separationMetres <= 0f ? 0f
         : separationMetres * 0.5f / MathF.Tan(MinResolvableDegrees * 0.5f * (MathF.PI / 180f));
}

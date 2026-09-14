using System;

namespace OpenFPS.Common;

/// <summary>
/// When the sounds of a shot reach a listener, and in what order.
///
/// A supersonic bullet makes two separate sounds, and for a player navigating by ear the relationship
/// between them is worth more than either one on its own:
///
///   * The MUZZLE BLAST leaves the weapon and travels at the speed of sound, so it arrives at d/c.
///   * The bullet itself outruns it. Everywhere it passes it drags a shock cone behind it — the CRACK —
///     and the crack you hear is made at the moment the bullet passes YOU, arriving at roughly d/v.
///
/// Since v &gt; c the crack arrives FIRST, and the gap between them is d·(1/c − 1/v): about 1.7 ms per
/// metre for a rifle. At a hundred metres that is a sixth of a second — comfortably perceivable, and a
/// direct readout of how far away the shooter is. Sighted games throw this away; here it is a genuine
/// instrument. Crack-then-thump close together means someone near. A long gap means someone far. No
/// crack at all means the round did not pass close to you, or was subsonic.
///
/// All pure and deterministic, so the server can use it for authoritative timing and the client for the
/// same numbers without either of them drifting.
/// </summary>
public static class Ballistics
{
    /// <summary>Below this a round makes no shock wave at all, so there is no crack to hear — only the
    /// report. Suppressed and subsonic loads live here, and their silence is the point.</summary>
    public const float SubsonicMarginMetresPerSecond = 5f;

    /// <summary>How close the round must pass before its shock is worth rendering, in metres. Beyond
    /// this the cone has spread and weakened into something that reads as part of the report.</summary>
    public const float MaxCrackMissDistance = 25f;

    /// <summary>
    /// Seconds from the trigger until the muzzle blast reaches a listener <paramref name="distance"/>
    /// metres away.
    /// </summary>
    public static float ReportArrival(float distance, float speedOfSound)
    {
        if (speedOfSound <= 1f) return 0f;
        return MathF.Max(0f, distance) / speedOfSound;
    }

    /// <summary>
    /// Seconds from the trigger until the bullet's shock wave reaches a listener, where
    /// <paramref name="distanceAlongPath"/> is how far the round travels before its closest approach
    /// and <paramref name="missDistance"/> is how far away it passes.
    ///
    /// The bullet's flight dominates; the short hop from the bullet's path to the ear travels at the
    /// speed of sound. Deceleration is not modelled — over the ranges this game will use, treating the
    /// muzzle velocity as constant is wrong by a couple of milliseconds.
    /// </summary>
    public static float CrackArrival(float distanceAlongPath, float missDistance,
                                     float muzzleVelocity, float speedOfSound)
    {
        if (muzzleVelocity <= 1f || speedOfSound <= 1f) return 0f;
        float travel = MathF.Max(0f, distanceAlongPath) / muzzleVelocity;
        float hop = MathF.Max(0f, missDistance) / speedOfSound;
        return travel + hop;
    }

    /// <summary>Whether this round makes a crack at all: supersonic, and passing close enough to matter.</summary>
    public static bool MakesCrack(float muzzleVelocity, float missDistance, float speedOfSound) =>
        muzzleVelocity > speedOfSound + SubsonicMarginMetresPerSecond &&
        missDistance >= 0f && missDistance <= MaxCrackMissDistance;

    /// <summary>
    /// The gap a listener actually hears between the crack and the report — the distance cue. Positive
    /// means the crack leads, which it always does for a supersonic round fired towards the listener.
    /// </summary>
    public static float CrackToReportSeconds(float distance, float muzzleVelocity, float speedOfSound)
    {
        if (muzzleVelocity <= 1f || speedOfSound <= 1f) return 0f;
        return MathF.Max(0f, distance) * (1f / speedOfSound - 1f / muzzleVelocity);
    }

    /// <summary>
    /// The inverse: how far away the shooter was, given the gap the listener heard. This is the sum the
    /// player's ear is doing, written down — useful for tuning, for a spoken range readout, and for
    /// checking that the timing actually encodes what it claims to.
    /// </summary>
    public static float DistanceFromCrackToReport(float gapSeconds, float muzzleVelocity, float speedOfSound)
    {
        float perMetre = 1f / speedOfSound - 1f / muzzleVelocity;
        if (perMetre <= 1e-9f) return 0f;
        return MathF.Max(0f, gapSeconds) / perMetre;
    }

    /// <summary>
    /// Half-angle of the Mach cone, radians: the shock trails the bullet at asin(c/v). A faster round
    /// drags a tighter cone, which is why a high-velocity rifle round cracks like a whip and a slower
    /// one sounds broader and duller.
    /// </summary>
    public static float MachConeAngle(float muzzleVelocity, float speedOfSound)
    {
        if (muzzleVelocity <= speedOfSound) return MathF.PI / 2f;
        return MathF.Asin(Math.Clamp(speedOfSound / muzzleVelocity, 0f, 1f));
    }

    /// <summary>
    /// Duration of the N-wave a passing round makes, in seconds. It grows with how far away the round
    /// passes and shrinks with velocity, which is what makes a near miss a sharp tearing CRACK and a
    /// distant one a flatter, duller snap. Derived from the classical N-wave width; the constant is
    /// folded from the round's calibre and length, which this game does not model separately.
    /// </summary>
    public static float CrackDurationSeconds(float missDistance, float muzzleVelocity, float speedOfSound)
    {
        if (muzzleVelocity <= speedOfSound) return 0f;
        float mach = muzzleVelocity / speedOfSound;
        float d = Math.Clamp(missDistance, 0.25f, MaxCrackMissDistance);
        // ~0.2 ms passing at a quarter metre, widening as the cube root of the miss distance.
        return 0.0002f * MathF.Cbrt(d / 0.25f) / MathF.Max(1f, mach - 1f + 1f);
    }
}

namespace OpenFPS.Common;

/// <summary>
/// How hard a tyre is being asked to work, and what that sounds like. Rolling, the blocks slap the
/// road: a broadband roar with a tone at the block rate. Sliding, a tread element grips, deflects,
/// lets go and snaps back at its own resonance: the squeal. Which one is heard is decided by one
/// number, friction demanded over friction available: quiet below about 0.8, squealing toward 1, and
/// past it the stick-slip cycle loses its regularity and collapses into a locked wheel's roar. Chirp,
/// squeal and skid are one continuum at three demands.
/// </summary>
public static class TyreFriction
{
    public const float G = 9.80665f;

    /// <summary>The fraction of available grip in use: 0 coasting straight, 1 the limit, above 1
    /// sliding. Longitudinal and lateral combine as a vector (the friction circle), not a sum.</summary>
    public static float Demand(float longitudinalMps2, float lateralMps2, float gripG)
    {
        float capacity = MathF.Max(0.05f, gripG) * G;
        float used = MathF.Sqrt(longitudinalMps2 * longitudinalMps2 + lateralMps2 * lateralMps2);
        return used / capacity;
    }

    /// <summary>Where the squeal begins, as a fraction of capacity. Below this the contact patch is
    /// gripping almost everywhere and the tyre just rolls.</summary>
    public const float SquealOnset = 0.78f;

    /// <summary>Where the patch is fully sliding and the stick-slip cycle stops being periodic.</summary>
    public const float SlideOnset = 1.02f;
    public const float FullSlide = 1.45f;

    /// <summary>How much tonal squeal there is, 0..1 — rising to the limit, then given up to the slide.</summary>
    public static float SquealAmount(float demand, float stickSlip = 0f)
    {
        float rise = Smoothstep(SquealOnset, SlideOnset, demand);
        return rise * (1f - SkidAmount(demand, stickSlip));
    }

    /// <summary>How much of the noise is a broadband slide, 0..1. On a surface where the rubber keeps
    /// sticking and slipping (<paramref name="stickSlip"/> 1, <see cref="RoadSurfaces.StickSlipOf"/>)
    /// a full slide stays a squeal.</summary>
    public static float SkidAmount(float demand, float stickSlip = 0f)
        => Smoothstep(SlideOnset, FullSlide, demand) * (1f - Math.Clamp(stickSlip, 0f, 1f));

    /// <summary>How far the squeal note bends, a multiplier on the tyre's resonance: the harder the
    /// patch is worked, the faster each stick-slip cycle, so the note rises toward the limit.</summary>
    public static float SquealPitch(float demand)
        => 1f + 0.22f * Math.Clamp((demand - SquealOnset) / (SlideOnset - SquealOnset), 0f, 1.6f);

    /// <summary>
    /// The extra demand a gear change puts through the driven wheels for an instant, about a tenth of a
    /// second: the engine and wheels meet at speeds the new ratio does not reconcile. It grows with the
    /// ratio step and the torque, so a part-throttle shift does nothing and a full-throttle one-two
    /// chirps the tyres.
    /// </summary>
    /// <param name="ratioStep">Outgoing overall ratio divided by incoming, 1 or more.</param>
    /// <param name="torqueFraction">How much of peak torque is behind it, 0..1.</param>
    public static float ShiftChirp(float ratioStep, float torqueFraction)
    {
        if (ratioStep <= 1.001f) return 0f;
        return MathF.Max(0f, (ratioStep - 1f) * 2.6f * Math.Clamp(torqueFraction, 0f, 1f));
    }

    private static float Smoothstep(float a, float b, float x)
    {
        float t = Math.Clamp((x - a) / MathF.Max(1e-6f, b - a), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}

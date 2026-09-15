using System;

namespace OpenFPS.Common;

/// <summary>
/// How hard a tyre is being asked to work, and what that sounds like.
///
/// A tyre makes two quite different noises, and they come from the same place. Rolling, the tread
/// deforms over the aggregate and the blocks slap the road: broadband roar with a tonal component at
/// the block passing rate. SLIDING, the rubber does something else entirely — a tread element grips,
/// deflects as the contact patch moves under it, lets go, and snaps back at its own resonance. That
/// relaxation oscillation is the squeal, and it is why a tyre at the limit sings a note rather than
/// simply getting louder.
///
/// What decides which of those you hear is one number: how much friction is being DEMANDED against
/// how much the tyre has. Not the speed, not the steering angle, not whether the game thinks a corner
/// is happening — the ratio. Below about eighty per cent of capacity a tyre is quiet. Approaching it,
/// more and more of the contact patch is sliding rather than gripping and the squeal comes up. Past
/// it the patch is fully sliding, the stick-slip cycle loses its regularity, and the note collapses
/// into the broadband roar of a locked wheel.
///
/// That arc is the whole of it: chirp, squeal, skid are not three sounds, they are one continuum
/// sampled at three demands. So this takes accelerations and a grip figure and returns where on the
/// continuum a tyre is, and nothing here knows about cars, corners, racetracks or handbrakes. A
/// spacecraft landing gear skidding on a deck would use the same function.
/// </summary>
public static class TyreFriction
{
    public const float G = 9.80665f;

    /// <summary>
    /// The fraction of available grip being used: 0 is coasting in a straight line, 1 is the limit,
    /// above 1 the tyre is sliding.
    ///
    /// Longitudinal and lateral demand combine as a VECTOR, not a sum — the friction circle. A tyre
    /// at its cornering limit has nothing left for braking, which is why trail-braking into a corner
    /// makes a car let go and why the same tyre can do either alone. That is not a rule anyone has to
    /// write down here; it is what taking the magnitude of the two components does.
    /// </summary>
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
    public static float SquealAmount(float demand)
    {
        float rise = Smoothstep(SquealOnset, SlideOnset, demand);
        return rise * (1f - SkidAmount(demand));
    }

    /// <summary>How much of the noise is a broadband slide, 0..1.</summary>
    public static float SkidAmount(float demand) => Smoothstep(SlideOnset, FullSlide, demand);

    /// <summary>
    /// How far the squeal note bends, as a multiplier on the tyre's resonance.
    ///
    /// The pitch is not fixed. The harder the patch is worked the faster each element completes its
    /// stick-deflect-release cycle, so the note rises as the tyre approaches the limit — the slide up
    /// that everyone recognises as a car being asked for more than it has.
    /// </summary>
    public static float SquealPitch(float demand)
        => 1f + 0.22f * Math.Clamp((demand - SquealOnset) / (SlideOnset - SquealOnset), 0f, 1.6f);

    /// <summary>
    /// The extra demand a gear change puts through the driven wheels for an instant.
    ///
    /// When the clutch comes back in, the engine and the wheels are turning at speeds the new ratio
    /// does not reconcile, and the difference is taken out of the tyres. It is brief — the driveline
    /// resolves it in a tenth of a second — and it is bigger the bigger the ratio step and the more
    /// torque is behind it. A short shift at part throttle does nothing; a full-throttle one-two in
    /// something with a close first gear chirps the tyres, which is the sound a shift kit is bought
    /// for. Derived, so any gearbox anyone builds gets it in proportion.
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

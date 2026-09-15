using System;
using System.Numerics;

namespace OpenFPS.Common;

/// <summary>Where a round ended up, and against what.</summary>
public readonly record struct PelletImpact(
    Vector3 Point,
    Vector3 Normal,
    float Distance,
    int HitEntityId,
    string Material,
    int Damage)
{
    public bool HitSomething => Distance > 0f && HitEntityId != 0 || Material.Length > 0;
}

/// <summary>
/// What one listener hears from one shot, and when.
///
/// Three arrivals, at three different times, from two different places — and keeping them separate is
/// the entire reason this type exists. A game that plays "gunshot.wav at the shooter" throws away the
/// only information a blind player actually has.
/// </summary>
public readonly record struct ShotAudition(
    /// <summary>Seconds from the trigger until the muzzle blast arrives. It comes FROM the shooter.</summary>
    float ReportDelaySeconds,
    /// <summary>Seconds until the round's shock wave arrives, or a negative number when this round
    /// makes no crack for this listener — subsonic, or it did not pass close enough. It comes from
    /// beside the LISTENER, not from the shooter, and it arrives first.</summary>
    float CrackDelaySeconds,
    /// <summary>How far the round passed from the listener's head, metres.</summary>
    float MissDistance,
    /// <summary>The point on the round's path closest to the listener: where the crack is rendered.</summary>
    Vector3 CrackOrigin,
    /// <summary>Seconds until the sound of the round striking something arrives, or negative if it hit
    /// nothing. The impact is at the far end and is often the first clue that a shot MISSED.</summary>
    float ImpactDelaySeconds,
    Vector3 ImpactPoint,
    string ImpactMaterial)
{
    public bool HasCrack => CrackDelaySeconds >= 0f;
    public bool HasImpact => ImpactDelaySeconds >= 0f;

    /// <summary>The gap the listener actually perceives. Zero when there is no crack to compare.</summary>
    public float CrackToReportSeconds =>
        HasCrack ? MathF.Max(0f, ReportDelaySeconds - CrackDelaySeconds) : 0f;
}

/// <summary>
/// Turns a pulled trigger into hits and into arrival times.
///
/// The design decision worth writing down: rounds are HITSCAN, and their travel is modelled in the
/// AUDIO rather than in the simulation.
///
/// A simulated projectile entity would have to be ticked, networked and reconciled, and it would push
/// hit registration behind a 0.2-0.3 s flight across a map this size — which is latency the shooter
/// feels and the server has to arbitrate. Meanwhile the thing that actually needs the flight time is
/// the sound: the crack that reaches a listener as the round passes them, and the report chasing it at
/// the speed of sound. <see cref="Ballistics"/> already computes both exactly. So the hit is resolved
/// on the tick the trigger is pulled, and every listener is then told when each of the three sounds
/// reaches THEM. Hit registration is instant and correct; the audio is physically timed. Neither has
/// to compromise for the other.
///
/// This is not an argument against projectile entities in general — <c>EntityType.Projectile</c> is
/// there, and a grenade or a thrown object wants to be one, because for those the flight IS the event
/// and a player needs to hear it coming. It is an argument about bullets specifically, where the
/// flight is too fast to interact with and too important to fake.
/// </summary>
public static class ShotResolver
{
    /// <summary>
    /// Where a straight shot passes closest to a point, and how far along the path that happens.
    ///
    /// Both numbers matter and they are different: the distance ALONG the path is how long the round
    /// took to get level with the listener, and the MISS distance is how far the shock had to travel
    /// sideways to reach them — and how sharp it is when it does.
    /// </summary>
    public static void ClosestApproach(Vector3 origin, Vector3 direction, float pathLength,
                                       Vector3 listener,
                                       out float alongPath, out float missDistance,
                                       out Vector3 closestPoint)
    {
        Vector3 dir = direction.LengthSquared() > 1e-9f ? Vector3.Normalize(direction) : Vector3.UnitZ;
        float t = Vector3.Dot(listener - origin, dir);
        // Clamped, because a round that stopped in a wall never got level with someone behind it, and
        // a listener behind the muzzle is not passed by anything at all.
        alongPath = Math.Clamp(t, 0f, MathF.Max(0f, pathLength));
        closestPoint = origin + dir * alongPath;
        missDistance = Vector3.Distance(listener, closestPoint);
    }

    /// <summary>
    /// What <paramref name="listener"/> hears from a shot fired along <paramref name="direction"/>
    /// from <paramref name="origin"/>, given where the round stopped.
    ///
    /// <paramref name="impactPoint"/> and <paramref name="impactMaterial"/> describe what it struck;
    /// pass a null material for a round that hit nothing within range.
    /// </summary>
    public static ShotAudition Audition(WeaponDefinition weapon, Vector3 origin, Vector3 direction,
                                        Vector3 impactPoint, string? impactMaterial,
                                        Vector3 listener, float speedOfSound)
    {
        float pathLength = Vector3.Distance(origin, impactPoint);
        float reportDistance = Vector3.Distance(origin, listener);
        float report = Ballistics.ReportArrival(reportDistance, speedOfSound);

        ClosestApproach(origin, direction, pathLength, listener,
                        out float along, out float miss, out Vector3 closest);

        float crack = -1f;
        if (weapon.IsSupersonic(speedOfSound) &&
            Ballistics.MakesCrack(weapon.MuzzleVelocity, miss, speedOfSound) &&
            // A round that has not yet reached the listener's position along its path never passed
            // them: it stopped short, in a wall or a body, and they hear the impact instead.
            along < pathLength - 0.01f)
        {
            crack = Ballistics.CrackArrival(along, miss, weapon.MuzzleVelocity, speedOfSound);
        }

        float impact = -1f;
        if (!string.IsNullOrEmpty(impactMaterial))
        {
            // The round gets there at its own speed; the sound of the strike comes back at the speed
            // of sound from wherever it landed. Both legs count.
            float flight = pathLength / MathF.Max(1f, weapon.MuzzleVelocity);
            impact = flight + Vector3.Distance(impactPoint, listener) / MathF.Max(1f, speedOfSound);
        }

        return new ShotAudition(report, crack, miss, closest, impact, impactPoint, impactMaterial ?? "");
    }

    /// <summary>
    /// Damage one pellet does at a distance. Halves every <c>DamageHalfDistance</c> metres, which is a
    /// smooth curve rather than a cliff and keeps buckshot lethal up close and useless across a street.
    /// </summary>
    public static int DamageAt(WeaponDefinition weapon, float distance)
    {
        if (distance >= weapon.MaxRange) return 0;
        float half = MathF.Max(1f, weapon.DamageHalfDistance);
        float factor = MathF.Pow(0.5f, MathF.Max(0f, distance) / half);
        return Math.Max(0, (int)MathF.Round(weapon.DamagePerPellet * factor));
    }

    /// <summary>
    /// The directions the pellets of one shot actually travel.
    ///
    /// Deterministic from <paramref name="seed"/> so the server and every client agree on where a
    /// shotgun blast went, and so a test can assert on the spread rather than on its average. A single
    /// projectile writes one direction and returns 1; buckshot fills the cone.
    /// </summary>
    public static int PelletDirections(WeaponDefinition weapon, Vector3 direction, int seed,
                                       Span<Vector3> into)
    {
        Vector3 fwd = direction.LengthSquared() > 1e-9f ? Vector3.Normalize(direction) : Vector3.UnitZ;
        int count = Math.Min(weapon.PelletsPerShot, into.Length);
        if (count <= 1 || weapon.SpreadDegrees <= 0f)
        {
            if (into.Length > 0) into[0] = fwd;
            return Math.Min(1, into.Length);
        }

        // A basis across the line of fire. Picking the world axis least parallel to the shot keeps the
        // cross product well conditioned; using a fixed axis makes the spread collapse to a line when
        // someone happens to be shooting along it.
        Vector3 axis = MathF.Abs(fwd.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
        Vector3 right = Vector3.Normalize(Vector3.Cross(axis, fwd));
        Vector3 up = Vector3.Cross(fwd, right);

        var rng = new Random(seed);
        float maxRadians = weapon.SpreadDegrees * MathF.PI / 180f * 0.5f;
        for (int i = 0; i < count; i++)
        {
            // Square-rooted radius, so pellets are spread evenly over the DISC rather than bunched in
            // the middle, which is what a real pattern does and what makes the edges of it matter.
            float r = MathF.Sqrt((float)rng.NextDouble()) * maxRadians;
            float theta = (float)(rng.NextDouble() * Math.PI * 2.0);
            into[i] = Vector3.Normalize(fwd + right * (MathF.Tan(r) * MathF.Cos(theta))
                                            + up * (MathF.Tan(r) * MathF.Sin(theta)));
        }
        return count;
    }
}

using System.Numerics;
using OpenFPS.Materials;

namespace OpenFPS.Physics;

/// <summary>
/// What a round is in flight: its mass, its size, and how fast it is turning end over end. A bullet
/// from the barrel is point first and spin-stable (no tumble); after a ricochet it is a deformed slug,
/// flattened on the side that met the face, tumbling.
/// </summary>
public readonly record struct Slug(float MassKg, float Length, float Width, float Thickness, float TumbleRadPerSec)
{
    public bool Tumbling => TumbleRadPerSec > 0f;

    /// <summary>Its volume, m³: a box of its three sizes with the corners taken off (π/4).</summary>
    public float Volume => MathF.PI / 4f * Length * Width * Thickness;

    /// <summary>kg/m³: a jacketed lead core is about 10,000.</summary>
    public float Density => MassKg / MathF.Max(1e-9f, Volume);

    /// <summary>The mean area it presents tumbling at random: a quarter of its surface, for any convex
    /// body (Cauchy's theorem).</summary>
    public float MeanArea => 0.5f * (Length * Width + Length * Thickness + Width * Thickness);

    /// <summary>Its moment of inertia end over end, kg m².</summary>
    public float PitchInertia => MassKg * (Length * Length + Width * Width) / 12f;

    /// <summary>Across, as the wake sees it: the geometric mean of its two short sides.</summary>
    public float Across => MathF.Sqrt(Width * Thickness);
}

/// <summary>
/// A round striking a face at a grazing angle, and what it does next.
///
/// WHETHER. Below a critical angle off the face a round does not dig in: the face pushes it back out
/// and it leaves, flattened and tumbling. The critical angle is a property of the pair (the face's
/// hardness and yielding, the bullet's construction and speed) and is measured by firing at it; the
/// figures here are the ones forensic reconstruction works with (L. C. Haag, Shooting Incident
/// Reconstruction, 2nd ed., 2011; Haag, "Bullet ricochet from water", AFTE J. 11(3), 1979), as ranges
/// for common handgun and rifle bullets, the middle taken. Soft stuff (grass, wood, plaster, carpet)
/// takes the round in instead, at any angle. Water is worked from the physics: a body skips off water
/// below θc = 18°/√(ρ_body/ρ_water) (G. Birkhoff et al., "Ricochet off water", AMP Memo 42.4M, 1944,
/// confirmed by W. Johnson and S. R. Reid, J. Mech. Eng. Sci. 17, 1975), 5.6° for a lead-cored bullet.
/// Within ±15 % of the critical angle the outcome is a matter of the particular round, and is drawn.
///
/// HOW IT LEAVES. Off a hard, non-yielding face (steel, concrete, stone) the departure angle is LESS
/// than the incidence, a fraction of it; off a yielding one (water, soil) it is greater, the round
/// having ploughed a ramp (Haag). It keeps most of its speed: the loss rises towards the critical
/// angle, to about 35 % there (the figure Haag and the Wikipedia article give for a deflection). It
/// leaves a few degrees to one side or the other of straight on.
///
/// WHAT IT IS AFTERWARDS. Flattened where it met the face (shorter, wider, thinner), lighter by what it
/// left behind, and TUMBLING: the blow's impulse at the end of the slug spins it end over end at
/// I·(L/4)/J. In flight, a tumbling body's drag is a bluff body's (Cd 1.0 subsonic, rising to 1.45
/// supersonic; S. F. Hoerner, Fluid-Dynamic Drag, 1965) on the mean area it presents, several times a
/// bullet's: it slows in tens of metres, and it has no stability to keep it straight. Its tumble
/// relaxes, over a time 4J/(ρvAL²), towards the rate air holds a tumbling flat body at, its tip moving
/// at about 0.3 of its speed (H. J. Lugt, "Autorotation", Annu. Rev. Fluid Mech. 15, 1983).
///
/// WHAT IT SOUNDS LIKE is the sound's: the strike and the whine (Ricochet, in OpenFPS.Sound) read what
/// this leaves.
/// </summary>
public static class RicochetLaw
{
    /// <summary>A face's part, from the material table (AcousticRegistry, aliases and all), or none (the round goes in,
    /// at any angle).</summary>
    public static bool TryFace(string? material, out RicochetFace face)
    {
        face = default;
        if (material == null || AcousticRegistry.GetProperties(material).Ricochet is not { } f) return false;
        face = f;
        return true;
    }

    /// <summary>The critical angle off a face for this slug, radians; zero where a round never skips.</summary>
    public static float CriticalRadians(string? material, Slug slug)
    {
        if (!TryFace(material, out var f)) return 0f;
        float degrees = f.CriticalDegrees >= 0f ? f.CriticalDegrees : 18f / MathF.Sqrt(MathF.Max(1f, slug.Density / 1000f));
        return degrees * MathF.PI / 180f;
    }

    /// <summary>What a ricochet left: where it goes (velocity), what it is now, how much energy it left
    /// in the face, and the angle it struck at.</summary>
    public readonly record struct Outcome(Vector3 Velocity, Slug Slug, float DumpedJoules, float GrazeRadians, float Kept);

    /// <summary>
    /// Whether a round striking <paramref name="material"/> at <paramref name="velocity"/> on a face
    /// whose outward normal is <paramref name="normal"/> ricochets, and if so how it leaves. The
    /// <paramref name="dice"/> decide what a particular round does near the critical angle, its exact
    /// departure and its sideways kick; the same dice give the same ricochet.
    /// </summary>
    public static bool TryBounce(string? material, Vector3 velocity, Vector3 normal, Slug slug, Random dice, out Outcome outcome)
    {
        outcome = default;
        float speed = velocity.Length();
        if (speed < 30f || !TryFace(material, out var face)) return false;
        Vector3 dir = velocity / speed;
        normal = Vector3.Normalize(normal);
        float into = -Vector3.Dot(dir, normal);
        if (into <= 0f) return false;
        float graze = MathF.Asin(Math.Clamp(into, 0f, 1f));
        float critical = CriticalRadians(material, slug);
        float drawn = critical * (0.85f + 0.3f * (float)dice.NextDouble());
        if (graze >= drawn) return false;

        float ratio = graze / MathF.Max(1e-4f, critical);
        float departure = Math.Clamp(face.DepartureRatio * graze * (0.75f + 0.5f * (float)dice.NextDouble()),
                                     0.3f * MathF.PI / 180f, 30f * MathF.PI / 180f);
        float kept = Math.Clamp(1f - face.LossAtCritical * ratio * (0.8f + 0.4f * (float)dice.NextDouble()), 0.3f, 0.97f);

        // Along the face, turned a little to one side, then lifted off it by the departure angle.
        Vector3 along = dir - Vector3.Dot(dir, normal) * normal;
        along = along.LengthSquared() < 1e-10f ? Vector3.Normalize(Vector3.Cross(normal, Vector3.UnitX + new Vector3(0, 0, 1e-3f))) : Vector3.Normalize(along);
        double g = Math.Sqrt(-2 * Math.Log(1 - dice.NextDouble())) * Math.Cos(2 * Math.PI * dice.NextDouble());
        float side = (float)(g * 2.5 * Math.PI / 180);
        along = Vector3.Normalize(Vector3.Transform(along, Quaternion.CreateFromAxisAngle(normal, side)));
        Vector3 leaving = Vector3.Normalize(along * MathF.Cos(departure) + normal * MathF.Sin(departure));
        float speedOut = speed * kept;

        // Flattened on the side that met the face, by how hard and how squarely it met it.
        float squash = face.Flatten * Math.Clamp(0.4f + 2f * MathF.Sin(graze), 0.4f, 1f);
        float mass = slug.MassKg * face.MassKept;
        float length = slug.Length * (1f - 0.15f * squash);
        float thick = slug.Thickness * (1f - 0.3f * squash);
        float width = slug.Width * (1f + 0.3f * squash);
        var after = new Slug(mass, length, width, thick, 0f);
        // The tumble the blow starts: the impulse into the face, at a quarter of the slug's length from
        // its centre, over its moment of inertia; added to any it had.
        float impulse = slug.MassKg * (speed * MathF.Sin(graze) + speedOut * MathF.Sin(departure));
        float spin = impulse * 0.25f * length / after.PitchInertia;
        after = after with { TumbleRadPerSec = MathF.Max(50f, spin + slug.TumbleRadPerSec * 0.5f) };

        float dumped = 0.5f * slug.MassKg * speed * speed - 0.5f * mass * speedOut * speedOut;
        outcome = new Outcome(leaving * speedOut, after, MathF.Max(0f, dumped), graze, kept);
        return true;
    }

    // ── A tumbling slug in flight ───────────────────────────────────────────────────────────────

    /// <summary>A tumbling body's drag coefficient on its mean presented area: 1.0 subsonic, rising
    /// through the transonic to 1.45 (Hoerner).</summary>
    public static float TumblingDrag(float mach)
    {
        if (mach <= 0.8f) return 1.0f;
        if (mach >= 1.2f) return 1.45f;
        return 1.0f + 0.45f * (mach - 0.8f) / 0.4f;
    }

    public static Vector3 Acceleration(Vector3 velocity, Vector3 wind, Slug slug, Air air, float gravity = Earth.StandardGravity)
    {
        Vector3 through = velocity - wind;
        float speed = through.Length();
        float k = 0.5f * air.Density * TumblingDrag(speed / air.SpeedOfSound) * slug.MeanArea / MathF.Max(1e-5f, slug.MassKg);
        return -k * speed * through + new Vector3(0f, -gravity, 0f);
    }

    /// <summary>Flies a tumbling slug on, as <see cref="ExternalBallistics.Advance"/> flies a bullet.</summary>
    public static void Advance(ref BulletState s, float seconds, Slug slug, Air air, Vector3 wind, float gravity = Earth.StandardGravity)
    {
        float left = seconds;
        while (left > 1e-7f)
        {
            float h = MathF.Min(ExternalBallistics.StepSeconds, left);
            Vector3 a0 = Acceleration(s.Velocity, wind, slug, air, gravity);
            Vector3 v1 = s.Velocity + a0 * h;
            Vector3 a1 = Acceleration(v1, wind, slug, air, gravity);
            Vector3 v = s.Velocity + 0.5f * (a0 + a1) * h;
            Vector3 step = 0.5f * (s.Velocity + v) * h;
            s.Position += step;
            s.Travelled += step.Length();
            s.Velocity = v;
            s.Seconds += h;
            left -= h;
        }
    }
}

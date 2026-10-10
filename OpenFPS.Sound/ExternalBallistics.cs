using System.Collections.Concurrent;
using System.Numerics;

namespace OpenFPS.Common;

/// <summary>The air a bullet flies through: what sets its drag (density) and where the drag rises
/// steeply (the speed of sound).</summary>
public readonly record struct Air(float TemperatureC, float PressureMb)
{
    /// <summary>The ICAO standard day at sea level, 15 °C and 1013.25 mb: what published tables assume
    /// and what a rifle is zeroed in.</summary>
    public static readonly Air Standard = new(15f, 1013.25f);

    /// <summary>Dry air, kg/m³. Humidity moves it by under one per cent and is left out.</summary>
    public float Density => PressureMb * 100f / (287.05f * (TemperatureC + 273.15f));

    public float SpeedOfSound => 331.3f * MathF.Sqrt(1f + TemperatureC / 273.15f);
}

/// <summary>A bullet in flight: where it is, how fast it is going, and for how long it has flown.</summary>
public struct BulletState
{
    public Vector3 Position;
    public Vector3 Velocity;
    public float Seconds;
    /// <summary>Path length so far, metres.</summary>
    public float Travelled;
}

/// <summary>Where a bullet is when it has gone so far down the line of sight: below it (negative
/// height) and to the side of it (drift, positive to the right), in metres, with its time and speed.</summary>
public readonly record struct FlightPoint(float Range, float Height, float Drift, float Seconds, float Speed);

/// <summary>
/// What a bullet does after it leaves the barrel: it slows, it falls, and the wind carries it.
///
/// A point mass under gravity and drag, the model every published drop table is computed with. Drag is
/// the G7 standard projectile's drag coefficient at the bullet's Mach number, scaled by the bullet's own
/// ballistic coefficient: retardation = (π/8)·ρ·v²·Cd7(M) / BC, with BC converted from lb/in² to kg/m².
/// The wind enters as the air's own velocity, so the drag acts against the bullet's speed THROUGH the
/// air, which is all that wind drift is: a crosswind gives the bullet a sideways airspeed that the drag
/// spends turning into a sideways velocity, a little at a time, for as long as it is in flight.
///
/// Gravity is the real one: a player who learns where a .308 lands at 600 metres here has learned the
/// real drop. Pure and deterministic, so the server flies the authoritative bullet with it and the
/// client works out its turret's zero with the same numbers.
/// </summary>
public static class ExternalBallistics
{
    public const float Gravity = 9.80665f;

    /// <summary>The integration step, seconds. Half a millisecond moves a rifle bullet 40 cm; the
    /// second-order step below is then accurate to well under a millimetre of drop at 600 m.</summary>
    public const float StepSeconds = 0.0005f;

    /// <summary>One lb/in² in kg/m²: the unit ballistic coefficients are published in.</summary>
    private const float PoundsPerSquareInch = 703.0696f;

    /// <summary>What a round flies with when it has no coefficient of its own: a round-nose pistol
    /// bullet's, so a gun that was never meant to reach far does not.</summary>
    public const float FallbackG7 = 0.1f;

    // The G7 standard drag function, Cd against Mach: the table published with the standard (the one
    // JBM, Applied Ballistics and every other solver interpolates).
    private static readonly float[] Mach =
    {
        0.00f, 0.05f, 0.10f, 0.15f, 0.20f, 0.25f, 0.30f, 0.35f, 0.40f, 0.45f, 0.50f, 0.55f, 0.60f, 0.65f,
        0.70f, 0.725f, 0.75f, 0.775f, 0.80f, 0.825f, 0.85f, 0.875f, 0.90f, 0.925f, 0.95f, 0.975f, 1.00f,
        1.025f, 1.05f, 1.075f, 1.10f, 1.125f, 1.15f, 1.20f, 1.25f, 1.30f, 1.35f, 1.40f, 1.50f, 1.55f,
        1.60f, 1.65f, 1.70f, 1.75f, 1.80f, 1.85f, 1.90f, 1.95f, 2.00f, 2.05f, 2.10f, 2.15f, 2.20f, 2.25f,
        2.30f, 2.35f, 2.40f, 2.45f, 2.50f, 2.55f, 2.60f, 2.65f, 2.70f, 2.75f, 2.80f, 2.85f, 2.90f, 2.95f,
        3.00f, 3.10f, 3.20f, 3.30f, 3.40f, 3.50f, 3.60f, 3.70f, 3.80f, 3.90f, 4.00f, 4.20f, 4.40f, 4.60f,
        4.80f, 5.00f,
    };
    private static readonly float[] Cd =
    {
        0.1198f, 0.1197f, 0.1196f, 0.1194f, 0.1193f, 0.1194f, 0.1194f, 0.1194f, 0.1193f, 0.1193f, 0.1194f,
        0.1193f, 0.1194f, 0.1197f, 0.1202f, 0.1207f, 0.1215f, 0.1226f, 0.1242f, 0.1266f, 0.1306f, 0.1368f,
        0.1464f, 0.1660f, 0.2054f, 0.2993f, 0.3803f, 0.4015f, 0.4043f, 0.4034f, 0.4014f, 0.3987f, 0.3955f,
        0.3884f, 0.3810f, 0.3732f, 0.3657f, 0.3580f, 0.3440f, 0.3376f, 0.3315f, 0.3260f, 0.3209f, 0.3160f,
        0.3117f, 0.3078f, 0.3042f, 0.3010f, 0.2980f, 0.2951f, 0.2922f, 0.2892f, 0.2864f, 0.2835f, 0.2807f,
        0.2779f, 0.2752f, 0.2725f, 0.2697f, 0.2670f, 0.2643f, 0.2615f, 0.2588f, 0.2561f, 0.2533f, 0.2506f,
        0.2479f, 0.2451f, 0.2424f, 0.2368f, 0.2313f, 0.2258f, 0.2205f, 0.2154f, 0.2106f, 0.2060f, 0.2017f,
        0.1975f, 0.1935f, 0.1861f, 0.1793f, 0.1730f, 0.1672f, 0.1618f,
    };

    /// <summary>The G7 drag coefficient at a Mach number, interpolated in the standard table.</summary>
    public static float DragCoefficientG7(float mach)
    {
        if (mach <= Mach[0]) return Cd[0];
        if (mach >= Mach[^1]) return Cd[^1];
        int hi = 1;
        while (Mach[hi] < mach) hi++;
        float t = (mach - Mach[hi - 1]) / (Mach[hi] - Mach[hi - 1]);
        return Cd[hi - 1] + t * (Cd[hi] - Cd[hi - 1]);
    }

    /// <summary>The coefficient a weapon's bullet flies with.</summary>
    public static float CoefficientOf(WeaponDefinition w) => w.BallisticCoefficientG7 > 0f ? w.BallisticCoefficientG7 : FallbackG7;

    /// <summary>Drag and gravity together, m/s², for a bullet at <paramref name="velocity"/> in air
    /// moving at <paramref name="wind"/>.</summary>
    public static Vector3 Acceleration(Vector3 velocity, Vector3 wind, float bcG7, Air air)
    {
        Vector3 through = velocity - wind;
        float speed = through.Length();
        float k = MathF.PI / 8f * air.Density * DragCoefficientG7(speed / air.SpeedOfSound) / (bcG7 * PoundsPerSquareInch);
        return -k * speed * through + new Vector3(0f, -Gravity, 0f);
    }

    /// <summary>Flies a bullet on for <paramref name="seconds"/>, in steps of <see cref="StepSeconds"/>
    /// (Heun's method: a step at the start's slope, corrected by the slope where it lands).</summary>
    public static void Advance(ref BulletState s, float seconds, float bcG7, Air air, Vector3 wind)
    {
        float left = seconds;
        while (left > 1e-7f)
        {
            float h = MathF.Min(StepSeconds, left);
            Vector3 a0 = Acceleration(s.Velocity, wind, bcG7, air);
            Vector3 v1 = s.Velocity + a0 * h;
            Vector3 a1 = Acceleration(v1, wind, bcG7, air);
            Vector3 v = s.Velocity + 0.5f * (a0 + a1) * h;
            Vector3 step = 0.5f * (s.Velocity + v) * h;
            s.Position += step;
            s.Travelled += step.Length();
            s.Velocity = v;
            s.Seconds += h;
            left -= h;
        }
    }

    /// <summary>
    /// The barrel's direction for a line of sight and an angle above it: the line of sight turned up
    /// by <paramref name="angleAboveSight"/> radians in its own vertical plane, so a shot down off a
    /// roof is raised along the slope it is fired down, as a scope's turret raises it.
    /// </summary>
    public static Vector3 Raise(Vector3 lineOfSight, float angleAboveSight)
    {
        Vector3 los = Vector3.Normalize(lineOfSight);
        Vector3 right = Vector3.Cross(Vector3.UnitY, los);
        if (right.LengthSquared() < 1e-8f) right = Vector3.UnitX;   // straight up or down: any axis will do
        right = Vector3.Normalize(right);
        Vector3 up = Vector3.Cross(los, right);
        return Vector3.Normalize(los * MathF.Cos(angleAboveSight) + up * MathF.Sin(angleAboveSight));
    }

    /// <summary>
    /// Flies a round down a level line of sight (along +Z, up +Y, right +X) from a barrel
    /// <paramref name="sightHeight"/> metres under the sight, raised <paramref name="angle"/> radians
    /// above it, and says where it is when it has gone <paramref name="range"/> metres downrange.
    /// </summary>
    public static FlightPoint Fly(WeaponDefinition w, float range, float angle, float sightHeight, Air air, Vector3 wind)
    {
        var s = new BulletState
        {
            Position = new Vector3(0f, -sightHeight, 0f),
            Velocity = w.MuzzleVelocity * new Vector3(0f, MathF.Sin(angle), MathF.Cos(angle)),
        };
        float bc = CoefficientOf(w);
        while (s.Position.Z < range && s.Seconds < 10f && s.Velocity.Z > 1f)
        {
            var before = s;
            Advance(ref s, StepSeconds, bc, air, wind);
            if (s.Position.Z >= range)
            {
                float t = (range - before.Position.Z) / MathF.Max(1e-6f, s.Position.Z - before.Position.Z);
                var p = Vector3.Lerp(before.Position, s.Position, t);
                float speed = MathHelper.Lerp(before.Velocity.Length(), s.Velocity.Length(), t);
                return new FlightPoint(range, p.Y, p.X, before.Seconds + t * (s.Seconds - before.Seconds), speed);
            }
        }
        return new FlightPoint(s.Position.Z, s.Position.Y, s.Position.X, s.Seconds, s.Velocity.Length());
    }

    private static readonly ConcurrentDictionary<(float Mv, float Bc, int Decimetres, int Mm), float> _zeroCache = new();

    /// <summary>
    /// The angle above the line of sight, radians, that puts the bullet back on the line of sight
    /// <paramref name="zeroMetres"/> downrange: what a rifle is zeroed at. On a standard day in still
    /// air, because that is the day a zero is set on and the turret then holds that angle whatever the
    /// day turns out to be.
    /// </summary>
    public static float ZeroAngle(WeaponDefinition w, float zeroMetres, float sightHeight)
    {
        zeroMetres = Math.Clamp(zeroMetres, 10f, 3000f);
        var key = (w.MuzzleVelocity, CoefficientOf(w), (int)MathF.Round(zeroMetres * 10f), (int)MathF.Round(sightHeight * 1000f));
        if (_zeroCache.TryGetValue(key, out float cached)) return cached;
        // The height at the zero rises with the angle, so the angle is found by halving.
        float lo = -0.01f, hi = 0.15f;
        for (int i = 0; i < 40; i++)
        {
            float mid = 0.5f * (lo + hi);
            if (Fly(w, zeroMetres, mid, sightHeight, Air.Standard, Vector3.Zero).Height > 0f) hi = mid; else lo = mid;
        }
        float angle = 0.5f * (lo + hi);
        _zeroCache[key] = angle;
        return angle;
    }

    /// <summary>
    /// Where a barrel raised <paramref name="angle"/> above the sight brings the bullet back down
    /// through the line of sight: the distance a turret setting is zeroed for. Null when it never
    /// comes back within <paramref name="maxMetres"/>, or never rises to the line at all.
    /// </summary>
    public static float? ZeroDistanceFor(WeaponDefinition w, float angle, float sightHeight, float maxMetres = 2000f)
    {
        var s = new BulletState
        {
            Position = new Vector3(0f, -sightHeight, 0f),
            Velocity = w.MuzzleVelocity * new Vector3(0f, MathF.Sin(angle), MathF.Cos(angle)),
        };
        float bc = CoefficientOf(w);
        bool above = false;
        while (s.Position.Z < maxMetres && s.Seconds < 10f)
        {
            var before = s;
            Advance(ref s, StepSeconds, bc, Air.Standard, Vector3.Zero);
            if (s.Position.Y > 0f) above = true;
            else if (above && before.Position.Y > 0f)
            {
                float t = before.Position.Y / MathF.Max(1e-9f, before.Position.Y - s.Position.Y);
                return before.Position.Z + t * (s.Position.Z - before.Position.Z);
            }
            // Falling away below the line before it ever rose to it: no distance is zeroed.
            if (!above && s.Velocity.Y < 0f) return null;
        }
        return null;
    }

    /// <summary>The elevation, milliradians, to dial over a zero at <paramref name="baseZeroMetres"/>
    /// to be zeroed at <paramref name="metres"/> instead: the come-up a dope card lists.</summary>
    public static float ComeUpMil(WeaponDefinition w, float metres, float baseZeroMetres, float sightHeight)
        => 1000f * (ZeroAngle(w, metres, sightHeight) - ZeroAngle(w, baseZeroMetres, sightHeight));

    /// <summary>
    /// Whether a bullet moving from <paramref name="from"/> to <paramref name="to"/> over
    /// <paramref name="seconds"/> passes through a body standing at <paramref name="feet"/> and moving
    /// at <paramref name="bodyVelocity"/> over the same interval: an upright cylinder of
    /// <paramref name="radius"/> and <paramref name="height"/>.
    ///
    /// Done in the body's own frame, where it stands still and the bullet's path is shifted back by how
    /// far the body moved: that is what makes a walking target need leading. <paramref name="fraction"/>
    /// is how far along the segment it struck, and
    /// <paramref name="heightOnBody"/> how high above the feet.
    /// </summary>
    public static bool SegmentHitsBody(Vector3 from, Vector3 to, float seconds, Vector3 feet, Vector3 bodyVelocity,
                                       float radius, float height, out float fraction, out float heightOnBody)
    {
        fraction = 0f; heightOnBody = 0f;
        Vector3 a = from - feet;
        Vector3 b = to - (feet + bodyVelocity * seconds);
        Vector3 d = b - a;

        // Across the ground: when is the path inside the circle?
        float A = d.X * d.X + d.Z * d.Z;
        float B = 2f * (a.X * d.X + a.Z * d.Z);
        float C = a.X * a.X + a.Z * a.Z - radius * radius;
        float s0, s1;
        if (A < 1e-12f)
        {
            if (C > 0f) return false;
            s0 = 0f; s1 = 1f;
        }
        else
        {
            float disc = B * B - 4f * A * C;
            if (disc < 0f) return false;
            float root = MathF.Sqrt(disc);
            s0 = (-B - root) / (2f * A);
            s1 = (-B + root) / (2f * A);
        }
        s0 = MathF.Max(s0, 0f);
        s1 = MathF.Min(s1, 1f);
        if (s0 > s1) return false;

        // ...and up the body: when is it between the feet and the top of the head?
        float y0, y1;
        if (MathF.Abs(d.Y) < 1e-9f)
        {
            if (a.Y < 0f || a.Y > height) return false;
            y0 = 0f; y1 = 1f;
        }
        else
        {
            float ta = (0f - a.Y) / d.Y, tb = (height - a.Y) / d.Y;
            y0 = MathF.Min(ta, tb); y1 = MathF.Max(ta, tb);
        }
        float first = MathF.Max(s0, y0), last = MathF.Min(s1, y1);
        if (first > last) return false;
        fraction = first;
        heightOnBody = a.Y + first * d.Y;
        return true;
    }

    /// <summary>The radius of the upright cylinder a person is hit as: the 45 cm torso, halved.</summary>
    public const float BodyRadius = WeaponDefinition.BodyWidthMetres * 0.5f;
    /// <summary>A person's height, to the top of the head.</summary>
    public const float BodyHeight = BodyConstants.PersonHeight;
    /// <summary>The top of a person that is the head: above this, a hit is a head shot.</summary>
    public const float HeadFrom = 1.55f;
}

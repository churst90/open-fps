using System;

namespace OpenFPS.Common;

/// <summary>
/// The wind over the map: how fast the air is moving at a place and a moment.
///
/// One field, read by everything the wind moves — the leaves on a tree, the flames of a fire, the
/// spray off a fountain — so a gust is ONE event that arrives at each of them in turn rather than a
/// random wobble each of them makes up for itself. That is the whole reason it is a field and not a
/// per-source noise generator: stand in a park and a gust is heard coming, through the trees upwind
/// first, then the ones round you, then away downwind.
///
/// THE MODEL. Near the ground the wind is a mean speed with turbulence on top of it. The mean grows
/// with height as the log of it (the surface layer: u(z) = u* / κ ln(z / z0)), so a fire on the
/// ground feels about half of what the crown of a tree does. The turbulence is a set of eddies
/// carried along by the mean flow (Taylor's frozen turbulence), so what a place feels at time t is
/// what a place upwind of it felt a little earlier: the gust signal is a function of
/// t − x·d / U, not of t alone. Its size is the turbulence intensity, the standard deviation over
/// the mean, which near the ground in open country with trees is about 0.2 to 0.35; its timescales
/// run from tens of seconds (the big gusts you notice) down to a second or two (the flutter inside
/// one), with less energy in the short ones, as a Kolmogorov spectrum has.
///
/// DETERMINISTIC. The gusts are hashed from a lattice in (time, cross-wind position), so two
/// clients asking about the same place and second get the same answer, and a test can hold it.
/// The clock is the caller's: UTC seconds keep clients together.
/// </summary>
public static class WindField
{
    /// <summary>The mean wind at ten metres, m/s. A moderate breeze — Beaufort 3, leaves and small
    /// twigs in constant motion — is 3.4 to 5.4.</summary>
    public static float MeanSpeed { get; set; } = 4.5f;

    /// <summary>Where it blows FROM, degrees clockwise from north (the way a forecast says it).
    /// West-south-west, the prevailing wind over most of the temperate world.</summary>
    public static float FromDegrees { get; set; } = 250f;

    /// <summary>Standard deviation of the speed over its mean.</summary>
    public static float Turbulence { get; set; } = 0.3f;

    /// <summary>The roughness length of the ground, m: about 0.1 for open grass with scattered trees,
    /// 0.5 to 1 for a suburb. It sets how fast the wind falls off toward the ground.</summary>
    public const float RoughnessMetres = 0.15f;

    /// <summary>Lateral size of a gust, m: eddies at this height are coherent over tens of metres
    /// across the wind and longer along it.</summary>
    public const float GustWidthMetres = 60f;

    /// <summary>The eddies, longest first: (timescale s, share of the variance). Roughly a −5/3 law:
    /// most of the variance in the slow gusts.</summary>
    private static readonly (float Seconds, float Weight)[] Eddies =
    {
        (40f, 1.0f), (16f, 0.75f), (6.5f, 0.5f), (2.6f, 0.3f), (1.1f, 0.16f),
    };

    private static readonly float Norm;

    static WindField()
    {
        // Value noise in two dimensions with cubic easing, lattice values uniform in [-1, 1], has a
        // variance of 0.184 (1/3 times 0.743 squared, the easing's loss per dimension, measured);
        // summing the eddies adds their variances.
        double v = 0;
        foreach (var (_, w) in Eddies) v += w * w * 0.184;
        Norm = (float)(1.0 / Math.Sqrt(v));
    }

    /// <summary>The unit vector the air moves TOWARD, in the map's x (east), z (north).</summary>
    public static (float X, float Z) Downwind
    {
        get
        {
            float a = FromDegrees * MathF.PI / 180f;
            // From the west (270) blows toward +x.
            return (-MathF.Sin(a), -MathF.Cos(a));
        }
    }

    /// <summary>The mean speed at a height, by the log law, m/s. Never below a tenth of the
    /// ten-metre speed, because the law runs to zero at the roughness height and air does not.</summary>
    public static float MeanAt(float heightMetres)
    {
        float z = MathF.Max(RoughnessMetres * 1.5f, heightMetres);
        float share = MathF.Log(z / RoughnessMetres) / MathF.Log(10f / RoughnessMetres);
        return MeanSpeed * MathF.Max(0.1f, share);
    }

    /// <summary>
    /// The gust signal at a place and moment: zero mean, unit standard deviation. Positive is a gust,
    /// negative a lull. Exposed so a test can check that a gust travels.
    /// </summary>
    public static float Gust(float x, float z, double seconds)
    {
        var (dx, dz) = Downwind;
        float along = x * dx + z * dz;
        float across = -x * dz + z * dx;
        float carry = MathF.Max(0.5f, MeanSpeed);
        // What arrives here now left a place `along` metres upwind of the origin along/U seconds ago.
        double t = seconds - along / carry;
        float sum = 0f;
        for (int k = 0; k < Eddies.Length; k++)
        {
            var (period, weight) = Eddies[k];
            sum += weight * Noise2(t / period, across / GustWidthMetres * (period / 40f + 0.5f), k);
        }
        return sum * Norm;
    }

    /// <summary>The wind speed at a place, m/s: the mean at that height times one plus the
    /// turbulence, never stopping entirely (a lull in a breeze is still air moving).</summary>
    public static float SpeedAt(float x, float heightMetres, float z, double seconds)
    {
        float mean = MeanAt(heightMetres);
        return mean * MathF.Max(0.12f, 1f + Turbulence * Gust(x, z, seconds));
    }

    /// <summary>The clock every voice reads the field with: UTC seconds, folded to keep a float's
    /// precision useful. Clients agree on it to within their clocks.</summary>
    public static double Now() => (DateTime.UtcNow - new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;

    // ── Value noise ────────────────────────────────────────────────────────────────────────────

    /// <summary>Time in double: at today's clock a float has 2 s resolution, which is a staircase.</summary>
    private static float Noise2(double u, float v, int layer)
    {
        double fu = Math.Floor(u);
        float fv = MathF.Floor(v);
        int iu = (int)(long)fu, iv = (int)fv;
        float tu = (float)(u - fu), tv = v - fv;
        tu = tu * tu * (3f - 2f * tu);
        tv = tv * tv * (3f - 2f * tv);
        float a = Lattice(iu, iv, layer), b = Lattice(iu + 1, iv, layer);
        float c = Lattice(iu, iv + 1, layer), d = Lattice(iu + 1, iv + 1, layer);
        float top = a + (b - a) * tu, bottom = c + (d - c) * tu;
        return top + (bottom - top) * tv;
    }

    private static float Lattice(int i, int j, int layer)
    {
        uint h = (uint)i * 0x9E3779B1u ^ (uint)j * 0x85EBCA77u ^ (uint)layer * 0xC2B2AE3Du;
        h ^= h >> 15; h *= 0x2C1B3C6Du; h ^= h >> 12; h *= 0x297A2D39u; h ^= h >> 15;
        return (h & 0xFFFFFF) / (float)0x7FFFFF - 1f;
    }
}

using OpenFPS.Physics;

namespace OpenFPS.Environment;

// The waves a wind raises over water, and the shape of the water it raises them over: the sea state, which
// the shores' sound reads (ShoreSpec in Waves.cs, ShoreSynth). See docs/WAVES_AND_SHORES.md section 1.

/// <summary>The shape of the water the waves are raised over, which decides how the fetch follows the
/// wind's direction.</summary>
public enum WaterBody
{
    /// <summary>A pond or a lake: about as wide one way as another. A wind at an angle φ to the line
    /// straight out from the shore has the chord of a circle to blow over, F cos φ.</summary>
    Round,
    /// <summary>A river or a canal: a strip, its width straight out from the bank. A wind at an angle
    /// blows over the strip slantwise, F / cos φ, until the reach runs out.</summary>
    Strip,
    /// <summary>The open sea: the fetch is the spec's, whatever the angle, and the swell comes whatever
    /// the wind.</summary>
    Open,
}

/// <summary>
/// Wind waves: how high and how long the waves a wind raises over a fetch are, and what they do at a
/// beach. The engineering relations every coastal and lake study uses (docs/WAVES_AND_SHORES.md
/// section 1).
/// </summary>
public static class WindWaves
{
    public const float Gravity = Earth.StandardGravity;

    /// <summary>Under this wind (m/s at 10 m) the water stays glassy: wind waves need a friction velocity
    /// of a few centimetres a second to start (Kahma and Donelan 1988).</summary>
    public const float OnsetWind = 1.5f;

    /// <summary>The ten-metre drag coefficient, C_D = 0.001 (1.1 + 0.035 U10) (CEM II-2, after Wu 1980).</summary>
    public static float DragCoefficient(float u10) => 0.001f * (1.1f + 0.035f * MathF.Max(0f, u10));

    /// <summary>u* = √C_D U10, m/s.</summary>
    public static float FrictionVelocity(float u10) => MathF.Sqrt(DragCoefficient(u10)) * MathF.Max(0f, u10);

    /// <summary>
    /// Fetch-limited growth in deep water, JONSWAP's own laws in the ten-metre wind (Hasselmann et al.
    /// 1973, eqs. 2.4.3 and 2.4.7, χ = g F / U²): g Hm0 / U² = 1.6e-3 χ^½ and g Tp / U = 0.286 χ^0.33, held
    /// to the fully developed sea of the Coastal Engineering Manual (Part II-2, eq. II-2-37: g Hm0 / u*² =
    /// 211.5, g Tp / u* = 239.8). The CEM's own growth laws in u* (II-2-36) give a period a quarter
    /// shorter at a lake's fetch, so waves twice as steep, and steepness decides how they break; the
    /// original is kept. In shallow water held to Young and Verhagen's (1996) depth limits as well. Below
    /// the onset the water is flat; the height ramps in over the metre a second above it.
    /// </summary>
    public static (float Hm0, float Tp) FetchLimited(float u10, float fetchMetres, float depthMetres = float.PositiveInfinity)
    {
        if (u10 <= OnsetWind || fetchMetres <= 0f) return (0f, 0f);
        float us = FrictionVelocity(u10);
        float g = Gravity, u2 = u10 * u10;
        float chi = g * MathF.Max(1f, fetchMetres) / u2;
        float h = MathF.Min(1.6e-3f * MathF.Sqrt(chi) * u2 / g, 211.5f * us * us / g);
        float t = MathF.Min(0.286f * MathF.Pow(chi, 0.33f) * u10 / g, 239.8f * us / g);
        if (float.IsFinite(depthMetres) && depthMetres > 0f)
        {
            float d = g * depthMetres / (u10 * u10);
            float hMax = 0.241f * MathF.Pow(MathF.Tanh(0.493f * MathF.Pow(d, 0.75f)), 0.87f) * u10 * u10 / g;
            float tMax = 7.519f * MathF.Pow(MathF.Tanh(0.331f * MathF.Pow(d, 1.01f)), 0.37f) * u10 / g;
            h = MathF.Min(h, hMax);
            t = MathF.Min(t, tMax);
        }
        float ramp = Math.Clamp(u10 - OnsetWind, 0f, 1f);
        return (h * ramp, t);
    }

    /// <summary>How long a wind must blow for its sea to reach the fetch-limited state, s: t = 77.23
    /// F^0.67 / (U^0.34 g^0.33), U the wind speed (CEM II-2-35). The sea follows the wind with about a
    /// third of this.</summary>
    public static float GrowthSeconds(float u10, float fetchMetres)
        => 77.23f * MathF.Pow(MathF.Max(1f, fetchMetres), 0.67f) / (MathF.Pow(MathF.Max(u10, OnsetWind + 0.5f), 0.34f) * MathF.Pow(Gravity, 0.33f));

    /// <summary>The deep-water wavelength for a period, m: g T² / 2π.</summary>
    public static float DeepWavelength(float periodSeconds) => Gravity * periodSeconds * periodSeconds / (2f * MathF.PI);

    /// <summary>The Iribarren (surf similarity) number ξ0 = tan β / √(H0 / L0) (Battjes 1974): under 0.5
    /// a wave spills, 0.5-3.3 it plunges, over 3.3 it collapses or surges up the slope unbroken.</summary>
    public static float Iribarren(float slope, float heightMetres, float periodSeconds)
    {
        float l0 = DeepWavelength(MathF.Max(0.1f, periodSeconds));
        return MathF.Max(0f, slope) / MathF.Sqrt(MathF.Max(1e-5f, heightMetres) / l0);
    }

    /// <summary>The height a wave breaks at, m: Komar and Gaughan (1972), Hb = 0.39 g^⅕ (T H0²)^⅖.</summary>
    public static float BreakerHeight(float heightMetres, float periodSeconds)
        => 0.39f * MathF.Pow(Gravity, 0.2f) * MathF.Pow(MathF.Max(1e-6f, periodSeconds * heightMetres * heightMetres), 0.4f);

    /// <summary>The depth it breaks in, m: Hb / 0.78 (McCowan 1894).</summary>
    public static float BreakerDepth(float breakerHeight) => breakerHeight / 0.78f;

    /// <summary>How far up the slope the swash runs, m (vertically): Hunt (1959), R = ξ0 H0 while the wave
    /// breaks, and no more than about 2.3 H0 when it surges unbroken (Battjes 1974).</summary>
    public static float RunUp(float slope, float heightMetres, float periodSeconds)
        => heightMetres * MathF.Min(Iribarren(slope, heightMetres, periodSeconds), 2.3f);

    /// <summary>
    /// The share of the dominant waves that break in open water (whitecaps), from the peak's steepness
    /// ε = Hp kp / 2, Hp four times the root of the variance between 0.7 and 1.3 fp: 0.87 Hs for a JONSWAP
    /// sea of γ 3.3. b = 22 (ε − 0.055)^2.01 over the threshold of 0.055 (Banner, Babanin and Young 2000;
    /// the threshold read in the paper, the fit's coefficients as commonly quoted). No whitecaps under
    /// 3 m/s (Monahan and Ó Muircheartaigh 1980).
    /// </summary>
    public static float BreakingProbability(float hs, float tp, float u10)
    {
        if (hs <= 0f || tp <= 0f || u10 < 3f) return 0f;
        float kp = 2f * MathF.PI / DeepWavelength(tp);
        float eps = 0.87f * hs * kp / 2f;
        return eps <= 0.055f ? 0f : MathF.Min(0.5f, 22f * MathF.Pow(eps - 0.055f, 2.01f));
    }

    /// <summary>Under this wind (m/s at 10 m) open water has no whitecaps: Monahan and Ó Muircheartaigh (1980) find none
    /// under about 3 m/s, Beaufort 2.</summary>
    public const float WhitecapOnsetWind = 3f;

    /// <summary>The wind's drift at the water's surface as a share of the ten-metre wind: about 3 per cent (Wu 1975, "Wind-
    /// induced drift currents", J. Fluid Mech. 68, 49-70; recalled, the figure every drift study since quotes).</summary>
    public const float DriftShare = 0.03f;

    /// <summary>
    /// The share of the dominant waves breaking where the wind at the water is <paramref name="uHere"/> m/s now and its
    /// mean, which raised the sea, <paramref name="uMean"/>: <see cref="BreakingProbability"/> on the sea's own steepness,
    /// that steepness taken as the gust or the lull makes it count. A wind drift q at the surface lowers the highest crest
    /// a wave can carry before it breaks to (c − q)² / 2g, c its phase speed (Banner and Phillips 1974, "On the incipient
    /// breaking of small scale waves", J. Fluid Mech. 65, 647-656), so a crest breaks as one ((c − q̄) / (c − q))² as
    /// steep would in the mean wind whose drift q̄ Banner, Babanin and Young's fit already holds. The drift follows the
    /// wind at <see cref="DriftShare"/>: a gust crossing the water breaks more of the crests it finds; a lull fewer. Under
    /// <see cref="WhitecapOnsetWind"/> here, none, and over the metre a second above it they come in smoothly (Monahan
    /// and Ó Muircheartaigh's coverage is a few millionths there): nothing switches on at a wind.
    /// </summary>
    public static float GustBreaking(float hs, float tp, float uMean, float uHere)
    {
        if (hs <= 0f || tp <= 0f || uHere <= WhitecapOnsetWind) return 0f;
        float x = MathF.Min(1f, uHere - WhitecapOnsetWind), onset = x * x * (3f - 2f * x);
        float c = Gravity * tp / (2f * MathF.PI);
        float qMean = MathF.Min(0.8f * c, DriftShare * MathF.Max(0f, uMean));
        float qHere = MathF.Min(0.8f * c, DriftShare * MathF.Max(0f, uHere));
        float k = (c - qMean) / (c - qHere);
        float kp = 2f * MathF.PI / DeepWavelength(tp);
        float eps = 0.87f * hs * kp / 2f * k * k;
        return eps <= 0.055f ? 0f : onset * MathF.Min(0.5f, 22f * MathF.Pow(eps - 0.055f, 2.01f));
    }

    /// <summary>
    /// What a belt of reeds leaves of a wave, H / H0, after <paramref name="beltMetres"/>: Dalrymple et
    /// al.'s (1984) vegetation decay H/H0 = 1 / (1 + β̃ x), its coefficient β̃ going with the wave height
    /// (drag on the stems goes as the orbital velocity squared). Taken as β̃ = 4 H0 per m² for a dense
    /// emergent stand of rushes, an estimate within the range Mendez and Losada (2004) fit.
    /// </summary>
    public static float ReedDamping(float beltMetres, float heightMetres)
        => beltMetres <= 0f ? 1f : 1f / (1f + 4f * MathF.Max(0f, heightMetres) * beltMetres);
}

using System;

namespace OpenFPS.Common;

/// <summary>How hard it is raining, by the usual classes of rain rate.</summary>
public enum RainCategory { None, Light, Moderate, Heavy, Violent }

/// <summary>
/// Rain as the thing it is: a rate of water arriving, carried by drops of a spread of sizes falling at
/// the speed their size allows. Everything heard of rain is those drops landing on things, so
/// everything here is about how many drops land on a square metre in a second and how big and fast
/// each one is. The sound of a drop on a surface is the surface's business (see
/// <see cref="RainSurfaces"/>); this is the sky's.
///
/// THE RATE. The server's weather carries a precipitation intensity, 0 to 1, that moves slowly
/// toward the front's target (WorldEnvironmentSystem). It is turned into a rain rate in millimetres an
/// hour, which is what rain is measured in, log-linear in the intensity from a drizzle at
/// <see cref="DrizzleIntensity"/> to a cloudburst at full intensity, because rain rates in nature span
/// two decades and the ear hears them as steps, not as a line. The classes: light under 2.5 mm/h,
/// moderate to 10, heavy to 50, violent above. The light line is the AMS Glossary's (it puts the
/// moderate/heavy line at 7.6 mm/h; 10 is the rounder figure many services use), and "violent" over
/// 50 mm/h is the Met Office's class for showers.
///
/// THE DROPS. Marshall and Palmer (1948, "The distribution of raindrops with size", J. Meteorology 5,
/// 165-166): the number of drops per cubic metre of air per millimetre of diameter is
/// N(D) = N0 exp(−Λ D), N0 = 8000 m⁻³ mm⁻¹, Λ = 4.1 R^−0.21 mm⁻¹ with R in mm/h. Heavier rain has
/// the same number of small drops and many more big ones. Each falls at its terminal speed, from
/// Gunn and Kinzer's (1949, J. Meteorology 6, 243-248) measurements as Atlas, Srivastava and Sekhon fitted
/// them (1973, Rev. Geophys. 11, 1-35): v = 9.65 − 10.3 exp(−0.6 D) m/s, D in mm — four metres a
/// second for a millimetre drop, nine for a five-millimetre one. Drops under
/// <see cref="SmallestDropMm"/> make no sound worth rendering (Medwin et al. 1992 found almost nothing
/// from drops under 0.8 mm on water save the one bubble), and drops over <see cref="LargestDropMm"/>
/// break up in the air.
///
/// THE CLOSURE. Marshall-Palmer is a fit and does not quite give back the rate it was asked for
/// when its drops are carried down at Gunn-Kinzer speeds between these limits. The number of drops is
/// scaled so that the water they carry IS the rate: the volume flux of the drops, (π/6) D³ N(D) v(D)
/// summed over the sizes, equals R. That fixes how many drops land on a square metre a second, which
/// is the number everything heard depends on — a few hundred in light rain, a few thousand in heavy.
/// </summary>
public static class Rainfall
{
    // ── The classes, mm/h ────────────────────────────────────────────────────────────────────────

    /// <summary>Under this is light rain, mm/h.</summary>
    public const float LightBelow = 2.5f;
    /// <summary>Under this is moderate rain, mm/h.</summary>
    public const float ModerateBelow = 10f;
    /// <summary>Under this is heavy rain, mm/h; at and over it, violent.</summary>
    public const float HeavyBelow = 50f;

    /// <summary>A representative rate in each class, mm/h, for the lab and the tests.</summary>
    public const float LightRate = 1.5f, ModerateRate = 5f, HeavyRate = 25f, ViolentRate = 70f;

    // ── Intensity to rate ────────────────────────────────────────────────────────────────────────

    /// <summary>The precipitation intensity at which rain is a drizzle, and the drizzle's rate (mm/h).
    /// Below it the rate falls linearly to nothing.</summary>
    public const float DrizzleIntensity = 0.1f, DrizzleRate = 0.5f;

    /// <summary>The rate at full intensity, mm/h: a convective downpour. The Storm front's target is
    /// full intensity, so a storm is violent rain; the Rain front's 0.6 is moderate (about 7 mm/h).</summary>
    public const float FullIntensityRate = 60f;

    /// <summary>Below this air temperature what falls is snow, and snow makes no rain sound, °C. The
    /// same line the footsteps draw (SoundMappingService), so the ground does not turn to snow under
    /// your feet while you hear rain on it.</summary>
    public const float SnowBelowCelsius = 2f;

    /// <summary>The rain rate for a precipitation intensity, mm/h. See the class summary.</summary>
    public static float RateFromIntensity(float intensity)
    {
        if (!(intensity > 0f)) return 0f;
        float i = MathF.Min(1f, intensity);
        if (i <= DrizzleIntensity) return DrizzleRate * i / DrizzleIntensity;
        float k = MathF.Log(FullIntensityRate / DrizzleRate) / (1f - DrizzleIntensity);
        return DrizzleRate * MathF.Exp(k * (i - DrizzleIntensity));
    }

    /// <summary>The precipitation intensity that gives a rain rate: <see cref="RateFromIntensity"/>
    /// backwards.</summary>
    public static float IntensityFor(float rate)
    {
        if (!(rate > 0f)) return 0f;
        if (rate <= DrizzleRate) return DrizzleIntensity * rate / DrizzleRate;
        float k = MathF.Log(FullIntensityRate / DrizzleRate) / (1f - DrizzleIntensity);
        return MathF.Min(1f, DrizzleIntensity + MathF.Log(rate / DrizzleRate) / k);
    }

    /// <summary>A rate by its class's word (light, moderate, heavy, violent), or a number of mm/h.</summary>
    public static bool TryParseRate(string word, out float rate)
    {
        rate = word.ToLowerInvariant() switch
        {
            "light" or "drizzle" => LightRate,
            "moderate" => ModerateRate,
            "heavy" => HeavyRate,
            "violent" or "torrential" => ViolentRate,
            _ => float.NaN,
        };
        if (!float.IsNaN(rate)) return true;
        string n = word.ToLowerInvariant().Replace("mm/h", "").Replace("mm", "");
        return float.TryParse(n, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out rate)
               && float.IsFinite(rate) && rate > 0f && rate <= FullIntensityRate;
    }

    /// <summary>The class's word, for saying.</summary>
    public static string Word(RainCategory c) => c switch
    {
        RainCategory.Light => "light",
        RainCategory.Moderate => "moderate",
        RainCategory.Heavy => "heavy",
        RainCategory.Violent => "violent",
        _ => "no",
    };

    /// <summary>The rain rate with the air's temperature in it: nothing when what falls is snow.</summary>
    public static float RateFor(float precipitationIntensity, float temperatureCelsius)
        => temperatureCelsius < SnowBelowCelsius ? 0f : RateFromIntensity(precipitationIntensity);

    /// <summary>The class a rate falls in.</summary>
    public static RainCategory Category(float rate)
        => !(rate > 0f) ? RainCategory.None
         : rate < LightBelow ? RainCategory.Light
         : rate < ModerateBelow ? RainCategory.Moderate
         : rate < HeavyBelow ? RainCategory.Heavy
         : RainCategory.Violent;

    // ── The drops ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Marshall-Palmer's intercept, drops per cubic metre per millimetre of diameter.</summary>
    public const float MarshallPalmerN0 = 8000f;
    /// <summary>Marshall-Palmer's slope: Λ = 4.1 R^−0.21 per mm.</summary>
    public const float LambdaCoefficient = 4.1f, LambdaExponent = -0.21f;

    /// <summary>The smallest drop rendered, mm diameter.</summary>
    public const float SmallestDropMm = 0.3f;
    /// <summary>The largest drop that survives its fall, mm diameter.</summary>
    public const float LargestDropMm = 6f;

    /// <summary>Marshall-Palmer's slope for a rate, per mm.</summary>
    public static float Lambda(float rate) => LambdaCoefficient * MathF.Pow(MathF.Max(0.01f, rate), LambdaExponent);

    /// <summary>
    /// A raindrop's terminal speed, m/s, for its diameter in mm: Atlas et al. (1973), which is within
    /// a few per cent of Gunn and Kinzer's measurements from 0.3 mm up. Under that the fit runs to zero
    /// where the real drops still fall at a quarter of a metre a second or more, so it is held off the
    /// floor by Gunn and Kinzer's own figure for a 0.1 mm drop.
    /// </summary>
    public static float TerminalSpeed(float diameterMm)
        => MathF.Max(0.27f, 9.65f - 10.3f * MathF.Exp(-0.6f * diameterMm));

    /// <summary>Marshall-Palmer's number density as published, per m³ per mm, before the closure.</summary>
    public static float MarshallPalmerDensity(float rate, float diameterMm)
        => rate > 0f ? MarshallPalmerN0 * MathF.Exp(-Lambda(rate) * diameterMm) : 0f;

    private const int Steps = 400;

    /// <summary>The rain rate, mm/h, that Marshall-Palmer's drops carry at Atlas speeds between
    /// <see cref="SmallestDropMm"/> and <see cref="LargestDropMm"/>, before the closure.</summary>
    public static float UnclosedRate(float rate)
    {
        if (!(rate > 0f)) return 0f;
        double sum = 0, dD = (LargestDropMm - SmallestDropMm) / (double)Steps;
        for (int k = 0; k < Steps; k++)
        {
            double d = SmallestDropMm + (k + 0.5) * dD;
            double vol = Math.PI / 6.0 * Math.Pow(d * 1e-3, 3);                 // m³
            sum += vol * MarshallPalmerDensity(rate, (float)d) * TerminalSpeed((float)d) * dD;   // m/s
        }
        return (float)(sum * 3.6e6);
    }

    /// <summary>The factor the drop count is scaled by so the drops carry the rate (the closure).</summary>
    public static float Closure(float rate)
    {
        float raw = UnclosedRate(rate);
        return raw > 0f ? rate / raw : 0f;
    }

    /// <summary>Drops in the air, per m³ per mm of diameter, with the closure.</summary>
    public static float NumberDensity(float rate, float diameterMm)
        => Closure(rate) * MarshallPalmerDensity(rate, diameterMm);

    /// <summary>Drops arriving on a square metre of level ground, per second per mm of diameter.</summary>
    public static float Flux(float rate, float diameterMm)
        => NumberDensity(rate, diameterMm) * TerminalSpeed(diameterMm);

    /// <summary>Every drop arriving on a square metre of level ground, per second, from the smallest
    /// rendered to the largest.</summary>
    public static float DropsPerSquareMetreSecond(float rate)
    {
        if (!(rate > 0f)) return 0f;
        float closure = Closure(rate);
        double sum = 0, dD = (LargestDropMm - SmallestDropMm) / (double)Steps;
        for (int k = 0; k < Steps; k++)
        {
            float d = (float)(SmallestDropMm + (k + 0.5) * dD);
            sum += closure * MarshallPalmerDensity(rate, d) * TerminalSpeed(d) * dD;
        }
        return (float)sum;
    }

    /// <summary>The rate, mm/h, that the closed drop flux carries: the closure's own check.</summary>
    public static float CarriedRate(float rate)
    {
        if (!(rate > 0f)) return 0f;
        double sum = 0, dD = (LargestDropMm - SmallestDropMm) / (double)Steps;
        for (int k = 0; k < Steps; k++)
        {
            float d = (float)(SmallestDropMm + (k + 0.5) * dD);
            sum += Math.PI / 6.0 * Math.Pow(d * 1e-3, 3) * Flux(rate, d) * dD;
        }
        return (float)(sum * 3.6e6);
    }

    /// <summary>The kinetic energy rain delivers to a square metre of ground in a second, W/m²: ½ m v²
    /// summed over the drops. A figure the soil-erosion literature measures (rainfall kinetic energy,
    /// about 20-30 J per m² per mm of rain for heavy rain: Kinnell 1981, van Dijk et al. 2002), and so
    /// a check on the drop sizes and speeds together.</summary>
    public static float KineticPower(float rate)
    {
        if (!(rate > 0f)) return 0f;
        double sum = 0, dD = (LargestDropMm - SmallestDropMm) / (double)Steps;
        for (int k = 0; k < Steps; k++)
        {
            float d = (float)(SmallestDropMm + (k + 0.5) * dD);
            double m = 1000.0 * Math.PI / 6.0 * Math.Pow(d * 1e-3, 3);
            double v = TerminalSpeed(d);
            sum += 0.5 * m * v * v * Flux(rate, d) * dD;
        }
        return (float)sum;
    }
}

/// <summary>
/// The sizes of the drops ARRIVING on a surface at one rate, for drawing them one at a time: the
/// Marshall-Palmer spread weighted by how fast each size falls, since a fast drop sweeps more air
/// and so lands more often. Built once per rate, drawn from with no allocation.
/// </summary>
public sealed class DropSizeTable
{
    private const int Points = 256;
    private readonly float[] _cdf = new float[Points + 1];
    private float _rate = -1f;

    /// <summary>The rate this table was built for, mm/h.</summary>
    public float Rate => _rate;

    /// <summary>Drops arriving per m² per second at this rate.</summary>
    public float DropsPerSquareMetreSecond { get; private set; }

    /// <summary>Rebuilds for a rate, unless it is within a per cent of the one built for.</summary>
    public void Build(float rate)
    {
        if (rate > 0f && _rate > 0f && MathF.Abs(rate - _rate) <= 0.01f * _rate) return;
        _rate = rate;
        if (!(rate > 0f)) { DropsPerSquareMetreSecond = 0f; return; }
        float lambda = Rainfall.Lambda(rate);
        float dD = (Rainfall.LargestDropMm - Rainfall.SmallestDropMm) / Points;
        double sum = 0;
        _cdf[0] = 0f;
        for (int k = 0; k < Points; k++)
        {
            float d = Rainfall.SmallestDropMm + (k + 0.5f) * dD;
            sum += MathF.Exp(-lambda * d) * Rainfall.TerminalSpeed(d) * dD;
            _cdf[k + 1] = (float)sum;
        }
        for (int k = 1; k <= Points; k++) _cdf[k] /= (float)sum;
        DropsPerSquareMetreSecond = Rainfall.DropsPerSquareMetreSecond(rate);
    }

    /// <summary>
    /// The size classes drops are drawn in, mm. A drop's sound grows as a high power of its size (its
    /// blow's energy as D⁵ v³), so the few big drops carry most of the sound and the many small ones
    /// almost none of it. Drawn one class at a time, every big drop that falls can be rendered, and
    /// only the swarm of small ones is stood for by a few — where drawing from the whole spread, a few
    /// at a time, stood a big drop for twenty when one came up and left long gaps when none did.
    /// </summary>
    public static readonly float[] ClassEdgesMm = { 0.3f, 0.8f, 1.3f, 2.0f, 3.0f, 4.2f, 6.0f };

    public static int Classes => ClassEdgesMm.Length - 1;

    /// <summary>The share of the arriving drops in class <paramref name="c"/>.</summary>
    public float ClassShare(int c) => Below(ClassEdgesMm[c + 1]) - Below(ClassEdgesMm[c]);

    /// <summary>A diameter in class <paramref name="c"/>, mm, for a uniform number in [0, 1).</summary>
    public float DrawIn(int c, float u)
    {
        float lo = Below(ClassEdgesMm[c]), hi = Below(ClassEdgesMm[c + 1]);
        return Math.Clamp(Draw(lo + (hi - lo) * u), ClassEdgesMm[c], ClassEdgesMm[c + 1]);
    }

    /// <summary>The share of arriving drops under a diameter.</summary>
    private float Below(float diameterMm)
    {
        float dD = (Rainfall.LargestDropMm - Rainfall.SmallestDropMm) / Points;
        float at = Math.Clamp((diameterMm - Rainfall.SmallestDropMm) / dD, 0f, Points);
        int i = Math.Min(Points - 1, (int)at);
        return _cdf[i] + (_cdf[i + 1] - _cdf[i]) * (at - i);
    }

    /// <summary>A diameter, mm, for a uniform number in [0, 1).</summary>
    public float Draw(float u)
    {
        int lo = 0, hi = Points;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) >> 1;
            if (_cdf[mid] <= u) lo = mid; else hi = mid;
        }
        float span = _cdf[hi] - _cdf[lo];
        float frac = span > 0f ? (u - _cdf[lo]) / span : 0.5f;
        float dD = (Rainfall.LargestDropMm - Rainfall.SmallestDropMm) / Points;
        return Rainfall.SmallestDropMm + (lo + frac) * dD;
    }
}

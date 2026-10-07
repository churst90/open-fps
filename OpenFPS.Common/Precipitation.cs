namespace OpenFPS.Common;

/// <summary>What is falling.</summary>
public enum PrecipitationKind
{
    /// <summary>Liquid drops. Drizzle is rain whose drops are all small.</summary>
    Rain = 0,
    /// <summary>Rain that freezes where it lands. It falls and sounds as rain.</summary>
    FreezingRain = 1,
    /// <summary>Ice pellets: raindrops frozen on the way down, 1-5 mm of clear ice.</summary>
    Sleet = 2,
    /// <summary>Snowflakes: aggregates of ice crystals, mostly air, falling at a metre a second.</summary>
    Snow = 3,
    /// <summary>Hailstones, with the heavy rain they fall in.</summary>
    Hail = 4,
}

/// <summary>
/// The weather's precipitation as the sound needs it: what kind, how much (a rate in millimetres of
/// water an hour), and how big the particles are — the median volume diameter D0 of the drops (zero:
/// the size Marshall and Palmer give that rate), and for hail the median stone. Rate and size are
/// separate because they are in nature: drizzle is many tiny drops at a low rate, a convective
/// downpour fewer and much bigger ones than a stratiform rain of the same rate (Ulbrich 1983; Bringi
/// et al. 2003, J. Atmos. Sci. 60, 354-365).
/// </summary>
public readonly record struct Precipitation(PrecipitationKind Kind, float RateMmPerHour, float MedianDropMm = 0f, float HailMm = 0f)
{
    public static readonly Precipitation None = new(PrecipitationKind.Rain, 0f);

    /// <summary>Anything falling at all.</summary>
    public bool Falling => RateMmPerHour > 0f || (Kind == PrecipitationKind.Hail && HailMm > 0f);

    /// <summary>The median volume diameter in use, mm: the one given, or the kind's own for the rate.</summary>
    public float EffectiveMedianMm => MedianDropMm > 0f ? MedianDropMm : Hydrometeors.DefaultMedianMm(Kind, RateMmPerHour);
}

/// <summary>
/// The particles each kind of precipitation is made of: how big, how dense, how fast they fall, how
/// many, and how they meet what they land on. Every figure is a published one, named where it is used.
/// </summary>
public static class Hydrometeors
{
    // ── Size ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The shape μ of the gamma drop-size spectrum, N(D) = N0 D^μ exp(−(3.67 + μ) D / D0) (Ulbrich
    /// 1983, J. Climate Appl. Meteor. 22, 1764-1775). Marshall-Palmer is μ = 0; disdrometers find μ
    /// mostly between 0 and 5 in rain of every kind, and 3 is the value the normalised-gamma work
    /// settles on when one value has to serve (Testud et al. 2001, J. Appl. Meteor. 40, 1118-1140;
    /// Bringi and Chandrasekar 2001). A narrower spread: fewer of the very small drops and fewer of the
    /// very large.
    /// </summary>
    public const float GammaShape = 3f;

    /// <summary>The median volume diameter Marshall and Palmer's spectrum gives a rate, mm: 3.67 / Λ.</summary>
    public static float MarshallPalmerMedianMm(float rate) => 3.67f / Rainfall.Lambda(MathF.Max(0.01f, rate));

    /// <summary>The median diameter a kind falls with at a rate when nobody has said, mm.</summary>
    public static float DefaultMedianMm(PrecipitationKind kind, float rate) => kind switch
    {
        PrecipitationKind.Sleet => SleetMedianMm,
        // Gunn and Marshall (1958, J. Meteor. 15, 452-461): snow by MELTED diameter is exponential with
        // Λ = 2.55 S^−0.48 per mm, S the water-equivalent rate in mm/h.
        PrecipitationKind.Snow => 3.67f / (2.55f * MathF.Pow(MathF.Max(0.01f, rate), -0.48f)),
        _ => MarshallPalmerMedianMm(rate),
    };

    /// <summary>Ice pellets are frozen raindrops, mostly 1-5 mm (AMS Glossary, "ice pellets"); their
    /// median taken as 2.5 mm.</summary>
    public const float SleetMedianMm = 2.5f;

    /// <summary>A snowflake's size over the size of the drop it melts to: aggregates of dendrites are
    /// mostly air, a density of 0.02-0.1 g/cm³ (Magono and Nakamura 1965), so a flake is some three to
    /// four times its melted diameter across.</summary>
    public const float FlakeSwell = 3.5f;

    /// <summary>Hail density, kg/m³: 0.7-0.9 g/cm³ for natural hailstones (Heymsfield, Giammanco and
    /// Wright 2014, Geophys. Res. Lett. 41, 8666-8672).</summary>
    public const float HailDensity = 800f;

    /// <summary>Clear ice, kg/m³: an ice pellet.</summary>
    public const float IceDensity = 917f;

    /// <summary>
    /// Hailstones on the scale the National Weather Service reports them by, median mm: pea 6, marble
    /// 13, penny 19, quarter 25, golf ball 44, tennis ball 64, baseball 70, softball 114.
    /// </summary>
    public static bool TryParseHail(string word, out float mm)
    {
        mm = word.ToLowerInvariant() switch
        {
            "pea" => 6f, "marble" => 13f, "penny" => 19f, "quarter" => 25f, "golf" or "golfball" => 44f,
            "tennis" => 64f, "baseball" => 70f, "softball" => 114f, "small" => 6f, "large" => 44f,
            _ => float.NaN,
        };
        if (!float.IsNaN(mm)) return true;
        string n = word.ToLowerInvariant().Replace("mm", "");
        return float.TryParse(n, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out mm)
               && float.IsFinite(mm) && mm >= 5f && mm <= 150f;
    }

    // ── Fall ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Air density at sea level, kg/m³.</summary>
    public const float AirDensity = 1.21f;

    /// <summary>A hailstone's drag coefficient: 0.6, measured on natural stones (Heymsfield et al. 2014),
    /// which with the drag law gives their v ≈ 12 D^0.5 (D in cm).</summary>
    public const float HailDrag = 0.6f;

    /// <summary>
    /// Fall speed, m/s, for a particle of this kind and diameter (mm; melted diameter for snow).
    ///   rain: Gunn and Kinzer (Rainfall.TerminalSpeed);
    ///   ice (sleet, hail): the drag law for a sphere, √(4 g D ρ / (3 ρa Cd)) with Cd = 0.6;
    ///   snow: aggregates of dendrites, 0.8 D^0.16 (D the flake's own size in mm; Locatelli and Hobbs
    ///   1974, J. Geophys. Res. 79, 2185-2197) — about a metre a second whatever the size.
    /// </summary>
    public static float FallSpeed(PrecipitationKind kind, float diameterMm) => kind switch
    {
        PrecipitationKind.Sleet => IceSphereSpeed(diameterMm, IceDensity),
        PrecipitationKind.Hail => IceSphereSpeed(diameterMm, HailDensity),
        PrecipitationKind.Snow => 0.8f * MathF.Pow(MathF.Max(0.1f, diameterMm * FlakeSwell), 0.16f),
        _ => Rainfall.TerminalSpeed(diameterMm),
    };

    private static float IceSphereSpeed(float diameterMm, float density)
        => MathF.Sqrt(4f * 9.81f * diameterMm * 1e-3f * density / (3f * AirDensity * HailDrag));

    /// <summary>The mass of one particle, kg.</summary>
    public static float Mass(PrecipitationKind kind, float diameterMm)
    {
        float d = diameterMm * 1e-3f;
        float rho = kind switch
        {
            PrecipitationKind.Sleet => IceDensity,
            PrecipitationKind.Hail => HailDensity,
            _ => 1000f,                     // snow by its melted diameter
        };
        return rho * MathF.PI / 6f * d * d * d;
    }

    /// <summary>Whether it is a hard sphere of ice (bounces, Hertz contact) rather than a drop or a flake.</summary>
    public static bool IsIce(PrecipitationKind kind) => kind is PrecipitationKind.Sleet or PrecipitationKind.Hail;

    // ── Meeting a surface ───────────────────────────────────────────────────────────────────────

    /// <summary>Ice's Young's modulus and Poisson's ratio: 9 GPa, 0.33 (polycrystalline ice).</summary>
    public const float IceModulusGPa = 9f, IcePoisson = 0.33f;

    /// <summary>
    /// How long an ice sphere is in contact with a surface it strikes, s: Hertz, t = 2.87 (m² / (R E*² v))^⅕,
    /// E* the two materials' combined modulus (Johnson, Contact Mechanics, 1985, §11.4). A 1 cm
    /// hailstone at 12 m/s on asphalt is in contact for about 40 µs, on a lawn for milliseconds.
    /// </summary>
    public static float HertzSeconds(PrecipitationKind kind, float diameterMm, float speed, float surfaceModulusGPa)
    {
        float m = Mass(kind, diameterMm);
        float r = 0.5f * diameterMm * 1e-3f;
        float ice = (1f - IcePoisson * IcePoisson) / (IceModulusGPa * 1e9f);
        float surface = (1f - 0.09f) / (MathF.Max(1e-4f, surfaceModulusGPa) * 1e9f);
        float eStar = 1f / (ice + surface);
        return 2.87f * MathF.Pow(m * m / (r * eStar * eStar * MathF.Max(0.1f, speed)), 0.2f);
    }

    /// <summary>
    /// How much of its speed an ice sphere keeps bouncing off something hard: about 0.4 at the speeds
    /// hail falls at (Higa, Arakawa and Maeno 1996, Planet. Space Sci. 44, 917-925: e falls from near
    /// 0.9 at a metre a second to 0.3-0.5 above ten); a tenth off something soft.
    /// </summary>
    public static float Restitution(float surfaceModulusGPa) => surfaceModulusGPa >= RainSurfaces.SoftBelowGPa ? 0.4f : 0.1f;

    /// <summary>A snowflake crushes rather than strikes: it stops over its own size, s.</summary>
    public static float FlakeCrushSeconds(float meltedMm, float speed) => meltedMm * FlakeSwell * 1e-3f / MathF.Max(0.1f, speed);

    // ── How many hailstones ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The hailstone spectrum's intercept for its median, per m³ per mm: Cheng and English (1983,
    /// J. Atmos. Sci. 40, 204-213) found N0 = 115 Λ^3.63 over many hailfalls, Λ = 3.67 / D0 per mm.
    /// Pea hail is hundreds of stones on a square metre a second; baseballs one or two.
    /// </summary>
    public static float HailIntercept(float medianMm)
    {
        float lambda = 3.67f / MathF.Max(1f, medianMm);
        return 115f * MathF.Pow(lambda, 3.63f);
    }

    /// <summary>The rain hail falls in, mm/h: hail comes out of convective storms, with heavy rain.</summary>
    public const float HailRainRate = 25f;

    // ── Radar ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>The rate a reflectivity means, mm/h, by the WSR-88D's default convective relation
    /// Z = 300 R^1.4 (Fulton et al. 1998, Wea. Forecasting 13, 377-395); Marshall-Palmer's stratiform
    /// Z = 200 R^1.6 is the other common one.</summary>
    public static float RateFromDbz(float dbz) => MathF.Pow(MathF.Pow(10f, dbz / 10f) / 300f, 1f / 1.4f);

    /// <summary>
    /// The median drop that, at this rate, gives this reflectivity, mm: Z grows as about the cube of
    /// D0 at a fixed rate, so a radar's dBZ and a rate together say how big the drops are — the
    /// difference between a stratiform rain and a convective one of the same rate.
    /// </summary>
    public static float MedianForDbz(PrecipitationKind kind, float rate, float dbz)
    {
        var s = new ParticleSpectrum();
        float lo = 0.2f, hi = 4.5f;
        for (int i = 0; i < 30; i++)
        {
            float mid = MathF.Sqrt(lo * hi);
            s.Build(kind, rate, mid);
            if (s.Dbz < dbz) lo = mid; else hi = mid;
        }
        return MathF.Sqrt(lo * hi);
    }

    /// <summary>The reflectivity a rate means by the same relation, dBZ.</summary>
    public static float DbzFromRate(float rate) => rate > 0f ? 10f * MathF.Log10(300f * MathF.Pow(rate, 1.4f)) : 0f;

    /// <summary>
    /// The colour a reflectivity is on the radar, as the common scale runs: under 20 dBZ the palest
    /// green (drizzle), 20-30 green, 30-40 yellow, 40-50 orange, 50-60 red, 60 and over purple, which is
    /// where hail is. Snow is drawn in blue.
    /// </summary>
    public static string RadarColour(float dbz, PrecipitationKind kind)
        => kind == PrecipitationKind.Snow ? "blue"
         : dbz < 20f ? "pale green" : dbz < 30f ? "green" : dbz < 40f ? "yellow" : dbz < 50f ? "orange" : dbz < 60f ? "red" : "purple";

    /// <summary>The rain classes Cody named: drizzle, light, moderate, heavy, extreme, with a
    /// representative rate (mm/h) and median drop (mm; zero for Marshall-Palmer's).</summary>
    public static bool TryParseRainClass(string word, out float rate, out float medianMm)
    {
        (rate, medianMm) = word.ToLowerInvariant() switch
        {
            // Drizzle: drops under half a millimetre, falling slowly, at well under a millimetre an hour
            // (AMS Glossary, "drizzle").
            "drizzle" => (0.3f, 0.35f),
            "light" => (Rainfall.LightRate, 0f),
            "moderate" => (Rainfall.ModerateRate, 0f),
            "heavy" => (Rainfall.HeavyRate, 0f),
            "extreme" or "violent" or "torrential" => (Rainfall.ViolentRate, 0f),
            _ => (float.NaN, 0f),
        };
        return !float.IsNaN(rate);
    }
}

/// <summary>
/// The particles of one precipitation, by size, for drawing them one at a time and for counting them:
/// how many fall on a square metre a second, how they spread over sizes, and what a radar would see.
///
/// Rain, freezing rain and sleet: a gamma spectrum of the given median (Hydrometeors.GammaShape),
/// scaled so the water it carries is the rate. Snow: Gunn and Marshall's exponential in melted
/// diameter, scaled the same way. Hail: an exponential of the given median at Cheng and English's
/// concentration, which does not depend on any rate.
///
/// Drawn in six size classes, log-spaced over the spectrum's range: a particle's sound grows as a high
/// power of its size, so the few big ones carry most of it, and drawing one class at a time lets every
/// big one be rendered while the swarm of small ones is stood for by a few.
/// </summary>
public sealed class ParticleSpectrum
{
    private const int Points = 256;
    public const int Classes = 6;
    private readonly float[] _d = new float[Points + 1];
    private readonly float[] _cdf = new float[Points + 1];
    private readonly float[] _classEdge = new float[Classes + 1];
    private readonly float[] _classShare = new float[Classes];
    private readonly float[] _classLo = new float[Classes], _classHi = new float[Classes];
    private PrecipitationKind _kind;
    private float _rate = -1f, _median = -1f;
    private bool _hail;

    public PrecipitationKind Kind => _kind;
    /// <summary>Particles arriving on a square metre of level ground a second.</summary>
    public float PerSquareMetreSecond { get; private set; }
    /// <summary>The radar's equivalent reflectivity, dBZ.</summary>
    public float Dbz { get; private set; }
    /// <summary>The median diameter in use, mm.</summary>
    public float MedianMm => _median;

    /// <summary>The spectrum of a precipitation's main particles: the hail itself for hail.</summary>
    public void Build(Precipitation p) => Build(p.Kind, p.Kind == PrecipitationKind.Hail ? 0f : p.RateMmPerHour,
                                               p.Kind == PrecipitationKind.Hail ? p.HailMm : p.EffectiveMedianMm,
                                               hail: p.Kind == PrecipitationKind.Hail);

    /// <summary>Builds for a kind, a water-equivalent rate (mm/h) and a median (mm), unless it is
    /// already that within a per cent.</summary>
    public void Build(PrecipitationKind kind, float rate, float medianMm, bool hail = false)
    {
        if (kind == _kind && hail == _hail && Close(rate, _rate) && Close(medianMm, _median)) return;
        _kind = kind; _rate = rate; _median = medianMm; _hail = hail;
        PerSquareMetreSecond = 0f; Dbz = 0f;
        if (!(medianMm > 0f) || (!hail && !(rate > 0f))) return;

        float lo, hi;
        if (hail) { lo = 5f; hi = MathF.Min(150f, 4f * medianMm); }
        else if (kind == PrecipitationKind.Snow) { lo = 0.1f; hi = MathF.Max(1f, 4f * medianMm); }
        else if (kind == PrecipitationKind.Sleet) { lo = 0.5f; hi = 6f; }
        else { lo = 0.1f; hi = Rainfall.LargestDropMm; }      // down to drizzle's tenth of a millimetre
        hi = MathF.Max(hi, lo * 1.5f);

        // Shape of the number density (unscaled), its flux and its mass flux on a log grid.
        float mu = hail || kind == PrecipitationKind.Snow ? 0f : Hydrometeors.GammaShape;
        float lambda = (3.67f + mu) / medianMm;
        double flux = 0, massFlux = 0, z = 0;
        double ln = Math.Log(hi / lo) / Points;
        _cdf[0] = 0f;
        for (int k = 0; k <= Points; k++) _d[k] = (float)(lo * Math.Exp(k * ln));
        for (int k = 0; k < Points; k++)
        {
            double d = Math.Sqrt(_d[k] * (double)_d[k + 1]), dD = _d[k + 1] - _d[k];
            double n = Math.Pow(d, mu) * Math.Exp(-lambda * d);                       // per m³ per mm, unscaled
            double v = Hydrometeors.FallSpeed(kind, (float)d);
            flux += n * v * dD;
            massFlux += n * v * dD * Hydrometeors.Mass(kind, (float)d);
            z += n * Math.Pow(d, 6) * dD;
            _cdf[k + 1] = (float)flux;
        }
        for (int k = 1; k <= Points; k++) _cdf[k] /= (float)flux;
        double scale;
        if (hail) scale = Hydrometeors.HailIntercept(medianMm);                      // m^-3 mm^-1 at D^0
        else scale = rate / 3.6e6 * 1000.0 / massFlux;                                // so the water carried is the rate
        PerSquareMetreSecond = (float)(scale * flux);
        // Ice reflects |K|² = 0.176 to water's 0.93 (Smith 1984); snow by its melted size.
        double k2 = kind == PrecipitationKind.Rain || kind == PrecipitationKind.FreezingRain ? 1.0 : 0.176 / 0.93;
        Dbz = (float)(10 * Math.Log10(Math.Max(1e-6, scale * z * k2)));

        for (int c = 0; c <= Classes; c++) _classEdge[c] = (float)(lo * Math.Pow(hi / lo, c / (double)Classes));
        for (int c = 0; c < Classes; c++)
        {
            _classLo[c] = Below(_classEdge[c]);
            _classHi[c] = Below(_classEdge[c + 1]);
            _classShare[c] = _classHi[c] - _classLo[c];
        }
    }

    private static bool Close(float a, float b) => MathF.Abs(a - b) <= 0.01f * MathF.Max(MathF.Abs(a), 1e-3f);

    /// <summary>The share of arriving particles in class <paramref name="c"/>.</summary>
    public float ClassShare(int c) => _classShare[c];

    /// <summary>The lower edge of class <paramref name="c"/>, mm.</summary>
    public float ClassFloorMm(int c) => _classEdge[c];

    /// <summary>A diameter in class <paramref name="c"/>, mm, for a uniform number in [0, 1).</summary>
    public float DrawIn(int c, float u) => Draw(_classLo[c] + (_classHi[c] - _classLo[c]) * u);

    /// <summary>The share of arriving particles at or above a diameter.</summary>
    public float ShareAbove(float diameterMm) => 1f - Below(diameterMm);

    /// <summary>A diameter, mm, at or above <paramref name="floorMm"/>, for a uniform number.</summary>
    public float DrawAbove(float floorMm, float u)
    {
        float b = Below(floorMm);
        return Draw(b + (1f - b) * u);
    }

    private float Below(float diameterMm)
    {
        if (diameterMm <= _d[0]) return 0f;
        if (diameterMm >= _d[Points]) return 1f;
        float at = MathF.Log(diameterMm / _d[0]) / MathF.Log(_d[Points] / _d[0]) * Points;
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
        float frac = span > 0f ? Math.Clamp((u - _cdf[lo]) / span, 0f, 1f) : 0.5f;
        return _d[lo] * MathF.Pow(_d[hi] / _d[lo], frac);
    }
}

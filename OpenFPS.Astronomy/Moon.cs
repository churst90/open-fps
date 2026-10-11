using static OpenFPS.Astronomy.AstroMath;

namespace OpenFPS.Astronomy;

/// <summary>The eight names of the moon's phase.</summary>
public enum MoonPhase
{
    New, WaxingCrescent, FirstQuarter, WaxingGibbous, Full, WaningGibbous, LastQuarter, WaningCrescent,
}

/// <summary>
/// Where the moon is seen from a place on the ground, degrees: azimuth from true north through east,
/// elevation with refraction, both topocentric. The phase is geocentric, as the almanacs give it.
/// </summary>
public readonly record struct MoonPosition(double Azimuth, double Elevation, double TrueElevation,
                                           double DistanceKm, double Parallax, double Elongation,
                                           double PhaseAngle, double Illuminated, bool Waxing, MoonPhase Phase);

/// <summary>Moonrise and moonset in one local day, as UTC instants; null where there is none that day.</summary>
public sealed record MoonDay(DateTime? Rise, DateTime? Set);

/// <summary>
/// The moon by Meeus's lunar series (Astronomical Algorithms, 2nd ed., chapter 47: ELP-2000/82 to 60 terms
/// in longitude and distance, 60 in latitude, about 10 arcseconds), seen from the ground with the parallax
/// of chapter 40: the moon is near enough to stand a degree lower on the horizon than from the earth's
/// centre.
/// </summary>
public static class Moon
{
    /// <summary>The upper limb on the horizon: 34 arcminutes of refraction, as the Naval Observatory takes it.
    /// The semidiameter is added per instant (it is 0.2725 of the parallax).</summary>
    private const double HorizonRefraction = 34.0 / 60.0;

    private const double EarthRadiusKm = 6378.14;

    public static MoonPosition Position(DateTime utc, double latitude, double longitude)
    {
        double jd = JulianDay(utc);
        var g = Geocentric(jd + DeltaTSeconds(jd) / 86400.0);
        var (az, alt) = Topocentric(g, jd, latitude, longitude);
        return new MoonPosition(az, alt + Refraction(alt), alt, g.DistanceKm, g.Parallax,
                                g.Elongation, g.PhaseAngle, Illuminated(g), g.Elongation < 180.0,
                                PhaseOf(Illuminated(g), g.Elongation < 180.0));
    }

    /// <summary>The local day starting at local midnight of <paramref name="date"/> at this UTC offset.</summary>
    public static MoonDay Day(DateOnly date, double latitude, double longitude, TimeSpan utcOffset)
    {
        DateTime start = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) - utcOffset;
        DateTime end = start.AddDays(1);
        return new MoonDay(Crossings.First(u => LimbAltitude(u, latitude, longitude), 0, start, end, rising: true),
                           Crossings.First(u => LimbAltitude(u, latitude, longitude), 0, start, end, rising: false));
    }

    /// <summary>The next moonrise (or moonset) after <paramref name="fromUtc"/> within <paramref name="days"/>.
    /// Past the polar circles the moon can stay up or down for about two weeks.</summary>
    public static DateTime? Next(DateTime fromUtc, double latitude, double longitude, bool rising, double days = 40)
        => Crossings.First(u => LimbAltitude(u, latitude, longitude), 0, fromUtc, fromUtc.AddDays(days), rising);

    /// <summary>Whether the moon's upper limb is above the horizon, by the same rule as rise and set.</summary>
    public static bool IsUp(DateTime utc, double latitude, double longitude) => LimbAltitude(utc, latitude, longitude) >= 0;

    /// <summary>The next instant of a principal phase (new, first quarter, full or last quarter) after
    /// <paramref name="fromUtc"/>: when the moon's apparent longitude is that far ahead of the sun's.</summary>
    public static DateTime NextPhase(DateTime fromUtc, MoonPhase principal)
    {
        double target = principal switch
        {
            MoonPhase.New => 0, MoonPhase.FirstQuarter => 90, MoonPhase.Full => 180, MoonPhase.LastQuarter => 270,
            _ => throw new ArgumentException("Only new, first quarter, full and last quarter have an instant.", nameof(principal)),
        };
        double Ahead(DateTime u)
        {
            double jd = JulianDay(u);
            return Signed(Geocentric(jd + DeltaTSeconds(jd) / 86400.0).Elongation - target);
        }
        // The elongation gains 12 degrees a day and never goes back, so the first rise through zero
        // from below, within a lunation, is the phase.
        DateTime? found = Crossings.First(Ahead, 0, fromUtc, fromUtc.AddDays(31), rising: true);
        return found ?? throw new InvalidOperationException("No phase within a lunation.");
    }

    /// <summary>
    /// The Naval Observatory's naming, found from its answers: by the lit share to the nearest percent, new at
    /// 0, full at 100, a quarter at 50, a crescent below half and gibbous above. So "new" and "full" each last
    /// about sixteen hours either side of the instant, and a quarter about an hour.
    /// </summary>
    internal static MoonPhase PhaseOf(double illuminated, bool waxing)
    {
        int percent = (int)Math.Round(illuminated * 100, MidpointRounding.AwayFromZero);
        return percent switch
        {
            0 => MoonPhase.New,
            100 => MoonPhase.Full,
            50 => waxing ? MoonPhase.FirstQuarter : MoonPhase.LastQuarter,
            < 50 => waxing ? MoonPhase.WaxingCrescent : MoonPhase.WaningCrescent,
            _ => waxing ? MoonPhase.WaxingGibbous : MoonPhase.WaningGibbous,
        };
    }

    private static double Illuminated(GeocentricMoon g) => (1 + CosD(g.PhaseAngle)) / 2;

    /// <summary>The upper limb's airless altitude above the level at which it is seen to touch the horizon.</summary>
    private static double LimbAltitude(DateTime utc, double latitude, double longitude)
    {
        double jd = JulianDay(utc);
        var g = Geocentric(jd + DeltaTSeconds(jd) / 86400.0);
        double semidiameter = Math.Asin(0.272481 * SinD(g.Parallax)) * Deg;
        return Topocentric(g, jd, latitude, longitude).Altitude + semidiameter + HorizonRefraction;
    }

    internal readonly record struct GeocentricMoon(double RightAscension, double Declination, double DistanceKm,
                                                   double Parallax, double Longitude, double Latitude,
                                                   double Elongation, double PhaseAngle, double TrueObliquity,
                                                   double NutationLongitude);

    /// <summary>The moon from the earth's centre at a dynamical-time Julian day: apparent ecliptic and
    /// equatorial coordinates, distance, parallax, and the phase (Meeus 47 and 48).</summary>
    internal static GeocentricMoon Geocentric(double jde)
    {
        double t = Centuries(jde);
        double lp = Normalize(218.3164477 + 481267.88123421 * t - 0.0015786 * t * t + t * t * t / 538841.0 - t * t * t * t / 65194000.0);
        double d = Normalize(297.8501921 + 445267.1114034 * t - 0.0018819 * t * t + t * t * t / 545868.0 - t * t * t * t / 113065000.0);
        double m = Normalize(357.5291092 + 35999.0502909 * t - 0.0001536 * t * t + t * t * t / 24490000.0);
        double mp = Normalize(134.9633964 + 477198.8675055 * t + 0.0087414 * t * t + t * t * t / 69699.0 - t * t * t * t / 14712000.0);
        double f = Normalize(93.2720950 + 483202.0175233 * t - 0.0036539 * t * t - t * t * t / 3526000.0 + t * t * t * t / 863310000.0);
        double a1 = Normalize(119.75 + 131.849 * t);
        double a2 = Normalize(53.09 + 479264.290 * t);
        double a3 = Normalize(313.45 + 481266.484 * t);
        double e = 1 - 0.002516 * t - 0.0000074 * t * t;

        double sl = 0, sr = 0, sb = 0;
        for (int i = 0; i < LongitudeDistance.Length; i++)
        {
            var (td, tm, tmp, tf, cl, cr) = LongitudeDistance[i];
            double arg = td * d + tm * m + tmp * mp + tf * f;
            double ef = Math.Abs(tm) == 1 ? e : Math.Abs(tm) == 2 ? e * e : 1;
            sl += cl * ef * SinD(arg);
            sr += cr * ef * CosD(arg);
        }
        for (int i = 0; i < LatitudeTerms.Length; i++)
        {
            var (td, tm, tmp, tf, cb) = LatitudeTerms[i];
            double arg = td * d + tm * m + tmp * mp + tf * f;
            double ef = Math.Abs(tm) == 1 ? e : Math.Abs(tm) == 2 ? e * e : 1;
            sb += cb * ef * SinD(arg);
        }
        sl += 3958 * SinD(a1) + 1962 * SinD(lp - f) + 318 * SinD(a2);
        sb += -2235 * SinD(lp) + 382 * SinD(a3) + 175 * SinD(a1 - f) + 175 * SinD(a1 + f)
            + 127 * SinD(lp - mp) - 115 * SinD(lp + mp);

        double lambda = lp + sl / 1_000_000.0;
        double beta = sb / 1_000_000.0;
        double distance = 385000.56 + sr / 1000.0;
        double parallax = Math.Asin(EarthRadiusKm / distance) * Deg;

        var (dpsi, deps) = Nutation(t);
        double epsilon = MeanObliquity(t) + deps;
        double apparentLambda = Normalize(lambda + dpsi);
        var (ra, dec) = ToEquatorial(apparentLambda, beta, epsilon);

        // The phase (Meeus 48.2 and 48.3): the elongation from the sun, and the angle sun-moon-earth.
        var (sunLong, sunAu) = Sun.Apparent(t);
        double elongation = Normalize(apparentLambda - sunLong);
        double psi = Math.Acos(Math.Clamp(CosD(beta) * CosD(apparentLambda - sunLong), -1, 1)) * Deg;
        double sunKm = sunAu * 149597870.7;
        double phaseAngle = Math.Atan2(sunKm * SinD(psi), distance - sunKm * CosD(psi)) * Deg;
        return new GeocentricMoon(ra, dec, distance, parallax, apparentLambda, beta, elongation,
                                  phaseAngle, epsilon, dpsi);
    }

    /// <summary>Azimuth and airless altitude seen from the ground (Meeus 11 and 40, sea level).</summary>
    private static (double Azimuth, double Altitude) Topocentric(GeocentricMoon g, double jdUt, double latitude, double longitude)
    {
        double sidereal = MeanSiderealTime(jdUt) + g.NutationLongitude * CosD(g.TrueObliquity);
        double hourAngle = Normalize(sidereal + longitude - g.RightAscension);

        double u = Math.Atan(0.99664719 * Math.Tan(latitude * Rad));
        double rhoSin = 0.99664719 * Math.Sin(u);
        double rhoCos = Math.Cos(u);
        double sinPi = SinD(g.Parallax);
        double denom = CosD(g.Declination) - rhoCos * sinPi * CosD(hourAngle);
        double dAlpha = Math.Atan2(-rhoCos * sinPi * SinD(hourAngle), denom) * Deg;
        double decTopo = Math.Atan2((SinD(g.Declination) - rhoSin * sinPi) * CosD(dAlpha), denom) * Deg;
        return ToHorizontal(hourAngle - dAlpha, decTopo, latitude);
    }

    /// <summary>Meeus table 47.A: multiples of D, M, M', F, then the sine coefficient of longitude and the
    /// cosine coefficient of distance (millionths of a degree, thousandths of a kilometre).</summary>
    private static readonly (int D, int M, int Mp, int F, int L, int R)[] LongitudeDistance =
    {
        (0, 0, 1, 0, 6288774, -20905355), (2, 0, -1, 0, 1274027, -3699111), (2, 0, 0, 0, 658314, -2955968),
        (0, 0, 2, 0, 213618, -569925), (0, 1, 0, 0, -185116, 48888), (0, 0, 0, 2, -114332, -3149),
        (2, 0, -2, 0, 58793, 246158), (2, -1, -1, 0, 57066, -152138), (2, 0, 1, 0, 53322, -170733),
        (2, -1, 0, 0, 45758, -204586), (0, 1, -1, 0, -40923, -129620), (1, 0, 0, 0, -34720, 108743),
        (0, 1, 1, 0, -30383, 104755), (2, 0, 0, -2, 15327, 10321), (0, 0, 1, 2, -12528, 0),
        (0, 0, 1, -2, 10980, 79661), (4, 0, -1, 0, 10675, -34782), (0, 0, 3, 0, 10034, -23210),
        (4, 0, -2, 0, 8548, -21636), (2, 1, -1, 0, -7888, 24208), (2, 1, 0, 0, -6766, 30824),
        (1, 0, -1, 0, -5163, -8379), (1, 1, 0, 0, 4987, -16675), (2, -1, 1, 0, 4036, -12831),
        (2, 0, 2, 0, 3994, -10445), (4, 0, 0, 0, 3861, -11650), (2, 0, -3, 0, 3665, 14403),
        (0, 1, -2, 0, -2689, -7003), (2, 0, -1, 2, -2602, 0), (2, -1, -2, 0, 2390, 10056),
        (1, 0, 1, 0, -2348, 6322), (2, -2, 0, 0, 2236, -9884), (0, 1, 2, 0, -2120, 5751),
        (0, 2, 0, 0, -2069, 0), (2, -2, -1, 0, 2048, -4950), (2, 0, 1, -2, -1773, 4130),
        (2, 0, 0, 2, -1595, 0), (4, -1, -1, 0, 1215, -3958), (0, 0, 2, 2, -1110, 0),
        (3, 0, -1, 0, -892, 3258), (2, 1, 1, 0, -810, 2616), (4, -1, -2, 0, 759, -1897),
        (0, 2, -1, 0, -713, -2117), (2, 2, -1, 0, -700, 2354), (2, 1, -2, 0, 691, 0),
        (2, -1, 0, -2, 596, 0), (4, 0, 1, 0, 549, -1423), (0, 0, 4, 0, 537, -1117),
        (4, -1, 0, 0, 520, -1571), (1, 0, -2, 0, -487, -1739), (2, 1, 0, -2, -399, 0),
        (0, 0, 2, -2, -381, -4421), (1, 1, 1, 0, 351, 0), (3, 0, -2, 0, -340, 0),
        (4, 0, -3, 0, 330, 0), (2, -1, 2, 0, 327, 0), (0, 2, 1, 0, -323, 1165),
        (1, 1, -1, 0, 299, 0), (2, 0, 3, 0, 294, 0), (2, 0, -1, -2, 0, 8752),
    };

    /// <summary>Meeus table 47.B: multiples of D, M, M', F, and the sine coefficient of latitude.</summary>
    private static readonly (int D, int M, int Mp, int F, int B)[] LatitudeTerms =
    {
        (0, 0, 0, 1, 5128122), (0, 0, 1, 1, 280602), (0, 0, 1, -1, 277693), (2, 0, 0, -1, 173237),
        (2, 0, -1, 1, 55413), (2, 0, -1, -1, 46271), (2, 0, 0, 1, 32573), (0, 0, 2, 1, 17198),
        (2, 0, 1, -1, 9266), (0, 0, 2, -1, 8822), (2, -1, 0, -1, 8216), (2, 0, -2, -1, 4324),
        (2, 0, 1, 1, 4200), (2, 1, 0, -1, -3359), (2, -1, -1, 1, 2463), (2, -1, 0, 1, 2211),
        (2, -1, -1, -1, 2065), (0, 1, -1, -1, -1870), (4, 0, -1, -1, 1828), (0, 1, 0, 1, -1794),
        (0, 0, 0, 3, -1749), (0, 1, -1, 1, -1565), (1, 0, 0, 1, -1491), (0, 1, 1, 1, -1475),
        (0, 1, 1, -1, -1410), (0, 1, 0, -1, -1344), (1, 0, 0, -1, -1335), (0, 0, 3, 1, 1107),
        (4, 0, 0, -1, 1021), (4, 0, -1, 1, 833), (0, 0, 1, -3, 777), (4, 0, -2, 1, 671),
        (2, 0, 0, -3, 607), (2, 0, 2, -1, 596), (2, -1, 1, -1, 491), (2, 0, -2, 1, -451),
        (0, 0, 3, -1, 439), (2, 0, 2, 1, 422), (2, 0, -3, -1, 421), (2, 1, -1, 1, -366),
        (2, 1, 0, 1, -351), (4, 0, 0, 1, 331), (2, -1, 1, 1, 315), (2, -2, 0, -1, 302),
        (0, 0, 1, 3, -283), (2, 1, 1, -1, -229), (1, 1, 0, -1, 223), (1, 1, 0, 1, 223),
        (0, 1, -2, -1, -220), (2, 1, -1, -1, -220), (1, 0, 1, 1, -185), (2, -1, -2, -1, 181),
        (0, 1, 2, 1, -177), (4, 0, -2, -1, 176), (4, -1, -1, -1, 166), (1, 0, 1, -1, -164),
        (4, 0, 1, -1, 132), (1, 0, -1, -1, -119), (4, -1, 0, -1, 115), (2, -2, 0, 1, 107),
    };
}

using static OpenFPS.Astronomy.AstroMath;

namespace OpenFPS.Astronomy;

/// <summary>Where the sun is, degrees. Azimuth from true north through east; elevation with refraction.</summary>
public readonly record struct SunPosition(double Azimuth, double Elevation, double TrueElevation,
                                          double Declination, double EquationOfTimeMinutes);

/// <summary>
/// The sun's events in one local day, as UTC instants; null where it does not happen that day (polar day
/// or night, or a twilight that never ends). Dawns are the mornings' crossings, dusks the evenings'.
/// </summary>
public sealed record SunDay(DateTime? Rise, DateTime? Set, DateTime Noon,
                            DateTime? CivilDawn, DateTime? CivilDusk,
                            DateTime? NauticalDawn, DateTime? NauticalDusk,
                            DateTime? AstronomicalDawn, DateTime? AstronomicalDusk);

/// <summary>
/// The sun by NOAA's solar position equations (the solar calculator's spreadsheet method, from Meeus),
/// about 0.01 degree between the polar circles. Like NOAA's calculator it takes UT for the dynamical time:
/// the 69 s between them move the sun 0.001 degree.
/// </summary>
public static class Sun
{
    /// <summary>Sunrise and sunset: the centre 50 arcminutes below the airless horizon (refraction and the
    /// semidiameter), as the Naval Observatory and NOAA both take it.</summary>
    public const double RiseAltitude = -0.833;
    public const double CivilAltitude = -6.0;
    public const double NauticalAltitude = -12.0;
    public const double AstronomicalAltitude = -18.0;

    public static SunPosition Position(DateTime utc, double latitude, double longitude)
    {
        double jd = JulianDay(utc);
        double t = Centuries(jd);
        double eqTime = EquationOfTime(t);
        double decl = Declination(t);
        double minutesUtc = (jd + 0.5 - Math.Floor(jd + 0.5)) * 1440.0;
        double trueSolarTime = minutesUtc + eqTime + 4.0 * longitude;
        double hourAngle = Signed(trueSolarTime / 4.0 - 180.0);
        var (az, alt) = ToHorizontal(hourAngle, decl, latitude);
        return new SunPosition(az, alt + Refraction(alt), alt, decl, eqTime);
    }

    /// <summary>The local day starting at local midnight of <paramref name="date"/> at this UTC offset.</summary>
    public static SunDay Day(DateOnly date, double latitude, double longitude, TimeSpan utcOffset)
    {
        DateTime start = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) - utcOffset;
        DateTime end = start.AddDays(1);
        double Alt(DateTime u) => Position(u, latitude, longitude).TrueElevation;
        (DateTime? Up, DateTime? Down) Pair(double h)
            => (Crossings.First(Alt, h, start, end, rising: true), Crossings.First(Alt, h, start, end, rising: false));
        var rs = Pair(RiseAltitude);
        var civil = Pair(CivilAltitude);
        var nautical = Pair(NauticalAltitude);
        var astro = Pair(AstronomicalAltitude);
        return new SunDay(rs.Up, rs.Down, Noon(start, end, longitude), civil.Up, civil.Down,
                          nautical.Up, nautical.Down, astro.Up, astro.Down);
    }

    /// <summary>The next time after <paramref name="fromUtc"/> the sun crosses this altitude going up (or
    /// down), within <paramref name="days"/>; null if it does not.</summary>
    public static DateTime? Next(DateTime fromUtc, double latitude, double longitude, bool rising,
                                 double altitude = RiseAltitude, double days = 370)
        => Crossings.First(u => Position(u, latitude, longitude).TrueElevation, altitude, fromUtc, fromUtc.AddDays(days), rising);

    /// <summary>Solar noon in [start, end): NOAA's equation of time, evaluated at noon itself.</summary>
    private static DateTime Noon(DateTime start, DateTime end, double longitude)
    {
        DateTime mid = start.AddHours(12);
        DateTime guess = mid;
        for (int i = 0; i < 3; i++)
        {
            double eq = EquationOfTime(Centuries(JulianDay(guess)));
            DateTime utcMidnight = guess.Date;
            guess = utcMidnight.AddMinutes(720.0 - 4.0 * longitude - eq);
            if (guess < start) guess = guess.AddDays(1);
            else if (guess >= end) guess = guess.AddDays(-1);
        }
        return DateTime.SpecifyKind(guess, DateTimeKind.Utc);
    }

    /// <summary>The sun's apparent ecliptic longitude, degrees, and its distance in AU, for the moon's phase.</summary>
    internal static (double Longitude, double DistanceAu) Apparent(double t)
    {
        double trueLong = GeomMeanLong(t) + EquationOfCentre(t);
        double omega = 125.04 - 1934.136 * t;
        double lambda = trueLong - 0.00569 - 0.00478 * SinD(omega);
        double e = Eccentricity(t);
        double v = GeomMeanAnomaly(t) + EquationOfCentre(t);
        double r = 1.000001018 * (1 - e * e) / (1 + e * CosD(v));
        return (Normalize(lambda), r);
    }

    private static double GeomMeanLong(double t) => Normalize(280.46646 + t * (36000.76983 + t * 0.0003032));
    private static double GeomMeanAnomaly(double t) => 357.52911 + t * (35999.05029 - 0.0001537 * t);
    private static double Eccentricity(double t) => 0.016708634 - t * (0.000042037 + 0.0000001267 * t);

    private static double EquationOfCentre(double t)
    {
        double m = GeomMeanAnomaly(t);
        return SinD(m) * (1.914602 - t * (0.004817 + 0.000014 * t)) + SinD(2 * m) * (0.019993 - 0.000101 * t) + SinD(3 * m) * 0.000289;
    }

    private static double ObliquityCorrected(double t) => MeanObliquity(t) + 0.00256 * CosD(125.04 - 1934.136 * t);

    private static double Declination(double t)
    {
        double lambda = Apparent(t).Longitude;
        return Math.Asin(SinD(ObliquityCorrected(t)) * SinD(lambda)) * Deg;
    }

    /// <summary>Minutes of time (NOAA's calcEquationOfTime).</summary>
    private static double EquationOfTime(double t)
    {
        double epsilon = ObliquityCorrected(t);
        double l0 = GeomMeanLong(t);
        double e = Eccentricity(t);
        double m = GeomMeanAnomaly(t);
        double y = Math.Tan(epsilon * Rad / 2.0);
        y *= y;
        double et = y * SinD(2 * l0) - 2 * e * SinD(m) + 4 * e * y * SinD(m) * CosD(2 * l0)
                  - 0.5 * y * y * SinD(4 * l0) - 1.25 * e * e * SinD(2 * m);
        return et * Deg * 4.0;
    }
}

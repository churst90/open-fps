namespace OpenFPS.Astronomy;

/// <summary>Time scales, angles and the sky's frames shared by the sun and the moon.</summary>
internal static class AstroMath
{
    public const double Rad = Math.PI / 180.0;
    public const double Deg = 180.0 / Math.PI;

    /// <summary>The Julian day of a UTC instant (UTC is taken for UT1; they differ by under a second).</summary>
    public static double JulianDay(DateTime utc)
        => (DateTime.SpecifyKind(utc, DateTimeKind.Utc) - DateTime.UnixEpoch).TotalDays + 2440587.5;

    public static DateTime FromJulianDay(double jd)
        => DateTime.UnixEpoch.AddTicks((long)Math.Round((jd - 2440587.5) * TimeSpan.TicksPerDay));

    /// <summary>Julian centuries from J2000.0.</summary>
    public static double Centuries(double jd) => (jd - 2451545.0) / 36525.0;

    /// <summary>
    /// TT minus UT in seconds. IERS measured 69.1 to 69.4 s through 2015 to 2026, flatter than any published
    /// polynomial (Espenak and Meeus's gives 75 s for 2026), so the measured value is held there; the
    /// polynomials of Espenak and Meeus (NASA, 2006) elsewhere. A second is 0.00015 degree of the moon.
    /// </summary>
    public static double DeltaTSeconds(double jd)
    {
        double y = 2000.0 + (jd - 2451545.0) / 365.25;
        if (y >= 2015 && y < 2035) return 69.2;
        if (y >= 2005 && y < 2050) { double t = y - 2000; return 62.92 + 0.32217 * t + 0.005589 * t * t; }
        if (y >= 1986 && y < 2005)
        {
            double t = y - 2000;
            return 63.86 + 0.3345 * t - 0.060374 * t * t + 0.0017275 * t * t * t + 0.000651814 * t * t * t * t
                 + 0.00002373599 * t * t * t * t * t;
        }
        double u = (y - 1820) / 100.0;
        return -20 + 32 * u * u;
    }

    public static double Normalize(double degrees)
    {
        double d = degrees % 360.0;
        return d < 0 ? d + 360.0 : d;
    }

    /// <summary>An angle folded into -180 to 180.</summary>
    public static double Signed(double degrees)
    {
        double d = Normalize(degrees);
        return d > 180.0 ? d - 360.0 : d;
    }

    public static double SinD(double d) => Math.Sin(d * Rad);
    public static double CosD(double d) => Math.Cos(d * Rad);

    /// <summary>Nutation in longitude and obliquity, degrees (Meeus 22, the 0.5" form).</summary>
    public static (double Longitude, double Obliquity) Nutation(double t)
    {
        double omega = 125.04452 - 1934.136261 * t + 0.0020708 * t * t + t * t * t / 450000.0;
        double l = 280.4665 + 36000.7698 * t;
        double lm = 218.3165 + 481267.8813 * t;
        double dpsi = -17.20 * SinD(omega) - 1.32 * SinD(2 * l) - 0.23 * SinD(2 * lm) + 0.21 * SinD(2 * omega);
        double deps = 9.20 * CosD(omega) + 0.57 * CosD(2 * l) + 0.10 * CosD(2 * lm) - 0.09 * CosD(2 * omega);
        return (dpsi / 3600.0, deps / 3600.0);
    }

    /// <summary>The mean obliquity of the ecliptic, degrees (Meeus 22.2).</summary>
    public static double MeanObliquity(double t)
    {
        double seconds = 21.448 - t * (46.8150 + t * (0.00059 - t * 0.001813));
        return 23.0 + (26.0 + seconds / 60.0) / 60.0;
    }

    /// <summary>Greenwich mean sidereal time, degrees, from the UT Julian day (Meeus 12.4).</summary>
    public static double MeanSiderealTime(double jdUt)
    {
        double t = Centuries(jdUt);
        return Normalize(280.46061837 + 360.98564736629 * (jdUt - 2451545.0) + 0.000387933 * t * t - t * t * t / 38710000.0);
    }

    /// <summary>Ecliptic to equatorial, all degrees.</summary>
    public static (double RightAscension, double Declination) ToEquatorial(double lambda, double beta, double epsilon)
    {
        double ra = Math.Atan2(SinD(lambda) * CosD(epsilon) - Math.Tan(beta * Rad) * SinD(epsilon), CosD(lambda)) * Deg;
        double dec = Math.Asin(SinD(beta) * CosD(epsilon) + CosD(beta) * SinD(epsilon) * SinD(lambda)) * Deg;
        return (Normalize(ra), dec);
    }

    /// <summary>Azimuth from true north through east, and altitude, degrees, from a local hour angle.</summary>
    public static (double Azimuth, double Altitude) ToHorizontal(double hourAngle, double declination, double latitude)
    {
        double az = Math.Atan2(SinD(hourAngle), CosD(hourAngle) * SinD(latitude) - Math.Tan(declination * Rad) * CosD(latitude)) * Deg;
        double alt = Math.Asin(Math.Clamp(SinD(latitude) * SinD(declination) + CosD(latitude) * CosD(declination) * CosD(hourAngle), -1.0, 1.0)) * Deg;
        return (Normalize(az + 180.0), alt);
    }

    /// <summary>
    /// The lift refraction gives a body at this airless altitude, degrees: NOAA's solar calculator's formula
    /// (Bennett's above 5 degrees, a fitted polynomial to the horizon, and a fall-off below it).
    /// </summary>
    public static double Refraction(double elevation)
    {
        if (elevation > 85.0) return 0.0;
        double te = Math.Tan(elevation * Rad);
        double seconds;
        if (elevation > 5.0) seconds = 58.1 / te - 0.07 / (te * te * te) + 0.000086 / (te * te * te * te * te);
        else if (elevation > -0.575) seconds = 1735.0 + elevation * (-518.2 + elevation * (103.4 + elevation * (-12.79 + elevation * 0.711)));
        else seconds = -20.774 / te;
        return seconds / 3600.0;
    }
}

namespace OpenFPS.Server.OneWorld;

/// <summary>
/// The Universal Transverse Mercator grid on WGS84 (docs/WORLD_STREAMING.md, Stage 2: one world): zones 6
/// degrees wide, metres east and north in each. The series of USGS Professional Paper 1395 (Snyder 1987),
/// good to millimetres near a zone's middle and centimetres at its edges. The exceptions round Norway and
/// Svalbard are not made: a place there is in its plain zone.
/// </summary>
public static class Utm
{
    private const double A = 6378137.0, F = 1 / 298.257223563, K0 = 0.9996;
    private static readonly double E2 = F * (2 - F), Ep2 = E2 / (1 - E2);
    private static readonly double E4 = E2 * E2, E6 = E4 * E2;
    private const double FalseEasting = 500_000.0, FalseNorthingSouth = 10_000_000.0;

    /// <summary>The zone a longitude is in, 1 to 60.</summary>
    public static int ZoneOf(double lon) => Math.Clamp((int)Math.Floor((lon + 180.0) / 6.0) + 1, 1, 60);

    /// <summary>The longitude down a zone's middle, degrees.</summary>
    public static double CentralMeridian(int zone) => (zone - 1) * 6.0 - 180.0 + 3.0;

    /// <summary>A latitude and longitude in its own zone (north of the equator in the northern half).</summary>
    public static (int Zone, bool North, double Easting, double Northing) FromLatLon(double lat, double lon)
    {
        int zone = ZoneOf(lon);
        var (e, n) = FromLatLon(lat, lon, zone, lat >= 0);
        return (zone, lat >= 0, e, n);
    }

    /// <summary>A latitude and longitude in a given zone and half: what a place just over a zone's edge is in
    /// its neighbour's frame.</summary>
    public static (double Easting, double Northing) FromLatLon(double lat, double lon, int zone, bool north)
    {
        double phi = lat * Math.PI / 180.0;
        double dl = (lon - CentralMeridian(zone)) * Math.PI / 180.0;
        double sin = Math.Sin(phi), cos = Math.Cos(phi), tan = Math.Tan(phi);
        double n = A / Math.Sqrt(1 - E2 * sin * sin);
        double t = tan * tan, c = Ep2 * cos * cos, a = cos * dl;
        double m = Meridian(phi);
        double x = K0 * n * (a + (1 - t + c) * Math.Pow(a, 3) / 6 + (5 - 18 * t + t * t + 72 * c - 58 * Ep2) * Math.Pow(a, 5) / 120);
        double y = K0 * (m + n * tan * (a * a / 2 + (5 - t + 9 * c + 4 * c * c) * Math.Pow(a, 4) / 24
                                         + (61 - 58 * t + t * t + 600 * c - 330 * Ep2) * Math.Pow(a, 6) / 720));
        return (x + FalseEasting, north ? y : y + FalseNorthingSouth);
    }

    /// <summary>A point of a zone back to latitude and longitude, degrees.</summary>
    public static (double Lat, double Lon) ToLatLon(int zone, bool north, double easting, double northing)
    {
        double x = easting - FalseEasting, y = north ? northing : northing - FalseNorthingSouth;
        double m = y / K0;
        double mu = m / (A * (1 - E2 / 4 - 3 * E4 / 64 - 5 * E6 / 256));
        double e1 = (1 - Math.Sqrt(1 - E2)) / (1 + Math.Sqrt(1 - E2));
        double phi1 = mu + (3 * e1 / 2 - 27 * Math.Pow(e1, 3) / 32) * Math.Sin(2 * mu)
                         + (21 * e1 * e1 / 16 - 55 * Math.Pow(e1, 4) / 32) * Math.Sin(4 * mu)
                         + (151 * Math.Pow(e1, 3) / 96) * Math.Sin(6 * mu)
                         + (1097 * Math.Pow(e1, 4) / 512) * Math.Sin(8 * mu);
        double sin = Math.Sin(phi1), cos = Math.Cos(phi1), tan = Math.Tan(phi1);
        double c1 = Ep2 * cos * cos, t1 = tan * tan;
        double n1 = A / Math.Sqrt(1 - E2 * sin * sin);
        double r1 = A * (1 - E2) / Math.Pow(1 - E2 * sin * sin, 1.5);
        double d = x / (n1 * K0);
        double phi = phi1 - (n1 * tan / r1) * (d * d / 2 - (5 + 3 * t1 + 10 * c1 - 4 * c1 * c1 - 9 * Ep2) * Math.Pow(d, 4) / 24
                                                 + (61 + 90 * t1 + 298 * c1 + 45 * t1 * t1 - 252 * Ep2 - 3 * c1 * c1) * Math.Pow(d, 6) / 720);
        double lam = (d - (1 + 2 * t1 + c1) * Math.Pow(d, 3) / 6
                      + (5 - 2 * c1 + 28 * t1 - 3 * c1 * c1 + 8 * Ep2 + 24 * t1 * t1) * Math.Pow(d, 5) / 120) / cos;
        return (phi * 180.0 / Math.PI, CentralMeridian(zone) + lam * 180.0 / Math.PI);
    }

    /// <summary>The distance along the meridian from the equator to latitude <paramref name="phi"/> (radians).</summary>
    private static double Meridian(double phi)
        => A * ((1 - E2 / 4 - 3 * E4 / 64 - 5 * E6 / 256) * phi
                - (3 * E2 / 8 + 3 * E4 / 32 + 45 * E6 / 1024) * Math.Sin(2 * phi)
                + (15 * E4 / 256 + 45 * E6 / 1024) * Math.Sin(4 * phi)
                - (35 * E6 / 3072) * Math.Sin(6 * phi));

    /// <summary>The EPSG code of a zone's WGS84 UTM frame (32615 is zone 15 north), as the 3DEP service takes it.</summary>
    public static int Epsg(int zone, bool north) => (north ? 32600 : 32700) + zone;
}

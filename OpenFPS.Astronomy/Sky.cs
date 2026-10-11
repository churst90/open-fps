namespace OpenFPS.Astronomy;

/// <summary>
/// One body as the readout needs it: bearing and refracted height in degrees, whether it is up, and when that
/// next changes (the set if it is up, the rise if not; null if not within the search).
/// </summary>
public sealed record BodyNow(double Azimuth, double Elevation, bool IsUp, DateTime? Next);

/// <summary>The sun and the moon at one instant and place. <see cref="MoonDetail"/> carries the phase.</summary>
public sealed record SkyNow(DateTime Utc, BodyNow Sun, BodyNow Moon, MoonPosition MoonDetail);

/// <summary>The sun and the moon together, for the readout key and /sky (docs/SKY.md).</summary>
public static class Sky
{
    public static SkyNow At(DateTime utc, double latitude, double longitude)
    {
        utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        var sun = Sun.Position(utc, latitude, longitude);
        bool sunUp = sun.TrueElevation >= Sun.RiseAltitude;
        var sunNext = Sun.Next(utc, latitude, longitude, rising: !sunUp);

        var moon = Moon.Position(utc, latitude, longitude);
        bool moonUp = Moon.IsUp(utc, latitude, longitude);
        var moonNext = Moon.Next(utc, latitude, longitude, rising: !moonUp);

        return new SkyNow(utc, new BodyNow(sun.Azimuth, sun.Elevation, sunUp, sunNext),
                          new BodyNow(moon.Azimuth, moon.Elevation, moonUp, moonNext), moon);
    }

    /// <summary>The readout for this instant and place, its clock times in <paramref name="zone"/>.</summary>
    public static string Readout(DateTime utc, double latitude, double longitude, TimeZoneInfo zone,
                                 Func<double, string>? bearing = null)
        => SkyWords.Readout(At(utc, latitude, longitude), zone, bearing);
}

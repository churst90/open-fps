"""
The Universal Transverse Mercator grid on WGS84, as OpenFPS.Server/OneWorld/Utm.cs has it (USGS
Professional Paper 1395's series, no Norway or Svalbard exceptions): the world's tiles are 250 m squares
of it, and the maps of real places are laid on it (tools/gen_osm.py), so a map is the world's tiles moved,
never turned. Keep the two in step.
"""
import math

A, F, K0 = 6378137.0, 1 / 298.257223563, 0.9996
E2 = F * (2 - F)
EP2 = E2 / (1 - E2)
E4, E6 = E2 * E2, E2 * E2 * E2
FALSE_EASTING, FALSE_NORTHING_SOUTH = 500_000.0, 10_000_000.0
TILE = 250.0


def zone_of(lon):
    return min(max(int(math.floor((lon + 180.0) / 6.0)) + 1, 1), 60)


def central_meridian(zone):
    return (zone - 1) * 6.0 - 180.0 + 3.0


def epsg(zone, north):
    return (32600 if north else 32700) + zone


def _meridian(phi):
    return A * ((1 - E2 / 4 - 3 * E4 / 64 - 5 * E6 / 256) * phi
                - (3 * E2 / 8 + 3 * E4 / 32 + 45 * E6 / 1024) * math.sin(2 * phi)
                + (15 * E4 / 256 + 45 * E6 / 1024) * math.sin(4 * phi)
                - (35 * E6 / 3072) * math.sin(6 * phi))


def from_latlon(lat, lon, zone, north):
    """Easting and northing of a latitude and longitude in a given zone and half."""
    phi = math.radians(lat)
    dl = math.radians(lon - central_meridian(zone))
    sin, cos, tan = math.sin(phi), math.cos(phi), math.tan(phi)
    n = A / math.sqrt(1 - E2 * sin * sin)
    t, c, a = tan * tan, EP2 * cos * cos, cos * dl
    m = _meridian(phi)
    x = K0 * n * (a + (1 - t + c) * a ** 3 / 6 + (5 - 18 * t + t * t + 72 * c - 58 * EP2) * a ** 5 / 120)
    y = K0 * (m + n * tan * (a * a / 2 + (5 - t + 9 * c + 4 * c * c) * a ** 4 / 24
                             + (61 - 58 * t + t * t + 600 * c - 330 * EP2) * a ** 6 / 720))
    return x + FALSE_EASTING, (y if north else y + FALSE_NORTHING_SOUTH)


def to_latlon(zone, north, easting, northing):
    """A point of a zone back to latitude and longitude, degrees."""
    x = easting - FALSE_EASTING
    y = northing if north else northing - FALSE_NORTHING_SOUTH
    m = y / K0
    mu = m / (A * (1 - E2 / 4 - 3 * E4 / 64 - 5 * E6 / 256))
    e1 = (1 - math.sqrt(1 - E2)) / (1 + math.sqrt(1 - E2))
    phi1 = (mu + (3 * e1 / 2 - 27 * e1 ** 3 / 32) * math.sin(2 * mu)
            + (21 * e1 * e1 / 16 - 55 * e1 ** 4 / 32) * math.sin(4 * mu)
            + (151 * e1 ** 3 / 96) * math.sin(6 * mu)
            + (1097 * e1 ** 4 / 512) * math.sin(8 * mu))
    sin, cos, tan = math.sin(phi1), math.cos(phi1), math.tan(phi1)
    c1, t1 = EP2 * cos * cos, tan * tan
    n1 = A / math.sqrt(1 - E2 * sin * sin)
    r1 = A * (1 - E2) / (1 - E2 * sin * sin) ** 1.5
    d = x / (n1 * K0)
    phi = phi1 - (n1 * tan / r1) * (d * d / 2 - (5 + 3 * t1 + 10 * c1 - 4 * c1 * c1 - 9 * EP2) * d ** 4 / 24
                                    + (61 + 90 * t1 + 298 * c1 + 45 * t1 * t1 - 252 * EP2 - 3 * c1 * c1) * d ** 6 / 720)
    lam = (d - (1 + 2 * t1 + c1) * d ** 3 / 6
           + (5 - 2 * c1 + 28 * t1 - 3 * c1 * c1 + 8 * EP2 + 24 * t1 * t1) * d ** 5 / 120) / cos
    return math.degrees(phi), central_meridian(zone) + math.degrees(lam)


def origin_of(lat, lon):
    """The zone, half and origin a place's map is laid from: the 2 m post of the zone nearest its origin, so
    the map's posts and the world's are the same posts."""
    zone, north = zone_of(lon), lat >= 0
    e, n = from_latlon(lat, lon, zone, north)
    return zone, north, 2.0 * round(e / 2.0), 2.0 * round(n / 2.0)

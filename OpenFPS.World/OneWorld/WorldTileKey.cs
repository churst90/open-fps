using System.Globalization;

namespace OpenFPS.Server.OneWorld;

/// <summary>
/// A tile of the one world (docs/WORLD_STREAMING.md, Stage 2: one world, decided by Cody 2026-10-06): the
/// UTM zone and half, and which 250 m square of it, counted from the zone's own origin. Written
/// "15N/1023/13345": zone 15 north, eastings 255,750 to 256,000, northings 3,336,250 to 3,336,500.
/// </summary>
public readonly record struct WorldTileKey(int Zone, bool North, int X, int Z)
{
    public const double TileMetres = 250.0;

    /// <summary>The square a point of a zone is in.</summary>
    public static WorldTileKey Of(int zone, bool north, double easting, double northing)
        => new(zone, north, (int)Math.Floor(easting / TileMetres), (int)Math.Floor(northing / TileMetres));

    /// <summary>The square a latitude and longitude is in, in its own zone.</summary>
    public static WorldTileKey OfLatLon(double lat, double lon)
    {
        var (zone, north, e, n) = Utm.FromLatLon(lat, lon);
        return Of(zone, north, e, n);
    }

    public double Easting => X * TileMetres;
    public double Northing => Z * TileMetres;

    /// <summary>The square <paramref name="dx"/> east and <paramref name="dz"/> north of this one, in the same zone.</summary>
    public WorldTileKey Offset(int dx, int dz) => this with { X = X + dx, Z = Z + dz };

    public override string ToString() => $"{Zone}{(North ? 'N' : 'S')}/{X}/{Z}";

    /// <summary>"15N/1023/13345" back to a key; false for anything else.</summary>
    public static bool TryParse(string? s, out WorldTileKey key)
    {
        key = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        var parts = s.Trim().Split('/');
        if (parts.Length != 3 || parts[0].Length < 2) return false;
        char half = char.ToUpperInvariant(parts[0][^1]);
        if (half != 'N' && half != 'S') return false;
        if (!int.TryParse(parts[0][..^1], NumberStyles.None, CultureInfo.InvariantCulture, out int zone) || zone < 1 || zone > 60) return false;
        if (!int.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int x)) return false;
        if (!int.TryParse(parts[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int z)) return false;
        key = new WorldTileKey(zone, half == 'N', x, z);
        return true;
    }

    /// <summary>Where the tile is kept under a store's version folder: zone and half, then x, then z.</summary>
    public string RelativePath => Path.Combine($"{Zone}{(North ? 'N' : 'S')}", X.ToString(CultureInfo.InvariantCulture), Z.ToString(CultureInfo.InvariantCulture));
}

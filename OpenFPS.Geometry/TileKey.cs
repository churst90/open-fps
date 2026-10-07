using System.Numerics;

namespace OpenFPS.Common;

/// <summary>
/// One square of a streamed map: TileMetres on a side, counted from the map's origin, x east and z
/// north. Tile (x, z) covers x·T ≤ X &lt; (x+1)·T and z·T ≤ Z &lt; (z+1)·T, which is what
/// tools/gen_osm.py writes in each entity's "Tile". See docs/WORLD_STREAMING.md.
/// </summary>
public readonly record struct TileKey(int X, int Z)
{
    public static TileKey Of(Vector3 position, float tileMetres)
        => new((int)MathF.Floor(position.X / tileMetres), (int)MathF.Floor(position.Z / tileMetres));

    /// <summary>How far a point is from this tile's square, horizontally: 0 inside it.</summary>
    public float DistanceFrom(Vector3 position, float tileMetres)
    {
        float x0 = X * tileMetres, z0 = Z * tileMetres;
        float dx = MathF.Max(0f, MathF.Max(x0 - position.X, position.X - (x0 + tileMetres)));
        float dz = MathF.Max(0f, MathF.Max(z0 - position.Z, position.Z - (z0 + tileMetres)));
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    public override string ToString() => $"{X},{Z}";
}

using System;
using System.Collections.Generic;
using System.Numerics;
using MemoryPack;

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

/// <summary>How much of a tile a client has. Ordered: more detail is a bigger number.</summary>
public enum TileDetail : byte
{
    None = 0,
    /// <summary>The ground, roads, building shells, rail and water: what keeps far buildings in the
    /// way of sound and far traffic on a road.</summary>
    Coarse = 1,
    Full = 2,
}

/// <summary>A tile and how much of it, on the wire (TileStreamUpdate).</summary>
[MemoryPackable]
public partial struct TileState
{
    public int X;
    public int Z;
    public TileDetail Detail;

    public TileState(TileKey key, TileDetail detail) { X = key.X; Z = key.Z; Detail = detail; }
    [MemoryPackIgnore] public TileKey Key => new(X, Z);
}

/// <summary>
/// How far round a player a streamed map is loaded: everything within <see cref="FullMetres"/>, the
/// coarse layer out to <see cref="FarMetres"/>, nothing beyond. The client's world detail setting
/// (low, medium, high) picks one of the presets; the server clamps whatever it is asked for.
/// </summary>
public readonly record struct StreamRadii(float FullMetres, float FarMetres)
{
    public static readonly StreamRadii Low = new(150f, 500f);
    public static readonly StreamRadii Medium = new(300f, 800f);
    public static readonly StreamRadii High = new(500f, 1200f);
    public static StreamRadii Default => Medium;

    public const float MinFullMetres = 100f, MaxFullMetres = 600f, MaxFarMetres = 1500f;

    /// <summary>A tile is upgraded at its radius and kept until the player is this much past it, so a
    /// walk along a tile edge does not load and drop the same tile over and over.</summary>
    public const float HysteresisMetres = 50f;

    /// <summary>What the server will actually do for a request: 0 asks for the default; the full radius
    /// within its limits, and the far radius no nearer than the full one and no further than the limit.</summary>
    public static StreamRadii Clamp(float full, float far)
    {
        if (!float.IsFinite(full) || full <= 0f) full = Default.FullMetres;
        if (!float.IsFinite(far) || far <= 0f) far = MathF.Max(full, Default.FarMetres);
        full = Math.Clamp(full, MinFullMetres, MaxFullMetres);
        far = Math.Clamp(far, full, MaxFarMetres);
        return new StreamRadii(full, far);
    }

    /// <summary>"low", "medium" or "high", or null for a word that is none of them.</summary>
    public static StreamRadii? Named(string? level) => level?.Trim().ToLowerInvariant() switch
    {
        "low" => Low,
        "medium" or "med" => Medium,
        "high" => High,
        _ => null,
    };
}

/// <summary>Which tiles a player should have, and at what detail. Pure: the server's interest and the
/// tests share it.</summary>
public static class TileSelection
{
    /// <summary>
    /// The level each tile should be at for a player at <paramref name="at"/>, given what they have now
    /// (<paramref name="current"/>, for the hysteresis), over the tiles from <paramref name="min"/> to
    /// <paramref name="max"/> inclusive (the map's). Tiles at None are left out. The tile the player
    /// stands in is always full.
    /// </summary>
    public static Dictionary<TileKey, TileDetail> Desired(Vector3 at, float tileMetres, StreamRadii radii,
        IReadOnlyDictionary<TileKey, TileDetail>? current, TileKey min, TileKey max,
        float hysteresis = StreamRadii.HysteresisMetres)
    {
        var result = new Dictionary<TileKey, TileDetail>();
        if (tileMetres <= 0f) return result;
        float reach = MathF.Max(radii.FullMetres, radii.FarMetres) + hysteresis;
        var lo = TileKey.Of(at - new Vector3(reach, 0f, reach), tileMetres);
        var hi = TileKey.Of(at + new Vector3(reach, 0f, reach), tileMetres);
        int x0 = Math.Max(lo.X, min.X), x1 = Math.Min(hi.X, max.X);
        int z0 = Math.Max(lo.Z, min.Z), z1 = Math.Min(hi.Z, max.Z);
        var here = TileKey.Of(at, tileMetres);
        for (int x = x0; x <= x1; x++)
        for (int z = z0; z <= z1; z++)
        {
            var key = new TileKey(x, z);
            float d = key.DistanceFrom(at, tileMetres);
            var had = current != null && current.TryGetValue(key, out var h) ? h : TileDetail.None;
            var level = Level(d, radii, had, hysteresis);
            if (key == here) level = TileDetail.Full;
            if (level != TileDetail.None) result[key] = level;
        }
        return result;
    }

    /// <summary>The level for a tile <paramref name="distance"/> away that is at <paramref name="had"/> now.</summary>
    public static TileDetail Level(float distance, StreamRadii radii, TileDetail had, float hysteresis = StreamRadii.HysteresisMetres)
    {
        if (distance <= radii.FullMetres) return TileDetail.Full;
        if (had == TileDetail.Full && distance <= radii.FullMetres + hysteresis) return TileDetail.Full;
        if (radii.FarMetres > radii.FullMetres)
        {
            if (distance <= radii.FarMetres) return TileDetail.Coarse;
            if (had >= TileDetail.Coarse && distance <= radii.FarMetres + hysteresis) return TileDetail.Coarse;
        }
        else if (had >= TileDetail.Coarse && distance <= radii.FullMetres + hysteresis)
        {
            // No coarse ring: a tile on its way out stays as it was until it is past the margin.
            return had;
        }
        return TileDetail.None;
    }
}

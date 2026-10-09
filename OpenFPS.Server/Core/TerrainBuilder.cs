using System.Numerics;
using Arch.Core;
using OpenFPS.Common.Components;
using OpenFPS.Server.Repositories;

namespace OpenFPS.Server.Core;

/// <summary>
/// Lays a map's terrain from its survey (docs/GEOMETRY.md 5.1): 2 m posts in 250 m tiles, the survey's
/// heights between its own posts, then graded to what rests on the ground. Under every slab lying on it (a
/// road, a lawn, a house's floor) the ground is flattened to the slab's underside, and every post of a cell
/// the slab covers is cut down to it, so no part of a triangle of ground can rise through a slab; the
/// ground round a graded patch is blended back to the survey over a few metres. Everything else that
/// stands on the ground was set into it by the generator.
///
/// <para>Graded over the whole map at once, so the posts on a tile's edge are the same for both tiles.</para>
/// </summary>
public static class TerrainBuilder
{
    /// <summary>The size of a tile of ground, metres: the streaming tiles'.</summary>
    public const float TileMetres = 250f;

    /// <summary>How far round a graded patch the ground is blended back to the survey, metres.</summary>
    public const float SkirtMetres = 4f;

    /// <summary>How far a slab's underside may be over the ground and still rest on it (the ground is
    /// filled up to it), and how far under it (the ground is cut down to it), metres. A floor further up
    /// than that is a floor of a building, not something on the ground.</summary>
    public const float MaxFill = 1.5f, MaxCut = 3f;

    /// <summary>A slab lying on the ground: its underside's plane (a point on it and its upward normal)
    /// and the four corners of its underside seen from above.</summary>
    public readonly record struct Slab(Vector3 Point, Vector3 Up, Vector2 A, Vector2 B, Vector2 C, Vector2 D)
    {
        /// <summary>The underside's height at (x, z).</summary>
        public float HeightAt(float x, float z) => Point.Y - (Up.X * (x - Point.X) + Up.Z * (z - Point.Z)) / Up.Y;

        public bool Covers(float x, float z)
        {
            var p = new Vector2(x, z);
            float s1 = Cross(B - A, p - A), s2 = Cross(C - B, p - B), s3 = Cross(D - C, p - C), s4 = Cross(A - D, p - D);
            return (s1 >= 0 && s2 >= 0 && s3 >= 0 && s4 >= 0) || (s1 <= 0 && s2 <= 0 && s3 <= 0 && s4 <= 0);
        }

        /// <summary>How far (x, z) is from the underside's footprint, seen from above; 0 inside it.</summary>
        public float DistanceFrom(float x, float z)
        {
            if (Covers(x, z)) return 0f;
            var p = new Vector2(x, z);
            return MathF.Min(MathF.Min(Edge(A, B, p), Edge(B, C, p)), MathF.Min(Edge(C, D, p), Edge(D, A, p)));
        }

        public (Vector2 Min, Vector2 Max) Bounds => (Vector2.Min(Vector2.Min(A, B), Vector2.Min(C, D)), Vector2.Max(Vector2.Max(A, B), Vector2.Max(C, D)));

        private static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;

        private static float Edge(Vector2 a, Vector2 b, Vector2 p)
        {
            var ab = b - a;
            float t = Math.Clamp(Vector2.Dot(p - a, ab) / MathF.Max(1e-9f, ab.LengthSquared()), 0f, 1f);
            return Vector2.Distance(p, a + ab * t);
        }
    }

    /// <summary>One tile of laid ground: its south-west corner and its heights, row by row.</summary>
    public sealed record Tile(int X, int Z, float CornerX, float CornerZ, int Posts, float Spacing, float[] Heights);

    /// <summary>The slabs of a world that could rest on the ground: fixed, solid, boxes, thin and level
    /// enough to be a floor (at most 0.6 m thick, at least a metre each way, tilted under 30 degrees).</summary>
    public static List<Slab> Slabs(World world)
    {
        var slabs = new List<Slab>();
        var q = new QueryDescription().WithAll<Transform, ColliderComponent>();
        world.Query(in q, (Entity e, ref Transform t, ref ColliderComponent c) =>
        {
            if (!c.IsSolid || c.Shape != ColliderShape.Box || c.Form != null) return;
            if (world.Has<Velocity>(e) || world.Has<PlayerComponent>(e) || world.Has<DoorComponent>(e)) return;
            if (world.Has<EntityType>(e) && world.Get<EntityType>(e) != EntityType.StaticObject) return;
            var s = c.Size;
            if (s.Y > 0.6f || s.X < 1f || s.Z < 1f) return;
            var r = t.Rotation.LengthSquared() < 1e-6f ? Quaternion.Identity : Quaternion.Normalize(t.Rotation);
            var up = Vector3.Transform(Vector3.UnitY, r);
            if (up.Y < 0.866f) return;
            var centre = t.Position;
            var bottom = centre - up * (s.Y * 0.5f);
            Vector2 Corner(float sx, float sz)
            {
                var w = centre + Vector3.Transform(new Vector3(sx * s.X * 0.5f, -s.Y * 0.5f, sz * s.Z * 0.5f), r);
                return new Vector2(w.X, w.Z);
            }
            slabs.Add(new Slab(bottom, up, Corner(-1, -1), Corner(1, -1), Corner(1, 1), Corner(-1, 1)));
        });
        return slabs;
    }

    /// <summary>
    /// The terrain over [min, max] (x and z), in whole tiles of <see cref="TileMetres"/>: the survey's
    /// heights at every post, graded to <paramref name="slabs"/>.
    /// </summary>
    public static List<Tile> Lay(MapElevation elevation, IReadOnlyList<Slab> slabs, Vector3 min, Vector3 max, float spacing = TerrainTiles.Spacing)
    {
        int perTile = (int)MathF.Round(TileMetres / spacing);
        int tx0 = (int)MathF.Floor(min.X / TileMetres), tx1 = (int)MathF.Floor((max.X - 1e-3f) / TileMetres);
        int tz0 = (int)MathF.Floor(min.Z / TileMetres), tz1 = (int)MathF.Floor((max.Z - 1e-3f) / TileMetres);
        int nx = (tx1 - tx0 + 1) * perTile + 1, nz = (tz1 - tz0 + 1) * perTile + 1;
        float x0 = tx0 * TileMetres, z0 = tz0 * TileMetres;

        var raw = new float[nx * nz];
        System.Threading.Tasks.Parallel.For(0, nz, j =>
        {
            for (int i = 0; i < nx; i++) raw[j * nx + i] = (float)elevation.HeightAt(x0 + i * spacing, z0 + j * spacing);
        });

        var h = Grade(raw, nx, nz, x0, z0, spacing, slabs);

        var tiles = new List<Tile>();
        int posts = perTile + 1;
        for (int tz = tz0; tz <= tz1; tz++)
            for (int tx = tx0; tx <= tx1; tx++)
            {
                var heights = new float[posts * posts];
                int oi = (tx - tx0) * perTile, oj = (tz - tz0) * perTile;
                for (int j = 0; j < posts; j++)
                    Array.Copy(h, (oj + j) * nx + oi, heights, j * posts, posts);
                tiles.Add(new Tile(tx, tz, tx * TileMetres, tz * TileMetres, posts, spacing, heights));
            }
        return tiles;
    }

    /// <summary>The posts graded to the slabs, and blended back to <paramref name="raw"/> round them.</summary>
    internal static float[] Grade(float[] raw, int nx, int nz, float x0, float z0, float spacing, IReadOnlyList<Slab> slabs)
    {
        var flat = new float[raw.Length];
        var cut = new float[raw.Length];
        Array.Fill(flat, float.PositiveInfinity);
        Array.Fill(cut, float.PositiveInfinity);
        float reach = spacing * 1.5f;
        foreach (var s in slabs)
        {
            var (lo, hi) = s.Bounds;
            int i0 = Math.Max(0, (int)MathF.Floor((lo.X - reach - x0) / spacing)), i1 = Math.Min(nx - 1, (int)MathF.Ceiling((hi.X + reach - x0) / spacing));
            int j0 = Math.Max(0, (int)MathF.Floor((lo.Y - reach - z0) / spacing)), j1 = Math.Min(nz - 1, (int)MathF.Ceiling((hi.Y + reach - z0) / spacing));
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                {
                    float x = x0 + i * spacing, z = z0 + j * spacing;
                    float d = s.DistanceFrom(x, z);
                    if (d > reach) continue;
                    int k = j * nx + i;
                    float under = s.HeightAt(x, z);
                    // A slab too far over the ground here is a floor of something, not lying on it; too far
                    // under it, a cellar's.
                    float above = under - raw[k];
                    if (above > MaxFill || above < -MaxCut) continue;
                    if (d == 0f) flat[k] = MathF.Min(flat[k], under);
                    // Every corner of a cell the slab covers: a post a cell's diagonal from it at most.
                    cut[k] = MathF.Min(cut[k], under);
                }
        }

        var h = new float[raw.Length];
        var changed = new bool[raw.Length];
        for (int k = 0; k < h.Length; k++)
        {
            float v = float.IsPositiveInfinity(flat[k]) ? raw[k] : flat[k];
            v = MathF.Min(v, cut[k]);
            h[k] = v;
            changed[k] = v != raw[k];
        }

        // Back to the survey round each graded patch: the nearest graded post's change, falling to nothing
        // SkirtMetres out.
        int r = (int)MathF.Ceiling(SkirtMetres / spacing);
        var skirt = (float[])h.Clone();
        System.Threading.Tasks.Parallel.For(0, nz, j =>
        {
            for (int i = 0; i < nx; i++)
            {
                int k = j * nx + i;
                if (changed[k]) continue;
                float bestD = float.MaxValue, bestDelta = 0f;
                for (int dj = -r; dj <= r; dj++)
                {
                    int jj = j + dj;
                    if (jj < 0 || jj >= nz) continue;
                    for (int di = -r; di <= r; di++)
                    {
                        int ii = i + di;
                        if (ii < 0 || ii >= nx) continue;
                        int kk = jj * nx + ii;
                        if (!changed[kk]) continue;
                        float d = spacing * MathF.Sqrt(di * di + dj * dj);
                        float delta = h[kk] - raw[kk];
                        if (d < bestD || (d == bestD && MathF.Abs(delta) > MathF.Abs(bestDelta))) { bestD = d; bestDelta = delta; }
                    }
                }
                if (bestD < SkirtMetres + spacing) skirt[k] = raw[k] + bestDelta * MathF.Max(0f, 1f - bestD / (SkirtMetres + spacing));
                // Never up through the edge of a slab the post is a corner of.
                skirt[k] = MathF.Min(skirt[k], cut[k]);
            }
        });
        return skirt;
    }

    /// <summary>The tiles as entities of a world: each a tile of ground (TerrainTiles), dirt.</summary>
    public static List<Entity> Spawn(World world, IEnumerable<Tile> tiles)
    {
        var made = new List<Entity>();
        foreach (var t in tiles)
        {
            var c = TerrainTiles.Component(t.Posts, t.Spacing, t.Heights, out float baseY);
            made.Add(TerrainTiles.Spawn(world, t.CornerX, t.CornerZ, baseY, c));
        }
        return made;
    }
}

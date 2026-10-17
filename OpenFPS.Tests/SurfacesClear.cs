using System.Numerics;
using OpenFPS.Geometry;
using OpenFPS.Server.OneWorld;
using OpenFPS.Server.Repositories;

namespace OpenFPS.Tests;

/// <summary>
/// Nothing laid along a line goes into a building (docs/GEOMETRY.md 12.6, "A road stops at a wall"): no piece of a
/// road, verge, sidewalk, kerb, drive or path (the roads, verges, paths and drives layers) overlaps a building's
/// footprint, an upright prism in the structure layer (a building's slab or ceiling, or a shed that is one solid).
/// Until 2026-10-11 nine rooms on the real places had a neighbour's driveway or a service road lying 6 to 8 cm over
/// their floors. Read from layers and geometry, never from names (they are only reported).
/// </summary>
internal static class SurfacesClear
{
    /// <summary>The most a piece may overlap a footprint, m²: a map's numbers round to a tenth of a millimetre.</summary>
    public const double Most = 0.005;
    private const float Cell = 20f;

    private static readonly string[] Laid = { "roads", "verges", "paths", "drives" };

    private sealed record Footprint(List<Vector2> Ring, string Name, Vector2 Lo, Vector2 Hi);

    /// <summary>Pieces looked at, footprints, and each piece that overlaps one (what, where, how much), at most 40.</summary>
    public sealed record Result(int Pieces, int Footprints, int Overlapping, List<string> Found);

    /// <summary>Every piece of <paramref name="entities"/> against every footprint, each entity moved by its offset
    /// (tiles' entities into one frame).</summary>
    public static Result Check(IEnumerable<(EntityData E, Vector3 Offset)> entities, IReadOnlyDictionary<string, WorldBuildings.Prefab> prefabs)
    {
        var list = entities.ToList();
        var feet = new List<Footprint>();
        var grid = new Dictionary<(int, int), List<int>>();
        foreach (var (e, off) in list)
        {
            if (e.Layer != "structure" || e.Form is not { Kind: ShapeKind.Prism, Outline: { Length: >= 6 } o }) continue;
            var r = Turn(e.Rotation);
            if (Vector3.Transform(Vector3.UnitY, r).Y < 0.999f) continue;
            var ring = new List<Vector2>(o.Length / 2);
            for (int i = 0; i < o.Length / 2; i++)
            {
                var w = Vector3.Transform(new Vector3(o[2 * i], 0f, o[2 * i + 1]), r) + e.Position + off;
                ring.Add(new Vector2(w.X, w.Z));
            }
            var lo = new Vector2(ring.Min(p => p.X), ring.Min(p => p.Y));
            var hi = new Vector2(ring.Max(p => p.X), ring.Max(p => p.Y));
            int k = feet.Count;
            feet.Add(new Footprint(ring, e.Name ?? e.PrefabId, lo, hi));
            foreach (var c in Cells(lo, hi))
            {
                if (!grid.TryGetValue(c, out var cell)) grid[c] = cell = new List<int>();
                cell.Add(k);
            }
        }

        int pieces = 0, overlapping = 0;
        var found = new List<string>();
        foreach (var (e, off) in list)
        {
            if (!Laid.Contains(e.Layer)) continue;
            pieces++;
            var size = prefabs.TryGetValue(e.PrefabId, out var pf) ? pf.Size : Vector3.One;
            var half = size * e.Scale * 0.5f;
            var r = Turn(e.Rotation);
            var quad = new List<Vector2>(4);
            foreach (var (sx, sz) in new[] { (-1f, -1f), (1f, -1f), (1f, 1f), (-1f, 1f) })
            {
                var w = Vector3.Transform(new Vector3(sx * half.X, half.Y, sz * half.Z), r) + e.Position + off;
                quad.Add(new Vector2(w.X, w.Z));
            }
            var lo = new Vector2(quad.Min(p => p.X), quad.Min(p => p.Y));
            var hi = new Vector2(quad.Max(p => p.X), quad.Max(p => p.Y));
            // Clipped about the piece's middle: a map's coordinates run to a few kilometres, and a float there
            // carries a tenth of a millimetre.
            var mid = (lo + hi) / 2;
            var near = quad.Select(p => p - mid).ToList();
            var seen = new HashSet<int>();
            double worst = 0;
            string? where = null;
            foreach (var c in Cells(lo, hi))
            {
                if (!grid.TryGetValue(c, out var cell)) continue;
                foreach (int k in cell)
                {
                    var f = feet[k];
                    if (!seen.Add(k) || f.Hi.X < lo.X || f.Lo.X > hi.X || f.Hi.Y < lo.Y || f.Lo.Y > hi.Y) continue;
                    double a = Area(Clip(f.Ring.Select(p => p - mid).ToList(), near));
                    if (a > worst) { worst = a; where = f.Name; }
                }
            }
            if (worst <= Most) continue;
            overlapping++;
            if (found.Count < 40)
                found.Add($"{e.Name ?? e.PrefabId} ({e.Layer}, {e.EntityId}) at ({e.Position.X + off.X:F1}, {e.Position.Z + off.Z:F1}): {worst:F2} m² in {where}");
        }
        return new Result(pieces, feet.Count, overlapping, found);
    }

    public static Result Check(IEnumerable<EntityData> entities, IReadOnlyDictionary<string, WorldBuildings.Prefab> prefabs)
        => Check(entities.Select(e => (e, Vector3.Zero)), prefabs);

    private static Quaternion Turn(Quaternion q) => q.LengthSquared() < 1e-6f ? Quaternion.Identity : Quaternion.Normalize(q);

    private static IEnumerable<(int, int)> Cells(Vector2 lo, Vector2 hi)
    {
        for (int i = (int)MathF.Floor(lo.X / Cell); i <= (int)MathF.Floor(hi.X / Cell); i++)
            for (int j = (int)MathF.Floor(lo.Y / Cell); j <= (int)MathF.Floor(hi.Y / Cell); j++)
                yield return (i, j);
    }

    /// <summary>A polygon clipped to a convex one (Sutherland-Hodgman), either way round.</summary>
    private static List<Vector2> Clip(List<Vector2> subject, List<Vector2> convex)
    {
        double turn = 0;
        for (int i = 0; i < convex.Count; i++) turn += Cross(convex[i], convex[(i + 1) % convex.Count]);
        float sign = turn >= 0 ? 1f : -1f;
        var poly = subject;
        for (int i = 0; i < convex.Count && poly.Count > 0; i++)
        {
            Vector2 a = convex[i], b = convex[(i + 1) % convex.Count];
            float Side(Vector2 p) => sign * ((b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X));
            var outp = new List<Vector2>(poly.Count + 4);
            for (int k = 0; k < poly.Count; k++)
            {
                Vector2 p = poly[k], q = poly[(k + 1) % poly.Count];
                float dp = Side(p), dq = Side(q);
                if (dp >= 0) outp.Add(p);
                if ((dp >= 0) != (dq >= 0)) outp.Add(p + (q - p) * (dp / (dp - dq)));
            }
            poly = outp;
        }
        return poly;
    }

    private static double Cross(Vector2 p, Vector2 q) => (double)p.X * q.Y - (double)q.X * p.Y;

    private static double Area(List<Vector2> poly)
    {
        double a = 0;
        for (int i = 0; i < poly.Count; i++) a += Cross(poly[i], poly[(i + 1) % poly.Count]);
        return Math.Abs(a) / 2;
    }
}

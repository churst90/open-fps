namespace OpenFPS.Common.Geometry;

/// <summary>A point in a plane, in doubles: the shapes' outlines and profiles are worked out in these.</summary>
public readonly record struct Point2(double X, double Y)
{
    public static Point2 operator +(Point2 a, Point2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Point2 operator -(Point2 a, Point2 b) => new(a.X - b.X, a.Y - b.Y);
    public static Point2 operator *(Point2 a, double s) => new(a.X * s, a.Y * s);
    public static double Cross(Point2 a, Point2 b) => a.X * b.Y - a.Y * b.X;
    public static double Dot(Point2 a, Point2 b) => a.X * b.X + a.Y * b.Y;
    public double Length => Math.Sqrt(X * X + Y * Y);
}

/// <summary>
/// Plane polygons for the shape library (docs/GEOMETRY.md 2.6): orientation, simplifying a traced outline,
/// cutting an outline with holes into triangles, and merging those into convex pieces. Plain arithmetic and
/// square roots only, so the server and every client make the same triangles of the same numbers.
/// </summary>
public static class Polygons
{
    /// <summary>Twice the signed area: positive when the points go counter-clockwise (x right, y up).</summary>
    public static double SignedArea2(IReadOnlyList<Point2> ring)
    {
        double a = 0;
        for (int i = 0, n = ring.Count; i < n; i++)
        {
            var p = ring[i]; var q = ring[(i + 1) % n];
            a += p.X * q.Y - q.X * p.Y;
        }
        return a;
    }

    /// <summary>The ring counter-clockwise (reversed if it was not), without a repeated closing point.</summary>
    public static List<Point2> CounterClockwise(IReadOnlyList<Point2> ring)
    {
        var r = new List<Point2>(ring);
        if (r.Count > 1 && r[0] == r[^1]) r.RemoveAt(r.Count - 1);
        if (SignedArea2(r) < 0) r.Reverse();
        return r;
    }

    /// <summary>
    /// A closed ring without its points that do no work: repeats, points nearer than <paramref name="minEdge"/>
    /// to the one before, and points that stand within <paramref name="tolerance"/> of the line through their
    /// neighbours. Removed one at a time, least work first, so the result does not depend on where the ring
    /// starts more than ties do.
    /// </summary>
    public static List<Point2> Simplify(IReadOnlyList<Point2> ring, double tolerance, double minEdge = 0.05)
    {
        var r = new List<Point2>(ring);
        if (r.Count > 1 && r[0] == r[^1]) r.RemoveAt(r.Count - 1);
        while (r.Count > 3)
        {
            int worst = -1; double least = double.MaxValue;
            for (int i = 0; i < r.Count; i++)
            {
                var a = r[(i + r.Count - 1) % r.Count]; var p = r[i]; var b = r[(i + 1) % r.Count];
                double ab = (b - a).Length;
                double off = ab < 1e-12 ? (p - a).Length : Math.Abs(Point2.Cross(b - a, p - a)) / ab;
                // A point on top of the one before costs nothing to remove whatever its offset.
                if ((p - a).Length < minEdge) off = Math.Min(off, 0);
                if (off < least) { least = off; worst = i; }
            }
            if (least > tolerance) break;
            r.RemoveAt(worst);
        }
        return r;
    }

    /// <summary>Whether a ring crosses itself (edges that are not neighbours meet), or has a zero-length edge.</summary>
    public static bool SelfIntersects(IReadOnlyList<Point2> ring)
    {
        int n = ring.Count;
        for (int i = 0; i < n; i++)
        {
            if ((ring[(i + 1) % n] - ring[i]).Length < 1e-9) return true;
            for (int j = i + 1; j < n; j++)
            {
                if (j == i + 1 || (i == 0 && j == n - 1)) continue;
                if (SegmentsMeet(ring[i], ring[(i + 1) % n], ring[j], ring[(j + 1) % n])) return true;
            }
        }
        return false;
    }

    /// <summary>Whether two rings cross or touch.</summary>
    public static bool RingsMeet(IReadOnlyList<Point2> a, IReadOnlyList<Point2> b)
    {
        for (int i = 0; i < a.Count; i++)
            for (int j = 0; j < b.Count; j++)
                if (SegmentsMeet(a[i], a[(i + 1) % a.Count], b[j], b[(j + 1) % b.Count])) return true;
        return false;
    }

    private static bool SegmentsMeet(Point2 p1, Point2 p2, Point2 q1, Point2 q2)
    {
        double d1 = Point2.Cross(q2 - q1, p1 - q1), d2 = Point2.Cross(q2 - q1, p2 - q1);
        double d3 = Point2.Cross(p2 - p1, q1 - p1), d4 = Point2.Cross(p2 - p1, q2 - p1);
        if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0))) return true;
        return (d1 == 0 && OnSegment(q1, q2, p1)) || (d2 == 0 && OnSegment(q1, q2, p2))
            || (d3 == 0 && OnSegment(p1, p2, q1)) || (d4 == 0 && OnSegment(p1, p2, q2));
    }

    private static bool OnSegment(Point2 a, Point2 b, Point2 p)
        => Math.Min(a.X, b.X) <= p.X && p.X <= Math.Max(a.X, b.X) && Math.Min(a.Y, b.Y) <= p.Y && p.Y <= Math.Max(a.Y, b.Y);

    /// <summary>Whether a point is inside a ring (even-odd).</summary>
    public static bool Contains(IReadOnlyList<Point2> ring, Point2 p)
    {
        bool inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            var a = ring[i]; var b = ring[j];
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }

    /// <summary>
    /// Triangles covering an outline less its holes, each three indices into the outline's points followed by
    /// every hole's in order, counter-clockwise. The outline must be counter-clockwise and each hole clockwise
    /// (<see cref="CounterClockwise"/>, reversed for a hole). Each hole is joined to the outline by a bridge to a
    /// point it can see (Eberly, "Triangulation by ear clipping", 2002), then ears are cut from the one ring.
    /// </summary>
    public static List<int> Triangulate(IReadOnlyList<Point2> outline, IReadOnlyList<IReadOnlyList<Point2>>? holes = null)
    {
        var pts = new List<Point2>(outline);
        var ring = new List<int>(outline.Count);
        for (int i = 0; i < outline.Count; i++) ring.Add(i);
        if (holes != null && holes.Count > 0)
        {
            var starts = new List<int>();
            foreach (var h in holes) { starts.Add(pts.Count); pts.AddRange(h); }
            // The hole reaching furthest right first: its bridge cannot cross a hole not yet joined.
            var order = Enumerable.Range(0, holes.Count).OrderByDescending(k => holes[k].Max(p => p.X)).ThenBy(k => k).ToList();
            foreach (int k in order) Bridge(pts, ring, starts[k], holes[k].Count);
        }
        return EarClip(pts, ring);
    }

    private static void Bridge(List<Point2> pts, List<int> ring, int start, int count)
    {
        // The hole's rightmost point (lowest index on a tie) and a ray from it to the right.
        int m = start;
        for (int i = start + 1; i < start + count; i++)
            if (pts[i].X > pts[m].X || (pts[i].X == pts[m].X && pts[i].Y < pts[m].Y)) m = i;
        var M = pts[m];
        double bestX = double.MaxValue; int bestEdge = -1;
        for (int k = 0; k < ring.Count; k++)
        {
            var a = pts[ring[k]]; var b = pts[ring[(k + 1) % ring.Count]];
            // Edges going down past the ray's height, seen from inside a counter-clockwise ring's right side.
            if ((a.Y > M.Y) == (b.Y > M.Y)) continue;
            double x = a.X + (M.Y - a.Y) * (b.X - a.X) / (b.Y - a.Y);
            if (x < M.X - 1e-12 || x >= bestX) continue;
            bestX = x; bestEdge = k;
        }
        int join;
        if (bestEdge < 0) join = 0;
        else
        {
            int ka = ring[bestEdge], kb = ring[(bestEdge + 1) % ring.Count];
            // The edge's end further right is the candidate; any reflex point inside the triangle M, hit, it
            // that makes the smallest angle with the ray is seen instead.
            int cand = pts[ka].X > pts[kb].X ? bestEdge : (bestEdge + 1) % ring.Count;
            var I = new Point2(bestX, M.Y);
            var P = pts[ring[cand]];
            join = cand;
            double bestAngle = double.MaxValue, bestDist = double.MaxValue;
            for (int k = 0; k < ring.Count; k++)
            {
                var q = pts[ring[k]];
                if (k == cand || !(q.X >= M.X)) continue;
                if (!Reflex(pts, ring, k)) continue;
                if (!InTriangle(M, I, P, q) && !InTriangle(M, P, I, q)) continue;
                var d = q - M;
                double angle = Math.Abs(d.Y) / Math.Max(1e-300, d.Length);
                if (angle < bestAngle || (angle == bestAngle && d.Length < bestDist)) { bestAngle = angle; bestDist = d.Length; join = k; }
            }
        }
        // ... join, m, the hole round from m, m again, join again ...
        var insert = new List<int>(count + 2);
        int mi = m - start;
        for (int i = 0; i <= count; i++) insert.Add(start + (mi + i) % count);
        insert.Add(ring[join]);
        ring.InsertRange(join + 1, insert);
    }

    private static bool Reflex(List<Point2> pts, List<int> ring, int k)
    {
        int n = ring.Count;
        var a = pts[ring[(k + n - 1) % n]]; var p = pts[ring[k]]; var b = pts[ring[(k + 1) % n]];
        return Point2.Cross(p - a, b - p) <= 0;
    }

    private static bool InTriangle(Point2 a, Point2 b, Point2 c, Point2 p)
    {
        // On an edge counts as in, to a part in a million of the edge: points that are in a line as floats (a
        // flight's step corners) are a hair off it as doubles, and a diagonal through one of them would leave
        // it in the middle of an edge.
        double d1 = Point2.Cross(b - a, p - a), d2 = Point2.Cross(c - b, p - b), d3 = Point2.Cross(a - c, p - c);
        double e1 = 1e-6 * (b - a).Length * (p - a).Length, e2 = 1e-6 * (c - b).Length * (p - b).Length, e3 = 1e-6 * (a - c).Length * (p - c).Length;
        return d1 >= -e1 && d2 >= -e2 && d3 >= -e3;
    }

    private static List<int> EarClip(List<Point2> pts, List<int> ring)
    {
        var tris = new List<int>((ring.Count - 2) * 3);
        var r = new List<int>(ring);
        int guard = r.Count * r.Count + 16;
        while (r.Count > 3 && guard-- > 0)
        {
            int n = r.Count, ear = -1;
            // The ear with the fattest corner: thin ears make slivers.
            double best = double.MinValue;
            for (int i = 0; i < n; i++)
            {
                int ia = r[(i + n - 1) % n], ip = r[i], ib = r[(i + 1) % n];
                Point2 a = pts[ia], p = pts[ip], b = pts[ib];
                double cross = Point2.Cross(p - a, b - p);
                if (cross <= 1e-7 * (p - a).Length * (b - p).Length) continue;
                bool blocked = false;
                for (int k = 0; k < n && !blocked; k++)
                {
                    int iq = r[k];
                    if (iq == ia || iq == ip || iq == ib) continue;
                    var q = pts[iq];
                    if (q == a || q == p || q == b) continue;
                    if (InTriangle(a, p, b, q)) blocked = true;
                }
                if (blocked) continue;
                double la = (p - a).Length, lb = (b - p).Length, lc = (b - a).Length;
                double quality = cross / Math.Max(1e-300, la * la + lb * lb + lc * lc);
                if (quality > best) { best = quality; ear = i; }
            }
            if (ear < 0)
            {
                // Rounding left no clean ear: cut the most convex corner and go on.
                for (int i = 0; i < n; i++)
                {
                    double cross = Point2.Cross(pts[r[i]] - pts[r[(i + n - 1) % n]], pts[r[(i + 1) % n]] - pts[r[i]]);
                    if (cross > best) { best = cross; ear = i; }
                }
            }
            int pa = r[(ear + n - 1) % n], pp = r[ear], pb = r[(ear + 1) % n];
            if (Point2.Cross(pts[pp] - pts[pa], pts[pb] - pts[pp]) > 0) { tris.Add(pa); tris.Add(pp); tris.Add(pb); }
            r.RemoveAt(ear);
        }
        if (r.Count == 3 && Point2.Cross(pts[r[1]] - pts[r[0]], pts[r[2]] - pts[r[1]]) > 0) { tris.Add(r[0]); tris.Add(r[1]); tris.Add(r[2]); }
        return tris;
    }

    /// <summary>
    /// Triangles merged into convex pieces across the diagonals that keep them convex (Hertel and Mehlhorn):
    /// at most four times the fewest pieces, and each a counter-clockwise list of point indices.
    /// </summary>
    public static List<List<int>> ConvexPieces(IReadOnlyList<Point2> pts, IReadOnlyList<int> triangles)
    {
        var pieces = new List<List<int>>();
        for (int t = 0; t + 2 < triangles.Count; t += 3) pieces.Add(new List<int> { triangles[t], triangles[t + 1], triangles[t + 2] });
        bool merged = true;
        while (merged)
        {
            merged = false;
            for (int a = 0; a < pieces.Count && !merged; a++)
                for (int b = a + 1; b < pieces.Count && !merged; b++)
                {
                    var m = Merge(pts, pieces[a], pieces[b]);
                    if (m == null) continue;
                    pieces[a] = m; pieces.RemoveAt(b); merged = true;
                }
        }
        return pieces;
    }

    private static List<int>? Merge(IReadOnlyList<Point2> pts, List<int> a, List<int> b)
    {
        // A shared edge: u->v in a, v->u in b.
        for (int i = 0; i < a.Count; i++)
        {
            int u = a[i], v = a[(i + 1) % a.Count];
            for (int j = 0; j < b.Count; j++)
            {
                if (b[j] != v || b[(j + 1) % b.Count] != u) continue;
                var m = new List<int>(a.Count + b.Count - 2);
                for (int k = 0; k < a.Count; k++) m.Add(a[(i + 1 + k) % a.Count]);         // v ... u
                for (int k = 2; k < b.Count; k++) m.Add(b[(j + k) % b.Count]);               // after u in b, up to before v
                return IsConvex(pts, m) ? m : null;
            }
        }
        return null;
    }

    /// <summary>Whether a counter-clockwise polygon turns left (or goes straight) at every point.</summary>
    public static bool IsConvex(IReadOnlyList<Point2> pts, IReadOnlyList<int> poly)
    {
        int n = poly.Count;
        for (int i = 0; i < n; i++)
        {
            var a = pts[poly[(i + n - 1) % n]]; var p = pts[poly[i]]; var b = pts[poly[(i + 1) % n]];
            double l = (p - a).Length * (b - p).Length;
            if (Point2.Cross(p - a, b - p) < -1e-9 * Math.Max(1e-12, l)) return false;
            if (p == a || p == b) return false;
        }
        return true;
    }
}

/// <summary>
/// Sine and cosine from plain arithmetic: the runtime's call into the C library may differ in its last bit
/// between Windows and Linux, and a client and the server must make the same round shape.
/// </summary>
public static class DetMath
{
    public const double Tau = 6.283185307179586;

    /// <summary>The sine and cosine of a whole turn's <paramref name="k"/>/<paramref name="n"/>, exact at the quarters.</summary>
    public static (double Sin, double Cos) SinCosOfTurn(int k, int n)
    {
        k %= n; if (k < 0) k += n;
        // Exact at the quarter turns, and the same octant symmetry everywhere else.
        if (4 * k == 0) return (0, 1);
        if (4 * k == n) return (1, 0);
        if (4 * k == 2 * n) return (0, -1);
        if (4 * k == 3 * n) return (-1, 0);
        return SinCos(Tau * k / n);
    }

    /// <summary>Sine and cosine of an angle in radians, to about a part in 10^16 for any angle under a few turns.</summary>
    public static (double Sin, double Cos) SinCos(double a)
    {
        // Into [-pi, pi], then into the octant round 0 or a quarter turn.
        a -= Tau * Math.Floor(a / Tau + 0.5);
        int quarter = (int)Math.Floor(a / (Tau / 4) + 0.5);
        double x = a - quarter * (Tau / 4);
        double x2 = x * x;
        double s = x * (1 - x2 / 6 * (1 - x2 / 20 * (1 - x2 / 42 * (1 - x2 / 72 * (1 - x2 / 110 * (1 - x2 / 156 * (1 - x2 / 210)))))));
        double c = 1 - x2 / 2 * (1 - x2 / 12 * (1 - x2 / 30 * (1 - x2 / 56 * (1 - x2 / 90 * (1 - x2 / 132 * (1 - x2 / 182))))));
        return (quarter & 3) switch
        {
            0 => (s, c),
            1 => (c, -s),
            2 => (-s, -c),
            _ => (-c, s),
        };
    }
}

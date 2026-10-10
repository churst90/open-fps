namespace OpenFPS.Common.Geometry;

/// <summary>
/// The straight skeleton of a simple polygon (Aichholzer and others, 1995), as the roof it makes: every edge
/// moves inward at the same speed, and the height of a point is how long the edge that reaches it first took.
/// Each edge's face is then a plane through the edge at one pitch, which is a hip roof over any footprint
/// (docs/GEOMETRY.md 2.6, 5.2). Events (an edge shrinking to nothing, a reflex corner splitting the front)
/// are taken one at a time, earliest first, so simultaneous events of a rectangle or an L are just events a
/// moment apart. Plain arithmetic in doubles, so the server and every client get the same roof.
/// </summary>
public static class StraightSkeleton
{
    /// <summary>A skeleton: its nodes (plan position and height at pitch 1: the time the front reached
    /// them) and, for each edge of the outline, its face as node indices counter-clockwise in plan. The
    /// outline's points are nodes 0 to n - 1, edge k runs from node k to node k + 1. A gable's face is a
    /// vertical triangle (<see cref="Vertical"/>).</summary>
    public sealed class Roof
    {
        public required List<Point2> Plan { get; init; }
        public required List<double> Height { get; init; }
        public required List<List<int>> Faces { get; init; }
        public required bool[] Vertical { get; init; }
        public double Top { get; init; }
    }

    private sealed class Front
    {
        public Point2 At; public double T; public Point2 V; public int Node, In, Out;
        public Point2 Pos(double t) => At + V * (t - T);
    }

    /// <summary>
    /// The hip roof of a counter-clockwise outline, or null when it cannot be made cleanly (the faces must
    /// cover the outline exactly: callers lay a flat roof instead). With <paramref name="gables"/>, every
    /// triangular end whose ridge meets its edge is stood up as a gable.
    /// </summary>
    public static Roof? Of(IReadOnlyList<Point2> outline, bool gables = false)
    {
        int n = outline.Count;
        if (n < 3 || Polygons.SignedArea2(outline) <= 0) return null;
        // Worked in a frame of the outline's own size, so every tolerance is relative.
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var p in outline) { minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y); maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y); }
        double scale = Math.Max(maxX - minX, maxY - minY);
        if (!(scale > 0)) return null;
        var origin = new Point2((minX + maxX) / 2, (minY + maxY) / 2);
        var pts = new Point2[n];
        for (int i = 0; i < n; i++) pts[i] = (outline[i] - origin) * (1 / scale);

        var dir = new Point2[n]; var nrm = new Point2[n];
        for (int i = 0; i < n; i++)
        {
            var d = pts[(i + 1) % n] - pts[i];
            double len = d.Length;
            if (len < 1e-9) return null;
            dir[i] = d * (1 / len);
            nrm[i] = new Point2(-dir[i].Y, dir[i].X);
        }

        var nodes = new List<Point2>(pts);
        var times = new List<double>(new double[n]);
        var arcs = new List<(int A, int B, int F1, int F2)>();
        Point2 Velocity(int a, int b)
        {
            double c = 1 + Point2.Dot(nrm[a], nrm[b]);
            return c < 1e-12 ? new Point2(0, 0) : (nrm[a] + nrm[b]) * (1 / c);
        }
        int Node(Point2 p, double t) { nodes.Add(p); times.Add(t); return nodes.Count - 1; }

        var fronts = new List<List<Front>>();
        var first = new List<Front>(n);
        for (int i = 0; i < n; i++)
            first.Add(new Front { At = pts[i], T = 0, V = Velocity((i + n - 1) % n, i), Node = i, In = (i + n - 1) % n, Out = i });
        fronts.Add(first);

        void Finish(List<Front> f, double t)
        {
            // Two fronts left: the two edges between them lie on one line now, a ridge.
            var a = f[0]; var b = f[1];
            int na = a.Node, nb = b.Node;
            if (a.T < t) { na = Node(a.Pos(t), t); arcs.Add((a.Node, na, a.In, a.Out)); }
            if (b.T < t) { nb = Node(b.Pos(t), t); arcs.Add((b.Node, nb, b.In, b.Out)); }
            arcs.Add((na, nb, a.Out, b.Out));
        }

        double now = 0;
        int guard = 20 * n + 100;
        const double Eps = 1e-10;
        bool done = false;
        while (guard-- > 0)
        {
            fronts.RemoveAll(f => f.Count < 3);
            if (fronts.Count == 0) { done = true; break; }
            double best = double.MaxValue;
            int bf = -1, bi = -1, bj = -1;     // bj < 0: an edge event of the edge after bi; else a split of bi against bj's edge
            for (int fi = 0; fi < fronts.Count; fi++)
            {
                var f = fronts[fi];
                int m = f.Count;
                for (int i = 0; i < m; i++)
                {
                    var a = f[i]; var b = f[(i + 1) % m];
                    var d = dir[a.Out];
                    double len = Point2.Dot(b.Pos(now) - a.Pos(now), d);
                    double rate = Point2.Dot(b.V - a.V, d);
                    double t;
                    if (len <= Eps) t = now;
                    else if (rate < -1e-12) t = now - len / rate;
                    else continue;
                    if (t < best - Eps) { best = t; bf = fi; bi = i; bj = -1; }
                }
                for (int i = 0; i < m; i++)
                {
                    var r = f[i];
                    if (Point2.Cross(dir[r.In], dir[r.Out]) >= -1e-9) continue;   // not reflex
                    var rp = r.Pos(now);
                    for (int j = 0; j < m; j++)
                    {
                        var a = f[j]; var b = f[(j + 1) % m];
                        if (a == r || b == r) continue;
                        int e = a.Out;
                        double dist = Point2.Dot(nrm[e], rp - pts[e]) - now;
                        double closing = 1 - Point2.Dot(nrm[e], r.V);
                        if (closing <= 1e-12 || dist < -1e-9) continue;
                        double t = now + Math.Max(0, dist) / closing;
                        if (t >= best - Eps) continue;
                        var hit = r.Pos(t); var ea = a.Pos(t); var eb = b.Pos(t);
                        double s = Point2.Dot(hit - ea, dir[e]), span = Point2.Dot(eb - ea, dir[e]);
                        if (s < -1e-9 || s > span + 1e-9) continue;
                        best = t; bf = fi; bi = i; bj = j;
                    }
                }
            }
            if (bf < 0) return null;
            now = Math.Max(now, best);
            var front = fronts[bf];
            int count = front.Count;
            if (bj < 0)
            {
                var a = front[bi]; var b = front[(bi + 1) % count];
                var at = (a.Pos(now) + b.Pos(now)) * 0.5;
                int nd = Node(at, now);
                arcs.Add((a.Node, nd, a.In, a.Out));
                arcs.Add((b.Node, nd, b.In, b.Out));
                var c = new Front { At = at, T = now, V = Velocity(a.In, b.Out), Node = nd, In = a.In, Out = b.Out };
                front[bi] = c;
                front.RemoveAt((bi + 1) % count);
                if (front.Count == 2) Finish(front, now);
            }
            else
            {
                var r = front[bi]; int e = front[bj].Out;
                var at = r.Pos(now);
                int nd = Node(at, now);
                arcs.Add((r.Node, nd, r.In, r.Out));
                var r1 = new Front { At = at, T = now, V = Velocity(e, r.Out), Node = nd, In = e, Out = r.Out };
                var r2 = new Front { At = at, T = now, V = Velocity(r.In, e), Node = nd, In = r.In, Out = e };
                var one = new List<Front>();
                for (int k = (bi + 1) % count; ; k = (k + 1) % count) { one.Add(front[k]); if (k == bj) break; }
                one.Add(r1);
                var two = new List<Front>();
                for (int k = (bj + 1) % count; k != bi; k = (k + 1) % count) two.Add(front[k]);
                two.Add(r2);
                fronts.RemoveAt(bf);
                foreach (var part in new[] { one, two })
                {
                    if (part.Count == 2) Finish(part, now);
                    else if (part.Count > 2) fronts.Add(part);
                }
            }
        }
        if (!done) return null;

        // Nodes in the same place are one (simultaneous events made several).
        var canon = new int[nodes.Count];
        for (int i = 0; i < nodes.Count; i++)
        {
            canon[i] = i;
            for (int j = 0; j < i; j++)
                if (canon[j] == j && (nodes[i] - nodes[j]).Length < 1e-8 && Math.Abs(times[i] - times[j]) < 1e-8) { canon[i] = j; break; }
        }

        var faces = new List<List<int>>(n);
        for (int k = 0; k < n; k++)
        {
            var adj = new Dictionary<int, List<int>>();
            void Link(int a, int b) { if (!adj.TryGetValue(a, out var l)) adj[a] = l = new List<int>(); if (!l.Contains(b)) l.Add(b); }
            foreach (var (a0, b0, f1, f2) in arcs)
            {
                if (f1 != k && f2 != k) continue;
                int a = canon[a0], b = canon[b0];
                if (a == b) continue;
                Link(a, b); Link(b, a);
            }
            int start = canon[k], end = canon[(k + 1) % n];
            var path = PathBetween(adj, end, start);
            if (path == null) return null;
            var face = new List<int> { start, end };
            face.AddRange(path.Take(path.Count - 1));
            faces.Add(face);
        }

        var vertical = new bool[n];
        if (gables) MakeGables(faces, nodes, times, pts, nrm, dir, vertical);

        // The faces must cover the outline, once: their plan areas add up to its area.
        double area = Polygons.SignedArea2(pts), sum = 0;
        for (int k = 0; k < n; k++)
        {
            if (vertical[k]) continue;
            var poly = faces[k].Select(i => nodes[i]).ToList();
            double a = Polygons.SignedArea2(poly);
            if (a < -1e-9) return null;
            sum += a;
        }
        if (Math.Abs(sum - area) > 1e-6 * Math.Max(1e-12, area)) return null;

        // Back to the outline's own metres; heights in metres at pitch 1.
        var plan = new List<Point2>(nodes.Count);
        var height = new List<double>(nodes.Count);
        double top = 0;
        for (int i = 0; i < nodes.Count; i++)
        {
            plan.Add(i < n ? outline[i] : nodes[i] * scale + origin);
            height.Add(i < n ? 0 : times[i] * scale);
            top = Math.Max(top, height[i]);
        }
        return new Roof { Plan = plan, Height = height, Faces = faces, Vertical = vertical, Top = top };
    }

    private static List<int>? PathBetween(Dictionary<int, List<int>> adj, int from, int to)
    {
        // A face's skeleton edges are one path from its edge's end back to its start.
        var path = new List<int>();
        var seen = new HashSet<int>();
        bool Walk(int at)
        {
            path.Add(at); seen.Add(at);
            if (at == to) return true;
            if (adj.TryGetValue(at, out var next))
                foreach (int b in next)
                {
                    if (seen.Contains(b)) continue;
                    if (b == to && path.Count == 1) continue;   // the edge itself is not a skeleton arc
                    if (Walk(b)) return true;
                }
            path.RemoveAt(path.Count - 1);
            return false;
        }
        if (!Walk(from)) return null;
        path.RemoveAt(0);
        return path;
    }

    /// <summary>
    /// A triangular hip end stood up as a gable: its apex moved along the ridge of the two faces beside it
    /// to the end's own edge, where the ridge meets the edge's vertical plane. The faces beside it stay planes
    /// (the apex stays on both); an end whose ridge would meet the edge outside it, or whose apex is shared
    /// with more than the three faces, stays a hip.
    /// </summary>
    private static void MakeGables(List<List<int>> faces, List<Point2> nodes, List<double> times, Point2[] pts, Point2[] nrm, Point2[] dir, bool[] vertical)
    {
        int n = faces.Count;
        for (int k = 0; k < n; k++)
        {
            var f = faces[k];
            if (f.Count != 3) continue;
            int apex = f[2];
            int left = (k + n - 1) % n, right = (k + 1) % n;
            int sharing = 0;
            for (int j = 0; j < n; j++) if (faces[j].Contains(apex)) sharing++;
            if (sharing != 3 || !faces[left].Contains(apex) || !faces[right].Contains(apex)) continue;
            if (vertical[left] || vertical[right]) continue;
            // Where the two neighbours' planes are the same height on the edge's line.
            var ak = pts[k];
            double den = Point2.Dot(nrm[left] - nrm[right], dir[k]);
            if (Math.Abs(den) < 1e-12) continue;
            double s = (Point2.Dot(nrm[right], ak - pts[right]) - Point2.Dot(nrm[left], ak - pts[left])) / den;
            double len = (pts[(k + 1) % n] - ak).Length;
            if (s <= 0.02 * len || s >= 0.98 * len) continue;
            var q = ak + dir[k] * s;
            double h = Point2.Dot(nrm[left], q - pts[left]);
            if (h <= 1e-9) continue;
            nodes.Add(q); times.Add(h);
            int nq = nodes.Count - 1;
            foreach (int side in new[] { left, right })
                for (int i = 0; i < faces[side].Count; i++)
                    if (faces[side][i] == apex) faces[side][i] = nq;
            faces[k] = new List<int> { f[0], f[1], nq };
            vertical[k] = true;
        }
    }
}

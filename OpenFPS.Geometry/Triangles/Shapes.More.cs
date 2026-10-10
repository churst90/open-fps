using System.Numerics;

namespace OpenFPS.Common.Geometry;

// The round, outlined, roofed and swept shapes of the library (docs/GEOMETRY.md 2.6, stage 4). Each is worked
// out in doubles from its numbers, fitted to its collider's box, and only then made floats, so the server and
// every client make the same bits. Outlines and profiles are Point2 (x, z) or (across, up); a plan polygon
// counter-clockwise in Point2 terms is clockwise seen from above, so a top face takes its triangles reversed.
public static partial class Shapes
{
    /// <summary>The most corners an outline, a hole, a profile or a path may have.</summary>
    public const int MaxPoints = 4096;

    /// <summary>How many sides a round thing has when its numbers do not say: as few as sound can tell apart,
    /// a column of 0.3 m radius at 12, a tank of 5 m at 32 (docs/GEOMETRY.md 2.6).</summary>
    public static int RoundSides(float radius)
        => Math.Clamp(4 * (int)MathF.Round(3f * MathF.Sqrt(MathF.Max(0f, radius) / 0.3f)), 8, 32);

    /// <summary>Round shapes fill their box: a polygon of sides not a multiple of four reaches the box at one
    /// corner on some sides only, so its circle is stretched to meet the box (exactly nothing for a multiple of
    /// four, whose quarter points are exact). The scale and shift for each axis of the unit circle.</summary>
    private static (double Scale, double Shift) Stretch(int n, bool cosine)
    {
        double lo = double.MaxValue, hi = double.MinValue;
        for (int k = 0; k < n; k++)
        {
            var (s, c) = DetMath.SinCosOfTurn(k, n);
            double v = cosine ? c : s;
            lo = Math.Min(lo, v); hi = Math.Max(hi, v);
        }
        double scale = 2 / (hi - lo);
        return (scale, -(lo + hi) / 2 * scale);
    }

    /// <summary>The roof deck sound goes through when a roof's own thickness is not said, metres.</summary>
    public const float RoofDeckMetres = 0.2f;

    /// <summary>
    /// The panel sound goes through a shape as (WallTransmission's panel: its smallest side is the thickness):
    /// the box for every shape but a roof, which is a deck of <see cref="ShapeSpec.Thickness"/> (or
    /// <see cref="RoofDeckMetres"/>) over its outline, whatever the attic under the slopes.
    /// </summary>
    public static Vector3 PanelOf(ShapeSpec? spec, Vector3 size)
        => spec is { Kind: ShapeKind.Roof, Style: not RoofStyle.Flat }
            ? new Vector3(size.X, MathF.Min(size.Y, spec.Thickness > 0f ? spec.Thickness : RoofDeckMetres), size.Z)
            : size;

    /// <summary>Each of a shape's surfaces (<see cref="SurfaceNames"/>) as <paramref name="main"/> with the form's
    /// material for it, and a roof's slopes marked roof; null when every one is <paramref name="main"/>.</summary>
    public static Surface[]? SlotSurfaces(ShapeSpec form, in Surface main)
    {
        var names = SurfaceNames(form);
        // An imported mesh's surfaces come with the materials its import gave them.
        var given = form.Kind == ShapeKind.Mesh && MeshLibrary.Shared.TryGet(form.Mesh, out var asset) ? asset.Data.SurfaceMaterials : null;
        var slots = new Surface[names.Length];
        bool differ = false;
        for (int i = 0; i < names.Length; i++)
        {
            var s = main;
            string? mat = form.Materials != null && i < form.Materials.Length && !string.IsNullOrWhiteSpace(form.Materials[i]) ? form.Materials[i]
                        : given != null && i < given.Length && !string.IsNullOrWhiteSpace(given[i]) ? given[i] : null;
            if (mat != null && !string.Equals(mat, main.Material, StringComparison.OrdinalIgnoreCase))
            {
                var flags = string.Equals(mat, "Glass", StringComparison.OrdinalIgnoreCase) ? s.Flags | SurfaceFlags.Glass : s.Flags & ~SurfaceFlags.Glass;
                s = s with { Material = mat, Flags = flags };
            }
            if (form.Kind == ShapeKind.Roof && i == 0) s = s with { Flags = s.Flags | SurfaceFlags.Roof };
            slots[i] = s;
            differ |= s != main;
        }
        return differ ? slots : null;
    }

    private static string? MoreProblem(ShapeSpec spec, Vector3 size)
    {
        if (spec.Materials is { } mats && mats.Length > SurfaceNames(spec.Kind).Length)
            return $"a {spec.Kind.ToString().ToLowerInvariant()} has {SurfaceNames(spec.Kind).Length} surfaces, not {mats.Length}";
        switch (spec.Kind)
        {
            case ShapeKind.Cylinder or ShapeKind.Sphere or ShapeKind.Dome:
                return spec.Segments is 0 or (>= 3 and <= 64) ? null : "a round shape has 3 to 64 sides";
            case ShapeKind.Cone:
                if (spec.Segments is not (0 or (>= 3 and <= 64))) return "a round shape has 3 to 64 sides";
                return spec.Top is >= 0f and <= 1f ? null : "a cone's top is 0 to 1 of its base";
            case ShapeKind.Prism:
                return OutlineProblem(spec.Outline, spec.Holes);
            case ShapeKind.Roof:
                if (spec.Style > RoofStyle.Flat) return $"no roof called {spec.Style}";
                return OutlineProblem(spec.Outline, null);
            case ShapeKind.Swept:
                return SweptProblem(spec.Profile, spec.Path);
            case ShapeKind.Mesh:
                return spec.Mesh is { Length: 16 } id && id.All(Uri.IsHexDigit) ? null : "a mesh is named by its sixteen-digit id";
            default:
                return $"no shape called {spec.Kind}";
        }
    }

    private static List<Point2>? Ring(float[]? pairs)
    {
        if (pairs == null || pairs.Length < 6 || pairs.Length % 2 != 0 || pairs.Length / 2 > MaxPoints) return null;
        var r = new List<Point2>(pairs.Length / 2);
        for (int i = 0; i < pairs.Length; i += 2)
        {
            if (!float.IsFinite(pairs[i]) || !float.IsFinite(pairs[i + 1])) return null;
            var p = new Point2(pairs[i], pairs[i + 1]);
            if (r.Count == 0 || r[^1] != p) r.Add(p);
        }
        if (r.Count > 1 && r[0] == r[^1]) r.RemoveAt(r.Count - 1);
        return r.Count >= 3 ? r : null;
    }

    private static string? OutlineProblem(float[]? outline, float[][]? holes)
    {
        var o = Ring(outline);
        if (o == null) return "an outline needs at least three corners, as x and z pairs";
        if (Polygons.SelfIntersects(o)) return "the outline crosses itself";
        if (Math.Abs(Polygons.SignedArea2(o)) < 2e-4) return "the outline encloses nothing";
        var hs = new List<List<Point2>>();
        foreach (var hole in holes ?? Array.Empty<float[]>())
        {
            var h = Ring(hole);
            if (h == null) return "a hole needs at least three corners, as x and z pairs";
            if (Polygons.SelfIntersects(h)) return "a hole crosses itself";
            if (Polygons.RingsMeet(o, h) || !h.All(p => Polygons.Contains(o, p))) return "a hole must be inside the outline";
            foreach (var other in hs)
                if (Polygons.RingsMeet(other, h) || Polygons.Contains(other, h[0]) || Polygons.Contains(h, other[0]))
                    return "two holes overlap";
            hs.Add(h);
        }
        return null;
    }

    private static List<Vector3d>? PathOf(float[]? triples)
    {
        if (triples == null || triples.Length < 6 || triples.Length % 3 != 0 || triples.Length / 3 > MaxPoints) return null;
        var p = new List<Vector3d>();
        for (int i = 0; i < triples.Length; i += 3)
        {
            if (!float.IsFinite(triples[i]) || !float.IsFinite(triples[i + 1]) || !float.IsFinite(triples[i + 2])) return null;
            p.Add(new Vector3d(triples[i], triples[i + 1], triples[i + 2]));
        }
        return p;
    }

    private static string? SweptProblem(float[]? profile, float[]? path)
    {
        var pr = Ring(profile);
        if (pr == null) return "a profile needs at least three corners, as across and up pairs";
        if (Polygons.SelfIntersects(pr)) return "the profile crosses itself";
        if (Math.Abs(Polygons.SignedArea2(pr)) < 2e-6) return "the profile encloses nothing";
        var p = PathOf(path);
        if (p == null) return "a path needs at least two points, as x, y and z triples";
        Vector3d? last = null;
        for (int i = 0; i + 1 < p.Count; i++)
        {
            var d = p[i + 1] - p[i];
            double len = d.Length;
            if (len < 0.01) return "a path's points must be at least a centimetre apart";
            var t = d * (1 / len);
            if (Math.Abs(t.Y) > 0.996) return "a path may not run straight up or down";
            if (last is { } l && Vector3d.Dot(l, t) < -0.866) return "a path may not turn back on itself";
            last = t;
        }
        return null;
    }

    private static ShapeMesh? MakeMore(ShapeSpec spec, Vector3 size)
    {
        var mats = spec.Materials;
        return spec.Kind switch
        {
            ShapeKind.Cylinder => Round(size, spec.Segments, 1.0, cap: true),
            ShapeKind.Cone => Round(size, spec.Segments, spec.Top, cap: spec.Top > 0f),
            ShapeKind.Sphere => Ball(size, spec.Segments, dome: false),
            ShapeKind.Dome => Ball(size, spec.Segments, dome: true),
            ShapeKind.Prism => Prism(size, Ring(spec.Outline)!, spec.Holes?.Select(h => Ring(h)!).ToList()),
            ShapeKind.Roof => Roof(size, Ring(spec.Outline)!, spec.Style),
            ShapeKind.Swept => Sweep(size, Ring(spec.Profile)!, PathOf(spec.Path)!),
            ShapeKind.Mesh => MeshLibrary.Shared.TryGet(spec.Mesh, out var entry) ? FitMesh(entry, size) : null,
            _ => null,
        };
    }

    /// <summary>A shape's surfaces by name (<see cref="SurfaceNames(ShapeKind)"/>; an imported mesh's are its own).</summary>
    public static string[] SurfaceNames(ShapeSpec spec)
        => spec.Kind == ShapeKind.Mesh && MeshLibrary.Shared.TryGet(spec.Mesh, out var e) && e.Data.SurfaceNames.Length > 0
            ? e.Data.SurfaceNames : SurfaceNames(spec.Kind);

    /// <summary>An imported mesh stretched to the box, with its convex pieces stretched the same.</summary>
    private static ShapeMesh FitMesh(MeshLibrary.Entry entry, Vector3 size)
    {
        var m = entry.Mesh;
        var ext = m.BoundsMax - m.BoundsMin;
        var mid = (m.BoundsMin + m.BoundsMax) * 0.5f;
        var s = new Vector3(ext.X > 0 ? size.X / ext.X : 1f, ext.Y > 0 ? size.Y / ext.Y : 1f, ext.Z > 0 ? size.Z / ext.Z : 1f);
        MeshAsset Fit(MeshAsset a, bool convex)
        {
            var v = new Vector3[a.Vertices.Length];
            for (int i = 0; i < v.Length; i++) v[i] = (a.Vertices[i] - mid) * s;
            return new MeshAsset(v, a.Indices, a.TriangleSurface, a.Closed, convex);
        }
        return new ShapeMesh(Fit(m, false), entry.Parts?.Select(p => Fit(p, true)).ToArray() ?? Array.Empty<MeshAsset>());
    }

    // ── Round things ────────────────────────────────────────────────────────────────────────────

    /// <summary>An upright cylinder (<paramref name="top"/> 1) or cone (<paramref name="top"/> under 1) filling the
    /// box, its ends on surface 1. Convex: its own one piece.</summary>
    private static ShapeMesh Round(Vector3 size, int segments, double top, bool cap)
    {
        var h = size * 0.5f;
        int n = segments > 0 ? segments : RoundSides(MathF.Max(h.X, h.Z));
        var lo = new Vector3[n]; var hi = new Vector3[n];
        var (xs, xo) = Stretch(n, cosine: true); var (zs, zo) = Stretch(n, cosine: false);
        for (int k = 0; k < n; k++)
        {
            var (s, c) = DetMath.SinCosOfTurn(k, n);
            double x = c * xs + xo, z = s * zs + zo;
            lo[k] = new Vector3((float)(h.X * x), -h.Y, (float)(h.Z * z));
            hi[k] = new Vector3((float)(h.X * x * top), h.Y, (float)(h.Z * z * top));
        }
        var apex = new Vector3(0, h.Y, 0);
        var b = new Builder();
        for (int k = 0; k < n; k++)
        {
            int q = (k + 1) % n;
            if (cap) b.Quad(lo[k], hi[k], hi[q], lo[q]);
            else b.Tri(lo[k], apex, lo[q]);
        }
        b.Slot = 1;
        for (int k = 1; k + 1 < n; k++)
        {
            b.Tri(lo[0], lo[k], lo[k + 1]);
            if (cap) b.Tri(hi[0], hi[k + 1], hi[k]);
        }
        return new ShapeMesh(b.Make(convex: true), null);
    }

    /// <summary>A ball filling the box, or its top half on a flat base (surface 1). Convex.</summary>
    private static ShapeMesh Ball(Vector3 size, int segments, bool dome)
    {
        var h = size * 0.5f;
        int n = segments > 0 ? segments : RoundSides(MathF.Max(h.X, MathF.Max(h.Y, h.Z)));
        // Rings by latitude: a ball from pole to pole in n/2 steps, a dome from its rim to its crown in n/4.
        int m = dome ? Math.Max(2, n / 4) : Math.Max(2, n / 2);
        var rings = new Vector3[m + 1][];
        var (xs, xo) = Stretch(n, cosine: true); var (zs, zo) = Stretch(n, cosine: false);
        // Latitude from -90 (or 0 for a dome) to +90 degrees; the widest ring meets the box.
        var lat = new (double Sin, double Cos)[m + 1];
        double widest = 0;
        for (int j = 0; j <= m; j++)
        {
            if (dome) lat[j] = DetMath.SinCosOfTurn(j, 4 * m);
            else { var (s, c) = DetMath.SinCosOfTurn(j, 2 * m); lat[j] = (-c, s); }
            widest = Math.Max(widest, lat[j].Cos);
        }
        for (int j = 0; j <= m; j++)
        {
            var (sinLat, cosLat) = lat[j];
            cosLat /= widest;
            double y = dome ? -h.Y + size.Y * sinLat : h.Y * sinLat;
            rings[j] = new Vector3[n];
            for (int k = 0; k < n; k++)
            {
                var (s, c) = DetMath.SinCosOfTurn(k, n);
                rings[j][k] = j == m || (!dome && j == 0) ? new Vector3(0, (float)y, 0)
                    : new Vector3((float)(h.X * (c * xs + xo) * cosLat), (float)y, (float)(h.Z * (s * zs + zo) * cosLat));
            }
        }
        var b = new Builder();
        for (int j = 0; j < m; j++)
            for (int k = 0; k < n; k++)
            {
                int q = (k + 1) % n;
                b.Quad(rings[j][k], rings[j + 1][k], rings[j + 1][q], rings[j][q]);
            }
        if (dome)
        {
            b.Slot = 1;
            for (int k = 1; k + 1 < n; k++) b.Tri(rings[0][0], rings[0][k], rings[0][k + 1]);
        }
        return new ShapeMesh(b.Make(convex: true), null);
    }

    // ── Outlines: prisms and roofs ──────────────────────────────────────────────────────────────

    /// <summary>Maps an outline's own metres onto the box: its bounds onto the box's X and Z.</summary>
    private readonly struct PlanFit
    {
        private readonly double _cx, _cz, _sx, _sz;
        public PlanFit(IEnumerable<Point2> pts, Vector3 size)
        {
            double x0 = double.MaxValue, x1 = double.MinValue, z0 = double.MaxValue, z1 = double.MinValue;
            foreach (var p in pts) { x0 = Math.Min(x0, p.X); x1 = Math.Max(x1, p.X); z0 = Math.Min(z0, p.Y); z1 = Math.Max(z1, p.Y); }
            _cx = (x0 + x1) / 2; _cz = (z0 + z1) / 2;
            _sx = x1 > x0 ? size.X / (x1 - x0) : 1; _sz = z1 > z0 ? size.Z / (z1 - z0) : 1;
        }
        public Vector3 At(Point2 p, double y) => new((float)((p.X - _cx) * _sx), (float)y, (float)((p.Y - _cz) * _sz));
    }

    /// <summary>
    /// An outline (less its holes) stood up through the box: sides on surface 0, the top 1, the bottom 2.
    /// When it is not convex, its convex pieces (the triangles merged where they stay convex) are the pieces a
    /// body meets.
    /// </summary>
    private static ShapeMesh Prism(Vector3 size, List<Point2> outline, List<List<Point2>>? holes)
    {
        var o = Polygons.CounterClockwise(outline);
        var hs = new List<IReadOnlyList<Point2>>();
        foreach (var h0 in holes ?? new List<List<Point2>>())
        {
            var h = Polygons.CounterClockwise(h0);
            h.Reverse();
            hs.Add(h);
        }
        var pts = new List<Point2>(o);
        foreach (var h in hs) pts.AddRange(h);
        var fit = new PlanFit(o, size);
        float y0 = -size.Y * 0.5f, y1 = size.Y * 0.5f;
        var tris = Polygons.Triangulate(o, hs);

        var b = new Builder();
        var rings = new List<(int Start, int Count)> { (0, o.Count) };
        int at = o.Count;
        foreach (var h in hs) { rings.Add((at, h.Count)); at += h.Count; }
        foreach (var (start, count) in rings)
            for (int i = 0; i < count; i++)
            {
                var p = pts[start + i]; var q = pts[start + (i + 1) % count];
                b.Quad(fit.At(p, y0), fit.At(p, y1), fit.At(q, y1), fit.At(q, y0));
            }
        b.Slot = 1;
        for (int t = 0; t < tris.Count; t += 3) b.Tri(fit.At(pts[tris[t]], y1), fit.At(pts[tris[t + 2]], y1), fit.At(pts[tris[t + 1]], y1));
        b.Slot = 2;
        for (int t = 0; t < tris.Count; t += 3) b.Tri(fit.At(pts[tris[t]], y0), fit.At(pts[tris[t + 1]], y0), fit.At(pts[tris[t + 2]], y0));

        bool convex = hs.Count == 0 && Polygons.IsConvex(o, Enumerable.Range(0, o.Count).ToList());
        if (convex) return new ShapeMesh(b.Make(convex: true), null);
        var parts = new List<MeshAsset>();
        foreach (var piece in Polygons.ConvexPieces(pts, tris))
            parts.Add(Column(piece.Select(i => fit.At(pts[i], y1)).ToList(), piece.Select(i => fit.At(pts[i], y0)).ToList()));
        return new ShapeMesh(b.Make(convex: false), parts.ToArray());
    }

    /// <summary>A convex column between a top and a bottom polygon of the same corners (counter-clockwise in plan
    /// terms), each top corner straight over its bottom one.</summary>
    private static MeshAsset Column(List<Vector3> top, List<Vector3> bottom)
    {
        var c = new Builder();
        int n = top.Count;
        for (int i = 0; i < n; i++)
        {
            int q = (i + 1) % n;
            c.Quad(bottom[i], top[i], top[q], bottom[q]);
        }
        for (int i = 1; i + 1 < n; i++)
        {
            c.Tri(top[0], top[i + 1], top[i]);
            c.Tri(bottom[0], bottom[i], bottom[i + 1]);
        }
        return c.Make(convex: true);
    }

    /// <summary>
    /// A roof over an outline, solid from its eaves (the bottom of the box) to the slopes, which reach the top
    /// of the box at the ridge: the slopes on surface 0, gable ends (and a shed's sides) on 1, the underside on
    /// 2. A hip or gable whose skeleton cannot be made cleanly is laid flat, the box's height thick.
    /// </summary>
    private static ShapeMesh Roof(Vector3 size, List<Point2> outline, RoofStyle style)
    {
        var o = Polygons.CounterClockwise(outline);
        if (style == RoofStyle.Flat) return Prism(size, o, null);
        var fit = new PlanFit(o, size);
        double y0 = -size.Y * 0.5;
        var b = new Builder();
        var parts = new List<MeshAsset>();
        var bottomTris = Polygons.Triangulate(o);

        if (style == RoofStyle.Shed)
        {
            double z0 = o.Min(p => p.Y), z1 = o.Max(p => p.Y);
            double Y(Point2 p) => z1 > z0 ? y0 + size.Y * (p.Y - z0) / (z1 - z0) : y0 + size.Y;
            b.Slot = 1;
            for (int i = 0; i < o.Count; i++)
            {
                var p = o[i]; var q = o[(i + 1) % o.Count];
                b.Quad(fit.At(p, y0), fit.At(p, Y(p)), fit.At(q, Y(q)), fit.At(q, y0));
            }
            b.Slot = 0;
            for (int t = 0; t < bottomTris.Count; t += 3)
                b.Tri(fit.At(o[bottomTris[t]], Y(o[bottomTris[t]])), fit.At(o[bottomTris[t + 2]], Y(o[bottomTris[t + 2]])), fit.At(o[bottomTris[t + 1]], Y(o[bottomTris[t + 1]])));
            b.Slot = 2;
            for (int t = 0; t < bottomTris.Count; t += 3)
                b.Tri(fit.At(o[bottomTris[t]], y0), fit.At(o[bottomTris[t + 1]], y0), fit.At(o[bottomTris[t + 2]], y0));
            if (Polygons.IsConvex(o, Enumerable.Range(0, o.Count).ToList())) return new ShapeMesh(b.Make(convex: true), null);
            foreach (var piece in Polygons.ConvexPieces(o, bottomTris))
                parts.Add(Column(piece.Select(i => fit.At(o[i], Y(o[i]))).ToList(), piece.Select(i => fit.At(o[i], y0)).ToList()));
            return new ShapeMesh(b.Make(convex: false), parts.ToArray());
        }

        var roof = StraightSkeleton.Of(o, gables: style == RoofStyle.Gable);
        if (roof == null || !(roof.Top > 0)) return Prism(size, o, null);
        double Height(int node) => y0 + size.Y * roof.Height[node] / roof.Top;
        Vector3 P(int node) => fit.At(roof.Plan[node], Height(node));
        int edges = o.Count;
        for (int k = 0; k < edges; k++)
        {
            var face = roof.Faces[k];
            if (roof.Vertical[k])
            {
                // A gable: the end of the house carried up to the ridge, facing out of the outline.
                b.Slot = 1;
                Vector3 s0 = P(face[0]), e0 = P(face[1]), q = P(face[2]);
                var d = roof.Plan[face[1]] - roof.Plan[face[0]];
                var outward = new Vector3((float)d.Y, 0, (float)-d.X);
                if (Vector3.Dot(Vector3.Cross(e0 - s0, q - s0), outward) >= 0) b.Tri(s0, e0, q); else b.Tri(s0, q, e0);
                continue;
            }
            var plan = face.Select(i => roof.Plan[i]).ToList();
            var tris = Polygons.Triangulate(plan);
            for (int t = 0; t < tris.Count; t += 3)
            {
                int a = face[tris[t]], c1 = face[tris[t + 1]], c2 = face[tris[t + 2]];
                b.Slot = 0;
                b.Tri(P(a), P(c2), P(c1));
                var top = new List<Vector3> { P(a), P(c1), P(c2) };
                var bottom = new List<Vector3> { fit.At(roof.Plan[a], y0), fit.At(roof.Plan[c1], y0), fit.At(roof.Plan[c2], y0) };
                parts.Add(Column(top, bottom));
            }
        }
        b.Slot = 2;
        for (int t = 0; t < bottomTris.Count; t += 3)
            b.Tri(fit.At(o[bottomTris[t]], y0), fit.At(o[bottomTris[t + 1]], y0), fit.At(o[bottomTris[t + 2]], y0));
        return new ShapeMesh(b.Make(convex: false), parts.ToArray());
    }

    // ── A profile along a path ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// A profile drawn along a path, mitred at each bend: the sides on surface 0, the two ends on 1. Across is
    /// level and to the right of the way the path goes, up is square to both. The pieces a body meets are each
    /// stretch of the path times each convex piece of the profile.
    /// </summary>
    private static ShapeMesh Sweep(Vector3 size, List<Point2> profile, List<Vector3d> path)
    {
        var pr = Polygons.CounterClockwise(profile);
        int m = path.Count - 1, n = pr.Count;
        var tangent = new Vector3d[m]; var across = new Vector3d[m]; var upv = new Vector3d[m];
        for (int i = 0; i < m; i++)
        {
            var d = path[i + 1] - path[i];
            tangent[i] = d * (1 / d.Length);
            var a = new Vector3d(tangent[i].Z, 0, -tangent[i].X);
            across[i] = a * (1 / a.Length);
            upv[i] = Vector3d.Cross(tangent[i], across[i]);
        }
        var rings = new Vector3d[m + 1][];
        for (int r = 0; r <= m; r++)
        {
            int seg = Math.Min(r, m - 1), before = Math.Max(r - 1, 0);
            rings[r] = new Vector3d[n];
            for (int k = 0; k < n; k++)
            {
                if (r == 0 || r == m)
                {
                    rings[r][k] = path[r] + across[seg] * pr[k].X + upv[seg] * pr[k].Y;
                    continue;
                }
                // Where the line of this corner along the stretch before meets the mitre plane.
                var q = path[r] + across[before] * pr[k].X + upv[before] * pr[k].Y;
                var mitre = tangent[before] + tangent[r];
                mitre = mitre * (1 / mitre.Length);
                double lambda = -Vector3d.Dot(q - path[r], mitre) / Vector3d.Dot(tangent[before], mitre);
                rings[r][k] = q + tangent[before] * lambda;
            }
        }
        var lo = new Vector3d(double.MaxValue, double.MaxValue, double.MaxValue);
        var hi = new Vector3d(double.MinValue, double.MinValue, double.MinValue);
        foreach (var ring in rings) foreach (var p in ring) { lo = Vector3d.Min(lo, p); hi = Vector3d.Max(hi, p); }
        var centre = (lo + hi) * 0.5;
        var ext = hi - lo;
        double sx = ext.X > 0 ? size.X / ext.X : 1, sy = ext.Y > 0 ? size.Y / ext.Y : 1, sz = ext.Z > 0 ? size.Z / ext.Z : 1;
        Vector3 F(Vector3d p) => new((float)((p.X - centre.X) * sx), (float)((p.Y - centre.Y) * sy), (float)((p.Z - centre.Z) * sz));
        var v = rings.Select(ring => ring.Select(F).ToArray()).ToArray();

        var b = new Builder();
        for (int r = 0; r < m; r++)
            for (int k = 0; k < n; k++)
            {
                int q = (k + 1) % n;
                b.Quad(v[r][k], v[r][q], v[r + 1][q], v[r + 1][k]);
            }
        var capTris = Polygons.Triangulate(pr);
        b.Slot = 1;
        for (int t = 0; t < capTris.Count; t += 3)
        {
            b.Tri(v[0][capTris[t]], v[0][capTris[t + 2]], v[0][capTris[t + 1]]);
            b.Tri(v[m][capTris[t]], v[m][capTris[t + 1]], v[m][capTris[t + 2]]);
        }
        bool convexProfile = Polygons.IsConvex(pr, Enumerable.Range(0, n).ToList());
        if (m == 1 && convexProfile) return new ShapeMesh(b.Make(convex: true), null);
        var pieces = convexProfile ? new List<List<int>> { Enumerable.Range(0, n).ToList() } : Polygons.ConvexPieces(pr, capTris);
        var parts = new List<MeshAsset>();
        for (int r = 0; r < m; r++)
            foreach (var piece in pieces)
            {
                var c = new Builder();
                for (int i = 0; i < piece.Count; i++)
                {
                    int a = piece[i], q = piece[(i + 1) % piece.Count];
                    c.Quad(v[r][a], v[r][q], v[r + 1][q], v[r + 1][a]);
                }
                for (int i = 1; i + 1 < piece.Count; i++)
                {
                    c.Tri(v[r][piece[0]], v[r][piece[i + 1]], v[r][piece[i]]);
                    c.Tri(v[r + 1][piece[0]], v[r + 1][piece[i]], v[r + 1][piece[i + 1]]);
                }
                parts.Add(c.Make(convex: true));
            }
        return new ShapeMesh(b.Make(convex: false), parts.ToArray());
    }
}

/// <summary>A point or direction in doubles, for the sweeps.</summary>
public readonly record struct Vector3d(double X, double Y, double Z)
{
    public static Vector3d operator +(Vector3d a, Vector3d b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vector3d operator -(Vector3d a, Vector3d b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vector3d operator *(Vector3d a, double s) => new(a.X * s, a.Y * s, a.Z * s);
    public static double Dot(Vector3d a, Vector3d b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    public static Vector3d Cross(Vector3d a, Vector3d b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    public static Vector3d Min(Vector3d a, Vector3d b) => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z));
    public static Vector3d Max(Vector3d a, Vector3d b) => new(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z));
    public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
}

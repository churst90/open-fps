using System.Numerics;

namespace OpenFPS.Sound.Core.Nature;

// An open water's surface heard from the whole of itself (docs/RUNNING_WATER.md 15.11): cut at the ear into pieces of
// equal angle, as a line is, each piece the part of the surface between two bearings.

/// <summary>An area cut at the ear (<see cref="ExtendedSources.CutArea"/>): each piece's place and gain, and the points of
/// the surface its synth reads the water at, with the area each stands for.</summary>
public sealed class AreaPieces
{
    /// <summary>Each piece's place: where its sound arrives from and the gain its voice takes over the source's own.</summary>
    public required ExtendedSources.PathPlace[] Places { get; init; }

    /// <summary>Each piece's area, m².</summary>
    public required float[] PieceArea { get; init; }

    /// <summary>Piece i's points are SampleStart[i] up to SampleStart[i + 1].</summary>
    public required int[] SampleStart { get; init; }

    /// <summary>Each point of the surface, east and north (the world's x and z), and the area it stands for, m².</summary>
    public required Vector2[] Samples { get; init; }
    public required float[] SampleArea { get; init; }

    /// <summary>How far the ear may move before the area is cut again, m: a quarter of its distance from the water's
    /// nearest edge, at least a quarter of a metre.</summary>
    public float RecutMetres { get; init; }

    /// <summary>Whether the ear was over the water when it was cut.</summary>
    public bool Inside { get; init; }

    /// <summary>The bearing the cut's edges are measured from (radians from +x toward +z), and the water's surface height.</summary>
    public float Facing { get; init; }
    public float Surface { get; init; }

    /// <summary>Where the ear was when it was cut: what a synth reads which piece a part of it is in from (the bearing of
    /// the part from here, <see cref="PieceOf"/>).</summary>
    public Vector3 Ear { get; init; }

    /// <summary>A fire's flames over its bed (docs/FIRE.md 12.2): how many voices above the pieces carry the roar, each over
    /// a run of neighbouring pieces (<see cref="ExtendedSources.FlameRun"/>), and how far above the bed they are, m. None for
    /// anything else.</summary>
    public int Flames { get; init; }
    public float Rise { get; init; }

    /// <summary>Where the thing cut stands and how it is turned: a fire's own frame (x across, z along), so its synth finds
    /// where each of its bodies of fire is in the world.</summary>
    public Vector3 Origin { get; init; }
    public Quaternion Turn { get; init; } = Quaternion.Identity;

    /// <summary>A wood's crowns cut at the ear (<see cref="ExtendedSources.CutPoints"/>): the samples are the crowns themselves
    /// (each its share of the wood in <see cref="SampleArea"/>, its height in <see cref="SampleHeight"/>), not strips of a
    /// surface.</summary>
    public bool Discrete { get; init; }
    public float[]? SampleHeight { get; init; }

    /// <summary>The whole surface's area, m².</summary>
    public float TotalArea => PieceArea.Sum();

    /// <summary>The piece a point (east, north) is in, by its bearing from where the ear was when it was cut; a point
    /// beyond either end of the bearings cut goes to the nearer end.</summary>
    public int PieceOf(Vector2 at, float[] edges)
    {
        double a = Math.IEEERemainder(Math.Atan2(at.Y - Ear.Z, at.X - Ear.X) - Facing, 2 * Math.PI);
        int lo = 0, hi = edges.Length - 2;
        if (a <= edges[0]) return 0;
        if (a >= edges[^1]) return hi;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (a < edges[mid + 1]) hi = mid; else lo = mid + 1;
        }
        return lo;
    }
}

public static partial class ExtendedSources
{
    /// <summary>Rays a piece of an area is integrated along, at most: one every two degrees of it, at least four.</summary>
    private const int MostRaysAPiece = 16;

    /// <summary>A piece's surface is read at points at most this far apart along a ray, m, and at most three to a ray.</summary>
    private const float AreaSampleMetres = 20f;

    /// <summary>Whether a point (east, north) is inside an outline (its points' x and z), by the even-odd rule.</summary>
    public static bool Inside(LinePath outline, Vector2 p)
    {
        bool inside = false;
        var pts = outline.Points;
        for (int i = 0, j = pts.Length - 1; i < pts.Length; j = i++)
        {
            float xi = pts[i].X, zi = pts[i].Z, xj = pts[j].X, zj = pts[j].Z;
            if ((zi > p.Y) != (zj > p.Y) && p.X < (xj - xi) * (p.Y - zi) / (zj - zi) + xi) inside = !inside;
        }
        return inside;
    }

    /// <summary>The bearings an outline's area covers at the ear (radians from +x toward +z), and their middle as the
    /// direction the cut's angles are measured from: all round from over the water, else the span its corners cover
    /// walked round it.</summary>
    private static (double From, double To, double Facing, bool Inside) AreaSpan(LinePath outline, Vector3 ear)
    {
        var e = new Vector2(ear.X, ear.Z);
        if (Inside(outline, e)) return (-Math.PI, Math.PI, 0.0, true);
        var pts = outline.Points;
        Vector2 mid = Vector2.Zero;
        foreach (var p in pts) mid += new Vector2(p.X, p.Z);
        mid /= pts.Length;
        double facing = Math.Atan2(mid.Y - e.Y, mid.X - e.X);
        double lo = double.MaxValue, hi = double.MinValue, walked = 0, last = 0;
        for (int i = 0; i < pts.Length; i++)
        {
            double a = Math.Atan2(pts[i].Z - e.Y, pts[i].X - e.X) - facing;
            a = Math.IEEERemainder(a, 2 * Math.PI);
            if (i == 0) walked = a;
            else
            {
                double step = Math.IEEERemainder(a - last, 2 * Math.PI);
                walked += step;
            }
            last = a;
            lo = Math.Min(lo, walked);
            hi = Math.Max(hi, walked);
        }
        if (hi - lo >= 2 * Math.PI - 1e-3) return (-Math.PI, Math.PI, facing, false);
        return (lo, hi, facing, false);
    }

    /// <summary>How much of the ear's view an area covers, degrees: 360 from over it.</summary>
    public static float AreaDegrees(LinePath outline, Vector3 ear)
    {
        var (from, to, _, _) = AreaSpan(outline, ear);
        return (float)((to - from) * 180.0 / Math.PI);
    }

    /// <summary>How many pieces an area wants from here, by the angle it covers (<see cref="WantedPieces(LineView, int)"/>).</summary>
    public static int WantedAreaPieces(LinePath outline, Vector3 ear, int previous) => WantedPieces(AreaDegrees(outline, ear), previous);

    /// <summary>
    /// An open water's surface (inside <paramref name="outline"/>, its points on the water's edge) cut at the ear into
    /// <paramref name="pieces"/> pieces of equal angle, as a line is cut: from over the water all round, from its shore
    /// across the bearings it covers. Each piece is placed by the mixer's law as a piece of a line is (Place): at the
    /// centroid of what each part of it brings to the ear, with the gain that makes the law there the law's mean over
    /// the piece, the ear the surface's height above it. The law along each bearing is in closed form; the bearings of a
    /// piece are summed, a few to a piece. Each piece's points, where its synth reads the water, a few along each bearing
    /// with the area each stands for. The cut's edges are the bearings, radians from the middle of those it covers.
    /// A fire's bed (<paramref name="fire"/>, <paramref name="flames"/> over 0) has its flames <paramref name="rise"/> m above
    /// it, each over a run of its pieces (FlameRun); <paramref name="origin"/> and <paramref name="turn"/> are its own frame.
    /// </summary>
    public static LineCut CutArea(LinePath outline, Vector3 ear, int pieces, float reference, bool fire = false, int flames = 0, float rise = 0f,
                                  Vector3 origin = default, Quaternion? turn = null)
    {
        int n = Math.Clamp(pieces, 1, MaxPlaces);
        var (from, to, facing, inside) = AreaSpan(outline, ear);
        float surface = SurfaceOf(outline);
        var edges = new float[n + 1];
        for (int i = 0; i <= n; i++) edges[i] = (float)(from + (to - from) * i / n);
        var places = new PathPlace[n];
        var areas = new float[n];
        var starts = new int[n + 1];
        var samples = new List<Vector2>();
        var sampleArea = new List<float>();
        for (int i = 0; i < n; i++)
        {
            starts[i] = samples.Count;
            (places[i], areas[i]) = AreaPiece(outline, ear, facing + edges[i], facing + edges[i + 1], reference, surface, inside, samples, sampleArea);
        }
        starts[n] = samples.Count;
        var e = new Vector2(ear.X, ear.Z);
        float edgeDistance = Vector2.Distance(e, Flat(outline.Nearest(ear with { Y = surface }, out _)));
        // A fire (its own frame, <paramref name="origin"/>) is cut again when the ear has moved an eighth of its shortest
        // piece, the one beside it: a piece is dTheta of the bearings, edgeDistance * dTheta m of its edge, and beside a
        // small fire a bearing swings fastest, where the pieces are thin and the ear is close (a line's own rule is a
        // quarter of its shortest piece along it). An open water's pieces are wide, and it keeps its quarter of the way to
        // its shore. Both bounded below, so a walked-around fire recuts a few cm, a few cheap rays an outline.
        // Measured 2026-10-18 in a Debug build: one cut of a 0.9 m pit or a burning car takes 21 us, and the fire synth's
        // redistribution after it 80 us. Walked round at 1.5 m the pit recuts every 2 cm, up to 70 times a second, so about
        // 7 ms of the placement thread's second, 0.1 ms a frame: small enough to stay on that thread.
        float recut = fire
            ? MathF.Max(0.02f, 0.125f * edgeDistance * (float)((to - from) / n))
            : MathF.Max(0.25f, 0.25f * edgeDistance);
        return new LineCut(edges)
        {
            Path = outline,
            Area = new AreaPieces
            {
                Places = places, PieceArea = areas, SampleStart = starts, Samples = samples.ToArray(), SampleArea = sampleArea.ToArray(),
                RecutMetres = recut, Inside = inside, Facing = (float)facing, Surface = surface,
                Ear = ear, Flames = n > 1 ? Math.Clamp(flames, 0, n) : 0, Rise = rise, Origin = origin, Turn = turn ?? Quaternion.Identity,
            },
        };

        static Vector2 Flat(Vector3 v) => new(v.X, v.Z);
    }

    /// <summary>
    /// Piece <paramref name="piece"/> of an area's cut placed from where the ear is now: the same bearings (the cut's, from
    /// the ear), integrated afresh, so its place and gain glide as the ear moves between cuts rather than stepping when it
    /// is cut again. A few hundred ray crossings, on the placing thread (tens of microseconds for a pond's outline).
    /// </summary>
    public static PathPlace PlaceArea(LineCut cut, Vector3 ear, int piece, float reference)
    {
        var area = cut.Area!;
        bool inside = Inside(cut.Path!, new Vector2(ear.X, ear.Z));
        return AreaPiece(cut.Path!, ear, area.Facing + cut.Edges[piece], area.Facing + cut.Edges[piece + 1], reference, area.Surface, inside, null, null).Place;
    }

    /// <summary>The water's surface height under an outline: its points' mean.</summary>
    private static float SurfaceOf(LinePath outline)
    {
        float surface = 0f;
        foreach (var p in outline.Points) surface += p.Y;
        return surface / outline.Points.Length;
    }

    /// <summary>
    /// The part of the surface between two bearings from the ear (radians from +x toward +z): its place and gain by the
    /// mixer's law (the centroid of what each part brings, the gain that makes the law there its mean over the part), its
    /// area, and when asked its points, a few along each bearing with the area each stands for. The law along each bearing
    /// is in closed form; the bearings are summed, one every two degrees, four to sixteen.
    /// </summary>
    private static (PathPlace Place, float Area) AreaPiece(LinePath outline, Vector3 ear, double thetaFrom, double thetaTo, float reference,
                                                          float surface, bool inside, List<Vector2>? samples, List<float>? sampleArea)
    {
        double r = MathF.Max(0.1f, reference), h = Math.Max(0.05, Math.Abs(ear.Y - surface));
        double span = thetaTo - thetaFrom;
        int rays = Math.Clamp((int)Math.Ceiling(Math.Abs(span) * 180.0 / Math.PI / 2.0), 4, MostRaysAPiece);
        double dTheta = span / rays;
        double law = 0, area = 0, mx = 0, mz = 0;
        Span<double> hits = stackalloc double[64];
        var e = new Vector2(ear.X, ear.Z);
        for (int k = 0; k < rays; k++)
        {
            double theta = thetaFrom + dTheta * (k + 0.5);
            var dir = new Vector2((float)Math.Cos(theta), (float)Math.Sin(theta));
            int count = Crossings(outline, e, dir, hits);
            // Inside the water from the ear to its first crossing when over it, else between the first two, and so on.
            for (int c = inside ? -1 : 0; c + 1 < count; c += 2)
            {
                double a = c < 0 ? 0.0 : hits[c], b = hits[c + 1];
                if (!(b > a)) continue;
                double l = LawAlongRay(a, b, h, r), m = MomentAlongRay(a, b, h, r);
                law += dTheta * l;
                area += dTheta * 0.5 * (b * b - a * a);
                mx += dTheta * m * dir.X;
                mz += dTheta * m * dir.Y;
                if (samples == null) continue;
                int parts = Math.Clamp((int)Math.Ceiling((b - a) / AreaSampleMetres), 1, 3);
                for (int s = 0; s < parts; s++)
                {
                    double sa = a + (b - a) * s / parts, sb = a + (b - a) * (s + 1) / parts;
                    // Each point at the middle of its part's area.
                    samples.Add(e + dir * (float)Math.Sqrt(0.5 * (sa * sa + sb * sb)));
                    sampleArea!.Add((float)(dTheta * 0.5 * (sb * sb - sa * sa)));
                }
            }
        }
        if (!(law > 1e-30) || !(area > 1e-9))
        {
            double mid = 0.5 * (thetaFrom + thetaTo);
            return (new PathPlace(new Vector3(ear.X + (float)Math.Cos(mid), surface, ear.Z + (float)Math.Sin(mid)), 0f, 0f), 0f);
        }
        var centre = new Vector3(ear.X + (float)(mx / law), surface, ear.Z + (float)(mz / law));
        double at = Math.Min(1.0, r / Math.Max(1e-6, Vector3.Distance(centre, ear)));
        return (new PathPlace(centre, 0f, (float)(Math.Sqrt(law / area) / at)), (float)area);
    }

    /// <summary>The mean of the mixer's law over an area's whole surface from the ear (<see cref="MeanLawPower(LinePath,
    /// Vector3, float)"/> for a line): how loud it is here against one point of it at the same reference.</summary>
    public static float AreaMeanLawPower(LinePath outline, Vector3 ear, float reference)
    {
        var (from, to, facing, inside) = AreaSpan(outline, ear);
        double r = MathF.Max(0.1f, reference);
        float surface = SurfaceOf(outline);
        double h = Math.Max(0.05, Math.Abs(ear.Y - surface));
        const int Rays = 72;
        double dTheta = (to - from) / Rays, law = 0, area = 0;
        Span<double> hits = stackalloc double[64];
        var e = new Vector2(ear.X, ear.Z);
        for (int k = 0; k < Rays; k++)
        {
            double theta = facing + from + dTheta * (k + 0.5);
            var dir = new Vector2((float)Math.Cos(theta), (float)Math.Sin(theta));
            int count = Crossings(outline, e, dir, hits);
            for (int c = inside ? -1 : 0; c + 1 < count; c += 2)
            {
                double a = c < 0 ? 0.0 : hits[c], b = hits[c + 1];
                if (!(b > a)) continue;
                law += dTheta * LawAlongRay(a, b, h, r);
                area += dTheta * 0.5 * (b * b - a * a);
            }
        }
        return area > 1e-9 ? (float)(law / area) : 0f;
    }

    /// <summary>Where a ray from <paramref name="from"/> along <paramref name="dir"/> crosses an outline's edges (its points'
    /// x and z, closed), m along it, ascending; how many (at most the span's length).</summary>
    private static int Crossings(LinePath outline, Vector2 from, Vector2 dir, Span<double> hits)
    {
        var pts = outline.Points;
        int count = 0;
        for (int i = 0, j = pts.Length - 1; i < pts.Length; j = i++)
        {
            var a = new Vector2(pts[j].X, pts[j].Z);
            var d = new Vector2(pts[i].X, pts[i].Z) - a;
            double den = (double)dir.X * d.Y - (double)dir.Y * d.X;
            if (Math.Abs(den) < 1e-12) continue;
            double ax = a.X - from.X, az = a.Y - from.Y;
            double t = (ax * d.Y - az * d.X) / den;
            double u = (ax * dir.Y - az * dir.X) / den;
            if (t < 0 || u < 0 || u >= 1) continue;
            if (count < hits.Length) hits[count++] = t;
        }
        hits[..count].Sort();
        return count;
    }

    /// <summary>∫ min(1, r²/(ρ² + h²)) ρ dρ from a to b: the mixer's law over a strip of a bearing's surface, the ear h above
    /// it, flat inside r.</summary>
    private static double LawAlongRay(double a, double b, double h, double r)
    {
        double r2 = r * r;
        double G(double rho)
        {
            double u = rho * rho + h * h;
            return u <= r2 ? 0.5 * u : 0.5 * r2 * (1.0 + Math.Log(u / r2));
        }
        return G(b) - G(a);
    }

    /// <summary>∫ min(1, r²/(ρ² + h²)) ρ² dρ from a to b: the same strip's moment about the ear, for its centroid.</summary>
    private static double MomentAlongRay(double a, double b, double h, double r)
    {
        double c = Math.Sqrt(Math.Max(0.0, r * r - h * h));
        double K(double rho)
        {
            if (rho <= c) return rho * rho * rho / 3.0;
            return c * c * c / 3.0 + r * r * ((rho - h * Math.Atan(rho / h)) - (c - h * Math.Atan(c / h)));
        }
        return K(b) - K(a);
    }
}

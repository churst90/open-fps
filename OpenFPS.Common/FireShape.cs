using System.Globalization;
using System.Numerics;
using System.Text;

namespace OpenFPS.Common;

/// <summary>What outline a fire's burning area has.</summary>
public enum FireShapeKind
{
    Rectangle,
    Circle,
    /// <summary>Any outline, given as its corners in order.</summary>
    Outline,
}

/// <summary>
/// The ground a fire burns over, in its own frame (x across, z along, metres from its middle): a
/// rectangle, a circle, or an outline (docs/FIRE.md 12.2). The fire's bodies are laid inside it and its
/// places spread as it does, by its own second moments, so a round pit, a long trench and a bent hedge
/// each sound their own shape.
/// </summary>
public sealed record FireShape
{
    public FireShapeKind Kind { get; init; }
    /// <summary>Across, m: a rectangle's width, a circle's diameter, an outline's extent.</summary>
    public float Width { get; init; }
    /// <summary>Along, m.</summary>
    public float Depth { get; init; }
    /// <summary>An outline's corners, centred on its middle.</summary>
    public Vector2[] Points { get; init; } = Array.Empty<Vector2>();

    public static FireShape Rectangle(float width, float depth)
        => new() { Kind = FireShapeKind.Rectangle, Width = MathF.Max(0.05f, width), Depth = MathF.Max(0.05f, depth) };

    public static FireShape Circle(float diameter)
        => new() { Kind = FireShapeKind.Circle, Width = MathF.Max(0.05f, diameter), Depth = MathF.Max(0.05f, diameter) };

    /// <summary>An outline from its corners anywhere: moved so its area's middle is at the origin.</summary>
    public static FireShape Outline(IReadOnlyList<Vector2> corners)
    {
        if (corners.Count < 3) return Rectangle(1f, 1f);
        var pts = corners.ToArray();
        var (area, cx, cz) = PolygonCentroid(pts);
        if (MathF.Abs(area) < 1e-4f) return Rectangle(1f, 1f);
        for (int i = 0; i < pts.Length; i++) pts[i] = new Vector2(pts[i].X - cx, pts[i].Y - cz);
        float minX = pts.Min(p => p.X), maxX = pts.Max(p => p.X), minZ = pts.Min(p => p.Y), maxZ = pts.Max(p => p.Y);
        return new() { Kind = FireShapeKind.Outline, Width = maxX - minX, Depth = maxZ - minZ, Points = pts };
    }

    /// <summary>The fire a placed thing's size describes: round, its circle (the diameter is its x size);
    /// otherwise its rectangle. The host says which is round (a polygon is its box until polygons carry
    /// their corners).</summary>
    public static FireShape Footprint(bool round, Vector3 size)
        => round
            ? Circle(size.X)
            : Rectangle(size.X, size.Z);

    /// <summary>The same outline to a centimetre.</summary>
    public bool SameAs(FireShape other)
        => Kind == other.Kind && MathF.Abs(Width - other.Width) < 0.01f && MathF.Abs(Depth - other.Depth) < 0.01f
           && (Kind != FireShapeKind.Outline || Points.AsSpan().SequenceEqual(other.Points));

    /// <summary>Square metres.</summary>
    public float Area => Kind switch
    {
        FireShapeKind.Circle => MathF.PI * 0.25f * Width * Width,
        FireShapeKind.Outline => MathF.Abs(PolygonCentroid(Points).Area),
        _ => Width * Depth,
    };

    /// <summary>The diameter of the circle of the same area, m.</summary>
    public float EquivalentDiameter => MathF.Sqrt(4f * Area / MathF.PI);

    /// <summary>Whether a point (x across, z along) is inside.</summary>
    public bool Contains(float x, float z)
    {
        switch (Kind)
        {
            case FireShapeKind.Circle:
            {
                float r = 0.5f * Width;
                return x * x + z * z <= r * r;
            }
            case FireShapeKind.Outline:
            {
                bool inside = false;
                var p = Points;
                for (int i = 0, j = p.Length - 1; i < p.Length; j = i++)
                    if ((p[i].Y > z) != (p[j].Y > z) && x < (p[j].X - p[i].X) * (z - p[i].Y) / (p[j].Y - p[i].Y) + p[i].X)
                        inside = !inside;
                return inside;
            }
            default:
                return MathF.Abs(x) <= 0.5f * Width && MathF.Abs(z) <= 0.5f * Depth;
        }
    }

    /// <summary>The point of the shape nearest a point (itself if inside).</summary>
    public Vector2 Closest(float x, float z)
    {
        switch (Kind)
        {
            case FireShapeKind.Circle:
            {
                float r = 0.5f * Width, len = MathF.Sqrt(x * x + z * z);
                return len <= r ? new Vector2(x, z) : new Vector2(x, z) * (r / len);
            }
            case FireShapeKind.Outline:
            {
                if (Contains(x, z)) return new Vector2(x, z);
                var p = new Vector2(x, z);
                Vector2 best = Points[0];
                float bestD = float.MaxValue;
                for (int i = 0, j = Points.Length - 1; i < Points.Length; j = i++)
                {
                    var a = Points[j];
                    var ab = Points[i] - a;
                    float u = Math.Clamp(Vector2.Dot(p - a, ab) / MathF.Max(1e-9f, ab.LengthSquared()), 0f, 1f);
                    var c = a + ab * u;
                    float dd = Vector2.DistanceSquared(c, p);
                    if (dd < bestD) { bestD = dd; best = c; }
                }
                return best;
            }
            default:
                return new Vector2(Math.Clamp(x, -0.5f * Width, 0.5f * Width), Math.Clamp(z, -0.5f * Depth, 0.5f * Depth));
        }
    }

    /// <summary>How far a point is outside the shape, m (0 inside).</summary>
    public float Outside(float x, float z) => Vector2.Distance(Closest(x, z), new Vector2(x, z));

    /// <summary>
    /// How the area spreads about its middle: the variances across and along and their covariance, m².
    /// A uniform w × d rectangle has w²/12 and d²/12; a circle of radius R has R²/4 each way.
    /// </summary>
    public (float VarX, float VarZ, float CovXZ) Moments()
    {
        switch (Kind)
        {
            case FireShapeKind.Circle:
            {
                float r = 0.5f * Width;
                return (0.25f * r * r, 0.25f * r * r, 0f);
            }
            case FireShapeKind.Outline:
            {
                // Green's theorem over the polygon (its middle is the origin).
                double a = 0, ixx = 0, izz = 0, ixz = 0;
                var p = Points;
                for (int i = 0, j = p.Length - 1; i < p.Length; j = i++)
                {
                    double x0 = p[j].X, z0 = p[j].Y, x1 = p[i].X, z1 = p[i].Y;
                    double cr = x0 * z1 - x1 * z0;
                    a += cr;
                    ixx += cr * (x0 * x0 + x0 * x1 + x1 * x1);
                    izz += cr * (z0 * z0 + z0 * z1 + z1 * z1);
                    ixz += cr * (x0 * z1 + 2 * x0 * z0 + 2 * x1 * z1 + x1 * z0);
                }
                a *= 0.5;
                if (Math.Abs(a) < 1e-9) return (0f, 0f, 0f);
                return ((float)(ixx / 12.0 / a), (float)(izz / 12.0 / a), (float)(ixz / 24.0 / a));
            }
            default:
                return (Width * Width / 12f, Depth * Depth / 12f, 0f);
        }
    }

    // ── In a key ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>"r3.5x2", "c0.9", or "p" and the corners "x,z;x,z;...": what goes after "shape=" in a
    /// fire's key (<see cref="FireSpec.KeyFor(string, double?, FireShape?)"/>).</summary>
    public string Format()
    {
        var ic = CultureInfo.InvariantCulture;
        switch (Kind)
        {
            case FireShapeKind.Circle: return "c" + Width.ToString("0.##", ic);
            case FireShapeKind.Outline:
            {
                var sb = new StringBuilder("p");
                for (int i = 0; i < Points.Length; i++)
                {
                    if (i > 0) sb.Append(';');
                    sb.Append(Points[i].X.ToString("0.##", ic)).Append(',').Append(Points[i].Y.ToString("0.##", ic));
                }
                return sb.ToString();
            }
            default: return "r" + Width.ToString("0.##", ic) + "x" + Depth.ToString("0.##", ic);
        }
    }

    public static bool TryParse(string text, out FireShape shape)
    {
        shape = Rectangle(1f, 1f);
        if (string.IsNullOrEmpty(text)) return false;
        var ic = CultureInfo.InvariantCulture;
        string body = text[1..];
        switch (char.ToLowerInvariant(text[0]))
        {
            case 'c':
                if (!float.TryParse(body, NumberStyles.Float, ic, out float d) || !(d > 0f)) return false;
                shape = Circle(d);
                return true;
            case 'r':
            {
                int x = body.IndexOf('x');
                if (x < 0 || !float.TryParse(body[..x], NumberStyles.Float, ic, out float w)
                          || !float.TryParse(body[(x + 1)..], NumberStyles.Float, ic, out float dep) || !(w > 0f) || !(dep > 0f)) return false;
                shape = Rectangle(w, dep);
                return true;
            }
            case 'p':
            {
                var pts = new List<Vector2>();
                foreach (var pair in body.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    int c = pair.IndexOf(',');
                    if (c < 0 || !float.TryParse(pair[..c], NumberStyles.Float, ic, out float px)
                              || !float.TryParse(pair[(c + 1)..], NumberStyles.Float, ic, out float pz)) return false;
                    pts.Add(new Vector2(px, pz));
                }
                if (pts.Count < 3) return false;
                shape = Outline(pts);
                return true;
            }
        }
        return false;
    }

    private static (float Area, float Cx, float Cz) PolygonCentroid(Vector2[] p)
    {
        double a = 0, cx = 0, cz = 0;
        for (int i = 0, j = p.Length - 1; i < p.Length; j = i++)
        {
            double cr = (double)p[j].X * p[i].Y - (double)p[i].X * p[j].Y;
            a += cr;
            cx += (p[j].X + p[i].X) * cr;
            cz += (p[j].Y + p[i].Y) * cr;
        }
        a *= 0.5;
        if (Math.Abs(a) < 1e-12) return (0f, 0f, 0f);
        return ((float)a, (float)(cx / (6 * a)), (float)(cz / (6 * a)));
    }
}

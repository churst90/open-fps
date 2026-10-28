using System.Numerics;

namespace OpenFPS.Geometry;

/// <summary>What a swept shape is: a ball, an upright capsule, or a box turned any way.</summary>
public enum SweepKind : byte { Sphere, Capsule, Box }

/// <summary>
/// A convex shape that is moved along a segment against the triangle world (<see cref="TriangleWorld.Sweep"/>;
/// docs/PHYSICS_BOUNDARY.md 18.2, step 5), at the place it starts. A ball and a capsule are a segment with a
/// radius (a ball's segment is one point), a box is a centre, three axes and half extents.
/// </summary>
public readonly struct SweptShape
{
    public SweepKind Kind { get; }
    /// <summary>The ends of the axis of a ball or a capsule, in the frame the shape is in.</summary>
    internal readonly Vector3 P0, P1;
    internal readonly float Radius;
    /// <summary>A box's centre, its unit axes and its half extents along them.</summary>
    internal readonly Vector3 Centre, AxisX, AxisY, AxisZ, Half;

    private SweptShape(SweepKind kind, Vector3 p0, Vector3 p1, float radius, Vector3 centre, Vector3 ax, Vector3 ay, Vector3 az, Vector3 half)
    {
        Kind = kind; P0 = p0; P1 = p1; Radius = radius; Centre = centre; AxisX = ax; AxisY = ay; AxisZ = az; Half = half;
    }

    /// <summary>A ball of <paramref name="radius"/> metres round <paramref name="centre"/>.</summary>
    public static SweptShape Sphere(Vector3 centre, float radius)
    {
        RequirePositive(radius, nameof(radius));
        return new SweptShape(SweepKind.Sphere, centre, centre, radius, default, default, default, default, default);
    }

    /// <summary>An upright capsule standing on <paramref name="feet"/>, <paramref name="height"/> metres tall in all (its
    /// two round ends included) and <paramref name="radius"/> round: the shape a walking body is.</summary>
    public static SweptShape Capsule(Vector3 feet, float radius, float height)
    {
        RequirePositive(radius, nameof(radius));
        RequirePositive(height, nameof(height));
        float low = feet.Y + radius, high = feet.Y + height - radius;
        if (high < low) low = high = feet.Y + height * 0.5f;     // shorter than it is wide: a ball
        return new SweptShape(SweepKind.Capsule, new Vector3(feet.X, low, feet.Z), new Vector3(feet.X, high, feet.Z), radius,
                              default, default, default, default, default);
    }

    /// <summary>A box round <paramref name="centre"/>, <paramref name="halfExtents"/> from its middle to each face along
    /// its own axes, turned by <paramref name="rotation"/>.</summary>
    public static SweptShape Box(Vector3 centre, Vector3 halfExtents, Quaternion rotation)
    {
        RequirePositive(halfExtents.X, nameof(halfExtents)); RequirePositive(halfExtents.Y, nameof(halfExtents)); RequirePositive(halfExtents.Z, nameof(halfExtents));
        rotation = rotation.LengthSquared() < 1e-6f ? Quaternion.Identity : Quaternion.Normalize(rotation);
        return new SweptShape(SweepKind.Box, default, default, 0f, centre,
                              Vector3.Transform(Vector3.UnitX, rotation), Vector3.Transform(Vector3.UnitY, rotation),
                              Vector3.Transform(Vector3.UnitZ, rotation), halfExtents);
    }

    private static void RequirePositive(float v, string name)
    {
        if (!(v > 0f) || !float.IsFinite(v)) throw new ArgumentOutOfRangeException(name, v, "must be a positive, finite size");
    }

    internal bool IsBox => Kind == SweepKind.Box;

    /// <summary>The same shape moved by <paramref name="by"/>.</summary>
    internal SweptShape Moved(Vector3 by)
        => new(Kind, P0 + by, P1 + by, Radius, Centre + by, AxisX, AxisY, AxisZ, Half);

    /// <summary>The same shape in the frame of a piece placed by <paramref name="inst"/>.</summary>
    internal SweptShape InFrame(in GeometryInstance inst)
        => IsBox
            ? new SweptShape(Kind, default, default, 0f, inst.ToLocal(Centre), inst.DirectionToLocal(AxisX), inst.DirectionToLocal(AxisY),
                             inst.DirectionToLocal(AxisZ), Half)
            : new SweptShape(Kind, inst.ToLocal(P0), inst.ToLocal(P1), Radius, default, default, default, default, default);

    /// <summary>The box that holds the shape where it is.</summary>
    internal void Bounds(out Vector3 min, out Vector3 max)
    {
        if (IsBox)
        {
            var reach = new Vector3(
                MathF.Abs(AxisX.X) * Half.X + MathF.Abs(AxisY.X) * Half.Y + MathF.Abs(AxisZ.X) * Half.Z,
                MathF.Abs(AxisX.Y) * Half.X + MathF.Abs(AxisY.Y) * Half.Y + MathF.Abs(AxisZ.Y) * Half.Z,
                MathF.Abs(AxisX.Z) * Half.X + MathF.Abs(AxisY.Z) * Half.Y + MathF.Abs(AxisZ.Z) * Half.Z);
            min = Centre - reach; max = Centre + reach;
            return;
        }
        min = Vector3.Min(P0, P1) - new Vector3(Radius); max = Vector3.Max(P0, P1) + new Vector3(Radius);
    }
}

/// <summary>
/// The first thing a swept shape met (<see cref="TriangleWorld.Sweep"/>): how far along the segment (0 to 1), where
/// on the surface it touched, which way the surface faces there, and what it is.
/// </summary>
public struct SweepHit
{
    /// <summary>How far along the segment the shape got before it touched, 0 (touching where it began) to 1.</summary>
    public float Fraction;
    /// <summary>The distance that is, metres.</summary>
    public float Distance;
    /// <summary>The point of the surface nearest the shape at that moment, in the world.</summary>
    public Vector3 Point;
    /// <summary>The unit direction from the surface towards the shape there: a face's normal, or at an edge or a vertex
    /// the way from it to the shape. When the shape began overlapping the surface, from the nearest face's side
    /// the shape's middle is on.</summary>
    public Vector3 Normal;
    public SolidRef Solid;
    /// <summary>The triangle in its piece, as <see cref="GeometryHit.Triangle"/> numbers them (terrain after boxes).</summary>
    public int Triangle;
    public int Owner;
    /// <summary>The shape was already overlapping the surface where it began (by more than the contact slop), so
    /// <see cref="Fraction"/> is 0, whichever way it was being moved.</summary>
    public bool Embedded;
}

/// <summary>What is between a shape and a triangle at one moment, in one frame (the narrow phase of a sweep).</summary>
internal struct ShapeGap
{
    /// <summary>The gap, metres: negative by the depth when they overlap by more than the slop.</summary>
    public float Distance;
    /// <summary>The point of the triangle nearest the shape.</summary>
    public Vector3 OnTriangle;
    /// <summary>Unit, from the triangle to the shape.</summary>
    public Vector3 Normal;
    public bool Embedded;
}

/// <summary>
/// The narrow phase of a sweep: the gap between a convex shape and a triangle (a ball and a capsule by the nearest
/// points of a segment and a triangle, a box by the separating axes when they overlap and the nearest features when
/// they do not), and the first time a shape moved in a straight line is touching it, by conservative advancement:
/// at each step the gap cannot close faster than the shape's speed towards the triangle's nearest point, so the
/// shape is moved by gap over that speed and no further, and it never goes through anything.
/// </summary>
internal static class SweepMath
{
    /// <summary>A gap this small is a touch, metres: float arithmetic on a tile's coordinates is good to a few
    /// hundredths of a millimetre, and a shape stops this close in front of what it meets.</summary>
    public const float Slop = 1e-4f;

    /// <summary>Steps at most per triangle; a shape that has not converged by then is reported where it got to, which is
    /// short of the touch, never past it.</summary>
    private const int MaxSteps = 48;

    /// <summary>Slower than this (metres of delta per unit of gap) is not closing.</summary>
    private const float NotClosing = 1e-12f;

    /// <summary>
    /// The first fraction of <paramref name="delta"/>, up to <paramref name="limit"/>, at which <paramref name="shape"/>
    /// touches the triangle abc (both in one frame). A shape moving away from, or along, a surface it only touches does not
    /// meet it; one that began overlapping it does, at 0.
    /// </summary>
    public static bool FirstTouch(in SweptShape shape, Vector3 delta, Vector3 a, Vector3 b, Vector3 c, float limit,
                                  out float fraction, out ShapeGap gap)
    {
        fraction = 0f;
        float t = 0f;
        gap = default;
        for (int step = 0; step < MaxSteps; step++)
        {
            var at = t == 0f ? shape : shape.Moved(delta * t);
            Measure(at, delta, a, b, c, out gap);
            fraction = t;
            if (gap.Embedded) return true;
            float closing = -Vector3.Dot(delta, gap.Normal);
            if (gap.Distance <= Slop) return closing > NotClosing && Penetrates(shape, delta, a, b, c, t);
            if (closing <= NotClosing) return false;
            t += (gap.Distance - 0.5f * Slop) / closing;
            if (t > limit) return false;
        }
        return Penetrates(shape, delta, a, b, c, fraction);
    }

    /// <summary>
    /// Whether the shape, moving on from <paramref name="from"/>, ever goes more than <see cref="Slop"/> into the
    /// triangle. A ball rolling over the seam of two flat triangles touches the edge of the one it is leaving at a
    /// closing speed that dies away to nothing and is never inside it; stopping there would stop every body on
    /// every floor. The gap between two convex shapes, one moved in a line, is convex in the distance moved, so
    /// its lowest point is found by a ternary search.
    /// </summary>
    private static bool Penetrates(SweptShape shape, Vector3 delta, Vector3 a, Vector3 b, Vector3 c, float from)
    {
        float Gap(float t)
        {
            Measure(shape.Moved(delta * t), delta, a, b, c, out var g);
            return g.Distance;
        }
        if (Gap(1f) < -Slop) return true;
        float lo = from, hi = 1f;
        for (int i = 0; i < 24; i++)
        {
            float m1 = lo + (hi - lo) / 3f, m2 = hi - (hi - lo) / 3f;
            float g1 = Gap(m1), g2 = Gap(m2);
            if (g1 < -Slop || g2 < -Slop) return true;
            if (g1 < g2) hi = m2; else lo = m1;
        }
        return false;
    }

    /// <summary>The gap between the shape and triangle abc, and which way it faces (<paramref name="delta"/> only
    /// breaks a tie when the shape is on the triangle and no side is nearer).</summary>
    public static void Measure(in SweptShape s, Vector3 delta, Vector3 a, Vector3 b, Vector3 c, out ShapeGap gap)
    {
        if (s.IsBox) MeasureBox(s, delta, a, b, c, out gap);
        else MeasureSegment(s, delta, a, b, c, out gap);
    }

    // ── A ball and a capsule ───────────────────────────────────────────────────────────────────────

    private static void MeasureSegment(in SweptShape s, Vector3 delta, Vector3 a, Vector3 b, Vector3 c, out ShapeGap gap)
    {
        SegmentToTriangle(s.P0, s.P1, a, b, c, out var onAxis, out var onTri, out bool pierces);
        float axisGap = pierces ? 0f : Vector3.Distance(onAxis, onTri);
        gap = new ShapeGap { Distance = axisGap - s.Radius, OnTriangle = onTri };
        gap.Embedded = gap.Distance < -Slop;
        if (!pierces && axisGap > 1e-7f) { gap.Normal = (onAxis - onTri) / axisGap; return; }
        gap.Normal = FaceSide(a, b, c, (s.P0 + s.P1) * 0.5f, delta);
    }

    /// <summary>The triangle's normal, the way of it that the point <paramref name="from"/> is on, else against the
    /// way the shape is going.</summary>
    private static Vector3 FaceSide(Vector3 a, Vector3 b, Vector3 c, Vector3 from, Vector3 delta)
    {
        var n = Vector3.Cross(b - a, c - a);
        float len = n.Length();
        n = len > 1e-20f ? n / len : Vector3.UnitY;
        float side = Vector3.Dot(n, from - a);
        if (MathF.Abs(side) > 1e-7f) return side > 0f ? n : -n;
        return Vector3.Dot(n, delta) > 0f ? -n : n;
    }

    /// <summary>The nearest points of segment pq and triangle abc, or <paramref name="pierces"/> where the segment passes
    /// through the triangle (then both points are where).</summary>
    private static void SegmentToTriangle(Vector3 p, Vector3 q, Vector3 a, Vector3 b, Vector3 c,
                                          out Vector3 onSegment, out Vector3 onTriangle, out bool pierces)
    {
        pierces = false;
        if (p == q)
        {
            onSegment = p; onTriangle = SolidContact.ClosestOnTriangle(p, a, b, c);
            return;
        }
        var tri = new GeometryTriangle { V0 = a, E1 = b - a, E2 = c - a };
        if (GeometryPiece.HitTriangle(tri, p, q - p, 0f, 1f, out float t, out _))
        {
            pierces = true;
            onSegment = onTriangle = p + (q - p) * t;
            return;
        }
        onSegment = p; onTriangle = SolidContact.ClosestOnTriangle(p, a, b, c);
        float best = Vector3.DistanceSquared(onSegment, onTriangle);
        var other = SolidContact.ClosestOnTriangle(q, a, b, c);
        float d = Vector3.DistanceSquared(q, other);
        if (d < best) { best = d; onSegment = q; onTriangle = other; }
        for (int e = 0; e < 3; e++)
        {
            var e0 = e == 0 ? a : e == 1 ? b : c;
            var e1 = e == 0 ? b : e == 1 ? c : a;
            SegmentToSegment(p, q, e0, e1, out var s1, out var s2);
            d = Vector3.DistanceSquared(s1, s2);
            if (d < best) { best = d; onSegment = s1; onTriangle = s2; }
        }
    }

    /// <summary>The nearest points of segments p1q1 and p2q2 (Ericson 5.1.9).</summary>
    internal static void SegmentToSegment(Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2, out Vector3 c1, out Vector3 c2)
    {
        var d1 = q1 - p1; var d2 = q2 - p2; var r = p1 - p2;
        float a = Vector3.Dot(d1, d1), e = Vector3.Dot(d2, d2), f = Vector3.Dot(d2, r);
        float s, t;
        const float tiny = 1e-12f;
        if (a <= tiny && e <= tiny) { s = 0f; t = 0f; }
        else if (a <= tiny) { s = 0f; t = Math.Clamp(f / e, 0f, 1f); }
        else
        {
            float cc = Vector3.Dot(d1, r);
            if (e <= tiny) { t = 0f; s = Math.Clamp(-cc / a, 0f, 1f); }
            else
            {
                float bb = Vector3.Dot(d1, d2), denom = a * e - bb * bb;
                s = denom > tiny ? Math.Clamp((bb * f - cc * e) / denom, 0f, 1f) : 0f;
                t = (bb * s + f) / e;
                if (t < 0f) { t = 0f; s = Math.Clamp(-cc / a, 0f, 1f); }
                else if (t > 1f) { t = 1f; s = Math.Clamp((bb - cc) / a, 0f, 1f); }
            }
        }
        c1 = p1 + d1 * s; c2 = p2 + d2 * t;
    }

    // ── A box ──────────────────────────────────────────────────────────────────────────────────────

    private static void MeasureBox(in SweptShape s, Vector3 delta, Vector3 a, Vector3 b, Vector3 c, out ShapeGap gap)
    {
        // The separating axes: the box's three, the triangle's normal, and the nine edge pairs. The biggest gap along
        // any of them is a floor under the true gap, and a negative one is the way out of an overlap by the least.
        Span<Vector3> edges = stackalloc Vector3[3];
        edges[0] = b - a; edges[1] = c - b; edges[2] = a - c;
        var faceNormal = Vector3.Cross(edges[0], -edges[2]);
        float best = float.MinValue;
        var bestAxis = Vector3.UnitY;
        Axis(s, a, b, c, s.AxisX, ref best, ref bestAxis); Axis(s, a, b, c, s.AxisY, ref best, ref bestAxis);
        Axis(s, a, b, c, s.AxisZ, ref best, ref bestAxis); Axis(s, a, b, c, faceNormal, ref best, ref bestAxis);
        for (int i = 0; i < 3; i++)
        {
            Axis(s, a, b, c, Vector3.Cross(s.AxisX, edges[i]), ref best, ref bestAxis);
            Axis(s, a, b, c, Vector3.Cross(s.AxisY, edges[i]), ref best, ref bestAxis);
            Axis(s, a, b, c, Vector3.Cross(s.AxisZ, edges[i]), ref best, ref bestAxis);
        }

        if (best < -Slop)
        {
            gap = new ShapeGap { Distance = best, OnTriangle = SolidContact.ClosestOnTriangle(s.Centre, a, b, c), Normal = bestAxis, Embedded = true };
            return;
        }

        // Apart (or touching): the nearest pair of features, a corner of one against a face of the other or an edge
        // against an edge.
        float bestSq = float.MaxValue;
        Vector3 onBox = s.Centre, onTriangle = a;
        void Pair(Vector3 pb, Vector3 pt)
        {
            float d = Vector3.DistanceSquared(pb, pt);
            if (d < bestSq) { bestSq = d; onBox = pb; onTriangle = pt; }
        }
        Span<Vector3> corners = stackalloc Vector3[8];
        for (int i = 0; i < 8; i++)
        {
            corners[i] = s.Centre + s.AxisX * ((i & 1) == 0 ? -s.Half.X : s.Half.X) + s.AxisY * ((i & 2) == 0 ? -s.Half.Y : s.Half.Y)
                         + s.AxisZ * ((i & 4) == 0 ? -s.Half.Z : s.Half.Z);
            Pair(corners[i], SolidContact.ClosestOnTriangle(corners[i], a, b, c));
        }
        Pair(ClosestOnBox(s, a), a); Pair(ClosestOnBox(s, b), b); Pair(ClosestOnBox(s, c), c);
        for (int i = 0; i < 8; i++)
            for (int bit = 0; bit < 3; bit++)
            {
                int j = i | (1 << bit);
                if (j == i) continue;
                for (int e = 0; e < 3; e++)
                {
                    var e0 = e == 0 ? a : e == 1 ? b : c;
                    var e1 = e == 0 ? b : e == 1 ? c : a;
                    SegmentToSegment(corners[i], corners[j], e0, e1, out var s1, out var s2);
                    Pair(s1, s2);
                }
            }
        float dist = MathF.Sqrt(bestSq);
        // Touching: the separating axis is the face they meet by.
        var normal = dist > 1e-6f ? (onBox - onTriangle) / dist : bestAxis;
        gap = new ShapeGap { Distance = dist, OnTriangle = onTriangle, Normal = normal };
    }

    /// <summary>One separating axis: the gap along it between the box and the triangle, kept if it is the biggest so far,
    /// with the axis turned to point from the triangle to the box.</summary>
    private static void Axis(in SweptShape s, Vector3 a, Vector3 b, Vector3 c, Vector3 axis, ref float best, ref Vector3 bestAxis)
    {
        float len = axis.Length();
        if (len < 1e-6f) return;
        axis /= len;
        float centre = Vector3.Dot(s.Centre, axis);
        float reach = MathF.Abs(Vector3.Dot(s.AxisX, axis)) * s.Half.X + MathF.Abs(Vector3.Dot(s.AxisY, axis)) * s.Half.Y
                      + MathF.Abs(Vector3.Dot(s.AxisZ, axis)) * s.Half.Z;
        float ta = Vector3.Dot(a, axis), tb = Vector3.Dot(b, axis), tc = Vector3.Dot(c, axis);
        float lo = MathF.Min(ta, MathF.Min(tb, tc)), hi = MathF.Max(ta, MathF.Max(tb, tc));
        float gap = MathF.Max(lo - (centre + reach), (centre - reach) - hi);
        if (gap <= best) return;
        best = gap;
        bestAxis = centre >= (lo + hi) * 0.5f ? axis : -axis;
    }

    /// <summary>The point of the box nearest p.</summary>
    private static Vector3 ClosestOnBox(in SweptShape s, Vector3 p)
    {
        var d = p - s.Centre;
        float x = Math.Clamp(Vector3.Dot(d, s.AxisX), -s.Half.X, s.Half.X);
        float y = Math.Clamp(Vector3.Dot(d, s.AxisY), -s.Half.Y, s.Half.Y);
        float z = Math.Clamp(Vector3.Dot(d, s.AxisZ), -s.Half.Z, s.Half.Z);
        return s.Centre + s.AxisX * x + s.AxisY * y + s.AxisZ * z;
    }
}

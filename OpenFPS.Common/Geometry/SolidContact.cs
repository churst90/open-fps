using System;
using System.Numerics;

namespace OpenFPS.Common.Geometry;

/// <summary>
/// A walking body against a solid made of triangles (docs/GEOMETRY.md 3.1): the body is the upright
/// cylinder it has always been, and the contact is worked out from the solid's triangles and face planes
/// in the world's frame, with the rules GeometryUtils.GetCylinderAABBOverlap has for a box. On a box the
/// answers are the box test's, to float rounding (the parity harness, AudioLab --geometry-parity).
///
/// <para>What the body meets is the solid's cross-section over the body's height, seen from above: the
/// solid cut by the two horizontal planes at the cylinder's ends and projected onto the ground. The body's
/// axis outside it is pushed out horizontally from its nearest point; inside it, out by its nearest edge
/// (found from the support of the cut triangles' corners along every edge direction they have, exact for
/// a convex solid); a solid above the middle of a body that can go down is a ceiling, and one below the
/// middle with the axis over it is a floor. Convex closed solids only (every solid in stage 1 is a box);
/// a solid that is not convex is met as if it were its convex hull.</para>
///
/// <para>Allocation-free: the triangles of one solid are taken onto the stack (36 corners for a box).</para>
/// </summary>
public static class SolidContact
{
    /// <summary>Within this far of the cross-section, horizontally, the axis counts as over it: the box
    /// test's 0.1 mm.</summary>
    private const float OverTolerance = 0.0001f;

    /// <summary>The largest solid taken on the stack, in triangles; bigger ones are taken on the heap.</summary>
    private const int StackTriangles = 64;

    /// <summary>
    /// How an upright cylinder centred at <paramref name="centre"/>, <paramref name="radius"/> wide and
    /// <paramref name="height"/> tall, overlaps a solid, and the way out: GetCylinderAABBOverlap's answer
    /// for the solid. The normal is in the world.
    /// </summary>
    public static GeometryUtils.CollisionResult CylinderOverlap(TriangleWorld world, SolidRef solid, Vector3 centre,
                                                                float radius, float height, bool canGoDown = false)
    {
        var result = new GeometryUtils.CollisionResult { IsColliding = false, Normal = Vector3.Zero, Penetration = 0f };
        // Most of what a body is asked about is nowhere near it: its bounds say so before its triangles are read.
        var (bmin, bmax) = world.BoundsOf(solid);
        float half = height * 0.5f;
        if (bmax.Y < centre.Y - half - 1e-3f || bmin.Y > centre.Y + half + 1e-3f) return result;
        float ox = MathF.Max(0f, MathF.Max(bmin.X - centre.X, centre.X - bmax.X)) - 1e-3f;
        float oz = MathF.Max(0f, MathF.Max(bmin.Z - centre.Z, centre.Z - bmax.Z)) - 1e-3f;
        if (ox > 0f && oz > 0f ? ox * ox + oz * oz >= radius * radius : MathF.Max(ox, oz) >= radius) return result;
        int tc = world.TriangleCountOf(solid);
        Span<Vector3> v = tc <= StackTriangles ? stackalloc Vector3[3 * StackTriangles] : new Vector3[3 * tc];
        world.TrianglesOf(solid, centre, v);
        v = v[..(3 * tc)];
        Span<Vector4> planes = tc <= StackTriangles ? stackalloc Vector4[StackTriangles] : new Vector4[tc];
        int pc = world.PlanesOf(solid, centre, planes);

        float y0 = -height * 0.5f, y1 = height * 0.5f;
        float minY = float.MaxValue, maxY = float.MinValue;
        float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
        foreach (var p in v)
        {
            minY = MathF.Min(minY, p.Y); maxY = MathF.Max(maxY, p.Y);
            minX = MathF.Min(minX, p.X); maxX = MathF.Max(maxX, p.X);
            minZ = MathF.Min(minZ, p.Z); maxZ = MathF.Max(maxZ, p.Z);
        }

        // 1. Over the body's height at all.
        if (maxY < y0 || minY > y1) return result;
        // A quick no: its bounds are a radius or more away.
        float bx = MathF.Max(0f, MathF.Max(minX, -maxX)), bz = MathF.Max(0f, MathF.Max(minZ, -maxZ));
        if (bx * bx + bz * bz >= radius * radius) return result;

        // 2. The cross-section: each triangle cut to the slab and seen from above. The nearest point of it
        //    to the axis, and every corner and edge direction, for the way out from inside.
        Span<Vector2> corners = tc <= StackTriangles ? stackalloc Vector2[5 * StackTriangles] : new Vector2[5 * tc];
        // Where each cut polygon's corners start in the list, and one past the last.
        Span<int> starts = tc <= StackTriangles ? stackalloc int[StackTriangles + 1] : new int[tc + 1];
        int cornerCount = 0, polys = 0;
        float bestSq = float.MaxValue;
        Vector2 closest = default, edge = default;
        bool overArea = false;
        Span<Vector3> poly = stackalloc Vector3[8];
        Span<Vector3> tmp = stackalloc Vector3[8];
        for (int t = 0; t < tc; t++)
        {
            int n = ClipToSlab(v[3 * t], v[3 * t + 1], v[3 * t + 2], y0, y1, poly, tmp);
            if (n == 0) continue;
            starts[polys++] = cornerCount;
            for (int k = 0; k < n; k++) corners[cornerCount++] = new Vector2(poly[k].X, poly[k].Z);
            float dSq = DistanceSq(poly[..n], out var c, out bool contains, out var along);
            if (contains) { overArea = true; bestSq = 0f; closest = Vector2.Zero; edge = Vector2.Zero; }
            else if (dSq < bestSq) { bestSq = dSq; closest = c; edge = along; }
        }
        starts[polys] = cornerCount;
        ReadOnlySpan<Vector2> cs = corners[..cornerCount];
        if (cornerCount == 0) return result;

        bool axisInside = overArea || (pc > 0 && AxisInside(planes[..pc], y0, y1));
        float dist = axisInside ? 0f : MathF.Sqrt(bestSq);
        if (!axisInside && bestSq >= radius * radius) return result;

        result.IsColliding = true;
        if (dist > OverTolerance)
        {
            var away = -closest / dist;
            if (edge != Vector2.Zero)
            {
                // Beside a face: square to it, toward the axis.
                var square = Vector2.Normalize(new Vector2(edge.Y, -edge.X));
                away = Vector2.Dot(square, away) >= 0f ? square : -square;
            }
            result.Normal = new Vector3(away.X, 0f, away.Y);
            result.Penetration = radius - dist;
        }
        else
        {
            // Over it: out by the nearest edge, the whole radius past it. The support of the cut corners
            // along each edge direction is how far the section reaches that way; the least of them is the
            // nearest edge of a convex section.
            // Every edge of every cut polygon is tried, both ways round: a direction that is not an edge
            // of the section (a diagonal) reaches at least as far as the boundary, so it never wins wrongly.
            float best = float.MaxValue;
            Vector2 way = Vector2.UnitX;
            for (int p = 0; p < polys; p++)
            {
                int s0 = starts[p], s1 = starts[p + 1];
                for (int i = s0; i < s1; i++)
                {
                    int j = i + 1 < s1 ? i + 1 : s0;
                    var e = cs[j] - cs[i];
                    float len = e.Length();
                    if (len < 1e-7f) continue;
                    var nrm = new Vector2(e.Y, -e.X) / len;
                    for (int sgn = 0; sgn < 2; sgn++, nrm = -nrm)
                    {
                        float reach = float.MinValue;
                        foreach (var c in cs) reach = MathF.Max(reach, Vector2.Dot(c, nrm));
                        if (reach < best) { best = reach; way = nrm; }
                    }
                }
            }
            result.Normal = new Vector3(way.X, 0f, way.Y);
            result.Penetration = best + radius;
        }

        // A solid above the middle of the body that the top of it reaches into is a ceiling.
        float intoCeiling = y1 - minY;
        if (canGoDown && minY > 0f && intoCeiling < result.Penetration)
        {
            result.Normal = -Vector3.UnitY;
            result.Penetration = intoCeiling;
            return result;
        }
        // And one below the middle with the axis over it is a floor.
        float intoFloor = maxY - y0;
        if (dist <= OverTolerance && maxY < 0f && intoFloor < result.Penetration)
        {
            result.Normal = Vector3.UnitY;
            result.Penetration = intoFloor;
        }
        return result;
    }

    /// <summary>Whether the cylinder and the solid share any volume (GeometryUtils.AABBIntersectsCylinder).</summary>
    public static bool CylinderIntersects(TriangleWorld world, SolidRef solid, Vector3 centre, float radius, float height)
    {
        var r = CylinderOverlap(world, solid, centre, radius, height);
        return r.IsColliding;
    }

    /// <summary>Does the axis segment x = z = 0, y in [y0, y1], pass through the convex solid?</summary>
    private static bool AxisInside(ReadOnlySpan<Vector4> planes, float y0, float y1)
    {
        float tIn = 0f, tOut = 1f, span = y1 - y0;
        foreach (var q in planes)
        {
            // n·p(t) - d <= 0 with p(t) = (0, y0 + t·span, 0).
            float den = q.Y * span, num = q.W - q.Y * y0;
            if (MathF.Abs(den) < 1e-12f)
            {
                if (num < 0f) return false;
                continue;
            }
            float t = num / den;
            if (den > 0f) { if (t < tOut) tOut = t; }
            else if (t > tIn) tIn = t;
            if (tIn > tOut) return false;
        }
        return true;
    }

    /// <summary>A triangle cut to y0 &lt;= y &lt;= y1 (Sutherland–Hodgman against the two planes): its
    /// corners into <paramref name="into"/>, how many (0 when none of it is between them).</summary>
    private static int ClipToSlab(Vector3 a, Vector3 b, Vector3 c, float y0, float y1, Span<Vector3> into, Span<Vector3> tmp)
    {
        tmp[0] = a; tmp[1] = b; tmp[2] = c;
        int n = ClipPlane(tmp, 3, into, y0, above: true);
        if (n == 0) return 0;
        n = ClipPlane(into, n, tmp, y1, above: false);
        for (int i = 0; i < n; i++) into[i] = tmp[i];
        return n;
    }

    private static int ClipPlane(ReadOnlySpan<Vector3> src, int count, Span<Vector3> dst, float y, bool above)
    {
        int n = 0;
        for (int i = 0; i < count; i++)
        {
            var p = src[i]; var q = src[(i + 1) % count];
            bool pin = above ? p.Y >= y : p.Y <= y;
            bool qin = above ? q.Y >= y : q.Y <= y;
            if (pin) dst[n++] = p;
            if (pin != qin)
            {
                float t = (y - p.Y) / (q.Y - p.Y);
                dst[n++] = new Vector3(p.X + (q.X - p.X) * t, y, p.Z + (q.Z - p.Z) * t);
            }
        }
        return n;
    }

    /// <summary>The squared distance from the origin to a polygon seen from above, its nearest point, and
    /// whether the polygon (having area) covers the origin.</summary>
    private static float DistanceSq(ReadOnlySpan<Vector3> poly, out Vector2 nearest, out bool contains)
        => DistanceSq(poly, out nearest, out contains, out _);

    /// <summary>The same, and when the nearest point lies along an edge rather than at a corner, that
    /// edge's direction (zero at a corner): the way out from beside a face is square to it, which the
    /// edge says to the precision of its length rather than of the distance to it.</summary>
    private static float DistanceSq(ReadOnlySpan<Vector3> poly, out Vector2 nearest, out bool contains, out Vector2 alongEdge)
    {
        contains = false;
        alongEdge = Vector2.Zero;
        nearest = new Vector2(poly[0].X, poly[0].Z);
        float best = nearest.LengthSquared();
        int n = poly.Length;
        float area = 0f;
        int sign = 0;
        bool mixed = false;
        for (int i = 0; i < n; i++)
        {
            var a = new Vector2(poly[i].X, poly[i].Z);
            var b = new Vector2(poly[(i + 1) % n].X, poly[(i + 1) % n].Z);
            var e = b - a;
            float lenSq = e.LengthSquared();
            Vector2 c;
            bool onEdge = false;
            if (lenSq < 1e-14f) c = a;
            else
            {
                float t = -Vector2.Dot(a, e) / lenSq;
                onEdge = t > 0f && t < 1f;
                c = a + e * Math.Clamp(t, 0f, 1f);
            }
            float d = c.LengthSquared();
            if (d < best) { best = d; nearest = c; alongEdge = onEdge ? e : Vector2.Zero; }
            // Which side of each edge the origin is on: all one side, and the polygon has area, is inside.
            float cross = e.X * (-a.Y) - e.Y * (-a.X);
            area += a.X * b.Y - a.Y * b.X;
            int s = cross > 0f ? 1 : cross < 0f ? -1 : 0;
            if (s != 0) { if (sign == 0) sign = s; else if (s != sign) mixed = true; }
        }
        if (!mixed && sign != 0 && MathF.Abs(area) > 1e-10f) { contains = true; nearest = Vector2.Zero; return 0f; }
        return best;
    }
}

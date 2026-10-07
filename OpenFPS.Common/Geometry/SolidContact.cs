using System;
using System.Numerics;

namespace OpenFPS.Common.Geometry;

/// <summary>What a walking body is, against what it walks into.</summary>
public enum BodyShape : byte
{
    /// <summary>An upright cylinder from a hand's breadth above the feet to the head: the body of stage 1
    /// and of the box path.</summary>
    Cylinder = 0,
    /// <summary>A capsule over the same span (docs/GEOMETRY.md 3.1): rounded at the bottom and the top, so
    /// an edge below the knee or above the brow is met by the curve, and every contact has a normal that
    /// says whether it is a floor, a wall or a ceiling.</summary>
    Capsule = 1,
}

/// <summary>
/// A walking body against a solid made of triangles (docs/GEOMETRY.md 3.1).
///
/// <para><b>The cylinder</b> (<see cref="CylinderOverlap"/>) is the body stage 1 kept: the contact worked out
/// from the solid's triangles and face planes with the rules GeometryUtils.GetCylinderAABBOverlap has for a
/// box, and on a box the box test's answers to float rounding. What the body meets is the solid's
/// cross-section over the body's height seen from above; the axis outside it is pushed out horizontally
/// from its nearest point, inside it out by its nearest edge; a solid above the middle of a body that can
/// go down is a ceiling, one below the middle with the axis over it a floor.</para>
///
/// <para><b>The capsule</b> (<see cref="CapsuleOverlap"/>) is stage 2's: the nearest point of the solid to
/// the body's axis segment, and the contact classified by the direction from it. A floor (within the
/// walkable slope of up) is left to the ground probe while the body is on the ground and lands it while
/// it is not; a ceiling pushes an airborne body down; everything else is a wall and pushes out
/// horizontally only, so a steep bank slides a body down it rather than lifting it. Against a box's
/// vertical face it is the cylinder to rounding; it differs only where an edge meets the rounded ends.</para>
///
/// <para>A solid that is not convex (stairs, an arch) is met piece by piece: its convex pieces, the
/// deepest contact of them. Allocation-free up to <see cref="StackTriangles"/> triangles a piece.</para>
/// </summary>
public static class SolidContact
{
    /// <summary>Within this far of the cross-section, horizontally, the axis counts as over it: the box
    /// test's 0.1 mm.</summary>
    private const float OverTolerance = 0.0001f;

    /// <summary>The largest piece taken on the stack, in triangles; bigger ones are taken on the heap.</summary>
    private const int StackTriangles = 64;

    /// <summary>The steepest a floor may be, as the cosine of its tilt from level: 45 degrees.</summary>
    public const float WalkableCos = 0.70710678f;

    // ═══ The cylinder ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// How an upright cylinder centred at <paramref name="centre"/>, <paramref name="radius"/> wide and
    /// <paramref name="height"/> tall, overlaps a solid, and the way out: GetCylinderAABBOverlap's answer
    /// for the solid. The normal is in the world.
    /// </summary>
    public static GeometryUtils.CollisionResult CylinderOverlap(TriangleWorld world, SolidRef solid, Vector3 centre,
                                                                float radius, float height, bool canGoDown = false)
    {
        var none = new GeometryUtils.CollisionResult { IsColliding = false, Normal = Vector3.Zero, Penetration = 0f };
        // Most of what a body is asked about is nowhere near it: its bounds say so before its triangles are read.
        var (bmin, bmax) = world.BoundsOf(solid);
        float half = height * 0.5f;
        if (bmax.Y < centre.Y - half - 1e-3f || bmin.Y > centre.Y + half + 1e-3f) return none;
        float ox = MathF.Max(0f, MathF.Max(bmin.X - centre.X, centre.X - bmax.X)) - 1e-3f;
        float oz = MathF.Max(0f, MathF.Max(bmin.Z - centre.Z, centre.Z - bmax.Z)) - 1e-3f;
        if (ox > 0f && oz > 0f ? ox * ox + oz * oz >= radius * radius : MathF.Max(ox, oz) >= radius) return none;

        int parts = world.PartCountOf(solid);
        if (parts == 0)
        {
            int tc = world.TriangleCountOf(solid);
            Span<Vector3> v = tc <= StackTriangles ? stackalloc Vector3[3 * StackTriangles] : new Vector3[3 * tc];
            world.TrianglesOf(solid, centre, v);
            Span<Vector4> planes = tc <= StackTriangles ? stackalloc Vector4[StackTriangles] : new Vector4[tc];
            int pc = world.PlanesOf(solid, centre, planes);
            return CylinderCore(v[..(3 * tc)], planes[..pc], radius, -half, half, 0f, canGoDown);
        }
        var best = none;
        for (int k = 0; k < parts; k++)
        {
            int tc = world.PartTriangleCountOf(solid, k);
            Span<Vector3> v = tc <= StackTriangles ? stackalloc Vector3[3 * StackTriangles] : new Vector3[3 * tc];
            world.PartTrianglesOf(solid, k, centre, v);
            Span<Vector4> planes = tc <= StackTriangles ? stackalloc Vector4[StackTriangles] : new Vector4[tc];
            int pc = world.PartPlanesOf(solid, k, centre, planes);
            var r = CylinderCore(v[..(3 * tc)], planes[..pc], radius, -half, half, 0f, canGoDown);
            if (r.IsColliding && (!best.IsColliding || r.Penetration > best.Penetration)) best = r;
        }
        return best;
    }

    /// <summary>Whether the cylinder and the solid share any volume (GeometryUtils.AABBIntersectsCylinder).</summary>
    public static bool CylinderIntersects(TriangleWorld world, SolidRef solid, Vector3 centre, float radius, float height)
        => CylinderOverlap(world, solid, centre, radius, height).IsColliding;

    /// <summary>
    /// The cylinder against one convex piece whose corners <paramref name="v"/> (three a triangle) and face
    /// planes are relative to a point on the body's axis: the body spans <paramref name="y0"/> to
    /// <paramref name="y1"/> along it, its middle at <paramref name="mid"/>.
    /// </summary>
    private static GeometryUtils.CollisionResult CylinderCore(ReadOnlySpan<Vector3> v, ReadOnlySpan<Vector4> planes, float radius,
                                                             float y0, float y1, float mid, bool canGoDown)
    {
        var result = new GeometryUtils.CollisionResult { IsColliding = false, Normal = Vector3.Zero, Penetration = 0f };
        int tc = v.Length / 3;
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

        bool axisInside = overArea || (planes.Length > 0 && AxisInside(planes, y0, y1));
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
        if (canGoDown && minY > mid && intoCeiling < result.Penetration)
        {
            result.Normal = -Vector3.UnitY;
            result.Penetration = intoCeiling;
            return result;
        }
        // And one below the middle with the axis over it is a floor.
        float intoFloor = maxY - y0;
        if (dist <= OverTolerance && maxY < mid && intoFloor < result.Penetration)
        {
            result.Normal = Vector3.UnitY;
            result.Penetration = intoFloor;
        }
        return result;
    }

    // ═══ The capsule ═════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A capsule body standing with its feet at <paramref name="feet"/>: from <paramref name="bottom"/>
    /// above them to <paramref name="top"/> above them, <paramref name="radius"/> round, its axis the
    /// segment between the centres of its two ends.
    /// </summary>
    public readonly record struct Capsule(float Radius, float Bottom, float Top)
    {
        public float AxisLow => Bottom + Radius;
        public float AxisHigh => MathF.Max(Bottom + Radius, Top - Radius);
    }

    /// <summary>What the capsule meets in one convex piece.</summary>
    private readonly record struct CapsuleTouch(bool Touching, bool Inside, Vector3 Point, Vector3 OnAxis, float Distance);

    /// <summary>
    /// How a capsule body with its feet at <paramref name="feet"/> overlaps a solid, and the way out (see the
    /// class remarks): a wall horizontally by how far it must move to clear it, a ceiling down while
    /// <paramref name="airborne"/>, a floor (only while airborne) up by how far its feet must rise to stand
    /// on the point it came down on. A floor met on the ground is no contact: the ground probe stands on it.
    /// A body whose axis is inside the solid is let out as the cylinder would be.
    /// </summary>
    public static GeometryUtils.CollisionResult CapsuleOverlap(TriangleWorld world, SolidRef solid, Vector3 feet, Capsule body, bool airborne)
        => CapsuleOverlap(world, solid, feet, body, airborne, out _);

    /// <summary>The same, and how deep the contact is (<see cref="CapsuleDepth"/>): what ranks one contact
    /// against another. The penetration is how far to move, which for a floor is a lift, not a depth.</summary>
    public static GeometryUtils.CollisionResult CapsuleOverlap(TriangleWorld world, SolidRef solid, Vector3 feet, Capsule body, bool airborne,
                                                               out float depth)
    {
        depth = 0f;
        var none = new GeometryUtils.CollisionResult { IsColliding = false, Normal = Vector3.Zero, Penetration = 0f };
        if (!NearBounds(world.BoundsOf(solid), feet, body)) return none;
        int parts = world.PartCountOf(solid);
        var best = none;
        for (int k = 0; k < Math.Max(1, parts); k++)
        {
            var r = OnePiece(world, solid, parts == 0 ? -1 : k, feet, body, airborne, out float d);
            if (r.IsColliding && (!best.IsColliding || d > depth)) { best = r; depth = d; }
        }
        return best;
    }

    /// <summary>Whether the capsule shares any volume with the solid.</summary>
    public static bool CapsuleIntersects(TriangleWorld world, SolidRef solid, Vector3 feet, Capsule body)
        => CapsuleDepth(world, solid, feet, body) > 0f;

    /// <summary>How deep the capsule is in the solid: its radius less the distance from its axis, or more
    /// than its radius when its axis is inside. 0 when clear.</summary>
    public static float CapsuleDepth(TriangleWorld world, SolidRef solid, Vector3 feet, Capsule body)
    {
        if (!NearBounds(world.BoundsOf(solid), feet, body)) return 0f;
        int parts = world.PartCountOf(solid);
        float deepest = 0f;
        for (int k = 0; k < Math.Max(1, parts); k++)
        {
            OnePiece(world, solid, parts == 0 ? -1 : k, feet, body, airborne: true, out float depth);
            deepest = MathF.Max(deepest, depth);
        }
        return deepest;
    }

    /// <summary>The capsule against a box (a moving thing's collider): the same rules as a solid.</summary>
    public static GeometryUtils.CollisionResult CapsuleOverlapBox(Vector3 boxCentre, Vector3 boxSize, Quaternion boxRotation,
                                                                  Vector3 feet, Capsule body, bool airborne, out float depth)
    {
        Span<Vector3> v = stackalloc Vector3[36];
        Span<Vector4> planes = stackalloc Vector4[6];
        BoxPiece(boxCentre - feet, boxSize, boxRotation, v, planes);
        return CapsuleCore(v, planes, body, airborne, out depth);
    }

    private static bool NearBounds((Vector3 Min, Vector3 Max) b, Vector3 feet, Capsule body)
    {
        float lo = feet.Y + body.Bottom, hi = feet.Y + body.Top;
        if (b.Max.Y < lo - 1e-3f || b.Min.Y > hi + 1e-3f) return false;
        float ox = MathF.Max(0f, MathF.Max(b.Min.X - feet.X, feet.X - b.Max.X)) - 1e-3f;
        float oz = MathF.Max(0f, MathF.Max(b.Min.Z - feet.Z, feet.Z - b.Max.Z)) - 1e-3f;
        return ox > 0f && oz > 0f ? ox * ox + oz * oz < body.Radius * body.Radius : MathF.Max(ox, oz) < body.Radius;
    }

    private static GeometryUtils.CollisionResult OnePiece(TriangleWorld world, SolidRef solid, int part, Vector3 feet, Capsule body,
                                                          bool airborne, out float depth)
    {
        int tc = part < 0 ? world.TriangleCountOf(solid) : world.PartTriangleCountOf(solid, part);
        Span<Vector3> v = tc <= StackTriangles ? stackalloc Vector3[3 * StackTriangles] : new Vector3[3 * tc];
        Span<Vector4> planes = tc <= StackTriangles ? stackalloc Vector4[StackTriangles] : new Vector4[tc];
        int pc;
        if (part < 0) { world.TrianglesOf(solid, feet, v); pc = world.PlanesOf(solid, feet, planes); }
        else { world.PartTrianglesOf(solid, part, feet, v); pc = world.PartPlanesOf(solid, part, feet, planes); }
        return CapsuleCore(v[..(3 * tc)], planes[..pc], body, airborne, out depth);
    }

    /// <summary>A box's twelve triangles and six planes relative to a body's feet.</summary>
    private static void BoxPiece(Vector3 centre, Vector3 size, Quaternion rotation, Span<Vector3> v, Span<Vector4> planes)
    {
        var turn = ShapeLibrary.RotationMatrix(rotation);
        Span<Vector3> c = stackalloc Vector3[8];
        for (int i = 0; i < 8; i++) c[i] = ShapeLibrary.BoxCorner(i, centre, size, turn, Vector3.Zero);
        var idx = ShapeLibrary.BoxTriangles;
        for (int k = 0; k < idx.Length; k++) v[k] = c[idx[k]];
        for (int f = 0; f < 6; f++)
        {
            Vector3 a = v[6 * f], b = v[6 * f + 1], d = v[6 * f + 2];
            var n = Vector3.Normalize(Vector3.Cross(b - a, d - a));
            planes[f] = new Vector4(n, Vector3.Dot(n, a));
        }
    }

    /// <summary>
    /// The capsule against one convex piece whose corners and planes are relative to the feet. The nearest
    /// point of the piece to the axis segment decides: see <see cref="CapsuleOverlap"/>.
    /// </summary>
    private static GeometryUtils.CollisionResult CapsuleCore(ReadOnlySpan<Vector3> v, ReadOnlySpan<Vector4> planes, Capsule body,
                                                            bool airborne, out float depth)
    {
        var result = new GeometryUtils.CollisionResult { IsColliding = false, Normal = Vector3.Zero, Penetration = 0f };
        depth = 0f;
        float r = body.Radius, a = body.AxisLow, b = body.AxisHigh;
        var touch = Nearest(v, planes, a, b, r);
        if (!touch.Touching) return result;

        if (touch.Inside || touch.Distance < 1e-6f)
        {
            // The axis is in it: let out as the cylinder over the same span would be. Its floor lifts the
            // bottom of the body onto the top; the feet go that much further, to stand on it.
            var c = CylinderCore(v, planes, r, body.Bottom, body.Top, 0.5f * (body.Bottom + body.Top), airborne);
            if (!c.IsColliding) return result;
            if (c.Normal.Y > 0.5f) c.Penetration += body.Bottom;
            depth = r + c.Penetration;
            return c;
        }

        float d = touch.Distance;
        var n = (touch.OnAxis - touch.Point) / d;
        depth = r - d;
        float vy = touch.OnAxis.Y - touch.Point.Y;
        float hx = touch.OnAxis.X - touch.Point.X, hz = touch.OnAxis.Z - touch.Point.Z;
        float hh = MathF.Sqrt(hx * hx + hz * hz);

        if (n.Y >= WalkableCos)
        {
            // A floor. On the ground the probe stands on it; coming down, the feet land on the point met.
            if (!airborne || touch.Point.Y <= 0f) return result;
            result.IsColliding = true;
            result.Normal = Vector3.UnitY;
            result.Penetration = touch.Point.Y;
            return result;
        }
        if (n.Y <= -WalkableCos && airborne)
        {
            // A ceiling over a body in the air: down until its top clears it.
            result.IsColliding = true;
            result.Normal = -Vector3.UnitY;
            result.Penetration = MathF.Sqrt(MathF.Max(0f, r * r - hh * hh)) + vy;
            return result;
        }
        // A wall (or a ceiling over a body on the ground): out across the ground until the distance from the
        // axis is the radius, at the height it was met.
        if (hh < 1e-6f)
        {
            // Straight over or under the axis with no way across given: as the cylinder.
            var c = CylinderCore(v, planes, r, body.Bottom, body.Top, 0.5f * (body.Bottom + body.Top), canGoDown: false);
            if (c.IsColliding && c.Normal.Y == 0f) return c;
            return result;
        }
        result.IsColliding = true;
        result.Normal = new Vector3(hx / hh, 0f, hz / hh);
        result.Penetration = MathF.Sqrt(MathF.Max(0f, r * r - vy * vy)) - hh;
        if (result.Penetration <= 0f) result.IsColliding = false;
        return result;
    }

    /// <summary>
    /// The nearest point of a convex piece to the vertical segment x = z = 0, y in [a, b], within
    /// <paramref name="r"/> of it, and whether the segment passes through the piece.
    /// </summary>
    private static CapsuleTouch Nearest(ReadOnlySpan<Vector3> v, ReadOnlySpan<Vector4> planes, float a, float b, float r)
    {
        // Bounds first: most pieces near a body are not within its radius.
        var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
        foreach (var p in v) { lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p); }
        float gx = MathF.Max(0f, MathF.Max(lo.X, -hi.X)), gz = MathF.Max(0f, MathF.Max(lo.Z, -hi.Z));
        float gy = MathF.Max(0f, MathF.Max(lo.Y - b, a - hi.Y));
        if (gx * gx + gz * gz + gy * gy >= r * r) return default;
        if (planes.Length > 0 && AxisInside(planes, a, b)) return new CapsuleTouch(true, true, default, default, 0f);

        float best = float.MaxValue;
        Vector3 bp = default, bq = default;
        var s0 = new Vector3(0f, a, 0f); var s1 = new Vector3(0f, b, 0f);
        for (int t = 0; t + 2 < v.Length; t += 3)
        {
            Vector3 p0 = v[t], p1 = v[t + 1], p2 = v[t + 2];
            // The segment's ends against the triangle, and the segment against each edge.
            Consider(ClosestOnTriangle(s0, p0, p1, p2), s0, ref best, ref bp, ref bq);
            Consider(ClosestOnTriangle(s1, p0, p1, p2), s1, ref best, ref bp, ref bq);
            SegmentEdge(a, b, p0, p1, ref best, ref bp, ref bq);
            SegmentEdge(a, b, p1, p2, ref best, ref bp, ref bq);
            SegmentEdge(a, b, p2, p0, ref best, ref bp, ref bq);
            // A face the segment passes straight through would put the axis inside; the planes said not.
        }
        if (best >= r * r) return default;
        return new CapsuleTouch(true, false, bp, bq, MathF.Sqrt(best));
    }

    private static void Consider(Vector3 onPiece, Vector3 onAxis, ref float best, ref Vector3 bp, ref Vector3 bq)
    {
        float d = Vector3.DistanceSquared(onPiece, onAxis);
        if (d < best) { best = d; bp = onPiece; bq = onAxis; }
    }

    /// <summary>The nearest points between the axis segment (0, a..b, 0) and the edge p-q.</summary>
    private static void SegmentEdge(float a, float b, Vector3 p, Vector3 q, ref float best, ref Vector3 bp, ref Vector3 bq)
    {
        // Ericson, Real-Time Collision Detection 5.1.9, with the first segment vertical.
        var d1 = new Vector3(0f, b - a, 0f);
        var d2 = q - p;
        var rr = new Vector3(0f, a, 0f) - p;
        float aa = d1.Y * d1.Y, e = Vector3.Dot(d2, d2), f = Vector3.Dot(d2, rr);
        float s, t;
        if (aa <= 1e-12f && e <= 1e-12f) { s = t = 0f; }
        else if (aa <= 1e-12f) { s = 0f; t = Math.Clamp(f / e, 0f, 1f); }
        else
        {
            float c = d1.Y * rr.Y;
            if (e <= 1e-12f) { t = 0f; s = Math.Clamp(-c / aa, 0f, 1f); }
            else
            {
                float bb = d1.Y * d2.Y;
                float denom = aa * e - bb * bb;
                s = denom != 0f ? Math.Clamp((bb * f - c * e) / denom, 0f, 1f) : 0f;
                t = (bb * s + f) / e;
                if (t < 0f) { t = 0f; s = Math.Clamp(-c / aa, 0f, 1f); }
                else if (t > 1f) { t = 1f; s = Math.Clamp((bb - c) / aa, 0f, 1f); }
            }
        }
        var onAxis = new Vector3(0f, a + s * (b - a), 0f);
        var onEdge = p + d2 * t;
        Consider(onEdge, onAxis, ref best, ref bp, ref bq);
    }

    /// <summary>The point of triangle abc nearest to p (Ericson 5.1.5).</summary>
    private static Vector3 ClosestOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        var ab = b - a; var ac = c - a; var ap = p - a;
        float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0f && d2 <= 0f) return a;
        var bp = p - b;
        float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0f && d4 <= d3) return b;
        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0f && d1 >= 0f && d3 <= 0f) return a + ab * (d1 / (d1 - d3));
        var cp = p - c;
        float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0f && d5 <= d6) return c;
        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0f && d2 >= 0f && d6 <= 0f) return a + ac * (d2 / (d2 - d6));
        float va = d3 * d6 - d5 * d4;
        if (va <= 0f && (d4 - d3) >= 0f && (d5 - d6) >= 0f) return b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)));
        float denom = 1f / (va + vb + vc);
        return a + ab * (vb * denom) + ac * (vc * denom);
    }

    // ═══ Shared ══════════════════════════════════════════════════════════════════════════════════

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

    /// <summary>The squared distance from the origin to a polygon seen from above, its nearest point, whether
    /// the polygon (having area) covers the origin, and when the nearest point lies along an edge rather than
    /// at a corner, that edge's direction (zero at a corner): the way out from beside a face is square to it,
    /// which the edge says to the precision of its length rather than of the distance to it.</summary>
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

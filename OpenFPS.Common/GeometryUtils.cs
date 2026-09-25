using System;
using System.Numerics;

namespace OpenFPS.Common;

public static class MathHelper
{
    public static float Lerp(float a, float b, float t) => a + (b - a) * Math.Clamp(t, 0, 1);
    public static float LerpAngle(float a, float b, float t)
    {
        float delta = ((b - a + MathF.PI) % (MathF.PI * 2)) - MathF.PI;
        if (delta < -MathF.PI) delta += MathF.PI * 2;
        return a + delta * Math.Clamp(t, 0, 1);
    }

    /// <summary>Folds an angle into (-PI, PI], so two yaws can be compared by magnitude.</summary>
    public static float WrapAngle(float radians)
    {
        float wrapped = ((radians + MathF.PI) % (MathF.PI * 2)) - MathF.PI;
        if (wrapped <= -MathF.PI) wrapped += MathF.PI * 2;
        return wrapped;
    }

    /// <summary>
    /// Inverse of <see cref="Quaternion.CreateFromYawPitchRoll"/> for the roll-free rotations the
    /// simulation uses: recovers the yaw/pitch pair that produced this orientation, so a client can
    /// reconcile its look angles against a quantized server transform.
    /// </summary>
    public static void ToYawPitch(Quaternion rotation, out float yaw, out float pitch)
    {
        Vector3 forward = Vector3.Transform(new Vector3(0, 0, 1), Quaternion.Normalize(rotation));
        yaw = MathF.Atan2(forward.X, forward.Z);
        pitch = -MathF.Asin(Math.Clamp(forward.Y, -1f, 1f));
    }
}

public static class GeometryUtils
{
    public static BoxContainment GetBoxContainmentInOBB(Vector3 boxCenter, Vector3 boxSize, Vector3 obbCenter, Vector3 obbSize, Quaternion obbRot)
    {
        // Sample all 8 corners of the Box against the OBB
        Vector3 halfSize = boxSize / 2f;
        int pointsInside = 0;
        
        Span<Vector3> corners = stackalloc Vector3[8];
        corners[0] = boxCenter + new Vector3(-halfSize.X, -halfSize.Y, -halfSize.Z);
        corners[1] = boxCenter + new Vector3( halfSize.X, -halfSize.Y, -halfSize.Z);
        corners[2] = boxCenter + new Vector3(-halfSize.X,  halfSize.Y, -halfSize.Z);
        corners[3] = boxCenter + new Vector3( halfSize.X,  halfSize.Y, -halfSize.Z);
        corners[4] = boxCenter + new Vector3(-halfSize.X, -halfSize.Y,  halfSize.Z);
        corners[5] = boxCenter + new Vector3( halfSize.X, -halfSize.Y,  halfSize.Z);
        corners[6] = boxCenter + new Vector3(-halfSize.X,  halfSize.Y,  halfSize.Z);
        corners[7] = boxCenter + new Vector3( halfSize.X,  halfSize.Y,  halfSize.Z);

        foreach (var p in corners)
        {
            if (IsPointInOBB(p, obbCenter, obbSize, obbRot)) pointsInside++;
        }

        if (pointsInside == 8) return BoxContainment.FullyInside;
        if (pointsInside > 0) return BoxContainment.Partial;

        // CORNER SAMPLING IS NOT AN INTERSECTION TEST, and believing it was lost whole regions.
        //
        // A long thin box can pass clean THROUGH a big cube without containing any of its eight
        // corners and without its own centre being inside it. The octree asks this question of nodes
        // hundreds of metres across, so a 24 m wide region volume laid along a racetrack answered
        // "Outside" for node after node, the recursion stopped there, and the region simply was not
        // in the grid over those stretches — while being perfectly present either side of them. The
        // symptom was a player walking a named straight and being told the name, then nothing, then
        // the name again.
        //
        // The honest test is separating axes: two convex boxes miss each other if and only if some
        // axis exists on which their projections do not overlap, and for a box pair the candidates
        // are the three axes of each plus the nine cross products.
        return ObbIntersectsAabb(boxCenter, boxSize, obbCenter, obbSize, obbRot)
             ? BoxContainment.Partial
             : BoxContainment.Outside;
    }

    /// <summary>
    /// Do an axis-aligned box and an oriented box share any volume at all? Separating-axis theorem,
    /// fifteen axes, no allocation.
    /// </summary>
    public static bool ObbIntersectsAabb(Vector3 aabbCenter, Vector3 aabbSize,
                                         Vector3 obbCenter, Vector3 obbSize, Quaternion obbRot)
    {
        Vector3 a = aabbSize * 0.5f;
        Vector3 b = obbSize * 0.5f;
        var m = Matrix4x4.CreateFromQuaternion(obbRot);
        Vector3 bx = new(m.M11, m.M12, m.M13);
        Vector3 by = new(m.M21, m.M22, m.M23);
        Vector3 bz = new(m.M31, m.M32, m.M33);
        Vector3 t = obbCenter - aabbCenter;

        // r[i,j] is the i-th world axis against the j-th box axis, and the epsilon keeps the cross
        // products from exploding when two axes are parallel — which for a yaw-only rotation they
        // always are, so it is the normal case here and not the corner case.
        Span<float> r = stackalloc float[9];
        Span<float> ar = stackalloc float[9];
        r[0] = bx.X; r[1] = by.X; r[2] = bz.X;
        r[3] = bx.Y; r[4] = by.Y; r[5] = bz.Y;
        r[6] = bx.Z; r[7] = by.Z; r[8] = bz.Z;
        for (int i = 0; i < 9; i++) ar[i] = MathF.Abs(r[i]) + 1e-6f;

        Span<float> ae = stackalloc float[3] { a.X, a.Y, a.Z };
        Span<float> be = stackalloc float[3] { b.X, b.Y, b.Z };
        Span<float> te = stackalloc float[3] { t.X, t.Y, t.Z };

        // The three axes of the axis-aligned box.
        for (int i = 0; i < 3; i++)
        {
            float ra = ae[i];
            float rb = be[0] * ar[i * 3] + be[1] * ar[i * 3 + 1] + be[2] * ar[i * 3 + 2];
            if (MathF.Abs(te[i]) > ra + rb) return false;
        }

        // The three axes of the oriented box.
        for (int j = 0; j < 3; j++)
        {
            float ra = ae[0] * ar[j] + ae[1] * ar[3 + j] + ae[2] * ar[6 + j];
            float rb = be[j];
            float tj = te[0] * r[j] + te[1] * r[3 + j] + te[2] * r[6 + j];
            if (MathF.Abs(tj) > ra + rb) return false;
        }

        // And the nine cross products of one axis with another.
        for (int i = 0; i < 3; i++)
        {
            int i1 = (i + 1) % 3, i2 = (i + 2) % 3;
            for (int j = 0; j < 3; j++)
            {
                int j1 = (j + 1) % 3, j2 = (j + 2) % 3;
                float ra = ae[i1] * ar[i2 * 3 + j] + ae[i2] * ar[i1 * 3 + j];
                float rb = be[j1] * ar[i * 3 + j2] + be[j2] * ar[i * 3 + j1];
                float tv = te[i2] * r[i1 * 3 + j] - te[i1] * r[i2 * 3 + j];
                if (MathF.Abs(tv) > ra + rb) return false;
            }
        }
        return true;
    }

    public static bool IsPointInOBB(Vector3 point, Vector3 boxPos, Vector3 boxSize, Quaternion boxRot)
    {
        // 1. Move point to origin relative to box
        Vector3 relativePoint = point - boxPos;

        // 2. Rotate point by inverse box rotation to align with local axes
        // This is mathematically equivalent to (Point - Pos) * Rot^-1
        Vector3 localPoint = Vector3.Transform(relativePoint, Quaternion.Inverse(boxRot));

        // 3. Check if point is within half-extents
        Vector3 halfSize = boxSize / 2.0f;
        return (Math.Abs(localPoint.X) <= halfSize.X &&
                Math.Abs(localPoint.Y) <= halfSize.Y &&
                Math.Abs(localPoint.Z) <= halfSize.Z);
    }

    public static bool RayIntersectsCylinder(Vector3 origin, Vector3 dir, Vector3 cylPos, float radius, float height, out float distance)
    {
        distance = 0;
        float dx = dir.X, dz = dir.Z;
        float ox = origin.X - cylPos.X, oz = origin.Z - cylPos.Z;

        float a = dx * dx + dz * dz;
        float b = 2 * (ox * dx + oz * dz);
        float c = (ox * ox + oz * oz) - radius * radius;

        if (a < 0.000001f) // Vertical ray
        {
            if (ox * ox + oz * oz > radius * radius) return false;
            float t1 = (cylPos.Y - height / 2.0f - origin.Y) / dir.Y;
            float t2 = (cylPos.Y + height / 2.0f - origin.Y) / dir.Y;
            float tmin = Math.Min(t1, t2), tmax = Math.Max(t1, t2);
            if (tmax < 0) return false;
            distance = Math.Max(0, tmin);
            return true;
        }

        float disc = b * b - 4 * a * c;
        if (disc < 0) return false;
        disc = MathF.Sqrt(disc);

        float tSide1 = (-b - disc) / (2 * a);
        float tSide2 = (-b + disc) / (2 * a);

        float y1 = origin.Y + tSide1 * dir.Y;
        float y2 = origin.Y + tSide2 * dir.Y;
        float yMin = cylPos.Y - height / 2.0f;
        float yMax = cylPos.Y + height / 2.0f;

        float tMin = float.MaxValue;
        bool hit = false;

        if (tSide1 >= 0 && y1 >= yMin && y1 <= yMax) { tMin = Math.Min(tMin, tSide1); hit = true; }
        if (tSide2 >= 0 && y2 >= yMin && y2 <= yMax) { tMin = Math.Min(tMin, tSide2); hit = true; }

        // Check caps
        if (dir.Y != 0)
        {
            float tCap1 = (yMin - origin.Y) / dir.Y;
            if (tCap1 >= 0 && tCap1 < tMin)
            {
                float px = origin.X + tCap1 * dx - cylPos.X;
                float pz = origin.Z + tCap1 * dz - cylPos.Z;
                if (px * px + pz * pz <= radius * radius) { tMin = tCap1; hit = true; }
            }
            float tCap2 = (yMax - origin.Y) / dir.Y;
            if (tCap2 >= 0 && tCap2 < tMin)
            {
                float px = origin.X + tCap2 * dx - cylPos.X;
                float pz = origin.Z + tCap2 * dz - cylPos.Z;
                if (px * px + pz * pz <= radius * radius) { tMin = tCap2; hit = true; }
            }
        }

        if (hit) distance = tMin;
        return hit;
    }

    public static bool LineIntersectsOBB(Vector3 start, Vector3 end, Vector3 boxPos, Vector3 boxSize, Quaternion boxRot)
    {
        // Transform line to local space of the OBB
        Matrix4x4 worldToLocal = Matrix4x4.CreateTranslation(-boxPos) * Matrix4x4.CreateFromQuaternion(Quaternion.Inverse(boxRot));
        Vector3 localStart = Vector3.Transform(start, worldToLocal);
        Vector3 localEnd = Vector3.Transform(end, worldToLocal);

        // Now it's an AABB intersection in local space
        return LineIntersectsAABB(localStart, localEnd, Vector3.Zero, boxSize);
    }

    /// <summary>
    /// Where a ray enters a box, and which way that face points.
    ///
    /// The boolean above answers "is this box in the way", which is all occlusion ever needed. Anything
    /// that has to follow sound PAST a surface needs two more things: how far away it was, so the
    /// nearest one wins, and which way the surface faces, so the sound can carry on in the direction it
    /// would really go. The slab method gives both — the axis whose near-plane was crossed last is the
    /// face that was hit, and its sign is the side.
    ///
    /// Returns false when the ray misses, when the box is behind the ray, or when the origin is already
    /// inside it (there is no entry face to report).
    /// </summary>
    public static bool RayHitsOBB(Vector3 origin, Vector3 direction, float maxDistance,
                                  Vector3 boxPos, Vector3 boxSize, Quaternion boxRot,
                                  out float distance, out Vector3 normal)
    {
        distance = 0f; normal = Vector3.Zero;

        Quaternion inv = Quaternion.Inverse(boxRot);
        Vector3 o = Vector3.Transform(origin - boxPos, inv);
        Vector3 d = Vector3.Transform(direction, inv);

        Vector3 h = boxSize * 0.5f;
        float tmin = 0f, tmax = maxDistance;
        int axis = -1;
        float sign = 1f;

        for (int i = 0; i < 3; i++)
        {
            float oi = i == 0 ? o.X : i == 1 ? o.Y : o.Z;
            float di = i == 0 ? d.X : i == 1 ? d.Y : d.Z;
            float hi = i == 0 ? h.X : i == 1 ? h.Y : h.Z;

            if (MathF.Abs(di) < 1e-9f)
            {
                if (oi < -hi || oi > hi) return false;   // parallel and outside this slab
                continue;
            }

            float t1 = (-hi - oi) / di;
            float t2 = (hi - oi) / di;
            float s = -1f;
            if (t1 > t2) { (t1, t2) = (t2, t1); s = 1f; }
            if (t1 > tmin) { tmin = t1; axis = i; sign = s; }
            if (t2 < tmax) tmax = t2;
            if (tmin > tmax) return false;
        }

        if (axis < 0) return false;                     // started inside, or degenerate
        distance = tmin;

        Vector3 localN = axis == 0 ? new Vector3(sign, 0, 0)
                       : axis == 1 ? new Vector3(0, sign, 0)
                                   : new Vector3(0, 0, sign);
        normal = Vector3.Transform(localN, boxRot);
        return true;
    }

    public static bool LineIntersectsAABB(Vector3 start, Vector3 end, Vector3 boxPos, Vector3 boxSize)
    {
        Vector3 min = boxPos - (boxSize / 2.0f);
        Vector3 max = boxPos + (boxSize / 2.0f);
        Vector3 dir = end - start;
        float tmin = -float.MaxValue, tmax = float.MaxValue;

        // X-axis
        if (Math.Abs(dir.X) > 0.000001f) {
            float t1 = (min.X - start.X) / dir.X, t2 = (max.X - start.X) / dir.X;
            tmin = Math.Max(tmin, Math.Min(t1, t2)); tmax = Math.Min(tmax, Math.Max(t1, t2));
        } else if (start.X < min.X || start.X > max.X) return false;
        
        // Y-axis
        if (Math.Abs(dir.Y) > 0.000001f) {
            float t1 = (min.Y - start.Y) / dir.Y, t2 = (max.Y - start.Y) / dir.Y;
            tmin = Math.Max(tmin, Math.Min(t1, t2)); tmax = Math.Min(tmax, Math.Max(t1, t2));
        } else if (start.Y < min.Y || start.Y > max.Y) return false;
        
        // Z-axis
        if (Math.Abs(dir.Z) > 0.000001f) {
            float t1 = (min.Z - start.Z) / dir.Z, t2 = (max.Z - start.Z) / dir.Z;
            tmin = Math.Max(tmin, Math.Min(t1, t2)); tmax = Math.Min(tmax, Math.Max(t1, t2));
        } else if (start.Z < min.Z || start.Z > max.Z) return false;

        return tmax >= tmin && tmax >= 0 && tmin <= 1.0f;
    }

    public static bool RayIntersectsOBB(Vector3 start, Vector3 dir, Vector3 boxPos, Vector3 boxSize, Quaternion boxRot, out float distance)
    {
        Matrix4x4 worldToLocal = Matrix4x4.CreateTranslation(-boxPos) * Matrix4x4.CreateFromQuaternion(Quaternion.Inverse(boxRot));
        Vector3 localStart = Vector3.Transform(start, worldToLocal);
        Vector3 localDir = Vector3.TransformNormal(dir, worldToLocal);
        return RayIntersectsAABB(localStart, localDir, Vector3.Zero, boxSize, out distance);
    }

    public static bool RayIntersectsAABB(Vector3 start, Vector3 dir, Vector3 boxPos, Vector3 boxSize, out float distance)
    {
        Vector3 min = boxPos - (boxSize / 2.0f);
        Vector3 max = boxPos + (boxSize / 2.0f);
        float tmin = -float.MaxValue, tmax = float.MaxValue;
        distance = 0;

        if (Math.Abs(dir.X) > 0.000001f) {
            float t1 = (min.X - start.X) / dir.X, t2 = (max.X - start.X) / dir.X;
            tmin = Math.Max(tmin, Math.Min(t1, t2)); tmax = Math.Min(tmax, Math.Max(t1, t2));
        } else if (start.X < min.X || start.X > max.X) return false;

        if (Math.Abs(dir.Y) > 0.000001f) {
            float t1 = (min.Y - start.Y) / dir.Y, t2 = (max.Y - start.Y) / dir.Y;
            tmin = Math.Max(tmin, Math.Min(t1, t2)); tmax = Math.Min(tmax, Math.Max(t1, t2));
        } else if (start.Y < min.Y || start.Y > max.Y) return false;

        if (Math.Abs(dir.Z) > 0.000001f) {
            float t1 = (min.Z - start.Z) / dir.Z, t2 = (max.Z - start.Z) / dir.Z;
            tmin = Math.Max(tmin, Math.Min(t1, t2)); tmax = Math.Min(tmax, Math.Max(t1, t2));
        } else if (start.Z < min.Z || start.Z > max.Z) return false;

        if (tmax >= tmin && tmax > 0) { distance = tmin > 0 ? tmin : 0; return true; }
        return false;
    }

    public static bool LineIntersectsSphere(Vector3 start, Vector3 end, Vector3 center, float radius)
    {
        Vector3 d = end - start;
        Vector3 f = start - center;
        float a = Vector3.Dot(d, d);
        float b = 2 * Vector3.Dot(f, d);
        float c = Vector3.Dot(f, f) - radius * radius;
        float discriminant = b * b - 4 * a * c;
        if (discriminant < 0) return false;
        discriminant = MathF.Sqrt(discriminant);
        float t1 = (-b - discriminant) / (2 * a);
        float t2 = (-b + discriminant) / (2 * a);
        return (t1 >= 0 && t1 <= 1) || (t2 >= 0 && t2 <= 1);
    }

    private static float Square(this float f) => f * f;

    public static bool RayIntersectsSphere(Vector3 start, Vector3 dir, Vector3 center, float radius, out float entry, out float exit)
    {
        entry = 0; exit = 0;
        Vector3 m = start - center;
        float b = Vector3.Dot(m, dir);
        float c = Vector3.Dot(m, m) - radius * radius;
        if (c > 0.0f && b > 0.0f) return false;
        float discr = b * b - c;
        if (discr < 0.0f) return false;
        float sqrtDiscr = MathF.Sqrt(discr);
        entry = -b - sqrtDiscr;
        exit = -b + sqrtDiscr;
        if (entry < 0.0f) entry = 0.0f;
        return true;
    }

    public static bool RayIntersectsCylinder(Vector3 start, Vector3 dir, Vector3 cylPos, float radius, float height, out float entry, out float exit)
    {
        entry = 0; exit = 0;
        float dx = dir.X, dz = dir.Z;
        float ox = start.X - cylPos.X, oz = start.Z - cylPos.Z;

        float a = dx * dx + dz * dz;
        float b = 2 * (ox * dx + oz * dz);
        float c = (ox * ox + oz * oz) - radius * radius;

        float yMin = cylPos.Y - height / 2.0f;
        float yMax = cylPos.Y + height / 2.0f;

        float tNear = -float.MaxValue;
        float tFar = float.MaxValue;

        // Check cylinder body
        if (a > 0.000001f)
        {
            float discr = b * b - 4 * a * c;
            if (discr < 0.0f) return false;
            float sqrtDiscr = MathF.Sqrt(discr);
            float t1 = (-b - sqrtDiscr) / (2 * a);
            float t2 = (-b + sqrtDiscr) / (2 * a);
            if (t1 > t2) { float tmp = t1; t1 = t2; t2 = tmp; }
            tNear = t1; tFar = t2;
        }
        else if (c > 0.0f) return false;

        // Check cylinder caps
        if (Math.Abs(dir.Y) > 0.000001f)
        {
            float ty1 = (yMin - start.Y) / dir.Y;
            float ty2 = (yMax - start.Y) / dir.Y;
            if (ty1 > ty2) { float tmp = ty1; ty1 = ty2; ty2 = tmp; }
            if (ty1 > tNear) tNear = ty1;
            if (ty2 < tFar) tFar = ty2;
        }
        else if (start.Y < yMin || start.Y > yMax) return false;

        if (tNear > tFar || tFar < 0.0f) return false;

        entry = tNear < 0.0f ? 0.0f : tNear;
        exit = tFar;
        return true;
    }

    public static bool RayIntersectsCone(Vector3 start, Vector3 dir, Vector3 conePos, float radius, float height, out float entry, out float exit)
    {
        entry = 0; exit = 0;
        // Cone tip is at conePos.Y + height/2. Base is at conePos.Y - height/2.
        float tipY = conePos.Y + height / 2.0f;
        float baseY = conePos.Y - height / 2.0f;

        float dx = dir.X, dy = dir.Y, dz = dir.Z;
        float ox = start.X - conePos.X, oy = start.Y - tipY, oz = start.Z - conePos.Z;

        float ratio = (radius * radius) / (height * height);

        float a = dx * dx + dz * dz - ratio * dy * dy;
        float b = 2 * (ox * dx + oz * dz - ratio * oy * dy);
        float c = ox * ox + oz * oz - ratio * oy * oy;

        // A cone and its base are one convex solid, so a ray crosses its boundary at most twice: the
        // path through it runs from the smallest valid crossing to the largest. The crossings used to
        // be filed by their order in the quadratic — the first root as the entry, the second as the
        // exit — and when one root lay on the mirror cone above the tip and was rightly thrown away,
        // the one left was filed in the wrong place: a ray down through the slope got its entry at
        // minus infinity (so "inside from the listener onward") and one up through the base an exit at
        // infinity.
        float tNear = float.MaxValue, tFar = -float.MaxValue;
        void Cross(float t) { if (t < tNear) tNear = t; if (t > tFar) tFar = t; }

        if (Math.Abs(a) > 0.000001f)
        {
            float discr = b * b - 4 * a * c;
            if (discr >= 0.0f)
            {
                float sqrtDiscr = MathF.Sqrt(discr);
                float t1 = (-b - sqrtDiscr) / (2 * a);
                float t2 = (-b + sqrtDiscr) / (2 * a);
                // Only the nappe below the tip, and only as far down as the base.
                float y1 = start.Y + t1 * dy;
                float y2 = start.Y + t2 * dy;
                if (y1 <= tipY && y1 >= baseY) Cross(t1);
                if (y2 <= tipY && y2 >= baseY) Cross(t2);
            }
        }
        else if (Math.Abs(b) > 0.000001f)
        {
            float t = -c / b;
            float y = start.Y + t * dy;
            if (y <= tipY && y >= baseY) Cross(t);
        }

        // The base.
        if (Math.Abs(dy) > 0.000001f)
        {
            float tCap = (baseY - start.Y) / dy;
            float px = start.X + tCap * dx - conePos.X;
            float pz = start.Z + tCap * dz - conePos.Z;
            if (px * px + pz * pz <= radius * radius) Cross(tCap);
        }

        if (tNear > tFar || tFar < 0.0f) return false;

        entry = tNear < 0.0f ? 0.0f : tNear;
        exit = tFar;
        return true;
    }

    public struct CollisionResult
    {
        public bool IsColliding;
        public Vector3 Normal;
        public float Penetration;
        public string Material;
    }

    public static bool AABBIntersectsCylinder(Vector3 aabbMin, Vector3 aabbMax, Vector3 cylPos, float radius, float height)
    {
        float cylMinY = cylPos.Y - height / 2.0f, cylMaxY = cylPos.Y + height / 2.0f;
        if (aabbMax.Y < cylMinY || aabbMin.Y > cylMaxY) return false;
        float closestX = Math.Max(aabbMin.X, Math.Min(cylPos.X, aabbMax.X));
        float closestZ = Math.Max(aabbMin.Z, Math.Min(cylPos.Z, aabbMax.Z));
        float distXZSq = (closestX - cylPos.X).Square() + (closestZ - cylPos.Z).Square();
        return distXZSq < radius * radius;
    }

    public static CollisionResult GetCylinderAABBOverlap(Vector3 aabbMin, Vector3 aabbMax, Vector3 cylPos, float radius, float height)
    {
        CollisionResult result = new CollisionResult { IsColliding = false, Normal = Vector3.Zero, Penetration = 0 };

        float cylMinY = cylPos.Y - height / 2.0f;
        float cylMaxY = cylPos.Y + height / 2.0f;

        // 1. Vertical check
        if (aabbMax.Y < cylMinY || aabbMin.Y > cylMaxY) return result;

        // 2. Horizontal check (treating AABB as a rectangle and Cylinder as a circle in XZ)
        float closestX = Math.Clamp(cylPos.X, aabbMin.X, aabbMax.X);
        float closestZ = Math.Clamp(cylPos.Z, aabbMin.Z, aabbMax.Z);

        float dx = cylPos.X - closestX;
        float dz = cylPos.Z - closestZ;
        float distSq = dx * dx + dz * dz;

        if (distSq >= radius * radius) return result;

        float dist = MathF.Sqrt(distSq);
        result.IsColliding = true;

        if (dist > 0.0001f)
        {
            result.Normal = new Vector3(dx / dist, 0, dz / dist);
            result.Penetration = radius - dist;
        }
        else
        {
            // Center is exactly on/inside AABB edge in XZ. 
            // We need a deterministic direction to push out.
            // We'll use the direction from the AABB center to the cylinder center.
            float midX = (aabbMin.X + aabbMax.X) / 2f;
            float midZ = (aabbMin.Z + aabbMax.Z) / 2f;
            
            Vector3 toCyl = new Vector3(cylPos.X - midX, 0, cylPos.Z - midZ);
            if (toCyl.LengthSquared() < 0.0001f) toCyl = Vector3.UnitX; // Fallback
            
            result.Normal = Vector3.Normalize(toCyl);
            result.Penetration = radius; 
        }

        return result;
    }

}

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
    public static bool AABBIntersectsOBB(Vector3 aabbMin, Vector3 aabbMax, Vector3 obbPos, Vector3 obbSize, Quaternion obbRot)
    {
        // 1. Point-in-OBB sampling (Fast & usually sufficient for voxelization)
        // Sample center + all 8 corners of the AABB
        if (IsPointInOBB((aabbMin + aabbMax) / 2f, obbPos, obbSize, obbRot)) return true;
        if (IsPointInOBB(aabbMin, obbPos, obbSize, obbRot)) return true;
        if (IsPointInOBB(aabbMax, obbPos, obbSize, obbRot)) return true;
        if (IsPointInOBB(new Vector3(aabbMin.X, aabbMin.Y, aabbMax.Z), obbPos, obbSize, obbRot)) return true;
        if (IsPointInOBB(new Vector3(aabbMax.X, aabbMin.Y, aabbMin.Z), obbPos, obbSize, obbRot)) return true;
        if (IsPointInOBB(new Vector3(aabbMin.X, aabbMax.Y, aabbMin.Z), obbPos, obbSize, obbRot)) return true;
        if (IsPointInOBB(new Vector3(aabbMax.X, aabbMax.Y, aabbMin.Z), obbPos, obbSize, obbRot)) return true;
        if (IsPointInOBB(new Vector3(aabbMin.X, aabbMax.Y, aabbMax.Z), obbPos, obbSize, obbRot)) return true;
        if (IsPointInOBB(new Vector3(aabbMax.X, aabbMin.Y, aabbMax.Z), obbPos, obbSize, obbRot)) return true;

        // 2. Sampling midpoints of AABB edges for extremely thin walls
        Vector3 center = (aabbMin + aabbMax) / 2f;
        if (IsPointInOBB(new Vector3(center.X, aabbMin.Y, center.Z), obbPos, obbSize, obbRot)) return true;
        if (IsPointInOBB(new Vector3(center.X, aabbMax.Y, center.Z), obbPos, obbSize, obbRot)) return true;

        return false;
    }

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
        
        // Check if OBB is entirely inside the Box (rare but possible for large nodes)
        if (Math.Abs(obbCenter.X - boxCenter.X) <= halfSize.X &&
            Math.Abs(obbCenter.Y - boxCenter.Y) <= halfSize.Y &&
            Math.Abs(obbCenter.Z - boxCenter.Z) <= halfSize.Z)
            return BoxContainment.Partial;

        return BoxContainment.Outside;
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

    public static bool IsPointInCylinder(Vector3 point, Vector3 cylPos, float radius, float height)
    {
        float dy = Math.Abs(point.Y - cylPos.Y);
        if (dy > height / 2.0f) return false;

        float dx = point.X - cylPos.X;
        float dz = point.Z - cylPos.Z;
        return (dx * dx + dz * dz) <= (radius * radius);
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

    public static bool LineIntersectsCylinder(Vector3 start, Vector3 end, Vector3 basePos, float radius, float height)
    {
        Vector3 d = end - start;
        float dx = d.X, dz = d.Z;
        float fx = start.X - basePos.X, fz = start.Z - basePos.Z;
        float a = dx * dx + dz * dz;
        float b = 2 * (fx * dx + fz * dz);
        float c = (fx * fx + fz * fz) - radius * radius;
        if (a < 0.000001f) {
            if (fx * fx + fz * fz > radius * radius) return false;
            float tminY = Math.Min(start.Y, end.Y), tmaxY = Math.Max(start.Y, end.Y);
            return tmaxY >= basePos.Y - height / 2.0f && tminY <= basePos.Y + height / 2.0f;
        }
        float discriminant = b * b - 4 * a * c;
        if (discriminant < 0) return false;
        discriminant = MathF.Sqrt(discriminant);
        float t1 = (-b - discriminant) / (2 * a), t2 = (-b + discriminant) / (2 * a);
        float y1 = start.Y + t1 * d.Y, y2 = start.Y + t2 * d.Y;
        float cylMinY = basePos.Y - height / 2.0f, cylMaxY = basePos.Y + height / 2.0f;
        if ((t1 >= 0 && t1 <= 1 && y1 >= cylMinY && y1 <= cylMaxY) || (t2 >= 0 && t2 <= 1 && y2 >= cylMinY && y2 <= cylMaxY)) return true;
        if (d.Y != 0) {
            float tB = (cylMinY - start.Y) / d.Y, tT = (cylMaxY - start.Y) / d.Y;
            if (tB >= 0 && tB <= 1 && (start.X + tB * dx - basePos.X).Square() + (start.Z + tB * dz - basePos.Z).Square() <= radius * radius) return true;
            if (tT >= 0 && tT <= 1 && (start.X + tT * dx - basePos.X).Square() + (start.Z + tT * dz - basePos.Z).Square() <= radius * radius) return true;
        }
        return false;
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

        float tNear = -float.MaxValue;
        float tFar = float.MaxValue;
        bool hitSurface = false;

        if (Math.Abs(a) > 0.000001f)
        {
            float discr = b * b - 4 * a * c;
            if (discr >= 0.0f)
            {
                float sqrtDiscr = MathF.Sqrt(discr);
                float t1 = (-b - sqrtDiscr) / (2 * a);
                float t2 = (-b + sqrtDiscr) / (2 * a);
                if (t1 > t2) { float tmp = t1; t1 = t2; t2 = tmp; }
                
                // Ensure the intersection is on the correct nappe of the cone (below tip)
                float y1 = start.Y + t1 * dy;
                float y2 = start.Y + t2 * dy;
                
                if (y1 <= tipY && y1 >= baseY) { tNear = Math.Max(tNear, t1); hitSurface = true; }
                if (y2 <= tipY && y2 >= baseY) { tFar = Math.Min(tFar, t2); hitSurface = true; }
            }
        }
        else if (Math.Abs(b) > 0.000001f)
        {
            float t = -c / b;
            float y = start.Y + t * dy;
            if (y <= tipY && y >= baseY) { tNear = t; tFar = t; hitSurface = true; }
        }

        // Cap check
        if (Math.Abs(dy) > 0.000001f)
        {
            float tCap = (baseY - start.Y) / dy;
            float px = start.X + tCap * dx - conePos.X;
            float pz = start.Z + tCap * dz - conePos.Z;
            if (px * px + pz * pz <= radius * radius)
            {
                if (!hitSurface) { tNear = tCap; tFar = tCap; hitSurface = true; }
                else
                {
                    if (tCap < tNear) tNear = tCap;
                    if (tCap > tFar) tFar = tCap;
                }
            }
        }

        if (!hitSurface || tNear > tFar || tFar < 0.0f) return false;
        
        entry = tNear < 0.0f ? 0.0f : tNear;
        exit = tFar;
        return true;
    }

    public static bool AABBIntersectsAABB(Vector3 min1, Vector3 max1, Vector3 min2, Vector3 max2)
    {
        return (min1.X <= max2.X && max1.X >= min2.X) && (min1.Y <= max2.Y && max1.Y >= min2.Y) && (min1.Z <= max2.Z && max1.Z >= min2.Z);
    }

    public static bool AABBIntersectsAABB2D(Vector3 min1, Vector3 max1, Vector3 min2, Vector3 max2)
    {
        return (min1.X <= max2.X && max1.X >= min2.X) && (min1.Z <= max2.Z && max1.Z >= min2.Z);
    }

    public static bool AABBIntersectsSphere(Vector3 aabbMin, Vector3 aabbMax, Vector3 sphereCenter, float radius)
    {
        float x = Math.Max(aabbMin.X, Math.Min(sphereCenter.X, aabbMax.X));
        float y = Math.Max(aabbMin.Y, Math.Min(sphereCenter.Y, aabbMax.Y));
        float z = Math.Max(aabbMin.Z, Math.Min(sphereCenter.Z, aabbMax.Z));
        float distSq = (x - sphereCenter.X).Square() + (y - sphereCenter.Y).Square() + (z - sphereCenter.Z).Square();
        return distSq < radius * radius;
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

    public static bool AABBIntersectsCone(Vector3 aabbMin, Vector3 aabbMax, Vector3 conePos, float radius, float height)
    {
        return AABBIntersectsCylinder(aabbMin, aabbMax, conePos, radius, height);
    }
}

using System.Numerics;
using OpenFPS.Common;

namespace OpenFPS.Tests;

/// <summary>
/// Unit tests for GeometryUtils — the collision detection and ray-cast primitives
/// used throughout the physics, audio occlusion, and acoustic pathfinding systems.
/// </summary>
public class GeometryUtilsTests
{
    // ─── AABB vs AABB ─────────────────────────────────────────────────────────────

    [Fact]
    public void AABB_Overlapping_ReturnsTrue()
    {
        var min1 = new Vector3(0, 0, 0); var max1 = new Vector3(2, 2, 2);
        var min2 = new Vector3(1, 1, 1); var max2 = new Vector3(3, 3, 3);
        Assert.True(GeometryUtils.AABBIntersectsAABB(min1, max1, min2, max2));
    }

    [Fact]
    public void AABB_Separated_ReturnsFalse()
    {
        var min1 = new Vector3(0, 0, 0); var max1 = new Vector3(1, 1, 1);
        var min2 = new Vector3(2, 2, 2); var max2 = new Vector3(3, 3, 3);
        Assert.False(GeometryUtils.AABBIntersectsAABB(min1, max1, min2, max2));
    }

    [Fact]
    public void AABB_TouchingEdge_ReturnsTrue()
    {
        var min1 = new Vector3(0, 0, 0); var max1 = new Vector3(1, 1, 1);
        var min2 = new Vector3(1, 0, 0); var max2 = new Vector3(2, 1, 1);
        Assert.True(GeometryUtils.AABBIntersectsAABB(min1, max1, min2, max2));
    }

    // ─── Point in OBB ─────────────────────────────────────────────────────────────

    [Fact]
    public void PointInOBB_CenterPoint_ReturnsTrue()
    {
        var center = new Vector3(5, 5, 5);
        var size   = new Vector3(4, 4, 4);
        Assert.True(GeometryUtils.IsPointInOBB(center, center, size, Quaternion.Identity));
    }

    [Fact]
    public void PointInOBB_OutsidePoint_ReturnsFalse()
    {
        var center = new Vector3(5, 5, 5);
        var size   = new Vector3(4, 4, 4);
        var point  = new Vector3(20, 5, 5);
        Assert.False(GeometryUtils.IsPointInOBB(point, center, size, Quaternion.Identity));
    }

    [Fact]
    public void PointInOBB_RotatedBox_CorrectlyClassifiesPoint()
    {
        // Box rotated 45° around Y — a point that would be inside axis-aligned but is outside the rotated box
        var center = Vector3.Zero;
        var size   = new Vector3(2, 4, 0.5f);
        var rot    = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 4f);

        // A point at (1.2, 0, 0) is inside the AABB of size (2,4,0.5) but outside the OBB when rotated 45°
        var outsideOBB = new Vector3(1.2f, 0, 0);
        Assert.False(GeometryUtils.IsPointInOBB(outsideOBB, center, size, rot),
            "Point should be outside the 45°-rotated OBB");
    }

    // ─── AABB vs Cylinder ─────────────────────────────────────────────────────────

    [Fact]
    public void AABBIntersectsCylinder_Overlapping_ReturnsTrue()
    {
        var min = new Vector3(-1, -1, -1);
        var max = new Vector3(1, 1, 1);
        var cylPos = new Vector3(0, 0, 0);
        Assert.True(GeometryUtils.AABBIntersectsCylinder(min, max, cylPos, radius: 0.5f, height: 2f));
    }

    [Fact]
    public void AABBIntersectsCylinder_NonOverlapping_ReturnsFalse()
    {
        var min = new Vector3(-1, -1, -1);
        var max = new Vector3(1, 1, 1);
        var cylPos = new Vector3(10, 0, 10);
        Assert.False(GeometryUtils.AABBIntersectsCylinder(min, max, cylPos, radius: 0.5f, height: 2f));
    }

    [Fact]
    public void AABBIntersectsCylinder_AboveCylinder_ReturnsFalse()
    {
        // AABB completely above the cylinder's height range
        var min = new Vector3(-1, 5, -1);
        var max = new Vector3(1, 7, 1);
        var cylPos = new Vector3(0, 0, 0);   // cylinder centered at y=0, height=2 → range [-1, 1]
        Assert.False(GeometryUtils.AABBIntersectsCylinder(min, max, cylPos, radius: 2f, height: 2f));
    }

    // ─── GetCylinderAABBOverlap ────────────────────────────────────────────────────

    [Fact]
    public void GetCylinderAABBOverlap_Intersecting_ReturnsValidNormalAndPenetration()
    {
        var aabbMin = new Vector3(-1, -1, -1);
        var aabbMax = new Vector3(1, 1, 1);
        var cylPos  = new Vector3(0.8f, 0, 0);   // Cylinder center partially inside AABB

        var result = GeometryUtils.GetCylinderAABBOverlap(aabbMin, aabbMax, cylPos, radius: 0.5f, height: 2f);

        Assert.True(result.IsColliding);
        Assert.True(result.Penetration > 0);
        Assert.True(result.Normal.LengthSquared() > 0.9f, "Normal should be approximately unit length");
    }

    [Fact]
    public void GetCylinderAABBOverlap_NonIntersecting_ReturnsNotColliding()
    {
        var aabbMin = new Vector3(-1, -1, -1);
        var aabbMax = new Vector3(1, 1, 1);
        var cylPos  = new Vector3(10, 0, 10);

        var result = GeometryUtils.GetCylinderAABBOverlap(aabbMin, aabbMax, cylPos, radius: 0.5f, height: 2f);
        Assert.False(result.IsColliding);
    }

    // ─── Ray vs AABB ───────────────────────────────────────────────────────────────

    [Fact]
    public void RayIntersectsAABB_HittingCenter_ReturnsTrue()
    {
        var start  = new Vector3(-5, 0, 0);
        var dir    = Vector3.Normalize(new Vector3(1, 0, 0));
        var boxPos = Vector3.Zero;
        var boxSize = new Vector3(2, 2, 2);

        bool hit = GeometryUtils.RayIntersectsAABB(start, dir, boxPos, boxSize, out float dist);
        Assert.True(hit);
        Assert.True(dist >= 0);
    }

    [Fact]
    public void RayIntersectsAABB_MissingBox_ReturnsFalse()
    {
        var start  = new Vector3(-5, 5, 0);    // Ray above the box
        var dir    = Vector3.Normalize(new Vector3(1, 0, 0));
        var boxPos = Vector3.Zero;
        var boxSize = new Vector3(2, 2, 2);

        bool hit = GeometryUtils.RayIntersectsAABB(start, dir, boxPos, boxSize, out _);
        Assert.False(hit);
    }

    // ─── Ray vs OBB ────────────────────────────────────────────────────────────────

    [Fact]
    public void RayIntersectsOBB_AxisAligned_HitsBox()
    {
        var start   = new Vector3(-5, 0, 0);
        var dir     = Vector3.Normalize(new Vector3(1, 0, 0));
        var boxPos  = Vector3.Zero;
        var boxSize = new Vector3(2, 2, 2);

        bool hit = GeometryUtils.RayIntersectsOBB(start, dir, boxPos, boxSize, Quaternion.Identity, out float dist);
        Assert.True(hit);
        Assert.True(dist > 0 && dist < 10f);
    }

    [Fact]
    public void RayIntersectsOBB_Rotated45_CorrectlyDetectsHit()
    {
        var start   = new Vector3(0, 0, -5);
        var dir     = Vector3.Normalize(new Vector3(0, 0, 1));
        var boxPos  = Vector3.Zero;
        var boxSize = new Vector3(2, 2, 2);
        var rot     = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 4f);

        bool hit = GeometryUtils.RayIntersectsOBB(start, dir, boxPos, boxSize, rot, out float dist);
        Assert.True(hit, "Ray through center should hit rotated OBB");
        Assert.True(dist >= 0);
    }

    // ─── Line vs Sphere ────────────────────────────────────────────────────────────

    [Fact]
    public void LineIntersectsSphere_ThroughCenter_ReturnsTrue()
    {
        var start  = new Vector3(-5, 0, 0);
        var end    = new Vector3(5, 0, 0);
        var center = Vector3.Zero;

        Assert.True(GeometryUtils.LineIntersectsSphere(start, end, center, radius: 1f));
    }

    [Fact]
    public void LineIntersectsSphere_NotIntersecting_ReturnsFalse()
    {
        var start  = new Vector3(-5, 5, 0);
        var end    = new Vector3(5, 5, 0);
        var center = Vector3.Zero;

        Assert.False(GeometryUtils.LineIntersectsSphere(start, end, center, radius: 1f));
    }

    // ─── Ray vs Sphere ────────────────────────────────────────────────────────────

    [Fact]
    public void RayIntersectsSphere_HittingCenter_ReturnsValidEntry()
    {
        var start  = new Vector3(-5, 0, 0);
        var dir    = Vector3.Normalize(new Vector3(1, 0, 0));
        var center = Vector3.Zero;

        bool hit = GeometryUtils.RayIntersectsSphere(start, dir, center, radius: 1f, out float entry, out float exit);
        Assert.True(hit);
        Assert.True(entry >= 0);
        Assert.True(exit > entry);
    }

    // ─── Point in Cylinder ────────────────────────────────────────────────────────

    [Fact]
    public void PointInCylinder_InsidePoint_ReturnsTrue()
    {
        var cylPos = new Vector3(0, 0, 0);
        Assert.True(GeometryUtils.IsPointInCylinder(new Vector3(0, 0, 0), cylPos, radius: 1f, height: 2f));
    }

    [Fact]
    public void PointInCylinder_OutsideRadially_ReturnsFalse()
    {
        var cylPos = new Vector3(0, 0, 0);
        Assert.False(GeometryUtils.IsPointInCylinder(new Vector3(2f, 0, 0), cylPos, radius: 1f, height: 2f));
    }

    [Fact]
    public void PointInCylinder_AboveCap_ReturnsFalse()
    {
        var cylPos = new Vector3(0, 0, 0);
        Assert.False(GeometryUtils.IsPointInCylinder(new Vector3(0, 5, 0), cylPos, radius: 1f, height: 2f));
    }

    // ─── BoxContainment ───────────────────────────────────────────────────────────

    [Fact]
    public void BoxContainment_FullyInside_ReturnsFullyInside()
    {
        var boxCenter = new Vector3(5, 5, 5);
        var boxSize   = new Vector3(10, 10, 10);
        var obbCenter = new Vector3(5, 5, 5);
        var obbSize   = new Vector3(20, 20, 20);

        var result = GeometryUtils.GetBoxContainmentInOBB(boxCenter, boxSize, obbCenter, obbSize, Quaternion.Identity);
        Assert.Equal(BoxContainment.FullyInside, result);
    }

    [Fact]
    public void BoxContainment_Outside_ReturnsOutside()
    {
        var boxCenter = new Vector3(50, 50, 50);
        var boxSize   = new Vector3(1, 1, 1);
        var obbCenter = Vector3.Zero;
        var obbSize   = new Vector3(2, 2, 2);

        var result = GeometryUtils.GetBoxContainmentInOBB(boxCenter, boxSize, obbCenter, obbSize, Quaternion.Identity);
        Assert.Equal(BoxContainment.Outside, result);
    }
}

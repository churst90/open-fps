using System;
using System.Numerics;
using OpenFPS.Common;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// Rays against a cylinder and a cone — a trunk, a post, a pole — as the client's spatial service
/// uses them: first hit for raycasts, entry and exit for how much of a sound's path passes through.
/// A cylinder and a cone of radius 1 and height 2 at the origin; the cone's tip at y = 1, its base at
/// y = -1, so its radius at height y is (1 - y) / 2. Written for the 343 mutants of these functions
/// that no test reached in the 2026-09-24 mutation run.
/// </summary>
public class RayShapeTests
{
    private static readonly Vector3 O = Vector3.Zero;
    private const float Tol = 1e-3f;
    private static readonly Vector3 Down = -Vector3.UnitY;
    private static Vector3 Dir(float x, float y, float z) => Vector3.Normalize(new Vector3(x, y, z));

    // ── Cylinder, first hit ───────────────────────────────────────────────────────────────────

    [Fact]
    public void ACylindersSideIsHitAtItsRadius()
    {
        Assert.True(GeometryUtils.RayIntersectsCylinder(new Vector3(-5, 0, 0), Vector3.UnitX, O, 1f, 2f, out float d));
        Assert.Equal(4f, d, 3);
        // From inside, the first surface ahead is the far side.
        Assert.True(GeometryUtils.RayIntersectsCylinder(O, Vector3.UnitX, O, 1f, 2f, out d));
        Assert.Equal(1f, d, 3);
    }

    [Fact]
    public void ACylinderIsMissedAboveBesideAndBehind()
    {
        Assert.False(GeometryUtils.RayIntersectsCylinder(new Vector3(-5, 2, 0), Vector3.UnitX, O, 1f, 2f, out _));
        Assert.False(GeometryUtils.RayIntersectsCylinder(new Vector3(-5, 0, 2), Vector3.UnitX, O, 1f, 2f, out _));
        Assert.False(GeometryUtils.RayIntersectsCylinder(new Vector3(5, 0, 0), Vector3.UnitX, O, 1f, 2f, out _));
    }

    [Fact]
    public void ACylindersCapsAreHitStraightDownAndAtAnAngle()
    {
        Assert.True(GeometryUtils.RayIntersectsCylinder(new Vector3(0, 5, 0), Down, O, 1f, 2f, out float d));
        Assert.Equal(4f, d, 3);
        Assert.False(GeometryUtils.RayIntersectsCylinder(new Vector3(2, 5, 0), Down, O, 1f, 2f, out _));
        Assert.False(GeometryUtils.RayIntersectsCylinder(new Vector3(0, 5, 0), Vector3.UnitY, O, 1f, 2f, out _));
        // Down at 45 degrees onto the top: at y = 1 after 2 sqrt 2, half a metre in from the rim.
        Assert.True(GeometryUtils.RayIntersectsCylinder(new Vector3(0, 3, -2.5f), Dir(0, -1, 1), O, 1f, 2f, out d));
        Assert.Equal(2f * MathF.Sqrt(2f), d, 3);
        // ...and up onto the bottom.
        Assert.True(GeometryUtils.RayIntersectsCylinder(new Vector3(0, -3, -2.5f), Dir(0, 1, 1), O, 1f, 2f, out d));
        Assert.Equal(2f * MathF.Sqrt(2f), d, 3);
    }

    /// <summary>A cap behind the ray does not count, and one reached after the side loses to it.</summary>
    [Fact]
    public void TheNearestCylinderSurfaceAheadIsTheHit()
    {
        // From inside, low down, angled up: the bottom cap's plane is behind; out through the side.
        Assert.True(GeometryUtils.RayIntersectsCylinder(new Vector3(0, -0.5f, 0), Dir(0, 1, 1), O, 1f, 2f, out float d));
        Assert.Equal(MathF.Sqrt(2f), d, 3);
        // Rising gently into the side just under the top: the side at z = -1 comes before the top.
        var dir = Dir(0, 0.2f, 1);
        Assert.True(GeometryUtils.RayIntersectsCylinder(new Vector3(0, 0.5f, -3), dir, O, 1f, 2f, out d));
        Assert.Equal(2f / dir.Z, d, 3);
    }

    // ── Cylinder, entry and exit ──────────────────────────────────────────────────────────────

    [Fact]
    public void ThroughACylindersSideAndUpItsAxis()
    {
        Assert.True(GeometryUtils.RayIntersectsCylinder(new Vector3(-5, 0, 0), Vector3.UnitX, O, 1f, 2f, out float en, out float ex));
        Assert.Equal(4f, en, 3); Assert.Equal(6f, ex, 3);
        Assert.True(GeometryUtils.RayIntersectsCylinder(O, Vector3.UnitX, O, 1f, 2f, out en, out ex));
        Assert.Equal(0f, en, 3); Assert.Equal(1f, ex, 3);
        Assert.True(GeometryUtils.RayIntersectsCylinder(new Vector3(0, 5, 0), Down, O, 1f, 2f, out en, out ex));
        Assert.Equal(4f, en, 3); Assert.Equal(6f, ex, 3);
        // In through the top at 45 degrees, out through the side.
        Assert.True(GeometryUtils.RayIntersectsCylinder(new Vector3(0, 3, -2.5f), Dir(0, -1, 1), O, 1f, 2f, out en, out ex));
        Assert.Equal(2f * MathF.Sqrt(2f), en, 3); Assert.Equal(3.5f * MathF.Sqrt(2f), ex, 3);
    }

    [Fact]
    public void PastACylinderIsNoPath()
    {
        Assert.False(GeometryUtils.RayIntersectsCylinder(new Vector3(2, 5, 0), Down, O, 1f, 2f, out _, out _));
        Assert.False(GeometryUtils.RayIntersectsCylinder(new Vector3(-5, 2, 0), Vector3.UnitX, O, 1f, 2f, out _, out _));
        Assert.False(GeometryUtils.RayIntersectsCylinder(new Vector3(-5, -2, 0), Vector3.UnitX, O, 1f, 2f, out _, out _));
        Assert.False(GeometryUtils.RayIntersectsCylinder(new Vector3(-5, 0, 2), Vector3.UnitX, O, 1f, 2f, out _, out _));
        Assert.False(GeometryUtils.RayIntersectsCylinder(new Vector3(5, 0, 0), Vector3.UnitX, O, 1f, 2f, out _, out _));
        // Level, just under the top, still in.
        Assert.True(GeometryUtils.RayIntersectsCylinder(new Vector3(-5, 0.99f, 0), Vector3.UnitX, O, 1f, 2f, out _, out _));
    }

    // ── Cone ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AConeIsNarrowerTowardsItsTip()
    {
        Assert.True(GeometryUtils.RayIntersectsCone(new Vector3(-5, 0, 0), Vector3.UnitX, O, 1f, 2f, out float en, out float ex));
        Assert.Equal(4.5f, en, 3); Assert.Equal(5.5f, ex, 3);
        Assert.True(GeometryUtils.RayIntersectsCone(new Vector3(-5, 0.5f, 0), Vector3.UnitX, O, 1f, 2f, out en, out ex));
        Assert.Equal(4.75f, en, 3); Assert.Equal(5.25f, ex, 3);
        Assert.True(GeometryUtils.RayIntersectsCone(new Vector3(-5, -0.5f, 0), Vector3.UnitX, O, 1f, 2f, out en, out ex));
        Assert.Equal(4.25f, en, 3); Assert.Equal(5.75f, ex, 3);
    }

    [Fact]
    public void DownAConeFromTipToBase()
    {
        Assert.True(GeometryUtils.RayIntersectsCone(new Vector3(0, 5, 0), Down, O, 1f, 2f, out float en, out float ex));
        Assert.InRange(en, 4f - Tol, 4f + Tol); Assert.InRange(ex, 6f - Tol, 6f + Tol);
        // Half a metre out: onto the slope at y = 0, out through the base.
        Assert.True(GeometryUtils.RayIntersectsCone(new Vector3(0.5f, 5, 0), Down, O, 1f, 2f, out en, out ex));
        Assert.InRange(en, 5f - Tol, 5f + Tol); Assert.InRange(ex, 6f - Tol, 6f + Tol);
        // Up into the base from below.
        Assert.True(GeometryUtils.RayIntersectsCone(new Vector3(0.5f, -5, 0), Vector3.UnitY, O, 1f, 2f, out en, out ex));
        Assert.InRange(en, 4f - Tol, 4f + Tol); Assert.InRange(ex, 5f - Tol, 5f + Tol);
    }

    [Fact]
    public void PastAConeIsNoPath()
    {
        Assert.False(GeometryUtils.RayIntersectsCone(new Vector3(-5, 1.5f, 0), Vector3.UnitX, O, 1f, 2f, out _, out _));   // over the tip
        Assert.False(GeometryUtils.RayIntersectsCone(new Vector3(-5, -1.5f, 0), Vector3.UnitX, O, 1f, 2f, out _, out _));  // under the base
        Assert.False(GeometryUtils.RayIntersectsCone(new Vector3(1.5f, 5, 0), Down, O, 1f, 2f, out _, out _));             // beside it
        Assert.False(GeometryUtils.RayIntersectsCone(new Vector3(5, 0, 0), Vector3.UnitX, O, 1f, 2f, out _, out _));       // behind
        Assert.False(GeometryUtils.RayIntersectsCone(new Vector3(-5, 0, 0.8f), Vector3.UnitX, O, 1f, 2f, out _, out _));   // wider than it is there
    }
}

using System;
using System.Numerics;
using OpenFPS.Common;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// Whether an axis-aligned box and an oriented one share volume, and what the acoustic grid makes of
/// it — a cell inside a wall, straddling it, crossed by it, or clear. The grid is what occlusion
/// reads, so this is every wall's say in what you hear. Written for the survivors of the 2026-09-24
/// mutation run over GeometryUtils.
/// </summary>
public class BoxOverlapTests
{
    private static readonly Vector3 Unit2 = new(2, 2, 2);     // the axis-aligned box: [-1, 1] cubed
    private static Quaternion Yaw(float deg) => Quaternion.CreateFromYawPitchRoll(deg * MathF.PI / 180f, 0, 0);

    [Fact]
    public void OverlappingTouchingAndApart()
    {
        Assert.True(GeometryUtils.ObbIntersectsAabb(Vector3.Zero, Unit2, Vector3.Zero, Unit2, Quaternion.Identity));
        Assert.True(GeometryUtils.ObbIntersectsAabb(Vector3.Zero, Unit2, new Vector3(1.99f, 0, 0), Unit2, Quaternion.Identity));
        Assert.False(GeometryUtils.ObbIntersectsAabb(Vector3.Zero, Unit2, new Vector3(2.01f, 0, 0), Unit2, Quaternion.Identity));
        Assert.False(GeometryUtils.ObbIntersectsAabb(Vector3.Zero, Unit2, new Vector3(0, 2.01f, 0), Unit2, Quaternion.Identity));
        Assert.False(GeometryUtils.ObbIntersectsAabb(Vector3.Zero, Unit2, new Vector3(0, 0, -2.01f), Unit2, Quaternion.Identity));
        // An offset axis-aligned box: the separation is between the centres, not from the origin.
        Assert.True(GeometryUtils.ObbIntersectsAabb(new Vector3(10, 0, 0), Unit2, new Vector3(11.5f, 0, 0), Unit2, Quaternion.Identity));
        Assert.False(GeometryUtils.ObbIntersectsAabb(new Vector3(10, 0, 0), Unit2, new Vector3(7.5f, 0, 0), Unit2, Quaternion.Identity));
    }

    /// <summary>A box turned 45 degrees off a cube's corner, clear of it along its own diagonal
    /// axis although its shadow on the world axes overlaps the cube's.</summary>
    [Fact]
    public void ApartAlongTheTurnedBoxsOwnAxis()
    {
        Assert.False(GeometryUtils.ObbIntersectsAabb(Vector3.Zero, Unit2, new Vector3(2.2f, 0, 2.2f), Unit2, Yaw(45)));
        Assert.True(GeometryUtils.ObbIntersectsAabb(Vector3.Zero, Unit2, new Vector3(1.6f, 0, 1.6f), Unit2, Yaw(45)));
        // ...and the same against a box that is itself the long one.
        var longSize = new Vector3(20f, 2f, 2f);
        // Its long axis passes 0.87 m from the cube's centre at 80 degrees (2.5 m at 60, a miss).
        Assert.True(GeometryUtils.ObbIntersectsAabb(Vector3.Zero, Unit2, new Vector3(0, 0, 5f), longSize, Yaw(80)));
        Assert.False(GeometryUtils.ObbIntersectsAabb(Vector3.Zero, Unit2, new Vector3(0, 0, 5f), longSize, Yaw(60)));
    }

    /// <summary>
    /// Skewed in three dimensions, where the edge-against-edge axes decide it: thousands of random
    /// pairs, each asserted only where an independent test is certain — a separating direction found
    /// by sampling means they miss, a point found inside both means they meet.
    /// </summary>
    [Fact]
    public void SkewedBoxesAgreeWithAnIndependentTest()
    {
        var rng = new Random(20260925);
        int apart = 0, meet = 0;
        for (int n = 0; n < 4000; n++)
        {
            var size = new Vector3(0.3f + 3f * (float)rng.NextDouble(), 0.3f + 3f * (float)rng.NextDouble(), 0.3f + 3f * (float)rng.NextDouble());
            var rot = Quaternion.Normalize(new Quaternion((float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f,
                                                          (float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f));
            var centre = new Vector3((float)rng.NextDouble() * 6 - 3, (float)rng.NextDouble() * 6 - 3, (float)rng.NextDouble() * 6 - 3);
            bool said = GeometryUtils.ObbIntersectsAabb(Vector3.Zero, Unit2, centre, size, rot);
            if (Separated(centre, size, rot, rng)) { Assert.False(said, $"case {n}: separated, said they meet"); apart++; }
            else if (SharedPoint(centre, size, rot)) { Assert.True(said, $"case {n}: a point in both, said they miss"); meet++; }
        }
        Assert.True(apart > 500 && meet > 500, $"apart {apart}, meet {meet}");
    }

    private static Vector3[] Corners(Vector3 c, Vector3 size, Quaternion r)
    {
        var h = size / 2f; var pts = new Vector3[8]; int k = 0;
        for (int x = -1; x <= 1; x += 2) for (int y = -1; y <= 1; y += 2) for (int z = -1; z <= 1; z += 2)
            pts[k++] = c + Vector3.Transform(new Vector3(x * h.X, y * h.Y, z * h.Z), r);
        return pts;
    }

    private static bool Separated(Vector3 c, Vector3 size, Quaternion r, Random rng)
    {
        var a = Corners(Vector3.Zero, Unit2, Quaternion.Identity); var b = Corners(c, size, r);
        for (int k = 0; k < 400; k++)
        {
            var axis = Vector3.Normalize(new Vector3((float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f));
            float amin = float.MaxValue, amax = float.MinValue, bmin = float.MaxValue, bmax = float.MinValue;
            foreach (var p in a) { float d = Vector3.Dot(p, axis); amin = MathF.Min(amin, d); amax = MathF.Max(amax, d); }
            foreach (var p in b) { float d = Vector3.Dot(p, axis); bmin = MathF.Min(bmin, d); bmax = MathF.Max(bmax, d); }
            if (amax < bmin - 1e-3f || bmax < amin - 1e-3f) return true;
        }
        return false;
    }

    private static bool SharedPoint(Vector3 c, Vector3 size, Quaternion r)
    {
        var h = size / 2f;
        for (int i = 0; i <= 8; i++) for (int j = 0; j <= 8; j++) for (int k = 0; k <= 8; k++)
        {
            var local = new Vector3(h.X * (i / 4f - 1f), h.Y * (j / 4f - 1f), h.Z * (k / 4f - 1f)) * 0.999f;
            var p = c + Vector3.Transform(local, r);
            if (MathF.Abs(p.X) < 1f && MathF.Abs(p.Y) < 1f && MathF.Abs(p.Z) < 1f) return true;
        }
        return false;
    }

    // ── What the grid makes of it ─────────────────────────────────────────────────────────────

    [Fact]
    public void ACellInAWallAcrossItOrClear()
    {
        var wallSize = new Vector3(10f, 3f, 0.4f);
        Assert.Equal(BoxContainment.FullyInside,
            GeometryUtils.GetBoxContainmentInOBB(new Vector3(0, 1.5f, 0), new Vector3(0.2f), new Vector3(0, 1.5f, 0), wallSize, Quaternion.Identity));
        Assert.Equal(BoxContainment.Partial,
            GeometryUtils.GetBoxContainmentInOBB(new Vector3(0, 1.5f, 0.2f), new Vector3(0.2f), new Vector3(0, 1.5f, 0), wallSize, Quaternion.Identity));
        Assert.Equal(BoxContainment.Outside,
            GeometryUtils.GetBoxContainmentInOBB(new Vector3(0, 1.5f, 3f), new Vector3(1f), new Vector3(0, 1.5f, 0), wallSize, Quaternion.Identity));
        // Every corner of the cell is tested: a cell whose one corner pokes into the wall is across it.
        foreach (var dx in new[] { -1f, 1f })
        foreach (var dy in new[] { -1f, 1f })
        foreach (var dz in new[] { -1f, 1f })
        {
            var cell = new Vector3(5.4f * dx, 1.5f + 1.9f * dy, 0.6f * dz);
            Assert.Equal(BoxContainment.Partial,
                GeometryUtils.GetBoxContainmentInOBB(cell, new Vector3(1f), new Vector3(0, 1.5f, 0), wallSize, Quaternion.Identity));
        }
    }

    /// <summary>A cell with just one corner out of a tilted wall is across it, not in it: all eight
    /// count. Found by search, because a cell square to a square wall shows its corners in pairs.</summary>
    [Fact]
    public void OneCornerOutIsAcross()
    {
        var wallRot = Quaternion.CreateFromYawPitchRoll(0.6f, 0.4f, 0.3f);
        var wallSize = new Vector3(4f, 3f, 2f);
        var rng = new Random(7);
        int found = 0;
        for (int n = 0; n < 200000 && found < 20; n++)
        {
            var c = new Vector3((float)rng.NextDouble() * 5 - 2.5f, (float)rng.NextDouble() * 4 - 2f, (float)rng.NextDouble() * 3 - 1.5f);
            var cell = new Vector3(0.6f);
            int inside = 0;
            foreach (var k in Corners(c, cell, Quaternion.Identity))
                if (GeometryUtils.IsPointInOBB(k, Vector3.Zero, wallSize, wallRot)) inside++;
            if (inside != 7) continue;
            found++;
            Assert.Equal(BoxContainment.Partial, GeometryUtils.GetBoxContainmentInOBB(c, cell, Vector3.Zero, wallSize, wallRot));
        }
        Assert.True(found >= 10, $"only {found} cells with one corner out");
    }

    /// <summary>A long thin wall passing clean through a big cell contains none of its corners, and
    /// the cell is still across it — the lost regions along the racetrack.</summary>
    [Fact]
    public void AThinWallThroughABigCellIsAcrossIt()
    {
        Assert.Equal(BoxContainment.Partial,
            GeometryUtils.GetBoxContainmentInOBB(Vector3.Zero, new Vector3(100f), new Vector3(0, 0, 0), new Vector3(400f, 3f, 0.4f), Yaw(30)));
    }
}

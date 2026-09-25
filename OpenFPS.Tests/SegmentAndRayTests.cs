using System;
using System.Numerics;
using OpenFPS.Common;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// Segments and rays against boxes and spheres — whether an echo's leg or a bent path is blocked,
/// and where a ray first meets something. A box of 2 x 2 x 2 at the origin, a sphere of radius 1.
/// Written for the survivors of the 2026-09-24 mutation run over GeometryUtils.
/// </summary>
public class SegmentAndRayTests
{
    private static readonly Vector3 Two = new(2, 2, 2);

    [Fact]
    public void ASegmentMeetsABoxOnlyBetweenItsEnds()
    {
        Assert.True(GeometryUtils.LineIntersectsAABB(new Vector3(-5, 0, 0), new Vector3(5, 0, 0), Vector3.Zero, Two));
        Assert.False(GeometryUtils.LineIntersectsAABB(new Vector3(-5, 0, 0), new Vector3(-1.5f, 0, 0), Vector3.Zero, Two));  // stops short
        Assert.False(GeometryUtils.LineIntersectsAABB(new Vector3(1.5f, 0, 0), new Vector3(5, 0, 0), Vector3.Zero, Two));    // starts beyond
        Assert.True(GeometryUtils.LineIntersectsAABB(new Vector3(-0.5f, 0, 0), new Vector3(0.5f, 0, 0), Vector3.Zero, Two)); // wholly inside
        Assert.True(GeometryUtils.LineIntersectsAABB(new Vector3(-5, 0, 0), new Vector3(-0.9f, 0, 0), Vector3.Zero, Two));   // ends inside
        // Beside it on each axis.
        Assert.False(GeometryUtils.LineIntersectsAABB(new Vector3(-5, 1.5f, 0), new Vector3(5, 1.5f, 0), Vector3.Zero, Two));
        Assert.False(GeometryUtils.LineIntersectsAABB(new Vector3(-5, 0, -1.5f), new Vector3(5, 0, -1.5f), Vector3.Zero, Two));
        Assert.False(GeometryUtils.LineIntersectsAABB(new Vector3(1.5f, -5, 0), new Vector3(1.5f, 5, 0), Vector3.Zero, Two));
        // Diagonal, clipping a corner, and passing just by it.
        Assert.True(GeometryUtils.LineIntersectsAABB(new Vector3(-3, 0, 1.5f), new Vector3(1.5f, 0, -3), Vector3.Zero, Two));   // x + z = -1.5 crosses the corner
        Assert.False(GeometryUtils.LineIntersectsAABB(new Vector3(-3, 0, -0.5f), new Vector3(-0.5f, 0, -3), Vector3.Zero, Two));
        // An offset box.
        Assert.True(GeometryUtils.LineIntersectsAABB(new Vector3(5, 5, -5), new Vector3(5, 5, 5), new Vector3(5, 5, 0), Two));
        Assert.False(GeometryUtils.LineIntersectsAABB(new Vector3(-5, 0, 0), new Vector3(5, 0, 0), new Vector3(5, 5, 0), Two));
    }

    /// <summary>A segment parallel to a face is in only if it lies within that slab.</summary>
    [Fact]
    public void ParallelToAFace()
    {
        foreach (var (axis, other) in new[] { (Vector3.UnitX, Vector3.UnitY), (Vector3.UnitY, Vector3.UnitZ), (Vector3.UnitZ, Vector3.UnitX) })
        {
            Assert.True(GeometryUtils.LineIntersectsAABB(-5 * axis + 0.5f * other, 5 * axis + 0.5f * other, Vector3.Zero, Two));
            Assert.False(GeometryUtils.LineIntersectsAABB(-5 * axis + 1.5f * other, 5 * axis + 1.5f * other, Vector3.Zero, Two));
            Assert.False(GeometryUtils.LineIntersectsAABB(-5 * axis - 1.5f * other, 5 * axis - 1.5f * other, Vector3.Zero, Two));
        }
    }

    [Fact]
    public void ASegmentAgainstATurnedBox()
    {
        var rot = Quaternion.CreateFromYawPitchRoll(MathF.PI / 4, 0, 0);
        var wall = new Vector3(6f, 3f, 0.4f);
        // Straight through the turned wall's middle.
        Assert.True(GeometryUtils.LineIntersectsOBB(new Vector3(-3, 0, 3), new Vector3(3, 0, -3), Vector3.Zero, wall, rot));
        // Along the wall's own length, just off its face: clear.
        var along = Vector3.Transform(Vector3.UnitX, rot);
        var off = Vector3.Transform(Vector3.UnitZ, rot) * 0.5f;
        Assert.False(GeometryUtils.LineIntersectsOBB(-4 * along + off, 4 * along + off, Vector3.Zero, wall, rot));
        // The same segment unturned would have hit it: the rotation is honoured.
        Assert.False(GeometryUtils.LineIntersectsOBB(new Vector3(-3, 0, 1.5f), new Vector3(-1.5f, 0, 3), Vector3.Zero, wall, rot));
        Assert.True(GeometryUtils.LineIntersectsOBB(new Vector3(10, 0, 3), new Vector3(10, 0, -3), new Vector3(10, 0, 0), wall, rot));
    }

    [Fact]
    public void ARayMeetsABoxAtItsNearFace()
    {
        Assert.True(GeometryUtils.RayIntersectsAABB(new Vector3(-5, 0, 0), Vector3.UnitX, Vector3.Zero, Two, out float d));
        Assert.Equal(4f, d, 4);
        Assert.True(GeometryUtils.RayIntersectsAABB(new Vector3(0, 7, 0.5f), -Vector3.UnitY, Vector3.Zero, Two, out d));
        Assert.Equal(6f, d, 4);
        Assert.True(GeometryUtils.RayIntersectsAABB(new Vector3(0.3f, 0.2f, 9), -Vector3.UnitZ, Vector3.Zero, Two, out d));
        Assert.Equal(8f, d, 4);
        Assert.True(GeometryUtils.RayIntersectsAABB(Vector3.Zero, Vector3.UnitX, Vector3.Zero, Two, out d));    // from inside
        Assert.Equal(0f, d);
        Assert.False(GeometryUtils.RayIntersectsAABB(new Vector3(5, 0, 0), Vector3.UnitX, Vector3.Zero, Two, out _));  // away
        Assert.False(GeometryUtils.RayIntersectsAABB(new Vector3(-5, 1.5f, 0), Vector3.UnitX, Vector3.Zero, Two, out _));
        Assert.False(GeometryUtils.RayIntersectsAABB(new Vector3(-5, 0, 1.5f), Vector3.UnitX, Vector3.Zero, Two, out _));
        Assert.False(GeometryUtils.RayIntersectsAABB(new Vector3(1.5f, 5, 0), -Vector3.UnitY, Vector3.Zero, Two, out _));
        Assert.False(GeometryUtils.RayIntersectsAABB(new Vector3(0, 1.5f, 5), -Vector3.UnitZ, Vector3.Zero, Two, out _));
        // An offset box, turned.
        var rot = Quaternion.CreateFromYawPitchRoll(MathF.PI / 2, 0, 0);
        Assert.True(GeometryUtils.RayIntersectsOBB(new Vector3(10, 0, -5), Vector3.UnitZ, new Vector3(10, 0, 0), new Vector3(6, 2, 0.4f), rot, out d));
        Assert.Equal(2f, d, 3);            // turned a quarter, the 6 m side lies along Z: its face at z = -3
    }

    [Fact]
    public void ASegmentMeetsASphereWhereItCrossesTheSurface()
    {
        Assert.True(GeometryUtils.LineIntersectsSphere(new Vector3(-5, 0, 0), new Vector3(5, 0, 0), Vector3.Zero, 1f));
        Assert.True(GeometryUtils.LineIntersectsSphere(new Vector3(-5, 0, 0), Vector3.Zero, Vector3.Zero, 1f));         // ends inside
        Assert.True(GeometryUtils.LineIntersectsSphere(Vector3.Zero, new Vector3(5, 0, 0), Vector3.Zero, 1f));          // starts inside
        Assert.False(GeometryUtils.LineIntersectsSphere(new Vector3(-5, 0, 0), new Vector3(-1.5f, 0, 0), Vector3.Zero, 1f));
        Assert.False(GeometryUtils.LineIntersectsSphere(new Vector3(1.5f, 0, 0), new Vector3(5, 0, 0), Vector3.Zero, 1f));
        Assert.False(GeometryUtils.LineIntersectsSphere(new Vector3(-5, 1.2f, 0), new Vector3(5, 1.2f, 0), Vector3.Zero, 1f));
        Assert.True(GeometryUtils.LineIntersectsSphere(new Vector3(-5, 0.9f, 0), new Vector3(5, 0.9f, 0), Vector3.Zero, 1f));
        Assert.True(GeometryUtils.LineIntersectsSphere(new Vector3(5, 5, 0), new Vector3(15, 5, 0), new Vector3(10, 5, 0), 1f));
        // Wholly inside crosses no surface: this is a surface test.
        Assert.False(GeometryUtils.LineIntersectsSphere(new Vector3(-0.5f, 0, 0), new Vector3(0.5f, 0, 0), Vector3.Zero, 1f));
    }

    [Fact]
    public void ARayThroughASphere()
    {
        Assert.True(GeometryUtils.RayIntersectsSphere(new Vector3(-5, 0, 0), Vector3.UnitX, Vector3.Zero, 1f, out float en, out float ex));
        Assert.Equal(4f, en, 4); Assert.Equal(6f, ex, 4);
        // Off-centre: half a metre out, the chord is sqrt(3) long.
        Assert.True(GeometryUtils.RayIntersectsSphere(new Vector3(-5, 0.5f, 0), Vector3.UnitX, Vector3.Zero, 1f, out en, out ex));
        Assert.Equal(5f - MathF.Sqrt(0.75f), en, 4); Assert.Equal(5f + MathF.Sqrt(0.75f), ex, 4);
        Assert.True(GeometryUtils.RayIntersectsSphere(Vector3.Zero, Vector3.UnitX, Vector3.Zero, 1f, out en, out ex));   // from inside
        Assert.Equal(0f, en); Assert.Equal(1f, ex, 4);
        Assert.False(GeometryUtils.RayIntersectsSphere(new Vector3(5, 0, 0), Vector3.UnitX, Vector3.Zero, 1f, out _, out _));   // away
        Assert.False(GeometryUtils.RayIntersectsSphere(new Vector3(-5, 1.5f, 0), Vector3.UnitX, Vector3.Zero, 1f, out _, out _));
        Assert.True(GeometryUtils.RayIntersectsSphere(new Vector3(5, 5, -5), Vector3.UnitZ, new Vector3(5, 5, 0), 1f, out en, out _));
        Assert.Equal(4f, en, 4);
    }
}

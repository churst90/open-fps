using System.Numerics;
using OpenFPS.Common;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// The overlap walking pushes a body out of a wall with: a standing cylinder against a box, the
/// depth and the direction out. A box [-1, 1] cubed; a body of radius 0.3 and height 1.6.
/// Written for the survivors of the 2026-09-24 mutation run over GeometryUtils.
/// </summary>
public class CylinderOverlapTests
{
    private static readonly Vector3 Min = new(-1, -1, -1), Max = new(1, 1, 1);
    private const float R = 0.3f, H = 1.6f;
    private static GeometryUtils.CollisionResult At(float x, float y, float z) => GeometryUtils.GetCylinderAABBOverlap(Min, Max, new Vector3(x, y, z), R, H);

    [Fact]
    public void ABoxAboveOrBelowTheBodyIsNotTouched()
    {
        Assert.False(At(1.1f, 1.81f, 0).IsColliding);     // body 1.01..2.61: clear above the box's top at 1
        Assert.False(At(1.1f, -1.81f, 0).IsColliding);    // body -2.61..-1.01, clear below
        Assert.True(At(1.1f, 1.79f, 0).IsColliding);      // its bottom just inside the box's top
        Assert.True(At(1.1f, -1.79f, 0).IsColliding);
    }

    [Fact]
    public void BesideAFaceItIsPushedStraightOut()
    {
        var hit = At(1.2f, 0, 0);
        Assert.True(hit.IsColliding);
        Assert.Equal(0.1f, hit.Penetration, 4);
        Assert.Equal(Vector3.UnitX, hit.Normal);
        hit = At(0, 0, -1.25f);
        Assert.Equal(0.05f, hit.Penetration, 4);
        Assert.Equal(-Vector3.UnitZ, hit.Normal);
        Assert.False(At(1.31f, 0, 0).IsColliding);        // just past the radius
        Assert.False(At(0, 0, 1.31f).IsColliding);
    }

    [Fact]
    public void OffACornerItIsPushedDiagonally()
    {
        var hit = At(1.1f, 0, 1.1f);
        Assert.True(hit.IsColliding);
        float d = 0.1f * MathF.Sqrt(2f);
        Assert.Equal(R - d, hit.Penetration, 4);
        Assert.Equal(1f / MathF.Sqrt(2f), hit.Normal.X, 4);
        Assert.Equal(1f / MathF.Sqrt(2f), hit.Normal.Z, 4);
        Assert.Equal(0f, hit.Normal.Y);
    }

    /// <summary>A body whose centre has got inside the box is pushed away from the box's centre by
    /// its radius — and along +X from the very middle, where there is no away.</summary>
    [Fact]
    public void FromInsideItIsPushedAwayFromTheMiddle()
    {
        var hit = At(0.5f, 0, 0);
        Assert.True(hit.IsColliding);
        Assert.Equal(R, hit.Penetration);
        Assert.Equal(Vector3.UnitX, hit.Normal);
        hit = At(0, 0, -0.4f);
        Assert.Equal(-Vector3.UnitZ, hit.Normal);
        hit = At(0, 0, 0);
        Assert.Equal(Vector3.UnitX, hit.Normal);
        // An offset box: the middle is the box's, not the origin.
        var off = GeometryUtils.GetCylinderAABBOverlap(new Vector3(9, -1, -1), new Vector3(11, 1, 1), new Vector3(9.5f, 0, 0), R, H);
        Assert.Equal(-Vector3.UnitX, off.Normal);
    }
}

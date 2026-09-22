using System.Numerics;
using OpenFPS.Common;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// Players read and type x east, y north, z height; the engine is Y-up. What C says must be what
/// /tp takes, or typing a spoken position back puts you twenty-seven metres in the air.
/// </summary>
public class PlayerCoordinatesTests
{
    [Fact]
    public void HeightIsSpokenLast()
    {
        // Engine: 150 east, 0.1 up, 27 north.
        Assert.Equal("150.0, 27.0, 0.1", PlayerCoordinates.Format(new Vector3(150f, 0.1f, 27f)));
    }

    [Fact]
    public void WhatIsSpokenCanBeTypedBack()
    {
        var at = new Vector3(-40f, 0.95f, -170f);
        var parts = PlayerCoordinates.Format(at).Split(", ");
        var back = PlayerCoordinates.ToWorld(float.Parse(parts[0]), float.Parse(parts[1]), float.Parse(parts[2]));
        Assert.Equal(at.X, back.X, 1);
        Assert.Equal(at.Y, back.Y, 1);
        Assert.Equal(at.Z, back.Z, 1);
    }
}

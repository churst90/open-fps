using System;
using System.Collections.Generic;
using System.Numerics;
using OpenFPS.Common;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// The enclosure measure and the room equation, held to the edges Stryker found untested
/// (2026-09-24): no scene at all, the two-bounce rule over the reverberant range, the two instruments
/// agreeing, and the surface-area fallback the room equation uses when nothing measured one.
/// </summary>
public class EnclosureMutationTests
{
    private static readonly Quaternion Q = Quaternion.Identity;

    private static List<Enclosure.Solid> SealedRoom() => new()
    {
        new(new Vector3(0, -0.25f, 0), new Vector3(11, 0.5f, 11), Q, "Concrete"),
        new(new Vector3(0, 4.25f, 0),  new Vector3(11, 0.5f, 11), Q, "Concrete"),
        new(new Vector3(0, 2, 5.25f),  new Vector3(11, 4, 0.5f), Q, "Concrete"),
        new(new Vector3(0, 2, -5.25f), new Vector3(11, 4, 0.5f), Q, "Concrete"),
        new(new Vector3(5.25f, 2, 0),  new Vector3(0.5f, 4, 11), Q, "Concrete"),
        new(new Vector3(-5.25f, 2, 0), new Vector3(0.5f, 4, 11), Q, "Concrete"),
    };

    /// <summary>No scene, whether an empty list or none at all, is open air: nothing comes back.</summary>
    [Fact]
    public void NoSceneMeasuresAsOpenAir()
    {
        Assert.Equal(0f, Enclosure.Measure(Vector3.Zero, null!));
        Assert.Equal(0f, Enclosure.Measure(Vector3.Zero, new List<Enclosure.Solid>()));
    }

    /// <summary>
    /// <see cref="Enclosure.Measure"/> and <see cref="Enclosure.Look"/> cast the same sphere of rays and
    /// the same two bounces; in a sealed room — where nothing crosses into a more open place, so
    /// Look's boundary test drops no ray — they must read the same enclosure, to the last bit.
    /// </summary>
    [Fact]
    public void MeasureAndLookAgreeInASealedRoom()
    {
        var room = SealedRoom();
        var at = new Vector3(0.3f, 1.6f, -0.7f);
        float measured = Enclosure.Measure(at, room);
        Assert.InRange(measured, 0.9f, 0.99f);
        Assert.Equal(Enclosure.Look(at, room).Enclosure, measured, 6);
    }

    /// <summary>
    /// Two walls further apart than <see cref="Enclosure.ReverberantRangeMetres"/> are not a room. One
    /// is 10 m off and one 55 m off, 65 m apart: a ray's second leg, from either wall to the other, is
    /// always longer than 60 m, so no ray survives two bounces and the enclosure is exactly nothing —
    /// although a ray leaving from where the listener stands WOULD reach the 55 m wall.
    /// </summary>
    [Fact]
    public void TwoWallsFurtherApartThanTheReverberantRangeAreNotARoom()
    {
        var walls = new List<Enclosure.Solid>
        {
            new(new Vector3(10.5f, 0, 0), new Vector3(1f, 200f, 200f), Q, "Concrete"),
            new(new Vector3(-55.5f, 0, 0), new Vector3(1f, 200f, 200f), Q, "Concrete"),
        };
        Assert.Equal(0f, Enclosure.Measure(Vector3.Zero, walls));
    }

    /// <summary>
    /// The second leg leaves from the FRONT of the face it struck. Maps overlap their boxes, so a wall
    /// can have another box buried in it a few millimetres behind its face; that is not a second
    /// surface the sound can reach, and a lone wall with a 4 mm slab inside it is still no room.
    /// </summary>
    [Fact]
    public void ABoxBuriedInsideAWallIsNotASecondSurface()
    {
        var wall = new List<Enclosure.Solid>
        {
            new(new Vector3(10.5f, 0, 0), new Vector3(1f, 200f, 200f), Q, "Concrete"),      // face at x = 10
            new(new Vector3(10.004f, 0, 0), new Vector3(0.004f, 200f, 200f), Q, "Concrete"), // 2-6 mm inside it
        };
        Assert.Equal(0f, Enclosure.Measure(Vector3.Zero, wall));
    }

    /// <summary>With only the mean free path known, the survey assumes a cube: S = 13.5·MFP².</summary>
    [Fact]
    public void ASurveyWithoutAMeasuredAreaAssumesACube()
    {
        var s = new Enclosure.Survey(0.5f, 0.2f, 4f, 0.1f, 0.1f, 0.1f);
        Assert.Equal(13.5f * 16f, s.SurfaceAreaSquareMetres, 3);
        Assert.Equal(Vector3.Zero, s.ReturnDirection);
        Assert.Equal(0f, s.Anisotropy);
    }

    /// <summary>
    /// The room equation's surface-area fallback. A measured area of one square metre or less is no
    /// measurement, and the cube's 13.5·MFP² stands in: reverberant/direct = 16π r² e / (S (1 - e)),
    /// with S = 216 m² for a 4 m mean free path. A real measurement is used as it is.
    /// </summary>
    [Fact]
    public void TheRoomEquationFallsBackToACubeWhenNoAreaWasMeasured()
    {
        float e = 0.5f, mfp = 4f, r = 2f;
        float cube = 16f * MathF.PI * r * r * e / (13.5f * mfp * mfp * (1f - e));

        Assert.Equal(cube, Enclosure.ReverberantToDirectPower(e, mfp, r), 4);
        Assert.Equal(cube, Enclosure.ReverberantToDirectPower(e, mfp, 0f, r), 4);
        Assert.Equal(cube, Enclosure.ReverberantToDirectPower(e, mfp, 1f, r), 4);

        float measured = 16f * MathF.PI * r * r * e / (1000f * (1f - e));
        Assert.Equal(measured, Enclosure.ReverberantToDirectPower(e, mfp, 1000f, r), 5);
    }

    /// <summary>An enclosure at the one-in-a-million floor is open air: no reverberant field at all.</summary>
    [Fact]
    public void AnEnclosureAtTheFloorHasNoReverberantField()
    {
        Assert.Equal(0f, Enclosure.ReverberantToDirectPower(1e-6f, 4f, 200f, 10f));
        Assert.True(Enclosure.ReverberantToDirectPower(1e-5f, 4f, 200f, 10f) > 0f);
    }
}

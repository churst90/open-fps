using System;
using System.Collections.Generic;
using System.Numerics;
using OpenFPS.Common;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// How big the room is, measured from inside it — and why assuming it was a cube was audible.
///
/// The room equation needs the surface area: the reverberant field a source raises goes as 1/(S·ā),
/// because that is how much the room absorbs per bounce. <see cref="Enclosure"/> used to substitute
/// S ≈ 13.5·MFP², which is exact for a cube and hopeless for anything flat or long — and a city is
/// made of flat and long things. A car park 21 by 28 metres and 2.5 high has 1,390 m² of surface; the
/// cube form gives it 246. Five times too little absorption is nine decibels too much reverberation,
/// and every footstep in that garage went to the master limiter's ceiling and stayed there.
///
/// It is measured now, from the same sphere of rays that measures everything else: the solid angle a
/// patch of wall subtends is dS·cosθ/d², so ∮(d²/cosθ)dω is the surface area of any convex room seen
/// from any point inside it. These hold that it really is — for a cube, a slab and a tube, from the
/// middle and from up against a wall.
/// </summary>
public class RoomSizeTests
{
    public RoomSizeTests() => AcousticRegistry.Initialize();

    /// <summary>A hollow box of a given size, as six slabs of concrete a quarter of a metre thick.</summary>
    private static List<Enclosure.Solid> Box(float x, float y, float z, string material = "Concrete")
    {
        const float t = 0.25f;
        float hx = x / 2, hy = y / 2, hz = z / 2;
        return new List<Enclosure.Solid>
        {
            new(new Vector3(0, -hy - t / 2, 0), new Vector3(x + 2 * t, t, z + 2 * t), Quaternion.Identity, material),
            new(new Vector3(0, +hy + t / 2, 0), new Vector3(x + 2 * t, t, z + 2 * t), Quaternion.Identity, material),
            new(new Vector3(0, 0, -hz - t / 2), new Vector3(x + 2 * t, y, t), Quaternion.Identity, material),
            new(new Vector3(0, 0, +hz + t / 2), new Vector3(x + 2 * t, y, t), Quaternion.Identity, material),
            new(new Vector3(-hx - t / 2, 0, 0), new Vector3(t, y, z), Quaternion.Identity, material),
            new(new Vector3(+hx + t / 2, 0, 0), new Vector3(t, y, z), Quaternion.Identity, material),
        };
    }

    private static float TrueSurface(float x, float y, float z) => 2f * (x * y + y * z + z * x);

    [Theory]
    // shape                              x     y     z    where the listener stands
    [InlineData("a cube",                8f,   8f,   8f,  0f, 0f, 0f)]
    [InlineData("a cube, against a wall", 8f,  8f,   8f,  3.4f, 0f, 3.4f)]
    [InlineData("a car park (slab)",     21f,  2.5f, 28f, 0f, -0.15f, 0f)]
    [InlineData("a car park, at a wall", 21f,  2.5f, 28f, 9f, -0.15f, 12f)]
    [InlineData("a corridor (tube)",      2.2f, 2.7f, 39f, 0f, 0f, 0f)]
    [InlineData("a tunnel",              12f,  5.5f, 30f, 0f, -1f, 0f)]
    [InlineData("a hall",                30f, 12f,  30f, 0f, -4f, 0f)]
    public void TheSurfaceIsMeasuredNotAssumed(string shape, float x, float y, float z,
                                               float px, float py, float pz)
    {
        var survey = Enclosure.Look(new Vector3(px, py, pz), Box(x, y, z));

        float truth = TrueSurface(x, y, z);
        float measured = survey.SurfaceAreaSquareMetres;
        float cube = 13.5f * survey.MeanFreePathMetres * survey.MeanFreePathMetres;

        // A long tube is the hard case and comes back about a third low: 192 rays cannot resolve the
        // far ends of a corridor, which subtend almost no solid angle and carry a great deal of area.
        // That is 1.6 dB too much reverberation in a corridor, against the 7.6 dB the cube form was
        // wrong by in a car park — so it is the limit of this measure, stated rather than hidden.
        Assert.True(measured > truth * 0.6f && measured < truth * 1.45f,
            $"{shape}: measured {measured:F0} m^2 against {truth:F0} true");

        // ...and for anything that is not a cube, the assumption it replaced was wrong by a lot.
        if (MathF.Abs(x - y) > 1f || MathF.Abs(y - z) > 1f)
            Assert.True(cube < truth * 0.6f,
                $"{shape}: the cube form gives {cube:F0} m^2 against {truth:F0} true — it was supposed to be wrong here");
    }

    /// <summary>
    /// The one that mattered: your own footsteps in a bare concrete car park.
    ///
    /// The classical room equation says the reverberant field at 1.6 m is (r/r_c)² with
    /// r_c = sqrt(S·ā/(1−ā)/16π) — about +5 dB of power for this room, an amplitude ratio near 1.7.
    /// The cube form said 3.0, nine decibels of power too much, and that is what clipped the master.
    /// </summary>
    [Fact]
    public void ACarParkAnswersAboutAsLoudlyAsItsOwnSurfaceAllows()
    {
        var survey = Enclosure.Look(new Vector3(0, -0.15f, 0), Box(21f, 2.5f, 28f));

        float measured = MathF.Sqrt(Enclosure.ReverberantToDirectPower(
            survey.Enclosure, survey.MeanFreePathMetres, survey.SurfaceAreaSquareMetres, 1.6f));
        float assumed = MathF.Sqrt(Enclosure.ReverberantToDirectPower(
            survey.Enclosure, survey.MeanFreePathMetres, 1.6f));

        Assert.InRange(measured, 0.6f, 2.2f);
        Assert.True(assumed > measured * 2f,
            $"the cube form gave {assumed:P0} where the measured surface gives {measured:P0}");
    }

    /// <summary>
    /// Open ground raises no reverberant field, whatever else changes. The floor of the ladder.
    /// </summary>
    [Fact]
    public void OpenGroundStaysDry()
    {
        var ground = new List<Enclosure.Solid>
        {
            new(new Vector3(0, -0.2f, 0), new Vector3(200f, 0.2f, 200f), Quaternion.Identity, "Asphalt"),
        };
        var survey = Enclosure.Look(new Vector3(0, 1.6f, 0), ground);

        float send = MathF.Sqrt(Enclosure.ReverberantToDirectPower(
            survey.Enclosure, survey.MeanFreePathMetres, survey.SurfaceAreaSquareMetres, 1.6f));
        Assert.True(send < 0.25f, $"a field sent {send:P0} of every sound to a reverb bus");
    }

    /// <summary>
    /// A listener does not change the room by standing somewhere else in it. The surface area is a
    /// fact about the room, so two places in the same car park have to agree — which the old measure
    /// (the mean distance to the nearest surface) did not: it read 4.2 m from the middle and 3.0 m
    /// against a wall, which is 3 dB of reverberation appearing because somebody walked.
    /// </summary>
    [Fact]
    public void TheRoomIsTheSameSizeWhereverYouStandInIt()
    {
        var box = Box(21f, 2.5f, 28f);
        float middle = Enclosure.Look(new Vector3(0, -0.15f, 0), box).SurfaceAreaSquareMetres;
        float corner = Enclosure.Look(new Vector3(9f, -0.15f, 12f), box).SurfaceAreaSquareMetres;

        float db = 20f * MathF.Log10(middle / corner);
        Assert.True(MathF.Abs(db) < 4f,
            $"the same car park measured {middle:F0} m^2 from the middle and {corner:F0} from a corner ({db:+0.0;-0.0} dB of reverb)");
    }
}

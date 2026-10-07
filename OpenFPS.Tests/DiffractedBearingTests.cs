using System.Numerics;
using OpenFPS.Common;

namespace OpenFPS.Tests;

/// <summary>
/// Where a blocked source is heard from: the edge the detour crosses, from
/// <see cref="Diffraction.PathDifferenceAroundBox(Vector3, Vector3, Quaternion, Vector3, Vector3, out float, out Vector3)"/>,
/// exact and continuous, not the 7.3 m probe grid that made a near car seem to stop on the speedway
/// (docs/CLIENT_NOTES.md, "The bearing follows the level"). A kerb, a stand and a doorway are one rule.
/// </summary>
public class DiffractedBearingTests
{
    private static float BearingShiftDegrees(Vector3 listener, Vector3 source, Vector3 edge)
    {
        var toSource = Vector3.Normalize(source - listener);
        var toEdge = Vector3.Normalize(edge - listener);
        return MathF.Acos(Math.Clamp(Vector3.Dot(toSource, toEdge), -1f, 1f)) * 180f / MathF.PI;
    }

    /// <summary>The reported fault: a car behind a 0.9 m pit wall breaks the sight line, but the route over
    /// the top leaves the straight line by a millimetre, so its bearing is the car's own.</summary>
    [Fact]
    public void ACarBehindAKneeHighWallIsStillHeardWhereItIs()
    {
        var centre = new Vector3(0f, 0.45f, 0f);
        var size = new Vector3(60f, 0.9f, 0.3f);
        var source = new Vector3(0f, 0.3f, -20f);
        var listener = new Vector3(0f, 1.7f, 45f);

        Assert.True(Diffraction.PathDifferenceAroundBox(centre, size, Quaternion.Identity,
                                                        source, listener, out float delta, out Vector3 edge));

        Assert.Equal(0.9f, edge.Y, 2);

        float shift = BearingShiftDegrees(listener, source, edge);
        Assert.True(shift < 1.0f,
            $"a car behind a knee-high wall must not appear to move; it shifted {shift:F1} degrees. " +
            "The probe-grid bearing this replaced moved it by tens of degrees and then held it there.");

        // Still audible: the level half of the same measurement.
        Assert.True(delta < 0.05f, $"path difference {delta:F3} m — a 0.9 m wall is a few centimetres of detour");
    }

    /// <summary>A source behind a grandstand is heard from its top front corner, a large audible angle: a
    /// rule that never moved a bearing would be as wrong as one that always did.</summary>
    [Fact]
    public void ASourceBehindAStandIsHeardFromItsEdge()
    {
        var centre = new Vector3(0f, 6f, 0f);
        var size = new Vector3(40f, 12f, 16f);
        var source = new Vector3(0f, 0.6f, -30f);
        var listener = new Vector3(0f, 1.7f, 30f);

        Assert.True(Diffraction.PathDifferenceAroundBox(centre, size, Quaternion.Identity,
                                                        source, listener, out float delta, out Vector3 edge));

        // The top edge of the face turned towards the listener.
        Assert.Equal(12f, edge.Y, 1);
        Assert.Equal(8f, edge.Z, 1);

        Assert.True(BearingShiftDegrees(listener, source, edge) > 15f,
            "a source behind a twelve-metre stand is heard from the stand, not through it");
        Assert.Equal(Diffraction.MaxInsertionLossDb, Diffraction.InsertionLossDb(delta, Diffraction.MidBandHz), 1);
    }

    /// <summary>The doorway in maps/default.json (the wood room's front wall, opening at x 6..8): square in
    /// front of the leaf, the megaphone inside is heard from the jamb.</summary>
    [Fact]
    public void ASourceThroughADoorwayIsHeardFromTheJamb()
    {
        var centre = new Vector3(4f, 2f, 10f);      // wall 303
        var size = new Vector3(4f, 4f, 0.5f);
        var source = new Vector3(4f, 1.5f, 15f);    // inside the room, behind the leaf
        var listener = new Vector3(4f, 1.6f, 5f);   // outside, square on

        Assert.True(Diffraction.PathDifferenceAroundBox(centre, size, Quaternion.Identity,
                                                        source, listener, out _, out Vector3 edge));

        // A jamb, not the top or the far end; the source is midway between the two, so either is right.
        Assert.True(MathF.Abs(edge.X - 6f) < 0.1f || MathF.Abs(edge.X - 2f) < 0.1f, $"edge {edge}");

        Assert.True(BearingShiftDegrees(listener, source, edge) > 20f,
            "standing in front of a wall with a door beside it, the sound comes from the door");
    }

    /// <summary>The way round is the way back: swapping source and listener cannot change the detour.</summary>
    [Theory]
    [InlineData(0f, 6f, 0f, 40f, 12f, 16f, 0f, 0.6f, -30f, 0f, 1.7f, 30f)]
    [InlineData(4f, 2f, 10f, 4f, 4f, 0.5f, 4f, 1.5f, 15f, 4f, 1.6f, 5f)]
    public void TheWayRoundIsTheSameInBothDirections(float cx, float cy, float cz, float sx, float sy, float sz,
                                                      float ax, float ay, float az, float bx, float by, float bz)
    {
        Vector3 centre = new(cx, cy, cz), size = new(sx, sy, sz), a = new(ax, ay, az), b = new(bx, by, bz);
        Assert.True(Diffraction.PathDifferenceAroundBox(centre, size, Quaternion.Identity, a, b, out float there));
        Assert.True(Diffraction.PathDifferenceAroundBox(centre, size, Quaternion.Identity, b, a, out float back));
        Assert.Equal(there, back, 2);
    }

    /// <summary>Over an 11 cm interior wall the route is the short one over its top.</summary>
    [Fact]
    public void OverAThinInteriorWallIsTheShortRoute()
    {
        var source = new Vector3(3f, 1.6f, 5f);
        var listener = new Vector3(0f, 1.6f, 0f);
        Assert.True(Diffraction.PathDifferenceAroundBox(new Vector3(0f, 1.5f, 3f), new Vector3(40f, 3f, 0.11f),
                                                        Quaternion.Identity, source, listener, out float over));
        Assert.True(over < 1f, $"detour {over:F2} m over a 3 m wall");
    }

    /// <summary>The crossing reported is the one the last leg leaves from: over a deep box the route
    /// climbs one edge and drops off the far one, and the entry corner would be the wrong place.</summary>
    [Fact]
    public void TheEdgeReportedIsTheOneNearestTheEar()
    {
        var centre = new Vector3(0f, 6f, 0f);
        var size = new Vector3(40f, 12f, 16f);
        var source = new Vector3(0f, 0.6f, -30f);
        var listener = new Vector3(0f, 1.7f, 30f);

        Assert.True(Diffraction.PathDifferenceAroundBox(centre, size, Quaternion.Identity,
                                                        source, listener, out _, out Vector3 edge));

        Assert.True(edge.Z > 0f,
            $"the crossing at z={edge.Z:F1} is on the source's side of the box; the ear is at z=+30");
    }

    /// <summary>The overload without the edge gives the same detour, so the knee-high-wall level fix holds.</summary>
    [Fact]
    public void TheLevelAnswerIsUnchanged()
    {
        var centre = new Vector3(0f, 0.45f, 0f);
        var size = new Vector3(60f, 0.9f, 0.3f);
        var source = new Vector3(0f, 0.3f, -20f);
        var listener = new Vector3(0f, 1.7f, 45f);

        Assert.True(Diffraction.PathDifferenceAroundBox(centre, size, Quaternion.Identity, source, listener, out float a));
        Assert.True(Diffraction.PathDifferenceAroundBox(centre, size, Quaternion.Identity, source, listener, out float b, out _));
        Assert.Equal(a, b, 6);
    }
    /// <summary>
    /// Every way round a box, not only the shortest: past a storey-high wall the top route runs into the
    /// slab above, and the jamb, a few centimetres longer, was never offered, so an open door let nothing
    /// round its corner (2026-09-30). The jamb comes with its crossings, to check against a hung leaf.
    /// </summary>
    [Fact]
    public void A_storey_wall_reports_the_jamb_as_well_as_the_top()
    {
        // The flat's wall at x = -18.7, 35 cm thick and 2.73 m high, from the doorway's edge north.
        var centre = new Vector3(-18.675f, 1.385f, -75.93f);
        var size = new Vector3(0.35f, 2.73f, 16.86f);
        var ear = new Vector3(-14f, 1.6f, -85f);
        var walker = new Vector3(-20f, 1.6f, -82f);
        var routes = new System.Collections.Generic.List<(float D, Vector3 SourceSide, Vector3 Edge)>();
        Assert.True(Diffraction.PathDifferenceAroundBox(centre, size, Quaternion.Identity, walker, ear,
                                                         out float shortest, out _, routes));
        var jamb = routes.FindAll(r => r.Edge.Z < -84.3f && MathF.Abs(r.Edge.Y - 1.6f) < 0.3f);
        Assert.NotEmpty(jamb);
        var best = jamb[0];
        foreach (var r in jamb) if (r.D < best.D) best = r;
        Assert.True(best.D < 1.0f, $"round the jamb {best.D:F2} m");
        Assert.True(best.D >= shortest);
        // The corridor corner at the wall's end, which the last leg leaves from across the doorway.
        Assert.True(MathF.Abs(best.SourceSide.Z + 84.36f) < 0.02f && MathF.Abs(best.Edge.Z + 84.36f) < 0.02f,
                    $"crossings {best.SourceSide} then {best.Edge}");
    }
}


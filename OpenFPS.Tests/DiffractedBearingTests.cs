using System;
using System.Numerics;
using OpenFPS.Common;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// Where a blocked source is heard FROM.
///
/// Reported on the speedway, 2026-09-18: "that car stopping in front of me problem at close range".
/// Nothing was stopping. The simulator's pathing stage was being asked to supply the arrival direction
/// for any source whose line of sight was broken, and its probes are laid on a uniform floor grid sized
/// to the map — 7.3 m apart over a 900 x 480 m track, measured from that session's own log. A bearing
/// quantised to 7.3 m is four degrees of error at a hundred metres and nearly thirty at fifteen, and it
/// holds still while the car crosses a cell and then jumps. The sound stopped tracking the car.
///
/// The level model already knew better. <see cref="Diffraction.PathDifferenceAroundBox"/> had been
/// measuring the detour past each obstacle since the knee-high-wall fix, purely to decide how much got
/// through. The same search knows WHERE it got through: the crossing point on the silhouette. An edge
/// is a secondary source at a place, so that point is the bearing — exact geometry, continuous as the
/// source moves, and no grid anywhere in it.
///
/// The three cases below are one rule, not three: a kerb, a stand and a doorway differ only in where
/// their edge happens to be.
/// </summary>
public class DiffractedBearingTests
{
    private static float BearingShiftDegrees(Vector3 listener, Vector3 source, Vector3 edge)
    {
        var toSource = Vector3.Normalize(source - listener);
        var toEdge = Vector3.Normalize(edge - listener);
        return MathF.Acos(Math.Clamp(Vector3.Dot(toSource, toEdge), -1f, 1f)) * 180f / MathF.PI;
    }

    /// <summary>
    /// The reported fault. A car behind the pit wall is still a car in front of you.
    ///
    /// The wall breaks the sight line — that is what put the source on the redirect path in the first
    /// place — but it is 0.9 m of it, and the route over the top leaves the straight line by a
    /// millimetre. The bearing has to be indistinguishable from the car's own.
    /// </summary>
    [Fact]
    public void ACarBehindAKneeHighWallIsStillHeardWhereItIs()
    {
        var centre = new Vector3(0f, 0.45f, 0f);
        var size = new Vector3(60f, 0.9f, 0.3f);
        var source = new Vector3(0f, 0.3f, -20f);
        var listener = new Vector3(0f, 1.7f, 45f);

        Assert.True(Diffraction.PathDifferenceAroundBox(centre, size, Quaternion.Identity,
                                                        source, listener, out float delta, out Vector3 edge));

        // On the wall's top edge, which is the only way past it.
        Assert.Equal(0.9f, edge.Y, 2);

        float shift = BearingShiftDegrees(listener, source, edge);
        Assert.True(shift < 1.0f,
            $"a car behind a knee-high wall must not appear to move; it shifted {shift:F1} degrees. " +
            "The probe-grid bearing this replaced moved it by tens of degrees and then held it there.");

        // And it is still audible, which is the level half of the same measurement.
        Assert.True(delta < 0.05f, $"path difference {delta:F3} m — a 0.9 m wall is a few centimetres of detour");
    }

    /// <summary>
    /// A grandstand is not a kerb, and the same rule says so: the sound comes off its top front corner.
    ///
    /// This is the part that has to keep working. A bearing rule that never moved anything would be
    /// just as wrong as one that moved everything — a source behind a building genuinely is heard from
    /// the building's edge, and that is a large, audible angle.
    /// </summary>
    [Fact]
    public void ASourceBehindAStandIsHeardFromItsEdge()
    {
        var centre = new Vector3(0f, 6f, 0f);
        var size = new Vector3(40f, 12f, 16f);
        var source = new Vector3(0f, 0.6f, -30f);
        var listener = new Vector3(0f, 1.7f, 30f);

        Assert.True(Diffraction.PathDifferenceAroundBox(centre, size, Quaternion.Identity,
                                                        source, listener, out float delta, out Vector3 edge));

        // The top edge of the face turned towards the listener — the corner the sound leaves from.
        Assert.Equal(12f, edge.Y, 1);
        Assert.Equal(8f, edge.Z, 1);

        Assert.True(BearingShiftDegrees(listener, source, edge) > 15f,
            "a source behind a twelve-metre stand is heard from the stand, not through it");
        Assert.Equal(Diffraction.MaxInsertionLossDb, Diffraction.InsertionLossDb(delta, Diffraction.MidBandHz), 1);
    }

    /// <summary>
    /// The doorway case, from maps/default.json: the west leaf of the wood room's front wall, with the
    /// opening at x 6..8. Stand square in front of the leaf and the shortest way past it is round the
    /// jamb — so that is where the megaphone inside is heard from.
    /// </summary>
    [Fact]
    public void ASourceThroughADoorwayIsHeardFromTheJamb()
    {
        var centre = new Vector3(4f, 2f, 10f);      // wall 303
        var size = new Vector3(4f, 4f, 0.5f);
        var source = new Vector3(4f, 1.5f, 15f);    // inside the room, behind the leaf
        var listener = new Vector3(4f, 1.6f, 5f);   // outside, square on

        Assert.True(Diffraction.PathDifferenceAroundBox(centre, size, Quaternion.Identity,
                                                        source, listener, out _, out Vector3 edge));

        // The east end of the leaf: the jamb of the opening, not the top and not the far end.
        Assert.Equal(6f, edge.X, 1);

        Assert.True(BearingShiftDegrees(listener, source, edge) > 20f,
            "standing in front of a wall with a door beside it, the sound comes from the door");
    }

    /// <summary>
    /// The crossing reported is the one the LAST leg leaves from.
    ///
    /// For a thin screen the two crossings coincide and nothing distinguishes them. For anything with
    /// depth — a stand, a building — the route climbs one edge, runs across the face and drops off the
    /// far one, and reporting the entry crossing would place the sound at the corner it went in by.
    /// </summary>
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

    /// <summary>The overload without the edge still answers exactly as it did — the level model is
    /// unchanged by any of this, and the knee-high-wall fix it carries must not have moved.</summary>
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
}

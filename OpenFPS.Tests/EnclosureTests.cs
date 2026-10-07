using System.Numerics;
using OpenFPS.Common;

namespace OpenFPS.Tests;

/// <summary>
/// Whether there is a reverberant field here, a different question from how long one would last: the
/// wet level taken from the decay time read a roofless yard as more reverberant than a roofed one.
/// Two bounces, so a plane is not a room (docs/COMMON_NOTES.md, "Enclosure, not decay time").
/// </summary>
public class EnclosureTests
{
    private static readonly Quaternion Q = Quaternion.Identity;

    private static Enclosure.Solid Floor(string material = "Concrete")
        => new(new Vector3(0, -0.25f, 0), new Vector3(400, 0.5f, 400), Q, material);

    private static List<Enclosure.Solid> SealedRoom(string material = "Concrete") => new()
    {
        new(new Vector3(0, -0.25f, 0), new Vector3(11, 0.5f, 11), Q, material),   // floor
        new(new Vector3(0, 4.25f, 0),  new Vector3(11, 0.5f, 11), Q, material),   // ceiling
        new(new Vector3(0, 2, 5.25f),  new Vector3(11, 4, 0.5f), Q, material),    // north
        new(new Vector3(0, 2, -5.25f), new Vector3(11, 4, 0.5f), Q, material),    // south
        new(new Vector3(5.25f, 2, 0),  new Vector3(0.5f, 4, 11), Q, material),    // east
        new(new Vector3(-5.25f, 2, 0), new Vector3(0.5f, 4, 11), Q, material),    // west
    };

    /// <summary>Bare hard ground is not half a room: one bounce scored a bare plaza 48 % enclosed, but a
    /// plane reflects sound away once and it is never heard again.</summary>
    [Fact]
    public void BareGroundIsNotEnclosed()
    {
        float e = Enclosure.Measure(new Vector3(0, 1.7f, 0), new[] { Floor() });
        Assert.True(e < 0.05f, $"open concrete ground measured {e:P0} enclosed; one bounce called it 48%");
    }

    /// <summary>The inside of a hard box is enclosed: the other end of the same scale.</summary>
    [Fact]
    public void ASealedHardRoomIsFullyEnclosed()
    {
        float e = Enclosure.Measure(new Vector3(0, 1.7f, 0), SealedRoom());
        Assert.True(e > 0.9f, $"a sealed concrete room measured only {e:P0} enclosed");
    }

    /// <summary>The same room without its roof is markedly less enclosed, which the decay time got
    /// backwards.</summary>
    [Fact]
    public void TakingTheRoofOffOpensTheRoom()
    {
        var room = SealedRoom();
        float sealedRoom = Enclosure.Measure(new Vector3(0, 1.7f, 0), room);
        room.RemoveAt(1);                                  // the ceiling
        float yard = Enclosure.Measure(new Vector3(0, 1.7f, 0), room);

        Assert.True(yard < sealedRoom * 0.75f,
            $"roofless {yard:P0} against sealed {sealedRoom:P0} — a yard has to read as the opener of the two");
        Assert.True(yard > 0.15f, $"it still has four hard walls; {yard:P0} is too open");
    }

    /// <summary>What a room is made of decides it, not its size: the box in carpet is a fraction of the
    /// box in concrete.</summary>
    [Fact]
    public void WhatTheWallsAreMadeOfDecidesIt()
    {
        var at = new Vector3(0, 1.7f, 0);
        float hard = Enclosure.Measure(at, SealedRoom("Concrete"));
        float soft = Enclosure.Measure(at, SealedRoom("Carpet"));
        Assert.True(soft < hard * 0.5f, $"carpet {soft:P0} against concrete {hard:P0}");
    }

    /// <summary>A single wall beside you is not a room, however big: on the speedway a ninety-metre concrete
    /// wall put a cathedral on the race.</summary>
    [Fact]
    public void OneLongWallBesideYouIsNotARoom()
    {
        var wall = new Enclosure.Solid(new Vector3(0, 1.75f, -2f), new Vector3(90, 3.5f, 0.6f), Q, "Concrete");
        float e = Enclosure.Measure(new Vector3(0, 1.7f, 0), new[] { Floor("Grass"), wall });
        Assert.True(e < 0.2f, $"a wall and the ground measured {e:P0} enclosed");
    }

    /// <summary>The field's level sums every generation of return, e/(1-e), which puts a room a long way
    /// above a wall.</summary>
    [Fact]
    public void TheFieldIsTheSumOfEveryReturn()
    {
        // e/(1-e): at 1/2 the series is worth 1 (0 dB); at 9/10 it is worth 9 (9.5 dB).
        Assert.Equal(0f, Enclosure.ReverberantGainDb(0.5f), 2);
        Assert.Equal(9.54f, Enclosure.ReverberantGainDb(0.9f), 2);
        Assert.True(Enclosure.ReverberantGainDb(0.98f) > Enclosure.ReverberantGainDb(0.5f) + 15f,
            "a sealed room must be far above a half-open one, not a little above it");
        // Nothing comes back: silence, not a divide by zero.
        Assert.Equal(-80f, Enclosure.ReverberantGainDb(0f), 2);
        Assert.True(float.IsFinite(Enclosure.ReverberantGainDb(1f)), "fully enclosed must not be infinite");
    }

    /// <summary>The same place measures the same every time: the rays are a fixed Fibonacci sphere, so a
    /// listener standing still does not hear the room breathe.</summary>
    [Fact]
    public void TheSamePlaceMeasuresTheSameTwice()
    {
        var room = SealedRoom();
        var at = new Vector3(1.5f, 1.7f, -2f);
        Assert.Equal(Enclosure.Measure(at, room), Enclosure.Measure(at, room), 6);
    }

    /// <summary>Nothing in the world is an open field, not a crash.</summary>
    [Fact]
    public void AnEmptyWorldIsOpen()
    {
        Assert.Equal(0f, Enclosure.Measure(Vector3.Zero, Array.Empty<Enclosure.Solid>()), 6);
    }

    /// <summary>The ray-box test reports the face actually struck; the second bounce's direction comes from
    /// its normal.</summary>
    [Fact]
    public void ARayReportsTheFaceItStruck()
    {
        // Straight down onto a slab: the top face, pointing up, half a metre away.
        Assert.True(GeometryUtils.RayHitsOBB(new Vector3(0, 1f, 0), -Vector3.UnitY, 60f,
                                             new Vector3(0, 0f, 0), new Vector3(10, 1f, 10), Q,
                                             out float d, out Vector3 n));
        Assert.Equal(0.5f, d, 3);
        Assert.Equal(1f, n.Y, 3);

        // And a ray pointing away from it hits nothing.
        Assert.False(GeometryUtils.RayHitsOBB(new Vector3(0, 1f, 0), Vector3.UnitY, 60f,
                                              new Vector3(0, 0f, 0), new Vector3(10, 1f, 10), Q, out _, out _));
    }

    // ── A small enclosure inside a big one ──────────────────────────────────────────────────────
    // Rays leaving a bus shelter's open front strike the building across the street and came home as the
    // shelter's own walls: a cathedral under a bus shelter. Any fix must bring the shelter down and leave the
    // flat garage alone; three keyed on a distance and were reverted (docs/THE_CITY.md, "The bus shelter,
    // and the gate on it").

    /// <summary>The city's bus shelter, with the street and the two buildings that flank it: a back
    /// pane, two end panes, a steel roof, open at the front, and a brick facade seventeen metres away
    /// across the road. Distances and materials taken from OpenFPS.Server/maps/city.json.</summary>
    private static List<Enclosure.Solid> StreetShelter() => new()
    {
        new(new Vector3(0, -0.25f, 0),    new Vector3(400, 0.5f, 400), Q, "Concrete"),  // road and pavement
        new(new Vector3(1.55f, 1.2f, 0),  new Vector3(0.1f, 2.4f, 4.4f), Q, "Glass"),   // back pane
        new(new Vector3(0, 1.2f, 2.17f),  new Vector3(3.2f, 2.4f, 0.06f), Q, "Glass"),  // end pane
        new(new Vector3(0, 1.2f, -2.17f), new Vector3(3.2f, 2.4f, 0.06f), Q, "Glass"),  // end pane
        new(new Vector3(0, 2.45f, 0),     new Vector3(3.2f, 0.1f, 4.4f), Q, "Metal"),   // roof
        new(new Vector3(1.78f, 5f, 0),    new Vector3(0.35f, 10f, 40f), Q, "Brick"),    // the block behind it
        new(new Vector3(-16f, 5f, 0),     new Vector3(0.35f, 10f, 40f), Q, "Brick"),    // the block opposite
    };

    /// <summary>The garage's level 0: 21 by 28 and 2.5 high, hard on every side. The flattest real
    /// room on the map, and the one every distance-keyed fix flattened along with the shelter.</summary>
    private static List<Enclosure.Solid> FlatGarage() => new()
    {
        new(new Vector3(0, -0.25f, 0),   new Vector3(21, 0.5f, 28), Q, "Concrete"),   // floor
        new(new Vector3(0, 2.75f, 0),    new Vector3(21, 0.5f, 28), Q, "Concrete"),   // ceiling
        new(new Vector3(0, 1.25f, 14.25f),  new Vector3(21, 2.5f, 0.5f), Q, "Concrete"),
        new(new Vector3(0, 1.25f, -14.25f), new Vector3(21, 2.5f, 0.5f), Q, "Concrete"),
        new(new Vector3(10.75f, 1.25f, 0),  new Vector3(0.5f, 2.5f, 28), Q, "Concrete"),
        new(new Vector3(-10.75f, 1.25f, 0), new Vector3(0.5f, 2.5f, 28), Q, "Concrete"),
    };

    /// <summary>
    /// A bus shelter on an open street rings for about a third of a second, not three, and not longer than the
    /// parking garage. Honest figures: about 65 m², a twelfth of it the opening, mean free path about 2 m, mean
    /// absorption about 0.3 with the opening counted: Eyring gives 0.2-0.3 s. It passes on the openness
    /// boundary (docs/THE_CITY.md).
    /// </summary>
    [Fact]
    public void AStreetShelterIsNotACathedral()
    {
        var survey = Enclosure.Look(new Vector3(0, 1.6f, 0), StreetShelter());
        var (_, mid, _) = Enclosure.DecaySeconds(survey);
        Console.WriteLine($"SHELTER open {survey.OpenFraction:P0} surface {survey.SurfaceAreaSquareMetres:F0} m2 mfp {survey.MeanFreePathMetres:F1} absorption {survey.AbsorptionMid:F2} mid {mid * 1000:F0} ms");

        // A twelfth of the surface is the open front, so a survey that sees it reads about 8 % open; measured
        // it reads 11 %, the front less the pavement beyond it, which counts as ground.
        Assert.True(survey.OpenFraction > 0.08f,
            $"the front of a shelter is open, and the survey saw {survey.OpenFraction:P0} of the sphere open");
        Assert.True(survey.SurfaceAreaSquareMetres < 150f,
            $"the shelter's surface measured {survey.SurfaceAreaSquareMetres:F0} m2; it is about 65");
        Assert.True(mid < 0.8f,
            $"standing under a bus shelter measured a {mid * 1000:F0} ms tail");
    }

    /// <summary>The guard beside it: the garage is flat, hard and twenty metres across, its far wall is its own,
    /// and its tail is the longest on the map. Median-keyed shrinking had put it at 0.7 s.</summary>
    [Fact]
    public void AFlatGarageStillRings()
    {
        var survey = Enclosure.Look(new Vector3(-4f, 1.6f, 6f), FlatGarage());
        var (_, mid, _) = Enclosure.DecaySeconds(survey);
        Console.WriteLine($"GARAGE open {survey.OpenFraction:P0} surface {survey.SurfaceAreaSquareMetres:F0} m2 mfp {survey.MeanFreePathMetres:F1} absorption {survey.AbsorptionMid:F2} mid {mid * 1000:F0} ms");

        Assert.True(survey.OpenFraction < 0.05f, $"the garage is sealed; {survey.OpenFraction:P0} read open");
        Assert.True(survey.SurfaceAreaSquareMetres > 900f,
            $"a 21 x 28 x 2.5 garage has about 1,400 m2 of surface; the survey read {survey.SurfaceAreaSquareMetres:F0}");
        Assert.True(mid > 3.5f, $"the garage measured a {mid * 1000:F0} ms tail; it is the longest on the map");
    }
}

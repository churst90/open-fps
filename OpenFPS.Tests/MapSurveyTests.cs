using System;
using System.Collections.Generic;
using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Server.Core;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// What a named place on a MAP is made of, measured from the walls that are there.
///
/// A composite is measured inside a box derived from its own parts — that is the whole trick of
/// <see cref="CompositeAcoustics.Survey"/>, and it is why a shed somebody built is a room without
/// anyone authoring one. A map is the other way round: the author draws the box (the region entity),
/// the walls around it are SHARED — the wall between two flats belongs to both, the corridor wall
/// runs the length of the building — and asking those parts to derive a box gives you the building.
///
/// So <see cref="CompositeAcoustics.SurveyBox"/> asks the same questions of a box that is already
/// known. These hold the three things that were wrong the first time it ran over a real city block,
/// each of which is invisible rather than obvious: a part six metres to one side is not your ceiling;
/// a wall three metres thick is still your wall; and a survey may fill in what an author left blank
/// but must never overrule what they said.
/// </summary>
public class MapSurveyTests
{
    public MapSurveyTests() => AcousticRegistry.Initialize();

    private static Entity Part(World w, string material, Vector3 centre, Vector3 size)
        => w.Create(
            new Transform { Position = centre, Rotation = Quaternion.Identity },
            new ColliderComponent { Size = size, IsSolid = true, Shape = ColliderShape.Box },
            new MaterialComponent { Material = material });

    /// <summary>A box written as the space it fills, the way a map writes one.</summary>
    private static Entity Slab(World w, string material, float x0, float x1, float y0, float y1, float z0, float z1)
        => Part(w, material, new Vector3((x0 + x1) / 2, (y0 + y1) / 2, (z0 + z1) / 2),
                             new Vector3(x1 - x0, y1 - y0, z1 - z0));

    /// <summary>Floor, ceiling, north, south, east, west — the order the whole codebase uses.</summary>
    private const int Floor = 0, Ceiling = 1, North = 2, South = 3, East = 4, West = 5;

    /// <summary>
    /// A flat is brick outside, concrete between the storeys and concrete between the flats, because
    /// that is what is round it. Nobody wrote any of those down.
    /// </summary>
    [Fact]
    public void AFlatIsMadeOfWhatIsRoundIt()
    {
        var w = World.Create();
        var parts = new List<Entity>();
        // A room 5 x 2.7 x 4, with a slab under and over it, brick on the east and concrete
        // partitions north and south.
        parts.Add(Slab(w, "Concrete", -3f, 3f, -0.3f, 0f, -3f, 3f));      // floor
        parts.Add(Slab(w, "Concrete", -3f, 3f, 2.7f, 3f, -3f, 3f));       // ceiling
        parts.Add(Slab(w, "Brick", 2.5f, 2.9f, 0f, 2.7f, -3f, 3f));       // outside wall, east
        parts.Add(Slab(w, "Concrete", -2.9f, -2.5f, 0f, 2.7f, -3f, 3f));  // corridor wall, west
        parts.Add(Slab(w, "Concrete", -3f, 3f, 0f, 2.7f, 2f, 2.4f));      // partition, north
        parts.Add(Slab(w, "Concrete", -3f, 3f, 0f, 2.7f, -2.4f, -2f));    // partition, south

        var survey = CompositeAcoustics.SurveyBox(w, parts, new Vector3(0f, 1.35f, 0f), new Vector3(5f, 2.7f, 4f));

        Assert.True(survey.Covered, $"only {survey.Walls} of six faces walled");
        Assert.Equal("Concrete", survey.Materials[Floor]);
        Assert.Equal("Concrete", survey.Materials[Ceiling]);
        Assert.Equal("Brick", survey.Materials[East]);
        Assert.Equal("Concrete", survey.Materials[West]);
        Assert.Equal("Concrete", survey.Materials[North]);
        Assert.Equal("Concrete", survey.Materials[South]);
        // A room with nothing in it is not solid, however thick its walls are.
        Assert.True(survey.SolidFraction < 0.2f, $"{survey.SolidFraction:P0} solid");
    }

    /// <summary>
    /// A slab at the right height and six metres to one side is not your ceiling.
    ///
    /// This is the fault that put a roof over the street. A building's first-floor slab is three
    /// metres up, which is within tolerance of a four-metre pavement's ceiling plane — and it is
    /// entirely inside the building. Measuring only the distance ALONG the face's axis, every stretch
    /// of pavement in the city came back enclosed, indoors, with a concrete ceiling over it.
    /// </summary>
    [Fact]
    public void SomethingBesideYouIsNotOverYou()
    {
        var w = World.Create();
        var parts = new List<Entity>
        {
            Slab(w, "Concrete", -2f, 2f, -0.1f, 0f, -9f, 9f),        // the pavement itself
            Slab(w, "Concrete", 6f, 26f, 2.75f, 3f, -9f, 9f),        // a building's slab, well to the east
        };

        var survey = CompositeAcoustics.SurveyBox(w, parts, new Vector3(0f, 2f, 0f), new Vector3(4f, 4f, 18f));

        Assert.True(survey.Coverage[Ceiling] < CompositeAcoustics.FaceCoverage,
            $"the pavement came back with a ceiling {survey.Coverage[Ceiling]:P0} covered");
        Assert.False(survey.Covered, "a stretch of pavement is not a room");
    }

    /// <summary>
    /// A wall three and a half metres thick is still the wall of the room beside it.
    ///
    /// The other half of the same fault, and the one that made a tunnel read as open sky. A part used
    /// to belong to a face if its OUTER edge was near the plane — true for a composite, whose box is
    /// derived FROM the parts, and false for a box drawn first, where a thick wall's far side is
    /// metres away. What faces you is the side of the wall that faces you.
    /// </summary>
    [Fact]
    public void AThickWallIsStillAWall()
    {
        var w = World.Create();
        var parts = new List<Entity>
        {
            Slab(w, "Asphalt", -6f, 6f, -0.1f, 0f, -5f, 5f),          // the carriageway
            Slab(w, "Concrete", -6f, 6f, 5.5f, 5.9f, -5f, 5f),        // the roof
            Slab(w, "Concrete", 6f, 9.5f, 0f, 5.5f, -5f, 5f),         // 3.5 m of wall, east
            Slab(w, "Concrete", -9.5f, -6f, 0f, 5.5f, -5f, 5f),       // and west
        };

        var survey = CompositeAcoustics.SurveyBox(w, parts, new Vector3(0f, 2.75f, 0f), new Vector3(12f, 5.5f, 10f));

        Assert.True(survey.Coverage[East] > 0.9f, $"east wall {survey.Coverage[East]:P0} covered");
        Assert.True(survey.Coverage[West] > 0.9f, $"west wall {survey.Coverage[West]:P0} covered");
        Assert.Equal("Asphalt", survey.Materials[Floor]);
        Assert.Equal("Concrete", survey.Materials[Ceiling]);
        // Four walls and two open ends: a tunnel, and the most enclosed thing on a city map.
        Assert.True(survey.Covered);
        Assert.Equal(4, survey.Walls);
    }

    /// <summary>
    /// Four glass sides, a metal roof and an open front is a bus shelter: enclosed, barely — which is
    /// exactly what standing in one is.
    /// </summary>
    [Fact]
    public void ABusShelterIsBarelyARoom()
    {
        var w = World.Create();
        var parts = new List<Entity>
        {
            Slab(w, "Concrete", -1.6f, 1.6f, -0.1f, 0f, -2.2f, 2.2f),
            Slab(w, "Metal", -1.6f, 1.6f, 2.4f, 2.5f, -2.2f, 2.2f),
            Slab(w, "Glass", 1.5f, 1.6f, 0f, 2.4f, -2.2f, 2.2f),
            Slab(w, "Glass", -1.6f, 1.6f, 0f, 2.4f, 2.1f, 2.2f),
            Slab(w, "Glass", -1.6f, 1.6f, 0f, 2.4f, -2.2f, -2.1f),
        };

        var survey = CompositeAcoustics.SurveyBox(w, parts, new Vector3(0f, 1.2f, 0f), new Vector3(3.2f, 2.4f, 4.4f));

        Assert.Equal("Metal", survey.Materials[Ceiling]);
        Assert.Equal("Glass", survey.Materials[East]);
        Assert.True(survey.Covered);
        Assert.True(survey.Coverage[West] < CompositeAcoustics.FaceCoverage, "the front of a shelter is open");
    }

    /// <summary>
    /// A pillar in the middle of a room covers nothing. Held because the overlap test could be
    /// written so that anything inside the box counts for every face.
    /// </summary>
    [Fact]
    public void APillarInTheMiddleIsNotAWall()
    {
        var w = World.Create();
        var parts = new List<Entity> { Slab(w, "Concrete", -0.2f, 0.2f, 0f, 3f, -0.2f, 0.2f) };

        var survey = CompositeAcoustics.SurveyBox(w, parts, new Vector3(0f, 1.5f, 0f), new Vector3(10f, 3f, 10f));

        for (int f = North; f <= West; f++)
            Assert.True(survey.Coverage[f] < CompositeAcoustics.FaceCoverage,
                $"{CompositeAcoustics.FaceNames[f]} came back {survey.Coverage[f]:P0} covered by a pillar");
        Assert.False(survey.Covered);
    }
}

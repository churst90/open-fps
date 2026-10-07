using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Server.Core;

namespace OpenFPS.Tests;

/// <summary>
/// What a named place on a map is made of, measured from the walls round its authored box
/// (<see cref="CompositeAcoustics.SurveyBox(World, List{Entity}, Vector3, Vector3)"/>; a map's walls are
/// shared, so deriving a box from them gives the building). Holds the faults found on the city block
/// (docs/THE_CITY_BLOCK.md), and that a survey fills in what an author left blank but never overrules it.
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

    /// <summary>A flat's materials come from what is round it: brick outside, concrete between storeys and flats.</summary>
    [Fact]
    public void AFlatIsMadeOfWhatIsRoundIt()
    {
        var w = World.Create();
        var parts = new List<Entity>();
        // A room 5 x 2.7 x 4.
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
    /// A slab at the right height six metres to one side is not your ceiling: measured only along the
    /// face's axis, every pavement in the city came back indoors under a building's first-floor slab.
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
    /// A wall 3.5 m thick is still the wall of the room beside it: measured to its outer edge, as suits a
    /// composite, a tunnel read as open sky. What faces you is the side of the wall that faces you.
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

    /// <summary>Glass sides, a metal roof and an open front, a bus shelter, are barely enclosed.</summary>
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

    /// <summary>A pillar in the middle of a room covers no face.</summary>
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

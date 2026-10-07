using System.Numerics;
using OpenFPS.Common;

namespace OpenFPS.Tests;

/// <summary>
/// The reverberant field has a direction: the survey's return centroid (energy-weighted mean direction
/// the rays came back from) and anisotropy (how one-sided), which steer the listener's reverb bus. Asked
/// for as "a wash of reverb coming ONLY from that half of the room" when standing on carpet facing the
/// hard half; no special case for that map.
/// </summary>
public class ReverbFieldDirectionTests
{
    private static readonly Quaternion Q = Quaternion.Identity;

    /// <summary>The material lab's shape: one hall, two halves, each half's floor and walls of its
    /// own material, open to the sky as the map's rooms are.</summary>
    private static List<Enclosure.Solid> Room(string west, string east) => new()
    {
        new(new Vector3(-5, -0.25f, 0), new Vector3(10, 0.5f, 11), Q, west),        // floor, west half
        new(new Vector3(5, -0.25f, 0),  new Vector3(10, 0.5f, 11), Q, east),        // floor, east half
        new(new Vector3(-5, 2, 5.25f),  new Vector3(10, 4, 0.5f), Q, west),         // north, west half
        new(new Vector3(-5, 2, -5.25f), new Vector3(10, 4, 0.5f), Q, west),         // south, west half
        new(new Vector3(-10.25f, 2, 0), new Vector3(0.5f, 4, 11), Q, west),         // west end
        new(new Vector3(5, 2, 5.25f),   new Vector3(10, 4, 0.5f), Q, east),         // north, east half
        new(new Vector3(5, 2, -5.25f),  new Vector3(10, 4, 0.5f), Q, east),         // south, east half
        new(new Vector3(10.25f, 2, 0),  new Vector3(0.5f, 4, 11), Q, east),         // east end
    };

    private static List<Enclosure.Solid> Sealed(string material) => new()
    {
        new(new Vector3(0, -0.25f, 0), new Vector3(11, 0.5f, 11), Q, material),
        new(new Vector3(0, 4.25f, 0),  new Vector3(11, 0.5f, 11), Q, material),
        new(new Vector3(0, 2, 5.25f),  new Vector3(11, 4, 0.5f), Q, material),
        new(new Vector3(0, 2, -5.25f), new Vector3(11, 4, 0.5f), Q, material),
        new(new Vector3(5.25f, 2, 0),  new Vector3(0.5f, 4, 11), Q, material),
        new(new Vector3(-5.25f, 2, 0), new Vector3(0.5f, 4, 11), Q, material),
    };

    [Fact]
    public void ACarpetedHalfHearsItsFieldFromTheHardHalf()
    {
        // The middle of the carpeted west half, against the same hall all concrete.
        var carpeted = Enclosure.Look(new Vector3(-5, 1.7f, 0), Room(west: "Carpet", east: "Concrete"));
        var uniform = Enclosure.Look(new Vector3(-5, 1.7f, 0), Room(west: "Concrete", east: "Concrete"));

        Assert.True(carpeted.ReturnDirection.X > 0.5f,
            $"the field should come from the east (the concrete), but the centroid points {carpeted.ReturnDirection}");
        // The carpet is what moves the field east: the same spot in an all-concrete hall leans toward
        // its own nearer end wall (west) and the floor, not east.
        Assert.True(carpeted.ReturnDirection.X > uniform.ReturnDirection.X + 0.3f,
            $"the carpet should push the field east: centroid {carpeted.ReturnDirection} against {uniform.ReturnDirection} all-concrete");
        // Measured 2026-09-18: carpeted (0.53, -0.84, 0.05) at 25 %, all-concrete 27 % leaning down and
        // west. Mild, because the registry's Carpet (0.5 mid absorption) returns half what concrete does.
        Assert.True(carpeted.Anisotropy > 0.15f, $"anisotropy read {carpeted.Anisotropy:P0}");
    }

    [Fact]
    public void ASealedUniformRoomHearsItsFieldFromEverywhere()
    {
        var s = Enclosure.Look(new Vector3(0, 1.7f, 0), Sealed("Concrete"));
        Assert.True(s.Anisotropy < 0.15f,
            $"a sealed uniform room returns energy from every side, but anisotropy read {s.Anisotropy:P0} (centroid {s.ReturnDirection})");
    }

    [Fact]
    public void ARooflessHallHearsItsFieldFromBelow()
    {
        // The sky returns nothing, so a roofless hall's field leans down (measured: centroid
        // (0.05, -1.00, -0.01), anisotropy 24 %).
        var s = Enclosure.Look(new Vector3(0, 1.7f, 0), Room(west: "Concrete", east: "Concrete"));
        Assert.True(s.ReturnDirection.Y < -0.8f, $"expected a downward centroid, got {s.ReturnDirection}");
        Assert.InRange(s.Anisotropy, 0.1f, 0.5f);
    }

    [Fact]
    public void OneHardWallInAFieldIsHeardFromThatWall()
    {
        var solids = new List<Enclosure.Solid>
        {
            new(new Vector3(0, -0.25f, 0), new Vector3(400, 0.5f, 400), Q, "Grass"),
            new(new Vector3(0, 3, 12), new Vector3(60, 6, 0.6f), Q, "Concrete"),   // a wall to the north
        };
        var s = Enclosure.Look(new Vector3(0, 1.7f, 0), solids);
        Assert.True(s.ReturnDirection.Z > 0.7f, $"the centroid should point north, at the wall: {s.ReturnDirection}");
        Assert.True(s.Anisotropy > 0.6f, $"one wall is nearly all of the return, but anisotropy read {s.Anisotropy:P0}");
    }

    [Fact]
    public void NothingComingBackHasNoDirection()
    {
        var s = Enclosure.Look(new Vector3(0, 1.7f, 0), new List<Enclosure.Solid>());
        Assert.Equal(Vector3.Zero, s.ReturnDirection);
        Assert.Equal(0f, s.Anisotropy);
    }
}

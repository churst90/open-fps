using OpenFPS.Common.Geometry;

namespace OpenFPS.Tests;

public partial class RealPlaceMapTests
{
    /// <summary>
    /// Rooms by flood (docs/GEOMETRY.md 12.6): every room of a place flooded through its server's triangle world with
    /// the doors shut and stopped at its openings. None meets the sky (a barn's doorway with no leaf in it is an
    /// opening to the outdoors), no room is without a start, and half the rooms or more share over 0.8 of their air with
    /// their boxes.
    /// </summary>
    [Theory]
    [MemberData(nameof(Places))]
    public void Rooms_flood_shut_and_as_drawn(string id)
    {
        var p = _maps.Get(id);
        var g = Gate.Measure(ServerWorld(p), p.Ecs, p.Data.MinBound);
        _o.WriteLine($"{id}: {g.Rooms} rooms, {g.Open.Count} open ({string.Join("; ", g.Open)}), {g.Unseeded} unseeded, median IoU {g.MedianIou:F2}");
        Assert.Empty(g.Open);
        Assert.Equal(0, g.Unseeded);
        Assert.True(g.MedianIou > 0.8, $"median IoU {g.MedianIou:F2}");
    }
}

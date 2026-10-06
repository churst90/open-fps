using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Systems;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// A route crosses each opening once. Two brick rooms side by side with a doorway between them, the west
/// one with a gap in its front wall to the street: the way from the east room to the west one is the
/// doorway, never out of the front gap and straight back in through it. Found by Resonance's port.
/// </summary>
public class OpeningRoutesOutAndBackTests
{
    private const float T = 0.2f, H = 2.6f;

    [Fact]
    public void TheNextRoomIsHeardThroughTheDoorwayNotOutAndBackThroughTheFrontGap()
    {
        AcousticRegistry.EnsureInitialized();
        var boxes = new List<(Vector3 Min, Vector3 Max, string Material)>
        {
            (new(-T, -T, -T), new(10f + T, 0f, 4f + T), "Concrete"),
            (new(-T, H, -T), new(10f + T, H + T, 4f + T), "Concrete"),
            (new(-T, 0f, 0f), new(0f, H, 4f), "Brick"),
            (new(10f, 0f, 0f), new(10f + T, H, 4f), "Brick"),
            (new(-T, 0f, 4f), new(10f + T, H, 4f + T), "Brick"),
            // Between the rooms, a doorway at z 1.5 to 2.5.
            (new(5f, 0f, 0f), new(5.2f, H, 1.5f), "Brick"),
            (new(5f, 0f, 2.5f), new(5.2f, H, 4f), "Brick"),
            (new(5f, 2.1f, 1.5f), new(5.2f, H, 2.5f), "Brick"),
            // The front wall, the west room's with a gap at x 2 to 3.
            (new(5f, 0f, -T), new(10f + T, H, 0f), "Brick"),
            (new(-T, 0f, -T), new(2f, H, 0f), "Brick"),
            (new(3f, 0f, -T), new(5f, H, 0f), "Brick"),
            (new(2f, 2.1f, -T), new(3f, H, 0f), "Brick"),
        };
        var rooms = new (int Id, Vector3 Centre, Vector3 Size)[] { (1, new(2.5f, H / 2f, 2f), new(5f, H, 4f)), (2, new(7.6f, H / 2f, 2f), new(4.8f, H, 4f)) };
        AcousticRegistry.TryGetResonanceIndex("Brick", out int brick);

        var faceBoxes = boxes.Select(b => new FaceOpenings.Box((b.Min + b.Max) / 2f, b.Max - b.Min, Quaternion.Identity)).ToList();
        var places = rooms.Select(r => new FaceOpenings.Place(r.Id, r.Centre, r.Size, Quaternion.Identity)).ToList();
        var declared = new List<OpeningRoutes.Declared>();
        int id = -2000;
        for (int i = 0; i < rooms.Length; i++)
            foreach (var gap in FaceOpenings.Find(places[i], [brick, brick, brick, brick, brick, brick], faceBoxes, places, 0.5f, -1))
            {
                if (rooms[i].Id == 2 && gap.Beyond == 1) continue;   // the doorway again, from its other side
                declared.Add(new OpeningRoutes.Declared(id--, OpeningRoutes.FaceKind, gap.Centre, gap.Rotation,
                                                        new Vector3(gap.Width, gap.Height, gap.Depth), 0f, rooms[i].Id, gap.Beyond));
            }
        Assert.Equal(2, declared.Count);

        var map = new AcousticMap();
        foreach (var r in rooms)
        {
            map.Regions[r.Id] = new RegionComponent { RoomSize = r.Size, Materials = [brick, brick, brick, brick, brick, brick], IsIndoor = true, FriendlyName = $"r{r.Id}" };
            map.RegionPositions[r.Id] = r.Centre;
        }
        int RegionAt(Vector3 p)
        {
            foreach (var r in rooms)
                if (Vector3.Abs(p - r.Centre) is var d && d.X <= r.Size.X / 2 && d.Y <= r.Size.Y / 2 && d.Z <= r.Size.Z / 2) return r.Id;
            return -1;
        }
        var solids = boxes.Select(b => new OpeningRoutes.Solid((b.Min + b.Max) / 2f, b.Max - b.Min, Quaternion.Identity, b.Material)).ToList();
        var routes = OpeningRoutes.Build(solids, map, declared, RegionAt);

        // The listener in the west room's north-west corner, the sound in the east room's north-east.
        var ears = new Vector3(0.5f, 1.6f, 3.5f);
        var source = new Vector3(9.5f, 1.6f, 3.5f);
        Assert.True(routes.Route(source, 2, ears, 1, out var answer));
        var doorway = new Vector3(5.1f, 1.05f, 2f);
        Assert.True(Vector3.Distance(answer.Apparent, doorway) < 1f, $"heard from {answer.Apparent} by {answer.Via}, not the doorway");
    }
}

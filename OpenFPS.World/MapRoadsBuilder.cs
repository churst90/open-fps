using OpenFPS.Common;
using OpenFPS.Server.Repositories;

namespace OpenFPS.Server.Core;

/// <summary>
/// What a client is told about a map's roads (the MapRoads message): the roads and junctions as the map
/// declares them, the level crossings as the rails lie (CrossingSystem.Rails), and the tracks no train
/// runs on, which are tracks a vehicle can drive round.
/// </summary>
public static class MapRoadsBuilder
{
    public static RoadMapData Build(MapData? map, IEnumerable<CrossingRails> crossings)
    {
        var data = new RoadMapData();
        if (map != null)
        {
            if (map.Roads != null) data.Roads.AddRange(map.Roads);
            if (map.Junctions != null) data.Junctions.AddRange(map.Junctions);
            var rail = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (map.Trains != null) foreach (var t in map.Trains) if (!string.IsNullOrEmpty(t.Track)) rail.Add(t.Track);
            if (map.Tracks != null)
                foreach (var t in map.Tracks)
                    if (!rail.Contains(t.Id) && t.Waypoints.Count >= 3)
                        data.Tracks.Add(new DriveTrack { Id = t.Id, Waypoints = t.Waypoints, WidthMetres = t.WidthMetres, BankingDegrees = t.BankingDegrees });
        }
        foreach (var c in crossings)
        {
            // The planking reaches across the widest road through the crossing, and a little beyond.
            float half = 4f;
            foreach (var road in data.Roads)
            {
                if (road.Centreline.Count < 2) continue;
                var (_, off) = RoadNetwork.Project(road.Centreline, c.Centre);
                if (off < road.WidthMetres * 0.5f + 2f) half = MathF.Max(half, road.WidthMetres * 0.5f + 1.5f);
            }
            c.HalfLengthMetres = half;
            data.Crossings.Add(c);
        }
        return data;
    }
}

using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Server.Repositories;
using Serilog;

namespace OpenFPS.Server.Systems;

/// <summary>
/// Vehicles that drive a map's roads (RouteData) rather than a drawn track: a tour of lanes and the
/// turns between them, built from the road network at load (LaneRoutes), driven like any other loop.
/// Where they stop comes from the roads too: the give-way line at every junction where their road does
/// not have priority, the roadside stops their way passes (a bus stop for a bus), and the level
/// crossings on it.
///
/// The laws alone, on the road network the host hands in (VehicleSystem.Routes has a map's from its map
/// manager), and where on its tour a vehicle is from its line and its lap.
/// </summary>
public static class RouteRules
{
    /// <summary>How close a roadside stop must be to a vehicle's way for the vehicle to use it, metres:
    /// within its own lane.</summary>
    public const float RoadStopReachMetres = 2f;
    /// <summary>A level crossing on a vehicle's way: this close to it, metres; it waits this far short.</summary>
    public const float CrossingReachMetres = 6f, CrossingStandOffMetres = 12f;

    /// <summary>The tour a vehicle's route asks for on these roads, or null (and why, in the log) if there is none.</summary>
    public static LaneRoute? Build(RoadNetwork net, string mapId, VehicleData vd)
    {
        var r = vd.Route!;
        LaneRoute? route;
        if (r.Via.Count > 0)
        {
            var via = new List<JunctionData>();
            foreach (var id in r.Via)
            {
                var j = net.Junctions.FirstOrDefault(x => x.Id == id);
                if (j == null) { Log.Warning("Map {Map}: route of '{Name}' names junction '{J}', which is not on the map.", mapId, vd.Name, id); return null; }
                via.Add(j);
            }
            route = LaneRoutes.Via(net, via);
        }
        else
        {
            var start = net.Segments
                // Between two junctions: a stretch that starts at a road's dead end is one nothing
                // leads back into, and a tour from it could never close.
                .Where(s => s.Road.Id == r.StartRoad && Math.Sign(s.Lane.Direction) == Math.Sign(r.Direction)
                            && LaneRoutes.IsKerbLane(s) && s.From != null && s.To != null)
                .OrderBy(s => s.Index).FirstOrDefault();
            if (start == null) { Log.Warning("Map {Map}: route of '{Name}' starts on road '{Road}', which has no such lane.", mapId, vd.Name, r.StartRoad); return null; }
            route = LaneRoutes.Random(net, start, r.Seed, r.WanderMetres);
        }
        if (route == null) Log.Warning("Map {Map}: no way round the roads could be found for '{Name}'; it will not be spawned.", mapId, vd.Name);
        return route;
    }

    /// <summary>The speed limit of the lane nearest a point on a route, m/s.</summary>
    public static Func<Vector3, float> LaneLimits(LaneRoute route)
        => p =>
        {
            float best = float.MaxValue, limit = 50f;
            foreach (var (seg, _, _) in route.Legs)
            {
                var (_, off) = RoadNetwork.Project(seg.Path, p);
                if (off < best) { best = off; limit = seg.Lane.SpeedLimitKmh; }
            }
            return limit / 3.6f;
        };

    /// <summary>Everywhere a vehicle on this route stops, in the line's own distance round it.</summary>
    public static (float At, float Dwell, string Kind)[] Stops(LaneRoute route, RaceLine line, MapData data, string preset)
    {
        float scale = line.Length / MathF.Max(1f, route.Length);
        var stops = new List<(float At, float Dwell, string Kind)>();
        // A STOP line is a stop. A give-way line is not: whether it waits there is decided by the
        // traffic it has to give way to (JunctionRules), not by a clock.
        foreach (var (seg, _, to) in route.Legs)
            if (seg.To != null && seg.To.Control == "stop" && seg.To.GivesWay(seg.Road))
                stops.Add(((to - 1f) * scale, seg.To.GiveWaySeconds, "stop"));
        foreach (var rs in data.RoadStops ?? new())
        {
            if (!string.IsNullOrEmpty(rs.ForPreset) && !preset.Contains(rs.ForPreset, StringComparison.OrdinalIgnoreCase)) continue;
            var (along, off) = RoadNetwork.Project(route.Points, rs.Position);
            if (off <= RoadStopReachMetres) stops.Add((along * scale, rs.DwellSeconds, rs.Kind));
        }
        foreach (var c in data.Crossings ?? new())
        {
            var (along, off) = RoadNetwork.Project(route.Points, c.Position);
            if (off <= CrossingReachMetres) stops.Add((((along - CrossingStandOffMetres) * scale + line.Length) % line.Length, 3f, "crossing"));
        }
        return stops.OrderBy(s => s.At).ToArray();
    }

    /// <summary>
    /// Which leg of its route a vehicle is on, and how far along that lane, measured from where it is:
    /// distance round the smoothed line differs by metres between routes sharing a lane. Negative while
    /// still in the junction before the lane.
    /// </summary>
    /// <param name="lap">Metres round its line.</param>
    /// <param name="routeScale">The route's metres per metre of its line.</param>
    public static (int Leg, float Along) WhereOnLane(LaneRoute route, RaceLine line, float lap, float routeScale)
    {
        var (leg, roughly) = route.LegAt(lap * routeScale);
        line.Sample(lap, out var at, out _, out _);
        var path = route.Legs[leg].Segment.Path;
        float along = roughly < 0f ? -Vector3.Distance(at, path[0]) : RoadNetwork.Project(path, at).Along;
        return (leg, along);
    }

    /// <summary>
    /// Whether a vehicle the lap puts in a junction is in fact still on the lane before it, short of the
    /// line; if so, which leg and how far along. The smoothed line drifts metres from the lanes round
    /// corners: a truck holding at the line was taken to be in the junction and drove on (traced 2026-09-28).
    /// </summary>
    public static bool ShortOfTheLine(LaneRoute route, RaceLine line, float lap, ref int leg, ref float along)
    {
        var legs = route.Legs;
        int prev = (leg - 1 + legs.Count) % legs.Count;
        var before = legs[prev].Segment;
        line.Sample(lap, out var at, out _, out _);
        var (a, off) = RoadNetwork.Project(before.Path, at);
        if (off >= before.Lane.WidthMetres * 0.5f || a >= before.LengthMetres - 0.05f) return false;
        leg = prev;
        along = a;
        return true;
    }
}

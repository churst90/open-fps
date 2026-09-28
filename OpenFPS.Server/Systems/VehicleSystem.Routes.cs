using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using Serilog;

namespace OpenFPS.Server.Systems;

/// <summary>
/// Vehicles that drive a map's roads (RouteData) rather than a drawn track: a tour of lanes and the
/// turns between them, built from the road network at load (LaneRoutes), driven like any other loop.
/// Where they stop comes from the roads too: the give-way line at every junction where their road does
/// not have priority, the roadside stops their way passes (a bus stop for a bus), and the level
/// crossings on it.
/// </summary>
public sealed partial class VehicleSystem
{
    /// <summary>How close a roadside stop must be to a vehicle's way for the vehicle to use it, metres:
    /// within its own lane.</summary>
    private const float RoadStopReachMetres = 2f;
    /// <summary>A level crossing on a vehicle's way: this close to it, metres; it waits this far short.</summary>
    private const float CrossingReachMetres = 6f, CrossingStandOffMetres = 12f;

    private LaneRoute? BuildRoute(MapManager maps, string mapId, VehicleData vd, int index)
    {
        if (!maps.TryGetRoads(mapId, out var net))
        {
            Log.Warning("Map {Map}: vehicle '{Name}' has a route but the map has no roads; it will not be spawned.", mapId, vd.Name);
            return null;
        }
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
    private static Func<Vector3, float> LaneLimits(LaneRoute route)
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
    private static (float At, float Dwell, string Kind)[] RouteStops(LaneRoute route, RaceLine line, MapData data, string preset)
    {
        float scale = line.Length / MathF.Max(1f, route.Length);
        var stops = new List<(float At, float Dwell, string Kind)>();
        // A STOP line is a stop. A give-way line is not: whether it waits there is decided by the
        // traffic it has to give way to (VehicleSystem.Junctions), not by a clock.
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

    // ── Following, by lane ─────────────────────────────────────────────────────────────────────
    //
    // Vehicles on different tours share lanes, so who is in front is a question about the lane, not
    // the tour. Each is filed under the lane its tour has it on (or heading into, while it is still
    // in the junction before it), with how far along; the one in front is the next along that lane,
    // or the last on the next lane of its own tour.

    private readonly Dictionary<(string Map, int Segment), List<(DemoVehicle V, float Along)>> _onSegment = new();

    private void IndexSegments(string mapId)
    {
        foreach (var list in _onSegment.Values) list.Clear();
        foreach (var v in _vehicles)
        {
            if (v.MapId != mapId || v.Route == null || v.Line == null || !InLane(v)) continue;
            var (leg, along) = WhereOnLane(v);
            var key = (mapId, v.Route.Legs[leg].Segment.Index);
            if (!_onSegment.TryGetValue(key, out var list)) _onSegment[key] = list = new();
            list.Add((v, along));
        }
    }

    /// <summary>
    /// Which leg of its route a vehicle is on, and how far along that lane it is, measured from where
    /// it actually is. Distance round its own route is not good enough: the line it drives is smoothed,
    /// which shortens corners more than straights, so two vehicles on different routes would disagree
    /// by metres about who is in front on a lane they share. Negative while still in the junction
    /// before the lane: the distance to the lane's first point.
    /// </summary>
    private static (int Leg, float Along) WhereOnLane(DemoVehicle v)
    {
        var (leg, roughly) = v.Route!.LegAt(v.Lap * v.RouteScale);
        v.Line!.Sample(v.Lap, out var at, out _, out _);
        var path = v.Route.Legs[leg].Segment.Path;
        float along = roughly < 0f ? -Vector3.Distance(at, path[0]) : RoadNetwork.Project(path, at).Along;
        return (leg, along);
    }

    /// <summary>The vehicle ahead of one on a route, and the gap to its tail, within a look of 120 m.</summary>
    private (DemoVehicle Lead, float Gap)? AheadOnRoute(DemoVehicle v)
    {
        var route = v.Route!;
        var (leg, along) = WhereOnLane(v);
        // Distances between legs are this route's own, from the start of the leg it is on.
        float Offset(int li)
        {
            float d = route.Legs[li].From - route.Legs[leg].From;
            return d < 0f ? d + route.Length : d;
        }
        // Whatever left this lane into the junction ahead is in front of it, wherever it is turning; but
        // only when nothing is between them on the lane itself.
        var lane = route.Legs[leg].Segment;
        bool clearToTheLine = !_onSegment.TryGetValue((v.MapId, lane.Index), out var onLane)
                              || !onLane.Any(x => x.V != v && (x.Along > along || (x.Along == along && x.V.Entity.Id < v.Entity.Id)));
        if (clearToTheLine && along >= 0f && lane.To != null && _atJunction.TryGetValue((v.MapId, lane.To.Id), out var here))
        {
            v.Line!.Sample(v.Lap, out var me, out _, out _);
            (DemoVehicle Lead, float Gap)? best = null;
            foreach (var (o, m) in here.Inside)
            {
                if (o == v || m.In != lane) continue;
                o.Line!.Sample(o.Lap, out var them, out _, out _);
                float gap = Vector3.Distance(me, them) - 0.5f * (o.LengthMetres + v.LengthMetres);
                if (best == null || gap < best.Value.Gap) best = (o, gap);
            }
            if (best != null) return best;
        }
        for (int k = 0; k < route.Legs.Count; k++)
        {
            int li = (leg + k) % route.Legs.Count;
            if (Offset(li) - along > 120f) break;
            var seg = route.Legs[li].Segment;
            if (_onSegment.TryGetValue((v.MapId, seg.Index), out var list))
            {
                DemoVehicle? lead = null; float leadAlong = float.MaxValue;
                foreach (var (o, a) in list)
                {
                    if (o == v) continue;
                    // Behind it, or dead level: level, the one with the lower id leads, so that two
                    // that arrive side by side do not both decide the other is not in front.
                    if (k == 0 && (a < along || (a == along && o.Entity.Id > v.Entity.Id))) continue;
                    if (a < leadAlong) { leadAlong = a; lead = o; }
                }
                if (lead != null)
                {
                    float gap = Offset(li) + leadAlong - along;
                    return (lead, gap - 0.5f * (lead.LengthMetres + v.LengthMetres));
                }
            }
        }
        return null;
    }
}

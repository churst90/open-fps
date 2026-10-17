using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using Serilog;

namespace OpenFPS.Server.Systems;

/// <summary>
/// Vehicles that drive a map's roads rather than a drawn track (RouteRules): the map's road network handed
/// to the rules, and who is ahead of whom on lanes the tours share.
/// </summary>
public sealed partial class VehicleSystem
{
    /// <summary>The tour a vehicle's route asks for on its map's roads (RouteRules.Build), or null.</summary>
    private LaneRoute? BuildRoute(MapManager maps, string mapId, VehicleData vd)
    {
        if (!maps.TryGetRoads(mapId, out var net))
        {
            Log.Warning("Map {Map}: vehicle '{Name}' has a route but the map has no roads; it will not be spawned.", mapId, vd.Name);
            return null;
        }
        return RouteRules.Build(net, mapId, vd);
    }

    // ── Following, by lane ─────────────────────────────────────────────────────────────────────
    //
    // Tours share lanes, so who is in front is asked of the lane: each vehicle is filed under the lane
    // it is on (or heading into from the junction before it), with how far along.

    private readonly Dictionary<(string Map, int Segment), List<(DemoVehicle V, float Along)>> _onSegment = new();

    private void IndexSegments(string mapId)
    {
        foreach (var list in _onSegment.Values) list.Clear();
        foreach (var v in _vehicles)
        {
            if (v.MapId != mapId || v.Route == null || v.Line == null || !InLane(v)) continue;
            var (leg, along) = WhereOnLane(v);
            if (along < 0f) ShortOfTheLine(v, ref leg, ref along);     // filed where the junctions file it
            var key = (mapId, v.Route.Legs[leg].Segment.Index);
            if (!_onSegment.TryGetValue(key, out var list)) _onSegment[key] = list = new();
            list.Add((v, along));
        }
    }

    /// <summary>Which leg of its route a vehicle is on, and how far along that lane (RouteRules.WhereOnLane).</summary>
    private static (int Leg, float Along) WhereOnLane(DemoVehicle v)
        => RouteRules.WhereOnLane(v.Route!, v.Line!, v.Lap, v.RouteScale);

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
        // Whatever left this lane into the junction ahead is in front, unless something on the lane is nearer.
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
                    // Dead level, the lower id leads, or two side by side both think the other is behind.
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

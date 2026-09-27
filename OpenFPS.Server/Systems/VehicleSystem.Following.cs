using OpenFPS.Server.Repositories;

namespace OpenFPS.Server.Systems;

/// <summary>
/// Keeping a gap to the vehicle in front.
///
/// Until 2026-09-27 no vehicle knew any other was there: two cars on the same lane with different
/// grip caught each other and drove through. Each now keeps a gap by the Intelligent Driver Model
/// (Treiber, Hennecke and Helbing 2000), whose braking term is
///
///   a_follow = a (1 - (s* / s)^2),   s* = s0 + max(0, v T + v (v - v_lead) / (2 sqrt(a b)))
///
/// with s the gap to the lead vehicle's tail, T the time headway and s0 the gap left standing (both
/// from the map's StreetLife), a this vehicle's acceleration and b its comfortable braking. The free
/// road term of the IDM is the speed the vehicle already chases (the line's limit, the next stop), so
/// following only ever takes speed away: the vehicle does whichever is slower.
///
/// Only on a map with street life. On the speedway the cars are racing; queueing behind a slower car
/// would turn every race into a procession.
/// </summary>
public partial class VehicleSystem
{
    /// <summary>Who laps which lane of which track, in order round it. Rebuilt every tick.</summary>
    private readonly Dictionary<(string Map, string Track, int Lane), List<DemoVehicle>> _lanes = new();

    private void IndexLanes(string mapId)
    {
        foreach (var list in _lanes.Values) list.Clear();
        if (!_streetLife.ContainsKey(mapId)) return;
        foreach (var v in _vehicles)
        {
            if (v.MapId != mapId || v.Line == null || !InLane(v)) continue;
            var key = (mapId, v.TrackId, (int)MathF.Round(v.LaneOffset * 2f));
            if (!_lanes.TryGetValue(key, out var list)) _lanes[key] = list = new List<DemoVehicle>();
            list.Add(v);
        }
        foreach (var list in _lanes.Values) list.Sort((a, b) => a.Lap.CompareTo(b.Lap));
    }

    /// <summary>Whether it occupies its lane: not parked at the kerb.</summary>
    private static bool InLane(DemoVehicle v) => v.Park is not { Phase: ParkPhase.Parked };

    /// <summary>The vehicle ahead in the same lane, and the gap to its tail, metres.</summary>
    private (DemoVehicle Lead, float Gap)? Ahead(DemoVehicle v)
    {
        if (!_lanes.TryGetValue((v.MapId, v.TrackId, (int)MathF.Round(v.LaneOffset * 2f)), out var list) || list.Count < 2)
            return null;
        int i = list.IndexOf(v);
        if (i < 0) return null;
        var lead = list[(i + 1) % list.Count];
        float gap = lead.Lap - v.Lap;
        if (gap <= 0f) gap += v.Line!.Length;
        return (lead, gap - 0.5f * (lead.LengthMetres + v.LengthMetres));
    }

    /// <summary>Takes this tick's speed down to what the gap ahead allows.</summary>
    private void Follow(DemoVehicle v, float wasSpeed, float dt)
    {
        if (!_streetLife.TryGetValue(v.MapId, out var life) || Ahead(v) is not { } ahead) return;
        float a = MathF.Max(0.1f, v.Accel), b = MathF.Max(0.1f, v.Brake);
        float s0 = life.FollowMinGapMetres, headway = life.FollowHeadwaySeconds;
        float s = MathF.Max(0.1f, ahead.Gap);
        float sStar = s0 + MathF.Max(0f, wasSpeed * headway + wasSpeed * (wasSpeed - ahead.Lead.Speed) / (2f * MathF.Sqrt(a * b)));
        float accel = a * (1f - (sStar / s) * (sStar / s));
        // Never harder than the tyres give: past that, the IDM's number is not a thing a car can do.
        accel = MathF.Max(accel, -MathF.Max(b, v.Grip * 9.81f));
        float allowed = MathF.Max(0f, wasSpeed + accel * dt);
        if (allowed < v.Speed) v.Speed = allowed;
        // Nose already at the tail: no faster than the vehicle in front.
        if (ahead.Gap <= 0.5f) v.Speed = MathF.Min(v.Speed, ahead.Lead.Speed);
    }

    /// <summary>Tests: every lane's vehicles in order, with their positions round it.</summary>
    internal IEnumerable<(string Track, float Lane, float Lap, float Length, float LapLength, float Speed)> RacersForTest(string mapId)
    {
        foreach (var v in _vehicles)
            if (v.MapId == mapId && v.Line != null && InLane(v))
                yield return (v.TrackId, v.LaneOffset, v.Lap, v.LengthMetres, v.Line.Length, v.Speed);
    }
}

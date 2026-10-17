using OpenFPS.Common;
using OpenFPS.Server.Repositories;

namespace OpenFPS.Server.Systems;

/// <summary>
/// Keeping a gap to the vehicle in front (CarFollowing, the Intelligent Driver Model): who is in front, by
/// lane, and how far. Only on a map with street life: on the speedway, queueing would turn every race into
/// a procession.
/// </summary>
public sealed partial class VehicleSystem
{
    /// <summary>Who laps which lane of which track, in order round it. Rebuilt every tick.</summary>
    private readonly Dictionary<(string Map, string Track, int Lane), List<DemoVehicle>> _lanes = new();

    private void IndexLanes(string mapId)
    {
        foreach (var list in _lanes.Values) list.Clear();
        if (!_streetLife.ContainsKey(mapId)) return;
        foreach (var v in _vehicles)
        {
            if (v.MapId != mapId || v.Line == null || v.Route != null || !InLane(v)) continue;
            var key = (mapId, v.TrackId, (int)MathF.Round(v.LaneOffset * 2f));
            if (!_lanes.TryGetValue(key, out var list)) _lanes[key] = list = new List<DemoVehicle>();
            list.Add(v);
        }
        foreach (var list in _lanes.Values) list.Sort((a, b) => a.Lap.CompareTo(b.Lap));
        IndexSegments(mapId);
        IndexJunctions(mapId);
    }

    /// <summary>Whether it occupies its lane: not parked at the kerb.</summary>
    private static bool InLane(DemoVehicle v) => v.Park is not { Phase: ParkPhase.Parked } && !v.Gone;

    /// <summary>The vehicle ahead in the same lane, and the gap to its tail, metres.</summary>
    private (DemoVehicle Lead, float Gap)? Ahead(DemoVehicle v)
    {
        if (v.Route != null) return AheadOnRoute(v);
        if (!_lanes.TryGetValue((v.MapId, v.TrackId, (int)MathF.Round(v.LaneOffset * 2f)), out var list) || list.Count < 2)
            return null;
        int i = list.IndexOf(v);
        if (i < 0) return null;
        var lead = list[(i + 1) % list.Count];
        float gap = lead.Lap - v.Lap;
        if (gap <= 0f) gap += v.Line!.Length;
        return (lead, gap - 0.5f * (lead.LengthMetres + v.LengthMetres));
    }

    /// <summary>How much drivers on this map are holding back for the weather (CarFollowing.RainCaution),
    /// from the water on its roads.</summary>
    internal static (float Speed, float Headway) RainCaution(string mapId, StreetLifeData life)
        => CarFollowing.RainCaution(RoadWaterSystem.WaterOf(mapId), life);

    /// <summary>Takes this tick's speed down to what the gap ahead allows.</summary>
    private void Follow(DemoVehicle v, float wasSpeed, float dt)
    {
        if (!_streetLife.TryGetValue(v.MapId, out var life) || Ahead(v) is not { } ahead) return;
        float allowed = CarFollowing.Allowed(life, RainCaution(v.MapId, life).Headway, wasSpeed, v.Accel, v.Brake,
                                             StreetEvents.HardestBrake(v.Grip, v.Gravity), ahead.Gap, ahead.Lead.Speed,
                                             ahead.Lead.Wheels?.Ax ?? 0f, dt);
        if (allowed < v.Speed) v.Speed = allowed;
    }

    /// <summary>Tests: every vehicle driving a line, where it is in the world and which way it points.</summary>
    internal IEnumerable<(string Name, string Preset, System.Numerics.Vector3 Position, float Heading, float Length, float Speed,
                          int Laps, bool OnRoute, string StopKind, bool Dwelling)> DriversForTest(string mapId, Arch.Core.World world)
    {
        foreach (var v in _vehicles)
        {
            if (v.MapId != mapId || v.Line == null || !InLane(v) || !world.IsAlive(v.Entity)) continue;
            var t = world.Get<OpenFPS.Common.Components.Transform>(v.Entity);
            v.Line.Sample(v.Lap, out _, out float heading, out _);
            if (v.Driver != null) heading += v.Driver.HeadingError;
            string kind = v.Stops.Length > 0 ? v.Stops[v.NextStop].Kind : "";
            yield return (v.DisplayName, v.Preset, t.Position, heading, v.LengthMetres, v.Speed, v.Laps, v.Route != null, kind, v.DwellLeft > 0f);
        }
    }

    /// <summary>Tests: every vehicle on its wheels, with the driver steering it (null where it is held
    /// to its line).</summary>
    internal IEnumerable<(string Name, string Preset, float Speed, float Lap, float TyreDemand, WheelDynamics Wheels, LineFollower? Driver,
                          Arch.Core.Entity Entity, RaceLine? Line, float CorneringG, float KerbShift)>
        WheelsForTest(string mapId)
    {
        foreach (var v in _vehicles)
            if (v.MapId == mapId && v.Wheels != null)
                yield return (v.DisplayName, v.Preset, v.Speed, v.Lap, v.TyreDemand, v.Wheels, v.Driver, v.Entity, v.Line, v.CorneringG, v.KerbShift);
    }
}

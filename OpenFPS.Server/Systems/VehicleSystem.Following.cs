using OpenFPS.Common;
using OpenFPS.Server.Repositories;

namespace OpenFPS.Server.Systems;

/// <summary>
/// Keeping a gap to the vehicle in front, by the Intelligent Driver Model (Treiber, Hennecke and
/// Helbing 2000), whose braking term is
///
///   a_follow = a (1 - (s* / s)^2),   s* = s0 + max(0, v T + v (v - v_lead) / (2 sqrt(a b)))
///
/// with s the gap to the lead vehicle's tail, T the time headway and s0 the gap left standing (both
/// from the map's StreetLife), a this vehicle's acceleration and b its comfortable braking. The free
/// road term of the IDM is the speed the vehicle already chases (the line's limit, the next stop), so
/// following only ever takes speed away: the vehicle does whichever is slower.
///
/// Only on a map with street life: on the speedway, queueing would turn every race into a procession.
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

    /// <summary>
    /// How much drivers on this map are holding back for the weather: the share of speed they give up and
    /// the share of time headway they add. Wet is the asphalt's texture full (RoadWater), heavy the rain
    /// against Rainfall.HeavyRate; each blends from the wet figures to the heavy ones.
    /// </summary>
    internal static (float Speed, float Headway) RainCaution(string mapId, StreetLifeData life)
    {
        var water = RoadWaterSystem.WaterOf(mapId);
        if (water == null) return (0f, 0f);
        byte asphalt = RoadSurfaces.IndexOf(RoadData.DefaultSurface);
        float wet = Math.Clamp(water.TextureMm(asphalt) / MathF.Max(0.05f, RoadWaterLaw.HoldsMm(asphalt)), 0f, 1f);
        float heavy = Math.Clamp(water.RainMmPerHour / Rainfall.HeavyRate, 0f, 1f);
        float speed = wet * life.WetSpeedReduction + heavy * MathF.Max(0f, life.HeavyRainSpeedReduction - life.WetSpeedReduction);
        float headway = wet * life.WetHeadwayIncrease + heavy * MathF.Max(0f, life.HeavyRainHeadwayIncrease - life.WetHeadwayIncrease);
        return (Math.Clamp(speed, 0f, 0.5f), MathF.Max(0f, headway));
    }

    /// <summary>Takes this tick's speed down to what the gap ahead allows.</summary>
    private void Follow(DemoVehicle v, float wasSpeed, float dt)
    {
        if (!_streetLife.TryGetValue(v.MapId, out var life) || Ahead(v) is not { } ahead) return;
        float a = MathF.Max(0.1f, v.Accel), b = MathF.Max(0.1f, v.Brake);
        float s0 = life.FollowMinGapMetres, headway = life.FollowHeadwaySeconds * (1f + RainCaution(v.MapId, life).Headway);
        float s = MathF.Max(0.1f, ahead.Gap);
        float sStar = s0 + MathF.Max(0f, wasSpeed * headway + wasSpeed * (wasSpeed - ahead.Lead.Speed) / (2f * MathF.Sqrt(a * b)));
        float idm = a * (1f - (sStar / s) * (sStar / s));
        float accel = Acc(idm, wasSpeed, ahead.Lead.Speed, ahead.Lead.Wheels?.Ax ?? 0f, s, a, b);
        // No harder than a staged hard stop, which leaves the wheels turning.
        accel = MathF.Max(accel, -MathF.Max(b, HardBrakeGripFraction * v.Grip * 9.81f));
        float allowed = MathF.Max(0f, wasSpeed + accel * dt);
        if (allowed < v.Speed) v.Speed = allowed;
    }

    /// <summary>
    /// The ACC model of Kesting, Treiber and Helbing ("Enhanced intelligent driver model to access the
    /// impact of driving strategies on traffic capacity", Phil. Trans. R. Soc. A 368, 2010; Treiber and
    /// Kesting, Traffic Flow Dynamics, 2013, section 11.3.6): the IDM's braking with its overreaction
    /// taken out. Below its desired gap the IDM brakes as if the lead might stop dead at any moment,
    /// so creeping up a queue at walking pace it stamps on the brakes. The constant-acceleration
    /// heuristic says what the situation really asks, assuming the lead keeps its acceleration:
    ///
    ///   a_CAH = v^2 a~ / (v_l^2 - 2 s a~)            if v_l (v - v_l) &lt;= -2 s a~
    ///         = a~ - (v - v_l)^2 Theta(v - v_l) / (2 s)   otherwise,        a~ = min(a_l, a)
    ///
    /// and where the IDM asks for more braking than that, the two are blended:
    ///
    ///   a_ACC = (1 - c) a_IDM + c [a_CAH + b tanh((a_IDM - a_CAH) / b)],   c = 0.99
    ///
    /// so the braking stays near the comfortable b unless the situation really is critical.
    /// </summary>
    internal static float Acc(float idm, float v, float vLead, float aLead, float s, float a, float b)
    {
        const float Coolness = 0.99f;
        float at = MathF.Min(aLead, a);
        float cah;
        float denominator = vLead * vLead - 2f * s * at;
        if (vLead * (v - vLead) <= -2f * s * at && denominator > 1e-3f) cah = v * v * at / denominator;
        else cah = at - (v > vLead ? (v - vLead) * (v - vLead) : 0f) / (2f * MathF.Max(0.1f, s));
        if (idm >= cah) return idm;
        return (1f - Coolness) * idm + Coolness * (cah + b * MathF.Tanh((idm - cah) / b));
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

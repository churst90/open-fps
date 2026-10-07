using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Server.Core;
using Serilog;

namespace OpenFPS.Server.Systems;

/// <summary>
/// Somebody on foot crossing a road. Nothing on the map declares a crossing: where a walker's line
/// passes over a carriageway is one, found at load. With no signals yet, every crossing is uncontrolled:
///
/// - The walker stops at the kerb and waits for a gap in the traffic long enough to walk across, the
///   Highway Capacity Manual's pedestrian critical headway t_c = L / S_p + t_s (StreetLifeData). A
///   vehicle that is stopping for the crossing is not a vehicle in the way.
/// - A driver stops for anybody on the crossing, and for somebody who has stood at the kerb for a while
///   (StreetLifeData.PedestrianAssertSeconds), if it can stop at its ordinary braking. One that cannot
///   stop goes through; the walker's gap check is what keeps that from meeting anyone. One that has
///   started stopping keeps stopping, harder if it must.
/// - A driver waiting at a junction's line stands short of a crossing there, not on it.
/// </summary>
public sealed partial class VehicleSystem
{
    /// <summary>One crossing: a road, and the kerb-to-kerb line across it that walkers take.</summary>
    private sealed class Crosswalk
    {
        public required string MapId;
        public required RoadData Road;
        /// <summary>The two kerbs, on the walkers' line.</summary>
        public required Vector3 A, B;
        /// <summary>Half the width of the strip walkers use, along the road, metres.</summary>
        public float HalfBand = 0.5f;
        /// <summary>Route vehicles whose line crosses this one, and where, in their line's metres.</summary>
        public readonly List<(DemoVehicle V, float At)> Traffic = new();

        // This tick.
        public int OnIt;
        public float LongestWait;

        public float Length => Vector3.Distance(Flat(A), Flat(B));
    }

    private readonly List<Crosswalk> _crosswalks = new();

    /// <summary>Metres a walker stands back from the carriageway's edge while waiting.</summary>
    private const float KerbStandBackMetres = 0.3f;
    /// <summary>Metres a vehicle's nose stays short of a crossing it stops for.</summary>
    private const float CrosswalkStandOffMetres = 1f;
    /// <summary>A walker's line that runs further than this inside a carriageway is walking along the
    /// road, not across it, and is left alone.</summary>
    private const float LongestCrossingMetres = 40f;
    /// <summary>Two walkers' crossings of one road this close along it are one crossing.</summary>
    private const float SameCrossingMetres = 3f;
    /// <summary>How far past each kerb a vehicle's line still counts as crossing a crossing, metres.</summary>
    private const float KerbOverrunMetres = 1.5f;
    /// <summary>How far past where it meant to stand a driver stopping for a crossing may run on its
    /// ordinary braking before braking harder, metres: half the stand-off.</summary>
    private const float StandOverrunMetres = 0.5f;

    private static Vector3 Flat(Vector3 v) => new(v.X, 0f, v.Z);

    /// <summary>Finds every crossing on a map from its walkers and roads, and tells both sides.</summary>
    private void BuildCrosswalks(MapManager maps, string mapId)
    {
        if (!maps.TryGetRoads(mapId, out var net) || net.Roads.Count == 0) return;
        int before = _crosswalks.Count;
        foreach (var w in _vehicles)
        {
            if (w.MapId != mapId || !w.IsWalker || w.Line != null) continue;
            var found = new List<(Crosswalk, float, float)>();
            foreach (var road in net.Roads)
                foreach (var (from, to) in InsideCarriageway(road, w.A, w.B))
                {
                    if (to - from > LongestCrossingMetres) continue;
                    var dir = Vector3.Normalize(w.B - w.A);
                    Vector3 ka = w.A + dir * from, kb = w.A + dir * to;
                    var cw = FindOrAddCrosswalk(mapId, road, ka, kb);
                    found.Add((cw, from, to));
                }
            w.Crossings = found.OrderBy(c => c.Item2).ToArray();
        }
        var mine = _crosswalks.Skip(before).ToList();
        if (mine.Count == 0) return;

        // On the line each vehicle actually drives, every half metre: the route's points, scaled onto the
        // smoothed line, are out by metres at a corner, which is where every crossing is.
        foreach (var v in _vehicles)
        {
            if (v.MapId != mapId || v.Route == null || v.Line == null) continue;
            var pts = new List<Vector3>();
            for (float d = 0f; d < v.Line.Length; d += 0.5f)
            {
                v.Line.Sample(d, out var p, out _, out _);
                pts.Add(p);
            }
            var events = new List<(float, Crosswalk)>();
            foreach (var cw in mine)
            {
                // Out past the kerbs too: a car turning a corner cuts across the pavement's edge, and a
                // walker waiting stands at it.
                var across = Vector3.Normalize(Flat(cw.B - cw.A));
                Vector3 ka = cw.A - across * KerbOverrunMetres, kb = cw.B + across * KerbOverrunMetres;
                float reach = cw.Length * 0.5f + KerbOverrunMetres + 1f;
                var mid = (cw.A + cw.B) * 0.5f;
                for (int i = 0; i < pts.Count; i++)
                {
                    Vector3 p = pts[i], q = pts[(i + 1) % pts.Count];
                    if (MathF.Abs(p.X - mid.X) > reach || MathF.Abs(p.Z - mid.Z) > reach) continue;
                    if (!Meet(p, q, ka, kb, out float t)) continue;
                    float at = (i + t) * 0.5f;
                    events.Add((at, cw));
                    cw.Traffic.Add((v, at));
                }
            }
            v.Crosswalks = events.OrderBy(e => e.Item1).ToArray();
        }
        Log.Information("Map {Map}: {Count} pedestrian crossing(s), taken by {Walkers} walker(s).",
                        mapId, mine.Count, _vehicles.Count(w => w.MapId == mapId && w.Crossings.Length > 0));
    }

    private Crosswalk FindOrAddCrosswalk(string mapId, RoadData road, Vector3 a, Vector3 b)
    {
        var mid = (a + b) * 0.5f;
        float along = RoadNetwork.Project(road.Centreline, mid).Along;
        foreach (var cw in _crosswalks)
        {
            if (cw.MapId != mapId || cw.Road != road) continue;
            float there = RoadNetwork.Project(road.Centreline, (cw.A + cw.B) * 0.5f).Along;
            if (MathF.Abs(there - along) > SameCrossingMetres) continue;
            // Widen the strip to take this line too.
            cw.HalfBand = MathF.Max(cw.HalfBand, MathF.Abs(there - along) + 0.5f);
            return cw;
        }
        var made = new Crosswalk { MapId = mapId, Road = road, A = a, B = b };
        _crosswalks.Add(made);
        return made;
    }

    /// <summary>The stretches of the line a to b that lie on a road's carriageway, metres from a.</summary>
    private static List<(float From, float To)> InsideCarriageway(RoadData road, Vector3 a, Vector3 b, float step = 0.25f)
    {
        var o = new List<(float, float)>();
        if (road.Centreline.Count < 2) return o;
        float length = Vector3.Distance(Flat(a), Flat(b));
        float roadLength = RoadNetwork.Length(road.Centreline);
        float half = road.WidthMetres * 0.5f;
        var dir = length > 1e-4f ? (b - a) / length : Vector3.Zero;
        float? start = null;
        int n = (int)(length / step);
        for (int i = 0; i <= n + 1; i++)
        {
            float d = MathF.Min(i * step, length);
            bool inside = false;
            if (i <= n)
            {
                var (along, off) = RoadNetwork.Project(road.Centreline, a + dir * d);
                inside = off < half && along > 0.01f && along < roadLength - 0.01f;
            }
            if (inside && start == null) start = d;
            else if (!inside && start != null) { o.Add((start.Value, d - step)); start = null; }
        }
        return o;
    }

    private static bool Meet(Vector3 p, Vector3 q, Vector3 a, Vector3 b, out float t)
    {
        t = 0f;
        float rx = q.X - p.X, rz = q.Z - p.Z, sx = b.X - a.X, sz = b.Z - a.Z;
        float den = rx * sz - rz * sx;
        if (MathF.Abs(den) < 1e-9f) return false;
        float ax = a.X - p.X, az = a.Z - p.Z;
        t = (ax * sz - az * sx) / den;
        float u = (ax * rz - az * rx) / den;
        return t >= 0f && t < 1f && u >= 0f && u <= 1f;
    }

    /// <summary>Who is on each crossing, and how long anyone has waited at its kerbs, this tick.</summary>
    private void IndexCrosswalks(string mapId)
    {
        foreach (var cw in _crosswalks)
            if (cw.MapId == mapId) { cw.OnIt = 0; cw.LongestWait = 0f; }
        foreach (var w in _vehicles)
        {
            if (w.MapId != mapId || w.Crossings.Length == 0) continue;
            if (w.ClearedFor != null) w.ClearedFor.OnIt++;
            if (w.WaitingFor != null) w.WaitingFor.LongestWait = MathF.Max(w.WaitingFor.LongestWait, w.KerbWait);
        }
    }

    /// <summary>
    /// A walker coming up to a crossing: the fastest it may walk to stand at the kerb, m/s, or MaxValue
    /// if it has the crossing (or none is ahead). Decides at the kerb whether to go.
    /// </summary>
    private float KerbHold(DemoVehicle w, float total, float dt)
    {
        bool outward = w.Pass % 2 == 0;
        // The next crossing not yet behind it, in this direction's metres.
        Crosswalk? cw = null;
        float from = 0f, to = 0f;
        foreach (var (c, f, t) in outward ? w.Crossings : Enumerable.Reverse(w.Crossings))
        {
            float cf = outward ? f : total - t, ct = outward ? t : total - f;
            if (ct <= w.Progress) continue;
            cw = c; from = cf; to = ct;
            break;
        }
        if (w.ClearedFor != null && w.ClearedFor != cw) w.ClearedFor = null;       // over it
        w.OnCarriageway = cw != null && w.Progress >= from && w.Progress <= to;
        if (cw == null || w.ClearedFor == cw) return float.MaxValue;

        float stand = from - KerbStandBackMetres;
        // Already on it (it started there): carry on across.
        if (w.Progress > stand + 0.1f) { w.ClearedFor = cw; w.WaitingFor = null; return float.MaxValue; }
        float left = MathF.Max(0f, stand - w.Progress);
        if (left < 0.1f && w.Speed < 0.2f)
        {
            if (w.WaitingFor != cw) { w.WaitingFor = cw; w.KerbWait = 0f; }
            w.KerbWait += dt;
            float pace = w.SpeedsMetresPerSecond[w.Pass % w.SpeedsMetresPerSecond.Length];
            if (GapToCross(w, cw, (to - from) / MathF.Max(0.3f, pace)))
            {
                w.ClearedFor = cw;
                w.WaitingFor = null;
                w.KerbWait = 0f;
                return float.MaxValue;
            }
        }
        return MathF.Sqrt(2f * w.Brake * left);
    }

    /// <summary>Whether nothing will reach the crossing before somebody taking this long has crossed it.</summary>
    private bool GapToCross(DemoVehicle w, Crosswalk cw, float walkSeconds)
    {
        if (!_streetLife.TryGetValue(w.MapId, out var life)) return true;
        float startUp = life.PedestrianStartUpSeconds;
        float need = walkSeconds + (w.KerbWait > life.PedestrianRiskSeconds ? 0f : startUp);
        foreach (var (v, at) in cw.Traffic)
        {
            if (!InLane(v) || v.Line == null) continue;
            float ahead = at - v.Lap;
            if (ahead < -v.Line.Length * 0.5f) ahead += v.Line.Length;
            // Its body over the crossing, or about to be.
            float reach = 0.5f * v.LengthMetres + cw.HalfBand + 0.5f;
            if (ahead < -reach) continue;                               // gone past
            if (ahead <= reach) return false;                           // on it
            // Stopping for us; but one that has stood there a while gets its turn, and nobody new steps out.
            if (v.StoppingFor == cw) { if (v.CrosswalkWait > startUp + life.PedestrianAssertSeconds) return false; continue; }
            // Until its body reaches the strip, not its middle: a bus crawling round a corner with its
            // nose at the strip is there at once (traced 2026-10-02). One standing still is not coming.
            if ((ahead - reach) / MathF.Max(v.Speed, 1e-3f) < need) return false;
        }
        return true;
    }

    /// <summary>
    /// How far a route vehicle may still go before it must be standing short of a crossing somebody is
    /// on, metres, or MaxValue; and how hard it may brake to stand there, m/s^2.
    ///
    /// The nearest of all of them: across the lap's seam the first in the list is not the nearest.
    /// A driver already stopping keeps stopping, braking harder up to an emergency stop if it runs past
    /// its mark, because the walkers stepped out on the strength of it (both traced 2026-10-02).
    /// </summary>
    private float CrosswalkHold(DemoVehicle v, float dt, out float decel)
    {
        var was = v.StoppingFor;
        v.StoppingFor = null;
        decel = v.Brake;
        if (v.Crosswalks.Length == 0 || !_streetLife.TryGetValue(v.MapId, out var life)) return float.MaxValue;
        var line = v.Line!;
        float stopping = v.Speed * v.Speed / (2f * MathF.Max(0.1f, v.Brake));
        float look = stopping + 30f;
        // Somebody waiting at a kerb is let across only before the junction: a driver stopping inside one
        // for the far kerb locked the whole junction (traced 2026-09-28).
        float laneLeft = -1f;
        if (v.Route != null)
        {
            var (leg, along) = WhereOnLane(v);
            if (along >= 0f) laneLeft = v.Route.Legs[leg].Segment.LengthMetres - along + 2f;
        }
        Crosswalk? stopFor = null;
        float nearest = float.MaxValue;
        foreach (var (at, cw) in v.Crosswalks)
        {
            float ahead = at - v.Lap;
            if (ahead < -line.Length * 0.5f) ahead += line.Length;
            if (ahead < 0f || ahead > look) continue;
            // Somebody waiting at the kerb is let across by a driver who has not already stood a while.
            bool turnTaken = v.CrosswalkWait > life.PedestrianStartUpSeconds + life.PedestrianAssertSeconds;
            bool letAcross = ahead <= laneLeft && !turnTaken && cw.LongestWait > life.PedestrianAssertSeconds;
            bool somebody = cw.OnIt > 0 || letAcross;
            if (!somebody) continue;
            float stand = ahead - cw.HalfBand - CrosswalkStandOffMetres - 0.5f * v.LengthMetres;
            float brake = v.Brake;
            if (cw == was)
            {
                // Already stopping for it: as hard as standing no more than a little past its mark takes.
                float room = stand + StandOverrunMetres;
                float need = room > 0.05f ? v.Speed * v.Speed / (2f * room) : float.MaxValue;
                brake = Math.Clamp(need, v.Brake, MathF.Max(v.Brake, HardBrakeGripFraction * v.Grip * 9.81f));
            }
            else
            {
                if (stand < -0.5f) continue;                             // its nose is already over
                // Too close to stop at its ordinary braking: through it goes (the walker checked for this).
                if (stand < stopping - 0.5f) continue;
            }
            if (stand < nearest) { nearest = stand; stopFor = cw; decel = brake; }
        }
        if (stopFor == null)
        {
            v.CrosswalkWait = 0f;
            decel = v.Brake;
            return float.MaxValue;
        }
        v.StoppingFor = stopFor;
        v.CrosswalkWait = was != null ? v.CrosswalkWait + (v.Speed < 0.3f ? dt : 0f) : 0f;
        return MathF.Max(0f, nearest);
    }

    /// <summary>Where a vehicle waiting at a junction line must stand instead, to keep off a crossing
    /// that lies before the line; MaxValue if none does.</summary>
    private float ShortOfCrosswalk(DemoVehicle v, float toLine)
    {
        float best = float.MaxValue;
        if (v.Line == null) return best;
        foreach (var (at, cw) in v.Crosswalks)
        {
            float ahead = at - v.Lap;
            if (ahead < -v.Line.Length * 0.5f) ahead += v.Line.Length;
            if (ahead < toLine - 8f || ahead > toLine + 2f) continue;
            float stand = ahead - cw.HalfBand - CrosswalkStandOffMetres - 0.5f * v.LengthMetres;
            if (stand >= 0f) best = MathF.Min(best, stand);
        }
        return best;
    }

    /// <summary>Tests: every walker, where it is, and the crossing it is on or waiting at.</summary>
    internal IEnumerable<(string Name, Vector3 Position, bool Crossing, bool OnCarriageway, bool Waiting, float Waited, int Crossings)> WalkersForTest(string mapId, Arch.Core.World world)
    {
        foreach (var w in _vehicles)
        {
            if (w.MapId != mapId || !w.IsWalker || !world.IsAlive(w.Entity)) continue;
            var t = world.Get<OpenFPS.Common.Components.Transform>(w.Entity);
            yield return (w.DisplayName, t.Position, w.ClearedFor != null, w.OnCarriageway, w.WaitingFor != null, w.KerbWait, w.Crossings.Length);
        }
    }

    /// <summary>Tests: what the traffic at the crossing nearest a point is doing.</summary>
    internal IEnumerable<string> CrosswalkStateForTest(string mapId, Vector3 near)
    {
        var cw = _crosswalks.Where(c => c.MapId == mapId).OrderBy(c => Vector3.Distance(Flat((c.A + c.B) * 0.5f), Flat(near))).FirstOrDefault();
        if (cw == null) yield break;
        yield return $"crossing of {cw.Road.Id} {cw.A} -> {cw.B}, half band {cw.HalfBand:F1}, on it {cw.OnIt}, longest wait {cw.LongestWait:F0} s";
        foreach (var (v, at) in cw.Traffic)
        {
            float ahead = at - v.Lap;
            if (ahead < -v.Line!.Length * 0.5f) ahead += v.Line.Length;
            if (MathF.Abs(ahead) > 60f) continue;
            yield return $"  {v.DisplayName}: ahead {ahead:F1} m, {v.Speed:F1} m/s, stopping for it {v.StoppingFor == cw} ({v.CrosswalkWait:F0} s), "
                       + $"holding at junction {v.Holding} ({v.WaitingAt} {v.WaitedSeconds:F0} s), dwell {v.DwellLeft:F1}, park {v.Park?.Phase}";
            if (v.WaitingAt != null && _atJunction.TryGetValue((mapId, v.WaitingAt), out var here))
                foreach (var (o, m) in here.Inside)
                    yield return $"    inside {v.WaitingAt}: {o.DisplayName} {o.Speed:F1} m/s, stopping for a crossing {o.StoppingFor != null} ({o.CrosswalkWait:F0} s), "
                               + $"on it {o.StoppingFor?.OnIt}, waited at kerb {o.StoppingFor?.LongestWait:F0}, holding {o.Holding}";
        }
    }

    /// <summary>Tests: every crossing's two kerbs and the half-width of its strip.</summary>
    internal IEnumerable<(Vector3 A, Vector3 B, float HalfBand)> CrosswalksForTest(string mapId)
        => _crosswalks.Where(c => c.MapId == mapId).Select(c => (c.A, c.B, c.HalfBand));
}

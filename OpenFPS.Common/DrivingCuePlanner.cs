using System.Numerics;

namespace OpenFPS.Common;

/// <summary>What the brake cue is about.</summary>
public enum CueHazard { None, Bend, Turn, GiveWay, Stop, RoadEnd, Crossing }

/// <summary>
/// Where the brake cue changes, as a share of the tyres' grip (<see cref="DrivingCuePlan.BrakeRatio"/>).
/// On a dry road (0.95 g) the steps are about 1.1, 2.3 and 4.7 m/s²: a lift, ordinary firm braking
/// (0.25 g, what a driver does for a junction), hard braking, and from 0.85 near the limit. A wet road
/// gives less grip, so the same braking is a larger share of it and the cue rises sooner.
/// </summary>
public static class DrivingCueBands
{
    public const float Lift = 0.12f, Brake = 0.25f, Hard = 0.5f, Limit = 0.85f;

    /// <summary>0 silent, 1 lift, 2 brake, 3 brake hard, 4 at the limit.</summary>
    public static int Of(float ratio)
        => ratio < Lift ? 0 : ratio < Brake ? 1 : ratio < Hard ? 2 : ratio < Limit ? 3 : 4;
}

/// <summary>One update of <see cref="DrivingCuePlanner"/>: the line ahead and what is on it.</summary>
public sealed class DrivingCuePlan
{
    /// <summary>On a lane, in a junction between two, or on a track. False: off the road network.</summary>
    public bool Located;
    public bool InJunction;
    public bool OnTrack;
    public string RoadName = "";
    /// <summary>The lane's speed limit, m/s; 0 where there is none (a track).</summary>
    public float SpeedLimitMps;

    /// <summary>Where to steer: the planned line this far ahead (<see cref="DrivingCuePlanner.GuideDistance"/>).</summary>
    public Vector3 GuidePoint;
    /// <summary>The line ahead, a point a metre, starting at the car.</summary>
    public readonly List<Vector3> Path = new();

    /// <summary>The steadiest braking that reaches every target ahead in time, m/s².</summary>
    public float NeededDecel;
    /// <summary>What the tyres can give on this road now, m/s².</summary>
    public float AvailableDecel;
    /// <summary>The share of the grip the turn the car is in already asks for (speed times yaw rate over
    /// the grip).</summary>
    public float LateralRatio;
    /// <summary>Needed over available (see <see cref="DrivingCueBands"/>): 1 is everything the tyres
    /// have. The larger of that and the turn under the car.</summary>
    public float BrakeRatio => MathF.Max(LateralRatio, AvailableDecel > 1e-3f ? NeededDecel / AvailableDecel : 0f);

    public CueHazard Hazard;
    public Vector3 HazardPoint;
    public float HazardDistance;
    /// <summary>The speed to be at by the hazard, m/s.</summary>
    public float HazardSpeed;

    /// <summary>The next junction on the line, how far to its line (where the lane ends), and the way through it.</summary>
    public JunctionData? Junction;
    public float JunctionDistance = float.MaxValue;
    public bool GivesWay;
    public bool StopControl;
    /// <summary>The way the line goes through it; null while it is not known (no indicator at a T).</summary>
    public Turn? NextTurn;
    public string NextRoad = "";
    /// <summary>Every way out of it from this lane, and the road each one is.</summary>
    public readonly List<(Turn Turn, string Road)> Exits = new();

    public CrossingRails? Crossing;
    /// <summary>To the first rail, metres along the line.</summary>
    public float CrossingDistance = float.MaxValue;
    public bool CrossingClosed;
}

/// <summary>
/// The line ahead of a car on a road network, and how hard it will have to brake to take it.
///
/// No audio and no client: the same roads the traffic drives (<see cref="RoadNetwork"/>), a turn from
/// one lane into the next that a car can make (<see cref="TurnCurve"/>), the same comfortable
/// cornering speed traffic slows to (<see cref="DriverSteering.ComfortTurnSpeed"/>). What it adds is the
/// question a driver who can see answers by looking: at the speed I am doing, how soon and how hard do I
/// need to brake for what is coming — a turn, a give-way line, a closed level crossing, the end of the road.
///
/// It keeps a little memory between updates: the lane the car was last on (so a car in a junction knows
/// which turn it is making) and the indicator.
/// </summary>
public sealed class DrivingCuePlanner
{
    public RoadNetwork? Network { get; }
    public IReadOnlyList<CrossingRails> Crossings { get; }
    private readonly List<(DriveTrack Track, RaceLine Line)> _tracks = new();

    /// <summary>-1 the left indicator is on, +1 the right, 0 neither.</summary>
    public int Indicator { get; set; }

    private RoadNetwork.LaneSegment? _lane;
    private float _laneHeading;

    /// <summary>The speed to be down to at a give-way line, m/s (15 km/h): slow enough to stop in a
    /// car's length if somebody is coming, which is what giving way asks.</summary>
    public const float GiveWaySpeed = 4.2f;

    /// <summary>
    /// How far before the nearest rail a car stops for a closed crossing, metres: the stop line is no
    /// nearer than 15 feet (MUTCD 2009, section 8B.28).
    /// </summary>
    public const float StopLineFromRail = 4.6f;

    /// <summary>Stop this far short of the end of a road, metres.</summary>
    public const float RoadEndMargin = 2f;

    /// <summary>Points on the line, metres apart.</summary>
    public const float Step = 1f;

    /// <summary>The share of the grip a track is driven to: a racing line's limit, with a margin.</summary>
    public const float TrackGripShare = 0.85f;

    public DrivingCuePlanner(RoadMapData data)
    {
        Crossings = data.Crossings;
        if (data.Roads.Count > 0)
        {
            try { Network = new RoadNetwork(data.Roads, data.Junctions); }
            catch (Exception) { Network = null; }
        }
        foreach (var t in data.Tracks)
        {
            try { _tracks.Add((t, new RaceLine(t.Waypoints, 0f, 90f, 1f, 6f, t.BankingDegrees))); }
            catch (ArgumentException) { }
        }
    }

    /// <summary>Where the guide sits ahead of the car, metres: further the faster it goes.</summary>
    public static float GuideDistance(float speed) => Math.Clamp(8f + speed * 0.8f, 8f, 30f);

    /// <summary>
    /// How far along the line the guide sits: <see cref="GuideDistance"/> on the straight, and closer
    /// round a tight turn, no more than 0.7 of the tightest radius within that reach. Steering for a
    /// point a long way round a corner cuts it: twelve metres ahead on a six-metre turn put a car's wheels
    /// five metres over the kerb inside (DrivingCueDrillTests); at 0.7 of the radius the cut is a
    /// third of a metre.
    /// </summary>
    public static float GuideReach(IReadOnlyList<Vector3> path, float speed)
    {
        float reach = GuideDistance(speed);
        float along = 0f, tightest = float.MaxValue;
        for (int i = 2; i < path.Count - 2 && along <= reach; i++)
        {
            along += Flat(path[i] - path[i - 1]).Length();
            float k = Menger(path[i - 2], path[i], path[i + 2]);
            if (k > 1e-4f) tightest = MathF.Min(tightest, 1f / k);
        }
        return tightest < float.MaxValue ? Math.Clamp(0.7f * tightest, 4f, reach) : reach;
    }

    /// <summary>How far ahead the line is planned, metres: three seconds and a gentle stop, at least forty.</summary>
    public static float LookAhead(float speed) => Math.Clamp(speed * 3f + speed * speed / 3f, 40f, 250f);

    /// <param name="at">The car's middle, on the ground.</param>
    /// <param name="forward">The way it points.</param>
    /// <param name="speed">How fast it is going, m/s.</param>
    /// <param name="availableDecel">What its tyres can give on this road now, m/s² (grip times g, wet or dry).</param>
    /// <param name="halfLength">Half the car's length, metres: its nose is this far ahead of its middle.</param>
    /// <param name="closed">Whether a crossing's bells are ringing; null for none ever.</param>
    /// <param name="yawRate">How fast the car is turning now, rad/s: with the speed, what the turn it is
    /// in asks of the tyres (<see cref="DrivingCuePlan.LateralRatio"/>). Zero when not known.</param>
    public DrivingCuePlan Update(Vector3 at, Vector3 forward, float speed, float availableDecel, float halfLength,
                                 Func<CrossingRails, bool>? closed = null, float yawRate = 0f)
    {
        var plan = new DrivingCuePlan { AvailableDecel = MathF.Max(0.1f, availableDecel) };
        plan.LateralRatio = MathF.Abs(speed * yawRate) / plan.AvailableDecel;
        forward = Flat(forward);
        if (forward.LengthSquared() < 1e-6f) forward = Vector3.UnitZ;
        forward = Vector3.Normalize(forward);
        speed = MathF.Max(0f, speed);
        float reach = LookAhead(speed);

        // Where the turns and the other targets fall on the line, as distances along it.
        var turnSpans = new List<(float From, float To)>();
        var targets = new List<(float At, float Speed, CueHazard Kind)>();

        if (Network != null && Locate(at, forward, out var lane, out float along))
        {
            plan.Located = true;
            if (!ReferenceEquals(lane, _lane)) { _lane = lane; _laneHeading = Heading(lane, along); }
            plan.RoadName = lane.Road.Name;
            plan.SpeedLimitMps = lane.Lane.SpeedLimitKmh / 3.6f;
            // The line starts on the lane beside the car: from the car itself, a car a metre off the middle
            // would put a kink in the first metre of its own line and read it as a hairpin.
            AppendSlice(plan.Path, lane.Path, along, lane.LengthMetres);
            Continue(plan, lane, reach, turnSpans, targets, halfLength);
        }
        else if (Network != null && _lane?.To is { } j && Near(at, j))
        {
            // Between two lanes in a junction: the turn the car is making, from where it is now.
            plan.Located = true;
            plan.InJunction = true;
            plan.RoadName = _lane.Road.Name;
            plan.SpeedLimitMps = _lane.Lane.SpeedLimitKmh / 3.6f;
            var exit = ChooseExit(_lane, TurnedSoFar(forward));
            if (exit != null)
            {
                // From the point of the turn beside the car, on round it.
                var curve = TurnCurve(_lane.Path, exit.Path, out _, out float skipOut);
                int nearest = Nearest(curve, at);
                for (int k = nearest; k < curve.Count; k++) plan.Path.Add(curve[k]);
                turnSpans.Add((0f, PathLength(plan.Path)));
                plan.NextTurn = TurnOf(_lane, exit);
                plan.NextRoad = exit.Road.Name;
                plan.Junction = j;
                plan.JunctionDistance = 0f;
                AppendSlice(plan.Path, exit.Path, skipOut, exit.LengthMetres);
                Continue(plan, exit, reach, turnSpans, targets, halfLength, skipJunctionInfo: true);
            }
        }
        else if (OnTrack(at, out var track, out float lap, out float offset))
        {
            plan.Located = true;
            plan.OnTrack = true;
            plan.RoadName = track.Track.Id;
            var line = track.Line;
            // Which way round the car is going.
            line.Sample(lap, out _, out float h, out _);
            int dir = Vector3.Dot(new Vector3(MathF.Sin(h), 0f, MathF.Cos(h)), forward) >= 0f ? 1 : -1;
            for (float s = 0f; s <= reach; s += Step)
            {
                line.Sample(lap + dir * s, out var p, out float hh, out _);
                var right = new Vector3(MathF.Cos(hh), 0f, -MathF.Sin(hh));
                plan.Path.Add(p + right * offset);
            }
            // A banked track holds a car up: v^2 = g R (mu + tan b) / (1 - mu tan b).
            float mu = TrackGripShare * plan.AvailableDecel / WheelDynamics.G;
            float tb = MathF.Tan(track.Track.BankingDegrees * MathF.PI / 180f);
            float effective = (mu + tb) / MathF.Max(0.2f, 1f - mu * tb);
            for (int i = 0; i < plan.Path.Count; i++)
            {
                float k = MathF.Abs(line.CurvatureAt(lap + dir * i * Step));
                if (k > 1e-4f) targets.Add((i * Step, MathF.Sqrt(effective * WheelDynamics.G / k), CueHazard.Bend));
            }
        }
        else
        {
            return plan;
        }

        Crossings_(plan, halfLength, closed, targets);
        Curvatures(plan, turnSpans, targets);
        Brake(plan, speed, targets);
        plan.GuidePoint = PointAlong(plan.Path, GuideReach(plan.Path, speed));
        return plan;
    }

    // ── Where the car is ────────────────────────────────────────────────────────────────────────

    /// <summary>The lane the car is in and pointing down: within its width and a metre, facing within
    /// about seventy degrees of its direction. The nearest such lane.</summary>
    private bool Locate(Vector3 at, Vector3 forward, out RoadNetwork.LaneSegment lane, out float along)
    {
        lane = null!; along = 0f;
        float best = float.MaxValue;
        foreach (var s in Network!.Segments)
        {
            var (a, off) = RoadNetwork.Project(s.Path, at);
            if (off > s.Lane.WidthMetres * 0.5f + 1f) continue;
            if ((a <= 0.01f || a >= s.LengthMetres - 0.01f) && off > 0.5f) continue;     // beyond its ends
            var dir = Direction(s.Path, a);
            if (Vector3.Dot(dir, forward) < 0.35f) continue;
            if (off < best) { best = off; lane = s; along = a; }
        }
        return best < float.MaxValue;
    }

    private static bool Near(Vector3 at, JunctionData j)
        => Flat(at - j.Position).Length() < j.RadiusMetres + 4f;

    private bool OnTrack(Vector3 at, out (DriveTrack Track, RaceLine Line) track, out float lap, out float offset)
    {
        track = default; lap = 0f; offset = 0f;
        float best = float.MaxValue;
        foreach (var t in _tracks)
        {
            // Every two metres round: a track is a few hundred nodes.
            for (float s = 0f; s < t.Line.Length; s += 2f)
            {
                t.Line.Sample(s, out var p, out float h, out _);
                var d = Flat(at - p);
                float dist = d.Length();
                if (dist < best && dist < t.Track.WidthMetres * 0.5f + 1f)
                {
                    best = dist; track = t; lap = s;
                    offset = Vector3.Dot(d, new Vector3(MathF.Cos(h), 0f, -MathF.Sin(h)));
                }
            }
        }
        return best < float.MaxValue;
    }

    // ── The line ahead ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// From the end of <paramref name="lane"/> on: through each junction the way the indicator (first) or
    /// the road (after) goes, into the next lane, until the line is long enough.
    /// </summary>
    private void Continue(DrivingCuePlan plan, RoadNetwork.LaneSegment lane, float reach,
                          List<(float, float)> turnSpans, List<(float, float, CueHazard)> targets,
                          float halfLength, bool skipJunctionInfo = false)
    {
        var current = lane;
        bool first = true;
        for (int hop = 0; hop < 4 && PathLength(plan.Path) < reach; hop++)
        {
            float laneEnd = PathLength(plan.Path);
            if (current.To == null)
            {
                // The end of the road: stop short of it.
                targets.Add((MathF.Max(0f, laneEnd - RoadEndMargin - halfLength), 0f, CueHazard.RoadEnd));
                break;
            }
            var j = current.To;
            bool givesWay = j.GivesWay(current.Road);
            bool stop = string.Equals(j.Control, "stop", StringComparison.OrdinalIgnoreCase);
            if (givesWay) targets.Add((MathF.Max(0f, laneEnd - halfLength), stop ? 0f : GiveWaySpeed, stop ? CueHazard.Stop : CueHazard.GiveWay));
            var exit = first && !skipJunctionInfo ? ChooseExit(current, 0f) : Straight(current);
            if (first && !skipJunctionInfo)
            {
                plan.Junction = j;
                plan.JunctionDistance = laneEnd;
                plan.GivesWay = givesWay;
                plan.StopControl = stop && givesWay;
                foreach (var (next, turn) in current.Next)
                    if (!plan.Exits.Any(e => e.Turn == turn)) plan.Exits.Add((turn, next.Road.Name));
                plan.NextTurn = exit == null ? null : TurnOf(current, exit);
                plan.NextRoad = exit?.Road.Name ?? "";
            }
            first = false;
            if (exit == null)
            {
                // Which way is not known (a T with no indicator): the line stops in the middle, and the
                // braking is for the slower of the ways out.
                float slowest = float.MaxValue;
                foreach (var (next, _) in current.Next)
                    slowest = MathF.Min(slowest, TurnSpeed(TurnCurve(current.Path, next.Path, out _, out _)));
                if (slowest < float.MaxValue) targets.Add((laneEnd + j.RadiusMetres * 0.5f, slowest, CueHazard.Turn));
                plan.Path.Add(Flat(j.Position, current.Path[^1].Y));
                break;
            }
            var curve = TurnCurve(current.Path, exit.Path, out float trimIn, out float skipOut);
            // A turn wider than the junction starts before the lane's end: the line leaves the lane there.
            int fromPoint = 1;
            if (trimIn > 0f)
            {
                // Already past where the turn begins: join the arc where the car is.
                if (PathLength(plan.Path) <= trimIn + 0.01f) { fromPoint = Nearest(curve, plan.Path[0]) + 1; plan.Path.RemoveRange(1, plan.Path.Count - 1); }
                else TrimEnd(plan.Path, trimIn);
            }
            float turnStart = PathLength(plan.Path);
            for (int k = fromPoint; k < curve.Count; k++) plan.Path.Add(curve[k]);
            float turnEnd = PathLength(plan.Path);
            if (TurnOf(current, exit) != Turn.Straight) turnSpans.Add((turnStart, turnEnd));
            AppendSlice(plan.Path, exit.Path, skipOut, exit.LengthMetres);
            current = exit;
        }
        Trim(plan.Path, reach);
    }

    /// <summary>The way out of a junction for a car arriving down <paramref name="lane"/>: the indicator's
    /// side, else what the car has already turned (in the junction), else straight on, else the only way.
    /// Null when it cannot be known.</summary>
    private RoadNetwork.LaneSegment? ChooseExit(RoadNetwork.LaneSegment lane, float turnedDegrees)
    {
        Turn want = Indicator < 0 ? Turn.Left : Indicator > 0 ? Turn.Right : Turn.Straight;
        if (Indicator == 0 && MathF.Abs(turnedDegrees) > 25f) want = turnedDegrees > 0f ? Turn.Right : Turn.Left;
        var exit = BestOf(lane, want);
        if (exit != null) return exit;
        if (lane.Next.Count == 1) return lane.Next[0].Next;
        return null;
    }

    private static RoadNetwork.LaneSegment? Straight(RoadNetwork.LaneSegment lane)
        => BestOf(lane, Turn.Straight) ?? (lane.Next.Count == 1 ? lane.Next[0].Next : null);

    /// <summary>Of the lanes a turn leads into, the one in the same place on its road as this lane is on
    /// its own (kerb lane into kerb lane).</summary>
    private static RoadNetwork.LaneSegment? BestOf(RoadNetwork.LaneSegment lane, Turn turn)
    {
        RoadNetwork.LaneSegment? best = null;
        float bestScore = float.MaxValue;
        bool kerb = RoadNetwork.IsKerbLane(lane);
        foreach (var (next, t) in lane.Next)
        {
            if (t != turn) continue;
            float score = (RoadNetwork.IsKerbLane(next) == kerb ? 0f : 10f)
                        + Flat(next.Path[0] - lane.Path[^1]).Length() * 0.01f;
            if (score < bestScore) { bestScore = score; best = next; }
        }
        return best;
    }

    private static Turn TurnOf(RoadNetwork.LaneSegment from, RoadNetwork.LaneSegment to)
    {
        foreach (var (n, t) in from.Next) if (ReferenceEquals(n, to)) return t;
        return Turn.Straight;
    }

    /// <summary>Degrees the car has turned from the lane it came down, positive to the right.</summary>
    private float TurnedSoFar(Vector3 forward)
    {
        float h = MathF.Atan2(forward.X, forward.Z);
        return MathF.IEEERemainder(h - _laneHeading, 2f * MathF.PI) * 180f / MathF.PI;
    }

    private static float Heading(RoadNetwork.LaneSegment lane, float along)
    {
        var d = Direction(lane.Path, along);
        return MathF.Atan2(d.X, d.Z);
    }

    // ── What is on it ───────────────────────────────────────────────────────────────────────────

    /// <summary>The level crossings the line runs over: the first rail's distance, and while the bells
    /// ring, a stop at the line.</summary>
    private void Crossings_(DrivingCuePlan plan, float halfLength, Func<CrossingRails, bool>? closed,
                            List<(float, float, CueHazard)> targets)
    {
        if (Crossings.Count == 0 || plan.Path.Count < 2) return;
        float along = 0f;
        for (int i = 1; i < plan.Path.Count; i++)
        {
            var a = plan.Path[i - 1];
            var b = plan.Path[i];
            float seg = Flat(b - a).Length();
            foreach (var c in Crossings)
            {
                if (RailCrossing(c, a, b, out float t))
                {
                    float d = along + t * seg;
                    if (d >= plan.CrossingDistance) continue;
                    plan.Crossing = c;
                    plan.CrossingDistance = d;
                    plan.CrossingClosed = closed?.Invoke(c) == true;
                }
            }
            along += seg;
            if (plan.Crossing != null) break;
        }
        if (plan.Crossing != null && plan.CrossingClosed)
            targets.Add((MathF.Max(0f, plan.CrossingDistance - StopLineFromRail - halfLength), 0f, CueHazard.Crossing));
    }

    /// <summary>Whether the step from a to b goes over either rail of a crossing, and how far along it.</summary>
    public static bool RailCrossing(CrossingRails c, Vector3 a, Vector3 b, out float t)
    {
        t = 0f;
        var across = new Vector3(c.Along.Z, 0f, -c.Along.X);
        var (left, right) = c.RailPoints();
        bool any = false;
        float best = float.MaxValue;
        foreach (var rail in new[] { left, right })
        {
            float sa = Vector3.Dot(Flat(a - rail), across), sb = Vector3.Dot(Flat(b - rail), across);
            if (sa == sb || MathF.Sign(sa) == MathF.Sign(sb) && sa != 0f) continue;
            float f = sa / (sa - sb);
            var p = Vector3.Lerp(a, b, f);
            if (MathF.Abs(Vector3.Dot(Flat(p - c.Centre), c.Along)) > c.HalfLengthMetres) continue;
            if (f < best) { best = f; any = true; }
        }
        t = best;
        return any;
    }

    /// <summary>
    /// The comfortable speed for every bend and turn on the line, from its curvature: the AASHTO side
    /// friction traffic slows to. A metre either side of each point on a road; the track's own curvature
    /// on a track (already in the targets).
    /// </summary>
    private static void Curvatures(DrivingCuePlan plan, List<(float From, float To)> turnSpans,
                                   List<(float, float, CueHazard)> targets)
    {
        if (plan.OnTrack) return;
        var p = plan.Path;
        float along = 0f;
        for (int i = 1; i < p.Count - 1; i++)
        {
            along += Flat(p[i] - p[i - 1]).Length();
            int a = Math.Max(0, i - 2), c = Math.Min(p.Count - 1, i + 2);
            float k = Menger(p[a], p[i], p[c]);
            if (k < 2e-3f) continue;
            bool turn = turnSpans.Any(s => along >= s.From - 0.5f && along <= s.To + 0.5f);
            targets.Add((along, DriverSteering.ComfortTurnSpeed(k), turn ? CueHazard.Turn : CueHazard.Bend));
        }
    }

    /// <summary>
    /// The steadiest braking that reaches every target in time: the largest (v^2 - vt^2) / 2d. A target
    /// the car is already on top of (inside half a second) is not something to brake for any more; how
    /// hard its turn is working the tyres is (<see cref="DrivingCuePlan.LateralRatio"/>).
    /// </summary>
    private static void Brake(DrivingCuePlan plan, float v, List<(float At, float Speed, CueHazard Kind)> targets)
    {
        float near = MathF.Max(1f, v * 0.5f);
        foreach (var (d, vt, kind) in targets)
        {
            if (d < near)
            {
                // A bend already under the car is not something to brake for any more; what it asks of
                // the tyres is the car's own turning (LateralRatio, from the yaw rate).
                if (kind is CueHazard.Stop or CueHazard.Crossing or CueHazard.RoadEnd && v > 0.5f)
                {
                    float a = v * v / (2f * MathF.Max(0.3f, d));
                    if (a > plan.NeededDecel) Set(plan, a, d, vt, kind);
                }
                continue;
            }
            if (v <= vt) continue;
            float need = (v * v - vt * vt) / (2f * d);
            if (need > plan.NeededDecel) Set(plan, need, d, vt, kind);
        }
    }

    private static void Set(DrivingCuePlan plan, float need, float d, float vt, CueHazard kind)
    {
        plan.NeededDecel = need;
        plan.Hazard = kind;
        plan.HazardDistance = d;
        plan.HazardSpeed = vt;
        plan.HazardPoint = PointAlong(plan.Path, d);
    }

    /// <summary>The comfortable speed through the tightest part of a turn's curve.</summary>
    private static float TurnSpeed(List<Vector3> curve)
    {
        float k = 0f;
        for (int i = 2; i < curve.Count - 2; i++) k = MathF.Max(k, Menger(curve[i - 2], curve[i], curve[i + 2]));
        return k < 2e-3f ? float.MaxValue : DriverSteering.ComfortTurnSpeed(k);
    }

    // ── The turn a car can make ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// The tightest a car takes a junction turn, metres (the path of its middle): a car's kerb-to-kerb
    /// turning circle is about 11 m across, and nobody drives a junction at full lock. A bigger vehicle
    /// sets its own (DrivingAids, from the chassis).
    /// </summary>
    public float MinTurnRadius { get; set; } = 6f;

    /// <summary>
    /// The line a car drives from one lane into the next: an arc tangent to both lanes' lines.
    ///
    /// Where the lanes leave room, it is the widest arc that starts and ends inside the junction. Where
    /// they do not — a kerb lane turning into the kerb lane round the near corner, whose lines meet a few
    /// metres from the lane ends — it is no tighter than <see cref="MinTurnRadius"/>, starting that much
    /// before the lane ends (<paramref name="trimIn"/>, metres to cut off the end of the lane) and joining
    /// the next lane that far along it (<paramref name="skipOut"/>). (Traffic drives the quadratic curve
    /// between the lane ends, LaneRoutes.Connector; at the near corner of a city junction that curve is
    /// a metre or two of radius, a turn no car can make.) Straight on, the straight line.
    /// </summary>
    public List<Vector3> TurnCurve(IReadOnlyList<Vector3> inPath, IReadOnlyList<Vector3> outPath, out float trimIn, out float skipOut)
    {
        trimIn = 0f; skipOut = 0f;
        Vector3 a = inPath[^1], b = outPath[0];
        Vector3 da = Flat(inPath[^1] - inPath[^2]), db = Flat(outPath[1] - outPath[0]);
        if (da.LengthSquared() < 1e-8f || db.LengthSquared() < 1e-8f) return LaneRoutes.Connector(inPath, outPath);
        da = Vector3.Normalize(da); db = Vector3.Normalize(db);
        float cross = da.X * db.Z - da.Z * db.X, dot = Vector3.Dot(da, db);
        if (MathF.Abs(cross) < 0.05f && dot > 0f) return LaneRoutes.Connector(inPath, outPath);
        // Where the two lanes' lines meet.
        Vector3 w = Flat(b - a);
        float t = (w.X * db.Z - w.Z * db.X) / cross;
        float u = (w.X * da.Z - w.Z * da.X) / cross;
        if (t < 0f || u > 0f) return LaneRoutes.Connector(inPath, outPath);
        var corner = a + da * t;
        corner.Y = 0.5f * (a.Y + b.Y);
        float turn = MathF.Acos(Math.Clamp(dot, -1f, 1f));                 // 0..pi
        float half = MathF.Tan(turn * 0.5f);
        if (half < 1e-3f) return LaneRoutes.Connector(inPath, outPath);
        // The widest arc inside the junction, and no tighter than a car turns.
        float room = MathF.Min(t, -u);
        float r = MathF.Max(room / half, MinTurnRadius);
        float tangent = r * half;
        trimIn = MathF.Max(0f, tangent - t);
        skipOut = MathF.Max(0f, tangent + u);
        var start = corner - da * tangent;
        var end = corner + db * tangent;
        // The centre is r to the inside of the turn from the start: right for a right turn.
        var inward = cross < 0f ? new Vector3(da.Z, 0f, -da.X) : new Vector3(-da.Z, 0f, da.X);
        var centre = start + inward * r;
        var from = start - centre;
        var to = end - centre;
        float a0 = MathF.Atan2(from.X, from.Z), a1 = MathF.Atan2(to.X, to.Z);
        float sweep = MathF.IEEERemainder(a1 - a0, 2f * MathF.PI);
        int n = Math.Max(2, (int)MathF.Ceiling(MathF.Abs(sweep) * r / 0.5f));
        var pts = new List<Vector3>(n + 1);
        for (int k = 0; k <= n; k++)
        {
            float ang = a0 + sweep * k / n;
            var p = centre + new Vector3(MathF.Sin(ang), 0f, MathF.Cos(ang)) * r;
            p.Y = corner.Y;
            pts.Add(p);
        }
        // The lane ends inside the arc's straight run: join them with what is left of the lines.
        if (trimIn <= 0f && t - tangent > 0.05f) pts.Insert(0, a);
        if (skipOut <= 0f && -u - tangent > 0.05f) pts.Add(b);
        return pts;
    }

    /// <summary>Cuts the last <paramref name="metres"/> off a line.</summary>
    private static void TrimEnd(List<Vector3> path, float metres)
    {
        float keep = PathLength(path) - metres;
        if (keep <= 0f) { if (path.Count > 1) path.RemoveRange(1, path.Count - 1); return; }
        var end = RoadNetwork.PointAt(path, keep);
        float along = 0f;
        for (int i = 1; i < path.Count; i++)
        {
            along += Flat(path[i] - path[i - 1]).Length();
            if (along >= keep)
            {
                path.RemoveRange(i, path.Count - i);
                path.Add(end);
                return;
            }
        }
    }

    // ── Geometry ────────────────────────────────────────────────────────────────────────────────

    /// <summary>The curvature of the circle through three points on the ground, 1/m.</summary>
    public static float Menger(Vector3 a, Vector3 b, Vector3 c)
    {
        var ab = Flat(b - a); var bc = Flat(c - b); var ca = Flat(a - c);
        float cross = MathF.Abs(ab.X * bc.Z - ab.Z * bc.X);
        float den = ab.Length() * bc.Length() * ca.Length();
        return den < 1e-6f ? 0f : 2f * cross / den;
    }

    private static Vector3 Direction(IReadOnlyList<Vector3> path, float along)
    {
        var d = Flat(RoadNetwork.PointAt(path, along + 0.5f) - RoadNetwork.PointAt(path, MathF.Max(0f, along - 0.5f)));
        if (d.LengthSquared() < 1e-8f) d = Flat(path[^1] - path[0]);
        return d.LengthSquared() < 1e-8f ? Vector3.UnitZ : Vector3.Normalize(d);
    }

    /// <summary>Appends the stretch of a path between two distances along it, a point a metre.</summary>
    private static void AppendSlice(List<Vector3> into, IReadOnlyList<Vector3> path, float from, float to)
    {
        for (float d = MathF.Ceiling(from / Step) * Step; d <= to; d += Step)
        {
            var p = RoadNetwork.PointAt(path, d);
            if (into.Count == 0 || Flat(p - into[^1]).Length() > 0.05f) into.Add(p);
        }
        var end = RoadNetwork.PointAt(path, to);
        if (into.Count == 0 || Flat(end - into[^1]).Length() > 0.05f) into.Add(end);
    }

    public static float PathLength(IReadOnlyList<Vector3> p) => RoadNetwork.Length(p);

    /// <summary>The point a distance along a line; its end if it is shorter.</summary>
    public static Vector3 PointAlong(IReadOnlyList<Vector3> p, float d)
        => p.Count == 0 ? Vector3.Zero : p.Count == 1 ? p[0] : RoadNetwork.PointAt(p, d);

    private static void Trim(List<Vector3> path, float reach)
    {
        float along = 0f;
        for (int i = 1; i < path.Count; i++)
        {
            along += Flat(path[i] - path[i - 1]).Length();
            if (along > reach + Step) { path.RemoveRange(i + 1, path.Count - i - 1); return; }
        }
    }

    private static int Nearest(List<Vector3> pts, Vector3 at)
    {
        int best = 0; float bd = float.MaxValue;
        for (int i = 0; i < pts.Count; i++)
        {
            float d = Flat(pts[i] - at).LengthSquared();
            if (d < bd) { bd = d; best = i; }
        }
        return best;
    }

    private static Vector3 Flat(Vector3 v) => new(v.X, 0f, v.Z);
    private static Vector3 Flat(Vector3 v, float y) => new(v.X, y, v.Z);
}

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
    /// <summary>The share of the grip the turn the car is in already asks for (v^2 k over the grip).</summary>
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
/// No audio and no client: the same roads the traffic drives (<see cref="RoadNetwork"/>), the same turn
/// a car makes from one lane into the next (<see cref="LaneRoutes.Connector"/>), the same comfortable
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

    /// <summary>How far ahead the line is planned, metres: three seconds and a gentle stop, at least forty.</summary>
    public static float LookAhead(float speed) => Math.Clamp(speed * 3f + speed * speed / 3f, 40f, 250f);

    /// <param name="at">The car's middle, on the ground.</param>
    /// <param name="forward">The way it points.</param>
    /// <param name="speed">How fast it is going, m/s.</param>
    /// <param name="availableDecel">What its tyres can give on this road now, m/s² (grip times g, wet or dry).</param>
    /// <param name="halfLength">Half the car's length, metres: its nose is this far ahead of its middle.</param>
    /// <param name="closed">Whether a crossing's bells are ringing; null for none ever.</param>
    public DrivingCuePlan Update(Vector3 at, Vector3 forward, float speed, float availableDecel, float halfLength,
                                 Func<CrossingRails, bool>? closed = null)
    {
        var plan = new DrivingCuePlan { AvailableDecel = MathF.Max(0.1f, availableDecel) };
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
            plan.Path.Add(Flat(at, lane.Path[0].Y));
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
            plan.Path.Add(Flat(at, j.Position.Y));
            if (exit != null)
            {
                var curve = LaneRoutes.Connector(_lane.Path, exit.Path);
                int nearest = Nearest(curve, at);
                for (int k = nearest + 1; k < curve.Count; k++) plan.Path.Add(curve[k]);
                turnSpans.Add((0f, PathLength(plan.Path)));
                plan.NextTurn = TurnOf(_lane, exit);
                plan.NextRoad = exit.Road.Name;
                plan.Junction = j;
                plan.JunctionDistance = 0f;
                AppendSlice(plan.Path, exit.Path, 0f, exit.LengthMetres);
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
        plan.GuidePoint = PointAlong(plan.Path, GuideDistance(speed));
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
                    slowest = MathF.Min(slowest, TurnSpeed(LaneRoutes.Connector(current.Path, next.Path)));
                if (slowest < float.MaxValue) targets.Add((laneEnd + j.RadiusMetres * 0.5f, slowest, CueHazard.Turn));
                plan.Path.Add(Flat(j.Position, current.Path[^1].Y));
                break;
            }
            var curve = LaneRoutes.Connector(current.Path, exit.Path);
            for (int k = 1; k < curve.Count; k++) plan.Path.Add(curve[k]);
            float turnEnd = PathLength(plan.Path);
            if (TurnOf(current, exit) != Turn.Straight) turnSpans.Add((laneEnd, turnEnd));
            AppendSlice(plan.Path, exit.Path, 0f, exit.LengthMetres);
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
                if (kind is CueHazard.Bend or CueHazard.Turn && vt > 0f && float.IsFinite(vt))
                {
                    // v^2 k = f g at the comfortable speed, so the demand here is (v / vt)^2 of that friction.
                    float k = CurvatureFor(vt);
                    plan.LateralRatio = MathF.Max(plan.LateralRatio, v * v * k / plan.AvailableDecel);
                }
                else if (kind is CueHazard.Stop or CueHazard.Crossing or CueHazard.RoadEnd && v > 0.5f)
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

    /// <summary>The curvature whose comfortable speed is <paramref name="speed"/>: v^2 k = f(v) g.</summary>
    private static float CurvatureFor(float speed)
        => DriverSteering.ComfortSideFriction(speed) * WheelDynamics.G / MathF.Max(0.25f, speed * speed);

    /// <summary>The comfortable speed through the tightest part of a turn's curve.</summary>
    private static float TurnSpeed(List<Vector3> curve)
    {
        float k = 0f;
        for (int i = 2; i < curve.Count - 2; i++) k = MathF.Max(k, Menger(curve[i - 2], curve[i], curve[i + 2]));
        return k < 2e-3f ? float.MaxValue : DriverSteering.ComfortTurnSpeed(k);
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

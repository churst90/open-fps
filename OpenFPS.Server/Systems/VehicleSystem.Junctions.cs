using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Server.Repositories;

namespace OpenFPS.Server.Systems;

/// <summary>
/// Who goes first at a junction.
///
/// Two rules, the ones a driver actually uses. A vehicle already in the junction has it: nobody
/// enters on a path that crosses or joins its path until it is out. And a driver giving way takes a
/// gap in the traffic it gives way to only if that gap is long enough: the Highway Capacity Manual's
/// critical headways (StreetLifeData), measured as the time the other vehicle needs to reach the
/// junction. Who gives way to whom: the priority road over the others (JunctionData.PriorityRoads),
/// and on the priority road a left turn across the oncoming stream gives way to it. Between two
/// approaches of the same standing, the one on the right goes first; and if everybody is waiting for
/// somebody, one of them goes after a while, as drivers do.
///
/// Without this, two cars turning into the same lane from different directions went through each
/// other in the middle of the junction (CarFollowingTests counted them), and a give-way was a
/// two-second stop whether anything was coming or not.
/// </summary>
public sealed partial class VehicleSystem
{
    /// <summary>A way through a junction: the lane in and the lane out.</summary>
    private readonly record struct Movement(RoadNetwork.LaneSegment In, RoadNetwork.LaneSegment Out)
    {
        public Turn Turn
        {
            get
            {
                var into = Out;
                foreach (var (next, turn) in In.Next) if (next == into) return turn;
                return Turn.Straight;
            }
        }
    }

    private sealed class AtJunction
    {
        public readonly List<(DemoVehicle V, Movement M)> Inside = new();
        public readonly List<(DemoVehicle V, Movement M, float ToLine)> Coming = new();
        /// <summary>The one driver the deadlock breaker has let go, until it is into the junction.</summary>
        public DemoVehicle? LetGo;
    }

    private readonly Dictionary<(string Map, string Junction), AtJunction> _atJunction = new();
    private readonly Dictionary<(int, int, int, int), bool> _conflicts = new();

    /// <summary>How far out a vehicle counts as coming to a junction, metres: far enough that the longest
    /// critical gap and the time to cross still cover a vehicle at city speed (7.1 s + 3 s at 14 m/s).</summary>
    private const float JunctionLookMetres = 150f;
    /// <summary>A vehicle this few seconds from the line is arriving, whoever has been waiting longest.</summary>
    private const float ImminentSeconds = 4f;

    /// <summary>Where each vehicle on a route is with respect to the junctions on its way, this tick.</summary>
    private void IndexJunctions(string mapId)
    {
        foreach (var a in _atJunction.Values) { a.Inside.Clear(); a.Coming.Clear(); }
        foreach (var v in _vehicles)
        {
            if (v.MapId != mapId || v.Route == null || v.Line == null || !InLane(v)) continue;
            var legs = v.Route.Legs;
            var (leg, along) = WhereOnLane(v);
            if (along < 0f && !ShortOfTheLine(v, ref leg, ref along))
            {
                // In the junction between the leg before and this one.
                var before = legs[(leg - 1 + legs.Count) % legs.Count].Segment;
                if (before.To != null) Slot(mapId, before.To).Inside.Add((v, new Movement(before, legs[leg].Segment)));
                continue;
            }
            // Coming to every junction on its way within the look, not only the next one: a car on
            // the stretch before the last is still arriving, and a driver waiting to pull out has to
            // see it. (Only the next was listed at first, and a motorbike at 13 m/s was invisible to a
            // car giving way until it was 36 m off, too late.)
            float toLine = legs[leg].Segment.LengthMetres - along;
            for (int k = 0; k < legs.Count && toLine <= JunctionLookMetres; k++)
            {
                int li = (leg + k) % legs.Count;
                var seg = legs[li].Segment;
                if (seg.To != null)
                    Slot(mapId, seg.To).Coming.Add((v, new Movement(seg, legs[(li + 1) % legs.Count].Segment), toLine));
                int next = (li + 1) % legs.Count;
                toLine += (legs[next].From - legs[li].To + v.Route.Length) % v.Route.Length + legs[next].Segment.LengthMetres;
            }
        }
    }

    private AtJunction Slot(string mapId, JunctionData j)
    {
        if (!_atJunction.TryGetValue((mapId, j.Id), out var a)) _atJunction[(mapId, j.Id)] = a = new AtJunction();
        return a;
    }

    /// <summary>
    /// How far a vehicle may still go before it must be standing at a junction's line, metres, or
    /// MaxValue if nothing makes it wait; and the speed it arrives at the line to look, m/s (a give-way
    /// approach), or MaxValue.
    /// </summary>
    private (float ToHold, float LookSpeed) JunctionHold(DemoVehicle v, float dt)
    {
        if (v.Route == null || !_streetLife.TryGetValue(v.MapId, out var life)) return (float.MaxValue, float.MaxValue);
        var legs = v.Route.Legs;
        var (leg, along) = WhereOnLane(v);
        if (along < 0f && !ShortOfTheLine(v, ref leg, ref along))
        { v.WaitingAt = null; v.Holding = false; return (float.MaxValue, float.MaxValue); }   // already in it: keep going
        var seg = legs[leg].Segment;
        var j = seg.To;
        float toLine = seg.LengthMetres - along;
        if (j == null || toLine > JunctionLookMetres) return (float.MaxValue, float.MaxValue);
        var mine = new Movement(seg, legs[(leg + 1) % legs.Count].Segment);
        bool givesWay = j.GivesWay(seg.Road);
        float look = givesWay ? life.GiveWayApproachKmh / 3.6f : float.MaxValue;

        // Only decide once it is near enough to be committing; further out it simply drives.
        // A crossing before the line moves where it stands further back, and so when it must decide.
        float atLine = MathF.Max(0f, toLine - 1f - 0.5f * v.LengthMetres);
        float holdAt = MathF.Min(atLine, ShortOfCrosswalk(v, toLine));
        float stopping = v.Speed * v.Speed / (2f * MathF.Max(0.1f, v.Brake)) + 6f;
        if (toLine - (atLine - holdAt) > stopping) return (float.MaxValue, look);

        var here = Slot(v.MapId, j);
        bool wait = false;
        foreach (var (o, m) in here.Inside)
            if (o != v && Conflict(mine, m)) { wait = true; break; }
        // ...and so does a vehicle already committed to it: too close to stop before the line and not
        // waiting there. Two drivers who each decided before the other had entered both went, and met.
        if (!wait)
            foreach (var (o, m, oToLine) in here.Coming)
                if (o != v && m.In != mine.In && !o.Holding && Committed(o, oToLine) && Conflict(mine, m)
                    && (!Committed(v, toLine) || oToLine < toLine || (oToLine == toLine && o.Entity.Id < v.Entity.Id)))
                { wait = true; break; }
        if (!wait)
        {
            int myRank = Rank(j, mine);
            foreach (var (o, m, oToLine) in here.Coming)
            {
                if (o == v || m.In == mine.In || !Conflict(mine, m)) continue;
                int theirRank = Rank(j, m);
                // Side by side on the same approach (two lanes of one road): whoever is further back
                // waits. Otherwise the higher standing goes first, and between equals the one on the right.
                bool sameApproach = m.In.Road == mine.In.Road && m.In.Lane.Direction == mine.In.Lane.Direction;
                bool yields = sameApproach
                    ? oToLine < toLine - 0.5f || (MathF.Abs(oToLine - toLine) <= 0.5f && o.Entity.Id < v.Entity.Id)
                    : theirRank > myRank || (theirRank == myRank && FromTheRight(mine, m) && oToLine < toLine + 20f);
                if (!yields) continue;
                float arrives = oToLine / MathF.Max(0.5f, o.Speed);
                if (o.Speed < 0.5f && oToLine > 3f) continue;            // standing well back: not coming
                // The gap is counted from when this one reaches the line, not from now: it is still
                // some way short of it, and will cross at no more than its looking speed.
                float mineToLine = toLine / MathF.Max(1f, MathF.Min(v.Speed + 0.5f, look < float.MaxValue ? look : v.Speed + 0.5f));
                if (arrives < CriticalGap(life, mine, givesWay) + mineToLine) { wait = true; break; }
            }
        }

        // Everybody waiting for somebody: after a while, one goes. Only one: the others wait for it until
        // it is into the junction, and are let go one at a time after it. Two that ran out of patience in
        // the same quarter of a second both pulled away from the line, slower than the 0.5 m/s that makes
        // one count as coming, and met in the middle (docs/MUTATION_2026-10-01.md, item 10).
        if (here.LetGo is { } went && !here.Coming.Any(x => x.V == went && x.ToLine < 6f + 0.5f * went.LengthMetres))
            here.LetGo = null;
        if (wait && v.Speed < 0.3f && toLine < 6f + 0.5f * v.LengthMetres)   // short of a crossing too
        {
            if (v.WaitingAt != j.Id) { v.WaitingAt = j.Id; v.WaitedSeconds = 0f; }
            v.WaitedSeconds += dt;
            // ...but not in front of somebody about to arrive: that is not a deadlock, it is traffic.
            if (v.WaitedSeconds > life.GiveWayPatienceSeconds && (here.LetGo == null || here.LetGo == v)
                && !here.Inside.Any(x => x.V != v && Conflict(mine, x.M))
                && !here.Coming.Any(x => x.V != v && x.M.In != mine.In && x.V.Speed > 0.5f
                                         && x.ToLine / x.V.Speed < ImminentSeconds && Conflict(mine, x.M)))
            {
                wait = false;
                here.LetGo = v;
            }
        }
        else if (!wait) v.WaitingAt = null;

        // Standing with the front bumper at the line, not the middle of the car: half a car further
        // back, or its nose is in the lane of the road it is waiting to cross.
        v.Holding = wait;
        // And short of a crossing that lies before the line, not on it.
        return (wait ? holdAt : float.MaxValue, look);
    }

    /// <summary>
    /// Whether a vehicle the lap puts in a junction is in fact still on the lane before it, short of the
    /// line; if so, which leg and how far along. The smoothed line is shorter round corners than the lanes,
    /// and the two drift apart by metres: a long truck holding at the line was taken to be in the junction
    /// already, drove on, and met a car coming the other way (traced 2026-09-28).
    /// </summary>
    private static bool ShortOfTheLine(DemoVehicle v, ref int leg, ref float along)
    {
        var legs = v.Route!.Legs;
        int prev = (leg - 1 + legs.Count) % legs.Count;
        var before = legs[prev].Segment;
        v.Line!.Sample(v.Lap, out var at, out _, out _);
        var (a, off) = RoadNetwork.Project(before.Path, at);
        if (off >= before.Lane.WidthMetres * 0.5f || a >= before.LengthMetres - 0.05f) return false;
        leg = prev;
        along = a;
        return true;
    }

    /// <summary>Too close to the line to stop before it on its own brakes: it is going in.</summary>
    private static bool Committed(DemoVehicle v, float toLine)
        => toLine <= v.Speed * v.Speed / (2f * MathF.Max(0.1f, v.Brake)) + 1f && v.Speed > 0.5f;

    /// <summary>Standing at a junction: the priority road above the rest, and on either a left turn
    /// across the oncoming stream a step below going straight or right, since it gives way to it.</summary>
    private static int Rank(JunctionData j, Movement m)
        => (!j.GivesWay(m.In.Road) ? 2 : 0) - (m.Turn == Turn.Left ? 1 : 0);

    private static float CriticalGap(StreetLifeData life, Movement m, bool givesWay)
        => !givesWay ? life.CriticalGapMajorLeftSeconds
         : m.Turn switch
           {
               Turn.Right => life.CriticalGapRightSeconds,
               Turn.Left => life.CriticalGapLeftSeconds,
               _ => life.CriticalGapStraightSeconds,
           };

    /// <summary>Whether the other approach is on this one's right.</summary>
    private static bool FromTheRight(Movement mine, Movement theirs)
    {
        Vector3 a = Heading(mine.In.Path), b = Heading(theirs.In.Path);
        // Coming from the right is travelling to the left across this one's path: in x east, z north,
        // a turn of their heading against mine to the left.
        return a.X * b.Z - a.Z * b.X > 0.3f;
    }

    private static Vector3 Heading(IReadOnlyList<Vector3> p)
    {
        var d = p[^1] - p[^2]; d.Y = 0f;
        return d.LengthSquared() > 1e-9f ? Vector3.Normalize(d) : Vector3.UnitZ;
    }

    /// <summary>Two ways through the same junction meet: into the same lane, or their paths cross.</summary>
    private bool Conflict(Movement a, Movement b)
    {
        if (a.In == b.In) return false;                        // the same approach: following handles it
        if (a.Out == b.Out) return true;                       // into the same lane
        var key = (a.In.Index, a.Out.Index, b.In.Index, b.Out.Index);
        if (_conflicts.TryGetValue(key, out bool c)) return c;
        var pa = LaneRoutes.Connector(a.In.Path, a.Out.Path);
        var pb = LaneRoutes.Connector(b.In.Path, b.Out.Path);
        // Crossing, or passing closer than two bodies' width: two turns into neighbouring lanes never
        // cross, but a car stopped partway round the inner one is in the way of the outer one (traced
        // 2026-09-28: 1.7 m apart, centre to centre, a truck and a police car).
        c = Cross(pa, pb) || Closest(pa, pb) < BodyClearanceMetres;
        _conflicts[key] = c;
        _conflicts[(b.In.Index, b.Out.Index, a.In.Index, a.Out.Index)] = c;
        return c;
    }

    /// <summary>Two vehicles' paths closer than this, centre to centre, have their bodies touching:
    /// two widths of about two metres, and the driven line cutting corners by several tenths of a
    /// metre against the connector it is smoothed from.</summary>
    private const float BodyClearanceMetres = 2.8f;

    /// <summary>How close two paths come, metres, point to point (the connectors are sampled finely).</summary>
    private static float Closest(List<Vector3> a, List<Vector3> b)
    {
        float best = float.MaxValue;
        foreach (var p in a)
            foreach (var q in b)
            {
                float dx = p.X - q.X, dz = p.Z - q.Z;
                best = MathF.Min(best, dx * dx + dz * dz);
            }
        return MathF.Sqrt(best);
    }

    private static bool Cross(List<Vector3> a, List<Vector3> b)
    {
        for (int i = 0; i + 1 < a.Count; i++)
            for (int k = 0; k + 1 < b.Count; k++)
                if (SegmentsMeet(a[i], a[i + 1], b[k], b[k + 1])) return true;
        return false;
    }

    private static bool SegmentsMeet(Vector3 p1, Vector3 p2, Vector3 q1, Vector3 q2)
    {
        static float Side(Vector3 a, Vector3 b, Vector3 c) => (b.X - a.X) * (c.Z - a.Z) - (b.Z - a.Z) * (c.X - a.X);
        float d1 = Side(q1, q2, p1), d2 = Side(q1, q2, p2), d3 = Side(p1, p2, q1), d4 = Side(p1, p2, q2);
        return ((d1 > 0) != (d2 > 0)) && ((d3 > 0) != (d4 > 0));
    }
}

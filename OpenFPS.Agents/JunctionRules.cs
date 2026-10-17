using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Server.Repositories;

namespace OpenFPS.Server.Systems;

/// <summary>A way through a junction: the lane in and the lane out.</summary>
public readonly record struct Movement(RoadNetwork.LaneSegment In, RoadNetwork.LaneSegment Out)
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
/// The laws alone: who is where round a junction this tick, and who has been let go, is the host's
/// (VehicleSystem.Junctions), which hands them in.
/// </summary>
public static class JunctionRules
{
    /// <summary>How far out a vehicle counts as coming to a junction, metres: far enough that the longest
    /// critical gap and the time to cross still cover a vehicle at city speed (7.1 s + 3 s at 14 m/s).</summary>
    public const float LookMetres = 150f;
    /// <summary>A vehicle this few seconds from the line is arriving, whoever has been waiting longest.</summary>
    public const float ImminentSeconds = 4f;
    /// <summary>Two vehicles' paths closer than this, centre to centre, have their bodies touching:
    /// two widths of about two metres, and the driven line cutting corners by several tenths of a
    /// metre against the connector it is smoothed from.</summary>
    public const float BodyClearanceMetres = 2.8f;

    /// <summary>Too close to the line to stop before it on its own brakes: it is going in.</summary>
    public static bool Committed(float speed, float brake, float toLine)
        => toLine <= speed * speed / (2f * MathF.Max(0.1f, brake)) + 1f && speed > 0.5f;

    /// <summary>Standing at a junction: the priority road above the rest, and on either a left turn
    /// across the oncoming stream a step below going straight or right, since it gives way to it.</summary>
    public static int Rank(JunctionData j, Movement m)
        => (!j.GivesWay(m.In.Road) ? 2 : 0) - (m.Turn == Turn.Left ? 1 : 0);

    /// <summary>The shortest gap a driver takes, seconds: the Highway Capacity Manual's critical headway for
    /// its movement, or for a left turn off the priority road.</summary>
    public static float CriticalGap(StreetLifeData life, Movement m, bool givesWay)
        => !givesWay ? life.CriticalGapMajorLeftSeconds
         : m.Turn switch
           {
               Turn.Right => life.CriticalGapRightSeconds,
               Turn.Left => life.CriticalGapLeftSeconds,
               _ => life.CriticalGapStraightSeconds,
           };

    /// <summary>
    /// Whether one driver gives way to another coming to the same junction on a way that meets its own.
    /// Side by side on the same approach (two lanes of one road): whoever is further back waits, and dead
    /// level the higher id. Otherwise the higher standing goes first, and between equals the one on the right.
    /// </summary>
    /// <param name="toLine">Metres from its line; <paramref name="theirToLine"/> the other's from its own.</param>
    public static bool GivesWayTo(JunctionData j, Movement mine, float toLine, int myId, Movement theirs, float theirToLine, int theirId)
    {
        bool sameApproach = theirs.In.Road == mine.In.Road && theirs.In.Lane.Direction == mine.In.Lane.Direction;
        return sameApproach
            ? theirToLine < toLine - 0.5f || (MathF.Abs(theirToLine - toLine) <= 0.5f && theirId < myId)
            : Rank(j, theirs) > Rank(j, mine) || (Rank(j, theirs) == Rank(j, mine) && FromTheRight(mine, theirs) && theirToLine < toLine + 20f);
    }

    /// <summary>
    /// Whether the gap before one it gives way to is too short to take: the other reaches the junction
    /// within the critical gap of this one reaching its line, at no more than its looking speed. One
    /// standing well back is not coming.
    /// </summary>
    /// <param name="look">Its looking speed at a give-way line, m/s, or MaxValue.</param>
    public static bool GapTooShort(StreetLifeData life, Movement mine, bool givesWay, float toLine, float speed, float look,
                                   float theirToLine, float theirSpeed)
    {
        float arrives = theirToLine / MathF.Max(0.5f, theirSpeed);
        if (theirSpeed < 0.5f && theirToLine > 3f) return false;            // standing well back: not coming
        // The gap counts from when this one reaches the line, at no more than its looking speed.
        float mineToLine = toLine / MathF.Max(1f, MathF.Min(speed + 0.5f, look < float.MaxValue ? look : speed + 0.5f));
        return arrives < CriticalGap(life, mine, givesWay) + mineToLine;
    }

    /// <summary>Whether the other approach is on this one's right.</summary>
    public static bool FromTheRight(Movement mine, Movement theirs)
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

    /// <summary>Two ways through the same junction meet: into the same lane, or their paths cross or come
    /// closer than two bodies' width. The same approach does not: following handles it.</summary>
    public static bool Meet(Movement a, Movement b)
    {
        if (a.In == b.In) return false;
        if (a.Out == b.Out) return true;
        var pa = LaneRoutes.Connector(a.In.Path, a.Out.Path);
        var pb = LaneRoutes.Connector(b.In.Path, b.Out.Path);
        // Closer than two bodies' width: turns into neighbouring lanes never cross, yet came 1.7 m
        // apart centre to centre (traced 2026-09-28).
        return Cross(pa, pb) || Closest(pa, pb) < BodyClearanceMetres;
    }

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

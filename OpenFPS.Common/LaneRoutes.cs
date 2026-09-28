using System.Numerics;

namespace OpenFPS.Common;

/// <summary>
/// A vehicle's way round a road network: a closed tour of lane segments, joined through each junction
/// by the curve a car actually drives from one lane into the next.
///
/// Closed on purpose. Everything that drives a vehicle round (RaceLine's speed from curvature and
/// braking, stops by distance round the lap, parking spots, the look-ahead for people in the road)
/// already works on a loop, so a tour through the city is a loop too. What changes is where the loop
/// comes from: the lanes, not a hand-drawn line down the middle of a road.
/// </summary>
public sealed class LaneRoute
{
    /// <summary>The line to drive, about a metre between points, closed (the last joins the first).</summary>
    public List<Vector3> Points { get; } = new();
    /// <summary>The lane segments in order, each with how far round the tour it starts and ends.</summary>
    public List<(RoadNetwork.LaneSegment Segment, float From, float To)> Legs { get; } = new();
    public float Length { get; internal set; }

    /// <summary>The leg (its index in <see cref="Legs"/>) a distance round the tour is on or heading
    /// into, and how far along that leg it is (negative while still in the junction before it).</summary>
    public (int Leg, float Along) LegAt(float lap)
    {
        if (Length > 0f) { lap %= Length; if (lap < 0f) lap += Length; }
        for (int i = 0; i < Legs.Count; i++)
            if (lap < Legs[i].To) return (i, lap - Legs[i].From);
        return (0, lap - Length - Legs[0].From);
    }
}

public static class LaneRoutes
{
    private const float Step = 1f;

    /// <summary>
    /// A tour through junctions in order, back to the first: the shortest lanes between each pair.
    /// Null if one cannot be reached from the one before.
    /// </summary>
    public static LaneRoute? Via(RoadNetwork net, IReadOnlyList<JunctionData> via)
    {
        if (via.Count < 2) return null;
        var legs = new List<RoadNetwork.LaneSegment>();
        // The first leg starts anywhere out of the first junction.
        IEnumerable<RoadNetwork.LaneSegment> from = net.Segments.Where(s => s.From == via[0]);
        for (int i = 1; i <= via.Count; i++)
        {
            var target = via[i % via.Count];
            var path = Shortest(net, from, s => s.To == target);
            if (path == null) return null;
            legs.AddRange(path);
            from = path[^1].Next.Select(n => n.Next);
        }
        // Close it: from the last leg into the first, which must be one of its next lanes.
        if (!legs[^1].Next.Any(n => n.Next == legs[0]))
        {
            var bridge = Shortest(net, legs[^1].Next.Select(n => n.Next), s => s.Next.Any(n => n.Next == legs[0]));
            if (bridge == null) return null;
            legs.AddRange(bridge);
        }
        return Build(legs);
    }

    /// <summary>
    /// A wandering tour from a lane: turns chosen at random (seeded, so the map is the same every
    /// time), straight on twice as often as a turn, and once it has gone far enough the shortest way
    /// back to where it began. Only lanes it can always get back from are taken, so it never runs into
    /// a dead end.
    /// </summary>
    public static LaneRoute? Random(RoadNetwork net, RoadNetwork.LaneSegment start, int seed,
                                    float wanderMetres = 900f)
    {
        var home = BackTo(net, start);                                  // metres from each lane back to the start
        if (!home.ContainsKey(start.Index)) return null;
        var rng = new System.Random(seed);
        var legs = new List<RoadNetwork.LaneSegment> { start };
        float gone = start.LengthMetres;
        var at = start;
        for (int guard = 0; guard < 400; guard++)
        {
            var options = at.Next.Where(n => home.ContainsKey(n.Next.Index)).ToList();
            if (options.Count == 0) return null;
            if (options.Any(n => n.Next == start) && gone >= wanderMetres) return Build(legs);
            RoadNetwork.LaneSegment next;
            if (gone < wanderMetres)
            {
                // Straight on twice as often as a turn; the kerb lane twice as often as an inner one.
                static float Weight((RoadNetwork.LaneSegment Next, Turn Turn) o)
                    => (o.Turn == Turn.Straight ? 2f : 1f) * (IsKerbLane(o.Next) ? 2f : 1f);
                float total = options.Sum(Weight);
                float pick = (float)rng.NextDouble() * total;
                next = options[^1].Next;
                foreach (var o in options)
                {
                    pick -= Weight(o);
                    if (pick <= 0f) { next = o.Next; break; }
                }
            }
            else next = options.OrderBy(o => home[o.Next.Index]).First().Next;   // head home
            if (next == start) return Build(legs);
            legs.Add(next);
            gone += next.LengthMetres;
            at = next;
        }
        return null;
    }

    /// <summary>How far each lane is from reaching <paramref name="target"/>, metres, for every lane that
    /// can reach it at all.</summary>
    private static Dictionary<int, float> BackTo(RoadNetwork net, RoadNetwork.LaneSegment target)
    {
        var into = new Dictionary<int, List<RoadNetwork.LaneSegment>>();
        foreach (var s in net.Segments)
            foreach (var (n, _) in s.Next)
            {
                if (!into.TryGetValue(n.Index, out var list)) into[n.Index] = list = new();
                list.Add(s);
            }
        var dist = new Dictionary<int, float> { [target.Index] = 0f };
        var queue = new PriorityQueue<RoadNetwork.LaneSegment, float>();
        queue.Enqueue(target, 0f);
        while (queue.TryDequeue(out var s, out float d))
        {
            if (d > dist[s.Index]) continue;
            if (!into.TryGetValue(s.Index, out var prev)) continue;
            foreach (var p in prev)
            {
                float nd = d + s.LengthMetres;
                if (dist.TryGetValue(p.Index, out float old) && old <= nd) continue;
                dist[p.Index] = nd;
                queue.Enqueue(p, nd);
            }
        }
        return dist;
    }

    /// <summary>Shortest chain of lanes, by length, from any of <paramref name="starts"/> to one that
    /// satisfies <paramref name="done"/>, both included.</summary>
    private static List<RoadNetwork.LaneSegment>? Shortest(RoadNetwork net, IEnumerable<RoadNetwork.LaneSegment> starts,
                                                          Func<RoadNetwork.LaneSegment, bool> done)
    {
        var dist = new Dictionary<int, float>();
        var back = new Dictionary<int, RoadNetwork.LaneSegment?>();
        var queue = new PriorityQueue<RoadNetwork.LaneSegment, float>();
        foreach (var s in starts)
        {
            if (dist.ContainsKey(s.Index)) continue;
            dist[s.Index] = s.LengthMetres; back[s.Index] = null;
            queue.Enqueue(s, s.LengthMetres);
        }
        while (queue.TryDequeue(out var s, out float d))
        {
            if (d > dist[s.Index]) continue;
            if (done(s))
            {
                var path = new List<RoadNetwork.LaneSegment>();
                for (RoadNetwork.LaneSegment? c = s; c != null; c = back[c.Index]) path.Add(c);
                path.Reverse();
                return path;
            }
            foreach (var (n, _) in s.Next)
            {
                float nd = d + n.LengthMetres * (IsKerbLane(n) ? 1f : InnerLaneCost);
                if (dist.TryGetValue(n.Index, out float old) && old <= nd) continue;
                dist[n.Index] = nd; back[n.Index] = s;
                queue.Enqueue(n, nd);
            }
        }
        return null;
    }

    /// <summary>
    /// Keep right except to pass: an inner lane is taken only when it saves more than this share of
    /// the distance. It is also what puts a bus in the kerb lane where its stops are.
    /// </summary>
    private const float InnerLaneCost = 1.1f;

    /// <summary>The lane nearest the kerb for its direction of travel.</summary>
    public static bool IsKerbLane(RoadNetwork.LaneSegment s) => RoadNetwork.IsKerbLane(s);

    /// <summary>The lanes joined up: each lane's own path, and between one and the next the curve
    /// through the junction.</summary>
    private static LaneRoute Build(List<RoadNetwork.LaneSegment> legs)
    {
        var route = new LaneRoute();
        float lap = 0f;
        for (int i = 0; i < legs.Count; i++)
        {
            var seg = legs[i];
            float from = lap;
            lap = Append(route.Points, seg.Path, lap);
            route.Legs.Add((seg, from, lap));
            var next = legs[(i + 1) % legs.Count];
            lap = Append(route.Points, Connector(seg.Path, next.Path), lap);
        }
        // Each path is appended without its last point, which is the next one's first; the last
        // connector's is the first lane's first point, where the loop closes.
        route.Length = lap + Vector3.Distance(route.Points[^1], route.Points[0]);
        return route;
    }

    /// <summary>Appends a path at about <see cref="Step"/> spacing, returning the distance travelled.</summary>
    private static float Append(List<Vector3> into, IReadOnlyList<Vector3> path, float lap)
    {
        for (int i = 0; i + 1 < path.Count; i++)
        {
            Vector3 a = path[i], b = path[i + 1];
            float len = Vector3.Distance(a, b);
            int n = Math.Max(1, (int)MathF.Ceiling(len / Step));
            for (int k = 0; k < n; k++)
            {
                var p = Vector3.Lerp(a, b, k / (float)n);
                if (into.Count > 0) lap += Vector3.Distance(into[^1], p);
                into.Add(p);
            }
        }
        return lap;
    }

    /// <summary>
    /// The way through a junction from the end of one lane to the start of the next: a quadratic curve
    /// whose middle control point is where the two lanes' lines would meet, which is the curve a car
    /// turning from one into the other follows. Straight on, it is the straight line.
    /// </summary>
    public static List<Vector3> Connector(IReadOnlyList<Vector3> inPath, IReadOnlyList<Vector3> outPath)
    {
        Vector3 a = inPath[^1], b = outPath[0];
        Vector3 da = Flat(inPath[^1] - inPath[^2]), db = Flat(outPath[1] - outPath[0]);
        var pts = new List<Vector3>();
        float cross = da.X * db.Z - da.Z * db.X;
        Vector3 control;
        if (MathF.Abs(cross) < 1e-3f * da.Length() * db.Length())
            control = Vector3.Lerp(a, b, 0.5f);                               // straight on
        else
        {
            // a + t da = b + u db, in the ground plane.
            Vector3 w = Flat(b - a);
            float t = (w.X * db.Z - w.Z * db.X) / cross;
            control = a + da * t;
            control.Y = 0.5f * (a.Y + b.Y);
        }
        float len = Vector3.Distance(a, control) + Vector3.Distance(control, b);
        int n = Math.Max(2, (int)MathF.Ceiling(len / Step));
        for (int k = 1; k < n; k++)
        {
            float t = k / (float)n;
            pts.Add((1 - t) * (1 - t) * a + 2 * (1 - t) * t * control + t * t * b);
        }
        pts.Insert(0, a);
        pts.Add(b);
        return pts;
    }

    private static Vector3 Flat(Vector3 v) => new(v.X, 0f, v.Z);
}

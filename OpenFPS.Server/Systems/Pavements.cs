using System.Numerics;
using Arch.Core;
using OpenFPS.Common.Components;

namespace OpenFPS.Server.Systems;

/// <summary>
/// The walkable network of a map's pavements, derived, not declared: each slab named as a pavement
/// contributes its middle line, lines that meet make corners, and a route is the shortest way along
/// them between the nearest points to its ends. What stands on a pavement is stepped round
/// (<see cref="Clear"/>).
/// </summary>
public sealed class Pavements
{
    /// <summary>One pavement: its middle line, end to end, and half its width.</summary>
    public readonly record struct Strip(Vector2 A, Vector2 B, float HalfWidth, string Name);

    /// <summary>A solid thing a walk must not pass through: a box, maybe turned, in the world.</summary>
    public readonly record struct Solid(Vector3 Centre, Quaternion Rotation, Vector3 Half);

    private readonly List<Vector2> _nodes = new();
    private readonly List<List<(int To, float Metres)>> _links = new();
    /// <summary>Every link, once, for finding the nearest point on the network.</summary>
    private readonly List<(int A, int B)> _edges = new();

    public IReadOnlyList<Strip> Strips { get; }
    public int NodeCount => _nodes.Count;
    public bool IsEmpty => _edges.Count == 0;

    /// <summary>How far past its end a pavement's line still meets another: a corner's kerb radius.</summary>
    public const float CornerReachMetres = 4f;
    /// <summary>Two points this close on the network are one.</summary>
    private const float SameNodeMetres = 0.5f;

    private Pavements(IReadOnlyList<Strip> strips) { Strips = strips; }

    /// <summary>The network made of these pavements: their lines, joined where they meet.</summary>
    public static Pavements Build(IEnumerable<Strip> strips)
    {
        var list = strips.Where(s => Vector2.Distance(s.A, s.B) > 0.5f).ToList();
        var net = new Pavements(list);
        // The points along each strip: its ends and wherever another strip's line crosses it.
        var along = list.Select(_ => new List<(float T, int Node)>()).ToList();
        for (int i = 0; i < list.Count; i++)
        {
            along[i].Add((0f, net.NodeAt(list[i].A)));
            along[i].Add((1f, net.NodeAt(list[i].B)));
        }
        for (int i = 0; i < list.Count; i++)
            for (int j = i + 1; j < list.Count; j++)
                if (Meet(list[i], list[j], out float ti, out float tj, out var at))
                {
                    int node = net.NodeAt(at);
                    along[i].Add((ti, node));
                    along[j].Add((tj, node));
                }
        for (int i = 0; i < list.Count; i++)
        {
            var points = along[i].OrderBy(p => p.T).ToList();
            for (int k = 1; k < points.Count; k++)
                if (points[k].Node != points[k - 1].Node) net.Link(points[k - 1].Node, points[k].Node);
        }
        return net;
    }

    /// <summary>Where two pavements' lines meet, as fractions along each, allowing a corner's reach past
    /// either end. Parallel lines never meet.</summary>
    private static bool Meet(Strip a, Strip b, out float ta, out float tb, out Vector2 at)
    {
        ta = tb = 0f; at = default;
        var da = a.B - a.A; var db = b.B - b.A;
        float cross = da.X * db.Y - da.Y * db.X;
        float la = da.Length(), lb = db.Length();
        if (MathF.Abs(cross) < 1e-3f * la * lb) return false;
        var w = b.A - a.A;
        ta = (w.X * db.Y - w.Y * db.X) / cross;
        tb = (w.X * da.Y - w.Y * da.X) / cross;
        float ra = CornerReachMetres / la, rb = CornerReachMetres / lb;
        if (ta < -ra || ta > 1f + ra || tb < -rb || tb > 1f + rb) return false;
        ta = Math.Clamp(ta, 0f, 1f);
        tb = Math.Clamp(tb, 0f, 1f);
        at = a.A + da * ta;
        return true;
    }

    private int NodeAt(Vector2 p)
    {
        for (int i = 0; i < _nodes.Count; i++)
            if (Vector2.DistanceSquared(_nodes[i], p) < SameNodeMetres * SameNodeMetres) return i;
        _nodes.Add(p);
        _links.Add(new List<(int, float)>());
        return _nodes.Count - 1;
    }

    private void Link(int a, int b)
    {
        if (_links[a].Any(l => l.To == b)) return;
        float d = Vector2.Distance(_nodes[a], _nodes[b]);
        _links[a].Add((b, d));
        _links[b].Add((a, d));
        _edges.Add((a, b));
    }

    /// <summary>The nearest point on the network to <paramref name="p"/>, and the link it is on.</summary>
    public Vector2 Nearest(Vector2 p, out int edge)
    {
        edge = -1;
        var best = p; float bestD = float.MaxValue;
        for (int i = 0; i < _edges.Count; i++)
        {
            var q = OnSegment(p, _nodes[_edges[i].A], _nodes[_edges[i].B]);
            float d = Vector2.DistanceSquared(p, q);
            if (d < bestD) { bestD = d; best = q; edge = i; }
        }
        return best;
    }

    /// <summary>The nearest points on the network to <paramref name="p"/>, one per link, nearest first.</summary>
    public IEnumerable<(Vector2 At, int Edge)> NearestPoints(Vector2 p)
        => Enumerable.Range(0, _edges.Count)
                     .Select(i => (At: OnSegment(p, _nodes[_edges[i].A], _nodes[_edges[i].B]), Edge: i))
                     .OrderBy(x => Vector2.DistanceSquared(p, x.At));

    private static Vector2 OnSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float len2 = ab.LengthSquared();
        if (len2 < 1e-9f) return a;
        float t = Math.Clamp(Vector2.Dot(p - a, ab) / len2, 0f, 1f);
        return a + ab * t;
    }

    /// <summary>
    /// The way on foot from one point to another: onto the network at the nearest point to the start,
    /// along it, and off it at the nearest point to the end. The points to walk through, in order,
    /// starting with <paramref name="from"/> and ending with <paramref name="to"/>.
    /// </summary>
    public List<Vector2> Route(Vector2 from, Vector2 to) => Route(from, Nearest(from, out int e0), e0, to, Nearest(to, out int e1), e1);

    /// <summary>As <see cref="Route(Vector2, Vector2)"/>, onto and off the network at the points given.</summary>
    public List<Vector2> Route(Vector2 from, Vector2 on, int onEdge, Vector2 to, Vector2 off, int offEdge)
    {
        var path = new List<Vector2> { from };
        if (onEdge < 0 || offEdge < 0) { path.Add(to); return path; }
        if (onEdge == offEdge)
        {
            path.Add(on); path.Add(off); path.Add(to);
            return Tidy(path);
        }
        // Dijkstra over the nodes, from the two ends of the start's link to the two ends of the end's.
        int n = _nodes.Count;
        var dist = Enumerable.Repeat(float.MaxValue, n).ToArray();
        var prev = Enumerable.Repeat(-1, n).ToArray();
        var queue = new PriorityQueue<int, float>();
        var (sa, sb) = _edges[onEdge];
        dist[sa] = Vector2.Distance(on, _nodes[sa]); queue.Enqueue(sa, dist[sa]);
        dist[sb] = Vector2.Distance(on, _nodes[sb]); queue.Enqueue(sb, dist[sb]);
        while (queue.TryDequeue(out int u, out float du))
        {
            if (du > dist[u]) continue;
            foreach (var (v, w) in _links[u])
                if (du + w < dist[v]) { dist[v] = du + w; prev[v] = u; queue.Enqueue(v, dist[v]); }
        }
        var (ea, eb) = _edges[offEdge];
        float viaA = dist[ea] + Vector2.Distance(_nodes[ea], off);
        float viaB = dist[eb] + Vector2.Distance(_nodes[eb], off);
        int last = viaA <= viaB ? ea : eb;
        if (dist[last] == float.MaxValue) { path.Add(to); return path; }
        var nodes = new List<int>();
        for (int k = last; k >= 0; k = prev[k]) nodes.Add(k);
        nodes.Reverse();
        path.Add(on);
        foreach (int k in nodes) path.Add(_nodes[k]);
        path.Add(off);
        path.Add(to);
        return Tidy(path);
    }

    /// <summary>The length of a way, metres.</summary>
    public static float Length(IReadOnlyList<Vector2> path)
    {
        float m = 0f;
        for (int i = 1; i < path.Count; i++) m += Vector2.Distance(path[i - 1], path[i]);
        return m;
    }

    /// <summary>Drops points on top of each other.</summary>
    private static List<Vector2> Tidy(List<Vector2> path)
    {
        var tidy = new List<Vector2>();
        for (int i = 0; i < path.Count; i++)
        {
            var p = path[i];
            if (tidy.Count == 0 || Vector2.Distance(tidy[^1], p) > 0.05f) tidy.Add(p);
            else if (i == path.Count - 1) tidy[^1] = p;      // the end is where he means to be, exactly
        }
        return tidy;
    }

    // ── What stands in the way ──────────────────────────────────────────────────────────────────

    /// <summary>A body's half width plus a little, metres: how far from a solid a walker keeps.</summary>
    public const float BodyMetres = 0.3f;

    /// <summary>Whether a person standing at <paramref name="at"/> (feet on the ground) would be inside a solid.</summary>
    public static bool Blocked(IReadOnlyList<Solid> solids, Vector2 at, float groundY = 0f)
    {
        var p = new Vector3(at.X, groundY + 1.0f, at.Y);
        foreach (var s in solids)
        {
            // Cheap reject by the box's widest reach, then the box itself, widened by a body.
            float reach = MathF.Max(s.Half.X, s.Half.Z) * 1.42f + BodyMetres;
            if (MathF.Abs(p.X - s.Centre.X) > reach || MathF.Abs(p.Z - s.Centre.Z) > reach) continue;
            var local = Vector3.Transform(p - s.Centre, Quaternion.Inverse(s.Rotation));
            if (MathF.Abs(local.X) <= s.Half.X + BodyMetres && MathF.Abs(local.Z) <= s.Half.Z + BodyMetres
                && MathF.Abs(local.Y) <= s.Half.Y + 0.8f) return true;
        }
        return false;
    }

    /// <summary>Whether the straight line between two points passes through anything solid.</summary>
    public static bool LineBlocked(IReadOnlyList<Solid> solids, Vector2 a, Vector2 b, float groundY = 0f)
    {
        float len = Vector2.Distance(a, b);
        int steps = Math.Max(1, (int)MathF.Ceiling(len / 0.25f));
        for (int i = 0; i <= steps; i++)
            if (Blocked(solids, Vector2.Lerp(a, b, i / (float)steps), groundY)) return true;
        return false;
    }

    /// <summary>
    /// A way stepped round whatever stands on it. Each leg is walked in quarter-metre steps; a stretch
    /// that meets a solid is passed a little to one side or the other, whichever is nearest and clear,
    /// stepping out a metre before it and back a metre after. A stretch nothing will clear (a building
    /// across the way) is left as it is, to be walked through rather than not walked at all.
    /// </summary>
    public static List<Vector2> Clear(IReadOnlyList<Vector2> path, IReadOnlyList<Solid> solids, float groundY = 0f)
    {
        if (solids.Count == 0 || path.Count < 2) return path.ToList();
        var output = new List<Vector2> { path[0] };
        for (int i = 1; i < path.Count; i++)
        {
            var a = path[i - 1]; var b = path[i];
            float len = Vector2.Distance(a, b);
            if (len < 1e-3f) continue;
            var dir = (b - a) / len;
            var side = new Vector2(-dir.Y, dir.X);
            const float step = 0.25f;
            int steps = (int)MathF.Ceiling(len / step);
            int k = 0;
            while (k <= steps)
            {
                float s = MathF.Min(len, k * step);
                // The two ends are where somebody is standing on purpose: never stepped away from.
                if (k == 0 || k == steps || !Blocked(solids, a + dir * s, groundY)) { k++; continue; }
                int end = k;
                while (end < steps && Blocked(solids, a + dir * MathF.Min(len, end * step), groundY)) end++;
                float s0 = MathF.Max(0f, s - 1.0f), s1 = MathF.Min(len, end * step + 1.0f);
                bool done = false;
                foreach (float off in new[] { 0.8f, -0.8f, 1.6f, -1.6f, 2.4f, -2.4f, 3.2f, -3.2f })
                {
                    var p0 = a + dir * s0 + side * off;
                    var p1 = a + dir * s1 + side * off;
                    if (LineBlocked(solids, a + dir * MathF.Max(0f, s0 - 1.0f), p0, groundY)
                        || LineBlocked(solids, p0, p1, groundY)
                        || LineBlocked(solids, p1, a + dir * MathF.Min(len, s1 + 1.0f), groundY)) continue;
                    output.Add(a + dir * MathF.Max(0f, s0 - 1.0f));
                    output.Add(p0);
                    output.Add(p1);
                    done = true;
                    break;
                }
                k = done ? (int)MathF.Ceiling(MathF.Min(len, s1 + 1.0f) / step) + 1 : end + 1;
                if (done && s1 + 1.0f < len) output.Add(a + dir * (s1 + 1.0f));
            }
            output.Add(b);
        }
        return Tidy(output);
    }

    // ── Reading a map ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every pavement on a map: a slab whose name has "sidewalk" or "pavement" in it, lying flat (under
    /// half a metre thick) and longer than it is wide.
    /// </summary>
    public static List<Strip> StripsOf(World world)
    {
        var strips = new List<Strip>();
        world.Query(new QueryDescription().WithAll<Transform, ColliderComponent>().WithNone<Velocity>(),
            (Entity e, ref Transform t, ref ColliderComponent c) =>
            {
                string name = world.Has<IdentityComponent>(e) && !string.IsNullOrWhiteSpace(world.Get<IdentityComponent>(e).Name)
                    ? world.Get<IdentityComponent>(e).Name
                    : world.Has<NameComponent>(e) ? world.Get<NameComponent>(e).Name ?? "" : "";
                if (!IsPavement(name) || c.Shape != ColliderShape.Box || c.Size.Y > 0.5f) return;
                var x = Vector3.Transform(Vector3.UnitX, t.Rotation); x.Y = 0f;
                var z = Vector3.Transform(Vector3.UnitZ, t.Rotation); z.Y = 0f;
                if (x.LengthSquared() < 1e-6f || z.LengthSquared() < 1e-6f) return;
                x = Vector3.Normalize(x); z = Vector3.Normalize(z);
                bool alongX = c.Size.X >= c.Size.Z;
                var axis = alongX ? x : z;
                float half = (alongX ? c.Size.X : c.Size.Z) * 0.5f;
                float width = alongX ? c.Size.Z : c.Size.X;
                if (half * 2f < width * 1.5f) return;
                var centre = new Vector2(t.Position.X, t.Position.Z);
                var d = new Vector2(axis.X, axis.Z) * half;
                strips.Add(new Strip(centre - d, centre + d, width * 0.5f, name));
            });
        // In entity order, so the network, and every route on it, is the same every time.
        return strips;
    }

    public static bool IsPavement(string name)
        => name.Contains("sidewalk", StringComparison.OrdinalIgnoreCase) || name.Contains("pavement", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The solid boxes a person walking could meet: standing between knee and head height, not
    /// moving, not a door (a door is walked through, open). Floors, kerbs and roofs are not in the way.
    /// </summary>
    public static List<Solid> SolidsOf(World world)
    {
        var solids = new List<Solid>();
        world.Query(new QueryDescription().WithAll<Transform, ColliderComponent>().WithNone<Velocity, DoorComponent>(),
            (Entity e, ref Transform t, ref ColliderComponent c) =>
            {
                if (!c.IsSolid || c.Shape != ColliderShape.Box || c.Size.X <= 0 || c.Size.Y <= 0 || c.Size.Z <= 0) return;
                float bottom = t.Position.Y - c.Size.Y * 0.5f, top = t.Position.Y + c.Size.Y * 0.5f;
                if (top < 0.45f || bottom > 1.85f) return;
                solids.Add(new Solid(t.Position, t.Rotation, c.Size * 0.5f));
            });
        return solids;
    }
}

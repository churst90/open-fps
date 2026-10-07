using System.Numerics;

namespace OpenFPS.Common;

// Roads as data (docs/NEXT_BODIES_WHEELS_ROADS.md, stage 1). A map declares a road's centreline, type
// and lanes, and a junction's point; the lane segments between junctions and the turns between them
// are derived, so the two cannot drift apart.

/// <summary>One road, as the map declares it.</summary>
public class RoadData
{
    public string Id { get; set; } = string.Empty;
    /// <summary>What it is called, for speech: "Main Street".</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>The kind of road: "arterial", "collector", "residential", "service".</summary>
    public string Type { get; set; } = "collector";
    /// <summary>The middle of the carriageway, in order. Direction +1 is from the first point to the last.</summary>
    public List<Vector3> Centreline { get; set; } = new();
    /// <summary>Kerb to kerb, metres.</summary>
    public float WidthMetres { get; set; }
    public List<LaneData> Lanes { get; set; } = new();
    /// <summary>What the carriageway is made of, stretch by stretch along the centreline. A stretch
    /// not covered is <see cref="DefaultSurface"/>.</summary>
    public List<SurfaceData> Surfaces { get; set; } = new();

    /// <summary>On a tiled map, the tiles the centreline passes through (EntityData.Tile). Nothing reads it yet.</summary>
    public List<string>? Tiles { get; set; }

    public const string DefaultSurface = "Asphalt";
}

/// <summary>One lane of a road.</summary>
public class LaneData
{
    /// <summary>Metres from the centreline to the middle of the lane, positive to the RIGHT of the
    /// centreline's own direction.</summary>
    public float OffsetMetres { get; set; }
    /// <summary>+1: traffic goes the way the centreline runs. -1: the other way.</summary>
    public int Direction { get; set; } = 1;
    public float WidthMetres { get; set; } = 3f;
    public float SpeedLimitKmh { get; set; } = 50f;
}

/// <summary>A stretch of a road's surface.</summary>
public class SurfaceData
{
    public float FromMetres { get; set; }
    public float ToMetres { get; set; }
    /// <summary>An acoustic material name, as the registry knows it ("Asphalt", "Concrete").</summary>
    public string Material { get; set; } = RoadData.DefaultSurface;
}

/// <summary>Where roads meet. Which roads, and which of their lanes connect, is worked out.</summary>
public class JunctionData
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public Vector3 Position { get; set; }
    /// <summary>How far the junction reaches from its point, metres: where the approaching lanes stop.</summary>
    public float RadiusMetres { get; set; } = 8f;
    /// <summary>"give_way", "stop", "signal" or "none".</summary>
    public string Control { get; set; } = "give_way";
    /// <summary>The roads (ids) whose traffic has priority here. Everything else gives way. Empty:
    /// every approach gives way.</summary>
    public List<string> PriorityRoads { get; set; } = new();
    /// <summary>How long a vehicle waits at the line when it gives way, seconds.</summary>
    public float GiveWaySeconds { get; set; } = 2f;
    /// <summary>On a tiled map, the tile the junction is in (EntityData.Tile). Nothing reads it yet.</summary>
    public string? Tile { get; set; }

    /// <summary>Whether traffic arriving along this road has to give way.</summary>
    public bool GivesWay(RoadData road)
        => Control is "give_way" or "stop" && !PriorityRoads.Contains(road.Id);
}

/// <summary>Which way a connection through a junction turns.</summary>
public enum Turn { Straight, Left, Right, UTurn }

/// <summary>
/// The road network a map's roads and junctions describe: every lane cut into the stretches between
/// junctions, each one a directed line a vehicle can follow, and where each can go next.
/// </summary>
public sealed class RoadNetwork
{
    /// <summary>A lane between two junctions (or a junction and a road's end), in its own direction.</summary>
    public sealed class LaneSegment
    {
        public int Index { get; init; }
        public RoadData Road { get; init; } = null!;
        public LaneData Lane { get; init; } = null!;
        /// <summary>The path along the middle of the lane, in the direction of travel.</summary>
        public List<Vector3> Path { get; init; } = new();
        /// <summary>The junction at the start and at the end, or null at a road's end.</summary>
        public JunctionData? From { get; init; }
        public JunctionData? To { get; init; }
        public float LengthMetres { get; init; }
        /// <summary>Where this segment starts along the road's centreline, metres, and which way it
        /// runs along it: to read the surface under a point.</summary>
        public float StartAlongRoad { get; init; }
        public List<(LaneSegment Next, Turn Turn)> Next { get; } = new();
    }

    public IReadOnlyList<RoadData> Roads { get; }
    public IReadOnlyList<JunctionData> Junctions { get; }
    public IReadOnlyList<LaneSegment> Segments => _segments;
    /// <summary>Everything wrong with the data, one line each. Empty for a sound network.</summary>
    public IReadOnlyList<string> Problems => _problems;

    private readonly List<LaneSegment> _segments = new();
    private readonly List<string> _problems = new();

    public RoadNetwork(IReadOnlyList<RoadData>? roads, IReadOnlyList<JunctionData>? junctions)
    {
        Roads = roads ?? Array.Empty<RoadData>();
        Junctions = junctions ?? Array.Empty<JunctionData>();
        foreach (var road in Roads) Cut(road);
        Connect();
        Check();
    }

    public int DeadEnds => _segments.Count(s => s.Next.Count == 0);

    /// <summary>The network a map file's Roads and Junctions describe, read straight from its JSON (for
    /// tools that do not load a server). Null if it has none.</summary>
    public static RoadNetwork? FromMapJson(System.Text.Json.JsonElement root)
    {
        var o = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true, IncludeFields = true };
        if (!root.TryGetProperty("Roads", out var r)) return null;
        var roads = System.Text.Json.JsonSerializer.Deserialize<List<RoadData>>(r, o);
        var junctions = root.TryGetProperty("Junctions", out var j) ? System.Text.Json.JsonSerializer.Deserialize<List<JunctionData>>(j, o) : null;
        return roads is { Count: > 0 } ? new RoadNetwork(roads, junctions) : null;
    }

    /// <summary>The downtown square of the city, as traffic drives it, for tools and tests.</summary>
    public static readonly string[] DowntownCorners = { "j_-130_-130", "j_-130_130", "j_130_130", "j_130_-130" };

    // ── Building ────────────────────────────────────────────────────────────────────────────

    /// <summary>The junctions on a road, with how far along its centreline each one is.</summary>
    private List<(JunctionData J, float Along)> JunctionsOn(RoadData road)
    {
        var on = new List<(JunctionData, float)>();
        foreach (var j in Junctions)
        {
            var (along, off) = Project(road.Centreline, j.Position);
            if (off <= road.WidthMetres * 0.5f + 0.5f) on.Add((j, along));
        }
        on.Sort((a, b) => a.Item2.CompareTo(b.Item2));
        return on;
    }

    private void Cut(RoadData road)
    {
        if (road.Centreline.Count < 2) { _problems.Add($"road {road.Id}: fewer than two centreline points"); return; }
        if (road.Lanes.Count == 0) { _problems.Add($"road {road.Id}: no lanes"); return; }
        // One NaN in the data is a NaN lane length, and the route search compares lengths: NaN is never
        // shorter, so it re-queues the same lanes round a loop for ever.
        if (!road.Centreline.All(p => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z))
            || !float.IsFinite(road.WidthMetres) || !road.Lanes.All(l => float.IsFinite(l.OffsetMetres)))
        { _problems.Add($"road {road.Id}: a position, width or lane offset is not a number"); return; }
        float length = Length(road.Centreline);
        var stops = JunctionsOn(road);
        // The pieces between one junction (or end) and the next.
        var marks = new List<(float Along, JunctionData? J)> { (0f, null) };
        foreach (var (j, along) in stops)
        {
            // A road that ends within a junction's reach ends in it: its lanes stop at the edge.
            if (along <= j.RadiusMetres) marks[0] = (0f, j);
            else if (along >= length - j.RadiusMetres) { marks.Add((length, j)); continue; }
            else marks.Add((along, j));
        }
        if (marks[^1].Along < length - 1e-3f) marks.Add((length, null));

        for (int k = 0; k + 1 < marks.Count; k++)
        {
            var (a, ja) = marks[k];
            var (b, jb) = marks[k + 1];
            // Measured from the junction's own point, not from where the road happens to begin.
            float from = ja != null ? Project(road.Centreline, ja.Position).Along + ja.RadiusMetres : a;
            float to = jb != null ? Project(road.Centreline, jb.Position).Along - jb.RadiusMetres : b;
            if (!(to - from >= 1f)) { _problems.Add($"road {road.Id}: {to - from:F1} m between {ja?.Id ?? "its start"} and {jb?.Id ?? "its end"}"); continue; }
            foreach (var lane in road.Lanes)
            {
                var path = Offset(Slice(road.Centreline, from, to), lane.OffsetMetres);
                bool forward = lane.Direction >= 0;
                if (!forward) path.Reverse();
                _segments.Add(new LaneSegment
                {
                    Index = _segments.Count, Road = road, Lane = lane, Path = path,
                    From = forward ? ja : jb, To = forward ? jb : ja,
                    LengthMetres = Length(path), StartAlongRoad = forward ? from : to,
                });
            }
        }
    }

    /// <summary>
    /// At each junction, every arriving lane can go into every leaving lane except back the way it came.
    /// No lane rules (right from the kerb lane, left from the inner) until vehicles change lanes along a
    /// block: tried 2026-09-27, tours ran into dead ends. Two cars side by side at a junction are kept
    /// apart instead: the one further back gives way.
    /// </summary>
    private void Connect()
    {
        foreach (var j in Junctions)
        {
            var arriving = _segments.Where(s => s.To == j).ToList();
            var leaving = _segments.Where(s => s.From == j).ToList();
            foreach (var a in arriving)
            foreach (var l in leaving)
            {
                var turn = TurnBetween(a, l);
                if (turn == Turn.UTurn) continue;
                a.Next.Add((l, turn));
            }
        }
    }

    /// <summary>The lane nearest the kerb for its direction of travel.</summary>
    public static bool IsKerbLane(LaneSegment s) => LanePlace(s) == 0;

    /// <summary>0 for the kerb lane, counting inward.</summary>
    private static int LanePlace(LaneSegment s)
    {
        float mine = MathF.Abs(s.Lane.OffsetMetres);
        return s.Road.Lanes.Count(l => l.Direction == s.Lane.Direction && MathF.Abs(l.OffsetMetres) > mine + 0.01f);
    }

    private void Check()
    {
        // Which junctions each road passes through, once per road rather than once per junction: on
        // a town's two hundred roads and junctions the second was a hundred million projections.
        var on = new Dictionary<JunctionData, int>();
        foreach (var r in Roads)
        {
            if (r.Centreline.Count < 2) continue;
            foreach (var j in JunctionsOn(r).Select(x => x.J).Distinct())
                on[j] = on.GetValueOrDefault(j) + 1;
        }
        foreach (var j in Junctions)
        {
            int roads = on.GetValueOrDefault(j);
            if (roads < 2) _problems.Add($"junction {j.Id}: on {roads} road(s)");
        }
        foreach (var road in Roads)
        {
            float lanes = road.Lanes.Sum(l => l.WidthMetres);
            if (road.WidthMetres > 0f && lanes > road.WidthMetres + 0.01f)
                _problems.Add($"road {road.Id}: lanes {lanes:F1} m wide on a {road.WidthMetres:F1} m carriageway");
            foreach (var lane in road.Lanes)
                if (MathF.Abs(lane.OffsetMetres) + lane.WidthMetres * 0.5f > road.WidthMetres * 0.5f + 0.01f)
                    _problems.Add($"road {road.Id}: a lane at {lane.OffsetMetres:F1} m runs off the carriageway");
        }
        foreach (var s in _segments)
            if (s.To != null && s.Next.Count == 0)
                _problems.Add($"{s.Road.Id} lane {s.Lane.OffsetMetres:F1}: arrives at {s.To.Id} and cannot leave it");
    }

    // ── Reading ─────────────────────────────────────────────────────────────────────────────

    /// <summary>What the road is made of this far along its centreline.</summary>
    public static string SurfaceAt(RoadData road, float alongMetres)
    {
        foreach (var s in road.Surfaces)
            if (alongMetres >= s.FromMetres && alongMetres < s.ToMetres) return s.Material;
        return RoadData.DefaultSurface;
    }

    // ── Geometry, in the ground plane ───────────────────────────────────────────────────────

    public static float Length(IReadOnlyList<Vector3> p)
    {
        float l = 0f;
        for (int i = 1; i < p.Count; i++) l += Flat(p[i] - p[i - 1]).Length();
        return l;
    }

    private static Vector3 Flat(Vector3 v) => new(v.X, 0f, v.Z);

    /// <summary>How far along a polyline the point nearest <paramref name="p"/> is, and how far off it.</summary>
    public static (float Along, float Off) Project(IReadOnlyList<Vector3> line, Vector3 p)
    {
        float along = 0f, bestAlong = 0f, bestOff = float.MaxValue;
        for (int i = 1; i < line.Count; i++)
        {
            Vector3 a = Flat(line[i - 1]), b = Flat(line[i]), q = Flat(p);
            Vector3 ab = b - a;
            float len = ab.Length();
            if (len < 1e-6f) continue;
            float t = Math.Clamp(Vector3.Dot(q - a, ab) / (len * len), 0f, 1f);
            float off = Vector3.Distance(q, a + ab * t);
            if (off < bestOff) { bestOff = off; bestAlong = along + t * len; }
            along += len;
        }
        return (bestAlong, bestOff);
    }

    /// <summary>The part of a polyline between two distances along it.</summary>
    public static List<Vector3> Slice(IReadOnlyList<Vector3> line, float from, float to)
    {
        var o = new List<Vector3> { PointAt(line, from) };
        float along = 0f;
        for (int i = 1; i < line.Count; i++)
        {
            along += Flat(line[i] - line[i - 1]).Length();
            if (along > from && along < to) o.Add(line[i]);
        }
        o.Add(PointAt(line, to));
        return o;
    }

    public static Vector3 PointAt(IReadOnlyList<Vector3> line, float d)
    {
        float along = 0f;
        for (int i = 1; i < line.Count; i++)
        {
            float len = Flat(line[i] - line[i - 1]).Length();
            if (along + len >= d && len > 0f) return Vector3.Lerp(line[i - 1], line[i], (d - along) / len);
            along += len;
        }
        return line[^1];
    }

    /// <summary>A polyline moved sideways, positive to the right of its direction.</summary>
    public static List<Vector3> Offset(IReadOnlyList<Vector3> line, float right)
    {
        var o = new List<Vector3>(line.Count);
        for (int i = 0; i < line.Count; i++)
        {
            Vector3 dir = Flat(line[Math.Min(i + 1, line.Count - 1)] - line[Math.Max(i - 1, 0)]);
            if (dir.LengthSquared() < 1e-9f) { o.Add(line[i]); continue; }
            dir = Vector3.Normalize(dir);
            // x east, z north: the right of a heading (dx, dz) is (dz, -dx).
            o.Add(line[i] + new Vector3(dir.Z, 0f, -dir.X) * right);
        }
        return o;
    }

    private static Turn TurnBetween(LaneSegment arriving, LaneSegment leaving)
    {
        Vector3 inDir = Heading(arriving.Path, atEnd: true), outDir = Heading(leaving.Path, atEnd: false);
        float cross = inDir.X * outDir.Z - inDir.Z * outDir.X;       // + is a turn to the left (x east, z north)
        float dot = Vector3.Dot(inDir, outDir);
        if (dot < -0.7f) return Turn.UTurn;
        if (dot > 0.7f) return Turn.Straight;
        return cross > 0f ? Turn.Left : Turn.Right;
    }

    private static Vector3 Heading(IReadOnlyList<Vector3> p, bool atEnd)
    {
        Vector3 d = atEnd ? p[^1] - p[^2] : p[1] - p[0];
        d = Flat(d);
        return d.LengthSquared() > 1e-9f ? Vector3.Normalize(d) : Vector3.UnitZ;
    }
}

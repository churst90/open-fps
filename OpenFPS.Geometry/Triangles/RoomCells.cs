using System.Numerics;

namespace OpenFPS.Common.Geometry;

/// <summary>
/// Which room's flood holds a point (docs/GEOMETRY.md 2.7, 12.6): every flooded room's air and the wall cells round
/// it, as runs of cells along x, sorted so a point is a binary search. Where no flood holds a point it is in no
/// flooded room, whatever their boxes say; a room that did not flood (its middle in a solid) is not here.
/// </summary>
public sealed class RoomCells
{
    public static readonly RoomCells Empty = new(Array.Empty<Span>(), new HashSet<int>(), new Dictionary<int, RoomFlood.Lining>(), Vector3.Zero,
                                                 new List<FloodOpenings.Frame>());

    /// <summary>Cells x0 to x1 of row (y, z), the room's id.</summary>
    private readonly record struct Span(long Row, int X0, int X1, int Room);

    private readonly Span[] _spans;
    private readonly HashSet<int> _flooded;
    private readonly Dictionary<int, RoomFlood.Lining> _linings;

    public Vector3 Origin { get; }
    /// <summary>The rooms that flooded: their floods, not their boxes, say where they are.</summary>
    public IReadOnlySet<int> Flooded => _flooded;
    public int SpanCount => _spans.Length;

    private RoomCells(Span[] spans, HashSet<int> flooded, Dictionary<int, RoomFlood.Lining> linings, Vector3 origin,
                      List<FloodOpenings.Frame> openings)
    { _spans = spans; _flooded = flooded; _linings = linings; Origin = origin; Openings = openings; }

    /// <summary>The openings as the floods met them, and where a flood met the open sky (<see cref="FloodOpenings"/>).</summary>
    public IReadOnlyList<FloodOpenings.Frame> Openings { get; }

    /// <summary>What lines a flooded room's air, where the flood measured it.</summary>
    public bool TryLining(int room, out RoomFlood.Lining lining) => _linings.TryGetValue(room, out lining!);

    private static long RowOf(int y, int z) => ((long)z << 32) | (uint)y;

    /// <summary>The floods of <paramref name="rooms"/> (RoomFlood.Flood), all from one origin, and the openings they
    /// met framed (<paramref name="barriers"/>, the ones they were flooded with).</summary>
    public static RoomCells From(IReadOnlyDictionary<int, RoomFlood.Room> rooms, IReadOnlyList<RoomFlood.Barrier>? barriers = null)
    {
        var spans = new List<Span>();
        var flooded = new HashSet<int>();
        var linings = new Dictionary<int, RoomFlood.Lining>();
        Vector3 origin = Vector3.Zero;
        var edges = new List<Span>();
        foreach (var (id, room) in rooms)
        {
            if (room.Unseeded || room.AirCount == 0) continue;
            flooded.Add(id);
            if (room.Lining != null) linings[id] = room.Lining;
            origin = room.Origin;
            foreach (var r in room.AirRuns) spans.Add(new Span(RowOf(r.Y, r.Z), r.X, r.X + r.Length - 1, id));
            foreach (var r in room.EdgeRuns) edges.Add(new Span(RowOf(r.Y, r.Z), r.X, r.X + r.Length - 1, id));
        }
        // Rooms flooded apart can both hold a wall's or an opening's cells between them: the air's first, then the
        // lower id, so whoever floods with whatever else, a cell is one room's.
        var seedOf = new Dictionary<int, Vector3>();
        foreach (var (id, room) in rooms) seedOf[id] = room.Seed;
        spans = Disjoint(spans, edges, seedOf, origin);
        // A room's air and its wall cells side by side in a row are one span.
        var merged = new List<Span>(spans.Count);
        foreach (var s in spans)
        {
            if (merged.Count > 0 && merged[^1] is var last && last.Row == s.Row && last.Room == s.Room && last.X1 + 1 == s.X0)
                merged[^1] = last with { X1 = s.X1 };
            else merged.Add(s);
        }
        return new RoomCells(merged.ToArray(), flooded, linings, origin, FloodOpenings.From(rooms, barriers ?? Array.Empty<RoomFlood.Barrier>()));
    }

    /// <summary>The air spans as they are (no two rooms' air is the same cell), and the wall spans cut to what the air
    /// does not hold, a cell two rooms' walls both hold going to the room whose seed is nearer (then the lower id), as
    /// a flood of the two together would give it to the first to reach it. Sorted by row and x.</summary>
    private static List<Span> Disjoint(List<Span> air, List<Span> edges, IReadOnlyDictionary<int, Vector3> seedOf, Vector3 origin)
    {
        int Order(Span a, Span b) => a.Row != b.Row ? a.Row.CompareTo(b.Row) : a.X0 != b.X0 ? a.X0.CompareTo(b.X0) : a.Room.CompareTo(b.Room);
        air.Sort(Order);
        var airByRow = new Dictionary<long, List<(int X0, int X1)>>();
        foreach (var a in air) { if (!airByRow.TryGetValue(a.Row, out var l)) airByRow[a.Row] = l = new(); l.Add((a.X0, a.X1)); }
        var result = new List<Span>(air);
        foreach (var row in edges.GroupBy(e => e.Row))
        {
            long r = row.Key;
            int y = (int)(uint)(r & 0xFFFFFFFF), z = (int)(r >> 32);
            var list = row.ToList();
            // The row cut at every span's ends; each piece is held by the spans over it.
            var cuts = new SortedSet<int>();
            foreach (var e in list) { cuts.Add(e.X0); cuts.Add(e.X1 + 1); }
            if (airByRow.TryGetValue(r, out var al)) foreach (var (a0, a1) in al) { cuts.Add(a0); cuts.Add(a1 + 1); }
            var bounds = cuts.ToArray();
            for (int b = 0; b + 1 < bounds.Length; b++)
            {
                int x0 = bounds[b], x1 = bounds[b + 1] - 1;
                if (al != null && al.Any(a => a.X0 <= x0 && a.X1 >= x1)) continue;
                var over = list.Where(e => e.X0 <= x0 && e.X1 >= x1).Select(e => e.Room).Distinct().OrderBy(id => id).ToList();
                if (over.Count == 0) continue;
                if (over.Count == 1) { result.Add(new Span(r, x0, x1, over[0])); continue; }
                for (int x = x0; x <= x1; x++)
                {
                    var c = RoomFlood.CentreOf(x, y, z, origin);
                    int best = over[0];
                    float bd = Vector3.DistanceSquared(c, seedOf.GetValueOrDefault(best));
                    foreach (int id in over.Skip(1))
                    {
                        float d = Vector3.DistanceSquared(c, seedOf.GetValueOrDefault(id));
                        if (d < bd) { bd = d; best = id; }
                    }
                    result.Add(new Span(r, x, x, best));
                }
            }
        }
        result.Sort(Order);
        return result;
    }

    /// <summary>Every span: its room, its row's y and z, and its first and last x, in cells from the origin.</summary>
    public void ForEachSpan(Action<int, int, int, int, int> each)
    {
        foreach (var s in _spans) each(s.Room, (int)(uint)(s.Row & 0xFFFFFFFF), (int)(s.Row >> 32), s.X0, s.X1);
    }

    /// <summary>The flooded room whose cells hold <paramref name="p"/>, or false.</summary>
    public bool TryRoomAt(Vector3 p, out int room)
    {
        room = 0;
        if (_spans.Length == 0) return false;
        var (x, y, z) = RoomFlood.CellOf(p, Origin);
        long row = RowOf(y, z);
        // The last span starting at or before x in this row.
        int lo = 0, hi = _spans.Length - 1, at = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            ref readonly var s = ref _spans[mid];
            if (s.Row < row || (s.Row == row && s.X0 <= x)) { at = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        if (at < 0) return false;
        ref readonly var hit = ref _spans[at];
        if (hit.Row != row || x > hit.X1) return false;
        room = hit.Room;
        return true;
    }
}

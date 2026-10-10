using System.Numerics;

namespace OpenFPS.Common.Geometry;

/// <summary>
/// Which room's flood holds a point (docs/GEOMETRY.md 2.7, 12.6): every flooded room's air and the wall cells round
/// it, as runs of cells along x, sorted so a point is a binary search. Where no flood holds a point it is in no
/// flooded room, whatever their boxes say; a room that did not flood (its middle in a solid) is not here.
/// </summary>
public sealed class RoomCells
{
    public static readonly RoomCells Empty = new(Array.Empty<Span>(), new HashSet<int>(), Vector3.Zero);

    /// <summary>Cells x0 to x1 of row (y, z), the room's id.</summary>
    private readonly record struct Span(long Row, int X0, int X1, int Room);

    private readonly Span[] _spans;
    private readonly HashSet<int> _flooded;

    public Vector3 Origin { get; }
    /// <summary>The rooms that flooded: their floods, not their boxes, say where they are.</summary>
    public IReadOnlySet<int> Flooded => _flooded;
    public int SpanCount => _spans.Length;

    private RoomCells(Span[] spans, HashSet<int> flooded, Vector3 origin) { _spans = spans; _flooded = flooded; Origin = origin; }

    private static long RowOf(int y, int z) => ((long)z << 32) | (uint)y;

    /// <summary>The floods of <paramref name="rooms"/> (RoomFlood.Flood), all from one origin.</summary>
    public static RoomCells From(IReadOnlyDictionary<int, RoomFlood.Room> rooms)
    {
        var spans = new List<Span>();
        var flooded = new HashSet<int>();
        Vector3 origin = Vector3.Zero;
        foreach (var (id, room) in rooms)
        {
            if (room.Unseeded || room.AirCount == 0) continue;
            flooded.Add(id);
            origin = room.Origin;
            foreach (var r in room.AirRuns.Concat(room.EdgeRuns)) spans.Add(new Span(RowOf(r.Y, r.Z), r.X, r.X + r.Length - 1, id));
        }
        spans.Sort((a, b) => a.Row != b.Row ? a.Row.CompareTo(b.Row) : a.X0.CompareTo(b.X0));
        // A room's air and its wall cells side by side in a row are one span.
        var merged = new List<Span>(spans.Count);
        foreach (var s in spans)
        {
            if (merged.Count > 0 && merged[^1] is var last && last.Row == s.Row && last.Room == s.Room && last.X1 + 1 == s.X0)
                merged[^1] = last with { X1 = s.X1 };
            else merged.Add(s);
        }
        return new RoomCells(merged.ToArray(), flooded, origin);
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

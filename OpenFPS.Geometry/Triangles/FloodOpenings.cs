using System.Numerics;

namespace OpenFPS.Common.Geometry;

/// <summary>
/// Openings framed from where the floods meet them (docs/GEOMETRY.md 12.6, step 3): each opening with no leaf as the
/// rooms' air reaches it, not as its author drew it or as a box's face would have it, and each place a room's air
/// meets the open sky with no opening there. A car park's open side between its piers is a frame a bay; a stair hole
/// with its flight in it is what the flight leaves open.
/// </summary>
public static class FloodOpenings
{
    /// <summary>One frame: the opening (<see cref="RoomFlood.Barrier.Id"/>, or 0 where a room meets the sky), the room
    /// whose air met it, its middle, its turn (X across, Y up, Z through), its size, and its area (the cells its air
    /// met, less than width by height where something stands in it).</summary>
    public readonly record struct Frame(int Opening, int Room, Vector3 Centre, Quaternion Rotation, Vector3 Size, float Area);

    /// <summary>The frames of every opening the floods met, each opening's in pieces where solids part it (a pier, a
    /// mullion), biggest first; then the floods' patches of open sky, a frame per patch on each face.</summary>
    public static List<Frame> From(IReadOnlyDictionary<int, RoomFlood.Room> rooms, IReadOnlyList<RoomFlood.Barrier> barriers)
    {
        var frames = new List<Frame>();
        var byId = new Dictionary<int, RoomFlood.Barrier>();
        foreach (var b in barriers) byId[b.Id] = b;
        var cells = new Dictionary<int, (int Room, HashSet<(int, int, int)> Cells, Vector3 Origin)>();
        foreach (var (id, room) in rooms.OrderBy(r => r.Key))
            foreach (var (opening, x, y, z) in room.OpeningCells)
            {
                if (!cells.TryGetValue(opening, out var e)) cells[opening] = e = (id, new HashSet<(int, int, int)>(), room.Origin);
                e.Cells.Add((x, y, z));
            }
        foreach (var (opening, (room, set, origin)) in cells.OrderBy(c => c.Key))
        {
            if (!byId.TryGetValue(opening, out var b)) continue;
            var rot = b.Rotation.LengthSquared() < 1e-6f ? Quaternion.Identity : Quaternion.Normalize(b.Rotation);
            var points = set.Select(c => Vector3.Transform(RoomFlood.CentreOf(c.Item1, c.Item2, c.Item3, origin) - b.Centre, Quaternion.Conjugate(rot)));
            foreach (var (u0, u1, v0, v1, area) in Patches(points.Select(p => (p.X, p.Y)), b.Size.X * 0.5f, b.Size.Y * 0.5f))
            {
                var mid = new Vector3((u0 + u1) * 0.5f, (v0 + v1) * 0.5f, 0f);
                frames.Add(new Frame(opening, room, b.Centre + Vector3.Transform(mid, rot), rot,
                                     new Vector3(u1 - u0, v1 - v0, MathF.Max(b.Size.Z, 0.05f)), area));
            }
        }

        // Air meeting the sky: by the face it meets it through and the plane it lies in.
        var turns = new[]
        {
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2), Quaternion.CreateFromAxisAngle(Vector3.UnitY, -MathF.PI / 2),
            Quaternion.CreateFromAxisAngle(Vector3.UnitX, -MathF.PI / 2), Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2),
            Quaternion.Identity, Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI),
        };
        foreach (var (id, room) in rooms.OrderBy(r => r.Key))
        {
            if (room.SkyCells.Count == 0) continue;
            foreach (var plane in room.SkyCells.GroupBy(c => (c.Dir, c.Dir < 2 ? c.X : c.Dir < 4 ? c.Y : c.Z)).OrderBy(g => g.Key))
            {
                var rot = turns[plane.Key.Dir];
                var centre = RoomFlood.CentreOf(plane.First().X, plane.First().Y, plane.First().Z, room.Origin);
                var points = plane.Select(c => Vector3.Transform(RoomFlood.CentreOf(c.X, c.Y, c.Z, room.Origin) - centre, Quaternion.Conjugate(rot)));
                foreach (var (u0, u1, v0, v1, area) in Patches(points.Select(p => (p.X, p.Y)), float.PositiveInfinity, float.PositiveInfinity))
                {
                    var mid = new Vector3((u0 + u1) * 0.5f, (v0 + v1) * 0.5f, 0f);
                    frames.Add(new Frame(0, id, centre + Vector3.Transform(mid, rot), rot, new Vector3(u1 - u0, v1 - v0, RoomFlood.Cell), area));
                }
            }
        }
        return frames;
    }

    /// <summary>
    /// Points in a plane (across, up) as cells of the flood's size, joined where they touch along an axis: each patch's
    /// bounds half a cell round its points, within half-width <paramref name="hu"/> and half-height <paramref name="hv"/> of the
    /// middle, and its area as the cells it holds. A patch of one cell is dropped: a corner the air only touched.
    /// Biggest first.
    /// </summary>
    private static List<(float U0, float U1, float V0, float V1, float Area)> Patches(IEnumerable<(float U, float V)> points, float hu, float hv)
    {
        const float c = RoomFlood.Cell;
        // A cell's middle a whole number of cells from another's lands in the bin it should, not the one before it.
        var bins = new Dictionary<(int, int), (float U0, float U1, float V0, float V1)>();
        foreach (var (u, v) in points)
        {
            var key = ((int)MathF.Floor(u / c + 1e-3f), (int)MathF.Floor(v / c + 1e-3f));
            bins[key] = bins.TryGetValue(key, out var b) ? (MathF.Min(b.U0, u), MathF.Max(b.U1, u), MathF.Min(b.V0, v), MathF.Max(b.V1, v)) : (u, u, v, v);
        }
        var seen = new HashSet<(int, int)>();
        var patches = new List<(float, float, float, float, float)>();
        foreach (var start in bins.Keys.OrderBy(b => b.Item2).ThenBy(b => b.Item1))
        {
            if (!seen.Add(start)) continue;
            int n = 0;
            float pu0 = float.MaxValue, pu1 = float.MinValue, pv0 = float.MaxValue, pv1 = float.MinValue;
            var stack = new Stack<(int, int)>();
            stack.Push(start);
            while (stack.Count > 0)
            {
                var (i, j) = stack.Pop();
                n++;
                var b = bins[(i, j)];
                pu0 = MathF.Min(pu0, b.U0); pu1 = MathF.Max(pu1, b.U1); pv0 = MathF.Min(pv0, b.V0); pv1 = MathF.Max(pv1, b.V1);
                foreach (var next in new[] { (i + 1, j), (i - 1, j), (i, j + 1), (i, j - 1) })
                    if (bins.ContainsKey(next) && seen.Add(next)) stack.Push(next);
            }
            if (n < 2) continue;
            // Half a cell round the middles of the cells it holds.
            float u0 = MathF.Max(-hu, pu0 - c * 0.5f), u1 = MathF.Min(hu, pu1 + c * 0.5f);
            float v0 = MathF.Max(-hv, pv0 - c * 0.5f), v1 = MathF.Min(hv, pv1 + c * 0.5f);
            if (u1 - u0 < 0.01f || v1 - v0 < 0.01f) continue;
            float area = MathF.Min(n * c * c, (u1 - u0) * (v1 - v0));
            patches.Add((u0, u1, v0, v1, area));
        }
        patches.Sort((a, b) => b.Item5.CompareTo(a.Item5));
        return patches;
    }
}

using System.Numerics;

namespace OpenFPS.Common.Geometry;

/// <summary>
/// Rooms measured from the walls (docs/GEOMETRY.md 2.7, 12.6): each room is a seed (its authored or generated box,
/// a hint) and its shape is a flood from the box's middle through the space the solids leave, stopped by walls,
/// floors, ceilings and shut door leaves, and kept within the hint box grown by a margin, so an open doorway does
/// not flood the street. An L, a bay, a porch inside the walls, a room whose box overhangs its walls, all come out
/// as the walls make them. All rooms flood at once, a cell going to the room that reaches it first, so an open
/// archway is shared by distance.
/// </summary>
public static class RoomFlood
{
    /// <summary>The cell the flood moves in, metres: the acoustic grid's own (its voxels are the flood's cells). A
    /// solid box thinner than a cell is taken as a cell thick, so a door leaf, a pane or a 25 cm wall stops a
    /// 6-connected flood.</summary>
    public const float Cell = 0.5f;

    /// <summary>How far past its hint box a room may reach, metres.</summary>
    public const float DefaultMargin = 1.0f;

    /// <summary>A room to flood: its id, and its hint box.</summary>
    public readonly record struct Seed(int Id, Vector3 Centre, Vector3 Size, Quaternion Rotation);

    /// <summary>One room's flood: its cells (centres), whether it reached the edge of its margin somewhere (open to
    /// beyond, as through an open doorway), and whether it could not start (its middle in a solid).</summary>
    public sealed class Room
    {
        public required int Id { get; init; }
        /// <summary>The room's air: the cells the flood reached.</summary>
        public List<Vector3> Cells { get; } = new();
        /// <summary>The cells of its walls, floor and ceiling next to its air: where a body standing against a wall
        /// is (its middle within half a cell of the wall), so the room is the room up to its walls, as its box was.</summary>
        public List<Vector3> Edge { get; } = new();
        public bool Open { get; set; }
        public bool Unseeded { get; set; }
        public float Volume => Cells.Count * Cell * Cell * Cell;
    }

    private static long Key(int x, int y, int z) => ((long)(x & 0x1FFFFF) << 42) | ((long)(y & 0x1FFFFF) << 21) | (long)(z & 0x1FFFFF);

    /// <summary>Floods every seed through <paramref name="world"/>'s solids of <paramref name="layers"/>. Cells
    /// are on a grid through <paramref name="origin"/> (the acoustic map's corner, so a cell is one of its voxels).</summary>
    public static Dictionary<int, Room> Flood(TriangleWorld world, IReadOnlyList<Seed> seeds, Vector3 origin,
                                              float margin = DefaultMargin, GeometryLayers layers = GeometryLayers.Acoustics)
    {
        var rooms = new Dictionary<int, Room>();
        if (seeds.Count == 0) return rooms;
        var claim = new Dictionary<long, int>();
        var bounds = new (Matrix4x4 Inv, Vector3 Half)[seeds.Count];
        var queue = new Queue<(int X, int Y, int Z, int Seed)>();
        for (int si = 0; si < seeds.Count; si++)
        {
            var s = seeds[si];
            var rot = s.Rotation.LengthSquared() < 1e-6f ? Quaternion.Identity : Quaternion.Normalize(s.Rotation);
            Matrix4x4.Invert(Matrix4x4.CreateFromQuaternion(rot) * Matrix4x4.CreateTranslation(s.Centre), out var inv);
            bounds[si] = (inv, s.Size * 0.5f + new Vector3(margin));
            rooms[s.Id] = new Room { Id = s.Id };
        }
        var solids = new SolidRaster(world, layers, origin);
        for (int si = 0; si < seeds.Count; si++)
        {
            var p = seeds[si].Centre - origin;
            int x = (int)MathF.Floor(p.X / Cell), y = (int)MathF.Floor(p.Y / Cell), z = (int)MathF.Floor(p.Z / Cell);
            // A middle in a solid (a column in the room): the nearest free cell within a metre.
            if (solids.Blocked(x, y, z))
            {
                bool found = false;
                for (int r = 1; r <= 2 && !found; r++)
                    for (int dz = -r; dz <= r && !found; dz++)
                        for (int dy = -r; dy <= r && !found; dy++)
                            for (int dx = -r; dx <= r && !found; dx++)
                            {
                                if (Math.Max(Math.Abs(dx), Math.Max(Math.Abs(dy), Math.Abs(dz))) != r) continue;
                                if (solids.Blocked(x + dx, y + dy, z + dz) || claim.ContainsKey(Key(x + dx, y + dy, z + dz))) continue;
                                (x, y, z) = (x + dx, y + dy, z + dz);
                                found = true;
                            }
                if (!found) { rooms[seeds[si].Id].Unseeded = true; continue; }
            }
            if (!claim.TryAdd(Key(x, y, z), si)) continue;
            queue.Enqueue((x, y, z, si));
        }
        Span<(int, int, int)> steps = stackalloc (int, int, int)[] { (1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1) };
        var walls = new List<(int X, int Y, int Z, int Seed)>();
        while (queue.Count > 0)
        {
            var (cx, cy, cz, si) = queue.Dequeue();
            var room = rooms[seeds[si].Id];
            room.Cells.Add(CentreOf(cx, cy, cz, origin));
            var (inv, h) = bounds[si];
            foreach (var (dx, dy, dz) in steps)
            {
                int nx = cx + dx, ny = cy + dy, nz = cz + dz;
                long k = Key(nx, ny, nz);
                if (claim.ContainsKey(k)) continue;
                // Inside the hint grown by the margin, in the hint's own frame.
                var local = Vector3.Transform(CentreOf(nx, ny, nz, origin), inv);
                if (MathF.Abs(local.X) > h.X || MathF.Abs(local.Y) > h.Y || MathF.Abs(local.Z) > h.Z) { room.Open = true; continue; }
                if (solids.Blocked(nx, ny, nz)) { walls.Add((nx, ny, nz, si)); continue; }
                claim[k] = si;
                queue.Enqueue((nx, ny, nz, si));
            }
        }
        // Each room's air takes the wall cells next to it (the first room to reach one), so it reaches its walls.
        foreach (var (wx, wy, wz, si) in walls)
            if (claim.TryAdd(Key(wx, wy, wz), si)) rooms[seeds[si].Id].Edge.Add(CentreOf(wx, wy, wz, origin));
        return rooms;
    }

    /// <summary>Whether the cell holding <paramref name="p"/> is solid, as a flood would find it.</summary>
    public static bool Solid(TriangleWorld world, Vector3 p, Vector3 origin, GeometryLayers layers = GeometryLayers.Acoustics)
    {
        var q = p - origin;
        return new SolidRaster(world, layers, origin).Blocked((int)MathF.Floor(q.X / Cell), (int)MathF.Floor(q.Y / Cell), (int)MathF.Floor(q.Z / Cell));
    }

    private static Vector3 CentreOf(int x, int y, int z, Vector3 origin)
        => origin + new Vector3((x + 0.5f) * Cell, (y + 0.5f) * Cell, (z + 0.5f) * Cell);

    /// <summary>
    /// Which cells the solids fill, worked out a block of the map at a time as the flood asks. A box is a cell
    /// thick at least along each of its axes; a shape is asked whether it holds each cell's middle.
    /// </summary>
    private sealed class SolidRaster
    {
        private readonly TriangleWorld _world;
        private readonly GeometryLayers _layers;
        private readonly Vector3 _origin;
        private readonly HashSet<long> _filled = new();
        private readonly HashSet<long> _blocks = new();
        private readonly List<SolidRef> _refs = new();
        private readonly List<SolidRef> _inside = new(4);
        private const int Block = 8;   // cells a side of a block rasterised at once (4 m)

        public SolidRaster(TriangleWorld world, GeometryLayers layers, Vector3 origin) { _world = world; _layers = layers; _origin = origin; }

        public bool Blocked(int x, int y, int z)
        {
            int bx = FloorDiv(x, Block), by = FloorDiv(y, Block), bz = FloorDiv(z, Block);
            if (_blocks.Add(Key(bx, by, bz))) Rasterise(bx, by, bz);
            return _filled.Contains(Key(x, y, z));
        }

        private static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);

        private static readonly Vector3[] Probes =
        {
            Vector3.Zero, new(0.249f * Cell * 2, 0, 0), new(-0.249f * Cell * 2, 0, 0), new(0, 0.249f * Cell * 2, 0),
            new(0, -0.249f * Cell * 2, 0), new(0, 0, 0.249f * Cell * 2), new(0, 0, -0.249f * Cell * 2),
        };

        /// <summary>Whether a solid holds the point (the ground: a cell whose middle is under it).</summary>
        private bool Holds(Vector3 p)
        {
            var any = new AcceptAll();
            _inside.Clear();
            _world.Containing(p, _layers, ref any, _inside);
            return _inside.Count > 0;
        }

        private bool ShapeHolds(Vector3 p)
        {
            var any = new AcceptAll();
            foreach (var d in Probes)
            {
                _inside.Clear();
                _world.Containing(p + d, _layers, ref any, _inside);
                if (_inside.Count > 0) return true;
            }
            return false;
        }

        private void Rasterise(int bx, int by, int bz)
        {
            var lo = _origin + new Vector3(bx, by, bz) * (Block * Cell);
            var hi = lo + new Vector3(Block * Cell);
            _refs.Clear();
            var all = new AcceptAll();
            _world.Overlapping(lo, hi, _layers, ref all, _refs);
            foreach (var r in _refs)
            {
                var (centre, size, rot) = _world.BoxOf(r);
                // Terrain (no box): asked like a shape, cell by cell, so a flood out of a doorway does not go on
                // under the house.
                bool terrain = size.X <= 0f || size.Y <= 0f || size.Z <= 0f;
                // A shape thinner than a cell (a ceiling of a footprint) is taken as its box, made a cell thick:
                // asking its triangles at a cell's middle would miss it.
                var shape = MathF.Min(size.X, MathF.Min(size.Y, size.Z)) < Cell ? null : _world.ShapeOf(r);
                var (smin, smax) = _world.BoundsOf(r);
                int x0 = Math.Max(bx * Block, (int)MathF.Floor((smin.X - _origin.X) / Cell) - 1), x1 = Math.Min(bx * Block + Block - 1, (int)MathF.Floor((smax.X - _origin.X) / Cell) + 1);
                int y0 = Math.Max(by * Block, (int)MathF.Floor((smin.Y - _origin.Y) / Cell) - 1), y1 = Math.Min(by * Block + Block - 1, (int)MathF.Floor((smax.Y - _origin.Y) / Cell) + 1);
                int z0 = Math.Max(bz * Block, (int)MathF.Floor((smin.Z - _origin.Z) / Cell) - 1), z1 = Math.Min(bz * Block + Block - 1, (int)MathF.Floor((smax.Z - _origin.Z) / Cell) + 1);
                var inv = Quaternion.Conjugate(rot.LengthSquared() < 1e-6f ? Quaternion.Identity : Quaternion.Normalize(rot));
                // At least a cell's diagonal thick each way: a wall at an angle to the grid is then a line of cells a
                // 6-connected flood cannot step through.
                var half = Vector3.Max(size * 0.5f, new Vector3(Cell * 0.5f * 1.4143f + 1e-4f));
                for (int z = z0; z <= z1; z++)
                    for (int y = y0; y <= y1; y++)
                        for (int x = x0; x <= x1; x++)
                        {
                            long k = Key(x, y, z);
                            if (_filled.Contains(k)) continue;
                            var p = _origin + new Vector3((x + 0.5f) * Cell, (y + 0.5f) * Cell, (z + 0.5f) * Cell);
                            if (!terrain)
                            {
                                var l = Vector3.Transform(p - centre, inv);
                                if (MathF.Abs(l.X) > half.X || MathF.Abs(l.Y) > half.Y || MathF.Abs(l.Z) > half.Z) continue;
                            }
                            // A shape fills less than its box: whether a solid holds the cell's middle or a point a
                            // hair under half a cell from it along an axis, so a slab a quarter metre thick (a
                            // footprint's floor) is in some cell whatever its height.
                            if (terrain ? !Holds(p) : shape != null && !ShapeHolds(p)) continue;
                            _filled.Add(k);
                        }
            }
        }
    }
}

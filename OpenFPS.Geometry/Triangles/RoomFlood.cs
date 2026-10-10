using System.Numerics;

namespace OpenFPS.Common.Geometry;

/// <summary>
/// Rooms measured from the walls (docs/GEOMETRY.md 2.7, 12.6): each room is a seed (its authored or generated box,
/// a hint) and its shape is a flood from the box's middle through the space the solids leave, stopped by walls,
/// floors, ceilings and shut door leaves, and kept within the hint box grown by a margin, so an open doorway does
/// not flood the street. An L, a bay, a porch inside the walls, a room whose box overhangs its walls, all come out
/// as the walls make them.
/// </summary>
/// <remarks>
/// The flood goes in two steps. First every room fills what of its own box it can reach, all rooms at once (a
/// cell to the room that reaches it first, so boxes drawn over each other share by distance); then each grows on
/// from there into its margin, into what no box holds or no other room reached: a room's own box is its own, and
/// the margin only picks up what the walls add to it. Past the margin, air the walls close in is still the room's (a
/// part of a footprint no box holds); air that reaches out past every room's reach is the outdoors, and the room is
/// open there (an open door to the street). Air meeting another room's air is an opening between the two.
/// Rooms whose reaches overlap flood together in one dense block of cells, so a cell is an array index, not a key.
/// </remarks>
public static class RoomFlood
{
    /// <summary>The cell the flood moves in, metres: half the acoustic grid's half-metre voxel, so a 1.1 m hall
    /// between walls a quarter metre thick keeps four cells of air across.</summary>
    public const float Cell = 0.25f;

    /// <summary>How far past its hint box a room may reach, metres.</summary>
    public const float DefaultMargin = 1.0f;

    /// <summary>How far past a room's margin air the walls close in is looked for, metres: an unroomed wing of a
    /// house, a bay deeper than the margin.</summary>
    public const float EnclosedReach = 4f;

    /// <summary>How far up nothing may be for air to be under the open sky, metres.</summary>
    public const float SkyReach = 500f;

    /// <summary>A room to flood: its id, and its hint box.</summary>
    public readonly record struct Seed(int Id, Vector3 Centre, Vector3 Size, Quaternion Rotation);

    /// <summary>
    /// The floods of groups of rooms kept from one flood to the next: a group whose seeds and whose geometry (every
    /// piece of the world its block touches, where it stands) are as they were is not flooded again. A flood keeps
    /// only the groups it used.
    /// </summary>
    public sealed class Cache
    {
        internal Dictionary<ulong, Room[]> Groups = new();
        internal Dictionary<ulong, Room[]> Used = new();
        public int Hits { get; internal set; }
        public int Misses { get; internal set; }
    }

    /// <summary>Cells along x from (X, Y, Z), in cells from the flood's origin.</summary>
    public readonly record struct Run(int X, int Y, int Z, int Length);

    /// <summary>One room's flood: its air, the wall cells round it, whether it is open past its margin, the rooms
    /// its air meets, and whether it could not start (its middle in a solid).</summary>
    public sealed class Room
    {
        public required int Id { get; init; }
        public required Vector3 Origin { get; init; }
        /// <summary>The room's air: the cells the flood reached.</summary>
        public List<Run> AirRuns { get; } = new();
        /// <summary>The cells of its walls, floor and ceiling next to its air (each to the first room that reaches
        /// it): a point on a wall's face is then in the room.</summary>
        public List<Run> EdgeRuns { get; } = new();
        public int AirCount { get; internal set; }
        public int EdgeCount { get; internal set; }
        /// <summary>Its air meets the outdoors past its margin: air no room holds that reaches out past every room's reach.</summary>
        public bool Open { get; internal set; }
        /// <summary>Where it met that air: the first few cells past its margin (to trace a way out).</summary>
        public List<Vector3> OpenAt { get; } = new();
        public bool Unseeded { get; internal set; }
        /// <summary>How many of its air's cells lie past its margin, in air the walls close in that no box holds.</summary>
        public int BeyondCount { get; internal set; }
        /// <summary>The rooms whose air its air meets: an opening between them.</summary>
        public HashSet<int> Meets { get; } = new();
        public float Volume => AirCount * Cell * Cell * Cell;
        /// <summary>What lines its air, measured when the flood was asked to (<see cref="Flood"/>'s measure): null otherwise.</summary>
        public Lining? Lining { get; internal set; }
        public IEnumerable<Vector3> Cells => Centres(AirRuns);
        public IEnumerable<Vector3> Edge => Centres(EdgeRuns);

        private IEnumerable<Vector3> Centres(List<Run> runs)
        {
            foreach (var r in runs)
                for (int i = 0; i < r.Length; i++) yield return CentreOf(r.X + i, r.Y, r.Z, Origin);
        }
    }

    /// <summary>
    /// What lines a room's air (docs/GEOMETRY.md 2.7 item 4), per face of its box's frame in RoomAcoustics' order
    /// (floor, ceiling, north +Z, south, east +X, west): from a ray out of each air cell that has a solid next to it, the
    /// surface it meets, its material, and its true area (a cell's face over the surface normal's L1 length, so a wall at
    /// 45 degrees to the grid is not counted as its staircase).
    /// </summary>
    public sealed class Lining
    {
        /// <summary>Area of each material on each face, m² (material "" where the ray met nothing it could name).</summary>
        public Dictionary<string, float>[] Surfaces { get; } = { new(), new(), new(), new(), new(), new() };
        /// <summary>Area of each face that is air under the open sky, m²: what reaches it leaves.</summary>
        public float[] Open { get; } = new float[6];
        /// <summary>Area of each face that is another room's air, m²: an opening with no door.</summary>
        public float[] Meets { get; } = new float[6];
        /// <summary>The air's volume, m³: its cells, and the air between the last cells and the surfaces the rays met.</summary>
        public float Volume { get; internal set; }
        /// <summary>The air's bounds in the world.</summary>
        public Vector3 Min { get; internal set; }
        public Vector3 Max { get; internal set; }

        public float Lined(int face) { float a = 0f; foreach (var v in Surfaces[face].Values) a += v; return a; }

        /// <summary>The face a direction from the air towards a surface is, in the room's own frame.</summary>
        public static int FaceOf(Vector3 local)
        {
            var a = Vector3.Abs(local);
            if (a.Y >= a.X && a.Y >= a.Z) return local.Y < 0f ? 0 : 1;
            if (a.Z >= a.X) return local.Z > 0f ? 2 : 3;
            return local.X > 0f ? 4 : 5;
        }
    }

    /// <summary>Floods every seed through <paramref name="world"/>'s solids of <paramref name="layers"/>. Cells
    /// are on a grid through <paramref name="origin"/> (the acoustic map's corner, so a voxel is a whole number
    /// of cells). Rooms that cannot meet flood apart, on up to <paramref name="threads"/> threads; each group that
    /// takes longer than <paramref name="slowMs"/> is told to <paramref name="report"/>. With <paramref name="cache"/>,
    /// groups that have not changed since the last flood with it are taken from it. With <paramref name="measure"/>,
    /// each room's <see cref="Room.Lining"/> is measured too.</summary>
    public static Dictionary<int, Room> Flood(TriangleWorld world, IReadOnlyList<Seed> seeds, Vector3 origin,
                                              float margin = DefaultMargin, GeometryLayers layers = GeometryLayers.Acoustics,
                                              int threads = 1, Action<string>? report = null, double slowMs = 250, Cache? cache = null,
                                              bool measure = false)
    {
        var rooms = new Dictionary<int, Room>();
        if (seeds.Count == 0) return rooms;
        var hints = new Hint[seeds.Count];
        for (int si = 0; si < seeds.Count; si++)
        {
            hints[si] = new Hint(seeds[si], margin, origin);
            rooms[seeds[si].Id] = new Room { Id = seeds[si].Id, Origin = origin };
        }
        var clusters = Clusters(hints);
        clusters.Sort((a, b) => b.Cells.CompareTo(a.Cells));
        if (cache != null) { cache.Used = new Dictionary<ulong, Room[]>(); cache.Hits = cache.Misses = 0; }
        var taken = new List<Room>();   // put in when every group is done: the others read the rooms meanwhile
        void One(Cluster c)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            ulong key = 0;
            if (cache != null)
            {
                key = Signature(world, c, seeds, origin, margin, layers, measure);
                Room[]? kept;
                lock (cache) cache.Groups.TryGetValue(key, out kept);
                if (kept != null)
                {
                    lock (cache) { cache.Used[key] = kept; cache.Hits++; }
                    lock (taken) taken.AddRange(kept);
                    return;
                }
            }
            FloodCluster(world, layers, origin, c, seeds, hints, rooms, measure);
            if (cache != null)
            {
                var made = c.Seeds.Select(i => rooms[seeds[i].Id]).ToArray();
                lock (cache) { cache.Used[key] = made; cache.Misses++; }
            }
            double ms = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            if (report != null && ms > slowMs)
            {
                int air = c.Seeds.Sum(i => rooms[seeds[i].Id].AirCount);
                lock (report) report($"{c.Seeds.Count} room(s) from {seeds[c.Seeds[0]].Id} in a block {(c.Hi.X - c.Lo.X + 1) * Cell:F0} by "
                                     + $"{(c.Hi.Y - c.Lo.Y + 1) * Cell:F0} by {(c.Hi.Z - c.Lo.Z + 1) * Cell:F0} m ({c.Cells:N0} cells, air {air:N0}): {ms:F0} ms");
            }
        }
        if (threads <= 1) foreach (var c in clusters) One(c);
        else Parallel.ForEach(clusters, new ParallelOptions { MaxDegreeOfParallelism = threads }, One);
        foreach (var r in taken) rooms[r.Id] = r;
        if (cache != null) cache.Groups = cache.Used;
        return rooms;
    }

    /// <summary>Whether the cell holding <paramref name="p"/> is solid, as a flood would find it.</summary>
    public static bool Solid(TriangleWorld world, Vector3 p, Vector3 origin, GeometryLayers layers = GeometryLayers.Acoustics)
    {
        var (x, y, z) = CellOf(p, origin);
        var block = new Block(x, y, z, 1, 1, 1);
        new Raster(world, layers, origin, block).Blocked(0);
        return block.State[0] == Solidity.Solid;
    }

    /// <summary>The cell holding a point, in cells from the origin.</summary>
    public static (int X, int Y, int Z) CellOf(Vector3 p, Vector3 origin)
    {
        var q = (p - origin) / Cell;
        return ((int)MathF.Floor(q.X), (int)MathF.Floor(q.Y), (int)MathF.Floor(q.Z));
    }

    public static Vector3 CentreOf(int x, int y, int z, Vector3 origin)
        => origin + new Vector3((x + 0.5f) * Cell, (y + 0.5f) * Cell, (z + 0.5f) * Cell);

    // ═══ Seeds and clusters ═════════════════════════════════════════════════════════════════════

    private readonly struct Hint
    {
        public readonly Matrix4x4 Inv;
        public readonly Vector3 Half, Grown;
        /// <summary>The cells the grown box's bounds cover, one more each way (where an escape is looked at).</summary>
        public readonly (int X, int Y, int Z) Lo, Hi;

        public Hint(Seed s, float margin, Vector3 origin)
        {
            var rot = s.Rotation.LengthSquared() < 1e-6f ? Quaternion.Identity : Quaternion.Normalize(s.Rotation);
            Matrix4x4.Invert(Matrix4x4.CreateFromQuaternion(rot) * Matrix4x4.CreateTranslation(s.Centre), out Inv);
            Half = s.Size * 0.5f;
            Grown = Half + new Vector3(margin);
            var ext = Vector3.Abs(Vector3.Transform(new Vector3(Grown.X, 0, 0), rot)) + Vector3.Abs(Vector3.Transform(new Vector3(0, Grown.Y, 0), rot))
                      + Vector3.Abs(Vector3.Transform(new Vector3(0, 0, Grown.Z), rot));
            var lo = CellOf(s.Centre - ext, origin); var hi = CellOf(s.Centre + ext, origin);
            Lo = (lo.X - 1, lo.Y - 1, lo.Z - 1);
            Hi = (hi.X + 1, hi.Y + 1, hi.Z + 1);
        }

        public bool InBox(Vector3 p, out bool inGrown)
        {
            var l = Vector3.Transform(p, Inv);
            l = Vector3.Abs(l);
            inGrown = l.X <= Grown.X && l.Y <= Grown.Y && l.Z <= Grown.Z;
            return l.X <= Half.X && l.Y <= Half.Y && l.Z <= Half.Z;
        }
    }

    private sealed class Cluster
    {
        public readonly List<int> Seeds = new();
        public (int X, int Y, int Z) Lo = (int.MaxValue, int.MaxValue, int.MaxValue), Hi = (int.MinValue, int.MinValue, int.MinValue);
        public long Cells => (long)(Hi.X - Lo.X + 1) * (Hi.Y - Lo.Y + 1) * (Hi.Z - Lo.Z + 1);
    }

    /// <summary>The seeds whose reaches (one cell more) overlap, together: a room can only meet a room of its own.</summary>
    private static List<Cluster> Clusters(Hint[] hints)
    {
        int n = hints.Length;
        var parent = new int[n];
        for (int i = 0; i < n; i++) parent[i] = i;
        int Find(int i) { while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; } return i; }
        var order = Enumerable.Range(0, n).OrderBy(i => hints[i].Lo.X).ToArray();
        var active = new List<int>();
        foreach (int i in order)
        {
            var h = hints[i];
            active.RemoveAll(j => hints[j].Hi.X < h.Lo.X);
            foreach (int j in active)
            {
                ref readonly var o = ref hints[j];
                if (o.Hi.Y < h.Lo.Y || o.Lo.Y > h.Hi.Y || o.Hi.Z < h.Lo.Z || o.Lo.Z > h.Hi.Z) continue;
                int a = Find(i), b = Find(j);
                if (a != b) parent[a] = b;
            }
            active.Add(i);
        }
        var byRoot = new Dictionary<int, Cluster>();
        for (int i = 0; i < n; i++)
        {
            int r = Find(i);
            if (!byRoot.TryGetValue(r, out var c)) byRoot[r] = c = new Cluster();
            c.Seeds.Add(i);
            var h = hints[i];
            c.Lo = (Math.Min(c.Lo.X, h.Lo.X), Math.Min(c.Lo.Y, h.Lo.Y), Math.Min(c.Lo.Z, h.Lo.Z));
            c.Hi = (Math.Max(c.Hi.X, h.Hi.X), Math.Max(c.Hi.Y, h.Hi.Y), Math.Max(c.Hi.Z, h.Hi.Z));
        }
        foreach (var c in byRoot.Values) c.Seeds.Sort();
        return byRoot.Values.ToList();
    }

    /// <summary>What a group's flood depends on: its seeds, the margin, and every piece its block touches where it is.</summary>
    private static ulong Signature(TriangleWorld world, Cluster c, IReadOnlyList<Seed> seeds, Vector3 origin, float margin, GeometryLayers layers, bool measure)
    {
        ulong h = 14695981039346656037UL;
        void Mix(ulong v) { h ^= v; h *= 1099511628211UL; h ^= h >> 29; }
        void MixF(float v) => Mix((ulong)BitConverter.SingleToUInt32Bits(v));
        void MixV(Vector3 v) { MixF(v.X); MixF(v.Y); MixF(v.Z); }
        MixF(margin); Mix((ulong)layers); MixV(origin); Mix(measure ? 1UL : 0UL);
        foreach (int i in c.Seeds)
        {
            var s = seeds[i];
            Mix((ulong)(uint)s.Id); MixV(s.Centre); MixV(s.Size);
            MixF(s.Rotation.X); MixF(s.Rotation.Y); MixF(s.Rotation.Z); MixF(s.Rotation.W);
        }
        int pad = (int)MathF.Ceiling(EnclosedReach / Cell);
        var lo = origin + new Vector3(c.Lo.X - pad, c.Lo.Y - pad, c.Lo.Z - pad) * Cell;
        var hi = origin + new Vector3(c.Hi.X + pad + 1, c.Hi.Y + pad + 1, c.Hi.Z + pad + 1) * Cell;
        foreach (ref readonly var inst in world.Instances)
        {
            if (inst.Max.X < lo.X || inst.Max.Y < lo.Y || inst.Max.Z < lo.Z || inst.Min.X > hi.X || inst.Min.Y > hi.Y || inst.Min.Z > hi.Z) continue;
            Mix(inst.Piece.Signature); Mix((ulong)(uint)inst.Owner); MixV(inst.Position);
            MixF(inst.Rotation.X); MixF(inst.Rotation.Y); MixF(inst.Rotation.Z); MixF(inst.Rotation.W);
        }
        return h;
    }

    // ═══ The flood ══════════════════════════════════════════════════════════════════════════════

    private static void FloodCluster(TriangleWorld world, GeometryLayers layers, Vector3 origin, Cluster cluster,
                                     IReadOnlyList<Seed> seeds, Hint[] hints, Dictionary<int, Room> rooms, bool measure)
    {
        int pad = (int)MathF.Ceiling(EnclosedReach / Cell);
        var (lx, ly, lz) = (cluster.Lo.X - pad, cluster.Lo.Y - pad, cluster.Lo.Z - pad);
        int nx = cluster.Hi.X + pad - lx + 1, ny = cluster.Hi.Y + pad - ly + 1, nz = cluster.Hi.Z + pad - lz + 1;
        if ((long)nx * ny * nz > int.MaxValue / 2)
            throw new InvalidOperationException($"rooms reaching {nx * Cell:F0} by {ny * Cell:F0} by {nz * Cell:F0} m flood as one block: too big");
        var block = new Block(lx, ly, lz, nx, ny, nz);
        var raster = new Raster(world, layers, origin, block);
        int n = nx * ny * nz, sx = 1, sy = nx, sz = nx * ny;
        // Which seed (its index in the cluster, plus one) holds each cell: air, or a wall cell its air reached.
        var claim = new int[n];
        var local = cluster.Seeds;
        Vector3 CentreAt(int i) { int x = i % nx, y = i / nx % ny, z = i / sz; return CentreOf(lx + x, ly + y, lz + z, origin); }
        int Index(int x, int y, int z) => (x - lx) + nx * ((y - ly) + ny * (z - lz));
        bool Inside(int x, int y, int z) => x >= lx && y >= ly && z >= lz && x < lx + nx && y < ly + ny && z < lz + nz;

        var queue = new List<int>();
        for (int k = 0; k < local.Count; k++)
        {
            var s = seeds[local[k]];
            var h = hints[local[k]];
            var (x, y, z) = CellOf(s.Centre, origin);
            // A middle in a solid (a column in the room, a narrow room's wall): the nearest free cell of the box
            // within a metre.
            bool Free(int cx, int cy, int cz)
                => Inside(cx, cy, cz) && claim[Index(cx, cy, cz)] == 0 && !raster.Blocked(Index(cx, cy, cz))
                   && h.InBox(CentreOf(cx, cy, cz, origin), out _);
            if (!Free(x, y, z))
            {
                bool found = false;
                int reach = (int)MathF.Ceiling(1f / Cell);
                for (int r = 1; r <= reach && !found; r++)
                    for (int dz = -r; dz <= r && !found; dz++)
                        for (int dy = -r; dy <= r && !found; dy++)
                            for (int dx = -r; dx <= r && !found; dx++)
                            {
                                if (Math.Max(Math.Abs(dx), Math.Max(Math.Abs(dy), Math.Abs(dz))) != r) continue;
                                if (!Free(x + dx, y + dy, z + dz)) continue;
                                (x, y, z) = (x + dx, y + dy, z + dz);
                                found = true;
                            }
                if (!found) { rooms[s.Id].Unseeded = true; continue; }
            }
            int i0 = Index(x, y, z);
            claim[i0] = k + 1;
            queue.Add(i0);
        }

        var deferred = new List<(int Cell, int Seed)>();
        var walls = new List<(int Cell, int Seed)>();
        var escapes = new List<(int Cell, int Seed)>();
        var meets = new HashSet<long>();
        void Meet(int a, int b) { if (a != b) meets.Add(a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a); }

        // Step 1: each room within its own box. Step 2: on from there into its margin.
        for (int step = 1; step <= 2; step++)
        {
            if (step == 2)
            {
                queue.Clear();
                foreach (var (c, k) in deferred)
                {
                    if (claim[c] != 0) { Meet(claim[c] - 1, k); continue; }
                    claim[c] = k + 1;
                    queue.Add(c);
                }
            }
            for (int head = 0; head < queue.Count; head++)
            {
                int c = queue[head];
                int k = claim[c] - 1;
                var h = hints[local[k]];
                int cx = c % nx, cy = c / nx % ny, cz = c / sz;
                for (int d = 0; d < 6; d++)
                {
                    int ax = cx, ay = cy, az = cz, step6;
                    switch (d)
                    {
                        case 0: ax++; step6 = sx; break;
                        case 1: ax--; step6 = -sx; break;
                        case 2: ay++; step6 = sy; break;
                        case 3: ay--; step6 = -sy; break;
                        case 4: az++; step6 = sz; break;
                        default: az--; step6 = -sz; break;
                    }
                    if ((uint)ax >= (uint)nx || (uint)ay >= (uint)ny || (uint)az >= (uint)nz) continue;
                    int m = c + step6;
                    if (raster.Blocked(m)) { walls.Add((m, k)); continue; }
                    if (claim[m] != 0) { Meet(claim[m] - 1, k); continue; }
                    bool inBox = h.InBox(CentreAt(m), out bool inGrown);
                    if (!inGrown) { escapes.Add((m, k)); continue; }
                    if (step == 1 && !inBox) { deferred.Add((m, k)); continue; }
                    claim[m] = k + 1;
                    queue.Add(m);
                }
            }
        }

        // Step 3: the air past a room's margin. Air open to the sky where it reaches the block's sides is the
        // outdoors, and the room is open; air the walls close in (a part of a footprint no box holds, a bay deeper
        // than the margin) is the room's, as the walls make it, as far as the block reaches
        // (EnclosedReach past the rooms' reach).
        var tag = escapes.Count > 0 ? new Place[n] : Array.Empty<Place>();
        var around = new List<int>();
        bool Step(int c, int d, out int m)
        {
            int cx = c % nx, cy = c / nx % ny, cz = c / sz;
            switch (d)
            {
                case 0: m = c + sx; return cx + 1 < nx;
                case 1: m = c - sx; return cx > 0;
                case 2: m = c + sy; return cy + 1 < ny;
                case 3: m = c - sy; return cy > 0;
                case 4: m = c + sz; return cz + 1 < nz;
                default: m = c - sz; return cz > 0;
            }
        }
        bool Sky(int c)
        {
            var all = new AcceptAll();
            return !world.Any(CentreAt(c), Vector3.UnitY, SkyReach, layers, RayFaces.Both, ref all);
        }
        Place Classify(int c0)
        {
            around.Clear();
            around.Add(c0);
            tag[c0] = Place.Looking;
            bool outdoors = false;
            for (int head = 0; head < around.Count && !outdoors; head++)
            {
                int c = around[head];
                int cx = c % nx, cy = c / nx % ny, cz = c / sz;
                if ((cx == 0 || cy == 0 || cz == 0 || cx == nx - 1 || cy == ny - 1 || cz == nz - 1) && Sky(c)) { outdoors = true; break; }
                for (int d = 0; d < 6; d++)
                {
                    if (!Step(c, d, out int m)) continue;
                    if (tag[m] == Place.Outdoors) { outdoors = true; break; }
                    if (tag[m] != Place.Unknown || claim[m] != 0 || raster.Blocked(m)) continue;
                    tag[m] = Place.Looking;
                    around.Add(m);
                }
            }
            var found = outdoors ? Place.Outdoors : Place.Enclosed;
            foreach (int c in around) tag[c] = found;
            return found;
        }
        queue.Clear();
        foreach (var (c, k) in escapes)
        {
            if (claim[c] != 0) { Meet(claim[c] - 1, k); continue; }
            if ((tag[c] == Place.Unknown ? Classify(c) : tag[c]) == Place.Enclosed)
            {
                claim[c] = k + 1;
                queue.Add(c);
                continue;
            }
            var room = rooms[seeds[local[k]].Id];
            room.Open = true;
            if (room.OpenAt.Count < 8) room.OpenAt.Add(CentreAt(c));
        }
        var beyond = new int[local.Count];
        for (int head = 0; head < queue.Count; head++)
        {
            int c = queue[head];
            int k = claim[c] - 1;
            beyond[k]++;
            for (int d = 0; d < 6; d++)
            {
                if (!Step(c, d, out int m)) continue;
                if (raster.Blocked(m)) { walls.Add((m, k)); continue; }
                if (claim[m] != 0) { Meet(claim[m] - 1, k); continue; }
                if (tag[m] != Place.Enclosed) continue;
                claim[m] = k + 1;
                queue.Add(m);
            }
        }
        for (int k = 0; k < local.Count; k++) rooms[seeds[local[k]].Id].BeyondCount = beyond[k];
        // Each room's air takes the wall cells next to it (the first room to reach one), so it reaches its walls.
        foreach (var (c, k) in walls)
            if (claim[c] == 0) claim[c] = k + 1;
        foreach (long pair in meets)
        {
            int a = local[(int)(pair >> 32)], b = local[(int)(pair & 0xFFFFFFFF)];
            rooms[seeds[a].Id].Meets.Add(seeds[b].Id);
            rooms[seeds[b].Id].Meets.Add(seeds[a].Id);
        }

        // The cells as runs along x, room by room.
        var counts = new int[local.Count * 2];
        for (int z = 0; z < nz; z++)
            for (int y = 0; y < ny; y++)
            {
                int row = nx * (y + ny * z);
                int x = 0;
                while (x < nx)
                {
                    int c = claim[row + x];
                    if (c == 0) { x++; continue; }
                    bool edge = block.State[row + x] == Solidity.Solid;
                    int x0 = x;
                    while (x < nx && claim[row + x] == c && (block.State[row + x] == Solidity.Solid) == edge) x++;
                    var room = rooms[seeds[local[c - 1]].Id];
                    (edge ? room.EdgeRuns : room.AirRuns).Add(new Run(lx + x0, ly + y, lz + z, x - x0));
                    counts[(c - 1) * 2 + (edge ? 1 : 0)] += x - x0;
                }
            }
        for (int k = 0; k < local.Count; k++)
        {
            var room = rooms[seeds[local[k]].Id];
            room.AirCount = counts[k * 2];
            room.EdgeCount = counts[k * 2 + 1];
        }
        if (measure) MeasureLinings(world, layers, origin, block, raster, claim, local, hints, seeds, rooms);
    }

    /// <summary>
    /// Where a block's air is under the open sky (nothing above it in its column, and a ray up from the block's top meets
    /// nothing), for a group with a room open past its margin: the margin the flood took past an open front, or over a
    /// roof, is the outdoors, not the room. Null when no room of the group is open.
    /// </summary>
    private static bool[]? UnderTheSky(TriangleWorld world, GeometryLayers layers, Vector3 origin, Block block, Raster raster,
                                       int[] claim, List<int> local, Dictionary<int, Room> rooms, IReadOnlyList<Seed> seeds)
    {
        bool any = false;
        foreach (int i in local) any |= rooms[seeds[i].Id].Open;
        if (!any) return null;
        int nx = block.Nx, ny = block.Ny, nz = block.Nz;
        var outside = new bool[claim.Length];
        var all = new AcceptAll();
        for (int z = 0; z < nz; z++)
            for (int x = 0; x < nx; x++)
            {
                // Only columns some open room's air is in.
                bool wanted = false;
                for (int y = 0; y < ny && !wanted; y++)
                {
                    int c = x + nx * (y + ny * z);
                    wanted = claim[c] != 0 && block.State[c] != Solidity.Solid && rooms[seeds[local[claim[c] - 1]].Id].Open;
                }
                if (!wanted) continue;
                var top = CentreOf(block.Lx + x, block.Ly + ny - 1, block.Lz + z, origin);
                if (world.Any(top, Vector3.UnitY, SkyReach, layers, RayFaces.Both, ref all)) continue;
                for (int y = ny - 1; y >= 0; y--)
                {
                    int c = x + nx * (y + ny * z);
                    if (raster.Blocked(c)) break;
                    outside[c] = true;
                }
            }
        return outside;
    }

    /// <summary>The faces of air against a solid that one ray answers for, cells a side: the faces of a room in one grid
    /// direction within a cube this size are one sample, cast from the face nearest the cube's middle. Each ray costs
    /// about 2 µs: one for every face was 5 million rays and 13 s on the city (a cube of 4 cells, 1.5 s).</summary>
    private const int SampleCells = 4;

    /// <summary>One sample's faces: how many, and the one nearest the cube's middle.</summary>
    private struct Group
    {
        public int Count, Cell, Dir;
        public float Distance;
    }

    /// <summary>
    /// Each room's <see cref="Lining"/>: every face of its air against something else, a solid (a ray to the surface,
    /// one for the faces of a <see cref="SampleCells"/> cube), the open sky or another room's air. The cells stop short
    /// of a surface by up to a cell (a finish a centimetre thick fills the cell it lies in), so the rays' gaps to the
    /// surfaces are added to the volume, and each face's area is grown by the gaps across it: a 2.7 m wall over a
    /// carpet's cell was 2.5 m of cells.
    /// </summary>
    private static void MeasureLinings(TriangleWorld world, GeometryLayers layers, Vector3 origin, Block block, Raster raster,
                                       int[] claim, List<int> local, Hint[] hints, IReadOnlyList<Seed> seeds, Dictionary<int, Room> rooms)
    {
        int nx = block.Nx, ny = block.Ny, nz = block.Nz, sz = nx * ny;
        int rc = local.Count;
        var linings = new Lining[rc];
        var air = new long[rc];
        var faces = new long[rc * 6];                         // every face of a room's air against something else, by direction
        var lo = new Vector3[rc];
        var hi = new Vector3[rc];
        for (int k = 0; k < rc; k++) { linings[k] = new Lining(); lo[k] = new Vector3(float.MaxValue); hi[k] = new Vector3(float.MinValue); }
        var outside = UnderTheSky(world, layers, origin, block, raster, claim, local, rooms, seeds);
        var groups = new Dictionary<long, Group>();
        var openings = new List<(int Room, int Dir, int Face, bool Meets)>();
        const float face = Cell * Cell, reach = 1.5f * Cell + 1e-3f;
        var dirs = new[] { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ };
        for (int z = 0; z < nz; z++)
            for (int y = 0; y < ny; y++)
                for (int x = 0; x < nx; x++)
                {
                    int c = x + nx * (y + ny * z);
                    int own = claim[c];
                    if (own == 0 || block.State[c] == Solidity.Solid || (outside != null && outside[c])) continue;
                    int k = own - 1;
                    var at = CentreOf(block.Lx + x, block.Ly + y, block.Lz + z, origin);
                    lo[k] = Vector3.Min(lo[k], at); hi[k] = Vector3.Max(hi[k], at);
                    air[k]++;
                    for (int d = 0; d < 6; d++)
                    {
                        int m;
                        switch (d)
                        {
                            case 0: if (x + 1 >= nx) continue; m = c + 1; break;
                            case 1: if (x == 0) continue; m = c - 1; break;
                            case 2: if (y + 1 >= ny) continue; m = c + nx; break;
                            case 3: if (y == 0) continue; m = c - nx; break;
                            case 4: if (z + 1 >= nz) continue; m = c + sz; break;
                            default: if (z == 0) continue; m = c - sz; break;
                        }
                        bool sky = outside != null && outside[m];
                        if (claim[m] == own && block.State[m] != Solidity.Solid && !sky) continue;
                        faces[k * 6 + d]++;
                        if (!raster.Blocked(m))
                        {
                            // Air no room holds (past every reach), or air under the open sky: what reaches it leaves.
                            openings.Add((k, d, Lining.FaceOf(Vector3.TransformNormal(dirs[d], hints[local[k]].Inv)), claim[m] != 0 && !sky));
                            continue;
                        }
                        int gx = x / SampleCells, gy = y / SampleCells, gz = z / SampleCells;
                        long key = ((((long)k * 6 + d) * 16384 + gz) * 16384 + gy) * 16384 + gx;
                        float dx = x - (gx + 0.5f) * SampleCells + 0.5f, dy = y - (gy + 0.5f) * SampleCells + 0.5f, dz = z - (gz + 0.5f) * SampleCells + 0.5f;
                        float distance = dx * dx + dy * dy + dz * dz;
                        groups.TryGetValue(key, out var g);
                        if (g.Count == 0 || distance < g.Distance) { g.Cell = c; g.Dir = d; g.Distance = distance; }
                        g.Count++;
                        groups[key] = g;
                    }
                }

        // One ray a group: what it met, and how far short of it the cells stop.
        var all = new AcceptAll();
        var met = new List<(int Room, int Dir, int Face, string Material, float Area)>(groups.Count);
        var gap = new double[rc * 6];
        var cast = new long[rc * 6];
        foreach (var (key, g) in groups)
        {
            int k = (int)(key / (6L * 16384 * 16384 * 16384));
            var inv = hints[local[k]].Inv;
            int x = g.Cell % nx, y = g.Cell / nx % ny, z = g.Cell / sz;
            var at = CentreOf(block.Lx + x, block.Ly + y, block.Lz + z, origin);
            var dir = dirs[g.Dir];
            if (world.Closest(at, dir, reach, layers, RayFaces.Both, ref all, out var hit))
            {
                var n = hit.Normal;
                float l1 = MathF.Abs(n.X) + MathF.Abs(n.Y) + MathF.Abs(n.Z);
                met.Add((k, g.Dir, Lining.FaceOf(Vector3.TransformNormal(hit.Front ? -n : dir, inv)), world.SurfaceOf(hit).Material ?? "",
                         g.Count * face / MathF.Max(1f, l1)));
                gap[k * 6 + g.Dir] += g.Count * Math.Clamp(hit.T - Cell * 0.5f, -Cell * 0.5f, Cell);
                cast[k * 6 + g.Dir] += g.Count;
            }
            else met.Add((k, g.Dir, Lining.FaceOf(Vector3.TransformNormal(dir, inv)), "", g.Count * face));
        }

        // Each axis's extent, as cells (the air over its faces across that axis) and as far as the surfaces; a face
        // across one axis is grown by how much further the other two reach.
        var grow = new float[rc * 3];
        var volume = new double[rc];
        for (int k = 0; k < rc; k++)
        {
            double cells = air[k] * (double)Cell * Cell * Cell;
            double volumeGap = 0;
            Span<double> stretch = stackalloc double[3];
            for (int a = 0; a < 3; a++)
            {
                double across = 0.5 * (faces[k * 6 + 2 * a] + faces[k * 6 + 2 * a + 1]) * face;
                double extent = across > 0 ? cells / across : 0;
                double gaps = 0;
                for (int side = 0; side < 2; side++)
                {
                    int i = k * 6 + 2 * a + side;
                    gaps += gap[i] / Math.Max(1, faces[i]);   // the mean gap over the side, open parts of it none
                    volumeGap += gap[i] * face;
                }
                stretch[a] = extent > 0 ? Math.Max(0.5, (extent + gaps) / extent) : 1.0;
            }
            for (int a = 0; a < 3; a++) grow[k * 3 + a] = (float)(stretch[(a + 1) % 3] * stretch[(a + 2) % 3]);
            volume[k] = cells + volumeGap;
        }
        foreach (var (k, d, f, material, area) in met)
        {
            var surfaces = linings[k].Surfaces[f];
            surfaces[material] = surfaces.GetValueOrDefault(material) + area * grow[k * 3 + d / 2];
        }
        foreach (var (k, d, f, meets) in openings)
        {
            if (meets) linings[k].Meets[f] += face * grow[k * 3 + d / 2];
            else linings[k].Open[f] += face * grow[k * 3 + d / 2];
        }
        for (int k = 0; k < rc; k++)
        {
            var room = rooms[seeds[local[k]].Id];
            // No air under a roof (a walled garden): an outdoor place, not a room to measure.
            if (room.AirCount == 0 || air[k] == 0) continue;
            var lining = linings[k];
            lining.Volume = (float)volume[k];
            lining.Min = lo[k] - new Vector3(Cell * 0.5f);
            lining.Max = hi[k] + new Vector3(Cell * 0.5f);
            room.Lining = lining;
        }
    }

    // ═══ Which cells the solids fill ════════════════════════════════════════════════════════════

    private enum Solidity : byte { Unknown, Free, Solid }

    /// <summary>What the air past a margin is: not looked at yet, being looked at, the outdoors, or closed in.</summary>
    private enum Place : byte { Unknown, Looking, Outdoors, Enclosed }

    /// <summary>A dense block of cells, x fastest; what fills each worked out a page at a time as the flood asks.</summary>
    private sealed class Block
    {
        public readonly int Lx, Ly, Lz, Nx, Ny, Nz;
        public readonly Solidity[] State;
        public Block(int lx, int ly, int lz, int nx, int ny, int nz)
        {
            (Lx, Ly, Lz, Nx, Ny, Nz) = (lx, ly, lz, nx, ny, nz);
            State = new Solidity[nx * ny * nz];
        }
    }

    /// <summary>
    /// Fills a block's cells from the solids, a page of it at a time. A box is solid where a cell's middle is in it,
    /// a box thinner than a cell along one of its axes taken as thick as a 6-connected flood needs to be stopped by
    /// it whatever its angle to the grid (a digital plane's least thickness: a cell times the largest component of
    /// the axis); a shape where its convex pieces, each face put out by half that thickness, hold the cell's middle;
    /// the ground where it holds the cell's middle.
    /// </summary>
    private sealed class Raster
    {
        private const int Page = 8;   // cells a side of a page rasterised at once (2 m)
        private readonly TriangleWorld _world;
        private readonly GeometryLayers _layers;
        private readonly Block _b;
        /// <summary>The block's corner: what planes and bounds are worked from (near, so a far map keeps its precision).</summary>
        private readonly Vector3 _base;
        private readonly int _px, _py;
        private readonly bool[] _done;
        private readonly List<SolidRef> _refs = new();
        /// <summary>Each shape's convex pieces once asked, from the block's corner (null: none to ask, so its box).</summary>
        private readonly Dictionary<SolidRef, Piece[]?> _pieces = new();
        private Vector4[] _planes = new Vector4[64];
        private Vector3[] _corners = new Vector3[192];

        private readonly record struct Piece(Vector4[] Planes, Vector3 Lo, Vector3 Hi);

        public Raster(TriangleWorld world, GeometryLayers layers, Vector3 origin, Block block)
        {
            _world = world; _layers = layers; _b = block;
            _base = origin + new Vector3(block.Lx, block.Ly, block.Lz) * Cell;
            _px = (block.Nx + Page - 1) / Page; _py = (block.Ny + Page - 1) / Page;
            _done = new bool[_px * _py * ((block.Nz + Page - 1) / Page)];
        }

        public bool Blocked(int i)
        {
            var s = _b.State[i];
            if (s == Solidity.Unknown)
            {
                int x = i % _b.Nx, y = i / _b.Nx % _b.Ny, z = i / (_b.Nx * _b.Ny);
                int p = x / Page + _px * (y / Page + _py * (z / Page));
                if (!_done[p]) { _done[p] = true; Rasterise(x / Page * Page, y / Page * Page, z / Page * Page); }
                s = _b.State[i];
            }
            return s == Solidity.Solid;
        }

        private void Rasterise(int px, int py, int pz)
        {
            int ex = Math.Min(px + Page, _b.Nx), ey = Math.Min(py + Page, _b.Ny), ez = Math.Min(pz + Page, _b.Nz);
            for (int z = pz; z < ez; z++)
                for (int y = py; y < ey; y++)
                    for (int x = px; x < ex; x++)
                        _b.State[x + _b.Nx * (y + _b.Ny * z)] = Solidity.Free;
            var lo = _base + new Vector3(px - 1, py - 1, pz - 1) * Cell;
            var hi = _base + new Vector3(ex + 1, ey + 1, ez + 1) * Cell;
            _refs.Clear();
            var all = new AcceptAll();
            _world.Overlapping(lo, hi, _layers, ref all, _refs);
            var page = (Lo: (X: px, Y: py, Z: pz), Hi: (X: ex - 1, Y: ey - 1, Z: ez - 1));
            foreach (var r in _refs)
            {
                var (centre, size, rot) = _world.BoxOf(r);
                bool terrain = size.X <= 0f || size.Y <= 0f || size.Z <= 0f;
                if (terrain || _world.ShapeOf(r) != null)
                {
                    if (!_pieces.TryGetValue(r, out var pieces)) _pieces[r] = pieces = PiecesOf(r, thicken: !terrain);
                    // A shape with no planes to ask is taken as its box.
                    if (pieces != null) { foreach (var piece in pieces) Fill(piece, page); continue; }
                    if (terrain) continue;
                }
                Box(r, centre - _base, size, rot, page);
            }
        }

        /// <summary>The cells of the page whose middles lie in [lo, hi] (from the block's corner).</summary>
        private static bool Range(Vector3 lo, Vector3 hi, ((int X, int Y, int Z) Lo, (int X, int Y, int Z) Hi) page,
                                  out int x0, out int x1, out int y0, out int y1, out int z0, out int z1)
        {
            x0 = Math.Max(page.Lo.X, (int)MathF.Ceiling(lo.X / Cell - 0.5f)); x1 = Math.Min(page.Hi.X, (int)MathF.Floor(hi.X / Cell - 0.5f));
            y0 = Math.Max(page.Lo.Y, (int)MathF.Ceiling(lo.Y / Cell - 0.5f)); y1 = Math.Min(page.Hi.Y, (int)MathF.Floor(hi.Y / Cell - 0.5f));
            z0 = Math.Max(page.Lo.Z, (int)MathF.Ceiling(lo.Z / Cell - 0.5f)); z1 = Math.Min(page.Hi.Z, (int)MathF.Floor(hi.Z / Cell - 0.5f));
            return x0 <= x1 && y0 <= y1 && z0 <= z1;
        }

        private void Box(SolidRef r, Vector3 centre, Vector3 size, Quaternion rot, ((int X, int Y, int Z) Lo, (int X, int Y, int Z) Hi) page)
        {
            rot = rot.LengthSquared() < 1e-6f ? Quaternion.Identity : Quaternion.Normalize(rot);
            var inv = Quaternion.Conjugate(rot);
            var half = size * 0.5f;
            static float Thin(Vector3 axis) => MathF.Max(MathF.Abs(axis.X), MathF.Max(MathF.Abs(axis.Y), MathF.Abs(axis.Z))) * Cell * 0.5f + 1e-4f;
            half = Vector3.Max(half, new Vector3(Thin(Vector3.Transform(Vector3.UnitX, rot)), Thin(Vector3.Transform(Vector3.UnitY, rot)),
                                                 Thin(Vector3.Transform(Vector3.UnitZ, rot))));
            var ext = Vector3.Abs(Vector3.Transform(new Vector3(half.X, 0, 0), rot)) + Vector3.Abs(Vector3.Transform(new Vector3(0, half.Y, 0), rot))
                      + Vector3.Abs(Vector3.Transform(new Vector3(0, 0, half.Z), rot));
            if (!Range(centre - ext, centre + ext, page, out int x0, out int x1, out int y0, out int y1, out int z0, out int z1)) return;
            for (int z = z0; z <= z1; z++)
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        int i = x + _b.Nx * (y + _b.Ny * z);
                        if (_b.State[i] == Solidity.Solid) continue;
                        var p = new Vector3((x + 0.5f) * Cell, (y + 0.5f) * Cell, (z + 0.5f) * Cell);
                        var l = Vector3.Abs(Vector3.Transform(p - centre, inv));
                        if (l.X <= half.X && l.Y <= half.Y && l.Z <= half.Z) _b.State[i] = Solidity.Solid;
                    }
        }

        /// <summary>A shape's convex pieces, each face out by a digital plane's half thickness for its angle and each
        /// piece within its bounds grown by half a cell: a roof's eave, a wedge thinning to nothing, still stops a
        /// flood. The ground's prisms as they are: the ground is never thin.</summary>
        private Piece[]? PiecesOf(SolidRef r, bool thicken)
        {
            int parts = _world.PartCountOf(r);
            int need = _world.TriangleCountOf(r) + 8;
            if (_planes.Length < need) _planes = new Vector4[need];
            if (_corners.Length < 3 * need) _corners = new Vector3[3 * need];
            var list = new List<Piece>(Math.Max(1, parts));
            for (int part = 0; part < Math.Max(1, parts); part++)
            {
                int count = parts == 0 ? _world.PlanesOf(r, _base, _planes) : _world.PartPlanesOf(r, part, _base, _planes);
                if (count == 0) continue;
                int tris = parts == 0 ? _world.TrianglesOf(r, _base, _corners) : _world.PartTrianglesOf(r, part, _base, _corners);
                var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
                for (int k = 0; k < 3 * tris; k++) { lo = Vector3.Min(lo, _corners[k]); hi = Vector3.Max(hi, _corners[k]); }
                var planes = _planes.AsSpan(0, count).ToArray();
                if (thicken)
                {
                    lo -= new Vector3(Cell * 0.5f); hi += new Vector3(Cell * 0.5f);
                    for (int k = 0; k < count; k++)
                    {
                        var q = planes[k];
                        float len = MathF.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z);
                        if (len < 1e-12f) continue;
                        float most = MathF.Max(MathF.Abs(q.X), MathF.Max(MathF.Abs(q.Y), MathF.Abs(q.Z))) / len;
                        planes[k] = new Vector4(q.X, q.Y, q.Z, q.W + len * (most * Cell * 0.5f + 1e-4f));
                    }
                }
                list.Add(new Piece(planes, lo, hi));
            }
            return list.Count > 0 ? list.ToArray() : null;
        }

        private void Fill(in Piece piece, ((int X, int Y, int Z) Lo, (int X, int Y, int Z) Hi) page)
        {
            if (!Range(piece.Lo, piece.Hi, page, out int x0, out int x1, out int y0, out int y1, out int z0, out int z1)) return;
            var planes = piece.Planes;
            for (int z = z0; z <= z1; z++)
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        int i = x + _b.Nx * (y + _b.Ny * z);
                        if (_b.State[i] == Solidity.Solid) continue;
                        if (Holds(planes, new Vector3((x + 0.5f) * Cell, (y + 0.5f) * Cell, (z + 0.5f) * Cell))) _b.State[i] = Solidity.Solid;
                    }
        }

        private static bool Holds(Vector4[] planes, Vector3 p)
        {
            foreach (var q in planes)
                if (q.X * p.X + q.Y * p.Y + q.Z * p.Z - q.W > TriangleWorld.InsideSlack) return false;
            return true;
        }
    }
}

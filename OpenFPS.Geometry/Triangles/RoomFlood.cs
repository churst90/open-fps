using System.Numerics;

namespace OpenFPS.Geometry;

/// <summary>
/// Rooms measured from the walls (docs/GEOMETRY.md 2.7, 12.6): each room is a name and a seed point, and its shape is
/// a flood from the point through the space the solids leave, stopped by walls, floors, ceilings, shut door leaves,
/// openings (a portal with no leaf, where one room ends and the next begins with no wall between) and the open sky.
/// An L, a bay, a wing no room was named for, a room open to the next through an archway, all come out as the walls
/// and openings make them.
/// </summary>
/// <remarks>
/// Each room starts in a block of cells round its point (as far as the walls round it, StartBlock), and a block whose
/// air reaches its side grows that way, keeping what it has flooded, until all of its air lies inside it. Groups whose
/// rooms' air is the same air flood again as one, a cell to the room that reaches it first; a wall's cells two rooms
/// flooded apart both reach go to the room whose point is nearer (RoomCells). The result does not depend on how big a
/// block began. A cell is an array index in its group's dense block, not a key.
/// </remarks>
public static class RoomFlood
{
    /// <summary>The cell the flood moves in, metres: half the acoustic grid's half-metre voxel, so a 1.1 m hall
    /// between walls a quarter metre thick keeps four cells of air across.</summary>
    public const float Cell = 0.25f;

    /// <summary>How far past a room's box its rooms were grouped by when the box still said where it was (MapTiles,
    /// until the boxes go), metres.</summary>
    public const float DefaultMargin = 1.0f;

    /// <summary>How far round its point a room's block starts, metres; a room that reaches its side grows it.</summary>
    public const float StartReach = 8f;

    /// <summary>
    /// The most cells a group of rooms floods in as one block: 3 bytes a cell while it floods, so 190 MB at most. A
    /// group whose block would be bigger is not flooded and its rooms keep their boxes. The largest on the three maps
    /// is Albany's college, 52 million; Albany's 640 by 193 m footprint round 34414 Highway 99e is 88 million.
    /// </summary>
    public const long MaxBlockCells = 64_000_000;

    /// <summary>How far up nothing may be for air to be under the open sky, metres.</summary>
    public const float SkyReach = 500f;

    /// <summary>A room to flood: its id, its point, and the frame its faces are named in. <see cref="Size"/> is its
    /// box where it has one: only grouping for tiles reads it (<see cref="Groups"/>), never the flood.</summary>
    public readonly record struct Seed(int Id, Vector3 Centre, Vector3 Size, Quaternion Rotation);

    /// <summary>An opening the flood stops at (a portal with no leaf): its middle, its size (across, up, through),
    /// its frame, and whether it opens to the outdoors. Thinner than a cell, it is taken as thick as a flood needs, as
    /// a thin wall is.</summary>
    public readonly record struct Barrier(int Id, Vector3 Centre, Vector3 Size, Quaternion Rotation, bool Outdoors = false);

    /// <summary>
    /// The floods of groups of rooms kept from one flood to the next: a group whose seeds, openings and geometry
    /// (every piece of the world over its block, where it stands) are as they were, and into whose block no other
    /// room has come, is not flooded again. A flood keeps only the groups it used.
    /// </summary>
    public sealed class Cache
    {
        internal List<Kept> Groups = new();
        internal List<Kept> Used = new();
        public int Hits { get; internal set; }
        public int Misses { get; internal set; }

        internal sealed class Kept
        {
            public required Seed[] Seeds;
            public required (int X, int Y, int Z) Lo, Hi, AirLo, AirHi;
            public required List<(int Seed, (int X, int Y, int Z) Lo, (int X, int Y, int Z) Hi)> Reaches;
            public required ulong Signature;
            public required Room[] Rooms;
        }
    }

    /// <summary>Cells along x from (X, Y, Z), in cells from the flood's origin.</summary>
    public readonly record struct Run(int X, int Y, int Z, int Length);

    /// <summary>One room's flood: its air, the wall cells round it, whether it meets the open sky, the rooms its air
    /// meets, the openings it stops at, and whether it could not start (its point in a solid, or under the sky).</summary>
    public sealed class Room
    {
        public required int Id { get; init; }
        public required Vector3 Origin { get; init; }
        /// <summary>Its seed's point: of two rooms flooded apart that both reach a wall's cell, the nearer has it.</summary>
        public Vector3 Seed { get; init; }
        /// <summary>The room's air: the cells the flood reached.</summary>
        public List<Run> AirRuns { get; } = new();
        /// <summary>The cells of its walls, floor and ceiling next to its air (each to the first room that reaches
        /// it): a point on a wall's face is then in the room.</summary>
        public List<Run> EdgeRuns { get; } = new();
        public int AirCount { get; internal set; }
        public int EdgeCount { get; internal set; }
        /// <summary>Its air meets air under the open sky: an open front, a doorway with nothing in it to the outdoors.</summary>
        public bool Open { get; internal set; }
        /// <summary>Where it met that air: the first few cells under the sky (to trace a way out).</summary>
        public List<Vector3> OpenAt { get; } = new();
        public bool Unseeded { get; internal set; }
        /// <summary>Its group's block was more than <see cref="MaxBlockCells"/>: not flooded, its box serves.</summary>
        public bool TooBig { get; internal set; }
        /// <summary>The rooms whose air its air meets with nothing between: an opening between them.</summary>
        public HashSet<int> Meets { get; } = new();
        /// <summary>The openings (<see cref="Barrier.Id"/>) its air stopped at.</summary>
        public HashSet<int> Openings { get; } = new();
        /// <summary>The cells of each opening its air meets (the opening's id, the cell): what the opening is framed
        /// from (<see cref="FloodOpenings"/>).</summary>
        public List<(int Opening, int X, int Y, int Z)> OpeningCells { get; } = new();
        /// <summary>The cells under the open sky its air meets, and which way from the air (0 +x, 1 -x, 2 +y, 3 -y,
        /// 4 +z, 5 -z): where it opens to the outdoors with no opening there.</summary>
        public List<(int Dir, int X, int Y, int Z)> SkyCells { get; } = new();
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
    /// What lines a room's air (docs/GEOMETRY.md 2.7 item 4), per face of its seed's frame in RoomAcoustics' order
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
        /// <summary>Area of each face that is another room's air or an opening, m².</summary>
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

    /// <summary>Floods every seed through <paramref name="world"/>'s solids of <paramref name="layers"/>, stopped too by
    /// <paramref name="barriers"/>. Cells are on a grid through <paramref name="origin"/> (the acoustic map's corner, so a
    /// voxel is a whole number of cells). Groups that cannot meet flood apart, on up to <paramref name="threads"/>
    /// threads; each that takes longer than <paramref name="slowMs"/> is told to <paramref name="report"/>. With
    /// <paramref name="cache"/>, groups that have not changed since the last flood with it are taken from it. With
    /// <paramref name="measure"/>, each room's <see cref="Room.Lining"/> is measured too.</summary>
    public static Dictionary<int, Room> Flood(TriangleWorld world, IReadOnlyList<Seed> seeds, Vector3 origin,
                                              IReadOnlyList<Barrier>? barriers = null, GeometryLayers layers = GeometryLayers.Acoustics,
                                              int threads = 1, Action<string>? report = null, double slowMs = 250, Cache? cache = null,
                                              bool measure = false)
    {
        var rooms = new Dictionary<int, Room>();
        if (seeds.Count == 0) return rooms;
        barriers ??= Array.Empty<Barrier>();
        var bars = new BarrierIndex(barriers, origin);
        var store = new PageStore(world, layers, origin, bars);
        var seedIndex = new Dictionary<int, int>();
        for (int si = 0; si < seeds.Count; si++) seedIndex[seeds[si].Id] = si;

        // Groups kept from the last flood: every seed as it was, the same geometry over the block, no other seed in it.
        var groups = new List<Cluster>();
        var taken = new bool[seeds.Count];
        if (cache != null)
        {
            cache.Used = new List<Cache.Kept>(); cache.Hits = cache.Misses = 0;
            foreach (var kept in cache.Groups)
            {
                bool same = true;
                foreach (var s in kept.Seeds)
                    if (!seedIndex.TryGetValue(s.Id, out int si) || taken[si] || seeds[si] != s) { same = false; break; }
                if (!same) continue;
                var ids = kept.Seeds.Select(s => s.Id).ToHashSet();
                for (int si = 0; si < seeds.Count && same; si++)
                    if (!ids.Contains(seeds[si].Id) && Within(CellOf(seeds[si].Centre, origin), kept.Lo, kept.Hi)) same = false;
                if (!same || Signature(world, bars, kept.Lo, kept.Hi, kept.Seeds, origin, layers, measure) != kept.Signature) continue;
                var c = new Cluster { Lo = kept.Lo, Hi = kept.Hi, Kept = kept };
                foreach (var s in kept.Seeds) { int si = seedIndex[s.Id]; taken[si] = true; c.Seeds.Add(si); }
                groups.Add(c);
            }
        }
        for (int si = 0; si < seeds.Count; si++)
        {
            if (taken[si]) continue;
            rooms[seeds[si].Id] = new Room { Id = seeds[si].Id, Origin = origin, Seed = seeds[si].Centre };
            var c = new Cluster();
            (c.Lo, c.Hi) = StartBlock(world, layers, seeds[si].Centre, origin);
            c.Seeds.Add(si);
            groups.Add(c);
        }
        foreach (var c in groups)
            if (c.Kept != null) { c.AirLo = c.Kept.AirLo; c.AirHi = c.Kept.AirHi; c.Reaches = c.Kept.Reaches; foreach (var r in c.Kept.Rooms) rooms[r.Id] = r; }

        var pending = groups.Where(c => c.Kept == null).ToList();
        for (int pass = 0; pending.Count > 0 && pass < 64; pass++)
        {
            pending.Sort((a, b) => b.Cells.CompareTo(a.Cells));
            void One(Cluster c)
            {
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                FloodCluster(world, layers, origin, c, seeds, store, rooms, measure);
                double ms = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                if (report == null) return;
                if (rooms[seeds[c.Seeds[0]].Id].TooBig)
                    lock (report) report($"{c.Seeds.Count} room(s) from {seeds[c.Seeds[0]].Id} reach {(c.Hi.X - c.Lo.X + 1) * Cell:F0} by {(c.Hi.Y - c.Lo.Y + 1) * Cell:F0} by "
                                         + $"{(c.Hi.Z - c.Lo.Z + 1) * Cell:F0} m: more than {MaxBlockCells:N0} cells to flood, so their boxes serve");
                else if (ms > slowMs)
                {
                    int air = c.Seeds.Sum(i => rooms[seeds[i].Id].AirCount);
                    lock (report) report($"{c.Seeds.Count} room(s) from {seeds[c.Seeds[0]].Id} in a block {(c.Hi.X - c.Lo.X + 1) * Cell:F0} by "
                                         + $"{(c.Hi.Y - c.Lo.Y + 1) * Cell:F0} by {(c.Hi.Z - c.Lo.Z + 1) * Cell:F0} m ({c.Cells:N0} cells, air {air:N0}): {ms:F0} ms");
                }
            }
            if (threads <= 1) foreach (var c in pending) One(c);
            // Taken one at a time in order, biggest first, so the biggest is never left to last behind a thread's chunk.
            else Parallel.ForEach(System.Collections.Concurrent.Partitioner.Create(pending, System.Collections.Concurrent.EnumerablePartitionerOptions.NoBuffering),
                                  new ParallelOptions { MaxDegreeOfParallelism = threads }, One);
            // Groups whose rooms' air turned out to be the same air flood again as one.
            pending = Join(groups, seeds, rooms, origin);
        }
        if (cache != null)
        {
            foreach (var c in groups)
            {
                if (c.Kept != null) { cache.Hits++; cache.Used.Add(c.Kept); continue; }
                cache.Misses++;
                var mine = c.Seeds.Select(i => seeds[i]).ToArray();
                cache.Used.Add(new Cache.Kept
                {
                    Seeds = mine, Lo = c.Lo, Hi = c.Hi, AirLo = c.AirLo, AirHi = c.AirHi, Reaches = c.Reaches,
                    Signature = Signature(world, bars, c.Lo, c.Hi, mine, origin, layers, measure),
                    Rooms = c.Seeds.Select(i => rooms[seeds[i].Id]).ToArray(),
                });
            }
            cache.Groups = cache.Used;
        }
        return rooms;
    }

    /// <summary>
    /// The block a room starts in: as far as the nearest surface each way along the grid's axes from its point, and a
    /// metre more (a corridor's block is the corridor's length at once, not grown to it a pass at a time). Only how
    /// big a block begins, never what floods: a room that reaches the side grows it.
    /// </summary>
    private static ((int X, int Y, int Z) Lo, (int X, int Y, int Z) Hi) StartBlock(TriangleWorld world, GeometryLayers layers, Vector3 p, Vector3 origin)
    {
        var all = new AcceptAll();
        float Reach(Vector3 d) => (world.Closest(p, d, 120f, layers, RayFaces.Both, ref all, out var hit) ? hit.T : 120f) + 1f;
        float px = Reach(Vector3.UnitX), nx = Reach(-Vector3.UnitX), py = Reach(Vector3.UnitY), ny = Reach(-Vector3.UnitY);
        float pz = Reach(Vector3.UnitZ), nz = Reach(-Vector3.UnitZ);
        float least = 2f;
        var lo = CellOf(p - new Vector3(MathF.Max(least, nx), MathF.Max(least, ny), MathF.Max(least, nz)), origin);
        var hi = CellOf(p + new Vector3(MathF.Max(least, px), MathF.Max(least, py), MathF.Max(least, pz)), origin);
        return (lo, hi);
    }

    private static bool Within((int X, int Y, int Z) p, (int X, int Y, int Z) lo, (int X, int Y, int Z) hi)
        => p.X >= lo.X && p.Y >= lo.Y && p.Z >= lo.Z && p.X <= hi.X && p.Y <= hi.Y && p.Z <= hi.Z;

    /// <summary>Which of the boxes overlap, joined into groups (each group's indices in order), groups in order of their first.</summary>
    private static List<List<int>> Overlapping(IReadOnlyList<((int X, int Y, int Z) Lo, (int X, int Y, int Z) Hi)> boxes)
    {
        int n = boxes.Count;
        var parent = Enumerable.Range(0, n).ToArray();
        int Find(int i) { while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; } return i; }
        var order = Enumerable.Range(0, n).OrderBy(i => boxes[i].Lo.X).ToArray();
        var active = new List<int>();
        foreach (int i in order)
        {
            var (lo, hi) = boxes[i];
            active.RemoveAll(j => boxes[j].Hi.X < lo.X);
            foreach (int j in active)
            {
                var (ol, oh) = boxes[j];
                if (oh.Y < lo.Y || ol.Y > hi.Y || oh.Z < lo.Z || ol.Z > hi.Z) continue;
                int a = Find(i), b = Find(j);
                if (a != b) parent[a] = b;
            }
            active.Add(i);
        }
        var byRoot = new Dictionary<int, List<int>>();
        for (int i = 0; i < n; i++)
        {
            int r = Find(i);
            if (!byRoot.TryGetValue(r, out var l)) byRoot[r] = l = new List<int>();
            l.Add(i);
        }
        return byRoot.Values.OrderBy(l => l[0]).ToList();
    }

    /// <summary>
    /// The groups flooded whole (none growing) whose air's reach overlaps, joined in place into one each (its block
    /// the union of theirs), returned to flood again.
    /// </summary>
    private static List<Cluster> Join(List<Cluster> groups, IReadOnlyList<Seed> seeds, Dictionary<int, Room> rooms, Vector3 origin)
    {
        var done = groups.Where(g => g.Reaches.Count > 0).ToList();
        var joined = new List<Cluster>();
        // Rooms of two groups whose air is the same air flood again as one (a cell to whichever reaches it first);
        // rooms that only share a wall's or an opening's cells need not: whose those are, RoomCells settles.
        var boxes = new List<(int Group, int Seed, (int X, int Y, int Z) Lo, (int X, int Y, int Z) Hi)>();
        for (int g = 0; g < done.Count; g++)
            foreach (var (seed, lo, hi) in done[g].Reaches) boxes.Add((g, seed, lo, hi));
        var parent = Enumerable.Range(0, done.Count).ToArray();
        int Find(int i) { while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; } return i; }
        boxes.Sort((a, b) => a.Lo.X.CompareTo(b.Lo.X));
        var active = new List<int>();
        for (int i = 0; i < boxes.Count; i++)
        {
            var bi = boxes[i];
            active.RemoveAll(j => boxes[j].Hi.X < bi.Lo.X);
            foreach (int j in active)
            {
                var bj = boxes[j];
                if (bj.Group == bi.Group || bj.Hi.Y < bi.Lo.Y || bj.Lo.Y > bi.Hi.Y || bj.Hi.Z < bi.Lo.Z || bj.Lo.Z > bi.Hi.Z) continue;
                int a = Find(bi.Group), b = Find(bj.Group);
                if (a == b) continue;
                if (SameAir(rooms[seeds[bi.Seed].Id], rooms[seeds[bj.Seed].Id])) parent[a] = b;
            }
            active.Add(i);
        }
        var byRoot = new Dictionary<int, List<int>>();
        for (int g = 0; g < done.Count; g++)
        {
            int r = Find(g);
            if (!byRoot.TryGetValue(r, out var l)) byRoot[r] = l = new List<int>();
            l.Add(g);
        }
        foreach (var members in byRoot.Values.OrderBy(l => l[0]))
        {
            if (members.Count == 1) continue;
            var c = new Cluster { Lo = done[members[0]].Lo, Hi = done[members[0]].Hi };
            foreach (int m in members)
            {
                var g = done[m];
                c.Seeds.AddRange(g.Seeds);
                c.Lo = (Math.Min(c.Lo.X, g.Lo.X), Math.Min(c.Lo.Y, g.Lo.Y), Math.Min(c.Lo.Z, g.Lo.Z));
                c.Hi = (Math.Max(c.Hi.X, g.Hi.X), Math.Max(c.Hi.Y, g.Hi.Y), Math.Max(c.Hi.Z, g.Hi.Z));
                groups.Remove(g);
            }
            c.Seeds.Sort();
            foreach (int si in c.Seeds) rooms[seeds[si].Id] = new Room { Id = seeds[si].Id, Origin = origin, Seed = seeds[si].Centre };
            groups.Add(c);
            joined.Add(c);
        }
        return joined;
    }

    /// <summary>Whether two rooms flooded apart hold any cell of air in common.</summary>
    private static bool SameAir(Room a, Room b)
    {
        if (a.AirRuns.Count > b.AirRuns.Count) (a, b) = (b, a);
        var rows = new Dictionary<(int, int), List<(int X0, int X1)>>();
        foreach (var r in a.AirRuns)
        {
            if (!rows.TryGetValue((r.Y, r.Z), out var l)) rows[(r.Y, r.Z)] = l = new List<(int, int)>();
            l.Add((r.X, r.X + r.Length - 1));
        }
        foreach (var r in b.AirRuns)
            if (rows.TryGetValue((r.Y, r.Z), out var l))
                foreach (var (x0, x1) in l)
                    if (r.X <= x1 && r.X + r.Length - 1 >= x0) return true;
        return false;
    }

    /// <summary>The ids of the seeds whose boxes grown by <paramref name="margin"/> overlap: rooms that went together in
    /// tiles while they had boxes (MapTiles).</summary>
    public static List<List<int>> Groups(IReadOnlyList<Seed> seeds, Vector3 origin, float margin = DefaultMargin)
    {
        var boxes = new List<((int X, int Y, int Z), (int X, int Y, int Z))>();
        foreach (var s in seeds)
        {
            var rot = s.Rotation.LengthSquared() < 1e-6f ? Quaternion.Identity : Quaternion.Normalize(s.Rotation);
            var g = s.Size * 0.5f + new Vector3(margin);
            var ext = Vector3.Abs(Vector3.Transform(new Vector3(g.X, 0, 0), rot)) + Vector3.Abs(Vector3.Transform(new Vector3(0, g.Y, 0), rot))
                      + Vector3.Abs(Vector3.Transform(new Vector3(0, 0, g.Z), rot));
            var lo = CellOf(s.Centre - ext, origin); var hi = CellOf(s.Centre + ext, origin);
            boxes.Add(((lo.X - 1, lo.Y - 1, lo.Z - 1), (hi.X + 1, hi.Y + 1, hi.Z + 1)));
        }
        return Overlapping(boxes).Select(l => l.Select(i => seeds[i].Id).ToList()).ToList();
    }

    /// <summary>Whether the cell holding <paramref name="p"/> is solid, as a flood would find it.</summary>
    public static bool Solid(TriangleWorld world, Vector3 p, Vector3 origin, GeometryLayers layers = GeometryLayers.Acoustics)
    {
        var (x, y, z) = CellOf(p, origin);
        var block = new Block(x, y, z, 1, 1, 1);
        new Raster(new PageStore(world, layers, origin, new BarrierIndex(Array.Empty<Barrier>(), origin)), block).Blocked(0);
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

    // ═══ Groups ═════════════════════════════════════════════════════════════════════════════════

    private sealed class Cluster
    {
        public readonly List<int> Seeds = new();
        public (int X, int Y, int Z) Lo, Hi;
        /// <summary>Its rooms' air's bounds, a cell further each way (the wall cells round it); empty before a flood.</summary>
        public (int X, int Y, int Z) AirLo = (int.MaxValue, 0, 0), AirHi = (int.MinValue, 0, 0);
        /// <summary>Each of its rooms' air's bounds, a cell further each way: groups whose rooms' bounds overlap flood as one.</summary>
        public List<(int Seed, (int X, int Y, int Z) Lo, (int X, int Y, int Z) Hi)> Reaches = new();
        public Cache.Kept? Kept;
        public long Cells => (long)(Hi.X - Lo.X + 1) * (Hi.Y - Lo.Y + 1) * (Hi.Z - Lo.Z + 1);
    }

    /// <summary>The openings filed by 4 m columns, to find those near a block.</summary>
    private sealed class BarrierIndex
    {
        public readonly Barrier[] All;
        public readonly (Vector3 Min, Vector3 Max)[] Bounds;
        private readonly Dictionary<(int, int), List<int>> _cells = new();
        private const float Column = 4f;

        public BarrierIndex(IReadOnlyList<Barrier> barriers, Vector3 origin)
        {
            All = barriers.ToArray();
            Bounds = new (Vector3, Vector3)[All.Length];
            for (int i = 0; i < All.Length; i++)
            {
                var b = All[i];
                var rot = b.Rotation.LengthSquared() < 1e-6f ? Quaternion.Identity : Quaternion.Normalize(b.Rotation);
                var h = b.Size * 0.5f + new Vector3(Cell);
                var ext = Vector3.Abs(Vector3.Transform(new Vector3(h.X, 0, 0), rot)) + Vector3.Abs(Vector3.Transform(new Vector3(0, h.Y, 0), rot))
                          + Vector3.Abs(Vector3.Transform(new Vector3(0, 0, h.Z), rot));
                Bounds[i] = (b.Centre - ext, b.Centre + ext);
                for (int x = (int)MathF.Floor(Bounds[i].Item1.X / Column); x <= (int)MathF.Floor(Bounds[i].Item2.X / Column); x++)
                    for (int z = (int)MathF.Floor(Bounds[i].Item1.Z / Column); z <= (int)MathF.Floor(Bounds[i].Item2.Z / Column); z++)
                    {
                        if (!_cells.TryGetValue((x, z), out var l)) _cells[(x, z)] = l = new List<int>();
                        l.Add(i);
                    }
            }
        }

        private Dictionary<int, bool>? _outdoors;

        /// <summary>Whether the opening of this id opens to the outdoors.</summary>
        public bool Outdoors(int id)
        {
            if (_outdoors == null)
            {
                var d = new Dictionary<int, bool>();
                foreach (var b in All) d[b.Id] = b.Outdoors;
                _outdoors = d;
            }
            return _outdoors.TryGetValue(id, out bool o) && o;
        }

        /// <summary>The openings whose bounds meet [lo, hi], in id order.</summary>
        public List<Barrier> Near(Vector3 lo, Vector3 hi)
        {
            var found = new HashSet<int>();
            if (All.Length > 0)
                for (int x = (int)MathF.Floor(lo.X / Column); x <= (int)MathF.Floor(hi.X / Column); x++)
                    for (int z = (int)MathF.Floor(lo.Z / Column); z <= (int)MathF.Floor(hi.Z / Column); z++)
                        if (_cells.TryGetValue((x, z), out var l))
                            foreach (int i in l)
                            {
                                var (bl, bh) = Bounds[i];
                                if (bh.X >= lo.X && bl.X <= hi.X && bh.Y >= lo.Y && bl.Y <= hi.Y && bh.Z >= lo.Z && bl.Z <= hi.Z) found.Add(i);
                            }
            return found.OrderBy(i => i).Select(i => All[i]).ToList();
        }
    }

    /// <summary>What a group's flood depends on: its seeds, the openings and every piece of the world over its block
    /// (and up to the sky above it), where they are.</summary>
    private static ulong Signature(TriangleWorld world, BarrierIndex bars, (int X, int Y, int Z) cLo, (int X, int Y, int Z) cHi,
                                   Seed[] seeds, Vector3 origin, GeometryLayers layers, bool measure)
    {
        ulong h = 14695981039346656037UL;
        void Mix(ulong v) { h ^= v; h *= 1099511628211UL; h ^= h >> 29; }
        void MixF(float v) => Mix((ulong)BitConverter.SingleToUInt32Bits(v));
        void MixV(Vector3 v) { MixF(v.X); MixF(v.Y); MixF(v.Z); }
        void MixQ(Quaternion q) { MixF(q.X); MixF(q.Y); MixF(q.Z); MixF(q.W); }
        Mix((ulong)layers); MixV(origin); Mix(measure ? 1UL : 0UL);
        Mix((ulong)(uint)cLo.X); Mix((ulong)(uint)cLo.Y); Mix((ulong)(uint)cLo.Z); Mix((ulong)(uint)cHi.X); Mix((ulong)(uint)cHi.Y); Mix((ulong)(uint)cHi.Z);
        foreach (var s in seeds) { Mix((ulong)(uint)s.Id); MixV(s.Centre); MixQ(s.Rotation); }
        var lo = origin + new Vector3(cLo.X, cLo.Y, cLo.Z) * Cell;
        var hi = origin + new Vector3(cHi.X + 1, cHi.Y + 1, cHi.Z + 1) * Cell;
        foreach (var b in bars.Near(lo, hi)) { Mix((ulong)(uint)b.Id); MixV(b.Centre); MixV(b.Size); MixQ(b.Rotation); }
        var top = hi + new Vector3(0, SkyReach, 0);
        foreach (ref readonly var inst in world.Instances)
        {
            if (inst.Max.X < lo.X || inst.Max.Y < lo.Y || inst.Max.Z < lo.Z || inst.Min.X > hi.X || inst.Min.Y > top.Y || inst.Min.Z > hi.Z) continue;
            Mix(inst.Piece.Signature); Mix((ulong)(uint)inst.Owner); MixV(inst.Position); MixQ(inst.Rotation);
        }
        return h;
    }

    // ═══ The flood ══════════════════════════════════════════════════════════════════════════════

    private static void FloodCluster(TriangleWorld world, GeometryLayers layers, Vector3 origin, Cluster cluster,
                                     IReadOnlyList<Seed> seeds, PageStore store, Dictionary<int, Room> rooms, bool measure)
    {
        int start = (int)MathF.Ceiling(StartReach / Cell);
        var (lx, ly, lz) = cluster.Lo;
        int nx = cluster.Hi.X - lx + 1, ny = cluster.Hi.Y - ly + 1, nz = cluster.Hi.Z - lz + 1;
        if ((long)nx * ny * nz > MaxBlockCells || cluster.Seeds.Count >= ushort.MaxValue)
        {
            foreach (int i in cluster.Seeds) rooms[seeds[i].Id].TooBig = true;
            cluster.AirLo = (int.MaxValue, 0, 0); cluster.AirHi = (int.MinValue, 0, 0); cluster.Reaches = new();
            return;
        }
        var block = new Block(lx, ly, lz, nx, ny, nz);
        var raster = new Raster(store, block);
        int n = nx * ny * nz, sx = 1, sy = nx, sz = nx * ny;
        // Which seed (its index in the group, plus one) holds each cell: air, or a wall cell its air reached.
        var claim = new ushort[n];
        var local = cluster.Seeds;
        Vector3 CentreAt(int i) { int x = i % nx, y = i / nx % ny, z = i / sz; return CentreOf(lx + x, ly + y, lz + z, origin); }
        int Index(int x, int y, int z) => (x - lx) + nx * ((y - ly) + ny * (z - lz));
        bool Inside(int x, int y, int z) => x >= lx && y >= ly && z >= lz && x < lx + nx && y < ly + ny && z < lz + nz;

        // Under the open sky: nothing over it (up its column through the pages, what is found kept for the column).
        var coveredTo = new float[nx * nz];
        var openFrom = new float[nx * nz];
        Array.Fill(coveredTo, float.NaN);
        bool Sky(int x, int y, int z)
        {
            int col = x + nx * z;
            float h = origin.Y + (ly + y + 0.5f) * Cell;
            if (float.IsNaN(coveredTo[col])) (coveredTo[col], openFrom[col]) = (float.NegativeInfinity, float.PositiveInfinity);
            if (h < coveredTo[col]) return false;
            if (h >= openFrom[col]) return true;
            bool sky = store.Sky(lx + x, ly + y, lz + z, out float to);
            if (sky) openFrom[col] = MathF.Min(openFrom[col], h);
            else coveredTo[col] = MathF.Max(coveredTo[col], to);
            return sky;
        }

        var queue = new List<int>();
        for (int k = 0; k < local.Count; k++)
        {
            var s = seeds[local[k]];
            var (x, y, z) = CellOf(s.Centre, origin);
            // A point in a solid (a column in the room, a narrow room's wall): the nearest free cell within a metre.
            bool Free(int cx, int cy, int cz)
                => Inside(cx, cy, cz) && claim[Index(cx, cy, cz)] == 0 && !raster.Blocked(Index(cx, cy, cz)) && !Sky(cx - lx, cy - ly, cz - lz);
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
            claim[i0] = (ushort)(k + 1);
            queue.Add(i0);
        }

        var meets = new HashSet<long>();
        var touched = new HashSet<long>();
        // The opening cells and the cells under the open sky each room's air meets, where in the world they are (a
        // block's indices move as it grows): what the openings are framed from (FloodOpenings).
        var openingCells = new HashSet<(int Room, int Opening, int X, int Y, int Z)>();
        var skyCells = new HashSet<(int Room, int Dir, int X, int Y, int Z)>();
        // Air on the block's outermost layer: the walls past it are not known yet, so the block grows that way and
        // the flood goes on from these cells.
        var atSide = new List<int>();
        int grow = 0;
        void Meet(int a, int b) { if (a != b) meets.Add(a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a); }
        void Side(int c, int ax, int ay, int az)
        {
            int bits = (ax == 0 ? 2 : 0) | (ax == nx - 1 ? 1 : 0) | (ay == 0 ? 8 : 0) | (ay == ny - 1 ? 4 : 0) | (az == 0 ? 32 : 0) | (az == nz - 1 ? 16 : 0);
            if (bits == 0) return;
            grow |= bits;
            atSide.Add(c);
        }
        foreach (int c in queue) Side(c, c % nx, c / nx % ny, c / sz);
        int head = 0;
        while (true)
        {
            for (; head < queue.Count; head++)
            {
                int c = queue[head];
                int k = claim[c] - 1;
                int cx = c % nx, cy = c / nx % ny, cz = c / sz;
                for (int d = 0; d < 6; d++)
                {
                    int ax = cx, ay = cy, az = cz, step;
                    switch (d)
                    {
                        case 0: ax++; step = sx; break;
                        case 1: ax--; step = -sx; break;
                        case 2: ay++; step = sy; break;
                        case 3: ay--; step = -sy; break;
                        case 4: az++; step = sz; break;
                        default: az--; step = -sz; break;
                    }
                    if ((uint)ax >= (uint)nx || (uint)ay >= (uint)ny || (uint)az >= (uint)nz) continue;   // on from it once grown
                    int m = c + step;
                    if (raster.Blocked(m))
                    {
                        // An opening's cells, like a wall's, go to the first room to reach them: a point in a doorway is
                        // in a room, never the outdoors.
                        if (claim[m] == 0) claim[m] = (ushort)(k + 1);
                        if (raster.IsOpening(m, out int opening))
                        {
                            touched.Add(((long)k << 32) | (uint)opening);
                            // Through the opening's cells the way the air came: a solid past them (a shutter shut in
                            // a car park's bay) means the air met a wall, not an opening.
                            int bx = ax, by = ay, bz = az, b = m;
                            while (true)
                            {
                                bx += ax - cx; by += ay - cy; bz += az - cz; b += step;
                                if ((uint)bx >= (uint)nx || (uint)by >= (uint)ny || (uint)bz >= (uint)nz || !raster.IsOpening(b, out _)) break;
                            }
                            bool shut = (uint)bx < (uint)nx && (uint)by < (uint)ny && (uint)bz < (uint)nz && raster.Blocked(b);
                            if (!shut) openingCells.Add((k, opening, lx + ax, ly + ay, lz + az));
                        }
                        continue;
                    }
                    if (claim[m] != 0) { Meet(claim[m] - 1, k); continue; }
                    if (Sky(ax, ay, az))
                    {
                        var room = rooms[seeds[local[k]].Id];
                        room.Open = true;
                        lock (room) if (room.OpenAt.Count < 8) room.OpenAt.Add(CentreAt(m));
                        skyCells.Add((k, d, lx + ax, ly + ay, lz + az));
                        continue;
                    }
                    claim[m] = (ushort)(k + 1);
                    queue.Add(m);
                    Side(m, ax, ay, az);
                }
            }
            if (grow == 0) break;

            // Grow the block the ways its air reached, by half its size that way (or the start's reach), keeping all
            // of it, and go on from the air that was at its sides: nothing is flooded twice.
            int g0x = (grow & 2) != 0 ? Math.Max(start, nx / 2) : 0, g1x = (grow & 1) != 0 ? Math.Max(start, nx / 2) : 0;
            int g0y = (grow & 8) != 0 ? Math.Max(start, ny / 2) : 0, g1y = (grow & 4) != 0 ? Math.Max(start, ny / 2) : 0;
            int g0z = (grow & 32) != 0 ? Math.Max(start, nz / 2) : 0, g1z = (grow & 16) != 0 ? Math.Max(start, nz / 2) : 0;
            int mx = nx + g0x + g1x, my = ny + g0y + g1y, mz = nz + g0z + g1z;
            if ((long)mx * my * mz > MaxBlockCells)
            {
                foreach (int i in cluster.Seeds) rooms[seeds[i].Id] = new Room { Id = seeds[i].Id, Origin = origin, Seed = seeds[i].Centre, TooBig = true };
                cluster.Lo = (lx - g0x, ly - g0y, lz - g0z); cluster.Hi = (lx + nx - 1 + g1x, ly + ny - 1 + g1y, lz + nz - 1 + g1z);
                cluster.AirLo = (int.MaxValue, 0, 0); cluster.AirHi = (int.MinValue, 0, 0); cluster.Reaches = new();
                return;
            }
            int ox = nx, oy = ny, oxy = nx * ny;
            int Moved(int c) => (c % ox + g0x) + mx * ((c / ox % oy + g0y) + my * (c / oxy + g0z));
            var nb = new Block(lx - g0x, ly - g0y, lz - g0z, mx, my, mz);
            var nclaim = new ushort[mx * my * mz];
            var ncov = new float[mx * mz];
            var nopen = new float[mx * mz];
            Array.Fill(ncov, float.NaN);
            for (int z = 0; z < nz; z++)
                for (int y = 0; y < ny; y++)
                {
                    int from = nx * (y + ny * z), to = (g0x) + mx * ((y + g0y) + my * (z + g0z));
                    Array.Copy(block.State, from, nb.State, to, nx);
                    Array.Copy(claim, from, nclaim, to, nx);
                }
            for (int z = 0; z < nz; z++)
                for (int x = 0; x < nx; x++)
                {
                    ncov[(x + g0x) + mx * (z + g0z)] = coveredTo[x + nx * z];
                    nopen[(x + g0x) + mx * (z + g0z)] = openFrom[x + nx * z];
                }
            for (int q = 0; q < queue.Count; q++) queue[q] = Moved(queue[q]);
            var again = atSide.Select(Moved).ToList();
            block = nb; claim = nclaim; coveredTo = ncov; openFrom = nopen;
            raster = new Raster(store, block, raster, ox, oy, g0x, g0y, g0z);
            (lx, ly, lz) = (block.Lx, block.Ly, block.Lz);
            nx = mx; ny = my; nz = mz; n = nx * ny * nz; sy = nx; sz = nx * ny;
            grow = 0;
            atSide.Clear();
            // Cells now inside the block go on from where they stopped; those still at a side say so again.
            foreach (int c in again) { queue.Add(c); Side(c, c % nx, c / nx % ny, c / sz); }
        }
        cluster.Lo = (lx, ly, lz); cluster.Hi = (lx + nx - 1, ly + ny - 1, lz + nz - 1);
        {
            int x0 = int.MaxValue, y0 = int.MaxValue, z0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue, z1 = int.MinValue;
            foreach (int c in queue)
            {
                int cx = c % nx, cy = c / nx % ny, cz = c / sz;
                x0 = Math.Min(x0, cx); y0 = Math.Min(y0, cy); z0 = Math.Min(z0, cz); x1 = Math.Max(x1, cx); y1 = Math.Max(y1, cy); z1 = Math.Max(z1, cz);
            }
            if (queue.Count > 0) { cluster.AirLo = (lx + x0 - 1, ly + y0 - 1, lz + z0 - 1); cluster.AirHi = (lx + x1 + 1, ly + y1 + 1, lz + z1 + 1); }
            else { cluster.AirLo = (int.MaxValue, 0, 0); cluster.AirHi = (int.MinValue, 0, 0); }
            var lo = new (int X, int Y, int Z)[local.Count];
            var hi = new (int X, int Y, int Z)[local.Count];
            Array.Fill(lo, (int.MaxValue, int.MaxValue, int.MaxValue)); Array.Fill(hi, (int.MinValue, int.MinValue, int.MinValue));
            foreach (int c in queue)
            {
                int k = claim[c] - 1, cx = lx + c % nx, cy = ly + c / nx % ny, cz = lz + c / sz;
                lo[k] = (Math.Min(lo[k].X, cx - 1), Math.Min(lo[k].Y, cy - 1), Math.Min(lo[k].Z, cz - 1));
                hi[k] = (Math.Max(hi[k].X, cx + 1), Math.Max(hi[k].Y, cy + 1), Math.Max(hi[k].Z, cz + 1));
            }
            cluster.Reaches = new();
            for (int k = 0; k < local.Count; k++) if (lo[k].X <= hi[k].X) cluster.Reaches.Add((local[k], lo[k], hi[k]));
        }

        foreach (long pair in meets)
        {
            int a = local[(int)(pair >> 32)], b = local[(int)(pair & 0xFFFFFFFF)];
            rooms[seeds[a].Id].Meets.Add(seeds[b].Id);
            rooms[seeds[b].Id].Meets.Add(seeds[a].Id);
        }
        foreach (long t in touched) rooms[seeds[local[(int)(t >> 32)]].Id].Openings.Add((int)(t & 0xFFFFFFFF));
        foreach (var (k, opening, x, y, z) in openingCells) rooms[seeds[local[k]].Id].OpeningCells.Add((opening, x, y, z));
        foreach (var (k, dir, x, y, z) in skyCells) rooms[seeds[local[k]].Id].SkyCells.Add((dir, x, y, z));

        // The cells as runs along x, room by room, over the bounds of what was claimed (the air's and a cell more for
        // its walls), not the whole block, most of which a room in a big block never reaches.
        var counts = new int[local.Count * 2];
        var held = new List<int>(queue.Count);
        held.AddRange(queue);
        if (queue.Count > 0)
        {
            int bx0 = Math.Max(0, cluster.AirLo.X - lx), by0 = Math.Max(0, cluster.AirLo.Y - ly), bz0 = Math.Max(0, cluster.AirLo.Z - lz);
            int bx1 = Math.Min(nx - 1, cluster.AirHi.X - lx), by1 = Math.Min(ny - 1, cluster.AirHi.Y - ly), bz1 = Math.Min(nz - 1, cluster.AirHi.Z - lz);
            for (int z = bz0; z <= bz1; z++)
                for (int y = by0; y <= by1; y++)
                {
                    int row = nx * (y + ny * z);
                    int x = bx0;
                    while (x <= bx1)
                    {
                        int c = claim[row + x];
                        if (c == 0) { x++; continue; }
                        bool edge = block.State[row + x] is Solidity.Solid or Solidity.Opening;
                        int x0 = x;
                        while (x <= bx1 && claim[row + x] == c && (block.State[row + x] is Solidity.Solid or Solidity.Opening) == edge) x++;
                        var room = rooms[seeds[local[c - 1]].Id];
                        (edge ? room.EdgeRuns : room.AirRuns).Add(new Run(lx + x0, ly + y, lz + z, x - x0));
                        counts[(c - 1) * 2 + (edge ? 1 : 0)] += x - x0;
                    }
                }
        }
        for (int k = 0; k < local.Count; k++)
        {
            var s = seeds[local[k]];
            var room = rooms[s.Id];
            room.AirCount = counts[k * 2];
            room.EdgeCount = counts[k * 2 + 1];
        }
        if (measure) MeasureLinings(world, layers, origin, block, raster, claim, local, seeds, rooms, held);
    }

    /// <summary>The faces of air against a solid that one ray answers for, cells a side: the faces of a room in one grid
    /// direction within a cube this size are one sample, cast from the face nearest the cube's middle. Each ray costs
    /// about 2 µs: one for every face was 5 million rays and 13 s on the city (a cube of 4 cells, 1.5 s).</summary>
    private const int SampleCells = 4;

    /// <summary>One sample's faces: how many, the one nearest the cube's middle and the one furthest from it, and all
    /// of them (asked one by one where the two rays meet different faces: a corner).</summary>
    private struct Group
    {
        public int Count, Cell, Dir, Far;
        public float Distance, FarDistance;
        public List<int> Cells;
    }

    /// <summary>
    /// Each room's <see cref="Lining"/>: every face of its air against something else, a solid (a ray to the surface,
    /// one for the faces of a <see cref="SampleCells"/> cube), the open sky or another room's air. The cells stop short
    /// of a surface by up to a cell (a finish a centimetre thick fills the cell it lies in), so the rays' gaps to the
    /// surfaces are added to the volume, and each face's area is grown by the gaps across it: a 2.7 m wall over a
    /// carpet's cell was 2.5 m of cells.
    /// </summary>
    private static void MeasureLinings(TriangleWorld world, GeometryLayers layers, Vector3 origin, Block block, Raster raster,
                                       ushort[] claim, List<int> local, IReadOnlyList<Seed> seeds, Dictionary<int, Room> rooms, List<int> held)
    {
        int nx = block.Nx, ny = block.Ny, nz = block.Nz, sz = nx * ny;
        int rc = local.Count;
        // Each room's faces are named in its seed's frame.
        var inverse = new Matrix4x4[rc];
        for (int k = 0; k < rc; k++)
        {
            var q = seeds[local[k]].Rotation;
            inverse[k] = Matrix4x4.CreateFromQuaternion(Quaternion.Conjugate(q.LengthSquared() < 1e-6f ? Quaternion.Identity : Quaternion.Normalize(q)));
        }
        var linings = new Lining[rc];
        var air = new long[rc];
        var faces = new long[rc * 6];                         // every face of a room's air against something else, by direction
        var lo = new Vector3[rc];
        var hi = new Vector3[rc];
        for (int k = 0; k < rc; k++) { linings[k] = new Lining(); lo[k] = new Vector3(float.MaxValue); hi[k] = new Vector3(float.MinValue); }
        var groups = new Dictionary<(int, int, int, int), Group>();
        var openings = new List<(int Room, int Dir, int Face, bool Meets)>();
        const float face = Cell * Cell, reach = 1.5f * Cell + 1e-3f;
        var dirs = new[] { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ };
        foreach (int c in held)
                {
                    int x = c % nx, y = c / nx % ny, z = c / sz;
                    int own = claim[c];
                    if (own == 0 || block.State[c] != Solidity.Free) continue;
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
                        if (claim[m] == own && block.State[m] == Solidity.Free) continue;
                        faces[k * 6 + d]++;
                        bool opening = raster.IsOpening(m, out int which);
                        if (opening || !raster.Blocked(m))
                        {
                            // Another room's air, or an opening between rooms: what leaves by it the routes count, at
                            // its own S·tau. Air no room holds is the open sky (the flood stops there), and an opening
                            // to the outdoors is the same sky: what reaches either leaves.
                            bool between = opening ? !raster.OpensOutdoors(which) : claim[m] != 0;
                            openings.Add((k, d, Lining.FaceOf(Vector3.TransformNormal(dirs[d], inverse[k])), between));
                            continue;
                        }
                        // The cubes on the map's grid, not the block's, so a room samples the same whatever its block.
                        int ax = block.Lx + x, ay = block.Ly + y, az = block.Lz + z;
                        int gx = (int)Math.Floor(ax / (double)SampleCells), gy = (int)Math.Floor(ay / (double)SampleCells), gz = (int)Math.Floor(az / (double)SampleCells);
                        var key = (k * 6 + d, gx, gy, gz);
                        float dx = ax - (gx + 0.5f) * SampleCells + 0.5f, dy = ay - (gy + 0.5f) * SampleCells + 0.5f, dz = az - (gz + 0.5f) * SampleCells + 0.5f;
                        float distance = dx * dx + dy * dy + dz * dz;
                        groups.TryGetValue(key, out var g);
                        if (g.Count == 0 || distance < g.Distance) { g.Cell = c; g.Dir = d; g.Distance = distance; }
                        if (g.Count == 0 || distance > g.FarDistance) { g.Far = c; g.FarDistance = distance; }
                        (g.Cells ??= new List<int>(4)).Add(c);
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
            int k = key.Item1 / 6;
            var inv = inverse[k];
            var dir = dirs[g.Dir];
            (bool Hit, int Face, string Material, float L1, float T) Ray(int cell)
            {
                int x = cell % nx, y = cell / nx % ny, z = cell / sz;
                var at = CentreOf(block.Lx + x, block.Ly + y, block.Lz + z, origin);
                if (!world.Closest(at, dir, reach, layers, RayFaces.Both, ref all, out var hit))
                    return (false, Lining.FaceOf(Vector3.TransformNormal(dir, inv)), "", 1f, 0f);
                var n = hit.Normal;
                return (true, Lining.FaceOf(Vector3.TransformNormal(hit.Front ? -n : dir, inv)), world.SurfaceOf(hit).Material ?? "",
                        MathF.Max(1f, MathF.Abs(n.X) + MathF.Abs(n.Y) + MathF.Abs(n.Z)), hit.T);
            }
            void Add((bool Hit, int Face, string Material, float L1, float T) r, int count)
            {
                met.Add((k, g.Dir, r.Face, r.Material, count * face / r.L1));
                if (!r.Hit) return;
                gap[k * 6 + g.Dir] += count * Math.Clamp(r.T - Cell * 0.5f, -Cell * 0.5f, Cell);
                cast[k * 6 + g.Dir] += count;
            }
            var middle = Ray(g.Cell);
            // A cube round a corner of walls at an angle to the grid holds faces of both: each is asked.
            if (g.Count > 1 && Ray(g.Far).Face != middle.Face)
                foreach (int c in g.Cells) Add(Ray(c), 1);
            else Add(middle, g.Count);
        }

        // Each axis's extent, as cells (the air over its faces across that axis) and as far as the surfaces; a face
        // across one axis is grown by how much further the other two reach.
        var grow = new float[rc * 3];
        var volume = new double[rc];
        Span<double> stretch = stackalloc double[3];   // every element is written for each room before it is read
        for (int k = 0; k < rc; k++)
        {
            double cells = air[k] * (double)Cell * Cell * Cell;
            double volumeGap = 0;
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

    private enum Solidity : byte { Unknown, Free, Solid, Opening }


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
    /// the axis), and, where that axis is at an angle to the grid, as much longer along its others (so two thin walls
    /// meeting at a corner meet in cells); a shape where its convex pieces, each face put out by half that thickness, hold the cell's middle;
    /// the ground where it holds the cell's middle.
    /// </summary>
    /// <summary>
    /// What fills the cells of one flood, a page (8 cells a side, on the map's grid) at a time, each page worked out once
    /// whichever block asks for it first and kept for the rest of the flood (a block that grows floods again over pages
    /// already known), and the height of the highest surface over each column.
    /// </summary>
    private sealed class PageStore
    {
        public const int Page = 8;
        public readonly TriangleWorld World;
        public readonly GeometryLayers Layers;
        public readonly Vector3 Origin;
        public readonly BarrierIndex Bars;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int, int), (Solidity[] State, Dictionary<int, int> Openings)> _pages = new();
        /// <summary>Each shape's convex pieces once asked, from the corner of the 64 m square it is asked in.</summary>
        public readonly System.Collections.Concurrent.ConcurrentDictionary<(SolidRef, int, int, int), PageRaster.Piece[]?> Pieces = new();

        /// <summary>The highest cell anything reaches: over it is the sky.</summary>
        private readonly int _topCell;

        public PageStore(TriangleWorld world, GeometryLayers layers, Vector3 origin, BarrierIndex bars)
        {
            World = world; Layers = layers; Origin = origin; Bars = bars;
            float top = origin.Y;
            foreach (ref readonly var inst in world.Instances) top = MathF.Max(top, inst.Max.Y);
            foreach (var b in bars.Bounds) top = MathF.Max(top, b.Max.Y);
            _topCell = (int)MathF.Floor((top - origin.Y) / Cell) + 1;
        }

        public (Solidity[] State, Dictionary<int, int> Openings) Get(int px, int py, int pz)
            => _pages.GetOrAdd((px, py, pz), k =>
            {
                var block = new Block(k.Item1 * Page, k.Item2 * Page, k.Item3 * Page, Page, Page, Page);
                var lo = Origin + new Vector3(block.Lx - 1, block.Ly - 1, block.Lz - 1) * Cell;
                var hi = Origin + new Vector3(block.Lx + Page + 1, block.Ly + Page + 1, block.Lz + Page + 1) * Cell;
                var raster = new PageRaster(this, block, Bars.Near(lo, hi));
                raster.Rasterise(0, 0, 0);
                return (block.State, raster.OpeningAt);
            });

        /// <summary>Whether the cell at height index <paramref name="y"/> of column (x, z) is under the open sky: nothing
        /// over it within <see cref="SkyReach"/>. What each ray finds is kept for the column (all below the surface it
        /// met is covered; all above a point found open is open), so a column is asked once or twice.</summary>
        public bool Sky(int x, int y, int z, out float coveredTo)
        {
            float h = Origin.Y + (y + 0.5f) * Cell;
            // Up the column's cells, through the pages, to the first that is solid (or an opening's), or past everything.
            coveredTo = float.NegativeInfinity;
            bool covered = false;
            static int Floor(int a) => a >= 0 ? a / Page : -((-a + Page - 1) / Page);
            int px = Floor(x), pz = Floor(z), ix = x - px * Page, iz = z - pz * Page;
            for (int yy = y + 1; yy <= _topCell && !covered; )
            {
                int py = Floor(yy);
                var (state, _) = Get(px, py, pz);
                for (int iy = yy - py * Page; iy < Page && yy <= _topCell; iy++, yy++)
                {
                    var st = state[ix + Page * (iy + Page * iz)];
                    if (st is Solidity.Solid or Solidity.Opening) { covered = true; coveredTo = Origin.Y + yy * Cell; break; }
                }
            }
            return !covered;
        }
    }

    /// <summary>A block's cells as the store has them: each page copied in the first time a cell of it is asked.</summary>
    private sealed class Raster
    {
        private const int Page = PageStore.Page;
        private readonly PageStore _store;
        private readonly Block _b;
        private readonly Dictionary<int, int> _openingAt = new();
        private readonly HashSet<(int, int, int)> _done = new();

        public Raster(PageStore store, Block block) { _store = store; _b = block; }

        /// <summary>The view of a block grown from <paramref name="was"/>'s (old sizes nx, ny; moved by g0): the pages it
        /// had copied in are in the new block already, their openings moved with them; a page is copied in whole again
        /// the first time a cell of it the old block did not hold is asked.</summary>
        public Raster(PageStore store, Block block, Raster was, int nx, int ny, int g0x, int g0y, int g0z)
        {
            _store = store; _b = block;
            foreach (var (i, id) in was._openingAt)
            {
                int x = i % nx, y = i / nx % ny, z = i / (nx * ny);
                _openingAt[(x + g0x) + block.Nx * ((y + g0y) + block.Ny * (z + g0z))] = id;
            }
        }

        public bool Blocked(int i)
        {
            var s = _b.State[i];
            if (s == Solidity.Unknown)
            {
                int x = i % _b.Nx, y = i / _b.Nx % _b.Ny, z = i / (_b.Nx * _b.Ny);
                Load(_b.Lx + x, _b.Ly + y, _b.Lz + z);
                s = _b.State[i];
            }
            return s == Solidity.Solid || s == Solidity.Opening;
        }

        private static int Floor(int a) => a >= 0 ? a / Page : -((-a + Page - 1) / Page);

        private void Load(int gx, int gy, int gz)
        {
            var key = (Floor(gx), Floor(gy), Floor(gz));
            if (!_done.Add(key)) return;
            var (state, openings) = _store.Get(key.Item1, key.Item2, key.Item3);
            int ox = key.Item1 * Page, oy = key.Item2 * Page, oz = key.Item3 * Page;
            for (int z = 0; z < Page; z++)
            {
                int bz = oz + z - _b.Lz;
                if ((uint)bz >= (uint)_b.Nz) continue;
                for (int y = 0; y < Page; y++)
                {
                    int by = oy + y - _b.Ly;
                    if ((uint)by >= (uint)_b.Ny) continue;
                    for (int x = 0; x < Page; x++)
                    {
                        int bx = ox + x - _b.Lx;
                        if ((uint)bx >= (uint)_b.Nx) continue;
                        int pi = x + Page * (y + Page * z), bi = bx + _b.Nx * (by + _b.Ny * bz);
                        _b.State[bi] = state[pi];
                        if (state[pi] == Solidity.Opening && openings.TryGetValue(pi, out int id)) _openingAt[bi] = id;
                    }
                }
            }
        }

        /// <summary>Whether the opening of this id opens to the outdoors.</summary>
        public bool OpensOutdoors(int id) => _store.Bars.Outdoors(id);

        /// <summary>Whether a cell is an opening's (the flood stops there, but it is not a surface), and whose.</summary>
        public bool IsOpening(int i, out int id)
        {
            id = 0;
            Blocked(i);
            return _b.State[i] == Solidity.Opening && _openingAt.TryGetValue(i, out id);
        }
    }

    private sealed class PageRaster
    {
        private const int Page = PageStore.Page;
        private readonly PageStore _store;
        private readonly TriangleWorld _world;
        private readonly GeometryLayers _layers;
        private readonly Block _b;
        /// <summary>The page's corner: what cells and boxes are worked from (near, so a far map keeps its precision).</summary>
        private readonly Vector3 _base;
        /// <summary>The corner of the 64 m square the page is in: what shapes' planes are worked from, shared by its pages.</summary>
        private readonly Vector3 _pieceBase;
        private readonly (int, int, int) _square;
        private readonly List<SolidRef> _refs = new();
        private Vector4[] _planes = new Vector4[64];
        private Vector3[] _corners = new Vector3[192];

        public readonly record struct Piece(Vector4[] Planes, Vector3 Lo, Vector3 Hi);

        /// <summary>The openings over the page, and which one each opening cell is.</summary>
        private readonly IReadOnlyList<Barrier> _barriers;
        public readonly Dictionary<int, int> OpeningAt = new();

        public PageRaster(PageStore store, Block block, IReadOnlyList<Barrier> barriers)
        {
            _store = store; _world = store.World; _layers = store.Layers; _b = block;
            _barriers = barriers;
            _base = store.Origin + new Vector3(block.Lx, block.Ly, block.Lz) * Cell;
            const int square = 256;
            static int Sq(int a) => a >= 0 ? a / square : -((-a + square - 1) / square);
            _square = (Sq(block.Lx), Sq(block.Ly), Sq(block.Lz));
            _pieceBase = store.Origin + new Vector3(_square.Item1, _square.Item2, _square.Item3) * (square * Cell);
        }

        public void Rasterise(int px, int py, int pz)
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
                    var key = (r, _square.Item1, _square.Item2, _square.Item3);
                    if (!_store.Pieces.TryGetValue(key, out var pieces)) _store.Pieces[key] = pieces = PiecesOf(r, thicken: !terrain);
                    // A shape with no planes to ask is taken as its box.
                    if (pieces != null) { foreach (var piece in pieces) Fill(piece, page); continue; }
                    if (terrain) continue;
                }
                Mark(centre - _base, size, rot, page, Solidity.Solid);
            }
            // The openings, in what the solids left free, a cell wider and taller than they are: where an opening's
            // side meets its jamb at an angle to the grid, a cell between the two would let the flood round it, and
            // into the jamb it changes nothing (a solid's cell stays the solid's).
            foreach (var b in _barriers)
                Mark(b.Centre - _base, b.Size + new Vector3(2f * Cell, 2f * Cell, 0f), b.Rotation, page, Solidity.Opening, b.Id);
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

        /// <summary>The cells of the page a box holds, made <paramref name="what"/>: a solid's over anything, an
        /// opening's only where nothing solid is.</summary>
        private void Mark(Vector3 centre, Vector3 size, Quaternion rot, ((int X, int Y, int Z) Lo, (int X, int Y, int Z) Hi) page,
                          Solidity what, int opening = 0)
        {
            rot = rot.LengthSquared() < 1e-6f ? Quaternion.Identity : Quaternion.Normalize(rot);
            var inv = Quaternion.Conjugate(rot);
            var half = size * 0.5f;
            static float Largest(Vector3 axis) => MathF.Max(MathF.Abs(axis.X), MathF.Max(MathF.Abs(axis.Y), MathF.Abs(axis.Z)));
            Vector3 ax = Vector3.Transform(Vector3.UnitX, rot), ay = Vector3.Transform(Vector3.UnitY, rot), az = Vector3.Transform(Vector3.UnitZ, rot);
            var grown = Vector3.Max(half, new Vector3(Largest(ax), Largest(ay), Largest(az)) * (Cell * 0.5f) + new Vector3(1e-4f));
            if (what == Solidity.Solid)
            {
                // A solid grown through a thin side at an angle to the grid is grown along its others by as much: two
                // such meeting at a corner (a timber house's 12 cm walls at 45 degrees to the grid) left a corner less
                // than a cell across between their grown faces, and a flood got out of it. Square to the grid, a thin
                // side's growth closes every corner as it is, and nothing else moves.
                float pad = 0f;
                if (grown.X > half.X && Largest(ax) < 0.999f) pad = MathF.Max(pad, grown.X - half.X);
                if (grown.Y > half.Y && Largest(ay) < 0.999f) pad = MathF.Max(pad, grown.Y - half.Y);
                if (grown.Z > half.Z && Largest(az) < 0.999f) pad = MathF.Max(pad, grown.Z - half.Z);
                if (pad > 0f)
                    grown = new Vector3(grown.X > half.X ? grown.X : grown.X + pad, grown.Y > half.Y ? grown.Y : grown.Y + pad,
                                        grown.Z > half.Z ? grown.Z : grown.Z + pad);
            }
            half = grown;
            var ext = Vector3.Abs(Vector3.Transform(new Vector3(half.X, 0, 0), rot)) + Vector3.Abs(Vector3.Transform(new Vector3(0, half.Y, 0), rot))
                      + Vector3.Abs(Vector3.Transform(new Vector3(0, 0, half.Z), rot));
            if (!Range(centre - ext, centre + ext, page, out int x0, out int x1, out int y0, out int y1, out int z0, out int z1)) return;
            for (int z = z0; z <= z1; z++)
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        int i = x + _b.Nx * (y + _b.Ny * z);
                        if (_b.State[i] == Solidity.Solid || (what == Solidity.Opening && _b.State[i] == Solidity.Opening)) continue;
                        var p = new Vector3((x + 0.5f) * Cell, (y + 0.5f) * Cell, (z + 0.5f) * Cell);
                        var l = Vector3.Abs(Vector3.Transform(p - centre, inv));
                        if (l.X > half.X || l.Y > half.Y || l.Z > half.Z) continue;
                        if (what == Solidity.Solid && _b.State[i] == Solidity.Opening) OpeningAt.Remove(i);
                        _b.State[i] = what;
                        if (what == Solidity.Opening) OpeningAt[i] = opening;
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
                int count = parts == 0 ? _world.PlanesOf(r, _pieceBase, _planes) : _world.PartPlanesOf(r, part, _pieceBase, _planes);
                if (count == 0) continue;
                int tris = parts == 0 ? _world.TrianglesOf(r, _pieceBase, _corners) : _world.PartTrianglesOf(r, part, _pieceBase, _corners);
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
            // The piece is from the square's corner; the cells from the page's.
            var off = _base - _pieceBase;
            if (!Range(piece.Lo - off, piece.Hi - off, page, out int x0, out int x1, out int y0, out int y1, out int z0, out int z1)) return;
            var planes = piece.Planes;
            for (int z = z0; z <= z1; z++)
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        int i = x + _b.Nx * (y + _b.Ny * z);
                        if (_b.State[i] == Solidity.Solid) continue;
                        if (Holds(planes, new Vector3((x + 0.5f) * Cell, (y + 0.5f) * Cell, (z + 0.5f) * Cell) + off)) _b.State[i] = Solidity.Solid;
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

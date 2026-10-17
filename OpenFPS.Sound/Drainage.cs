using OpenFPS.Common;

namespace OpenFPS.Server.Water;

/// <summary>
/// One tile's drainage, worked out once from its ground and kept with the tile (WorldTile.Drainage): which
/// way each 2 m cell drains, how deep the hollow it lies in would fill before it spilt, and what its
/// surface is to the rain. Cells row by row from the south-west, as the tile's own (TerrainTileComponent).
/// </summary>
public sealed class TileDrainage
{
    /// <summary>How it was worked out: bumped when the method changes, so stored tiles are worked out again.</summary>
    public const int CurrentMethod = 1;

    public int Method { get; set; } = CurrentMethod;
    /// <summary>Cells a side (125 for a 250 m tile).</summary>
    public int Cells { get; set; }
    public float CellMetres { get; set; } = 2f;
    /// <summary>How many cells round the tile its directions were decided from.</summary>
    public int MarginCells { get; set; }
    /// <summary>Each cell's direction, 0..7 east, north-east, north ... south-east (<see cref="Drainage.Dx"/>),
    /// or <see cref="Drainage.Sink"/>.</summary>
    public byte[] Flow { get; set; } = Array.Empty<byte>();
    /// <summary>How far under the level its hollow fills to each cell is, whole centimetres (0 on open slopes).</summary>
    public short[] FillCm { get; set; } = Array.Empty<short>();
    /// <summary>Each cell's surface (<see cref="GroundSurface"/>).</summary>
    public byte[] Surface { get; set; } = Array.Empty<byte>();
}

/// <summary>
/// Flow directions over a heightfield (docs/RUNNING_WATER.md 13.1). The hollows are filled by Priority-Flood
/// (Barnes, Lehman and Mulla 2014, Computers and Geosciences 62, 117: the window's edge is the outlet, cells
/// taken lowest first, a plain queue for the ones under the water level), recording for each cell the
/// neighbour that reached it. A cell with a lower neighbour on the filled surface drains to its steepest
/// (D8, O'Callaghan and Mark 1984); a cell on a flat or in a filled hollow drains to the neighbour that
/// reached it, back along the flood toward the hollow's spill point. Steepest descent always falls and the
/// flood's own links follow the order cells were taken in, so nothing within a window drains in a circle.
/// </summary>
public static class Drainage
{
    /// <summary>The eight directions, east first and anticlockwise: x east, z north.</summary>
    public static readonly int[] Dx = { 1, 1, 0, -1, -1, -1, 0, 1 };
    public static readonly int[] Dz = { 0, 1, 1, 1, 0, -1, -1, -1 };
    /// <summary>A cell with nowhere to drain: water stays there (only a window's own edge, or a lake).</summary>
    public const byte Sink = 8;

    public static float StepMetres(byte dir, float cell) => dir >= Sink ? 0f : (dir & 1) == 1 ? cell * 1.41421356f : cell;

    /// <summary>The heights of a grid's cells, each the mean of its four corner posts.</summary>
    public static float[] CellHeights(float[] posts, int postsX, int postsZ)
    {
        int cx = postsX - 1, cz = postsZ - 1;
        var h = new float[cx * cz];
        for (int j = 0; j < cz; j++)
            for (int i = 0; i < cx; i++)
            {
                int k = j * postsX + i;
                h[j * cx + i] = 0.25f * (posts[k] + posts[k + 1] + posts[k + postsX] + posts[k + postsX + 1]);
            }
        return h;
    }

    /// <summary>
    /// Routes a window of cells (<paramref name="nx"/> by <paramref name="nz"/>, row by row from the south-west)
    /// and returns the directions and hollow depths of the part of it from (<paramref name="ox"/>,
    /// <paramref name="oz"/>), <paramref name="ownX"/> by <paramref name="ownZ"/> cells: the tile in the middle of
    /// its margin. Water reaching the window's edge leaves it.
    /// </summary>
    public static (byte[] Flow, short[] FillCm) Route(float[] heights, int nx, int nz, int ox, int oz, int ownX, int ownZ, float cellMetres)
    {
        int n = nx * nz;
        var filled = new float[n];
        var parent = new byte[n];
        var seen = new bool[n];
        var open = new PriorityQueue<int, (float H, int Seq)>(Math.Max(16, 2 * (nx + nz)), Order.Instance);
        var pit = new Queue<int>();
        int seq = 0;
        float H(int k) => float.IsFinite(heights[k]) ? heights[k] : 0f;

        void Seed(int i, int j)
        {
            int k = j * nx + i;
            if (seen[k]) return;
            seen[k] = true;
            filled[k] = H(k);
            parent[k] = Sink;
            open.Enqueue(k, (filled[k], seq++));
        }
        for (int i = 0; i < nx; i++) { Seed(i, 0); Seed(i, nz - 1); }
        for (int j = 0; j < nz; j++) { Seed(0, j); Seed(nx - 1, j); }

        while (pit.Count > 0 || open.Count > 0)
        {
            int c = pit.Count > 0 ? pit.Dequeue() : open.Dequeue();
            int ci = c % nx, cj = c / nx;
            float fc = filled[c];
            for (byte d = 0; d < 8; d++)
            {
                int i = ci + Dx[d], j = cj + Dz[d];
                if (i < 0 || j < 0 || i >= nx || j >= nz) continue;
                int k = j * nx + i;
                if (seen[k]) continue;
                seen[k] = true;
                parent[k] = (byte)((d + 4) & 7);
                float h = H(k);
                if (h <= fc) { filled[k] = fc; pit.Enqueue(k); }
                else { filled[k] = h; open.Enqueue(k, (h, seq++)); }
            }
        }

        var flow = new byte[ownX * ownZ];
        var fill = new short[ownX * ownZ];
        for (int j = 0; j < ownZ; j++)
            for (int i = 0; i < ownX; i++)
            {
                int wi = ox + i, wj = oz + j, c = wj * nx + wi;
                float fc = filled[c];
                int best = -1;
                float bestSlope = 0f;
                for (int d = 0; d < 8; d++)
                {
                    int a = wi + Dx[d], b = wj + Dz[d];
                    if (a < 0 || b < 0 || a >= nx || b >= nz) continue;
                    float drop = fc - filled[b * nx + a];
                    if (drop <= 0f) continue;
                    float s = (d & 1) == 1 ? drop * 0.70710678f : drop;
                    if (s > bestSlope) { bestSlope = s; best = d; }
                }
                int o = j * ownX + i;
                flow[o] = best >= 0 ? (byte)best : parent[c];
                fill[o] = (short)Math.Clamp(MathF.Round((fc - H(c)) * 100f), 0f, short.MaxValue);
            }
        return (flow, fill);
    }

    /// <summary>
    /// A tile's drainage from a window of posts (<paramref name="postsX"/> by <paramref name="postsZ"/>, 2 m
    /// apart, row by row from the south-west) whose tile starts <paramref name="marginCells"/> cells in from the
    /// west and the south and is <paramref name="cells"/> cells a side, with its cells' surfaces.
    /// </summary>
    public static TileDrainage OfWindow(float[] posts, int postsX, int postsZ, int marginX, int marginZ, int cells,
                                        float cellMetres, byte[] surfaces, int marginCells)
    {
        var h = CellHeights(posts, postsX, postsZ);
        var (flow, fill) = Route(h, postsX - 1, postsZ - 1, marginX, marginZ, cells, cells, cellMetres);
        return new TileDrainage
        {
            Cells = cells, CellMetres = cellMetres, MarginCells = marginCells,
            Flow = flow, FillCm = fill, Surface = surfaces,
        };
    }

    /// <summary>Route the whole grid at once (<see cref="OfGrid"/>): the margin is the whole place.</summary>
    public const int WholeGrid = -1;

    /// <summary>
    /// The drainage of every tile of a grid of posts laid as whole tiles (a map's ground: TerrainBuilder.Lay):
    /// each from a window of <paramref name="marginCells"/> round it cut from the grid (held at the grid's edge),
    /// or, with <see cref="WholeGrid"/>, the whole grid routed at once and cut into tiles, as a map's ground is
    /// graded at once so its tiles' edges agree.
    /// </summary>
    public static TileDrainage[,] OfGrid(float[] posts, int postsX, int postsZ, int cellsPerTile, float cellMetres,
                                         Func<int, int, byte[]> surfacesOf, int marginCells)
    {
        int tilesX = (postsX - 1) / cellsPerTile, tilesZ = (postsZ - 1) / cellsPerTile;
        var cells = CellHeights(posts, postsX, postsZ);
        int cx = postsX - 1, cz = postsZ - 1;
        var result = new TileDrainage[tilesX, tilesZ];
        if (marginCells == WholeGrid)
        {
            var (all, allFill) = Route(cells, cx, cz, 0, 0, cx, cz, cellMetres);
            System.Threading.Tasks.Parallel.For(0, tilesX * tilesZ, t =>
            {
                int tx = t % tilesX, tz = t / tilesX;
                var flow = new byte[cellsPerTile * cellsPerTile];
                var fill = new short[cellsPerTile * cellsPerTile];
                for (int j = 0; j < cellsPerTile; j++)
                {
                    Array.Copy(all, (tz * cellsPerTile + j) * cx + tx * cellsPerTile, flow, j * cellsPerTile, cellsPerTile);
                    Array.Copy(allFill, (tz * cellsPerTile + j) * cx + tx * cellsPerTile, fill, j * cellsPerTile, cellsPerTile);
                }
                result[tx, tz] = new TileDrainage
                {
                    Cells = cellsPerTile, CellMetres = cellMetres, MarginCells = Math.Max(cx, cz),
                    Flow = flow, FillCm = fill, Surface = surfacesOf(tx, tz),
                };
            });
            return result;
        }
        System.Threading.Tasks.Parallel.For(0, tilesX * tilesZ, t =>
        {
            int tx = t % tilesX, tz = t / tilesX;
            int i0 = tx * cellsPerTile - marginCells, j0 = tz * cellsPerTile - marginCells;
            int lo = Math.Max(0, i0), lz = Math.Max(0, j0);
            int hi = Math.Min(cx, i0 + cellsPerTile + 2 * marginCells), hz = Math.Min(cz, j0 + cellsPerTile + 2 * marginCells);
            int nx = hi - lo, nz = hz - lz;
            var w = new float[nx * nz];
            for (int j = 0; j < nz; j++) Array.Copy(cells, (lz + j) * cx + lo, w, j * nx, nx);
            var (flow, fill) = Route(w, nx, nz, tx * cellsPerTile - lo, tz * cellsPerTile - lz, cellsPerTile, cellsPerTile, cellMetres);
            result[tx, tz] = new TileDrainage
            {
                Cells = cellsPerTile, CellMetres = cellMetres, MarginCells = marginCells,
                Flow = flow, FillCm = fill, Surface = surfacesOf(tx, tz),
            };
        });
        return result;
    }

    /// <summary>Lower first, then the order cells were reached in: the same flood whatever the queue does with ties.</summary>
    private sealed class Order : IComparer<(float H, int Seq)>
    {
        public static readonly Order Instance = new();
        public int Compare((float H, int Seq) a, (float H, int Seq) b)
        {
            int c = a.H.CompareTo(b.H);
            return c != 0 ? c : a.Seq.CompareTo(b.Seq);
        }
    }
}

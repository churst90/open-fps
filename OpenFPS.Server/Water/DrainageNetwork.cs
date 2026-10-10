using System.Numerics;
using OpenFPS.Common;

namespace OpenFPS.Server.Water;

/// <summary>
/// A map's drainage, put together from its tiles' (docs/RUNNING_WATER.md section 13): every cell's way
/// downhill joined across tile edges, how much ground drains through each cell, which hollows keep water
/// (ponds and puddles) and which spill at once (filled, or held by a road with a culvert through it), how long
/// water takes to reach each place, and the drainage lines where it could be heard, cut into the stretches a
/// voice stands for. Built once when the tiles are known; read by GroundWaterSystem every tick.
///
/// <para>Joining tiles is Barnes (2017)'s way for tiled grids (Environmental Modelling and Software 92, 202):
/// each tile's directions are its own, decided from its margin; the map links each cell on an edge to the cell
/// it drains to in the next tile and accumulates over the whole. Where two tiles' margins saw a flat or a
/// hollow differently, a pair of edge cells can drain into each other; the network finds such loops and lets
/// the lowest cell of each hold the water (<see cref="LoopsBroken"/>, counted).</para>
/// </summary>
public sealed class DrainageNetwork
{
    /// <summary>A tile of ground: where it is on the map's tile grid, its south-west corner (the map's metres),
    /// its cells' heights (the map's y), and its drainage.</summary>
    public sealed record TileInput(int X, int Z, float CornerX, float CornerZ, float[] CellHeights, TileDrainage Drainage);

    // ── The thresholds, as data ─────────────────────────────────────────────────────────────────

    /// <summary>Where a drainage line starts, m² of ground draining through: half a hectare. Less is a slope
    /// the rain runs off as a sheet and in rills too small and too brief to place a voice on.</summary>
    public float LineSquareMetres { get; init; } = 5000f;

    /// <summary>Where water runs in a channel of its own rather than as shallow concentrated flow, for its
    /// travel time (TR-55 ch. 3: where the channel shows), m²: five hectares. A judgement.</summary>
    public float ChannelSquareMetres { get; init; } = 5e4f;

    /// <summary>Where a line is a creek with a bed of its own rather than a rill or a ditch, m²: 20 hectares.
    /// A judgement.</summary>
    public float CreekSquareMetres { get; init; } = 2e5f;

    /// <summary>A hollow keeps water as a pond when it is at least this deep and holds this much, m and m³;
    /// smaller hollows are filled, their storage being within the curve number's initial abstraction (TR-55
    /// ch. 2: Ia includes "surface depression storage") and the survey's own noise of a few centimetres.</summary>
    public float PondDepthMetres { get; init; } = 0.03f;
    public float PondCubicMetres { get; init; } = 1f;

    /// <summary>A hollow on a paved surface keeps water at this depth and up, m: a puddle (docs/WET_ROADS.md:
    /// 6 to 25 mm deep when full).</summary>
    public float PuddleDepthMetres { get; init; } = 0.01f;

    /// <summary>No voice is placed within this of running water a map has placed by hand (a creek, a gutter,
    /// a fountain), m: that water is already there, as the map made it.</summary>
    public float PlacedWaterClearMetres { get; init; } = 15f;

    /// <summary>A line whose reference flow (GroundChannels.ReferenceFlow) is less than this gets no voice,
    /// L/s.</summary>
    public float VoiceReferenceLitresPerSecond { get; init; } = 0.05f;

    // ── What it is made of ──────────────────────────────────────────────────────────────────────

    public int CellsPerTile { get; private set; }
    public float CellMetres { get; private set; }
    public float CellArea => CellMetres * CellMetres;
    public int CellCount { get; private set; }
    public IReadOnlyList<TileInput> Tiles => _tiles;

    private TileInput[] _tiles = Array.Empty<TileInput>();
    private readonly Dictionary<(int X, int Z), int> _slot = new();
    private int[] _next = Array.Empty<int>();
    private int[] _area = Array.Empty<int>();
    private float[] _height = Array.Empty<float>();
    private byte[] _surface = Array.Empty<byte>();
    private float[] _tc = Array.Empty<float>();
    private int[] _order = Array.Empty<int>();
    private int[] _pondOf = Array.Empty<int>();
    private readonly Dictionary<int, float[]> _lineAreas = new();
    private readonly Dictionary<int, float[]> _lineLags = new();
    private float[] _toEnd = Array.Empty<float>();
    private readonly Dictionary<int, List<int>> _pondsAt = new();
    private readonly List<Pond> _ponds = new();
    private readonly List<LineInfo> _lines = new();
    private readonly List<Voice> _voices = new();

    public const int Leaves = -1, Holds = -2;

    /// <summary>Loops between tiles' edge cells found and broken.</summary>
    public int LoopsBroken { get; private set; }
    /// <summary>Hollows found, and of them how many keep water, and how many are held by a road and drain
    /// through its culvert.</summary>
    public int Hollows { get; private set; }
    public int Culverts { get; private set; }
    public TimeSpan BuildTime { get; private set; }

    /// <summary>A hollow that keeps water: its cells, how much it holds full, what drains to it.</summary>
    public sealed class Pond
    {
        public int Id;
        /// <summary>Its cells, lowest first, with their heights; the level it spills at.</summary>
        public int[] Cells = Array.Empty<int>();
        public float[] Heights = Array.Empty<float>();
        public float SpillLevel;
        public float CapacityCubicMetres;
        /// <summary>The ground whose water it takes before anything passes it, by surface, m² (not through
        /// another pond), and the ponds that overflow into it.</summary>
        public float[] Catchment = new float[GroundHydrology.Surfaces];
        /// <summary>The same with every pond above it included: what drains to it in all, m².</summary>
        public float[] Total = new float[GroundHydrology.Surfaces];
        public List<int> Upstream = new();
        /// <summary>The mean time each surface's water takes to reach it, s.</summary>
        public float[] Lags = new float[GroundHydrology.Surfaces];
        /// <summary>What it takes from the rain, as a catchment of the map's water (no base flow: the groundwater
        /// comes out into the channels below, GroundCatchment.BaseSquareMetres).</summary>
        public GroundCatchment RainCatchment => _rain ??= new(Catchment, Lags, Array.Empty<int>(), 0f);
        private GroundCatchment? _rain;
        /// <summary>All the ground draining to it, m².</summary>
        public float TotalSquareMetres => Total.Sum();
        /// <summary>The surface of its lowest cell: a puddle on a road, or a pond on open ground.</summary>
        public GroundSurface Bottom;
        /// <summary>Where it overflows to (a cell), or <see cref="Leaves"/>/<see cref="Holds"/>.</summary>
        public int SpillsTo;
        /// <summary>Its place in the order ponds pass water down: upstream first.</summary>
        public int Rank;
        public bool IsPuddle => GroundHydrology.Road(Bottom);

        /// <summary>The volume it holds with its water at <paramref name="level"/>, m³.</summary>
        public float VolumeAt(float level, float cellArea)
        {
            float v = 0f;
            foreach (float h in Heights) { if (h >= level) break; v += (level - h) * cellArea; }
            return v;
        }

        /// <summary>The level its water stands at holding <paramref name="cubicMetres"/>, and the area wet.</summary>
        public (float Level, float WetSquareMetres) LevelOf(float cubicMetres, float cellArea)
        {
            if (Heights.Length == 0 || cubicMetres <= 0f) return (Heights.Length > 0 ? Heights[0] : 0f, 0f);
            float v = 0f;
            for (int k = 0; k < Heights.Length; k++)
            {
                float next = k + 1 < Heights.Length ? MathF.Min(Heights[k + 1], SpillLevel) : SpillLevel;
                float add = (next - Heights[k]) * (k + 1) * cellArea;
                if (v + add >= cubicMetres || k + 1 == Heights.Length || Heights[k + 1] >= SpillLevel)
                {
                    float level = Heights[k] + (cubicMetres - v) / ((k + 1) * cellArea);
                    return (MathF.Min(level, SpillLevel), (k + 1) * cellArea);
                }
                v += add;
            }
            return (SpillLevel, Heights.Length * cellArea);
        }
    }

    /// <summary>A drainage line: its cells from its head downstream to where it joins a bigger one or ends.</summary>
    public sealed class LineInfo
    {
        public int[] Cells = Array.Empty<int>();
        public float LengthMetres;
        /// <summary>Ground draining through its last cell, m².</summary>
        public float SquareMetres;
    }

    /// <summary>A stretch of a line that is heard: where its voice stands, which way the line runs, and what it is.</summary>
    public sealed record Voice(Vector3 Position, float Yaw, GroundChannelKind Kind, float WidthMetres, float Slope, float LengthMetres,
                               float ReferenceLitresPerSecond, GroundCatchment Catchment, int Cell, float SquareMetres)
    {
        public string SoundId => "flow:" + GroundChannels.Key(Kind, WidthMetres, Slope, LengthMetres, ReferenceLitresPerSecond, Catchment);
    }

    public IReadOnlyList<Pond> Ponds => _ponds;
    public IReadOnlyList<LineInfo> Lines => _lines;
    public IReadOnlyList<Voice> Voices => _voices;

    // ── Building ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The network of these tiles. <paramref name="placedWater"/>: where the map already has running water of
    /// its own (no voice near it).
    /// </summary>
    public DrainageNetwork Build(IEnumerable<TileInput> tiles, IEnumerable<Vector3>? placedWater = null)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        _tiles = tiles.OrderBy(t => t.X).ThenBy(t => t.Z).ToArray();
        if (_tiles.Length == 0) return this;
        CellsPerTile = _tiles[0].Drainage.Cells;
        CellMetres = _tiles[0].Drainage.CellMetres;
        int per = CellsPerTile * CellsPerTile;
        _slot.Clear();
        for (int s = 0; s < _tiles.Length; s++) _slot[(_tiles[s].X, _tiles[s].Z)] = s;
        CellCount = _tiles.Length * per;
        _next = new int[CellCount];
        _height = new float[CellCount];
        _surface = new byte[CellCount];
        var fill = new short[CellCount];
        for (int s = 0; s < _tiles.Length; s++)
        {
            var t = _tiles[s];
            Array.Copy(t.CellHeights, 0, _height, s * per, per);
            Array.Copy(t.Drainage.Surface, 0, _surface, s * per, Math.Min(per, t.Drainage.Surface.Length));
            Array.Copy(t.Drainage.FillCm, 0, fill, s * per, Math.Min(per, t.Drainage.FillCm.Length));
            for (int k = 0; k < per; k++)
            {
                int c = s * per + k;
                byte d = k < t.Drainage.Flow.Length ? t.Drainage.Flow[k] : Drainage.Sink;
                _next[c] = _surface[c] == (byte)GroundSurface.Water ? Holds : d >= Drainage.Sink ? Holds : Neighbour(c, d);
            }
        }
        // A cell with nowhere to drain on the known ground's outer edge drains off it (its window's edge was the
        // outlet); only one inside holds its water.
        for (int c = 0; c < CellCount; c++)
        {
            if (_next[c] != Holds || _surface[c] == (byte)GroundSurface.Water) continue;
            for (int d = 0; d < 8; d++)
                if (Neighbour(c, d) == Leaves) { _next[c] = Leaves; break; }
        }

        BreakLoops();
        FindHollows(fill);
        Accumulate();
        FindLines(placedWater?.ToList() ?? new List<Vector3>());
        // Only the building needs these: tens of megabytes on a town.
        _order = Array.Empty<int>();
        _toEnd = Array.Empty<float>();
        BuildTime = clock.Elapsed;
        return this;
    }

    /// <summary>The cell <paramref name="dir"/> from a cell, across a tile edge if need be; <see cref="Leaves"/>
    /// if that tile is not here.</summary>
    public int Neighbour(int cell, int dir)
    {
        int per = CellsPerTile * CellsPerTile, s = cell / per, k = cell % per;
        int i = k % CellsPerTile + Drainage.Dx[dir], j = k / CellsPerTile + Drainage.Dz[dir];
        int tx = _tiles[s].X, tz = _tiles[s].Z;
        if (i < 0) { tx--; i += CellsPerTile; } else if (i >= CellsPerTile) { tx++; i -= CellsPerTile; }
        if (j < 0) { tz--; j += CellsPerTile; } else if (j >= CellsPerTile) { tz++; j -= CellsPerTile; }
        if (tx != _tiles[s].X || tz != _tiles[s].Z)
        {
            if (!_slot.TryGetValue((tx, tz), out s)) return Leaves;
        }
        return s * per + j * CellsPerTile + i;
    }

    /// <summary>Kahn's order (every cell after all that drain into it); loops are found as what it never reaches
    /// and broken at their lowest cell.</summary>
    private void BreakLoops()
    {
        LoopsBroken = 0;
        while (true)
        {
            var indeg = new int[CellCount];
            for (int c = 0; c < CellCount; c++) if (_next[c] >= 0) indeg[_next[c]]++;
            var order = new int[CellCount];
            int head = 0, tail = 0;
            for (int c = 0; c < CellCount; c++) if (indeg[c] == 0) order[tail++] = c;
            while (head < tail)
            {
                int c = order[head++], n = _next[c];
                if (n >= 0 && --indeg[n] == 0) order[tail++] = n;
            }
            if (tail == CellCount) { _order = order; return; }
            // What is left is loops and what drains into them; walk from each to its loop.
            var mark = new int[CellCount];
            int pass = 0;
            for (int c = 0; c < CellCount; c++)
            {
                if (indeg[c] == 0 || mark[c] != 0) continue;
                pass++;
                int x = c;
                while (x >= 0 && mark[x] == 0 && indeg[x] > 0) { mark[x] = pass; x = _next[x]; }
                if (x < 0 || mark[x] != pass) continue;
                // x is on a loop: hold the water at its lowest cell.
                int low = x, y = _next[x];
                while (y != x) { if (_height[y] < _height[low]) low = y; y = _next[y]; }
                _next[low] = Holds;
                LoopsBroken++;
            }
        }
    }

    /// <summary>
    /// The hollows: cells whose fill is over nothing, joined across tile edges. One a road holds back (its
    /// spill is on a road and its bottom is not) drains through the road's culvert; a deep enough one, or one on
    /// paving, keeps its water as a pond or a puddle; the rest are filled.
    /// </summary>
    private void FindHollows(short[] fill)
    {
        _pondOf = new int[CellCount];
        Array.Fill(_pondOf, -1);
        _ponds.Clear();
        Hollows = Culverts = 0;
        var label = new int[CellCount];
        Array.Fill(label, -1);
        var stack = new Stack<int>();
        var members = new List<int>();
        int id = 0;
        for (int c0 = 0; c0 < CellCount; c0++)
        {
            if (fill[c0] <= 0 || label[c0] >= 0) continue;
            members.Clear();
            label[c0] = id;
            stack.Push(c0);
            while (stack.Count > 0)
            {
                int c = stack.Pop();
                members.Add(c);
                for (int d = 0; d < 8; d++)
                {
                    int n = Neighbour(c, d);
                    if (n < 0 || label[n] >= 0 || fill[n] <= 0) continue;
                    label[n] = id;
                    stack.Push(n);
                }
            }
            Hollows++;
            int pit = members[0];
            float maxFill = 0f, volume = 0f;
            foreach (int m in members)
            {
                if (_height[m] < _height[pit]) pit = m;
                maxFill = MathF.Max(maxFill, fill[m] * 0.01f);
                volume += fill[m] * 0.01f * CellArea;
            }
            // Where it spills: the first cell outside it that its water reaches.
            int spill = Holds, exit = -1;
            foreach (int m in members)
            {
                int n = _next[m];
                if (n >= 0 && label[n] == id) continue;
                exit = m; spill = n;
                break;
            }
            var bottom = (GroundSurface)_surface[pit];
            bool culvert = spill >= 0 && GroundHydrology.Road((GroundSurface)_surface[spill]) && !GroundHydrology.Road(bottom);
            bool keep = !culvert && ((maxFill >= PondDepthMetres && volume >= PondCubicMetres)
                                     || (GroundHydrology.Road(bottom) && maxFill >= PuddleDepthMetres));
            if (culvert) Culverts++;
            id++;
            if (!keep) continue;
            var cells = members.OrderBy(m => _height[m]).ThenBy(m => m).ToArray();
            var pond = new Pond
            {
                Id = _ponds.Count,
                Cells = cells,
                Heights = cells.Select(m => _height[m]).ToArray(),
                Bottom = bottom,
                SpillsTo = spill,
            };
            pond.SpillLevel = cells.Max(m => _height[m] + fill[m] * 0.01f);
            pond.CapacityCubicMetres = pond.VolumeAt(pond.SpillLevel, CellArea);
            foreach (int m in cells) _pondOf[m] = pond.Id;
            _ponds.Add(pond);
            _ = exit;
        }
    }

    /// <summary>Ground draining through each cell, its time of concentration, and each surface's ground not
    /// through a pond at every line cell and every pond.</summary>
    private void Accumulate()
    {
        _area = new int[CellCount];
        _tc = new float[CellCount];
        var cross = new float[CellCount];
        var tin = new float[CellCount];
        var length = new float[CellCount];
        for (int c = 0; c < CellCount; c++) _area[c] = 1;
        float lineCells = LineSquareMetres / CellArea, channelCells = ChannelSquareMetres / CellArea;
        foreach (int c in _order)
        {
            int n = _next[c];
            byte d = DirOf(c);
            float step = d < Drainage.Sink ? Drainage.StepMetres(d, CellMetres) : CellMetres;
            float slope = n >= 0 ? MathF.Max(0.002f, (_height[c] - _height[n]) / step) : 0.002f;
            var surface = (GroundSurface)_surface[c];
            float dt;
            if (length[c] < GroundHydrology.SheetFlowMetres)
                dt = GroundHydrology.SheetSeconds(length[c] + step, slope, surface) - GroundHydrology.SheetSeconds(length[c], slope, surface);
            else if (_area[c] < channelCells)
                dt = step / GroundHydrology.ShallowSpeed(slope, GroundHydrology.Paved(surface));
            else
                dt = step / ChannelSpeed(_area[c] * CellArea, slope);
            cross[c] = MathF.Max(0f, dt);
            _tc[c] = tin[c] + cross[c];
            if (n < 0) continue;
            _area[n] += _area[c];
            if (_tc[c] > tin[n]) tin[n] = _tc[c];
            float l = length[c] + step;
            if (l > length[n]) length[n] = l;
        }

        // How long water takes from each cell to where the network ends: so the time from a cell to any place
        // below it is the difference of the two.
        _toEnd = new float[CellCount];
        for (int k = _order.Length - 1; k >= 0; k--)
        {
            int c = _order[k], n = _next[c];
            _toEnd[c] = cross[c] + (n >= 0 ? _toEnd[n] : 0f);
        }

        // Each surface's ground at the line cells and into the ponds, and the mean time its water takes to get
        // there: one pass a surface, a pond taking what reaches it.
        _lineAreas.Clear();
        _lineLags.Clear();
        foreach (var p in _ponds) { Array.Clear(p.Catchment); Array.Clear(p.Total); Array.Clear(p.Lags); p.Upstream.Clear(); }
        var val = new float[CellCount];
        var timed = new double[CellCount];
        for (int s = 0; s < GroundHydrology.Surfaces; s++)
        {
            for (int c = 0; c < CellCount; c++)
            {
                val[c] = _surface[c] == s ? CellArea : 0f;
                timed[c] = val[c] * (double)_toEnd[c];
            }
            foreach (int c in _order)
            {
                int n = _next[c], p = _pondOf[c];
                if (_area[c] >= lineCells)
                {
                    if (!_lineAreas.TryGetValue(c, out var a)) { _lineAreas[c] = a = new float[GroundHydrology.Surfaces]; _lineLags[c] = new float[GroundHydrology.Surfaces]; }
                    a[s] = val[c];
                    _lineLags[c][s] = val[c] > 0f ? (float)(timed[c] / val[c]) - _toEnd[c] + cross[c] : 0f;
                }
                if (p >= 0 && (n < 0 || _pondOf[n] != p))
                {
                    _ponds[p].Catchment[s] += val[c];
                    _ponds[p].Lags[s] += (float)(timed[c] - val[c] * (double)(_toEnd[c] - cross[c]));
                    continue;
                }
                if (n >= 0) { val[n] += val[c]; timed[n] += timed[c]; }
            }
        }
        foreach (var p in _ponds)
            for (int s = 0; s < GroundHydrology.Surfaces; s++)
                p.Lags[s] = p.Catchment[s] > 0f ? MathF.Max(0f, p.Lags[s] / p.Catchment[s]) : 0f;

        // Which ponds overflow into which, and their order: a pond's overflow runs down from where it spills
        // until it reaches another pond.
        var rank = new int[CellCount];
        for (int k = 0; k < _order.Length; k++) rank[_order[k]] = k;
        foreach (var p in _ponds)
        {
            int last = p.Cells[0];
            foreach (int c in p.Cells) if (rank[c] > rank[last]) last = c;
            p.Rank = rank[last];
            int x = p.SpillsTo, guard = 0;
            while (x >= 0 && guard++ < CellCount)
            {
                if (_pondOf[x] >= 0 && _pondOf[x] != p.Id) { _ponds[_pondOf[x]].Upstream.Add(p.Id); break; }
                x = _next[x];
            }
        }
        foreach (var p in _ponds.OrderBy(p => p.Rank))
        {
            for (int s = 0; s < GroundHydrology.Surfaces; s++) p.Total[s] = p.Catchment[s];
            foreach (int u in p.Upstream)
                for (int s = 0; s < GroundHydrology.Surfaces; s++) p.Total[s] += _ponds[u].Total[s];
        }
    }

    /// <summary>The speed of water in a channel draining this much ground in the reference rain, m/s: Manning in a
    /// wide channel, v = q^0.4 S^0.3 / n^0.6 with q = Q / w, its width from hydraulic geometry (GroundChannels).</summary>
    private static float ChannelSpeed(float squareMetres, float slope)
    {
        float q = squareMetres * 10f / 3.6e6f;   // 10 mm/h running off: the order of a heavy storm's run-off
        float w = Math.Clamp(3f * MathF.Sqrt(q), 0.3f, 8f);
        float n = GroundChannels.ManningN(GroundChannelKind.Creek);
        float y = MathF.Pow(q / w * n / MathF.Sqrt(slope), 0.6f);
        return MathF.Max(0.05f, q / w / MathF.Max(1e-4f, y));
    }

    private byte DirOf(int c)
    {
        int per = CellsPerTile * CellsPerTile;
        var dr = _tiles[c / per].Drainage;
        int k = c % per;
        return k < dr.Flow.Length ? dr.Flow[k] : Drainage.Sink;
    }

    /// <summary>The drainage lines, and the voices along them.</summary>
    private void FindLines(List<Vector3> placed)
    {
        _lines.Clear();
        _voices.Clear();
        float lineCells = LineSquareMetres / CellArea;
        var isLine = new bool[CellCount];
        for (int c = 0; c < CellCount; c++) isLine[c] = _area[c] >= lineCells;
        // A cell's line donors among the line cells only.
        var lineDonors = new int[CellCount];
        for (int c = 0; c < CellCount; c++)
            if (isLine[c] && _next[c] >= 0 && isLine[_next[c]]) lineDonors[_next[c]]++;
        var taken = new bool[CellCount];
        // Heads first in a fixed order, the biggest lines last so each runs on until it meets another.
        var heads = Enumerable.Range(0, CellCount).Where(c => isLine[c] && lineDonors[c] == 0).ToList();
        foreach (int h in heads)
        {
            var cells = new List<int>();
            int x = h;
            while (x >= 0 && isLine[x] && !taken[x])
            {
                taken[x] = true;
                cells.Add(x);
                x = _next[x];
            }
            // Joined at a confluence: carry on into the cell it joins, so the stretch reaches the bigger line.
            if (x >= 0 && taken[x]) cells.Add(x);
            if (cells.Count < 2) continue;
            float len = 0f;
            for (int k = 1; k < cells.Count; k++) len += Vector2.Distance(Centre2(cells[k - 1]), Centre2(cells[k]));
            _lines.Add(new LineInfo { Cells = cells.ToArray(), LengthMetres = len, SquareMetres = _area[cells[^1]] * CellArea });
        }

        // The ponds that overflow straight into each line cell (and the cell each spills onto): walked down
        // from each pond's spill until another pond takes it.
        _pondsAt.Clear();
        foreach (var p in _ponds)
        {
            int x = p.SpillsTo, guard = 0;
            while (x >= 0 && guard++ < CellCount && _pondOf[x] < 0)
            {
                if (isLine[x] || x == p.SpillsTo)
                {
                    if (!_pondsAt.TryGetValue(x, out var list)) _pondsAt[x] = list = new List<int>();
                    if (!list.Contains(p.Id)) list.Add(p.Id);
                }
                x = _next[x];
            }
        }

        foreach (var line in _lines) PlaceVoices(line, placed, _pondsAt);
    }

    private void PlaceVoices(LineInfo line, List<Vector3> placed, Dictionary<int, List<int>> pondsAt)
    {
        var cells = line.Cells;
        var along = new float[cells.Length];
        for (int k = 1; k < cells.Length; k++) along[k] = along[k - 1] + Vector2.Distance(Centre2(cells[k - 1]), Centre2(cells[k]));
        float seg = GroundChannels.SegmentMetres;
        int pieces = Math.Max(1, (int)MathF.Floor(along[^1] / seg));
        if (along[^1] < 0.5f * seg) return;
        for (int p = 0; p < pieces; p++)
        {
            float a0 = p * seg, a1 = p == pieces - 1 ? along[^1] : (p + 1) * seg;
            int k0 = Array.FindIndex(along, v => v >= a0), k1 = Array.FindLastIndex(along, v => v <= a1);
            if (k0 < 0 || k1 <= k0) continue;
            int mid = cells[(k0 + k1) / 2];
            // Water standing there already, or a lake: no running water to hear.
            if (_pondOf[mid] >= 0 || _surface[mid] == (byte)GroundSurface.Water) continue;
            var c0 = Centre(cells[k0]);
            var c1 = Centre(cells[k1]);
            var centre = Centre(mid);
            if (placed.Any(w => Vector2.Distance(new Vector2(w.X, w.Z), new Vector2(centre.X, centre.Z)) < PlacedWaterClearMetres)) continue;
            float length = MathF.Max(1f, a1 - a0);
            float slope = MathF.Max(0.002f, (_height[cells[k0]] - _height[cells[k1]]) / length);
            var catchment = CatchmentOf(mid);
            var areas = catchment.AreaSquareMetres;
            var ponds = catchment.Ponds;
            // Its bed is shaped by everything that drains through it, the ponds' ground included.
            var total = (float[])areas.Clone();
            foreach (int id in ponds)
                for (int s = 0; s < total.Length; s++) total[s] += _ponds[id].Total[s];
            float reference = GroundChannels.ReferenceFlow(new GroundCatchment(total, 0f));
            if (reference < VoiceReferenceLitresPerSecond) continue;
            var kind = KindOf(cells, k0, k1, _area[mid] * CellArea);
            float width = GroundChannels.WidthFor(kind, reference);
            var dir = new Vector2(c1.X - c0.X, c1.Z - c0.Z);
            float yaw = dir.LengthSquared() > 1e-6f ? MathF.Atan2(dir.X, dir.Y) : 0f;
            _voices.Add(new Voice(centre + new Vector3(0f, 0.05f, 0f), yaw, kind, width, slope, length, reference, catchment, mid, _area[mid] * CellArea));
        }
    }

    /// <summary>What a stretch is: a creek where enough drains through it; water running over paving where most
    /// of it is sealed; a ditch where most of it runs beside a road (within 6 m); a rivulet otherwise.</summary>
    private GroundChannelKind KindOf(int[] cells, int k0, int k1, float squareMetres)
    {
        if (squareMetres >= CreekSquareMetres) return GroundChannelKind.Creek;
        int sealedCount = 0, besideRoad = 0, count = k1 - k0 + 1;
        for (int k = k0; k <= k1; k++)
        {
            int c = cells[k];
            if (_surface[c] == (byte)GroundSurface.Impervious) { sealedCount++; continue; }
            if (NearRoad(c, 3)) besideRoad++;
        }
        if (sealedCount * 10 >= count * 6) return GroundChannelKind.Runnel;
        if (besideRoad * 2 >= count) return GroundChannelKind.Ditch;
        return GroundChannelKind.Rill;
    }

    private bool NearRoad(int c, int reach)
    {
        var p = Centre2(c);
        for (int dj = -reach; dj <= reach; dj++)
            for (int di = -reach; di <= reach; di++)
            {
                int n = CellAt(p.X + di * CellMetres, p.Y + dj * CellMetres);
                if (n >= 0 && GroundHydrology.Road((GroundSurface)_surface[n])) return true;
            }
        return false;
    }

    // ── Reading it ──────────────────────────────────────────────────────────────────────────────

    /// <summary>The cell a point of the map is in, or -1.</summary>
    public int CellAt(float x, float z)
    {
        if (_tiles.Length == 0) return -1;
        float tileMetres = CellsPerTile * CellMetres;
        var t0 = _tiles[0];
        // Tiles are on one grid: the first's corner says where it is.
        float gx = (x - t0.CornerX) / tileMetres + t0.X, gz = (z - t0.CornerZ) / tileMetres + t0.Z;
        int tx = (int)MathF.Floor(gx), tz = (int)MathF.Floor(gz);
        if (!_slot.TryGetValue((tx, tz), out int s)) return -1;
        var t = _tiles[s];
        int i = Math.Clamp((int)MathF.Floor((x - t.CornerX) / CellMetres), 0, CellsPerTile - 1);
        int j = Math.Clamp((int)MathF.Floor((z - t.CornerZ) / CellMetres), 0, CellsPerTile - 1);
        return s * CellsPerTile * CellsPerTile + j * CellsPerTile + i;
    }

    public Vector3 Centre(int c)
    {
        int per = CellsPerTile * CellsPerTile, s = c / per, k = c % per;
        var t = _tiles[s];
        return new Vector3(t.CornerX + (k % CellsPerTile + 0.5f) * CellMetres, _height[c], t.CornerZ + (k / CellsPerTile + 0.5f) * CellMetres);
    }

    private Vector2 Centre2(int c) { var v = Centre(c); return new Vector2(v.X, v.Z); }

    public int Next(int c) => _next[c];
    public float Height(int c) => _height[c];
    public GroundSurface SurfaceOf(int c) => (GroundSurface)_surface[c];
    /// <summary>Ground draining through a cell, m², itself included.</summary>
    public float SquareMetres(int c) => _area[c] * CellArea;
    /// <summary>How long water takes to reach the cell from the furthest ground draining to it, s.</summary>
    public float TimeOfConcentration(int c) => _tc[c];
    public int PondOf(int c) => _pondOf[c];
    public bool IsLine(int c) => _area[c] * CellArea >= LineSquareMetres;
    /// <summary>Each surface's ground draining to a line cell not through a pond, m²; null off the lines.</summary>
    public float[]? LineAreas(int c) => _lineAreas.TryGetValue(c, out var a) ? a : null;

    /// <summary>The catchment of any cell: a line cell's own (each surface's ground not through a pond, its mean
    /// travel time, the ponds overflowing into it, and all its ground for the base flow); off the lines, its ground
    /// taken as the cell's own surface (a slope's sheet: what is upslope is much like what is here) at 0.6 of its
    /// time of concentration.</summary>
    public GroundCatchment CatchmentOf(int c)
    {
        float total = _area[c] * CellArea;
        if (_lineAreas.TryGetValue(c, out var a))
        {
            var ponds = _pondsAt.TryGetValue(c, out var pl) ? pl.Distinct().OrderBy(i => i).ToArray() : Array.Empty<int>();
            return new GroundCatchment((float[])a.Clone(), (float[])_lineLags[c].Clone(), ponds, total);
        }
        var areas = new float[GroundHydrology.Surfaces];
        areas[_surface[c]] = total;
        return new GroundCatchment(areas, GroundHydrology.LagShare * _tc[c]);
    }

    /// <summary>The ponds whose overflow reaches a cell without passing another pond (line cells, and the cell
    /// each pond spills onto).</summary>
    public IReadOnlyList<int> PondsInto(int c) => _pondsAt.TryGetValue(c, out var l) ? l : Array.Empty<int>();

    /// <summary>The longest lines first: for the lab's list.</summary>
    public IEnumerable<LineInfo> Biggest(int count) => _lines.OrderByDescending(l => l.SquareMetres).Take(count);
}

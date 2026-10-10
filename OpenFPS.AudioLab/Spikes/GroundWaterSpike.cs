using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.Core;
using OpenFPS.Client.Services;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using OpenFPS.Server.Systems;
using OpenFPS.Server.Water;

namespace OpenFPS.AudioLab.Spikes;

/// <summary>
/// --ground-water: rain running over a real place's ground (docs/RUNNING_WATER.md section 13).
///
///   --ground-water map [place=magnolia_tx] [lines=25] [out=DIR]
///        loads the place as the server does and reports its drainage: what it cost (each tile's routing on
///        its own, the network, the voices), how the tiles' edges agree against the whole place routed at once,
///        the biggest drainage lines (where, how long, how much ground drains through them), the ponds and the
///        voices by kind; with out=, DIR/lines.csv and DIR/map.txt (a coarse map of the lines).
///   --ground-water flows [place=] [rain=25] [wet=10] [after=30]
///        the flows of the lines the renders stand by, every minute through a storm and after it
///   --ground-water levels [sec=20]
///        each kind of line's level at a metre at its flows and slopes: GroundChannels.LevelDb's table
///   --ground-water game out=DIR [place=] [sec=30]
///        the scenes through the game's own path (as --running-water game): by the creek dry, after ten
///        minutes of heavy rain (in it, and with the rain not heard), half an hour after it stops; a ditch
///        beside a road the same; DIR/capture.post.wav and DIR/segments.csv.
/// </summary>
public static class GroundWaterSpike
{
    private const int Rate = 48000;

    public static int Run(string[] args)
    {
        AcousticRegistry.Initialize();
        if (args.Contains("levels")) return Levels(args);
        string place = Arg(args, "place=") ?? "magnolia_tx";
        var loaded = Load(place);
        if (loaded == null) return 1;
        try
        {
            if (args.Contains("flows")) return Flows(loaded.Value, args);
            if (args.Contains("game")) return Game(loaded.Value, args);
            return MapReport(loaded.Value, args);
        }
        finally
        {
            try { Directory.Delete(loaded.Value.Dir, true); } catch (IOException) { }
        }
    }

    private readonly record struct Loaded(string Id, MapManager Maps, MapData Data, DrainageNetwork Net, string Dir, TimeSpan LoadTime);

    private static Loaded? Load(string id)
    {
        string src = LabPaths.Server("maps", "places", id + ".json");
        if (!File.Exists(src)) { Console.WriteLine($"FAIL: no {src}"); return null; }
        string dir = Path.Combine(Path.GetTempPath(), "openfps-groundwater-" + Guid.NewGuid().ToString("N"));
        string maps = Path.Combine(dir, "maps");
        Directory.CreateDirectory(Path.Combine(maps, "places"));
        File.Copy(src, Path.Combine(maps, "places", id + ".json"));
        string ground = LabPaths.Server("maps", "places", id + ".elevation");
        if (File.Exists(ground)) File.Copy(ground, Path.Combine(maps, "places", id + ".elevation"));
        var clock = Stopwatch.StartNew();
        var mm = new MapManager(new MapRepository(maps), new PrefabRepository(LabPaths.Server("prefabs")));
        mm.Initialize();
        var load = clock.Elapsed;
        if (!mm.TryGetMapData(id, out var data)) { Console.WriteLine("FAIL: the map did not load"); return null; }
        var net = GroundWaterSystem.NetworkOf(id);
        if (net == null) { Console.WriteLine("FAIL: the map has no drainage (no elevation?)"); return null; }
        Console.WriteLine($"{id}: loaded in {load.TotalSeconds:F1} s; drainage network built in {net.BuildTime.TotalMilliseconds:F0} ms");
        return new Loaded(id, mm, data, net, dir, load);
    }

    // ── The map ─────────────────────────────────────────────────────────────────────────────────

    private static int MapReport(Loaded l, string[] args)
    {
        var net = l.Net;
        Console.WriteLine($"{net.Tiles.Count} tiles, {net.CellCount:N0} cells of {net.CellMetres} m");

        // Each tile on its own, as a world tile is made: its window (the tile and its margin) routed alone; then
        // the tiles joined, against the whole place routed at once.
        var grid = Grid(net, out int cx, out int cz);
        int per = net.CellsPerTile;
        int tx0 = net.Tiles.Min(t => t.X), tz0 = net.Tiles.Min(t => t.Z);
        var swAll = Stopwatch.StartNew();
        var (whole, wholeFill) = Drainage.Route(grid, cx, cz, 0, 0, cx, cz, net.CellMetres);
        Console.WriteLine($"The whole place routed at once: {swAll.Elapsed.TotalMilliseconds:F0} ms");
        var reference = Joined(net, (t, i, j) => whole[((t.Z - tz0) * per + j) * cx + (t.X - tx0) * per + i],
                               (t, i, j) => wholeFill[((t.Z - tz0) * per + j) * cx + (t.X - tx0) * per + i]);
        foreach (int m in new[] { 0, 10, MapDrainage.MarginCells, 50, 125 })
        {
            var times = new List<double>();
            var flows = new Dictionary<(int, int), (byte[] Flow, short[] Fill)>();
            foreach (var t in net.Tiles)
            {
                int i0 = (t.X - tx0) * per - m, j0 = (t.Z - tz0) * per - m;
                int lo = Math.Max(0, i0), lz = Math.Max(0, j0), hi = Math.Min(cx, i0 + per + 2 * m), hz = Math.Min(cz, j0 + per + 2 * m);
                int nx = hi - lo, nz = hz - lz;
                var w = new float[nx * nz];
                for (int j = 0; j < nz; j++) Array.Copy(grid, (lz + j) * cx + lo, w, j * nx, nx);
                var sw = Stopwatch.StartNew();
                flows[(t.X, t.Z)] = Drainage.Route(w, nx, nz, (t.X - tx0) * per - lo, (t.Z - tz0) * per - lz, per, per, net.CellMetres);
                times.Add(sw.Elapsed.TotalMilliseconds);
            }
            times.Sort();
            int differ = 0, edgeDiffer = 0, edgeCells = 0;
            foreach (var t in net.Tiles)
            {
                var mine = flows[(t.X, t.Z)].Flow;
                for (int j = 0; j < per; j++)
                    for (int i = 0; i < per; i++)
                    {
                        bool edge = i == 0 || j == 0 || i == per - 1 || j == per - 1;
                        if (edge) edgeCells++;
                        if (mine[j * per + i] != whole[((t.Z - tz0) * per + j) * cx + (t.X - tx0) * per + i]) { differ++; if (edge) edgeDiffer++; }
                    }
            }
            var tiled = Joined(net, (t, i, j) => flows[(t.X, t.Z)].Flow[j * per + i], (t, i, j) => flows[(t.X, t.Z)].Fill[j * per + i]);
            var (count, within10, within25) = Compare(reference, tiled);
            Console.WriteLine($"Margin {m * net.CellMetres:F0} m: a tile's {per + 2 * m}-cell window routed alone (one core) median {times[times.Count / 2]:F1} ms, worst {times[^1]:F1} ms; " +
                              $"directions unlike the whole place's {100.0 * differ / net.CellCount:F2} % ({100.0 * edgeDiffer / Math.Max(1, edgeCells):F2} % on tile edges); " +
                              $"loops between tiles {tiled.LoopsBroken}; of the whole place's {count:N0} line cells over 5 ha, the tiles' ground within 10 % {100.0 * within10 / Math.Max(1, count):F1} %, within 25 % {100.0 * within25 / Math.Max(1, count):F1} %; " +
                              $"lines {tiled.Lines.Count} ({tiled.Lines.Sum(x => x.LengthMetres) / 1000:F1} km), ponds {tiled.Ponds.Count}");
        }
        Console.WriteLine($"The whole place at once: lines {reference.Lines.Count} ({reference.Lines.Sum(x => x.LengthMetres) / 1000:F1} km), ponds {reference.Ponds.Count}, " +
                          $"{reference.Ponds.Sum(p => p.CapacityCubicMetres):F0} m³");
        Console.WriteLine($"As loaded (the whole place routed at once, cut into tiles): loops between tiles broken {net.LoopsBroken}");

        // The lines.
        double total = net.Lines.Sum(x => x.LengthMetres);
        Console.WriteLine($"Drainage lines (over {net.LineSquareMetres / 1e4:F1} ha): {net.Lines.Count}, {total / 1000:F1} km in all");
        foreach (var band in new[] { (0.5, 2.0), (2.0, 20.0), (20.0, 100.0), (100.0, 1e9) })
        {
            var inBand = net.Lines.Where(x => x.SquareMetres / 1e4 >= band.Item1 && x.SquareMetres / 1e4 < band.Item2).ToList();
            Console.WriteLine($"  ending with {band.Item1:0.#} to {(band.Item2 > 1e8 ? "any" : band.Item2.ToString("0.#"))} ha draining: {inBand.Count} lines, {inBand.Sum(x => x.LengthMetres) / 1000:F1} km");
        }
        Console.WriteLine($"Hollows {net.Hollows:N0}: {net.Ponds.Count} keep water ({net.Ponds.Count(p => p.IsPuddle)} puddles on paving, " +
                          $"{net.Ponds.Count(p => !p.IsPuddle)} ponds; {net.Ponds.Sum(p => p.CapacityCubicMetres):F0} m³ in all), {net.Culverts} held by a road and drained through its culvert");
        foreach (var kind in Enum.GetValues<GroundChannelKind>())
        {
            var vs = net.Voices.Where(v => v.Kind == kind).ToList();
            Console.WriteLine($"  voices, {kind}: {vs.Count}, {vs.Sum(v => v.LengthMetres) / 1000:F1} km");
        }
        foreach (var kind in Enum.GetValues<GroundChannelKind>())
        {
            var refs = net.Voices.Where(v => v.Kind == kind).Select(v => v.ReferenceLitresPerSecond).ToList();
            Console.WriteLine($"  {kind} reference flows: " + string.Join(", ", new[] { 0.05f, 0.2f, 0.5f, 1f, 5f, 20f, 100f }
                .Select(q => $"{refs.Count(r => r >= q)} at {q:0.##} L/s and over")));
        }
        int show = (int)(float.TryParse(Arg(args, "lines="), NumberStyles.Float, CultureInfo.InvariantCulture, out float s) ? s : 25f);
        Console.WriteLine();
        Console.WriteLine($"The {show} biggest lines at least 150 m long, each from its head to where it joins a bigger one (map metres: x east, z north; heights over the map's y = 0):");
        var roads = l.Maps.TryGetRoads(l.Id, out var rn) ? rn.Roads : (IReadOnlyList<RoadData>)Array.Empty<RoadData>();
        foreach (var line in net.Lines.Where(x => x.LengthMetres >= 150f).OrderByDescending(x => x.SquareMetres).Take(show))
        {
            var a = net.Centre(line.Cells[0]);
            var b = net.Centre(line.Cells[^1]);
            Console.WriteLine($"  {line.SquareMetres / 1e4,8:F1} ha  {line.LengthMetres,6:F0} m  from ({a.X:F0}, {a.Z:F0}, {a.Y:F1} m) to ({b.X:F0}, {b.Z:F0}, {b.Y:F1} m)" +
                              $"  {Nearest(roads, b)}");
        }
        string? outDir = Arg(args, "out=");
        if (outDir != null)
        {
            Directory.CreateDirectory(outDir);
            var csv = new StringBuilder("hectares,length_m,from_x,from_z,to_x,to_z,to_height_m,near\n");
            foreach (var line in net.Lines.OrderByDescending(x => x.SquareMetres))
            {
                var a = net.Centre(line.Cells[0]);
                var b = net.Centre(line.Cells[^1]);
                csv.Append(CultureInfo.InvariantCulture, $"{line.SquareMetres / 1e4:F2},{line.LengthMetres:F0},{a.X:F0},{a.Z:F0},{b.X:F0},{b.Z:F0},{b.Y:F2},\"{Nearest(roads, b)}\"\n");
            }
            File.WriteAllText(Path.Combine(outDir, "lines.csv"), csv.ToString());
            File.WriteAllText(Path.Combine(outDir, "map.txt"), AsciiMap(net, l.Data));
            Console.WriteLine($"Wrote {outDir}/lines.csv and map.txt");
        }
        return 0;
    }

    /// <summary>A network of the place's tiles with other directions and fills (its surfaces and heights the same).</summary>
    private static DrainageNetwork Joined(DrainageNetwork net, Func<DrainageNetwork.TileInput, int, int, byte> flow,
                                          Func<DrainageNetwork.TileInput, int, int, short> fill)
    {
        int per = net.CellsPerTile;
        var tiles = net.Tiles.Select(t =>
        {
            var f = new byte[per * per];
            var h = new short[per * per];
            for (int j = 0; j < per; j++)
                for (int i = 0; i < per; i++) { f[j * per + i] = flow(t, i, j); h[j * per + i] = fill(t, i, j); }
            return t with { Drainage = new TileDrainage { Cells = per, CellMetres = net.CellMetres, Flow = f, FillCm = h, Surface = t.Drainage.Surface } };
        }).ToList();
        return new DrainageNetwork().Build(tiles);
    }

    /// <summary>Of the reference's line cells draining over 5 ha, how many have the same ground draining through
    /// them in the other, within 10 % and 25 %, at the cell or one beside it (a line one cell over is the same line).</summary>
    private static (int Count, int Within10, int Within25) Compare(DrainageNetwork reference, DrainageNetwork other)
    {
        int count = 0, w10 = 0, w25 = 0;
        for (int c = 0; c < reference.CellCount; c++)
        {
            float a = reference.SquareMetres(c);
            if (a < 5e4f) continue;
            count++;
            float best = float.MaxValue;
            float r0 = MathF.Abs(other.SquareMetres(c) / a - 1f);
            best = MathF.Min(best, r0);
            for (int d = 0; d < 8; d++)
            {
                int n = other.Neighbour(c, d);
                if (n >= 0) best = MathF.Min(best, MathF.Abs(other.SquareMetres(n) / a - 1f));
            }
            if (best <= 0.1f) w10++;
            if (best <= 0.25f) w25++;
        }
        return (count, w10, w25);
    }

    /// <summary>The place's cell heights as one grid, row by row from its first tile's south-west corner.</summary>
    private static float[] Grid(DrainageNetwork net, out int cx, out int cz)
    {
        int per = net.CellsPerTile;
        int tx0 = net.Tiles.Min(t => t.X), tz0 = net.Tiles.Min(t => t.Z);
        cx = (net.Tiles.Max(t => t.X) - tx0 + 1) * per;
        cz = (net.Tiles.Max(t => t.Z) - tz0 + 1) * per;
        var grid = new float[cx * cz];
        foreach (var t in net.Tiles)
            for (int j = 0; j < per; j++)
                Array.Copy(t.CellHeights, j * per, grid, ((t.Z - tz0) * per + j) * cx + (t.X - tx0) * per, per);
        return grid;
    }

    private static string Nearest(IReadOnlyList<RoadData> roads, Vector3 p)
    {
        string best = "";
        float d = float.MaxValue;
        foreach (var r in roads)
            for (int k = 1; k < r.Centreline.Count; k++)
            {
                var a = new Vector2(r.Centreline[k - 1].X, r.Centreline[k - 1].Z);
                var b = new Vector2(r.Centreline[k].X, r.Centreline[k].Z);
                var ab = b - a;
                float t = Math.Clamp(Vector2.Dot(new Vector2(p.X, p.Z) - a, ab) / MathF.Max(1e-6f, ab.LengthSquared()), 0f, 1f);
                float dd = Vector2.Distance(new Vector2(p.X, p.Z), a + ab * t);
                if (dd < d) { d = dd; best = r.Name; }
            }
        return best.Length > 0 ? $"{d:F0} m from {best}" : "";
    }

    /// <summary>A map of the lines, a character for every 50 m: blank no line, '.' a line from half a hectare,
    /// ':' over 5 ha, '#' a creek's (over 20 ha), 'o' a pond, '@' the spawn.</summary>
    private static string AsciiMap(DrainageNetwork net, MapData data)
    {
        const float step = 50f;
        int per = net.CellsPerTile;
        float x0 = net.Tiles.Min(t => t.CornerX), z0 = net.Tiles.Min(t => t.CornerZ);
        float x1 = net.Tiles.Max(t => t.CornerX) + per * net.CellMetres, z1 = net.Tiles.Max(t => t.CornerZ) + per * net.CellMetres;
        int w = (int)((x1 - x0) / step), h = (int)((z1 - z0) / step);
        var chars = new char[w, h];
        for (int i = 0; i < w; i++) for (int j = 0; j < h; j++) chars[i, j] = ' ';
        for (int c = 0; c < net.CellCount; c++)
        {
            var p = net.Centre(c);
            int i = (int)((p.X - x0) / step), j = (int)((p.Z - z0) / step);
            if (i < 0 || j < 0 || i >= w || j >= h) continue;
            char mark = net.PondOf(c) >= 0 && !net.Ponds[net.PondOf(c)].IsPuddle && net.Ponds[net.PondOf(c)].CapacityCubicMetres > 20f ? 'o'
                : net.SquareMetres(c) >= net.CreekSquareMetres ? '#'
                : net.SquareMetres(c) >= 5e4f ? ':'
                : net.IsLine(c) ? '.' : ' ';
            if (Rank(mark) > Rank(chars[i, j])) chars[i, j] = mark;
        }
        int si = (int)((data.SpawnPoint.Position.X - x0) / step), sj = (int)((data.SpawnPoint.Position.Z - z0) / step);
        if (si >= 0 && sj >= 0 && si < w && sj < h) chars[si, sj] = '@';
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Drainage lines of {data.DisplayName}, one character for every {step:F0} m, north up; x from {x0:F0} to {x1:F0}, z from {z0:F0} to {z1:F0} (map metres).");
        sb.AppendLine("'.' a line draining half a hectare or more, ':' over 5 ha, '#' a creek (over 20 ha), 'o' a pond holding over 20 m³, '@' the spawn.");
        for (int j = h - 1; j >= 0; j--)
        {
            for (int i = 0; i < w; i++) sb.Append(chars[i, j]);
            sb.AppendLine();
        }
        return sb.ToString();
        static int Rank(char ch) => ch switch { '@' => 5, 'o' => 4, '#' => 3, ':' => 2, '.' => 1, _ => 0 };
    }

    // ── Flows through a storm ───────────────────────────────────────────────────────────────────

    /// <summary>The stretches the renders stand by: the creek with the most ground draining through it, and the
    /// ditch beside a road with the most paving draining into it.</summary>
    private static (DrainageNetwork.Voice Creek, DrainageNetwork.Voice Ditch) Chosen(DrainageNetwork net)
    {
        var creek = net.Voices.Where(v => v.Kind == GroundChannelKind.Creek).OrderByDescending(v => v.SquareMetres).FirstOrDefault()
                    ?? net.Voices.OrderByDescending(v => v.SquareMetres).First();
        var ditch = net.Voices.Where(v => v.Kind == GroundChannelKind.Ditch)
                    .OrderByDescending(v => v.Catchment.AreaSquareMetres[(int)GroundSurface.Impervious]).FirstOrDefault()
                    ?? net.Voices.OrderByDescending(v => v.Catchment.AreaSquareMetres[(int)GroundSurface.Impervious]).First();
        return (creek, ditch);
    }

    private static string Describe(DrainageNetwork.Voice v)
    {
        var a = v.Catchment.AreaSquareMetres;
        return $"{v.Kind} at ({v.Position.X:F0}, {v.Position.Z:F0}), {v.SquareMetres / 1e4:F1} ha draining through it " +
               $"(not through a pond: sealed {a[1] / 1e4:F2} ha, lawn {a[4] / 1e4:F2}, woods {a[5] / 1e4:F2}, open {a[0] / 1e4:F2}, gravel/dirt road {(a[2] + a[3]) / 1e4:F2}, water {a[6] / 1e4:F2}), " +
               $"{v.Catchment.Ponds.Length} ponds above it; mean travel time sealed {v.Catchment.LagOf(1) / 60:F1} min, lawn {v.Catchment.LagOf(4) / 60:F1}, woods {v.Catchment.LagOf(5) / 60:F1}, open {v.Catchment.LagOf(0) / 60:F1}; " +
               $"bed {v.WidthMetres:F2} m at {v.Slope * 100:F1} %; reference {v.ReferenceLitresPerSecond:F2} L/s";
    }

    private static int Flows(Loaded l, string[] args)
    {
        float rain = ArgF(args, "rain=", Rainfall.HeavyRate), wet = ArgF(args, "wet=", 10f), after = ArgF(args, "after=", 30f);
        var (creek, ditch) = Chosen(l.Net);
        Console.WriteLine("Creek: " + Describe(creek));
        Console.WriteLine("Ditch: " + Describe(ditch));
        GroundWaterSystem.Settle(l.Id, 0f);
        Console.WriteLine("minute  rain mm/h   creek L/s   ditch L/s   event mm   base flow mm/h");
        void Row(float minute)
            => Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{minute,6:F0}  {GroundWaterSystem.WaterOf(l.Id)!.RainMmPerHour,9:F1}  {GroundWaterSystem.FlowAt(l.Id, creek.Position),10:F3}  {GroundWaterSystem.FlowAt(l.Id, ditch.Position),10:F3}  {GroundWaterSystem.WaterOf(l.Id)!.EventMm,9:F1}  {GroundWaterSystem.WaterOf(l.Id)!.BaseflowMmPerHour,8:F4}"));
        Row(0);
        for (int t = 1; t <= (int)((wet + after) * 60f); t++)
        {
            GroundWaterSystem.Update(l.Id, t <= wet * 60f ? rain : 0f, 0.05f, 1f);
            if (t % 60 == 0) Row(t / 60f);
        }
        return 0;
    }

    // ── Levels ──────────────────────────────────────────────────────────────────────────────────

    private static int Levels(string[] args)
    {
        float sec = ArgF(args, "sec=", 20f);
        var kinds = new (GroundChannelKind Kind, float[] Flows)[]
        {
            (GroundChannelKind.Rill, new[] { 0.05f, 0.5f, 5f, 50f }),
            (GroundChannelKind.Ditch, new[] { 0.1f, 1f, 10f, 100f }),
            (GroundChannelKind.Runnel, new[] { 0.05f, 0.3f, 3f, 30f }),
            (GroundChannelKind.Creek, new[] { 1f, 10f, 40f, 160f, 600f }),
        };
        var catchment = new GroundCatchment(new float[GroundHydrology.Surfaces], 600f);
        foreach (var (kind, flows) in kinds)
        {
            Console.WriteLine($"== {kind}");
            foreach (float slope in new[] { 0.005f, 0.02f, 0.08f })
            {
                var row = new StringBuilder($"  slope {slope * 100,4:F1} %:");
                foreach (float q in flows)
                {
                    var spec = GroundChannels.SpecFor(kind, GroundChannels.WidthFor(kind, q), slope, GroundChannels.SegmentMetres, q, catchment);
                    var pa = RunningWaterSpike.Render(spec, q, sec, 7);
                    double sum = 0; foreach (float v in pa) sum += (double)v * v;
                    double leq = 10 * Math.Log10(Math.Max(1e-24, sum / pa.Length) / (20e-6 * 20e-6));
                    var st = Hydraulics.Of(spec, q);
                    row.Append(CultureInfo.InvariantCulture, $"  {q:0.###} L/s {leq:F1} dB (w {spec.WidthMetres:F2} m, y {st.DepthMetres * 1000:F0} mm, Fr {st.Froude:F2})");
                }
                Console.WriteLine(row);
            }
        }
        return 0;
    }

    // ── Through the game ────────────────────────────────────────────────────────────────────────

    private sealed record Segment(string Name, double Start, double Seconds);

    private static int Game(Loaded l, string[] args)
    {
        string outDir = Arg(args, "out=") ?? "/tmp/openfps-ground-water";
        double sec = ArgF(args, "sec=", 30f);
        Directory.CreateDirectory(outDir);
        var (creek, ditch) = Chosen(l.Net);
        var notes = new StringBuilder();
        notes.AppendLine("Creek: " + Describe(creek));
        notes.AppendLine("Ditch: " + Describe(ditch));

        // The map's water at each moment of the storm, as the server would send it.
        var states = new Dictionary<string, (float[] State, float Creek, float Ditch, float Rain)>();
        GroundWaterSystem.Settle(l.Id, 0f);
        void Keep(string name) => states[name] = (GroundWaterSystem.WaterOf(l.Id)!.Save(), GroundWaterSystem.FlowAt(l.Id, creek.Position),
                                                 GroundWaterSystem.FlowAt(l.Id, ditch.Position), GroundWaterSystem.WaterOf(l.Id)!.RainMmPerHour);
        Keep("dry");
        for (int t = 1; t <= 600; t++) GroundWaterSystem.Update(l.Id, Rainfall.HeavyRate, 0.05f, 1f);
        Keep("rain10");
        for (int t = 1; t <= 1800; t++) GroundWaterSystem.Update(l.Id, 0f, 0.05f, 1f);
        Keep("after30");
        foreach (var (k, v) in states)
            notes.AppendLine(string.Create(CultureInfo.InvariantCulture, $"{k}: creek {v.Creek:F3} L/s, ditch {v.Ditch:F3} L/s, rain {v.Rain:F1} mm/h"));
        Console.Write(notes);

        Environment.SetEnvironmentVariable("OPENFPS_DITHER", "0");
        Environment.SetEnvironmentVariable("OPENFPS_FMOD_WAV", Path.Combine(outDir, "capture.wav"));
        Environment.SetEnvironmentVariable("OPENFPS_AUDIO_CAPTURE", Path.Combine(outDir, "capture.post.wav"));
        Environment.SetEnvironmentVariable("OPENFPS_AUDIO_CAPTURE_FLOAT", "1");
        var provider = new OpenFPS.Client.AudioEngine.Fmod.FmodAudioProvider();
        var facade = new AudioEngineFacade(provider);
        string sounds = LabPaths.Sounds();
        facade.InitializeForTest(sounds);
        if (!facade.IsInitialized) { Console.WriteLine("FAIL: provider init failed"); return 1; }
        provider.EarWindEnabled = false;
        var clock = Stopwatch.StartNew();
        var world = new ClientWorldState();
        world.Clear(new Vector3(4000, 400, 4000));
        var player = new LocalPlayerState();
        var mapping = new SoundMappingService(player);
        mapping.Initialize(sounds);
        var audio = new ClientAudioSystem(facade, mapping, player, () => clock.Elapsed.TotalSeconds);
        world.RegisterDefinition(new EntityDefinition
        {
            EntityId = 1, Type = EntityType.StaticObject,
            Transform = new Transform { Position = new Vector3(0f, -0.5f, 0f), Rotation = Quaternion.Identity, Scale = Vector3.One },
            Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(600f, 1f, 600f), IsSolid = true },
            Material = new MaterialComponent { Material = "Grass" },
        });
        WindField.Weather = WindWeather.Steady(1.5f, 250f, 0.2f);
        var segments = new List<Segment>();
        int nextId = 100;

        void Pump(double seconds)
        {
            var until = clock.Elapsed.TotalSeconds + seconds;
            while (clock.Elapsed.TotalSeconds < until)
            {
                audio.Update(world.GetSnapshot());
                facade.PumpForTest();
                Thread.Sleep(4);
            }
        }
        void Record(string name, double seconds)
        {
            double start = clock.Elapsed.TotalSeconds;
            Pump(seconds);
            segments.Add(new Segment(name, start, seconds));
            Console.WriteLine($"  {start,7:F2} s  {name} ({seconds:F1} s)");
        }
        void Weather(float rainMmPerHour)
        {
            world.UpdateAtmosphere(new WorldStateUpdate
            {
                Temperature = 14f, Humidity = rainMmPerHour > 0f ? 0.92f : 0.75f, AirPressure = 101325f, AirAbsorptionMultiplier = 1f,
                PrecipitationIntensity = rainMmPerHour > 0f ? Rainfall.IntensityFor(rainMmPerHour) : 0f,
                RainRateMmPerHour = rainMmPerHour,
            });
        }
        void Water(string state)
        {
            GroundWater.Held = false;
            GroundWater.Shared.Load(states[state].State);
            GroundWater.Held = true;
        }
        // The voices of the lines within 80 m of where the listener stands, moved so the listener is near the
        // origin; the ground's y kept relative.
        int[] Voices(Vector3 stand)
        {
            var ids = new List<int>();
            foreach (var v in l.Net.Voices)
            {
                if (Vector2.Distance(new Vector2(v.Position.X, v.Position.Z), new Vector2(stand.X, stand.Z)) > 80f) continue;
                int id = nextId++;
                var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, v.Yaw - MathF.PI / 2f);
                var def = new EntityDefinition
                {
                    EntityId = id, Type = EntityType.StaticObject,
                    Transform = new Transform { Position = new Vector3(v.Position.X - stand.X, v.Position.Y - stand.Y + 0.05f, v.Position.Z - stand.Z), Rotation = turn, Scale = Vector3.One },
                };
                var spec = RunningWaterSpec.ByName(v.SoundId[5..]);
                def.SoundEmitter = new SoundEmitterComponent
                {
                    IsSynth = true, SoundId = v.SoundId, Mode = PlaybackMode.LoopOne, Volume = 1f,
                    Range = MathF.Max(60f, Loudness.AudibleRange(spec.SourceLevelDb + 6f)), MinDistance = 1f, ExtentMetres = spec.ExtentMetres,
                };
                world.RegisterDefinition(def);
                ids.Add(id);
            }
            return ids.ToArray();
        }
        void Remove(int[] ids)
        {
            world.RemoveEntities(ids);
            foreach (int id in ids) audio.ForgetEntity(id);
            Pump(2.0);
        }
        void Scene(string label, DrainageNetwork.Voice at, string state, bool rainHeard)
        {
            // Standing on the bank, 2.5 m to the side of the line's middle, facing it.
            var side = new Vector3(MathF.Cos(at.Yaw), 0f, -MathF.Sin(at.Yaw));
            var stand = at.Position + side * (0.5f * at.WidthMetres + 2.5f);
            Water(state);
            Weather(rainHeard ? states[state].Rain : 0f);
            var ids = Voices(new Vector3(stand.X, at.Position.Y, stand.Z));
            player.Position = new Vector3(0f, 0f, 0f);
            var toLine = at.Position - stand;
            float yaw = MathF.Atan2(toLine.X, toLine.Z);
            player.Yaw = yaw;
            player.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);
            Pump(5.0);
            Record($"{label} ({ids.Length} voices within 80 m)", sec);
            Remove(ids);
        }

        try
        {
            Weather(0f);
            Water("dry");
            Pump(2.0);
            Record("silence", 2.0);
            Scene("creek dry", creek, "dry", false);
            Scene("creek after 10 min heavy rain, in the rain", creek, "rain10", true);
            Scene("creek after 10 min heavy rain, rain not heard", creek, "rain10", false);
            Scene("creek 30 min after the rain", creek, "after30", false);
            Scene("ditch dry", ditch, "dry", false);
            Scene("ditch after 10 min heavy rain, in the rain", ditch, "rain10", true);
            Scene("ditch after 10 min heavy rain, rain not heard", ditch, "rain10", false);
            Scene("ditch 30 min after the rain", ditch, "after30", false);
            Record("silence end", 1.0);
        }
        finally
        {
            GroundWater.Held = false;
            facade.Dispose();
        }
        var sb = new StringBuilder("name,start,seconds,cpu_percent,mixer_load,hrtf_voices\n");
        foreach (var s in segments)
            sb.Append(CultureInfo.InvariantCulture, $"{s.Name.Replace(',', ';')},{s.Start:F3},{s.Seconds:F3},0,0,0\n");
        File.WriteAllText(Path.Combine(outDir, "segments.csv"), sb.ToString());
        File.WriteAllText(Path.Combine(outDir, "scenes.txt"), notes.ToString());
        Console.WriteLine($"Wrote {outDir}/capture.post.wav, segments.csv and scenes.txt ({segments.Count} segments)");
        return 0;
    }

    private static string? Arg(string[] args, string prefix) => args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];

    private static float ArgF(string[] args, string prefix, float fallback)
        => float.TryParse(Arg(args, prefix), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : fallback;
}

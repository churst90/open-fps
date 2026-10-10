using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Server.OneWorld;
using OpenFPS.Server.Repositories;
using OpenFPS.Server.Systems;
using OpenFPS.Server.Water;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// Water over the ground (docs/RUNNING_WATER.md section 13): which way each cell drains (Priority-Flood and
/// D8), tiles joined across their edges, hollows kept or filled or drained through a road's culvert, the
/// curve number's run-off per surface through the ladder of reservoirs, ponds that fill and overflow, water
/// poured at a point running downhill, and the wire. Made-up ground; Magnolia in GroundWaterMagnoliaTests.
/// </summary>
[Collection("GroundWater")]
public class GroundWaterTests : IDisposable
{
    private readonly ITestOutputHelper _o;
    public GroundWaterTests(ITestOutputHelper o) { _o = o; GroundWaterSystem.Reset(); }
    public void Dispose() => GroundWaterSystem.Reset();

    private const float Cell = 2f;

    // ── Directions ──────────────────────────────────────────────────────────────────────────────

    /// <summary>A valley running north to south: sides falling to its axis at x = 20 cells, the axis falling
    /// south. Every cell drains, no loops, everything reaches the south edge along the axis.</summary>
    [Fact]
    public void A_valley_drains_to_its_axis_and_down_it()
    {
        const int n = 40;
        var h = new float[n * n];
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++) h[j * n + i] = 0.05f * MathF.Abs(i - 20) + 0.01f * j;
        var (flow, fill) = Drainage.Route(h, n, n, 0, 0, n, n, Cell);
        Assert.All(fill, f => Assert.Equal(0, f));
        // Off the axis every cell drains toward it (east of it west-ish, west of it east-ish).
        for (int j = 1; j < n - 1; j++)
        {
            Assert.Contains(flow[j * n + 30], new byte[] { 3, 4, 5 });
            Assert.Contains(flow[j * n + 10], new byte[] { 7, 0, 1 });
            Assert.Equal(6, flow[j * n + 20]);   // the axis runs south
        }
        var net = OneTile(h, n, flow, fill);
        int outlet = net.CellAt(20.5f * Cell, 0.5f * Cell);
        Assert.Equal(0, net.LoopsBroken);
        // The bottom of the axis gathers all of the valley but the edge cells that leave it on their own.
        Assert.True(net.SquareMetres(outlet) > 0.8f * n * n * Cell * Cell, $"{net.SquareMetres(outlet)} m²");
    }

    /// <summary>A bowl 50 cm deep in a slope: Priority-Flood fills it to its rim, its cells drain out over its
    /// spill point, and it is kept as a pond holding what the bowl holds.</summary>
    [Fact]
    public void A_hollow_fills_to_its_rim_and_keeps_its_water()
    {
        const int n = 40;
        var h = new float[n * n];
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                float r = MathF.Sqrt((i - 20) * (i - 20) + (j - 20) * (j - 20));
                h[j * n + i] = 10f + 0.02f * j - (r < 6f ? 0.5f * (1f - r * r / 36f) : 0f);
            }
        var (flow, fill) = Drainage.Route(h, n, n, 0, 0, n, n, Cell);
        // Filled to its lowest rim, on the downhill side: 0.5 m deep less the slope across its 6 cells.
        Assert.InRange(fill[20 * n + 20], 30, 50);
        var net = OneTile(h, n, flow, fill);
        Assert.Equal(0, net.LoopsBroken);
        Assert.Single(net.Ponds);
        var pond = net.Ponds[0];
        Assert.False(pond.IsPuddle);
        Assert.InRange(pond.CapacityCubicMetres, 5f, 60f);
        // Following any of its cells leaves the bowl and reaches the south edge.
        int c = net.CellAt(20.5f * Cell, 20.5f * Cell), steps = 0;
        while (net.Next(c) >= 0 && steps++ < 1000) c = net.Next(c);
        Assert.True(steps < 1000);
        Assert.Equal(DrainageNetwork.Leaves, net.Next(c));
        _o.WriteLine($"bowl: fill {fill[20 * n + 20]} cm, {pond.Cells.Length} cells, holds {pond.CapacityCubicMetres:F1} m³");
    }

    /// <summary>A hollow held back by a road across its fall (its spill on the road, its bottom off it) drains
    /// through the road's culvert at once; one on paving is a puddle.</summary>
    [Fact]
    public void A_road_across_a_hollow_has_a_culvert_and_a_dip_in_a_road_is_a_puddle()
    {
        const int n = 40;
        var h = new float[n * n];
        var surface = new byte[n * n];
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                // A slope falling south, its lower part a valley falling to its middle (so what the road holds
                // back can only leave over the road).
                h[j * n + i] = 10f + 0.05f * j + (j < 25 ? 0.05f * MathF.Abs(i - 20) : 0f);
                // A road along rows 10 and 11, raised 60 cm: an embankment across the valley.
                if (j is 10 or 11) { h[j * n + i] += 0.6f; surface[j * n + i] = (byte)GroundSurface.Impervious; }
            }
        // A paved yard at the top, level, with a dip in it 2 cm deep.
        for (int j = 28; j < 36; j++)
            for (int i = 3; i < 11; i++) { h[j * n + i] = 11.75f; surface[j * n + i] = (byte)GroundSurface.Impervious; }
        h[31 * n + 6] -= 0.02f; h[31 * n + 7] -= 0.02f; h[32 * n + 6] -= 0.02f; h[32 * n + 7] -= 0.02f;
        var (flow, fill) = Drainage.Route(h, n, n, 0, 0, n, n, Cell);
        var net = OneTile(h, n, flow, fill, surface);
        Assert.True(net.Culverts >= 1, "the water held behind the road drains through it");
        Assert.DoesNotContain(net.Ponds, p => !p.IsPuddle);
        Assert.Contains(net.Ponds, p => p.IsPuddle);
    }

    /// <summary>
    /// A hill over four tiles, each tile routed alone from its window with a margin, then joined: the edges agree
    /// (no loops across them), each tile's directions are what the whole would give on a hill with no flats, and
    /// what drains across an edge carries on.
    /// </summary>
    [Fact]
    public void Tiles_routed_alone_join_across_their_edges()
    {
        const int per = 50, m = 10, tiles = 2, n = per * tiles;
        float H(int i, int j) => 30f - 0.0004f * ((i - 37) * (i - 37) + (j - 61) * (j - 61)) + 0.01f * MathF.Sin(i * 0.37f) * MathF.Cos(j * 0.23f);
        var whole = new float[n * n];
        for (int j = 0; j < n; j++) for (int i = 0; i < n; i++) whole[j * n + i] = H(i, j);
        var (wholeFlow, _) = Drainage.Route(whole, n, n, 0, 0, n, n, Cell);
        var inputs = new List<DrainageNetwork.TileInput>();
        int differ = 0;
        for (int tz = 0; tz < tiles; tz++)
            for (int tx = 0; tx < tiles; tx++)
            {
                int i0 = Math.Max(0, tx * per - m), j0 = Math.Max(0, tz * per - m);
                int i1 = Math.Min(n, tx * per + per + m), j1 = Math.Min(n, tz * per + per + m);
                var w = new float[(i1 - i0) * (j1 - j0)];
                for (int j = j0; j < j1; j++) for (int i = i0; i < i1; i++) w[(j - j0) * (i1 - i0) + i - i0] = whole[j * n + i];
                var (flow, fill) = Drainage.Route(w, i1 - i0, j1 - j0, tx * per - i0, tz * per - j0, per, per, Cell);
                var hh = new float[per * per];
                for (int j = 0; j < per; j++)
                    for (int i = 0; i < per; i++)
                    {
                        hh[j * per + i] = whole[(tz * per + j) * n + tx * per + i];
                        if (flow[j * per + i] != wholeFlow[(tz * per + j) * n + tx * per + i]) differ++;
                    }
                inputs.Add(new DrainageNetwork.TileInput(tx, tz, tx * per * Cell, tz * per * Cell, hh,
                    new TileDrainage { Cells = per, CellMetres = Cell, MarginCells = m, Flow = flow, FillCm = fill, Surface = new byte[per * per] }));
            }
        var net = new DrainageNetwork { LineSquareMetres = 400f }.Build(inputs);
        _o.WriteLine($"{differ} of {n * n} directions differ from the whole routed at once; {net.LoopsBroken} loops");
        Assert.Equal(0, net.LoopsBroken);
        Assert.True(differ < n * n / 100, $"{differ} directions differ");
        // Every cell's way down ends at the map's edge, crossing tile edges where it must.
        int crossings = 0;
        for (int c = 0; c < net.CellCount; c++)
        {
            int x = c, steps = 0;
            while (net.Next(x) >= 0 && steps++ < 2 * n * n)
            {
                if (net.Next(x) / (per * per) != x / (per * per)) crossings++;
                x = net.Next(x);
            }
            Assert.Equal(DrainageNetwork.Leaves, net.Next(x));
        }
        Assert.True(crossings > 0);
    }

    /// <summary>A tile's drainage is the same bytes whichever of two neighbours is made first, and the edge they
    /// share agrees: made through the world's own service from a made-up survey, a valley across the edge.</summary>
    [Fact]
    public async Task A_world_tile_s_drainage_is_the_same_whichever_tile_is_made_first()
    {
        string dir = Path.Combine(Path.GetTempPath(), "openfps-groundwater-" + Guid.NewGuid().ToString("N"));
        try
        {
            var sizes = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs")).Prefabs
                .ToDictionary(kv => kv.Key, kv => kv.Value.ColliderSize ?? Vector3.One, StringComparer.OrdinalIgnoreCase);
            var a = new WorldTileKey(15, true, 1000, 13300);
            var b = a.Offset(1, 0);
            WorldTileService Make(string name) => new(new WorldStore(Path.Combine(dir, name)), new Valley()) { Features = new WorldFeatures(new NoRoads(), sizes) };
            var one = Make("one");
            var two = Make("two");
            foreach (var k in new[] { a, b }) Assert.NotNull(await one.GetAsync(k));
            foreach (var k in new[] { b, a }) Assert.NotNull(await two.GetAsync(k));
            foreach (var k in new[] { a, b })
            {
                Assert.True(one.Store.TryRead(k, out var t1));
                Assert.True(two.Store.TryRead(k, out var t2));
                Assert.NotNull(t1.Drainage);
                Assert.Equal(TileDrainage.CurrentMethod, t1.Drainage!.Method);
                Assert.Equal(WorldTileService.Posts - 1, t1.Drainage.Cells);
                Assert.Equal(t1.Drainage.Flow, t2.Drainage!.Flow);
                Assert.Equal(t1.Drainage.FillCm, t2.Drainage.FillCm);
            }
            Assert.True(one.Store.TryRead(a, out var ta));
            Assert.True(one.Store.TryRead(b, out var tb));
            // Joined: nothing drains in a loop across the edge, and the valley's line runs from A into B.
            var net = new DrainageNetwork().Build(new[] { Input(ta, 0), Input(tb, 1) });
            Assert.Equal(0, net.LoopsBroken);
            int crossing = 0;
            for (int c = 0; c < net.CellCount; c++)
                if (net.IsLine(c) && net.Next(c) >= 0 && net.Next(c) / (125 * 125) != c / (125 * 125)) crossing++;
            Assert.True(crossing > 0, "the valley's line crosses the shared edge");
            _o.WriteLine($"tile {ta.ToBytes().Length / 1024.0:F1} KB with its drainage; lines {net.Lines.Count}, crossing the edge {crossing}");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    private static DrainageNetwork.TileInput Input(WorldTile t, int x)
    {
        var g = t.Terrain!;
        var posts = new float[g.Posts * g.Posts];
        for (int k = 0; k < posts.Length; k++) posts[k] = g.BaseY + g.HeightsCm[k] * 0.01f;
        return new DrainageNetwork.TileInput(x, 0, x * 250f, 0f, Drainage.CellHeights(posts, g.Posts, g.Posts), t.Drainage!);
    }

    /// <summary>A valley running east, falling 1 %, across the tiles' edge; its sides 4 % up to it.</summary>
    private sealed class Valley : IElevationSource
    {
        public string Name => "made-up valley";
        public static float At(double e, double n) => (float)(60 - 0.01 * (e - 250000) + 0.04 * Math.Abs(n - 3325125));
        public Task<float[]?> HeightsAsync(WorldTileKey key, int posts, double spacing, CancellationToken ct)
            => WindowAsync(key.Zone, key.North, key.Easting, key.Northing, posts, spacing, ct);
        public Task<float[]?> WindowAsync(int zone, bool north, double west, double south, int posts, double spacing, CancellationToken ct)
        {
            var h = new float[posts * posts];
            for (int j = 0; j < posts; j++)
                for (int i = 0; i < posts; i++) h[j * posts + i] = At(west + i * spacing, south + j * spacing);
            return Task.FromResult<float[]?>(h);
        }
    }

    private sealed class NoRoads : IOsmSource
    {
        public string Name => "no roads";
        public Task<IReadOnlyList<OsmWay>> HighwaysAsync(double south, double west, double north, double east, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<OsmWay>>(Array.Empty<OsmWay>());
    }

    [Fact]
    public void A_world_tile_keeps_its_drainage_through_the_store()
    {
        var key = new WorldTileKey(15, true, 1000, 13300);
        var heights = new float[WorldTileService.Posts * WorldTileService.Posts];
        for (int j = 0; j < WorldTileService.Posts; j++)
            for (int i = 0; i < WorldTileService.Posts; i++) heights[j * WorldTileService.Posts + i] = 50f + 0.02f * i;
        var t = WorldTileService.Make(key, heights, "test");
        Assert.NotNull(t.Drainage);
        var back = WorldTile.FromBytes(t.ToBytes());
        Assert.Equal(t.Drainage!.Flow, back.Drainage!.Flow);
        Assert.Equal(t.Drainage.FillCm, back.Drainage.FillCm);
        Assert.Equal(t.Drainage.Surface, back.Drainage.Surface);
        // Falling west: everything drains west (direction 4) but the west edge.
        Assert.All(back.Drainage.Flow.Where((_, k) => k % 125 != 0), d => Assert.Contains(d, new byte[] { 3, 4, 5 }));
    }

    // ── Run-off ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>TR-55's curve number: nothing runs off until the initial abstraction has fallen, a road sheds
    /// most of the rain at once and a lawn or the woods soak up a storm's first inches; the depths are the
    /// equation's (CN 98, 25 mm: Q = (25 − 1.04)² / (25 − 1.04 + 5.18) = 19.7 mm).</summary>
    [Fact]
    public void The_curve_number_sheds_rain_as_TR55_says()
    {
        Assert.Equal(98f, GroundHydrology.CurveNumber(GroundSurface.Impervious));
        Assert.Equal(55f, GroundHydrology.CurveNumber(GroundSurface.Woods));
        Assert.Equal(61f, GroundHydrology.CurveNumber(GroundSurface.Lawn));
        Assert.Equal(19.7f, GroundHydrology.RunoffMm(GroundSurface.Impervious, 25f), 1);
        Assert.Equal(0f, GroundHydrology.RunoffMm(GroundSurface.Lawn, 25f));
        Assert.Equal(0f, GroundHydrology.ExcessShare(GroundSurface.Impervious, 0.5f));
        Assert.InRange(GroundHydrology.ExcessShare(GroundSurface.Impervious, 4.2f), 0.55f, 0.65f);
        Assert.Equal(0f, GroundHydrology.ExcessShare(GroundSurface.Woods, 30f));
        Assert.True(GroundHydrology.ExcessShare(GroundSurface.Open, 60f) > 0f);
        Assert.Equal(1f, GroundHydrology.ExcessShare(GroundSurface.Water, 0f));
        // Its slope integrates to its depth.
        float sum = 0f;
        for (float p = 0.005f; p < 80f; p += 0.01f) sum += GroundHydrology.ExcessShare(GroundSurface.Open, p) * 0.01f;
        Assert.Equal(GroundHydrology.RunoffMm(GroundSurface.Open, 80f), sum, 1);
    }

    [Fact]
    public void Surfaces_come_from_what_covers_the_ground()
    {
        Assert.Equal(GroundSurface.Impervious, GroundHydrology.OfMaterial("Asphalt"));
        Assert.Equal(GroundSurface.Impervious, GroundHydrology.OfMaterial("Brick"));
        Assert.Equal(GroundSurface.Lawn, GroundHydrology.OfMaterial("Grass"));
        Assert.Equal(GroundSurface.Woods, GroundHydrology.OfMaterial("Foliage"));
        Assert.Equal(GroundSurface.Open, GroundHydrology.OfMaterial("Dirt"));
        Assert.Equal(GroundSurface.DirtRoad, GroundHydrology.OfMaterial("Dirt", road: true));
        Assert.Equal((byte)GroundSurface.Woods, SurfaceRaster.OfLandCover(10));
        Assert.Equal((byte)GroundSurface.Water, SurfaceRaster.OfLandCover(80));
        Assert.Equal((byte)GroundSurface.Open, SurfaceRaster.OfLandCover(30));
    }

    /// <summary>A road's water through the ladder: nothing for the first millimetre, rising through a downpour,
    /// running on after it stops and falling away; the slower the catchment the later and the longer.</summary>
    [Fact]
    public void The_ground_s_water_rises_in_rain_and_falls_after()
    {
        var w = new GroundWater();
        w.Step(0f, 1f);
        var road = new GroundCatchment(new float[] { 0, 1000, 0, 0, 0, 0, 0 }, 120f);
        var slow = new GroundCatchment(new float[] { 0, 1000, 0, 0, 0, 0, 0 }, 1200f);
        Assert.Equal(0f, w.FlowLitresPerSecond(road));
        float peak = 0f, peakSlow = 0f;
        for (int t = 0; t < 600; t++) { w.Step(25f, 1f); peak = MathF.Max(peak, w.FlowLitresPerSecond(road)); peakSlow = MathF.Max(peakSlow, w.FlowLitresPerSecond(slow)); }
        float atStop = w.FlowLitresPerSecond(road);
        Assert.True(atStop > 2f, $"{atStop} L/s off 1000 m² of road after 10 min of 25 mm/h");
        Assert.True(peakSlow < peak);
        for (int t = 0; t < 1800; t++) w.Step(0f, 1f);
        float after = w.FlowLitresPerSecond(road), afterSlow = w.FlowLitresPerSecond(slow);
        Assert.True(after < 0.1f * atStop);
        Assert.True(afterSlow > after, "the slow catchment runs on longer");
        _o.WriteLine($"1000 m² of road: {atStop:F2} L/s at the end of 10 min of 25 mm/h, {after:F3} L/s 30 min on; with a 20-min lag {afterSlow:F3}");
    }

    [Fact]
    public void Base_flow_comes_from_the_whole_catchment_and_only_a_big_one()
    {
        var w = new GroundWater();
        w.Step(0f, 1f);
        Assert.Equal(0f, w.BaseLitresPerSecond(1e4f));
        float creek = w.BaseLitresPerSecond(3e6f);
        Assert.InRange(creek, 5f, 12f);
        var c = new GroundCatchment(new float[GroundHydrology.Surfaces], new float[GroundHydrology.Surfaces], new[] { 3 }, 3e6f);
        Assert.Equal(creek, w.FlowLitresPerSecond(c), 3);
    }

    [Fact]
    public void The_ground_s_water_crosses_the_wire_whole()
    {
        var w = new GroundWater();
        w.Step(10f, 1f);
        for (int t = 0; t < 300; t++) w.Step(10f, 1f);
        w.SetSpills(new[] { new KeyValuePair<int, float>(7, 1.5f), new KeyValuePair<int, float>(2, 0.25f) });
        var c = new GroundCatchment(new float[] { 100, 200, 0, 0, 300, 400, 0 }, new float[] { 60, 120, 0, 0, 600, 900, 0 }, new[] { 2, 7, 9 }, 2e6f);
        var copy = new GroundWater();
        copy.Load(w.Save());
        Assert.Equal(w.FlowLitresPerSecond(c), copy.FlowLitresPerSecond(c), 5);
        Assert.Equal(1.5f, copy.SpillOf(7));
        Assert.Equal(0f, copy.SpillOf(9));
        copy.Load(null);
        Assert.Equal(0f, copy.FlowLitresPerSecond(c));
        copy.Load(new float[] { 1, 2, 3 });
        Assert.Equal(0f, copy.RainMmPerHour);
    }

    /// <summary>A drainage line's voice: its key says all of it, a client makes the same spec, and its flow is the
    /// map's water through its catchment.</summary>
    [Fact]
    public void A_line_s_voice_is_its_key()
    {
        var c = new GroundCatchment(new float[] { 1500, 820, 0, 0, 3000, 12000, 0 }, new float[] { 900, 240, 0, 0, 700, 1600, 0 }, new[] { 4, 11 }, 2.2e4f);
        Assert.True(GroundCatchment.TryParse(c.Key(), out var back));
        Assert.Equal(c.AreaSquareMetres, back.AreaSquareMetres);
        Assert.Equal(c.Ponds, back.Ponds);
        Assert.Equal(c.LagOf(5), back.LagOf(5));
        Assert.Equal(c.BaseSquareMetres, back.BaseSquareMetres);
        string key = GroundChannels.Key(GroundChannelKind.Ditch, 0.6f, 0.012f, 20f, 3.4f, c);
        var spec = RunningWaterSpec.ByName(key);
        Assert.NotNull(spec.Ground);
        Assert.Equal(FlowChannel.Stream, spec.Channel);
        Assert.Equal(0.6f, spec.WidthMetres, 3);
        Assert.Equal(0.012f, spec.Slope, 4);
        Assert.Equal(3.4f, spec.ReferenceFlow, 3);
        Assert.Equal(GroundChannels.LevelDb(GroundChannelKind.Ditch, 3.4f, 0.012f), spec.SourceLevelDb);
        Assert.Same(spec, RunningWaterSpec.ByName(key));
        Assert.Equal(GroundChannels.DryLitresPerSecond, spec.DryBelowLitresPerSecond);
        Assert.Equal(RunningWaterSpec.DryLitresPerSecond, RunningWaterSpec.ByName("gutter").DryBelowLitresPerSecond);
        try
        {
            GroundWater.Shared.Settle(25f);
            GroundWater.Held = true;
            Assert.True(spec.FlowNow() > 0.1f);
            Assert.Equal(GroundWater.Shared.FlowLitresPerSecond(c), spec.FlowNow());
        }
        finally
        {
            GroundWater.Held = false;
            GroundWater.Shared.Load(null);
        }
        Assert.False(GroundChannels.TryParse("ground/rill/x", out _));
        Assert.False(GroundChannels.TryParse("creek", out _));
    }

    [Fact]
    public void A_declared_level_is_the_loudest_a_line_does_at_or_under_its_flow()
    {
        foreach (var kind in Enum.GetValues<GroundChannelKind>())
        {
            float last = 0f;
            foreach (float q in new[] { 0.05f, 0.3f, 1f, 5f, 30f, 200f, 1000f })
            {
                float l = GroundChannels.LevelDb(kind, q, 0.01f);
                Assert.True(l >= last, $"{kind} {q} L/s: {l} under {last}");
                Assert.InRange(l, 30f, 100f);
                last = l;
            }
        }
        // A natural bed's stones grow with the water it was made for.
        var small = GroundChannels.SpecFor(GroundChannelKind.Creek, 0.8f, 0.01f, 20f, 5f, new GroundCatchment(new float[7], 60f));
        var big = GroundChannels.SpecFor(GroundChannelKind.Creek, 4f, 0.01f, 20f, 2000f, new GroundCatchment(new float[7], 60f));
        Assert.True(big.Obstacles!.MedianDropMetres > small.Obstacles!.MedianDropMetres);
        Assert.True(big.Obstacles.PerMetre < small.Obstacles.PerMetre);
    }

    // ── A map's ground water ────────────────────────────────────────────────────────────────────

    /// <summary>A slope with a road and a hollow on a line: rain fills the hollow, which then overflows down the
    /// line; the line's flow rises in the rain and falls after; and the map's state crosses to a client.</summary>
    [Fact]
    public void A_pond_fills_then_overflows_and_the_line_runs()
    {
        var (net, line) = SlopeWithHollow();
        GroundWaterSystem.Register("slope", net);
        Assert.NotEmpty(net.Ponds);
        var pond = net.Ponds.OrderByDescending(p => p.CapacityCubicMetres).First();
        GroundWaterSystem.Settle("slope", 0f);
        Assert.Equal(0f, GroundWaterSystem.PondState("slope", pond.Id).CubicMetres);
        float before = GroundWaterSystem.FlowAt("slope", line);
        float filledAt = float.NaN, spillAt = float.NaN;
        for (int t = 1; t <= 3600; t++)
        {
            GroundWaterSystem.Update("slope", 50f, 0f, 1f);
            var (v, spill) = GroundWaterSystem.PondState("slope", pond.Id);
            if (float.IsNaN(filledAt) && v > 0.5f * pond.CapacityCubicMetres) filledAt = t;
            if (float.IsNaN(spillAt) && spill > 0f) spillAt = t;
        }
        float raining = GroundWaterSystem.FlowAt("slope", line);
        for (int t = 1; t <= 3600; t++) GroundWaterSystem.Update("slope", 0f, 0.1f, 1f);
        float after = GroundWaterSystem.FlowAt("slope", line);
        _o.WriteLine($"pond {pond.CapacityCubicMetres:F1} m³: half full at {filledAt} s, overflowing at {spillAt} s; line {before:F3} -> {raining:F3} -> {after:F3} L/s");
        Assert.False(float.IsNaN(spillAt), "the pond overflows in an hour of 50 mm/h");
        Assert.True(spillAt > filledAt);
        Assert.True(raining > before + 0.1f);
        Assert.True(after < raining);
        var wet = GroundWaterSystem.WetnessAt("slope", Vector3.Zero with { X = 41f, Z = 41f });
        Assert.True(wet.EventMm > 40f);
        Assert.True(GroundWaterSystem.WaterOf("slope")!.Save().Length > 4);
    }

    /// <summary>Water poured at a point runs downhill, further over paving than over grass, reaches what is below
    /// it, and is soaked up along the way.</summary>
    [Fact]
    public void Poured_water_runs_downhill_and_soaks_in()
    {
        float Run(GroundSurface ground, out float reached)
        {
            GroundWaterSystem.Reset();
            const int n = 60;
            var h = new float[n * n];
            var s = new byte[n * n];
            for (int j = 0; j < n; j++) for (int i = 0; i < n; i++) { h[j * n + i] = 20f + 0.05f * j + 0.002f * MathF.Abs(i - 30); s[j * n + i] = (byte)ground; }
            var (flow, fill) = Drainage.Route(h, n, n, 0, 0, n, n, Cell);
            var net = OneTile(h, n, flow, fill, s);
            GroundWaterSystem.Register("pour", net);
            GroundWaterSystem.Settle("pour", 0f);
            Assert.True(GroundWaterSystem.AddWater("pour", new Vector3(61f, 0f, 110f), 10f));
            reached = 0f;
            for (int t = 0; t < 400; t++)
            {
                GroundWaterSystem.Update("pour", 0f, 0f, 0.25f);
                reached += GroundWaterSystem.WaterReaching("pour", new Vector3(61f, 0f, 90f), 3f).PouredLitresPerSecond * 0.25f;
            }
            float wetAt = GroundWaterSystem.WetnessAt("pour", new Vector3(61f, 0f, 104f)).PouredMm;
            Assert.Equal(0f, GroundWaterSystem.PouredRunningLitres("pour"), 2);
            return wetAt;
        }
        float paved = Run(GroundSurface.Impervious, out float pavedReached);
        float grass = Run(GroundSurface.Lawn, out float grassReached);
        _o.WriteLine($"10 L poured: 10 m below it {pavedReached:F1} L passed on paving, {grassReached:F1} L on grass; 6 m below wetted {paved:F2} / {grass:F2} mm");
        Assert.True(pavedReached > 2f, "a good share of a bucket on paving runs 20 m");
        Assert.True(grassReached < pavedReached);
        Assert.True(paved > 0f);
    }

    [Fact]
    public void Rain_wets_the_ground_and_reaches_a_patch()
    {
        var (net, _) = SlopeWithHollow();
        GroundWaterSystem.Register("wet", net);
        GroundWaterSystem.Settle("wet", 0f);
        var dry = GroundWaterSystem.WetnessAt("wet", new Vector3(20f, 0f, 60f));
        Assert.Equal(0f, dry.SoilWetness);
        for (int t = 0; t < 1200; t++) GroundWaterSystem.Update("wet", 25f, 0f, 1f);
        var wet = GroundWaterSystem.WetnessAt("wet", new Vector3(20f, 0f, 60f));
        Assert.True(wet.SoilWetness > 0.01f);
        Assert.Equal(25f, wet.RainMmPerHour);
        var reach = GroundWaterSystem.WaterReaching("wet", new Vector3(20f, 0f, 60f), 2f);
        Assert.Equal(25f * MathF.PI * 4f / 3600f, reach.RainLitresPerSecond, 4);
        Assert.Equal(default, GroundWaterSystem.WetnessAt("nowhere", Vector3.Zero));
        Assert.False(GroundWaterSystem.AddWater("nowhere", Vector3.Zero, 10f));
    }

    /// <summary>A frame of the world builds its network from the tiles it is given, again when one goes.</summary>
    [Fact]
    public void A_frame_s_network_follows_its_tiles()
    {
        const int per = 25;
        DrainageNetwork.TileInput Tile(int x)
        {
            var h = new float[per * per];
            for (int j = 0; j < per; j++) for (int i = 0; i < per; i++) h[j * per + i] = 10f - 0.02f * (x * per + i);
            var (flow, fill) = Drainage.Route(h, per, per, 0, 0, per, per, Cell);
            return new DrainageNetwork.TileInput(x, 0, x * per * Cell, 0f, h,
                new TileDrainage { Cells = per, CellMetres = Cell, Flow = flow, FillCm = fill, Surface = new byte[per * per] });
        }
        GroundWaterSystem.TileArrived("frame", Tile(0));
        GroundWaterSystem.TileArrived("frame", Tile(1));
        var net = GroundWaterSystem.BuildFrameNow("frame");
        Assert.NotNull(net);
        Assert.Equal(2, net!.Tiles.Count);
        Assert.NotNull(GroundWaterSystem.TakeRebuilt("frame"));
        Assert.Null(GroundWaterSystem.TakeRebuilt("frame"));
        GroundWaterSystem.TileLeft("frame", 1, 0);
        Assert.Single(GroundWaterSystem.BuildFrameNow("frame")!.Tiles);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    private static DrainageNetwork OneTile(float[] h, int n, byte[] flow, short[] fill, byte[]? surface = null)
        => new DrainageNetwork { LineSquareMetres = 200f }.Build(new[]
        {
            new DrainageNetwork.TileInput(0, 0, 0f, 0f, h,
                new TileDrainage { Cells = n, CellMetres = Cell, Flow = flow, FillCm = fill, Surface = surface ?? new byte[n * n] }),
        });

    /// <summary>A 120 m slope falling south, a road across its top third, a hollow on its middle line, the line
    /// below it.</summary>
    private static (DrainageNetwork Net, Vector3 Line) SlopeWithHollow()
    {
        const int n = 60;
        var h = new float[n * n];
        var s = new byte[n * n];
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                float r = MathF.Sqrt((i - 30) * (i - 30) + (j - 30) * (j - 30));
                h[j * n + i] = 20f + 0.03f * j + 0.02f * MathF.Abs(i - 30) - (r < 5f ? 0.3f * (1f - r * r / 25f) : 0f);
                s[j * n + i] = (byte)(j is >= 44 and <= 46 ? GroundSurface.Impervious : GroundSurface.Lawn);
            }
        var (flow, fill) = Drainage.Route(h, n, n, 0, 0, n, n, Cell);
        var net = OneTile(h, n, flow, fill, s);
        return (net, new Vector3(30.5f * Cell + 0.5f, 0f, 12.5f * Cell));
    }
}

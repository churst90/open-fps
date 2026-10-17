using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Server.Core;
using Serilog;
using OpenFPS.Geometry;

namespace OpenFPS.Server.Water;

/// <summary>
/// The drainage of a map laid on the survey (Magnolia, Albany; docs/RUNNING_WATER.md 13): worked out when it
/// loads, tile by tile from the graded ground each tile and its margin have, as a world tile is from its
/// survey; the tiles joined into the map's network; and a voice on every stretch of drainage line that could
/// carry enough water to be heard.
/// </summary>
public static class MapDrainage
{
    /// <summary>The cells round a world tile its directions are decided from: the margin its roads are laid with
    /// (WorldFeatures.MarginMetres, 50 m). A map's tiles (and a place's copies in the world) are decided from the
    /// whole place at once (Drainage.WholeGrid), as its ground is graded at once.</summary>
    public const int MarginCells = 25;

    /// <summary>The layer the voices are on: sent only with a tile's full detail (MapTiles.Needs: not a coarse
    /// layer), as the inside of houses is; a ditch is not heard from the far ring.</summary>
    public const string Layer = "runoff";

    /// <summary>The fixed boxes of a world that could cover its ground, in the order the world holds them, as
    /// the rain's raster takes them (SurfaceRaster): plain boxes, not formed solids; nothing that moves, no
    /// player, no region or opening, nothing but a static thing.</summary>
    public static List<SurfaceRaster.Box> Boxes(World world)
    {
        var boxes = new List<SurfaceRaster.Box>();
        var q = new QueryDescription().WithAll<Transform, ColliderComponent>();
        world.Query(in q, (Entity e, ref Transform t, ref ColliderComponent c) =>
        {
            if (c.Shape != ColliderShape.Box || c.Form != null) return;
            if (world.Has<Velocity>(e) || world.Has<PlayerComponent>(e) || world.Has<RegionComponent>(e) || world.Has<PortalComponent>(e)) return;
            if (world.Has<EntityType>(e) && world.Get<EntityType>(e) != EntityType.StaticObject) return;
            string? material = world.Has<MaterialComponent>(e) ? world.Get<MaterialComponent>(e).Material : null;
            boxes.Add(new SurfaceRaster.Box(t.Position, t.Rotation, c.Size, c.IsSolid, material));
        });
        return boxes;
    }

    /// <summary>The network of a map's laid tiles (TerrainBuilder.Lay), its surfaces read off the map's
    /// things, on its world's soil. Null for no tiles.</summary>
    public static DrainageNetwork? Build(World world, IReadOnlyList<TerrainBuilder.Tile> tiles, out TileDrainage[,] perTile,
                                         out (int X0, int Z0) origin, int soilGroup = GroundHydrology.DefaultSoilGroup)
    {
        perTile = new TileDrainage[0, 0];
        origin = (0, 0);
        if (tiles.Count == 0) return null;
        int posts = tiles[0].Posts, cells = posts - 1;
        float spacing = tiles[0].Spacing;
        int tx0 = tiles.Min(t => t.X), tx1 = tiles.Max(t => t.X), tz0 = tiles.Min(t => t.Z), tz1 = tiles.Max(t => t.Z);
        int ntx = tx1 - tx0 + 1, ntz = tz1 - tz0 + 1;
        int px = ntx * cells + 1, pz = ntz * cells + 1;
        var grid = new float[px * pz];
        foreach (var t in tiles)
        {
            int oi = (t.X - tx0) * cells, oj = (t.Z - tz0) * cells;
            for (int j = 0; j < posts; j++) Array.Copy(t.Heights, j * posts, grid, (oj + j) * px + oi, posts);
        }
        float x0 = tiles.First(t => t.X == tx0).CornerX, z0 = tiles.First(t => t.Z == tz0).CornerZ;
        int cx = px - 1, cz = pz - 1;
        var raster = SurfaceRaster.Of(Boxes(world), x0, z0, cx, cz, spacing);
        var drainage = Drainage.OfGrid(grid, px, pz, cells, spacing, (tx, tz) =>
        {
            var s = new byte[cells * cells];
            for (int j = 0; j < cells; j++)
                for (int i = 0; i < cells; i++)
                {
                    byte v = raster[(tz * cells + j) * cx + tx * cells + i];
                    // A place's ground is dirt where the map has nothing on it: open ground.
                    s[j * cells + i] = v == SurfaceRaster.Uncovered ? (byte)GroundSurface.Open : v;
                }
            return s;
        }, Drainage.WholeGrid);
        perTile = drainage;
        origin = (tx0, tz0);

        var inputs = new List<DrainageNetwork.TileInput>();
        var cellHeights = Drainage.CellHeights(grid, px, pz);
        foreach (var t in tiles)
        {
            var h = new float[cells * cells];
            int oi = (t.X - tx0) * cells, oj = (t.Z - tz0) * cells;
            for (int j = 0; j < cells; j++) Array.Copy(cellHeights, (oj + j) * cx + oi, h, j * cells, cells);
            inputs.Add(new DrainageNetwork.TileInput(t.X, t.Z, t.CornerX, t.CornerZ, h, drainage[t.X - tx0, t.Z - tz0]));
        }
        return new DrainageNetwork { SoilGroup = soilGroup }.Build(inputs, PlacedWater(world));
    }

    /// <summary>Where a map already has running or falling water of its own: its creeks, gutters, drains,
    /// fountains and shores, placed by hand. The ground's own lines keep clear of them.</summary>
    public static List<Vector3> PlacedWater(World world)
    {
        var found = new List<Vector3>();
        var q = new QueryDescription().WithAll<Transform, SoundEmitterComponent>();
        world.Query(in q, (ref Transform t, ref SoundEmitterComponent s) =>
        {
            if (ModelKindTable.KindOf(s.SoundId)?.Synth is not (ModelSynth.Flow or ModelSynth.Water or ModelSynth.Shore)) return;
            found.Add(t.Position);
            // A mapped creek all along it: every few metres of its line.
            if (s.Line is { Length: >= 2 } line)
            {
                var turn = t.Rotation.LengthSquared() < 1e-6f ? Quaternion.Identity : t.Rotation;
                for (int i = 1; i < line.Length; i++)
                {
                    var a = t.Position + Vector3.Transform(line[i - 1], turn);
                    var b = t.Position + Vector3.Transform(line[i], turn);
                    int n = Math.Max(1, (int)MathF.Ceiling(Vector3.Distance(a, b) / PlacedLineStepMetres));
                    for (int k = 0; k <= n; k++) found.Add(Vector3.Lerp(a, b, k / (float)n));
                }
            }
        });
        return found;
    }

    /// <summary>The prefabs a map's own creeks and ditches are laid with along their lines (tools/gen_osm.py): their
    /// box's X is their bed's width, and their sound a kind alone until <see cref="VoiceWaterways"/> measures them.</summary>
    public const string WaterwayPrefix = "waterway_";

    /// <summary>A mapped part and its line in the world, upstream first once its chain is known.</summary>
    private sealed class Waterway
    {
        public Entity Entity;
        public Vector3[] Points = Array.Empty<Vector3>();
        public float Width;
        public GroundChannelKind Kind;
        public bool Reversed;
        public float[] HeadAreas = Array.Empty<float>(), EndAreas = Array.Empty<float>();
        public float[] EndLags = Array.Empty<float>();
        public int[] EndPonds = Array.Empty<int>();
        public float EndBase;
    }

    /// <summary>
    /// A map's own creeks heard where it draws them (docs/RUNNING_WATER.md 15.6): every mapped waterway (an entity of
    /// a <see cref="WaterwayPrefix"/> prefab with a line) given its running water from the ground's drainage. Its
    /// parts end to end are one waterway, taken downstream by the survey's heights. What drains to it is what the
    /// map's ground sends its water cells, gathered along it; a creek's bed was shaped by more than the map holds
    /// when its width says so (w = 3 Q^0.5, GroundChannelBed.WidthFor), and the rest comes from upstream, off the map,
    /// as open ground of that size. Each part's key is its own: its width, its slope from its line's heights, its
    /// length, its downstream end's flow and catchment and the share of that flow at its upstream end. How many it
    /// voiced.
    /// </summary>
    public static int VoiceWaterways(World world, DrainageNetwork net, int soilGroup = GroundHydrology.DefaultSoilGroup)
    {
        var parts = new List<Waterway>();
        var q = new QueryDescription().WithAll<Transform, SoundEmitterComponent, IdentityComponent, ColliderComponent>();
        world.Query(in q, (Entity e, ref Transform t, ref SoundEmitterComponent s, ref IdentityComponent id, ref ColliderComponent c) =>
        {
            if (s.Line is not { Length: >= 2 } line || id.PrefabId == null || !id.PrefabId.StartsWith(WaterwayPrefix, StringComparison.Ordinal)) return;
            var turn = t.Rotation.LengthSquared() < 1e-6f ? Quaternion.Identity : t.Rotation;
            var pos = t.Position;
            parts.Add(new Waterway
            {
                Entity = e,
                Points = line.Select(p => pos + Vector3.Transform(p, turn)).ToArray(),
                Width = MathF.Max(0.3f, c.Size.X),
                Kind = id.PrefabId.Contains("ditch", StringComparison.Ordinal) ? GroundChannelKind.Ditch : GroundChannelKind.Creek,
            });
        });
        foreach (var chain in Chains(parts)) Gather(chain, net, soilGroup);
        foreach (var w in parts)
        {
            if (w.EndAreas.Length == 0) continue;
            ref var t = ref world.Get<Transform>(w.Entity);
            ref var s = ref world.Get<SoundEmitterComponent>(w.Entity);
            var points = w.Reversed ? w.Points.Reverse().ToArray() : w.Points;
            float length = 0f;
            for (int i = 1; i < points.Length; i++) length += Vector3.Distance(points[i - 1], points[i]);
            length = MathF.Max(1f, length);
            float slope = MathF.Max(0.001f, (points[0].Y - points[^1].Y) / length);
            var end = new GroundCatchment(w.EndAreas, w.EndLags, w.EndPonds, w.EndBase);
            float endFlow = GroundChannelBed.ReferenceFlow(end, soilGroup);
            float headFlow = GroundChannelBed.ReferenceFlow(new GroundCatchment(w.HeadAreas, 0f), soilGroup);
            float head = endFlow > 0f ? Math.Clamp(headFlow / endFlow, 0.001f, 1f) : 1f;
            string soundId = GroundChannels.SoundId(w.Kind, w.Width, slope, length, endFlow, end, head);
            var spec = ModelLibrary.Presets.Flow(soundId[5..]);
            var turn = t.Rotation.LengthSquared() < 1e-6f ? Quaternion.Identity : t.Rotation;
            var unturn = Quaternion.Inverse(turn);
            var pos = t.Position;
            s.SoundId = soundId;
            s.Line = points.Select(p => Vector3.Transform(p - pos, unturn)).ToArray();
            s.Range = MathF.Max(60f, Loudness.AudibleRange(spec.SourceLevelDb + 6f, Loudness.DefaultCompression));
            s.MinDistance = 1f;
            s.ExtentMetres = spec.ExtentMetres;
        }
        return parts.Count(w => w.EndAreas.Length > 0);
    }

    /// <summary>Mapped parts whose ends meet, end to end, each chain upstream first by the survey's heights (a part
    /// drawn the other way round marked reversed).</summary>
    private static List<List<Waterway>> Chains(List<Waterway> parts)
    {
        const float Join = 0.5f;
        var chains = new List<List<Waterway>>();
        var used = new HashSet<Waterway>();
        Vector3 Start(Waterway w) => w.Reversed ? w.Points[^1] : w.Points[0];
        Vector3 End(Waterway w) => w.Reversed ? w.Points[0] : w.Points[^1];
        foreach (var seed in parts)
        {
            if (!used.Add(seed)) continue;
            var chain = new LinkedList<Waterway>();
            chain.AddFirst(seed);
            for (bool grown = true; grown;)
            {
                grown = false;
                foreach (var w in parts)
                {
                    if (used.Contains(w)) continue;
                    var head = Start(chain.First!.Value);
                    var tail = End(chain.Last!.Value);
                    if (Vector3.Distance(w.Points[0], tail) < Join) { w.Reversed = false; chain.AddLast(w); }
                    else if (Vector3.Distance(w.Points[^1], tail) < Join) { w.Reversed = true; chain.AddLast(w); }
                    else if (Vector3.Distance(w.Points[^1], head) < Join) { w.Reversed = false; chain.AddFirst(w); }
                    else if (Vector3.Distance(w.Points[0], head) < Join) { w.Reversed = true; chain.AddFirst(w); }
                    else continue;
                    used.Add(w);
                    grown = true;
                }
            }
            var list = chain.ToList();
            // Downstream is down.
            if (Start(list[0]).Y < End(list[^1]).Y)
            {
                list.Reverse();
                foreach (var w in list) w.Reversed = !w.Reversed;
            }
            chains.Add(list);
        }
        return chains;
    }

    /// <summary>What drains to a chain's water cells, gathered from upstream down, at each part's ends: the ground's
    /// own, and what a creek's width says comes from upstream off the map.</summary>
    private static void Gather(List<Waterway> chain, DrainageNetwork net, int soilGroup)
    {
        int surfaces = GroundHydrology.Surfaces;
        var areas = new float[surfaces];
        var lagged = new double[surfaces];
        var ponds = new SortedSet<int>();
        float baseArea = 0f;
        var seen = new HashSet<int>();
        var heads = new List<float[]>();
        foreach (var w in chain)
        {
            heads.Add((float[])areas.Clone());
            var points = w.Reversed ? w.Points.Reverse().ToArray() : w.Points;
            float reach = 0.5f * w.Width + net.CellMetres;
            for (int i = 1; i < points.Length; i++)
            {
                var a = points[i - 1];
                var b = points[i];
                var flat = new Vector2(b.X - a.X, b.Z - a.Z);
                float len = flat.Length();
                var across = len > 1e-4f ? new Vector2(-flat.Y, flat.X) / len : Vector2.UnitX;
                int steps = Math.Max(1, (int)MathF.Ceiling(len / (0.5f * net.CellMetres)));
                for (int k = 0; k <= steps; k++)
                {
                    var p = Vector3.Lerp(a, b, k / (float)steps);
                    for (float o = -reach; o <= reach + 1e-3f; o += 0.5f * net.CellMetres)
                    {
                        int c = net.CellAt(p.X + across.X * o, p.Z + across.Y * o);
                        if (c < 0 || net.SurfaceOf(c) != GroundSurface.Water || !seen.Add(c)) continue;
                        Add(c);
                    }
                }
            }
            w.EndAreas = (float[])areas.Clone();
            w.EndLags = Enumerable.Range(0, surfaces).Select(s => areas[s] > 0f ? (float)(lagged[s] / areas[s]) : 0f).ToArray();
            w.EndPonds = ponds.ToArray();
            w.EndBase = baseArea;
        }
        for (int i = 0; i < chain.Count; i++) chain[i].HeadAreas = heads[i];

        // A creek's bed was shaped by its reference flow (GroundChannelBed.WidthFor, without its clamps): what the map
        // does not give it at its downstream end comes from upstream, off the map, as open ground.
        var last = chain[^1];
        if (last.Kind != GroundChannelKind.Creek) return;
        float bed = 1000f * MathF.Pow(last.Width / 3f, 2f);
        float mapped = GroundChannelBed.ReferenceFlow(new GroundCatchment(last.EndAreas, 0f), soilGroup);
        float perSquareMetre = GroundHydrology.RunoffMm(GroundSurface.Open, GroundChannelBed.ReferenceStormMm, soilGroup) / (2f * 3600f);
        if (bed <= mapped || perSquareMetre <= 0f) return;
        float upstream = (bed - mapped) / perSquareMetre;
        // Its water arrives as off a catchment of its size: a channel about one and a half times the square root of
        // its area long, run at a metre a second (a judgement), its lag LagShare of that.
        float lag = GroundHydrology.LagShare * 1.5f * MathF.Sqrt(upstream);
        int open = (int)GroundSurface.Open;
        foreach (var w in chain)
        {
            float before = w.EndAreas[open];
            w.EndLags[open] = (before * w.EndLags[open] + upstream * lag) / MathF.Max(1e-6f, before + upstream);
            w.EndAreas[open] += upstream;
            w.HeadAreas[open] += upstream;
            w.EndBase += upstream;
        }

        void Add(int c)
        {
            var catchment = net.CatchmentOf(c);
            float total = net.SquareMetres(c);
            if (net.LineAreas(c) is { } lineAreas)
            {
                for (int s = 0; s < surfaces; s++) { areas[s] += lineAreas[s]; lagged[s] += lineAreas[s] * (double)catchment.LagOf(s); }
                foreach (int p in catchment.Ponds) ponds.Add(p);
            }
            else
            {
                // Off the lines: the cell's own water and, for what runs onto it, the ground beside it (its own is water).
                areas[(int)GroundSurface.Water] += net.CellArea;
                int beside = (int)BesideSurface(net, c);
                areas[beside] += MathF.Max(0f, total - net.CellArea);
                lagged[beside] += MathF.Max(0f, total - net.CellArea) * (double)catchment.LagOf((int)GroundSurface.Water);
            }
            baseArea += total;
        }
    }

    /// <summary>The commonest surface of the ground round a cell that is not water: open ground if there is none.</summary>
    private static GroundSurface BesideSurface(DrainageNetwork net, int c)
    {
        Span<int> count = stackalloc int[GroundHydrology.Surfaces];
        for (int d = 0; d < 8; d++)
        {
            int n = net.Neighbour(c, d);
            if (n >= 0 && net.SurfaceOf(n) != GroundSurface.Water) count[(int)net.SurfaceOf(n)]++;
        }
        int best = (int)GroundSurface.Open;
        for (int s = 0; s < count.Length; s++) if (count[s] > count[best]) best = s;
        return (GroundSurface)best;
    }

    /// <summary>A placed line is kept clear of every this often along it, m: a third of the drainage's clearance.</summary>
    private const float PlacedLineStepMetres = 5f;

    /// <summary>The sound id of a drainage line's voice: its running water (GroundChannels), from what the line is.</summary>
    public static string SoundId(DrainageNetwork.Voice v)
        => GroundChannels.SoundId(v.Kind, v.WidthMetres, v.Slope, v.LengthMetres, v.ReferenceLitresPerSecond, v.Catchment, v.HeadShare);

    /// <summary>A voice's line in its entity's own frame (unturned, about its position): the line, or null.</summary>
    public static Vector3[]? LineOf(DrainageNetwork.Voice v) => v.Line?.Select(p => p - v.Position).ToArray();

    /// <summary>A voice of a drainage line as an entity of the map: the line's running water along the line itself
    /// (SoundEmitterComponent.Line, from its cells), silent while the line is dry.</summary>
    public static Entity SpawnVoice(World world, DrainageNetwork.Voice v)
    {
        string soundId = SoundId(v);
        var spec = ModelLibrary.Presets.Flow(soundId[5..]);
        string name = spec.Name;
        // A line of its own needs no turn; a straight one runs along x: the yaw that turns x onto the line (x east,
        // z north; yaw about y from north).
        var turn = v.Line != null ? Quaternion.Identity : Quaternion.CreateFromAxisAngle(Vector3.UnitY, v.Yaw - MathF.PI / 2f);
        return world.Create(
            EntityType.StaticObject,
            new Transform { Position = v.Position, Rotation = turn, Scale = Vector3.One },
            new NameComponent { Name = name },
            new IdentityComponent { Name = name, Description = "Water the ground's shape and the rain run along: " + name.ToLowerInvariant() + "." },
            new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(0.2f, 0.2f, 0.2f), IsSolid = false },
            new MaterialComponent { Material = "Water" },
            new SoundEmitterComponent
            {
                IsSynth = true,
                SoundId = soundId,
                Mode = PlaybackMode.LoopOne,
                Volume = 1f,
                Range = MathF.Max(60f, Loudness.AudibleRange(spec.SourceLevelDb + 6f, Loudness.DefaultCompression)),
                MinDistance = 1f,
                ExtentMetres = spec.ExtentMetres,
                Line = LineOf(v),
            });
    }

    /// <summary>
    /// At a map's load (MapManager), after its ground is laid: the network on the world's soil, registered with
    /// the world's ground water for its tick, and its voices spawned into the map, each on <see cref="Layer"/>.
    /// Logs what it found and cost.
    /// </summary>
    public static List<Entity> AtLoad(string mapId, World world, IReadOnlyList<TerrainBuilder.Tile> tiles, Systems.GroundWaterSystem ground)
    {
        var made = new List<Entity>();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var net = Build(world, tiles, out _, out _, ground.SoilGroup);
        if (net == null) return made;
        int mapped = VoiceWaterways(world, net, ground.SoilGroup);
        foreach (var v in net.Voices) made.Add(SpawnVoice(world, v));
        ground.Register(mapId, net);
        Log.Information("MapManager: '{Id}' drains over {Tiles} tiles: {Lines} drainage lines, {Voices} voices ({Km:F1} km), {Mapped} mapped waterways voiced, {Ponds} ponds of {Hollows} hollows ({Culverts} held by a road, through its culvert), {Loops} loops between tiles broken ({Ms} ms).",
                        mapId, tiles.Count, net.Lines.Count, net.Voices.Count, net.Voices.Sum(v => v.LengthMetres) / 1000f, mapped, net.Ponds.Count, net.Hollows,
                        net.Culverts, net.LoopsBroken, clock.ElapsedMilliseconds);
        return made;
    }
}

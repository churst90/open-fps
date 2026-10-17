using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Server.Core;
using Serilog;

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
            if (ModelKindTable.KindOf(s.SoundId)?.Synth is ModelSynth.Flow or ModelSynth.Water or ModelSynth.Shore)
                found.Add(t.Position);
        });
        return found;
    }

    /// <summary>The sound id of a drainage line's voice: its running water (GroundChannels), from what the line is.</summary>
    public static string SoundId(DrainageNetwork.Voice v)
        => GroundChannels.SoundId(v.Kind, v.WidthMetres, v.Slope, v.LengthMetres, v.ReferenceLitresPerSecond, v.Catchment);

    /// <summary>A voice of a drainage line as an entity of the map: the line's running water, along the entity's
    /// own x axis (RunningWaterSynth's line), silent while the line is dry.</summary>
    public static Entity SpawnVoice(World world, DrainageNetwork.Voice v)
    {
        string soundId = SoundId(v);
        var spec = ModelLibrary.Presets.Flow(soundId[5..]);
        string name = spec.Name;
        // The line runs along x: the yaw that turns x onto the line (x east, z north; yaw about y from north).
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, v.Yaw - MathF.PI / 2f);
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
        foreach (var v in net.Voices) made.Add(SpawnVoice(world, v));
        ground.Register(mapId, net);
        Log.Information("MapManager: '{Id}' drains over {Tiles} tiles: {Lines} drainage lines, {Voices} voices, {Ponds} ponds of {Hollows} hollows ({Culverts} held by a road, through its culvert), {Loops} loops between tiles broken ({Ms} ms).",
                        mapId, tiles.Count, net.Lines.Count, net.Voices.Count, net.Ponds.Count, net.Hollows, net.Culverts, net.LoopsBroken,
                        clock.ElapsedMilliseconds);
        return made;
    }
}

using System;
using System.Collections.Generic;
using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Geometry;

namespace OpenFPS.Server.Core;

/// <summary>
/// A map's static geometry as a triangle world (docs/GEOMETRY.md, stage 1), kept beside its spatial grid:
/// built from every fixed solid box at load and whenever the static geometry changes (MapManager.RefreshGrid),
/// door leaves moved every tick they swing, and statics spawned in between taken in once a tick.
/// </summary>
public sealed class ServerGeometry
{
    private readonly TriangleWorldBuilder _builder;
    private readonly List<Entity> _movers = new();
    private readonly List<(int, Vector3, Quaternion)> _poses = new();
    private bool _dirty;

    public ServerGeometry(float tileMetres) => _builder = new TriangleWorldBuilder(tileMetres);

    public TriangleWorld World => _builder.Current;
    public TriangleWorldBuilder Builder => _builder;
    public double LastBuildMs => _builder.LastBuildMs;
    public int LastBuilt => _builder.LastBuilt;
    /// <summary>Of the last rebuild, reading the world's solids, milliseconds.</summary>
    public double LastCollectMs { get; private set; }

    /// <summary>A static was added outside a rebuild: take it in at the next <see cref="Sync"/>.</summary>
    public void MarkDirty() => _dirty = true;

    // ── What the grid and the triangles hold, entity by entity (an incremental RefreshGrid) ─────────
    //
    // A refresh read every fixed thing on the map and filed every one of them in the grid again, and
    // built the triangles' input from all of them: 50 to 120 ms on a town for an item picked up. Each
    // fixed thing is now remembered as it was filed (where, how big, and a hash of everything its solid
    // is made of), and a refresh files again and rebuilds the tiles of only what changed.

    /// <summary>One fixed thing as it was last filed.</summary>
    private readonly record struct Filed(Entity Entity, Vector3 Position, Quaternion Rotation, Vector3 Size, long Hash,
                                         GeometryRole Role, TileKey Tile, SolidSpec Spec);

    private readonly Dictionary<int, Filed> _filed = new();
    private readonly Dictionary<TileKey, Dictionary<int, SolidSpec>> _byTile = new();
    private readonly Dictionary<int, SolidSpec> _moverSpecs = new();
    private readonly Dictionary<int, Entity> _unindexedById = new();
    private readonly HashSet<int> _seen = new();
    private bool _primed;

    /// <summary>Whether the last <see cref="Refresh"/> had a record of everything to compare with.</summary>
    public bool Primed => _primed;
    /// <summary>Of the last refresh: how many fixed things changed, came or went, and how long it took.</summary>
    public int LastChanged { get; private set; }
    public double LastRefreshMs { get; private set; }

    /// <summary>
    /// The grid's static half and the triangles brought up to the world's fixed things, by what changed
    /// since they were last filed: each changed thing taken out of the grid and filed again, each tile that
    /// held or holds one built again, nothing else touched. The same grid and the same triangles as a full
    /// refresh (except the order of the things filed in one cell). Falls back to a full refresh before the
    /// first one.
    /// </summary>
    public void Refresh(World world, SpatialGrid<Entity> grid, Func<SpatialGrid<Entity>, int> fullGrid)
    {
        if (!_primed)
        {
            grid.ClearAll();
            fullGrid(grid);
            Rebuild(world, grid);
            return;
        }
        _world = world;
        _grid = grid;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var dirtyTiles = new HashSet<TileKey>();
        bool moversChanged = false, unindexedChanged = false;
        int changed = 0;
        _seen.Clear();
        int dropped = 0;
        world.Query(new QueryDescription().WithAll<Transform, ColliderComponent>(), (Entity e, ref Transform t, ref ColliderComponent c) =>
        {
            if (world.Has<Velocity>(e) || world.Has<PlayerComponent>(e))
            {
                // The grid's dynamic half, every tick. One that was filed as fixed (spawned fixed, then made
                // a part of something that moves) comes out of the static half; the full refresh cleared it.
                if (grid.RemoveStatic(e)) dropped++;
                return;
            }
            _seen.Add(e.Id);
            long hash = Hash(world, e, c);
            if (_filed.TryGetValue(e.Id, out var was) && was.Entity == e && was.Hash == hash
                && was.Position == t.Position && was.Rotation == t.Rotation && was.Size == c.Size) return;
            changed++;
            if (_filed.ContainsKey(e.Id)) Unfile(e.Id, ref moversChanged, ref unindexedChanged, dirtyTiles);
            grid.RemoveStatic(e);   // a thing IndexEntity filed since: filed again below
            grid.AddOverlapping(t.Position, c.Size, t.Rotation, e, isStatic: true);
            File(world, e, t, c, hash, ref moversChanged, ref unindexedChanged, dirtyTiles);
        });
        List<int>? gone = null;
        foreach (var id in _filed.Keys) if (!_seen.Contains(id)) (gone ??= new List<int>()).Add(id);
        if (gone != null)
            foreach (int id in gone)
            {
                changed++;
                grid.RemoveStatic(_filed[id].Entity);
                Unfile(id, ref moversChanged, ref unindexedChanged, dirtyTiles);
            }
        changed += dropped;
        if (dirtyTiles.Count > 0 || moversChanged)
        {
            var tiles = new Dictionary<TileKey, List<SolidSpec>>(dirtyTiles.Count);
            foreach (var k in dirtyTiles)
                tiles[k] = _byTile.TryGetValue(k, out var specs) ? new List<SolidSpec>(specs.Values) : new List<SolidSpec>();
            _movers.Clear();
            foreach (var id in _moverSpecs.Keys) if (_filed.TryGetValue(id, out var f)) _movers.Add(f.Entity);
            var built = _builder.Rebuild(tiles, Array.Empty<TileKey>(), new List<SolidSpec>(_moverSpecs.Values));
            _placedAt = MoverPoses.Version;
            grid.SetGeometry(built, _unindexedById.Values);
        }
        // What the triangles do not hold, as filed now: also anything the grid was told about since
        // (IndexEntity) that is not fixed after all.
        else if (unindexedChanged || dropped > 0 || grid.Unindexed.Count != _unindexedById.Count)
            grid.SetGeometry(grid.Geometry, _unindexedById.Values);
        _dirty = false;
        LastChanged = changed;
        LastRefreshMs = clock.Elapsed.TotalMilliseconds;
    }

    /// <summary>Everything a fixed thing's place in the grid and the triangles is made of, but its place,
    /// size and turn (compared exactly beside it).</summary>
    private static long Hash(World world, Entity e, in ColliderComponent c)
    {
        var h = new HashCode();
        h.Add(c.Shape); h.Add(c.IsSolid); h.Add(c.Form);
        h.Add(world.Has<EntityType>(e) ? world.Get<EntityType>(e) : EntityType.StaticObject);
        if (world.Has<MaterialComponent>(e)) h.Add(world.Get<MaterialComponent>(e).Material);
        if (world.Has<AcousticComponent>(e))
        {
            var a = world.Get<AcousticComponent>(e);
            h.Add(a.LeafMetres); h.Add(a.StudSpacingMetres); h.Add(a.IsHollow); h.Add(a.ShellThickness); h.Add(a.Absorption);
        }
        if (world.Has<SoundEmitterComponent>(e)) h.Add(string.IsNullOrEmpty(world.Get<SoundEmitterComponent>(e).SoundId));
        if (world.Has<IdentityComponent>(e)) { var id = world.Get<IdentityComponent>(e); h.Add(id.Name); h.Add(id.Announce); }
        if (world.Has<PortalComponent>(e)) { var p = world.Get<PortalComponent>(e); h.Add(p.RegionAId); h.Add(p.RegionBId); }
        return h.ToHashCode();
    }

    /// <summary>A fixed thing filed: its record, and its solid in its tile, as a mover or as tested the old way.</summary>
    private void File(World world, Entity e, in Transform t, in ColliderComponent c, long hash,
                      ref bool moversChanged, ref bool unindexedChanged, HashSet<TileKey> dirtyTiles)
    {
        var type = world.Has<EntityType>(e) ? world.Get<EntityType>(e) : EntityType.StaticObject;
        var portal = world.Has<PortalComponent>(e) ? world.Get<PortalComponent>(e) : default;
        bool announced = world.Has<IdentityComponent>(e) && world.Get<IdentityComponent>(e).Announce;
        var role = EntityGeometry.Classify(type, moves: false, c, portal.RegionAId, portal.RegionBId, announced);
        SolidSpec spec = default;
        var tile = default(TileKey);
        switch (role)
        {
            case GeometryRole.Static:
            case GeometryRole.SightOnly:
                spec = SpecOf(world, e, t, c, leaf: false);
                if (role == GeometryRole.SightOnly) spec = spec with { Surface = EntityGeometry.SightOnly(spec.Surface) };
                tile = _builder.KeyOf(spec);
                if (!_byTile.TryGetValue(tile, out var specs)) _byTile[tile] = specs = new Dictionary<int, SolidSpec>();
                specs[e.Id] = spec;
                dirtyTiles.Add(tile);
                break;
            case GeometryRole.Mover:
                spec = SpecOf(world, e, t, c, leaf: true);
                _moverSpecs[e.Id] = spec;
                moversChanged = true;
                break;
            case GeometryRole.Unindexed:
                _unindexedById[e.Id] = e;
                unindexedChanged = true;
                break;
        }
        _filed[e.Id] = new Filed(e, t.Position, t.Rotation, c.Size, hash, role, tile, spec);
    }

    /// <summary>A fixed thing's record taken out, and its solid with it.</summary>
    private void Unfile(int id, ref bool moversChanged, ref bool unindexedChanged, HashSet<TileKey> dirtyTiles)
    {
        if (!_filed.Remove(id, out var was)) return;
        switch (was.Role)
        {
            case GeometryRole.Static:
            case GeometryRole.SightOnly:
                if (_byTile.TryGetValue(was.Tile, out var specs))
                {
                    specs.Remove(id);
                    if (specs.Count == 0) _byTile.Remove(was.Tile);
                }
                dirtyTiles.Add(was.Tile);
                break;
            case GeometryRole.Mover:
                _moverSpecs.Remove(id);
                moversChanged = true;
                break;
            case GeometryRole.Unindexed:
                _unindexedById.Remove(id);
                unindexedChanged = true;
                break;
        }
    }

    /// <summary>Everything again from the world's fixed solids, into the grid. Unchanged tiles are kept.</summary>
    public void Rebuild(World world, SpatialGrid<Entity> grid)
    {
        _world = world;
        _grid = grid;
        grid.BeforeGeometry ??= PlaceMoversIfMoved;
        var statics = new List<SolidSpec>();
        var movers = new List<SolidSpec>();
        var unindexed = new List<Entity>();
        _movers.Clear();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Collect(world, statics, movers, unindexed, _movers);
        LastCollectMs = clock.Elapsed.TotalMilliseconds;
        _placedAt = MoverPoses.Version;
        // Most changes are not to the solids at all (an item picked up or put down is not one of them): an
        // order-free hash of every solid says so, and the tiles are then not looked at again.
        long fingerprint = Fingerprint(statics, poses: true) ^ (Fingerprint(movers, poses: false) * 31);   // a leaf's pose is its instance's
        var built = fingerprint == _fingerprint && _builder.TileCount > 0 ? _builder.Current : _builder.Build(statics, movers);
        _fingerprint = fingerprint;
        grid.SetGeometry(built, unindexed);
        _dirty = false;
        Prime(world);
    }

    /// <summary>Every fixed thing's record, as the grid and the triangles now hold it.</summary>
    private void Prime(World world)
    {
        _filed.Clear(); _byTile.Clear(); _moverSpecs.Clear(); _unindexedById.Clear();
        bool m = false, u = false;
        var dirty = new HashSet<TileKey>();
        world.Query(new QueryDescription().WithAll<Transform, ColliderComponent>(), (Entity e, ref Transform t, ref ColliderComponent c) =>
        {
            if (world.Has<Velocity>(e) || world.Has<PlayerComponent>(e)) return;
            File(world, e, t, c, Hash(world, e, c), ref m, ref u, dirty);
        });
        _primed = true;
    }

    private long _fingerprint;

    private static long Fingerprint(List<SolidSpec> solids, bool poses)
    {
        long sum = 17;
        foreach (var s in solids)
        {
            var h = new HashCode();
            h.Add(s.Owner); h.Add(s.BoxSize); h.Add(s.Surface); h.Add(s.Mesh?.Hash ?? 0UL);
            if (poses) { h.Add(s.Position); h.Add(s.Rotation); }
            sum += h.ToHashCode();
        }
        return sum ^ solids.Count;
    }

    private World? _world;
    private SpatialGrid<Entity>? _grid;
    private long _placedAt = -1;

    /// <summary>Before the grid hands its geometry out: if anything has moved a leaf since they were last
    /// placed (MoverPoses), every leaf goes where its transform now is.</summary>
    private void PlaceMoversIfMoved()
    {
        if (_world == null || _grid == null || _movers.Count == 0) return;
        long now = MoverPoses.Version;
        if (now == _placedAt) return;
        _placedAt = now;
        PlaceMovers(_world, _grid);
    }

    private void PlaceMovers(World world, SpatialGrid<Entity> grid)
    {
        _poses.Clear();
        foreach (var e in _movers)
        {
            if (!world.IsAlive(e) || !world.Has<Transform>(e)) continue;
            var t = world.Get<Transform>(e);
            _poses.Add((e.Id, t.Position, t.Rotation));
        }
        grid.MoveGeometry(_builder.Move(_poses));
    }

    /// <summary>Once a tick, after the doors and the parts have been placed: door leaves follow their
    /// transforms, and anything spawned since the last build is taken in.</summary>
    public void Sync(World world, SpatialGrid<Entity> grid)
    {
        if (_dirty)
        {
            if (_primed && TriangleGeometry.Incremental) Refresh(world, grid, _ => 0);
            else Rebuild(world, grid);
            return;
        }
        PlaceMoversIfMoved();
    }

    /// <summary>The fixed solids of a world as the triangle world takes them, sorted into its kinds.</summary>
    public static void Collect(World world, List<SolidSpec> statics, List<SolidSpec> movers, List<Entity> unindexed, List<Entity>? moverEntities)
    {
        world.Query(new QueryDescription().WithAll<Transform, ColliderComponent>(), (Entity e, ref Transform t, ref ColliderComponent c) =>
        {
            bool moves = world.Has<Velocity>(e) || world.Has<PlayerComponent>(e);
            var type = world.Has<EntityType>(e) ? world.Get<EntityType>(e) : EntityType.StaticObject;
            var portal = world.Has<PortalComponent>(e) ? world.Get<PortalComponent>(e) : default;
            bool announced = world.Has<IdentityComponent>(e) && world.Get<IdentityComponent>(e).Announce;
            var role = EntityGeometry.Classify(type, moves, c, portal.RegionAId, portal.RegionBId, announced);
            if (role == GeometryRole.None) return;
            if (role == GeometryRole.Unindexed) { unindexed.Add(e); return; }
            var spec = SpecOf(world, e, t, c, role == GeometryRole.Mover);
            if (role == GeometryRole.SightOnly) spec = spec with { Surface = EntityGeometry.SightOnly(spec.Surface) };
            if (role == GeometryRole.Mover) { movers.Add(spec); moverEntities?.Add(e); }
            else statics.Add(spec);
        });
    }

    /// <summary>One entity's solid, from its components exactly as its definition carries them to a client.</summary>
    public static SolidSpec SpecOf(World world, Entity e, in Transform t, in ColliderComponent c, bool leaf)
    {
        var material = world.Has<MaterialComponent>(e) ? world.Get<MaterialComponent>(e).Material : "Generic";
        var a = world.Has<AcousticComponent>(e) ? world.Get<AcousticComponent>(e) : new AcousticComponent();
        bool emitter = world.Has<SoundEmitterComponent>(e) && !string.IsNullOrEmpty(world.Get<SoundEmitterComponent>(e).SoundId);
        string? name = world.Has<IdentityComponent>(e) ? world.Get<IdentityComponent>(e).Name : null;
        var surface = EntityGeometry.SurfaceOf(material, c.Size, a.LeafMetres, a.StudSpacingMetres, a.IsHollow, a.ShellThickness,
                                               a.Absorption, emitter, moves: false, doorLeaf: leaf, name);
        return SolidSpec.Of(e.Id, t.Position, t.Rotation, c.Size, surface, Shapes.Make(c.Form, c.Size));
    }
}

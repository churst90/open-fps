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
        if (_dirty) { Rebuild(world, grid); return; }
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

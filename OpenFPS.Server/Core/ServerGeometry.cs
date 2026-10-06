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

    /// <summary>A static was added outside a rebuild: take it in at the next <see cref="Sync"/>.</summary>
    public void MarkDirty() => _dirty = true;

    /// <summary>Everything again from the world's fixed solids, into the grid. Unchanged tiles are kept.</summary>
    public void Rebuild(World world, SpatialGrid<Entity> grid)
    {
        var statics = new List<SolidSpec>();
        var movers = new List<SolidSpec>();
        var unindexed = new List<Entity>();
        _movers.Clear();
        Collect(world, statics, movers, unindexed, _movers);
        var built = _builder.Build(statics, movers);
        grid.SetGeometry(built, unindexed);
        _dirty = false;
    }

    /// <summary>Once a tick, after the doors and the parts have been placed: door leaves follow their
    /// transforms, and anything spawned since the last build is taken in.</summary>
    public void Sync(World world, SpatialGrid<Entity> grid)
    {
        if (_dirty) { Rebuild(world, grid); return; }
        if (_movers.Count == 0) return;
        _poses.Clear();
        foreach (var e in _movers)
        {
            if (!world.IsAlive(e) || !world.Has<Transform>(e)) continue;
            var t = world.Get<Transform>(e);
            _poses.Add((e.Id, t.Position, t.Rotation));
        }
        grid.MoveGeometry(_builder.Move(_poses));
    }

    /// <summary>The fixed solids of a world as the triangle world takes them, sorted into its kinds.</summary>
    public static void Collect(World world, List<SolidSpec> statics, List<SolidSpec> movers, List<Entity> unindexed, List<Entity>? moverEntities)
    {
        world.Query(new QueryDescription().WithAll<Transform, ColliderComponent>(), (Entity e, ref Transform t, ref ColliderComponent c) =>
        {
            bool moves = world.Has<Velocity>(e) || world.Has<PlayerComponent>(e);
            var type = world.Has<EntityType>(e) ? world.Get<EntityType>(e) : EntityType.StaticObject;
            var portal = world.Has<PortalComponent>(e) ? world.Get<PortalComponent>(e) : default;
            var role = EntityGeometry.Classify(type, moves, c, portal.RegionAId, portal.RegionBId);
            if (role == GeometryRole.None) return;
            if (role == GeometryRole.Unindexed) { unindexed.Add(e); return; }
            var spec = SpecOf(world, e, t, c, role == GeometryRole.Mover);
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
        return new SolidSpec(e.Id, t.Position, t.Rotation, c.Size, surface);
    }
}

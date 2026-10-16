using System.Numerics;
using Arch.Core;
using OpenFPS.Common.Components;
using OpenFPS.Common.Geometry;

namespace OpenFPS.Common;

/// <summary>
/// The server's world as the physics sees it (<see cref="IPhysicsWorld"/>): the grid's triangle world, the
/// statics it does not hold and the moving things filed in the grid this tick, each by its entity's box.
/// One per thread, pointed at a map before each use (<see cref="Of"/>), so a probe allocates nothing.
/// </summary>
public sealed class ServerPhysicsView : IPhysicsWorld
{
    [ThreadStatic] private static ServerPhysicsView? _current;

    private World _world = null!;
    private SpatialGrid<Entity> _grid = null!;
    private TriangleWorld? _triangles;
    private readonly List<Entity> _entities = new(64);
    private readonly HashSet<Entity> _seen = new();
    private readonly Dictionary<int, Entity> _met = new();

    /// <summary>This thread's view, pointed at <paramref name="world"/> and its grid. The triangle world is
    /// read here, once: its movers (door leaves) are brought up to where they stand as it is handed out.</summary>
    public static ServerPhysicsView Of(World world, SpatialGrid<Entity> grid) => (_current ??= new ServerPhysicsView()).Over(world, grid);

    /// <summary>Points this view at a map.</summary>
    public ServerPhysicsView Over(World world, SpatialGrid<Entity> grid)
    {
        _world = world;
        _grid = grid;
        _triangles = TriangleGeometry.Enabled ? grid.Geometry : null;
        return this;
    }

    public TriangleWorld? Triangles => _triangles;

    /// <summary>The server's triangles are rebuilt in place: none is ever stale.</summary>
    public IReadOnlySet<int>? Stale => null;

    public void Near(Vector3 at, float reach, List<PhysicsBody> into)
    {
        into.Clear();
        // With a triangle world, only what it does not answer for: the grid's moving half and the statics the
        // triangles do not hold. Without one, every solid filed round the point. Either way the grid's cells
        // round the point hold every box near it (PhysicsBody.Near), and the rule picks them out.
        if (_triangles != null) _grid.CollectDynamicInRadius(at, reach, _entities, _seen);
        else _grid.CollectInRadius(at, reach, _entities, _seen);
        foreach (var e in _entities)
            if (TryBody(e, out var body) && body.Near(at, reach)) into.Add(body);
        into.Sort(PhysicsBody.ById);
    }

    public void Everything(List<PhysicsBody> into)
    {
        into.Clear();
        var world = _world;
        world.Query(new QueryDescription().WithAll<Transform, ColliderComponent>(), (Entity e) =>
        {
            if (TryBody(e, out var body)) into.Add(body);
        });
        into.Sort(PhysicsBody.ById);
    }

    public void Boxes(Vector3 at, float radius, List<PhysicsBody> into)
    {
        into.Clear();
        _seen.Clear();
        _met.Clear();
        foreach (var e in _grid.GetItemsInRadius(at, radius))
        {
            if (!_seen.Add(e) || !TryBody(e, out var body)) continue;
            _met[e.Id] = e;
            into.Add(body);
        }
    }

    /// <summary>The entity behind a body <see cref="Boxes"/> handed out, for the host to turn a contact into
    /// what is heard.</summary>
    public bool TryEntity(int id, out Entity entity) => _met.TryGetValue(id, out entity) && _world.IsAlive(entity);

    /// <summary>A solid's box. Gone from the world but still in the grid: asking it anything throws (2026-09-28,
    /// 92 "failed to move" errors from the parked cars after something was removed).</summary>
    private bool TryBody(Entity e, out PhysicsBody body)
    {
        body = default;
        var world = _world;
        if (!world.IsAlive(e) || !world.Has<Transform>(e) || !world.Has<ColliderComponent>(e)) return false;
        ref var c = ref world.Get<ColliderComponent>(e);
        if (!c.IsSolid) return false;
        ref var t = ref world.Get<Transform>(e);
        string? material = world.Has<MaterialComponent>(e) ? world.Get<MaterialComponent>(e).Material : null;
        body = PhysicsBody.Of(e.Id, t.Position, c.Size, c.Shape, t.Rotation, material);
        return true;
    }
}

/// <summary>
/// A client's world as the physics sees it (<see cref="IPhysicsWorld"/>): its snapshot's triangle world (built
/// in the background, so with the owners it is rebuilding left out), the statics it does not hold and every
/// moving thing it has been told of, each by its definition's box.
/// </summary>
public sealed class SnapshotPhysicsView : IPhysicsWorld
{
    [ThreadStatic] private static SnapshotPhysicsView? _current;

    private WorldSnapshot _snapshot = null!;
    private TriangleWorld? _triangles;
    private readonly List<int> _ids = new(64);
    private readonly HashSet<int> _idSeen = new(), _gridSeen = new();

    /// <summary>This thread's view, pointed at <paramref name="snapshot"/>.</summary>
    public static SnapshotPhysicsView Of(WorldSnapshot snapshot) => (_current ??= new SnapshotPhysicsView()).Over(snapshot);

    /// <summary>Points this view at a snapshot.</summary>
    public SnapshotPhysicsView Over(WorldSnapshot snapshot)
    {
        _snapshot = snapshot;
        _triangles = snapshot.Geometry != null && TriangleGeometry.Enabled ? snapshot.Geometry : null;
        return this;
    }

    public TriangleWorld? Triangles => _triangles;

    public IReadOnlySet<int>? Stale => _triangles != null ? _snapshot.GeometryStale : null;

    public void Near(Vector3 at, float reach, List<PhysicsBody> into) => Collect(at, reach, into, held: false);

    public void Everything(List<PhysicsBody> into)
    {
        into.Clear();
        foreach (var s in _snapshot.Entities.Values)
            if (TryBody(s, out var body)) into.Add(body);
        into.Sort(PhysicsBody.ById);
    }

    public void Boxes(Vector3 at, float radius, List<PhysicsBody> into) => Collect(at, radius, into, held: true);

    /// <summary>The statics round the point (those the triangles hold too when <paramref name="held"/>, or
    /// with no triangle world), then every moving thing, those near; each once, by id.</summary>
    private void Collect(Vector3 at, float reach, List<PhysicsBody> into, bool held)
    {
        into.Clear();
        _idSeen.Clear();
        var snap = _snapshot;
        if (_triangles != null && !held)
        {
            foreach (int id in snap.UnindexedStatics) Add(id);
        }
        else if (snap.StaticGrid != null)
        {
            snap.StaticGrid.CollectInRadius(at, reach, _ids, _gridSeen);
            foreach (int id in _ids) Add(id);
        }
        else
        {
            // No grid yet (between a map arriving and its first build): every solid.
            foreach (var s in snap.Entities.Values)
                if (_idSeen.Add(s.Id) && TryBody(s, out var body) && body.Near(at, reach)) into.Add(body);
        }
        foreach (var s in snap.DynamicEntities)
            if (_idSeen.Add(s.Id) && TryBody(s, out var body) && body.Near(at, reach)) into.Add(body);
        into.Sort(PhysicsBody.ById);

        void Add(int id)
        {
            if (_idSeen.Add(id) && snap.Entities.TryGetValue(id, out var s) && TryBody(s, out var body) && body.Near(at, reach)) into.Add(body);
        }
    }

    private static bool TryBody(in EntitySnapshot s, out PhysicsBody body)
    {
        body = default;
        var def = s.Definition;
        if (def == null || !def.Collider.IsSolid) return false;
        body = PhysicsBody.Of(s.Id, s.Transform.Position, def.Collider.Size, def.Collider.Shape, s.Transform.Rotation, def.Material.Material);
        return true;
    }
}

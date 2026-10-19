using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Geometry;
using OpenFPS.Physics;
using OpenFPS.Server.Repositories;
using OpenFPS.Server.Systems;
using OpenFPS.Sound;

namespace OpenFPS.Server.Core;

/// <summary>
/// The server's world as the people's laws see it (docs/ENGINE_PACKAGES.md, stage 10): the lists of people,
/// players, vehicles, doors, regions, portals and named boxes in entity-id order, the lookups by id, and the
/// ground, place and room a point is in. The physics questions go to <see cref="ServerPhysicsView"/>, the one
/// view the server already fills, so there is not a second physics. Pointed at a map's world and grid before
/// each host steps; without a grid it answers the people/door/place half only.
/// </summary>
public sealed class ServerPeopleView : IPeopleWorld
{
    [ThreadStatic] private static ServerPeopleView? _current;

    private World _world = null!;
    private SpatialGrid<Entity>? _grid;

    /// <summary>This thread's view, pointed at a world and its grid (null for a test world with no physics).</summary>
    public static ServerPeopleView Of(World world, SpatialGrid<Entity>? grid)
        => (_current ??= new ServerPeopleView()).Over(world, grid);

    /// <summary>Points this view at a map.</summary>
    public ServerPeopleView Over(World world, SpatialGrid<Entity>? grid)
    {
        _world = world;
        _grid = grid;
        return this;
    }

    private ServerPhysicsView Physics() => _grid != null
        ? ServerPhysicsView.Of(_world, _grid)
        : throw new InvalidOperationException("This people view has no grid: the people, doors and places only.");

    public TriangleWorld? Triangles => Physics().Triangles;
    public IReadOnlySet<int>? Stale => Physics().Stale;
    public void Near(Vector3 at, float reach, List<PhysicsBody> into) => Physics().Near(at, reach, into);
    public void Everything(List<PhysicsBody> into) => Physics().Everything(into);
    public void Boxes(Vector3 at, float radius, List<PhysicsBody> into) => Physics().Boxes(at, radius, into);

    public void People(List<PersonBody> into)
    {
        into.Clear();
        _world.Query(new QueryDescription().WithAll<Transform, Velocity, Pedestrian>(),
            (Entity e, ref Transform t, ref Velocity v, ref Pedestrian p) => into.Add(PersonOf(e, t, v, p)));
        into.Sort(static (a, b) => a.Id.CompareTo(b.Id));
    }

    public void Players(List<PlayerBody> into)
    {
        into.Clear();
        _world.Query(new QueryDescription().WithAll<Transform, PlayerComponent>(),
            (Entity e, ref Transform t, ref PlayerComponent p) => into.Add(new PlayerBody(e.Id, t.Position, p.IsInVehicle)));
        into.Sort(static (a, b) => a.Id.CompareTo(b.Id));
    }

    public void Vehicles(List<VehicleView> into)
    {
        into.Clear();
        _world.Query(new QueryDescription().WithAll<Transform, VehicleComponent>(),
            (Entity e, ref Transform t, ref VehicleComponent v) =>
            {
                var vel = _world.Has<Velocity>(e) ? _world.Get<Velocity>(e).Linear : Vector3.Zero;
                var seats = _world.Has<OccupancyComponent>(e) && _world.Get<OccupancyComponent>(e).Seats is { } s
                    ? s.Select(static seat => new SeatBody(seat.Name, seat.LocalPosition, seat.LocalYaw, seat.Controls)).ToArray()
                    : Array.Empty<SeatBody>();
                bool serving = _world.Has<SoundEmitterComponent>(e) && _world.Get<SoundEmitterComponent>(e).ServingStop;
                into.Add(new VehicleView(e.Id, t.Position, t.Rotation, vel,
                    new Vector2(vel.X, vel.Z).Length(), serving, v.VehicleType, seats));
            });
        into.Sort(static (a, b) => a.Id.CompareTo(b.Id));
    }

    public void Doors(List<DoorBody> into)
    {
        into.Clear();
        _world.Query(new QueryDescription().WithAll<Transform, DoorComponent>(),
            (Entity e, ref Transform t, ref DoorComponent d) => into.Add(DoorOf(e, t, d)));
        into.Sort(static (a, b) => a.Id.CompareTo(b.Id));
    }

    public void Regions(List<RegionBody> into)
    {
        into.Clear();
        var floods = MapManager.RoomsOf(_world);
        _world.Query(new QueryDescription().WithAll<Transform, RegionComponent>(),
            (Entity e, ref Transform t, ref RegionComponent r) =>
            {
                float air;
                if (floods != null && floods.TryAir(e.Id, out _, out _, out float flooded)) air = flooded;
                else
                {
                    var s = r.RoomSize;
                    air = s.X > 0f && s.Y > 0f && s.Z > 0f ? s.X * s.Y * s.Z : float.MaxValue;
                }
                into.Add(new RegionBody(e.Id, r.IsIndoor, t.Position, r.RoomSize, r.FriendlyName, air));
            });
        into.Sort(static (a, b) => a.Id.CompareTo(b.Id));
    }

    public void Portals(List<PortalBody> into)
    {
        into.Clear();
        _world.Query(new QueryDescription().WithAll<PortalComponent>(),
            (Entity e, ref PortalComponent p) => into.Add(new PortalBody(e.Id, p.RegionAId, p.RegionBId)));
        into.Sort(static (a, b) => a.Id.CompareTo(b.Id));
    }

    public void NamedBoxes(List<NamedBox> into)
    {
        into.Clear();
        _world.Query(new QueryDescription().WithAll<Transform, ColliderComponent>(),
            (Entity e, ref Transform t, ref ColliderComponent c) => into.Add(new NamedBox(
                e.Id, NameOf(e), t.Position, t.Rotation, c.Size, c.Shape, c.IsSolid,
                _world.Has<Velocity>(e), _world.Has<DoorComponent>(e))));
        into.Sort(static (a, b) => a.Id.CompareTo(b.Id));
    }

    public bool TryPerson(int id, out PersonBody person)
    {
        var found = default(PersonBody);
        bool any = false;
        _world.Query(new QueryDescription().WithAll<Transform, Velocity, Pedestrian>(),
            (Entity e, ref Transform t, ref Velocity v, ref Pedestrian p) =>
            {
                if (any || e.Id != id) return;
                found = PersonOf(e, t, v, p);
                any = true;
            });
        person = found;
        return any;
    }

    public bool TryDoor(int id, out DoorBody door)
    {
        var found = default(DoorBody);
        bool any = false;
        _world.Query(new QueryDescription().WithAll<Transform, DoorComponent>(),
            (Entity e, ref Transform t, ref DoorComponent d) =>
            {
                if (any || e.Id != id) return;
                found = DoorOf(e, t, d);
                any = true;
            });
        door = found;
        return any;
    }

    public bool Doorway(int doorId, out DoorwayView doorway)
    {
        if (!TryDoor(doorId, out var door)) { doorway = default; return false; }
        doorway = door.Doorway;
        return true;
    }

    public bool InDoorway(int doorId, Vector3 at)
        => Doorway(doorId, out var doorway)
           && DoorSystem.InDoorway(at, doorway.Centre, doorway.Rotation, doorway.HalfWidth, doorway.Slides);

    public bool OpenEnough(int doorId) => TryDoor(doorId, out var door) && door.Openness >= 0.9f;

    public bool OpensByHand(int doorId) => TryDoor(doorId, out var door) && !door.Powered;

    public int RegionAt(Vector3 at) => MapManager.RegionAt(_world, at);

    public string? PlaceAt(Vector3 at) => CommandHandler.PlaceAt(_world, at);

    public float Ground(Vector3 at)
        => _grid == null ? at.Y : PhysicsUtils.GetGroundHeight(_world, _grid, at, out _);

    private PersonBody PersonOf(Entity e, in Transform t, in Velocity v, in Pedestrian p)
    {
        bool carried = _world.Has<OccupantComponent>(e);
        var seat = carried ? _world.Get<OccupantComponent>(e) : default;
        return new PersonBody(e.Id, p.Voice, p.Pair, p.Character, t.Position, t.Rotation, v.Linear,
            new Vector2(v.Linear.X, v.Linear.Z).Length(), _world.Has<DeadComponent>(e), _world.Has<FrozenComponent>(e),
            !carried, carried ? seat.RootEntityId : -1, carried ? seat.SeatIndex : -1);
    }

    private DoorBody DoorOf(Entity e, in Transform t, in DoorComponent d)
    {
        DoorSystem.Doorway(_world, e, out var centre, out var rotation);
        float half = _world.Has<ColliderComponent>(e) ? _world.Get<ColliderComponent>(e).Size.X * 0.5f : d.Aperture * 0.5f;
        var leaf = _world.Has<ColliderComponent>(e)
            ? _world.Get<ColliderComponent>(e).Size
            : new Vector3(d.Aperture, 2f, 0.05f);
        int a = -1, b = -1;
        if (_world.Has<PortalComponent>(e))
        {
            var portal = _world.Get<PortalComponent>(e);
            a = portal.RegionAId; b = portal.RegionBId;
        }
        return new DoorBody(e.Id, new DoorwayView(centre, rotation, half, d.Slides), NameOf(e), d.KeyedSide,
            leaf, (DoorKind)d.Kind, d.Openness, d.Target, d.Powered, d.SensorMetres, d.CloseAfterSeconds,
            a, b, d.HandId, _world.Has<DeadComponent>(e));
    }

    private string NameOf(Entity e)
        => _world.Has<IdentityComponent>(e) && !string.IsNullOrWhiteSpace(_world.Get<IdentityComponent>(e).Name)
            ? _world.Get<IdentityComponent>(e).Name
            : _world.Has<NameComponent>(e) ? _world.Get<NameComponent>(e).Name ?? "" : "";
}

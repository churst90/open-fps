using System.Numerics;
using Arch.Core;
using OpenFPS.Common.Components;
using OpenFPS.Geometry;
using OpenFPS.Physics;
using OpenFPS.Server.Core;
using OpenFPS.Server.Systems;
using OpenFPS.Sound;

namespace OpenFPS.Tests;

/// <summary>
/// The view the people's laws read (docs/ENGINE_PACKAGES.md, stage 10): the server fills it from a small world,
/// every list in entity-id order, and the lookups and store facts answer about that world. It is one view of
/// the physics, not a second, and without a grid it answers the people, doors and places only.
/// </summary>
public class PeopleWorldViewTests : IDisposable
{
    private readonly World _world = World.Create();
    private readonly SpatialGrid<Entity> _grid = new(10f);
    private readonly Entity _floor, _sidewalk, _wall, _door, _room, _hall, _person, _rider, _player, _bus;

    private static Transform At(Vector3 p, Quaternion? r = null)
        => new() { Position = p, Rotation = r ?? Quaternion.Identity, Scale = Vector3.One };

    public PeopleWorldViewTests()
    {
        _floor = _world.Create(At(new Vector3(0f, -0.5f, 0f)),
            new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(100f, 1f, 100f), IsSolid = true },
            new MaterialComponent { Material = "Asphalt" }, new NameComponent { Name = "ground" });
        _grid.AddOverlapping(new Vector3(0f, -0.5f, 0f), new Vector3(100f, 1f, 100f), _floor, isStatic: true);

        _sidewalk = _world.Create(At(new Vector3(4f, 0.05f, 0f)),
            new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(2f, 0.1f, 20f), IsSolid = true },
            new MaterialComponent { Material = "Concrete" }, new NameComponent { Name = "sidewalk" });
        _wall = _world.Create(At(new Vector3(-3f, 1.5f, 0f)),
            new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(1f, 3f, 1f), IsSolid = true },
            new MaterialComponent { Material = "Brick" }, new IdentityComponent { Name = "wall" });
        _grid.AddOverlapping(new Vector3(-3f, 1.5f, 0f), new Vector3(1f, 3f, 1f), _wall, isStatic: true);
        _grid.AddOverlapping(new Vector3(4f, 0.05f, 0f), new Vector3(2f, 0.1f, 20f), _sidewalk, isStatic: true);

        _room = _world.Create(At(new Vector3(0f, 1.5f, -4f)),
            new RegionComponent { FriendlyName = "Marlow Tower lobby", IsIndoor = true, RoomSize = new Vector3(6f, 3f, 8f) });
        _hall = _world.Create(At(new Vector3(0f, 1.5f, 6f)),
            new RegionComponent { FriendlyName = "Marlow Tower hall", IsIndoor = true, RoomSize = new Vector3(20f, 3f, 10f) });
        _world.Create(new PortalComponent { RegionAId = _hall.Id, RegionBId = -1 });

        _door = _world.Create(At(new Vector3(0f, 1.05f, 0f)),
            new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(0.9f, 2.1f, 0.05f), IsSolid = true },
            new MaterialComponent { Material = "Wood" }, new IdentityComponent { Name = "Marlow Tower front entrance" },
            new DoorComponent { Kind = (int)DoorKind.Hinged, Aperture = 0.9f, KeyedSide = -1f, Openness = 0.5f, Target = 1f },
            new PortalComponent { RegionAId = _room.Id, RegionBId = -1 });
        _grid.AddOverlapping(new Vector3(0f, 1.05f, 0f), new Vector3(0.9f, 2.1f, 0.05f), _door, isStatic: false);

        _person = _world.Create(At(new Vector3(1f, 0f, 0f)), new Velocity { Linear = new Vector3(1.2f, 0f, 0f) },
            new Pedestrian { Voice = "alex", Pair = "", Character = "Alex" });
        _bus = _world.Create(At(new Vector3(10f, 0f, 5f)), new Velocity(), new VehicleComponent { VehicleType = "bus" },
            new OccupancyComponent
            {
                Seats = new List<Seat> { new() { Name = "driver", Controls = true }, new() { Name = "passenger", LocalPosition = new Vector3(0.5f, 0f, 1f) } },
            },
            new SoundEmitterComponent { ServingStop = true });
        _rider = _world.Create(At(new Vector3(10.5f, 0f, 6f)), new Velocity(),
            new Pedestrian { Voice = "ben", Pair = "", Character = "" },
            new OccupantComponent { RootEntityId = _bus.Id, SeatIndex = 1 });
        _player = _world.Create(At(new Vector3(2f, 0f, 2f)), new Velocity(),
            new PlayerComponent { Username = "tester" });

        _world.Create(At(new Vector3(30f, 2f, 30f)),
            new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(10f, 5f, 10f), IsSolid = false },
            new IdentityComponent { Name = "Market Square", PrefabId = CommandHandler.NamedPlacePrefab });
    }

    public void Dispose() => World.Destroy(_world);

    [Fact]
    public void EveryListIsTheWorldsAndInEntityIdOrder()
    {
        var view = ServerPeopleView.Of(_world, _grid);

        var people = new List<PersonBody>();
        view.People(people);
        Assert.Equal(new[] { _person.Id, _rider.Id }.OrderBy(i => i), people.Select(p => p.Id));
        var alex = people.Single(p => p.Id == _person.Id);
        Assert.Equal(("alex", "", "Alex"), (alex.Voice, alex.Pair, alex.Character));
        Assert.True(alex.OnFoot);
        Assert.Equal(-1, alex.RootId);
        Assert.Equal(1.2f, alex.Speed, 3);
        Assert.Equal(new Vector3(1.2f, 0f, 0f), alex.Velocity);
        var rider = people.Single(p => p.Id == _rider.Id);
        Assert.False(rider.OnFoot);
        Assert.Equal((_bus.Id, 1), (rider.RootId, rider.SeatIndex));

        var players = new List<PlayerBody>();
        view.Players(players);
        Assert.Equal(new[] { _player.Id }, players.Select(p => p.Id));
        Assert.False(players[0].InVehicle);
        Assert.Equal(new Vector3(2f, 0f, 2f), players[0].Position);

        var vehicles = new List<VehicleView>();
        view.Vehicles(vehicles);
        var bus = Assert.Single(vehicles);
        Assert.Equal((_bus.Id, "bus", true), (bus.Id, bus.Type, bus.ServingStop));
        Assert.Equal(new[] { "driver", "passenger" }, bus.Seats.Select(s => s.Name));
        Assert.True(bus.Seats[0].Controls);
        Assert.False(bus.Seats[1].Controls);
        Assert.Equal(new Vector3(0.5f, 0f, 1f), bus.Seats[1].LocalPosition);

        var doors = new List<DoorBody>();
        view.Doors(doors);
        var door = Assert.Single(doors);
        Assert.Equal((_door.Id, "Marlow Tower front entrance"), (door.Id, door.Name));
        Assert.Equal(0.45f, door.Doorway.HalfWidth, 4);
        Assert.Equal((_room.Id, -1), (door.RegionAId, door.RegionBId));
        Assert.Equal(0.5f, door.Openness, 4);
        Assert.True(door.Powered == false);
        Assert.Equal(DoorKind.Hinged, door.Kind);

        var regions = new List<RegionBody>();
        view.Regions(regions);
        Assert.Equal(new[] { _room.Id, _hall.Id }.OrderBy(i => i), regions.Select(r => r.Id));
        Assert.All(regions, r => Assert.True(r.Indoor));
        Assert.Equal(6f * 3f * 8f, regions.Single(r => r.Id == _room.Id).Air, 3);
        Assert.Equal("Marlow Tower lobby", regions.Single(r => r.Id == _room.Id).Name);

        var portals = new List<PortalBody>();
        view.Portals(portals);
        Assert.Equal(2, portals.Count);
        Assert.Contains(portals, p => p.RegionAId == _room.Id && p.RegionBId == -1);
        Assert.Contains(portals, p => p.RegionAId == _hall.Id && p.RegionBId == -1);
        Assert.Equal(portals.Select(p => p.Id).OrderBy(i => i), portals.Select(p => p.Id));

        var boxes = new List<NamedBox>();
        view.NamedBoxes(boxes);
        Assert.Equal(boxes.Select(b => b.Id).OrderBy(i => i), boxes.Select(b => b.Id));
        var sidewalk = boxes.Single(b => b.Name == "sidewalk");
        Assert.Equal((new Vector3(2f, 0.1f, 20f), false, false), (sidewalk.Size, sidewalk.Moves, sidewalk.IsDoor));
        Assert.True(boxes.Single(b => b.Id == _door.Id).IsDoor);
    }

    [Fact]
    public void TheLookupsAndStoreFactsAnswerAboutThatWorld()
    {
        var view = ServerPeopleView.Of(_world, _grid);

        Assert.True(view.TryPerson(_person.Id, out var person));
        Assert.Equal("alex", person.Voice);
        Assert.False(view.TryPerson(-123, out _));
        Assert.True(view.TryDoor(_door.Id, out var door));
        Assert.Equal("Marlow Tower front entrance", door.Name);
        Assert.False(view.TryDoor(-123, out _));

        Assert.True(view.Doorway(_door.Id, out var doorway));
        Assert.Equal(new Vector3(0f, 1.05f, 0f), doorway.Centre);
        Assert.Equal(0.45f, doorway.HalfWidth, 4);
        Assert.True(view.InDoorway(_door.Id, new Vector3(0.1f, 1f, 0.2f)));
        Assert.False(view.InDoorway(_door.Id, new Vector3(0.1f, 1f, 5f)));
        Assert.False(view.OpenEnough(_door.Id));
        Assert.True(view.OpensByHand(_door.Id));
        Assert.False(view.Doorway(-123, out _));
        Assert.False(view.OpenEnough(-123));
        Assert.False(view.OpensByHand(-123));

        Assert.Equal(_room.Id, view.RegionAt(new Vector3(0f, 1f, -4f)));
        Assert.Equal("Market Square", view.PlaceAt(new Vector3(30f, 2f, 30f)));
        Assert.Equal(0f, view.Ground(new Vector3(1f, 5f, 1f)), 4);

        // The physics questions are the one server physics view's: every solid, by id.
        var solids = new List<PhysicsBody>();
        view.Everything(solids);
        Assert.Equal(new[] { _floor.Id, _sidewalk.Id, _wall.Id, _door.Id }.OrderBy(i => i), solids.Select(b => b.Id));
        Assert.Equal("Asphalt", solids.Single(b => b.Id == _floor.Id).Material);
    }

    [Fact]
    public void WithoutAGridItAnswersThePeopleDoorsAndPlacesOnly()
    {
        var view = ServerPeopleView.Of(_world, null);
        var people = new List<PersonBody>();
        view.People(people);
        Assert.Equal(2, people.Count);
        Assert.Equal(5f, view.Ground(new Vector3(0f, 5f, 0f)), 4);
        Assert.Throws<InvalidOperationException>(() => view.Everything(new List<PhysicsBody>()));
    }
}

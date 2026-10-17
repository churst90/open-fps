using System.Numerics;
using System.Text.Json;
using Arch.Core;
using OpenFPS.Geometry;
using OpenFPS.Common.Components;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using OpenFPS.Server.Systems;

namespace OpenFPS.Tests;

/// <summary>
/// Every map is its own world and entity ids start again in each, so state kept by id alone is shared by
/// a car on one map and whatever has the same id on another. Each of these loads two maps whose first
/// entities have the same ids and asks after one of them.
/// </summary>
public class MapScopedStateTests : IDisposable
{
    private const float Dt = 1f / 30f;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "openfps-mapids-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static List<Vector3> Circle(float r, int n = 48)
        => Enumerable.Range(0, n).Select(k =>
        {
            double a = 2 * Math.PI * k / n;
            return new Vector3((float)(r * Math.Sin(a)), 0.1f, (float)(r * Math.Cos(a)));
        }).ToList();

    private static MapData Map(string id, VehicleData first) => new()
    {
        Id = id,
        MinBound = new Vector3(-400, -10, -400),
        MaxBound = new Vector3(400, 50, 400),
        Tracks = new List<TrackData> { new() { Id = "loop", Waypoints = Circle(60f), WidthMetres = 12f } },
        Vehicles = new List<VehicleData> { first },
    };

    /// <summary>Two maps, alpha with a car lapping and beta with somebody walking, spawned in that order.</summary>
    private (MapManager Maps, VehicleSystem Vehicles, World Alpha, Entity Car, World Beta, Entity Walker) TwoMaps()
    {
        string maps = Path.Combine(_dir, "maps");
        Directory.CreateDirectory(maps);
        var car = new VehicleData
        {
            Name = "Car", Preset = "i4_economy", Track = "loop", TopSpeedKmh = 80f, CorneringG = 0.5f,
            AccelerationMps2 = 3f, BrakingMps2 = 4f,
        };
        var walker = new VehicleData
        {
            Name = "Walker", Preset = "walker", RoadStart = new Vector3(20, 0.1f, 0), RoadEnd = new Vector3(20, 0.1f, 100),
            SpeedsKmh = new[] { 5f }, AccelerationMps2 = 1f, BrakingMps2 = 1f,
        };
        File.WriteAllText(Path.Combine(maps, "alpha.json"), JsonSerializer.Serialize(Map("alpha", car), MapRepository.JsonOptions));
        File.WriteAllText(Path.Combine(maps, "beta.json"), JsonSerializer.Serialize(Map("beta", walker), MapRepository.JsonOptions));
        var prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
        var manager = new MapManager(new MapRepository(maps), prefabs);
        manager.Initialize();
        var vehicles = new VehicleSystem();
        vehicles.Spawn(manager);
        Assert.True(manager.TryGetMap("alpha", out World alpha, out _, out _, out _));
        Assert.True(manager.TryGetMap("beta", out World beta, out _, out _, out _));
        return (manager, vehicles, alpha, Named(alpha, "Car"), beta, Named(beta, "Walker"));
    }

    private static Entity Named(World world, string name)
    {
        Entity found = Entity.Null;
        world.Query(new QueryDescription().WithAll<NameComponent>(), (Entity e, ref NameComponent n) =>
        {
            if (n.Name == name) found = e;
        });
        Assert.NotEqual(Entity.Null, found);
        return found;
    }

    /// <summary>The broadcast asks for a car's wheels and tyre demand by its map and id: alpha's car keeps
    /// its own although beta's walker, spawned after it, has the same id.</summary>
    [Fact]
    public void TrafficWheelsAndTyreDemandAreTheCarsOwnWhateverAnotherMapHasAtItsId()
    {
        var (_, vehicles, alpha, car, beta, walker) = TwoMaps();
        Assert.Equal(car.Id, walker.Id);             // the collision this is about

        for (int i = 0; i < 90; i++)
        {
            vehicles.Update("alpha", alpha, Dt);
            vehicles.Update("beta", beta, Dt);
        }

        Assert.True(vehicles.TryGetWheels("alpha", car.Id, out var wheels), "alpha's car has no wheels on the wire");
        Assert.NotNull(wheels);
        Assert.Contains(wheels!, w => w.AngularSpeedRadPerSec > 1f);
        Assert.True(vehicles.TryGetTyreDemand("alpha", car.Id, out float demand));
        Assert.True(demand > 0f, "alpha's car, lapping a 60 m circle, works its tyres not at all");

        Assert.False(vehicles.TryGetWheels("beta", walker.Id, out _), "beta's walker has alpha's car's wheels");
        Assert.True(vehicles.Moves("beta", walker.Id));

        // The admin gun forgets beta's walker before vaporizing it, and alpha's car goes on lapping.
        Assert.True(vehicles.Forget("beta", walker.Id), "beta's walker was not one of the traffic's");
        Assert.False(vehicles.Moves("beta", walker.Id));
        Assert.True(vehicles.Moves("alpha", car.Id));
        Assert.True(vehicles.TryGetWheels("alpha", car.Id, out _), "forgetting beta's walker forgot alpha's car");
    }

    /// <summary>A driven car's whole simulation is its own: a parked car with the same id on another map,
    /// stepped after it, does not stop its wheels.</summary>
    [Fact]
    public void ADrivenCarsWheelsAreNotAParkedCarsWithTheSameIdOnAnotherMap()
    {
        var one = World.Create();
        var two = World.Create();
        try
        {
            Entity Car(World w, float speed) => w.Create(
                new Transform { Position = new Vector3(0, 0, 0), Rotation = Quaternion.Identity },
                new DriveComponent { Preset = "i4_economy", Speed = speed, EngineOn = true, EngineOnFor = 10f },
                new Velocity { Linear = new Vector3(0, 0, speed) });
            var moving = Car(one, 20f);
            var parked = Car(two, 0f);
            Assert.Equal(moving.Id, parked.Id);

            var gridOne = new SpatialGrid<Entity>(10f);
            var gridTwo = new SpatialGrid<Entity>(10f);
            var lo = new Vector3(-5000, -100, -5000);
            var hi = new Vector3(5000, 100, 5000);
            for (int i = 0; i < 10; i++)
            {
                DrivingSystem.Update(one, gridOne, lo, hi, Dt);
                DrivingSystem.Update(two, gridTwo, lo, hi, Dt);
            }

            Assert.True(one.Get<DriveComponent>(moving).Speed > 15f);
            Assert.True(DrivingSystem.TryGetWheels(one, moving, out var wheels));
            Assert.Contains(wheels!, w => w.AngularSpeedRadPerSec > 20f);
        }
        finally
        {
            World.Destroy(one);
            World.Destroy(two);
        }
    }

    /// <summary>A player sitting in a car that is not turning keeps facing where they face, whatever a bus
    /// with the same id on another map is doing.</summary>
    [Fact]
    public void ARiderTurnsOnlyWithWhatTheyAreRidingIn()
    {
        var one = World.Create();
        var two = World.Create();
        try
        {
            Entity Root(World w, float yaw) => w.Create(
                new Transform { Position = Vector3.Zero, Rotation = Quaternion.CreateFromYawPitchRoll(yaw, 0f, 0f) },
                new OccupancyComponent { Seats = new List<Seat> { new() { Name = "driver", LocalPosition = Vector3.Zero } } });
            var car = Root(one, 0f);
            var bus = Root(two, 1.5f);
            Assert.Equal(car.Id, bus.Id);
            var rider = one.Create(new Transform(), new PlayerComponent { Username = "cody", Yaw = 0.3f },
                                   new OccupantComponent { RootEntityId = car.Id, SeatIndex = 0 });
            var alex = two.Create(new Transform(), new OccupantComponent { RootEntityId = bus.Id, SeatIndex = 0 });
            var lookupOne = new Dictionary<int, Entity> { [car.Id] = car, [rider.Id] = rider };
            var lookupTwo = new Dictionary<int, Entity> { [bus.Id] = bus, [alex.Id] = alex };

            var seats = new OccupancySystem();
            for (int i = 0; i < 5; i++)
            {
                seats.Update(one, lookupOne);
                seats.Update(two, lookupTwo);
            }
            Assert.Equal(0.3f, one.Get<PlayerComponent>(rider).Yaw, 4);
        }
        finally
        {
            World.Destroy(one);
            World.Destroy(two);
        }
    }

    /// <summary>A door's aperture is announced when it changes, per world: a door that has not moved is not
    /// re-sent because a door with the same id on another map is open.</summary>
    [Fact]
    public void ADoorIsAnnouncedForItsOwnMovesOnly()
    {
        var one = World.Create();
        var two = World.Create();
        try
        {
            Entity Door(World w, float openness) => w.Create(
                new Transform { Position = Vector3.Zero, Rotation = Quaternion.Identity },
                new DoorComponent { Openness = openness, Target = openness, Aperture = 1f, Captured = true },
                new PortalComponent());
            var shut = Door(one, 0f);
            var open = Door(two, 1f);
            Assert.Equal(shut.Id, open.Id);

            var doors = new DoorSystem();
            var announced = new List<int>();
            for (int i = 0; i < 3; i++)
            {
                doors.Update(one, Dt, id => announced.Add(id));
                doors.Update(two, Dt, _ => { });
            }
            // Announced once when first seen, and never again: it has not moved.
            Assert.Single(announced);
        }
        finally
        {
            World.Destroy(one);
            World.Destroy(two);
        }
    }
}

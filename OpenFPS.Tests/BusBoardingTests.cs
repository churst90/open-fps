using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using OpenFPS.Server.Systems;
using Xunit;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// "I need to be able to board the bus."
///
/// A bus that stops at a bus stop takes passengers, and that is the whole of how it is decided: it
/// gets the body a parked car has, with the seats and without the driver's. Wait at the stop, get on
/// when it has stopped, ride it round, and get off at the next one — not between them.
/// </summary>
public class BusBoardingTests
{
    private readonly ITestOutputHelper _o;
    public BusBoardingTests(ITestOutputHelper o) => _o = o;

    private sealed class City
    {
        public MapManager Maps = null!;
        public World World = null!;
        public Dictionary<int, Entity> Lookup = null!;
        public OccupancyService Seats = null!;
        public readonly VehicleSystem Traffic = new();
        public readonly OccupancySystem Occupancy = new();
        public readonly SessionManager Sessions = new();

        public void Tick(int ticks)
        {
            float dt = PhysicsConstants.FixedDeltaTime;
            for (int i = 0; i < ticks; i++)
            {
                Traffic.Update("city", World, dt);
                ParentSystem.Update(World, Lookup);
                Occupancy.Update(World, Lookup);
            }
        }
    }

    private static City Load()
    {
        var c = new City();
        var prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
        c.Maps = new MapManager(new MapRepository(Path.Combine(AppContext.BaseDirectory, "maps")), prefabs);
        c.Maps.Initialize();
        var composites = new CompositeService(c.Maps, prefabs,
            new CompositeRepository(Path.Combine(Path.GetTempPath(), "openfps-no-composites-" + Guid.NewGuid())));
        c.Traffic.Spawn(c.Maps, composites);
        c.Seats = new OccupancyService(c.Maps);
        Assert.True(c.Maps.TryGetMap("city", out c.World, out _, out _, out c.Lookup));
        return c;
    }

    private static Entity Bus(City c)
    {
        Entity found = Entity.Null;
        c.World.Query(new QueryDescription().WithAll<VehicleComponent, OccupancyComponent, NameComponent>(),
            (Entity e, ref NameComponent n) => { if (found == Entity.Null && n.Name == "City bus 1") found = e; });
        Assert.NotEqual(Entity.Null, found);
        return found;
    }

    private static float Speed(City c, Entity bus) => c.World.Get<VehicleComponent>(bus).Speed;

    private static UserSession Passenger(City c, Vector3 at)
    {
        var entity = c.World.Create(
            new PlayerComponent { ConnectionId = 1, Username = "rider" },
            EntityType.Player,
            new Transform { Position = at, Rotation = Quaternion.Identity },
            new Velocity { Linear = Vector3.Zero },
            new MaterialComponent { Material = "Generic" },
            new NameComponent { Name = "rider" },
            new ColliderComponent
            {
                Shape = ColliderShape.Cylinder,
                Size = new Vector3(PhysicsConstants.PlayerRadius * 2, PhysicsConstants.PlayerHeight, PhysicsConstants.PlayerRadius * 2),
                IsSolid = true,
            });
        c.Maps.IndexEntity("city", entity);
        var session = new UserSession { ConnectionId = 1, Username = "rider", Entity = entity, CurrentMapId = "city" };
        c.Sessions.AddSession(1, session);
        return session;
    }

    /// <summary>Ticks until the bus has been standing still for a second, or gives up.</summary>
    private static void UntilStopped(City c, Entity bus, int maxSeconds = 120)
    {
        int still = 0;
        for (int i = 0; i < maxSeconds * 30 && still < 30; i++)
        {
            c.Tick(1);
            still = Speed(c, bus) < 0.05f ? still + 1 : 0;
        }
        Assert.True(still >= 30, "the bus never stopped");
    }

    [Fact]
    public void ABusThatStopsAtBusStopsHasSeatsButNoDriversSeat()
    {
        var c = Load();
        var bus = Bus(c);
        var seats = c.World.Get<OccupancyComponent>(bus).Seats;
        _o.WriteLine($"City bus 1: {seats.Count} seats");
        Assert.True(seats.Count >= 10, $"a bus with {seats.Count} seats");
        Assert.DoesNotContain(seats, s => s.Controls);
    }

    [Fact]
    public void WaitAtTheStopGetOnRideAndGetOffAtTheNext()
    {
        var c = Load();
        var bus = Bus(c);
        UntilStopped(c, bus);

        // Standing on the pavement beside the front of the bus: the kerb side is its right.
        var t = c.World.Get<Transform>(bus);
        var right = Vector3.Transform(Vector3.UnitX, t.Rotation);
        var ahead = Vector3.Transform(Vector3.UnitZ, t.Rotation);
        var rider = Passenger(c, t.Position + right * 2.2f + ahead * 3.5f);
        Assert.True(c.Seats.Enter(rider, bus.Id, null, out string on), on);
        _o.WriteLine(on);

        // Carried: it drives off and so do you.
        var boardedAt = c.World.Get<Transform>(rider.Entity).Position;
        int moving = 0;
        for (int i = 0; i < 60 * 30 && moving < 90; i++) { c.Tick(1); if (Speed(c, bus) > 3f) moving++; }
        Assert.True(moving >= 90, "the bus never pulled away");
        float carried = Vector3.Distance(boardedAt, c.World.Get<Transform>(rider.Entity).Position);
        Assert.True(carried > 10f, $"the passenger moved {carried:F1} m while the bus drove");
        Assert.False(c.Seats.Exit(rider, out string refused), "got off a moving bus");
        _o.WriteLine(refused);

        UntilStopped(c, bus);
        Assert.True(c.Seats.Exit(rider, out string off), off);
        _o.WriteLine(off);
        Assert.False(c.World.Has<OccupantComponent>(rider.Entity));
    }

    [Fact]
    public void NobodyGetsOnAMovingBus()
    {
        var c = Load();
        var bus = Bus(c);
        for (int i = 0; i < 30 * 60 && Speed(c, bus) < 5f; i++) c.Tick(1);
        Assert.True(Speed(c, bus) >= 5f);
        var t = c.World.Get<Transform>(bus);
        var rider = Passenger(c, t.Position + Vector3.Transform(Vector3.UnitX, t.Rotation) * 2.2f);
        Assert.False(c.Seats.Enter(rider, bus.Id, null, out string message));
        Assert.Contains("moving", message);
    }
}

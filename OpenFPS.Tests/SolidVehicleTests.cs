using System;
using System.IO;
using System.Linq;
using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using OpenFPS.Server.Systems;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// "If vehicles are solid objects, I shouldn't be able to walk right through them."
///
/// They were not solid at all: every vehicle's collider was IsSolid = false, there only so the spatial
/// grid would carry it into earshot. And even solid, three more things had to be right before a bus
/// could stop anybody: it had to be bus-sized rather than the one car box every vehicle was given; the
/// grid had to file it by the ground it actually covers once it is turned; and the client had to test
/// against things that move, not only against the static grid.
/// </summary>
public class SolidVehicleTests
{
    private static (World World, SpatialGrid<Entity> Grid) LoadCityWithTraffic()
    {
        var prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
        var maps = new MapRepository(Path.Combine(AppContext.BaseDirectory, "maps"));
        var manager = new MapManager(maps, prefabs);
        manager.Initialize();
        new VehicleSystem().Spawn(manager);
        Assert.True(manager.TryGetMap("city", out World world, out _, out var grid, out _));
        return (world, grid);
    }

    /// <summary>Re-files the moving things the way the server tick does.</summary>
    private static void RefreshDynamic(World world, SpatialGrid<Entity> grid)
    {
        grid.Clear();
        world.Query(new QueryDescription().WithAll<Transform, ColliderComponent>().WithAny<Velocity, PlayerComponent>(),
            (Entity e, ref Transform t, ref ColliderComponent c) => grid.AddOverlapping(t.Position, c.Size, t.Rotation, e, false));
    }

    [Fact]
    public void ABusIsSolidAndBusSized()
    {
        var (world, _) = LoadCityWithTraffic();
        var bus = FindVehicle(world, "school_bus");
        var c = world.Get<ColliderComponent>(bus);
        Assert.True(c.IsSolid, "a bus you can walk through");
        Assert.True(c.Size.Z > 10f, $"the bus is {c.Size.Z:F1} m long — the car box again");
    }

    /// <summary>
    /// Standing a metre off the back of a bus, wherever it happens to be pointing: the player is
    /// blocked. Measured at the back bumper, five and a half metres from the bus's centre, because that
    /// is where a grid filed by the unturned size misses it and a car-sized box does not reach.
    /// </summary>
    [Theory]
    [InlineData(0f)]
    [InlineData(90f)]    // heading east — where a grid filed by the unturned size misses the bumper
    [InlineData(35f)]
    public void YouCannotWalkIntoTheBackOfABus(float headingDegrees)
    {
        var (world, grid) = LoadCityWithTraffic();
        var bus = FindVehicle(world, "school_bus");
        ref var turn = ref world.Get<Transform>(bus);
        turn.Rotation = Quaternion.CreateFromYawPitchRoll(headingDegrees * MathF.PI / 180f, 0f, 0f);
        RefreshDynamic(world, grid);
        var t = world.Get<Transform>(bus);
        var c = world.Get<ColliderComponent>(bus);
        var back = Vector3.Transform(new Vector3(0f, 0f, -1f), t.Rotation);
        var feet = new Vector3(t.Position.X, t.Position.Y - c.Size.Y * 0.5f + 0.05f, t.Position.Z);
        var justInside = feet + back * (c.Size.Z * 0.5f - 0.3f);
        var wellClear = feet + back * (c.Size.Z * 0.5f + 1.5f);

        Assert.True(TouchesOnly(world, grid, bus, justInside),
            "a player at the back bumper of a bus is not touching it");
        // Behind the bus is wherever the map happens to put it — a kerb, a shelter, the next car — so
        // "clear" means clear OF THE BUS: the one thing this test is about must not reach that far.
        Assert.False(TouchesOnly(world, grid, bus, wellClear),
            "a player a metre and a half behind the bus is inside it");
    }

    [Fact]
    public void PedestriansAndAeroplanesStayWalkThrough()
    {
        var (world, _) = LoadCityWithTraffic();
        int walkers = 0, aircraft = 0;
        world.Query(new QueryDescription().WithAll<VehicleComponent, ColliderComponent, SoundEmitterComponent>(),
            (ref VehicleComponent v, ref ColliderComponent c, ref SoundEmitterComponent s) =>
            {
                if (s.SoundId?.StartsWith("aircraft:") == true) { aircraft++; Assert.False(c.IsSolid); }
            });
        world.Query(new QueryDescription().WithAll<ColliderComponent, NameComponent>().WithNone<SoundEmitterComponent>(),
            (ref ColliderComponent c, ref NameComponent n) =>
            {
                if (n.Name.StartsWith("Pedestrian")) { walkers++; Assert.False(c.IsSolid); }
            });
        // The city has had no aircraft since 2026-09-25 ("remove the planes ... remove the helicopter
        // for now"); any that come back are held to the same rule above.
        Assert.True(walkers > 0, $"found {aircraft} aircraft and {walkers} walkers to check");
    }

    /// <summary>The city parks four cars in the garage, and every one of them can be driven.</summary>
    [Fact]
    public void TheGarageHasCarsYouCanDrive()
    {
        var prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
        var manager = new MapManager(new MapRepository(Path.Combine(AppContext.BaseDirectory, "maps")), prefabs);
        manager.Initialize();
        var composites = new CompositeService(manager, prefabs,
            new CompositeRepository(Path.Combine(Path.GetTempPath(), "openfps-no-composites-" + Guid.NewGuid())));
        composites.PlaceRecorded(manager);
        Assert.True(manager.TryGetMap("city", out World world, out _, out _, out _));
        int drivable = 0;
        world.Query(new QueryDescription().WithAll<DriveComponent, OccupancyComponent>(),
            (ref DriveComponent d, ref OccupancyComponent o) => { if (o.Seats.Exists(s => s.Controls)) drivable++; });
        Assert.Equal(4, drivable);
    }

    private static bool TouchesOnly(World world, SpatialGrid<Entity> grid, Entity target, Vector3 feet)
    {
        // The server's own search radius, so a grid that files the bus in the wrong cells fails here the
        // way it fails in play: the bus is simply never offered as a candidate.
        foreach (var e in grid.GetItemsInRadius(feet, PhysicsConstants.CollisionSearchRadius))
        {
            if (e != target) continue;
            var t = world.Get<Transform>(e);
            var c = world.Get<ColliderComponent>(e);
            float h = PhysicsConstants.PlayerHeight - 0.4f;
            var centre = feet + new Vector3(0f, 0.4f + h / 2f, 0f);
            var local = Vector3.Transform(centre - t.Position, Matrix4x4.CreateFromQuaternion(Quaternion.Inverse(t.Rotation)));
            if (GeometryUtils.AABBIntersectsCylinder(-c.Size / 2f, c.Size / 2f, local, PhysicsConstants.PlayerRadius, h)) return true;
        }
        return false;
    }

    private static Entity FindVehicle(World world, string presetPrefix)
    {
        Entity found = Entity.Null;
        world.Query(new QueryDescription().WithAll<VehicleComponent>(), (Entity e, ref VehicleComponent v) =>
        {
            if (found == Entity.Null && v.VehicleType.StartsWith(presetPrefix, StringComparison.OrdinalIgnoreCase)) found = e;
        });
        Assert.NotEqual(Entity.Null, found);
        return found;
    }
}

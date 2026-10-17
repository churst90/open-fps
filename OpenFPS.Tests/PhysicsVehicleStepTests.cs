using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Systems;
using OpenFPS.Geometry;
using OpenFPS.Physics;

namespace OpenFPS.Tests;

/// <summary>
/// The vehicle step (physics stage 3, docs/PHYSICS_BOUNDARY.md 17): DrivingSystem's mechanics as the physics'
/// VehicleStep, one per map, and a car's impact a contact in the map's stream that the host hears. The driving
/// replay holds the step to the tracks it made inside DrivingSystem.
/// </summary>
public class PhysicsVehicleStepTests
{
    private const float Dt = PhysicsConstants.FixedDeltaTime;

    /// <summary>A floor, a wall across the road ahead, and a car doing 15 m/s at it with nobody's foot down.</summary>
    private static (World World, SpatialGrid<Entity> Grid, Entity Car, Entity Wall) Road()
    {
        var world = World.Create();
        var grid = new SpatialGrid<Entity>(10f);
        var floor = world.Create(new Transform { Position = new Vector3(0, -0.5f, 0), Rotation = Quaternion.Identity, Scale = Vector3.One },
            new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(100, 1, 100), IsSolid = true },
            new MaterialComponent { Material = "Asphalt" });
        grid.AddOverlapping(new Vector3(0, -0.5f, 0), new Vector3(100, 1, 100), floor, isStatic: true);
        var wall = world.Create(new Transform { Position = new Vector3(0, 1.5f, 8f), Rotation = Quaternion.Identity, Scale = Vector3.One },
            new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(10, 3, 0.5f), IsSolid = true },
            new MaterialComponent { Material = "Brick" });
        grid.AddOverlapping(new Vector3(0, 1.5f, 8f), new Vector3(10, 3, 0.5f), wall, isStatic: true);
        var profile = MachineRegistry.VehicleFor("i4_economy");
        var car = world.Create(
            new Transform { Position = Vector3.Zero, Rotation = Quaternion.Identity, Scale = Vector3.One },
            new DriveComponent { Preset = "i4_economy", Speed = 15f, EngineOn = true, EngineOnFor = 10f },
            new Velocity { Linear = new Vector3(0, 0, 15f) }, new MaterialComponent { Material = "Metal" },
            new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(profile.WidthMetres, profile.HeightMetres, profile.LengthMetres), IsSolid = true });
        return (world, grid, car, wall);
    }

    /// <summary>
    /// A car that strikes a wall stops dead, and the strike is a contact in the map's stream (the car, the wall,
    /// the way back out of it, the momentum the car lost, the closing speed, both surfaces) that the host turns
    /// into the impact heard.
    /// </summary>
    [Fact]
    public void ACarThatStrikesAWallIsAContactTheHostHears()
    {
        var (world, grid, car, wall) = Road();
        try
        {
            var contacts = ContactStreams.Of(world);
            var heard = new List<string>();
            ContactEvent? struck = null;
            for (int tick = 0; tick < 30 && struck == null; tick++)
            {
                grid.Clear();
                var t = world.Get<Transform>(car);
                grid.AddOverlapping(t.Position, world.Get<ColliderComponent>(car).Size, t.Rotation, car, false);
                contacts.Begin();
                DrivingSystem.Update(world, grid, new Vector3(-50, -10, -50), new Vector3(50, 50, 50), Dt,
                                     (id, label, sounds) => { if (id == car.Id && sounds.Count > 0) heard.Add(label); });
                contacts.Close();
                if (contacts.Events.Count > 0) struck = contacts.Events[0];
            }
            Assert.NotNull(struck);
            var c = struck!.Value;
            Assert.Equal((car.Id, wall.Id), (c.Body, c.Other));
            Assert.Equal(ContactPhase.Began, c.Phase);
            Assert.Equal(("Metal", "Brick"), (c.Surface, c.OtherSurface));
            Assert.True(c.ClosingSpeed > 10f, $"struck at {c.ClosingSpeed:F1} m/s");
            float mass = MachineRegistry.VehicleFor("i4_economy").MassKg;
            Assert.Equal(mass * c.ClosingSpeed, c.NormalImpulse, 1);
            Assert.True(c.Normal.Z < -0.99f, $"the way out of the wall is back down the road, {c.Normal}");
            Assert.Equal(0f, world.Get<DriveComponent>(car).Speed);
            Assert.True(world.Get<Transform>(car).Position.Z < 8f - 0.25f - 2f, "the car is in the wall");
            Assert.Equal(new[] { "impact" }, heard);
        }
        finally { World.Destroy(world); }
    }

    /// <summary>The state the step leaves is copied onto the components the wire carries: the speed the client
    /// picks its gear from, the heading, the tyres' demand and the wheels.</summary>
    [Fact]
    public void TheCarsStateGoesOntoItsComponents()
    {
        var (world, grid, car, _) = Road();
        try
        {
            world.Add(car, new VehicleComponent());
            DrivingSystem.Update(world, grid, new Vector3(-50, -10, -50), new Vector3(50, 50, 50), Dt);
            var drive = world.Get<DriveComponent>(car);
            Assert.InRange(drive.Speed, 14f, 15f);
            Assert.Equal(drive.Speed, world.Get<VehicleComponent>(car).Speed);
            Assert.Equal(drive.Speed, world.Get<Velocity>(car).Linear.Z, 4);
            Assert.True(DrivingSystem.TryGetWheels(world, car, out var wheels));
            Assert.Contains(wheels!, w => w.AngularSpeedRadPerSec > 30f);
        }
        finally { World.Destroy(world); }
    }
}

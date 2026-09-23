using System;
using System.IO;
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
/// "At 24 Birch Street I pressed E, it said the door swung open, and I can't walk forward." The door
/// did open. A curtain meant for the front window hung across the doorway instead, solid, from knee
/// height to the lintel, in every house on the estate.
/// </summary>
public class EstateDoorTests
{
    /// <summary>
    /// The estate house (a curtain across the doorway), the Union Building's entrance and an airport
    /// terminal door (both stood at right angles to their own walls: shut, the doorway was open round
    /// them; opened, the leaf swung across it — "it says the door is open and I can't walk out").
    /// </summary>
    [Theory]
    [InlineData(-347f, -9.325f, 0f, -0.8f)]      // 24 Birch Street, through to the hall
    [InlineData(9.675f, 28.08f, 0.8f, 0f)]       // the Union Building, into the stairwell
    [InlineData(225.97f, 30.0f, -0.8f, 0f)]      // the terminal, out to the apron
    public void YouCanWalkThroughAnOpenDoor(float x, float z, float stepX, float stepZ)
    {
        var prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
        var maps = new MapManager(new MapRepository(Path.Combine(AppContext.BaseDirectory, "maps")), prefabs);
        maps.Initialize();
        Assert.True(maps.TryGetMap("city", out World world, out _, out var grid, out _));

        Entity door = Entity.Null;
        world.Query(new QueryDescription().WithAll<Transform, DoorComponent>(), (Entity e, ref Transform t) =>
        {
            if (Vector2.Distance(new Vector2(t.Position.X, t.Position.Z), new Vector2(x, z)) < 0.5f && t.Position.Y < 2f) door = e;
        });
        Assert.NotEqual(Entity.Null, door);

        // In the doorway and a step inside, beside where the leaf hinges.
        var doorway = new Vector3(x, 0.1f, z);
        var inside = new Vector3(x + stepX, 0.1f, z + stepZ);
        Assert.True(MovementSystem.CheckCollision(world, grid, doorway, PhysicsConstants.PlayerRadius, PhysicsConstants.PlayerHeight),
            "the shut door is not in the doorway");

        var doors = new DoorSystem();
        Assert.True(DoorSystem.Set(world, door, open: true));
        for (int i = 0; i < 90; i++) doors.Update(world, 1f / 30f, _ => { });

        Assert.False(MovementSystem.CheckCollision(world, grid, doorway, PhysicsConstants.PlayerRadius, PhysicsConstants.PlayerHeight),
            "the door is open and the doorway is still solid");
        Assert.False(MovementSystem.CheckCollision(world, grid, inside, PhysicsConstants.PlayerRadius, PhysicsConstants.PlayerHeight),
            "through the doorway there is something solid");
    }
}

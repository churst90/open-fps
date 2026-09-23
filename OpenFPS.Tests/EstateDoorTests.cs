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
    [Fact]
    public void YouCanWalkThroughAnOpenFrontDoor()
    {
        var prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
        var maps = new MapManager(new MapRepository(Path.Combine(AppContext.BaseDirectory, "maps")), prefabs);
        maps.Initialize();
        Assert.True(maps.TryGetMap("city", out World world, out _, out var grid, out _));

        Entity door = Entity.Null;
        world.Query(new QueryDescription().WithAll<Transform, DoorComponent>(), (Entity e, ref Transform t) =>
        {
            if (Vector3.Distance(t.Position, new Vector3(-347f, 1.09f, -9.325f)) < 0.5f) door = e;
        });
        Assert.NotEqual(Entity.Null, door);

        // In the doorway and a step inside, beside where the leaf hinges.
        var doorway = new Vector3(-347f, 0.1f, -9.3f);
        var inside = new Vector3(-347f, 0.1f, -10.1f);
        Assert.True(MovementSystem.CheckCollision(world, grid, doorway, PhysicsConstants.PlayerRadius, PhysicsConstants.PlayerHeight),
            "the shut door is not in the doorway");

        var doors = new DoorSystem();
        Assert.True(DoorSystem.Set(world, door, open: true));
        for (int i = 0; i < 90; i++) doors.Update(world, 1f / 30f, _ => { });

        Assert.False(MovementSystem.CheckCollision(world, grid, doorway, PhysicsConstants.PlayerRadius, PhysicsConstants.PlayerHeight),
            "the door is open and the doorway is still solid");
        Assert.False(MovementSystem.CheckCollision(world, grid, inside, PhysicsConstants.PlayerRadius, PhysicsConstants.PlayerHeight),
            "through the doorway there is something solid — the curtain was hung across it");
    }
}

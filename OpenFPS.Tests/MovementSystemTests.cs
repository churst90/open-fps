using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.Json;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using OpenFPS.Server.Systems;
using Xunit;
using static OpenFPS.Common.PhysicsConstants;

namespace OpenFPS.Tests;

/// <summary>
/// The server's side of a player's movement: what it lets an input buy, and what it will not. The
/// walking itself is SharedMovementEngine's; these pin the guards round it — the real-time budget
/// that closes the "send inputs faster" speed hack, the claimed step's clamp, the look limits, a
/// seated body, what counts as solid, the fall out of the world, and the teleport check.
/// Written for the survivors of the 2026-09-24 mutation run over MovementSystem.
/// </summary>
public class MovementSystemTests : IDisposable
{
    private const int Conn = 11;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "openfps-move-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private sealed class Rig
    {
        public required World World;
        public required SpatialGrid<Entity> Grid;
        public required SessionManager Sessions;
        public required UserSession Session;
        public required Entity Player;
        public MapManager? Maps;
        public long Seq;

        public Vector3 Position => World.Get<Transform>(Player).Position;
        public ref PlayerComponent Body => ref World.Get<PlayerComponent>(Player);

        public void Queue(Vector3 move, float dt = FixedDeltaTime, int count = 1, Vector2 look = default, bool sprint = false)
        {
            for (int i = 0; i < count; i++)
                Session.InputQueue.Enqueue(new ClientInputUpdate
                {
                    SequenceId = ++Seq, MoveDirection = move, DeltaTime = dt, LookDelta = look, Sprint = sprint,
                });
        }

        public void Tick(int n = 1)
        {
            for (int i = 0; i < n; i++)
                MovementSystem.Update(World, new Vector3(-100, -100, -100), new Vector3(100, 100, 100), Grid,
                                      new Dictionary<int, Entity>(), Sessions, Maps!, FixedDeltaTime);
        }

        public Entity Box(Vector3 at, Vector3 size, bool solid)
        {
            var e = World.Create(
                new Transform { Position = at, Rotation = Quaternion.Identity, Scale = Vector3.One },
                new ColliderComponent { Shape = ColliderShape.Box, Size = size, IsSolid = solid },
                new MaterialComponent { Material = "Brick" });
            Grid.AddOverlapping(at, size, e, true);
            return e;
        }
    }

    private Rig Build(Vector3? start = null)
    {
        var world = World.Create();
        var grid = new SpatialGrid<Entity>(5f);
        var floor = world.Create(
            new Transform { Position = new Vector3(0, -0.5f, 0), Rotation = Quaternion.Identity, Scale = Vector3.One },
            new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(100, 1, 100), IsSolid = true },
            new MaterialComponent { Material = "Concrete" });
        grid.AddOverlapping(new Vector3(0, -0.5f, 0), new Vector3(100, 1, 100), floor, true);
        var player = world.Create(
            new PlayerComponent { Username = "walker", ConnectionId = Conn },
            new Transform { Position = start ?? Vector3.Zero, Rotation = Quaternion.Identity, Scale = Vector3.One },
            new Velocity(),
            new MaterialComponent(),
            new ColliderComponent { Shape = ColliderShape.Box, Size = PlayerSize, IsSolid = false });
        var sessions = new SessionManager();
        var session = new UserSession { ConnectionId = Conn, Username = "walker", CurrentMapId = "move" };
        sessions.AddSession(Conn, session);
        return new Rig { World = world, Grid = grid, Sessions = sessions, Session = session, Player = player };
    }

    private static readonly Vector3 Forward = new(0, 0, 1);

    // ── The budget ────────────────────────────────────────────────────────────────────────────

    /// <summary>One tick buys one tick of movement, however many inputs arrive in it: extra packets
    /// buy latency, never distance.</summary>
    [Fact]
    public void ATickBuysOneTickOfMovementHoweverManyInputsArrive()
    {
        var rig = Build();
        rig.Tick(30);                                   // settle onto the floor, spend the idle budget
        rig.Session.InputBudget = 0f;
        var from = rig.Position;
        rig.Queue(Forward, count: 20);
        rig.Tick();
        Assert.Equal(1, rig.Session.LastProcessedSequenceId);
        Assert.Equal(19, rig.Session.InputQueue.Count);
        // An input bigger than what is left is not taken at all, not taken in part.
        Assert.InRange(rig.Session.InputBudget, 0f, 1e-4f);
        float moved = Vector3.Distance(from, rig.Position);
        Assert.InRange(moved, WalkSpeed * FixedDeltaTime * 0.5f, WalkSpeed * FixedDeltaTime * 1.05f);
    }

    /// <summary>Budget saved up while idle is held to three ticks' worth, and no more than four
    /// inputs are taken in a tick.</summary>
    [Fact]
    public void IdleBudgetIsCappedAtThreeTicks()
    {
        var rig = Build();
        rig.Tick(30);
        Assert.InRange(rig.Session.InputBudget, FixedDeltaTime * MaxInputBudgetTicks - 1e-5f, FixedDeltaTime * MaxInputBudgetTicks + 1e-5f);
        rig.Queue(Forward, count: 10);
        rig.Tick();
        Assert.Equal(3, rig.Session.LastProcessedSequenceId);
        // ...and the fourth waits whole for the next tick rather than being taken for a sliver.
        rig.Tick();
        Assert.Equal(4, rig.Session.LastProcessedSequenceId);

        // Tiny inputs are cheap, but the count still stops at four.
        var rig2 = Build();
        rig2.Tick(30);
        rig2.Queue(Forward, dt: 0.001f, count: 10);
        rig2.Tick();
        Assert.Equal(MaxInputsPerTick, (int)rig2.Session.LastProcessedSequenceId);
    }

    /// <summary>A claimed step is advisory: clamped to one and a half ticks, and a missing one is a
    /// tick.</summary>
    [Fact]
    public void AClaimedStepIsClamped()
    {
        var rig = Build();
        rig.Tick(30);
        float before = rig.Session.InputBudget;
        rig.Queue(Forward, dt: 1.0f);
        rig.Tick();
        // Spent 1.5 ticks of a budget topped up by one tick, not a second.
        float cap = FixedDeltaTime * MaxInputBudgetTicks;
        float spent = MathF.Min(before + FixedDeltaTime, cap) - rig.Session.InputBudget;
        Assert.InRange(spent, MaxInputDeltaTime - 1e-4f, MaxInputDeltaTime + 1e-4f);

        var rig2 = Build();
        rig2.Tick(30);
        float before2 = rig2.Session.InputBudget;
        rig2.Queue(Forward, dt: 0f);
        rig2.Tick();
        float spent2 = MathF.Min(before2 + FixedDeltaTime, cap) - rig2.Session.InputBudget;
        Assert.InRange(spent2, FixedDeltaTime - 1e-4f, FixedDeltaTime + 1e-4f);
    }

    // ── Looking ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void LookingTurnsAtTheSharedRateAndPitchStopsAtItsLimit()
    {
        var rig = Build();
        rig.Tick(30);
        rig.Queue(Vector3.Zero, look: new Vector2(1f, 1000f));
        rig.Tick();
        Assert.Equal(1.5f, rig.Body.Pitch, 4);
        Assert.Equal(-RotationSpeed * FixedDeltaTime, rig.Body.Yaw, 4);
        Assert.NotEqual(Quaternion.Identity, rig.World.Get<Transform>(rig.Player).Rotation);
        rig.Queue(Vector3.Zero, look: new Vector2(0f, -100000f));
        rig.Tick();
        Assert.Equal(-1.5f, rig.Body.Pitch, 4);
    }

    /// <summary>A seated body looks about but does not walk: the seat owns where it is.</summary>
    [Fact]
    public void ASeatedPlayerLooksButDoesNotWalk()
    {
        var rig = Build();
        rig.Tick(30);
        rig.World.Add(rig.Player, new OccupantComponent { RootEntityId = -1, Controls = false });
        var from = rig.Position;
        rig.Queue(Forward, count: 3, look: new Vector2(1f, 0.5f));
        rig.Tick(3);
        Assert.Equal(from, rig.Position);
        Assert.True(rig.Body.Yaw < 0f);
        Assert.True(rig.Body.Pitch > 0f);
        Assert.Equal(3, rig.Session.LastProcessedSequenceId);
    }

    // ── What stops you ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ASolidThingStopsYouAndANonSolidOneDoesNot()
    {
        float Walk(bool solid)
        {
            var rig = Build();
            rig.Box(new Vector3(0, 1f, 1.5f), new Vector3(6f, 2f, 0.2f), solid);
            rig.Tick(10);
            for (int i = 0; i < 60; i++) { rig.Queue(Forward); rig.Tick(); }
            return rig.Position.Z;
        }
        float blocked = Walk(true), open = Walk(false);
        Assert.True(blocked < 1.5f - 0.1f - PlayerRadius + 0.05f, $"walked to z {blocked:F2} through a solid wall at 1.4");
        Assert.True(open > 3f, $"a non-solid panel held the walker at z {open:F2}");
    }

    [Fact]
    public void SprintingIsTheSprintMultiple()
    {
        float Run(bool sprint)
        {
            var rig = Build();
            rig.Tick(10);
            var from = rig.Position;
            for (int i = 0; i < 30; i++) { rig.Queue(Forward, sprint: sprint); rig.Tick(); }
            return Vector3.Distance(from, rig.Position);
        }
        float walk = Run(false), sprint = Run(true);
        Assert.InRange(sprint / walk, SprintMultiplier * 0.95f, SprintMultiplier * 1.05f);
    }

    [Fact]
    public void TheFloorsMaterialIsWhatYouAreStandingOn()
    {
        var rig = Build(new Vector3(0, 0.05f, 0));
        rig.Queue(Vector3.Zero);
        rig.Tick();
        Assert.Equal("Concrete", rig.World.Get<MaterialComponent>(rig.Player).Material);
    }

    // ── Out of the world ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void FallingOutOfTheWorldPutsYouBackAtTheSpawnWithYourInputDropped()
    {
        string maps = Path.Combine(_dir, "maps");
        Directory.CreateDirectory(maps);
        var data = new MapData
        {
            Id = "move",
            SpawnPoint = new Transform { Position = new Vector3(3f, 1f, 4f), Rotation = Quaternion.Identity },
        };
        File.WriteAllText(Path.Combine(maps, "move.json"), JsonSerializer.Serialize(data, MapRepository.JsonOptions));
        var manager = new MapManager(new MapRepository(maps), new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs")));
        manager.Initialize();

        // Five metres below the map's floor is still falling; past that you are gone.
        var still = Build(new Vector3(0, MapMinimumY - 4.9f, 0));
        still.Maps = manager;
        still.Queue(Forward, count: 2);
        still.Tick();
        Assert.True(still.Position.Y < MapMinimumY - 4.8f, "respawned before falling past the margin");

        var rig = Build(new Vector3(0, MapMinimumY - 5.1f, 0));
        rig.Maps = manager;
        rig.World.Get<Velocity>(rig.Player).Linear = new Vector3(0, -20f, 0);
        rig.Queue(Forward, count: 3);
        rig.Tick();
        Assert.Equal(new Vector3(3f, 1f, 4f), rig.Position);
        Assert.Equal(Vector3.Zero, rig.World.Get<Velocity>(rig.Player).Linear);
        Assert.Empty(rig.Session.InputQueue);
        Assert.True(rig.World.Get<Transform>(rig.Player).IsDirty);
    }

    // ── The teleport check ────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheTeleportCheckSeesSolidThingsAtBodyHeightOnly()
    {
        var rig = Build();
        var pillar = rig.Box(new Vector3(10f, 1f, 10f), new Vector3(1f, 2f, 1f), solid: true);
        var ghost = rig.Box(new Vector3(-10f, 1f, 10f), new Vector3(1f, 2f, 1f), solid: false);
        var kerb = rig.Box(new Vector3(10f, 0.15f, -10f), new Vector3(2f, 0.3f, 2f), solid: true);

        Assert.True(MovementSystem.CheckCollision(rig.World, rig.Grid, new Vector3(10f, 0f, 10f), PlayerRadius, PlayerHeight));
        Assert.False(MovementSystem.CheckCollision(rig.World, rig.Grid, new Vector3(10f, 0f, 12f), PlayerRadius, PlayerHeight));
        Assert.False(MovementSystem.CheckCollision(rig.World, rig.Grid, new Vector3(-10f, 0f, 10f), PlayerRadius, PlayerHeight));
        Assert.False(MovementSystem.CheckCollision(rig.World, rig.Grid, new Vector3(10f, 0f, 10f), PlayerRadius, PlayerHeight, pillar));
        // A kerb under your feet is stepped onto, not collided with: the check starts 0.4 m up.
        Assert.False(MovementSystem.CheckCollision(rig.World, rig.Grid, new Vector3(10f, 0f, -10f), PlayerRadius, PlayerHeight));
        // ...and one at chest height is.
        Assert.True(MovementSystem.CheckCollision(rig.World, rig.Grid, new Vector3(10f, -1f, -10f), PlayerRadius, PlayerHeight));
    }
}

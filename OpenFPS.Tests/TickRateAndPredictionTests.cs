using System;
using System.Collections.Generic;
using System.Numerics;
using Arch.Core;
using OpenFPS.Client.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Core;
using OpenFPS.Server.Systems;
using Xunit;
using static OpenFPS.Common.PhysicsConstants;

namespace OpenFPS.Tests;

/// <summary>
/// Covers step 2 of the engineering audit: one tick rate shared by client and server, a
/// stateless predictor whose replay is deterministic, a bounded input history, and a per-tick
/// input budget that closes the "send inputs faster than real time" speed hack.
/// </summary>
public class TickRateAndPredictionTests
{
    // ── Fixtures ────────────────────────────────────────────────────────────────

    private const int TestConnectionId = 7;

    /// <summary>A world with a wide floor at y=0 and one player standing on it.</summary>
    private static (World world, SpatialGrid<Entity> grid, SessionManager sessions, Entity player) BuildWorld()
    {
        var world = World.Create();
        var grid = new SpatialGrid<Entity>(new Vector2(-100, -100), new Vector2(100, 100), 5f);

        var floor = world.Create(
            new Transform { Position = new Vector3(0, -0.5f, 0), Rotation = Quaternion.Identity, Scale = Vector3.One },
            new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(100, 1, 100), IsSolid = true },
            new MaterialComponent { Material = "Concrete" });
        grid.AddOverlapping(new Vector3(0, -0.5f, 0), new Vector3(100, 1, 100), floor, true);

        var player = world.Create(
            new PlayerComponent { Username = "tester", ConnectionId = TestConnectionId },
            new Transform { Position = Vector3.Zero, Rotation = Quaternion.Identity, Scale = Vector3.One },
            new Velocity(),
            new MaterialComponent(),
            new ColliderComponent { Shape = ColliderShape.Box, Size = PlayerSize, IsSolid = false });

        var sessions = new SessionManager();
        sessions.AddSession(TestConnectionId, new UserSession { ConnectionId = TestConnectionId, Username = "tester" });
        return (world, grid, sessions, player);
    }

    private static ClientInputUpdate Forward(long seq, float dt) =>
        new() { SequenceId = seq, MoveDirection = new Vector3(0, 0, 1), DeltaTime = dt };

    private static void RunServerTick(World world, SpatialGrid<Entity> grid, SessionManager sessions) =>
        MovementSystem.Update(world, new Vector3(-100, -100, -100), new Vector3(100, 100, 100), grid, sessions, null!, FixedDeltaTime);

    // ── Tick-rate unification ───────────────────────────────────────────────────

    [Fact]
    public void ServerAndClientAgreeOnOneSecondOfWalking()
    {
        var (world, grid, sessions, player) = BuildWorld();
        sessions.TryGetSession(TestConnectionId, out var session);

        // Exactly what a client running the shared fixed step produces in one second.
        for (int i = 0; i < TickRate; i++)
        {
            session.InputQueue.Enqueue(Forward(i + 1, FixedDeltaTime));
            RunServerTick(world, grid, sessions);
        }

        float travelled = world.Get<Transform>(player).Position.Z;

        // The client predicts WalkSpeed by construction (SharedMovementEngine integrates
        // Speed * dt), so the server matching WalkSpeed IS the parity assertion. Before the
        // rates were unified the server produced WalkSpeed * 20/30 = 3.0 m here.
        Assert.InRange(travelled, WalkSpeed * 0.97f, WalkSpeed * 1.03f);
        World.Destroy(world);
    }

    [Fact]
    public void OneTickOfSimulatedTimeMatchesTheFixedStep()
    {
        // Guards against a second, divergent tick constant reappearing.
        Assert.Equal(30, TickRate);
        Assert.Equal(1.0f, TickRate * FixedDeltaTime, 5);
    }

    // ── Per-tick input budget (speed hack) ──────────────────────────────────────

    [Fact]
    public void FloodingInputsCannotBuyExtraDistanceInOneTick()
    {
        var (world, grid, sessions, player) = BuildWorld();
        sessions.TryGetSession(TestConnectionId, out var session);

        // A cheating client dumps a second's worth of inputs into a single tick.
        for (int i = 0; i < 200; i++) session.InputQueue.Enqueue(Forward(i + 1, FixedDeltaTime));
        RunServerTick(world, grid, sessions);

        float travelled = world.Get<Transform>(player).Position.Z;
        float budgetCeiling = WalkSpeed * FixedDeltaTime * MaxInputBudgetTicks;

        Assert.True(travelled <= budgetCeiling + 0.01f,
            $"One tick moved the player {travelled:F3} m, above the {budgetCeiling:F3} m budget ceiling.");
        World.Destroy(world);
    }

    [Fact]
    public void FloodingInputsCannotBeatRealTimeOverASecond()
    {
        var (world, grid, sessions, player) = BuildWorld();
        sessions.TryGetSession(TestConnectionId, out var session);

        long seq = 0;
        for (int tick = 0; tick < TickRate; tick++)
        {
            // Ten inputs per tick instead of one — a 10x speed hack under the old drain-everything loop.
            for (int i = 0; i < 10; i++) session.InputQueue.Enqueue(Forward(++seq, FixedDeltaTime));
            RunServerTick(world, grid, sessions);
        }

        float travelled = world.Get<Transform>(player).Position.Z;
        // One second of walking, plus at most the banked lag slack.
        float ceiling = WalkSpeed * (1.0f + FixedDeltaTime * MaxInputBudgetTicks);

        Assert.True(travelled <= ceiling + 0.01f,
            $"A flooding client travelled {travelled:F3} m in one second (ceiling {ceiling:F3} m).");
        World.Destroy(world);
    }

    [Fact]
    public void ForgedDeltaTimeIsClamped()
    {
        var (world, grid, sessions, player) = BuildWorld();
        sessions.TryGetSession(TestConnectionId, out var session);

        // A single input claiming a whole second of simulation.
        session.InputQueue.Enqueue(Forward(1, 1.0f));
        RunServerTick(world, grid, sessions);

        float travelled = world.Get<Transform>(player).Position.Z;
        Assert.True(travelled <= WalkSpeed * MaxInputDeltaTime + 0.01f,
            $"A forged DeltaTime moved the player {travelled:F3} m in one tick.");
        World.Destroy(world);
    }

    // ── Look angles ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(1.2f, 0.4f)]
    [InlineData(-2.5f, -1.1f)]
    [InlineData(3.0f, 1.5f)]
    public void ToYawPitchInvertsCreateFromYawPitchRoll(float yaw, float pitch)
    {
        var q = Quaternion.CreateFromYawPitchRoll(yaw, pitch, 0);
        MathHelper.ToYawPitch(q, out float recoveredYaw, out float recoveredPitch);

        Assert.Equal(0f, MathHelper.WrapAngle(recoveredYaw - yaw), 3);
        Assert.Equal(pitch, recoveredPitch, 3);
    }

    [Fact]
    public void WrapAngleFoldsIntoASingleTurn()
    {
        Assert.Equal(0f, MathHelper.WrapAngle(MathF.PI * 4), 4);
        Assert.Equal(-MathF.PI / 2, MathHelper.WrapAngle(MathF.PI * 1.5f), 4);
    }

    // ── Predictor / reconciler ──────────────────────────────────────────────────

    private static (LocalPlayerState state, ClientPhysicsSystem physics, PredictionReconciler rec, WorldSnapshot snap) BuildClient()
    {
        var state = new LocalPlayerState { Position = Vector3.Zero };
        var physics = new ClientPhysicsSystem(state, new SpatialService()) { OwnEntityId = 1 };
        var snap = new WorldSnapshot();

        var floorDef = new EntityDefinition
        {
            EntityId = 2,
            Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(100, 1, 100), IsSolid = true },
            Material = new MaterialComponent { Material = "Concrete" }
        };
        snap.Entities[2] = new EntitySnapshot
        {
            Id = 2,
            Definition = floorDef,
            Transform = new Transform { Position = new Vector3(0, -0.5f, 0), Rotation = Quaternion.Identity, Scale = Vector3.One }
        };

        return (state, physics, new PredictionReconciler(state, physics), snap);
    }

    [Fact]
    public void ReplayFromTheServerStateReproducesThePredictedPath()
    {
        // Ten steps forward, none acknowledged.
        var (state, _, rec, snap) = BuildClient();
        for (int i = 0; i < 10; i++) rec.Step(Forward(i + 1, FixedDeltaTime), snap, FixedDeltaTime);
        Vector3 predicted = state.Position;

        // A correction that rewinds to the origin (the server has processed nothing) must, after
        // replaying the ten unacknowledged inputs, land back on exactly the predicted position.
        // That only holds if Predict reads no hidden state of its own.
        var serverState = new EntityState
        {
            EntityId = 1,
            Transform = QuantizedTransform.FromTransform(new Transform { Position = Vector3.Zero, Rotation = Quaternion.Identity, Scale = Vector3.One }),
            LinearVelocity = Vector3.Zero
        };
        rec.ApplyServerCorrection(serverState, lastProcessedId: 0, snap);

        Assert.Equal(predicted.X, state.Position.X, 4);
        Assert.Equal(predicted.Y, state.Position.Y, 4);
        Assert.Equal(predicted.Z, state.Position.Z, 4);
        Assert.Equal(10, rec.PendingInputs);
    }

    [Fact]
    public void RotationIsNotReplayedAndSoDoesNotCompound()
    {
        var (state, physics, rec, snap) = BuildClient();

        // Three turn inputs, none acknowledged yet.
        for (int i = 0; i < 3; i++)
            rec.Step(new ClientInputUpdate { SequenceId = i + 1, LookDelta = new Vector2(3, 0), DeltaTime = FixedDeltaTime }, snap, FixedDeltaTime);

        float yawAfterInput = state.Yaw;
        float expectedTurn = -3f * RotationSpeed * FixedDeltaTime * 3f;
        Assert.Equal(expectedTurn, yawAfterInput, 4);

        // The server has seen none of them: its state still reports the original heading.
        var serverState = new EntityState
        {
            EntityId = 1,
            Transform = QuantizedTransform.FromTransform(new Transform { Position = Vector3.Zero, Rotation = Quaternion.Identity, Scale = Vector3.One }),
            LinearVelocity = Vector3.Zero
        };
        rec.ApplyServerCorrection(serverState, lastProcessedId: 0, snap);

        // Reconciliation must project the server yaw forward by the pending turns — not add them
        // again on top of the local heading.
        Assert.Equal(expectedTurn, state.Yaw, 3);
    }

    [Fact]
    public void ReconciliationSnapsYawWhenTheServerDisagrees()
    {
        var (state, physics, rec, snap) = BuildClient();
        state.Yaw = 2.0f;

        // Server says we are facing 0 and has acknowledged everything.
        var serverState = new EntityState
        {
            EntityId = 1,
            Transform = QuantizedTransform.FromTransform(new Transform { Position = Vector3.Zero, Rotation = Quaternion.Identity, Scale = Vector3.One }),
            LinearVelocity = Vector3.Zero
        };
        rec.ApplyServerCorrection(serverState, lastProcessedId: 100, snap);

        Assert.Equal(0f, MathHelper.WrapAngle(state.Yaw), 3);
    }

    [Fact]
    public void InputHistoryIsBounded()
    {
        var (state, physics, rec, snap) = BuildClient();

        // The server never acknowledges anything — the old code grew this list forever.
        for (int i = 0; i < MaxInputHistory * 3; i++)
            rec.Step(Forward(i + 1, FixedDeltaTime), snap, FixedDeltaTime);

        Assert.True(rec.PendingInputs <= MaxInputHistory,
            $"History grew to {rec.PendingInputs}, above the {MaxInputHistory} cap.");
    }
}

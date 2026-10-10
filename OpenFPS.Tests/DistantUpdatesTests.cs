using System.Numerics;
using Arch.Core;
using MemoryPack;
using OpenFPS.Client.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Instruments;
using OpenFPS.Server;
using OpenFPS.Server.Core;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// Far things sent less often (docs/CODY_ASKS_2026-10-08.md section 4). Cody's condition: only where it costs
/// no realism, shown by a test of real scenes with a bearing within 1 degree, a pitch within 0.5 % and no steps
/// in a pass-by. The prediction both ends share (DistantMotion), the server's choice of what to send
/// (RestingStates), the client's carrying (ClientWorldState), and the city run both ways
/// (DistantUpdatesRig, which AudioLab runs as --distant-updates).
/// </summary>
public class DistantUpdatesTests
{
    private readonly ITestOutputHelper _o;
    public DistantUpdatesTests(ITestOutputHelper o) => _o = o;

    private const float Dt = PhysicsConstants.FixedDeltaTime;

    // ── The prediction ──────────────────────────────────────────────────────────────────────────

    /// <summary>Round a bend at a steady speed: what a car does in a corner and a train on a curve.</summary>
    [Fact]
    public void APredictionFollowsABendAtASteadySpeed()
    {
        const float radius = 60f, speed = 15f;
        float omega = speed / radius;
        Vector3 At(float t) => new(radius * MathF.Sin(omega * t), 0f, radius * (1f - MathF.Cos(omega * t)));
        Vector3 Vel(float t) => new(speed * MathF.Cos(omega * t), 0f, speed * MathF.Sin(omega * t));

        var rates = DistantMotion.RatesOf(Vel(-Dt), Vel(0f), Dt);
        foreach (float ahead in new[] { 0.1f, 0.2f, 0.4f })
        {
            var p = DistantMotion.Predict(At(0f), Vel(0f), Quaternion.Identity, rates, ahead);
            _o.WriteLine($"{ahead:F1} s ahead: {Vector3.Distance(p.Position, At(ahead)) * 1000:F2} mm out, speed {p.Velocity.Length():F4}");
            Assert.True(Vector3.Distance(p.Position, At(ahead)) < 0.002f);
            Assert.True(Vector3.Distance(p.Velocity, Vel(ahead)) < 0.002f);
        }
        // The difference of two velocities would have read the bend as braking.
        Assert.True(MathF.Abs(rates.SpeedRate) < 1e-3f, $"a steady bend read as {rates.SpeedRate} m/s²");
    }

    [Fact]
    public void APredictionStopsAndDoesNotGoBack()
    {
        var rates = new DistantMotion.Rates(-6f, Vector3.Zero);
        var p = DistantMotion.Predict(Vector3.Zero, new Vector3(0f, 0f, 3f), Quaternion.Identity, rates, 0.5f);
        Assert.Equal(0f, p.Velocity.Length());
        Assert.Equal(0.75f, p.Position.Z, 4);   // 3²/(2·6)
    }

    [Fact]
    public void NothingMovesAtZeroSeconds()
    {
        var at = new Vector3(812.5f, 0.1f, -1503.2f);
        var v = new Vector3(13f, 0f, 2f);
        var q = Quaternion.CreateFromYawPitchRoll(1.2f, 0f, 0f);
        var p = DistantMotion.Predict(at, v, q, new DistantMotion.Rates(2f, new Vector3(0f, 0.3f, 0f)), 0f);
        Assert.Equal(at, p.Position);
        Assert.Equal(v, p.Velocity);
        Assert.Equal(q, p.Rotation);
    }

    [Fact]
    public void RatesGoOverTheWireOnlyWhenThereAreAny()
    {
        var s = new EntityState
        {
            EntityId = 4321,
            Transform = QuantizedTransform.FromTransform(new Transform { Position = new Vector3(812.5f, 0.1f, -1503.2f), Rotation = Quaternion.Identity }),
            LinearVelocity = new Vector3(13f, 0f, 2f),
        };
        int plain = StatePacking.Pack(new List<EntityState> { s }, 0, 1).Length;
        var sent = new DistantMotion.Rates(-2.345f, new Vector3(0f, 0.4567f, 0f)).WriteTo(ref s);
        var back = new List<EntityState>();
        StatePacking.Unpack(StatePacking.Pack(new List<EntityState> { s }, 0, 1), back);
        var read = DistantMotion.Rates.Of(back[0]);
        Assert.Equal(sent.SpeedRate, read.SpeedRate);
        Assert.Equal(sent.Turn, read.Turn);
        Assert.Equal(-2.345f, read.SpeedRate, 3);
        Assert.Equal(plain + 8, StatePacking.Pack(new List<EntityState> { s }, 0, 1).Length);
    }

    // ── What the server sends ───────────────────────────────────────────────────────────────────

    private static EntityState Car(int id, Vector3 at, Vector3 velocity) => new()
    {
        EntityId = id,
        Transform = QuantizedTransform.FromTransform(new Transform
        {
            Position = at,
            Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.Atan2(velocity.X, velocity.Z)),
        }),
        LinearVelocity = velocity,
    };

    /// <summary>The ticks a car going steadily up the road is sent on, at a distance, from tick 1 to 60.</summary>
    private static List<long> SentTicks(float distance, Func<long, (Vector3 At, Vector3 V, byte Signals)> car)
    {
        var sent = new Dictionary<int, SentState>();
        var ticks = new List<long>();
        for (long tick = 1; tick <= 60; tick++)
        {
            var (at, v, signals) = car(tick);
            var s = Car(7, at, v);
            s.Signals = signals;
            if (RestingStates.ShouldSend(sent, ref s, tick, force: false, distance)) ticks.Add(tick);
        }
        return ticks;
    }

    [Fact]
    public void AFarSteadyCarGoesFiveTimesASecondANearOneEveryTick()
    {
        (Vector3, Vector3, byte) Steady(long tick) => (new Vector3(0f, 0f, 20f * tick * Dt), new Vector3(0f, 0f, 20f), 0);
        var far = SentTicks(300f, Steady);
        _o.WriteLine("far: " + string.Join(",", far));
        // Every sixth tick, and the once-a-second keep-alive every state has.
        Assert.All(far.Zip(far.Skip(1)), p => Assert.InRange(p.Second - p.First, 1, DistantMotion.IntervalTicks));
        Assert.InRange(far.Count, 10, 12);
        Assert.Equal(60, SentTicks(149f, Steady).Count);
        // Zero is what the broadcast passes for what the player rides, drives or carries.
        Assert.Equal(60, SentTicks(0f, Steady).Count);
    }

    [Fact]
    public void AFarCarThatBrakesGoesTheTickItStraysAndTheTickAfter()
    {
        // 20 m/s, then braking at 6 m/s² from tick 40.
        (Vector3, Vector3, byte) Braking(long tick)
        {
            float t = tick * Dt, t0 = 40 * Dt;
            if (tick < 40) return (new Vector3(0f, 0f, 20f * t), new Vector3(0f, 0f, 20f), 0);
            float b = t - t0;
            return (new Vector3(0f, 0f, 20f * t0 + 20f * b - 3f * b * b), new Vector3(0f, 0f, 20f - 6f * b), 0);
        }
        var ticks = SentTicks(300f, Braking);
        _o.WriteLine(string.Join(",", ticks));
        Assert.Contains(41L, ticks);   // the first tick the speed is off what was predicted
        Assert.Contains(42L, ticks);   // ...and once more
        Assert.DoesNotContain(39L, ticks);
    }

    [Fact]
    public void AFarHornGoesTheTickItSounds()
    {
        (Vector3, Vector3, byte) Horn(long tick) => (new Vector3(0f, 0f, 20f * tick * Dt), new Vector3(0f, 0f, 20f), (byte)(tick >= 33 ? 1 : 0));
        var ticks = SentTicks(300f, Horn);
        Assert.Contains(33L, ticks);
        Assert.Contains(34L, ticks);
    }

    /// <summary>
    /// A far car's keep-alive (once a second, by its id: tick 23 for entity 7) that falls on the tick it starts
    /// to brake still goes again the tick after. Without the repeat, that keep-alive lost on a poor connection
    /// left a car pulling away 543 m off read as still braking for a fifth of a second: 43 % of its pitch.
    /// </summary>
    [Fact]
    public void AFarKeepAliveOnTheTickACarStraysGoesAgainTheTickAfter()
    {
        Assert.Equal(0, (23 + 7) % RestingStates.KeepAliveTicks);
        (Vector3, Vector3, byte) Braking(long tick)
        {
            float t = tick * Dt, t0 = 22 * Dt;
            if (tick < 22) return (new Vector3(0f, 0f, 20f * t), new Vector3(0f, 0f, 20f), 0);
            float b = t - t0;
            return (new Vector3(0f, 0f, 20f * t0 + 20f * b - 3f * b * b), new Vector3(0f, 0f, 20f - 6f * b), 0);
        }
        var ticks = SentTicks(300f, Braking);
        _o.WriteLine(string.Join(",", ticks));
        Assert.Contains(23L, ticks);   // the keep-alive, and the first tick the speed is off what was predicted
        Assert.Contains(24L, ticks);   // ...and once more
    }

    [Fact]
    public void WhatAPlayerRidesOrCarriesIsAlwaysFullRate()
    {
        var world = World.Create();
        try
        {
            var body = world.Create(new Transform());
            var bus = world.Create(new Transform());
            var panel = world.Create(new Transform(), new ParentComponent { ParentEntityId = bus.Id });
            var gun = world.Create(new Transform(), new HeldComponent { HolderEntityId = body.Id });
            var other = world.Create(new Transform(), new ParentComponent { ParentEntityId = 999_999 });
            Assert.True(GameServer.Involved(world, bus, body, bus.Id));
            Assert.True(GameServer.Involved(world, panel, body, bus.Id));
            Assert.True(GameServer.Involved(world, gun, body, -1));
            Assert.False(GameServer.Involved(world, other, body, bus.Id));
            Assert.False(GameServer.Involved(world, bus, body, -1));
        }
        finally { World.Destroy(world); }
    }

    // ── The client ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A car 300 m away going round a bend, braking to a stop and pulling away, through the server's choice and
    /// the wire into two clients, one sent every tick: where each puts it, frame by frame.
    /// </summary>
    [Fact]
    public void TheClientCarriesAFarCarAsIfItWereSentEveryTick()
    {
        const int ticks = 12 * 30;
        var truth = new List<EntityState>();
        Vector3 at = new(300f, 0f, 0f);
        float speed = 0f, heading = 0f;
        for (int k = 0; k < ticks; k++)
        {
            float t = k * Dt;
            float accel = t < 4 ? 3f : t < 7 ? 0f : t < 9 ? -6f : t < 10 ? 0f : 2f;
            speed = MathF.Max(0f, speed + accel * Dt);
            if (t is >= 4 and < 7) heading += speed / 40f * Dt;
            var v = new Vector3(MathF.Sin(heading), 0f, MathF.Cos(heading)) * speed;
            at += v * Dt;
            truth.Add(Car(101, at, v));
        }

        List<(Vector3 P, Vector3 V)> Play(bool lessOften, out int statesSent)
        {
            var world = new ClientWorldState();
            var sent = new Dictionary<int, SentState>();
            var seen = new List<(Vector3, Vector3)>();
            var flight = new List<(double At, ServerStateUpdate U)>();
            statesSent = 0;
            const double frame = 1.0 / 60.0;
            int next = 0;
            for (int f = 0; f < ticks * 2 - 20; f++)
            {
                double now = f * frame;
                while (next < ticks && next * Dt <= now)
                {
                    var s = truth[next];
                    var update = new ServerStateUpdate { Tick = next + 1 };
                    float distance = lessOften ? Vector3.Distance(s.Transform.ToTransform().Position, Vector3.Zero) : 0f;
                    if (RestingStates.ShouldSend(sent, ref s, next + 1, force: false, distance)) { update.States.Add(s); statesSent++; }
                    foreach (var piece in NetworkService.Pieces(update, 1400))
                        flight.Add((next * Dt + 0.05, (ServerStateUpdate)MemoryPackSerializer.Deserialize<IMessage>(MemoryPackSerializer.Serialize<IMessage>(piece))!));
                    next++;
                }
                for (int i = 0; i < flight.Count; i++)
                    if (flight[i].At <= now) { world.SyncState(flight[i].U); flight.RemoveAt(i--); }
                world.UpdateInterpolation((float)frame, localPlayerId: 1);
                world.TryGetInterpolatedMotion(101, out var tr, out var vel);
                seen.Add((tr.Position, vel));
            }
            return seen;
        }

        var full = Play(false, out int fullStates);
        var less = Play(true, out int lessStates);
        double worstBearing = 0, worstSpeed = 0;
        for (int f = 0; f < full.Count; f++)
        {
            if (full[f].P == Vector3.Zero) continue;
            double a = Math.Atan2(full[f].P.X, full[f].P.Z), b = Math.Atan2(less[f].P.X, less[f].P.Z);
            worstBearing = Math.Max(worstBearing, Math.Abs(a - b) * 180 / Math.PI);
            float fs = full[f].V.Length();
            if (fs > 1f) worstSpeed = Math.Max(worstSpeed, Math.Abs(less[f].V.Length() / fs - 1));
        }
        _o.WriteLine($"states {fullStates} every tick, {lessStates} less often; worst bearing {worstBearing:F4}°, speed {worstSpeed * 100:F3} %");
        Assert.True(lessStates < fullStates / 2, $"{lessStates} of {fullStates} states sent");
        Assert.True(worstBearing < 0.05, $"bearing {worstBearing:F4}°");
        Assert.True(worstSpeed < 0.002, $"speed {worstSpeed * 100:F3} %");
    }

    // ── The city, both ways ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The acceptance test. The city runs on the real server with a car passing at 108 km/h, an airliner flying
    /// over, the trains and the walkers; two players stand side by side, one sent everything every tick and one
    /// far things less often, on a home connection (40 to 60 ms, nothing lost). For every scene, at every one of
    /// the mixer's instants, what the second is given is compared with the first.
    /// </summary>
    [Fact]
    public void FarThingsSentLessOftenSoundTheSame()
    {
        var rig = DistantUpdatesRig.Run(45, log: _o.WriteLine);
        foreach (var line in rig.Report()) _o.WriteLine(line);

        foreach (string name in new[] { "pass-by", "fly-over", "train", "walker", "traffic" })
        {
            Assert.True(rig.Scenes.TryGetValue(name, out var s), $"no {name} was heard");
            Assert.True(s!.FarInstants > 1000, $"{name}: never far");
            // Bearing within 1 degree and pitch within 0.5 %, against everything sent every tick and against the
            // server's own track.
            Assert.True(s.WorstBearing < 1.0, $"{name}: bearing {s.WorstBearing:F3}° ({s.WorstBearingAt})");
            Assert.True(s.WorstPitch < 0.005, $"{name}: pitch {s.WorstPitch * 100:F3} % ({s.WorstPitchAt})");
            Assert.True(s.TruthBearingLess < 1.0, $"{name}: bearing against the server {s.TruthBearingLess:F3}°");
            Assert.True(s.TruthPitchLess < 0.005, $"{name}: pitch against the server {s.TruthPitchLess * 100:F3} %");
            // No steps: no change from one mixer instant to the next bigger than every tick ever makes, but for
            // a hundredth of a degree, a hundredth of a percent of pitch, or a centimetre at 150 m and more.
            Assert.True(s.LessStepBearing <= s.FullStepBearing + 0.01, $"{name}: bearing step {s.LessStepBearing:F4}° against {s.FullStepBearing:F4}°");
            Assert.True(s.LessStepPitch <= s.FullStepPitch + 0.0001, $"{name}: pitch step {s.LessStepPitch * 100:F4} % against {s.FullStepPitch * 100:F4} %");
            Assert.True(s.LessStepPosition <= s.FullStepPosition + 0.01, $"{name}: step {s.LessStepPosition:F4} m against {s.FullStepPosition:F4} m");
            // A train's notch may change a step earlier or later, never stay apart.
            Assert.True(s.LeverRun <= 1, $"{name}: notches apart for {s.LeverRun} steps");
        }
        // The pass-by, the fly-over and the train each cross 150 m.
        foreach (string name in new[] { "pass-by", "fly-over", "train" })
            Assert.True(rig.Scenes[name].Instants > rig.Scenes[name].FarInstants, $"{name} never came within 150 m");
        foreach (var b in rig.Bytes)
            Assert.True(b.LessMbit < 0.6 * b.FullMbit, $"at {b.Where}: {b.LessMbit:F2} Mbit/s against {b.FullMbit:F2}");
    }

    /// <summary>
    /// The same on a poor connection: 30 to 90 ms, so one tick in ten overtakes the one before, and 2 % lost.
    /// Both players' clients leave the server's own track then, where the stream runs dry; the one sent far
    /// things less often is no further off, in bearing or in pitch, than the one sent everything.
    /// </summary>
    [Fact]
    public void OnAPoorConnectionFarThingsAreNoFurtherOff()
    {
        var rig = DistantUpdatesRig.Run(45, log: _o.WriteLine, network: DistantUpdatesRig.Network.Poor);
        foreach (var line in rig.Report()) _o.WriteLine(line);
        foreach (var s in rig.Scenes.Values)
            Assert.True(s.TruthBearingLess <= s.TruthBearingFull + 0.1,
                $"{s.Name}: {s.TruthBearingLess:F3}° off the server's track against {s.TruthBearingFull:F3}° sent every tick");
        foreach (var s in rig.Scenes.Values)
            Assert.True(s.TruthPitchLess <= s.TruthPitchFull + 0.005,
                $"{s.Name}: pitch {s.TruthPitchLess * 100:F3} % off the server's track against {s.TruthPitchFull * 100:F3} % sent every tick");
    }
}

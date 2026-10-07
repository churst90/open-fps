using System.Numerics;
using MemoryPack;
using OpenFPS.Client.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server;
using OpenFPS.Server.Core;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// The state stream got smaller (2026-10-05): resting things are not re-sent every tick, the states are
/// packed by hand, and the stats go only when they change. None of it may change where anything is
/// heard. These tests hold the packing to what the old encoding carried, and drive the same motion
/// through the client's interpolation both ways and compare what comes out, frame by frame.
/// </summary>
public class NetworkTrimTests
{
    private readonly ITestOutputHelper _o;
    public NetworkTrimTests(ITestOutputHelper o) => _o = o;

    // ── Packing ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>The angle between two rotations, in degrees, worked in doubles: an acos of a float dot
    /// product cannot see anything under about 0.04 degrees.</summary>
    private static double Degrees(Quaternion a, Quaternion b)
    {
        double d = Math.Min(Dist(a, b, 1), Dist(a, b, -1));
        return 4 * Math.Asin(Math.Min(1, d / 2)) * 180 / Math.PI;

        static double Dist(Quaternion a, Quaternion b, int sign)
        {
            double x = a.X - sign * (double)b.X, y = a.Y - sign * (double)b.Y, z = a.Z - sign * (double)b.Z, w = a.W - sign * (double)b.W;
            return Math.Sqrt(x * x + y * y + z * z + w * w);
        }
    }

    private static EntityState RoundTrip(EntityState s)
    {
        var list = new List<EntityState>();
        StatePacking.Unpack(StatePacking.Pack(new List<EntityState> { s }, 0, 1), list);
        Assert.Single(list);
        return list[0];
    }

    [Fact]
    public void PositionTyreDemandAndWheelsComeBackExactly()
    {
        var wheels = new[]
        {
            WheelState.Encode(4500f, 33.3f, 0.02f, -0.01f, 3, 0.4f),
            WheelState.Encode(4400f, 33.1f, 0.01f, 0.03f, 3, 0.5f),
        };
        var s = new EntityState
        {
            EntityId = 2_000_000,
            Transform = new QuantizedTransform { X = -4_999_999, Y = 12_345, Z = 1_234_567, QW = 32767 },
            LinearVelocity = new Vector3(1, 0, 0),
            TyreDemand = 201,
            Wheels = wheels,
        };
        var back = RoundTrip(s);
        Assert.Equal(s.EntityId, back.EntityId);
        Assert.Equal((s.Transform.X, s.Transform.Y, s.Transform.Z), (back.Transform.X, back.Transform.Y, back.Transform.Z));
        Assert.Equal(s.TyreDemand, back.TyreDemand);
        Assert.NotNull(back.Wheels);
        Assert.Equal(wheels, back.Wheels);

        // No wheels stays no wheels: the client keeps the last it was sent.
        Assert.Null(RoundTrip(new EntityState { EntityId = 5 }).Wheels);
    }

    [Fact]
    public void RotationIsAsFineAsTheFourShortsItReplaces()
    {
        var rng = new Random(7);
        double worst = 0;
        for (int i = 0; i < 20000; i++)
        {
            var q = Quaternion.Normalize(new Quaternion((float)rng.NextDouble() * 2 - 1, (float)rng.NextDouble() * 2 - 1,
                                                        (float)rng.NextDouble() * 2 - 1, (float)rng.NextDouble() * 2 - 1));
            if (i < 8) q = Quaternion.CreateFromYawPitchRoll(i * MathF.PI / 4, 0, 0);   // the yaws a car mostly has
            var t = QuantizedTransform.FromTransform(new Transform { Rotation = q });
            var before = t.ToTransform().Rotation;
            var after = RoundTrip(new EntityState { EntityId = 1, Transform = t }).Transform.ToTransform().Rotation;
            worst = Math.Max(worst, Degrees(before, after));
            // ...and the same sign it went with, not its negation.
            Assert.True(Quaternion.Dot(before, after) > 0, $"{before} came back as {after}");
        }
        _o.WriteLine($"worst rotation change through the wire: {worst:F4} degrees");
        Assert.True(worst < 0.01, $"a rotation moved {worst:F4} degrees");
    }

    [Fact]
    public void VelocityIsAMillimetreASecondBelow32AndExactAbove()
    {
        var slow = new Vector3(1.23456f, -0.0004f, 31.9f);
        var back = RoundTrip(new EntityState { EntityId = 1, LinearVelocity = slow }).LinearVelocity;
        Assert.True(Vector3.Distance(slow, back) < 0.001f, $"{slow} came back {back}");
        Assert.Equal(StatePacking.WireVelocity(slow), back);

        var fast = new Vector3(83.123457f, 0.5f, -1.25f);   // a car on the speedway: one axis over the range
        Assert.Equal(fast, RoundTrip(new EntityState { EntityId = 1, LinearVelocity = fast }).LinearVelocity);

        Assert.Equal(Vector3.Zero, RoundTrip(new EntityState { EntityId = 1, LinearVelocity = new Vector3(0.0004f, 0, -0.0004f) }).LinearVelocity);
    }

    [Fact]
    public void AWalkerIsHalfWhatItWas()
    {
        var walker = new EntityState
        {
            EntityId = 4321,
            Transform = QuantizedTransform.FromTransform(new Transform { Position = new Vector3(812.5f, 0.1f, -1503.2f),
                Rotation = Quaternion.CreateFromYawPitchRoll(1.2f, 0, 0) }),
            LinearVelocity = new Vector3(0.8f, 0, 1.1f),
        };
        int packed = StatePacking.Pack(new List<EntityState> { walker }, 0, 1).Length;
        int before = MemoryPackSerializer.Serialize(walker).Length;
        _o.WriteLine($"a walker: {before} bytes as MemoryPack wrote it, {packed} packed");
        Assert.True(packed <= 27, $"{packed} bytes");
        Assert.True(packed <= before * 0.6, $"{packed} packed against {before}");
    }

    // ── Resting ────────────────────────────────────────────────────────────────────────────────

    private static EntityState At(int id, float z, float speed, WheelState[]? wheels = null) => new()
    {
        EntityId = id,
        Transform = QuantizedTransform.FromTransform(new Transform { Position = new Vector3(0, 0, z), Rotation = Quaternion.Identity }),
        LinearVelocity = new Vector3(0, 0, speed),
        Wheels = wheels,
    };

    [Fact]
    public void AThingThatStopsIsSentThreeTimesThenOnceASecond()
    {
        var sent = new Dictionary<int, SentState>();
        const int id = 17;
        var ticks = new List<long>();
        float z = 0;
        for (long tick = 1; tick <= 160; tick++)
        {
            float speed = tick <= 10 || tick > 130 ? 5f : 0f;
            z += speed * PhysicsConstants.FixedDeltaTime;
            var s = At(id, z, speed);
            if (RestingStates.ShouldSend(sent, ref s, tick, force: false)) ticks.Add(tick);
        }
        _o.WriteLine(string.Join(",", ticks));
        // Moving: every tick.
        for (long t = 1; t <= 10; t++) Assert.Contains(t, ticks);
        for (long t = 131; t <= 160; t++) Assert.Contains(t, ticks);
        // Stopped at 11: that tick and two more, then keep-alives only.
        Assert.Contains(11L, ticks); Assert.Contains(12L, ticks); Assert.Contains(13L, ticks);
        var resting = ticks.Where(t => t > 13 && t <= 130).ToList();
        Assert.All(resting, t => Assert.Equal(0, (t + id) % RestingStates.KeepAliveTicks));
        Assert.InRange(resting.Count, 3, 5);
    }

    [Fact]
    public void WheelsThatHaveNotChangedStayBehind()
    {
        var sent = new Dictionary<int, SentState>();
        var wheels = new[] { WheelState.Encode(4000, 10, 0, 0, 1, 0.2f) };
        int withWheels = 0;
        for (long tick = 1; tick <= 20; tick++)
        {
            var s = At(9, tick * 0.5f, 15f, wheels);          // moving steadily, the same wheels throughout
            Assert.True(RestingStates.ShouldSend(sent, ref s, tick, force: false));
            if (s.Wheels != null) withWheels++;
        }
        Assert.InRange(withWheels, 3, 4);   // the first, two repeats, and perhaps a keep-alive
    }

    [Fact]
    public void ForcedStatesAlwaysGo()
    {
        var sent = new Dictionary<int, SentState>();
        for (long tick = 1; tick <= 100; tick++)
        {
            var s = At(3, 1f, 0f);
            Assert.True(RestingStates.ShouldSend(sent, ref s, tick, force: true));
        }
    }

    [Fact]
    public void StatsThatSayTheSameThingAreTheSame()
    {
        var a = new StatsUpdate { Health = 100, MaxHealth = 100, CurrentMaterial = "Concrete", HeldRounds = 5 };
        var b = new StatsUpdate { Health = 100, MaxHealth = 100, CurrentMaterial = "Concrete", HeldRounds = 5 };
        Assert.False(GameServer.SameStats(null, b));
        Assert.True(GameServer.SameStats(a, b));
        b.HeldRounds = 4;
        Assert.False(GameServer.SameStats(a, b));
        // Lifting a body slows you: the new pace must go, or the client predicts the old one.
        b.HeldRounds = 5;
        b.SpeedLimit = 1f;
        Assert.False(GameServer.SameStats(a, b));
    }

    // ── The client hears the same world ─────────────────────────────────────────────────────────

    /// <summary>
    /// A small street, scripted: a car that waits, pulls away, cruises round a bend, brakes to a stop,
    /// waits and goes again; a walker who stops and starts; a car parked throughout; and the client's
    /// own body. Each tick's true states, in the order the server would gather them.
    /// </summary>
    private static List<List<EntityState>> Street(int ticks)
    {
        const int car = 101, walker = 102, parked = 103, me = 1;
        var wheels = new WheelState[4];
        var stream = new List<List<EntityState>>();
        Vector3 carPos = new(0, 0, 0), walkerPos = new(5, 0, 0);
        float carSpeed = 0, yaw = 0;
        float dt = PhysicsConstants.FixedDeltaTime;
        for (int k = 0; k < ticks; k++)
        {
            float t = k * dt;
            // car: rest 1 s, 2.5 m/s² for 4 s, cruise and turn 3 s, brake 2 s, rest 2 s, go again.
            float accel = t < 1 ? 0 : t < 5 ? 2.5f : t < 8 ? 0 : t < 10 ? -5f : t < 12 ? 0 : 3f;
            carSpeed = MathF.Max(0, carSpeed + accel * dt);
            float yawRate = t is >= 5 and < 8 ? 0.3f : 0f;
            yaw += yawRate * dt;
            var heading = new Vector3(MathF.Sin(yaw), 0, MathF.Cos(yaw));
            carPos += heading * carSpeed * dt;
            for (int w = 0; w < 4; w++)
                wheels[w] = WheelState.Encode(4000 + w, carSpeed / 0.3f, 0, 0, 1, 0.1f + accel * 0.02f);

            // walker: 1.4 m/s for 1.5 s, still for 1 s, and so on.
            float walkSpeed = (t % 2.5f) < 1.5f ? 1.4f : 0f;
            walkerPos += new Vector3(walkSpeed * dt, 0, 0);

            stream.Add(new List<EntityState>
            {
                new() { EntityId = me, Transform = Q(new Vector3(-3, 0, 0), 0) },
                new() { EntityId = car, Transform = Q(carPos, yaw), LinearVelocity = heading * carSpeed,
                        TyreDemand = EntityState.EncodeTyreDemand(0.1f + MathF.Abs(accel) * 0.05f), Wheels = (WheelState[])wheels.Clone() },
                new() { EntityId = walker, Transform = Q(walkerPos, MathF.PI / 2), LinearVelocity = new Vector3(walkSpeed, 0, 0) },
                new() { EntityId = parked, Transform = Q(new Vector3(10, 0, 3), 1f), Wheels = new WheelState[4] },
            });
        }
        return stream;

        static QuantizedTransform Q(Vector3 p, float yaw)
            => QuantizedTransform.FromTransform(new Transform { Position = p, Rotation = Quaternion.CreateFromYawPitchRoll(yaw, 0, 0) });
    }

    /// <summary>What the client's interpolation makes of a stream, sampled every frame at 60 Hz.
    /// Each tick arrives 40 ms after the server made it; a null in <paramref name="packets"/> never does.</summary>
    private static List<Dictionary<int, (Vector3 Pos, Quaternion Rot, Vector3 Vel, WheelState[]? Wheels)>> Play(
        List<ServerStateUpdate?> packets, int[] ids, int frames)
    {
        var world = new ClientWorldState();
        var seen = new List<Dictionary<int, (Vector3, Quaternion, Vector3, WheelState[]?)>>();
        const float frameDt = 1f / 60f;
        int next = 0;
        var snapshotWheels = typeof(ClientWorldState).GetField("_serverWheels",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        for (int f = 0; f < frames; f++)
        {
            double now = f * frameDt;
            while (next < packets.Count && next * PhysicsConstants.FixedDeltaTime + 0.04 <= now)
            {
                if (packets[next] is { } p) world.SyncState(p);
                next++;
            }
            world.UpdateInterpolation(frameDt, localPlayerId: 1);
            var wheels = (System.Collections.Concurrent.ConcurrentDictionary<int, WheelState[]>)snapshotWheels.GetValue(world)!;
            var frame = new Dictionary<int, (Vector3, Quaternion, Vector3, WheelState[]?)>();
            foreach (int id in ids)
                if (world.TryGetInterpolatedTransform(id, out var tr))
                    frame[id] = (tr.Position, tr.Rotation, Velocity(world, id), wheels.GetValueOrDefault(id));
            seen.Add(frame);
        }
        return seen;
    }

    private static Vector3 Velocity(ClientWorldState world, int id)
    {
        var field = typeof(ClientWorldState).GetField("_serverVelocities",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var d = (System.Collections.Concurrent.ConcurrentDictionary<int, Vector3>)field.GetValue(world)!;
        return d.GetValueOrDefault(id);
    }

    /// <summary>The old stream: everything every tick, as MemoryPack wrote the list.</summary>
    private static List<ServerStateUpdate?> Old(List<List<EntityState>> street, HashSet<int> lost)
        => street.Select((states, k) => lost.Contains(k) ? null
            : (ServerStateUpdate)MemoryPackSerializer.Deserialize<IMessage>(MemoryPackSerializer.Serialize<IMessage>(
                new ServerStateUpdate { Tick = k + 1, States = states.Select(Copy).ToList() }))!).ToList();

    /// <summary>The new: through RestingStates, packed, split into packets, and read back.</summary>
    private static List<ServerStateUpdate?> New(List<List<EntityState>> street, HashSet<int> lost, out int statesSent)
    {
        var sent = new Dictionary<int, SentState>();
        var list = new List<ServerStateUpdate?>();
        statesSent = 0;
        for (int k = 0; k < street.Count; k++)
        {
            var update = new ServerStateUpdate { Tick = k + 1 };
            foreach (var original in street[k])
            {
                var s = Copy(original);
                if (RestingStates.ShouldSend(sent, ref s, k + 1, force: s.EntityId == 1)) update.States.Add(s);
            }
            statesSent += update.States.Count;
            if (lost.Contains(k)) { list.Add(null); continue; }
            ServerStateUpdate? merged = null;
            foreach (var piece in NetworkService.Pieces(update, 1024))
            {
                var back = (ServerStateUpdate)MemoryPackSerializer.Deserialize<IMessage>(MemoryPackSerializer.Serialize<IMessage>(piece))!;
                if (merged == null) merged = back; else merged.States.AddRange(back.States);
            }
            list.Add(merged);
        }
        return list;
    }

    private static EntityState Copy(EntityState s) { s.Wheels = (WheelState[]?)s.Wheels?.Clone(); return s; }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.05)]
    [InlineData(-1.0)]
    public void TheClientHearsTheSameStreet(double loss)
    {
        const int ticks = 15 * 30;
        var street = Street(ticks);
        var rng = new Random(11);
        var lost = new HashSet<int>(Enumerable.Range(0, ticks).Where(_ => rng.NextDouble() < loss));
        // -1: the worst places to lose packets — the ticks where the walker stops (1.5 s) and sets off
        // (2.5 s), where the car sets off (1 s), stops (10 s) and sets off again (12 s), two or three at once.
        if (loss < 0) lost = new HashSet<int> { 29, 30, 31, 44, 45, 46, 74, 75, 299, 300, 301, 359, 360, 361 };
        int[] ids = { 101, 102, 103 };
        int frames = 14 * 60;

        var before = Play(Old(street, lost), ids, frames);
        var after = Play(New(street, lost, out int statesSent), ids, frames);
        int statesBefore = street.Sum(s => s.Count);
        _o.WriteLine($"states: {statesBefore} before, {statesSent} after ({100.0 * statesSent / statesBefore:F0} %)");

        foreach (int id in ids)
        {
            float worstPos = 0, worstVel = 0, worstDeg = 0; int at = -1, wheelsDiffer = 0;
            for (int f = 0; f < frames; f++)
            {
                bool a = before[f].TryGetValue(id, out var b), c = after[f].TryGetValue(id, out var n);
                Assert.Equal(a, c);
                if (!a) continue;
                float d = Vector3.Distance(b.Pos, n.Pos);
                if (d > worstPos) { worstPos = d; at = f; }
                worstVel = MathF.Max(worstVel, Vector3.Distance(b.Vel, n.Vel));
                worstDeg = MathF.Max(worstDeg, (float)Degrees(b.Rot, n.Rot));
                if (!(b.Wheels ?? Array.Empty<WheelState>()).SequenceEqual(n.Wheels ?? Array.Empty<WheelState>())) wheelsDiffer++;
            }
            _o.WriteLine($"entity {id}: worst position {worstPos * 1000:F2} mm (frame {at}), velocity {worstVel * 1000:F2} mm/s, "
                         + $"rotation {worstDeg:F4} deg, frames with other wheels {wheelsDiffer}");
            // Nothing may differ by more than the wire's own rounding, lost packets or not: the old stream
            // bridged a lost tick from the ticks either side, and the new one bridges it the same way —
            // a thing missing from a snapshot because it rested is held, and one missing because the
            // packet was lost goes on from where it was last seen. Without that (measured): the walker
            // 12 cm out and 1.2 m/s wrong as it set off after a lost packet, the car 2 mm out pulling away.
            Assert.True(worstPos < 0.0015f, $"entity {id} was {worstPos * 1000:F2} mm away from where the old stream put it");
            Assert.True(worstVel < 0.0015f, $"entity {id}'s velocity differed by {worstVel * 1000:F2} mm/s");
            Assert.True(worstDeg < 0.02f, $"entity {id} turned {worstDeg:F4} degrees differently");
            Assert.Equal(0, wheelsDiffer);
        }

        // And at rest exactly where the old stream had it, lost packets or not: the parked car throughout,
        // and the car while it waits at 11.5 s.
        for (int f = 0; f < frames; f++)
            if (before[f].TryGetValue(103, out var b))
                Assert.True(Vector3.Distance(b.Pos, after[f][103].Pos) < 0.0015f);
        int waiting = (int)(11.5 * 60);
        Assert.True(Vector3.Distance(before[waiting][101].Pos, after[waiting][101].Pos) < 0.0015f,
                    $"the waiting car is {Vector3.Distance(before[waiting][101].Pos, after[waiting][101].Pos) * 1000:F1} mm from where it stopped");
    }
}

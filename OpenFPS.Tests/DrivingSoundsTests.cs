using System.Numerics;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// What the client makes of a driver's horn and siren switches, and of a vehicle's wheels over a
/// level crossing's rails: the voices the mixer is asked for, and when each tyre strikes.
/// </summary>
public class DrivingSoundsTests
{
    private readonly ITestOutputHelper _o;
    public DrivingSoundsTests(ITestOutputHelper o) => _o = o;

    private static void Signals(ClientAudioHarness h, int id, Vector3 at, byte signals, Vector3 velocity = default)
        => h.World.SyncState(new[] { new EntityState
        {
            EntityId = id,
            Transform = QuantizedTransform.FromTransform(new Transform { Position = at, Rotation = Quaternion.Identity }),
            LinearVelocity = velocity,
            Signals = signals,
        } });

    [Fact]
    public void AHeldHornIsTheVehiclesOwnHornAtItsNoseUntilTheKeyComesUp()
    {
        var h = new ClientAudioHarness();
        h.StandAt(new Vector3(0f, 0f, 20f));
        const int bus = 501;
        var at = new Vector3(0f, 0f, 0f);
        h.AddCar(bus, "transit_bus", at);
        Signals(h, bus, at, VehicleSignalBits.Encode(true, true, false, SirenMode.Wail));
        int voice = ClientAudioSystem.HeldHornVoiceBase - bus;
        Assert.True(h.TickUntil(() => h.Mixer.WasStarted(voice), 60));
        var e = h.Mixer.Latest[voice];
        _o.WriteLine($"{e.PhysicalKey} at {e.Position}, blowing {e.EngineRunning}");
        Assert.Equal("horn:air:bus_horn:hold", e.PhysicalKey);
        Assert.True(e.EngineRunning);
        // A bus is twelve metres long: its horn is at its nose, not a car's 1.9 m ahead of the middle.
        Assert.InRange(e.Position.Z, 5f, 6f);

        Signals(h, bus, at, VehicleSignalBits.Encode(true, false, false, SirenMode.Wail));
        h.Tick(3);
        Assert.False(h.Mixer.Latest[voice].EngineRunning);
        Assert.DoesNotContain(voice, h.Mixer.Stopped);
        h.Tick((int)(1.2 * ClientAudioSystem.UpdateHz));
        Assert.Contains(voice, h.Mixer.Stopped);
    }

    [Fact]
    public void ADriversSirenFollowsItsSwitchAndTrafficsDoesNot()
    {
        var h = new ClientAudioHarness();
        h.StandAt(new Vector3(0f, 0f, 30f));
        const int car = 601;
        var at = Vector3.Zero;
        h.AddCar(car, "police_interceptor", at);
        int voice = ClientAudioSystem.SirenVoiceBase - car;
        // Standing still with the switch on: a driver's siren sounds where traffic's never would.
        Signals(h, car, at, VehicleSignalBits.Encode(true, false, true, SirenMode.Yelp));
        Assert.True(h.TickUntil(() => h.Mixer.WasStarted(voice), 60));
        Assert.Equal((float)(int)SirenMode.Yelp, h.Mixer.Latest[voice].PowerLever);
        Signals(h, car, at, VehicleSignalBits.Encode(true, false, true, SirenMode.Phaser));
        h.Tick(2);
        Assert.Equal((float)(int)SirenMode.Phaser, h.Mixer.Latest[voice].PowerLever);
        Signals(h, car, at, VehicleSignalBits.Encode(true, false, false, SirenMode.Phaser));
        h.Tick(2);
        Assert.Contains(voice, h.Mixer.Stopped);

        // The same car as traffic, standing: SirenController keeps it quiet.
        var t = new ClientAudioHarness();
        t.StandAt(new Vector3(0f, 0f, 30f));
        t.AddCar(car, "police_interceptor", at);
        t.Tick(60);
        Assert.False(t.Mixer.WasStarted(voice));
    }

    [Fact]
    public void EveryWheelStrikesEachRailOnceInTheOrderACarMeetsThem()
    {
        var h = new ClientAudioHarness();
        // Rails running east-west across the origin, a road crossing them northward.
        h.Audio.SetCrossings(new[] { new CrossingRails { Name = "test", Centre = Vector3.Zero, Along = Vector3.UnitX, HalfLengthMetres = 7f } });
        const string preset = "i4_economy";
        float v = 10f;
        var strikes = new List<WheelStrike>();
        double t0 = 100.0;
        // The car's middle from 12 m south to 12 m north, a frame at a time.
        for (int f = 0; f <= 240; f++)
        {
            double now = t0 + f / 60.0;
            var pos = new Vector3(1.5f, 0f, -12f + v * (float)(f / 60.0));
            var snap = new EntitySnapshot
            {
                Id = 77, Transform = new Transform { Position = pos, Rotation = Quaternion.Identity },
                Velocity = new Vector3(0f, 0f, v),
            };
            if (h.Audio.RailStrikes(snap, preset, now) is { } batch) strikes.AddRange(batch);
        }
        foreach (var s in strikes.OrderBy(s => s.At))
            _o.WriteLine($"wheel {s.Wheel} at {s.At - t0:F3} s, {20 * Math.Log10(s.Pascals / 20e-6):F1} dB peak, contact {s.ContactSeconds * 1000:F1} ms");
        var body = new WheelDynamics(MachineRegistry.VehicleFor(preset));
        Assert.Equal(body.Wheels.Length * 2, strikes.Count);
        Assert.Equal(strikes.Count, strikes.Select(s => (s.Wheel, Math.Round(s.At, 3))).Distinct().Count());
        // Each wheel: the two rails a rail-centre apart in time.
        foreach (var g in strikes.GroupBy(s => s.Wheel))
        {
            var two = g.OrderBy(s => s.At).ToArray();
            Assert.Equal(CrossingRails.StandardRailCentres / v, two[1].At - two[0].At, 2);
        }
        // Front wheels before the back ones, a wheelbase apart.
        double front = strikes.Where(s => body.Wheels[s.Wheel].Front).Min(s => s.At);
        double rear = strikes.Where(s => !body.Wheels[s.Wheel].Front).Min(s => s.At);
        Assert.Equal(body.Wheelbase / v, rear - front, 2);
    }

    [Fact]
    public void AStrikeLandsOnTheSamplePlayedAtItsTime()
    {
        var w = new WheelStrikes(new[] { 0.33f, 0.33f }, 48000f);
        // Playing sample 10,000 now; 0.5 s rendered ahead; the strike 0.6 s from now.
        w.Queue(new[] { new WheelStrike(1, 5.6, 2f, 0.015f) });
        w.Drain(written: 10_000 + 24_000, played: 10_000, now: 5.0, sampleRate: 48000f, consumeRate: 1f);
        Assert.True(w.Busy);
        long due = 10_000 + (long)(0.6 * 48000);
        float before = 0f, after = 0f;
        for (long s = 34_000; s < due + 4800; s++)
        {
            w.Advance(s);
            float a = MathF.Abs(w.Out(0)), b = MathF.Abs(w.Out(1));
            Assert.Equal(0f, a);
            if (s < due) before = MathF.Max(before, b); else after = MathF.Max(after, b);
        }
        _o.WriteLine($"before {before}, after {after} Pa");
        Assert.Equal(0f, before);
        Assert.InRange(after, 1.2f, 3f);
    }
}

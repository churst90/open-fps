using System;
using System.Collections.Generic;
using System.Linq;
using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Core.Engine;
using OpenFPS.Client.AudioEngine.Core.Pneumatics;
using OpenFPS.Client.AudioEngine.Fmod;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// What the engine mutation run (2026-09-25 to 09-28, 93.7%) found unchecked: the scripted driver's
/// pedals, clutch and shifts, the network driver switching the engine off, the valve solver at the
/// ends of its bracket, and the air system's compressor.
/// </summary>
public class EngineMutationTests
{
    private const int Sr = 22050;
    private const float Dt = 1f / Sr;

    private static (EngineSynth Engine, Driveline Line, Driver Driver) Car()
    {
        var v = VehicleProfile.V8Muscle;
        var engine = new EngineSynth(v.Engine, Sr, 4);
        var dl = new Driveline(v);
        return (engine, dl, new Driver(dl, engine));
    }

    private static float LaunchRpm(EngineProfile e) => MathF.Max(e.IdleRpm * 2.2f, e.PeakTorqueRpm * 0.45f);

    // ── The scripted driver ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(DriverAction.Off)]
    [InlineData(DriverAction.ShuttingDown)]
    public void OffAndShuttingDownCutTheIgnitionAndTheStarter(DriverAction action)
    {
        var (engine, dl, driver) = Car();
        engine.Ignition = true; engine.Starter = true; engine.Throttle = 0.5f;
        driver.Apply(new DriveOrder(action, 1f), 0f, Dt);
        Assert.False(engine.Ignition);
        Assert.False(engine.Starter);
        Assert.Equal(0f, engine.Throttle);
        Assert.Equal(0, dl.Gear);
        Assert.Equal(1f, dl.Brake);
    }

    /// <summary>The brake pedal is on only for a braking order, and a car that is braking or coasting
    /// is not put into gear from neutral; one that is to be driven is.</summary>
    [Fact]
    public void OnlyABrakingOrderBrakesAndOnlyADrivingOrderPutsItInGear()
    {
        var (_, dl, driver) = Car();
        driver.Apply(new DriveOrder(DriverAction.Braking, 1f), 0f, Dt);
        Assert.Equal(0.7f, dl.Brake);
        Assert.Equal(0, dl.Gear);

        driver.Apply(new DriveOrder(DriverAction.Coasting, 1f), 0f, Dt);
        Assert.Equal(0f, dl.Brake);
        Assert.Equal(0, dl.Gear);

        driver.Apply(new DriveOrder(DriverAction.Cruising, 1f, 10f), 0f, Dt);
        Assert.Equal(0f, dl.Brake);
        Assert.Equal(1, dl.Gear);
    }

    /// <summary>Full throttle below the target speed, a trickle at or above it; cruising opens the
    /// throttle in proportion to how far short it is.</summary>
    [Fact]
    public void TheThrottleFollowsTheIntent()
    {
        var (engine, dl, driver) = Car();
        dl.Gear = 3;
        dl.Teleport(10f);
        driver.Apply(new DriveOrder(DriverAction.Accelerating, 1f, 10f), 0f, Dt);
        Assert.Equal(0.2f, engine.Throttle, 5);                  // at the target: a trickle
        driver.Apply(new DriveOrder(DriverAction.Accelerating, 1f, 30f), 0f, Dt);
        Assert.Equal(1f, engine.Throttle, 5);
        driver.Apply(new DriveOrder(DriverAction.Cruising, 1f, 10.4f), 0f, Dt);
        Assert.Equal(0.18f + 0.4f * 0.25f, engine.Throttle, 4);
        Assert.Equal(1f, dl.Clutch);
    }

    /// <summary>Rolling slowly in gear off the throttle, the clutch goes in so the engine does not
    /// stall; with the throttle open it stays up.</summary>
    [Fact]
    public void RollingToAStopInGearTheClutchGoesIn()
    {
        var (engine, dl, driver) = Car();
        dl.Gear = 2;
        dl.Teleport(0.5f);
        Assert.True(dl.GearRpm(2) < engine.Profile.IdleRpm * 0.9f);
        driver.Apply(new DriveOrder(DriverAction.Coasting, 1f), 0f, Dt);
        Assert.Equal(0f, dl.Clutch);
        Assert.Equal(0f, engine.Throttle);
        driver.Apply(new DriveOrder(DriverAction.Cruising, 1f, 1f), 0f, Dt);
        Assert.Equal(1f, dl.Clutch);
    }

    /// <summary>
    /// At a standstill in first the clutch slips and the throttle is set to hold launch revs, and from
    /// a dead engine that is the most slip and the most throttle there is. On the road, the engine is
    /// held near launch revs until the road catches up.
    /// </summary>
    [Fact]
    public void ALaunchSlipsTheClutchAndHoldsLaunchRevs()
    {
        var (engine, dl, driver) = Car();
        driver.Apply(new DriveOrder(DriverAction.Accelerating, 1f, 20f), 0f, Dt);
        Assert.Equal(1, dl.Gear);
        Assert.Equal(0.08f, dl.Clutch, 4);                       // engine at 0 rpm: all the way down
        Assert.Equal(0.9f, engine.Throttle, 4);

        // Start it, then drive off.
        var start = new DriveOrder(DriverAction.Idling, 3f);
        for (int i = 0; i < Sr * 3; i++) { driver.Apply(start, i * Dt, Dt); dl.Step(engine, Dt); }
        float launch = LaunchRpm(engine.Profile);
        var go = new DriveOrder(DriverAction.Accelerating, 3f, 25f);
        var revs = new List<float>();
        var clutch = new List<float>();
        for (int i = 0; i < Sr * 3; i++)
        {
            driver.Apply(go, i * Dt, Dt);
            dl.Step(engine, Dt);
            if (i > Sr / 2 && dl.Gear == 1 && dl.GearRpm(1) < launch * 0.8f) { revs.Add(engine.Rpm); clutch.Add(dl.Clutch); }
        }
        Assert.NotEmpty(revs);
        Assert.InRange(revs.Average(), launch * 0.8f, launch * 1.25f);
        Assert.True(clutch.All(c => c > 0.079f && c < 1f), "the clutch did not slip through the launch");
        Assert.True(dl.Speed > 3f, $"the car barely moved: {dl.Speed:F1} m/s");
    }

    /// <summary>
    /// A shift is throttle off, clutch down, neutral for the gearbox's shift time, then the next gear
    /// and the clutch up. Up the box at full throttle, and down one at a time on the brakes, in four
    /// fifths of the time.
    /// </summary>
    [Fact]
    public void ShiftsAreThrottleOffClutchDownAndOneGearAtATime()
    {
        var (engine, dl, driver) = Car();
        var gb = dl.Vehicle.Gearbox;
        var start = new DriveOrder(DriverAction.Idling, 3f);
        for (int i = 0; i < Sr * 3; i++) { driver.Apply(start, i * Dt, Dt); dl.Step(engine, Dt); }

        var shifts = new List<(int From, int To, float Seconds)>();
        int lastGear = dl.Gear, fromGear = 0;
        float shiftStart = -1f;
        void Drive(DriveOrder order, float seconds)
        {
            for (int i = 0; i < Sr * seconds; i++)
            {
                float t = i * Dt;
                driver.Apply(order, t, Dt);
                if (driver.Shifting)
                {
                    Assert.Equal(0f, engine.Throttle);
                    Assert.Equal(0f, dl.Clutch);
                    Assert.Equal(0, dl.Gear);
                    if (shiftStart < 0f) { shiftStart = t; fromGear = lastGear; }
                }
                else if (shiftStart >= 0f)
                {
                    shifts.Add((fromGear, dl.Gear, t - shiftStart));
                    shiftStart = -1f;
                }
                if (dl.Gear != 0) lastGear = dl.Gear;
                dl.Step(engine, Dt);
            }
        }
        Drive(new DriveOrder(DriverAction.Accelerating, 20f, 45f), 20f);
        var ups = shifts.ToList();
        Assert.True(ups.Count >= 2, $"only {ups.Count} upshift(s)");
        foreach (var (from, to, s) in ups)
        {
            Assert.Equal(from + 1, to);
            Assert.InRange(s, gb.ShiftSeconds - 2 * Dt, gb.ShiftSeconds + 2 * Dt);
        }
        shifts.Clear();
        Drive(new DriveOrder(DriverAction.Braking, 12f), 12f);
        Assert.NotEmpty(shifts);
        foreach (var (from, to, s) in shifts)
        {
            Assert.Equal(from - 1, to);
            Assert.InRange(s, gb.ShiftSeconds * 0.8f - 2 * Dt, gb.ShiftSeconds * 0.8f + 2 * Dt);
        }
    }

    /// <summary>With no throttle given, holding finds whatever throttle keeps the engine at the
    /// target unloaded.</summary>
    [Fact]
    public void HoldingWithoutAThrottleFindsTheOneThatHoldsTheRevs()
    {
        var (engine, dl, driver) = Car();
        var start = new DriveOrder(DriverAction.Idling, 3f);
        for (int i = 0; i < Sr * 3; i++) { driver.Apply(start, i * Dt, Dt); dl.Step(engine, Dt); }
        var hold = new DriveOrder(DriverAction.Holding, 6f, 2500f);
        var late = new List<float>();
        for (int i = 0; i < Sr * 6; i++)
        {
            driver.Apply(hold, i * Dt, Dt);
            dl.Step(engine, Dt);
            if (i > Sr * 4) late.Add(engine.Rpm);
        }
        Assert.InRange(late.Average(), 2500f * 0.9f, 2500f * 1.1f);
    }

    // ── The network driver and the engine ───────────────────────────────────────────────────

    /// <summary>Told the engine is off, it switches it off and leaves it off: it does not go on to
    /// start it again in the same step.</summary>
    [Fact]
    public void TheNetworkDriverSwitchesTheEngineOff()
    {
        var v = VehicleProfile.V8Muscle;
        var engine = new EngineSynth(v.Engine, Sr, 4);
        var dl = new Driveline(v);
        var drv = new VirtualDriver(dl, engine) { TargetSpeed = 10f, Running = false };
        engine.Ignition = true; engine.Starter = true; engine.Throttle = 1f;
        drv.Apply(Dt);
        Assert.False(engine.Ignition);
        Assert.False(engine.Starter);
        Assert.Equal(0f, engine.Throttle);
        Assert.Equal(0, dl.Gear);
        Assert.Equal(1f, dl.Brake);
    }

    /// <summary>Running means turning AND lit: a still engine is not running, and neither is one
    /// still spinning down with the ignition off.</summary>
    [Fact]
    public void RunningNeedsBothSpeedAndIgnition()
    {
        var (engine, dl, driver) = Car();
        Assert.False(engine.Running);                           // cold and still, ignition on
        var start = new DriveOrder(DriverAction.Idling, 3f);
        for (int i = 0; i < Sr * 3; i++) { driver.Apply(start, i * Dt, Dt); dl.Step(engine, Dt); }
        Assert.True(engine.Running);
        engine.Ignition = false;
        Assert.True(engine.Rpm > 200f);
        Assert.False(engine.Running);
        float deg = engine.CrankDegrees;
        Assert.InRange(deg, 0f, engine.Profile.CycleDegrees);
    }

    // ── The valve solver ────────────────────────────────────────────────────────────────────

    private const float Z = 4.0e5f, Area = 8e-4f, TPipe = 900f, Gamma = 1.35f, Mass = 5e-4f, UCap = 0.02f, PMean = 101325f;

    /// <summary>At a moderate pressure difference the answer sits inside the bracket and the pipe's
    /// volume velocity equals the valve's flow over the density at the port.</summary>
    [Fact]
    public void TheValveBalancesThePipeAndTheOrifice()
    {
        float pCyl = PMean + 3000f;
        float b = EngineSynth.SolveValve(0f, 0f, Z, Area, pCyl, 1100f, TPipe, Gamma, Mass, Dt, 0f, UCap, PMean, out float mdot);
        Assert.True(mdot > 0f, "gas did not leave a cylinder above the port pressure");
        float rho = PMean / (288f * TPipe);
        Assert.InRange(b / Z, 0f, UCap);
        Assert.Equal(mdot / rho, b / Z, 2);
    }

    /// <summary>A cylinder far above the pipe drives the flow to the cap and out; one far below it
    /// draws the most the pipe can give, inward. These are the two ends of the bracket, returned
    /// as they are.</summary>
    [Fact]
    public void AtTheEndsOfTheBracketTheFlowIsCapped()
    {
        float bOut = EngineSynth.SolveValve(0f, 0f, Z, 0.01f, 60e5f, 2000f, TPipe, Gamma, 5e-3f, Dt, 0f, UCap, PMean, out float mOut);
        Assert.True(mOut > 0f);
        Assert.InRange(bOut, Z * UCap * 0.99f, Z * UCap * 1.06f);

        float bIn = EngineSynth.SolveValve(0f, 0f, Z, 0.01f, 0.1e5f, 300f, TPipe, Gamma, 5e-3f, Dt, 0f, UCap, PMean, out float mIn);
        Assert.True(mIn < 0f, "a near-empty cylinder did not draw from the pipe");
        Assert.InRange(bIn, -Z * UCap * 1.06f, -Z * UCap * 0.99f);
    }

    // ── The air system ──────────────────────────────────────────────────────────────────────

    /// <summary>Drains the reservoir below cut-in and lets the ports go quiet, so the compressor is
    /// loaded and is the only thing making a sound.</summary>
    private static AirSystem Loaded(bool compressorAtFront)
    {
        var air = new AirSystem(ModelLibrary.Air("transit_bus"), Sr);
        air.PlaceAtFront(_ => false, compressorAtFront);
        air.EngineRpm = 0f;
        for (int k = 0; k < 40 && air.ReservoirKPa > air.Spec.CutInKPa - 20f; k++) air.Vent("kneel");
        foreach (var name in air.Ports.Keys.ToList()) air.Close(name);
        for (int i = 0; i < Sr * 2; i++) air.Step();
        Assert.True(air.Ports.Values.All(p => !p.Venting), "a port was still venting");
        return air;
    }

    /// <summary>
    /// Loaded, the compressor knocks once per pump stroke, rpm / 60 × its order a second, at about its
    /// declared level; with the engine stopped it is silent, and it comes from the front only when the
    /// engine is at the front.
    /// </summary>
    [Fact]
    public void TheCompressorKnocksAtItsStrokeRateOnlyWhileTheEngineTurns()
    {
        var air = Loaded(compressorAtFront: true);
        Assert.True(air.ReservoirKPa <= air.Spec.CutInKPa);

        // Engine stopped: nothing.
        air.EngineRpm = 150f;
        double still = 0;
        for (int i = 0; i < Sr / 2; i++) { float y = air.Step(); still += y * y; }
        Assert.Equal(0.0, still, 12);

        air.EngineRpm = 900f;
        Assert.True(air.CompressorLoaded);
        int n = Sr * 2;
        var y2 = new float[n];
        double front = 0, all = 0;
        for (int i = 0; i < n; i++) { y2[i] = air.Step(); all += y2[i] * y2[i]; front += air.FrontOut * air.FrontOut; }
        Assert.True(all > 0, "the loaded compressor made no sound");
        Assert.Equal(all, front, 6);

        // Knocks: count the envelope's rising crossings of half its peak.
        float peak = y2.Max(MathF.Abs);
        int knocks = 0; bool above = false;
        float env = 0f;
        foreach (float v in y2)
        {
            env = MathF.Max(MathF.Abs(v), env * 0.995f);
            if (!above && env > 0.5f * peak) { knocks++; above = true; }
            else if (above && env < 0.2f * peak) above = false;
        }
        double expected = 900.0 / 60.0 * air.Spec.CompressorOrder * 2.0;
        Assert.InRange(knocks, expected * 0.8, expected * 1.2);

        // About its declared level: the peak pressure within 10 dB of the spec at one metre.
        float specPa = 20e-6f * MathF.Pow(10f, air.Spec.CompressorDb / 20f);
        Assert.InRange(20f * MathF.Log10(peak / specPa), -10f, 10f);

        var rear = Loaded(compressorAtFront: false);
        rear.EngineRpm = 900f;
        double rearFront = 0;
        for (int i = 0; i < Sr / 2; i++) { rear.Step(); rearFront += rear.FrontOut * rear.FrontOut; }
        Assert.Equal(0.0, rearFront, 12);
    }

    /// <summary>A port vented again while it is still venting keeps the higher pressure; closing it
    /// stops it; asking for a port the vehicle does not have does nothing.</summary>
    [Fact]
    public void PortsKeepTheHigherPressureCloseAndIgnoreUnknownNames()
    {
        var air = new AirSystem(ModelLibrary.Air("transit_bus"), Sr);
        air.EngineRpm = 0f;
        var door = air.Ports["door"];
        air.Vent("door");
        Assert.True(door.Venting);
        float first = door.PressureKPa;
        door.Vent(first * 0.3f);
        Assert.Equal(first, door.PressureKPa);

        air.Close("door");
        Assert.False(door.Venting);

        float reservoir = air.ReservoirKPa;
        air.Vent("no_such_port");
        air.Close("no_such_port");
        Assert.Equal(reservoir, air.ReservoirKPa);

        // A service application lets a little into the chambers; the release lets it all go.
        air.Apply();
        float applied = air.Ports["service_release"].PressureKPa;
        Assert.InRange(applied, air.ReservoirKPa * 0.2f, air.ReservoirKPa * 0.35f);
        air.Release();
        Assert.True(air.Ports["service_release"].PressureKPa > applied * 2f);
    }
}

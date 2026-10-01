using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using OpenFPS.Common;
using OpenFPS.Server.Systems;
using Xunit;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// An instrument, not a test: runs the city's traffic for a few minutes of sim time and writes what
/// its tyres were asked for — braking decelerations, the tyre demand the client plays as squeal and,
/// where the vehicle has wheels, each wheel's slip ratio and slip angle. Reads the vehicles by
/// reflection so the same file runs against older builds of the traffic. Set OPENFPS_PROBE_OUT to a
/// directory to have the per-tick log (ticks.csv) and a summary written there; OPENFPS_PROBE_SECONDS
/// sets the length (180 s) and OPENFPS_PROBE_LIFE=0 leaves out the staged street life (hard stops,
/// parking). Without OPENFPS_PROBE_OUT it does nothing.
/// </summary>
public class TrafficBrakingProbe
{
    private readonly ITestOutputHelper _o;
    public TrafficBrakingProbe(ITestOutputHelper o) => _o = o;

    [Fact]
    [Trait("Category", "Probe")]
    public void Probe_city_traffic_braking_and_slip()
    {
        string? outDir = Environment.GetEnvironmentVariable("OPENFPS_PROBE_OUT");
        if (string.IsNullOrEmpty(outDir)) return;
        Directory.CreateDirectory(outDir);
        float seconds = float.TryParse(Environment.GetEnvironmentVariable("OPENFPS_PROBE_SECONDS"), out var s) ? s : 180f;
        bool life = Environment.GetEnvironmentVariable("OPENFPS_PROBE_LIFE") != "0";

        var (world, vehicles) = CarFollowingTests.City(streetLife: life);
        var list = (IList)typeof(VehicleSystem).GetField("_vehicles", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(vehicles)!;
        Type vt = list.Count > 0 ? list[0]!.GetType() : typeof(object);
        FieldInfo F(string n) => vt.GetField(n)!;
        var fSpeed = F("Speed"); var fDemand = F("TyreDemand"); var fRoute = F("Route"); var fName = F("DisplayName");
        var fPreset = F("Preset"); var fBrake = F("Brake"); var fGrip = F("Grip"); var fMap = F("MapId");
        var fWheels = vt.GetField("Wheels");

        const float dt = 1f / 30f;
        var prev = new Dictionary<int, float>();
        using var csv = new StreamWriter(Path.Combine(outDir, "ticks.csv"));
        csv.WriteLine("t,veh,preset,speed,accel,demand,maxSlipRatio,maxSlipAngleDeg,worstWheel,ay,lineAy,steerDeg,offset,yawRate,lineYaw,frontDemand,rearDemand,bendWant,comfortHere,kAhead");
        var fLine = F("Line"); var fLap = F("Lap"); var fDriver = vt.GetField("Driver");
        var brakeHist = new int[8];
        int samples = 0, squeal = 0, slide = 0, onsets = 0, braking = 0, brakingSqueal = 0;
        float maxDecel = 0f;
        var wasSqueal = new Dictionary<int, bool>();
        var wasAudible = new Dictionary<int, bool>();
        int audible = 0, audibleOnsets = 0;
        int wheelSamples = 0, slipOver = 0, angleOver = 0;
        for (int tick = 0; tick < seconds * 30; tick++)
        {
            vehicles.Update("city", world, dt);
            if (tick < 10 * 30) { for (int i = 0; i < list.Count; i++) prev[i] = (float)fSpeed.GetValue(list[i])!; continue; }
            for (int i = 0; i < list.Count; i++)
            {
                object v = list[i]!;
                if ((string)fMap.GetValue(v)! != "city" || fRoute.GetValue(v) == null) continue;
                float speed = (float)fSpeed.GetValue(v)!;
                float demand = (float)fDemand.GetValue(v)!;
                float a = (speed - prev.GetValueOrDefault(i, speed)) / dt;
                prev[i] = speed;
                samples++;
                bool sq = demand >= TyreFriction.SquealOnset;
                if (sq) squeal++;
                if (demand >= TyreFriction.SlideOnset) slide++;
                if (sq && !wasSqueal.GetValueOrDefault(i)) onsets++;
                wasSqueal[i] = sq;
                bool au = TyreFriction.SquealAmount(demand) + TyreFriction.SkidAmount(demand) >= 0.05f;
                if (au) audible++;
                if (au && !wasAudible.GetValueOrDefault(i)) audibleOnsets++;
                wasAudible[i] = au;
                if (a < -0.25f)
                {
                    braking++;
                    if (sq) brakingSqueal++;
                    brakeHist[Math.Min(7, (int)(-a))]++;
                    maxDecel = MathF.Max(maxDecel, -a);
                }
                float msr = 0f, msa = 0f; int worst = -1;
                string extra = "", extra2 = "";
                if (fWheels?.GetValue(v) is WheelDynamics wd)
                {
                    float wd0 = 0f;
                    for (int k = 0; k < wd.Wheels.Length; k++)
                    {
                        var w = wd.Wheels[k];
                        wheelSamples++;
                        if (MathF.Abs(w.SlipRatio) > MathF.Abs(msr)) msr = w.SlipRatio;
                        if (MathF.Abs(w.SlipAngle) > MathF.Abs(msa)) msa = w.SlipAngle;
                        if (w.Demand > wd0) { wd0 = w.Demand; worst = k; }
                        if (MathF.Abs(w.SlipRatio) > 0.05f) slipOver++;
                        if (MathF.Abs(w.SlipAngle) > 4f * MathF.PI / 180f) angleOver++;
                    }
                    var line = (RaceLine)fLine.GetValue(v)!;
                    float lap = (float)fLap.GetValue(v)!;
                    float kl = line.CurvatureAt(lap);
                    float off = fDriver?.GetValue(v) is LineFollower lf ? lf.Offset : 0f;
                    float fd = 0f, rd = 0f;
                    foreach (var w in wd.Wheels) { if (w.Front) fd = MathF.Max(fd, w.Demand); else rd = MathF.Max(rd, w.Demand); }
                    float bend = float.NaN;
                    if (vt.GetField("CornerSpeed")?.GetValue(v) is Func<float, float> cs)
                    {
                        float brk = (float)fBrake.GetValue(v)!;
                        float look = MathF.Max(8f, speed * speed / (2f * brk));
                        bend = line.BendSpeedWithin(lap, 2f * look, (float)vt.GetField("CorneringG")!.GetValue(v)!, brk, cs, DriverSteering.ComfortSideFriction);
                    }
                    float kMax = 0f;
                    for (float d = 0f; d < 20f; d += 0.5f) kMax = MathF.Max(kMax, MathF.Abs(line.CurvatureAt(lap + d)));
                    extra2 = $",{bend:F3},{DriverSteering.ComfortTurnSpeed(kl):F3},{kMax:F4}";
                    extra = $",{wd.Ay:F3},{speed * speed * kl:F3},{wd.SteerAngle * 180f / MathF.PI:F2},{off:F3},{wd.YawRate:F4},{speed * kl:F4},{fd:F3},{rd:F3}";
                }
                csv.WriteLine($"{tick * dt:F3},{i},{fPreset.GetValue(v)},{speed:F3},{a:F3},{demand:F3},{msr:F4},{msa * 180f / MathF.PI:F3},{worst}{extra}{extra2}");
            }
        }
        string[] bins = { "0-1", "1-2", "2-3", "3-4", "4-5", "5-6", "6-7", "7+" };
        var lines = new List<string>
        {
            $"vehicle-ticks on routes: {samples}, braking (a < -0.25 m/s2): {braking}",
            "braking decel histogram (m/s2): " + string.Join("  ", bins.Select((b, k) => $"{b}:{100.0 * brakeHist[k] / Math.Max(1, braking):F1}%")),
            $"max decel {maxDecel:F2} m/s2",
            $"demand >= squeal onset: {100.0 * squeal / Math.Max(1, samples):F2} % of vehicle-ticks; >= slide onset {100.0 * slide / Math.Max(1, samples):F3} %",
            $"squeal onsets: {onsets} in {seconds - 10:F0} s ({onsets / ((seconds - 10) / 60f):F1} a minute over the field); while braking {100.0 * brakingSqueal / Math.Max(1, braking):F1} % of braking ticks squeal",
            $"audible (squeal or skid amount >= 0.05): {100.0 * audible / Math.Max(1, samples):F3} % of vehicle-ticks, {audibleOnsets} onsets",
            $"wheels: |slip ratio| > 0.05 {100.0 * slipOver / Math.Max(1, wheelSamples):F2} %, |slip angle| > 4 deg {100.0 * angleOver / Math.Max(1, wheelSamples):F2} %",
        };
        var presetBrake = new Dictionary<string, (float, float)>();
        foreach (var v in list) presetBrake[(string)fPreset.GetValue(v)!] = ((float)fBrake.GetValue(v)!, (float)fGrip.GetValue(v)!);
        lines.Add("preset brake/grip: " + string.Join(", ", presetBrake.Select(kv => $"{kv.Key} {kv.Value.Item1:F1}/{kv.Value.Item2:F2}")));
        File.WriteAllLines(Path.Combine(outDir, "summary.txt"), lines);
        foreach (var l in lines) _o.WriteLine(l);
    }
}

/// <summary>An instrument: the worst wheel's share of its grip in steady turns, printed for a few of
/// the city's presets. Runs only with OPENFPS_PROBE_OUT set.</summary>
public class SteadyTurnProbe
{
    private readonly ITestOutputHelper _o;
    public SteadyTurnProbe(ITestOutputHelper o) => _o = o;

    [Fact]
    [Trait("Category", "Probe")]
    public void Probe_steady_turn_demand()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OPENFPS_PROBE_OUT"))) return;
        foreach (var (preset, grip) in new[] { ("i4_midsize", 0.85f), ("v6", 0.85f), ("cummins_compound", 0.75f), ("transit_bus", 0.7f), ("mail_truck", 0.75f) })
        {
            var p = MachineRegistry.VehicleFor(preset);
            var wd = new WheelDynamics(p, grip);
            _o.WriteLine($"{preset}: steady lateral limit at onset {wd.SteadyLateralLimit(TyreFriction.SquealOnset) / 9.81f:F2} g, at 1.0 {wd.SteadyLateralLimit(1f) / 9.81f:F2} g");
            foreach (float r in new[] { 8f, 12f, 20f })
            {
                float vOn = wd.SteadyTurnSpeed(1f / r, TyreFriction.SquealOnset);
                string row = $"  R {r} m: onset speed {vOn:F2} m/s ({vOn * vOn / r / 9.81f:F2} g);";
                foreach (float g in new[] { 0.2f, 0.3f, 0.4f, 0.5f })
                {
                    float v = MathF.Sqrt(g * 9.81f * r);
                    row += $" {g:F1}g->{wd.SteadyTurn(v, 1f / r):F2}";
                }
                _o.WriteLine(row);
            }
        }
    }
}

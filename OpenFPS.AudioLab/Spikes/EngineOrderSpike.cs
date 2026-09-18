using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Core.Engine;

namespace OpenFPS.Client.Core.AudioEngine.Fmod;

/// <summary>
/// Measures the engine instead of listening to it and guessing.
///
/// Engine acoustics is done in ORDERS: an order is one event per crankshaft revolution, so order 4
/// at 3000 rpm is 200 Hz. Working in orders makes an engine's identity visible, because the identity
/// is a pattern across orders that stays put while the pitch moves: a four-cylinder is order 2 and
/// its multiples with nothing between; a flat-plane V8 is order 4 and its multiples; a cross-plane V8
/// puts real energy on 0.5, 1.5, 2.5... because each bank fires unevenly and the pattern only repeats
/// every two revolutions. That half-order energy IS the rumble.
///
/// The physical checks that matter most, because they are what the synthesis was built around:
///   * port pulse amplitude — about 0.1 bar at idle, 0.5-1 bar at full load (Blair; US 10823593)
///   * level rises about 30 dB from idle to full load at speed (muffler shootout data)
///   * half orders 5-15 dB below whole orders for a cross-plane V8 with unequal pipes (Selamet 2004)
/// </summary>
public static class EngineOrderSpike
{
    private const int Sr = VehicleSynth.SampleRate;

    /// <summary>Command line: --engine-orders [preset] [rpm=..] [thr=..] [seconds=..] [wav]</summary>
    public static int Run(string[] args)
    {
        string preset = "v8_muscle";
        float thr = -1f;
        var rpms = new List<float>();
        bool wav = args.Contains("wav");
        foreach (var arg in args)
        {
            if (arg.StartsWith("--") || arg == "wav") continue;
            int eq = arg.IndexOf('=');
            if (eq > 0)
            {
                string k = arg[..eq]; string val = arg[(eq + 1)..];
                if (k == "rpm") foreach (var r in val.Split(',')) rpms.Add(float.Parse(r));
                else if (k == "thr") thr = float.Parse(val);
            }
            else if (VehicleProfile.Presets.ContainsKey(arg)) preset = arg;
        }
        var v = Override(VehicleProfile.ByName(preset), args);
        var listener = ListenerFromArgs(args, out string standing);
        if (rpms.Count == 0)
        {
            rpms.Add(v.Engine.IdleRpm);
            rpms.Add(v.Engine.RedlineRpm * 0.35f);
            rpms.Add(v.Engine.RedlineRpm * 0.6f);
            rpms.Add(v.Engine.RedlineRpm * 0.9f);
        }

        Console.WriteLine($"\n  {v.Name} — {v.Engine.Name}");
        var probe = new EngineSynth(v.Engine, Sr);
        foreach (var line in probe.Describe()) Console.WriteLine($"    {line}");
        Console.WriteLine($"    {standing}");
        Console.WriteLine();

        foreach (float rpm in rpms)
        {
            // Idle at whatever throttle holds it; everything else at full throttle unless told.
            float t = thr >= 0f ? thr : (rpm <= v.Engine.IdleRpm * 1.05f ? -1f : 1f);
            Measure(v, rpm, t, wav, listener);
        }

        Console.WriteLine("""

    Reading it
      level        dB SPL at 1 m in the analysed window. Real: 80-90 idling, 105-120 at full load
                   for a loud aftermarket system; a stock car is 15 dB under that.
      port peak    peak pressure in the primary, bar gauge. Real: ~0.1 idling, 0.5-1 at full load.
      half/whole   energy on half-integer orders vs whole ones. A cross-plane V8 wants -5 to -15;
                   an even-firing engine wants far below that.
      rumble/buzz  the same ratio split below and above order 3. Rumble is the beat you want;
                   buzz above order 3 is a sawtooth and you do not.
      lope         once-per-cycle envelope modulation depth. -16 dB is nearly flat; audible is
                   about -6 and a big cam at idle is -4 or louder. chop is cycle-to-cycle change.
      crest        peak over rms of the waveform; a shocked, crackling exhaust runs high (>4).
""");
        return 0;
    }

    /// <summary>Prints the engine's state every quarter second through a scripted run — the first
    /// thing to reach for when a number in the bench makes no sense.</summary>
    public static int Trace(string[] args)
    {
        string preset = "v8_muscle";
        float thr = -1f, rpm = 0f, seconds = 4f, dumpFrom = 0f;
        bool idleOnly = args.Contains("idle");
        foreach (var arg in args)
        {
            if (arg.StartsWith("--")) continue;
            int eq = arg.IndexOf('=');
            if (eq > 0)
            {
                string k = arg[..eq]; float val = float.Parse(arg[(eq + 1)..]);
                if (k == "rpm") rpm = val; else if (k == "thr") thr = val; else if (k == "sec") seconds = val; else if (k == "dumpfrom") dumpFrom = val;
            }
            else if (VehicleProfile.Presets.ContainsKey(arg)) preset = arg;
        }
        var v = Override(VehicleProfile.ByName(preset), args);
        EngineSynth.DebugRigidValves = args.Contains("rigid");
        var engine = new EngineSynth(v.Engine, Sr, 7);
        var dl = new Driveline(v);
        var driver = new Driver(dl, engine);
        foreach (var line in engine.Describe()) Console.WriteLine($"    {line}");
        bool off = args.Contains("off");
        var orders = off ? new List<DriveOrder> { new(DriverAction.Off, seconds) }
                   : new List<DriveOrder> { new(DriverAction.Cranking, 0.6f), new(DriverAction.Idling, idleOnly ? seconds : 2.5f) };
        if (!idleOnly && !off) orders.Add(new(DriverAction.Holding, seconds, rpm > 0 ? rpm : v.Engine.RedlineRpm * 0.5f, thr));
        float dt = 1f / Sr;
        Console.WriteLine("\n      t    rpm   MAP  idle  thr   Nm    q    dil  port  exh dB  in dB  blk dB  state");
        double ex = 0, inn = 0, blk = 0, tq = 0, mapAcc = 0; int cnt = 0; bool nan = false;
        float t = 0f;
        foreach (var order in orders)
        {
            int len = (int)(order.Seconds * Sr);
            float phase = 0f;
            for (int i = 0; i < len; i++, phase += dt, t += dt)
            {
                driver.Apply(order, phase, dt);
                dl.Step(engine, dt);
                if (!float.IsFinite(engine.Exhaust) || !float.IsFinite(engine.Rpm)) nan = true;
                ex += engine.Exhaust * engine.Exhaust; inn += engine.Intake * engine.Intake; blk += engine.Block * engine.Block; cnt++;
                tq += engine.Torque; mapAcc += engine.ManifoldBar;
                if (cnt >= Sr / 4)
                {
                    if (args.Contains("dump") && t > dumpFrom && t < dumpFrom + 1.1f) foreach (var l in engine.DescribeCylinders()) Console.WriteLine("        " + l);
                    Console.WriteLine($"  {t,5:F2}  {engine.Rpm,5:F0}  {mapAcc / cnt,4:F2}  {engine.IdleAir,5:F3}  {engine.Throttle,4:F2}  {tq / cnt,5:F0}  {engine.LastBurnQuality,4:F2}  {engine.LastDilution,4:F2}  {engine.PortPeak / 1e5f,4:F2}   {Db2(ex / cnt),5:F1}  {Db2(inn / cnt),5:F1}  {Db2(blk / cnt),5:F1}  {order.Action}{(nan ? "  NaN!" : "")}");
                    ex = inn = blk = tq = mapAcc = 0; cnt = 0;
                }
            }
        }
        return 0;

        static float Db2(double meanSq) => (float)(10 * Math.Log10(Math.Max(1e-20, meanSq) / (2e-5 * 2e-5)));
    }

    /// <summary>
    /// Is the engine's redline the ENGINE's, or the sample rate's?
    ///
    ///   --engine-alias [preset] [rpm=..] [rates=44100,88200,176400]
    ///
    /// An even-firing V10 can have NO half-order energy — five evenly spaced firings a bank, twice a
    /// cycle, cannot produce a component at half the crank order. So any half-order energy this
    /// measures is the model failing, and how it moves with sample rate says why. If it falls when
    /// the rate rises, the firing events are being under-resolved and the redline in the profile is
    /// a property of the integrator rather than of the engine.
    ///
    /// It builds the engine directly rather than going through VehicleSynth, because the whole point
    /// is to vary the one thing VehicleSynth holds constant.
    /// </summary>
    public static int Alias(string[] args)
    {
        string preset = args.FirstOrDefault(a => VehicleProfile.Presets.ContainsKey(a)) ?? "f1_v10";
        var v = Override(VehicleProfile.ByName(preset), args);
        float rpm = 0f;
        var rates = new List<int> { 44100, 88200, 176400 };
        foreach (var a in args)
        {
            int eq = a.IndexOf('=');
            if (eq <= 0) continue;
            if (a[..eq] == "rpm") rpm = float.Parse(a[(eq + 1)..]);
            else if (a[..eq] == "rates") { rates.Clear(); foreach (var r in a[(eq + 1)..].Split(',')) rates.Add(int.Parse(r)); }
        }
        if (rpm <= 0f) rpm = v.Engine.RedlineRpm;

        Console.WriteLine($"\n  {v.Engine.Name} held at {rpm:F0} rpm, {v.Engine.Cylinders} cylinders even-firing.");
        Console.WriteLine($"  Firing {rpm / 60f * v.Engine.Cylinders / 2f:F0} Hz.\n");
        Console.WriteLine("    rate      samples/firing   half/whole   structure");

        foreach (int rate in rates)
        {
            var engine = new EngineSynth(v.Engine, rate, 7);
            var dl = new Driveline(v);
            var driver = new Driver(dl, engine);
            float dt = 1f / rate;
            var orders = new List<DriveOrder>
            {
                new(DriverAction.Cranking, 0.5f), new(DriverAction.Idling, 1.5f),
                new(DriverAction.Holding, 12f, rpm, 1f),
            };
            int total = 0;
            foreach (var o in orders) total += (int)(o.Seconds * rate);
            var buf = new float[total];
            int at = 0;
            float achieved = 0f; int rc = 0;
            foreach (var order in orders)
            {
                int len = (int)(order.Seconds * rate);
                float phase = 0f;
                for (int i = 0; i < len; i++, phase += dt)
                {
                    driver.Apply(order, phase, dt);
                    dl.Step(engine, dt);
                    buf[at++] = engine.Exhaust;
                    if (at > total - rate * 2) { achieved += engine.Rpm; rc++; }
                }
            }
            int from = total - rate * 2;
            var x = new float[total - from];
            Array.Copy(buf, from, x, 0, x.Length);
            float f1 = achieved / Math.Max(1, rc) / 60f;

            // THE SAME BAND AT EVERY RATE. Measured up to Nyquist, a 176.4 kHz render was being
            // judged on energy out to 80 kHz that no listener and no loudspeaker will ever meet, and
            // a shock front one sample thick puts plenty there; that is how "worse when oversampled"
            // was read off a bench that was not comparing like with like. Twenty kilohertz is the
            // ceiling for all of them.
            const float audible = 20000f;
            double half = 0, whole = 0, harm = 0, floor = 0;
            for (float o = 0.5f; o * f1 < MathF.Min(audible, rate * 0.45f) && o <= 200f; o += 0.5f)
            {
                float m = G(x, o * f1, rate);
                bool isWhole = MathF.Abs(o - MathF.Round(o)) < 0.01f;
                if (o >= 1f) { if (isWhole) whole += m * m; else half += m * m; }
                harm += m * m;
                float g = G(x, (o + 0.25f) * f1, rate);
                floor += g * g;
            }
            float hw = (float)(10 * Math.Log10(Math.Max(1e-20, half) / Math.Max(1e-20, whole)));
            float hnr = (float)(10 * Math.Log10(Math.Max(1e-20, harm) / Math.Max(1e-20, floor)));
            float perFiring = rate / (f1 * v.Engine.Cylinders / 2f);
            Console.WriteLine($"  {rate,7} Hz {perFiring,12:F1}   {hw,10:F1} dB {hnr,10:F1} dB");
        }
        Console.WriteLine("""

    An even-firing engine wants half/whole as far below zero as it can get. If that number climbs
    toward zero at 44.1 kHz and falls again at 88.2, the redline is the integrator's and not the
    engine's, and the fix is to oversample the engine rather than to lower the limiter.
""");
        return 0;
    }

    private static float G(float[] x, float freq, int rate)
    {
        double w = 2 * Math.PI * freq / rate;
        double sr = 0, si = 0, norm = 0;
        for (int i = 0; i < x.Length; i++)
        {
            double win = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (x.Length - 1));
            double a = w * i;
            sr += x[i] * win * Math.Cos(a);
            si -= x[i] * win * Math.Sin(a);
            norm += win;
        }
        return (float)(2 * Math.Sqrt(sr * sr + si * si) / Math.Max(1, norm));
    }

    /// <summary>key=value overrides so the model can be swept without a rebuild: steep= wall= torque=
    /// idlemap= rough= jet= port= flowloss= hdr= sprd= induction= ilevel= absorb= valves= knock=
    /// turbo= whine= bore= stroke= rod= cr= evo= exdur= indur= excl= incl= exvalve= invalve= redline=
    /// idlerpm= inertia= plenum= airbox= snorkel= throttle= colpipe= midpipe= tail= coldia= prdia=
    /// taildia=, plus muffler=none|glass|chambered|stock and crank=even|odd</summary>
    /// <summary>
    /// Where the bench stands: `azimuth=<deg>` round from dead behind the car (positive toward the
    /// machine's +x side), `dist=<m>` from the exhaust (default 10), ear 1.2 m above the tailpipe.
    /// Null — no azimuth given — is the old bench exactly: every tailpipe summed at one point, which
    /// is what a listener on the centre line hears. `pipe=<n>` solos one tailpipe for A/B by ear.
    /// </summary>
    public static Vector3? ListenerFromArgs(string[] args, out string describe)
    {
        float? az = null; float dist = 10f;
        EngineSynth.DebugSoloTailpipe = -1;
        foreach (var arg in args)
        {
            int eq = arg.IndexOf('=');
            if (eq <= 0 || arg.StartsWith("--")) continue;
            string k = arg[..eq];
            if (!float.TryParse(arg[(eq + 1)..], out float n)) continue;
            if (k == "azimuth") az = n;
            else if (k == "dist") dist = MathF.Max(0.5f, n);
            else if (k == "pipe") EngineSynth.DebugSoloTailpipe = (int)n;
        }
        string solo = EngineSynth.DebugSoloTailpipe >= 0 ? $", tailpipe {EngineSynth.DebugSoloTailpipe} alone" : "";
        if (az is not { } deg)
        {
            describe = "listener: on the centre line, every tailpipe summed at one point" + solo;
            return null;
        }
        float rad = deg * MathF.PI / 180f;
        var p = new Vector3(dist * MathF.Sin(rad), 1.2f, -dist * MathF.Cos(rad));
        describe = $"listener: {dist:F0} m from the exhaust, {deg:F0} deg round from dead behind{solo}";
        return p;
    }

    public static VehicleProfile Override(VehicleProfile v, string[] args)
    {
        var e = v.Engine;
        var x = e.Exhaust;
        var n_ = e.Intake;
        var m_ = e.Mechanical;
        foreach (var arg in args)
        {
            int eq = arg.IndexOf('=');
            if (eq <= 0 || arg.StartsWith("--")) continue;
            string k = arg[..eq];
            if (k == "crank")
            {
                // Even-fire or shared-pin, on a ten. A 90-degree V10 whose crankpins carry two rods
                // with no offset fires 90 then 54 degrees apart for ever: the pair on a pin goes off
                // a bank-angle apart, then waits for the next pin. That unevenness is the whole of
                // where half-order energy — the rumble — comes from, and an even-fire ten has none
                // of it by construction.
                if (e.Cylinders == 10)
                    e = e with { FiringAngles = arg[(eq + 1)..] == "odd"
                        ? EngineProfile.IntervalFire(new[] { 1, 6, 2, 7, 3, 8, 4, 9, 5, 10 },
                                                     new[] { 90f, 54f, 90f, 54f, 90f, 54f, 90f, 54f, 90f, 54f })
                        : EngineProfile.EvenFire(new[] { 1, 6, 5, 10, 2, 7, 3, 8, 4, 9 }) };
                Console.WriteLine($"  override crank = {arg[(eq + 1)..]}");
                continue;
            }
            if (k == "muffler")
            {
                // The one override that is not a number: which can, if any, is on the end.
                x = x with { Muffler = arg[(eq + 1)..] switch
                {
                    "none" or "straight" => MufflerSpec.StraightPipe,
                    "glass" => MufflerSpec.Glasspack,
                    "chambered" => MufflerSpec.Chambered40,
                    "stock" => MufflerSpec.Stock,
                    _ => x.Muffler,
                } };
                Console.WriteLine($"  override muffler = {arg[(eq + 1)..]}");
                continue;
            }
            if (!float.TryParse(arg[(eq + 1)..], out float n)) continue;
            switch (k)
            {
                case "steep": x = x with { Steepening = n }; break;
                case "wall": x = x with { WallLossMultiplier = n }; break;
                case "jet": x = x with { JetNoiseLevel = n }; break;
                case "flowloss": x = x with { FlowLoss = n }; break;
                case "hdr": x = x with { PrimaryLengthMetres = n, PrimaryLengthsMetres = null }; break;
                case "sprd": x = x with { PrimarySpread = n, PrimaryLengthsMetres = null }; break;
                case "torque": e = e with { PeakTorqueNm = n }; break;
                case "idlemap": e = e with { IdleMapBar = n }; break;
                case "rough": e = e with { IdleRoughness = n }; break;
                case "gov": e = e with { IdleGovernorGain = n }; break;
                // Every part of the engine a bisection has to be able to move one at a time.
                case "bore": e = e with { BoreMm = n }; break;
                case "stroke": e = e with { StrokeMm = n }; break;
                case "rod": e = e with { RodRatio = n }; break;
                case "cr": e = e with { CompressionRatio = n }; break;
                case "evo": e = e with { EvoTemperatureK = n }; break;
                case "exdur": e = e with { ExhaustCam = e.ExhaustCam with { DurationDegrees = n } }; break;
                case "indur": e = e with { IntakeCam = e.IntakeCam with { DurationDegrees = n } }; break;
                case "excl": e = e with { ExhaustCam = e.ExhaustCam with { CentrelineDegrees = n } }; break;
                case "incl": e = e with { IntakeCam = e.IntakeCam with { CentrelineDegrees = n } }; break;
                case "exlift": e = e with { ExhaustCam = e.ExhaustCam with { MaxLiftMm = n } }; break;
                case "inlift": e = e with { IntakeCam = e.IntakeCam with { MaxLiftMm = n } }; break;
                case "exramp": e = e with { ExhaustCam = e.ExhaustCam with { RampFraction = n } }; break;
                case "inramp": e = e with { IntakeCam = e.IntakeCam with { RampFraction = n } }; break;
                case "exvalve": e = e with { ExhaustValve = e.ExhaustValve with { DiameterMm = n } }; break;
                case "invalve": e = e with { IntakeValve = e.IntakeValve with { DiameterMm = n } }; break;
                case "excd": e = e with { ExhaustValve = e.ExhaustValve with { DischargeCoefficient = n } }; break;
                case "friction": e = e with { FrictionNm = n }; break;
                case "prdia": x = x with { PrimaryDiameterMm = n }; break;
                case "taildia": x = x with { TailpipeDiameterMm = n }; break;
                case "port": x = x with { PortNoiseLevel = n }; break;
                case "colpipe": x = x with { CollectorPipeMetres = n }; break;
                case "midpipe": x = x with { MidPipeMetres = n }; break;
                case "tail": x = x with { TailpipeMetres = new[] { n, n * 1.12f } }; break;
                case "coldia": x = x with { CollectorDiameterMm = n }; break;
                case "redline": e = e with { RedlineRpm = n }; break;
                case "idlerpm": e = e with { IdleRpm = n }; break;
                case "inertia": e = e with { InertiaKgM2 = n }; break;
                case "induction": n_ = n_ with { FlowNoiseLevel = n }; break;
                case "plenum": n_ = n_ with { PlenumLitres = n }; break;
                case "airbox": n_ = n_ with { AirboxLitres = n }; break;
                case "snorkel": n_ = n_ with { SnorkelLengthMetres = n }; break;
                case "throttle": n_ = n_ with { ThrottleDiameterMm = n }; break;
                case "ilevel": n_ = n_ with { Level = n }; break;
                case "absorb": n_ = n_ with { Absorption = n }; break;
                case "valves": m_ = m_ with { ValvetrainLevel = n }; break;
                case "knock": m_ = m_ with { CombustionKnock = n }; break;
                case "turbo": m_ = m_ with { TurboWhistleLevel = n }; break;
                case "whine": m_ = m_ with { AccessoryWhineLevel = n }; break;
                default: continue;
            }
            Console.WriteLine($"  override {k} = {n}");
        }
        return v with { Engine = e with { Exhaust = x, Intake = n_, Mechanical = m_ } };
    }

    /// <summary>Renders every preset at idle and at speed to WAV so the family can be auditioned.</summary>
    public static int Gallery(string[] args)
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "ASSETS", "SOUNDS", "VEHICLES", "GALLERY");
        Directory.CreateDirectory(dir);
        Console.WriteLine($"\n  Every engine: cranks, idles, blips twice, and is shut off.\n");
        string? only = args.FirstOrDefault(a => VehicleProfile.Presets.ContainsKey(a));
        var listener = ListenerFromArgs(args, out string standing);
        Console.WriteLine($"  {standing}\n");
        foreach (var (key, make) in VehicleProfile.Presets)
        {
            if (only != null && key != only) continue;
            var v = Override(make(), args);
            var e = v.Engine;
            var orders = new List<DriveOrder>
            {
                new(DriverAction.Cranking, 0.8f),
                new(DriverAction.Idling, 4.0f),
                new(DriverAction.Revving, 1.8f, e.RedlineRpm * 0.55f),
                new(DriverAction.Idling, 1.2f),
                new(DriverAction.Revving, 2.4f, e.RedlineRpm * 0.95f),
                new(DriverAction.Idling, 1.8f),
                new(DriverAction.ShuttingDown, 1.5f),
            };
            var r = VehicleSynth.Render(v, orders, seed: 5, listener: listener);
            File.WriteAllBytes(Path.Combine(dir, $"{key}_exhaust.wav"), VehicleSynth.ToWav16(r.Exhaust));
            File.WriteAllBytes(Path.Combine(dir, $"{key}_front.wav"), VehicleSynth.ToWav16(r.Intake));
            // A near mix: exhaust with the front of the car 8 dB under it.
            var mix = new float[r.Exhaust.Length];
            for (int i = 0; i < mix.Length; i++) mix[i] = r.Exhaust[i] + r.Intake[i] * 0.4f;
            float peak = 0f; int bad = 0;
            foreach (var s in mix) { if (!float.IsFinite(s)) bad++; else peak = MathF.Max(peak, MathF.Abs(s)); }
            if (bad > 0) Console.WriteLine($"    !! {bad} non-finite samples in {key}");
            if (peak > 1e-6f) for (int i = 0; i < mix.Length; i++) mix[i] = float.IsFinite(mix[i]) ? mix[i] * 0.95f / peak : 0f;
            float after = 0f; foreach (var s in mix) after = MathF.Max(after, MathF.Abs(s));
            int ip = 0; for (int i = 0; i < r.Intake.Length; i++) if (MathF.Abs(r.Intake[i]) > MathF.Abs(r.Intake[ip])) ip = i;
            int ep = 0; for (int i = 0; i < r.Exhaust.Length; i++) if (MathF.Abs(r.Exhaust[i]) > MathF.Abs(r.Exhaust[ep])) ep = i;
            Console.WriteLine($"    exhaust peak {r.Exhaust[ep]:F3} at {ep / (float)Sr:F2}s, front peak {r.Intake[ip]:F3} at {ip / (float)Sr:F2}s");
            File.WriteAllBytes(Path.Combine(dir, $"{key}.wav"), VehicleSynth.ToWav16(mix));
            Console.WriteLine($"    {key,-13} {e.Name,-42} exhaust {r.ExhaustDb,3:F0} dB  front {r.IntakeDb,3:F0} dB");
            foreach (var line in r.Log)
                if (line.Contains("Idling") || line.Contains("reached")) Console.WriteLine($"                  {line.Trim()}");
        }
        Console.WriteLine($"\n  wrote {dir}");
        return 0;
    }

    private static void Measure(VehicleProfile v, float rpm, float throttle, bool wav, Vector3? listener = null)
    {
        // Run for long enough for the governor and THE PIPES to settle, analyse the last two seconds.
        //
        // The pipes are the slow half and five seconds was not enough for them. An exhaust primary is
        // a quarter-wave resonator whose note is set by the SPEED OF SOUND IN THE GAS, and the gas
        // takes several seconds to go from its idle temperature to its full-load one. So a pipe that
        // rings with the firing rate when the engine picks up walks away from it as it heats: the
        // V10 measured 136.8 dB at 3,300 rpm, held it for three seconds, and then fell to 121.6 and
        // stayed there — same rpm, same torque, same manifold pressure, a pipe that had detuned.
        //
        // Analysed at five seconds that engine reads twenty decibels louder than it is, and the two
        // seconds written to a WAV are a fade rather than a steady state, which is exactly what a
        // listener reported. Twelve seconds costs render time and buys a number that is true.
        bool idle = rpm <= v.Engine.IdleRpm * 1.05f && throttle < 0f;
        var orders = idle
            ? new List<DriveOrder> { new(DriverAction.Cranking, 0.5f), new(DriverAction.Idling, 13.5f) }
            : new List<DriveOrder>
            {
                new(DriverAction.Cranking, 0.5f),
                new(DriverAction.Idling, 1.5f),
                new(DriverAction.Holding, 12f, rpm, throttle < 0f ? 1f : throttle),
            };
        var r = VehicleSynth.Render(v, orders, seed: 7, listener: listener);
        int from = r.Exhaust.Length - Sr * 2;
        var x = Window(r.Exhaust, from, r.Exhaust.Length);
        var pa = new float[x.Length];
        for (int i = 0; i < x.Length; i++) pa[i] = x[i] * r.PascalsPerUnit;
        float achieved = 0f;
        for (int i = from; i < r.Rpm.Length; i++) achieved += r.Rpm[i];
        achieved /= Math.Max(1, r.Rpm.Length - from);
        float f1 = achieved / 60f;

        var mag = new Dictionary<float, float>();
        for (float o = 0.5f; o <= 16f; o += 0.5f)
        {
            float f = o * f1;
            if (f > Sr * 0.45f) break;
            mag[o] = Goertzel(x, f);
        }
        float lowHalf = 0f, lowWhole = 0f, hiHalf = 0f, hiWhole = 0f;
        foreach (var (o, m) in mag)
        {
            if (o < 1f) continue;
            bool whole = MathF.Abs(o - MathF.Round(o)) < 0.01f;
            if (o <= 3f) { if (whole) lowWhole += m * m; else lowHalf += m * m; }
            else { if (whole) hiWhole += m * m; else hiHalf += m * m; }
        }
        float halfP = lowHalf + hiHalf, wholeP = lowWhole + hiWhole;

        // STRUCTURE AGAINST FLOOR, which is the difference between an engine and a hiss.
        //
        // Every band measure here is blind to it: a cross-plane V8 puts most of its energy below
        // 80 Hz because its half order IS 40 Hz, and an engine whose broadband floor has risen to
        // swamp everything puts energy there too. Those read identically by band and could not sound
        // less alike. So this samples the same spectrum ON the orders and BETWEEN them — a quarter of
        // an order off, where nothing periodic can live — and reports the ratio. A clean engine runs
        // 40 dB and up; the thing a listener calls white noise through a pipe was at 25.
        double harm = 0, floor = 0;
        for (float o = 0.5f; o * f1 < Sr * 0.45f && o <= 200f; o += 0.5f)
        {
            float m = Goertzel(x, o * f1);
            harm += m * m;
            float g = Goertzel(x, (o + 0.25f) * f1);
            floor += g * g;
        }
        float hnr = (float)(10 * Math.Log10(Math.Max(1e-20, harm) / Math.Max(1e-20, floor)));

        var (lopeDb, swing) = Lump(x, achieved);
        double rms = 0; float peak = 0f;
        foreach (var s in pa) { rms += s * s; peak = MathF.Max(peak, MathF.Abs(s)); }
        rms = Math.Sqrt(rms / pa.Length);
        float db = (float)(20 * Math.Log10(Math.Max(1e-9, rms) / 2e-5));
        string holding = r.Log.Last(l => l.Contains("Holding") || l.Contains("Idling"));
        string portPeak = holding.Contains("port peak") ? holding[holding.IndexOf("port peak")..].Split(',')[0] : "";
        string map = holding.Contains("MAP") ? holding[holding.IndexOf("MAP")..].Split(',')[0] : "";

        Console.WriteLine($"  ── asked {rpm:F0} rpm, got {achieved:F0} — order 1 = {f1:F1} Hz, firing = {f1 * v.Engine.Cylinders / (v.Engine.Strokes == 2 ? 1f : 2f):F0} Hz   {map}  {portPeak}");
        Console.WriteLine($"     level {db,5:F1} dB SPL   crest {peak / MathF.Max(1e-6f, (float)rms),4:F1}   centroid {Centroid(x),5:F0} Hz   "
                        + $"half/whole {Db(halfP / MathF.Max(1e-12f, wholeP)) * 0.5f,6:F1} dB   structure {hnr,5:F1} dB");
        Console.WriteLine($"     rumble {Db(lowHalf / MathF.Max(1e-12f, lowWhole)) * 0.5f,6:F1} dB   buzz {Db(hiHalf / MathF.Max(1e-12f, hiWhole)) * 0.5f,6:F1} dB   "
                        + $"lope {lopeDb,6:F1} dB  swing {swing * 100,5:F1} %   chop {Chop(x, achieved) * 100,5:F1} %   blop {Blop(x) * 100,5:F1} %");
        Console.WriteLine($"     bands  <80Hz {Band(x, 20f, 80f),5:F1}   80-250 {Band(x, 80f, 250f),5:F1}"
                        + $"   250-800 {Band(x, 250f, 800f),5:F1}   800-3k {Band(x, 800f, 3000f),5:F1}   >3k {Band(x, 3000f, 12000f),5:F1}  dB");
        float top = 0f;
        foreach (var (_, m) in mag) top = MathF.Max(top, m);
        var line = new System.Text.StringBuilder("     ");
        foreach (var (o, m) in mag)
        {
            if (o > 8f) break;
            line.Append($"{o:0.#}:{Db(m / MathF.Max(1e-12f, top)),5:F0}  ");
        }
        Console.WriteLine(line.ToString());
        Console.WriteLine($"     {Bars(mag, top)}\n");

        if (wav)
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "ASSETS", "SOUNDS", "VEHICLES");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"bench_{v.EngineKey}_{rpm:F0}.wav");
            File.WriteAllBytes(path, VehicleSynth.ToWav16(x));
            Console.WriteLine($"     wrote {path}\n");
        }
    }

    private static (float LopeDb, float Swing) Lump(float[] x, float rpm)
    {
        var env = new float[x.Length];
        float a = 1f - MathF.Exp(-2f * MathF.PI * 30f / Sr);
        float e = 0f;
        for (int i = 0; i < x.Length; i++) { e += a * (MathF.Abs(x[i]) - e); env[i] = e; }
        float mean = 0f;
        foreach (var v in env) mean += v;
        mean /= env.Length;
        if (mean < 1e-9f) return (-99f, 0f);
        float var2 = 0f;
        foreach (var v in env) var2 += (v - mean) * (v - mean);
        float swing = MathF.Sqrt(var2 / env.Length) / mean;
        float lope = Goertzel(env, rpm / 120f) / mean;
        return (20f * MathF.Log10(MathF.Max(1e-6f, lope)), swing);
    }

    private static string Bars(Dictionary<float, float> mag, float peak)
    {
        const string Ramp = " .:-=+*#%@";
        var sb = new System.Text.StringBuilder();
        foreach (var (o, m) in mag)
        {
            if (o > 8f) break;
            float db = Db(m / MathF.Max(1e-12f, peak));
            int level = (int)Math.Clamp((db + 45f) / 45f * (Ramp.Length - 1), 0, Ramp.Length - 1);
            sb.Append(Ramp[level]);
            sb.Append(MathF.Abs(o - MathF.Round(o)) < 0.01f ? '|' : ' ');
        }
        return sb.ToString();
    }

    private static float Goertzel(float[] x, float freq)
    {
        double w = 2 * Math.PI * freq / Sr;
        double sr = 0, si = 0, norm = 0;
        for (int i = 0; i < x.Length; i++)
        {
            double win = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (x.Length - 1));
            double a = w * i;
            sr += x[i] * win * Math.Cos(a);
            si -= x[i] * win * Math.Sin(a);
            norm += win;
        }
        return (float)(2 * Math.Sqrt(sr * sr + si * si) / Math.Max(1, norm));
    }

    private static float Chop(float[] x, float rpm)
    {
        int cycle = (int)(Sr * 120f / MathF.Max(200f, rpm));
        int cycles = x.Length / Math.Max(1, cycle);
        if (cycles < 3) return 0f;
        var energy = new float[cycles];
        for (int c = 0; c < cycles; c++)
        {
            float e = 0f;
            for (int i = c * cycle; i < (c + 1) * cycle; i++) e += x[i] * x[i];
            energy[c] = MathF.Sqrt(e / cycle);
        }
        float mean = 0f;
        foreach (var e in energy) mean += e;
        mean /= cycles;
        if (mean < 1e-9f) return 0f;
        float diff = 0f;
        for (int c = 1; c < cycles; c++) diff += MathF.Abs(energy[c] - energy[c - 1]);
        return diff / (cycles - 1) / mean;
    }

    private static float Blop(float[] x)
    {
        var env = new float[x.Length];
        float a = 1f - MathF.Exp(-2f * MathF.PI * 40f / Sr);
        float e = 0f;
        for (int i = 0; i < x.Length; i++) { e += a * (MathF.Abs(x[i]) - e); env[i] = e; }
        float mean = 0f;
        foreach (var v in env) mean += v;
        mean /= env.Length;
        if (mean < 1e-9f) return 0f;
        float band = 0f;
        for (float f = 2f; f <= 10f; f += 0.5f) { float m = Goertzel(env, f); band += m * m; }
        return MathF.Sqrt(band) / mean;
    }

    private static float Band(float[] x, float lo, float hi)
    {
        float band = 0f, all = 0f;
        for (float f = 25f; f < Sr * 0.4f; f *= 1.1225f)
        {
            float m = Goertzel(x, f);
            all += m * m;
            if (f >= lo && f < hi) band += m * m;
        }
        return Db(band / MathF.Max(1e-12f, all));
    }

    private static float Centroid(float[] x)
    {
        float num = 0f, den = 0f;
        for (float f = 31.5f; f < Sr * 0.4f; f *= 1.2599f)
        {
            float m = Goertzel(x, f);
            num += f * m * m;
            den += m * m;
        }
        return den > 1e-12f ? num / den : 0f;
    }

    private static float[] Window(float[] x, int from, int to)
    {
        from = Math.Max(0, from); to = Math.Min(x.Length, to);
        var y = new float[Math.Max(1, to - from)];
        Array.Copy(x, from, y, 0, y.Length);
        return y;
    }

    private static float Db(float power) => 10f * MathF.Log10(MathF.Max(1e-12f, power));
}

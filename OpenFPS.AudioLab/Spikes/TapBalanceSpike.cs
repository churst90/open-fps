using System.Collections.Generic;
using System;
using System.Linq;
using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Core.Engine;

namespace OpenFPS.Client.AudioEngine.Fmod;

/// <summary>
/// How loud each end of a machine is: the rear voice (tailpipe, body, rear tyres, brake air) against
/// the front voice (intake, engine bay, fan, front tyres, door), idling and cruising, for every preset.
/// Reported as dB at one metre from each outlet, and the rear minus the front.
///
///   --tap-balance [preset ...]
/// </summary>
public static class TapBalanceSpike
{
    const int Rate = 44100, Block = 1024;

    public static int Run(string[] args)
    {
        var names = args.Where(a => !a.StartsWith("--") && a != "parts").ToArray();
        if (names.Length == 0) names = VehicleProfile.Presets.Keys.OrderBy(k => k).ToArray();
        if (args.Contains("parts")) return Parts(names);
        if (args.FirstOrDefault(a => a.StartsWith("seed=")) is { } sd)
        {
            int seed = int.Parse(sd[5..]);
            foreach (var n in names.Where(n => n != "squeal" && !n.StartsWith("seed=")))
            {
                var q = new EngineVoiceState(VehicleProfile.ByName(n), Rate, seed).Squeal;
                Console.WriteLine($"{n} seed {seed}: {(q.Squeals ? $"squeals at {q.Hz:F0} Hz" : "does not squeal")}");
            }
            return 0;
        }
        if (args.Contains("front")) return Front(names.Where(n => n != "front").ToArray());
        if (args.Contains("cost")) return Cost(names.Where(n => n != "cost").ToArray());
        if (args.Contains("knock")) return Knock(names.Where(n => n != "knock").ToArray());
        if (args.Contains("pipe")) return Pipe(names.Where(n => n != "pipe").ToArray());
        if (args.Contains("turbo")) return Turbo(names.Where(n => n != "turbo").ToArray());
        if (args.Contains("frontparts")) return FrontParts(names.Where(n => n != "frontparts").ToArray());
        if (args.Contains("nan")) return NanHunt(names.Where(n => n != "nan").ToArray());
        if (args.Contains("audit")) return Audit(names.Where(n => n != "audit").ToArray());
        if (args.Contains("placed")) return Placed();
        if (args.Contains("port")) return Port(names.Where(n => n != "port").ToArray());
        if (args.Contains("shifts")) return Shifts(names.Where(n => n != "shifts").ToArray());
        if (args.Contains("squeal")) return Squeal(names.Where(n => n != "squeal").ToArray());
        if (args.Contains("whoosh")) return Whoosh(names.Where(n => n != "whoosh").ToArray());
        Console.WriteLine($"{"preset",-22} {"idle rear",9} {"front",7} {"r-f",6}   {"12 m/s rear",11} {"front",7} {"r-f",6}");
        foreach (var n in names)
        {
            var v = VehicleProfile.ByName(n);
            var (ri, fi) = Measure(v, 0f);
            var (rc, fc) = Measure(v, 12f);
            Console.WriteLine($"{n,-22} {ri,9:F1} {fi,7:F1} {ri - fi,6:F1}   {rc,11:F1} {fc,7:F1} {rc - fc,6:F1}");
        }
        return 0;
    }

    /// <summary>At a city cruise (12 m/s): each end's level, and what the tyres, the fan and the
    /// engine bay are each worth to it (the level lost when they are muted).</summary>
    /// <summary>
    /// The front of each vehicle against its back, at idle and at a cruise, and what the intake and
    /// the engine bay are each worth to the front: "I can really hear the air intake ... the intake
    /// I don't think should be audible".
    /// </summary>
    static int Front(string[] names)
    {
        Console.WriteLine($"{"preset",-22} {"",7} {"rear",6} {"front",6} {"front-rear",10}  intake worth (all paths)  bay worth");
        foreach (var n in names)
        {
            var v = VehicleProfile.ByName(n);
            var quiet = v with { Engine = v.Engine with { Intake = v.Engine.Intake with { Level = 0f } } };
            foreach (var (label, sp, from) in new[] { ("idle", 0f, -1f), ("cruise", 12f, -1f), ("floored", 30f, 3f) })
            {
                var (r, f) = Measure(v, sp, null, from);
                var (ri, fi) = Measure(quiet, sp, null, from);
                var (_, fb) = Measure(v, sp, s => s.BayLeakage = 0f, from);
                Console.WriteLine($"{n,-22} {label,7} {r,6:F1} {f,6:F1} {f - r,10:F1}  front {f - fi,5:F1} rear {r - ri,5:F1}  {f - fb,9:F1}");
            }
        }
        return 0;
    }

    static int Parts(string[] names)
    {
        Console.WriteLine($"{"preset",-20} {"rear",6} {"front",6}   worth to rear: {"tyres",5}   to front: {"tyres",5} {"fan",5} {"bay",5}");
        foreach (var n in names)
        {
            var v = VehicleProfile.ByName(n);
            var (r, f) = Measure(v, 12f);
            var (rT, fT) = Measure(v, 12f, s => s.TyreMix = 0f);
            var (_, fF) = Measure(v, 12f, s => s.FanMix = 0f);
            var (_, fB) = Measure(v, 12f, s => s.BayLeakage = 0f);
            Console.WriteLine($"{n,-20} {r,6:F1} {f,6:F1}   {"",14}{r - rT,5:F1}   {"",10}{f - fT,5:F1} {f - fF,5:F1} {f - fB,5:F1}");
        }
        return 0;
    }

    /// <summary>
    /// What carries: the whole machine (both ends summed, as it is heard from a distance) at a city
    /// cruise, in three bands, and what the exhaust's turbulent jet and the cooling fan are each worth
    /// in each band. Energy at one metre is ruled by the bass; what is heard blocks away is the middle.
    /// </summary>
    static int Whoosh(string[] names)
    {
        var mutes = new (string Name, Action<EngineVoiceState> Mute)[]
        {
            ("tyres", s => s.TyreMix = 0f), ("fan", s => s.FanMix = 0f), ("bay", s => s.BayLeakage = 0f),
            ("intake", s => s.FrontMix = 0f), ("body", s => s.BodyMix = 0f),
        };
        Console.WriteLine($"{"preset",-18} {"low",5} {"mid",5} {"high",5}   worth in the mid band (and high): " +
                          string.Join(" ", mutes.Select(m => $"{m.Name,11}")) + $" {"jet",11} {"air",11} {"steepening",11}");
        foreach (var n in names)
        {
            var v = VehicleProfile.ByName(n);
            var b = BandsOf(v, null, "base");
            var cols = mutes.Select(m => { var x = BandsOf(v, m.Mute, "no" + m.Name); return $"{b.M - x.M,5:F1}({b.H - x.H,4:F1})"; }).ToList();
            var noJet = v with { Engine = v.Engine with { Exhaust = v.Engine.Exhaust with { JetNoiseLevel = 0f } } };
            var j = BandsOf(noJet, null);
            cols.Add($"{b.M - j.M,5:F1}({b.H - j.H,4:F1})");
            var noAir = BandsOf(v with { AirSystem = null }, null, "noair");
            cols.Add($"{b.M - noAir.M,5:F1}({b.H - noAir.H,4:F1})");
            Action<EngineVoiceState> bare = s => { s.TyreMix = 0f; s.FanMix = 0f; s.BayLeakage = 0f; s.FrontMix = 0f; s.BodyMix = 0f; };
            BandsOf(v with { AirSystem = null }, bare, "pipeonly");
            BandsOf(v with { AirSystem = null, Engine = v.Engine with { CombustionVariation = 0f } }, bare, "pipesteady");
            Action<EngineVoiceState> noFan = s => s.FanMix = 0f;
            var m = v.Engine.Mechanical;
            BandsOf(v with { Engine = v.Engine with { Mechanical = m with { ValvetrainLevel = 0f } } }, noFan, "nofan.novalves");
            BandsOf(v with { Engine = v.Engine with { Mechanical = m with { TurboWhistleLevel = 0f } } }, noFan, "nofan.noturbo");
            BandsOf(v with { Engine = v.Engine with { Mechanical = m with { AccessoryWhineLevel = 0f, BlowerWhineLevel = 0f } } }, noFan, "nofan.nowhine");
            var flat = v with { Engine = v.Engine with { Exhaust = v.Engine.Exhaust with { Steepening = 0f } } };
            var st = BandsOf(flat, null, "steep0");
            cols.Add($"{b.M - st.M,5:F1}({b.H - st.H,4:F1})");
            Console.WriteLine($"{n,-18} {b.L,5:F1} {b.M,5:F1} {b.H,5:F1}   {"",34}" + string.Join(" ", cols));
        }
        return 0;
    }

    /// <summary>
    /// The tailpipe alone at a city cruise, with the exhaust's silencing taken apart one piece at a
    /// time: the packing, the wall loss, the long mid-pipe. Dumped (OPENFPS_WHOOSH_DUMP) for octave
    /// analysis; printed here as the three bands.
    /// </summary>
    static int Pipe(string[] names)
    {
        Action<EngineVoiceState> bare = s => { s.TyreMix = 0f; s.FanMix = 0f; s.BayLeakage = 0f; s.FrontMix = 0f; s.BodyMix = 0f; };
        foreach (var n in names)
        {
            var v = VehicleProfile.ByName(n) with { AirSystem = null };
            var x = v.Engine.Exhaust;
            var variants = new (string Tag, ExhaustSpec E)[]
            {
                ("base", x),
                ("nopacking", x with { Muffler = x.Muffler with { Absorption = 0f } }),
                ("wall1", x with { WallLossMultiplier = 1f }),
                ("mid1", x with { MidPipeMetres = 1f }),
                ("nopacking.wall1", x with { Muffler = x.Muffler with { Absorption = 0f }, WallLossMultiplier = 1f }),
            };
            foreach (var (tag, e) in variants)
            {
                var b = BandsOf(v with { Engine = v.Engine with { Exhaust = e } }, bare, "pipe." + tag);
                Console.WriteLine($"{n,-18} {tag,-18} low {b.L,5:F1} mid {b.M,5:F1} high {b.H,5:F1}");
            }
        }
        return 0;
    }

    /// <summary>The offline bench held at 60 % of the governed speed under load: the pressure at the
    /// ports against what leaves the tailpipe, dumped for octave analysis.</summary>
    static int Port(string[] names)
    {
        string dir = Environment.GetEnvironmentVariable("OPENFPS_WHOOSH_DUMP") ?? ".";
        foreach (var n in names)
        {
            var v = VehicleProfile.ByName(n);
            var orders = new List<DriveOrder>
            {
                new(DriverAction.Cranking, 0.8f), new(DriverAction.Idling, 1.5f),
                new(DriverAction.Holding, 4f, MathF.Min(2100f, v.Engine.RedlineRpm * 0.85f), 1f),
            };
            if (Environment.GetEnvironmentVariable("OPENFPS_VALVE_K") is string ks) EngineSynth.ValveFlowNoiseK = float.Parse(ks);
            var r = VehicleSynth.Render(v, orders, seed: 5);
            int from = (int)(r.SampleRate * 3.5f);
            void Dump(string tag, float[] x, float scale)
            {
                using var w = new BinaryWriter(File.Create(Path.Combine(dir, $"{n}.{tag}.f32")));
                for (int i = from; i < x.Length; i++) w.Write(x[i] * scale);
            }
            Dump("port", r.Port, 1f);
            Dump("tail", r.Exhaust, r.PascalsPerUnit);
            Console.WriteLine($"{n}: exhaust {r.ExhaustDb:F1} dB");
            EngineSynth.ValveJetNoise = false;
            var q = VehicleSynth.Render(v, orders, seed: 5);
            EngineSynth.ValveJetNoise = true;
            Dump("port.nojet", q.Port, 1f);
            Dump("tail.nojet", q.Exhaust, q.PascalsPerUnit);
            Console.WriteLine($"{n}: exhaust without the valve jet {q.ExhaustDb:F1} dB");
            var x = v.Engine.Exhaust;
            var open = v with { Engine = v.Engine with { Exhaust = x with { Muffler = x.Muffler with { Kind = MufflerKind.None } } } };
            var o = VehicleSynth.Render(open, orders, seed: 5);
            Dump("tail.nomuffler", o.Exhaust, o.PascalsPerUnit);
            Console.WriteLine($"{n}: dumped");
        }
        return 0;
    }

    static (float L, float M, float H) BandsOf(VehicleProfile v, Action<EngineVoiceState>? mute, string? tag = null)
    {
        var voice = new EngineVoiceState(v, Rate, 11) { TargetSpeed = 12f };
        mute?.Invoke(voice);
        voice.PlaceAtSpeed(12f);
        voice.Revive();
        var buf = new float[Block];
        double lo = 0, mid = 0, hi = 0; long count = 0;
        string? dumpDir = tag != null ? Environment.GetEnvironmentVariable("OPENFPS_WHOOSH_DUMP") : null;
        using var dump = dumpDir == null ? null : new BinaryWriter(File.Create(Path.Combine(dumpDir, $"{v.Name}.{tag}.f32")));
        float a300 = 1f - MathF.Exp(-2f * MathF.PI * 300f / Rate), a3k = 1f - MathF.Exp(-2f * MathF.PI * 3000f / Rate);
        float l1 = 0, l1b = 0, l2 = 0, l2b = 0;
        for (int b = 0; b < Rate * 4 / Block; b++)
        {
            voice.Render(buf);
            if (b < Rate / Block) continue;
            if (dump != null) foreach (float x in buf) dump.Write(x * voice.PascalsAtFullScale);
            foreach (float x in buf)
            {
                // Two-pole low-passes; the middle is what lies between them.
                l1 += (x - l1) * a300; l1b += (l1 - l1b) * a300;
                l2 += (x - l2) * a3k; l2b += (l2 - l2b) * a3k;
                float low = l1b, high = x - l2b, m = l2b - l1b;
                lo += low * (double)low; mid += m * (double)m; hi += high * (double)high;
            }
            count += Block;
        }
        float scale = voice.PascalsAtFullScale;
        float Db(double e) => 20f * MathF.Log10(MathF.Max(1e-9f, MathF.Sqrt((float)(e / count)) * scale) / 20e-6f);
        return (Db(lo), Db(mid), Db(hi));
    }

    /// <summary>
    /// A stop the way the server makes one: speed stepped down at the vehicle's braking rate on the
    /// network's 20 Hz tick, from 10 m/s to rest. Rendered with the squeal and without, same seed, so
    /// the difference is the squeal alone; reports how many seeds squeal, and for the first that
    /// does, the squeal's loudest 100 ms against the whole front tap's.
    /// </summary>
    static int Squeal(string[] names)
    {
        foreach (var n in names)
        {
            var v = VehicleProfile.ByName(n);
            int squealing = 0, first = -1;
            for (int seed = 1; seed <= 200; seed++)
                if (new EngineVoiceState(v, Rate, seed).Squeal.Squeals) { squealing++; if (first < 0) first = seed; }
            Console.Write($"{n,-18} {squealing,3}/200 squeal");
            if (first < 0) { Console.WriteLine(); continue; }
            float hz = new EngineVoiceState(v, Rate, first).Squeal.Hz;
            var with = StopFront(v, first, 1f, 2.92f);
            var without = StopFront(v, first, 0f, 2.92f);
            int win = Rate / 10;
            float best = -99f, bestAll = -99f, at = 0f;
            for (int i = 0; i + win <= with.Length; i += win)
            {
                double e = 0, all = 0;
                for (int k = i; k < i + win; k++) { double d = with[k] - without[k]; e += d * d; all += with[k] * (double)with[k]; }
                float db = (float)(10 * Math.Log10(e / win / 4e-10 + 1e-12)), dbAll = (float)(10 * Math.Log10(all / win / 4e-10 + 1e-12));
                if (db > best) { best = db; bestAll = dbAll; at = i / (float)Rate; }
            }
            Console.WriteLine($"; seed {first} at {hz:F0} Hz: loudest {best:F1} dB at 1 m, {at:F1} s into the stop, whole front tap {bestAll:F1} dB");
        }
        return 0;
    }

    /// <summary>
    /// The tunnel truck's day: 40 km/h, up to 46 and 54 at 0.8 m/s², down to 40 at 1.03, on the
    /// network's 20 Hz tick. Prints every gear change with the rpm either side of it.
    /// </summary>
    static int Shifts(string[] names)
    {
        foreach (var n in names)
        {
            var v = VehicleProfile.ByName(n);
            var voice = new EngineVoiceState(v, Rate, 5961) { TargetSpeed = 40f / 3.6f };
            voice.PlaceAtSpeed(40f / 3.6f);
            voice.Revive();
            var buf = new float[256];
            float t = 0f, told = 40f / 3.6f;
            int gear = voice.Driveline.Gear; float rpmBefore = voice.Engine.Rpm;
            (float until, float kmh, float rate)[] legs = { (15f, 40f, 0.8f), (40f, 46f, 0.8f), (70f, 54f, 0.8f), (110f, 40f, 1.03f) };
            Console.WriteLine($"{n}:");
            int changes = 0, from = 0; float low = 0, high = 0, entered = 0;
            foreach (var leg in legs)
                while (t < leg.until)
                {
                    float want = leg.kmh / 3.6f, step = leg.rate * 0.05f;
                    if (MathF.Abs(want - told) > 1e-3f && (int)(t * 20f) != (int)((t + 256f / Rate) * 20f))
                        told += Math.Clamp(want - told, -step, step);
                    voice.TargetSpeed = told;
                    voice.Render(buf);
                    t += 256f / Rate;
                    int g = voice.Driveline.Gear;
                    float rpm = voice.Engine.Rpm;
                    if (g == 0 && gear != 0) { from = gear; low = high = rpm; entered = rpmBefore; }
                    if (g == 0) { low = MathF.Min(low, rpm); high = MathF.Max(high, rpm); }
                    if (g != 0 && gear == 0)
                    {
                        changes++;
                        Console.WriteLine($"  {t,6:F1} s  {told * 3.6f,5:F1} km/h  gear {from} -> {g}: {entered,5:F0} rpm, in neutral {low,5:F0}-{high,5:F0}, clutch in at {rpmBefore,5:F0}, then {rpm,5:F0}");
                    }
                    gear = g;
                    rpmBefore = rpm;
                }
            Console.WriteLine($"  {changes} shifts in {t:F0} s");
        }
        return 0;
    }

    /// <summary>
    /// The combustion knock alone, at a city cruise and at idle: the engine with tyres and fan muted,
    /// rendered with and without knock (same seed), dumped for octave analysis as
    /// {name}.{speed}.engine.f32 and {name}.{speed}.noknock.f32 into OPENFPS_WHOOSH_DUMP.
    /// </summary>
    /// <summary>What each engine costs to synthesize: seconds of one core per second of sound, at a
    /// city cruise. Forty cars at 0.02 each is most of a core.</summary>
    static int Cost(string[] names)
    {
        foreach (var n in names)
        {
            var v = VehicleProfile.ByName(n);
            var voice = new EngineVoiceState(v, Rate, 3) { TargetSpeed = 12f };
            voice.PlaceAtSpeed(12f); voice.Revive();
            var buf = new float[Block];
            for (int b = 0; b < Rate / Block; b++) voice.Render(buf);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int blocks = Rate * 5 / Block;
            for (int b = 0; b < blocks; b++) voice.Render(buf);
            double rt = sw.Elapsed.TotalSeconds / (blocks * (double)Block / Rate);
            // The same again with the exhaust valves' flow noise off: what that mechanism costs.
            EngineSynth.ValveJetNoise = false;
            var v2 = new EngineVoiceState(v, Rate, 3) { TargetSpeed = 12f };
            v2.PlaceAtSpeed(12f); v2.Revive();
            for (int b = 0; b < Rate / Block; b++) v2.Render(buf);
            sw.Restart();
            for (int b = 0; b < blocks; b++) v2.Render(buf);
            double rtOff = sw.Elapsed.TotalSeconds / (blocks * (double)Block / Rate);
            EngineSynth.ValveJetNoise = true;
            Console.WriteLine($"{n,-22} {rt,6:F3} core-seconds per second ({rtOff:F3} without the valve flow noise)");
        }
        return 0;
    }

    static int Knock(string[] names)
    {
        Action<EngineVoiceState> engineOnly = s => { s.TyreMix = 0f; s.FanMix = 0f; };
        foreach (var n in names)
        {
            var v = VehicleProfile.ByName(n);
            var quiet = v with { Engine = v.Engine with { Mechanical = v.Engine.Mechanical with { CombustionKnock = 0f } } };
            foreach (float speed in new[] { 0f, 12f })
            {
                BandsAt(v with { AirSystem = null }, engineOnly, $"{speed:F0}.engine", speed);
                BandsAt(quiet with { AirSystem = null }, engineOnly, $"{speed:F0}.noknock", speed);
                var still = v with { Engine = v.Engine with { Mechanical = v.Engine.Mechanical with { ValvetrainLevel = 0f } } };
                BandsAt(still with { AirSystem = null }, engineOnly, $"{speed:F0}.novalves", speed);
            }
            Console.WriteLine($"{n}: dumped");
        }
        return 0;
    }

    static void BandsAt(VehicleProfile v, Action<EngineVoiceState> mute, string tag, float speed)
    {
        string dir = Environment.GetEnvironmentVariable("OPENFPS_WHOOSH_DUMP") ?? "/tmp";
        var voice = new EngineVoiceState(v, Rate, 11) { TargetSpeed = speed };
        mute(voice);
        voice.PlaceAtSpeed(speed);
        voice.Revive();
        var buf = new float[Block];
        using var w = new BinaryWriter(File.Create(Path.Combine(dir, $"{v.EngineKey}.{tag}.f32")));
        for (int b = 0; b < Rate * 5 / Block; b++)
        {
            voice.Render(buf);
            if (b < Rate / Block) continue;
            foreach (float x in buf) w.Write(x * voice.PascalsAtFullScale);
        }
    }

    static float[] StopFront(VehicleProfile v, int seed, float squeal, float decel)
    {
        var voice = new EngineVoiceState(v, Rate, seed) { TargetSpeed = 10f, SplitVoices = true, SquealMix = squeal };
        voice.PlaceAtSpeed(10f);
        voice.Revive();
        var tap = new EngineTapState(voice);
        var rear = new float[Block]; var front = new float[Block];
        var outp = new List<float>();
        float t = 0f;
        for (int b = 0; b < Rate * 7 / Block; b++)
        {
            // The network's tick: the speed the server would have sent by now.
            float tick = MathF.Floor(t * 20f) / 20f;
            voice.TargetSpeed = MathF.Max(0f, 10f - decel * MathF.Max(0f, tick - 1f));
            voice.Produce();
            tap.Render(front);
            voice.Consume(rear);
            foreach (float x in front) outp.Add(x * voice.PascalsAtFullScale);
            t += Block / (float)Rate;
        }
        return outp.ToArray();
    }

    /// <summary>
    /// What the turbo is worth: both ends of the live voice at idle, a cruise and floored, with the
    /// whistle and without, dumped (OPENFPS_WHOOSH_DUMP) as {name}.{state}.{rear|front}[.noturbo].
    /// </summary>
    static int Turbo(string[] names)
    {
        string dir = Environment.GetEnvironmentVariable("OPENFPS_WHOOSH_DUMP") ?? ".";
        foreach (var n in names)
        {
            var v = VehicleProfile.ByName(n);
            var quiet = v with { Engine = v.Engine with { Mechanical = v.Engine.Mechanical with { TurboWhistleLevel = 0f } } };
            foreach (var (state, sp, from) in new[] { ("idle", 0f, -1f), ("cruise", 12f, -1f), ("floored", 30f, 3f) })
                foreach (var (tag, prof) in new[] { ("", v), (".noturbo", quiet) })
                {
                    var voice = new EngineVoiceState(prof, Rate, 11) { TargetSpeed = sp, SplitVoices = true };
                    voice.PlaceAtSpeed(from >= 0f ? from : sp);
                    voice.Revive();
                    var tap = new EngineTapState(voice);
                    var rear = new float[Block]; var front = new float[Block];
                    using var wr = new BinaryWriter(File.Create(Path.Combine(dir, $"{n}.{state}.rear{tag}.f32")));
                    using var wf = new BinaryWriter(File.Create(Path.Combine(dir, $"{n}.{state}.front{tag}.f32")));
                    float scale = voice.PascalsAtFullScale;
                    for (int b = 0; b < Rate * 4 / Block; b++)
                    {
                        voice.Produce();
                        tap.Render(front);
                        voice.Consume(rear);
                        if (b < Rate / Block) continue;
                        for (int i = 0; i < Block; i++) { wr.Write(rear[i] * scale); wf.Write(front[i] * scale); }
                    }
                }
            Console.WriteLine($"{n}: dumped");
        }
        return 0;
    }

    /// <summary>The front tap alone at idle and a cruise, whole and with each part taken out,
    /// dumped as {name}.{state}.front.{part}.f32 for octave analysis.</summary>
    static int FrontParts(string[] names)
    {
        string dir = Environment.GetEnvironmentVariable("OPENFPS_WHOOSH_DUMP") ?? ".";
        foreach (var n in names)
        {
            var v = VehicleProfile.ByName(n);
            var m = v.Engine.Mechanical;
            var parts = new (string Tag, VehicleProfile P, Action<EngineVoiceState>? Mute)[]
            {
                ("all", v, null),
                ("nobay", v, s => s.BayLeakage = 0f),
                ("nofan", v, s => s.FanMix = 0f),
                ("notyres", v, s => s.TyreMix = 0f),
                ("nointake", v, s => s.FrontMix = 0f),
                ("noknock", v with { Engine = v.Engine with { Mechanical = m with { CombustionKnock = 0f } } }, null),
                ("novalves", v with { Engine = v.Engine with { Mechanical = m with { ValvetrainLevel = 0f } } }, null),
                ("noturbo", v with { Engine = v.Engine with { Mechanical = m with { TurboWhistleLevel = 0f } } }, null),
            };
            foreach (var (state, sp) in new[] { ("idle", 0f), ("cruise", 12f) })
                foreach (var (tag, prof, mute) in parts)
                {
                    var voice = new EngineVoiceState(prof, Rate, 11) { TargetSpeed = sp, SplitVoices = true };
                    mute?.Invoke(voice);
                    voice.PlaceAtSpeed(sp);
                    voice.Revive();
                    var tap = new EngineTapState(voice);
                    var rear = new float[Block]; var front = new float[Block];
                    using var wf = new BinaryWriter(File.Create(Path.Combine(dir, $"{n}.{state}.front.{tag}.f32")));
                    float scale = voice.PascalsAtFullScale;
                    for (int b = 0; b < Rate * 4 / Block; b++)
                    {
                        voice.Produce();
                        tap.Render(front);
                        voice.Consume(rear);
                        if (b < Rate / Block) continue;
                        for (int i = 0; i < Block; i++) wf.Write(front[i] * scale);
                    }
                }
            Console.WriteLine($"{n}: dumped");
        }
        return 0;
    }

    /// <summary>Full throttle from half the redline in first, as the level test drives it; says
    /// where the first non-finite value appears and in which part.</summary>
    static int NanHunt(string[] names)
    {
        foreach (var n in names)
        {
            var v = VehicleProfile.ByName(n);
            var voice = new EngineVoiceState(v, Rate, 5);
            float start = v.Engine.RedlineRpm * 0.5f / 60f * 2f * MathF.PI * v.Gearbox.WheelRadiusMetres
                        / MathF.Max(0.1f, v.Gearbox.Ratios[0] * v.Gearbox.FinalDrive);
            voice.PlaceAtSpeed(start);
            voice.Revive();
            voice.TargetSpeed = 200f;
            var buf = new float[Block];
            for (int b = 0; b < Rate * 25 / Block; b++)
            {
                voice.Render(buf);
                var e = voice.Engine;
                bool bad = false;
                foreach (float x in buf) if (!float.IsFinite(x)) { bad = true; break; }
                if (bad || !float.IsFinite(e.Exhaust) || !float.IsFinite(e.Block) || !float.IsFinite(e.Intake) || !float.IsFinite(e.Rpm))
                {
                    Console.WriteLine($"{n}: non-finite at {b * Block / (float)Rate:F2} s — speed {voice.Driveline.Speed:F1} m/s, rpm {e.Rpm:F0}, gear {voice.Driveline.Gear}; "
                                    + $"buffer {(bad ? "NaN" : "ok")}, exhaust {e.Exhaust}, block {e.Block}, intake {e.Intake}");
                    goto next;
                }
            }
            Console.WriteLine($"{n}: finite for 25 s, ended at {voice.Driveline.Speed:F1} m/s, gear {voice.Driveline.Gear}");
            next:;
        }
        return 0;
    }

    /// <summary>How each machine is put together, as the model sees it: where the engine and its
    /// outlets are, what the exhaust is, what gets out of the bay. One line each.</summary>
    static int Audit(string[] names)
    {
        Console.WriteLine($"{"preset",-20} {"engine",-44} {"L",4} {"cyl",3} {"ind",5} {"bay",4} {"rear",4} {"inZ",5} {"exZ",5} {"exH",4} {"len",4} {"exitZ",6} {"pipes",5} {"tailmm",6} {"muffler",-11} {"sys m",5} {"body",-10} {"fan",3} {"tyr",3} {"dB",5}");
        foreach (var n in names)
        {
            var v = VehicleProfile.ByName(n);
            var e = v.Engine; var x = e.Exhaust;
            float litres = MathF.PI / 4f * e.BoreMm * e.BoreMm * e.StrokeMm * e.Cylinders * 1e-6f;
            float prim = x.PrimaryLengthsMetres?.Max() ?? x.PrimaryLengthMetres;
            float sys = prim + x.CollectorPipeMetres + x.MidPipeMetres + x.TailpipeMetres.Max()
                      + (x.Muffler.Kind == MufflerKind.None ? 0f : x.Muffler.ChamberLengthsMetres.Sum() + x.Muffler.AbsorptiveLengthMetres);
            int groups = e.CollectorGroups.Length;
            var exit = new OpenFPS.Client.AudioEngine.Core.Engine.ExhaustRadiation(v, Rate).Exit;
            string body = v.Body?.GetType().Name ?? "";
            Console.WriteLine($"{n,-20} {e.Name,-44} {litres,4:F1} {e.Cylinders,3} {e.Induction.ToString()[..5],5} {v.EngineBayLeakage,4:F2} {(v.EngineAtRear ? "yes" : ""),4} {v.IntakeOffsetZ,5:F1} {v.ExhaustOffsetZ,5:F1} {v.ExhaustHeight,4:F1} {v.LengthMetres,4:F1} {exit.Z,6:F2} {x.TailpipeCount(groups),5} {x.TailpipeDiameterMm,6:F0} {x.Muffler.Kind,-11} {sys,5:F2} {v.TyreCount,3} {(v.CoolingFan != null ? "yes" : ""),3} {v.TyreCount,3} {v.SourceLevelDb,5:F1}");
        }
        return 0;
    }

    /// <summary>Where the loudness law puts a shot and a car in the mix: dB re full scale at a range
    /// of distances, before the master makeup and limiter.</summary>
    static int Placed()
    {
        Console.WriteLine($"ceiling {Loudness.RenderCeilingDb:F1} dB SPL, compression {Loudness.DynamicRangeCompression:F2}");
        var rows = new List<(string, float, float)>
        {
            ("5.56 rifle", Loudness.Rifle556Db, 0f), ("7.62x39 rifle", Loudness.Rifle762Db, 0f),
            ("9 mm pistol", Loudness.Pistol9mmDb, 0f), (".45 pistol", Loudness.Pistol45Db, 0f), ("12 ga", Loudness.Shotgun12GaugeDb, 0f),
        };
        foreach (var k in new[] { "i4_economy", "v6", "cummins_compound", "transit_bus", "police_interceptor" })
        {
            var v = VehicleProfile.ByName(k);
            rows.Add((k, v.SourceLevelDb, MathF.Max(0f, v.LengthMetres * 0.3f)));
        }
        Console.WriteLine($"{"source",-20} {"dB@1m",6} {"gain",6} {"ref m",6} {"range",6}   dBFS at 1 / 10 / 30 / 100 / 300 m");
        foreach (var (name, db, extent) in rows)
        {
            var (g, r) = extent > 0f ? Loudness.Place(db, extent) : Loudness.Place(db);
            float range = Loudness.AudibleRange(db);
            string At(float d) => $"{20f * MathF.Log10(MathF.Max(1e-9f, Loudness.RenderedGain(g, r, range, d))),6:F1}";
            Console.WriteLine($"{name,-20} {db,6:F0} {20f * MathF.Log10(g),6:F1} {r,6:F1} {range,6:F0}   {At(1)} {At(10)} {At(30)} {At(100)} {At(300)}");
        }
        return 0;
    }

    static (float Rear, float Front) Measure(VehicleProfile v, float speed, Action<EngineVoiceState>? mute = null, float from = -1f)
    {
        var voice = new EngineVoiceState(v, Rate, 11) { TargetSpeed = speed, SplitVoices = true };
        mute?.Invoke(voice);
        voice.PlaceAtSpeed(from >= 0f ? from : speed);
        voice.Revive();
        var tap = new EngineTapState(voice);
        var rear = new float[Block]; var front = new float[Block];
        double r = 0, f = 0; long count = 0;
        for (int b = 0; b < Rate * 4 / Block; b++)
        {
            voice.Produce();
            tap.Render(front);
            voice.Consume(rear);
            if (b < Rate / Block) continue;
            for (int i = 0; i < Block; i++) { r += rear[i] * (double)rear[i]; f += front[i] * (double)front[i]; }
            count += Block;
        }
        float scale = voice.PascalsAtFullScale;
        float Db(double s) => 20f * MathF.Log10(MathF.Max(1e-9f, MathF.Sqrt((float)(s / count)) * scale) / 20e-6f);
        return (Db(r), Db(f));
    }
}

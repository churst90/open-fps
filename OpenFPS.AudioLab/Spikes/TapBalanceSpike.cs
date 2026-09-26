using System.Collections.Generic;
using System;
using System.Linq;
using OpenFPS.Common;

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

    static (float Rear, float Front) Measure(VehicleProfile v, float speed, Action<EngineVoiceState>? mute = null)
    {
        var voice = new EngineVoiceState(v, Rate, 11) { TargetSpeed = speed, SplitVoices = true };
        mute?.Invoke(voice);
        voice.PlaceAtSpeed(speed);
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

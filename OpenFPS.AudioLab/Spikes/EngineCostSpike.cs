using System.Diagnostics;
using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Core.Engine;

namespace OpenFPS.Client.Core.AudioEngine.Fmod;

/// <summary>
/// The game's engine voice (<see cref="EngineVoiceState"/>) on the bench: what one live vehicle costs
/// a core (--engine-cost), its level at a metre (--engine-levels, --voice-levels) and its crackle
/// (--engine-jumps). Cost is measured at a lapping speed, not idle: the valve solver and the
/// steepening work harder under load, so idle flatters the budget.
/// </summary>
public static class EngineCostSpike
{
    /// <summary>
    /// Every machine's offline tailpipe at full load: its level and its peak (--engine-levels). The level
    /// places the emitter (Loudness.Place); the peak sets what full scale means in the DSP, and a wrong one
    /// clips through EngineVoiceState's tanh (a race engine against a road car's reference is a square wave).
    /// </summary>
    public static int Levels()
    {
        Console.WriteLine("\n  Full load, one metre. level = loudest second; peak = the largest sample in it.\n");
        Console.WriteLine("    preset            level dB    peak dB   99.9% dB   crest dB   sust dB   declared");
        // Every machine the game would play: an authored machine overrides a built-in preset.
        foreach (var key in MachineRegistry.Ids)
        {
            var v = MachineRegistry.VehicleFor(key);
            var orders = new System.Collections.Generic.List<DriveOrder>
            {
                new(DriverAction.Cranking, 0.5f),
                new(DriverAction.Idling, 1.2f),
                new(DriverAction.Holding, 3.5f, v.Engine.RedlineRpm * 0.85f, 1f),
            };
            var r = VehicleSynth.Render(v, orders, seed: 5);
            // The offline buffers are normalised; PascalsPerUnit puts them back in pascals (without it
            // the crest factor printed as -30 dB).
            float peak = 0f;
            int from = r.OrderStart[2];
            for (int i = from; i < r.Exhaust.Length; i++)
            {
                float pa = MathF.Abs(r.Exhaust[i] + r.Intake[i] * 0.35f) * r.PascalsPerUnit;
                if (pa > peak) peak = pa;
            }
            // The headroom must clear the 99.9th percentile, not the single largest sample (one backfire
            // in three seconds): rounding one transient is limiting, rounding all of them is clipping.
            var sorted = new System.Collections.Generic.List<float>();
            for (int i = from; i < r.Exhaust.Length; i++)
                sorted.Add(MathF.Abs(r.Exhaust[i] + r.Intake[i] * 0.35f) * r.PascalsPerUnit);
            sorted.Sort();
            float p999 = sorted[(int)(sorted.Count * 0.999f)];
            float peakDb = 20f * MathF.Log10(MathF.Max(1e-6f, peak) / 20e-6f);
            float p999Db = 20f * MathF.Log10(MathF.Max(1e-6f, p999) / 20e-6f);
            Console.WriteLine($"    {key,-16}  {r.ExhaustDb,8:F1}   {peakDb,8:F1}   {p999Db,8:F1}   "
                            + $"{peakDb - r.ExhaustDb,7:F1}   {p999Db - r.ExhaustDb,7:F1}   {v.SourceLevelDb,8:F0}");
        }
        Console.WriteLine();
        return 0;
    }

    /// <summary>
    /// Sample-to-sample jumps in the game's voice alone, at a constant speed and with nothing else in the
    /// graph: whether a crackle is in the synthesis or added by the mixer, reflections or limiter.
    /// Deterministic, so two runs compare. --engine-jumps [preset ...] [kmh=] [sec=] [blame]
    /// </summary>
    public static int Jumps(string[] args)
    {
        if (args.Contains("blame")) return Blame(args);
        float kmh = Arg(args, "kmh", 220f), seconds = Arg(args, "sec", 6f);
        var presets = args.Where(a => VehicleProfile.Presets.ContainsKey(a)).ToList();
        if (presets.Count == 0) presets.AddRange(VehicleProfile.Presets.Keys);

        const int sr = OpenFPS.Client.AudioEngine.Fmod.MixerQuality.DefaultRate, block = 1024;   // the rate the game runs it at
        Console.WriteLine($"\n  Discontinuities in one live voice at {kmh:F0} km/h, {seconds:F0} s, no mixer.\n");
        Console.WriteLine("    preset            peak     median step    >0.04/s    >0.08/s    >0.15/s   largest");
        foreach (var key in presets)
        {
            var voice = new EngineVoiceState(VehicleProfile.ByName(key), sr, 11) { TargetSpeed = kmh / 3.6f };
            voice.PlaceAtSpeed(kmh / 3.6f);
            var buf = new float[block];
            for (int i = 0; i < sr / block; i++) voice.Render(buf);   // settle

            int blocks = (int)(seconds * sr / block);
            var steps = new List<float>(blocks * block);
            float prev = 0f, peak = 0f, largest = 0f;
            for (int b = 0; b < blocks; b++)
            {
                voice.Render(buf);
                foreach (float x in buf)
                {
                    float d = MathF.Abs(x - prev);
                    steps.Add(d); prev = x;
                    if (MathF.Abs(x) > peak) peak = MathF.Abs(x);
                    if (d > largest) largest = d;
                }
            }
            steps.Sort();
            float secs = blocks * block / (float)sr;
            int c04 = steps.Count(x => x > 0.04f), c08 = steps.Count(x => x > 0.08f), c15 = steps.Count(x => x > 0.15f);
            Console.WriteLine($"    {key,-16}  {peak,5:F3}      {steps[steps.Count / 2],9:F5}  {c04 / secs,9:F2}  {c08 / secs,9:F2}  {c15 / secs,9:F2}   {largest,7:F4}");
        }
        Console.WriteLine();
        return 0;
    }

    /// <summary>
    /// Which part of the voice (tailpipe, intake, block, tyres) makes the worst jumps: a shock front at the
    /// tailpipe and a glitch in the crank solver look identical in the total.
    /// </summary>
    private static int Blame(string[] args)
    {
        float kmh = Arg(args, "kmh", 280f), seconds = Arg(args, "sec", 5f);
        string key = args.FirstOrDefault(a => VehicleProfile.Presets.ContainsKey(a)) ?? "nascar_v8";
        var v = VehicleProfile.ByName(key);
        const int sr = OpenFPS.Client.AudioEngine.Fmod.MixerQuality.DefaultRate;   // the rate the game runs it at

        var engine = new EngineSynth(v.Engine, sr, 11);
        var dl = new Driveline(v);
        var driver = new VirtualDriver(dl, engine);
        float speed = kmh / 3.6f;
        dl.Teleport(speed);
        driver.TargetSpeed = speed;
        int gear = v.Gearbox.TopGear;
        for (int g = 1; g <= v.Gearbox.TopGear; g++)
            if (v.Gearbox.RpmFor(speed, g) <= v.Gearbox.UpshiftRpm) { gear = g; break; }
        dl.Gear = gear; dl.Clutch = 1f;
        engine.SpinTo(MathF.Max(v.Engine.IdleRpm, v.Gearbox.RpmFor(speed, gear)));

        float dt = 1f / sr;
        float scale = 1f / v.PascalsAtFullScale;
        Console.WriteLine($"\n  {key} at {kmh:F0} km/h — gear {gear}, {engine.Rpm:F0} rpm. Worst steps:\n");
        Console.WriteLine("       t(s)      step |      exhaust from ->  to     | crank deg |    rpm");

        float pEx = 0, pIn = 0, pBl = 0, pTot = 0;
        var worst = new List<(float D, float T, float From, float To, float Deg, float Rpm)>();
        int n = (int)(seconds * sr);
        for (int i = 0; i < n + sr; i++)
        {
            driver.TargetSpeed = speed;
            driver.Apply(dt);
            dl.Step(engine, dt);
            float ex = engine.Exhaust * scale;
            float inn = engine.Intake * scale;
            float bl = engine.Block * scale;
            float tot = (ex + (inn + bl * 0.4f) * 0.35f);
            if (i > sr)
            {
                float d = MathF.Abs(tot - pTot);
                worst.Add((d, (i - sr) / (float)sr, pEx, ex, engine.CrankDegrees, engine.Rpm));
            }
            pEx = ex; pIn = inn; pBl = bl; pTot = tot;
        }
        foreach (var w in worst.OrderByDescending(x => x.D).Take(14))
            Console.WriteLine($"    {w.T,7:F3}  {w.D,9:F4} |  {w.From,14:F4} -> {w.To,8:F4} |  {w.Deg,8:F1} | {w.Rpm,6:F0}");
        Console.WriteLine();
        return 0;
    }

    /// <summary>--engine-cost [preset ...] [kmh=] [sec=] [body=off] [rate=]: the real-time factor of one
    /// voice, steady and in its first half second. body=off measures what the body's resonances cost.</summary>
    public static int Run(string[] args)
    {
        float kmh = 180f, seconds = 4f;
        bool withBody = true;
        int rate = OpenFPS.Client.AudioEngine.Fmod.MixerQuality.DefaultRate;   // rate=44100 for the old mixer
        var presets = new System.Collections.Generic.List<string>();
        foreach (var arg in args)
        {
            if (arg.StartsWith("--")) continue;
            int eq = arg.IndexOf('=');
            if (eq > 0)
            {
                string k = arg[..eq], v = arg[(eq + 1)..];
                if (k == "kmh") kmh = float.Parse(v);
                else if (k == "sec") seconds = float.Parse(v);
                else if (k == "body") withBody = v != "off" && v != "0";
                else if (k == "rate") rate = int.Parse(v);
            }
            else if (VehicleProfile.Presets.ContainsKey(arg)) presets.Add(arg);
        }
        if (presets.Count == 0) presets.AddRange(VehicleProfile.Presets.Keys);

        int sr = rate, block = 512;
        var buf = new float[block];
        int blocks = (int)(seconds * sr / block);

        Console.WriteLine($"\n  Cost of one live engine voice — {seconds:F0} s of audio at {sr} Hz, {kmh:F0} km/h, {block}-sample blocks.\n");
        Console.WriteLine("    preset            realtime x    us/block    voices per core (at 50% headroom)   first 500 ms");

        foreach (var key in presets)
        {
            var profile = VehicleProfile.ByName(key);
            if (!withBody) profile = profile with { Body = VehicleBody.None };
            var voice = new EngineVoiceState(profile, sr, 7) { TargetSpeed = kmh / 3.6f };

            // The first half second: a map load creates thirty voices while the JIT keeps them at tier-0,
            // measured at twice steady cost (docs/AUDIO_LOAD_DROPOUTS.md section 2). Only the first preset
            // of a run is cold; DOTNET_TC_CallCountingDelayMs=100000 makes every row cold, and should read
            // close to steady state. Half of it means the per-sample path lost AggressiveOptimization.
            int coldBlocks = (int)(0.5 * sr / block);
            var coldClock = Stopwatch.StartNew();
            for (int i = 0; i < coldBlocks; i++) voice.Render(buf);
            coldClock.Stop();
            double cold = coldBlocks * (double)block / sr / coldClock.Elapsed.TotalSeconds;

            // Let the engine reach the speed and the pipes fill before the steady clock starts.
            for (int i = 0; i < sr / block; i++) voice.Render(buf);

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < blocks; i++) voice.Render(buf);
            sw.Stop();

            double rendered = blocks * (double)block / sr;
            double factor = rendered / sw.Elapsed.TotalSeconds;
            double usPerBlock = sw.Elapsed.TotalMilliseconds * 1000.0 / blocks;
            Console.WriteLine($"    {key,-16}  {factor,9:F1}    {usPerBlock,8:F0}    {factor * 0.5,6:F1}                     {cold,6:F1}x");
        }

        Console.WriteLine("""

    Reading it
      realtime x   seconds of engine rendered per second of one core. 20x means one voice costs 5%
                   of a core.
      voices       how many fit in half a core, which is the most a mixer callback should ever take:
                   the rest of the frame still has HRTF, reverb, reflections and the game in it.
      first 500ms  the same figure for a voice that has just been created, which is what a map load
                   makes thirty of at once. Only the first row of a run is genuinely cold; pin
                   tiering with DOTNET_TC_CallCountingDelayMs=100000 to put every row in that state.
""");
        return 0;
    }


    /// <summary>
    /// The game's whole voice at one metre against its declaration (--voice-levels [preset ...] [sweep]
    /// [parts]). <c>--engine-levels</c> measures the offline tailpipe plus a third of the intake, which is
    /// what <see cref="VehicleProfile.SourceLevelDb"/> was set from; the live voice adds the body, tyres,
    /// bay leak and air system, and <see cref="Loudness.Place(float)"/> knows only the declaration. Each
    /// layer's worth is the total's fall with it muted: under about a decibel it is not heard as itself.
    /// </summary>
    public static int VoiceLevels(string[] args)
    {
        const int sr = OpenFPS.Client.AudioEngine.Fmod.MixerQuality.DefaultRate, block = 1024;   // the rate the game runs it at
        var presets = args.Where(a => MachineRegistry.Knows(a)).ToList();
        if (presets.Count == 0) presets.AddRange(MachineRegistry.Ids);
        if (args.Contains("sweep")) return Sweep(presets, sr, block);
        if (args.Contains("parts")) return Parts(presets, sr, block);

        Console.WriteLine("\n  The live voice at one metre, full load. Every column is dB SPL.\n");
        Console.WriteLine("    preset            declared    live     diff      bay      fan    tyres     rpm   gear");

        foreach (var key in presets)
        {
            var v = MachineRegistry.VehicleFor(key);
            float full = Measure(v, sr, block, bay: true, tyres: true, fan: true, out float rpm, out int gear);
            float noBay = Measure(v, sr, block, bay: false, tyres: true, fan: true, out _, out _);
            float noTyre = Measure(v, sr, block, bay: true, tyres: false, fan: true, out _, out _);
            float noFan = Measure(v, sr, block, bay: true, tyres: true, fan: false, out _, out _);
            string bayCol = v.EngineBayLeakage > 0f ? $"{full - noBay,7:F1}" : "      —";
            string fanCol = v.CoolingFan != null ? $"{full - noFan,7:F1}" : "      —";
            Console.WriteLine($"    {key,-16}  {v.SourceLevelDb,8:F0}  {full,6:F1}  {full - v.SourceLevelDb,7:+0.0;-0.0;0.0}  "
                            + $"{bayCol}  {fanCol}  {full - noTyre,6:F1}  {rpm,6:F0}  {gear,5}");
        }
        Console.WriteLine("\n  diff is the live voice against what the profile declares: positive means the mix is");
        Console.WriteLine("  placing the machine quieter than it renders, negative means louder.\n");
        return 0;
    }



    /// <summary>
    /// Each part of the live voice at one metre (`--voice-levels &lt;p&gt; parts`), written for the diesels'
    /// buried engines. Exhaust, intake and block are metered where EngineSynth makes them; the body, bay,
    /// fan and tyres are not separable, so each is metered by the total's fall with it muted.
    /// </summary>
    private static int Parts(System.Collections.Generic.List<string> presets, int sr, int block)
    {
        Console.WriteLine("\n  The live voice taken apart, dB SPL at one metre at full load.");
        Console.WriteLine("  exhaust / intake / block are metered where they are made; the rest by muting and differencing.\n");
        Console.WriteLine("    preset            total  exhaust   intake    block     body      bay      fan    tyres");

        foreach (var key in presets)
        {
            var v = MachineRegistry.VehicleFor(key);
            var r = Run(v, sr, block, bay: true, tyres: true, fan: true, body: true);
            if (float.IsNaN(r.Total)) { Console.WriteLine($"    {key,-16}  — never reached full load"); continue; }
            float noBody = Run(v, sr, block, bay: true, tyres: true, fan: true, body: false).Total;
            float noBay  = Run(v, sr, block, bay: false, tyres: true, fan: true, body: true).Total;
            float noFan  = Run(v, sr, block, bay: true, tyres: true, fan: false, body: true).Total;
            float noTyre = Run(v, sr, block, bay: true, tyres: false, fan: true, body: true).Total;

            static string Worth(float full, float without) =>
                float.IsNaN(without) ? "      —" : $"{full - without,7:F1}";
            static string Own(float db) => db < 1f ? "      —" : $"{db,7:F1}";

            Console.WriteLine($"    {key,-16} {r.Total,6:F1}  {Own(r.Exhaust)}  {Own(r.Intake)}  {Own(r.Block)}  "
                            + $"{Worth(r.Total, noBody)}  {(v.EngineBayLeakage > 0f ? Worth(r.Total, noBay) : "      —")}  "
                            + $"{(v.CoolingFan != null ? Worth(r.Total, noFan) : "      —")}  {Worth(r.Total, noTyre)}");
        }
        Console.WriteLine("\n  The three outlets are absolute levels; the last four are what the TOTAL loses without them.");
        Console.WriteLine("  A layer worth under a decibel is inaudible as itself whatever it measures alone.\n");
        return 0;
    }

    /// <summary>
    /// The live voice's level against engine speed (`--voice-levels &lt;p&gt; sweep`). Written when the bus
    /// read 6 dB under its declaration live and on it offline: a curve tells a quieter voice from a
    /// different operating point, which one number cannot.
    /// </summary>
    private static int Sweep(System.Collections.Generic.List<string> presets, int sr, int block)
    {
        foreach (var key in presets)
        {
            var v = MachineRegistry.VehicleFor(key);
            float redline = v.Engine.RedlineRpm;
            Console.WriteLine($"\n  {key}: the live voice against engine speed. Declared {v.SourceLevelDb:F0} dB; "
                            + $"the bench holds {redline * 0.85f:F0} rpm.\n");
            Console.WriteLine("      rpm    fraction   throttle   gear    live dB   exhaust dB   boost bar");

            var voice = new EngineVoiceState(v, sr, 5);
            float start = redline * 0.35f / 60f * 2f * MathF.PI * v.Gearbox.WheelRadiusMetres
                        / MathF.Max(0.1f, v.Gearbox.Ratios[0] * v.Gearbox.FinalDrive);
            voice.PlaceAtSpeed(start);
            voice.Revive();
            voice.TargetSpeed = 200f;
            var buf = new float[block];
            for (int i = 0; i < sr / block; i++) voice.Render(buf);

            const int buckets = 20;
            var sum = new double[buckets]; var count = new long[buckets];
            var thr = new double[buckets]; var gearOf = new int[buckets];
            // The tailpipe alone in pascals, the offline bench's quantity: where the two disagree, the
            // difference is inside EngineSynth.
            var ex = new double[buckets]; var boost = new double[buckets];
            for (int b = 0; b < (int)(40f * sr / block); b++)
            {
                voice.Render(buf);
                if (voice.Engine.Throttle < 0.8f) continue;       // shifting: not full load
                int bi = (int)(voice.Engine.Rpm / redline * buckets);
                if (bi < 0 || bi >= buckets) continue;
                foreach (float x in buf) { sum[bi] += (double)x * x; count[bi]++; }
                thr[bi] = voice.Engine.Throttle; gearOf[bi] = voice.Driveline.Gear;
                ex[bi] += (double)voice.Engine.Exhaust * voice.Engine.Exhaust * buf.Length;
                boost[bi] = 0;
            }
            for (int bi = 0; bi < buckets; bi++)
            {
                if (count[bi] < sr / 8) continue;                 // less than an eighth of a second
                float rms = MathF.Sqrt((float)(sum[bi] / count[bi]));
                float db = 20f * MathF.Log10(MathF.Max(1e-9f, rms * voice.PascalsAtFullScale) / 20e-6f);
                float frac = (bi + 0.5f) / buckets;
                float exDb = 20f * MathF.Log10(MathF.Max(1e-9f, MathF.Sqrt((float)(ex[bi] / count[bi]))) / 20e-6f);
                Console.WriteLine($"    {frac * redline,6:F0}      {frac,6:F2}     {thr[bi],6:F2}  {gearOf[bi],5}   {db,8:F1}   {exDb,10:F1}   {boost[bi],9:F2}");
            }
        }
        Console.WriteLine();
        return 0;
    }


    /// <summary>One run of the live voice at full load, reporting the total and the engine's three
    /// outlets. The mutes are the same ones <see cref="Measure"/> uses.</summary>
    private static (float Total, float Exhaust, float Intake, float Block)
        Run(VehicleProfile v, int sr, int block, bool bay, bool tyres, bool fan, bool body)
    {
        var voice = new EngineVoiceState(v, sr, 5);
        if (!bay) voice.BayLeakage = 0f;
        if (!tyres) voice.TyreMix = 0f;
        if (!fan) voice.FanMix = 0f;
        if (!body) voice.BodyMix = 0f;
        float redline = v.Engine.RedlineRpm;
        float start = redline * 0.5f / 60f * 2f * MathF.PI * v.Gearbox.WheelRadiusMetres
                    / MathF.Max(0.1f, v.Gearbox.Ratios[0] * v.Gearbox.FinalDrive);
        voice.PlaceAtSpeed(start);
        voice.Revive();
        voice.TargetSpeed = 200f;

        var buf = new float[block];
        for (int i = 0; i < sr / block; i++) voice.Render(buf);

        double tot = 0, ex = 0, inn = 0, bl = 0; long n = 0;
        for (int b = 0; b < (int)(18f * sr / block) && n <= 3L * sr; b++)
        {
            voice.Render(buf);
            float rpm = voice.Engine.Rpm;
            if (rpm < redline * 0.78f || rpm > redline * 0.95f || voice.Engine.Throttle < 0.8f) continue;
            foreach (float x in buf) { tot += (double)x * x; n++; }
            // Sampled once per block: a block is 23 ms, and the outlets' statistics change slower.
            ex += (double)voice.Engine.Exhaust * voice.Engine.Exhaust * buf.Length;
            inn += (double)voice.Engine.Intake * voice.Engine.Intake * buf.Length;
            bl += (double)voice.Engine.Block * voice.Engine.Block * buf.Length;
        }
        if (n == 0) return (float.NaN, float.NaN, float.NaN, float.NaN);
        static float D(double sum, long n) => 20f * MathF.Log10(MathF.Max(1e-12f, MathF.Sqrt((float)(sum / n))) / 20e-6f);
        float total = 20f * MathF.Log10(MathF.Max(1e-9f, MathF.Sqrt((float)(tot / n)) * voice.PascalsAtFullScale) / 20e-6f);
        return (total, D(ex, n), D(inn, n), D(bl, n));
    }

    /// <summary>
    /// One vehicle's live voice at full throttle, metered near its redline as <c>--engine-levels</c> holds
    /// it; a steady cruise would meter a part-throttle engine and read low.
    /// </summary>
    private static float Measure(VehicleProfile v, int sr, int block, bool bay, bool tyres, bool fan,
                                 out float atRpm, out int atGear)
    {
        var voice = new EngineVoiceState(v, sr, 5);
        if (!bay) voice.BayLeakage = 0f;
        if (!tyres) voice.TyreMix = 0f;
        if (!fan) voice.FanMix = 0f;
        float redline = v.Engine.RedlineRpm;
        // Rolling at half the redline in first, from the gearbox, then floored.
        float start = redline * 0.5f / 60f * 2f * MathF.PI * v.Gearbox.WheelRadiusMetres
                    / MathF.Max(0.1f, v.Gearbox.Ratios[0] * v.Gearbox.FinalDrive);
        voice.PlaceAtSpeed(start);
        voice.Revive();
        voice.TargetSpeed = 200f;   // floored: past anything on the road, so the driver never lifts

        var buf = new float[block];
        for (int i = 0; i < sr / block; i++) voice.Render(buf);   // settle the pipes and the envelope

        double sum = 0; long n = 0;
        atRpm = 0f; atGear = 0;
        int blocks = (int)(18f * sr / block);
        for (int b = 0; b < blocks; b++)
        {
            voice.Render(buf);
            float r = voice.Engine.Rpm;
            // Only where the bench holds the engine. The throttle test is needed: a bus's 0.9 s gear
            // change is off the throttle inside the rpm window, and metering it read several dB low.
            if (r < redline * 0.78f || r > redline * 0.95f || voice.Engine.Throttle < 0.8f) continue;
            foreach (float x in buf) { sum += (double)x * x; n++; }
            atRpm = r; atGear = voice.Driveline.Gear;
            if (n > 3L * sr) break;
        }
        if (n == 0) { atRpm = voice.Engine.Rpm; atGear = voice.Driveline.Gear; return float.NaN; }
        float rms = MathF.Sqrt((float)(sum / n));
        // Full scale is PascalsAtFullScale (declared level plus peak headroom); dB SPL re 20 uPa.
        return 20f * MathF.Log10(MathF.Max(1e-9f, rms * voice.PascalsAtFullScale) / 20e-6f);
    }

    private static float Arg(string[] args, string key, float fallback)
    {
        string? a = args.FirstOrDefault(x => x.StartsWith(key + "=", StringComparison.Ordinal));
        return a != null && float.TryParse(a[(key.Length + 1)..], out float v) ? v : fallback;
    }
}

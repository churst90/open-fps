using System;
using System.Diagnostics;
using System.Linq;
using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Core.Engine;

namespace OpenFPS.Client.Core.AudioEngine.Fmod;

/// <summary>
/// How much of a CPU one live vehicle costs.
///
/// A grid of cars is not an audio design question until it is a budget question: every car on the
/// track is a whole engine integrated sample by sample inside the FMOD mixer callback, and the mixer
/// callback has a hard deadline. This renders the GAME'S voice — <see cref="EngineVoiceState"/>,
/// the same object the DSP wraps — for a fixed stretch of audio and reports the real-time factor,
/// which is how many seconds of engine one second of CPU buys. Divide a sensible share of a core by
/// that and you have the size of the grid.
///
/// Measured at the speed a car actually laps at, not at idle: the valve solver iterates more and the
/// steepening does more work under load, so an idle measurement flatters the budget.
/// </summary>
public static class EngineCostSpike
{
    /// <summary>
    /// What every preset actually measures at full load, and the peak that goes with it.
    ///
    /// Both numbers are needed and they are different numbers. The LEVEL decides where the emitter
    /// is placed in the world (Loudness.Place). The PEAK decides what one full-scale sample has to
    /// mean inside the DSP, and getting that wrong does not make the engine quiet or loud — it makes
    /// it CLIP, because EngineVoiceState runs everything past its reference through a tanh. A race
    /// engine is twenty decibels over a road car and, against a road car's reference, arrives as a
    /// square wave.
    ///
    /// Command line: --engine-levels
    /// </summary>
    public static int Levels(string[] args)
    {
        Console.WriteLine("\n  Full load, one metre. level = loudest second; peak = the largest sample in it.\n");
        Console.WriteLine("    preset            level dB    peak dB   99.9% dB   crest dB   sust dB   declared");
        // Every machine the game would play, not every preset the library holds: an authored machine
        // that overrides a built-in has to be measured as the thing that will actually be heard.
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
            // The offline buffers are NORMALISED; PascalsPerUnit is what puts them back in pascals.
            // Comparing a normalised peak against a level in dB SPL is how a crest factor of minus
            // thirty decibels gets printed, which is not a thing.
            float peak = 0f;
            int from = r.OrderStart[2];
            for (int i = from; i < r.Exhaust.Length; i++)
            {
                float pa = MathF.Abs(r.Exhaust[i] + r.Intake[i] * 0.35f) * r.PascalsPerUnit;
                if (pa > peak) peak = pa;
            }
            // The absolute peak can be one backfire in three seconds. The 99.9th percentile is what
            // the waveform does over and over, and it is that — not the single largest sample — the
            // headroom has to clear, because rounding one transient is limiting and rounding all of
            // them is clipping.
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
    /// Sample-to-sample discontinuities in the GAME'S voice, rendered on its own.
    ///
    /// "It crackles" has to be narrowed down before it can be fixed, and the first cut is whether the
    /// crackle is IN the synthesis or added by the things around it — the mixer, the reflections, the
    /// limiter. This renders EngineVoiceState alone at a constant speed, with no spatialisation and
    /// nothing else in the graph, and counts the jumps. Fully deterministic, so two runs can actually
    /// be compared, which a live scene cannot.
    ///
    /// Command line: --engine-jumps [preset ...] [kmh=..] [sec=..] [blame]
    /// </summary>
    public static int Jumps(string[] args)
    {
        if (args.Contains("blame")) return Blame(args);
        float kmh = Arg(args, "kmh", 220f), seconds = Arg(args, "sec", 6f);
        var presets = args.Where(a => VehicleProfile.Presets.ContainsKey(a)).ToList();
        if (presets.Count == 0) presets.AddRange(VehicleProfile.Presets.Keys);

        const int sr = 44100, block = 1024;
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
    /// Which PART of the engine is stepping.
    ///
    /// The voice sums four things — the tailpipe, the intake, the block and the tyres — and a jump in
    /// the total says nothing about which. This steps the synthesis one sample at a time and, on the
    /// worst discontinuities, prints what each component did across them. A shock front arriving at
    /// the tailpipe and a glitch in the crank solver look identical in the output and are fixed in
    /// completely different places.
    /// </summary>
    private static int Blame(string[] args)
    {
        float kmh = Arg(args, "kmh", 280f), seconds = Arg(args, "sec", 5f);
        string key = args.FirstOrDefault(a => VehicleProfile.Presets.ContainsKey(a)) ?? "nascar_v8";
        var v = VehicleProfile.ByName(key);
        const int sr = 44100;

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

    /// <summary>Command line: --engine-cost [preset ...] [kmh=..] [sec=..]</summary>
    public static int Run(string[] args)
    {
        float kmh = 180f, seconds = 4f;
        // body=off strips the car's own resonances, so what they cost can be measured rather than
        // assumed. A remembered figure from a different build is not a baseline.
        bool withBody = true;
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
            }
            else if (VehicleProfile.Presets.ContainsKey(arg)) presets.Add(arg);
        }
        if (presets.Count == 0) presets.AddRange(VehicleProfile.Presets.Keys);

        const int sr = 48000, block = 512;
        var buf = new float[block];
        int blocks = (int)(seconds * sr / block);

        Console.WriteLine($"\n  Cost of one live engine voice — {seconds:F0} s of audio at {sr} Hz, {kmh:F0} km/h, {block}-sample blocks.\n");
        Console.WriteLine("    preset            realtime x    us/block    voices per core (at 50% headroom)   first 500 ms");

        foreach (var key in presets)
        {
            var profile = VehicleProfile.ByName(key);
            if (!withBody) profile = profile with { Body = VehicleBody.None };
            var voice = new EngineVoiceState(profile, sr, 7) { TargetSpeed = kmh / 3.6f };

            // The FIRST half second, before anything has settled — the column this spike spent its
            // whole life not having.
            //
            // It rendered a second of audio to let the engine reach speed and the pipes fill, and
            // only then started the clock, so it measured the one condition that never matters: a
            // voice that has been running for a while on an idle machine. What a map load is, is the
            // opposite — thirty voices created at once while the JIT is busy compiling everything
            // else, which keeps the synthesis at unoptimized tier-0 for as long as the load lasts.
            // That cost twice steady state when it was finally measured, and it was invisible here.
            //
            // Only the FIRST preset in a run sees a genuinely cold JIT; after that the code is warm
            // whatever this column does. To reproduce the load condition for every row, pin tiering:
            //     DOTNET_TC_CallCountingDelayMs=100000 dotnet ... --engine-cost <preset>
            // which should now read close to the steady-state column. If it reads half of it again,
            // something on the per-sample path has lost its AggressiveOptimization.
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
    /// What the GAME'S voice measures at one metre — not what the offline render does.
    ///
    /// <c>--engine-levels</c> answers a different question than anyone reading it thinks. It renders
    /// <see cref="VehicleSynth"/> offline and measures the TAILPIPE plus a third of the intake, and
    /// that figure is what every preset's <see cref="VehicleProfile.SourceLevelDb"/> was set from.
    /// The thing the game actually plays is <see cref="EngineVoiceState"/>, which is that plus the
    /// body ringing, plus the tyres, plus whatever leaks out of the engine bay, plus the air system.
    /// Those extra layers were each added and heard, but the declared level was never re-measured
    /// against them — and the declared level is what <see cref="Loudness.Place"/> hangs the emitter
    /// off, so anything the voice makes above its declaration is loudness the mix does not know it
    /// has, and anything below is a machine placed louder than it can fill.
    ///
    /// So this renders the live voice at full load and reports the one-metre level it truly has, the
    /// declaration beside it, and what each added layer is worth by muting it and differencing.
    /// A layer worth less than about a decibel is not audible as itself, whatever it was intended to
    /// do; that is the column to read after adding one.
    ///
    /// Command line: --voice-levels [preset ...]
    /// </summary>
    public static int VoiceLevels(string[] args)
    {
        const int sr = 44100, block = 1024;
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
            // What a layer is WORTH is how much the total falls without it, which is the honest
            // figure: a layer twenty decibels under the exhaust moves the total by nothing at all,
            // however loud it measures on its own.
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
    /// What each PART of the live voice makes, in pascals at one metre — `--voice-levels &lt;p&gt; parts`.
    ///
    /// The diesel evaluation. "I can hardly hear the engines on those diesels" is a statement about
    /// a balance inside one machine, and the only way to read a balance is to meter the parts
    /// separately: a mechanical layer twenty decibels under the tailpipe is not a quiet layer, it is
    /// an absent one, and it looks identical in the total to a layer that is working.
    ///
    /// The engine's three outlets are metered where they are made — exhaust, intake and block are
    /// what EngineSynth hands out every sample. The rest are metered by DIFFERENCE, rendering the
    /// same voice with one thing muted, because the body, the bay, the fan and the tyres are not
    /// separable signals inside the voice; what they are worth is how much the total falls without
    /// them, which is the number that decides whether they are audible at all.
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
    /// The live voice's level against ENGINE SPEED, for one preset — `--voice-levels &lt;p&gt; sweep`.
    ///
    /// Written to settle a single disagreement: the bus measured six decibels under its declaration
    /// on the live voice and exactly on it offline, and there are only two things that can be —
    /// the voice is quieter than the offline render, or the two are being metered at different
    /// operating points. One number cannot tell them apart; a curve can. If the live curve passes
    /// through the declared figure at the rpm the bench holds, the declaration is right and the
    /// single-number measurement was reading the wrong place on it.
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

            // One bucket per twentieth of the redline, filled by whatever the run passes through.
            const int buckets = 20;
            var sum = new double[buckets]; var count = new long[buckets];
            var thr = new double[buckets]; var gearOf = new int[buckets];
            // The tailpipe ON ITS OWN, in pascals, straight off the synthesis — before the body,
            // the bay, the fan, the tyres and the full-scale reference. This is the same quantity
            // the offline bench reports, so where the two disagree the disagreement is inside
            // EngineSynth and not in anything the live voice adds around it.
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
            // The outlets are sampled once per block rather than per sample: they are already
            // pascals, and a block is 23 ms of a signal whose statistics do not change that fast.
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
    /// One vehicle's live voice, accelerating at full throttle, metered over the stretch where the
    /// engine is near its redline — the same condition <c>--engine-levels</c> holds, so the two
    /// numbers are comparable. Steady cruise would measure a part-throttle engine and read low.
    /// </summary>
    private static float Measure(VehicleProfile v, int sr, int block, bool bay, bool tyres, bool fan,
                                 out float atRpm, out int atGear)
    {
        var voice = new EngineVoiceState(v, sr, 5);
        if (!bay) voice.BayLeakage = 0f;
        if (!tyres) voice.TyreMix = 0f;
        if (!fan) voice.FanMix = 0f;
        float redline = v.Engine.RedlineRpm;
        // Start rolling at a road speed and floor it. A vehicle's gearing decides what speed that
        // is, so it is found from the gearbox rather than guessed: half the redline in first.
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
            // Meter only where the engine is doing what the offline bench holds it at. Everything
            // else in the run is the getting-there.
            //
            // The THROTTLE condition is not belt and braces. A bus's gearbox takes nine tenths of a
            // second to change gear and the engine is off the throttle for all of it, at an rpm that
            // is still inside the window — so a run metered on rpm alone averages full load together
            // with a shut throttle and reads several decibels under what the same engine measures on
            // a bench that simply holds it there.
            if (r < redline * 0.78f || r > redline * 0.95f || voice.Engine.Throttle < 0.8f) continue;
            foreach (float x in buf) { sum += (double)x * x; n++; }
            atRpm = r; atGear = voice.Driveline.Gear;
            if (n > 3L * sr) break;
        }
        if (n == 0) { atRpm = voice.Engine.Rpm; atGear = voice.Driveline.Gear; return float.NaN; }
        float rms = MathF.Sqrt((float)(sum / n));
        // Full scale means PascalsAtFullScale pascals, which is where the profile's declared level
        // plus the peak headroom put it. dB SPL against 20 micropascals, like every other level here.
        return 20f * MathF.Log10(MathF.Max(1e-9f, rms * voice.PascalsAtFullScale) / 20e-6f);
    }

    private static float Arg(string[] args, string key, float fallback)
    {
        string? a = args.FirstOrDefault(x => x.StartsWith(key + "=", StringComparison.Ordinal));
        return a != null && float.TryParse(a[(key.Length + 1)..], out float v) ? v : fallback;
    }
}

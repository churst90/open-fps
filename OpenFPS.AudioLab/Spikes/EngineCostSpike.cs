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
        foreach (var key in VehicleProfile.Presets.Keys)
        {
            var v = VehicleProfile.ByName(key);
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
            var voice = new EngineVoiceState(VehicleProfile.ByName(key), sr, 7) { TargetSpeed = kmh / 3.6f };

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

    private static float Arg(string[] args, string key, float fallback)
    {
        string? a = args.FirstOrDefault(x => x.StartsWith(key + "=", StringComparison.Ordinal));
        return a != null && float.TryParse(a[(key.Length + 1)..], out float v) ? v : fallback;
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Core.Yard;

namespace OpenFPS.Client.Core.AudioEngine.Fmod;

/// <summary>
/// The machinery that stands in a garden and runs: mowers and air conditioners.
///
///   --yard [preset ...] [sec=s] [levels] [pass] [dist=m]
///
/// Three things, in the order they should be asked:
///
///   LEVELS is the first and it is not optional. What a machine MEASURES at one metre, and what
///   shape it is across nine bands, decided before anybody listens to it — a mower that reads 96 dB
///   with two thirds of its energy under 125 Hz is wrong however it sounds through one pair of
///   speakers, and finding that out by ear costs a session (the memory is `synthesis-failures`).
///
///   The RUN is a script of the thing doing its job: a mower started, walked into grass, stopped to
///   turn, put into something thick enough to bog it, and switched off; a condenser unit running its
///   fan, calling for the compressor, and satisfying the thermostat. Each one is written as the
///   LOAD and the GROUND SPEED over time, because those are the only two inputs a governed machine
///   has, and everything you hear happening to it has to come out of them.
///
///   The PASS is the mower walked past a listener at three metres, which is the one that says
///   whether the thing is a place in the world rather than a texture.
/// </summary>
public static class YardSpike
{
    private const int Sr = VehicleSynth.SampleRate;

    public static int Run(string[] args)
    {
        AcousticRegistry.Initialize();
        float seconds = Arg(args, "sec", 14f), dist = Arg(args, "dist", 3f);
        bool levels = args.Contains("levels"), pass = args.Contains("pass");
        var presets = args.Where(a => SmallMachineSpec.Presets.ContainsKey(a)).ToList();
        if (presets.Count == 0) presets = SmallMachineSpec.Presets.Keys.ToList();

        string dir = Path.Combine(AppContext.BaseDirectory, "ASSETS", "SOUNDS", "YARD");
        Directory.CreateDirectory(dir);
        Console.WriteLine("\n  Small machines: what they are made of, what they measure, what they do.\n");

        foreach (var key in presets)
        {
            var spec = SmallMachineSpec.ByName(key);
            var synth = new SmallMachineSynth(spec, Sr, 17);
            Console.WriteLine($"  {key}");
            foreach (var line in synth.Describe()) Console.WriteLine($"    {line}");

            if (levels) { Levels(spec); Console.WriteLine(); continue; }

            var (pcm, peak, trace) = Script(spec, seconds);
            string path = Path.Combine(dir, $"yard_{key}.wav");
            File.WriteAllBytes(path, VehicleSynth.ToWav16(Normalise(pcm)));
            Console.WriteLine($"    run: peak {peak:F0} dB at 1 m over {seconds:F0} s; wrote {path}");
            // What the machine DID, a second at a time, so the script can be checked without
            // listening to it: a governed engine's rpm is its load readout and nothing else.
            if (trace.Count > 0) Console.WriteLine($"    rpm by the second: {string.Join(" ", trace.Select(v => v.ToString("F0")))}");

            if (pass && spec.Cutting != null)
            {
                var by = PassBy(spec, dist);
                string p2 = Path.Combine(dir, $"yard_{key}_pass.wav");
                File.WriteAllBytes(p2, VehicleSynth.ToWav16(Normalise(by)));
                Console.WriteLine($"    pass: walked past at {dist:F0} m; wrote {p2}");
            }
            Console.WriteLine();
        }
        return 0;
    }

    /// <summary>
    /// What the machine measures at one metre, running its job, and what shape it is.
    ///
    /// The level is an RMS over the steady part — not a peak, because a mower's peak is one exhaust
    /// pulse and says nothing about how far away you can hear it. The bands are relative, so they
    /// answer "is this the right KIND of sound" separately from "is this the right volume", which
    /// are two questions that have been confused here before.
    /// </summary>
    private static void Levels(SmallMachineSpec spec)
    {
        var synth = new SmallMachineSynth(spec, Sr, 17)
        {
            Load = spec.Cutting != null ? 0.5f : 0.6f,
            GroundSpeed = spec.Cutting != null ? 1.1f : 0f,
            Running = true, CompressorOn = true,
        };
        // Let it start, come up to speed and settle: a petrol engine cranks, a compressor spools.
        for (int i = 0; i < Sr * 6; i++) synth.Step();

        int n = Sr * 6;
        var total = new float[n];
        double sTotal = 0, sEngine = 0, sBlades = 0, sCut = 0, sComp = 0, sCase = 0;
        for (int i = 0; i < n; i++)
        {
            synth.Step();
            total[i] = synth.Total;
            sTotal += synth.Total * synth.Total;
            sEngine += synth.Engine * synth.Engine;
            sBlades += synth.Blades * synth.Blades;
            sCut += synth.Cutting * synth.Cutting;
            sComp += synth.Compressor * synth.Compressor;
            sCase += synth.Casing * synth.Casing;
        }

        Console.WriteLine($"    MEASURED at 1 m: {Spl(sTotal, n):F1} dB   (declared {spec.SourceLevelDb:F0})");
        Console.Write("    parts:");
        if (sEngine > 0) Console.Write($" engine {Spl(sEngine, n):F1}");
        if (sBlades > 0) Console.Write($" blade {Spl(sBlades, n):F1}");
        if (sCut > 0) Console.Write($" cutting {Spl(sCut, n):F1}");
        if (sComp > 0) Console.Write($" compressor {Spl(sComp, n):F1}");
        if (sCase > 0) Console.Write($" casing {Spl(sCase, n):F1}");
        Console.WriteLine($"   rpm {synth.Rpm:F0}, blade {synth.BladeRpm:F0}, throttle {synth.Throttle:P0}");

        var bands = Spectrum.BandsDb(total, Sr);
        Console.WriteLine("    shape, dB against the whole:");
        for (int i = 0; i < Spectrum.BandCount; i++)
            Console.WriteLine($"      {Spectrum.BandName(i),-14} {bands[i],6:F1}");
    }

    private static float Spl(double sumSquares, int n)
        => 20f * MathF.Log10(MathF.Max(1e-9f, (float)Math.Sqrt(sumSquares / n)) / 20e-6f);

    /// <summary>
    /// The machine doing its job, written as the two inputs it has.
    ///
    /// For a mower: started on the spot, walked into grass, stopped for a turn, into something thick,
    /// out the other side, and switched off — so the governor is heard sagging and recovering four
    /// times, and the cutting hiss appears and disappears with the walking rather than with the
    /// throttle. For an air conditioner: the fan alone, the compressor called for, a minute of it,
    /// and the thermostat satisfied.
    /// </summary>
    private static (float[] Pcm, float PeakDb, List<float> RpmTrace) Script(SmallMachineSpec spec, float seconds)
    {
        var synth = new SmallMachineSynth(spec, Sr, 17);
        synth.SetListener(new Vector3(0.6f, 1.4f, -1.1f));   // behind and above it, where the operator is
        int n = (int)(seconds * Sr);
        var outBuf = new float[n];
        float peak = 0f;
        var trace = new List<float>();

        for (int i = 0; i < n; i++)
        {
            if (spec.EngineKey != null && i % Sr == 0) trace.Add(synth.Rpm);
            float t = i / (float)Sr;
            if (spec.Cutting != null)
            {
                //  0-2 s   started, standing still on the path
                //  2-5 s   walking into ordinary grass
                //  5-6.5 s stopped to turn round, blade still spinning
                //  6.5-9   walking again, and the grass gets thick at 7.5
                //  9-11    out of it
                //  11-     stopped, then the engine killed
                bool walking = (t > 2f && t < 5f) || (t > 6.5f && t < 11f);
                float thick = t > 7.5f && t < 9.2f ? 1f : 0.45f;
                synth.GroundSpeed = walking ? 1.15f : 0f;
                synth.Load = walking ? thick : 0.05f;
                synth.Running = t < seconds - 2.2f;
            }
            else
            {
                //  fan from the start; the compressor called for at 2.5 and satisfied at 10.5
                synth.Running = true;
                synth.CompressorOn = t > 2.5f && t < seconds - 3.5f;
            }
            synth.Step();
            outBuf[i] = synth.Total;
            peak = MathF.Max(peak, MathF.Abs(synth.Total));
        }
        return (outBuf, 20f * MathF.Log10(MathF.Max(1e-9f, peak) / 20e-6f), trace);
    }

    /// <summary>
    /// The mower walked past a listener standing still, at a distance, with the arrival-time deposit
    /// the aircraft flyover uses: the machine is integrated in its own time and each sample lands
    /// when it ARRIVES. Doppler is not applied, it happens.
    /// </summary>
    private static float[] PassBy(SmallMachineSpec spec, float offset)
    {
        const float c = 343f;
        float speed = 1.15f, seconds = 24f;
        var synth = new SmallMachineSynth(spec, Sr, 17) { Load = 0.5f, GroundSpeed = speed };
        var ear = new Vector3(0f, 1.6f, 0f);
        int n = (int)(seconds * Sr);
        var outBuf = new float[n + Sr];

        for (int i = 0; i < Sr * 5; i++) synth.Step();   // start it out of earshot

        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Sr;
            var at = new Vector3(speed * (t - seconds * 0.5f), 0.15f, offset);
            var toEar = ear - at;
            float d = MathF.Max(0.5f, toEar.Length());
            synth.SetListener(new Vector3(toEar.X, toEar.Y, toEar.Z));
            synth.Step();

            int land = i + (int)(d / c * Sr);
            if (land >= outBuf.Length) continue;
            // Spreading, and the air taking the top off over the path. One pole is enough at these
            // distances; it is the same shape the flyover uses.
            float g = 1f / d;
            _lp += OnePoleAlpha(MathF.Max(1500f, 12000f - 160f * d)) * (synth.Total - _lp);
            outBuf[land] += _lp * g;
        }
        return outBuf;
    }

    private static float _lp;
    private static float OnePoleAlpha(float hz) => 1f - MathF.Exp(-MathF.Tau * hz / Sr);

    private static float[] Normalise(float[] x)
    {
        float peak = 0f;
        foreach (var v in x) peak = MathF.Max(peak, MathF.Abs(v));
        if (peak <= 0f) return x;
        float g = 0.89f / peak;
        var y = new float[x.Length];
        for (int i = 0; i < x.Length; i++) y[i] = x[i] * g;
        return y;
    }

    private static float Arg(string[] args, string name, float fallback)
    {
        foreach (var a in args)
            if (a.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase)
                && float.TryParse(a[(name.Length + 1)..], out var v)) return v;
        return fallback;
    }
}

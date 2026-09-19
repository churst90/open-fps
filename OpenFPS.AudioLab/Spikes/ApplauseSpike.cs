using System;
using System.IO;
using System.Linq;
using OpenFPS.Common;

namespace OpenFPS.Client.Core.AudioEngine.Fmod;

/// <summary>
/// A crowd, on its own, at whatever size and temper you ask for.
///
///   --applause [people=400] [intensity=0.7] [sec=3] [out=DIR]
///   --applause compare=DIR          one synthesised clap against a folder of recorded ones
///
/// With no arguments it writes a sweep of them — a handful of people barely bothering, a stand
/// reacting, and a full ovation — because the interesting thing about this model is that those are
/// one sound at three arrival rates rather than three recordings.
/// </summary>
public static class ApplauseSpike
{
    private const int Sr = 44100;

    public static int Run(string[] args)
    {
        string? compare = args.FirstOrDefault(a => a.StartsWith("compare=", StringComparison.OrdinalIgnoreCase))?[8..];
        if (compare != null) return Compare(compare);

        string outDir = Arg(args, "out", ".");
        Directory.CreateDirectory(outDir);

        var specs = args.Any(a => a.StartsWith("people=") || a.StartsWith("intensity="))
            ? new[] { new CrowdApplause((int)Num(args, "people", 400), Num(args, "intensity", 0.7f), Num(args, "sec", 3f)) }
            : new[]
            {
                new CrowdApplause(30, 0.10f, 3f),      // a few people, barely bothering
                new CrowdApplause(400, 0.55f, 3f),     // a stand reacting to a pass
                new CrowdApplause(4000, 1.00f, 4f),    // the whole place on its feet
            };

        foreach (var spec in specs)
        {
            float[] pcm = Applause.Render(spec, Sr, 17);
            var bands = VehicleBody.Bands(pcm, Sr);
            string path = Path.Combine(outDir, $"applause_{spec.Clappers}_{spec.Intensity:0.00}.wav");
            WriteWav(path, pcm, Sr);

            Console.WriteLine();
            Console.WriteLine($"── {spec.Clappers} people at intensity {spec.Intensity:0.00} ──");
            Console.WriteLine($"  {Applause.LevelDb(spec.Clappers, spec.Intensity):0.0} dB at one metre, "
                            + $"{Math.Min(spec.Clappers, Applause.MaxRendered)} rendered "
                            + $"(x{MathF.Sqrt(spec.Clappers / (float)Math.Min(spec.Clappers, Applause.MaxRendered)):0.00} for the rest)");
            Console.WriteLine($"  {100 * bands.Low:0}% below 200 Hz, {100 * bands.Mid:0}% to 1.5 kHz, {100 * bands.High:0}% above");
            Console.WriteLine($"  key: {Applause.Key(spec)}");
            Console.WriteLine($"  written: {path}");
        }
        return 0;
    }

    /// <summary>
    /// The model's single clap held up against real ones, the way a footstep is: SHAPE per band,
    /// normalised to each side's own total, and how fast each one is gone.
    ///
    /// Point it at a folder of single-clap WAVs — `tools/split_footsteps.py` cuts them out of a
    /// recording of somebody clapping slowly, since a clap and a footstep are both one transient
    /// with a gap after it. A recording carries whatever room it was made in, so the tail it reports
    /// is an upper bound on the clap's own; the bands above 500 Hz are barely touched by that, the
    /// ones below are where a room adds most.
    ///
    ///   --applause compare=DIR
    /// </summary>
    private static int Compare(string dir)
    {
        var files = Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*.wav").OrderBy(f => f).ToArray()
            : Array.Empty<string>();
        if (files.Length == 0)
        {
            Console.WriteLine($"  no .wav files in {dir}");
            return 1;
        }

        var realAcc = new double[Spectrum.BandCount];
        var realDecay20 = new System.Collections.Generic.List<float>();
        var realDecay40 = new System.Collections.Generic.List<float>();
        int used = 0;
        foreach (string f in files)
        {
            var x = OpenFPS.Client.AudioEngine.Core.WeaponSynth.ReadWav16Mono(File.ReadAllBytes(f));
            if (x.Length < 512) continue;
            var e = Spectrum.BandEnergy(x, Sr);
            for (int i = 0; i < e.Length; i++) realAcc[i] += e[i];
            realDecay20.Add(DecayMs(x, -20f));
            realDecay40.Add(DecayMs(x, -40f));
            used++;
        }

        var synthAcc = new double[Spectrum.BandCount];
        var synthDecay20 = new System.Collections.Generic.List<float>();
        var synthDecay40 = new System.Collections.Generic.List<float>();
        int seeds = Math.Max(8, Math.Min(used, 32));
        for (int seed = 0; seed < seeds; seed++)
        {
            var clap = OneClap(seed);
            var e = Spectrum.BandEnergy(clap, Sr);
            for (int i = 0; i < e.Length; i++) synthAcc[i] += e[i];
            synthDecay20.Add(DecayMs(clap, -20f));
            synthDecay40.Add(DecayMs(clap, -40f));
        }

        var real = Normalise(realAcc);
        var synth = Normalise(synthAcc);

        Console.WriteLine($"\n  {used} real clap(s) from {Path.GetFileName(dir)}  vs  {seeds} synthesised single claps");
        Console.WriteLine("  Both normalised to their own total, so this is SHAPE and not level.\n");
        Console.WriteLine("    band              real    synth     gap");
        float worst = 0f; int worstBand = 0;
        for (int i = 0; i < Spectrum.BandCount; i++)
        {
            float gap = synth[i] - real[i];
            if (MathF.Abs(gap) > MathF.Abs(worst)) { worst = gap; worstBand = i; }
            Console.WriteLine($"    {Spectrum.BandName(i),-14} {real[i],6:F1}   {synth[i],6:F1}   {gap,+6:F1}");
        }
        Console.WriteLine($"\n  worst band: {Spectrum.BandName(worstBand)} at {worst:+0.0;-0.0} dB");
        Console.WriteLine("  (positive means the model has too much there)");
        Console.WriteLine($"\n  gone by 20 dB: real median {Median(realDecay20):F1} ms, synth {Median(synthDecay20):F1} ms");
        Console.WriteLine($"  gone by 40 dB: real median {Median(realDecay40):F1} ms, synth {Median(synthDecay40):F1} ms");
        Console.WriteLine("  (the real tail includes whatever room the recording was made in)\n");
        return 0;
    }

    /// <summary>One synthesised clap, cut from its own peak the way <c>ClapTests</c> cuts it.</summary>
    private static float[] OneClap(int seed)
    {
        var buf = Applause.Render(new CrowdApplause(1, 0.7f, 2f), Sr, seed);
        int peak = 0; float best = 0;
        for (int i = 0; i < buf.Length; i++) if (MathF.Abs(buf[i]) > best) { best = MathF.Abs(buf[i]); peak = i; }
        int start = Math.Max(0, peak - Sr * 3 / 1000);
        var clap = new float[Math.Min(Sr / 4, buf.Length - start)];
        Array.Copy(buf, start, clap, 0, clap.Length);
        return clap;
    }

    /// <summary>Milliseconds from the peak until the 1 ms RMS envelope last sits above the peak by this
    /// many decibels — when the sound is gone, for a listener's purposes.</summary>
    private static float DecayMs(float[] x, float db)
    {
        int peakAt = 0; float peak = 0;
        for (int i = 0; i < x.Length; i++) if (MathF.Abs(x[i]) > peak) { peak = MathF.Abs(x[i]); peakAt = i; }
        if (peak <= 0) return 0;
        float floor = peak * MathF.Pow(10f, db / 20f);
        int win = Sr / 1000;
        for (int at = x.Length - win; at > peakAt; at -= win)
        {
            double sum = 0;
            for (int i = at; i < at + win; i++) sum += x[i] * (double)x[i];
            if (Math.Sqrt(sum / win) > floor) return 1000f * (at + win - peakAt) / Sr;
        }
        return 0;
    }

    private static float Median(System.Collections.Generic.List<float> v)
    {
        if (v.Count == 0) return 0;
        var s = v.OrderBy(x => x).ToList();
        return s[s.Count / 2];
    }

    private static float[] Normalise(double[] energy)
    {
        double total = 0;
        foreach (double v in energy) total += v;
        if (total <= 0) total = 1e-12;
        var db = new float[energy.Length];
        for (int i = 0; i < energy.Length; i++)
            db[i] = 10f * MathF.Log10((float)Math.Max(energy[i] / total, 1e-9));
        return db;
    }

    private static float Num(string[] args, string key, float fallback)
    {
        foreach (string a in args)
            if (a.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)
                && float.TryParse(a[(key.Length + 1)..], out float v)) return v;
        return fallback;
    }

    private static string Arg(string[] args, string key, string fallback)
    {
        foreach (string a in args)
            if (a.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)) return a[(key.Length + 1)..];
        return fallback;
    }

    private static void WriteWav(string path, float[] samples, int rate)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(fs);
        int bytes = samples.Length * 2;
        w.Write("RIFF".ToCharArray()); w.Write(36 + bytes); w.Write("WAVE".ToCharArray());
        w.Write("fmt ".ToCharArray()); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data".ToCharArray()); w.Write(bytes);
        foreach (float v in samples) w.Write((short)Math.Clamp((int)(v * 32767f), short.MinValue, short.MaxValue));
    }
}

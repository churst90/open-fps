using System;
using System.IO;
using System.Linq;
using OpenFPS.Common;

namespace OpenFPS.Client.Core.AudioEngine.Fmod;

/// <summary>
/// A crowd, on its own, at whatever size and temper you ask for.
///
///   --applause [people=400] [intensity=0.7] [sec=3] [out=DIR]
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

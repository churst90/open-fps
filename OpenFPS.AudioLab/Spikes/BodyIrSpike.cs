using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenFPS.Common;

namespace OpenFPS.Client.Core.AudioEngine.Fmod;

/// <summary>
/// The car, on its own, with no engine in it.
///
/// Renders a vehicle body's impulse response, writes it to a WAV and measures it. Three jobs, and
/// they are all the same job: an impulse response is a sound you can listen to, a file another tool
/// can load, and a spectrum you can hold a recording of a real car against. Doing all three from one
/// command is the point — every correction in this project so far came from lining a rendered thing
/// up against a measured one.
///
///   --body-ir [preset ...] [out=DIR] [sec=0.25]
///
/// With no preset it does every distinct body the vehicle presets use.
/// </summary>
public static class BodyIrSpike
{
    private const int SampleRate = 48000;

    public static int Run(string[] args)
    {
        float seconds = Arg(args, "sec", 0.25f);
        string outDir = ArgString(args, "out", ".");
        Directory.CreateDirectory(outDir);

        var names = args.Where(a => !a.StartsWith("--") && !a.Contains('=')).ToList();
        var bodies = new List<(string Name, VehicleBody Body)>();

        if (names.Count > 0)
        {
            foreach (string n in names)
            {
                var body = ByName(n);
                if (body == null) { Console.WriteLine($"No body called '{n}'. Known: {string.Join(", ", KnownNames)}"); return 2; }
                bodies.Add((n, body));
            }
        }
        else
        {
            foreach (string n in KnownNames) bodies.Add((n, ByName(n)!));
        }

        foreach (var (name, body) in bodies)
        {
            var modes = body.Modes();
            float[] ir = body.ImpulseResponse(SampleRate, seconds);

            string path = Path.Combine(outDir, $"body_{name}.wav");
            WriteWav(path, ir, SampleRate);

            Console.WriteLine();
            Console.WriteLine($"── {name} ──");
            Console.WriteLine($"  {modes.Count} mode(s), coupling {body.Coupling:0.00}, panel loss {body.PanelLoss:0.000}");

            foreach (var m in modes.OrderBy(m => m.Hz))
                Console.WriteLine($"    {m.Hz,7:0.#} Hz   T60 {m.DecaySeconds * 1000f,6:0.#} ms   weight {m.Weight:0.000}");

            Console.WriteLine($"  Response: {Describe(ir)}");
            Console.WriteLine($"  Written: {path}");
        }

        Console.WriteLine();
        Console.WriteLine("These are the bodies alone. Hear one against an engine with --speedway, or");
        Console.WriteLine("compare two cars by giving them different bodies and rendering the same engine.");
        return 0;
    }

    private static readonly string[] KnownNames = { "saloon", "van", "supercar", "racesaloon", "openwheeler" };

    private static VehicleBody? ByName(string n) => n.ToLowerInvariant() switch
    {
        "saloon" => VehicleBody.Saloon,
        "van" => VehicleBody.Van,
        "supercar" => VehicleBody.Supercar,
        "racesaloon" or "race" => VehicleBody.RaceSaloon,
        "openwheeler" or "open" => VehicleBody.OpenWheeler,
        "none" => VehicleBody.None,
        _ => null,
    };

    private static string Describe(float[] ir)
    {
        var bands = VehicleBody.Bands(ir, SampleRate);
        if (bands.Low + bands.Mid + bands.High <= 0f) return "silent";
        // One decimal, because rounding to whole percent hid the cabin entirely once the modes
        // acquired their zeros at DC and the low band fell to a couple of per cent.
        return $"{100 * bands.Low:0.0}% below 200 Hz, {100 * bands.Mid:0.0}% to 1.5 kHz, "
             + $"{100 * bands.High:0.0}% above; rings {VehicleBody.DecayMs(ir, SampleRate):0} ms";
    }

    private static void WriteWav(string path, float[] samples, int sampleRate)
    {
        // Normalised, because an impulse response has no absolute level — it is a ratio, and what it
        // is a ratio OF is decided where it is applied, not here.
        float peak = 0f;
        foreach (float v in samples) peak = MathF.Max(peak, MathF.Abs(v));
        float scale = peak > 0f ? 0.99f / peak : 1f;

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(fs);
        int dataBytes = samples.Length * 2;

        w.Write("RIFF".ToCharArray()); w.Write(36 + dataBytes); w.Write("WAVE".ToCharArray());
        w.Write("fmt ".ToCharArray()); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(sampleRate); w.Write(sampleRate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data".ToCharArray()); w.Write(dataBytes);
        foreach (float v in samples)
            w.Write((short)Math.Clamp((int)(v * scale * 32767f), short.MinValue, short.MaxValue));
    }

    private static float Arg(string[] args, string key, float fallback)
    {
        foreach (string a in args)
            if (a.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)
                && float.TryParse(a[(key.Length + 1)..], out float v)) return v;
        return fallback;
    }

    private static string ArgString(string[] args, string key, string fallback)
    {
        foreach (string a in args)
            if (a.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)) return a[(key.Length + 1)..];
        return fallback;
    }
}

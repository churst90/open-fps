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
        var names = args.Where(a => !a.StartsWith("--")).ToArray();
        if (names.Length == 0) names = VehicleProfile.Presets.Keys.OrderBy(k => k).ToArray();
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

    static (float Rear, float Front) Measure(VehicleProfile v, float speed)
    {
        var voice = new EngineVoiceState(v, Rate, 11) { TargetSpeed = speed, SplitVoices = true };
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

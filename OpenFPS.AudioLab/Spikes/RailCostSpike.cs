using System.Diagnostics;
using OpenFPS.Client.AudioEngine.Core.Rail;
using OpenFPS.Common;

namespace OpenFPS.AudioLab.Spikes;

/// <summary>
/// --rail-cost: what one train's synth costs a core, as the game steps it (every source, every sample,
/// at 48 kHz), and what each kind of source costs within it. A train is rendered by one thread under one
/// lock (TrainVoiceState), so a synth that needs more than one core cannot keep up however many cores
/// the machine has.
///
///   --rail-cost [preset ...] [sec=5]
/// </summary>
public static class RailCostSpike
{
    private const int Rate = 48000;

    public static int Run(string[] args)
    {
        AcousticRegistry.Initialize();
        float sec = 5f;
        foreach (var a in args)
            if (a.StartsWith("sec=", StringComparison.Ordinal)) float.TryParse(a[4..], System.Globalization.CultureInfo.InvariantCulture, out sec);
        var presets = args.Where(a => ModelLibrary.Knows(ModelLibrary.Kinds.Train, a)).ToList();
        if (presets.Count == 0) presets = ModelLibrary.Ids(ModelLibrary.Kinds.Train).ToList();

        Console.WriteLine($"\n  Train synth cost at {Rate} Hz, {sec:F0} s timed after 1 s settled.\n");
        foreach (var key in presets)
        {
            var p = ModelLibrary.Train(key);
            var train = new TrainSynth(p, Rate, 41) { Speed = p.TypicalSpeedMps, Notch = 6f };
            for (int i = 0; i < Rate; i++) train.Step();
            int n = (int)(sec * Rate);
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < n; i++) train.Step();
            double share = sw.Elapsed.TotalSeconds / sec;
            Console.WriteLine($"  {key,-11} {train.Sources.Count,4} sources  {share * 100.0,6:F1} % of a core"
                              + (share > 1.0 ? "   CANNOT KEEP UP on one thread" : ""));

            // Per kind: each source's Render timed on its own, in the order the synth runs them.
            var byKind = new Dictionary<string, (int Count, double Seconds)>();
            int m = Rate;   // one second each
            foreach (var s in train.Sources)
            {
                string kind = Kind(s.Label);
                var t = Stopwatch.StartNew();
                for (int i = 0; i < m; i++) _ = s.Render();
                var e = byKind.GetValueOrDefault(kind);
                byKind[kind] = (e.Count + 1, e.Seconds + t.Elapsed.TotalSeconds);
            }
            foreach (var (kind, (count, seconds)) in byKind.OrderByDescending(k => k.Value.Seconds))
                Console.WriteLine($"      {kind,-10} x{count,-4} {seconds * 100.0,6:F1} % of a core  ({seconds / count * 100.0:F2} % each)");
        }
        return 0;
    }

    private static string Kind(string label)
    {
        foreach (var k in new[] { "bogie", "body", "exhaust", "fans", "traction", "chimney", "horn", "whistle", "bell" })
            if (label.Contains(k, StringComparison.Ordinal)) return k;
        return "other";
    }
}

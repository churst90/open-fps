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
        if (args.Contains("law"))
        {
            // What the mixer plays a train's sources at, by distance, and what the ear model adds.
            var layout = TrainLayout.Sources(TrainProfile.ByName(args.FirstOrDefault(a => ModelLibrary.Knows(ModelLibrary.Kinds.Train, a)) ?? "freight"));
            foreach (var e in layout.Where(e => e.Index < 12 || e.Index % 40 == 0))
            {
                float corr = OpenFPS.Client.AudioEngine.Core.EarTimbres.CorrectionDb("rail:freight/x/" + e.Index, e.LevelDb);
                var (g, r) = Loudness.Place(e.LevelDb, e.ExtentMetres);
                Console.WriteLine($"  {e.Index,3} {e.Kind,-12} {e.LevelDb,6:F1} dB  extent {e.ExtentMetres,4:F1}  place gain {20 * MathF.Log10(g),6:F1} dB ref {r,5:F1} m  ear {corr,+5:F1} dB  "
                    + string.Join("  ", new[] { 3f, 30f, 100f, 360f }.Select(d => $"{d,4:F0} m {20 * MathF.Log10(MathF.Max(1e-9f, OpenFPS.Client.AudioEngine.Core.Rail.TrainVoicing.LawGain(e.LevelDb, e.ExtentMetres, d))) + corr - 16f,6:F1} dBFS")));
            }
            return 0;
        }
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
                for (int i = 0; i < m; i++) _ = s.Render(p.TypicalSpeedMps);
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

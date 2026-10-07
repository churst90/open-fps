using OpenFPS.Common;

namespace OpenFPS.AudioLab.Spikes;

/// <summary>
/// The knob door rendered as the game names it, in pascals, for measuring against the recordings
/// (kyles' light wood door, Sudd's lever) with the scratchpad's analysis. The recordings are yardsticks
/// only: nothing of them is played by the game.
///
///   --knob-renders [key=K;K] [out=DIR] [stems=DIR] [events]
///
/// Without key= it renders every character opening and shutting (gentle, normal, hard, slam) at the game's
/// 1.1 and 1.4 m leaves. Each file is 32-bit float at 48 kHz, one unit being KnobDoor.PascalsAtFullScale at a
/// metre, so a file's level in dBFS plus 120 is dB SPL at a metre. Prints each render's LAFmax and peak.
/// </summary>
public static class KnobRefSpike
{
    public static int Run(string[] args)
    {
        string dir = Arg(args, "out") ?? ".";
        Directory.CreateDirectory(dir);
        string? stems = Arg(args, "stems");
        var keys = new List<string>();
        string? given = Arg(args, "key");
        if (given != null) keys.AddRange(given.Split(';', StringSplitOptions.RemoveEmptyEntries));
        else
            foreach (float w in new[] { 1.1f, 1.4f })
                for (int v = 0; v < KnobDoor.Variants; v++)
                {
                    keys.Add(KnobDoor.Key(false, KnobDoor.Construction.HollowCore, v, 0.9f, KnobDoor.Shut.Normal, w, 2.1f));
                    foreach (var how in new[] { KnobDoor.Shut.Gentle, KnobDoor.Shut.Normal, KnobDoor.Shut.Hard, KnobDoor.Shut.Slam })
                        keys.Add(KnobDoor.Key(true, KnobDoor.Construction.HollowCore, v, 0.9f, how, w, 2.1f));
                }
        var rows = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
        System.Threading.Tasks.Parallel.ForEach(keys, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = stems != null ? 1 : 4 }, key =>
        {
            if (!KnobDoor.TryParseKey(key, out bool closing, out var door, out float swing, out var how)) { rows[key] = key + " ?"; return; }
            var rep = new KnobDoor.Report();
            string name = key.Substring(KnobDoor.KeyPrefix.Length).Replace(':', '-');
            if (stems != null) { KnobDoor.StemFolder = Path.Combine(stems, name); Directory.CreateDirectory(KnobDoor.StemFolder); }
            var pcm = closing ? KnobDoor.RenderGameClose(door, 48000, how, rep) : KnobDoor.RenderOpen(door, 48000, swing, rep);
            WriteFloat(Path.Combine(dir, name + ".wav"), pcm, 48000);
            double fs = 20 * Math.Log10(KnobDoor.PascalsAtFullScale / 2e-5);
            string row = $"{key,-44} LAFmax {HeardLevelsSpike.LafMaxDbfs(pcm) + fs,6:F1}  peak {20 * Math.Log10(Math.Max(1e-9, pcm.Max(Math.Abs))) + fs,6:F1}";
            if (args.Contains("events")) row += "\n" + rep;
            rows[key] = row;
            Console.Error.WriteLine(key);
        });
        foreach (var k in keys) Console.WriteLine(rows[k]);
        return 0;
    }

    private static void WriteFloat(string path, float[] pcm, int rate)
    {
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8); w.Write(36 + pcm.Length * 4); w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)3); w.Write((short)1);
        w.Write(rate); w.Write(rate * 4); w.Write((short)4); w.Write((short)32); w.Write("data"u8); w.Write(pcm.Length * 4);
        foreach (float v in pcm) w.Write(v);
    }

    private static string? Arg(string[] args, string name)
        => args.FirstOrDefault(a => a.StartsWith(name + "=", StringComparison.Ordinal))?.Substring(name.Length + 1);
}

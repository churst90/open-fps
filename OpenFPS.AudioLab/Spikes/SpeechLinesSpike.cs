using FMOD;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Common;

/// <summary>--speech-lines: decodes every shipped voice line through the same FMOD path the client
/// uses (GranularBank.TryDecode, on a NOSOUND system) and reports what came back: how many decoded,
/// the rate, the length against the catalogue, and the peak once brought to Speech.BufferLoudnessLufs. A line
/// that fails here is a person who says nothing in the game.</summary>
public static class SpeechLinesSpike
{
    public static int Run()
    {
        Factory.System_Create(out FMOD.System sys);
        sys.setOutput(OUTPUTTYPE.NOSOUND);
        sys.init(32, INITFLAGS.NORMAL, IntPtr.Zero);
        var bank = new GranularBank(sys);

        int ok = 0, failed = 0, wrongRate = 0;
        double worstLength = 0, rmsLo = 0, rmsHi = -200, worstPeak = -200;
        foreach (var t in Speech.Takes)
        {
            // By full path: the lab does not carry the client's ASSETS folder, and GranularBank takes a
            // path with ASSETS in it as it stands.
            string id = OpenFPS.AudioLab.LabPaths.Sounds("VOICES", t.Voice, t.Line + ".ogg");
            if (!bank.TryDecode(id, out var pcm, out int ch, out int rate) || pcm.Length == 0)
            {
                failed++;
                if (failed <= 5) Console.WriteLine($"FAILED {id}");
                continue;
            }
            ok++;
            if (rate != 48000 || ch != 1) wrongRate++;
            double seconds = pcm.Length / (double)ch / rate;
            worstLength = Math.Max(worstLength, Math.Abs(seconds - t.Seconds));
            double rms = 20 * Math.Log10(Math.Sqrt(pcm.Select(v => (double)v * v).Average()) + 1e-12);
            rmsLo = ok == 1 ? rms : Math.Min(rmsLo, rms);
            rmsHi = Math.Max(rmsHi, rms);
            double peak = 20 * Math.Log10(pcm.Max(v => Math.Abs((double)v)) + 1e-12);
            double lufs = rate == 48000 && ch == 1 ? Speech.LoudnessLufs(pcm) : rms;
            worstPeak = Math.Max(worstPeak, peak - lufs + Speech.BufferLoudnessLufs);
        }
        Console.WriteLine($"{Speech.Voices.Count} voices, {Speech.Takes.Count} lines: {ok} decoded, {failed} failed, "
                        + $"{wrongRate} not mono 48 kHz");
        Console.WriteLine($"length off the catalogue by at most {worstLength * 1000:F0} ms");
        Console.WriteLine($"RMS {rmsLo:F1} to {rmsHi:F1} dBFS as recorded; the client brings every line to {Speech.BufferLoudnessLufs} LUFS");
        Console.WriteLine($"peak after rescaling at most {worstPeak:F1} dBFS (must stay under 0)");
        sys.release();
        return failed == 0 ? 0 : 1;
    }
}

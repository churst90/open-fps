using System.Diagnostics;
using System.Text;

namespace OpenFPS.Client.AudioEngine.Fmod;

/// <summary>
/// Where the mixer's time goes: each of our DSP callbacks adds what it took, by kind, and the mixer
/// load line says it as a share of the time that passed. What FMOD's own units take (the three-band EQ,
/// the low-pass, the reverb units, the mixing itself) is the rest of the dsp figure.
/// </summary>
internal static class MixerProfile
{
    public enum Kind { Binaural, Engine, EngineTap, EngineEcho, Physical, Granular, Synth, Ear, TracedReverb, TracedEchoes, Boundary, Bed, OwnVoice, EarWind, Master, Loudspeaker }

    private static readonly string[] Names =
    {
        "binaural", "engines", "engine taps", "engine echoes", "physical voices", "samples", "synths", "ear stage",
        "traced reverb", "traced echoes", "boundary", "beds", "own voice", "ear wind", "master", "loudspeakers",
    };

    private static readonly long[] _ticks = new long[Names.Length];
    private static readonly int[] _calls = new int[Names.Length];
    private static long _since = Stopwatch.GetTimestamp();

    public static long Start() => Stopwatch.GetTimestamp();

    public static void Stop(Kind kind, long start)
    {
        Interlocked.Add(ref _ticks[(int)kind], Stopwatch.GetTimestamp() - start);
        Interlocked.Increment(ref _calls[(int)kind]);
    }

    /// <summary>Each kind's share of the time since the last call, largest first, and their total; and
    /// starts again.</summary>
    public static string Take(out float total)
    {
        long now = Stopwatch.GetTimestamp();
        double span = Math.Max(1, now - Interlocked.Exchange(ref _since, now));
        var parts = new List<(string Name, double Share, int Calls)>();
        total = 0f;
        for (int i = 0; i < Names.Length; i++)
        {
            long t = Interlocked.Exchange(ref _ticks[i], 0);
            int c = Interlocked.Exchange(ref _calls[i], 0);
            if (c == 0) continue;
            double share = t / span;
            total += (float)share;
            parts.Add((Names[i], share, c));
        }
        var sb = new StringBuilder();
        foreach (var (name, share, calls) in parts.OrderByDescending(p => p.Share))
        {
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(name).Append(' ').Append((share * 100).ToString("F1", System.Globalization.CultureInfo.InvariantCulture)).Append(" %");
        }
        return sb.ToString();
    }
}

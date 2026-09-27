using System.Globalization;
using System.Reflection;

namespace OpenFPS.Common;

/// <summary>
/// People talking: which voices exist, what each can say, and how loud a person says it.
///
/// The lines are recordings, one take per voice per line, listed in <c>Speech/voices.csv</c> (written
/// by <c>tools/import_npc_voices.py</c> from the inbox manifest). The server picks who says what; the
/// client plays <c>VOICES/&lt;voice&gt;/&lt;line&gt;</c> through the ordinary world-sound path, so a greeting is
/// placed, occluded, reflected and reverberated like any other sound.
/// </summary>
public static class Speech
{
    // ── Vocal effort ────────────────────────────────────────────────────────────────────────────
    //
    // ANSI S3.5-1997, Table 3: the overall speech level one metre in front of a talker, for each vocal
    // effort. These are long-term levels, the speech's own RMS.

    /// <summary>Talking to someone beside you.</summary>
    public const float NormalDb = 62.35f;
    /// <summary>Calling to someone a few metres off, or telling someone to watch it.</summary>
    public const float RaisedDb = 68.34f;
    /// <summary>Calling across a street.</summary>
    public const float LoudDb = 74.85f;
    /// <summary>Shouting: a driver yelling out of a window.</summary>
    public const float ShoutDb = 82.3f;

    /// <summary>
    /// The level a line is rescaled to when the client loads it, dBFS RMS.
    ///
    /// The recordings are made at about -20, but not all of them: a take whose peaks would pass
    /// -1 dBFS was left quieter rather than clipped, and a yell is peakier than talk. Every line is
    /// brought to -28, which leaves room for the peakiest take in the set (26.3 dB from RMS to peak,
    /// measured with --speech-lines on 2026-09-27) with 1.7 dB to spare, and makes every person equally loud
    /// for the same effort.
    /// </summary>
    public const float BufferRmsDbfs = -28f;

    /// <summary>
    /// The level to send for a line said at a given effort: a world sound's level is its buffer's full
    /// scale at one metre, and a line at <see cref="BufferRmsDbfs"/> has its full scale that far above
    /// its speech level.
    /// </summary>
    public static float LevelDb(float effortDb) => effortDb - BufferRmsDbfs;

    /// <summary>The key a world sound carries to name a recorded line: <c>voice:maria/greet_hi</c>.</summary>
    public static string Key(string voice, string line) => $"voice:{voice}/{line}";

    /// <summary>Reads a key back into the bank's sound id, <c>VOICES/maria/greet_hi</c>.</summary>
    public static bool TryParseKey(string? key, out string soundId)
    {
        soundId = "";
        if (string.IsNullOrEmpty(key) || !key.StartsWith("voice:", StringComparison.OrdinalIgnoreCase)) return false;
        string rest = key["voice:".Length..];
        int slash = rest.IndexOf('/');
        if (slash <= 0 || slash == rest.Length - 1) return false;
        soundId = "VOICES/" + rest;
        return true;
    }

    /// <summary>
    /// How far off a normal voice on a street can still be made out word for word, metres. Past this it
    /// is heard as somebody talking, not as what they said, so a text player is only told the words
    /// inside it.
    /// </summary>
    public const float MadeOutMetres = 10f;

    // ── Which way the mouth faces ───────────────────────────────────────────────────────────────
    //
    // A talker is loudest in front. Behind the head the low end is almost unchanged and the top end
    // is much lower, so a voice from behind is duller as well as quieter. Levels at 180 degrees
    // relative to straight ahead, approximately, from Chu and Warnock 2002 (NRC, "Detailed directivity
    // of sound fields around human talkers") and Monson et al. 2012 (JASA 132, "Horizontal directivity
    // of low- and high-frequency energy in speech and singing").

    /// <summary>Below about 500 Hz, behind against in front.</summary>
    public const float BehindLowDb = -2f;
    /// <summary>About 500 Hz to 2 kHz.</summary>
    public const float BehindMidDb = -6f;
    /// <summary>Above about 2 kHz.</summary>
    public const float BehindHighDb = -13f;

    /// <summary>
    /// The band gains for a listener in a given direction from a talker facing <paramref name="forward"/>.
    /// Straight ahead is unity; the loss grows smoothly with the angle to its full value behind.
    /// </summary>
    public static (float Low, float Mid, float High) Directivity(System.Numerics.Vector3 forward,
                                                                 System.Numerics.Vector3 toListener)
    {
        forward.Y = 0f; toListener.Y = 0f;
        if (forward.LengthSquared() < 1e-6f || toListener.LengthSquared() < 1e-6f) return (1f, 1f, 1f);
        float cos = System.Numerics.Vector3.Dot(System.Numerics.Vector3.Normalize(forward),
                                                System.Numerics.Vector3.Normalize(toListener));
        float behind = (1f - Math.Clamp(cos, -1f, 1f)) * 0.5f;
        static float Gain(float db) => MathF.Pow(10f, db / 20f);
        return (Gain(BehindLowDb * behind), Gain(BehindMidDb * behind), Gain(BehindHighDb * behind));
    }

    /// <summary>How high a standing adult's mouth is above their feet, metres.</summary>
    public const float MouthHeight = 1.55f;

    // ── The catalogue ───────────────────────────────────────────────────────────────────────────

    /// <summary>One recorded take.</summary>
    public sealed record Take(string Voice, string Kind, string Category, string Line, string Text, float Seconds);

    private static IReadOnlyList<Take>? _takes;

    /// <summary>Every take shipped, in catalogue order.</summary>
    public static IReadOnlyList<Take> Takes => _takes ??= Load();

    /// <summary>Every voice, in catalogue order.</summary>
    public static IReadOnlyList<string> Voices => _voices ??= Takes.Select(t => t.Voice).Distinct().ToArray();
    private static IReadOnlyList<string>? _voices;

    /// <summary>The voices that recorded anything in a category ("greet", "yell", "story"), in
    /// catalogue order. A driver's voice is one with yells; a pedestrian's is one with greetings.</summary>
    public static IReadOnlyList<string> VoicesWith(string category)
        => Takes.Where(t => t.Category == category).Select(t => t.Voice).Distinct().ToArray();

    /// <summary>A voice's lines in one category.</summary>
    public static IReadOnlyList<string> LinesOf(string voice, string category)
        => Takes.Where(t => t.Voice == voice && t.Category == category).Select(t => t.Line).ToArray();


    private static Dictionary<(string, string), Take>? _byVoiceLine;

    /// <summary>A voice's take of a line, or null if that voice never recorded it.</summary>
    public static Take? Find(string voice, string line)
    {
        _byVoiceLine ??= Takes.ToDictionary(t => (t.Voice, t.Line));
        return _byVoiceLine.TryGetValue((voice, line), out var t) ? t : null;
    }

    private static IReadOnlyList<Take> Load()
    {
        using var stream = typeof(Speech).Assembly.GetManifestResourceStream("OpenFPS.Common.Speech.voices.csv");
        if (stream == null) return Array.Empty<Take>();
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    /// <summary>Reads the catalogue: voice, kind, category, line, text, seconds. The text is quoted
    /// when it has a comma in it.</summary>
    public static IReadOnlyList<Take> Parse(string csv)
    {
        var takes = new List<Take>();
        foreach (var raw in csv.Split('\n').Skip(1))
        {
            string row = raw.TrimEnd('\r');
            if (row.Length == 0) continue;
            var f = SplitCsv(row);
            if (f.Count < 6) continue;
            if (!float.TryParse(f[5], NumberStyles.Float, CultureInfo.InvariantCulture, out float seconds)) continue;
            takes.Add(new Take(f[0], f[1], f[2], f[3], f[4], seconds));
        }
        return takes;
    }

    private static List<string> SplitCsv(string row)
    {
        var fields = new List<string>();
        var cur = new System.Text.StringBuilder();
        bool quoted = false;
        for (int i = 0; i < row.Length; i++)
        {
            char c = row[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < row.Length && row[i + 1] == '"') { cur.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cur.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { fields.Add(cur.ToString()); cur.Clear(); }
            else cur.Append(c);
        }
        fields.Add(cur.ToString());
        return fields;
    }
}

using System.Globalization;

namespace OpenFPS.Common;

/// <summary>
/// People talking: which voices exist, what each can say, and how loud a person says it.
///
/// The lines are recordings, one take per voice per line, listed in <c>Speech/voices.csv</c> (written
/// by <c>tools/import_npc_voices.py</c> from the manifest in approved/voices). The server picks who says what; the
/// client plays <c>VOICES/&lt;voice&gt;/&lt;line&gt;</c> through the ordinary world-sound path, so a greeting is
/// placed, occluded, reflected and reverberated like any other sound.
/// </summary>
public static class Speech
{
    // ── Vocal effort ────────────────────────────────────────────────────────────────────────────
    //
    // ANSI S3.5-1997, Table 3: the overall speech level one metre in front of a talker, for each vocal
    // effort. These are long-term levels, the speech's own RMS.

    /// <summary>Talking to someone beside you: the reference voice the levels are calibrated by.</summary>
    public const float NormalDb = Hearing.ReferenceVoice.NormalDb;
    /// <summary>Calling to someone a few metres off, or telling someone to watch it.</summary>
    public const float RaisedDb = 68.34f;
    /// <summary>Calling across a street.</summary>
    public const float LoudDb = 74.85f;
    /// <summary>Shouting: a driver yelling out of a window.</summary>
    public const float ShoutDb = 82.3f;

    /// <summary>
    /// Where a loaded line sits on average, dBFS RMS (<see cref="Hearing.ReferenceVoice.BufferRmsDbfs"/>):
    /// lines are brought to <see cref="BufferLoudnessLufs"/>, which for the shipped set is this within a
    /// decibel or so.
    /// </summary>
    public const float BufferRmsDbfs = Hearing.ReferenceVoice.BufferRmsDbfs;

    /// <summary>
    /// The loudness a line is brought to, LUFS (ITU-R BS.1770 K-weighted, ungated: a few seconds of
    /// speech). Matched by RMS, voices were up to 2.6 dB apart to the ear (2026-09-27: seanterry at -28.0
    /// LUFS, joel at -25.4). -27.2 is the set's median, so the ANSI levels in <see cref="LevelDb"/> keep
    /// their meaning.
    /// </summary>
    public const float BufferLoudnessLufs = -27.2f;

    /// <summary>
    /// K-weighted loudness of a 48 kHz mono buffer, LUFS, ungated. The two BS.1770-4 filters at their
    /// published 48 kHz coefficients: the head's high shelf (+4 dB above about 2 kHz) and the
    /// revised low-frequency B-curve (a high-pass at about 38 Hz).
    /// </summary>
    public static double LoudnessLufs(float[] pcm)
    {
        if (pcm.Length == 0) return double.NegativeInfinity;
        double sx1 = 0, sx2 = 0, sy1 = 0, sy2 = 0;     // the shelf
        double hy1 = 0, hy2 = 0;                        // the high-pass; its input is the shelf's output
        double sum = 0;
        foreach (float xf in pcm)
        {
            double x = xf;
            double y = 1.53512485958697 * x - 2.69169618940638 * sx1 + 1.19839281085285 * sx2
                     + 1.69065929318241 * sy1 - 0.73248077421585 * sy2;
            sx2 = sx1; sx1 = x;
            double z = y - 2.0 * sy1 + sy2 + 1.99004745483398 * hy1 - 0.99007225036621 * hy2;
            sy2 = sy1; sy1 = y;
            hy2 = hy1; hy1 = z;
            sum += z * z;
        }
        double ms = sum / pcm.Length;
        return ms > 0 ? -0.691 + 10.0 * Math.Log10(ms) : double.NegativeInfinity;
    }

    /// <summary>
    /// The level to send for a line said at a given effort: a world sound's level is its buffer's full
    /// scale at one metre, and a line at <see cref="BufferRmsDbfs"/> has its full scale that far above
    /// its speech level.
    /// </summary>
    public static float LevelDb(float effortDb) => Hearing.ReferenceVoice.LevelDb(effortDb);

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

    /// <summary>How far off a normal voice on a street is made out word for word, metres: a text player is
    /// told the words only inside it.</summary>
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

    private static Dictionary<(string, string), string[]>? _byVoiceCategory;

    /// <summary>A voice's lines in one category.</summary>
    public static IReadOnlyList<string> LinesOf(string voice, string category)
    {
        _byVoiceCategory ??= Takes.GroupBy(t => (t.Voice, t.Category)).ToDictionary(g => g.Key, g => g.Select(t => t.Line).ToArray());
        return _byVoiceCategory.TryGetValue((voice, category), out var l) ? l : Array.Empty<string>();
    }

    // ── Scripts: phone calls and two people walking together ────────────────────────────────────
    //
    // The order of a call's lines and a conversation's turns, exported from the line lists by
    // tools/export_speech_scripts.py into Speech/scripts.json. A call is one voice with pauses where
    // the far end talks; a conversation is two voices taking turns.

    /// <summary>One step of a call: a line, or a pause while the other end talks.</summary>
    public sealed record CallTurn(string? Line, float Pause);
    public sealed record Call(string Name, IReadOnlyDictionary<string, IReadOnlyList<CallTurn>> Voices);
    /// <summary>One turn of a conversation: who ("A" or "B"), and the line, or a cue with no
    /// recording yet ("laughs"); Cut when it interrupts the turn before.</summary>
    public sealed record Turn(string Who, string? Line, string? Cue, bool Cut);
    public sealed record Conversation(string Name, string A, string B, IReadOnlyList<Turn> Turns);

    private static (IReadOnlyList<Call> Calls, IReadOnlyList<Conversation> Pairs)? _scripts;

    public static IReadOnlyList<Call> Calls => (_scripts ??= LoadScripts()).Calls;
    public static IReadOnlyList<Conversation> Conversations => (_scripts ??= LoadScripts()).Pairs;

    /// <summary>The calls this voice recorded every line of.</summary>
    public static IReadOnlyList<IReadOnlyList<CallTurn>> CallsFor(string voice)
        => Calls.Where(c => c.Voices.ContainsKey(voice)).Select(c => c.Voices[voice]).ToArray();

    /// <summary>The pairs of voices that have a conversation between them, each once.</summary>
    public static IReadOnlyList<(string A, string B)> ConversationPairs
        => Conversations.Select(c => (c.A, c.B)).Distinct().ToArray();

    /// <summary>Conversations between these two voices, either way round.</summary>
    public static IReadOnlyList<Conversation> ConversationsBetween(string x, string y)
        => Conversations.Where(c => (c.A == x && c.B == y) || (c.A == y && c.B == x)).ToArray();

    private static (IReadOnlyList<Call>, IReadOnlyList<Conversation>) LoadScripts()
    {
        using var stream = typeof(Speech).Assembly.GetManifestResourceStream("OpenFPS.Common.Speech.scripts.json");
        if (stream == null) return (Array.Empty<Call>(), Array.Empty<Conversation>());
        using var doc = System.Text.Json.JsonDocument.Parse(stream);
        var calls = new List<Call>();
        foreach (var c in doc.RootElement.GetProperty("calls").EnumerateArray())
        {
            var voices = new Dictionary<string, IReadOnlyList<CallTurn>>();
            foreach (var v in c.GetProperty("voices").EnumerateObject())
                voices[v.Name] = v.Value.EnumerateArray().Select(t => t.TryGetProperty("line", out var l)
                    ? new CallTurn(l.GetString(), 0f) : new CallTurn(null, (float)t.GetProperty("pause").GetDouble())).ToArray();
            calls.Add(new Call(c.GetProperty("name").GetString()!, voices));
        }
        var pairs = new List<Conversation>();
        foreach (var c in doc.RootElement.GetProperty("pairs").EnumerateArray())
            pairs.Add(new Conversation(c.GetProperty("name").GetString()!, c.GetProperty("a").GetString()!, c.GetProperty("b").GetString()!,
                c.GetProperty("turns").EnumerateArray().Select(t => new Turn(t.GetProperty("who").GetString()!,
                    t.TryGetProperty("line", out var l) ? l.GetString() : null,
                    t.TryGetProperty("cue", out var q) ? q.GetString() : null,
                    t.TryGetProperty("cut", out _))).ToArray()));
        return (calls, pairs);
    }


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

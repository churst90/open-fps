using System.Text.RegularExpressions;
using OpenFPS.Environment;
using OpenFPS.Models;

namespace OpenFPS.Server.Systems;

/// <summary>Which of a homeless man's lines fit when.</summary>
public static class HomelessLines
{
    /// <summary>Of his idle lines, how many are an outburst at nobody.</summary>
    public const double OutburstChance = 0.1;

    /// <summary>Muttering is quieter than talking to someone: about 8 dB under a normal voice.</summary>
    public const float MutterDb = Speech.NormalDb - 8f;

    /// <summary>What he asks for: mostly money; somewhere to sleep only at night or in the cold and wet.</summary>
    public static string AskCategory(SpeechConditions c, Random rng)
    {
        bool shelter = AlexDay.Sheltering(c);
        var weights = new (string Cat, double W)[]
        {
            ("homeless_money", 45), ("homeless_smokes", 20), ("homeless_food", 15),
            ("homeless_weed", 10), ("homeless_drugs", 4), ("homeless_shelter", shelter ? 20 : 0),
        };
        double pick = rng.NextDouble() * weights.Sum(w => w.W);
        foreach (var (cat, w) in weights)
        {
            pick -= w;
            if (w > 0 && pick <= 0) return cat;
        }
        return "homeless_money";
    }

    /// <summary>What he says to nobody, and how loud.</summary>
    public static (string Category, float Db) Idle(SpeechConditions c, bool atBusStop, Random rng)
    {
        double r = rng.NextDouble();
        if (r < OutburstChance) return ("homeless_angry", Speech.RaisedDb);
        if (AlexDay.Sheltering(c) && r < 0.4) return ("homeless_shelter", Speech.NormalDb - 3f);
        if (atBusStop && r < 0.5) return ("waiting", MutterDb);
        if (r < 0.8) return ("homeless_mutter", MutterDb);
        return (r < 0.9 ? "mutter" : "think_aloud", MutterDb);
    }

    private static readonly Regex Always = new(
        @"\b(one|every|every single|last|worst|other) (day|days|night|morning|week)\b|\btwo days\b|\bhot dog\b|\byou're cold\b|\bnot today\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Words = new(
        @"\b(this morning|cold tonight|morning|afternoon|evening|tonight|night|noon|lunch|breakfast|dinner|day|today|days|tomorrow|yesterday|weekend|monday|tuesday|wednesday|thursday|friday|saturday|sunday|spring|summer|autumn|winter|weather|rain|raining|snow|snowing|cold|freezing|hot|heat|sun|sunny|storm|wind|windy|fog|foggy)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Today = new(@"\b(this morning|cold tonight|tonight|today)\b",
                                              RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Whether a story is true now: only what it says about today ("this morning", "tonight")
    /// is checked, since the rest of a story is in the past.</summary>
    public static bool StoryTrueNow(string text, SpeechConditions c) => TrueNow(text, c, Today);

    /// <summary>
    /// Whether every word in a line about the hour or the weather is true now. Phrases that are not about
    /// now ("not today", "every night", "a hot dog") are struck out first.
    /// </summary>
    public static bool TrueNow(string text, SpeechConditions c) => TrueNow(text, c, Words);

    private static bool TrueNow(string text, SpeechConditions c, Regex words)
    {
        string rest = Always.Replace(text, " ");
        foreach (Match m in words.Matches(rest))
        {
            float h = c.Hour;
            bool ok = m.Value.ToLowerInvariant() switch
            {
                "this morning" => h >= 7f && h < 20f,
                "cold tonight" => h >= 12f && c.TemperatureC <= 15f,
                "morning" => StreetLines.IsMorning(h),
                "afternoon" => h >= 12f && h < 18f,
                "evening" => h >= 16f && h < 23f,
                "tonight" => h >= 14f || h < 3f,
                "night" => h >= 18f || h < 5f,
                "noon" or "lunch" => h >= 11f && h < 15f,
                "breakfast" => h >= 5f && h < 11f,
                "dinner" => h >= 16f && h < 23f,
                "day" or "today" => h >= 6f && h < 22f,
                "days" or "tomorrow" or "yesterday" or "weekend" or "weather" => true,
                "rain" or "raining" => c.Weather is WeatherType.Rain or WeatherType.Storm,
                "snow" or "snowing" => c.Weather == WeatherType.Snow,
                "storm" => c.Weather == WeatherType.Storm,
                "cold" or "freezing" => c.TemperatureC <= 12f,
                "hot" or "heat" => c.TemperatureC >= 26f,
                "sun" or "sunny" => StreetLines.IsFine(c),
                _ => false,
            };
            if (!ok) return false;
        }
        return true;
    }
}

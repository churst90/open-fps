using System;
using System.Globalization;
using OpenFPS.Common;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Systems;

namespace OpenFPS.Server.Core;

/// <summary>
/// /weather for what falls, as a forecaster or a radar would say it:
///
///     /weather drizzle
///     /weather rain light|moderate|heavy|extreme     (or drizzle; violent is extreme)
///     /weather rain 12                               millimetres an hour
///     /weather rain 45 dBZ                           what the radar shows; the rate by Z = 300 R^1.4
///     /weather rain heavy drops 3 mm                 the median drop, separately from the rate
///     /weather freezing rain [same as rain]
///     /weather sleet [light|moderate|heavy|rate]
///     /weather snow light|moderate|heavy|rate        (water equivalent, mm/h)
///     /weather hail pea|marble|quarter|golf|baseball|N mm   with the heavy rain it falls in
///
/// Plain /weather rain, snow and storm are the fronts, as before. The answer is one short sentence,
/// with the colour it would be on the radar.
/// </summary>
public partial class CommandHandler
{
    private const string PrecipitationUsage =
        "Say light, moderate, heavy or extreme, a rate in millimetres an hour, or dBZ, and drops then a size in millimetres.";

    /// <summary>Handles the forms above; false when the words are not one of them.</summary>
    private bool TryHandlePrecipitation(string first, string[] args, WorldEnvironmentSystem env, Action<IMessage> reply)
    {
        PrecipitationKind kind;
        int from = 1;
        switch (first)
        {
            case "drizzle":
                kind = PrecipitationKind.Rain; from = 0; break;
            case "rain" when args.Length > 1:
                kind = PrecipitationKind.Rain; break;
            case "freezing":
            case "freezingrain":
                kind = PrecipitationKind.FreezingRain;
                if (args.Length > 1 && args[1].Equals("rain", StringComparison.OrdinalIgnoreCase)) from = 2;
                break;
            case "sleet":
                kind = PrecipitationKind.Sleet; break;
            case "snow" when args.Length > 1:
                kind = PrecipitationKind.Snow; break;
            case "hail":
                kind = PrecipitationKind.Hail; break;
            default:
                return false;
        }

        if (!TryReadPrecipitation(kind, args.AsSpan(from), out var p, out string? error))
        {
            Say(reply, error ?? PrecipitationUsage);
            return true;
        }
        env.PinPrecipitation(p);
        _server.BroadcastEnvironment();
        Say(reply, DescribePrecipitation(env.HeldPrecipitation ?? p));
        return true;
    }

    /// <summary>Reads a rate, a class, a reflectivity, a drop size or a hailstone from the words.</summary>
    public static bool TryReadPrecipitation(PrecipitationKind kind, ReadOnlySpan<string> args, out Precipitation p, out string? error)
    {
        error = null;
        float rate = kind switch
        {
            PrecipitationKind.Snow => SnowModerate,
            PrecipitationKind.Sleet => SleetModerate,
            PrecipitationKind.Hail => Hydrometeors.HailRainRate,
            _ => Rainfall.ModerateRate,
        };
        float median = 0f, hail = kind == PrecipitationKind.Hail ? 13f : 0f;
        float? dbz = null;
        var words = new System.Collections.Generic.List<string>();
        foreach (var a in args) words.Add(a.ToLowerInvariant().Replace(",", ""));
        for (int i = 0; i < words.Count; i++)
        {
            string w = words[i];
            if (w is "drizzle" && kind is PrecipitationKind.Rain or PrecipitationKind.FreezingRain)
            {
                Hydrometeors.TryParseRainClass(w, out rate, out median);
                continue;
            }
            if (w is "drop" or "drops" or "size")
            {
                if (i + 1 >= words.Count || !TryNumber(words[i + 1].Replace("mm", ""), out median) || median < 0.1f || median > 5f)
                {
                    error = "Say drops then a median size from 0.1 to 5 millimetres.";
                    p = default; return false;
                }
                i++;
                if (i + 1 < words.Count && words[i + 1] is "mm" or "millimetres" or "millimeters") i++;
                continue;
            }
            if (kind == PrecipitationKind.Hail && Hydrometeors.TryParseHail(w, out float stone))
            {
                hail = stone;
                if (i + 1 < words.Count && words[i + 1] is "mm" or "ball" or "size" or "sized") i++;
                continue;
            }
            float snow = 0f;
            if ((kind == PrecipitationKind.Snow && TrySnowClass(w, out snow))
                || (kind == PrecipitationKind.Sleet && TrySnowClass(w, out snow, sleet: true)))
            {
                rate = snow;
                continue;
            }
            if (kind is PrecipitationKind.Rain or PrecipitationKind.FreezingRain && Hydrometeors.TryParseRainClass(w, out float r, out float m))
            {
                rate = r; median = m;
                continue;
            }
            bool saysDbz = w.EndsWith("dbz", StringComparison.Ordinal) || (i + 1 < words.Count && words[i + 1] == "dbz");
            if (TryNumber(w.Replace("dbz", "").Replace("mm/h", "").Replace("mm", ""), out float n))
            {
                if (saysDbz)
                {
                    if (n < 5f || n > 70f) { error = "Say a reflectivity from 5 to 70 dBZ."; p = default; return false; }
                    dbz = n;
                    if (!w.EndsWith("dbz", StringComparison.Ordinal)) i++;
                }
                else
                {
                    if (n <= 0f || n > Rainfall.FullIntensityRate) { error = "Say a rate up to 60 millimetres an hour."; p = default; return false; }
                    rate = n;
                    if (i + 1 < words.Count && words[i + 1] is "mm/h" or "mm" or "millimetres") i++;
                }
                continue;
            }
            error = $"I do not know {w}. {PrecipitationUsage}";
            p = default; return false;
        }
        if (dbz is float z)
        {
            // What the radar says: the rate by the WSR-88D's relation, and the drops the size that,
            // at that rate, reflect that much (unless a size was given too, which then stands).
            rate = Math.Min(Rainfall.FullIntensityRate, Hydrometeors.RateFromDbz(z));
            if (median <= 0f) median = Hydrometeors.MedianForDbz(kind, rate, z);
        }
        p = new Precipitation(kind, rate, median, hail);
        return true;
    }

    /// <summary>Snow rates as water, mm/h: light under 1 mm of water an hour, heavy over 2.5 (NWS
    /// snowfall intensity, about 1 cm of snow an hour per millimetre of water).</summary>
    private const float SnowLight = 0.5f, SnowModerate = 1.5f, SnowHeavy = 4f;
    private const float SleetLight = 1f, SleetModerate = 3f, SleetHeavy = 8f;

    private static bool TrySnowClass(string w, out float rate, bool sleet = false)
    {
        rate = w switch
        {
            "light" => sleet ? SleetLight : SnowLight,
            "moderate" => sleet ? SleetModerate : SnowModerate,
            "heavy" => sleet ? SleetHeavy : SnowHeavy,
            _ => 0f,
        };
        return rate > 0f;
    }

    private static bool TryNumber(string s, out float n)
        => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out n) && float.IsFinite(n);

    /// <summary>"Rain, heavy, 25 millimetres an hour, drops 1.8 millimetres, orange on the radar."</summary>
    public static string DescribePrecipitation(Precipitation p)
    {
        var spectrum = new ParticleSpectrum();
        var rain = p with { Kind = p.Kind == PrecipitationKind.Hail ? PrecipitationKind.Rain : p.Kind };
        spectrum.Build(rain);
        float dbz = spectrum.Dbz;
        string rate = p.RateMmPerHour.ToString("0.#", CultureInfo.InvariantCulture);
        string drops = p.EffectiveMedianMm.ToString("0.#", CultureInfo.InvariantCulture);
        string colour = Hydrometeors.RadarColour(dbz, p.Kind);
        switch (p.Kind)
        {
            case PrecipitationKind.Snow:
                return $"Snow, {rate} millimetres of water an hour, {colour} on the radar.";
            case PrecipitationKind.Sleet:
                return $"Sleet, {rate} millimetres an hour, ice pellets {drops} millimetres.";
            case PrecipitationKind.Hail:
                var stones = new ParticleSpectrum();
                stones.Build(p);
                float total = 10f * MathF.Log10(MathF.Pow(10f, dbz / 10f) + MathF.Pow(10f, stones.Dbz / 10f));
                return $"Hail, {p.HailMm.ToString("0", CultureInfo.InvariantCulture)} millimetres, with heavy rain, {Hydrometeors.RadarColour(total, p.Kind)} on the radar.";
        }
        string word = p.EffectiveMedianMm < 0.5f && p.RateMmPerHour < 1f ? "drizzle" : Rainfall.Word(Rainfall.Category(p.RateMmPerHour));
        string what = p.Kind == PrecipitationKind.FreezingRain ? "Freezing rain" : "Rain";
        if (word == "drizzle") what = p.Kind == PrecipitationKind.FreezingRain ? "Freezing drizzle" : "Drizzle";
        else what += ", " + (word == "violent" ? "extreme" : word);
        return $"{what}, {rate} millimetres an hour, drops {drops} millimetres, {colour} on the radar.";
    }
}

using System;
using System.Globalization;
using System.Linq;

namespace OpenFPS.Common;

/// <summary>
/// A horn being sounded: WHICH horn, and the rhythm of the hand on it.
///
/// It travels as a <see cref="TransientSound.SynthKey"/> on the one channel every short sound in the
/// world already uses, with the vehicle as the event's source. The rhythm is sent rather than a
/// sound because the client plays it ON the vehicle — a one-second blast from a car doing fifty is
/// fourteen metres of road, and a sound left behind where the button was pressed is a horn in the
/// wrong place. So the server decides that somebody honked and how; the vehicle's own horn does the
/// rest, and it Dopplers, occludes and moves with the car like everything else on it.
///
/// The pattern is alternating seconds: on, off, on, off... A tap is one number.
/// </summary>
public static class Honk
{
    public const string Prefix = "horn:";

    /// <summary>The key for one honk. <paramref name="horn"/> is "electric:disc_pair" or
    /// "air:truck_dual" — see <see cref="VehicleProfile.HornFor"/>.</summary>
    public static string Key(string horn, float[] pattern)
        => Prefix + horn + ":" + string.Join(",", pattern.Select(p => p.ToString("0.###", CultureInfo.InvariantCulture)));

    public static bool TryParse(string? key, out string horn, out float[] pattern)
    {
        horn = ""; pattern = Array.Empty<float>();
        if (key == null || !key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return false;
        string body = key[Prefix.Length..];
        int cut = body.LastIndexOf(':');
        if (cut <= 0) return false;
        var parts = body[(cut + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries);
        var values = new float[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i])) return false;
            values[i] = Math.Clamp(values[i], 0f, 20f);
        }
        if (values.Length == 0) return false;
        // Filled in only on success: a key with a good horn and a bad rhythm must not leave the
        // horn behind for a caller that forgot to check.
        horn = body[..cut];
        pattern = values;
        return true;
    }

    /// <summary>
    /// How loud a horn is at one metre while it is blowing, dB SPL — from its own model's declared
    /// level, so the server's earshot and the client's voice agree on it.
    /// </summary>
    public static float LevelDb(string horn)
    {
        int colon = horn.IndexOf(':');
        string kind = colon > 0 ? horn[..colon] : "";
        string preset = colon > 0 ? horn[(colon + 1)..] : horn;
        try
        {
            return kind.ToLowerInvariant() switch
            {
                "air" => ChimeHornSpec.ByName(preset).ReferenceDb,
                "electric" => ElectricHornSpec.ByName(preset).ReferenceDb,
                _ => 110f,
            };
        }
        catch (ArgumentException) { return 110f; }
    }

    /// <summary>How long the whole pattern lasts, seconds.</summary>
    public static float Duration(float[] pattern) => pattern.Sum();

    /// <summary>Whether the hand is on the horn this far into the pattern.</summary>
    public static bool BlowingAt(float[] pattern, float seconds)
    {
        if (seconds < 0f) return false;
        float t = 0f;
        for (int i = 0; i < pattern.Length; i++)
        {
            t += pattern[i];
            if (seconds < t) return i % 2 == 0;
        }
        return false;
    }

    /// <summary>
    /// What an ordinary driver does with a horn, most often to least. A tap — somebody saying hello,
    /// or "the light is green". Two quick ones. And now and then a proper lean on it.
    /// </summary>
    public static float[] Everyday(Random rng)
    {
        double r = rng.NextDouble();
        float Jit(float s) => s * (0.8f + 0.4f * (float)rng.NextDouble());
        if (r < 0.55) return new[] { Jit(0.16f) };
        if (r < 0.85) return new[] { Jit(0.14f), Jit(0.12f), Jit(0.18f) };
        return new[] { 0.6f + 0.8f * (float)rng.NextDouble() };
    }

    /// <summary>
    /// What a driver does after somebody made them stand on the brakes: a long one, or a long one
    /// with a short one after it for emphasis. Never a polite tap.
    /// </summary>
    public static float[] Startled(Random rng)
        => rng.NextDouble() < 0.6
            ? new[] { 0.8f + 0.7f * (float)rng.NextDouble() }
            : new[] { 0.5f + 0.3f * (float)rng.NextDouble(), 0.15f, 0.25f };

    /// <summary>
    /// A train approaching a level crossing: long, long, short, long (FRA 49 CFR 222.21), the last
    /// held until the crossing is reached. Durations in seconds; the last is extended by the caller
    /// to reach the crossing.
    /// </summary>
    public static float[] Crossing(float lastLongSeconds)
        => new[] { 3.0f, 1.0f, 3.0f, 1.0f, 1.0f, 1.0f, Math.Max(3f, lastLongSeconds) };
}

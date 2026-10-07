using System.Globalization;

namespace OpenFPS.Common;

/// <summary>
/// A horn being sounded: which horn, and the rhythm of the hand on it, as a
/// <see cref="TransientSound.SynthKey"/> with the vehicle as source. The rhythm is sent, not a sound,
/// so the client plays it on the moving vehicle: a one-second blast at 50 km/h is fourteen metres of
/// road. The pattern is alternating seconds on, off, on...; a tap is one number.
/// </summary>
public static class Honk
{
    public const string Prefix = "horn:";

    /// <summary>The key for one honk. <paramref name="horn"/> is "electric:disc_pair" or
    /// "air:truck_dual" — see <see cref="VehicleProfile.HornFor"/>.</summary>
    public static string Key(string horn, float[] pattern)
        => Prefix + horn + ":" + string.Join(",", pattern.Select(p => p.ToString("0.###", CultureInfo.InvariantCulture)));

    /// <summary>The word that stands for a rhythm in a held horn's key: blowing for as long as the
    /// hand is on it, which only the voice's Running flag knows.</summary>
    public const string HoldWord = "hold";

    /// <summary>The key for a horn held down: one endless blast (<see cref="Held"/>); the voice lets go
    /// when it is told to stop, so the horn's own valve or relay ends the note.</summary>
    public static string HoldKey(string horn) => Prefix + horn + ":" + HoldWord;

    /// <summary>The pattern a held horn parses to: one blast with no end.</summary>
    public static bool Held(float[] pattern) => pattern.Length == 1 && float.IsPositiveInfinity(pattern[0]);

    public static bool TryParse(string? key, out string horn, out float[] pattern)
    {
        horn = ""; pattern = Array.Empty<float>();
        if (key == null || !key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return false;
        string body = key[Prefix.Length..];
        int cut = body.LastIndexOf(':');
        if (cut <= 0) return false;
        if (string.Equals(body[(cut + 1)..], HoldWord, StringComparison.OrdinalIgnoreCase))
        {
            horn = body[..cut];
            pattern = new[] { float.PositiveInfinity };
            return true;
        }
        var parts = body[(cut + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries);
        var values = new float[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i])) return false;
            values[i] = Math.Clamp(values[i], 0f, 20f);
        }
        if (values.Length == 0) return false;
        // Only on success: a bad rhythm must not leave the horn set for a caller that did not check.
        horn = body[..cut];
        pattern = values;
        return true;
    }

    /// <summary>A horn's level at one metre while blowing, dB SPL, from its model, so the server's
    /// earshot and the client's voice agree.</summary>
    public static float LevelDb(string horn)
    {
        int colon = horn.IndexOf(':');
        string kind = colon > 0 ? horn[..colon] : "";
        string preset = colon > 0 ? horn[(colon + 1)..] : horn;
        try
        {
            return kind.ToLowerInvariant() switch
            {
                "air" => ModelLibrary.Horn(preset).ReferenceDb,
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

    /// <summary>What an ordinary driver does with a horn, most often to least: a tap, two quick ones,
    /// now and then a lean on it.</summary>
    public static float[] Everyday(Random rng)
    {
        double r = rng.NextDouble();
        float Jit(float s) => s * (0.8f + 0.4f * (float)rng.NextDouble());
        if (r < 0.55) return new[] { Jit(0.16f) };
        if (r < 0.85) return new[] { Jit(0.14f), Jit(0.12f), Jit(0.18f) };
        return new[] { 0.6f + 0.8f * (float)rng.NextDouble() };
    }

    /// <summary>After somebody made the driver stand on the brakes: a long one, or a long and a short.</summary>
    public static float[] Startled(Random rng)
        => rng.NextDouble() < 0.6
            ? new[] { 0.8f + 0.7f * (float)rng.NextDouble() }
            : new[] { 0.5f + 0.3f * (float)rng.NextDouble(), 0.15f, 0.25f };

    /// <summary>A train approaching a level crossing: long, long, short, long (FRA 49 CFR 222.21),
    /// seconds; the caller stretches the last to reach the crossing.</summary>
    public static float[] Crossing(float lastLongSeconds)
        => new[] { 3.0f, 1.0f, 3.0f, 1.0f, 1.0f, 1.0f, Math.Max(3f, lastLongSeconds) };
}

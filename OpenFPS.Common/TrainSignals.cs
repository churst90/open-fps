using System.Collections.Generic;
using System.Globalization;

namespace OpenFPS.Common;

/// <summary>
/// A train sounding its own horn (or whistle) and ringing its own bell: which train, the rhythm on the
/// horn valve, and how long the bell rings. Sent like <see cref="Honk"/>, as a
/// <see cref="TransientSound.SynthKey"/>; the client plays it on the train's own horn, whistle and bell
/// sources (<see cref="TrainLayout"/>). The key names the train (<c>preset/train</c>, as the sources'
/// SoundIds do), because the synth is the train's, not any one entity's.
/// </summary>
public static class TrainSignal
{
    public const string Prefix = "railsignal:";

    /// <summary>"railsignal:&lt;preset&gt;/&lt;train&gt;:&lt;rhythm&gt;:&lt;bell seconds&gt;". The rhythm is
    /// alternating seconds, on, off, on... as Honk's.</summary>
    public static string Key(string preset, string train, float[] warning, float bellSeconds)
        => Prefix + preset + "/" + train + ":"
         + string.Join(",", warning.Select(p => p.ToString("0.###", CultureInfo.InvariantCulture))) + ":"
         + MathF.Max(0f, bellSeconds).ToString("0.###", CultureInfo.InvariantCulture);

    public static bool TryParse(string? key, out string train, out float[] warning, out float bellSeconds)
    {
        train = ""; warning = Array.Empty<float>(); bellSeconds = 0f;
        if (key == null || !key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var parts = key[Prefix.Length..].Split(':');
        if (parts.Length != 3 || parts[0].IndexOf('/') <= 0) return false;
        if (!float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float bell)) return false;
        var beats = parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries);
        var values = new float[beats.Length];
        for (int i = 0; i < beats.Length; i++)
        {
            if (!float.TryParse(beats[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i])) return false;
            values[i] = Math.Clamp(values[i], 0f, 60f);
        }
        train = parts[0];
        warning = values;
        bellSeconds = Math.Clamp(bell, 0f, 120f);
        return true;
    }

    /// <summary>How long the whole signal lasts, seconds: the horn's rhythm or the bell, whichever is longer.</summary>
    public static float Duration(float[] warning, float bellSeconds) => MathF.Max(Honk.Duration(warning), bellSeconds);

    /// <summary>
    /// What a train sounds for a level crossing <paramref name="etaSeconds"/> ahead: long, long, short,
    /// long on the horn, the last held until the lead reaches the crossing (49 CFR 222.21), and the bell
    /// rung from the first blast until the crossing is occupied (GCOR 5.8.1).
    /// </summary>
    public static (float[] Warning, float BellSeconds) ForCrossing(float etaSeconds)
        => (Honk.Crossing(etaSeconds - 10f), MathF.Max(0f, etaSeconds));

    /// <summary>The source that sounds a train's warning: its horn, or a steam engine's whistle; -1 for a
    /// train with neither.</summary>
    public static int WarningSource(IReadOnlyList<TrainLayout.Entry> layout)
    {
        int whistle = -1;
        foreach (var e in layout)
        {
            if (e.Kind == TrainLayout.Kind.Horn) return e.Index;
            if (e.Kind == TrainLayout.Kind.Whistle && whistle < 0) whistle = e.Index;
        }
        return whistle;
    }

    /// <summary>The source that is a train's bell, or -1.</summary>
    public static int BellSource(IReadOnlyList<TrainLayout.Entry> layout)
    {
        foreach (var e in layout) if (e.Kind == TrainLayout.Kind.Bell) return e.Index;
        return -1;
    }
}

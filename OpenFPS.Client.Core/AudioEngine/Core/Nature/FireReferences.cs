using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenFPS.Client.AudioEngine.Core.Nature;

/// <summary>
/// The recorded fires the fire model is held to (docs/FIRE.md section 8): the texture statistics
/// (TextureStatistics, McDermott and Simoncelli 2011) of each recording in 20 s pieces, measured with
/// tools/texture_stats.py, by kind. The recordings are in ~/openfps-scratch-archive/fire-2026-10-06/refs
/// with SOURCES.txt; they are yardsticks and are never played or shipped, so their numbers are written
/// here for the tests.
/// </summary>
public static class FireReferences
{
    /// <summary>The fourteen statistics the nature models are fitted on.</summary>
    public static string[] FittedKeys => ShoreReferences.FittedKeys;

    /// <summary>The kind of recording each preset is held to.</summary>
    public static string KindFor(string preset) => preset.ToLowerInvariant() switch
    {
        "campfire" or "fire_pit" => "campfire",
        "bonfire" => "bonfire",
        "burning_car" => "car",
        "house_fire" => "house",
        "burning_trees" => "trees",
        "crown_fire" => "wildfire",
        _ => "campfire",
    };

    public static bool Has(string kind) => Rows.TryGetValue(kind, out var r) && r.Length > 0;

    public static IReadOnlyList<(string File, IReadOnlyDictionary<string, double> Summary)> References(string kind)
        => Rows[kind].Select(r => (r.File, (IReadOnlyDictionary<string, double>)TextureStatistics.Keys
                                             .Select((k, i) => (k, r.Values[i])).ToDictionary(p => p.k, p => p.Item2))).ToList();

    /// <summary>The recordings' range of one statistic.</summary>
    public static (double Min, double Max) Range(string kind, string key)
    {
        var rows = References(kind);
        return (rows.Min(r => r.Summary[key]), rows.Max(r => r.Summary[key]));
    }

    /// <summary>The 10 ms 4-16 kHz waveform kurtosis of each kind's recordings: lowest and highest.</summary>
    public static readonly Dictionary<string, (double Min, double Max)> WaveformKurtosis = new();

    /// <summary>Each kind's recordings' octave levels 63 Hz-16 kHz re the 1 kHz octave: lowest and highest.</summary>
    public static readonly Dictionary<string, (double[] Min, double[] Max)> Octaves = new();

    private static readonly Dictionary<string, (string File, double[] Values)[]> Rows = new();
}

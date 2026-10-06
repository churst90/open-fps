using System;
using System.Collections.Generic;
using System.Globalization;

namespace OpenFPS.Client.AudioEngine.Core.Nature;

/// <summary>TEMPORARY, for the texture fit: OPENFPS_TUNE="name=value,..." overrides a constant in the lab.
/// Removed before the round is merged.</summary>
internal static class FitTune
{
    private static readonly Dictionary<string, float> Values = Parse();

    private static Dictionary<string, float> Parse()
    {
        var d = new Dictionary<string, float>();
        var s = Environment.GetEnvironmentVariable("OPENFPS_TUNE");
        if (string.IsNullOrEmpty(s)) return d;
        foreach (var part in s.Split(','))
        {
            var kv = part.Split('=');
            if (kv.Length == 2 && float.TryParse(kv[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float v)) d[kv[0]] = v;
        }
        return d;
    }

    public static float T(string name, float fallback) => Values.TryGetValue(name, out float v) ? v : fallback;
}

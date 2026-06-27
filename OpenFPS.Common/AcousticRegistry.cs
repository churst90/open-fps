using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace OpenFPS.Common;

public struct MaterialProperties
{
    public float Absorption { get; set; }
    public float AbsorptionLow { get; set; }
    public float AbsorptionMid { get; set; }
    public float AbsorptionHigh { get; set; }
    public float Scattering { get; set; }
    public float TransmissionLow { get; set; }
    public float TransmissionMid { get; set; }
    public float TransmissionHigh { get; set; }
    public int ResonanceIndex { get; set; }
}

/// <summary>JSON override DTO — every field nullable so omitted fields don't clobber the
/// hardcoded frequency-band defaults during the merge.</summary>
internal sealed class MaterialOverride
{
    public float? Absorption { get; set; }
    public float? AbsorptionLow { get; set; }
    public float? AbsorptionMid { get; set; }
    public float? AbsorptionHigh { get; set; }
    public float? Scattering { get; set; }
    public float? TransmissionLow { get; set; }
    public float? TransmissionMid { get; set; }
    public float? TransmissionHigh { get; set; }
    public int? ResonanceIndex { get; set; }
}

public static class AcousticRegistry
{
    private static Dictionary<string, MaterialProperties> _registry = new(System.StringComparer.OrdinalIgnoreCase);

    public static void Initialize()
    {
        // HARDCODED REALISM CONSTANTS: Ensuring these are ALWAYS used regardless of external JSON
        _registry["Generic"] = new MaterialProperties { Absorption = 0.2f, AbsorptionLow = 0.1f, AbsorptionMid = 0.2f, AbsorptionHigh = 0.3f, Scattering = 0.2f, TransmissionLow = 0.4f, TransmissionMid = 0.3f, TransmissionHigh = 0.2f, ResonanceIndex = 22 };
        _registry["Wood"] = new MaterialProperties { Absorption = 0.15f, AbsorptionLow = 0.1f, AbsorptionMid = 0.15f, AbsorptionHigh = 0.2f, Scattering = 0.4f, TransmissionLow = 0.6f, TransmissionMid = 0.4f, TransmissionHigh = 0.2f, ResonanceIndex = 21 };
        _registry["Metal"] = new MaterialProperties { Absorption = 0.05f, AbsorptionLow = 0.05f, AbsorptionMid = 0.05f, AbsorptionHigh = 0.1f, Scattering = 0.1f, TransmissionLow = 0.1f, TransmissionMid = 0.05f, TransmissionHigh = 0.02f, ResonanceIndex = 13 };
        _registry["Concrete"] = new MaterialProperties { Absorption = 0.02f, AbsorptionLow = 0.01f, AbsorptionMid = 0.02f, AbsorptionHigh = 0.02f, Scattering = 0.1f, TransmissionLow = 0.05f, TransmissionMid = 0.02f, TransmissionHigh = 0.01f, ResonanceIndex = 18 };
        _registry["Marble"] = new MaterialProperties { Absorption = 0.01f, AbsorptionLow = 0.01f, AbsorptionMid = 0.01f, AbsorptionHigh = 0.01f, Scattering = 0.05f, TransmissionLow = 0.05f, TransmissionMid = 0.02f, TransmissionHigh = 0.01f, ResonanceIndex = 12 };
        _registry["Carpet"] = new MaterialProperties { Absorption = 0.60f, AbsorptionLow = 0.15f, AbsorptionMid = 0.5f, AbsorptionHigh = 0.75f, Scattering = 0.6f, TransmissionLow = 0.1f, TransmissionMid = 0.05f, TransmissionHigh = 0.01f, ResonanceIndex = 6 };
        _registry["Glass"] = new MaterialProperties { Absorption = 0.05f, AbsorptionLow = 0.05f, AbsorptionMid = 0.05f, AbsorptionHigh = 0.05f, Scattering = 0.05f, TransmissionLow = 0.7f, TransmissionMid = 0.5f, TransmissionHigh = 0.3f, ResonanceIndex = 3 };
        _registry["None"] = new MaterialProperties { Absorption = 0.0f, AbsorptionLow = 0.0f, AbsorptionMid = 0.0f, AbsorptionHigh = 0.0f, Scattering = 0.0f, TransmissionLow = 1.0f, TransmissionMid = 1.0f, TransmissionHigh = 1.0f, ResonanceIndex = 0 };
        _registry["Plastic"] = new MaterialProperties { Absorption = 0.1f, AbsorptionLow = 0.05f, AbsorptionMid = 0.1f, AbsorptionHigh = 0.2f, Scattering = 0.2f, TransmissionLow = 0.5f, TransmissionMid = 0.4f, TransmissionHigh = 0.2f, ResonanceIndex = 15 };
        _registry["Grass"] = new MaterialProperties { Absorption = 0.75f, AbsorptionLow = 0.5f, AbsorptionMid = 0.7f, AbsorptionHigh = 0.9f, Scattering = 0.9f, TransmissionLow = 0.4f, TransmissionMid = 0.6f, TransmissionHigh = 0.8f, ResonanceIndex = 2 };
        _registry["Dirt"] = new MaterialProperties { Absorption = 0.60f, AbsorptionLow = 0.4f, AbsorptionMid = 0.5f, AbsorptionHigh = 0.6f, Scattering = 0.8f, TransmissionLow = 0.3f, TransmissionMid = 0.4f, TransmissionHigh = 0.5f, ResonanceIndex = 4 };

        string path = "materials.json";
        if (File.Exists(path))
        {
            try {
                string json = File.ReadAllText(path);
                // MERGE, don't replace: materials.json typically carries only a subset of fields
                // (Absorption/Scattering/ResonanceIndex). Deserializing into the full struct and
                // overwriting would zero the frequency bands (Transmission*/Absorption{Low,Mid,High}),
                // collapsing occlusion EQ and wall transmission. Start from the hardcoded entry and
                // apply only the fields the JSON actually specifies.
                var loaded = JsonSerializer.Deserialize<Dictionary<string, MaterialOverride>>(json);
                if (loaded != null) {
                    foreach (var kvp in loaded) {
                        var p = _registry.TryGetValue(kvp.Key, out var existing) ? existing : _registry["Generic"];
                        var o = kvp.Value;
                        if (o.Absorption.HasValue) p.Absorption = o.Absorption.Value;
                        if (o.AbsorptionLow.HasValue) p.AbsorptionLow = o.AbsorptionLow.Value;
                        if (o.AbsorptionMid.HasValue) p.AbsorptionMid = o.AbsorptionMid.Value;
                        if (o.AbsorptionHigh.HasValue) p.AbsorptionHigh = o.AbsorptionHigh.Value;
                        if (o.Scattering.HasValue) p.Scattering = o.Scattering.Value;
                        if (o.TransmissionLow.HasValue) p.TransmissionLow = o.TransmissionLow.Value;
                        if (o.TransmissionMid.HasValue) p.TransmissionMid = o.TransmissionMid.Value;
                        if (o.TransmissionHigh.HasValue) p.TransmissionHigh = o.TransmissionHigh.Value;
                        if (o.ResonanceIndex.HasValue) p.ResonanceIndex = o.ResonanceIndex.Value;
                        _registry[kvp.Key] = p;
                    }
                }
            } catch {}
        }

        // Startup assertion: detect any ResonanceIndex collisions introduced by materials.json overrides.
        var seen = new Dictionary<int, string>();
        foreach (var kvp in _registry)
        {
            int idx = kvp.Value.ResonanceIndex;
            if (idx == 0) continue; // 0 is "None" wildcard — collisions allowed
            if (seen.TryGetValue(idx, out string? existing))
                System.Console.WriteLine($"[ERROR] AcousticRegistry: ResonanceIndex collision! '{existing}' and '{kvp.Key}' both use index {idx}.");
            else
                seen[idx] = kvp.Key;
        }
    }

    public static MaterialProperties GetProperties(string type)
    {
        if (string.IsNullOrEmpty(type)) return _registry["Generic"];
        if (_registry.TryGetValue(type, out var props)) return props;
        System.Console.WriteLine($"[WARNING] AcousticRegistry: Material '{type}' not found, falling back to 'Generic'.");
        return _registry["Generic"];
    }

    public static MaterialProperties GetPropertiesByResonanceIndex(int index)
    {
        foreach (var props in _registry.Values)
        {
            if (props.ResonanceIndex == index) return props;
        }
        return _registry["Generic"];
    }
}

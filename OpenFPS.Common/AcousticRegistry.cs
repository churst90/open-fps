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

    // -- What the stuff IS, as opposed to how it treats sound arriving at it ----------------------
    //
    // Everything above describes a material as a SURFACE: what it absorbs, what it lets through.
    // That is enough for a wall between you and a noise, and not nearly enough for a wall that IS
    // the noise. A panel struck by a door latch, by a hailstone, or by another car rings at its own
    // modes, and where those modes are is a matter of how stiff and how heavy the panel is.
    //
    // Two numbers do it. A flat panel's fundamental goes as sqrt(E / rho), times its thickness over
    // its span squared - so steel rings high and hard, glass higher still because it is stiff for
    // its weight, and a carpet does not ring at all. They live here rather than in any one caller
    // because a door panel, a windscreen in hail and two cars meeting are the same calculation asked
    // three times.

    /// <summary>Density, kg/m^3.</summary>
    public float DensityKgM3 { get; set; }

    /// <summary>Young's modulus, GPa. With density, this is the note it rings at.</summary>
    public float YoungsModulusGPa { get; set; }

    /// <summary>
    /// How fast that ring dies away: roughly the fraction of energy lost per cycle.
    ///
    /// NOT the same number as <see cref="Absorption"/>, which is about sound ARRIVING at the surface
    /// out of the air. This is internal damping - a struck bell and a struck lump of putty differ
    /// here by orders of magnitude and absorb airborne sound about the same.
    /// </summary>
    public float LossFactor { get; set; }
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
    public float? DensityKgM3 { get; set; }
    public float? YoungsModulusGPa { get; set; }
    public float? LossFactor { get; set; }
}

public static class AcousticRegistry
{
    // Volatile + build-then-swap: the registry is read from many threads (game loop, audio worker, FMOD
    // audio thread) while Initialize() may run on any of them. We build a fresh dictionary and atomically
    // publish it, and serialize writers with a lock, so readers never observe a dictionary mid-mutation
    // (which throws "operations that change non-concurrent collections must have exclusive access").
    private static volatile Dictionary<string, MaterialProperties> _registry = new(System.StringComparer.OrdinalIgnoreCase);
    private static readonly object _initLock = new();

    public static void Initialize()
    {
        // Build into a LOCAL dictionary, then publish it atomically (see _registry note). Never mutate the
        // currently-published dictionary in place — other threads may be reading it.
        lock (_initLock)
        {
            var reg = new Dictionary<string, MaterialProperties>(System.StringComparer.OrdinalIgnoreCase);

            // HARDCODED REALISM CONSTANTS: Ensuring these are ALWAYS used regardless of external JSON
            reg["Generic"] = new MaterialProperties { Absorption = 0.2f, AbsorptionLow = 0.1f, AbsorptionMid = 0.2f, AbsorptionHigh = 0.3f, Scattering = 0.2f, TransmissionLow = 0.4f, TransmissionMid = 0.3f, TransmissionHigh = 0.2f, ResonanceIndex = 22, DensityKgM3 = 1200f, YoungsModulusGPa = 5f, LossFactor = 0.02f };
            reg["Wood"] = new MaterialProperties { Absorption = 0.15f, AbsorptionLow = 0.1f, AbsorptionMid = 0.15f, AbsorptionHigh = 0.2f, Scattering = 0.4f, TransmissionLow = 0.6f, TransmissionMid = 0.4f, TransmissionHigh = 0.2f, ResonanceIndex = 21, DensityKgM3 = 650f, YoungsModulusGPa = 11f, LossFactor = 0.03f };
            reg["Metal"] = new MaterialProperties { Absorption = 0.05f, AbsorptionLow = 0.05f, AbsorptionMid = 0.05f, AbsorptionHigh = 0.1f, Scattering = 0.1f, TransmissionLow = 0.1f, TransmissionMid = 0.05f, TransmissionHigh = 0.02f, ResonanceIndex = 13, DensityKgM3 = 7850f, YoungsModulusGPa = 200f, LossFactor = 0.0002f };
            reg["Concrete"] = new MaterialProperties { Absorption = 0.02f, AbsorptionLow = 0.01f, AbsorptionMid = 0.02f, AbsorptionHigh = 0.02f, Scattering = 0.1f, TransmissionLow = 0.05f, TransmissionMid = 0.02f, TransmissionHigh = 0.01f, ResonanceIndex = 18, DensityKgM3 = 2400f, YoungsModulusGPa = 30f, LossFactor = 0.015f };
            reg["Marble"] = new MaterialProperties { Absorption = 0.01f, AbsorptionLow = 0.01f, AbsorptionMid = 0.01f, AbsorptionHigh = 0.01f, Scattering = 0.05f, TransmissionLow = 0.05f, TransmissionMid = 0.02f, TransmissionHigh = 0.01f, ResonanceIndex = 12, DensityKgM3 = 2700f, YoungsModulusGPa = 60f, LossFactor = 0.002f };
            reg["Carpet"] = new MaterialProperties { Absorption = 0.60f, AbsorptionLow = 0.15f, AbsorptionMid = 0.5f, AbsorptionHigh = 0.75f, Scattering = 0.6f, TransmissionLow = 0.1f, TransmissionMid = 0.05f, TransmissionHigh = 0.01f, ResonanceIndex = 6, DensityKgM3 = 200f, YoungsModulusGPa = 0.01f, LossFactor = 0.4f };
            reg["Glass"] = new MaterialProperties { Absorption = 0.05f, AbsorptionLow = 0.05f, AbsorptionMid = 0.05f, AbsorptionHigh = 0.05f, Scattering = 0.05f, TransmissionLow = 0.7f, TransmissionMid = 0.5f, TransmissionHigh = 0.3f, ResonanceIndex = 3, DensityKgM3 = 2500f, YoungsModulusGPa = 70f, LossFactor = 0.001f };
            reg["None"] = new MaterialProperties { Absorption = 0.0f, AbsorptionLow = 0.0f, AbsorptionMid = 0.0f, AbsorptionHigh = 0.0f, Scattering = 0.0f, TransmissionLow = 1.0f, TransmissionMid = 1.0f, TransmissionHigh = 1.0f, ResonanceIndex = 0, DensityKgM3 = 0f, YoungsModulusGPa = 0f, LossFactor = 1f };
            reg["Plastic"] = new MaterialProperties { Absorption = 0.1f, AbsorptionLow = 0.05f, AbsorptionMid = 0.1f, AbsorptionHigh = 0.2f, Scattering = 0.2f, TransmissionLow = 0.5f, TransmissionMid = 0.4f, TransmissionHigh = 0.2f, ResonanceIndex = 15, DensityKgM3 = 1100f, YoungsModulusGPa = 2.5f, LossFactor = 0.05f };
            reg["Grass"] = new MaterialProperties { Absorption = 0.75f, AbsorptionLow = 0.5f, AbsorptionMid = 0.7f, AbsorptionHigh = 0.9f, Scattering = 0.9f, TransmissionLow = 0.4f, TransmissionMid = 0.6f, TransmissionHigh = 0.8f, ResonanceIndex = 2, DensityKgM3 = 400f, YoungsModulusGPa = 0.005f, LossFactor = 0.6f };
            // A grandstand full of people, which is a MATERIAL and not a special case: it is the
            // most absorbent and the most scattering thing in ordinary acoustics — an occupied seating
            // area takes about three quarters of what reaches it, and what it does return leaves in
            // every direction at once, because it is seats, steps, railings and people rather than a
            // surface. It is why a full house deadens a hall and an empty one rings. Modelled here so
            // a map can say "the face this stand presents to the track is a crowd, not a slab", which
            // is the difference between a crisp copy of the applause coming back and a wash.
            reg["Audience"] = new MaterialProperties { Absorption = 0.72f, AbsorptionLow = 0.5f, AbsorptionMid = 0.75f, AbsorptionHigh = 0.85f, Scattering = 0.8f, TransmissionLow = 0.3f, TransmissionMid = 0.15f, TransmissionHigh = 0.05f, ResonanceIndex = 5, DensityKgM3 = 300f, YoungsModulusGPa = 0.01f, LossFactor = 0.5f };
            reg["Dirt"] = new MaterialProperties { Absorption = 0.60f, AbsorptionLow = 0.4f, AbsorptionMid = 0.5f, AbsorptionHigh = 0.6f, Scattering = 0.8f, TransmissionLow = 0.3f, TransmissionMid = 0.4f, TransmissionHigh = 0.5f, ResonanceIndex = 4, DensityKgM3 = 1600f, YoungsModulusGPa = 0.05f, LossFactor = 0.5f };

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
                            var p = reg.TryGetValue(kvp.Key, out var existing) ? existing : reg["Generic"];
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
                            if (o.DensityKgM3.HasValue) p.DensityKgM3 = o.DensityKgM3.Value;
                            if (o.YoungsModulusGPa.HasValue) p.YoungsModulusGPa = o.YoungsModulusGPa.Value;
                            if (o.LossFactor.HasValue) p.LossFactor = o.LossFactor.Value;
                            reg[kvp.Key] = p;
                        }
                    }
                } catch {}
            }

            // Startup assertion: detect any ResonanceIndex collisions introduced by materials.json overrides.
            var seen = new Dictionary<int, string>();
            foreach (var kvp in reg)
            {
                int idx = kvp.Value.ResonanceIndex;
                if (idx == 0) continue; // 0 is "None" wildcard — collisions allowed
                if (seen.TryGetValue(idx, out string? existing))
                    System.Console.WriteLine($"[ERROR] AcousticRegistry: ResonanceIndex collision! '{existing}' and '{kvp.Key}' both use index {idx}.");
                else
                    seen[idx] = kvp.Key;
            }

            _registry = reg; // atomic publish
        }
    }

    /// <summary>
    /// Initializes the table if nothing has yet — the server never called <see cref="Initialize"/>, so
    /// anything on the server side that needs to know what a material *is* (prefab validation, name to
    /// resonance-index resolution) would otherwise read an empty registry and reject every material name.
    /// Idempotent; a caller that wants to re-read materials.json still calls <see cref="Initialize"/>.
    /// </summary>
    public static void EnsureInitialized()
    {
        if (_registry.Count == 0) Initialize();
    }

    /// <summary>True if <paramref name="type"/> names a material the registry actually knows about.
    /// <see cref="GetProperties"/> substitutes "Generic" for anything else, which is the right runtime
    /// behaviour and the wrong authoring behaviour — a typo'd material must be reported, not guessed.</summary>
    public static bool IsKnown(string type)
    {
        EnsureInitialized();
        return !string.IsNullOrEmpty(type) && _registry.ContainsKey(type);
    }

    /// <summary>Resolves a material NAME to the ResonanceIndex that region face arrays are stored as.
    /// Authoring by raw index (`"Materials": [18, 18, ...]`) is unreadable and unverifiable.</summary>
    public static bool TryGetResonanceIndex(string type, out int index)
    {
        EnsureInitialized();
        index = 0;
        if (string.IsNullOrEmpty(type)) return false;
        if (!_registry.TryGetValue(type, out var props)) return false;
        index = props.ResonanceIndex;
        return true;
    }

    /// <summary>Every known material name, sorted — for naming the alternatives in an error message.</summary>
    public static IReadOnlyList<string> KnownMaterials()
    {
        EnsureInitialized();
        var names = new List<string>(_registry.Keys);
        names.Sort(System.StringComparer.OrdinalIgnoreCase);
        return names;
    }

    public static MaterialProperties GetProperties(string type)
    {
        // Every other accessor does this; this one did not, and so the one call that reached the
        // registry before anything had initialised it threw KeyNotFoundException on "Generic"
        // instead of returning the fallback it advertises.
        EnsureInitialized();
        if (string.IsNullOrEmpty(type)) return _registry["Generic"];
        if (_registry.TryGetValue(type, out var props)) return props;
        System.Console.WriteLine($"[WARNING] AcousticRegistry: Material '{type}' not found, falling back to 'Generic'.");
        return _registry["Generic"];
    }

    public static MaterialProperties GetPropertiesByResonanceIndex(int index)
    {
        EnsureInitialized();
        foreach (var props in _registry.Values)
        {
            if (props.ResonanceIndex == index) return props;
        }
        return _registry["Generic"];
    }
}

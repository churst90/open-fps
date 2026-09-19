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


            // ── Things you walk on, and things you walk in ───────────────────────────────────────
            //
            // Gravel is not a surface, it is a HEAP: very absorbent because the sound goes down into
            // the voids between the stones and does not come back, and almost entirely scattering
            // because there is no flat face anywhere in it. Which is also why a gravel drive is the
            // quietest hard ground there is to stand on and the loudest to walk on.
            reg["Gravel"] = new MaterialProperties { Absorption = 0.65f, AbsorptionLow = 0.35f, AbsorptionMid = 0.65f, AbsorptionHigh = 0.80f, Scattering = 0.95f, TransmissionLow = 0.35f, TransmissionMid = 0.45f, TransmissionHigh = 0.55f, ResonanceIndex = 7, DensityKgM3 = 1700f, YoungsModulusGPa = 0.35f, LossFactor = 0.55f };

            // ── A city is made of four things the table did not have ─────────────────────────────
            //
            // Written for the city block, and each of them is a difference a listener can hear
            // against the Concrete that was standing in for all of them.

            // BRICK. Acoustically close to concrete in how much it takes — masonry absorbs almost
            // nothing — and quite different in what it does with the rest. A brick wall is courses
            // and raked mortar joints, a centimetre of relief every seventy millimetres, which is a
            // quarter wavelength at 8 kHz and a sixteenth at 2: it SCATTERS where a poured concrete
            // wall mirrors. That is why a brick street is a wash and a concrete underpass is a
            // slapback, and it is one number apart. Fired clay is also much less stiff than
            // concrete and far lossier, so a brick wall does not ring when something hits it.
            reg["Brick"] = new MaterialProperties { Absorption = 0.04f, AbsorptionLow = 0.03f, AbsorptionMid = 0.04f, AbsorptionHigh = 0.07f, Scattering = 0.45f, TransmissionLow = 0.06f, TransmissionMid = 0.03f, TransmissionHigh = 0.015f, ResonanceIndex = 23, DensityKgM3 = 1900f, YoungsModulusGPa = 15f, LossFactor = 0.02f };

            // ASPHALT. The reason a concrete motorway is louder than a bituminous one, and it is not
            // a small effect: dense-graded asphalt is POROUS, so sound at grazing incidence goes into
            // the voids between the aggregate and does not all come back. Three to four times
            // concrete's absorption, most of it at the top of the band. And bitumen is a viscous
            // solid — a loss factor two orders up on concrete's — so a road surface is the one hard
            // ground that does not ring at all: a dropped bolt on asphalt thuds, on concrete it
            // rings.
            reg["Asphalt"] = new MaterialProperties { Absorption = 0.09f, AbsorptionLow = 0.04f, AbsorptionMid = 0.08f, AbsorptionHigh = 0.16f, Scattering = 0.35f, TransmissionLow = 0.1f, TransmissionMid = 0.05f, TransmissionHigh = 0.02f, ResonanceIndex = 24, DensityKgM3 = 2300f, YoungsModulusGPa = 3f, LossFactor = 0.18f };

            // TILE. The hardest, flattest, least absorbent surface in ordinary life — glazed ceramic
            // on a solid bed takes about one per cent and returns the rest as a mirror. It is why a
            // tiled station concourse or a public lavatory is the most reverberant room most people
            // ever stand in, far more so than a concrete one. It also RINGS: fired glaze is stiff
            // and almost lossless, a hundredth of concrete's damping, which is the tick under a
            // heel on a station floor.
            reg["Tile"] = new MaterialProperties { Absorption = 0.015f, AbsorptionLow = 0.01f, AbsorptionMid = 0.015f, AbsorptionHigh = 0.02f, Scattering = 0.06f, TransmissionLow = 0.15f, TransmissionMid = 0.08f, TransmissionHigh = 0.03f, ResonanceIndex = 25, DensityKgM3 = 2300f, YoungsModulusGPa = 60f, LossFactor = 0.005f };

            // FOLIAGE. A street tree or a hedge is not a surface at all, it is a VOLUME of thousands
            // of small scatterers, so it is the extreme of the same pair of numbers the Audience is:
            // nearly everything that goes in comes back out in every direction, and the higher the
            // frequency the less of it comes back out at all. A row of trees between a road and a
            // house is worth a few decibels of traffic and takes the edge off all of it, which is
            // what people mean when they say a treed street is quieter.
            reg["Foliage"] = new MaterialProperties { Absorption = 0.55f, AbsorptionLow = 0.2f, AbsorptionMid = 0.5f, AbsorptionHigh = 0.8f, Scattering = 0.92f, TransmissionLow = 0.85f, TransmissionMid = 0.6f, TransmissionHigh = 0.3f, ResonanceIndex = 26, DensityKgM3 = 500f, YoungsModulusGPa = 0.01f, LossFactor = 0.6f };

            // PLASTER — plasterboard on studs, which is what the inside of a building is made of, and
            // the only common material whose absorption goes DOWN with frequency.
            //
            // It is a membrane: a light sheet with an air cavity behind it, so a long wavelength
            // flexes it and loses energy while a short one bounces off. That is the exact opposite of
            // carpet, and it is why the two together make a room sound like a room. A carpeted flat
            // with SOLID walls keeps a two-second bass tail over a 600 ms middle — measured on the
            // city map, and reported as "the carpeted flat sounds reverby like it's a reflective room
            // not carpet". The carpet was working; nothing in the room was taking the bottom out,
            // because nothing in it was a membrane.
            //
            // 0.28 at the bottom against 0.05 at the top is the published curve for 12 mm board on
            // studs, and it is a fact about the construction rather than a preference.
            reg["Plaster"] = new MaterialProperties { Absorption = 0.12f, AbsorptionLow = 0.28f, AbsorptionMid = 0.10f, AbsorptionHigh = 0.05f, Scattering = 0.15f, TransmissionLow = 0.35f, TransmissionMid = 0.18f, TransmissionHigh = 0.08f, ResonanceIndex = 27, DensityKgM3 = 800f, YoungsModulusGPa = 3f, LossFactor = 0.03f };

            // ── Soles ───────────────────────────────────────────────────────────────────────────
            //
            // A sole is a material like any other, and putting it in the same table as the ground is
            // the whole reason a shoe does not need a sound of its own: what a footstep sounds like
            // falls out of the SOFTER of the two things that meet, and these are the soft ones.
            // Their moduli span four decades, which is two octaves of contact brightness — see
            // Footsteps.ContactSeconds — and that single span is most of the difference between
            // every kind of footwear there is.

            /// Soft trainer sole: EVA foam and soft rubber, around 20 MPa.
            reg["Rubber"] = new MaterialProperties { Absorption = 0.20f, AbsorptionLow = 0.10f, AbsorptionMid = 0.20f, AbsorptionHigh = 0.35f, Scattering = 0.35f, TransmissionLow = 0.5f, TransmissionMid = 0.35f, TransmissionHigh = 0.2f, ResonanceIndex = 8, DensityKgM3 = 1100f, YoungsModulusGPa = 0.02f, LossFactor = 0.25f };

            // A leather board sole: two orders of magnitude stiffer than a trainer's, which is why it
            // is the one kind of shoe that can make a click.
            reg["Leather"] = new MaterialProperties { Absorption = 0.12f, AbsorptionLow = 0.08f, AbsorptionMid = 0.12f, AbsorptionHigh = 0.18f, Scattering = 0.15f, TransmissionLow = 0.5f, TransmissionMid = 0.4f, TransmissionHigh = 0.25f, ResonanceIndex = 9, DensityKgM3 = 900f, YoungsModulusGPa = 0.45f, LossFactor = 0.12f };

            // A work boot's sole: hard vulcanised rubber, ten times a trainer's and a tenth of leather.
            reg["BootRubber"] = new MaterialProperties { Absorption = 0.15f, AbsorptionLow = 0.08f, AbsorptionMid = 0.15f, AbsorptionHigh = 0.25f, Scattering = 0.30f, TransmissionLow = 0.5f, TransmissionMid = 0.35f, TransmissionHigh = 0.2f, ResonanceIndex = 10, DensityKgM3 = 1250f, YoungsModulusGPa = 0.20f, LossFactor = 0.20f };

            // A bare foot. Softer than any sole ever made, which is exactly why it slaps rather than
            // clicks on everything, however hard the floor is.
            reg["Skin"] = new MaterialProperties { Absorption = 0.30f, AbsorptionLow = 0.15f, AbsorptionMid = 0.30f, AbsorptionHigh = 0.45f, Scattering = 0.45f, TransmissionLow = 0.6f, TransmissionMid = 0.45f, TransmissionHigh = 0.3f, ResonanceIndex = 11, DensityKgM3 = 1050f, YoungsModulusGPa = 0.0015f, LossFactor = 0.45f };

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

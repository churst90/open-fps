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

    // What the stuff is, for a struck panel that is itself the noise (a latch, hail, a collision): a
    // flat panel's fundamental goes as sqrt(E / rho) times its thickness over its span squared.

    /// <summary>Density, kg/m^3.</summary>
    public float DensityKgM3 { get; set; }

    /// <summary>Young's modulus, GPa. With density, this is the note it rings at.</summary>
    public float YoungsModulusGPa { get; set; }

    /// <summary>Internal damping, roughly the fraction of energy lost per cycle. Not
    /// <see cref="Absorption"/>: a bell and putty differ here by orders of magnitude and absorb airborne
    /// sound about the same.</summary>
    public float LossFactor { get; set; }

    /// <summary>
    /// Sound gets through by its openings, not by moving it (a fence, a hedge, a crowd, a carpet), so the
    /// Transmission figures stand. Anything else is an airtight panel, and its mass, stiffness, damping
    /// and build decide (<see cref="WallTransmission"/>): a 35 cm brick wall is not a 10 cm one.
    /// </summary>
    public bool Porous { get; set; }
}

public static class AcousticRegistry
{
    // Read from the game loop, the audio worker and FMOD's thread while Initialize may run on any of
    // them: built fresh and published atomically, writers locked, so no reader sees one mid-mutation
    // ("operations that change non-concurrent collections must have exclusive access").
    private static volatile Dictionary<string, MaterialProperties> _registry = new(System.StringComparer.OrdinalIgnoreCase);
    private static readonly object _initLock = new();

    public static void Initialize()
    {
        // Never mutate the published dictionary in place.
        lock (_initLock)
        {
            var reg = new Dictionary<string, MaterialProperties>(System.StringComparer.OrdinalIgnoreCase);

            reg["Generic"] = new MaterialProperties { Absorption = 0.2f, AbsorptionLow = 0.1f, AbsorptionMid = 0.2f, AbsorptionHigh = 0.3f, Scattering = 0.2f, TransmissionLow = 0.4f, TransmissionMid = 0.3f, TransmissionHigh = 0.2f, ResonanceIndex = 22, DensityKgM3 = 1200f, YoungsModulusGPa = 5f, LossFactor = 0.02f };
            reg["Wood"] = new MaterialProperties { Absorption = 0.15f, AbsorptionLow = 0.1f, AbsorptionMid = 0.15f, AbsorptionHigh = 0.2f, Scattering = 0.4f, TransmissionLow = 0.6f, TransmissionMid = 0.4f, TransmissionHigh = 0.2f, ResonanceIndex = 21, DensityKgM3 = 650f, YoungsModulusGPa = 11f, LossFactor = 0.03f };
            reg["Metal"] = new MaterialProperties { Absorption = 0.05f, AbsorptionLow = 0.05f, AbsorptionMid = 0.05f, AbsorptionHigh = 0.1f, Scattering = 0.1f, TransmissionLow = 0.1f, TransmissionMid = 0.05f, TransmissionHigh = 0.02f, ResonanceIndex = 13, DensityKgM3 = 7850f, YoungsModulusGPa = 200f, LossFactor = 0.0002f };
            // A palisade fence is air with some steel in it: half to two thirds gap, the pales scattering
            // the top end. A material of its own because the Steam Audio scene keys materials by name and
            // never sees a prefab's override: 200 panels of "Metal with the numbers changed" along a
            // railway silenced every vehicle behind them ("like it is going under a bridge").
            reg["Fence"] = new MaterialProperties { Absorption = 0.08f, AbsorptionLow = 0.05f, AbsorptionMid = 0.08f, AbsorptionHigh = 0.12f, Scattering = 0.55f, TransmissionLow = 0.94f, TransmissionMid = 0.88f, TransmissionHigh = 0.72f, ResonanceIndex = 29, DensityKgM3 = 7850f, YoungsModulusGPa = 200f, LossFactor = 0.0004f };   // 29: its own, not Metal's 13
            reg["Concrete"] = new MaterialProperties { Absorption = 0.02f, AbsorptionLow = 0.01f, AbsorptionMid = 0.02f, AbsorptionHigh = 0.02f, Scattering = 0.1f, TransmissionLow = 0.05f, TransmissionMid = 0.02f, TransmissionHigh = 0.01f, ResonanceIndex = 18, DensityKgM3 = 2400f, YoungsModulusGPa = 30f, LossFactor = 0.015f };
            reg["Marble"] = new MaterialProperties { Absorption = 0.01f, AbsorptionLow = 0.01f, AbsorptionMid = 0.01f, AbsorptionHigh = 0.01f, Scattering = 0.05f, TransmissionLow = 0.05f, TransmissionMid = 0.02f, TransmissionHigh = 0.01f, ResonanceIndex = 12, DensityKgM3 = 2700f, YoungsModulusGPa = 60f, LossFactor = 0.002f };
            reg["Carpet"] = new MaterialProperties { Absorption = 0.60f, AbsorptionLow = 0.15f, AbsorptionMid = 0.5f, AbsorptionHigh = 0.75f, Scattering = 0.6f, TransmissionLow = 0.1f, TransmissionMid = 0.05f, TransmissionHigh = 0.01f, ResonanceIndex = 6, DensityKgM3 = 200f, YoungsModulusGPa = 0.01f, LossFactor = 0.4f };
            reg["Glass"] = new MaterialProperties { Absorption = 0.05f, AbsorptionLow = 0.05f, AbsorptionMid = 0.05f, AbsorptionHigh = 0.05f, Scattering = 0.05f, TransmissionLow = 0.7f, TransmissionMid = 0.5f, TransmissionHigh = 0.3f, ResonanceIndex = 3, DensityKgM3 = 2500f, YoungsModulusGPa = 70f, LossFactor = 0.001f };
            reg["None"] = new MaterialProperties { Absorption = 0.0f, AbsorptionLow = 0.0f, AbsorptionMid = 0.0f, AbsorptionHigh = 0.0f, Scattering = 0.0f, TransmissionLow = 1.0f, TransmissionMid = 1.0f, TransmissionHigh = 1.0f, ResonanceIndex = 0, DensityKgM3 = 0f, YoungsModulusGPa = 0f, LossFactor = 1f };
            reg["Plastic"] = new MaterialProperties { Absorption = 0.1f, AbsorptionLow = 0.05f, AbsorptionMid = 0.1f, AbsorptionHigh = 0.2f, Scattering = 0.2f, TransmissionLow = 0.5f, TransmissionMid = 0.4f, TransmissionHigh = 0.2f, ResonanceIndex = 15, DensityKgM3 = 1100f, YoungsModulusGPa = 2.5f, LossFactor = 0.05f };
            reg["Grass"] = new MaterialProperties { Absorption = 0.75f, AbsorptionLow = 0.5f, AbsorptionMid = 0.7f, AbsorptionHigh = 0.9f, Scattering = 0.9f, TransmissionLow = 0.4f, TransmissionMid = 0.6f, TransmissionHigh = 0.8f, ResonanceIndex = 2, DensityKgM3 = 400f, YoungsModulusGPa = 0.005f, LossFactor = 0.6f };
            // A grandstand full of people: occupied seating takes about three quarters of what reaches
            // it and scatters the rest, so applause comes back as a wash, not a crisp copy.
            reg["Audience"] = new MaterialProperties { Absorption = 0.72f, AbsorptionLow = 0.5f, AbsorptionMid = 0.75f, AbsorptionHigh = 0.85f, Scattering = 0.8f, TransmissionLow = 0.3f, TransmissionMid = 0.15f, TransmissionHigh = 0.05f, ResonanceIndex = 5, DensityKgM3 = 300f, YoungsModulusGPa = 0.01f, LossFactor = 0.5f };
            reg["Dirt"] = new MaterialProperties { Absorption = 0.60f, AbsorptionLow = 0.4f, AbsorptionMid = 0.5f, AbsorptionHigh = 0.6f, Scattering = 0.8f, TransmissionLow = 0.3f, TransmissionMid = 0.4f, TransmissionHigh = 0.5f, ResonanceIndex = 4, DensityKgM3 = 1600f, YoungsModulusGPa = 0.05f, LossFactor = 0.5f };


            // Gravel is a heap: sound goes into the voids and does not come back, and no face is flat.
            reg["Gravel"] = new MaterialProperties { Absorption = 0.65f, AbsorptionLow = 0.35f, AbsorptionMid = 0.65f, AbsorptionHigh = 0.80f, Scattering = 0.95f, TransmissionLow = 0.35f, TransmissionMid = 0.45f, TransmissionHigh = 0.55f, ResonanceIndex = 7, DensityKgM3 = 1700f, YoungsModulusGPa = 0.35f, LossFactor = 0.55f };

            // Brick absorbs as little as concrete but scatters: a centimetre of mortar relief every
            // 70 mm is a quarter wavelength at 8 kHz, so a brick street is a wash where a concrete
            // underpass slaps back. Fired clay is less stiff and far lossier: it does not ring.
            reg["Brick"] = new MaterialProperties { Absorption = 0.04f, AbsorptionLow = 0.03f, AbsorptionMid = 0.04f, AbsorptionHigh = 0.07f, Scattering = 0.45f, TransmissionLow = 0.06f, TransmissionMid = 0.03f, TransmissionHigh = 0.015f, ResonanceIndex = 23, DensityKgM3 = 1900f, YoungsModulusGPa = 15f, LossFactor = 0.02f };

            // Asphalt is porous at grazing incidence: three to four times concrete's absorption, mostly
            // at the top, which is why a concrete motorway is louder. Bitumen's loss factor is two
            // orders up on concrete's: a dropped bolt thuds.
            reg["Asphalt"] = new MaterialProperties { Absorption = 0.09f, AbsorptionLow = 0.04f, AbsorptionMid = 0.08f, AbsorptionHigh = 0.16f, Scattering = 0.35f, TransmissionLow = 0.1f, TransmissionMid = 0.05f, TransmissionHigh = 0.02f, ResonanceIndex = 24, DensityKgM3 = 2300f, YoungsModulusGPa = 3f, LossFactor = 0.18f };

            // Glazed tile on a solid bed takes about one per cent and mirrors the rest, the most
            // reverberant room most people stand in; a hundredth of concrete's damping, so it rings
            // (the tick under a heel on a station floor).
            reg["Tile"] = new MaterialProperties { Absorption = 0.015f, AbsorptionLow = 0.01f, AbsorptionMid = 0.015f, AbsorptionHigh = 0.02f, Scattering = 0.06f, TransmissionLow = 0.15f, TransmissionMid = 0.08f, TransmissionHigh = 0.03f, ResonanceIndex = 25, DensityKgM3 = 2300f, YoungsModulusGPa = 60f, LossFactor = 0.005f };

            // Foliage is a volume of small scatterers: nearly all of it scattered, less coming back the
            // higher the frequency. A row of trees is worth a few decibels of traffic.
            reg["Foliage"] = new MaterialProperties { Absorption = 0.55f, AbsorptionLow = 0.2f, AbsorptionMid = 0.5f, AbsorptionHigh = 0.8f, Scattering = 0.92f, TransmissionLow = 0.85f, TransmissionMid = 0.6f, TransmissionHigh = 0.3f, ResonanceIndex = 26, DensityKgM3 = 500f, YoungsModulusGPa = 0.01f, LossFactor = 0.6f };

            // Plasterboard on studs is a membrane, the one common material whose absorption falls with
            // frequency: carpet takes the top, the walls the bass. With solid walls a carpeted flat on
            // the city map kept a two-second bass tail over a 600 ms middle ("sounds reverby like it's a
            // reflective room not carpet"). 0.28 low, 0.05 high: the published curve for 12 mm board.
            reg["Plaster"] = new MaterialProperties { Absorption = 0.12f, AbsorptionLow = 0.28f, AbsorptionMid = 0.10f, AbsorptionHigh = 0.05f, Scattering = 0.15f, TransmissionLow = 0.35f, TransmissionMid = 0.18f, TransmissionHigh = 0.08f, ResonanceIndex = 27, DensityKgM3 = 800f, YoungsModulusGPa = 3f, LossFactor = 0.03f };

            // A suspended mineral-fibre ceiling, a void above: the published curve for a 16-19 mm tile
            // (NRC 0.70) is 0.35-0.40 low and 0.80-0.85 mid and high; porous, so the bass passes into the
            // void, and flat. Bare concrete overhead rang the airport terminal 7-10 s where a real one
            // is 2-3 (2026-09-29).
            reg["AcousticTile"] = new MaterialProperties { Absorption = 0.70f, AbsorptionLow = 0.38f, AbsorptionMid = 0.80f, AbsorptionHigh = 0.82f, Scattering = 0.10f, TransmissionLow = 0.55f, TransmissionMid = 0.35f, TransmissionHigh = 0.15f, ResonanceIndex = 30, DensityKgM3 = 250f, YoungsModulusGPa = 0.05f, LossFactor = 0.3f };

            // Soles: a footstep falls out of the softer of the two things that meet. Their moduli span
            // four decades, two octaves of contact brightness (Footsteps.ContactSeconds).

            // Soft trainer sole: EVA foam and soft rubber, around 20 MPa.
            reg["Rubber"] = new MaterialProperties { Absorption = 0.20f, AbsorptionLow = 0.10f, AbsorptionMid = 0.20f, AbsorptionHigh = 0.35f, Scattering = 0.35f, TransmissionLow = 0.5f, TransmissionMid = 0.35f, TransmissionHigh = 0.2f, ResonanceIndex = 8, DensityKgM3 = 1100f, YoungsModulusGPa = 0.02f, LossFactor = 0.25f };

            // Leather board: two orders stiffer than a trainer's, the one sole that clicks.
            reg["Leather"] = new MaterialProperties { Absorption = 0.12f, AbsorptionLow = 0.08f, AbsorptionMid = 0.12f, AbsorptionHigh = 0.18f, Scattering = 0.15f, TransmissionLow = 0.5f, TransmissionMid = 0.4f, TransmissionHigh = 0.25f, ResonanceIndex = 9, DensityKgM3 = 900f, YoungsModulusGPa = 0.45f, LossFactor = 0.12f };

            // A work boot's sole: hard vulcanised rubber, ten times a trainer's and a tenth of leather.
            reg["BootRubber"] = new MaterialProperties { Absorption = 0.15f, AbsorptionLow = 0.08f, AbsorptionMid = 0.15f, AbsorptionHigh = 0.25f, Scattering = 0.30f, TransmissionLow = 0.5f, TransmissionMid = 0.35f, TransmissionHigh = 0.2f, ResonanceIndex = 10, DensityKgM3 = 1250f, YoungsModulusGPa = 0.20f, LossFactor = 0.20f };

            // A bare foot, softer than any sole: it slaps on everything.
            reg["Skin"] = new MaterialProperties { Absorption = 0.30f, AbsorptionLow = 0.15f, AbsorptionMid = 0.30f, AbsorptionHigh = 0.45f, Scattering = 0.45f, TransmissionLow = 0.6f, TransmissionMid = 0.45f, TransmissionHigh = 0.3f, ResonanceIndex = 11, DensityKgM3 = 1050f, YoungsModulusGPa = 0.0015f, LossFactor = 0.45f };

            // Still water: impedance 3,500 times air's, absorbing a per cent or two. A round skips below
            // Birkhoff's angle (Ricochet, BulletImpact). Its modulus is the bulk modulus, 2.2 GPa.
            reg["Water"] = new MaterialProperties { Absorption = 0.015f, AbsorptionLow = 0.01f, AbsorptionMid = 0.015f, AbsorptionHigh = 0.02f, Scattering = 0.05f, TransmissionLow = 0.01f, TransmissionMid = 0.005f, TransmissionHigh = 0.002f, ResonanceIndex = 31, DensityKgM3 = 1000f, YoungsModulusGPa = 2.2f, LossFactor = 0.5f };

            foreach (var porous in new[] { "Fence", "Foliage", "Grass", "Audience", "Dirt", "Gravel", "Carpet", "AcousticTile", "None" })
                if (reg.TryGetValue(porous, out var pp)) { pp.Porous = true; reg[porous] = pp; }

            // Every material needs its own ResonanceIndex.
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

    /// <summary>Initializes the table if nothing has: the server never calls <see cref="Initialize"/>, and
    /// its prefab validation would otherwise reject every material name.</summary>
    public static void EnsureInitialized()
    {
        if (_registry.Count == 0) Initialize();
    }

    /// <summary>Whether <paramref name="type"/> names a known material. <see cref="GetProperties"/> falls
    /// back to "Generic", right at run time and wrong when authoring: a typo must be reported.</summary>
    public static bool IsKnown(string type)
    {
        EnsureInitialized();
        return !string.IsNullOrEmpty(type) && _registry.ContainsKey(type);
    }

    /// <summary>A material name's ResonanceIndex, as region face arrays store it, so maps can be authored
    /// by name.</summary>
    public static bool TryGetResonanceIndex(string type, out int index)
    {
        EnsureInitialized();
        index = 0;
        if (string.IsNullOrEmpty(type)) return false;
        if (!_registry.TryGetValue(type, out var props)) return false;
        index = props.ResonanceIndex;
        return true;
    }

    /// <summary>The table's three band centres, Hz: every Low/Mid/High figure in it means these.</summary>
    public static readonly (float Low, float Mid, float High) BandCentresHz = (200f, 1250f, 8000f);

    /// <summary>
    /// A band figure of the table (at <see cref="BandCentresHz"/>) at another frequency: linear in
    /// log-frequency between the two centres either side, held flat beyond the ends.
    /// </summary>
    public static float AtFrequency(float low, float mid, float high, float hz)
    {
        var c = BandCentresHz;
        if (hz <= c.Low) return low;
        if (hz >= c.High) return high;
        if (hz <= c.Mid) return low + (mid - low) * MathF.Log(hz / c.Low) / MathF.Log(c.Mid / c.Low);
        return mid + (high - mid) * MathF.Log(hz / c.Mid) / MathF.Log(c.High / c.Mid);
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
        // Without it a call before initialisation threw KeyNotFoundException on "Generic".
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

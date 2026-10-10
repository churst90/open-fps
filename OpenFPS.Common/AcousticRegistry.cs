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

    // ── The rest of what the stuff is (docs/MATTER.md section 2) ─────────────────────────────────
    //
    // Nothing that sounded before reads these, so adding them changed no sound. Each value's source is in
    // docs/MATTER.md, "The material table", and on the line that sets it in AcousticRegistry.Initialize.

    /// <summary>Which family of the table it belongs to (metal, stone, glass, wood, polymer, ground, soft,
    /// liquid, building, none): for lists and for a builder choosing one.</summary>
    public string Family { get; set; }

    /// <summary>Poisson's ratio: how much it narrows as it is stretched. With the modulus, the plate's bending
    /// stiffness and the Hertz contact.</summary>
    public float PoissonRatio { get; set; }

    /// <summary>
    /// How the loss factor changes with frequency: <see cref="LossFactor"/> is its value at 1 kHz, and at f
    /// it is LossFactor (f / 1 kHz)^LossExponent. Zero for metals, glass and stone, whose loss hardly moves
    /// with frequency; wood and polymers are viscoelastic and damp their high modes much faster (wood's
    /// is DoorPhysics.WoodLoss's Rayleigh fit, about 0.8 between 300 Hz and 5 kHz).
    /// </summary>
    public float LossExponent { get; set; }

    /// <summary>Young's modulus across the grain, GPa, for a material that has one (wood, plywood); zero
    /// when it is the same every way. <see cref="YoungsModulusGPa"/> is along the grain: wood is ten to
    /// fifteen times stiffer along it, so a plank and a block of the same wood ring differently.</summary>
    public float TransverseModulusGPa { get; set; }

    /// <summary>Indentation hardness, MPa: the mean pressure under a contact at which it gives (dents,
    /// crumbles) instead of springing back. A steel rod on lead dents it and the blow lengthens; on glass
    /// it does not.</summary>
    public float HardnessMPa { get; set; }

    /// <summary>Surface roughness, Ra, millimetres: float glass a hundred-thousandth, planed wood a
    /// hundredth, a concrete slab half a millimetre. For friction, scrapes and how a reflection smears.</summary>
    public float RoughnessMm { get; set; }

    /// <summary>The stress it breaks at, MPa: tensile for metals, polymers and wood along the grain;
    /// flexural for brittle things (glass, stone, brick, concrete).</summary>
    public float StrengthMPa { get; set; }

    /// <summary>Specific heat, J/(kg K).</summary>
    public float SpecificHeatJKgK { get; set; }

    /// <summary>Thermal conductivity, W/(m K).</summary>
    public float ThermalConductivityWmK { get; set; }

    /// <summary>Where it melts (or for glass and bitumen softens), degrees C; null when it never melts in
    /// anything the game makes: it chars, burns, calcines or decomposes first (wood, concrete, gypsum).</summary>
    public float? MeltingPointC { get; set; }

    /// <summary>How much water it takes up when soaked, percent of its dry mass (wood to its fibre saturation
    /// point; brick and stone by immersion; plastics by 24 h immersion, ASTM D570).</summary>
    public float WaterUptakePercent { get; set; }

    /// <summary>Water vapour resistance factor, mu (EN ISO 10456): how much harder vapour passes through it
    /// than through air, and so how slowly it dries. Infinity for metal and glass.</summary>
    public float VapourResistance { get; set; }

    // Fuel: left for the fire work (docs/FIRE.md, "Fire that burns what is there") to fill. Zero: not
    // filled, which a reader must take as "does not burn" until it is.

    /// <summary>The temperature at which it gives off burnable gas, degrees C.</summary>
    public float PyrolysisC { get; set; }

    /// <summary>Heat of combustion, MJ/kg.</summary>
    public float HeatOfCombustionMJKg { get; set; }

    /// <summary>The loss factor at <paramref name="hz"/>: <see cref="LossFactor"/> at 1 kHz, scaled by
    /// <see cref="LossExponent"/>.</summary>
    public readonly float LossAt(float hz)
        => LossExponent == 0f ? LossFactor : LossFactor * MathF.Pow(MathF.Max(1f, hz) / 1000f, LossExponent);

    /// <summary>Poisson's ratio, or 0.3 for a material that has none set (one made in code).</summary>
    public readonly float Poisson => PoissonRatio > 0f ? PoissonRatio : 0.3f;

    /// <summary>The modulus across the grain, GPa: <see cref="YoungsModulusGPa"/> when it has no grain.</summary>
    public readonly float AcrossGrainGPa => TransverseModulusGPa > 0f ? TransverseModulusGPa : YoungsModulusGPa;
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

            AddMatter(reg);

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

    // ── The material table (docs/MATTER.md section 2) ──────────────────────────────────────────────
    //
    // Source keys, in full in docs/MATTER.md, "Sources":
    //   BH    Bies & Hansen, Engineering Noise Control, 4th ed. (2009), App. C: density, E, Poisson, loss factor.
    //   CHP   Cremer, Heckl & Petersson, Structure-Borne Sound, 3rd ed. (2005), ch. 3: loss factors.
    //   ASM   ASM Handbook vol. 2 and MatWeb data sheets for the named alloy: hardness, strength, cp, k, melting.
    //   WH    USDA Wood Handbook FPL-GTR-190 (2010): specific gravity, MOE, MOR (tables 5-3), E_T/E_L and
    //         Poisson (tables 5-1, 5-2), fibre saturation (ch. 4).
    //   ON    Ono & Norimoto (1983), Jpn. J. Appl. Phys. 22, 611: wood's loss along the grain, 0.005-0.015.
    //   RYL   Ren, Yeh & Lin, the Rayleigh-form wood loss DoorPhysics.WoodLoss already uses: alpha/w + beta w.
    //   ISO   EN ISO 10456:2007 table 3: building materials' conductivity, specific heat, vapour resistance.
    //   EN572 EN 572-1:2012, soda-lime silicate glass: density, E, Poisson, bending strength, cp, k.
    //   ASHBY Ashby, Materials Selection in Mechanical Design, 4th ed. (2011), app. C: polymers, foams, elastomers.
    //   D570  ASTM D570 water absorption, 24 h, as MatWeb's typical values for the named polymer.
    //   ROCK  Toksoz & Johnston (eds.), Seismic Wave Attenuation (SEG 1981): rock Q 30-400, loss = 1/Q.
    //   SOIL  Hardin & Drnevich (1972), J. Soil Mech. Found. Div. 98: soil damping ratio 2-10 %, loss = 2 zeta.
    //   ICE   Petrenko & Whitworth, Physics of Ice (1999); Schulson (1999) JOM 51(2): E 9-10 GPa, tensile 0.7-3 MPa.
    //   SNOW  Mellor (1975), A review of basic snow mechanics, IAHS 114; Sturm et al. (1997) J. Glaciol. 43: k.
    //   DUCK  Duck, Physical Properties of Tissue (1990): soft tissue cp and k.
    //   FIN   Typical surface finishes (ISO 1302 / machinists' Ra tables): rolled and machined metal, float glass.
    //   EST   An estimate, not a measurement: the line says from what.

    /// <summary>
    /// Fills in the rest of each material's properties and adds the families of docs/MATTER.md 2.3. An
    /// existing material keeps every figure it had: only what was not there is added.
    /// </summary>
    private static void AddMatter(Dictionary<string, MaterialProperties> reg)
    {
        const float Tight = float.PositiveInfinity;   // vapour-tight (ISO 10456 lists metal and glass as infinite)

        // ── Metals and alloys: loss hardly moves with frequency (exponent 0) ────────────────────────
        // "Metal" is mild steel, AISI 1018 hot rolled: Poisson BH; HB 126 (about 1270 MPa), 440 MPa, ASM.
        Matter(reg, "Metal", "metal", 0.29f, 0f, 1270f, 0.0016f, 440f, 486f, 51.9f, 1450f, 0f, Tight);
        // A palisade fence is galvanised mild steel.
        Matter(reg, "Fence", "metal", 0.29f, 0f, 1270f, 0.0016f, 440f, 486f, 51.9f, 1450f, 0f, Tight);
        // 6061-T6: 2700 kg/m3, 69 GPa, 0.33, loss 1e-4 (BH, CHP); HV 107, 310 MPa, 896, 167, 582-652 C (ASM).
        Solid(reg, "Aluminium", 32, like: "Metal", 2700f, 69f, 0.0001f);
        Matter(reg, "Aluminium", "metal", 0.33f, 0f, 1050f, 0.0008f, 310f, 896f, 167f, 650f, 0f, Tight);
        // 304 annealed: 8000, 193 GPa, 0.29 (ASM); loss as carbon steel (CHP 1-6e-4); HV 129, 505 MPa, 500, 16.2, 1400 C.
        Solid(reg, "StainlessSteel", 33, like: "Metal", 8000f, 193f, 0.0002f);
        Matter(reg, "StainlessSteel", "metal", 0.29f, 0f, 1270f, 0.0004f, 505f, 500f, 16.2f, 1400f, 0f, Tight);
        // Grey iron, ASTM A48 class 30: 7200, 100 GPa, 0.26, HB 210, 214 MPa, 490, 46, 1200 C (ASM). Its graphite
        // flakes damp it: specific damping capacity a few per cent (ASM vol. 1), so loss 0.005 (EST from that):
        // steel rings, cast iron clanks.
        Solid(reg, "CastIron", 34, like: "Metal", 7200f, 100f, 0.005f);
        Matter(reg, "CastIron", "metal", 0.26f, 0f, 2160f, 0.0063f, 214f, 490f, 46f, 1200f, 0f, Tight);
        // C11000 annealed: 8940, 117 GPa, 0.34 (ASM); loss 0.002 (BH, CHP); HV 50, 220 MPa, 385, 391, 1083 C.
        Solid(reg, "Copper", 35, like: "Metal", 8940f, 117f, 0.002f);
        Matter(reg, "Copper", "metal", 0.34f, 0f, 490f, 0.0008f, 220f, 385f, 391f, 1083f, 0f, Tight);
        // C26000 cartridge brass: 8530, 110 GPa, 0.35, HV 100, 340 MPa, 375, 120, 915 C (ASM); loss under 1e-3 (CHP).
        Solid(reg, "Brass", 36, like: "Metal", 8530f, 110f, 0.0008f);
        Matter(reg, "Brass", "metal", 0.35f, 0f, 980f, 0.0008f, 340f, 375f, 120f, 915f, 0f, Tight);
        // Tin bronze (C90700 / C51000): 8800, 110 GPa, 0.34, HV 130, about 300 MPa, 380, 50, 950 C (ASM). Loss
        // 3e-4: bell bronze partials have Q of thousands (EST from Rossing, Science of Percussion Instruments, 2000).
        Solid(reg, "Bronze", 37, like: "Metal", 8800f, 110f, 0.0003f);
        Matter(reg, "Bronze", "metal", 0.34f, 0f, 1270f, 0.0016f, 300f, 380f, 50f, 950f, 0f, Tight);
        // Lead: 11340, 16 GPa, 0.44, loss 0.015 (BH; CHP 0.015-0.02); HV 5, 17 MPa, 129, 35, 327 C (ASM).
        Solid(reg, "Lead", 38, like: "Metal", 11340f, 16f, 0.015f);
        Matter(reg, "Lead", "metal", 0.44f, 0f, 49f, 0.0032f, 17f, 129f, 35f, 327f, 0f, Tight);
        // Ti-6Al-4V: 4430, 114 GPa, 0.34, HV 349, 950 MPa, 526, 6.7, 1650 C (ASM); loss 2e-4 (EST, as steel).
        Solid(reg, "Titanium", 39, like: "Metal", 4430f, 114f, 0.0002f);
        Matter(reg, "Titanium", "metal", 0.34f, 0f, 3420f, 0.0008f, 950f, 526f, 6.7f, 1650f, 0f, Tight);

        // ── Stone, ceramic and glass ──────────────────────────────────────────────────────────────────
        // Concrete: Poisson 0.2 (BH); hardness 3x a 40 MPa crushing strength (EST, Tabor); 3 MPa in tension;
        // cp 1000, k 2.0, mu 100 between ISO's 130 dry and 80 wet (ISO); 5 % uptake (EST, typical 3-6 %).
        Matter(reg, "Concrete", "stone", 0.2f, 0f, 120f, 0.5f, 3f, 1000f, 2.0f, null, 5f, 100f);
        // Marble: Poisson 0.27 (EST, calcite rock); calcite HV about 130; 15 MPa flexural; ISO 1000, 3.5, 10000;
        // it calcines near 825 C rather than melting.
        Matter(reg, "Marble", "stone", 0.27f, 0f, 1300f, 0.0005f, 15f, 1000f, 3.5f, null, 0.2f, 10000f);
        // Soda-lime glass (EN572: 0.22, 45 MPa, 720, 1.0; HV 550); softens near 726 C. Float glass Ra 1e-5 mm (FIN).
        Matter(reg, "Glass", "glass", 0.22f, 0f, 5400f, 0.00001f, 45f, 720f, 1.0f, 726f, 0f, Tight);
        // Clay brick: Poisson 0.15 (EST; BH lists 0.12); hardness 3x crushing (EST); ISO 1000, 0.77, 10; 12 %
        // uptake (EST, ASTM C67 range 5-20 %).
        Matter(reg, "Brick", "stone", 0.15f, 0f, 150f, 1.0f, 3f, 1000f, 0.77f, null, 12f, 10f);
        // Porcelain and glazed ceramic tile: Poisson 0.25 (EST); glaze HV about 600 (EST); 35 MPa and under
        // 0.5 % uptake (ISO 13006 group BIa); ISO 840, 1.3; the glaze is vapour-tight (EST); vitrified near
        // 1300 C (EST).
        Matter(reg, "Tile", "stone", 0.25f, 0f, 6000f, 0.0005f, 35f, 840f, 1.3f, 1300f, 0.5f, Tight);
        // Granite: 2700, 50 GPa (BH range 10-70), 0.25; loss 1/Q with Q about 250 (ROCK); quartz and feldspar
        // HV 6-11 GPa (EST, ASHBY); 15 MPa flexural (ASTM C880 typical); ISO 1000, 2.8, 10000; melts 1215-1260 C.
        Solid(reg, "Granite", 40, like: "Marble", 2700f, 50f, 0.004f);
        Matter(reg, "Granite", "stone", 0.25f, 0f, 6000f, 0.001f, 15f, 1000f, 2.8f, 1250f, 0.2f, 10000f);
        // Sandstone: 2300, 15 GPa, 0.2; Q 30-100 (ROCK) so 0.02; hardness 3x crushing (EST); its face scatters
        // like brick's; ISO 1000, 2.3; quartz grains melt near 1650 C (EST); 5 % uptake (EST, range 2-10 %);
        // mu 40 (EST between ISO's light and dense sedimentary rock).
        Solid(reg, "Sandstone", 41, like: "Brick", 2300f, 15f, 0.02f);
        Matter(reg, "Sandstone", "stone", 0.2f, 0f, 300f, 0.5f, 5f, 1000f, 2.3f, 1650f, 5f, 40f);
        // Laminated glass: two panes on a PVB interlayer. The glass's stiffness, but the interlayer's shear
        // takes the ring out: loss 0.03-0.1 at 20 C in published ISO 16940 measurements, 0.04 here (EST).
        Solid(reg, "LaminatedGlass", 42, like: "Glass", 2500f, 70f, 0.04f);
        Matter(reg, "LaminatedGlass", "glass", 0.22f, 0f, 5400f, 0.00001f, 45f, 760f, 0.9f, 726f, 0f, Tight);

        // ── Wood: stiff along the grain, viscoelastic (loss rising with frequency, RYL) ───────────────
        // "Wood" (a generic softwood board as it was): Poisson 0.37 and E_T/E_L 0.07 (WH); Brinell about 25 MPa
        // (EST, EN 1534 range); 80 MPa MOR (WH); ISO 1600, 0.13, 50; 30 % fibre saturation (WH).
        Matter(reg, "Wood", "wood", 0.37f, 0.8f, 25f, 0.01f, 80f, 1600f, 0.13f, null, 30f, 50f, acrossGrainGPa: 0.8f);
        // White oak at 12 % moisture: SG 0.68 (about 760 kg/m3), MOE 12.3 GPa, MOR 105 MPa, E_T/E_L 0.072,
        // Poisson LR 0.37 (WH); loss along the grain 0.012 (ON); Brinell 34 MPa (EST, EN 1534); ISO hardwood k 0.18.
        Solid(reg, "Oak", 43, like: "Wood", 760f, 12.3f, 0.012f);
        Matter(reg, "Oak", "wood", 0.37f, 0.8f, 34f, 0.01f, 105f, 1600f, 0.18f, null, 30f, 50f, acrossGrainGPa: 0.89f);
        // Southern pine (loblolly): SG 0.51 (about 570), MOE 12.3 GPa, MOR 88 MPa, E_T/E_L 0.078, Poisson 0.33
        // (WH); loss 0.010 (ON); Brinell 16 MPa (EST); ISO softwood k 0.13.
        Solid(reg, "Pine", 44, like: "Wood", 570f, 12.3f, 0.010f);
        Matter(reg, "Pine", "wood", 0.33f, 0.8f, 16f, 0.01f, 88f, 1600f, 0.13f, null, 30f, 50f, acrossGrainGPa: 0.96f);
        // Sugar maple: SG 0.63 (about 705), MOE 12.6 GPa, MOR 109 MPa, E_T/E_L 0.065, Poisson 0.42 (WH);
        // loss 0.008 (ON, the low end: maple is a tone wood); Brinell 40 MPa (EST); ISO hardwood k 0.18.
        Solid(reg, "Maple", 45, like: "Wood", 705f, 12.6f, 0.008f);
        Matter(reg, "Maple", "wood", 0.42f, 0.8f, 40f, 0.01f, 109f, 1600f, 0.18f, null, 30f, 50f, acrossGrainGPa: 0.82f);
        // Plywood: 600, 5.4 GPa, loss 0.013 (BH); Poisson 0.2, across the face grain 0.6x (EST, five plies);
        // 40 MPa (EST); ISO 1600, 0.13, mu 200 dry.
        Solid(reg, "Plywood", 46, like: "Wood", 600f, 5.4f, 0.013f);
        Matter(reg, "Plywood", "wood", 0.2f, 0.8f, 25f, 0.01f, 40f, 1600f, 0.13f, null, 30f, 200f, acrossGrainGPa: 3.2f);
        // MDF: 750, 3.5 GPa (EN 622-5 bending modulus 2.5-4, EST mid); loss 0.025 (EST: fibre board damps more than
        // solid wood); no grain in its plane; 30 MPa MOR (EN 622-5); ISO 1700, 0.18, mu 20; 12 % (EST).
        Solid(reg, "MDF", 47, like: "Wood", 750f, 3.5f, 0.025f);
        Matter(reg, "MDF", "wood", 0.25f, 0.8f, 40f, 0.005f, 30f, 1700f, 0.18f, null, 12f, 20f);

        // ── Polymers: viscoelastic, loss rising gently with frequency (exponent 0.3, EST from ASHBY's loss
        // coefficients at 1 Hz-10 kHz) ──────────────────────────────────────────────────────────────────
        // "Plastic" (a generic thermoplastic, ABS-like): Poisson 0.37, hardness about 3x yield (ASHBY), 40 MPa;
        // ISO 1500, 0.2, 10000; melts near 220 C (EST, ABS processing); 0.3 % (D570).
        Matter(reg, "Plastic", "polymer", 0.37f, 0.3f, 100f, 0.0008f, 40f, 1500f, 0.2f, 220f, 0.3f, 10000f);
        // Rigid PVC: 1400, 3.0 GPa, 0.38, loss 0.02 (ASHBY); 50 MPa; ISO 1000, 0.17, 50000; decomposes near 200 C
        // (EST); 0.1 % (D570).
        Solid(reg, "PVC", 48, like: "Plastic", 1400f, 3.0f, 0.02f);
        Matter(reg, "PVC", "polymer", 0.38f, 0.3f, 150f, 0.0008f, 50f, 1000f, 0.17f, 200f, 0.1f, 50000f);
        // Acrylic (PMMA): 1190, 3.2 GPa, 0.37 (ASHBY); loss 0.03 (BH and CHP, plexiglass 0.02-0.04); H 200 MPa,
        // 70 MPa (ASHBY); ISO 1500, 0.20, 10000; flows near 160 C (EST); 0.3 % (D570).
        Solid(reg, "Acrylic", 49, like: "Plastic", 1190f, 3.2f, 0.03f);
        Matter(reg, "Acrylic", "polymer", 0.37f, 0.3f, 200f, 0.0002f, 70f, 1500f, 0.20f, 160f, 0.3f, 10000f);
        // Polycarbonate: 1200, 2.3 GPa, 0.37, 65 MPa (ASHBY); loss 0.015 (EST, its low room-temperature loss
        // tangent); ISO 1200, 0.20, 5000; melts near 260 C; 0.15 % (D570).
        Solid(reg, "Polycarbonate", 50, like: "Plastic", 1200f, 2.3f, 0.015f);
        Matter(reg, "Polycarbonate", "polymer", 0.37f, 0.3f, 150f, 0.0002f, 65f, 1200f, 0.20f, 260f, 0.15f, 5000f);
        // Nylon 66, dry: 1140, 2.5 GPa, 0.39, 75 MPa (ASHBY); loss 0.03 (EST); ISO 1700, 0.25, 50000; melts 255 C;
        // 1.3 % in 24 h (D570).
        Solid(reg, "Nylon", 51, like: "Plastic", 1140f, 2.5f, 0.03f);
        Matter(reg, "Nylon", "polymer", 0.39f, 0.3f, 120f, 0.0008f, 75f, 1700f, 0.25f, 255f, 1.3f, 50000f);
        // Rubber (as it was: a soft sole, EVA and rubber): Poisson 0.48 (ASHBY elastomers); Shore A 60 (EST,
        // 3 MPa); 15 MPa; ISO natural rubber 1100, 0.13, 10000; decomposes, never melts.
        Matter(reg, "Rubber", "polymer", 0.48f, 0.3f, 3f, 0.005f, 15f, 1100f, 0.13f, null, 1f, 10000f);
        // A boot sole, hard rubber: ISO hard rubber 1400, 0.17; harder (EST).
        Matter(reg, "BootRubber", "polymer", 0.48f, 0.3f, 10f, 0.005f, 15f, 1400f, 0.17f, null, 1f, 10000f);
        // Flexible open-cell polyurethane foam: 30 kg/m3, E about 50 kPa (ASHBY flexible foams 10-100 kPa);
        // loss 0.3 (EST); ISO 1400, 0.04; mu 3 (EST, open cell); it holds many times its weight of water (EST).
        reg["Foam"] = new MaterialProperties
        {
            // 50 mm open-cell foam: about 0.08 at 125 Hz, 0.6 at 500, 0.9 and up from 1 kHz (EST from the
            // published curves for 50 mm melamine and polyurethane panels).
            Absorption = 0.75f, AbsorptionLow = 0.2f, AbsorptionMid = 0.9f, AbsorptionHigh = 0.95f, Scattering = 0.1f,
            TransmissionLow = 0.8f, TransmissionMid = 0.7f, TransmissionHigh = 0.5f, ResonanceIndex = 52,
            DensityKgM3 = 30f, YoungsModulusGPa = 0.00005f, LossFactor = 0.3f, Porous = true,
        };
        Matter(reg, "Foam", "polymer", 0.3f, 0f, 0.02f, 0.5f, 0.1f, 1400f, 0.04f, null, 1000f, 3f);

        // ── Ground ─────────────────────────────────────────────────────────────────────────────────────
        // "Dirt" is soil: ISO clay or silt 1670, 1.5, mu 50; bearing about 1 MPa (EST); 25 % at field capacity (EST).
        Matter(reg, "Dirt", "ground", 0.3f, 0f, 1f, 2f, 0.01f, 1670f, 1.5f, null, 25f, 50f);
        // Sand, dry: 1600 kg/m3, 30 MPa (EST, loose to medium dense 10-50 MPa), damping ratio 5 % (SOIL); ISO sand
        // and gravel 910, 2.0, 50; 22 % fills its pores (EST from 35 % porosity); quartz melts near 1700 C.
        Solid(reg, "Sand", 53, like: "Dirt", 1600f, 0.03f, 0.1f);
        Matter(reg, "Sand", "ground", 0.3f, 0f, 0.3f, 0.5f, 0f, 910f, 2.0f, 1700f, 22f, 50f);
        // Clay, firm and moist: 1800, 30 MPa (EST, firm clay 5-50 MPa), Poisson 0.4, damping 3 % (SOIL); ISO
        // 1670, 1.5, 50; 40 % (EST); fires to ceramic rather than melting. Not porous: a cob wall is airtight.
        Solid(reg, "Clay", 54, like: "Dirt", 1800f, 0.03f, 0.06f, porous: false);
        Matter(reg, "Clay", "ground", 0.4f, 0f, 0.2f, 0.5f, 0.02f, 1670f, 1.5f, null, 40f, 50f);
        // Gravel is a heap of stone: ISO sand and gravel 910, 2.0, 50; the stones melt near 1250 C (EST).
        Matter(reg, "Gravel", "ground", 0.3f, 0f, 1f, 20f, 0f, 910f, 2.0f, 1250f, 3f, 50f);
        // Grass is turf on soil: the soil's figures, wetter (EST).
        Matter(reg, "Grass", "ground", 0.3f, 0f, 0.5f, 20f, 0f, 1670f, 1.5f, null, 30f, 50f);
        // Asphalt: ISO 1000, 0.70, 50000; bitumen softens near 50 C (EN 1427 ring and ball, paving grades);
        // Poisson 0.35, viscoelastic (EST); texture depth about 1 mm (EST).
        Matter(reg, "Asphalt", "ground", 0.35f, 0.3f, 10f, 1f, 2f, 1000f, 0.70f, 50f, 0.5f, 50000f);
        // Ice near -10 C: 917 kg/m3, 9 GPa, 0.33, tensile 1.5 MPa (ICE); Vickers about 60 MPa (EST from Barnes and
        // Tabor 1966); loss 0.005 (EST); ISO 2000, 2.3. Its acoustics are still water's.
        Solid(reg, "Ice", 55, like: "Water", 917f, 9f, 0.005f);
        Matter(reg, "Ice", "ground", 0.33f, 0f, 60f, 0.0001f, 1.5f, 2000f, 2.3f, 0f, 0f, Tight);
        // Settled snow, 300 kg/m3: E about 2 MPa (SNOW, 1-10 MPa at this density), Poisson 0.2, tensile 0.02
        // MPa (SNOW); k 0.13 (Sturm's regression); loss 0.2 (EST); holds 5 % liquid water (EST).
        reg["Snow"] = new MaterialProperties
        {
            // Fresh snow is one of the best absorbers outdoors: about 0.45 at 125 Hz, 0.75 at 500, over 0.9 from
            // 1 kHz (EST from published impedance-tube measurements of snow).
            Absorption = 0.75f, AbsorptionLow = 0.5f, AbsorptionMid = 0.85f, AbsorptionHigh = 0.9f, Scattering = 0.6f,
            TransmissionLow = 0.3f, TransmissionMid = 0.2f, TransmissionHigh = 0.1f, ResonanceIndex = 56,
            DensityKgM3 = 300f, YoungsModulusGPa = 0.002f, LossFactor = 0.2f, Porous = true,
        };
        Matter(reg, "Snow", "ground", 0.2f, 0f, 0.05f, 1f, 0.02f, 2050f, 0.13f, 0f, 5f, 2f);

        // ── Soft and living ─────────────────────────────────────────────────────────────────────────────
        // Leather: Poisson 0.4, 20 MPa (EST, tensile 8-25), k 0.16 (EST, 0.14-0.18), cp 1500 (EST); it chars.
        Matter(reg, "Leather", "soft", 0.4f, 0.3f, 20f, 0.02f, 20f, 1500f, 0.16f, null, 30f, 1000f);
        // Skin and the soft tissue under it: Poisson 0.49, cp 3500, k 0.37 (DUCK); 15 MPa (EST).
        Matter(reg, "Skin", "soft", 0.49f, 0f, 0.05f, 0.03f, 15f, 3500f, 0.37f, null, 5f, 100f);
        // A crowd is people: tissue's figures (DUCK).
        Matter(reg, "Audience", "soft", 0.45f, 0f, 0.1f, 10f, 1f, 3500f, 0.37f, null, 0f, 100f);
        // Carpet: ISO textile floor covering 1300, 0.06, mu 5; a nylon or polypropylene pile melts near 220 C (EST).
        Matter(reg, "Carpet", "soft", 0.3f, 0f, 0.1f, 2f, 5f, 1300f, 0.06f, 220f, 20f, 5f);
        // Leaves are mostly water (EST: moisture about 100 % of dry mass).
        Matter(reg, "Foliage", "soft", 0.3f, 0f, 0.1f, 5f, 1f, 2000f, 0.3f, null, 100f, 10f);
        // Fabric, a heavy curtain or upholstery layer: cotton cp 1300, k 0.06 (EST, engineering tables); its
        // bulk is soft (EST, 0.1 MPa); it chars.
        reg["Fabric"] = new MaterialProperties
        {
            // Heavy velour, draped: 0.14 at 125 Hz, 0.55 at 500, about 0.7 from 1 kHz (the classic published curve).
            Absorption = 0.55f, AbsorptionLow = 0.25f, AbsorptionMid = 0.7f, AbsorptionHigh = 0.65f, Scattering = 0.5f,
            TransmissionLow = 0.7f, TransmissionMid = 0.5f, TransmissionHigh = 0.3f, ResonanceIndex = 57,
            DensityKgM3 = 300f, YoungsModulusGPa = 0.0001f, LossFactor = 0.3f, Porous = true,
        };
        Matter(reg, "Fabric", "soft", 0.3f, 0f, 0.05f, 0.5f, 10f, 1300f, 0.06f, null, 25f, 5f);

        // ── Building boards ─────────────────────────────────────────────────────────────────────────────
        // Gypsum plasterboard: Poisson 0.2 (EST); ISO 1000, 0.25, mu 8 (between 10 dry and 4 wet); calcines
        // rather than melting; 5 MPa flexural (EST, EN 520 boards).
        Matter(reg, "Plaster", "building", 0.2f, 0f, 20f, 0.05f, 5f, 1000f, 0.25f, null, 10f, 8f);
        // Mineral-fibre ceiling tile: ISO mineral wool 1030, 0.04, mu 1.
        Matter(reg, "AcousticTile", "building", 0.2f, 0f, 0.5f, 1f, 0.5f, 1030f, 0.04f, null, 1f, 1f);

        // ── Liquids, and the rest ───────────────────────────────────────────────────────────────────────
        // Water at 20 C: a liquid, Poisson 0.5; cp 4182, k 0.6; freezes (and ice melts) at 0 C.
        Matter(reg, "Water", "liquid", 0.5f, 0f, 0f, 0f, 0f, 4182f, 0.6f, 0f, 0f, 0f);
        // Generic stands for something unnamed: a plastic, as its mechanical figures already were (EST).
        Matter(reg, "Generic", "polymer", 0.35f, 0.3f, 100f, 0.001f, 40f, 1500f, 0.2f, 220f, 0.5f, 10000f);
        if (reg.TryGetValue("None", out var none)) { none.Family = "none"; reg["None"] = none; }
    }

    /// <summary>A new material: its mechanics, and the face (absorption, scattering, transmission and
    /// porosity) of <paramref name="like"/>, the existing material whose surface it shares.</summary>
    private static void Solid(Dictionary<string, MaterialProperties> reg, string name, int index, string like,
                              float densityKgM3, float youngsGPa, float lossAt1kHz, bool? porous = null)
    {
        var p = reg[like];
        p.ResonanceIndex = index;
        p.DensityKgM3 = densityKgM3;
        p.YoungsModulusGPa = youngsGPa;
        p.LossFactor = lossAt1kHz;
        if (porous is { } isPorous) p.Porous = isPorous;
        reg[name] = p;
    }

    /// <summary>The properties beyond sound for a material already in the table.</summary>
    private static void Matter(Dictionary<string, MaterialProperties> reg, string name, string family,
                               float poisson, float lossExponent, float hardnessMPa, float roughnessMm,
                               float strengthMPa, float specificHeat, float conductivity, float? meltingC,
                               float waterUptakePercent, float vapourResistance, float acrossGrainGPa = 0f)
    {
        var p = reg[name];
        p.Family = family;
        p.PoissonRatio = poisson;
        p.LossExponent = lossExponent;
        p.HardnessMPa = hardnessMPa;
        p.RoughnessMm = roughnessMm;
        p.StrengthMPa = strengthMPa;
        p.SpecificHeatJKgK = specificHeat;
        p.ThermalConductivityWmK = conductivity;
        p.MeltingPointC = meltingC;
        p.WaterUptakePercent = waterUptakePercent;
        p.VapourResistance = vapourResistance;
        p.TransverseModulusGPa = acrossGrainGPa;
        reg[name] = p;
    }

    /// <summary>Other names for materials in the table: what a builder or a map may call them. "Metal" is mild
    /// steel. An alias reads its material's properties and index; it is not listed in KnownMaterials.</summary>
    private static readonly Dictionary<string, string> Aliases = new(System.StringComparer.OrdinalIgnoreCase)
    {
        ["Steel"] = "Metal", ["MildSteel"] = "Metal", ["Stainless"] = "StainlessSteel",
        ["Aluminum"] = "Aluminium", ["SodaLimeGlass"] = "Glass", ["Soil"] = "Dirt",
        ["Porcelain"] = "Tile", ["CeramicTile"] = "Tile", ["Plasterboard"] = "Plaster", ["Gypsum"] = "Plaster",
    };

    /// <summary>The table's own name for <paramref name="type"/>: itself, or what it is an alias of.</summary>
    public static string Canonical(string type)
        => !string.IsNullOrEmpty(type) && Aliases.TryGetValue(type, out var real) ? real : type;

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
        return !string.IsNullOrEmpty(type) && _registry.ContainsKey(Canonical(type));
    }

    /// <summary>A material name's ResonanceIndex, as region face arrays store it, so maps can be authored
    /// by name.</summary>
    public static bool TryGetResonanceIndex(string type, out int index)
    {
        EnsureInitialized();
        index = 0;
        if (string.IsNullOrEmpty(type)) return false;
        if (!_registry.TryGetValue(Canonical(type), out var props)) return false;
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

    /// <summary>
    /// A material's properties by name (or alias). An unknown name is "Generic", a plastic, and says so once
    /// in the log: a misspelt "Steel" used to build a sheet-metal part out of plastic without a word.
    /// </summary>
    public static MaterialProperties GetProperties(string type)
    {
        // Without it a call before initialisation threw KeyNotFoundException on "Generic".
        EnsureInitialized();
        if (string.IsNullOrEmpty(type)) return _registry["Generic"];
        if (_registry.TryGetValue(type, out var props)) return props;
        if (Aliases.TryGetValue(type, out var real) && _registry.TryGetValue(real, out props)) return props;
        WarnOnce(type, $"material '{type}' is not in the table; it sounds as 'Generic', a plastic");
        return _registry["Generic"];
    }

    public static MaterialProperties GetPropertiesByResonanceIndex(int index)
    {
        EnsureInitialized();
        foreach (var props in _registry.Values)
        {
            if (props.ResonanceIndex == index) return props;
        }
        WarnOnce("#" + index, $"no material has resonance index {index}; it sounds as 'Generic', a plastic");
        return _registry["Generic"];
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _warned = new(System.StringComparer.OrdinalIgnoreCase);

    /// <summary>Every unknown name (or "#index") the table has been asked for and warned about.</summary>
    public static IReadOnlyCollection<string> UnknownNamesAsked => (IReadOnlyCollection<string>)_warned.Keys;

    /// <summary>Logged once per name: GetProperties sits in loops that run every frame.</summary>
    private static void WarnOnce(string key, string what)
    {
        if (!_warned.TryAdd(key, true)) return;
        Serilog.Log.Warning("[MATERIAL] AcousticRegistry: {What}. Known: {Known}", what, string.Join(", ", KnownMaterials()));
    }
}

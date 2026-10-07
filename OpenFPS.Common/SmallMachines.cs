using System.Collections.Generic;
using OpenFPS.Common.Editing;

namespace OpenFPS.Common;

/// <summary>
/// What holds a small engine at one speed: flyweights against a spring open the throttle as it slows,
/// so the note is constant and what changes is the load. A proportional governor trades speed for
/// throttle (the droop, 5-10 % on a mechanical one): the bog in thick grass, and the response is the
/// recovery.
/// </summary>
public sealed record GovernorSpec
{
    /// <summary>Where the spring is set: the speed with nothing to do, rpm.</summary>
    [Tunable("rpm", 1000, 6000, "Where the governor spring is set: the speed with nothing to do.", Label = "governed speed", Step = 50)]
    public required float SettingRpm { get; init; }

    /// <summary>Speed given up between no load and full throttle, as a fraction of the setting.
    /// Mechanical governors droop 5-10 %; an electronic one on a generator droops almost nothing.</summary>
    [Tunable("", 0, 0.3, "Speed given up from no load to full throttle, as a fraction of the setting. Mechanical governors droop 0.05 to 0.1.", Step = 0.01, Source = "mechanical governor droop, 5 to 10 per cent")]
    public float Droop { get; init; } = 0.07f;

    /// <summary>How fast the flyweights and the spring get there, Hz. Small and light: a mower
    /// governor is audibly hunting at a few hertz when it is badly adjusted.</summary>
    [Tunable("Hz", 0.5, 20, "How fast the flyweights and spring respond. A badly adjusted mower hunts at a few hertz.", Label = "response", Step = 0.5)]
    public float ResponseHz { get; init; } = 6f;

    /// <summary>Smallest throttle the linkage will close to while it is running.</summary>
    [Tunable("", 0, 0.5, "The smallest throttle the linkage closes to while running.", Label = "minimum throttle", Step = 0.01)]
    public float MinThrottle { get; init; } = 0.08f;

    /// <summary>The throttle this governor asks for at a speed, given the load it is carrying.</summary>
    public float Throttle(float rpm)
    {
        // The droop is the proportional band: shut at the setting, wide open a full droop under it.
        float band = MathF.Max(0.005f, Droop) * SettingRpm;
        float error = SettingRpm - rpm;
        return Math.Clamp(error / band, MinThrottle, 1f);
    }
}

/// <summary>
/// A rotary mower deck, the steel pan the blade runs inside and most of the colour of its noise: a
/// cavity stopped at the top, with a quarter-wave depth mode and a half-wave mode across. A 21 inch
/// deck 10 cm deep is about 860 and 320 Hz.
/// </summary>
public sealed record MowerDeckSpec
{
    [Tunable("m", 0.2, 2, "Diameter of the deck pan. The width mode is a half wave across it.", Label = "diameter", Step = 0.01)]
    public required float DiameterMetres { get; init; }
    [Tunable("m", 0.03, 0.3, "Depth of the deck pan. The depth mode is a quarter wave of it.", Label = "depth", Step = 0.005)]
    public required float DepthMetres { get; init; }
    /// <summary>Pan thickness, millimetres.</summary>
    [Tunable("mm", 0.5, 6, "Pan thickness: a cheap stamped deck rings more than a cast one.", Label = "thickness", Step = 0.1)]
    public float ThicknessMm { get; init; } = 1.5f;
    /// <summary>A material in the <see cref="AcousticRegistry"/>. Steel is "Metal": "Steel" silently
    /// gets Generic (1,200 kg/m³ and 5 GPa, a plastic), and the deck had the modes of a bucket.</summary>
    [Tunable("", 0, 0, "What it is made of. Steel is Metal.", Choices = "materials")]
    public string Material { get; init; } = "Metal";
    /// <summary>How sharply the cavity modes stand out: low, because an open-bottomed pan leaks.</summary>
    [Tunable("", 0.5, 20, "How sharply the deck cavity modes stand out. An open-bottomed pan leaks, so these are low.", Label = "cavity sharpness", Step = 0.5)]
    public float CavityQ { get; init; } = 3.5f;
    /// <summary>How much of the blade's noise goes out through the pan rather than straight out of
    /// the bottom, 0..1.</summary>
    [Tunable("", 0, 1, "How much of the blade noise leaves through the pan rather than straight out of the bottom.", Label = "pan share", Step = 0.05)]
    public float PanShare { get; init; } = 0.45f;

    /// <summary>The depth mode: a quarter wave in a cavity stopped at one end.</summary>
    public float DepthModeHz => 343f / (4f * MathF.Max(0.02f, DepthMetres));
    /// <summary>The mode across the pan: a half wave over its diameter.</summary>
    public float WidthModeHz => 343f / (2f * MathF.Max(0.1f, DiameterMetres));
}

/// <summary>
/// Grass being cut: the tip arrives at 70-80 m/s and each stalk fails in one blow, so cutting is a
/// Poisson train of snaps at stalks per square metre times swath times walking speed, under the blade
/// passing. A mower standing still makes none of it.
/// </summary>
public sealed record CuttingSpec
{
    /// <summary>Standing shoots per square metre. A mown lawn is ten to twenty thousand.</summary>
    [Tunable("per m²", 0, 50000, "Standing shoots per square metre. A mown lawn is ten to twenty thousand.", Label = "stalks per square metre", Step = 1000)]
    public float StalksPerSquareMetre { get; init; } = 15000f;

    /// <summary>What one clipping weighs, milligrams. This and the tip speed are the whole of how
    /// loud the cutting is: see <see cref="ImpactDb"/>.</summary>
    [Tunable("mg", 0.5, 50, "What one clipping weighs. With the tip speed, this is how loud the cutting is.", Label = "clipping weight", Step = 0.5)]
    public float ClippingMilligrams { get; init; } = 5f;

    /// <summary>Where a clipping hitting the pan puts its energy, Hz.</summary>
    [Tunable("Hz", 500, 10000, "Where a clipping hitting the pan puts its energy.", Label = "clipping pitch", Step = 100)]
    public float CentreHz { get; init; } = 3200f;
    [Tunable("", 0.3, 10, "How narrow the clipping band is.", Label = "clipping sharpness", Step = 0.1)]
    public float Q { get; init; } = 1.1f;

    /// <summary>
    /// One clipping striking the deck at the tip speed, dB at one metre, through
    /// <see cref="PanelAcoustics.ImpactReferenceDb"/> (one joule is 74 dB at a metre): 5 mg at 80 m/s is
    /// 16 mJ, about 56 dB, a hiss in the fifties against a machine in the nineties. Left that quiet: what
    /// is heard in grass is the engine bogging and the blade loading, from the load path.
    /// </summary>
    public float ImpactDb(float tipSpeedMps)
    {
        float joules = 0.5f * ClippingMilligrams * 1e-6f * tipSpeedMps * tipSpeedMps;
        return joules <= 0f ? 0f : PanelAcoustics.ImpactReferenceDb + 10f * MathF.Log10(joules);
    }
}

/// <summary>
/// A hermetic compressor, a motor and pump welded inside a steel can. The hum is the magnetic pull,
/// at twice the line frequency (120 Hz in North America, 100 in Europe) whatever the shaft does. The
/// pump is a second series at the shaft, near 58 Hz for a two-pole motor, and the beat between them
/// makes it restless. The can's ring filters all of it.
/// </summary>
public sealed record CompressorSpec
{
    /// <summary>Mains frequency, Hz. The hum is at twice this.</summary>
    [Tunable("Hz", 45, 65, "Mains frequency. The hum is twice this: 120 Hz in North America, 100 Hz in Europe.", Label = "mains frequency", Step = 1, Source = "the magnetic pull pulses at twice the line frequency")]
    public float LineHz { get; init; } = 60f;
    /// <summary>Pole PAIRS. One pair is a nominal 3,600 rpm on 60 Hz.</summary>
    [Tunable("", 1, 4, "Pole pairs of the motor. One pair is a nominal 3,600 rpm on 60 Hz.", Label = "pole pairs")]
    public int PolePairs { get; init; } = 1;
    /// <summary>How far the rotor falls behind the field under load, as a fraction. A few per cent.</summary>
    [Tunable("", 0, 0.2, "How far the rotor falls behind the field under load, as a fraction. A few per cent.", Step = 0.005)]
    public float Slip { get; init; } = 0.04f;

    /// <summary>Compression events per revolution: 1 for a scroll or a rotary, one per cylinder for
    /// a reciprocating machine.</summary>
    [Tunable("", 1, 6, "Compressions per revolution: 1 for a scroll or rotary, one per cylinder for a reciprocating pump.", Label = "compressions per revolution")]
    public int EventsPerRevolution { get; init; } = 1;

    /// <summary>The magnetic hum at one metre, dB, with the can around it.</summary>
    [Tunable("dB", 30, 90, "The magnetic hum at one metre, with the can around it.", Label = "hum level", Step = 1)]
    public float HumDb { get; init; } = 62f;
    /// <summary>The gas pulsation at one metre, dB.</summary>
    [Tunable("dB", 30, 90, "The gas pulsation of the pump at one metre.", Label = "pumping level", Step = 1)]
    public float PulsationDb { get; init; } = 58f;
    /// <summary>Gas rushing in the discharge line: broadband, and the only part of a compressor that
    /// is not a tone.</summary>
    [Tunable("dB", 20, 80, "Gas rushing in the discharge line: the broadband part.", Label = "gas flow level", Step = 1)]
    public float FlowDb { get; init; } = 48f;

    /// <summary>The can's shell ring, Hz, and how sharp it is.</summary>
    [Tunable("Hz", 100, 3000, "Where the compressor can rings.", Label = "shell ring", Step = 10)]
    public float ShellHz { get; init; } = 520f;
    [Tunable("", 0.5, 30, "How sharply the can rings.", Label = "shell ring sharpness", Step = 0.5)]
    public float ShellQ { get; init; } = 6f;

    /// <summary>How long the motor takes to come up to speed against the pump, seconds: the growl
    /// before the hum settles.</summary>
    [Tunable("s", 0, 5, "How long the motor takes to come up to speed: the growl before the hum settles.", Label = "start time", Step = 0.05)]
    public float StartSeconds { get; init; } = 0.55f;

    /// <summary>The shaft speed under load, rpm.</summary>
    public float ShaftRpm => ShaftRpmAt(1f);
    /// <summary>The hum: twice the LINE frequency, not twice the shaft.</summary>
    public float HumHz => 2f * LineHz;
    /// <summary>The pumping series' fundamental.</summary>
    public float PulsationHz => PulsationHzAt(1f);

    /// <summary>The shaft speed at a load (1 = the rating), rpm: an induction motor's slip goes with its
    /// torque, so no two compressors on a street turn at quite the same speed.</summary>
    public float ShaftRpmAt(float load)
        => LineHz * 60f / MathF.Max(1, PolePairs) * (1f - Math.Clamp(Slip * MathF.Max(0f, load), 0f, 0.5f));

    /// <summary>The pumping series' fundamental at a load.</summary>
    public float PulsationHzAt(float load) => ShaftRpmAt(load) / 60f * MathF.Max(1, EventsPerRevolution);

    /// <summary>
    /// How hard the pump works at this outdoor temperature against its rating (1): its torque goes with
    /// the lift. The coil evaporates near 7 °C and the condenser runs about 11 K over the air, so the
    /// lift is 39 K at the 35 °C rating point (AHRI 210/240) and 29 on a 25 °C evening.
    /// </summary>
    public static float LoadAt(float outdoorCelsius) => Math.Clamp((outdoorCelsius + 11f - 7f) / 39f, 0.2f, 1.4f);
}

/// <summary>
/// The house an air conditioner cools, as its thermostat sees it. The share of the time the compressor
/// runs goes from nothing at the balance point to all of it at the design temperature; how often it
/// cycles follows NEMA DC 3, N = Nmax 4 D (1 - D) with Nmax = 3 an hour for cooling.
/// </summary>
public sealed record ThermostatSpec
{
    /// <summary>The outdoor temperature at which the house needs no cooling, °C: the setpoint (24) less
    /// what the house's own heat is worth.</summary>
    public float BalanceCelsius { get; init; } = 18f;
    /// <summary>The outdoor temperature it was sized to keep up with running flat out, °C (a 1 % cooling
    /// design day in the southern half of the US).</summary>
    public float DesignCelsius { get; init; } = 35f;
    /// <summary>Cycles an hour at half duty, the most there are.</summary>
    public float MaxCyclesPerHour { get; init; } = 3f;
    /// <summary>How far one house's balance point is from the next, ±°C: insulation, shade, how many
    /// people are in.</summary>
    public float BalanceSpreadCelsius { get; init; } = 2f;

    /// <summary>The share of the time the compressor runs at an outdoor temperature.</summary>
    public float Duty(float outdoorCelsius, float balanceOffset = 0f)
    {
        float balance = BalanceCelsius + balanceOffset;
        return Math.Clamp((outdoorCelsius - balance) / MathF.Max(1f, DesignCelsius - balance), 0f, 1f);
    }

    /// <summary>On-off cycles an hour at a duty.</summary>
    public float CyclesPerHour(float duty) => MaxCyclesPerHour * 4f * Math.Clamp(duty, 0f, 1f) * (1f - Math.Clamp(duty, 0f, 1f));
}

/// <summary>The sheet-metal cabinet as a panel that rings (<see cref="PanelAcoustics"/>): a big thin
/// one booms and a small thick one knocks.</summary>
public sealed record CasingSpec
{
    [Tunable("m", 0.1, 3, "Width of the cabinet panel.", Label = "width", Step = 0.05)]
    public required float WidthMetres { get; init; }
    [Tunable("m", 0.1, 3, "Height of the cabinet panel.", Label = "height", Step = 0.05)]
    public required float HeightMetres { get; init; }
    [Tunable("mm", 0.3, 5, "Sheet thickness of the cabinet. Thin and big booms; small and thick knocks.", Label = "thickness", Step = 0.1)]
    public float ThicknessMm { get; init; } = 0.8f;
    /// <summary>A material in the <see cref="AcousticRegistry"/>; steel is "Metal" there.</summary>
    [Tunable("", 0, 0, "What it is made of. Steel is Metal.", Choices = "materials")]
    public string Material { get; init; } = "Metal";
    /// <summary>How much of the machine's vibration gets into the panel, 0..1.</summary>
    [Tunable("", 0, 1, "How much of the machine's vibration gets into the panel. Rubber grommets keep it small.", Step = 0.05)]
    public float Coupling { get; init; } = 0.25f;

    public float RingHz => PanelAcoustics.RingHz(
        AcousticRegistry.GetProperties(Material), WidthMetres, HeightMetres, ThicknessMm / 1000f);
}

/// <summary>
/// A machine that stands still and runs (a mower, an air-conditioning condenser, a generator, a pump),
/// as a parts list with dimensions like a vehicle: a mower is an engine under a governor with a blade
/// in a pan, a condenser a fan and a compressor in a box. docs/YARD_MACHINES.md.
/// </summary>
public sealed record SmallMachineSpec
{
    [Tunable("", 0, 0, "What this machine is called.", Label = "name")]
    public required string Name { get; init; }

    /// <summary>An <see cref="EngineProfile"/> preset key, for a machine with a piston engine.</summary>
    public string? EngineKey { get; init; }
    /// <summary>What holds that engine's speed.</summary>
    public GovernorSpec? Governor { get; init; }
    /// <summary>Rotating inertia the crank sees through whatever it drives, kg m^2.</summary>
    [Tunable("kg m²", 0.001, 1, "Rotating inertia the crank sees through what it drives. A mower blade is the flywheel.", Label = "driven inertia", Step = 0.001)]
    public float DrivenInertiaKgM2 { get; init; } = 0.01f;

    /// <summary>A row of blades: a mower blade, a condenser fan, a blower. The same
    /// <see cref="BladeRowSpec"/> a propeller uses.</summary>
    public BladeRowSpec? Blade { get; init; }
    /// <summary>How many such rows. A 42 inch mower deck is two 21 inch blades side by side.</summary>
    [Tunable("", 1, 4, "How many rows of blades. A 42 inch deck is two 21 inch blades.", Label = "blade rows")]
    public int BladeRows { get; init; } = 1;
    /// <summary>Blade speed over crank speed. A mower blade is bolted to the crankshaft: 1.</summary>
    [Tunable("", 0.1, 10, "Blade speed over crank speed. A mower blade bolted to the crankshaft is 1.", Label = "blade gear ratio", Step = 0.05)]
    public float BladeGearRatio { get; init; } = 1f;

    public MowerDeckSpec? Deck { get; init; }
    public CuttingSpec? Cutting { get; init; }

    /// <summary>The compressor, for a machine that is refrigeration rather than combustion.</summary>
    public CompressorSpec? Compressor { get; init; }

    /// <summary>What calls for the compressor: the house it cools. Defaults when a compressor has none.</summary>
    public ThermostatSpec? Thermostat { get; init; }

    public CasingSpec? Casing { get; init; }

    /// <summary>Overall level at one metre, for placing the voice: measured with
    /// <c>--yard levels</c>, not chosen.</summary>
    [Tunable("dB", 30, 120, "Overall level at one metre, used to place the voice. Measured with --yard levels, not chosen.", Label = "source level", Step = 1, Source = "measured with AudioLab --yard levels")]
    public required float SourceLevelDb { get; init; }

    /// <summary>How big the machine is acoustically, the distance between the parts it radiates from:
    /// inside it the level is flat, as for a car's outlet separation.</summary>
    [Tunable("m", 0.1, 10, "How big the machine is acoustically: inside this the level is flat.", Label = "size", Step = 0.1)]
    public float ExtentMetres { get; init; } = 0.8f;

    // ── Presets ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A walk-behind rotary mower: a 163 cc OHV single governed at 2,900 rpm, its 21 inch blade bolted
    /// to the crankshaft. The blade-passing tone (about 97 Hz) is locked to the engine's firing (24 Hz),
    /// one note; and the blade is the flywheel, 0.014 kg m², more than the engine's own, so the
    /// governor's recovery takes the best part of a second.
    /// </summary>
    public static SmallMachineSpec PushMower => new()
    {
        Name = "Walk-behind rotary mower, 163 cc",
        EngineKey = "mower_single",
        Governor = new GovernorSpec { SettingRpm = 2900f, Droop = 0.09f, ResponseHz = 5f },
        // A 0.53 m steel bar of 0.6 kg about its centre: mL^2/12.
        DrivenInertiaKgM2 = 0.014f,
        Blade = new BladeRowSpec
        {
            Blades = 2, DiameterMetres = 0.53f, ChordMetres = 0.055f,
            // A flat bar with a bent lift wing, not an aerofoil: blunt, which is why it roars.
            ThicknessRatio = 0.20f,
            RpmMax = 3200f, RpmIdle = 1200f,
            // The broadband roar is the machine: an electric mower, a blade in a deck and nothing
            // else, is about 88 dB at the operator.
            ReferenceDb = 93f, SelfNoiseDb = 88f, BladeScatter = 0.02f,
        },
        Deck = new MowerDeckSpec { DiameterMetres = 0.53f, DepthMetres = 0.095f, ThicknessMm = 1.6f },
        Cutting = new CuttingSpec(),
        Casing = null,
        // Measured with `--yard mower_push levels`, mowing at 1.15 m/s in ordinary grass: 92 dB, the
        // engine 91 and the blade in its deck 85.
        SourceLevelDb = 92f,
        ExtentMetres = 0.6f,
    };

    /// <summary>
    /// A lawn tractor: a 500 cc V-twin governed at 3,200 rpm, belted 1:1 to a 42 inch deck of two 21 inch
    /// blades. Firing once a revolution, an octave above the push mower; the two rows' 100 Hz tones beat
    /// slowly; and twice the swath makes the cutting the loudest thing about it from a distance.
    /// </summary>
    public static SmallMachineSpec RidingMower => new()
    {
        Name = "Lawn tractor, 500 cc twin, 42 inch deck",
        EngineKey = "mower_twin",
        Governor = new GovernorSpec { SettingRpm = 3200f, Droop = 0.06f, ResponseHz = 4f },
        DrivenInertiaKgM2 = 0.05f,
        Blade = new BladeRowSpec
        {
            Blades = 2, DiameterMetres = 0.53f, ChordMetres = 0.06f, ThicknessRatio = 0.20f,
            // Per row: two sum incoherently, three decibels over a 21 inch deck.
            RpmMax = 3400f, RpmIdle = 1400f, ReferenceDb = 95f, SelfNoiseDb = 87f, BladeScatter = 0.025f,
        },
        BladeRows = 2,
        Deck = new MowerDeckSpec { DiameterMetres = 1.07f, DepthMetres = 0.11f, ThicknessMm = 2.0f, PanShare = 0.5f },
        Cutting = new CuttingSpec(),
        SourceLevelDb = 96f,
        ExtentMetres = 1.6f,
    };

    /// <summary>
    /// A three-ton split-system condenser: a scroll compressor in a steel cabinet, a 20 inch fan in the
    /// lid. The fan is broadband (Mach 0.065 at the tips, its 42 Hz blade tone only a rumble); the
    /// compressor hums at 120 Hz with its 57 Hz pumping. Across a street you hear the hum, underneath
    /// it the fan.
    /// </summary>
    public static SmallMachineSpec AirConditionerCondenser => new()
    {
        Name = "Condenser unit, 3 ton",
        Blade = new BladeRowSpec
        {
            Blades = 3, DiameterMetres = 0.50f, ChordMetres = 0.11f, ThicknessRatio = 0.06f,
            // A condenser fan alone, compressor off, is about 63 dB at a metre, all rush of air.
            RpmMax = 840f, RpmIdle = 840f, ReferenceDb = 66f, SelfNoiseDb = 63f, BladeScatter = 0.03f,
        },
        Compressor = new CompressorSpec
        {
            LineHz = 60f, PolePairs = 1, Slip = 0.042f, EventsPerRevolution = 1,
            HumDb = 63f, PulsationDb = 57f, FlowDb = 47f,
            ShellHz = 520f, ShellQ = 6f, StartSeconds = 0.6f,
        },
        Thermostat = new ThermostatSpec(),
        Casing = new CasingSpec { WidthMetres = 0.8f, HeightMetres = 0.9f, ThicknessMm = 0.8f, Coupling = 0.3f },
        // Measured at 65 dB at a metre, a modern quiet unit: makers quote a sound power near 72 dB(A),
        // which over a hemisphere at a metre is 64.
        SourceLevelDb = 65f,
        ExtentMetres = 0.9f,
    };

    /// <summary>A window unit, heard from the street: a small slow fan, a rotary compressor humming at
    /// 120 Hz, and a thinner, smaller cabinet that rings higher, so it rattles where a condenser drones.</summary>
    public static SmallMachineSpec AirConditionerWindow => new()
    {
        Name = "Window air conditioner",
        Blade = new BladeRowSpec
        {
            Blades = 3, DiameterMetres = 0.26f, ChordMetres = 0.06f, ThicknessRatio = 0.07f,
            RpmMax = 1050f, RpmIdle = 1050f, ReferenceDb = 58f, SelfNoiseDb = 55f, BladeScatter = 0.035f,
        },
        Compressor = new CompressorSpec
        {
            LineHz = 60f, PolePairs = 1, Slip = 0.05f, EventsPerRevolution = 1,
            HumDb = 58f, PulsationDb = 54f, FlowDb = 44f,
            ShellHz = 700f, ShellQ = 7f, StartSeconds = 0.4f,
        },
        Thermostat = new ThermostatSpec(),
        Casing = new CasingSpec { WidthMetres = 0.56f, HeightMetres = 0.4f, ThicknessMm = 0.6f, Coupling = 0.4f },
        SourceLevelDb = 59f,
        ExtentMetres = 0.5f,
    };

    public static IReadOnlyDictionary<string, Func<SmallMachineSpec>> Presets { get; } =
        new Dictionary<string, Func<SmallMachineSpec>>(StringComparer.OrdinalIgnoreCase)
        {
            ["mower_push"] = () => PushMower,
            ["mower_riding"] = () => RidingMower,
            ["ac_condenser"] = () => AirConditionerCondenser,
            ["ac_window"] = () => AirConditionerWindow,
        };

    /// <summary>A preset by name, through the <see cref="ModelLibrary"/> rather than
    /// <see cref="Presets"/>, so a map's own authored machine of that name wins.</summary>
    public static SmallMachineSpec ByName(string key) => ModelLibrary.SmallMachine(key);
}

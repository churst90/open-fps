using System;
using System.Collections.Generic;

namespace OpenFPS.Common;

/// <summary>
/// What holds a small engine at one speed.
///
/// A car engine is asked for a throttle position and does what it likes with it. A mower, a
/// generator, a pump and a chainsaw are not: a pair of flyweights on the camshaft pull against a
/// spring, and when the engine slows the spring wins and opens the throttle. That one part is why
/// yard machinery sounds the way it does — the note is CONSTANT, and everything you hear happening
/// to it is the load changing, not the operator.
///
/// Two numbers say all of it. The SETTING is where the spring is wound to, which is the no-load
/// speed. The DROOP is how much speed the governor must give up to open the throttle at all: a
/// proportional controller with no integral term cannot hold its setting under load, it can only
/// trade speed for throttle, and a mechanical governor's trade is five to ten per cent from no load
/// to full. That droop IS the bog you hear when a mower goes into thick grass, and the recovery
/// after it is the flyweights catching up, which is the third number.
/// </summary>
public sealed record GovernorSpec
{
    /// <summary>Where the spring is set: the speed with nothing to do, rpm.</summary>
    public required float SettingRpm { get; init; }

    /// <summary>Speed given up between no load and full throttle, as a fraction of the setting.
    /// Mechanical governors droop 5-10 %; an electronic one on a generator droops almost nothing.</summary>
    public float Droop { get; init; } = 0.07f;

    /// <summary>How fast the flyweights and the spring get there, Hz. Small and light: a mower
    /// governor is audibly hunting at a few hertz when it is badly adjusted.</summary>
    public float ResponseHz { get; init; } = 6f;

    /// <summary>Smallest throttle the linkage will close to while it is running.</summary>
    public float MinThrottle { get; init; } = 0.08f;

    /// <summary>The throttle this governor asks for at a speed, given the load it is carrying.</summary>
    public float Throttle(float rpm)
    {
        // Proportional, with the droop as the proportional band: at the setting it is shut, and a
        // full droop under it, it is wide open. Nothing integrates, which is why it cannot hold the
        // setting and why the speed sags under load instead.
        float band = MathF.Max(0.005f, Droop) * SettingRpm;
        float error = SettingRpm - rpm;
        return Math.Clamp(error / band, MinThrottle, 1f);
    }
}

/// <summary>
/// A rotary mower deck: the shallow steel pan the blade runs inside.
///
/// The pan is not decoration on the blade noise, it is most of the colour of it. It is a cylindrical
/// cavity open at the bottom and stopped at the top, so it has a depth mode at a quarter wave of its
/// own depth, and a diameter mode across it at a half wave — a 21 inch deck 10 cm deep is about
/// 860 Hz and 320 Hz, and those two are what make a mower a mower rather than a generator with a fan
/// on it. The steel itself rings too, and how much depends on how thick it is, which is exactly the
/// difference between a cheap stamped deck and a cast one.
/// </summary>
public sealed record MowerDeckSpec
{
    public required float DiameterMetres { get; init; }
    public required float DepthMetres { get; init; }
    /// <summary>Pan thickness, millimetres.</summary>
    public float ThicknessMm { get; init; } = 1.5f;
    /// <summary>A material in the <see cref="AcousticRegistry"/>. Steel is spelled "Metal" there,
    /// and asking for "Steel" gets Generic — 1,200 kg/m^3 and 5 GPa, which is a plastic — without
    /// complaining. That cost an hour: the deck came out with the modes of a bucket.</summary>
    public string Material { get; init; } = "Metal";
    /// <summary>How sharply the cavity modes stand out. An open-bottomed pan leaks badly, so these
    /// are low Qs: a resonance you can hear the shape of, not a note.</summary>
    public float CavityQ { get; init; } = 3.5f;
    /// <summary>How much of the blade's noise goes out through the pan rather than straight out of
    /// the bottom, 0..1.</summary>
    public float PanShare { get; init; } = 0.45f;

    /// <summary>The depth mode: a quarter wave in a cavity stopped at one end.</summary>
    public float DepthModeHz => 343f / (4f * MathF.Max(0.02f, DepthMetres));
    /// <summary>The mode across the pan: a half wave over its diameter.</summary>
    public float WidthModeHz => 343f / (2f * MathF.Max(0.1f, DiameterMetres));
}

/// <summary>
/// Grass being cut, which is a rate of very small impacts and not a texture.
///
/// A rotary blade does not saw, it hits: the tip arrives at seventy or eighty metres a second and
/// each stalk fails in one blow. So the sound of cutting is a Poisson train of tiny snaps whose RATE
/// is arithmetic — stalks per square metre, times the swath, times how fast the machine is walking —
/// and whose ENVELOPE is the blade passing, because nothing is being cut while no tip is in the
/// standing grass ahead.
///
/// It falls out of that for free that a mower standing still with the blade spinning does not make
/// this sound at all, and that pushing faster makes it louder and denser rather than just louder.
/// </summary>
public sealed record CuttingSpec
{
    /// <summary>Standing shoots per square metre. A mown lawn is ten to twenty thousand.</summary>
    public float StalksPerSquareMetre { get; init; } = 15000f;

    /// <summary>What one clipping weighs, milligrams. This and the tip speed are the whole of how
    /// loud the cutting is: see <see cref="ImpactDb"/>.</summary>
    public float ClippingMilligrams { get; init; } = 5f;

    /// <summary>Where a clipping hitting the pan puts its energy, Hz — short and high, the same
    /// place a footstep on leaves sits.</summary>
    public float CentreHz { get; init; } = 3200f;
    public float Q { get; init; } = 1.1f;

    /// <summary>
    /// One clipping striking the deck at the blade's tip speed, dB at one metre — DERIVED, not
    /// declared.
    ///
    /// It is half m v squared arriving at a steel pan, through the same constant every other impact
    /// in this engine goes through (<see cref="PanelAcoustics.ImpactReferenceDb"/>: one joule is
    /// 74 dB at a metre). Five milligrams at eighty metres a second is sixteen millijoules, which is
    /// about 56 dB — and at ten thousand a second that is a hiss in the fifties against a machine in
    /// the nineties.
    ///
    /// That answer is worth stating plainly because it contradicts the obvious guess. The difference
    /// everybody hears between a mower in grass and a mower on a path is NOT this hiss: it is the
    /// engine bogging, the governor opening, and the blade loading up. Those come out of the load
    /// path, which is why this is allowed to be as quiet as the arithmetic says it is instead of
    /// being propped up to meet an expectation.
    /// </summary>
    public float ImpactDb(float tipSpeedMps)
    {
        float joules = 0.5f * ClippingMilligrams * 1e-6f * tipSpeedMps * tipSpeedMps;
        return joules <= 0f ? 0f : PanelAcoustics.ImpactReferenceDb + 10f * MathF.Log10(joules);
    }
}

/// <summary>
/// A hermetic compressor: a motor and a pump welded inside one steel can.
///
/// Everything anybody has ever called "the hum of an air conditioner" is in the first line of this.
/// The magnetic pull between stator and rotor does not care which way round the field is, so it
/// pulses at TWICE the line frequency — 120 Hz in North America, 100 Hz in Europe — regardless of
/// how fast the motor is actually turning. That tone and its harmonics are the hum, it is the same
/// note in every unit on the street, and it is the mains, not the machine.
///
/// The PUMP is the other half and it is not at the same frequency. A two-pole motor turns near 3,500
/// rpm under load, so the shaft is near 58 Hz, and a scroll compresses once per revolution while a
/// reciprocating one does it once per cylinder per revolution. That gives a second series, slightly
/// lower than the hum and unrelated to it, and the beat between the two is why a compressor sounds
/// restless rather than steady.
///
/// The CAN is a thick steel shell on rubber grommets. It rings where a shell that size rings, and
/// since everything above is happening inside it, that ring is the filter through which all of it
/// is heard.
/// </summary>
public sealed record CompressorSpec
{
    /// <summary>Mains frequency, Hz. The hum is at twice this, and it is the whole reason a European
    /// air conditioner hums a tone lower than an American one.</summary>
    public float LineHz { get; init; } = 60f;
    /// <summary>Pole PAIRS. One pair is a nominal 3,600 rpm on 60 Hz.</summary>
    public int PolePairs { get; init; } = 1;
    /// <summary>How far the rotor falls behind the field under load, as a fraction. A few per cent.</summary>
    public float Slip { get; init; } = 0.04f;

    /// <summary>Compression events per revolution: 1 for a scroll or a rotary, one per cylinder for
    /// a reciprocating machine.</summary>
    public int EventsPerRevolution { get; init; } = 1;

    /// <summary>The magnetic hum at one metre, dB, with the can around it.</summary>
    public float HumDb { get; init; } = 62f;
    /// <summary>The gas pulsation at one metre, dB.</summary>
    public float PulsationDb { get; init; } = 58f;
    /// <summary>Gas rushing in the discharge line: broadband, and the only part of a compressor that
    /// is not a tone.</summary>
    public float FlowDb { get; init; } = 48f;

    /// <summary>The can's shell ring, Hz, and how sharp it is.</summary>
    public float ShellHz { get; init; } = 520f;
    public float ShellQ { get; init; } = 6f;

    /// <summary>How long the motor takes to come up to speed against the pump, seconds. This is the
    /// growl you hear a second before the hum settles.</summary>
    public float StartSeconds { get; init; } = 0.55f;

    /// <summary>The shaft speed under load, rpm.</summary>
    public float ShaftRpm => LineHz * 60f / MathF.Max(1, PolePairs) * (1f - Math.Clamp(Slip, 0f, 0.5f));
    /// <summary>The hum: twice the LINE frequency, not twice the shaft.</summary>
    public float HumHz => 2f * LineHz;
    /// <summary>The pumping series' fundamental.</summary>
    public float PulsationHz => ShaftRpm / 60f * MathF.Max(1, EventsPerRevolution);
}

/// <summary>
/// The sheet-metal box everything is bolted into, as the thing it acoustically is: a panel that
/// rings, driven by whatever is shaking it.
///
/// The note comes from <see cref="PanelAcoustics"/> — the same law as a door leaf and a car's wing,
/// because it is the same physics — so a big thin cabinet booms and a small thick one knocks, and
/// neither is a number anybody chose.
/// </summary>
public sealed record CasingSpec
{
    public required float WidthMetres { get; init; }
    public required float HeightMetres { get; init; }
    public float ThicknessMm { get; init; } = 0.8f;
    /// <summary>A material in the <see cref="AcousticRegistry"/>; steel is "Metal" there.</summary>
    public string Material { get; init; } = "Metal";
    /// <summary>How much of the machine's vibration gets into the panel, 0..1. Rubber grommets
    /// under a compressor are there precisely to make this small.</summary>
    public float Coupling { get; init; } = 0.25f;

    public float RingHz => PanelAcoustics.RingHz(
        AcousticRegistry.GetProperties(Material), WidthMetres, HeightMetres, ThicknessMm / 1000f);
}

/// <summary>
/// A machine that stands still and runs: a lawn mower, an air-conditioning condenser, a generator,
/// a pump.
///
/// It is the same idea as a vehicle — a parts list with dimensions, no samples — and deliberately
/// the same shape, so that the thing which makes a mower a mower is which parts it has rather than
/// which subsystem it belongs to. A mower is an engine under a governor with a blade in a pan; a
/// condenser unit is a fan and a compressor in a box. Neither needed a new kind of sound, only a new
/// combination of the ones the engine, the aircraft and the rail models already established.
/// </summary>
public sealed record SmallMachineSpec
{
    public required string Name { get; init; }

    /// <summary>An <see cref="EngineProfile"/> preset key, for a machine with a piston engine.</summary>
    public string? EngineKey { get; init; }
    /// <summary>What holds that engine's speed. A petrol machine without one would be a car engine
    /// with nobody's foot on it.</summary>
    public GovernorSpec? Governor { get; init; }
    /// <summary>Rotating inertia the crank sees through whatever it drives, kg m^2.</summary>
    public float DrivenInertiaKgM2 { get; init; } = 0.01f;

    /// <summary>A row of blades: a mower blade, a condenser fan, a blower. The same
    /// <see cref="BladeRowSpec"/> a propeller uses.</summary>
    public BladeRowSpec? Blade { get; init; }
    /// <summary>How many such rows. A 42 inch mower deck is two 21 inch blades side by side.</summary>
    public int BladeRows { get; init; } = 1;
    /// <summary>Blade speed over crank speed. A mower blade is bolted to the crankshaft: 1.</summary>
    public float BladeGearRatio { get; init; } = 1f;

    public MowerDeckSpec? Deck { get; init; }
    public CuttingSpec? Cutting { get; init; }

    /// <summary>The compressor, for a machine that is refrigeration rather than combustion.</summary>
    public CompressorSpec? Compressor { get; init; }

    public CasingSpec? Casing { get; init; }

    /// <summary>Overall level at one metre, for placing the voice. MEASURED, with
    /// <c>--yard levels</c>, not chosen.</summary>
    public required float SourceLevelDb { get; init; }

    /// <summary>How big the machine is acoustically — the distance between the parts it radiates
    /// from. Inside it the level is flat, because a step nearer the engine is a step further from
    /// the deck. Same rule as a car's outlet separation.</summary>
    public float ExtentMetres { get; init; } = 0.8f;

    // ── Presets ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A walk-behind rotary mower: a 163 cc overhead-valve single, governed at 2,900 rpm, with a
    /// 21 inch steel blade bolted straight to the crankshaft and turning inside a stamped pan.
    ///
    /// Direct drive is the fact that decides how it sounds. The blade turns at engine speed, so the
    /// blade-passing tone (two tips, about 97 Hz) is the SECOND order of a single-cylinder engine
    /// firing every other revolution at 24 Hz — the two are locked, and the machine is one note with
    /// a great deal happening around it rather than an engine and a fan beating against each other.
    /// It is also why the blade is the flywheel: 0.014 kg m^2 of steel bar is more rotating inertia
    /// than the engine has of its own, which is what keeps it from stalling in thick grass and what
    /// makes the governor's recovery take the best part of a second.
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
            // A mower blade is a flat bar with a bent lift wing, not an aerofoil: blunt, and it
            // pushes a great deal of air for its size. That thickness is why it roars.
            ThicknessRatio = 0.20f,
            RpmMax = 3200f, RpmIdle = 1200f,
            // The tone is the thump at twice crank speed; the ROAR is the broadband, and on a mower
            // the roar is the machine. An electric mower — a blade in a deck and nothing else — is
            // about 88 dB at the operator, which is what this number is.
            ReferenceDb = 93f, SelfNoiseDb = 88f, BladeScatter = 0.02f,
        },
        Deck = new MowerDeckSpec { DiameterMetres = 0.53f, DepthMetres = 0.095f, ThicknessMm = 1.6f },
        Cutting = new CuttingSpec(),
        Casing = null,
        // MEASURED with `--yard mower_push levels`, mowing at 1.15 m/s in ordinary grass: 92 dB, of
        // which the engine is 91 and the blade in its deck 85.
        SourceLevelDb = 92f,
        ExtentMetres = 0.6f,
    };

    /// <summary>
    /// A lawn tractor: a 500 cc V-twin governed at 3,200 rpm driving a 42 inch deck by belt — two
    /// 21 inch blades side by side, turning at the same speed as the engine through a 1:1 pulley.
    ///
    /// Two blades and two cylinders is the whole difference from the push mower. The firing rate is
    /// one per revolution instead of one per two, so the engine note is an octave up and much
    /// smoother; the two blade rows are independent, so their 100 Hz tones beat slowly against each
    /// other rather than locking; and the deck is twice as wide, so it cuts twice as much grass a
    /// second and the cutting hiss is the loudest thing about it from a distance.
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
            // Per ROW: two of them sum incoherently, which is the three decibels that make a 42 inch
            // deck louder than a 21 inch one at the same tip speed.
            RpmMax = 3400f, RpmIdle = 1400f, ReferenceDb = 95f, SelfNoiseDb = 87f, BladeScatter = 0.025f,
        },
        BladeRows = 2,
        Deck = new MowerDeckSpec { DiameterMetres = 1.07f, DepthMetres = 0.11f, ThicknessMm = 2.0f, PanShare = 0.5f },
        Cutting = new CuttingSpec(),
        SourceLevelDb = 96f,
        ExtentMetres = 1.6f,
    };

    /// <summary>
    /// The outdoor half of a residential split system: a three-ton condenser, which is a steel
    /// cabinet with a scroll compressor in the bottom of it and a 20 inch propeller fan in the lid
    /// blowing straight up.
    ///
    /// Two sounds and they are unrelated to each other. The fan is nearly all broadband — 22 m/s at
    /// the tips is Mach 0.065, far too slow for the blade-passing tone at 42 Hz to be anything but a
    /// rumble under the rush of air. The compressor is the hum, at 120 Hz because that is twice the
    /// mains and nothing to do with how fast anything is turning, with the scroll's own 57 Hz
    /// pumping beating against it. From across a street the hum is what you hear; from underneath it
    /// the fan is.
    /// </summary>
    public static SmallMachineSpec AirConditionerCondenser => new()
    {
        Name = "Condenser unit, 3 ton",
        Blade = new BladeRowSpec
        {
            Blades = 3, DiameterMetres = 0.50f, ChordMetres = 0.11f, ThicknessRatio = 0.06f,
            // Mach 0.065: there is nothing else. A condenser fan on its own, compressor off, is
            // about 63 dB at a metre and it is all rush of air.
            RpmMax = 840f, RpmIdle = 840f, ReferenceDb = 66f, SelfNoiseDb = 63f, BladeScatter = 0.03f,
        },
        Compressor = new CompressorSpec
        {
            LineHz = 60f, PolePairs = 1, Slip = 0.042f, EventsPerRevolution = 1,
            HumDb = 63f, PulsationDb = 57f, FlowDb = 47f,
            ShellHz = 520f, ShellQ = 6f, StartSeconds = 0.6f,
        },
        Casing = new CasingSpec { WidthMetres = 0.8f, HeightMetres = 0.9f, ThicknessMm = 0.8f, Coupling = 0.3f },
        // MEASURED at 65 dB at one metre, which is a modern quiet unit: manufacturers quote these as
        // a sound POWER near 72 dB(A), and 72 less ten log of a hemisphere at a metre is 64.
        SourceLevelDb = 65f,
        ExtentMetres = 0.9f,
    };

    /// <summary>
    /// A window unit, heard from the street below it. One shaft carries a squirrel-cage blower
    /// indoors and a small propeller fan outdoors, so the fan is slow and small, and the rotary
    /// compressor an arm's length behind it hums at the same 120 Hz as anything else on the mains.
    /// The cabinet is thinner and smaller than a condenser's and rings higher, which is most of why a
    /// window unit rattles where a condenser drones.
    /// </summary>
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

    /// <summary>
    /// A preset by name — through the <see cref="ModelLibrary"/>, so a map's own authored machine of
    /// that name wins over the built-in one. That is the whole point of the library and it is why
    /// this does not read <see cref="Presets"/> directly.
    /// </summary>
    public static SmallMachineSpec ByName(string key) => ModelLibrary.SmallMachine(key);
}

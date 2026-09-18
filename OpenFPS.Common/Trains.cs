using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace OpenFPS.Common;

// ═══════════════════════════════════════════════════════════════════════════════════════════════
//  A train, described as the machine it is.
//
//  The first thing to get right about a train is that MOST OF IT IS NOT THE ENGINE. A locomotive
//  under power is one source among forty, and from the lineside the sound of a train going past is
//  overwhelmingly steel wheels on steel rail. Every vehicle makes it, whether it is driven or towed,
//  loaded or empty; a hundred cars of unpowered freight are deafening. So the parts list here starts
//  with the wheels and the track and treats traction as something some vehicles happen to have.
//
//  ROLLING NOISE is roughness. Neither the wheel nor the rail is smooth: both carry a corrugation
//  spectrum a few microns deep, and rolling one over the other at V metres a second turns a
//  wavelength of lambda metres into a frequency of V/lambda hertz. That single sentence explains why
//  rolling noise is broadband from a hundred hertz to five kilohertz, why the whole spectrum slides
//  UP as the train speeds up, and why a train is quieter on new rail. Three things then radiate it:
//  the RAIL, which rings from a couple of hundred hertz to a kilohertz and carries several metres of
//  itself into the sound; the SLEEPERS, which are big flat things and radiate the bottom; and the
//  WHEEL, which is a steel ring with almost no damping and owns everything above about a kilohertz.
//
//  Two details that are not details:
//
//    THE CONTACT PATCH FILTERS IT. Wheel and rail touch over an ellipse about a centimetre long, and
//    an irregularity shorter than that is averaged away instead of being ridden over. So there is a
//    low-pass in WAVELENGTH, which means its corner in HERTZ rises with speed — and it is why a slow
//    train rumbles and a fast one hisses, rather than simply being a louder version of the same thing.
//
//    TREAD BRAKES ROUGHEN WHEELS. A cast-iron block dragging on the tread wears it into corrugations;
//    a disc brake leaves the tread alone. That is the whole reason a freight train is ten decibels
//    louder than a passenger train at the same speed, and it is a property of the BRAKE, in the parts
//    list, not a "freight is louder" rule.
//
//  IMPACTS are geometry. Jointed rail has a gap every rail length, and the wheel drops into it: the
//  impulse is the unsprung mass meeting the Hertzian contact spring, which gives a force of a couple
//  of hundred kilonewtons over two or three milliseconds and rings the same wheel and the same rail
//  that the roughness does. Everything about the RHYTHM — the two-and-two of a bogie, the gap to the
//  next car, how it all speeds up — falls out of the wheelbase, the bogie centres, the car length,
//  the rail length and the speed. Nothing sequences it.
//
//  CURVE SQUEAL is a wheelset being asked to go round a corner it cannot steer into. The axle is
//  rigid, so on a curve the wheels have to creep sideways; past a few milliradians the friction
//  saturates and stick-slip drives one of the wheel's own modes into a limit cycle. Whether a train
//  squeals is therefore a question about the curve radius and the bogie wheelbase, and about nothing
//  else.
// ═══════════════════════════════════════════════════════════════════════════════════════════════

public enum RailTraction { None, DieselElectric, Electric, Steam }
public enum SleeperKind { Timber, Concrete, SlabTrack }

/// <summary>A wheelset: two steel wheels pressed on an axle, and how rough they are.</summary>
public sealed record WheelsetSpec
{
    public required float DiameterMetres { get; init; }
    /// <summary>The rim, metres: how thick radially and how wide across the tread. These two and the
    /// diameter are the wheel's NOTE — an out-of-plane ring's modes go as n(n^2-1)/sqrt(n^2+1) times
    /// the square root of its bending stiffness over its mass, divided by the square of its radius,
    /// so a tram's small wheel rings a good deal higher than a locomotive's.</summary>
    public float RimThicknessMetres { get; init; } = 0.030f;
    public float RimWidthMetres { get; init; } = 0.135f;
    /// <summary>Unsprung mass per wheel, kg: the wheel, its share of the axle and of the gear. This
    /// is what meets the rail at a joint, and with the Hertzian contact stiffness it sets both how
    /// hard and how LONG the blow is.</summary>
    public float UnsprungKg { get; init; } = 900f;
    public float AxleLoadTonnes { get; init; } = 16f;
    /// <summary>Braked on the tread by a cast-iron block, which corrugates it. Worth eight to ten
    /// decibels over a disc-braked wheel and it is the single biggest difference between a freight
    /// train and a passenger train.</summary>
    public bool TreadBraked { get; init; }
    /// <summary>Damping in the wheel. Bare steel is about 1e-4 — nothing at all, which is why wheels
    /// squeal; a ring damper or a resilient wheel takes it to 1e-2 and they stop.</summary>
    public float LossFactor { get; init; } = 1.2e-4f;
    /// <summary>A flat spot worn on the tread, metres. It bangs once a revolution. Zero for a wheel
    /// in good order.</summary>
    public float FlatLengthMetres { get; init; }

    /// <summary>Revolutions a second at this speed — the rate a flat spot bangs at.</summary>
    public float RotationHz(float mps) => mps / (MathF.PI * MathF.Max(0.1f, DiameterMetres));
}

/// <summary>The track: what the wheels are running on, and how it is put together.</summary>
public sealed record TrackSpec
{
    public required string Name { get; init; }
    /// <summary>Rail mass per metre, kg. 60 for a heavy main line, 45 for a branch, 35 for a tramway
    /// groove rail.</summary>
    public float RailKgPerMetre { get; init; } = 60f;
    /// <summary>Second moment of area of the rail section, m^4. 3.055e-5 for UIC60.</summary>
    public float RailInertiaM4 { get; init; } = 3.055e-5f;
    public float SleeperSpacingMetres { get; init; } = 0.60f;
    public SleeperKind Sleepers { get; init; } = SleeperKind.Concrete;
    /// <summary>Rail length between joints, metres. ZERO means continuous welded rail and no
    /// clatter at all — which is most modern main line, and is why a train on good track is a hiss
    /// and a roar rather than the sound everybody thinks a train makes.</summary>
    public float JointSpacingMetres { get; init; }
    /// <summary>The dip at a joint, radians of angle the wheel drops through. A tight new joint is
    /// 3 milliradians, a hammered old one 15. This times the speed IS the impact velocity.</summary>
    public float JointDipRadians { get; init; } = 0.008f;
    /// <summary>Joints on the two rails offset by half a rail length, so the bangs come twice as
    /// often and singly rather than in pairs. American practice staggers; British squares them up.</summary>
    public bool StaggeredJoints { get; init; } = true;
    /// <summary>How rough the railhead is against a reference, dB. Ground rail is -6, ordinary
    /// main line 0, and rail that has not been ground in twenty years +8.</summary>
    public float RoughnessDb { get; init; }
    /// <summary>Curve radius, metres. Zero is straight. Below about four hundred metres a rigid
    /// wheelset has to creep sideways enough to squeal.</summary>
    public float CurveRadiusMetres { get; init; }
    /// <summary>A rail bolted to a bridge deck, or a train in a cutting or a tunnel, radiates into
    /// something. This is the extra, dB, and it is the reason a bridge is audible from a mile off.
    /// </summary>
    public float StructureDb { get; init; }

    /// <summary>
    /// The pinned-pinned resonance: where half a bending wavelength in the rail equals one sleeper
    /// bay, so the rail flaps between its supports and radiates hard. It is the peak in the middle
    /// of every rolling-noise spectrum and it is a property of the rail section and the sleeper
    /// spacing, nothing else.
    /// </summary>
    [JsonIgnore]
    public float PinnedPinnedHz
    {
        get
        {
            const float e = 210e9f;
            float m = MathF.Max(10f, RailKgPerMetre);
            float w = MathF.Pow(MathF.PI / MathF.Max(0.2f, SleeperSpacingMetres), 2f) * MathF.Sqrt(e * RailInertiaM4 / m);
            return w / MathF.Tau;
        }
    }

    /// <summary>The sleepers going under at this speed — the low flutter under a slow train.</summary>
    public float SleeperPassHz(float mps) => mps / MathF.Max(0.2f, SleeperSpacingMetres);

    public static TrackSpec WeldedMainLine => new()
    {
        Name = "continuous welded rail on concrete, main line",
        RailKgPerMetre = 60f, SleeperSpacingMetres = 0.60f, Sleepers = SleeperKind.Concrete,
        JointSpacingMetres = 0f, RoughnessDb = 0f,
    };

    /// <summary>
    /// Jointed rail in thirty-nine foot lengths — the North American standard, because that is what
    /// fit in a gondola. At 25 m/s a bogie's two axles are 0.10 s apart and the two bogies of an
    /// 85 foot car 0.74 s apart, which is the clickety-clack exactly: two quick, a gap, two quick.
    /// </summary>
    public static TrackSpec JointedTimber => new()
    {
        Name = "jointed rail, 39 ft lengths on timber",
        RailKgPerMetre = 57f, RailInertiaM4 = 2.73e-5f,
        SleeperSpacingMetres = 0.53f, Sleepers = SleeperKind.Timber,
        JointSpacingMetres = 11.887f, JointDipRadians = 0.010f, StaggeredJoints = true,
        RoughnessDb = 3f,
    };

    /// <summary>Street track: light grooved rail bedded in concrete, tight curves, and nothing
    /// resilient anywhere. Slab track radiates less at the bottom and more in the middle.</summary>
    public static TrackSpec StreetTramway => new()
    {
        Name = "grooved rail in a street, slab",
        RailKgPerMetre = 40f, RailInertiaM4 = 1.2e-5f,
        SleeperSpacingMetres = 0.75f, Sleepers = SleeperKind.SlabTrack,
        JointSpacingMetres = 0f, RoughnessDb = 4f, CurveRadiusMetres = 25f,
    };

    public static TrackSpec MetroSlab => new()
    {
        Name = "metro slab track",
        RailKgPerMetre = 54f, RailInertiaM4 = 2.35e-5f,
        SleeperSpacingMetres = 0.70f, Sleepers = SleeperKind.SlabTrack,
        JointSpacingMetres = 0f, RoughnessDb = 5f, CurveRadiusMetres = 250f, StructureDb = 3f,
    };
}

/// <summary>Electric traction: a motor, a gearbox and the inverter that feeds it.</summary>
public sealed record ElectricDriveSpec
{
    /// <summary>Teeth on the pinion and on the gearwheel. Their ratio is the gearing and the PINION
    /// COUNT times the motor's revolutions is the mesh frequency — the whine that rises smoothly
    /// with speed and is the most recognisable thing about an electric train.</summary>
    public int PinionTeeth { get; init; } = 17;
    public int GearTeeth { get; init; } = 96;
    /// <summary>Pole PAIRS. The magnetic pull in the air gap goes round at the electrical frequency
    /// and pulses at twice it, so a four-pole motor at 3,000 rpm hums at 200 Hz.</summary>
    public int PolePairs { get; init; } = 2;
    /// <summary>Stator slots. The rotor's teeth going past them is a much higher tone and it is the
    /// thin edge on top of the hum.</summary>
    public int StatorSlots { get; init; } = 48;
    public float MotorMaxRpm { get; init; } = 4200f;
    /// <summary>The inverter's carrier, Hz, while it is modulating asynchronously — the FIXED tone
    /// at a standstill and at low speed, before it locks to the motor.</summary>
    public float CarrierHz { get; init; } = 1050f;
    /// <summary>Pulse counts the inverter steps down through as the output frequency rises. In each
    /// mode the carrier is that many times the motor's electrical frequency, so the tone RISES
    /// through the mode and DROPS at each change — the staircase everybody knows from a modern train
    /// pulling out, and an emergent thing: the drive is trying to keep its switching losses down.</summary>
    public int[] PulseModes { get; init; } = { 27, 15, 9, 5, 3, 1 };
    /// <summary>Motor output frequency at which asynchronous modulation gives way, Hz.</summary>
    public float SyncFromHz { get; init; } = 20f;
    public float InverterLevelDb { get; init; } = 84f;
    public float GearLevelDb { get; init; } = 88f;
    public float MotorHumDb { get; init; } = 80f;
    /// <summary>Forced-ventilation blower: broadband, and on all the time the train is alive.</summary>
    public float BlowerDb { get; init; } = 74f;
}

/// <summary>A steam locomotive's front end: what happens between the cylinders and the chimney.</summary>
public sealed record SteamLocoSpec
{
    public required float DriverDiameterMetres { get; init; }
    /// <summary>Cylinders. Two is the usual; each is double-acting, so each gives TWO exhaust beats
    /// per revolution of the drivers and a two-cylinder engine barks four times a turn.</summary>
    public int Cylinders { get; init; } = 2;
    public float CylinderBoreMetres { get; init; } = 0.635f;
    public float CylinderStrokeMetres { get; init; } = 0.762f;
    /// <summary>The blast nozzle at the top of the exhaust pipe, metres. Small nozzle, fast jet,
    /// sharp bark and a fierce draught on the fire; big nozzle, soft exhaust, lazy fire. Draughting
    /// a locomotive was the whole art, and it is audible.</summary>
    public float BlastNozzleMetres { get; init; } = 0.135f;
    /// <summary>The chimney above it: a pipe open at both ends, so the chuff is tuned to c/2L of it.
    /// A tall thin stack rings; a short wide one barks.</summary>
    public float StackDiameterMetres { get; init; } = 0.48f;
    public float StackLengthMetres { get; init; } = 0.95f;
    public float BoilerKPa { get; init; } = 1550f;
    /// <summary>How far the valve gear is out of square, as a fraction of a beat. No locomotive was
    /// ever perfect and the uneven beat is most of the character — a engine with a bad setting limps
    /// audibly at every revolution.</summary>
    public float ValveSettingError { get; init; } = 0.035f;
    /// <summary>The blower, and every joint in the thing: a continuous hiss that is there even when
    /// the regulator is shut, which is why a steam locomotive drifting is not silent.</summary>
    public float LeakageDb { get; init; } = 86f;
    /// <summary>Rods, crossheads and axleboxes, all with play in them: a metallic clank at the
    /// driver rate and a general clatter over it.</summary>
    public float MotionDb { get; init; } = 92f;
    public string WhistleKey { get; init; } = "three_chime";
    public string BellKey { get; init; } = "loco_bell";

    /// <summary>Exhaust beats a second at this speed. Two cylinders, double acting: four a turn.</summary>
    public float ChuffHz(float mps) => 2f * Cylinders * mps / (MathF.PI * MathF.Max(0.3f, DriverDiameterMetres));
    /// <summary>The chimney's first resonance — the note in the bark.</summary>
    [JsonIgnore]
    public float StackHz => 343f / (2f * MathF.Max(0.1f, StackLengthMetres + 0.3f * StackDiameterMetres));
}

/// <summary>What a vehicle is driven by, if anything.</summary>
public sealed record RailTractionSpec
{
    public required RailTraction Kind { get; init; }
    /// <summary>Diesel-electric: the prime mover, as an ordinary EngineProfile key. A locomotive
    /// diesel is a diesel — very big, very slow and governed to fixed notches, and the same cylinder
    /// model runs it.</summary>
    public string? EngineKey { get; init; }
    /// <summary>The notches the governor will hold, rpm. A diesel-electric does not have a throttle,
    /// it has eight steps, and that is why it changes speed in audible jumps.</summary>
    public float[] NotchRpm { get; init; } = Array.Empty<float>();
    /// <summary>Radiator fans: how many blades, how fast, and how loud. On a big locomotive these
    /// are a metre and a half across and they are most of what you hear at idle.</summary>
    public int FanBlades { get; init; } = 10;
    public float FanRpm { get; init; } = 900f;
    public float FanDb { get; init; } = 96f;
    public ElectricDriveSpec? Drive { get; init; }
    public SteamLocoSpec? Steam { get; init; }
    public string? HornKey { get; init; }
    public string? BellKey { get; init; }
}

/// <summary>One vehicle in a train: a locomotive, a coach, a wagon, a tram section.</summary>
public sealed record RailVehicleSpec
{
    public required string Name { get; init; }
    public required float LengthMetres { get; init; }
    /// <summary>Distance between the two bogie centres. With the length this is the whole rhythm of
    /// a passing train.</summary>
    public required float BogieCentresMetres { get; init; }
    /// <summary>Axle spacing within a bogie. This is the "clack-CLACK" — the two axles of one bogie
    /// hitting the same joint a tenth of a second apart.</summary>
    public float BogieWheelbaseMetres { get; init; } = 2.56f;
    public int Bogies { get; init; } = 2;
    [JsonIgnore]
    public int AxlesPerBogie { get; init; } = 2;
    public required WheelsetSpec Wheels { get; init; }
    public float MassTonnes { get; init; } = 45f;
    public RailTractionSpec? Traction { get; init; }
    /// <summary>An empty steel box drums; a loaded one does not. This is the level of the body's own
    /// ring, dB, excited by everything the bogies do.</summary>
    public float BodyDrumDb { get; init; }
    public float BodyDrumHz { get; init; } = 70f;

    [JsonIgnore]
    public int Axles => Bogies * AxlesPerBogie;
}

/// <summary>A train: vehicles in order, on a track.</summary>
/// <summary>How many of one vehicle, in a row. A named pair rather than a tuple, because this is a
/// thing an author writes in a file.</summary>
public sealed record ConsistEntry
{
    public required RailVehicleSpec Vehicle { get; init; }
    public int Count { get; init; } = 1;
    public void Deconstruct(out RailVehicleSpec vehicle, out int count) { vehicle = Vehicle; count = Count; }
}

public sealed record TrainProfile
{
    public required string Name { get; init; }
    /// <summary>The consist, head to tail.</summary>
    public required ConsistEntry[] Consist { get; init; }
    public required TrackSpec Track { get; init; }
    public float TypicalSpeedMps { get; init; } = 25f;
    /// <summary>
    /// Per wheelset at one metre at 100 km/h on reference-rough rail, disc braked.
    ///
    /// The one anchor in the whole rail model. The SHAPE and the way it changes with speed come out
    /// of the roughness spectrum, the contact patch and the three radiators; the absolute level is
    /// taken from what a train measures at the lineside, because the radiation integrals for a rail
    /// on ballast are not worth pretending to know to a decibel.
    ///
    /// And it is anchored against a PASS-BY and not against a single axle, because that is the
    /// measurement that exists: eighty-two A-weighted decibels at 7.5 metres from a disc-braked
    /// passenger train at 80 km/h. Setting it by extrapolating one axle back to a metre and then
    /// forward again put it twelve decibels light, which is what a line of sources does to anybody
    /// who reasons about one of them.
    /// </summary>
    public float RollingReferenceDb { get; init; } = 104f;

    [JsonIgnore]
    public int TotalAxles => Consist.Sum(c => c.Vehicle.Axles * c.Count);
    [JsonIgnore]
    public float LengthMetres => Consist.Sum(c => c.Vehicle.LengthMetres * c.Count);

    // ── The vehicles ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A GE Genesis: the cowl-bodied diesel-electric on the front of most Amtrak trains away from
    /// the wires. A 7FDL16 — sixteen cylinders of 228 mm bore and 267 mm stroke, turbocharged,
    /// governed from 440 rpm at idle to 1,050 in notch 8, which is a firing rate of 59 Hz to 140 Hz.
    /// Disc-braked wheels, so it is quiet on the rail for its weight; and two radiator fans that are
    /// most of what you hear when it is standing.
    /// </summary>
    public static RailVehicleSpec GenesisP42 => new()
    {
        Name = "GE Genesis P42, diesel-electric",
        LengthMetres = 21.2f, BogieCentresMetres = 13.3f, BogieWheelbaseMetres = 3.9f,
        Bogies = 2, AxlesPerBogie = 2, MassTonnes = 120f,
        Wheels = new WheelsetSpec
        {
            DiameterMetres = 1.016f, RimThicknessMetres = 0.045f, RimWidthMetres = 0.140f,
            UnsprungKg = 1450f, AxleLoadTonnes = 30f, TreadBraked = false, LossFactor = 2e-4f,
        },
        Traction = new RailTractionSpec
        {
            Kind = RailTraction.DieselElectric,
            EngineKey = "ge_7fdl16",
            NotchRpm = new[] { 440f, 490f, 570f, 660f, 750f, 840f, 930f, 1000f, 1050f },
            FanBlades = 10, FanRpm = 1150f, FanDb = 98f,
            HornKey = "k5la", BellKey = "loco_bell",
        },
    };

    /// <summary>
    /// An EMD road switcher on a freight: a 645E3 two-stroke V16, which fires every cylinder every
    /// revolution and so makes twice the firing rate of a four-stroke of the same size — 240 Hz at
    /// its 900 rpm maximum against a Genesis's 140. That, and the Roots blower geared off the crank,
    /// is the whole of why an EMD sounds like an EMD.
    /// </summary>
    public static RailVehicleSpec EmdRoadSwitcher => new()
    {
        Name = "EMD road switcher, 645 two-stroke V16",
        LengthMetres = 20.0f, BogieCentresMetres = 12.0f, BogieWheelbaseMetres = 4.1f,
        Bogies = 2, AxlesPerBogie = 3, MassTonnes = 172f,
        Wheels = new WheelsetSpec
        {
            DiameterMetres = 1.016f, RimThicknessMetres = 0.048f, RimWidthMetres = 0.145f,
            UnsprungKg = 1500f, AxleLoadTonnes = 29f, TreadBraked = true, LossFactor = 2e-4f,
        },
        Traction = new RailTractionSpec
        {
            Kind = RailTraction.DieselElectric,
            EngineKey = "emd_645e3",
            NotchRpm = new[] { 315f, 400f, 490f, 570f, 650f, 730f, 820f, 870f, 900f },
            FanBlades = 12, FanRpm = 1000f, FanDb = 99f,
            HornKey = "k5la", BellKey = "loco_bell",
        },
    };

    /// <summary>An Amfleet-style single-level coach: 26 m over couplers, bogie centres 18.3 m,
    /// disc brakes, and nothing driving it. Four of these make more noise than the locomotive.</summary>
    public static RailVehicleSpec PassengerCoach => new()
    {
        Name = "single-level passenger coach",
        LengthMetres = 25.9f, BogieCentresMetres = 18.3f, BogieWheelbaseMetres = 2.59f,
        Bogies = 2, AxlesPerBogie = 2, MassTonnes = 52f,
        Wheels = new WheelsetSpec
        {
            DiameterMetres = 0.915f, RimThicknessMetres = 0.032f, RimWidthMetres = 0.135f,
            UnsprungKg = 880f, AxleLoadTonnes = 13f, TreadBraked = false, LossFactor = 1.5e-4f,
        },
        BodyDrumDb = 74f, BodyDrumHz = 55f,
    };

    /// <summary>
    /// A freight wagon: shorter, stiffer, tread-braked and usually empty. The tread brakes are worth
    /// nine decibels over the coach on their own, and an empty steel body drums under it.
    /// </summary>
    public static RailVehicleSpec FreightWagon => new()
    {
        Name = "bogie freight wagon, tread braked",
        LengthMetres = 17.4f, BogieCentresMetres = 12.4f, BogieWheelbaseMetres = 1.78f,
        Bogies = 2, AxlesPerBogie = 2, MassTonnes = 30f,
        Wheels = new WheelsetSpec
        {
            DiameterMetres = 0.840f, RimThicknessMetres = 0.035f, RimWidthMetres = 0.140f,
            UnsprungKg = 1050f, AxleLoadTonnes = 8f, TreadBraked = true, LossFactor = 1.1e-4f,
            FlatLengthMetres = 0.02f,
        },
        BodyDrumDb = 80f, BodyDrumHz = 44f,
    };

    /// <summary>
    /// A low-floor tram: small wheels, resilient (rubber-sprung) so they do NOT squeal the way a
    /// solid wheel would, a four-pole motor through a 5.6:1 gearbox, and an inverter stepping down
    /// its pulse count as it accelerates.
    /// </summary>
    public static RailVehicleSpec LightRailCar => new()
    {
        Name = "low-floor light rail vehicle",
        LengthMetres = 27.5f, BogieCentresMetres = 19.0f, BogieWheelbaseMetres = 1.80f,
        Bogies = 2, AxlesPerBogie = 2, MassTonnes = 38f,
        Wheels = new WheelsetSpec
        {
            DiameterMetres = 0.660f, RimThicknessMetres = 0.028f, RimWidthMetres = 0.105f,
            UnsprungKg = 480f, AxleLoadTonnes = 9.5f, TreadBraked = false, LossFactor = 9e-3f,
        },
        Traction = new RailTractionSpec
        {
            Kind = RailTraction.Electric,
            Drive = new ElectricDriveSpec
            {
                PinionTeeth = 16, GearTeeth = 90, PolePairs = 2, StatorSlots = 48,
                MotorMaxRpm = 4000f, CarrierHz = 1000f, SyncFromHz = 22f,
                PulseModes = new[] { 27, 15, 9, 5, 3, 1 },
                InverterLevelDb = 86f, GearLevelDb = 90f, MotorHumDb = 82f, BlowerDb = 76f,
            },
            HornKey = "two_tone", BellKey = "tram_gong",
        },
        BodyDrumDb = 70f, BodyDrumHz = 60f,
    };

    /// <summary>
    /// A heavy metro car on older chopper-fed DC motors: no inverter staircase, just the gear whine
    /// and the motor growl, solid wheels that squeal on every curve, and slab track in a tunnel.
    /// </summary>
    public static RailVehicleSpec MetroCar => new()
    {
        Name = "heavy metro car, DC traction",
        LengthMetres = 18.4f, BogieCentresMetres = 12.6f, BogieWheelbaseMetres = 2.10f,
        Bogies = 2, AxlesPerBogie = 2, MassTonnes = 33f,
        Wheels = new WheelsetSpec
        {
            DiameterMetres = 0.780f, RimThicknessMetres = 0.030f, RimWidthMetres = 0.127f,
            UnsprungKg = 620f, AxleLoadTonnes = 10.5f, TreadBraked = true, LossFactor = 1.3e-4f,
        },
        Traction = new RailTractionSpec
        {
            Kind = RailTraction.Electric,
            Drive = new ElectricDriveSpec
            {
                PinionTeeth = 15, GearTeeth = 79, PolePairs = 2, StatorSlots = 36,
                MotorMaxRpm = 3200f, CarrierHz = 0f, SyncFromHz = 1e6f,   // no inverter at all
                PulseModes = new[] { 1 },
                InverterLevelDb = 0f, GearLevelDb = 92f, MotorHumDb = 86f, BlowerDb = 80f,
            },
            HornKey = "two_tone",
        },
        BodyDrumDb = 78f, BodyDrumHz = 50f,
    };

    /// <summary>
    /// A 4-8-4: two cylinders of 25 by 30 inches on 1.85 m drivers, 225 psi, a 135 mm blast nozzle
    /// under a 0.95 m chimney. At 25 m/s the drivers turn 4.3 times a second and it barks 17 times —
    /// fast enough that the beats have started to run together into a roar, which is what a big
    /// engine at speed actually sounds like and not what people expect.
    /// </summary>
    public static RailVehicleSpec SteamNorthern => new()
    {
        Name = "4-8-4 Northern, two cylinders",
        LengthMetres = 30.5f, BogieCentresMetres = 16.0f, BogieWheelbaseMetres = 5.5f,
        Bogies = 2, AxlesPerBogie = 3, MassTonnes = 210f,
        Wheels = new WheelsetSpec
        {
            DiameterMetres = 1.854f, RimThicknessMetres = 0.060f, RimWidthMetres = 0.155f,
            UnsprungKg = 2100f, AxleLoadTonnes = 31f, TreadBraked = true, LossFactor = 2.5e-4f,
        },
        Traction = new RailTractionSpec
        {
            Kind = RailTraction.Steam,
            Steam = new SteamLocoSpec
            {
                DriverDiameterMetres = 1.854f, Cylinders = 2,
                CylinderBoreMetres = 0.635f, CylinderStrokeMetres = 0.762f,
                BlastNozzleMetres = 0.135f, StackDiameterMetres = 0.48f, StackLengthMetres = 0.95f,
                BoilerKPa = 1550f, ValveSettingError = 0.035f,
                LeakageDb = 88f, MotionDb = 94f,
                WhistleKey = "three_chime", BellKey = "loco_bell",
            },
            BellKey = "loco_bell",
        },
    };

    /// <summary>The tender behind it: eight wheels, tread braked, and nothing else.</summary>
    public static RailVehicleSpec SteamTender => new()
    {
        Name = "tender",
        LengthMetres = 13.7f, BogieCentresMetres = 8.6f, BogieWheelbaseMetres = 1.75f,
        Bogies = 2, AxlesPerBogie = 3, MassTonnes = 160f,
        Wheels = new WheelsetSpec
        {
            DiameterMetres = 0.840f, RimThicknessMetres = 0.038f, RimWidthMetres = 0.140f,
            UnsprungKg = 1150f, AxleLoadTonnes = 27f, TreadBraked = true, LossFactor = 1.2e-4f,
        },
        BodyDrumDb = 72f, BodyDrumHz = 48f,
    };

    /// <summary>A heavyweight steam-era passenger car: six-wheel bogies, tread brakes, and heavy.</summary>
    public static RailVehicleSpec HeavyweightCoach => new()
    {
        Name = "heavyweight coach, six-wheel bogies",
        LengthMetres = 25.3f, BogieCentresMetres = 17.4f, BogieWheelbaseMetres = 2.44f,
        Bogies = 2, AxlesPerBogie = 3, MassTonnes = 78f,
        Wheels = new WheelsetSpec
        {
            DiameterMetres = 0.915f, RimThicknessMetres = 0.034f, RimWidthMetres = 0.140f,
            UnsprungKg = 940f, AxleLoadTonnes = 13f, TreadBraked = true, LossFactor = 1.2e-4f,
        },
        BodyDrumDb = 76f, BodyDrumHz = 52f,
    };

    // ── The trains ──────────────────────────────────────────────────────────────────────────────

    public static TrainProfile AmtrakDiesel => new()
    {
        Name = "Amtrak, Genesis and six coaches, welded rail",
        Consist = new ConsistEntry[] { new() { Vehicle = GenesisP42, Count = 1 }, new() { Vehicle = PassengerCoach, Count = 6 } },
        Track = TrackSpec.WeldedMainLine,
        TypicalSpeedMps = 36f,
    };

    public static TrainProfile FreightJointed => new()
    {
        Name = "freight, two EMDs and fifty wagons on jointed rail",
        Consist = new ConsistEntry[] { new() { Vehicle = EmdRoadSwitcher, Count = 2 }, new() { Vehicle = FreightWagon, Count = 50 } },
        Track = TrackSpec.JointedTimber,
        TypicalSpeedMps = 18f,
    };

    public static TrainProfile SteamPassenger => new()
    {
        Name = "4-8-4 with a tender and five heavyweights, jointed rail",
        Consist = new ConsistEntry[] { new() { Vehicle = SteamNorthern, Count = 1 }, new() { Vehicle = SteamTender, Count = 1 }, new() { Vehicle = HeavyweightCoach, Count = 5 } },
        Track = TrackSpec.JointedTimber,
        TypicalSpeedMps = 24f,
    };

    public static TrainProfile LightRail => new()
    {
        Name = "two-car light rail set in the street",
        Consist = new ConsistEntry[] { new() { Vehicle = LightRailCar, Count = 2 } },
        Track = TrackSpec.StreetTramway,
        TypicalSpeedMps = 12f,
    };

    public static TrainProfile Metro => new()
    {
        Name = "six-car metro on slab track",
        Consist = new ConsistEntry[] { new() { Vehicle = MetroCar, Count = 6 } },
        Track = TrackSpec.MetroSlab,
        TypicalSpeedMps = 17f,
    };

    public static IReadOnlyDictionary<string, Func<TrainProfile>> Presets { get; } =
        new Dictionary<string, Func<TrainProfile>>(StringComparer.OrdinalIgnoreCase)
        {
            ["amtrak"] = () => AmtrakDiesel,
            ["freight"] = () => FreightJointed,
            ["steam"] = () => SteamPassenger,
            ["light_rail"] = () => LightRail,
            ["metro"] = () => Metro,
        };

    public static TrainProfile ByName(string key)
        => Presets.TryGetValue(key, out var make) ? make()
         : throw new ArgumentException($"No train preset '{key}'. Known: {string.Join(", ", Presets.Keys)}");
}

using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using OpenFPS.Common.Editing;

namespace OpenFPS.Common;

// A train as the machine it is (docs/TRAINS.md). Most of it is not the engine: from the lineside a
// train is steel wheels on steel rail, so the parts list starts with the wheels and the track.
// - Rolling noise is roughness, a wavelength λ ridden at V becoming V/λ Hz, radiated by the rail, the
//   sleepers and the wheel. The centimetre-long contact patch averages shorter wavelengths away, so a
//   slow train rumbles and a fast one hisses.
// - Tread brakes corrugate the wheels: about ten decibels between freight and passenger, a property
//   of the brake, not a rule about freight.
// - Joint impacts (a couple of hundred kilonewtons over 2-3 ms) and their rhythm fall out of the
//   wheelbase, bogie centres, car length, rail length and speed.
// - Curve squeal is a rigid wheelset creeping sideways on a curve until stick-slip locks a wheel mode:
//   the curve radius and the bogie wheelbase decide it.

public enum RailTraction { None, DieselElectric, Electric, Steam }
public enum SleeperKind { Timber, Concrete, SlabTrack }

/// <summary>A wheelset: two steel wheels pressed on an axle, and how rough they are.</summary>
public sealed record WheelsetSpec
{
    [Tunable("m", 0.3, 2.2, "Wheel diameter on the tread. A small wheel rings higher.", Label = "diameter", Step = 0.005)]
    public required float DiameterMetres { get; init; }
    /// <summary>The rim, metres, radially: with its width and the diameter, the wheel's note. A ring's
    /// out-of-plane modes go as n(n²-1)/√(n²+1) √(EI/m) / r², so a tram's small wheel rings higher.</summary>
    [Tunable("m", 0.01, 0.1, "Radial thickness of the rim. With the width and diameter it sets the wheel's ring.", Label = "rim thickness", Step = 0.001)]
    public float RimThicknessMetres { get; init; } = 0.030f;
    [Tunable("m", 0.05, 0.2, "Width of the rim across the tread.", Label = "rim width", Step = 0.001)]
    public float RimWidthMetres { get; init; } = 0.135f;
    /// <summary>Unsprung mass per wheel, kg: with the Hertzian contact stiffness, how hard and how long
    /// the blow at a joint is.</summary>
    [Tunable("kg", 100, 4000, "Unsprung mass per wheel: the wheel, its share of the axle and the gear. More mass hits a rail joint harder and longer.", Label = "unsprung mass per wheel", Step = 10)]
    public float UnsprungKg { get; init; } = 900f;
    [Tunable("t", 1, 40, "Weight carried by one axle.", Label = "axle load", Step = 0.5)]
    public float AxleLoadTonnes { get; init; } = 16f;
    /// <summary>Braked on the tread by a cast-iron block, which corrugates it: eight to ten decibels over
    /// a disc-braked wheel.</summary>
    [Tunable("", 0, 1, "Braked by a cast-iron block on the tread, which roughens it: eight to ten decibels louder than a disc-braked wheel.", Label = "tread braked")]
    public bool TreadBraked { get; init; }
    /// <summary>Damping in the wheel: bare steel about 1e-4, and squeals; a ring damper or a resilient
    /// wheel about 1e-2, and does not.</summary>
    [Tunable("", 0.00001, 0.05, "Damping in the wheel. Bare steel is about 0.0001 and squeals; a damped or resilient wheel is about 0.01 and does not.", Label = "wheel loss factor", Step = 0.00001)]
    public float LossFactor { get; init; } = 1.2e-4f;
    /// <summary>A flat spot worn on the tread, metres, banging once a revolution; zero for none.</summary>
    [Tunable("m", 0, 0.1, "Length of a flat spot worn on the tread. It bangs once a revolution. Zero for a wheel in good order.", Label = "wheel flat length", Step = 0.005)]
    public float FlatLengthMetres { get; init; }
}

/// <summary>The track: what the wheels are running on, and how it is put together.</summary>
public sealed record TrackSpec
{
    [Tunable("", 0, 0, "The track's name as it is said.")]
    public required string Name { get; init; }
    /// <summary>Rail mass per metre, kg. 60 for a heavy main line, 45 for a branch, 35 for a tramway
    /// groove rail.</summary>
    [Tunable("kg/m", 20, 80, "Rail mass per metre. 60 for a heavy main line, 45 for a branch, 35 for tramway groove rail.", Label = "rail mass per metre", Step = 1)]
    public float RailKgPerMetre { get; init; } = 60f;
    /// <summary>Second moment of area of the rail section, m^4. 3.055e-5 for UIC60.</summary>
    [Tunable("m⁴", 0.000001, 0.00006, "Second moment of area of the rail section: its bending stiffness. With the sleeper spacing it sets the pinned-pinned peak.", Label = "rail second moment of area", Step = 0.0000001, Source = "UIC60 rail section: 3.055e-5")]
    public float RailInertiaM4 { get; init; } = 3.055e-5f;
    [Tunable("m", 0.3, 1.5, "Distance between sleepers. Closer sleepers raise the pinned-pinned peak and the sleeper-passing flutter.", Label = "sleeper spacing", Step = 0.01)]
    public float SleeperSpacingMetres { get; init; } = 0.60f;
    [Tunable("", 0, 0, "What the rail is laid on: timber, concrete or slab.")]
    public SleeperKind Sleepers { get; init; } = SleeperKind.Concrete;
    /// <summary>Rail length between joints, metres. Zero is continuous welded rail, most modern main
    /// line, with no clatter at all.</summary>
    [Tunable("m", 0, 50, "Rail length between joints. Zero is continuous welded rail with no clatter.", Label = "joint spacing", Step = 0.1)]
    public float JointSpacingMetres { get; init; }
    /// <summary>The angle the wheel drops through at a joint, radians: 3 milliradians for a tight new
    /// joint, 15 for a hammered old one. Times the speed, the impact velocity.</summary>
    [Tunable("rad", 0, 0.03, "Angle the wheel drops through at a joint. A tight new joint is 0.003, a hammered old one 0.015.", Label = "joint dip", Step = 0.001)]
    public float JointDipRadians { get; init; } = 0.008f;
    /// <summary>Joints on the two rails offset by half a rail length, so the bangs come twice as
    /// often and singly rather than in pairs. American practice staggers; British squares them up.</summary>
    [Tunable("", 0, 1, "Joints on the two rails offset by half a rail, so the bangs come singly and twice as often.", Label = "staggered joints")]
    public bool StaggeredJoints { get; init; } = true;
    /// <summary>How rough the railhead is against a reference, dB. Ground rail is -6, ordinary
    /// main line 0, and rail that has not been ground in twenty years +8.</summary>
    [Tunable("dB", -10, 15, "Railhead roughness against a reference. Ground rail is -6, ordinary main line 0, rail not ground for twenty years +8.", Label = "rail roughness", Step = 1)]
    public float RoughnessDb { get; init; }
    /// <summary>Curve radius, metres. Zero is straight. Below about four hundred metres a rigid
    /// wheelset has to creep sideways enough to squeal.</summary>
    [Tunable("m", 0, 5000, "Curve radius. Zero is straight. Below about 400 m a rigid wheelset creeps sideways enough to squeal.", Label = "curve radius", Step = 5)]
    public float CurveRadiusMetres { get; init; }
    /// <summary>The extra, dB, when the rail radiates into a structure: a bridge deck, a cutting or a
    /// tunnel.</summary>
    [Tunable("dB", 0, 20, "Extra level from a structure the rail radiates into: a bridge deck, a cutting or a tunnel. Zero on open ground.", Label = "bridge or tunnel extra", Step = 1)]
    public float StructureDb { get; init; }

    /// <summary>The pinned-pinned resonance, where half a bending wavelength in the rail is one sleeper
    /// bay: the peak in the middle of every rolling-noise spectrum.</summary>
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

    /// <summary>The sleepers going under at this speed: the low flutter under a slow train.</summary>
    public float SleeperPassHz(float mps) => mps / MathF.Max(0.2f, SleeperSpacingMetres);

    public static TrackSpec WeldedMainLine => new()
    {
        Name = "continuous welded rail on concrete, main line",
        RailKgPerMetre = 60f, SleeperSpacingMetres = 0.60f, Sleepers = SleeperKind.Concrete,
        JointSpacingMetres = 0f, RoughnessDb = 0f,
    };

    /// <summary>Jointed rail in 39 ft lengths, the North American standard (what fit in a gondola). At
    /// 25 m/s a bogie's axles are 0.10 s apart and an 85 ft car's bogies 0.74 s: clickety-clack.</summary>
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
    /// <summary>Teeth on the pinion and the gearwheel. Pinion teeth times motor revolutions is the mesh
    /// whine that rises with speed.</summary>
    [Tunable("", 8, 40, "Teeth on the motor pinion. Pinion teeth times motor revolutions is the gear whine.", Label = "pinion teeth")]
    public int PinionTeeth { get; init; } = 17;
    [Tunable("", 30, 150, "Teeth on the axle gearwheel. With the pinion this is the gear ratio.", Label = "gearwheel teeth")]
    public int GearTeeth { get; init; } = 96;
    /// <summary>Pole pairs. The air-gap pull pulses at twice the electrical frequency: a four-pole motor
    /// at 3,000 rpm hums at 200 Hz.</summary>
    [Tunable("", 1, 6, "Pole pairs of the traction motor. The magnetic hum is at twice the electrical frequency.", Label = "pole pairs")]
    public int PolePairs { get; init; } = 2;
    /// <summary>Stator slots: the rotor passing them is the thin high tone over the hum.</summary>
    [Tunable("", 12, 120, "Slots in the stator. The rotor passing them is the thin high tone over the hum.", Label = "stator slots")]
    public int StatorSlots { get; init; } = 48;
    [Tunable("rpm", 1000, 8000, "Motor speed at the vehicle's top speed.", Label = "motor top speed", Step = 100)]
    public float MotorMaxRpm { get; init; } = 4200f;
    /// <summary>The inverter's carrier, Hz, while modulating asynchronously: the fixed tone at a
    /// standstill and low speed, before it locks to the motor.</summary>
    [Tunable("Hz", 0, 5000, "The inverter's fixed switching tone at a standstill and low speed. Zero for no inverter.", Label = "inverter carrier", Step = 10)]
    public float CarrierHz { get; init; } = 1050f;
    /// <summary>Pulse counts the inverter steps down through as the output frequency rises. In each mode
    /// the carrier is that many times the electrical frequency, so the tone rises through a mode and
    /// drops at each change: the staircase of a modern train pulling out.</summary>
    public int[] PulseModes { get; init; } = { 27, 15, 9, 5, 3, 1 };
    /// <summary>Motor output frequency at which asynchronous modulation gives way, Hz.</summary>
    public float SyncFromHz { get; init; } = 20f;
    [Tunable("dB", 0, 120, "Level at one metre of the inverter's switching tone. Zero for no inverter.", Label = "inverter level", Step = 1)]
    public float InverterLevelDb { get; init; } = 84f;
    [Tunable("dB", 50, 120, "Level at one metre of the gear whine.", Label = "gear whine level", Step = 1)]
    public float GearLevelDb { get; init; } = 88f;
    [Tunable("dB", 50, 120, "Level at one metre of the motor's magnetic hum.", Label = "motor hum level", Step = 1)]
    public float MotorHumDb { get; init; } = 80f;
    /// <summary>Forced-ventilation blower: broadband, and on all the time the train is alive.</summary>
    [Tunable("dB", 40, 110, "Level at one metre of the motor cooling blower, which runs whenever the train is alive.", Label = "blower level", Step = 1)]
    public float BlowerDb { get; init; } = 74f;
}

/// <summary>A steam locomotive's front end: what happens between the cylinders and the chimney.</summary>
public sealed record SteamLocoSpec
{
    [Tunable("m", 0.8, 2.4, "Diameter of the driving wheels. With the speed it sets how fast the engine barks.", Label = "driving wheel diameter", Step = 0.01)]
    public required float DriverDiameterMetres { get; init; }
    /// <summary>Cylinders, each double-acting: a two-cylinder engine barks four times a turn.</summary>
    [Tunable("", 1, 4, "Cylinders. Each is double acting, so a two-cylinder engine barks four times a turn.")]
    public int Cylinders { get; init; } = 2;
    [Tunable("m", 0.2, 1, "Cylinder bore.", Label = "cylinder bore", Step = 0.005)]
    public float CylinderBoreMetres { get; init; } = 0.635f;
    [Tunable("m", 0.3, 1.1, "Piston stroke.", Label = "cylinder stroke", Step = 0.005)]
    public float CylinderStrokeMetres { get; init; } = 0.762f;
    /// <summary>The blast nozzle at the top of the exhaust pipe, metres: small is a fast jet and a sharp
    /// bark, big a soft exhaust.</summary>
    [Tunable("m", 0.05, 0.3, "Diameter of the blast nozzle under the chimney. Smaller is a faster jet and a sharper bark.", Label = "blast nozzle diameter", Step = 0.005)]
    public float BlastNozzleMetres { get; init; } = 0.135f;
    /// <summary>The chimney, open at both ends, so the chuff is tuned to c/2L. A tall thin stack rings; a
    /// short wide one barks.</summary>
    [Tunable("m", 0.2, 1, "Inside diameter of the chimney. A short wide stack barks; a tall thin one rings.", Label = "chimney diameter", Step = 0.01)]
    public float StackDiameterMetres { get; init; } = 0.48f;
    [Tunable("m", 0.3, 3, "Length of the chimney. It is open at both ends, so the chuff is tuned to a half wave of it.", Label = "chimney length", Step = 0.01)]
    public float StackLengthMetres { get; init; } = 0.95f;
    [Tunable("kPa", 500, 2200, "Boiler pressure, gauge.", Label = "boiler pressure", Step = 10)]
    public float BoilerKPa { get; init; } = 1550f;
    /// <summary>How far the valve gear is out of square, as a fraction of a beat: the uneven beat is most
    /// of the character, and a bad setting limps every revolution.</summary>
    [Tunable("", 0, 0.2, "How far the valve gear is out of square, as a fraction of a beat. Larger makes the engine limp at every turn.", Label = "valve setting error", Step = 0.005)]
    public float ValveSettingError { get; init; } = 0.035f;
    /// <summary>The blower and every leaking joint: a hiss even with the regulator shut.</summary>
    [Tunable("dB", 50, 110, "Level at one metre of the blower and leaking joints: a hiss that is there even with the regulator shut.", Label = "steam leak level", Step = 1)]
    public float LeakageDb { get; init; } = 86f;
    /// <summary>Rods, crossheads and axleboxes, all with play in them: a metallic clank at the
    /// driver rate and a general clatter over it.</summary>
    [Tunable("dB", 60, 115, "Level at one metre of the rods, crossheads and axleboxes clanking.", Label = "motion clank level", Step = 1)]
    public float MotionDb { get; init; } = 92f;
    [Tunable("", 0, 0, "The whistle this engine carries.", Label = "whistle", Choices = "models:whistle")]
    public string WhistleKey { get; init; } = "three_chime";
    [Tunable("", 0, 0, "The bell this engine carries.", Label = "bell", Choices = "models:bell")]
    public string BellKey { get; init; } = "loco_bell";

    /// <summary>Exhaust beats a second at this speed. Two cylinders, double acting: four a turn.</summary>
    public float ChuffHz(float mps) => 2f * Cylinders * mps / (MathF.PI * MathF.Max(0.3f, DriverDiameterMetres));
    /// <summary>The chimney's first resonance: the note in the bark.</summary>
    [JsonIgnore]
    public float StackHz => 343f / (2f * MathF.Max(0.1f, StackLengthMetres + 0.3f * StackDiameterMetres));
}

/// <summary>What a vehicle is driven by, if anything.</summary>
public sealed record RailTractionSpec
{
    [Tunable("", 0, 0, "What drives the vehicle: nothing, diesel-electric, electric or steam.", Label = "traction")]
    public required RailTraction Kind { get; init; }
    /// <summary>Diesel-electric: the prime mover, an ordinary EngineProfile key run by the same cylinder
    /// model.</summary>
    [Tunable("", 0, 0, "The diesel prime mover, as an engine model.", Label = "engine", Choices = "models:engine")]
    public string? EngineKey { get; init; }
    /// <summary>The notches the governor will hold, rpm: eight steps, not a throttle, so it changes
    /// speed in audible jumps.</summary>
    public float[] NotchRpm { get; init; } = Array.Empty<float>();
    /// <summary>Radiator fans: blades, speed and level. On a big locomotive they are a metre and a half
    /// across and most of what you hear at idle.</summary>
    [Tunable("", 2, 20, "Blades on each radiator fan. Blades times fan speed is the blade tone.", Label = "radiator fan blades")]
    public int FanBlades { get; init; } = 10;
    [Tunable("rpm", 200, 3000, "Radiator fan speed.", Label = "radiator fan speed", Step = 10)]
    public float FanRpm { get; init; } = 900f;
    [Tunable("dB", 60, 115, "Level at one metre of the radiator fans. On a big locomotive they are most of what you hear at idle.", Label = "radiator fan level", Step = 1)]
    public float FanDb { get; init; } = 96f;
    public ElectricDriveSpec? Drive { get; init; }
    public SteamLocoSpec? Steam { get; init; }
    [Tunable("", 0, 0, "The horn this vehicle carries.", Label = "horn", Choices = "models:horn")]
    public string? HornKey { get; init; }
    [Tunable("", 0, 0, "The bell this vehicle carries.", Label = "bell", Choices = "models:bell")]
    public string? BellKey { get; init; }
}

/// <summary>One vehicle in a train: a locomotive, a coach, a wagon, a tram section.</summary>
public sealed record RailVehicleSpec
{
    [Tunable("", 0, 0, "The vehicle's name as it is said.")]
    public required string Name { get; init; }
    [Tunable("m", 3, 40, "Length over the couplers.", Label = "length", Step = 0.1)]
    public required float LengthMetres { get; init; }
    /// <summary>Distance between the two bogie centres: with the length, the rhythm of a passing
    /// train.</summary>
    [Tunable("m", 1, 30, "Distance between the two bogie centres. With the length this sets the rhythm of a passing train.", Label = "bogie centres", Step = 0.1)]
    public required float BogieCentresMetres { get; init; }
    /// <summary>Axle spacing within a bogie: the "clack-CLACK" of its two axles at one joint.</summary>
    [Tunable("m", 0.8, 8, "Axle spacing within a bogie: the gap between the two clacks of one bogie at a joint.", Label = "bogie wheelbase", Step = 0.05)]
    public float BogieWheelbaseMetres { get; init; } = 2.56f;
    [Tunable("", 1, 6, "Bogies under the vehicle.")]
    public int Bogies { get; init; } = 2;
    [Tunable("", 1, 4, "Axles in each bogie.", Label = "axles per bogie")]
    public int AxlesPerBogie { get; init; } = 2;
    public required WheelsetSpec Wheels { get; init; }
    [Tunable("t", 5, 300, "Mass of the whole vehicle.", Label = "mass", Step = 1)]
    public float MassTonnes { get; init; } = 45f;
    public RailTractionSpec? Traction { get; init; }
    /// <summary>The level of the body's own ring, dB, excited by the bogies: an empty steel box drums.</summary>
    [Tunable("dB", 0, 100, "Level at one metre of the body's own ring. An empty steel box drums; zero for none.", Label = "body drum level", Step = 1)]
    public float BodyDrumDb { get; init; }
    [Tunable("Hz", 20, 200, "Where the body rings when it drums.", Label = "body drum pitch", Step = 1)]
    public float BodyDrumHz { get; init; } = 70f;

    [JsonIgnore]
    public int Axles => Bogies * AxlesPerBogie;
}

/// <summary>How many of one vehicle, in a row: a named pair, because an author writes it in a file.</summary>
public sealed record ConsistEntry
{
    public required RailVehicleSpec Vehicle { get; init; }
    [Tunable("", 1, 200, "How many of this vehicle in a row.")]
    public int Count { get; init; } = 1;
    public void Deconstruct(out RailVehicleSpec vehicle, out int count) { vehicle = Vehicle; count = Count; }
}

/// <summary>
/// Where a train's sound comes from (bogies, drives, body drums, horn, bell along the consist), as a
/// list the server places and the client indexes. The server spawns one entity per entry at
/// <c>head − Along</c> round the track, so a 55 m set wraps a 26 m corner; the client runs one synth
/// and gives entity <c>i</c> source <c>i</c>. This order must match <c>TrainSynth.BuildVehicle</c>'s,
/// or every bogie plays in the wrong place (<c>RailAndSignalTests</c> holds them together).
/// </summary>
public static class TrainLayout
{
    public enum Kind { Bogie, Body, ExhaustStack, RadiatorFans, Traction, Chimney, Horn, Whistle, Bell }

    /// <summary>One source. <paramref name="HeadroomDb"/> is how far its loudest moments stand over
    /// <paramref name="LevelDb"/>: the shared figure, except a bell's, which is nothing but blows.</summary>
    public sealed record Entry(int Index, Kind Kind, string Label, float AlongMetres, float HeightMetres,
                               float ExtentMetres, float LevelDb, float HeadroomDb = VehicleProfile.PeakHeadroomDb)
    {
        /// <summary>Horns, whistles and bells make no sound until the train sounds them
        /// (<see cref="TrainSignal"/>): silent the rest of the time, and given no voice then.</summary>
        public bool IsSignal => Kind is Kind.Horn or Kind.Whistle or Kind.Bell;
    }

    /// <summary>A road locomotive's exhaust stack in run 8, at 1 m: certification-style figures put
    /// the whole locomotive at 90-96 dBA at 30 m, which is about this at the stack.</summary>
    public const float LocomotiveStackDb = 112f;

    /// <summary>
    /// The rolling noise of one bogie at 1 m, at the profile's typical speed: the per-axle reference at
    /// 100 km/h under the speed law the model measures, 28·log10(V) (docs/TRAINS.md), so the level used
    /// for ranking is what the synth makes.
    /// </summary>
    public static float BogieLevelDb(TrainProfile p, RailVehicleSpec v)
        => p.RollingReferenceDb
         + 10f * MathF.Log10(MathF.Max(1, v.AxlesPerBogie))
         + 28f * MathF.Log10(MathF.Max(0.05f, p.TypicalSpeedMps * 3.6f / 100f))
         + (v.Wheels.TreadBraked ? 9f : 0f);

    public static IReadOnlyList<Entry> Sources(TrainProfile p)
    {
        var list = new List<Entry>();
        float along = 0f;
        int unit = 0;
        bool horn = false, whistle = false, bell = false;
        foreach (var (v, count) in p.Consist)
            for (int c = 0; c < count; c++, unit++)
            {
                float mid = along + v.LengthMetres * 0.5f;
                for (int b = 0; b < v.Bogies; b++)
                {
                    float at = v.Bogies == 1 ? mid : mid + (b - (v.Bogies - 1) * 0.5f) * v.BogieCentresMetres;
                    list.Add(new Entry(list.Count, Kind.Bogie, $"{v.Name} #{unit + 1} bogie {b + 1}", at, 0.45f, 2.2f, BogieLevelDb(p, v)));
                }
                if (v.BodyDrumDb > 1f)
                    list.Add(new Entry(list.Count, Kind.Body, $"{v.Name} #{unit + 1} body", mid, 2.0f, v.LengthMetres * 0.5f, v.BodyDrumDb));
                if (v.Traction is { } tr)
                {
                    switch (tr.Kind)
                    {
                        case RailTraction.DieselElectric when tr.EngineKey != null:
                            list.Add(new Entry(list.Count, Kind.ExhaustStack, $"{v.Name} #{unit + 1} exhaust stack",
                                               mid - v.LengthMetres * 0.22f, 4.4f, 1.0f, LocomotiveStackDb));
                            list.Add(new Entry(list.Count, Kind.RadiatorFans, $"{v.Name} #{unit + 1} radiator fans",
                                               mid + v.LengthMetres * 0.34f, 4.2f, 1.6f, tr.FanDb));
                            break;
                        case RailTraction.Electric when tr.Drive != null:
                            for (int b = 0; b < v.Bogies; b++)
                            {
                                float at = v.Bogies == 1 ? mid : mid + (b - (v.Bogies - 1) * 0.5f) * v.BogieCentresMetres;
                                float lv = 10f * MathF.Log10(MathF.Pow(10f, tr.Drive.InverterLevelDb / 10f) + MathF.Pow(10f, tr.Drive.GearLevelDb / 10f)
                                                             + MathF.Pow(10f, tr.Drive.MotorHumDb / 10f) + MathF.Pow(10f, tr.Drive.BlowerDb / 10f));
                                list.Add(new Entry(list.Count, Kind.Traction, $"{v.Name} #{unit + 1} traction {b + 1}", at, 0.7f, 2.0f, lv));
                            }
                            break;
                        case RailTraction.Steam when tr.Steam != null:
                            list.Add(new Entry(list.Count, Kind.Chimney, $"{v.Name} #{unit + 1} chimney",
                                               along + v.LengthMetres * 0.18f, 4.6f, 0.8f, tr.Steam.MotionDb + 6f));
                            break;
                    }
                    // The signals' levels are their own models' (ModelLibrary), so a tram's two-chime
                    // horn is placed as the 124 dB it is and not as a freight horn's 139.
                    if (tr.HornKey != null && !horn)
                    {
                        horn = true;
                        list.Add(new Entry(list.Count, Kind.Horn, $"{v.Name} #{unit + 1} horn", along + 2.5f, 4.8f, 0.6f,
                                           ModelLibrary.Horn(tr.HornKey).ReferenceDb));
                    }
                    if (tr.Steam?.WhistleKey is { } wk && !whistle)
                    {
                        whistle = true;
                        list.Add(new Entry(list.Count, Kind.Whistle, $"{v.Name} #{unit + 1} whistle", along + v.LengthMetres * 0.55f, 4.4f, 0.5f,
                                           ModelLibrary.Whistle(wk).ReferenceDb));
                    }
                    if ((tr.BellKey ?? tr.Steam?.BellKey) is { } bk && !bell)
                    {
                        bell = true;
                        var bellSpec = ModelLibrary.Bell(bk);
                        list.Add(new Entry(list.Count, Kind.Bell, $"{v.Name} #{unit + 1} bell", along + 1.8f, 3.2f, 0.4f,
                                           bellSpec.ReferenceDb, MathF.Max(VehicleProfile.PeakHeadroomDb, bellSpec.PeakHeadroomDb)));
                    }
                }
                along += v.LengthMetres;
            }
        return list;
    }
}

/// <summary>A train: vehicles in order, on a track.</summary>
public sealed record TrainProfile
{
    [Tunable("", 0, 0, "The train's name as it is said.")]
    public required string Name { get; init; }
    /// <summary>The consist, head to tail.</summary>
    public required ConsistEntry[] Consist { get; init; }
    public required TrackSpec Track { get; init; }
    [Tunable("m/s", 1, 90, "The speed the train usually runs at. Rolling noise rises with it.", Label = "typical speed", Step = 0.5)]
    public float TypicalSpeedMps { get; init; } = 25f;
    /// <summary>
    /// Per wheelset at one metre at 100 km/h on reference-rough rail, disc braked: the rail model's one
    /// anchor. Anchored against a pass-by, the measurement that exists (82 dBA at 7.5 m from a
    /// disc-braked passenger train at 80 km/h); extrapolating one axle to a metre and back put it twelve
    /// decibels light.
    /// </summary>
    [Tunable("dB", 80, 120, "Rolling noise per wheelset at one metre at 100 km/h on reference rail, disc braked. The one level anchor of the rail model.", Label = "rolling noise per wheelset", Step = 1, Source = "pass-by measurement: 82 dBA at 7.5 m from a disc-braked passenger train at 80 km/h")]
    public float RollingReferenceDb { get; init; } = 104f;

    [JsonIgnore]
    public int TotalAxles => Consist.Sum(c => c.Vehicle.Axles * c.Count);
    [JsonIgnore]
    public float LengthMetres => Consist.Sum(c => c.Vehicle.LengthMetres * c.Count);

    // ── The vehicles ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A GE Genesis, on most Amtrak trains away from the wires: a turbocharged 7FDL16 (228 mm bore,
    /// 267 mm stroke) from 440 rpm at idle to 1,050 in notch 8, firing 59 to 140 Hz. Disc-braked; its
    /// radiator fans are most of what you hear when it stands.
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
    /// An EMD road switcher: a 645E3 two-stroke V16 firing every cylinder every revolution, 240 Hz at
    /// its 900 rpm maximum against a Genesis's 140. That and the Roots blower are the EMD sound.
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

    /// <summary>A freight wagon: shorter, stiffer, tread-braked (nine decibels over the coach) and
    /// usually empty, so its body drums.</summary>
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

    /// <summary>A low-floor tram: small resilient wheels that do not squeal, a four-pole motor through a
    /// 5.6:1 gearbox, and an inverter stepping down its pulse count as it accelerates.</summary>
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
            HornKey = "lrv_two_chime", BellKey = "tram_gong",
        },
        BodyDrumDb = 70f, BodyDrumHz = 60f,
    };

    /// <summary>A heavy metro car on chopper-fed DC motors: no inverter staircase, gear whine and motor
    /// growl, and solid wheels that squeal on every curve.</summary>
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
    /// A 4-8-4: two 25 by 30 inch cylinders, 1.85 m drivers, 225 psi, a 135 mm blast nozzle under a
    /// 0.95 m chimney. At 25 m/s it barks 17 times a second, the beats running together into a roar.
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

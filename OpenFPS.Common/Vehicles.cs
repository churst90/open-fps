using System.Collections.Generic;
using System.Numerics;
using OpenFPS.Common.Editing;

namespace OpenFPS.Common;

/// <summary>A manual gearbox: the ratios, and how long the driver's left foot takes.</summary>
public sealed record Gearbox
{
    /// <summary>Ratio per gear, first at index 0.</summary>
    public required float[] Ratios { get; init; }
    [Tunable("", 1.5, 15, "Ratio of the final drive: turns of the gearbox output for one turn of the wheel. It includes any chain or primary reduction.", Label = "final drive ratio", Step = 0.01)]
    public required float FinalDrive { get; init; }
    /// <summary>Rolling radius of the driven wheel, metres.</summary>
    [Tunable("m", 0.15, 0.8, "Rolling radius of the driven wheel.", Label = "wheel radius", Step = 0.005)]
    public required float WheelRadiusMetres { get; init; }

    /// <summary>How long the clutch is down during a shift: the silence in the middle of it, most of
    /// what makes a change sound like a person. A quick change is about a quarter of a second.</summary>
    [Tunable("s", 0.02, 2, "How long the clutch is down during a shift: the gap in the sound. A quick change is about a quarter of a second.", Label = "shift time", Step = 0.01)]
    public float ShiftSeconds { get; init; } = 0.28f;
    /// <summary>RPM the driver shifts up at when accelerating hard.</summary>
    [Tunable("rpm", 500, 20000, "Engine speed the driver changes up at when accelerating hard.", Label = "upshift speed", Step = 50)]
    public float UpshiftRpm { get; init; } = 6100f;
    /// <summary>...and drops to when coasting down.</summary>
    [Tunable("rpm", 300, 15000, "Engine speed the driver changes down at when coasting.", Label = "downshift speed", Step = 50)]
    public float DownshiftRpm { get; init; } = 1500f;
    /// <summary>RPM the driver changes up at on a light throttle. Unset, 85 % of the torque peak: right
    /// for a car, but a litre bike (peak at 11,000, first gear to 150 km/h) never left first in town.</summary>
    public float? CruiseUpshiftRpm { get; init; }
    /// <summary>RPM held while the clutch slips pulling away. Unset, twice idle or 40 % of the torque
    /// peak, whichever is higher.</summary>
    public float? LaunchRpm { get; init; }

    public int TopGear => Ratios.Length;

    /// <summary>Engine RPM for a road speed in a given gear.</summary>
    public float RpmFor(float speedMetresPerSecond, int gear)
    {
        if (gear < 1 || gear > Ratios.Length) return 0f;
        float wheelRevsPerSec = speedMetresPerSecond / (2f * MathF.PI * WheelRadiusMetres);
        return MathF.Max(0f, wheelRevsPerSec * Ratios[gear - 1] * FinalDrive * 60f);
    }

    /// <summary>Road speed at which a gear reaches a given RPM.</summary>
    public float SpeedFor(float rpm, int gear)
    {
        if (gear < 1 || gear > Ratios.Length) return 0f;
        return rpm / 60f * 2f * MathF.PI * WheelRadiusMetres / (Ratios[gear - 1] * FinalDrive);
    }

    /// <summary>A six-speed sports car on 255/40R19s.</summary>
    public static Gearbox SixSpeedSports => new()
    {
        Ratios = new[] { 2.97f, 2.07f, 1.43f, 1.00f, 0.84f, 0.56f },
        FinalDrive = 3.42f,
        WheelRadiusMetres = 0.337f,
        ShiftSeconds = 0.26f,
        UpshiftRpm = 6150f,
        DownshiftRpm = 1450f,
    };
}

/// <summary>A fan clutch: when the cooling fan is driven, from the coolant's temperature (the heat
/// balance is CoolingSystem's).</summary>
public sealed record FanClutchSpec
{
    /// <summary>Coolant temperature the clutch engages at, degrees C.</summary>
    public float EngageCelsius { get; init; } = 95f;
    /// <summary>And releases at, lower, so it does not chatter on the threshold.</summary>
    public float ReleaseCelsius { get; init; } = 90f;
    /// <summary>Fan speed over its driven speed while disengaged: bearing and fluid drag, and the air
    /// through the grille windmilling it.</summary>
    public float DisengagedFraction { get; init; } = 0.2f;
    /// <summary>How long the fan takes to come up to speed once engaged, seconds.</summary>
    public float EngageSeconds { get; init; } = 1f;

    /// <summary>An air-actuated friction clutch, as on a heavy truck: a second to lock up.</summary>
    public static FanClutchSpec OnOff => new();

    /// <summary>A viscous (silicone fluid) clutch, as on most buses: it thickens as it warms, so the
    /// fan comes up over several seconds, and disengaged it drags a little harder.</summary>
    public static FanClutchSpec Viscous => new() { DisengagedFraction = 0.25f, EngageSeconds = 6f };
}

/// <summary>
/// A car's electric cooling fan: a motor on a relay behind the radiator, switched by the coolant and by
/// the air conditioning, its speed nothing to do with the crank's. Two speeds, each on at one coolant
/// temperature and off at a lower one. The condenser sits in front of the radiator, so while the
/// compressor runs at low road speed the fan runs at low speed whatever the coolant: a queue of cars at
/// a light on a hot day is a row of fans.
/// </summary>
public sealed record ElectricFanSpec
{
    /// <summary>Coolant temperature the low speed comes on at, and goes off at, degrees C. The
    /// first stage of a two-stage radiator thermo-switch (VW-type switches are marked 95/84 C for
    /// stage one and 102/91 C for stage two).</summary>
    public float LowOnCelsius { get; init; } = 95f;
    public float LowOffCelsius { get; init; } = 84f;
    /// <summary>...and the high speed, on and off.</summary>
    public float HighOnCelsius { get; init; } = 102f;
    public float HighOffCelsius { get; init; } = 91f;
    /// <summary>The low speed over the high one. A series resistor drops a two-speed fan's motor to
    /// roughly two thirds of its speed; the broadband falls as the cube of that, about 11 dB.</summary>
    public float LowSpeedFraction { get; init; } = 0.65f;
    /// <summary>How long the motor takes to come up to speed, seconds.</summary>
    public float SpinUpSeconds { get; init; } = 1.5f;
    /// <summary>The air conditioning keeps the fan running below this road speed, m/s (about 30 km/h),
    /// and lets it go above half as fast again. A setting, not a published figure.</summary>
    public float AirConBelowMetresPerSecond { get; init; } = 8f;
    /// <summary>The air temperature drivers turn the air conditioning on at, degrees C, and how far
    /// either side of it one driver differs from the next (each car is fixed, from its seed). A
    /// setting, not a survey.</summary>
    public float AirConAmbientCelsius { get; init; } = 24f;
    public float AirConAmbientSpread { get; init; } = 4f;

    /// <summary>A two-speed fan with air conditioning: what nearly every road car carries.</summary>
    public static ElectricFanSpec TwoSpeed => new();
}

/// <summary>
/// The engine bay as something the engine's own noise has to get out of: its openings and its lining.
///
/// An enclosure with holes is an energy balance: an open hole absorbs everything that reaches it, so
/// the share that escapes is open / (open + absorption) and the pressure its square root,
/// <see cref="Leakage"/> (Beranek and Ver, Noise and Vibration Control Engineering, enclosures). The
/// bonnet is left out: 6-7 kg/m² of steel loses 25 dB and more above 250 Hz.
///
/// The check is NHTSA (FMVSS 141 final rule, 81 FR 90416, 14 December 2016, VRTC phase 3): idling ICE
/// cars were "6 to 10 dB lower" behind than in front, and passed at 10 km/h at 56.6 to 59.9 dB(A).
/// The Car preset lands an idling 1.6 hatchback inside both (AudioLab --car-fronts); the areas are
/// estimates from the parts' sizes. A sealed-bay constant (0.15) had put it louder behind than in front.
/// </summary>
public sealed record EngineBaySpec
{
    /// <summary>The grille's open area as the sound sees it, through the condenser and radiator
    /// cores, square metres.</summary>
    public float GrilleOpenM2 { get; init; }
    /// <summary>The bay floor that is open to the road, square metres: the bay's footprint less
    /// whatever undertray covers it.</summary>
    public float UndersideOpenM2 { get; init; }
    /// <summary>
    /// The absorption inside the bay, square metres (metric sabins): each lining's area times its
    /// absorption coefficient, plus the bare metal and the engine's own surfaces at about 0.05.
    /// </summary>
    public float AbsorptionM2 { get; init; }
    /// <summary>
    /// What the gap under the floor takes off the sound from the bay's open floor, dB per metre of
    /// underbody it runs along (BayRadiation). No published figure; fitted to NHTSA's 6 to 10 dB: at
    /// 0.6 the 1.6 hatchback is 6.4 dB(A) louder in front and the 2.8 diesel pickup 9.7 (at 1.0 the
    /// pickup went over, 10.7; at 1.5, 11.8). A hatchback's floor loses about 2 dB to the back bumper.
    /// </summary>
    public float UnderbodyLossDbPerMetre { get; init; } = 0.6f;

    /// <summary>The pressure share of the engine's noise that gets out, 0..1:
    /// sqrt(open / (open + absorption)).</summary>
    public float Leakage
    {
        get
        {
            float open = GrilleOpenM2 + UndersideOpenM2;
            return open <= 0f ? 0f : MathF.Sqrt(open / (open + MathF.Max(0f, AbsorptionM2)));
        }
    }

    /// <summary>The share of what gets out that leaves by the grille rather than underneath.</summary>
    public float GrilleShare
    {
        get
        {
            float open = GrilleOpenM2 + UndersideOpenM2;
            return open <= 0f ? 0f : GrilleOpenM2 / open;
        }
    }

    /// <summary>
    /// A modern car: a plastic undertray over about half of a square-metre bay floor, the grille's
    /// opening seen through the condenser and radiator, a lined bonnet. Open 0.12 + 0.5 m^2;
    /// absorption 0.9 m^2 of bonnet liner at 0.6, 0.6 m^2 of bulkhead pad at 0.4 and 3 m^2 of bare
    /// metal and engine at 0.05, 0.93 m^2. Leakage 0.63, about -4 dB.
    /// </summary>
    public static EngineBaySpec Car => new() { GrilleOpenM2 = 0.12f, UndersideOpenM2 = 0.5f, AbsorptionM2 = 0.93f };

    /// <summary>
    /// A car of the sixties and seventies: no undertray, no bonnet liner, a big grille. Open 0.2 +
    /// 1.0 m^2; absorption a firewall pad and bare metal, 0.3 m^2. Leakage 0.89, about -1 dB.
    /// </summary>
    public static EngineBaySpec ClassicCar => new() { GrilleOpenM2 = 0.2f, UndersideOpenM2 = 1.0f, AbsorptionM2 = 0.3f };

    /// <summary>
    /// A pickup or a body-on-frame utility: a bigger grille than a car's, and an engine undercover
    /// (skid plate) over the front of a bay floor that is otherwise open between the frame rails.
    /// Open 0.2 + 0.5 m^2; absorption 0.9 m^2. Leakage 0.66, about -3.6 dB.
    /// </summary>
    public static EngineBaySpec Pickup => new() { GrilleOpenM2 = 0.2f, UndersideOpenM2 = 0.5f, AbsorptionM2 = 0.9f };

    /// <summary>
    /// A step van or a delivery truck: an engine under a short bonnet on a truck chassis, open
    /// underneath and barely lined. Open 0.35 + 1.2 m^2; absorption 0.5 m^2. Leakage 0.87.
    /// </summary>
    public static EngineBaySpec Van => new() { GrilleOpenM2 = 0.35f, UndersideOpenM2 = 1.2f, AbsorptionM2 = 0.5f };
}

/// <summary>The tyre and the road under it.</summary>
public sealed record TyreProfile
{
    /// <summary>Tread blocks around the circumference. Their passing rate is the tone in tyre roar that
    /// rises in pitch with speed.</summary>
    [Tunable("", 0, 150, "Tread blocks around the circumference. Their passing rate is the tone of the tyre roar. Zero for a slick or a grooved bike tyre.")]
    public int TreadBlocks { get; init; } = 68;
    /// <summary>How rough the surface is, 0 = polished concrete, 1 = coarse chip seal: broadband roar
    /// against the tone.</summary>
    [Tunable("", 0, 1, "How rough the road surface is: 0 is polished concrete, 1 coarse chip seal. Rougher makes more broadband roar.", Label = "road roughness", Step = 0.05)]
    public float SurfaceRoughness { get; init; } = 0.55f;
    /// <summary>
    /// One tyre's rolling noise at 20 m/s (72 km/h), dB SPL at 1 m; tyres dominate a car above about
    /// 40 km/h. A car at 70 km/h on dense asphalt passes at about 73 dB(A) at 7.5 m, nearly all tyres:
    /// 90.5 dB at a metre for four, 84 for one. CNOSSOS-EU's light-vehicle rolling sound power (103 dB at
    /// 70 km/h) agrees within two decibels. Ten to fifteen decibels short, the cars had no roar at all.
    /// </summary>
    [Tunable("dB", 60, 100, "One tyre's rolling noise at 72 km/h, at 1 metre.", Label = "rolling noise at 72 km/h", Step = 0.5, Source = "pass-by measurements, 73 dB(A) at 7.5 m for a car at 70 km/h; CNOSSOS-EU light vehicle rolling noise")]
    public float ReferenceDb { get; init; } = 84f;

    // ── Sliding ─────────────────────────────────────────────────────────────────────────────────
    //
    // On the tyre, not the car or the map: slicks on a van squeal like a racing car.

    /// <summary>
    /// Peak friction this tyre can deliver, in g, before the contact patch slides and sings. A road tyre
    /// on dry asphalt is about 0.95, a performance tyre 1.1, a loaded truck tyre 0.75.
    /// </summary>
    [Tunable("g", 0.3, 5, "Peak friction the tyre can deliver before it slides. A road tyre on dry asphalt is about 0.95, a loaded truck tyre 0.75.", Label = "peak grip", Step = 0.05)]
    public float PeakGripG { get; init; } = 0.95f;

    /// <summary>The stick-slip resonance of a tread element, Hz: the squeal's fundamental. It falls as
    /// tyres get bigger; a kart tyre shrieks, a truck tyre groans.</summary>
    [Tunable("Hz", 200, 3000, "Stick-slip resonance of a tread element: the note the tyre squeals at. Bigger tyres squeal lower.", Label = "squeal pitch", Step = 10)]
    public float SquealHz { get; init; } = 950f;

    /// <summary>How sharp that resonance is: high a clean squeal, low a rough scrub.</summary>
    [Tunable("", 1, 40, "How sharp the squeal resonance is. High is a clean squeal, low a rough scrub.", Label = "squeal sharpness", Step = 0.5)]
    public float SquealQ { get; init; } = 14f;

    /// <summary>Level of a full squeal at 1 m, dB SPL.</summary>
    [Tunable("dB", 70, 120, "Level of a full squeal at 1 metre.", Label = "squeal level", Step = 1)]
    public float SquealDb { get; init; } = 92f;

    // ── Force ───────────────────────────────────────────────────────────────────────────────────
    //
    // What the tyre pushes on the road with, for the wheel model (WheelDynamics). Pacejka's Magic
    // Formula, F = D sin(C atan(B x - E (B x - atan(B x)))), with the peak D = mu Fz from
    // PeakGripG and the rest from a published parameter set: the 205/60R15 91V passenger tyre at
    // 2.2 bar in Pacejka, Tire and Vehicle Dynamics (2nd ed. 2006, appendix 3), as reproduced in
    // github.com/jcmadsen/PacTire_Matlab (205_60_R15_91V_2-2bar.tire). Only the first coefficient of
    // each term is used (PCY1, PEY1, PKY1, PKY2, PCX1, PEX1, PKX1, PKX2, PDY2), so camber, the
    // curvature's dependence on load and the curve offsets are left out. The nominal load those
    // coefficients are relative to is taken as the wheel's own static load on its vehicle, which
    // scales the passenger tyre to a truck's or a motorcycle's: no other tyre has published data.

    /// <summary>Lateral shape factor C (PCY1).</summary>
    [Tunable("", 0.8, 2, "Magic Formula lateral shape factor C (PCY1).", Step = 0.001, Source = "Pacejka, Tire and Vehicle Dynamics, 2nd ed. 2006, appendix 3: the 205/60R15 91V tyre at 2.2 bar")]
    public float LateralShape { get; init; } = 1.193f;
    /// <summary>Lateral curvature factor E (PEY1).</summary>
    [Tunable("", -3, 1, "Magic Formula lateral curvature factor E (PEY1).", Step = 0.001, Source = "Pacejka, Tire and Vehicle Dynamics, 2nd ed. 2006, appendix 3: the 205/60R15 91V tyre at 2.2 bar")]
    public float LateralCurvature { get; init; } = -1.003f;
    /// <summary>Cornering stiffness at the nominal load, over that load, before the saturation
    /// (PKY1): K = PKY1 Fz0 sin(2 atan(Fz / (PKY2 Fz0))), per radian.</summary>
    [Tunable("per radian", 3, 40, "Cornering stiffness at the nominal load, over that load (PKY1).", Step = 0.05, Source = "Pacejka, Tire and Vehicle Dynamics, 2nd ed. 2006, appendix 3: the 205/60R15 91V tyre at 2.2 bar")]
    public float CorneringStiffness { get; init; } = 14.95f;
    /// <summary>The load, in nominal loads, at which the cornering stiffness stops rising (PKY2).</summary>
    [Tunable("", 0.5, 6, "The load, in nominal loads, at which the cornering stiffness stops rising (PKY2).", Step = 0.01, Source = "Pacejka, Tire and Vehicle Dynamics, 2nd ed. 2006, appendix 3: the 205/60R15 91V tyre at 2.2 bar")]
    public float CorneringStiffnessLoad { get; init; } = 2.130f;
    /// <summary>Longitudinal shape factor C (PCX1).</summary>
    [Tunable("", 0.8, 2.5, "Magic Formula longitudinal shape factor C (PCX1).", Step = 0.001, Source = "Pacejka, Tire and Vehicle Dynamics, 2nd ed. 2006, appendix 3: the 205/60R15 91V tyre at 2.2 bar")]
    public float LongitudinalShape { get; init; } = 1.685f;
    /// <summary>Longitudinal curvature factor E (PEX1).</summary>
    [Tunable("", -3, 1, "Magic Formula longitudinal curvature factor E (PEX1).", Step = 0.001, Source = "Pacejka, Tire and Vehicle Dynamics, 2nd ed. 2006, appendix 3: the 205/60R15 91V tyre at 2.2 bar")]
    public float LongitudinalCurvature { get; init; } = 0.344f;
    /// <summary>Longitudinal slip stiffness over load, per unit slip (PKX1), and its change with load (PKX2).</summary>
    [Tunable("", 3, 60, "Longitudinal slip stiffness over load, per unit slip (PKX1).", Step = 0.05, Source = "Pacejka, Tire and Vehicle Dynamics, 2nd ed. 2006, appendix 3: the 205/60R15 91V tyre at 2.2 bar")]
    public float SlipStiffness { get; init; } = 21.51f;
    [Tunable("", -1, 1, "Change of the slip stiffness with load (PKX2).", Step = 0.001, Source = "Pacejka, Tire and Vehicle Dynamics, 2nd ed. 2006, appendix 3: the 205/60R15 91V tyre at 2.2 bar")]
    public float SlipStiffnessLoad { get; init; } = -0.163f;
    /// <summary>How the friction coefficient changes with load, per nominal load: PDY2 over PDY1,
    /// 0.145 / 0.990, falling. Loaded half as hard again it grips about 7 % less per newton, which is
    /// why load transfer in a corner costs the axle grip.</summary>
    [Tunable("", -0.5, 0, "Change of the friction coefficient with load, per nominal load (PDY2 over PDY1). Negative: a harder loaded tyre grips less per newton.", Step = 0.001, Source = "Pacejka, Tire and Vehicle Dynamics, 2nd ed. 2006, appendix 3: the 205/60R15 91V tyre at 2.2 bar")]
    public float LoadSensitivity { get; init; } = -0.146f;

    // ── Water ───────────────────────────────────────────────────────────────────────────────────
    //
    // What decides how a tyre meets a wet road (RoadWater, docs/WET_ROADS.md): its pressure sets the
    // speed it aquaplanes at (Horne's 6.34 sqrt(p kPa) km/h for a flooded smooth tyre; Gallaway's
    // equation for a treaded one), and its tread's grooves carry the water out of the contact patch,
    // so a worn tyre or a slick lets a film lift it sooner.

    /// <summary>Inflation pressure, kPa. The force data above are for the tyre at 2.2 bar.</summary>
    [Tunable("kPa", 80, 1000, "Inflation pressure. It sets the speed the tyre aquaplanes at.", Label = "inflation pressure", Step = 5)]
    public float InflationKPa { get; init; } = 220f;

    /// <summary>Tread depth, mm: a car tyre is 8 mm new and 1.6 at the legal limit; a part-worn one, 5.</summary>
    [Tunable("mm", 0, 30, "Tread depth. A car tyre is 8 mm new and 1.6 at the legal limit. Deeper grooves clear water better.", Label = "tread depth", Step = 0.5)]
    public float TreadDepthMm { get; init; } = 5f;

    /// <summary>A decent road tyre on dry asphalt.</summary>
    public static TyreProfile SportsOnAsphalt => new();

    /// <summary>
    /// A sports motorcycle's road tyre: long grooves and no blocks, so roar and no tread tone (a car
    /// tyre's 68 blocks on a bike sang at 859 Hz and 1.7 kHz at 90 km/h, over the whole bike).
    /// </summary>
    public static TyreProfile SportBikeRoad => new()
    {
        TreadBlocks = 0, SurfaceRoughness = 0.45f,
        // Narrower than a car tyre (120-180 mm against 225): about 4 dB less rolling noise each.
        ReferenceDb = 80f,
        PeakGripG = 1.1f,
        // 2.5 bar front and 2.9 rear on a sports bike; 5-6 mm of tread new, 3 part-worn.
        InflationKPa = 270f, TreadDepthMm = 3f,
    };

    /// <summary>
    /// A racing slick: no tread, so the rolling sound is all roar; when it lets go it is loud and low,
    /// because the element sticking and slipping is the whole contact patch.
    /// </summary>
    public static TyreProfile RaceSlick => new()
    {
        TreadBlocks = 0, SurfaceRoughness = 0.35f, ReferenceDb = 82f,
        // Effective grip on a banked oval, not the slick's 1.75 g on the flat: the client measures
        // lateral acceleration in the world frame, banking included, and cannot see a bank angle.
        // TODO: send the server's friction demand from the racing line instead (a protocol change).
        PeakGripG = 3.1f, SquealHz = 620f, SquealQ = 17f, SquealDb = 99f,
        // No grooves at all, run at 1.4-1.8 bar hot: it aquaplanes on any film.
        InflationKPa = 160f, TreadDepthMm = 0f,
    };

    /// <summary>A loaded truck tyre: coarse tread, a lot of roar, and it gives up early and groans.</summary>
    public static TyreProfile TruckOnAsphalt => new()
    {
        TreadBlocks = 96, SurfaceRoughness = 0.72f, ReferenceDb = 85f,
        PeakGripG = 0.75f, SquealHz = 430f, SquealQ = 9f, SquealDb = 97f,
        // 7.6 bar (110 psi) and 12 mm of tread part-worn (a drive tyre is 20-26 mm new).
        InflationKPa = 760f, TreadDepthMm = 12f,
    };

    /// <summary>Every tyre by key, so a machine's parts list can name one (see MachinePart).</summary>
    public static IReadOnlyDictionary<string, Func<TyreProfile>> Presets { get; } =
        new Dictionary<string, Func<TyreProfile>>(StringComparer.OrdinalIgnoreCase)
        {
            ["sports_asphalt"] = () => SportsOnAsphalt,
            ["race_slick"] = () => RaceSlick,
            ["truck_asphalt"] = () => TruckOnAsphalt,
        };

    public static TyreProfile ByName(string key)
        => Presets.TryGetValue(key, out var make) ? make()
         : throw new ArgumentException($"No tyre preset '{key}'. Known: {string.Join(", ", Presets.Keys)}");
}

/// <summary>Everything about one vehicle.</summary>
public sealed record VehicleProfile
{
    public required string Name { get; init; }
    public required EngineProfile Engine { get; init; }
    public required Gearbox Gearbox { get; init; }
    public required TyreProfile Tyres { get; init; }
    public float MassKg { get; init; } = 1620f;
    public float DragArea { get; init; } = 0.62f;      // Cd * A
    public float RollingResistance { get; init; } = 0.013f;

    /// <summary>Where each emitter sits relative to the car's centre, metres (x right, y up, z forward).
    /// At close range the exhaust three metres behind the intake tells a listener which way the car
    /// points.</summary>
    public float ExhaustOffsetZ { get; init; } = -2.05f;
    public float IntakeOffsetZ { get; init; } = 1.35f;

    /// <summary>How far right of the centreline the tailpipe is, metres: a side pipe ahead of the rear
    /// wheel.</summary>
    public float ExhaustOffsetX { get; init; }

    /// <summary>How high the tailpipe is above the contact patch, metres: a truck's stack is not a
    /// saloon's, and it keeps the occlusion probe out of the road.</summary>
    public float ExhaustHeight { get; init; } = 0.3f;

    /// <summary>
    /// Which way the tailpipe points, in the vehicle's frame (x right, y up, z forward): straight back
    /// unless declared (a stock car's side exit, a truck's stack at the sky). A pipe beams its highs
    /// along this axis, and the body blocks the other side (ExhaustRadiation).
    /// </summary>
    public Vector3 ExhaustAxis { get; init; } = new(0f, 0f, -1f);

    /// <summary>How high the intake mouth is above the contact patch, metres: about the top of the
    /// bay on a car, over the driver's head on a formula car. The occlusion probe goes there.</summary>
    public float IntakeHeight { get; init; } = 0.7f;

    /// <summary>
    /// The engine sits at the back, behind the passengers (a city transit bus): declared, not guessed
    /// from the slots. The bay, fan and intake then leave by the rear voice with the exhaust, and the
    /// front voice is the front tyres, the door air and the chime.
    /// </summary>
    public bool EngineAtRear { get; init; }

    /// <summary>Where the front voice sits: at the intake, over the engine, unless the engine is in
    /// the back, when it is the front axle.</summary>
    public float FrontTapZ => EngineAtRear ? FrontAxleZ : IntakeOffsetZ;
    public float FrontTapHeight => EngineAtRear ? 0.8f : IntakeHeight;

    /// <summary>How far back along <see cref="ExhaustOffsetZ"/> a single combined engine voice sits:
    /// between intake and exhaust, nearer the exhaust, where most of the sound is.</summary>
    public const float ExhaustEmitterBias = 0.6f;

    /// <summary>Where the car's engine voice sits, in the car's own frame: the emitter slot occlusion,
    /// distance and direction are measured from.</summary>
    public Vector3 ExhaustOffset => new(ExhaustOffsetX * ExhaustEmitterBias, ExhaustHeight, ExhaustOffsetZ * ExhaustEmitterBias);

    /// <summary>Where the gas leaves, in the vehicle's own frame: the true tailpipe, not the
    /// compromise a single voice sits at.</summary>
    public Vector3 ExhaustSlot => new(ExhaustOffsetX, ExhaustHeight, ExhaustOffsetZ);

    public float FrontAxleZ { get; init; } = 1.25f;
    public float RearAxleZ { get; init; } = -1.35f;

    /// <summary>
    /// The body's outside size, metres: bumper to bumper, mirror-less width, ground to roof. What you
    /// walk into; declared because nothing else on the profile knows where the bumpers are.
    /// </summary>
    public float LengthMetres { get; init; } = 4.6f;
    public float WidthMetres { get; init; } = 1.9f;
    public float HeightMetres { get; init; } = 1.4f;

    /// <summary>The preset key of the engine, for a map or a command line to name. See EngineProfile.Presets.</summary>
    public string EngineKey { get; init; } = "";

    /// <summary>The car the engine is bolted into, as something the sound has to get out through
    /// (<see cref="VehicleBody"/>): most of what separates two cars with the same engine.</summary>
    public VehicleBody Body { get; init; } = VehicleBody.Saloon;

    /// <summary>
    /// The compressed-air system, by <see cref="AirSystemSpec"/> preset name, or null for hydraulic
    /// brakes. Service brake exhaust, spring brake dumps and dryer purges are made by the voice from the
    /// vehicle's own speed history, so nothing on the wire carries them.
    /// </summary>
    public string? AirSystem { get; init; }

    /// <summary>The siren head, by <see cref="SirenSpec"/> preset name, or null. In the voice beside the
    /// engine, so it Dopplers, occludes and reflects with the car.</summary>
    public string? Siren { get; init; }

    /// <summary>Whether this vehicle beeps while its doors are open. A bus does; a truck with the
    /// same air system does not. See DoorChimeSpec.</summary>
    public bool DoorChime { get; init; }

    /// <summary>
    /// The horn, as "electric:&lt;ElectricHornSpec&gt;" or "air:&lt;a horn in the ModelLibrary&gt;", or null for
    /// the one <see cref="HornFor"/> gives. Declared where the body does not say it: a motorcycle and a
    /// racing car share an open-wheeled body and only one has a horn button.
    /// </summary>
    public string? Horn { get; init; }

    /// <summary>
    /// The horn a vehicle carries, from what it is: anything on air brakes blows air (a tractor unit its
    /// roof pair, a bus its single trumpet), anything else has the electric disc pair behind the grille.
    /// </summary>
    public static string HornFor(VehicleProfile p)
    {
        if (p.Horn != null) return p.Horn;
        if (string.Equals(p.AirSystem, "tractor_trailer", StringComparison.OrdinalIgnoreCase)) return "air:truck_dual";
        if (p.AirSystem != null) return "air:bus_horn";
        return "electric:disc_pair";
    }

    /// <summary>
    /// How much of the engine's mechanical noise (valves, injection clatter, timing gears, the block)
    /// reaches the street, 0..1, as a pressure share. The block's only route out: the front tap must
    /// not carry it as well (a second fixed leak is most of the engine on a bus). The intake leaves the
    /// same way, its mouth under the same bonnet.
    ///
    /// From the bay (<see cref="EngineBaySpec.Leakage"/>) unless declared: 0.63 a modern car (the
    /// default; 0.15 before 2026-10-05 made every car's front silent at idle), 0.89 a classic car, 0.66 a
    /// pickup, 0.87 a step van; declared 0.80 for a truck or bus (approved by ear; the bay model would
    /// give about 0.9) and 1 for a motorcycle, which has no bay.
    /// </summary>
    public float EngineBayLeakage
    {
        get => _engineBayLeakage ?? EngineBay?.Leakage ?? 1f;
        init => _engineBayLeakage = value;
    }
    private readonly float? _engineBayLeakage;

    /// <summary>
    /// The engine bay (<see cref="EngineBaySpec"/>): how much of the engine gets out and where from, the
    /// grille ahead and the open floor underneath, heard from behind only round the body. Null leaks the
    /// declared share evenly in every direction (motorcycles, trucks, buses).
    /// </summary>
    public EngineBaySpec? EngineBay { get; init; } = EngineBaySpec.Car;

    /// <summary>
    /// The engine's cooling fan: a <see cref="BladeRowSpec"/>, as a propeller and a mower blade are. A
    /// car's is electric (<see cref="ElectricFan"/>). A truck's or bus's is belt-driven off the crank,
    /// and at full load is one of the loudest things on it: most of why a bus pulling away roars rather
    /// than clatters, and invisible on a tailpipe bench. At Mach 0.2 the blade tone is a thump under the
    /// broadband (<see cref="BladeRowSpec.SelfNoiseDb"/>).
    /// </summary>
    public BladeRowSpec? CoolingFan { get; init; }

    /// <summary>Fan speed over crank speed. A fan on the crank nose runs a little over engine speed
    /// through its pulleys; one on a jackshaft may run under.</summary>
    public float FanDriveRatio { get; init; } = 1f;

    /// <summary>
    /// The clutch between the crank and the cooling fan, or null for a fan bolted solid. Bolted solid at
    /// crank speed, a semi's fan was half its 1-4 kHz hiss at a city cruise. Engaged on coolant
    /// temperature (air-actuated on a truck, viscous on a bus); at a cruise it slips and its broadband
    /// falls about thirty decibels, and its coming in pulling away or idling hot is a recognisable roar.
    /// </summary>
    public FanClutchSpec? FanClutch { get; init; }

    /// <summary>The relay a car's electric fan runs on (<see cref="ElectricFanSpec"/>), or null for a
    /// fan on the crank. With it <see cref="CoolingFan"/> turns at its motor's speed, whatever the
    /// engine does.</summary>
    public ElectricFanSpec? ElectricFan { get; init; }

    /// <summary>
    /// A car's electric radiator fan: seven swept blades, 36 cm (14 in) across, 2,700 rpm on high, tips
    /// at 51 m/s (Mach 0.15). Levels derived, not measured: the truck fan's anchor (88 dB tone, 93 dB
    /// broadband at 93 m/s) carried down by tip speed cubed (-16 dB) and blade area (a fifth, -7 dB), so
    /// 70 dB broadband and 64 dB of tone at a metre on high; low speed is 11 dB under.
    /// </summary>
    public static BladeRowSpec CarRadiatorFan => new()
    {
        Blades = 7, DiameterMetres = 0.36f, ChordMetres = 0.06f, ThicknessRatio = 0.08f,
        RpmMax = 2700f, RpmIdle = 0f, ReferenceDb = 64f, SelfNoiseDb = 70f,
        // Car fans space their blades unevenly to spread the blade-passing tone.
        BladeScatter = 0.05f,
    };

    /// <summary>How many tyres are on the road; the rolling noise is one tyre's
    /// (<see cref="TyreProfile.ReferenceDb"/>) summed over them.</summary>
    public int TyreCount { get; init; } = 4;

    /// <summary>The running gear (<see cref="ChassisSpec"/>), or null for
    /// <see cref="ChassisSpec.Default"/>, built from the fields above.</summary>
    public ChassisSpec? Chassis { get; init; }

    /// <summary>The chassis this vehicle runs on: its own, or the one its other fields imply.</summary>
    public ChassisSpec Running => (Chassis ?? ChassisSpec.Default(this)).PlacedOn(this);

    /// <summary>
    /// What this vehicle measures at one metre at full load, dB SPL: the number the audio chain hangs
    /// off. It places the emitter in the mix (<see cref="Loudness.Place(float)"/>) and sets what one
    /// full-scale sample means in the synthesis (<c>EngineVoiceState.PascalsAtFullScale</c>). Too low,
    /// and a race engine past it goes through the voice's tanh as a square wave: the first speedway,
    /// "everything is overloaded, crackling and breaking up", clipped before any volume control.
    /// Measured by AudioLab --engine-levels; EngineSynthTests.DeclaredSourceLevelMatchesWhatThePresetMeasures
    /// fails if a preset drifts.
    /// </summary>
    public float SourceLevelDb { get; init; } = 116f;

    /// <summary>
    /// Headroom between the declared level (an RMS, the loudest second) and full scale, dB. One number
    /// for every engine, so the crest factor cannot leak into the mix as it would if each voice were
    /// normalised by its own peak. Sixteen is the largest sustained crest across the presets (99.9th
    /// percentile against the mean, 6 to 15 dB, --engine-levels); absolute peaks reach 21 dB, single
    /// backfires and pops that are better limited than clipped. A peak-normalised sample file sits at
    /// the same place, so synthesis and recordings meet Loudness.Place on the same terms.
    /// </summary>
    public const float PeakHeadroomDb = 16f;

    /// <summary>The pressure that renders at full scale inside the synthesis, pascals.</summary>
    public float PascalsAtFullScale
        => 20e-6f * MathF.Pow(10f, (SourceLevelDb + PeakHeadroomDb) / 20f);

    /// <summary>Every vehicle preset by key, so a map can say "v8_muscle" and get a whole car.</summary>
    public static IReadOnlyDictionary<string, Func<VehicleProfile>> Presets { get; } =
        new Dictionary<string, Func<VehicleProfile>>(StringComparer.OrdinalIgnoreCase)
        {
            ["v8_muscle"] = () => V8Muscle,
            ["v8_sports"] = () => V8Sports,
            ["v8_flatplane"] = () => Supercar,
            ["i4_economy"] = () => Hatchback,
            ["i4_sport"] = () => HotHatch,
            ["i4_turbo"] = () => TurboHatch,
            ["i6"] = () => Saloon6,
            ["v6"] = () => Sedan6,
            ["vtwin"] = () => Cruiser,
            ["single"] = () => DirtBike,
            ["diesel_i4"] = () => Pickup,
            ["diesel_truck"] = () => Truck,
            ["diesel_cummins"] = () => DieselPickupLoud,
            ["pickup_v8"] = () => PickupV8,
            ["pickup_v8_flowmaster"] = () => PickupV8Flowmaster,
            ["powerstroke73"] = () => PowerStrokePickup,
            ["duramax_compound"] = () => DuramaxCompoundPickup,
            ["cummins_compound"] = () => CumminsCompoundPickup,
            ["step_van"] = () => StepVan,
            ["mail_truck"] = () => MailTruck,
            ["school_bus"] = () => SchoolBus,
            ["diesel_cummins_na"] = () => DieselPickupNa,
            ["school_bus_na"] = () => SchoolBusNa,
            ["boxer4"] = () => Wagon,
            ["v10"] = () => V10Coupe,
            ["v12"] = () => GrandTourer,
            ["nascar_v8"] = () => StockCar,
            ["f1_v10"] = () => FormulaCar,
            ["police_v8"] = () => PoliceCar,
            ["police_interceptor"] = () => PoliceInterceptor,
            ["charger440"] = () => Charger440,
            ["vtwin_stock"] = () => CruiserStock,
            ["vtwin_slipon"] = () => CruiserSlipOn,
            ["v8_open_headers"] = () => OpenHeaderMuscle,
            ["v8_glasspack"] = () => GlasspackMuscle,
            ["v8_mild"] = () => MildMuscle,
            ["v8_bigcam"] = () => BigCamMuscle,
            ["v8_blown"] = () => BlownMuscle,
            ["sportbike"] = () => SportBike,
            ["i4_compact"] = () => Compact18,
            ["i4_midsize"] = () => Midsize25,
            ["i4_sport_street"] = () => SportCompact,
            ["boxer4_street"] = () => FlatFourSedan,
            ["i6_street"] = () => SportSaloon6,
            ["transit_bus"] = () => TransitBus,
        };

    /// <summary>
    /// A preset by name, built once and shared: asked several times per car per frame on the audio
    /// thread, and building one is a whole engine with its networks. Safe to share because every member
    /// is init-only; a variation uses `with`.
    /// </summary>
    public static VehicleProfile ByName(string key)
        => _cache.GetOrAdd(key, static k => Presets.TryGetValue(k, out var make) ? make()
             : throw new ArgumentException($"No vehicle preset '{k}'. Known: {string.Join(", ", Presets.Keys)}"));

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, VehicleProfile> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    // ── Four muscle cars, one V8, four exhausts ─────────────────────────────────────────────────
    //
    // The same car underneath (mass, gearing, tyres), so any difference heard is the hardware.

    /// <summary>Open headers: the only one with no can at all.</summary>
    public static VehicleProfile OpenHeaderMuscle => V8Muscle with
    {
        Name = "Muscle car, open headers",
        EngineKey = "v8_open_headers",
        Engine = EngineProfile.V8OpenHeaders,
        SourceLevelDb = 128f,
    };

    /// <summary>The same big block through glasspacks: mellower, and no metallic ring, because the
    /// packing damps the case too.</summary>
    public static VehicleProfile GlasspackMuscle => V8Muscle with
    {
        Name = "Muscle car, glasspacks",
        EngineKey = "v8_glasspack",
        Engine = EngineProfile.V8BigBlockGlasspack,
        SourceLevelDb = 123f,
    };

    /// <summary>A mild small block on stock manifolds: the plain one the others are heard against.</summary>
    public static VehicleProfile MildMuscle => V8Muscle with
    {
        Name = "Muscle car, mild small block",
        EngineKey = "v8_mild",
        Engine = EngineProfile.V8MildSmallBlock,
        MassKg = 1520f,
        SourceLevelDb = 112f,
    };

    /// <summary>7.4 litres on a 330-degree cam through 40-series cans: it lopes at idle because the
    /// overlap makes the burn ragged.</summary>
    public static VehicleProfile BigCamMuscle => V8Muscle with
    {
        Name = "Muscle car, big cam",
        EngineKey = "v8_bigcam",
        Engine = EngineProfile.V8BigCam,
        MassKg = 1720f,
        SourceLevelDb = 119f,
    };

    /// <summary>
    /// A litre sports bike: no body, no cabin, a tenth-of-a-second shift. At 14,500 rpm a four fires 483
    /// times a second, so its fundamental is a musical pitch where a car's is a beat you could count.
    /// </summary>
    public static VehicleProfile SportBike => new()
    {
        Chassis = RunningGear.YamahaR1,
        Horn = "electric:moto_disc",
        LengthMetres = 2.1f, WidthMetres = 0.8f, HeightMetres = 1.15f,
        Name = "Litre sports bike",
        EngineKey = "sportbike",
        Engine = EngineProfile.SportBike,
        Gearbox = Gearbox.SixSpeedSports with
        {
            Ratios = new[] { 2.57f, 1.94f, 1.61f, 1.41f, 1.29f, 1.19f },
            // The chain's 3.0 times the primary reduction (1.63 on a litre four). Without it first gear
            // overall was 7.7:1 against a real 12, and the bike lugged at 1,500-3,300 rpm at 50 km/h.
            FinalDrive = 3.0f * 1.63f, WheelRadiusMetres = 0.31f,
            // A quickshifter: the ignition is cut while the dog rings move, a cut rather than a lift.
            ShiftSeconds = 0.09f, UpshiftRpm = 13800f, DownshiftRpm = 3000f,
            CruiseUpshiftRpm = 5000f, LaunchRpm = 2800f,
        },
        Tyres = TyreProfile.SportBikeRoad,
        MassKg = 200f,
        DragArea = 0.42f,
        RollingResistance = 0.015f,
        Body = VehicleBody.OpenWheeler,
        TyreCount = 2,
        ExhaustOffsetZ = -0.75f, IntakeOffsetZ = 0.25f, ExhaustHeight = 0.55f,
        EngineBayLeakage = 1f,     // no bay: the engine hangs in the frame and the airbox is under the tank
        EngineBay = null,
        FrontAxleZ = 0.70f, RearAxleZ = -0.70f,
        // 106.8 on the live voice with the stock system: the pipe 100.6, the engine itself 103.0.
        SourceLevelDb = 107f,
    };


    /// <summary>A blown big block: rotor whine over the cam's lope, and no lag, because a supercharger
    /// is geared to the crank.</summary>
    public static VehicleProfile BlownMuscle => V8Muscle with
    {
        Name = "Muscle car, blown big block",
        EngineKey = "v8_blown",
        Engine = EngineProfile.V8Blown,
        MassKg = 1780f,
        Body = VehicleBody.RaceSaloon,
        SourceLevelDb = 119f,
    };

    /// <summary>
    /// A 1969 Charger with a 440 and Flowmasters: the muscle car on a street. <see cref="V8Muscle"/>
    /// (long tubes, a 320-degree cam, open duals, 122 dB) is a race saloon; this has cast manifolds, a
    /// street cam, an H-pipe and chambered cans, and measures like a loud road car.
    /// </summary>
    public static VehicleProfile Charger440 => new()
    {
        Chassis = RunningGear.Charger69,
        Horn = "electric:trumpet_pair",
        LengthMetres = 5.3f, WidthMetres = 1.95f, HeightMetres = 1.35f,
        // Two tons of steel with a full interior: a well-damped body, not a race shell.
        Body = VehicleBody.Saloon,
        Name = "1969 big-block Charger, Flowmasters",
        EngineBay = EngineBaySpec.ClassicCar,
        EngineKey = "v8_charger440",
        SourceLevelDb = 113f,          // measured on the live voice
        Engine = EngineProfile.V8Charger440,
        // A four-speed with a 3.23 rear.
        Gearbox = Gearbox.SixSpeedSports with
        {
            Ratios = new[] { 2.66f, 1.91f, 1.39f, 1.00f },
            FinalDrive = 3.23f, ShiftSeconds = 0.40f,
            UpshiftRpm = 5000f, DownshiftRpm = 1300f, WheelRadiusMetres = 0.36f,
        },
        Tyres = TyreProfile.SportsOnAsphalt with { TreadBlocks = 44, SurfaceRoughness = 0.55f, PeakGripG = 0.85f },
        MassKg = 1790f, DragArea = 0.95f, RollingResistance = 0.013f,
        ExhaustOffsetZ = -2.6f, IntakeOffsetZ = 1.5f, FrontAxleZ = 1.6f, RearAxleZ = -1.6f,
    };

    /// <summary>A stock big twin: the same 45-degree beat through baffled cans, about twenty decibels
    /// under straight pipe.</summary>
    public static VehicleProfile CruiserStock => Cruiser with
    {
        Name = "V-twin cruiser, stock mufflers",
        EngineKey = "vtwin_stock",
        Engine = EngineProfile.VTwin45Stock,
        SourceLevelDb = 101f,   // 101.1 measured on the live voice
    };

    /// <summary>The same bike with slip-on cans, which is what most of them are wearing.</summary>
    public static VehicleProfile CruiserSlipOn => Cruiser with
    {
        Name = "V-twin cruiser, slip-on cans",
        EngineKey = "vtwin_slipon",
        Engine = EngineProfile.VTwin45SlipOn,
        // 116 measured, only four decibels under open pipe: that is a straight-through packed can.
        // The big step on a V-twin is to a baffled muffler (98 dB, stock).
        SourceLevelDb = 116f,
    };

    /// <summary>A big-block muscle car: long cam, true duals, four-speed.</summary>
    public static VehicleProfile V8Muscle => new()
    {
        Chassis = RunningGear.Chevelle70,
        LengthMetres = 4.9f, WidthMetres = 1.9f, HeightMetres = 1.35f,
        Name = "Big-block muscle car, true duals",
        EngineBay = EngineBaySpec.ClassicCar,
        EngineKey = "v8_muscle",
        SourceLevelDb = 122f,
        Engine = EngineProfile.V8MuscleBigBlock,
        Gearbox = Gearbox.SixSpeedSports with
        {
            Ratios = new[] { 2.52f, 1.88f, 1.46f, 1.00f },
            FinalDrive = 3.73f,
            ShiftSeconds = 0.38f,
            UpshiftRpm = 5300f,
            DownshiftRpm = 1300f,
        },
        Tyres = TyreProfile.SportsOnAsphalt,
        MassKg = 1720f,
        DragArea = 0.78f,
    };

    public static VehicleProfile V8Sports => new()
    {
        Chassis = RunningGear.MustangGt11,
        Name = "V8 sports car, Flowmaster 40s",
        CoolingFan = CarRadiatorFan, ElectricFan = ElectricFanSpec.TwoSpeed,
        EngineKey = "v8_sports",
        SourceLevelDb = 116f,
        Engine = EngineProfile.V8SportsFlowmaster40,
        Gearbox = Gearbox.SixSpeedSports,
        Tyres = TyreProfile.SportsOnAsphalt,
    };

    public static VehicleProfile Supercar => new()
    {
        Chassis = RunningGear.Ferrari458,
        LengthMetres = 4.6f, WidthMetres = 2.0f, HeightMetres = 1.2f,
        // Small aluminium panels, a tiny cabin, and an exhaust that barely touches the shell.
        Body = VehicleBody.Supercar,
        Name = "Flat-plane V8 supercar",
        CoolingFan = CarRadiatorFan, ElectricFan = ElectricFanSpec.TwoSpeed,
        EngineKey = "v8_flatplane",
        SourceLevelDb = 125f,
        Engine = EngineProfile.V8FlatPlane,
        Gearbox = Gearbox.SixSpeedSports with { Ratios = new[] { 3.08f, 2.19f, 1.63f, 1.29f, 1.03f, 0.84f, 0.69f }, FinalDrive = 4.1f, ShiftSeconds = 0.12f, UpshiftRpm = 8600f, DownshiftRpm = 2500f },
        Tyres = TyreProfile.SportsOnAsphalt,
        MassKg = 1420f, DragArea = 0.68f,
        ExhaustOffsetZ = -1.9f, IntakeOffsetZ = -0.4f,
    };

    public static VehicleProfile Hatchback => new()
    {
        Chassis = RunningGear.Golf4,
        Horn = "electric:disc_single",
        LengthMetres = 4.1f, WidthMetres = 1.75f, HeightMetres = 1.45f,
        Name = "1.6 hatchback",
        CoolingFan = CarRadiatorFan, ElectricFan = ElectricFanSpec.TwoSpeed,
        EngineKey = "i4_economy",
        // 99.7 on the live voice: flat out near the redline it is at motorway speed, mostly tyres.
        SourceLevelDb = 99.5f,
        Engine = EngineProfile.Inline4Economy,
        Gearbox = Gearbox.SixSpeedSports with { Ratios = new[] { 3.6f, 2.0f, 1.36f, 1.03f, 0.82f }, FinalDrive = 4.2f, ShiftSeconds = 0.4f, UpshiftRpm = 5200f, DownshiftRpm = 1400f, WheelRadiusMetres = 0.30f },
        Tyres = TyreProfile.SportsOnAsphalt with { TreadBlocks = 60 },
        MassKg = 1150f, DragArea = 0.66f,
        ExhaustOffsetZ = -1.8f, IntakeOffsetZ = 1.4f, FrontAxleZ = 1.2f, RearAxleZ = -1.3f,
    };

    // ── Street cars ───────────────────────────────────────────────────────────────────────────
    //
    // The city's field: the speedway's engines (the hot hatch is 119 dB, the flat four 118) with the
    // exhausts a road car leaves the dealer with, or a cat-back. Every level measured on the live voice.

    /// <summary>A 1.8 four in a compact saloon: the economy engine, stroked. Stock can.</summary>
    public static VehicleProfile Compact18 => Hatchback with
    {
        Chassis = RunningGear.CorollaE140,
        Name = "1.8 compact saloon",
        EngineKey = "i4_compact",
        LengthMetres = 4.6f,
        Engine = EngineProfile.Inline4Compact18,
        SourceLevelDb = 99.5f,   // 99.7 measured (--voice-levels)
    };

    /// <summary>A 2.5 four in a mid-size saloon: long stroke, a lazy torque curve, a big quiet can.</summary>
    public static VehicleProfile Midsize25 => Hatchback with
    {
        Chassis = RunningGear.CamryXV40,
        Name = "2.5 mid-size saloon",
        EngineKey = "i4_midsize",
        LengthMetres = 4.85f, WidthMetres = 1.84f, MassKg = 1500f,
        Engine = EngineProfile.Inline4Midsize25,
        Gearbox = Gearbox.SixSpeedSports with { Ratios = new[] { 4.1f, 2.4f, 1.55f, 1.15f, 0.86f, 0.67f }, FinalDrive = 3.3f, ShiftSeconds = 0.4f, UpshiftRpm = 5800f, DownshiftRpm = 1300f, WheelRadiusMetres = 0.33f },
        SourceLevelDb = 98.5f,   // 98.4 measured
    };

    /// <summary>The hot hatch's engine behind a sport cat-back rather than a straight-through pack:
    /// a baffled can with lighter packing and a small resonator. Raspy, not a race car.</summary>
    public static VehicleProfile SportCompact => HotHatch with
    {
        Name = "2.0 sport compact, cat-back",
        EngineKey = "i4_sport_street",
        Engine = EngineProfile.Inline4SportStreet,
        SourceLevelDb = 104f,   // 103.9 measured
    };

    /// <summary>The flat four with its unequal headers (the burble is the headers, not the can) behind
    /// a chambered cat-back.</summary>
    public static VehicleProfile FlatFourSedan => Wagon with
    {
        Chassis = RunningGear.Legacy,
        Name = "2.5 flat-four sedan, cat-back",
        EngineKey = "boxer4_street",
        Body = VehicleBody.Saloon,
        LengthMetres = 4.6f,
        Engine = EngineProfile.Boxer4Street,
        SourceLevelDb = 103f,   // 103.3 measured
    };

    /// <summary>The straight six with the factory's can: smooth, and a little hard at the top.</summary>
    public static VehicleProfile SportSaloon6 => Saloon6 with
    {
        Name = "3.0 straight-six sport saloon",
        EngineKey = "i6_street",
        Engine = EngineProfile.Inline6Street,
        SourceLevelDb = 102f,   // 101.9 measured; 99.0 before the bay was opened up (2026-10-05)
    };

    public static VehicleProfile HotHatch => new()
    {
        Chassis = RunningGear.CivicTypeR,
        LengthMetres = 4.25f, WidthMetres = 1.8f, HeightMetres = 1.45f,
        Name = "2.0 hot hatch",
        CoolingFan = CarRadiatorFan, ElectricFan = ElectricFanSpec.TwoSpeed,
        EngineKey = "i4_sport",
        SourceLevelDb = 119f,
        Engine = EngineProfile.Inline4Sport,
        Gearbox = Gearbox.SixSpeedSports with { Ratios = new[] { 3.27f, 2.13f, 1.52f, 1.15f, 0.92f, 0.76f }, FinalDrive = 4.3f, ShiftSeconds = 0.3f, UpshiftRpm = 7800f, DownshiftRpm = 1800f, WheelRadiusMetres = 0.31f },
        Tyres = TyreProfile.SportsOnAsphalt,
        MassKg = 1280f, DragArea = 0.68f,
        ExhaustOffsetZ = -1.9f, IntakeOffsetZ = 1.4f,
    };

    /// <summary>The turbo version of the hot hatch: with torque from two thousand it is geared longer
    /// and sounds lazy next to the atmospheric car doing the same lap time.</summary>
    public static VehicleProfile TurboHatch => new()
    {
        Chassis = RunningGear.GolfGti7,
        LengthMetres = 4.3f, WidthMetres = 1.8f, HeightMetres = 1.45f,
        Name = "2.0 turbo hatch",
        CoolingFan = CarRadiatorFan, ElectricFan = ElectricFanSpec.TwoSpeed,
        EngineKey = "i4_turbo",
        SourceLevelDb = 111f,     // 111.1 measured, with the turbine as a flat 6 dB loss
        Engine = EngineProfile.I4Turbo,
        Gearbox = Gearbox.SixSpeedSports with { Ratios = new[] { 3.4f, 2.05f, 1.42f, 1.06f, 0.84f, 0.68f }, FinalDrive = 3.7f, ShiftSeconds = 0.18f, UpshiftRpm = 6300f, DownshiftRpm = 2000f },
        Tyres = TyreProfile.SportsOnAsphalt with { TreadBlocks = 58, PeakGripG = 1.1f, SquealHz = 880f },
        MassKg = 1380f, DragArea = 0.68f,
        ExhaustOffsetZ = -1.85f, IntakeOffsetZ = 1.3f,
    };

    public static VehicleProfile Saloon6 => new()
    {
        Chassis = RunningGear.Bmw530iE60,
        LengthMetres = 4.9f, WidthMetres = 1.85f, HeightMetres = 1.45f,
        Name = "3.0 straight-six saloon",
        CoolingFan = CarRadiatorFan, ElectricFan = ElectricFanSpec.TwoSpeed,
        EngineKey = "i6",
        SourceLevelDb = 117f,
        Engine = EngineProfile.Inline6,
        Gearbox = Gearbox.SixSpeedSports with { Ratios = new[] { 4.06f, 2.37f, 1.56f, 1.16f, 0.85f, 0.67f }, FinalDrive = 3.15f, ShiftSeconds = 0.3f, UpshiftRpm = 6600f, DownshiftRpm = 1500f },
        Tyres = TyreProfile.SportsOnAsphalt,
        MassKg = 1600f, DragArea = 0.64f,
    };

    public static VehicleProfile Sedan6 => new()
    {
        Chassis = RunningGear.AccordV6,
        LengthMetres = 4.9f, WidthMetres = 1.85f, HeightMetres = 1.45f,
        Name = "3.5 V6 sedan",
        CoolingFan = CarRadiatorFan, ElectricFan = ElectricFanSpec.TwoSpeed,
        EngineKey = "v6",
        // Measured on the live voice (--voice-levels), not the tailpipe bench: a silenced car's body
        // and tyres carry several decibels (DeclaredSourceLevelMatchesTheLiveVoice). The car most
        // sensitive to the airbox loss: without it its intake measures 11 dB above its tailpipe.
        SourceLevelDb = 104f,
        Engine = EngineProfile.V6Sedan,
        Gearbox = Gearbox.SixSpeedSports with { Ratios = new[] { 4.58f, 2.96f, 1.91f, 1.45f, 1.0f, 0.75f }, FinalDrive = 3.3f, ShiftSeconds = 0.35f, UpshiftRpm = 6000f, DownshiftRpm = 1400f, WheelRadiusMetres = 0.33f },
        Tyres = TyreProfile.SportsOnAsphalt with { TreadBlocks = 62 },
        MassKg = 1650f, DragArea = 0.66f,
    };

    public static VehicleProfile Cruiser => new()
    {
        Chassis = RunningGear.RoadKing,
        Horn = "electric:moto_disc",
        LengthMetres = 2.45f, WidthMetres = 0.95f, HeightMetres = 1.15f,
        Body = VehicleBody.OpenWheeler,
        TyreCount = 2,
        Name = "V-twin cruiser motorcycle",
        EngineKey = "vtwin",
        SourceLevelDb = 121f,
        Engine = EngineProfile.VTwin45,
        Gearbox = Gearbox.SixSpeedSports with { Ratios = new[] { 3.34f, 2.3f, 1.71f, 1.41f, 1.18f, 1.0f }, FinalDrive = 2.87f, ShiftSeconds = 0.25f, UpshiftRpm = 5000f, DownshiftRpm = 1800f, WheelRadiusMetres = 0.33f },
        Tyres = TyreProfile.SportsOnAsphalt with { TreadBlocks = 40, SurfaceRoughness = 0.4f },
        MassKg = 380f, DragArea = 0.55f,
        ExhaustOffsetZ = -0.5f, IntakeOffsetZ = 0.1f, FrontAxleZ = 0.8f, RearAxleZ = -0.8f,
        EngineBayLeakage = 1f,     // no bay: the engine hangs in the frame and the airbox is under the tank
        EngineBay = null,
    };

    public static VehicleProfile DirtBike => new()
    {
        Chassis = RunningGear.Crf450,
        Horn = "electric:moto_disc",
        LengthMetres = 2.2f, WidthMetres = 0.85f, HeightMetres = 1.25f,
        Body = VehicleBody.OpenWheeler,
        TyreCount = 2,
        Name = "450 dirt bike",
        EngineKey = "single",
        SourceLevelDb = 122f,   // 122.2 measured on the live voice
        Engine = EngineProfile.Single450,
        // With the primary reduction (about 2.9 on a 450 single): without it first gear overall was 9:1
        // against a real 20, and the bike lugged at 3-4,000 rpm.
        Gearbox = Gearbox.SixSpeedSports with { Ratios = new[] { 2.4f, 1.8f, 1.4f, 1.15f, 0.96f }, FinalDrive = 3.8f * 2.9f, ShiftSeconds = 0.18f, UpshiftRpm = 10000f, DownshiftRpm = 3000f, WheelRadiusMetres = 0.33f },
        Tyres = TyreProfile.SportsOnAsphalt with { TreadBlocks = 28, SurfaceRoughness = 0.7f },
        MassKg = 190f, DragArea = 0.5f,
        ExhaustOffsetZ = -0.4f, IntakeOffsetZ = 0.1f, FrontAxleZ = 0.75f, RearAxleZ = -0.75f,
        EngineBayLeakage = 1f,     // no bay: the engine hangs in the frame and the airbox is under the tank
        EngineBay = null,
    };

    /// <summary>A full-size gas pickup with the 5.3 V8, as it left the factory.</summary>
    public static VehicleProfile PickupV8 => Pickup with
    {
        Chassis = RunningGear.Silverado1500,
        LengthMetres = 5.8f, WidthMetres = 2.0f, HeightMetres = 1.9f,
        Name = "5.3 V8 pickup, stock",
        EngineKey = "pickup_v8",
        SourceLevelDb = 100f,
        Engine = EngineProfile.PickupV8Stock,
        Gearbox = Gearbox.SixSpeedSports with { Ratios = new[] { 4.03f, 2.36f, 1.53f, 1.15f, 0.85f, 0.67f }, FinalDrive = 3.42f, ShiftSeconds = 0.35f, UpshiftRpm = 4800f, DownshiftRpm = 1400f, WheelRadiusMetres = 0.39f },
        MassKg = 2400f, DragArea = 1.3f,
        ExhaustOffsetZ = -2.9f, IntakeOffsetZ = 2.0f, FrontAxleZ = 1.8f, RearAxleZ = -1.8f,
    };

    /// <summary>The same pickup with a 5.0 V8 on Flowmaster 40s: the burble people fit to be heard.</summary>
    public static VehicleProfile PickupV8Flowmaster => PickupV8 with
    {
        Name = "5.0 V8 pickup, Flowmaster 40s",
        EngineKey = "pickup_v8_flowmaster",
        SourceLevelDb = 108f,
        Engine = EngineProfile.V8SportsFlowmaster40,
    };

    /// <summary>A late-1990s Ford Super Duty with the 7.3 Power Stroke and the four-speed automatic.</summary>
    public static VehicleProfile PowerStrokePickup => Pickup with
    {
        Chassis = RunningGear.F250HD,
        LengthMetres = 6.0f, WidthMetres = 2.0f, HeightMetres = 2.0f,
        Name = "1998 Ford F-250, 7.3 Power Stroke",
        EngineKey = "powerstroke73",
        SourceLevelDb = 104f,   // 104.1 on the live voice; 101.2 before the bay was opened up (2026-10-05)
        Engine = EngineProfile.PowerStroke73,
        Gearbox = Gearbox.SixSpeedSports with { Ratios = new[] { 2.71f, 1.54f, 1.00f, 0.71f }, FinalDrive = 3.73f, ShiftSeconds = 0.5f, UpshiftRpm = 2800f, DownshiftRpm = 1200f, WheelRadiusMetres = 0.40f },
        MassKg = 3200f, DragArea = 1.6f,
        ExhaustOffsetZ = -3.0f, IntakeOffsetZ = 2.1f, FrontAxleZ = 1.9f, RearAxleZ = -2.0f,
    };

    /// <summary>
    /// A Duramax pickup with compound turbos and a straight pipe: the turbos whistle at idle and
    /// scream on the throttle.
    /// </summary>
    public static VehicleProfile DuramaxCompoundPickup => PowerStrokePickup with
    {
        Chassis = RunningGear.Silverado2500HD,
        Name = "6.6 Duramax pickup, compound turbos, straight pipe",
        EngineKey = "duramax_compound",
        SourceLevelDb = 118f,     // 118.3 on the live voice: five-inch pipe, turbine a flat 6 dB
        Engine = EngineProfile.DuramaxCompound,
        Gearbox = Gearbox.SixSpeedSports with { Ratios = new[] { 3.10f, 1.81f, 1.41f, 1.00f, 0.71f }, FinalDrive = 3.73f, ShiftSeconds = 0.45f, UpshiftRpm = 3100f, DownshiftRpm = 1300f, WheelRadiusMetres = 0.40f },
        // A side exit ahead of the rear wheel, kerb side, as a straight-piped diesel is usually run;
        // out of the bumper the six-metre body takes the mids and top off the pass-by.
        ExhaustOffsetZ = -1.3f, ExhaustAxis = new Vector3(1f, 0f, 0f),
    };

    /// <summary>The same idea on a 5.9 Cummins: compound turbos and a five-inch straight pipe.</summary>
    public static VehicleProfile CumminsCompoundPickup => DieselPickupLoud with
    {
        Name = "5.9 Cummins pickup, compound turbos, straight pipe",
        EngineKey = "cummins_compound",
        SourceLevelDb = 120f,     // 119.8 on the live voice: six-inch stack, turbine a flat 6 dB
        Engine = EngineProfile.CumminsCompound,
        Gearbox = DieselPickupLoud.Gearbox with { UpshiftRpm = 3000f },
        // Side exit ahead of the rear wheel, kerb side, as on the Duramax.
        ExhaustOffsetZ = -1.3f, ExhaustAxis = new Vector3(1f, 0f, 0f),
    };

    /// <summary>A parcel step van: aluminium box body, the ISB six, an automatic, duals at the back.</summary>
    public static VehicleProfile StepVan => Pickup with
    {
        Chassis = RunningGear.Mt45,
        LengthMetres = 7.3f, WidthMetres = 2.4f, HeightMetres = 3.1f,
        EngineBay = EngineBaySpec.Van,
        Body = VehicleBody.Van,
        Name = "parcel step van",
        EngineKey = "step_van",
        SourceLevelDb = 101f,     // 100.9 on the live voice; 97.6 before the bay was opened up (2026-10-05)
        Engine = EngineProfile.CumminsIsbStepVan,
        Gearbox = Gearbox.SixSpeedSports with { Ratios = new[] { 3.10f, 1.81f, 1.41f, 1.00f, 0.71f }, FinalDrive = 4.88f, ShiftSeconds = 0.5f, UpshiftRpm = 2400f, DownshiftRpm = 1100f, WheelRadiusMetres = 0.42f },
        Tyres = TyreProfile.TruckOnAsphalt,
        TyreCount = 6,
        MassKg = 7500f, DragArea = 4.5f, RollingResistance = 0.010f,
        ExhaustOffsetZ = -3.5f, IntakeOffsetZ = 2.8f, FrontAxleZ = 2.6f, RearAxleZ = -1.9f,
    };

    /// <summary>The Grumman LLV mail truck: the Iron Duke four and a three-speed automatic.</summary>
    public static VehicleProfile MailTruck => Pickup with
    {
        Chassis = RunningGear.Llv,
        LengthMetres = 4.4f, WidthMetres = 2.0f, HeightMetres = 2.2f,
        Name = "mail truck (LLV)",
        EngineKey = "mail_truck",
        SourceLevelDb = 96f,
        Engine = EngineProfile.IronDuke25,
        Gearbox = Gearbox.SixSpeedSports with { Ratios = new[] { 2.84f, 1.60f, 1.00f }, FinalDrive = 3.73f, ShiftSeconds = 0.4f, UpshiftRpm = 3800f, DownshiftRpm = 1200f, WheelRadiusMetres = 0.34f },
        Tyres = TyreProfile.SportsOnAsphalt,
        MassKg = 1350f, DragArea = 1.5f,
        ExhaustOffsetZ = -2.1f, IntakeOffsetZ = 1.4f, FrontAxleZ = 1.3f, RearAxleZ = -1.4f,
    };

    public static VehicleProfile Pickup => new()
    {
        Chassis = RunningGear.Hilux,
        LengthMetres = 5.3f, WidthMetres = 1.9f, HeightMetres = 1.8f,
        // A cab and an empty steel bed.
        EngineBay = EngineBaySpec.Pickup,
        Body = VehicleBody.Van,
        Name = "2.8 turbo-diesel pickup",
        EngineKey = "diesel_i4",
        // 102.5 on the live voice against 88 on the tailpipe bench: at motorway speed the tyres and the
        // bay's clatter are both louder than the pipe (99.4 with the old 0.30 bay, before 2026-10-05).
        SourceLevelDb = 103f,
        Engine = EngineProfile.DieselPickupI4,
        Gearbox = Gearbox.SixSpeedSports with { Ratios = new[] { 4.31f, 2.33f, 1.52f, 1.13f, 0.86f, 0.68f }, FinalDrive = 3.73f, ShiftSeconds = 0.45f, UpshiftRpm = 3600f, DownshiftRpm = 1300f, WheelRadiusMetres = 0.38f },
        Tyres = TyreProfile.SportsOnAsphalt with { TreadBlocks = 48, SurfaceRoughness = 0.6f },
        MassKg = 2200f, DragArea = 1.1f,
        ExhaustOffsetZ = -2.4f, IntakeOffsetZ = 1.8f, FrontAxleZ = 1.6f, RearAxleZ = -1.6f,
    };

    public static VehicleProfile Truck => new()
    {
        Chassis = RunningGear.Cascadia6x4,
        LengthMetres = 6.4f, WidthMetres = 2.5f, HeightMetres = 3.9f,
        // Big flat undeadened panels over a big box.
        Body = VehicleBody.Van,
        Name = "13 litre semi truck",
        EngineKey = "diesel_truck",
        // Measured on the live voice (--voice-levels), the whole vehicle, not the tailpipe alone
        // (--engine-levels): the bay is worth 3.6 dB of it.
        SourceLevelDb = 104f,   // includes the exhaust valves' flow noise and the block's clatter
        AirSystem = "tractor_trailer",
        EngineBayLeakage = 0.8f,   // engine in the open under a cab, behind an open grille
        EngineBay = null,
        // Nine paddles of 0.81 m on the crank nose. At the governed 1,800 rpm the tips do 72 m/s (Mach
        // 0.21), all rush: twelve times the mower's blade area at four fifths of its tip speed, six
        // decibels over it on the mower's own anchor.
        CoolingFan = new BladeRowSpec
        {
            Blades = 9, DiameterMetres = 0.81f, ChordMetres = 0.10f, ThicknessRatio = 0.11f,
            RpmMax = 2100f, RpmIdle = 0f, ReferenceDb = 88f, SelfNoiseDb = 93f, BladeScatter = 0.02f,
        },
        FanDriveRatio = 1.05f,
        FanClutch = FanClutchSpec.OnOff,
        TyreCount = 18,
        Engine = EngineProfile.DieselTruckI6,
        Gearbox = Gearbox.SixSpeedSports with { Ratios = new[] { 11.7f, 7.6f, 5.0f, 3.3f, 2.2f, 1.45f, 1.0f, 0.78f }, FinalDrive = 3.55f, ShiftSeconds = 0.8f, UpshiftRpm = 1800f, DownshiftRpm = 1100f, WheelRadiusMetres = 0.51f },
        Tyres = TyreProfile.TruckOnAsphalt,
        MassKg = 14000f, DragArea = 5.5f, RollingResistance = 0.008f,
        ExhaustOffsetZ = 1.0f, IntakeOffsetZ = 2.5f, FrontAxleZ = 3.5f, RearAxleZ = -3.0f,
        // A stack behind the cab, pointing at the sky, not a car's tailpipe at the rear bumper.
        ExhaustHeight = 4.0f, ExhaustAxis = new Vector3(0f, 1f, 0f),
    };

    /// <summary>A Dodge Ram with the 5.9 Cummins and five inches of straight pipe out the back, behind
    /// the rear axle: loudest going away, the bed over most of the pipe's run.</summary>
    public static VehicleProfile DieselPickupLoud => new()
    {
        Chassis = RunningGear.Ram2500,
        LengthMetres = 5.9f, WidthMetres = 2.0f, HeightMetres = 1.95f,
        EngineBay = EngineBaySpec.Pickup,
        Body = VehicleBody.Van,
        Name = "5.9 Cummins pickup, straight pipe",
        // --engine-levels: eleven decibels above a silenced pickup diesel.
        EngineKey = "diesel_cummins",
        SourceLevelDb = 113f,     // 113.3 measured, with the turbine as a flat 6 dB loss
        Engine = EngineProfile.DieselCumminsI6,
        // Governed at 2,900, top gear runs out around 160 km/h.
        Gearbox = Gearbox.SixSpeedSports with
        {
            Ratios = new[] { 5.61f, 3.04f, 1.67f, 1.00f, 0.75f },
            FinalDrive = 3.55f, ShiftSeconds = 0.55f,
            UpshiftRpm = 2750f, DownshiftRpm = 1250f, WheelRadiusMetres = 0.40f,
        },
        Tyres = TyreProfile.SportsOnAsphalt with { TreadBlocks = 40, SurfaceRoughness = 0.9f },
        MassKg = 3200f, DragArea = 1.9f, RollingResistance = 0.012f,
        ExhaustOffsetZ = -2.7f, IntakeOffsetZ = 1.9f, FrontAxleZ = 1.8f, RearAxleZ = -1.8f,
    };

    /// <summary>A school bus: a DT466 under a long steel box, silenced. The clatter comes from the nose
    /// and the exhaust from the tail eleven metres away, heard as two places.</summary>
    public static VehicleProfile SchoolBus => new()
    {
        Chassis = RunningGear.BlueBirdVision,
        LengthMetres = 10.9f, WidthMetres = 2.4f, HeightMetres = 3.2f,
        Body = VehicleBody.SchoolBus,
        Name = "school bus",
        EngineKey = "diesel_bus",
        // Measured on the live voice: the turbine eats the tailpipe (83 dB), and the block is 97.7 dB
        // of it. The tailpipe figure would leave the bus inaudible.
        SourceLevelDb = 100f,   // 100.4 measured, including the exhaust valves' flow noise and the clatter
        AirSystem = "transit_bus",
        DoorChime = true,
        EngineBayLeakage = 0.8f,   // a doghouse ventilated by grilles, inside the cabin
        EngineBay = null,
        // Seven blades of 0.66 m off the crank: the truck's tip speed as it is worked, a third of the
        // blade area.
        CoolingFan = new BladeRowSpec
        {
            Blades = 7, DiameterMetres = 0.66f, ChordMetres = 0.085f, ThicknessRatio = 0.11f,
            RpmMax = 2600f, RpmIdle = 0f, ReferenceDb = 86f, SelfNoiseDb = 91f, BladeScatter = 0.02f,
        },
        FanDriveRatio = 1f,
        FanClutch = FanClutchSpec.Viscous,
        TyreCount = 6,
        Engine = EngineProfile.DieselBusI6,
        Gearbox = Gearbox.SixSpeedSports with
        {
            Ratios = new[] { 7.05f, 4.14f, 2.52f, 1.56f, 1.00f, 0.74f },
            FinalDrive = 4.78f, ShiftSeconds = 0.9f,
            UpshiftRpm = 2350f, DownshiftRpm = 1150f, WheelRadiusMetres = 0.50f,
        },
        Tyres = TyreProfile.TruckOnAsphalt,
        MassKg = 11000f, DragArea = 5.8f, RollingResistance = 0.009f,
        ExhaustOffsetZ = -5.2f, IntakeOffsetZ = 4.6f, FrontAxleZ = 3.4f, RearAxleZ = -3.2f,
    };

    /// <summary>
    /// A city transit bus: the school bus's engine and body with the machinery across the rear, fan on
    /// the side, exhaust out the back. Its front is the doors, the tyres and the air.
    /// </summary>
    public static VehicleProfile TransitBus => SchoolBusNa with
    {
        Chassis = RunningGear.XcelsiorXD40,
        Name = "city bus",
        EngineKey = "diesel_bus_na",
        LengthMetres = 12.2f, WidthMetres = 2.6f, HeightMetres = 3.2f,
        EngineAtRear = true,
        SourceLevelDb = 97f,   // 96.8 measured
        IntakeOffsetZ = -4.9f, ExhaustOffsetZ = -6.0f, ExhaustHeight = 0.6f,
        FrontAxleZ = 3.9f, RearAxleZ = -2.3f,
    };

    /// <summary>The same pickup with the turbo taken off, to hear the turbo as a mechanism.</summary>
    public static VehicleProfile DieselPickupNa => DieselPickupLoud with
    {
        Name = "5.9 Cummins 6B pickup, no turbo",
        EngineKey = "diesel_cummins_na",
        Engine = EngineProfile.DieselCumminsNaI6,
        // Nearly eight decibels above the turbo version: no turbine eats the pulse energy.
        SourceLevelDb = 115f,
    };

    /// <summary>The school bus with the turbo taken off.</summary>
    public static VehicleProfile SchoolBusNa => SchoolBus with
    {
        Name = "school bus, no turbo",
        EngineKey = "diesel_bus_na",
        Engine = EngineProfile.DieselBusNaI6,
        // Thirteen decibels above the turbo bus at the tailpipe, close to it on the whole vehicle: 95
        // on the live voice.
        SourceLevelDb = 95f,
    };

    public static VehicleProfile Wagon => new()
    {
        Chassis = RunningGear.Outback,
        LengthMetres = 4.7f, WidthMetres = 1.8f, HeightMetres = 1.5f,
        // A long roof and a big rear volume: a wagon booms where a saloon does not.
        Body = VehicleBody.Van,
        Name = "2.5 flat-four wagon",
        CoolingFan = CarRadiatorFan, ElectricFan = ElectricFanSpec.TwoSpeed,
        EngineKey = "boxer4",
        SourceLevelDb = 118f,
        Engine = EngineProfile.Boxer4,
        Gearbox = Gearbox.SixSpeedSports with { Ratios = new[] { 3.45f, 1.95f, 1.37f, 0.97f, 0.74f }, FinalDrive = 4.11f, ShiftSeconds = 0.35f, UpshiftRpm = 6000f, DownshiftRpm = 1500f, WheelRadiusMetres = 0.32f },
        Tyres = TyreProfile.SportsOnAsphalt,
        MassKg = 1500f, DragArea = 0.72f,
    };

    public static VehicleProfile V10Coupe => new()
    {
        Chassis = RunningGear.ViperSrt10,
        Name = "V10 coupe, side pipes",
        CoolingFan = CarRadiatorFan, ElectricFan = ElectricFanSpec.TwoSpeed,
        EngineKey = "v10",
        SourceLevelDb = 120f,   // 120.0 measured: the exhaust valves' flow noise on side pipes
        Engine = EngineProfile.V10,
        // An automated single-clutch box: 60 ms against a manual's 280 is too short to hear as a gap,
        // and the note steps down like a hammer.
        Gearbox = Gearbox.SixSpeedSports with { Ratios = new[] { 2.66f, 1.78f, 1.3f, 1.0f, 0.74f, 0.5f }, FinalDrive = 3.07f, ShiftSeconds = 0.06f, UpshiftRpm = 5900f, DownshiftRpm = 3000f },
        Tyres = TyreProfile.SportsOnAsphalt,
        MassKg = 1560f, DragArea = 0.75f,
        ExhaustOffsetZ = 0.3f, IntakeOffsetZ = 1.3f,
    };

    /// <summary>
    /// A Cup stock car: 1450 kg, no muffler, a four-speed geared for a one-mile oval. The exhaust leaves
    /// through the side behind the driver's door, so it is loudest square abeam.
    /// </summary>
    public static VehicleProfile StockCar => new()
    {
        Chassis = RunningGear.NascarNextGen,
        LengthMetres = 5.1f, WidthMetres = 1.95f, HeightMetres = 1.3f,
        Body = VehicleBody.RaceSaloon,
        Name = "NASCAR Cup stock car",
        EngineKey = "nascar_v8",
        SourceLevelDb = 130f,
        Engine = EngineProfile.NascarV8,
        Gearbox = Gearbox.SixSpeedSports with
        {
            Ratios = new[] { 2.20f, 1.56f, 1.24f, 1.00f },
            FinalDrive = 3.90f,
            WheelRadiusMetres = 0.356f,
            ShiftSeconds = 0.14f,
            UpshiftRpm = 8900f,
            DownshiftRpm = 5200f,   // a race driver never leaves the power band
        },
        Tyres = TyreProfile.RaceSlick,
        MassKg = 1450f, DragArea = 0.92f, RollingResistance = 0.011f,
        // Side exit level with the driver (left, in a Cup car); the airbox under the windscreen cowl.
        ExhaustOffsetZ = 0.1f, IntakeOffsetZ = 1.1f, FrontAxleZ = 1.4f, RearAxleZ = -1.4f,
        ExhaustAxis = new Vector3(-1f, 0f, 0f),
    };

    /// <summary>
    /// A three-litre V10 formula car: 620 kg with the driver, seven gears, no silencer. The pipes exit
    /// behind the rear axle pointing back and up, so it is loudest going away.
    /// </summary>
    public static VehicleProfile FormulaCar => new()
    {
        Chassis = RunningGear.F2004,
        LengthMetres = 5.3f, WidthMetres = 1.9f, HeightMetres = 0.95f,
        Body = VehicleBody.OpenWheeler,
        Name = "V10 formula car",
        EngineKey = "f1_v10",
        SourceLevelDb = 133f,
        Engine = EngineProfile.F1V10,
        Gearbox = Gearbox.SixSpeedSports with
        {
            Ratios = new[] { 5.90f, 4.40f, 3.55f, 2.95f, 2.50f, 2.10f, 1.75f },
            // An oval gear set: top gear runs out at 330 km/h on the limiter.
            FinalDrive = 3.23f,
            WheelRadiusMetres = 0.33f,
            ShiftSeconds = 0.05f,   // seamless shift: the note steps rather than sweeps
            UpshiftRpm = 15100f,
            DownshiftRpm = 8500f,
        },
        Tyres = TyreProfile.RaceSlick with { SurfaceRoughness = 0.3f, ReferenceDb = 80f, PeakGripG = 4.2f, SquealHz = 690f },
        MassKg = 620f,
        // Oval trim. With a road-course 1.35 m^2 it could not reach 230 km/h of the speedway's 327 and
        // droned flat out at 10,500 of 15,500 rpm; 0.85 gives 296 km/h at 13,450, its torque peak.
        // Measured: 1.35 -> 230, 1.10 -> 270, 0.95 -> 285, 0.85 -> 296 km/h.
        DragArea = 0.85f, RollingResistance = 0.014f,
        ExhaustOffsetZ = -1.6f, IntakeOffsetZ = -0.2f, FrontAxleZ = 1.7f, RearAxleZ = -1.6f,
    };

    /// <summary>
    /// A road-going police interceptor, the one on a city street. Never to share a preset with
    /// <see cref="PoliceCar"/>, a pace car at 132 dB (fourteen over a muscle car, thirty-five over a
    /// bus). This is a saloon with a catalyst and a silencer; what you hear coming is the siren, thirty
    /// decibels over its exhaust.
    /// </summary>
    public static VehicleProfile PoliceInterceptor => new()
    {
        Chassis = RunningGear.ChargerPursuit,
        LengthMetres = 5.1f, WidthMetres = 2.0f, HeightMetres = 1.55f,
        Body = VehicleBody.Saloon,
        Name = "Police interceptor, road",
        CoolingFan = CarRadiatorFan, ElectricFan = ElectricFanSpec.TwoSpeed,
        EngineKey = "police_interceptor",
        // 99.6 on the live voice with the pursuit exhaust: a V8 you hear working, where a fully stock
        // one at 93 sits level with an economy hatchback.
        SourceLevelDb = 100f,
        Engine = EngineProfile.PoliceInterceptorV8,
        Siren = "patrol",
        Gearbox = Gearbox.SixSpeedSports with
        {
            Ratios = new[] { 3.54f, 2.26f, 1.54f, 1.14f, 0.85f, 0.67f },
            FinalDrive = 3.15f, WheelRadiusMetres = 0.35f,
            ShiftSeconds = 0.30f, UpshiftRpm = 5600f, DownshiftRpm = 1600f,
        },
        Tyres = TyreProfile.SportsOnAsphalt with { TreadBlocks = 60, SurfaceRoughness = 0.55f },
        MassKg = 2050f, DragArea = 0.78f, RollingResistance = 0.012f,
        ExhaustOffsetZ = -2.2f, IntakeOffsetZ = 1.3f, FrontAxleZ = 1.5f, RearAxleZ = -1.5f,
    };

    /// <summary>
    /// The speedway's pace car: an open-exhaust, big-cam V8 geared to run with the field, and a siren.
    /// Heavier and draggier than a stock car, so it tops out under the racers and has to work.
    /// </summary>
    public static VehicleProfile PoliceCar => new()
    {
        Chassis = RunningGear.ChargerPursuit,
        LengthMetres = 5.1f, WidthMetres = 2.0f, HeightMetres = 1.55f,
        // Stripped: no carpet, no trim, a cage and bare steel.
        Body = VehicleBody.RaceSaloon,
        Name = "Police interceptor",
        EngineKey = "police_v8",
        SourceLevelDb = 132f,
        Siren = "patrol",
        Engine = EngineProfile.PoliceV8,
        Gearbox = Gearbox.SixSpeedSports with
        {
            Ratios = new[] { 2.97f, 2.07f, 1.43f, 1.00f, 0.85f },
            FinalDrive = 3.55f,
            WheelRadiusMetres = 0.35f,
            ShiftSeconds = 0.25f,
            UpshiftRpm = 6500f,
            DownshiftRpm = 2200f,
        },
        Tyres = TyreProfile.SportsOnAsphalt with { TreadBlocks = 4, SurfaceRoughness = 0.4f },
        MassKg = 1980f, DragArea = 1.05f, RollingResistance = 0.013f,
        ExhaustOffsetZ = -2.2f, IntakeOffsetZ = 1.3f, FrontAxleZ = 1.5f, RearAxleZ = -1.5f,
    };

    public static VehicleProfile GrandTourer => new()
    {
        Chassis = RunningGear.Db9,
        Horn = "electric:trumpet_pair",
        LengthMetres = 4.9f, WidthMetres = 2.0f, HeightMetres = 1.3f,
        Name = "V12 grand tourer",
        CoolingFan = CarRadiatorFan, ElectricFan = ElectricFanSpec.TwoSpeed,
        EngineKey = "v12",
        SourceLevelDb = 124f,   // the live voice; 128 at the tailpipe
        Engine = EngineProfile.V12,
        Gearbox = Gearbox.SixSpeedSports with { Ratios = new[] { 4.17f, 2.34f, 1.52f, 1.14f, 0.87f, 0.69f }, FinalDrive = 3.46f, ShiftSeconds = 0.25f, UpshiftRpm = 7200f, DownshiftRpm = 1600f },
        Tyres = TyreProfile.SportsOnAsphalt,
        MassKg = 1850f, DragArea = 0.7f,
    };
}

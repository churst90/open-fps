using System;
using System.Collections.Generic;
using System.Numerics;

namespace OpenFPS.Common;

/// <summary>A manual gearbox: the ratios, and how long the driver's left foot takes.</summary>
public sealed record Gearbox
{
    /// <summary>Ratio per gear, first at index 0.</summary>
    public required float[] Ratios { get; init; }
    public required float FinalDrive { get; init; }
    /// <summary>Rolling radius of the driven wheel, metres.</summary>
    public required float WheelRadiusMetres { get; init; }

    /// <summary>How long the clutch is DOWN during a shift. This is the silence in the middle of the
    /// shift, and getting it right is most of what makes a change sound like a person rather than an
    /// event: a quick change is about a quarter of a second.</summary>
    public float ShiftSeconds { get; init; } = 0.28f;
    /// <summary>RPM the driver shifts up at when accelerating hard.</summary>
    public float UpshiftRpm { get; init; } = 6100f;
    /// <summary>...and drops to when coasting down.</summary>
    public float DownshiftRpm { get; init; } = 1500f;

    public int TopGear => Ratios.Length;

    /// <summary>Engine RPM for a road speed in a given gear. The whole reason a gearbox is audible.</summary>
    public float RpmFor(float speedMetresPerSecond, int gear)
    {
        if (gear < 1 || gear > Ratios.Length) return 0f;
        float wheelRevsPerSec = speedMetresPerSecond / (2f * MathF.PI * WheelRadiusMetres);
        return MathF.Max(0f, wheelRevsPerSec * Ratios[gear - 1] * FinalDrive * 60f);
    }

    /// <summary>Road speed at which a gear reaches a given RPM — for picking a starting gear.</summary>
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

/// <summary>The tyre and the road under it.</summary>
public sealed record TyreProfile
{
    /// <summary>Tread blocks around the circumference. Their passing rate is a real tonal component of
    /// tyre noise — speed divided by block spacing — and it is why tyre roar rises in PITCH with speed
    /// rather than only in level.</summary>
    public int TreadBlocks { get; init; } = 68;
    /// <summary>How rough the surface is, 0 = polished concrete, 1 = coarse chip seal. Drives how much
    /// broadband roar there is against the tonal component.</summary>
    public float SurfaceRoughness { get; init; } = 0.55f;
    /// <summary>Level at a reference 20 m/s, dB SPL at 1 m. Tyres are the dominant sound of a car above
    /// about fifty km/h, which surprises people who expect the engine to be.</summary>
    public float ReferenceDb { get; init; } = 74f;

    // ── Sliding ─────────────────────────────────────────────────────────────────────────────────
    //
    // Everything below describes what this tyre does when it is asked for more than it has. It lives
    // on the TYRE, not on the car and not on the map, because that is where it comes from: a road
    // tyre lets go at about one g and squeals near a kilohertz; a slick holds three and sings lower
    // and louder because the casing is bigger; a truck tyre gives up early and groans. Put a set of
    // slicks on a van and the van squeals like a racing car, which is correct.

    /// <summary>
    /// Peak friction this tyre can deliver, in g.
    ///
    /// The denominator of everything: how hard a car can corner, brake or accelerate before the
    /// contact patch starts sliding and singing. A road tyre on dry asphalt is about 0.95; a modern
    /// performance tyre 1.1; a stock-car slick on a banked oval nearer 2.9 once the banking is
    /// carrying part of the load; a loaded truck tyre 0.75.
    /// </summary>
    public float PeakGripG { get; init; } = 0.95f;

    /// <summary>
    /// The stick-slip resonance of a tread element, Hz — the note the tyre squeals.
    ///
    /// Set by how stiff the rubber is and how big the block is, so it goes DOWN as tyres get bigger:
    /// a kart tyre shrieks, a truck tyre groans. This is the fundamental; the harmonics come with it.
    /// </summary>
    public float SquealHz { get; init; } = 950f;

    /// <summary>How sharp that resonance is. High is a clean, almost musical squeal; low is a rough,
    /// noisy scrub. Soft compounds and worn surfaces blur it.</summary>
    public float SquealQ { get; init; } = 14f;

    /// <summary>Level of a full squeal at 1 m, dB SPL. Loud: a car at the limit is heard from a long
    /// way, and on a track it carries further than the engines because it is higher up the spectrum.</summary>
    public float SquealDb { get; init; } = 92f;

    /// <summary>A decent road tyre on dry asphalt.</summary>
    public static TyreProfile SportsOnAsphalt => new();

    /// <summary>
    /// A racing slick: no tread, so no block tone at all — the whole rolling sound is roar. Enormous
    /// grip, and when it finally lets go it does so loudly and at a lower pitch, because the block
    /// that is sticking and slipping is the width of the whole contact patch.
    /// </summary>
    public static TyreProfile RaceSlick => new()
    {
        TreadBlocks = 0, SurfaceRoughness = 0.35f, ReferenceDb = 78f,
        // EFFECTIVE grip, not the tyre's pure lateral figure, and the difference is worth writing
        // down. A slick does about 1.75 g on the flat. On a banked turn the load vector tilts, so the
        // same tyre delivers far more cornering than that — and the client measures a car's lateral
        // acceleration from its own motion, which it can only do in the world frame. It sees the
        // total, including the part the banking is carrying, and it has no way to see a bank angle.
        //
        // So the figure here is what these tyres achieve on the surfaces they run on. It is right for
        // the reason it looks wrong, and the honest fix — the server sending the friction demand it
        // already computes from the racing line, banking included — is a protocol change for later.
        PeakGripG = 3.1f, SquealHz = 620f, SquealQ = 17f, SquealDb = 99f,
    };

    /// <summary>A loaded truck tyre: coarse tread, a lot of roar, and it gives up early and groans.</summary>
    public static TyreProfile TruckOnAsphalt => new()
    {
        TreadBlocks = 96, SurfaceRoughness = 0.72f, ReferenceDb = 82f,
        PeakGripG = 0.75f, SquealHz = 430f, SquealQ = 9f, SquealDb = 97f,
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
    /// A vehicle is a RIG, not a sound: the exhaust is three metres behind the intake, and at close
    /// range that separation tells a listener which way the car is pointing.</summary>
    public float ExhaustOffsetZ { get; init; } = -2.05f;
    public float IntakeOffsetZ { get; init; } = 1.35f;

    /// <summary>How high the tailpipe is above the car's contact patch, metres. Its own field because
    /// a truck's stack and a saloon's tailpipe are not at the same height, and because it is the number
    /// that keeps the occlusion probe out of the road surface.</summary>
    public float ExhaustHeight { get; init; } = 0.3f;

    /// <summary>
    /// How high the intake mouth is above the contact patch, metres.
    ///
    /// The other end of the rig, and it had no number at all while the car was one voice, because a
    /// single emitter between the two ends only ever needed the tailpipe's height. A car breathes at
    /// about the top of the engine bay; a formula car's airbox is above the driver's head, and a
    /// bike's is between your knees. It matters for the same reason the exhaust's does: it is where
    /// the occlusion probe goes, and it is what a listener standing beside the car actually hears
    /// from the front of it.
    /// </summary>
    public float IntakeHeight { get; init; } = 0.7f;

    /// <summary>
    /// How far back along <see cref="ExhaustOffsetZ"/> the single combined engine voice actually sits.
    ///
    /// A car heard as ONE voice is a compromise between an intake at the front and an exhaust at the
    /// back; the voice belongs between them, nearer the exhaust because that is where most of the
    /// sound is. Named so it stops being an unexplained 0.6 in two different files.
    /// </summary>
    public const float ExhaustEmitterBias = 0.6f;

    /// <summary>Where the car's engine voice sits, in the car's own frame. This is the emitter slot:
    /// where the sound comes out, which is what occlusion, distance and direction are all about.</summary>
    public Vector3 ExhaustOffset => new(0f, ExhaustHeight, ExhaustOffsetZ * ExhaustEmitterBias);
    public float FrontAxleZ { get; init; } = 1.25f;
    public float RearAxleZ { get; init; } = -1.35f;

    /// <summary>The preset key of the engine, for a map or a command line to name. See EngineProfile.Presets.</summary>
    public string EngineKey { get; init; } = "";

    /// <summary>
    /// The car the engine is bolted into, as something the sound has to get out through.
    ///
    /// Two cars with the same engine do not sound the same, and most of the difference is here: how
    /// much steel there is, how big the panels are, how much deadening the manufacturer paid for,
    /// and whether there is a cabin behind it. See <see cref="VehicleBody"/> for why this is the one
    /// part of a vehicle that can honestly be an impulse response while the exhaust cannot.
    /// </summary>
    public VehicleBody Body { get; init; } = VehicleBody.Saloon;

    /// <summary>
    /// The compressed-air system this vehicle carries, by <see cref="AirSystemSpec"/> preset name, or
    /// null for a vehicle with hydraulic brakes. A bus or a heavy truck has one, and it is heard: the
    /// service brakes exhaust to atmosphere every time the pedal comes up, the spring brakes dump
    /// their chambers when the park brake is set, the dryer purges when the governor cuts out. The
    /// voice makes those from the vehicle's own speed history, so nothing on the wire carries them.
    /// </summary>
    public string? AirSystem { get; init; }

    /// <summary>
    /// How much of the engine's MECHANICAL noise — injection clatter, timing gears, the block —
    /// reaches the street, 0..1. A car's engine sits under a bonnet in a lined bay and next to none
    /// of it does; the exhaust is the car. A truck's engine hangs in the open air under a cab and a
    /// bus's sits in a compartment ventilated by grilles, and for both of those the mechanical noise
    /// is a good part of what a bystander hears — it is why a diesel truck idling is loud and a
    /// diesel car idling is not, on the same fuel. Zero leaves the voice exactly as it was.
    /// </summary>
    public float EngineBayLeakage { get; init; }

    /// <summary>
    /// What this vehicle measures at one metre at full load, dB SPL — and the number the whole
    /// audio chain is hung off.
    ///
    /// It decides two separate things, and they are both wrong if it is wrong. It decides where the
    /// emitter sits in the mix (<see cref="Loudness.Place"/>), so a car that claims to be quieter
    /// than it is gets placed too far down. And it decides what ONE FULL-SCALE SAMPLE MEANS inside
    /// the synthesis (<c>EngineVoiceState.PascalsAtFullScale</c>), which is the one that bites: the
    /// voice runs anything past its reference through a tanh, so a race engine twenty decibels over
    /// a road car's reference does not arrive loud, it arrives as a SQUARE WAVE. That is what the
    /// first speedway sounded like — "everything is overloaded, crackling and breaking up" — and no
    /// amount of turning the volume down fixes it, because the clipping happens before the volume.
    ///
    /// Measured, not chosen: `--engine-levels` renders every preset at full load and prints it, and
    /// `EngineSynthTests.DeclaredSourceLevelMatchesWhatThePresetMeasures` fails if one drifts.
    /// </summary>
    public float SourceLevelDb { get; init; } = 116f;

    /// <summary>
    /// Headroom between the declared level and the pressure that maps to full scale, dB.
    ///
    /// The declared level is the loudest SECOND — an RMS — and a firing engine's peaks run well above
    /// its mean: the measured crest factor across the presets is 8 to 21 dB, a backfire further still.
    ///
    /// It is ONE number for every engine on purpose. Normalising each voice by its own peak instead
    /// would let the crest factor leak into the mix, so an engine with peaky transients would render
    /// quieter than an equally loud smooth one — the relative loudness of two cars would depend on
    /// the shape of their pulses rather than on how loud they are. With a shared headroom, every
    /// engine's RMS lands at the same place below full scale and Loudness.Place alone decides the
    /// balance, which is what it is for.
    ///
    /// Sixteen is measured, not chosen: it is the largest SUSTAINED crest across the presets (the
    /// 99.9th percentile against the mean, 6 to 15 dB — see `--engine-levels`). The absolute peaks
    /// run higher, to 21 dB, but those are individual backfires and overrun pops a few times a
    /// second, and rounding one transient is limiting where rounding all of them is clipping.
    ///
    /// It leaves an engine's mean about sixteen decibels under full scale, which is where a
    /// peak-normalised sample file sits too — so a synthesized engine and a recorded sound arrive at
    /// Loudness.Place on the same terms.
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
            ["school_bus"] = () => SchoolBus,
            ["diesel_cummins_na"] = () => DieselPickupNa,
            ["school_bus_na"] = () => SchoolBusNa,
            ["boxer4"] = () => Wagon,
            ["v10"] = () => V10Coupe,
            ["v12"] = () => GrandTourer,
            ["nascar_v8"] = () => StockCar,
            ["f1_v10"] = () => FormulaCar,
            ["police_v8"] = () => PoliceCar,
            ["v8_open_headers"] = () => OpenHeaderMuscle,
            ["v8_glasspack"] = () => GlasspackMuscle,
            ["v8_mild"] = () => MildMuscle,
            ["v8_bigcam"] = () => BigCamMuscle,
            ["v8_blown"] = () => BlownMuscle,
            ["sportbike"] = () => SportBike,
        };

    /// <summary>
    /// A preset by name — built once and then shared.
    ///
    /// Memoised because this is asked on the hot path, several times per car per frame, and building
    /// one is not cheap: a whole engine, its cams, valves, exhaust and intake networks, a gearbox and
    /// a set of tyres, constructed and thrown away to read a single field. Thirty cars at sixty
    /// frames a second was thousands of them a second on the audio thread.
    ///
    /// Safe to share: every member is init-only, so a profile cannot be changed after construction.
    /// Anything that wants a variation uses `with`, which copies.
    /// </summary>
    public static VehicleProfile ByName(string key)
        => _cache.GetOrAdd(key, static k => Presets.TryGetValue(k, out var make) ? make()
             : throw new ArgumentException($"No vehicle preset '{k}'. Known: {string.Join(", ", Presets.Keys)}"));

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, VehicleProfile> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    // ── Four muscle cars, one V8, four exhausts ─────────────────────────────────────────────────
    //
    // Same car underneath, so nothing but the engine and what is bolted to it can account for the
    // difference. They share the muscle car's mass, gearing and tyres deliberately: put them on a
    // circuit together and any difference you can hear is the hardware.

    /// <summary>Open headers. The rawest thing on the track, and the only one with no can at all.</summary>
    public static VehicleProfile OpenHeaderMuscle => V8Muscle with
    {
        Name = "Muscle car, open headers",
        EngineKey = "v8_open_headers",
        Engine = EngineProfile.V8OpenHeaders,
        SourceLevelDb = 128f,
    };

    /// <summary>The same big block through glasspacks: mellower, and no metallic ring, because the
    /// packing that absorbs the gas is against the case and damps it too.</summary>
    public static VehicleProfile GlasspackMuscle => V8Muscle with
    {
        Name = "Muscle car, glasspacks",
        EngineKey = "v8_glasspack",
        Engine = EngineProfile.V8BigBlockGlasspack,
        SourceLevelDb = 123f,
    };

    /// <summary>A mild small block on stock manifolds — the least dramatic car out there, which is
    /// what makes the others audible as choices rather than as the only way a V8 sounds.</summary>
    public static VehicleProfile MildMuscle => V8Muscle with
    {
        Name = "Muscle car, mild small block",
        EngineKey = "v8_mild",
        Engine = EngineProfile.V8MildSmallBlock,
        MassKg = 1520f,
        SourceLevelDb = 112f,
    };

    /// <summary>The beefiest: 7.4 litres on a 330-degree cam through 40-series cans. It lopes at
    /// idle because the cam overlaps enough to make the burn ragged, which is what the cam is for.</summary>
    public static VehicleProfile BigCamMuscle => V8Muscle with
    {
        Name = "Muscle car, big cam",
        EngineKey = "v8_bigcam",
        Engine = EngineProfile.V8BigCam,
        MassKg = 1720f,
        SourceLevelDb = 119f,
    };

    /// <summary>
    /// A litre sports bike. No body, no cabin, and it changes gear in a tenth of a second.
    ///
    /// It is the loudest small thing on a circuit and it sounds nothing like a car, for a reason that
    /// is arithmetic rather than character: at 14,500 rpm a four fires 483 times a second, so its
    /// FUNDAMENTAL is a musical pitch and its orders run into the kilohertz. A car's fundamental is a
    /// beat you could count.
    /// </summary>
    public static VehicleProfile SportBike => new()
    {
        Name = "Litre sports bike",
        EngineKey = "sportbike",
        Engine = EngineProfile.SportBike,
        Gearbox = Gearbox.SixSpeedSports with
        {
            Ratios = new[] { 2.57f, 1.94f, 1.61f, 1.41f, 1.29f, 1.19f },
            FinalDrive = 3.0f, WheelRadiusMetres = 0.31f,
            // A sequential box with a quickshifter: the clutch never opens and the ignition is cut
            // for the instant the dog rings move. A tenth of a second, and audible as a CUT rather
            // than a lift.
            ShiftSeconds = 0.09f, UpshiftRpm = 13800f, DownshiftRpm = 5000f,
        },
        Tyres = TyreProfile.SportsOnAsphalt,
        MassKg = 200f,
        DragArea = 0.42f,
        RollingResistance = 0.015f,
        // Nothing to radiate through: an open frame with the engine hanging in it.
        Body = VehicleBody.OpenWheeler,
        ExhaustOffsetZ = -0.75f, IntakeOffsetZ = 0.25f, ExhaustHeight = 0.55f,
        FrontAxleZ = 0.70f, RearAxleZ = -0.70f,
        SourceLevelDb = 118f,
    };

    /// <summary>A blown big block: the whine of the rotors over the lope of the cam, and no lag at
    /// all, because a supercharger is geared to the crank and has nothing to spool.</summary>
    public static VehicleProfile BlownMuscle => V8Muscle with
    {
        Name = "Muscle car, blown big block",
        EngineKey = "v8_blown",
        Engine = EngineProfile.V8Blown,
        MassKg = 1780f,
        Body = VehicleBody.RaceSaloon,
        SourceLevelDb = 119f,
    };

    /// <summary>A big-block muscle car: long cam, true duals, four-speed.</summary>
    public static VehicleProfile V8Muscle => new()
    {
        Name = "Big-block muscle car, true duals",
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
        Name = "V8 sports car, Flowmaster 40s",
        EngineKey = "v8_sports",
        SourceLevelDb = 116f,
        Engine = EngineProfile.V8SportsFlowmaster40,
        Gearbox = Gearbox.SixSpeedSports,
        Tyres = TyreProfile.SportsOnAsphalt,
    };

    public static VehicleProfile Supercar => new()
    {
        // Small aluminium panels, a tiny cabin, and an exhaust that barely touches the shell.
        Body = VehicleBody.Supercar,
        Name = "Flat-plane V8 supercar",
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
        Name = "1.6 hatchback",
        EngineKey = "i4_economy",
        SourceLevelDb = 94f,
        Engine = EngineProfile.Inline4Economy,
        Gearbox = Gearbox.SixSpeedSports with { Ratios = new[] { 3.6f, 2.0f, 1.36f, 1.03f, 0.82f }, FinalDrive = 4.2f, ShiftSeconds = 0.4f, UpshiftRpm = 5200f, DownshiftRpm = 1400f, WheelRadiusMetres = 0.30f },
        Tyres = TyreProfile.SportsOnAsphalt with { TreadBlocks = 60 },
        MassKg = 1150f, DragArea = 0.66f,
        ExhaustOffsetZ = -1.8f, IntakeOffsetZ = 1.4f, FrontAxleZ = 1.2f, RearAxleZ = -1.3f,
    };

    public static VehicleProfile HotHatch => new()
    {
        Name = "2.0 hot hatch",
        EngineKey = "i4_sport",
        SourceLevelDb = 119f,
        Engine = EngineProfile.Inline4Sport,
        Gearbox = Gearbox.SixSpeedSports with { Ratios = new[] { 3.27f, 2.13f, 1.52f, 1.15f, 0.92f, 0.76f }, FinalDrive = 4.3f, ShiftSeconds = 0.3f, UpshiftRpm = 7800f, DownshiftRpm = 1800f, WheelRadiusMetres = 0.31f },
        Tyres = TyreProfile.SportsOnAsphalt,
        MassKg = 1280f, DragArea = 0.68f,
        ExhaustOffsetZ = -1.9f, IntakeOffsetZ = 1.4f,
    };

    /// <summary>
    /// The turbo version of the hot hatch: same size of car, a very different noise.
    ///
    /// Shorter gearing is not needed, because the torque is there from two thousand — so it is
    /// LONGER geared than the atmospheric car and spends a lap in fewer, taller gears. That is an
    /// audible consequence of the engine rather than a styling choice, and it is why a turbo car
    /// sounds lazy next to a screaming naturally-aspirated one doing the same lap time.
    /// </summary>
    public static VehicleProfile TurboHatch => new()
    {
        Name = "2.0 turbo hatch",
        EngineKey = "i4_turbo",
        // Measured, not guessed: EngineSynthTests renders every preset and holds its declared level
        // to what it actually produces. A first guess of 101 was sixteen decibels light, which would
        // have put this car forty times too quiet next to the field it shares a track with.
        SourceLevelDb = 100f,
        Engine = EngineProfile.I4Turbo,
        Gearbox = Gearbox.SixSpeedSports with { Ratios = new[] { 3.4f, 2.05f, 1.42f, 1.06f, 0.84f, 0.68f }, FinalDrive = 3.7f, ShiftSeconds = 0.18f, UpshiftRpm = 6300f, DownshiftRpm = 2000f },
        Tyres = TyreProfile.SportsOnAsphalt with { TreadBlocks = 58, PeakGripG = 1.1f, SquealHz = 880f },
        MassKg = 1380f, DragArea = 0.68f,
        ExhaustOffsetZ = -1.85f, IntakeOffsetZ = 1.3f,
    };

    public static VehicleProfile Saloon6 => new()
    {
        Name = "3.0 straight-six saloon",
        EngineKey = "i6",
        SourceLevelDb = 117f,
        Engine = EngineProfile.Inline6,
        Gearbox = Gearbox.SixSpeedSports with { Ratios = new[] { 4.06f, 2.37f, 1.56f, 1.16f, 0.85f, 0.67f }, FinalDrive = 3.15f, ShiftSeconds = 0.3f, UpshiftRpm = 6600f, DownshiftRpm = 1500f },
        Tyres = TyreProfile.SportsOnAsphalt,
        MassKg = 1600f, DragArea = 0.64f,
    };

    public static VehicleProfile Sedan6 => new()
    {
        Name = "3.5 V6 sedan",
        EngineKey = "v6",
        SourceLevelDb = 100f,
        Engine = EngineProfile.V6Sedan,
        Gearbox = Gearbox.SixSpeedSports with { Ratios = new[] { 4.58f, 2.96f, 1.91f, 1.45f, 1.0f, 0.75f }, FinalDrive = 3.3f, ShiftSeconds = 0.35f, UpshiftRpm = 6000f, DownshiftRpm = 1400f, WheelRadiusMetres = 0.33f },
        Tyres = TyreProfile.SportsOnAsphalt with { TreadBlocks = 62 },
        MassKg = 1650f, DragArea = 0.66f,
    };

    public static VehicleProfile Cruiser => new()
    {
        // A motorcycle has no body and no cabin: the pipes radiate into open air.
        Body = VehicleBody.OpenWheeler,
        Name = "V-twin cruiser motorcycle",
        EngineKey = "vtwin",
        SourceLevelDb = 121f,
        Engine = EngineProfile.VTwin45,
        Gearbox = Gearbox.SixSpeedSports with { Ratios = new[] { 3.34f, 2.3f, 1.71f, 1.41f, 1.18f, 1.0f }, FinalDrive = 2.87f, ShiftSeconds = 0.25f, UpshiftRpm = 5000f, DownshiftRpm = 1800f, WheelRadiusMetres = 0.33f },
        Tyres = TyreProfile.SportsOnAsphalt with { TreadBlocks = 40, SurfaceRoughness = 0.4f },
        MassKg = 380f, DragArea = 0.55f,
        ExhaustOffsetZ = -0.5f, IntakeOffsetZ = 0.1f, FrontAxleZ = 0.8f, RearAxleZ = -0.8f,
    };

    public static VehicleProfile DirtBike => new()
    {
        // Likewise, and even less of it.
        Body = VehicleBody.OpenWheeler,
        Name = "450 dirt bike",
        EngineKey = "single",
        SourceLevelDb = 118f,
        Engine = EngineProfile.Single450,
        Gearbox = Gearbox.SixSpeedSports with { Ratios = new[] { 2.4f, 1.8f, 1.4f, 1.15f, 0.96f }, FinalDrive = 3.8f, ShiftSeconds = 0.18f, UpshiftRpm = 10000f, DownshiftRpm = 3000f, WheelRadiusMetres = 0.33f },
        Tyres = TyreProfile.SportsOnAsphalt with { TreadBlocks = 28, SurfaceRoughness = 0.7f },
        MassKg = 190f, DragArea = 0.5f,
        ExhaustOffsetZ = -0.4f, IntakeOffsetZ = 0.1f, FrontAxleZ = 0.75f, RearAxleZ = -0.75f,
    };

    public static VehicleProfile Pickup => new()
    {
        // A cab and an empty steel bed, which is the most resonant thing on the road.
        Body = VehicleBody.Van,
        Name = "2.8 turbo-diesel pickup",
        EngineKey = "diesel_i4",
        SourceLevelDb = 88f,   // re-measured 2026-09-18 after the jet went onto Lighthill: was 92, most of the difference was hiss
        Engine = EngineProfile.DieselPickupI4,
        Gearbox = Gearbox.SixSpeedSports with { Ratios = new[] { 4.31f, 2.33f, 1.52f, 1.13f, 0.86f, 0.68f }, FinalDrive = 3.73f, ShiftSeconds = 0.45f, UpshiftRpm = 3600f, DownshiftRpm = 1300f, WheelRadiusMetres = 0.38f },
        Tyres = TyreProfile.SportsOnAsphalt with { TreadBlocks = 48, SurfaceRoughness = 0.6f },
        MassKg = 2200f, DragArea = 1.1f,
        ExhaustOffsetZ = -2.4f, IntakeOffsetZ = 1.8f, FrontAxleZ = 1.6f, RearAxleZ = -1.6f,
    };

    public static VehicleProfile Truck => new()
    {
        // Big flat undeadened panels over a big box.
        Body = VehicleBody.Van,
        Name = "13 litre semi truck",
        EngineKey = "diesel_truck",
        SourceLevelDb = 93f,
        AirSystem = "tractor_trailer",
        EngineBayLeakage = 0.6f,
        Engine = EngineProfile.DieselTruckI6,
        Gearbox = Gearbox.SixSpeedSports with { Ratios = new[] { 11.7f, 7.6f, 5.0f, 3.3f, 2.2f, 1.45f, 1.0f, 0.78f }, FinalDrive = 3.55f, ShiftSeconds = 0.8f, UpshiftRpm = 1800f, DownshiftRpm = 1100f, WheelRadiusMetres = 0.51f },
        Tyres = TyreProfile.TruckOnAsphalt,
        MassKg = 14000f, DragArea = 5.5f, RollingResistance = 0.008f,
        ExhaustOffsetZ = 1.0f, IntakeOffsetZ = 2.5f, FrontAxleZ = 3.5f, RearAxleZ = -3.0f,
    };

    /// <summary>
    /// A Dodge Ram with the 5.9 Cummins in it and five inches of straight pipe out the back.
    ///
    /// A cab and an empty steel bed — the most resonant thing on the road — over an engine with no
    /// silencer at all. The pipe exits behind the rear axle, so it is loudest going away, and the
    /// bed sits directly over most of its run.
    /// </summary>
    public static VehicleProfile DieselPickupLoud => new()
    {
        Body = VehicleBody.Van,
        Name = "5.9 Cummins pickup, straight pipe",
        // Measured with --engine-levels, not guessed: a straight-piped 5.9 is eleven decibels above
        // what a silenced pickup diesel makes.
        EngineKey = "diesel_cummins",
        SourceLevelDb = 107f,
        Engine = EngineProfile.DieselCumminsI6,
        // Four ratios and a very tall final drive: this engine has no revs to give and does not need
        // any. Governed at 2,900, top gear runs out around 160 km/h.
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

    /// <summary>
    /// A school bus: a DT466 under a long steel box, silenced, and geared to get there eventually.
    ///
    /// The engine is at the FRONT and the pipe comes out at the BACK, eleven metres away, which is
    /// the one thing about a bus that a listener outside it notices without being told — the clatter
    /// arrives from the nose and the exhaust from the tail, and they are far enough apart to hear as
    /// two places.
    /// </summary>
    public static VehicleProfile SchoolBus => new()
    {
        Body = VehicleBody.SchoolBus,
        Name = "school bus",
        EngineKey = "diesel_bus",
        SourceLevelDb = 85f,
        AirSystem = "transit_bus",
        EngineBayLeakage = 0.45f,
        Engine = EngineProfile.DieselBusI6,
        Gearbox = Gearbox.SixSpeedSports with
        {
            Ratios = new[] { 7.05f, 4.14f, 2.52f, 1.56f, 1.00f, 0.74f },
            FinalDrive = 4.78f, ShiftSeconds = 0.9f,
            UpshiftRpm = 2350f, DownshiftRpm = 1150f, WheelRadiusMetres = 0.50f,
        },
        Tyres = TyreProfile.TruckOnAsphalt,
        MassKg = 11000f, DragArea = 5.8f, RollingResistance = 0.009f,
        // Nose to tail, which is what makes it read as a bus rather than a truck.
        ExhaustOffsetZ = -5.2f, IntakeOffsetZ = 4.6f, FrontAxleZ = 3.4f, RearAxleZ = -3.2f,
    };

    /// <summary>The same pickup with the turbo taken off, so the two can be run on one lap and the
    /// turbo heard as a mechanism rather than as a setting.</summary>
    public static VehicleProfile DieselPickupNa => DieselPickupLoud with
    {
        Name = "5.9 Cummins 6B pickup, no turbo",
        EngineKey = "diesel_cummins_na",
        Engine = EngineProfile.DieselCumminsNaI6,
        // Measured: nearly eight decibels ABOVE the turbo version, because no turbine is eating the
        // pulse energy any more. Taking a turbo off makes a diesel louder, not quieter.
        SourceLevelDb = 115f,
    };

    /// <summary>And the bus, likewise.</summary>
    public static VehicleProfile SchoolBusNa => SchoolBus with
    {
        Name = "school bus, no turbo",
        EngineKey = "diesel_bus_na",
        Engine = EngineProfile.DieselBusNaI6,
        // Thirteen decibels above the turbo bus, same reason.
        SourceLevelDb = 98f,
    };

    public static VehicleProfile Wagon => new()
    {
        // A long roof and a big rear volume — a wagon booms where a saloon does not.
        Body = VehicleBody.Van,
        Name = "2.5 flat-four wagon",
        EngineKey = "boxer4",
        SourceLevelDb = 118f,
        Engine = EngineProfile.Boxer4,
        Gearbox = Gearbox.SixSpeedSports with { Ratios = new[] { 3.45f, 1.95f, 1.37f, 0.97f, 0.74f }, FinalDrive = 4.11f, ShiftSeconds = 0.35f, UpshiftRpm = 6000f, DownshiftRpm = 1500f, WheelRadiusMetres = 0.32f },
        Tyres = TyreProfile.SportsOnAsphalt,
        MassKg = 1500f, DragArea = 0.72f,
    };

    public static VehicleProfile V10Coupe => new()
    {
        Name = "V10 coupe, side pipes",
        EngineKey = "v10",
        SourceLevelDb = 116f,
        Engine = EngineProfile.V10,
        // An automated single-clutch box, and the shift time is the whole character of it. A manual
        // takes about 280 ms and you hear the revs fall through the gap; this takes 60, which is too
        // short to hear as a gap at all — the note simply steps down, like a hammer. It is the same
        // mechanism as a racing box and it is why one sounds violent where a manual sounds smooth.
        Gearbox = Gearbox.SixSpeedSports with { Ratios = new[] { 2.66f, 1.78f, 1.3f, 1.0f, 0.74f, 0.5f }, FinalDrive = 3.07f, ShiftSeconds = 0.06f, UpshiftRpm = 8200f, DownshiftRpm = 3000f },
        Tyres = TyreProfile.SportsOnAsphalt,
        MassKg = 1560f, DragArea = 0.75f,
        ExhaustOffsetZ = 0.3f, IntakeOffsetZ = 1.3f,
    };

    /// <summary>
    /// A Cup stock car: 1450 kg, no muffler, a four-speed geared for a one-mile oval so top gear runs
    /// out right at the end of the straight. The exhaust leaves through the side of the car just
    /// behind the driver's door rather than out of the back, which is why a stock car going past is
    /// loudest square abeam and not after it has gone.
    /// </summary>
    public static VehicleProfile StockCar => new()
    {
        // A stripped steel shell with side exits hard against it — hollow, and loud with it.
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
            // A race driver never lets it fall out of the power band; there is nothing down there.
            DownshiftRpm = 5200f,
        },
        Tyres = TyreProfile.RaceSlick,
        MassKg = 1450f, DragArea = 0.92f, RollingResistance = 0.011f,
        // Side exit, level with the driver; the airbox faces forward under the windscreen cowl.
        ExhaustOffsetZ = 0.1f, IntakeOffsetZ = 1.1f, FrontAxleZ = 1.4f, RearAxleZ = -1.4f,
    };

    /// <summary>
    /// A three-litre V10 formula car: 620 kg with the driver, seven gears, and a diffuser instead of
    /// a silencer. The pipes exit behind the rear axle pointing back and up, so unlike the stock car
    /// it is loudest going away from you.
    /// </summary>
    public static VehicleProfile FormulaCar => new()
    {
        // No panels worth the name and nothing enclosed at all.
        Body = VehicleBody.OpenWheeler,
        Name = "V10 formula car",
        EngineKey = "f1_v10",
        SourceLevelDb = 133f,
        Engine = EngineProfile.F1V10,
        Gearbox = Gearbox.SixSpeedSports with
        {
            Ratios = new[] { 5.90f, 4.40f, 3.55f, 2.95f, 2.50f, 2.10f, 1.75f },
            // Geared so top gear runs out at 330 km/h right on the limiter, which is what an oval
            // gear set is for. Taller than a road course would use, and the reason the car sits
            // pinned near the top of the range for most of a lap instead of shifting through it.
            FinalDrive = 3.23f,
            WheelRadiusMetres = 0.33f,
            // A seamless-shift box is quicker than a person; the gap is the reason the note steps
            // rather than sweeps.
            ShiftSeconds = 0.05f,
            UpshiftRpm = 15100f,
            DownshiftRpm = 8500f,
        },
        Tyres = TyreProfile.RaceSlick with { SurfaceRoughness = 0.3f, ReferenceDb = 76f, PeakGripG = 4.2f, SquealHz = 690f },
        MassKg = 620f,
        // OVAL TRIM, and it has to be. 1.35 m^2 is a road-course wing package, and with it this car
        // could not reach 230 km/h — the map asked it for 327, so it sat permanently flat out, a
        // hundred short, droning at 10,500 of 15,500 rpm and never once revving out. Ten of the
        // thirty cars on the speedway were doing that. A car set up for a banked oval runs the wings
        // off: about 0.85 here, which puts it at 296 km/h with the crank at 13,450 — its torque peak
        // is 13,500 — so it sits in the meat of the range where a V10 is supposed to live.
        // Measured, not guessed: 1.35 -> 230, 1.10 -> 270, 0.95 -> 285, 0.85 -> 296 km/h.
        DragArea = 0.85f, RollingResistance = 0.014f,
        ExhaustOffsetZ = -1.6f, IntakeOffsetZ = -0.2f, FrontAxleZ = 1.7f, RearAxleZ = -1.6f,
    };

    /// <summary>
    /// The pace/police car: interceptor V8, enough gearing to run with the field, and a siren.
    ///
    /// Heavier and draggier than a stock car because it is a road car with a bar on the roof, so it
    /// tops out under the racers and has to work — which is the point. You hear it coming a long way
    /// off, because the exhaust is open and the cam is enormous.
    /// </summary>
    public static VehicleProfile PoliceCar => new()
    {
        // A stripped interior: no carpet, no trim, a cage and a lot of bare steel.
        Body = VehicleBody.RaceSaloon,
        Name = "Police interceptor",
        EngineKey = "police_v8",
        SourceLevelDb = 132f,
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
        Tyres = TyreProfile.SportsOnAsphalt with { TreadBlocks = 4, SurfaceRoughness = 0.4f, ReferenceDb = 74f },
        MassKg = 1980f, DragArea = 1.05f, RollingResistance = 0.013f,
        ExhaustOffsetZ = -2.2f, IntakeOffsetZ = 1.3f, FrontAxleZ = 1.5f, RearAxleZ = -1.5f,
    };

    public static VehicleProfile GrandTourer => new()
    {
        Name = "V12 grand tourer",
        EngineKey = "v12",
        SourceLevelDb = 128f,
        Engine = EngineProfile.V12,
        Gearbox = Gearbox.SixSpeedSports with { Ratios = new[] { 4.17f, 2.34f, 1.52f, 1.14f, 0.87f, 0.69f }, FinalDrive = 3.46f, ShiftSeconds = 0.25f, UpshiftRpm = 7200f, DownshiftRpm = 1600f },
        Tyres = TyreProfile.SportsOnAsphalt,
        MassKg = 1850f, DragArea = 0.7f,
    };
}

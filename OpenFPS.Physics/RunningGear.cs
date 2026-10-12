namespace OpenFPS.Common;

/// <summary>
/// The running gear of each vehicle preset (see <see cref="ChassisSpec"/>), one real vehicle each,
/// with where every number comes from.
///
/// Axle positions are not repeated here: the wheels stand where the preset's FrontAxleZ and
/// RearAxleZ already put the axles for the sound rig (<see cref="AxleSpec.Z"/>). A turning circle is
/// the published one with the published wheelbase, turned into a lock angle by
/// <see cref="ChassisSpec.LockFromTurningCircle"/>; where none is published the lock stays the
/// 0.61 rad (35 degrees at the wheel) every vehicle had.
///
/// Sources marked "class" are a measured vehicle of the same kind standing in for one with no
/// published figure: most of them from Heydinger et al., "Measured Vehicle Inertial Parameters —
/// NHTSA's Data Through November 1998", SAE 1999-01-1336 (centre of gravity height, front weight
/// share from the centre of gravity's distance behind the front axle, and the yaw inertia over
/// m a b, all measured with a driver aboard). Marked "to confirm" where nothing better was found.
/// </summary>
public static class RunningGear
{
    /// <summary>The steering lock where no turning circle is published, radians.</summary>
    public const float UnpublishedLock = 0.61f;

    private static AxleSpec Front(string tyre, float track, bool driven, BrakeKind brake) => new()
    {
        Tyre = TyreSize.Parse(tyre), TrackMetres = track, Steered = true, Driven = driven, Brake = brake,
    };

    private static AxleSpec Rear(string tyre, float track, bool driven, BrakeKind brake, int tyresPerWheel = 1, float offset = 0f) => new()
    {
        Tyre = TyreSize.Parse(tyre), TrackMetres = track, Driven = driven, Brake = brake,
        TyresPerWheel = tyresPerWheel, TandemOffset = offset,
    };

    private const BrakeKind Disc = BrakeKind.Disc, Drum = BrakeKind.Drum;

    // ── Small and mid-size front-drive cars ─────────────────────────────────────────────────────

    /// <summary>
    /// VW Golf Mk4 1.6 (1998-2003): 195/65R15, track 1513/1494 mm, wheelbase 2511 mm, turning circle
    /// 10.9 m, discs all round, front drive (auto-data.net). No weight split or centre of gravity is
    /// published; class: the 1998 Honda Civic, 60.4 % front, 0.513 m, yaw index 0.95 (Heydinger 1999).
    /// </summary>
    public static ChassisSpec Golf4 => new()
    {
        Axles = new[] { Front("195/65R15", 1.513f, true, Disc), Rear("195/65R15", 1.494f, false, Disc) },
        FrontWeightShare = 0.604f, CentreOfGravityHeightMetres = 0.513f, YawInertiaIndex = 0.95f,
        MaxSteerAngleRad = ChassisSpec.LockFromTurningCircle(10.9f, 2.511f, 1.513f),
    };

    /// <summary>
    /// Toyota Corolla E140 1.8, US (2009-2013), LE: P205/55R16, track 1532/1534 mm, wheelbase 2601 mm,
    /// kerb-to-kerb turning circle 10.85 m, disc front and drum rear, front drive (Toyota 2010 Corolla
    /// brochure; brakes auto123). Weight split and centre of gravity: class, the 1998 Civic (Heydinger 1999).
    /// </summary>
    public static ChassisSpec CorollaE140 => new()
    {
        Axles = new[] { Front("205/55R16", 1.532f, true, Disc), Rear("205/55R16", 1.534f, false, Drum) },
        FrontWeightShare = 0.604f, CentreOfGravityHeightMetres = 0.513f, YawInertiaIndex = 0.95f,
        MaxSteerAngleRad = ChassisSpec.LockFromTurningCircle(10.85f, 2.601f, 1.532f),
    };

    /// <summary>
    /// Toyota Camry XV40 2.5 (2010-2011): P215/60R16, track 1575/1565 mm, wheelbase 2776 mm, turning
    /// circle 11.0 m kerb to kerb, discs, front drive (jbcarpages.com; tyres Edmunds). Class: the 1987
    /// Camry, 60.9 % front, 0.549 m, yaw index 1.13 (Heydinger 1999).
    /// </summary>
    public static ChassisSpec CamryXV40 => new()
    {
        Axles = new[] { Front("215/60R16", 1.575f, true, Disc), Rear("215/60R16", 1.565f, false, Disc) },
        FrontWeightShare = 0.609f, CentreOfGravityHeightMetres = 0.549f, YawInertiaIndex = 1.13f,
        MaxSteerAngleRad = ChassisSpec.LockFromTurningCircle(11.0f, 2.776f, 1.575f),
    };

    /// <summary>
    /// Honda Civic Type R FN2 (2007, Europe): 225/40R18, track 1505/1530 mm, wheelbase 2635 mm, turning
    /// circle 11.2 m, discs, front drive (auto-data.net). Class: the 1998 Civic (Heydinger 1999).
    /// </summary>
    public static ChassisSpec CivicTypeR => new()
    {
        Axles = new[] { Front("225/40R18", 1.505f, true, Disc), Rear("225/40R18", 1.530f, false, Disc) },
        FrontWeightShare = 0.604f, CentreOfGravityHeightMetres = 0.513f, YawInertiaIndex = 0.95f,
        MaxSteerAngleRad = ChassisSpec.LockFromTurningCircle(11.2f, 2.635f, 1.505f),
    };

    /// <summary>
    /// VW Golf GTI Mk7 (2013-2017): 225/45R17, track 1538/1516 mm, wheelbase 2631 mm, turning circle
    /// 10.9 m, discs, front drive (auto-data.net). Class: the 1998 Civic (Heydinger 1999).
    /// </summary>
    public static ChassisSpec GolfGti7 => new()
    {
        Axles = new[] { Front("225/45R17", 1.538f, true, Disc), Rear("225/45R17", 1.516f, false, Disc) },
        FrontWeightShare = 0.604f, CentreOfGravityHeightMetres = 0.513f, YawInertiaIndex = 0.95f,
        MaxSteerAngleRad = ChassisSpec.LockFromTurningCircle(10.9f, 2.631f, 1.538f),
    };

    /// <summary>
    /// Honda Accord V6 sedan (2010): P225/50R17, track 1580/1580 mm, wheelbase 2799 mm, turning circle
    /// 11.5 m kerb to kerb (to confirm: the same release also reads 37.1 ft, 11.3 m), discs, front drive
    /// (Honda, "2010 Honda Accord Specifications and Features"). Class: the 1991 Accord, 60.7 % front,
    /// 0.504 m, yaw index 1.05 (Heydinger 1999).
    /// </summary>
    public static ChassisSpec AccordV6 => new()
    {
        Axles = new[] { Front("225/50R17", 1.580f, true, Disc), Rear("225/50R17", 1.580f, false, Disc) },
        FrontWeightShare = 0.607f, CentreOfGravityHeightMetres = 0.504f, YawInertiaIndex = 1.05f,
        MaxSteerAngleRad = ChassisSpec.LockFromTurningCircle(11.5f, 2.799f, 1.580f),
    };

    // ── Rear-drive saloons, coupes and sports cars ──────────────────────────────────────────────

    /// <summary>
    /// BMW 530i E60 (2004-2007): 225/50R17, track 1558/1582 mm, wheelbase 2888 mm, discs, rear drive
    /// (auto-data.net); turning circle 11.4 m from the E61 Touring (ultimatespecs). Split 50.8/49.2 as
    /// owners report it (5series.net; BMW publishes none). Class: the 1986 BMW 325i, 0.533 m, yaw
    /// index 0.98 (Heydinger 1999).
    /// </summary>
    public static ChassisSpec Bmw530iE60 => new()
    {
        Axles = new[] { Front("225/50R17", 1.558f, false, Disc), Rear("225/50R17", 1.582f, true, Disc) },
        FrontWeightShare = 0.508f, CentreOfGravityHeightMetres = 0.533f, YawInertiaIndex = 0.98f,
        MaxSteerAngleRad = ChassisSpec.LockFromTurningCircle(11.4f, 2.888f, 1.558f),
    };

    /// <summary>
    /// 1970 Chevrolet Chevelle SS 454: F70-14 bias tyres (225/70R14 is the metric equivalent), track
    /// 1524/1519 mm, wheelbase 2845 mm, front disc (Z15) and rear drum, rear drive (1970 Chevrolet
    /// mid-size fact sheet). No turning circle found. Class: the 1978 Dodge Diplomat, a rear-drive V8
    /// sedan of the period, 56.7 % front and 0.543 m (Heydinger 1999).
    /// </summary>
    public static ChassisSpec Chevelle70 => new()
    {
        Axles = new[] { Front("225/70R14", 1.524f, false, Disc), Rear("225/70R14", 1.519f, true, Drum) },
        FrontWeightShare = 0.567f, CentreOfGravityHeightMetres = 0.543f,
        MaxSteerAngleRad = UnpublishedLock,
    };

    /// <summary>
    /// 1969 Dodge Charger R/T 440: F70-14 (225/70R14 metric), track 1511/1504 mm, wheelbase 2972 mm,
    /// 11 inch drums all round, rear drive (1969 Dodge mid-size fact sheet). Class: the 1978 Dodge
    /// Diplomat (Heydinger 1999).
    /// </summary>
    public static ChassisSpec Charger69 => new()
    {
        Axles = new[] { Front("225/70R14", 1.511f, false, Drum), Rear("225/70R14", 1.504f, true, Drum) },
        FrontWeightShare = 0.567f, CentreOfGravityHeightMetres = 0.543f,
        MaxSteerAngleRad = UnpublishedLock,
    };

    /// <summary>
    /// Ford Mustang GT (2011): P235/50R18, track 1582/1598 mm, wheelbase 2720 mm, 54/46 (est.), turning
    /// circle 10.18 m kerb to kerb (est.), discs, rear drive (Ford "2011 Mustang GT Technical
    /// Specifications"). Class: the 1988 Mustang GT, 0.532 m, yaw index 1.11 (Heydinger 1999).
    /// </summary>
    public static ChassisSpec MustangGt11 => new()
    {
        Axles = new[] { Front("235/50R18", 1.582f, false, Disc), Rear("235/50R18", 1.598f, true, Disc) },
        FrontWeightShare = 0.54f, CentreOfGravityHeightMetres = 0.532f, YawInertiaIndex = 1.11f,
        MaxSteerAngleRad = ChassisSpec.LockFromTurningCircle(10.18f, 2.720f, 1.582f),
    };

    /// <summary>
    /// Ferrari 458 Italia: 235/35ZR20 front, 295/35ZR20 rear, track 1672/1606 mm, wheelbase 2650 mm
    /// (owner's manual), 42/58 (sbraceengineering.co.uk), carbon-ceramic discs, rear drive. No turning
    /// circle found. Centre of gravity: class, the C7 Corvette's 0.445 m (Car and Driver, as quoted in
    /// Wikipedia "Automobile handling"; to confirm).
    /// </summary>
    public static ChassisSpec Ferrari458 => new()
    {
        Axles = new[] { Front("235/35ZR20", 1.672f, false, Disc), Rear("295/35ZR20", 1.606f, true, Disc) },
        FrontWeightShare = 0.42f, CentreOfGravityHeightMetres = 0.445f,
        MaxSteerAngleRad = UnpublishedLock,
    };

    /// <summary>
    /// Dodge Viper SRT-10 coupe (2006): P275/35ZR18 front, P345/30ZR19 rear, track 1565/1547 mm,
    /// wheelbase 2510 mm, 49.4/50.6, turning circle 12.34 m kerb to kerb, discs, rear drive (Chrysler
    /// "2006 Viper SRT-10 Specifications"). Class: the 1979 Datsun 280ZX, a front-engined coupe,
    /// 0.489 m (Heydinger 1999).
    /// </summary>
    public static ChassisSpec ViperSrt10 => new()
    {
        Axles = new[] { Front("275/35ZR18", 1.565f, false, Disc), Rear("345/30ZR19", 1.547f, true, Disc) },
        FrontWeightShare = 0.494f, CentreOfGravityHeightMetres = 0.489f,
        MaxSteerAngleRad = ChassisSpec.LockFromTurningCircle(12.34f, 2.510f, 1.565f),
    };

    /// <summary>
    /// Aston Martin DB9 (2004): 235/40ZR19 front, 275/35ZR19 rear, track 1570/1560 mm, "50:50", turning
    /// circle 11.5 m, discs, rear drive through a rear transaxle (press text, netcarshow.com); wheelbase
    /// 2740 mm. Class: the 1979 Datsun 280ZX, 0.489 m (Heydinger 1999).
    /// </summary>
    public static ChassisSpec Db9 => new()
    {
        Axles = new[] { Front("235/40ZR19", 1.570f, false, Disc), Rear("275/35ZR19", 1.560f, true, Disc) },
        FrontWeightShare = 0.50f, CentreOfGravityHeightMetres = 0.489f,
        MaxSteerAngleRad = ChassisSpec.LockFromTurningCircle(11.5f, 2.740f, 1.570f),
    };

    /// <summary>
    /// Dodge Charger Pursuit (2015), V8 rear drive: P225/60R18, track 1611/1620 mm, wheelbase 3052 mm,
    /// 53/47, turning circle 11.5 m kerb to kerb, discs (FCA "2015 Dodge Charger Pursuit
    /// Specifications"). Class: the 1983 Chevrolet Caprice, a full-size rear-drive police-car body,
    /// 0.599 m (Heydinger 1999).
    /// </summary>
    public static ChassisSpec ChargerPursuit => new()
    {
        Axles = new[] { Front("225/60R18", 1.611f, false, Disc), Rear("225/60R18", 1.620f, true, Disc) },
        FrontWeightShare = 0.53f, CentreOfGravityHeightMetres = 0.599f,
        MaxSteerAngleRad = ChassisSpec.LockFromTurningCircle(11.5f, 3.052f, 1.611f),
    };

    // ── All-wheel drive ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Subaru Outback 2.5i (2012), Premium: 225/60R17, track 1549/1549 mm, wheelbase 2741 mm, turning
    /// circle 11.2 m, discs, all-wheel drive (autodetective.com, auto123.com). Class: the 1984 Audi
    /// Quattro 4000, an all-wheel-drive car with its engine ahead of the front axle, 55.4 % front and
    /// 0.506 m (Heydinger 1999).
    /// </summary>
    public static ChassisSpec Outback => new()
    {
        Axles = new[] { Front("225/60R17", 1.549f, true, Disc), Rear("225/60R17", 1.549f, true, Disc) },
        FrontWeightShare = 0.554f, CentreOfGravityHeightMetres = 0.506f,
        MaxSteerAngleRad = ChassisSpec.LockFromTurningCircle(11.2f, 2.741f, 1.549f),
    };

    /// <summary>
    /// Subaru Legacy 2.5i sedan (2012): 205/60R16, track 1565/1575 mm, wheelbase 2751 mm, turning
    /// circle 11.2 m, discs, all-wheel drive (autodetective.com, auto123.com). Class: the 1984 Audi
    /// Quattro 4000 (Heydinger 1999).
    /// </summary>
    public static ChassisSpec Legacy => new()
    {
        Axles = new[] { Front("205/60R16", 1.565f, true, Disc), Rear("205/60R16", 1.575f, true, Disc) },
        FrontWeightShare = 0.554f, CentreOfGravityHeightMetres = 0.506f,
        MaxSteerAngleRad = ChassisSpec.LockFromTurningCircle(11.2f, 2.751f, 1.565f),
    };

    // ── Pickups and vans ────────────────────────────────────────────────────────────────────────
    //
    // The four-by-fours are part-time: on a made road they drive the rear axle.

    /// <summary>
    /// Toyota Hilux 2.8 GD-6 double cab (2016 on): 265/65R17, track 1540/1550 mm, wheelbase 3085 mm,
    /// minimum turning radius 6.4 m at the tyre (a 12.8 m circle), ventilated disc front and drum rear
    /// (Toyota UK Hilux technical specifications, 2021). Class: the 1998 Dodge Dakota, a mid-size
    /// pickup, 59.3 % front and 0.618 m; yaw index 0.89, the 1998 C1500's (Heydinger 1999).
    /// </summary>
    public static ChassisSpec Hilux => new()
    {
        Axles = new[] { Front("265/65R17", 1.540f, false, Disc), Rear("265/65R17", 1.550f, true, Drum) },
        FrontWeightShare = 0.593f, CentreOfGravityHeightMetres = 0.618f, YawInertiaIndex = 0.89f,
        MaxSteerAngleRad = ChassisSpec.LockFromTurningCircle(12.8f, 3.085f, 1.540f),
    };

    /// <summary>
    /// Chevrolet Silverado 1500 5.3 crew cab, 5'8" box, two-wheel drive (2015): P245/70R17, track
    /// 1747/1716 mm, wheelbase 3645 mm, 56/44, turning circle 14.4 m kerb to kerb, discs (GM "2015
    /// Silverado 1500 Specifications"). Class: the 1998 Chevrolet C1500, 0.665 m, yaw index 0.89
    /// (Heydinger 1999).
    /// </summary>
    public static ChassisSpec Silverado1500 => new()
    {
        Axles = new[] { Front("245/70R17", 1.747f, false, Disc), Rear("245/70R17", 1.716f, true, Disc) },
        FrontWeightShare = 0.56f, CentreOfGravityHeightMetres = 0.665f, YawInertiaIndex = 0.89f,
        MaxSteerAngleRad = ChassisSpec.LockFromTurningCircle(14.4f, 3.645f, 1.747f),
    };

    /// <summary>
    /// Ford F-250 HD regular cab, 4x4 (1997, the last of the old body the 7.3 Power Stroke came in):
    /// LT235/85R16, track 1694/1633 mm, wheelbase 3378 mm, turning circle about 14.0 m (46 ft) kerb to
    /// kerb, disc front and drum rear (autodetective.com). Class: the 1985 Chevrolet K-20, a
    /// three-quarter-ton four-by-four, 57.6 % front and 0.747 m; yaw index 0.92, the 1984 F250's
    /// (Heydinger 1999).
    /// </summary>
    public static ChassisSpec F250HD => new()
    {
        Axles = new[] { Front("235/85R16", 1.694f, false, Disc), Rear("235/85R16", 1.633f, true, Drum) },
        FrontWeightShare = 0.576f, CentreOfGravityHeightMetres = 0.747f, YawInertiaIndex = 0.92f,
        MaxSteerAngleRad = ChassisSpec.LockFromTurningCircle(14.0f, 3.378f, 1.694f),
    };

    /// <summary>
    /// Chevrolet Silverado 2500HD Duramax (2011-2014): LT245/75R17 and track 1748/1709 mm (to confirm:
    /// thecarconnection.com, search summary only), discs (carsdirect.com), turning circle 16.7 m on
    /// the 4260 mm long-box wheelbase (auto123.com). Class: the 1985 K-20 (Heydinger 1999).
    /// </summary>
    public static ChassisSpec Silverado2500HD => new()
    {
        Axles = new[] { Front("245/75R17", 1.748f, false, Disc), Rear("245/75R17", 1.709f, true, Disc) },
        FrontWeightShare = 0.576f, CentreOfGravityHeightMetres = 0.747f, YawInertiaIndex = 0.92f,
        MaxSteerAngleRad = ChassisSpec.LockFromTurningCircle(16.7f, 4.260f, 1.748f),
    };

    /// <summary>
    /// Dodge Ram 2500 5.9 Cummins quad cab, short bed, 4x4 (1999): LT245/75R16, track 1750/1727 mm,
    /// wheelbase 3523 mm, turning circle 14.5 m, disc front and drum rear (autodetective.com). Class:
    /// the 1991 Dodge Ram D-150, 59.7 % front and 0.651 m; yaw index 0.92, the 1984 F250's
    /// (Heydinger 1999).
    /// </summary>
    public static ChassisSpec Ram2500 => new()
    {
        Axles = new[] { Front("245/75R16", 1.750f, false, Disc), Rear("245/75R16", 1.727f, true, Drum) },
        FrontWeightShare = 0.597f, CentreOfGravityHeightMetres = 0.651f, YawInertiaIndex = 0.92f,
        MaxSteerAngleRad = ChassisSpec.LockFromTurningCircle(14.5f, 3.523f, 1.750f),
    };

    /// <summary>
    /// Freightliner MT45 walk-in van: 225/70R19.5 on both axles, duals at the rear, hydraulic discs,
    /// rear drive; at gross weight 5,790 lb front and 9,200 lb rear (38.6 %) (Washington State DES
    /// vehicle specification 2453; Morgan Olson MT45/55 sheet). The preset's 7.5 t is a loaded van.
    /// Track not published: the default rule (<see cref="ChassisSpec.DefaultTrackOverWidth"/> of its
    /// 2.4 m body). Centre of gravity: class, a laden full-size van, the 1987 Dodge Ram B-150 with
    /// eight aboard and ballast, 0.903 m; yaw index 0.87, the 1998 Ford Club Wagon's (Heydinger 1999).
    /// </summary>
    public static ChassisSpec Mt45 => new()
    {
        Axles = new[]
        {
            Front("225/70R19.5", 2.4f * ChassisSpec.DefaultTrackOverWidth, false, Disc),
            Rear("225/70R19.5", 2.4f * ChassisSpec.DefaultTrackOverWidth, true, Disc, tyresPerWheel: 2),
        },
        FrontWeightShare = 0.386f, CentreOfGravityHeightMetres = 0.903f, YawInertiaIndex = 0.87f,
        MaxSteerAngleRad = UnpublishedLock,
    };

    /// <summary>
    /// Grumman LLV: P195/75R14, wheelbase 2553 mm (US DOE electric-conversion fact sheet), on a Chevrolet
    /// S-10 chassis, rear drive; disc front and drum rear as the S-10 of its day (inferred). Split
    /// 50.4/49.6, measured on the electric conversion, not the stock van (DOE). Track: the S-10's
    /// 1379 mm (Heydinger 1999, 1986 S-10). Centre of gravity: class, the 1998 Chevrolet Astro, a van
    /// on a light GM chassis, 0.736 m, yaw index 0.99 (Heydinger 1999).
    /// </summary>
    public static ChassisSpec Llv => new()
    {
        Axles = new[] { Front("195/75R14", 1.379f, false, Disc), Rear("195/75R14", 1.379f, true, Drum) },
        FrontWeightShare = 0.504f, CentreOfGravityHeightMetres = 0.736f, YawInertiaIndex = 0.99f,
        MaxSteerAngleRad = UnpublishedLock,
    };

    // ── Heavy vehicles ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Freightliner Cascadia 116 day cab, 6x4: 295/75R22.5 on the steer axle and on duals on the
    /// tandem drive, the tandem axles 51 in (1.30 m) apart, air disc brakes, an estimated 8,912 lb
    /// (4,042 kg) on the steer axle (factory specification proposal, Virginia Sheriffs' Association
    /// bid). The preset's 14 t includes the trailer's share on the fifth wheel over the drive tandem, so
    /// the steer axle keeps its own 4,042 kg: 28.9 % front. Track: not published for the Cascadia; the
    /// Peterbilt heavy-duty chassis, steer 2037 mm and 1862 mm between dual centres (Peterbilt HD Body
    /// Builder Manual, rev. D, table 3-21). Centre of gravity: no tractor figure found; 1.1 m, between
    /// the measured full-size van (0.79 m, Heydinger 1999) and a laden trailer's payload (2.03 m,
    /// Winkler and Ervin, UMTRI-99-19) — to confirm.
    /// </summary>
    public static ChassisSpec Cascadia6x4 => new()
    {
        Axles = new[]
        {
            Front("295/75R22.5", 2.037f, false, Disc),
            Rear("295/75R22.5", 1.862f, true, Disc, tyresPerWheel: 2, offset: 0.648f),
            Rear("295/75R22.5", 1.862f, true, Disc, tyresPerWheel: 2, offset: -0.648f),
        },
        FrontWeightShare = 0.289f, CentreOfGravityHeightMetres = 1.1f,
        MaxSteerAngleRad = UnpublishedLock,
    };

    /// <summary>
    /// Blue Bird Vision school bus, 54 passengers: 11R22.5 (295/80R22.5 metric), duals at the rear,
    /// hydraulic discs, rear drive, 217 in (5512 mm) wheelbase, axle ratings 12,000/21,000 lb (Blue Bird
    /// Vision specification sheet SB-VIS-PP-0512); turning radius 28.9 ft, a 17.6 m circle (search
    /// summary only; to confirm). Split: the axle ratings' 36.4 % front, as buses are specified to
    /// their laden axle loads (inferred). Track: not published; the Peterbilt heavy axles, 2037 mm and
    /// 1862 mm (Peterbilt HD Body Builder Manual). Centre of gravity: none found; 1.1 m, to confirm.
    /// </summary>
    public static ChassisSpec BlueBirdVision => new()
    {
        Axles = new[]
        {
            Front("295/80R22.5", 2.037f, false, Disc),
            Rear("295/80R22.5", 1.862f, true, Disc, tyresPerWheel: 2),
        },
        FrontWeightShare = 0.364f, CentreOfGravityHeightMetres = 1.1f,
        MaxSteerAngleRad = ChassisSpec.LockFromTurningCircle(17.6f, 5.512f, 2.037f),
    };

    /// <summary>
    /// New Flyer Xcelsior XD40: 305/70R22.5, duals at the rear, track 2184 mm front and 1880 mm between
    /// the rear duals' centres, wheelbase 7195 mm, measured kerb weight 8,750 lb front and 18,980 lb
    /// rear (31.6 %), discs, rear engine and drive (Altoona bus test report PTI-BT-R1211-P); turning
    /// radius 44 ft, a 26.8 m circle (search summary only; to confirm). Centre of gravity: none found;
    /// 1.0 m, to confirm.
    /// </summary>
    public static ChassisSpec XcelsiorXD40 => new()
    {
        Axles = new[]
        {
            Front("305/70R22.5", 2.184f, false, Disc),
            Rear("305/70R22.5", 1.880f, true, Disc, tyresPerWheel: 2),
        },
        FrontWeightShare = 0.316f, CentreOfGravityHeightMetres = 1.0f,
        MaxSteerAngleRad = ChassisSpec.LockFromTurningCircle(26.8f, 7.195f, 2.184f),
    };

    // ── Racing cars ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// NASCAR Cup Next Gen (2022 on): Goodyear 365/35R18 on all four, 2794 mm wheelbase, rear drive
    /// through a transaxle, discs (Wikipedia "Next Gen (NASCAR)"; tiretechnologyinternational.com).
    /// Front track 73.81 in (1875 mm) from a search summary only, the rear taken equal; weight split
    /// and centre of gravity not published: 50 % and 0.43 m, to confirm.
    /// </summary>
    public static ChassisSpec NascarNextGen => new()
    {
        Axles = new[] { Front("365/35R18", 1.875f, false, Disc), Rear("365/35R18", 1.875f, true, Disc) },
        FrontWeightShare = 0.50f, CentreOfGravityHeightMetres = 0.43f,
        MaxSteerAngleRad = UnpublishedLock,
    };

    /// <summary>
    /// Ferrari F2004: track 1470/1405 mm, wheelbase 3050 mm (Ferrari technical specification, as at
    /// italiaspeed.com), rear drive, carbon discs. No tyre size code is published: the 2004 FIA
    /// Technical Regulations (art. 12.4) allow 305-355 mm wide at the front and 365-380 mm at the rear
    /// on 13 in rims, at most 660 mm across, so 305/54R13 and 375/44R13 are those widths at that
    /// diameter. Weight split not found, 45 % to confirm; centre of gravity about 0.25 m (Wikipedia,
    /// uncited; to confirm).
    /// </summary>
    public static ChassisSpec F2004 => new()
    {
        Axles = new[] { Front("305/54R13", 1.470f, false, Disc), Rear("375/44R13", 1.405f, true, Disc) },
        FrontWeightShare = 0.45f, CentreOfGravityHeightMetres = 0.25f,
        MaxSteerAngleRad = UnpublishedLock,
    };

    // ── Motorcycles ─────────────────────────────────────────────────────────────────────────────
    //
    // One wheel at each end on the centreline, so no load moves across an axle: the bike leans
    // instead. Centre of gravity: Sharp's measured machine with its rider, 0.616 m (Sharp, "The
    // Stability and Control of Motorcycles", J. Mech. Eng. Sci. 13(5), 1971, appendix 3).

    /// <summary>Yamaha YZF-R1 (2009): 120/70ZR17 and 190/55ZR17, 1415 mm wheelbase (Yamaha); 52.4/47.6
    /// (search summary only; to confirm).</summary>
    public static ChassisSpec YamahaR1 => new()
    {
        Axles = new[] { Front("120/70ZR17", 0f, false, Disc), Rear("190/55ZR17", 0f, true, Disc) },
        FrontWeightShare = 0.524f, CentreOfGravityHeightMetres = 0.616f,
        MaxSteerAngleRad = UnpublishedLock,
    };

    /// <summary>Harley-Davidson Road King (2021): 130/70B18 and 180/55B18, 1625 mm wheelbase (H-D
    /// media kit). Weight split not found: 50 %, to confirm.</summary>
    public static ChassisSpec RoadKing => new()
    {
        Axles = new[] { Front("130/70B18", 0f, false, Disc), Rear("180/55B18", 0f, true, Disc) },
        FrontWeightShare = 0.50f, CentreOfGravityHeightMetres = 0.616f,
        MaxSteerAngleRad = UnpublishedLock,
    };

    /// <summary>Honda CRF450R (2021): 80/100-21 and 120/80-19, 1481 mm wheelbase (Honda). Weight split
    /// not found: 50 %, to confirm.</summary>
    public static ChassisSpec Crf450 => new()
    {
        Axles = new[] { Front("80/100-21", 0f, false, Disc), Rear("120/80-19", 0f, true, Disc) },
        FrontWeightShare = 0.50f, CentreOfGravityHeightMetres = 0.616f,
        MaxSteerAngleRad = UnpublishedLock,
    };
}

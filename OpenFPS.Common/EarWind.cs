using System.Numerics;

namespace OpenFPS.Common;

/// <summary>
/// Where the listener is, for the wind at their ears: what the game knows each frame.
/// </summary>
/// <param name="Head">Map position of the head (x east, y up, z north).</param>
/// <param name="HeightMetres">How far the head is above the ground under it, for the log law.</param>
/// <param name="Velocity">How the head moves over the ground, m/s, east and north: your own walk, or
/// the vehicle you are in.</param>
/// <param name="FacingDegrees">The way the face points, degrees clockwise from north.</param>
/// <param name="Exposure">How much of the moving air reaches the ears here, 0 (indoors) to 1 (open
/// ground): one minus the place's enclosure. It scales your own movement too (see EarWind.Relative).</param>
/// <param name="Cabin">How the head is shielded from the air it moves through: <see cref="EarCover.None"/>
/// on foot or on a bicycle, a helmet on a motorcycle, a cabin with its windows open some of the way.</param>
/// <param name="WindowsOpen">In a cabin, how far the windows are down, 0 to 1.</param>
public readonly record struct EarWindListener(Vector3 Head, float HeightMetres, Vector2 Velocity, float FacingDegrees,
                                              float Exposure, EarCover Cabin = EarCover.None, float WindowsOpen = 0f);

/// <summary>What is between the ear and the moving air.</summary>
public enum EarCover { None, Helmet, Cabin }

/// <summary>What the wind does at each ear at a moment.</summary>
/// <param name="MeanSpeed">The wind past the head without the gusts, m/s: the weather's mean at head
/// height, minus your own movement. What the level is declared from.</param>
/// <param name="Speed">The same with the gust in it, now.</param>
/// <param name="FromDegrees">Where the air comes from relative to the face: 0 ahead, 90 from the
/// right, 180 from behind, 270 from the left.</param>
/// <param name="LeftDb">Left ear, dB SPL.</param>
/// <param name="RightDb">Right ear, dB SPL.</param>
/// <param name="KneeHz">Where the spectrum turns down.</param>
/// <param name="DeclaredDb">The level the loudness law places, dB SPL: the ungusted speed at grazing
/// incidence, under whatever covers the ear.</param>
public readonly record struct EarWindAtEars(float MeanSpeed, float Speed, float FromDegrees,
                                            float LeftDb, float RightDb, float KneeHz, float DeclaredDb);

/// <summary>
/// The wind in your ears: the noise turbulent air makes as it flows over the head and the pinna.
///
/// It is not sound arriving from anywhere. It is the pressure under the eddies that separate off the
/// edges of the ear and roll past the ear canal, so it is made AT each ear, by each ear's own flow,
/// and the two ears hear unrelated noise: turbulent pressure loses its correlation within a
/// centimetre or so (Corcos 1963/64, as hearing-aid wind studies use it), against eighteen
/// centimetres between the ears. It gets no reverb and no head-related filtering, because the head is
/// not between it and the ear: it is the head.
///
/// THE NUMBERS, all published, none tuned:
///
/// LEVEL against speed. Seidman et al. 2017 (Otolaryngol Head Neck Surg 157(5):848-852, a cyclist
/// manikin in a wind tunnel, "microphones attached near the ears") measured 84.9 dB at 10 mph
/// (4.47 m/s), rising with speed to 120.3 dB at 60 mph (26.8 m/s): 45 dB per decade. Flow-noise
/// theory gives 40 (the mean square pressure growing as U^4; the hearing-aid review by Korhonen
/// 2021, Semin Hear 42(3):248-259, states it as 12 dB per doubling). The slope here is 43 dB per
/// decade of speed.
///
/// THOSE ARE MICROPHONES IN THE FLOW, NOT EARDRUMS. A microphone on the outside of the ear makes its
/// own flow noise; the ear canal sits in the concha, behind the tragus, out of the stream. The one
/// published comparison at the same place and speeds: a microphone in the ear canal measured 14 to
/// 19 dB less wind noise overall than one on top of the pinna, at 2 to 8 m/s (Groth 2020, GN Hearing,
/// as Korhonen 2021 reports it; in-the-canal hearing aids are also the quietest in Zakis 2011, JASA
/// 129(6):3897-3907). So the eardrum here is <see cref="OpenEarDb"/>, the middle of that, under the
/// in-the-flow law: 68.5 dB at 4.47 m/s, nearly all of it under 300 Hz (about 57 dB(A)); 53 dB at
/// 2 m/s (33 dB(A)); 47 dB at a real walk's 1.4 m/s (22 dB(A), about nothing). Below 2 m/s nothing is
/// measured and the slope is carried on.
///
/// DIRECTION. Chung, Mongeau and McKibben 2009 (JASA 125(4):2243-2259) on a manikin: the noise was
/// lowest with the microphone facing upstream, higher facing downstream, highest with the flow
/// grazing it (wind from ahead or behind); Seidman found the ear turned 90 degrees AWAY from the wind,
/// in the head's wake, the loudest of all; Chung, McKibben and Mongeau 2010 (JASA 127(4):2529-2542)
/// found in-the-ear devices loudest with the wind from behind and the far side (190 to 250 degrees).
/// Zakis 2011 measured up to 12 dB between microphones on one device. So the ear the wind blows
/// straight into is 8 dB down, the ear in the wake 1.5 dB up, and wind from behind 1.5 dB up on both
/// (the back of the pinna turns the flow into the concha). With the wind from ahead or behind the two
/// ears match, as Korhonen reports; with it from the side they differ by nine and a half.
///
/// SPECTRUM. Flat below a knee and falling at 26 dB per octave above it, the knee near 300 Hz
/// (Dillon, Roe and Katsch 1999, Wuttke 1991, Raspet et al. 2006, as Korhonen quotes them), with
/// energy spreading upward as the speed rises. A knee set by the flow is a Strouhal frequency, so it
/// moves in proportion to the speed: 300 Hz at 5 m/s. Below 20 Hz it is pressure nobody hears and
/// headroom nobody gets back, so it is filtered out.
/// </summary>
public static class EarWind
{
    /// <summary>The level at <see cref="ReferenceSpeed"/>, grazing incidence, at a microphone in the
    /// flow beside the ear, dB SPL (Seidman 2017).</summary>
    public const float ReferenceDb = 85f;
    /// <summary>The ear canal against a microphone on top of the pinna: 14 to 19 dB less (Groth 2020,
    /// via Korhonen 2021). The middle of it.</summary>
    public const float OpenEarDb = -16.5f;
    /// <summary>10 mph, m/s.</summary>
    public const float ReferenceSpeed = 4.47f;
    /// <summary>dB per decade of speed: between flow-noise theory's 40 and Seidman's measured 45.</summary>
    public const float DbPerDecade = 43f;

    /// <summary>How much quieter the ear the wind blows straight into is, dB.</summary>
    public const float WindwardDb = 8f;
    /// <summary>How much louder the ear in the head's wake is, dB.</summary>
    public const float LeeDb = 1.5f;
    /// <summary>How much louder both are with the wind from behind, dB.</summary>
    public const float FromBehindDb = 1.5f;

    /// <summary>The knee at 1 m/s, Hz: 300 Hz at 5 m/s.</summary>
    public const float KneeHzPerMetrePerSecond = 60f;
    public const float MinKneeHz = 40f, MaxKneeHz = 2500f;

    /// <summary>A full-face motorcycle helmet over the ear. Riders are reported at 105 to 110 dB at the
    /// ear at 100 km/h under one (McCombe 1995; Jordan et al. 2004, figures from memory, not checked
    /// against the papers); 12 dB under the bare ear is an assumption.</summary>
    public const float HelmetDb = -12f;

    /// <summary>The air reaching an occupant's head through an open side window, as a share of the
    /// car's speed through the air, with the window right down. AN ASSUMPTION, not a measurement: the
    /// flow inside a cabin is a recirculating eddy driven by the opening's shear layer, and a quarter
    /// puts 100 km/h with the windows down at about 77 dB at the ear.</summary>
    public const float CabinFlowShare = 0.25f;

    /// <summary>A real walk, m/s: what people walk at, and what holding the walk key stands for.</summary>
    public const float RealWalkMetresPerSecond = 1.4f;

    /// <summary>
    /// How much of your own speed on foot counts as air past your ears: a real walk's share of the
    /// game's walk, 1.4 / 4.5.
    ///
    /// A DECISION, said here so it is not taken for physics. The game's walk is 4.5 m/s
    /// (PhysicsConstants.WalkSpeed), a brisk jog, standing in for walking until somebody decides to
    /// slow it. Taken literally, the law above puts a jog through still air at 67 dB at the eardrum,
    /// about 55 dB(A) of low rumble, which the loudness law plays at −26 LUFS: as loud as the busy street
    /// the mix is set for, on every walk. Heard as the walk it stands for (1.4 m/s) it is 47 dB, 22
    /// dB(A), and plays at about −37 LUFS, nearly all of it under 100 Hz (−61 dBFS A-weighted): faint,
    /// which is what the measurements give for walking.
    /// Running counts the same way (7.2 m/s is heard as 2.2). Vehicles count at their real speed.
    /// If the walk is ever brought down to a real walk, this goes to one.
    /// </summary>
    public static float OnFootShare => RealWalkMetresPerSecond / PhysicsConstants.WalkSpeed;

    /// <summary>Below this the air is still, as far as an ear is concerned, m/s.</summary>
    public const float StillAir = 0.05f;

    /// <summary>The level at the eardrum for a wind past the head, grazing incidence, dB SPL.</summary>
    public static float GrazingDb(float speed)
        => ReferenceDb + OpenEarDb + DbPerDecade * MathF.Log10(MathF.Max(StillAir, speed) / ReferenceSpeed);

    /// <summary>Where the spectrum turns down for a wind past the head, Hz.</summary>
    public static float KneeHz(float speed) => Math.Clamp(KneeHzPerMetrePerSecond * speed, MinKneeHz, MaxKneeHz);

    /// <summary>
    /// What an ear adds for the direction of the wind, dB. <paramref name="fromDegrees"/> is where the
    /// air comes from relative to the face (0 ahead, 90 right); <paramref name="rightEar"/> picks the ear.
    /// </summary>
    public static float DirectionDb(float fromDegrees, bool rightEar)
    {
        float a = fromDegrees * MathF.PI / 180f;
        // How squarely the wind blows into this ear: 1 straight in, -1 from the far side.
        float into = rightEar ? MathF.Sin(a) : -MathF.Sin(a);
        float behind = MathF.Max(0f, -MathF.Cos(a));
        float windward = MathF.Max(0f, into), lee = MathF.Max(0f, -into);
        return -WindwardDb * windward * windward + LeeDb * lee * lee + FromBehindDb * behind;
    }

    /// <summary>The direction term averaged over every direction (energy mean): what an ear hears of
    /// air that is going round in circles, as it does inside a car with its windows down.</summary>
    public static float DiffuseDb(bool rightEar)
    {
        double e = 0;
        for (int i = 0; i < 360; i++) e += Math.Pow(10, DirectionDb(i, rightEar) / 10.0);
        return (float)(10 * Math.Log10(e / 360));
    }

    private static readonly float Diffuse = DiffuseDb(true);

    /// <summary>
    /// The air moving past the head, m/s east and north: the weather's wind where the head is (the
    /// field's speed at head height, along the weather's direction) minus the head's own movement,
    /// the whole of it scaled by how exposed the place is, and then whatever covers the ear.
    ///
    /// Exposure scales the whole of it, your own movement included, so indoors there is no wind at
    /// the ears however you move, and among tall buildings less. That is a decision, not physics: the
    /// air in a room is still, and walking through it moves it past your ears just as outside. But
    /// nobody notices their own walk as wind in a room, and it would be in the ears on every walk
    /// through every building.
    /// </summary>
    public static Vector2 Relative(Vector2 air, in EarWindListener l)
    {
        // On foot, your movement counts as the walk it stands for (OnFootShare).
        Vector2 own = l.Cabin == EarCover.None ? l.Velocity * OnFootShare : l.Velocity;
        Vector2 rel = (air - own) * Math.Clamp(l.Exposure, 0f, 1f);
        if (l.Cabin == EarCover.Cabin) rel *= CabinFlowShare * Math.Clamp(l.WindowsOpen, 0f, 1f);
        return rel;
    }

    /// <summary>
    /// What each ear hears now, from the weather and where the listener is.
    /// </summary>
    public static EarWindAtEars Hear(WindWeather weather, in EarWindListener l, double seconds)
    {
        var air = weather.At(seconds);
        float height = MathF.Max(0.5f, l.HeightMetres);
        var (dx, dz) = air.Downwind;
        Vector2 dir = new(dx, dz);
        // The mean at head height, and the same with the gust where the head is.
        Vector2 meanAir = dir * WindField.MeanAt(air.Speed, height);
        Vector2 gustAir = dir * WindField.SpeedAt(weather, l.Head.X, height, l.Head.Z, seconds);
        Vector2 mean = Relative(meanAir, l), now = Relative(gustAir, l);
        return Ears(mean.Length(), now, l.FacingDegrees, l.Cabin);
    }

    /// <summary>The ears' levels for a relative wind <paramref name="now"/> (the way the air moves past
    /// the head, east and north) whose ungusted speed is <paramref name="meanSpeed"/>.</summary>
    public static EarWindAtEars Ears(float meanSpeed, Vector2 now, float facingDegrees, EarCover cover = EarCover.None)
    {
        float speed = now.Length();
        float from = 0f;
        if (speed > 1e-4f)
        {
            // The air moves along `now`; it comes FROM the opposite way.
            float bearing = MathF.Atan2(-now.X, -now.Y) * 180f / MathF.PI;
            from = Wrap(bearing - facingDegrees);
        }
        float covered = cover == EarCover.Helmet ? HelmetDb : 0f;
        float level = GrazingDb(speed) + covered;
        float left, right;
        if (cover == EarCover.Cabin) { left = level + Diffuse; right = level + Diffuse; }
        else { left = level + DirectionDb(from, false); right = level + DirectionDb(from, true); }
        return new EarWindAtEars(meanSpeed, speed, from, left, right, KneeHz(speed), GrazingDb(meanSpeed) + covered);
    }

    /// <summary>
    /// How loud an ear's level plays, dBFS RMS, under the loudness law.
    ///
    /// The weather's level is a source level, so it goes through the same compression as every other
    /// source: a storm is placed against a breeze the way a lorry is against a car. It lands the
    /// shared headroom (<see cref="VehicleProfile.PeakHeadroomDb"/>) under that, as every synthesized
    /// voice's mean does, so its buffets have the same room as an engine's pulses. What moves ON it —
    /// a gust swelling, the ear that faces the wind against the one in its wake — is the sound's own
    /// shape and is kept as it is, the way a fire's crackle is kept over its declared level.
    /// <paramref name="declaredDb"/> is <see cref="EarWindAtEars.DeclaredDb"/>, <paramref name="earDb"/>
    /// the ear's level now.
    /// </summary>
    public static float RenderedDb(float declaredDb, float earDb)
    {
        float shape = Math.Clamp(earDb - declaredDb, -40f, 15f);
        return HeldDb(PlacedDb(declaredDb)) + shape;
    }

    /// <summary>Where the loudness law puts a declared ear level, dBFS RMS, before the ceiling.</summary>
    public static float PlacedDb(float declaredDb)
        => (declaredDb - Loudness.RenderCeilingDb) * Loudness.DynamicRangeCompression - VehicleProfile.PeakHeadroomDb;

    /// <summary>
    /// The loudest the weather's level is ever placed at, dBFS RMS. A storm in the ears is loud, and it
    /// is loud here; but its buffets stand sixteen decibels over its mean, and placed by the law alone a
    /// gale past the head would run the master limiter on every buffet and pull the whole world down
    /// with it. Above <see cref="SoftKneeDb"/> the placed level bends over toward this and never
    /// reaches it; the two ears' difference and the gusts ride on top unchanged. A deliberate departure
    /// from the law, said here so it is not mistaken for physics: by the law alone 15 m/s past the head
    /// is −25.4 dBFS and 25 m/s −21.1; held, they are −25.6 and −23.2.
    /// </summary>
    public const float CeilingDb = -21f;
    public const float SoftKneeDb = -27f;

    /// <summary>A placed level held under <see cref="CeilingDb"/>: unchanged up to the knee, then
    /// bending over smoothly (slope one at the knee, none at the ceiling).</summary>
    public static float HeldDb(float placedDb)
    {
        if (!(placedDb > SoftKneeDb)) return placedDb;
        float over = placedDb - SoftKneeDb;
        float room = CeilingDb - SoftKneeDb;
        return SoftKneeDb + room * (1f - MathF.Exp(-over / room));
    }

    private static float Wrap(float deg)
    {
        deg %= 360f;
        return deg < 0 ? deg + 360f : deg;
    }
}

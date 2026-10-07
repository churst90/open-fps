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
/// The wind in your ears: the pressure under the eddies that roll past each ear canal. It is made at
/// each ear, and the two hear unrelated noise (turbulent pressure decorrelates within a centimetre or
/// so: Corcos 1963/64, against 18 cm between the ears). No reverb and no HRTF: the head is not between
/// it and the ear, it is the head. Every number is published, none tuned:
///
/// Level. Seidman et al. 2017 (Otolaryngol Head Neck Surg 157(5):848-852, a cyclist manikin, microphones
/// near the ears) measured 84.9 dB at 10 mph (4.47 m/s) to 120.3 dB at 60 mph: 45 dB a decade; flow-noise
/// theory gives 40 (12 dB per doubling, Korhonen 2021, Semin Hear 42(3):248-259). Here 43.
///
/// Those are microphones in the flow, not eardrums. A microphone in the ear canal measured 14 to 19 dB
/// less than one on top of the pinna at 2 to 8 m/s (Groth 2020, GN Hearing, via Korhonen 2021; in-canal
/// aids are quietest in Zakis 2011, JASA 129(6):3897-3907 too), so the eardrum is <see cref="OpenEarDb"/>
/// under that law: 68.5 dB at 4.47 m/s (about 57 dB(A), nearly all under 300 Hz), 53 dB at 2 m/s
/// (33 dB(A)), 47 dB at a real walk's 1.4 m/s (22 dB(A)). Below 2 m/s nothing is measured; the slope is
/// carried on.
///
/// Direction. Lowest facing upstream, highest grazing (Chung, Mongeau and McKibben 2009, JASA
/// 125(4):2243-2259); the ear in the head's wake loudest of all (Seidman); in-the-ear devices loudest
/// with wind from behind and the far side, 190-250 degrees (Chung, McKibben and Mongeau 2010, JASA
/// 127(4):2529-2542); up to 12 dB between microphones on one device (Zakis 2011). So the windward ear is
/// 8 dB down, the lee ear 1.5 up, wind from behind 1.5 up on both: ears match with wind ahead or behind
/// and differ by 9.5 dB from the side.
///
/// Spectrum. Flat below a knee near 300 Hz, falling 26 dB an octave above it (Dillon, Roe and Katsch
/// 1999; Wuttke 1991; Raspet et al. 2006, as Korhonen quotes them). The knee is a Strouhal frequency, so
/// it moves with the speed: 300 Hz at 5 m/s. Below 20 Hz it is filtered out: pressure nobody hears and
/// headroom nobody gets back.
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
    /// How much of your own speed on foot counts as air past your ears: 1.4 / 4.5. A decision, not
    /// physics: the game's walk (PhysicsConstants.WalkSpeed) is a jog, and taken literally it is 67 dB at
    /// the eardrum (55 dB(A)), played at −26 LUFS, as loud as the busy street, on every walk. As the walk
    /// it stands for it is 47 dB, about −37 LUFS, nearly all under 100 Hz (−61 dBFS A-weighted): faint,
    /// as measured for walking. Running
    /// counts the same (7.2 m/s heard as 2.2); vehicles at their real speed. If the walk is ever brought
    /// down to a real walk, this goes to one.
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
    /// The air moving past the head, m/s east and north: the weather's wind at head height minus the
    /// head's own movement, scaled by exposure, then by whatever covers the ear. Exposure scales your
    /// own movement too, so indoors there is no wind at the ears however you move. A decision, not
    /// physics: nobody notices their own walk as wind in a room, and it would be on every walk indoors.
    /// </summary>
    public static Vector2 Relative(Vector2 air, in EarWindListener l)
    {
        Vector2 own = l.Cabin == EarCover.None ? l.Velocity * OnFootShare : l.Velocity;
        Vector2 rel = (air - own) * Math.Clamp(l.Exposure, 0f, 1f);
        if (l.Cabin == EarCover.Cabin) rel *= CabinFlowShare * Math.Clamp(l.WindowsOpen, 0f, 1f);
        return rel;
    }

    /// <summary>What each ear hears now, from the weather and where the listener is.</summary>
    public static EarWindAtEars Hear(WindWeather weather, in EarWindListener l, double seconds)
    {
        var air = weather.At(seconds);
        float height = MathF.Max(0.5f, l.HeightMetres);
        var (dx, dz) = air.Downwind;
        Vector2 dir = new(dx, dz);
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
    /// How loud an ear's level plays, dBFS RMS, under the loudness law. The declared level is compressed
    /// like any source's and lands the shared headroom (<see cref="VehicleProfile.PeakHeadroomDb"/>) under
    /// it; what moves on it (a gust, windward ear against lee) is the sound's own shape and is kept, as a
    /// fire's crackle is. <paramref name="declaredDb"/> is <see cref="EarWindAtEars.DeclaredDb"/>.
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
    /// The loudest the weather's level is ever placed at, dBFS RMS. Its buffets stand 16 dB over its
    /// mean, and placed by the law alone a gale would run the master limiter on every buffet and pull the
    /// world down with it; above <see cref="SoftKneeDb"/> it bends toward this, gusts and ear difference
    /// riding on top. A deliberate departure from the law: 15 m/s is −25.4 dBFS by the law and 25 m/s
    /// −21.1; held, −25.6 and −23.2.
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

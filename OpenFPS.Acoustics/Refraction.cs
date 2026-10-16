namespace OpenFPS.Common;

/// <summary>
/// Sound bent by the air: the wind and the temperature change with height, so the speed of sound does, and a ray
/// curves toward the slower air. Bent up (upwind, a sunny afternoon, thunder kilometres up) the lowest ray from each end
/// grazes the ground somewhere, and past that a listener is in shadow; bent down (downwind, a clear night, warm air over
/// cold water) both legs of the ground's bounce arc down, and the bounce comes in as if both ends stood higher. The air's
/// stir scatters some sound into a shadow and makes a far sound rise and fall. One rule for every sound and for thunder.
/// </summary>
/// <remarks>
/// <para>The effective speed of sound along the way the sound goes is Harmonoise's lin-log profile,
/// c(z) = c0 + Log ln(1 + z/z0) + Linear z: the wind's log law and the temperature's two terms from Monin-Obukhov
/// similarity (Nota, van Leeuwen, Barelds et al., "Harmonoise: simplification of comprehensive source and propagation
/// models into an accurate and practicable engineering method", Inter-noise 2004, eqs. 4 and 5; Heimann and Salomons,
/// Applied Acoustics 65, 2004). Harmonoise turns it into one curvature for each path, the linear term's and the log
/// term's added (eqs. 6 and 7), the log term's from the height its ray climbs to over the distance. That relation is
/// the log term's slope, 4/π of it, read at the ray's top; here the top is never below either end, so a source
/// kilometres up (thunder) reads the profile where it is.</para>
///
/// <para>Bent up, the shadow's edge is where the lowest ray from each end grazes the ground, √(2Rh) off (Fleagle, J.
/// Meteorol. 6, 1949). Past it the sound that reaches the ear has crept round the curve: in the curved-ground analogy
/// (Almgren, J. Sound Vib. 118, 1987, which Harmonoise adopts) refraction of radius R is a homogeneous air over a ground
/// curved to R, and round a rigid curved surface the field dies as exp(-Im τ1 (k / 2R²)^(1/3) x), τ1 the first zero of
/// Ai' turned by π/3 (Keller's creeping waves; Pierce, "Acoustics", 1981, ch. 9; measured on a curved surface by Berry
/// and Daigle, J. Acoust. Soc. Am. 83, 1988): more for the treble than the bass. The air's stir scatters sound down
/// into the shadow and stops it 20 to 30 dB down (Embleton, "Tutorial on sound propagation outdoors", J. Acoust. Soc.
/// Am. 100, 1996), the less the stirrier the air.</para>
/// </remarks>
public static class Refraction
{
    /// <summary>The effective speed of sound's profile along one path, m/s: c(z) = c0 + Log ln(1 + z/z0) + Linear z,
    /// z0 the ground's roughness. Positive terms bend sound down.</summary>
    public readonly record struct Profile(float Log, float Linear);

    /// <summary>A figure for each of the voice's bands (AcousticBands): dB, or nepers, or metres, as said.</summary>
    public readonly record struct Bands(float Low, float Mid, float High)
    {
        public static readonly Bands None = new(0f, 0f, 0f);
    }

    /// <summary>
    /// Im τ1 for a rigid surface: the first zero of Ai'(-τ), 1.01879, times sin(π/3). A soft ground's (the
    /// pressure-release limit, Ai's first zero 2.33811) is twice as much; grass at the frequencies that carry far is
    /// nearer rigid, so the shadow is taken at its shallowest.
    /// </summary>
    public const float CreepingDecay = 1.01879f * 0.8660254f;

    /// <summary>
    /// The deepest a shadow goes, dB, in the stillest air and in the stirriest (Embleton 1996: the air's stir scatters
    /// sound into it and holds it 20 to 30 dB down). Scattered sound goes as the variance of the refractive index
    /// (single scattering), so the floor is read off that: 30 dB in a calm (<see cref="CalmStirVariance"/>), ten less
    /// for each tenfold stir, never under 20.
    /// </summary>
    public const float DeepestShadowDb = 30f, ShallowestFloorDb = 20f;

    // ── The air's stir ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The variance of the refractive index, μ², of air whose wind swings <paramref name="along"/>,
    /// <paramref name="across"/> and <paramref name="up"/> m/s and whose temperature swings
    /// <paramref name="temperatureSigmaK"/> K (one standard deviation each; the surface layer's, AirLayers.Layer.EddiesAt),
    /// at <paramref name="temperatureC"/>: σ_T²/(4T²) + σ_v²/c², the Gaussian model's effective variance (Ostashev and
    /// Wilson, "Acoustics in Moving Inhomogeneous Media", 2nd ed. 2015, ch. 6; Salomons, "Computational Atmospheric
    /// Acoustics", 2001, appendix I). The model takes the turbulence as isotropic, so σ_v² is the mean of the three
    /// components' variances: the same turbulent energy.
    /// </summary>
    public static float StirVariance(float along, float across, float up, float temperatureSigmaK, float temperatureC, float speedOfSound)
    {
        float velocity = (along * along + across * across + up * up) / 3f;
        float st = temperatureSigmaK / (2f * (temperatureC + 273.15f));
        return velocity / (speedOfSound * speedOfSound) + st * st;
    }

    /// <summary>The temperature's fluctuation, K, and the wind's turbulent velocity in still air and per m/s of wind,
    /// doubled at full gustiness (a storm's 18 m/s gusting gives about 1.2 m/s, the order Ostashev and Wilson 2015,
    /// ch. 6, give for a windy day). The thunder's judgement since 2026-09, which it keeps (Thunder.TurbulenceVariance):
    /// its sound was approved with it, and its way runs kilometres up, out of the surface layer.</summary>
    public const float TemperatureSigmaK = 0.5f, CalmVelocitySigma = 0.2f, TurbulenceIntensity = 0.03f;

    /// <summary>
    /// The thunder's judged μ²: the wind's turbulent velocity and the temperature's fluctuation, each against the speed
    /// of sound. <paramref name="windSpeed"/> the mean wind, m/s; <paramref name="gustiness"/> the server's 0..1.
    /// </summary>
    public static float JudgedStirVariance(float windSpeed, float gustiness, float temperatureC, float speedOfSound)
    {
        float sv = CalmVelocitySigma + TurbulenceIntensity * windSpeed * (1f + Math.Clamp(gustiness, 0f, 1f));
        float st = TemperatureSigmaK / (2f * (temperatureC + 273.15f));
        return sv * sv / (speedOfSound * speedOfSound) + st * st;
    }

    /// <summary>The μ² the shadow's floor is measured from, 30 dB down: the judged stir of still air on the standard
    /// day (Air.Standard), 1.1e-6, at the bottom of the 1e-6 to 1e-5 measured near the ground (Daigle, Piercy and
    /// Embleton, J. Acoust. Soc. Am. 74, 1983). Still air off the surface layer stirs less and keeps the 30.</summary>
    public static readonly float CalmStirVariance = JudgedStirVariance(0f, 0f, Air.Standard.TemperatureC, Air.Standard.SpeedOfSound);

    // ── The curvature ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The curvature of the sound's way over a path, per metre: positive bent down, negative bent up, zero straight.
    /// Harmonoise's two radii (Nota et al. 2004, eq. 6), R_lin = c0 / Linear held to at least half the distance, and
    /// the log term's 1/R = (4/π) Log / (c0 h), h the height its ray climbs to, D √(|Log| / 2π c0) (eq. 6's own) or the
    /// higher end; added as curvatures (eq. 7).
    /// </summary>
    public static float Curvature(in Profile p, float distance, float sourceHeight, float earHeight, float speedOfSound)
    {
        float d = MathF.Max(1f, distance), c = speedOfSound;
        float k = 0f;
        if (p.Linear != 0f)
        {
            float r = MathF.Sqrt(c / p.Linear * (c / p.Linear) + 0.25f * d * d);
            k += MathF.Sign(p.Linear) / r;
        }
        if (p.Log != 0f)
        {
            float top = MathF.Max(d * MathF.Sqrt(MathF.Abs(p.Log) / (2f * MathF.PI * c)), MathF.Max(sourceHeight, earHeight));
            k += 4f / MathF.PI * p.Log / (c * MathF.Max(0.01f, top));
        }
        return k;
    }

    /// <summary>
    /// How far off a sound bent up with <paramref name="curvature"/> (negative) still reaches the ground between source
    /// and ear, metres: the lowest ray from each grazes it √(2Rh) off (Fleagle 1949). Infinity when it is not bent up.
    /// </summary>
    public static float ShadowEdge(float curvature, float sourceHeight, float earHeight)
    {
        if (!(curvature < 0f)) return float.PositiveInfinity;
        float twiceR = 2f / -curvature;
        return MathF.Sqrt(twiceR) * (MathF.Sqrt(MathF.Max(0.01f, sourceHeight)) + MathF.Sqrt(MathF.Max(0.01f, earHeight)));
    }

    /// <summary>
    /// What a shadow takes from each band, dB (zero short of its edge): the creeping wave's decay past the edge, at each
    /// band's centre, held to the floor the air's stir <paramref name="stirVariance"/> leaves.
    /// </summary>
    public static Bands Shadow(float distance, float sourceHeight, float earHeight, float curvature, float stirVariance, float speedOfSound)
    {
        float edge = ShadowEdge(curvature, sourceHeight, earHeight);
        if (!(distance > edge)) return Bands.None;
        float past = distance - edge;
        float floor = ShadowFloorDb(stirVariance);
        // (k / 2R²)^(1/3) = (k κ² / 2)^(1/3) with κ the curvature.
        float perK = 0.5f * curvature * curvature;
        float Taken(float hz)
        {
            float k = 2f * MathF.PI * hz / speedOfSound;
            return MathF.Min(floor, 8.685889f * CreepingDecay * MathF.Cbrt(k * perK) * past);
        }
        var (lo, mid, hi) = AcousticBands.GeometricCentresHz;
        return new Bands(Taken(lo), Taken(mid), Taken(hi));
    }

    /// <summary>The deepest a shadow goes in air stirred to <paramref name="stirVariance"/>, dB.</summary>
    public static float ShadowFloorDb(float stirVariance)
        => Math.Clamp(DeepestShadowDb - 10f * MathF.Log10(MathF.Max(1e-12f, stirVariance) / CalmStirVariance),
                      ShallowestFloorDb, DeepestShadowDb);

    // ── Bent down: the ground's bounce ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The most bending down lifts either end of the ground's bounce, metres. Past it a second bounce comes in, which
    /// one copy cannot carry (Salomons, "Computational Atmospheric Acoustics", 2001, ch. 3: multiple ground
    /// reflections in downward refraction). A judgement.
    /// </summary>
    public const float MostRaiseMetres = 30f;

    /// <summary>
    /// How much higher the source and the ear stand over the ground's bounce under sound bent down
    /// (<paramref name="curvature"/> positive), metres: in the curved-ground analogy (Almgren 1987) the ground under each
    /// leg rises d²/2R toward its end, d the leg's run from where the sound meets the ground. Zero bent up or straight:
    /// there the shadow is the whole of it.
    /// </summary>
    public static (float Source, float Ear) Raise(float sourceHeight, float earHeight, float distance, float curvature)
    {
        if (!(curvature > 0f) || !(distance > 0f)) return (0f, 0f);
        float hs = MathF.Max(0.02f, sourceHeight), hr = MathF.Max(0.02f, earHeight);
        float ds = distance * hs / (hs + hr), dr = distance - ds;
        return (MathF.Min(MostRaiseMetres, 0.5f * curvature * ds * ds), MathF.Min(MostRaiseMetres, 0.5f * curvature * dr * dr));
    }

    // ── The stir: a far sound rises and falls ──────────────────────────────────────────────────────

    /// <summary>The eddies' outer scale near the ground, metres: the ground's coherence's (Rietdijk, Chalmers 2017, eqs.
    /// 2.55 to 2.58; GroundAnswer.Coherence), fitted by Daigle et al. (1983) to sound between ends 1 to 2 m up.</summary>
    public const float OuterScaleMetres = 1.1f;

    /// <summary>The largest outer scale a way near the ground is given, metres: the height the surface layer's figures
    /// are taken at, the server's ten-metre wind. A way higher up is a source's in the open air, which the flicker's cap
    /// already holds. A judgement.</summary>
    public const float MostOuterScaleMetres = 10f;

    /// <summary>
    /// The eddies' outer scale for a way whose mean height over the ground is <paramref name="heightMetres"/>, metres:
    /// the height itself. Near the ground eddies are about as big as their height (the mixing length κz; Ostashev and
    /// Wilson 2015, ch. 6, take the shear's eddies' scale in proportion to height); a car's exhaust 0.5 m up heard by a
    /// standing ear gives 1.1 m, Daigle's figure. Held to <see cref="MostOuterScaleMetres"/>.
    /// </summary>
    public static float OuterScale(float heightMetres) => Math.Clamp(heightMetres, 0.05f, MostOuterScaleMetres);

    /// <summary>
    /// The most a far sound swings, one standard deviation of its level, nepers: 3 dB. A single tone through strong
    /// turbulence saturates at 5.6 dB, its amplitude Rayleigh-distributed (Ostashev and Wilson 2015, ch. 7); a band of
    /// sound averages part of that away across its frequencies. A judgement between the two.
    /// </summary>
    public const float MostFlickerNepers = 0.345f;

    /// <summary>
    /// How far a sound goes before the stir is let swing it, metres. Over a hundred metres the law below already swings a
    /// pure tone two decibels; a band of real sound much less, and nothing that near should be heard to move (Cody,
    /// 2026-10-16: refraction is for sounds past 150 m). A judgement.
    /// </summary>
    public const float FlickerFromMetres = 100f;

    /// <summary>
    /// How much a sound <paramref name="distance"/> off swings with the stir, per band, one standard deviation of its log
    /// amplitude, nepers: weak scattering of a spherical wave through Gaussian turbulence, χ² = (√π/2) μ² k² L L0 (the
    /// Rytov approximation; Ostashev and Wilson 2015, ch. 7), L the way past <see cref="FlickerFromMetres"/>, L0 the
    /// eddies' <paramref name="outerScale"/> (<see cref="OuterScale"/>), capped.
    /// </summary>
    public static Bands Flicker(float distance, float stirVariance, float outerScale, float speedOfSound)
    {
        float l = distance - FlickerFromMetres;
        if (!(l > 0f)) return Bands.None;
        float Sigma(float hz)
        {
            float k = 2f * MathF.PI * hz / speedOfSound;
            float chi2 = 0.5f * MathF.Sqrt(MathF.PI) * stirVariance * k * k * l * outerScale;
            return MathF.Min(MostFlickerNepers, MathF.Sqrt(chi2));
        }
        var (lo, mid, hi) = AcousticBands.GeometricCentresHz;
        return new Bands(Sigma(lo), Sigma(mid), Sigma(hi));
    }

    /// <summary>
    /// The level a swing of <paramref name="sigma"/> nepers puts on a band for a stir sample <paramref name="n"/> (zero
    /// mean, unit variance), dB: log-normal with its mean power kept, gain e^(σn − σ²).
    /// </summary>
    public static float SwingDb(float sigma, float n) => 8.685889f * (sigma * n - sigma * sigma);
}

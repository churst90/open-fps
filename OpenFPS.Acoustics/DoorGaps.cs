using System.Collections.Concurrent;
using System.Numerics;
using OpenFPS.Materials;

namespace OpenFPS.Acoustics;

/// <summary>
/// The gaps round a shut door's leaf, metres: <paramref name="RoundMetres"/> at its jambs and head,
/// <paramref name="UnderMetres"/> under it at the floor. A shut door passes most of what it passes through
/// these, not through its leaf: a hollow-core leaf takes about 35 dB at 1.25 kHz by its weight, a hung interior
/// door with its gaps 15 to 20 dB. A door's leaks against its leaf: Hongisto, Sound insulation of doors, part 1,
/// J. Sound Vib. 230 (2000) 133-148.
/// </summary>
public readonly record struct DoorGaps(float RoundMetres, float UnderMetres)
{
    /// <summary>The gap at the jambs and head of a door hung between two rooms, metres: what it needs to swing
    /// free of its frame.</summary>
    public const float InteriorRoundMetres = 0.003f;

    /// <summary>The gap under a door hung between two rooms, metres: undercut to let the rooms' air through.</summary>
    public const float InteriorUnderMetres = 0.010f;

    /// <summary>What is left open round a door to the open air (the outdoors, a roof), metres, at its jambs, head and
    /// threshold alike: weatherstripped and swept shut against the weather.</summary>
    public const float OutdoorMetres = 0.0003f;

    /// <summary>
    /// A door's gaps as its data gives them (<paramref name="round"/>, <paramref name="under"/>), each zero or less
    /// taking the default for where it hangs: <see cref="OutdoorMetres"/> round a door with open air on either side
    /// (<paramref name="toOutdoors"/>: the outdoors, or a place that is not indoors such as a roof, whatever surfaces it
    /// has), <see cref="InteriorRoundMetres"/> and <see cref="InteriorUnderMetres"/> round one between two rooms.
    /// </summary>
    public static DoorGaps For(float round, float under, bool toOutdoors)
        => new(round > 0f ? round : toOutdoors ? OutdoorMetres : InteriorRoundMetres,
               under > 0f ? under : toOutdoors ? OutdoorMetres : InteriorUnderMetres);

    /// <summary>
    /// What these gaps let through a shut doorway <paramref name="width"/> by <paramref name="height"/> metres in a
    /// leaf <paramref name="depth"/> thick, per band, as a share of the doorway's area: each gap's area times its
    /// slit's transmission (<see cref="SlitTransmission"/>), the jambs' and head's along 2h + w, the undercut's
    /// along w at the floor. Added to what the leaf lets through, it is the shut doorway's transmission.
    /// </summary>
    public Vector3 Leak(float width, float height, float depth)
    {
        float area = width * height;
        if (area <= 0f) return Vector3.Zero;
        Vector3 round = RoundMetres > 0f ? SlitTransmission(RoundMetres, depth, atFloor: false) * ((2f * height + width) * RoundMetres) : Vector3.Zero;
        Vector3 under = UnderMetres > 0f ? SlitTransmission(UnderMetres, depth, atFloor: true) * (width * UnderMetres) : Vector3.Zero;
        return (round + under) / area;
    }

    // ═══ A slit ══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The narrowest gap the slit is reckoned for, metres: below it the walls' friction has taken all.</summary>
    private const float NarrowestMetres = 1e-5f;

    private static readonly ConcurrentDictionary<(float Width, float Depth, bool AtFloor), Vector3> Slits = new();

    /// <summary>
    /// The energy a long slit <paramref name="width"/> wide through a wall <paramref name="depth"/> thick passes, per
    /// band, against what falls on its own area from a diffuse field. Over 1 where the slit gathers more than its
    /// area: a narrow slit takes sound from either side of it, and rings at its depth's half-wavelengths. Each band
    /// is the energy averaged over its one-third octaves, as a wall's is (<see cref="AcousticBands"/>).
    ///
    /// The slit is Gomperts' (Acustica 14, 1964, 1-16; Gomperts and Kihlman, Acustica 18, 1967, 144-150): one plane
    /// wave in the slit, joined at each mouth to the radiation impedance of a strip in a baffle, per unit length and
    /// against ρc/w, z = kw/2 + j kw e, with the end correction e = (ln(8/kw) - γ)/π (γ Euler's constant). Driven by
    /// the blocked pressure on its mouth, it passes τ = 4 Re z / |2 z cos k_s d + j (z_s + z²/z_s) sin k_s d|² of a
    /// plane wave at normal incidence, and twice that of a diffuse field: the field's pressure on a wall is twice its
    /// mean square, against a quarter of it in intensity on unit area, where a plane wave head on brings four
    /// times its pressure and all its intensity. Against 49 slits 0.5 to 20 mm wide measured in five laboratories,
    /// Gomperts' slit is within their spread (Griffin, ICSV25, 2018); Kim and An found it within 2 dB in the weighted
    /// index of slits they measured (Noise Control Eng. J. 57, 2009, 515-523).
    ///
    /// The air in a gap under a millimetre is held back by its walls, which Gomperts' lossless slit leaves out: the
    /// slit's wavenumber k_s and impedance z_s are a narrow slit's (Stinson, JASA 89, 1991, 550-558; Allard and
    /// Atalla, Propagation of Sound in Porous Media, 2nd ed., 2009, §4.3): ρ_e = ρ / (1 - tanh λ / λ) and
    /// K_e = γ_a P / (1 + (γ_a - 1) tanh λ' / λ'), λ = (w/2) √(jωρ/η), λ' the same with ρ Pr. At low frequency this
    /// is Poiseuille's flow between plates, 12η/w² a metre. A weatherstrip's 0.3 mm in a 40 mm leaf passes about a
    /// quarter of what the lossless slit says at 200 Hz, and its resonances are damped.
    ///
    /// A slit along the floor (<paramref name="atFloor"/>, a door's undercut) is half of one twice as wide, the floor
    /// its mirror. The strip impedance is the narrow slit's, kw under 1; the undercut's top band reaches kw of 4,
    /// where a form that tends to the open doorway's τ of 1 differs by under half a decibel on a door's whole leak.
    /// </summary>
    public static Vector3 SlitTransmission(float width, float depth, bool atFloor)
    {
        width = MathF.Max(NarrowestMetres, width);
        depth = MathF.Max(0f, depth);
        return Slits.GetOrAdd((width, depth, atFloor), static key =>
        {
            double w = key.AtFloor ? 2.0 * key.Width : key.Width;
            return new Vector3(Band(AcousticBands.LowThirdsHz, w, key.Depth),
                               Band(AcousticBands.MidThirdsHz, w, key.Depth),
                               Band(AcousticBands.HighThirdsHz, w, key.Depth));
        });
    }

    /// <summary>Points taken across each one-third octave: a slit's resonances are narrower than a third.</summary>
    private const int PointsPerThird = 5;

    private static float Band(float[] thirds, double w, double d)
    {
        double sum = 0;
        foreach (float centre in thirds)
            for (int i = 0; i < PointsPerThird; i++)
            {
                double hz = centre * Math.Pow(2.0, (-0.5 + (i + 0.5) / PointsPerThird) / 3.0);
                sum += Diffuse(hz, w, d);
            }
        return (float)(sum / (thirds.Length * PointsPerThird));
    }

    /// <summary>Euler's constant, γ.</summary>
    private const double Euler = 0.5772156649;

    /// <summary>What a slit <paramref name="w"/> wide and <paramref name="d"/> deep passes of a diffuse field at one
    /// frequency (<see cref="SlitTransmission"/>).</summary>
    internal static double Diffuse(double hz, double w, double d)
    {
        var air = Air.Reference;
        double rho = air.Density, c = AcousticConstants.SpeedOfSound, eta = air.Viscosity, p = air.PressureMb * 100.0;
        double omega = 2.0 * Math.PI * hz, kw = omega / c * w;

        // The narrow slit's air: its density and stiffness, the walls' friction and heat in them.
        Complex lambda = w / 2.0 * Complex.Sqrt(new Complex(0.0, omega * rho / eta));
        Complex lambdaHeat = w / 2.0 * Complex.Sqrt(new Complex(0.0, omega * rho * Air.Prandtl / eta));
        Complex density = rho / (1.0 - Tanh(lambda) / lambda);
        Complex stiffness = Air.HeatCapacityRatio * p / (1.0 + (Air.HeatCapacityRatio - 1.0) * Tanh(lambdaHeat) / lambdaHeat);
        Complex ks = omega * Complex.Sqrt(density / stiffness);
        Complex zs = Complex.Sqrt(density * stiffness) / (rho * c);

        // Each mouth a strip in a baffle.
        double e = Math.Max(0.0, (Math.Log(8.0 / kw) - Euler) / Math.PI);
        var z = new Complex(kw / 2.0, kw * e);
        Complex phase = ks * d;
        Complex drive = 2.0 * z * Complex.Cos(phase) + Complex.ImaginaryOne * (zs + z * z / zs) * Complex.Sin(phase);
        double normal = 4.0 * z.Real / (drive.Magnitude * drive.Magnitude);
        return 2.0 * normal;
    }

    /// <summary>tanh, which is 1 well before cosh overflows (a wide gap's λ is hundreds).</summary>
    private static Complex Tanh(Complex x) => x.Real > 20.0 ? Complex.One : Complex.Tanh(x);
}

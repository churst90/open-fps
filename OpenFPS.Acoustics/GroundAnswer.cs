using System.Numerics;
using OpenFPS.Materials;

namespace OpenFPS.Acoustics;

/// <summary>
/// What the ground under the path hands back of a source standing on it: the copy a voice plays of itself, off the
/// ground, a fraction of a millisecond later (GroundReflection), and what the ground takes from the voice's own bands
/// besides. Ground whose flow resistivity is known answers as its impedance says; grazed from far off, a lawn sends the
/// middle of the range back turned over and cancels part of the direct sound, which a copy that only ever adds cannot do.
/// </summary>
/// <remarks>
/// <para>The copy is the one Cody approved (2026-09-25): the surface's two bands from its absorption, the top falling
/// with range, each scaled by the turbulent coherence of the two paths. The impedance only takes away from it, never
/// adds: in each of the voice's bands, sound and copy together are brought down to what sound and the real ground carry
/// where that is less, and left alone where it is more. So a road, a car park, concrete and water sound as before (for
/// a source 0.3 m up or more and a standing ear their real ground carries as much as the copy, asphalt to within
/// 0.6 dB), and a lawn, a forest floor, sand or snow lose their middle once the sound grazes them from far enough.</para>
///
/// <para>The real ground, at e^(-iωt): the direct sound and the image's, Q e^(ikR2) / R2 against e^(ikR1) / R1, their
/// cross term scaled by the turbulent coherence C, so the mean power over free field is 1 + |Qs|² + 2 C Re(Qs e^(ikΔ)),
/// s = R1 / R2, Δ = R2 - R1 (Clifford and Lataitis, "Turbulence effects on acoustic wave propagation over a smooth
/// surface", J. Acoust. Soc. Am. 73 (1983) 1545). Q is the spherical wave's reflection off a locally reacting ground,
/// Q = Rp + (1 - Rp) F(w), with the plane wave's Rp at the grazing angle and the ground wave F (Attenborough, Li and
/// Horoshenkov, Predicting Outdoor Sound, Taylor and Francis 2007, chapter 2). The ground's impedance is Delany and
/// Bazley's, in Embleton's form for kPa s/m² (Embleton, Piercy and Daigle 1983): the flow resistivities in the material
/// table were fitted through this model and belong with it (MaterialProperties.FlowResistivityKPa).</para>
///
/// <para>Each band is judged over stretches, each over three thirds of itself, with e^(ikΔ) averaged over each third in
/// closed form: a fixed frequency jumps between lobes of the comb as Δ moves, the mean over a stretch moves smoothly.
/// The copy's power is reckoned as the voice's own stage makes it: through its one-pole split at
/// <see cref="CopySplitHz"/>, delayed by Δ, with no coherence (it is a copy). Two things a voice carries are not seen
/// here and taken as absent: the share of a car that is its tyres (GroundReflection.NearGroundShare, added in phase),
/// which makes the copy carry more than reckoned, so the ground takes less from a car than it should, never more; and a
/// texture's ground (GroundReflection.Texture, in phase only low down, its power above), reckoned as a comb.</para>
/// </remarks>
public static class GroundAnswer
{
    /// <summary>Where a voice's copy of its ground splits into its low and its high, Hz (GroundReflection's one-pole).</summary>
    public const float CopySplitHz = 1000f;

    /// <summary>
    /// The least power a band keeps, sound and ground together, over free field: 20 dB down (an estimate). A flat, even
    /// ground in still air cancels a grazing sound ever more with range; the ground's unevenness and the scattering it
    /// brings, which stop real ground short of that, are not modelled, and this floor stands in for them. Snow, the forest
    /// floor and sources lying on grass reach it, at a hundred metres and more.
    /// </summary>
    public const double LeastBandPower = 0.01;

    /// <summary>
    /// What the ground hands back: the height it bounces off, how much later (the extra path over the speed of sound), the
    /// copy's pressure below and above <see cref="CopySplitHz"/> (spreading included, never below zero), and the gain on
    /// each of the voice's own bands (AcousticBands) for what the ground takes besides: 1 where the copy says it all.
    /// </summary>
    public readonly record struct Bounce(float Height, float DelaySeconds, float LowGain, float HighGain,
                                         float ExcessLow, float ExcessMid, float ExcessHigh);

    // The stretches the ground is judged over, Hz: the voice's low band, its middle cut at the copy's split, and its high,
    // from the bottom of AcousticBands' lowest third-octave to the top of its highest. Each is judged over three thirds
    // of itself (SubFrom to SubTo, read at SubHz), and weighs its width in octaves, as a band's figure weighs its thirds.
    private static readonly double[] Edges =
    {
        AcousticBands.LowThirdsHz[0] / Math.Pow(2.0, 1.0 / 6.0), AcousticBands.LowCrossoverHz, CopySplitHz,
        AcousticBands.HighCrossoverHz, AcousticBands.HighThirdsHz[^1] * Math.Pow(2.0, 1.0 / 6.0),
    };
    private const int Stretches = 4, Thirds = 3;
    private static readonly double[] SubFrom = new double[Stretches * Thirds], SubTo = new double[Stretches * Thirds];
    private static readonly float[] SubHz = new float[Stretches * Thirds];
    private static readonly double[] Weight = new double[Stretches];

    static GroundAnswer()
    {
        for (int b = 0; b < Stretches; b++)
        {
            double from = Edges[b], to = Edges[b + 1];
            Weight[b] = Math.Log(to / from);
            for (int i = 0; i < Thirds; i++)
            {
                int j = b * Thirds + i;
                SubFrom[j] = from * Math.Pow(to / from, i / (double)Thirds);
                SubTo[j] = from * Math.Pow(to / from, (i + 1) / (double)Thirds);
                SubHz[j] = (float)Math.Sqrt(SubFrom[j] * SubTo[j]);
            }
        }
    }

    /// <summary>
    /// What the ground at <paramref name="groundHeight"/>, of <paramref name="ground"/>, sends back of a source at
    /// <paramref name="source"/> heard at <paramref name="ear"/>; null when the surface is above either, or the source
    /// stands within <paramref name="minHeight"/> of it. Allocates nothing: it runs for every voice that hears the ground,
    /// every frame. Under sound bent down (Refraction.Raise) the bounce comes in as if the source stood
    /// <paramref name="sourceRaise"/> and the ear <paramref name="earRaise"/> higher over it, metres.
    /// </summary>
    public static Bounce? Reflect(float groundHeight, in MaterialProperties ground, Vector3 source, Vector3 ear,
                                  float minHeight, float speedOfSound, float sourceRaise = 0f, float earRaise = 0f)
    {
        if (!Bounced(groundHeight, ground, source, ear, minHeight, speedOfSound, sourceRaise, earRaise, out var path)) return null;
        if (ground.FlowResistivityKPa is not { } flow || !(flow > 0f))
            return new Bounce(path.Height, path.Delay, path.Low, path.High, 1f, 1f, 1f);
        Span<double> real = stackalloc double[3], copy = stackalloc double[3];
        Judge(flow, path, speedOfSound, real, copy);
        static float Taken(double r, double c) => (float)Math.Sqrt(Math.Min(1.0, r / c));
        return new Bounce(path.Height, path.Delay, path.Low, path.High,
                          Taken(real[0], copy[0]), Taken(real[1], copy[1]), Taken(real[2], copy[2]));
    }

    /// <summary>
    /// For the lab and the tests: over each of the voice's three bands, the mean power over free field of sound and the
    /// real ground (<paramref name="real"/>) and of sound and the copy (<paramref name="copy"/>), as <see cref="Reflect"/>
    /// judges them. False where there is no bounce or the ground has no flow resistivity.
    /// </summary>
    public static bool Powers(float groundHeight, in MaterialProperties ground, Vector3 source, Vector3 ear, float speedOfSound,
                              Span<double> real, Span<double> copy, float sourceRaise = 0f, float earRaise = 0f)
    {
        if (!Bounced(groundHeight, ground, source, ear, 0f, speedOfSound, sourceRaise, earRaise, out var path)
            || ground.FlowResistivityKPa is not { } flow || !(flow > 0f)) return false;
        Judge(flow, path, speedOfSound, real, copy);
        return true;
    }

    /// <summary>The geometry of one bounce and the copy a voice plays of it.</summary>
    private readonly record struct Path(float Height, float Delay, float Low, float High, float Direct, float Mirrored,
                                        float Spread, float Rho, float Heights);

    /// <summary>
    /// The copy Cody approved: the surface's two bands from its absorption, the top falling with range, each scaled by the
    /// paths' coherence and the longer path's extra spreading. False where there is nothing to bounce off. The raises
    /// lift the ends over the bounce, as sound bent down sees them; whether there is a bounce is the ends' own heights'.
    /// </summary>
    private static bool Bounced(float gb, in MaterialProperties ground, Vector3 source, Vector3 ear, float minHeight,
                                float speedOfSound, float sourceRaise, float earRaise, out Path path)
    {
        path = default;
        if (gb > MathF.Min(source.Y, ear.Y)) return false;             // nothing to bounce off below both
        if (source.Y - gb < minHeight) return false;                   // a recording that has it already
        source.Y += sourceRaise;
        ear.Y += earRaise;

        var image = new Vector3(source.X, 2f * gb - source.Y, source.Z);
        float direct = MathF.Max(0.1f, Vector3.Distance(source, ear));
        float mirrored = Vector3.Distance(image, ear);
        float spread = direct / MathF.Max(direct, mirrored);
        float low = MathF.Sqrt(Math.Clamp(1f - 0.5f * (ground.AbsorptionLow + ground.AbsorptionMid), 0f, 1f));
        float high = MathF.Sqrt(Math.Clamp(1f - ground.AbsorptionHigh, 0f, 1f));
        // At grazing incidence even asphalt is not a mirror at the top: its spherical-wave reflection falls with range
        // (Delany-Bazley, 20,000 kPa s/m2, source 0.3 m: 0.88 at 10 m and 0.64 at 50 m at 4 kHz). And the air is never
        // still: turbulence decorrelates the two paths, the Clifford-Lataitis coherence (see Coherence).
        high *= Math.Clamp(0.94f - 0.0055f * direct, 0.45f, 1f);
        float hs = MathF.Max(0.02f, source.Y - gb), hr = MathF.Max(0.02f, ear.Y - gb);
        float rho = 1f / (0.5f * (1f / hs + 1f / hr));
        low *= Coherence(250f, direct, rho, speedOfSound);
        high *= Coherence(2500f, direct, rho, speedOfSound);
        path = new Path(gb, (mirrored - direct) / speedOfSound, low * spread, high * spread, direct, mirrored, spread, rho, hs + hr);
        return true;
    }

    /// <summary>
    /// Over each of the voice's three bands, the mean power over free field of sound and a ground of
    /// <paramref name="flowKPa"/> (<paramref name="real"/>, never under <see cref="LeastBandPower"/>) and of sound and the
    /// copy (<paramref name="copy"/>). The sine of the grazing angle is the two heights over the image path.
    /// </summary>
    private static void Judge(float flowKPa, in Path path, float speedOfSound, Span<double> real, Span<double> copy)
    {
        double extra = Math.Max(0.0, path.Mirrored - (double)path.Direct);
        float grazing = Math.Clamp(path.Heights / MathF.Max(path.Mirrored, 1e-3f), 1e-4f, 1f);
        double low = path.Low, high = path.High;
        Span<double> r = stackalloc double[Stretches], c = stackalloc double[Stretches];
        for (int b = 0; b < Stretches; b++)
        {
            double rb = 0.0, cb = 0.0;
            for (int j = b * Thirds; j < (b + 1) * Thirds; j++)
            {
                float hz = SubHz[j];
                var turn = MeanTurn(SubFrom[j], SubTo[j], extra, speedOfSound);
                var q = SphericalReflection(hz, flowKPa, grazing, path.Mirrored, speedOfSound) * path.Spread;
                rb += 1.0 + q.Real * q.Real + q.Imaginary * q.Imaginary
                      + 2.0 * Coherence(hz, path.Direct, path.Rho, speedOfSound) * (q * turn).Real;
                // The copy as GroundReflection plays it: high, plus (low - high) through a one-pole low-pass at the split
                // (1 / (1 - i f / fc) at e^(-iωt)), delayed by the extra path; a copy is coherent by construction.
                var split = Complex.One / new Complex(1.0, -hz / (double)CopySplitHz);
                var g = high + (low - high) * split;
                cb += 1.0 + g.Real * g.Real + g.Imaginary * g.Imaginary + 2.0 * (g * turn).Real;
            }
            r[b] = Math.Max(LeastBandPower, rb / Thirds);
            c[b] = Math.Max(1e-6, cb / Thirds);
        }
        // The voice's bands: the low is the first stretch, the middle the next two, the high the last.
        for (int band = 0, first = 0; band < 3; band++)
        {
            int last = band == 1 ? first + 1 : first;
            double rr = 0.0, cc = 0.0, w = 0.0;
            for (int b = first; b <= last; b++) { rr += Weight[b] * r[b]; cc += Weight[b] * c[b]; w += Weight[b]; }
            real[band] = rr / w;
            copy[band] = cc / w;
            first = last + 1;
        }
    }

    /// <summary>The mean of e^(i 2πfΔ/c) over f from <paramref name="fromHz"/> to <paramref name="toHz"/>: how much of the
    /// image stays in step with the sound across a stretch, worked out whole so it moves smoothly as Δ does.</summary>
    internal static Complex MeanTurn(double fromHz, double toHz, double extra, float speedOfSound)
    {
        double a1 = 2.0 * Math.PI * fromHz * extra / speedOfSound, a2 = 2.0 * Math.PI * toHz * extra / speedOfSound;
        if (Math.Abs(a2 - a1) < 1e-6) return Complex.FromPolarCoordinates(1.0, 0.5 * (a1 + a2));
        return (Complex.FromPolarCoordinates(1.0, a2) - Complex.FromPolarCoordinates(1.0, a1)) / new Complex(0.0, a2 - a1);
    }

    /// <summary>
    /// A ground's impedance over air's at <paramref name="hz"/> for its flow resistivity in kPa s/m² (Delany and Bazley,
    /// "Acoustical properties of fibrous absorbent materials", Applied Acoustics 3 (1970) 105, in Embleton, Piercy and
    /// Daigle's form, 1983): Z = 1 + 9.08 (f/σ)^-0.75 + i 11.9 (f/σ)^-0.73, at e^(-iωt). Their 0.0571 X^-0.754 and
    /// 0.087 X^-0.732 with X = ρ f / σ, ρ = 1.2 kg/m³ and σ in Pa s/m², are these to the third figure.
    /// </summary>
    public static Complex Impedance(float hz, float flowKPa)
    {
        double x = hz / (double)flowKPa;
        return new Complex(1.0 + 9.08 * Math.Pow(x, -0.75), 11.9 * Math.Pow(x, -0.73));
    }

    /// <summary>
    /// A spherical wave's reflection off a locally reacting ground, Q = Rp + (1 - Rp) F(w): Rp = (sin ψ - β) / (sin ψ + β)
    /// the plane wave's at grazing angle ψ, β = 1 / Z, and the ground wave F(w) = 1 + i √π w W(w) with the numerical
    /// distance w = √(i k R2 / 2) (sin ψ + β) (Attenborough, Li and Horoshenkov 2007, chapter 2). A rigid ground
    /// (infinite flow resistivity) hands the wave back whole.
    /// </summary>
    public static Complex SphericalReflection(float hz, float flowKPa, float sinGrazing, float mirrored, float speedOfSound)
    {
        if (float.IsPositiveInfinity(flowKPa)) return Complex.One;
        var beta = Complex.One / Impedance(hz, flowKPa);
        var rp = (sinGrazing - beta) / (sinGrazing + beta);
        double k = 2.0 * Math.PI * hz / speedOfSound;
        var w = Complex.Sqrt(new Complex(0.0, k * mirrored / 2.0)) * (sinGrazing + beta);
        var f = Complex.One + new Complex(0.0, Math.Sqrt(Math.PI)) * w * Faddeeva(w);
        return rp + (Complex.One - rp) * f;
    }

    /// <summary>
    /// The Faddeeva function W(z) = e^(-z²) erfc(-iz) anywhere in the plane: Humlicek's W4 in the upper half ("Optimized
    /// computation of the Voigt and complex probability functions", J. Quant. Spectrosc. Radiat. Transfer 27 (1982) 437;
    /// a part in 10⁴ or better), and below it W(z) = 2 e^(-z²) - W(-z).
    /// </summary>
    public static Complex Faddeeva(Complex z)
    {
        if (z.Imaginary < 0.0) return 2.0 * Complex.Exp(-z * z) - Faddeeva(-z);
        var t = new Complex(z.Imaginary, -z.Real);
        double s = Math.Abs(z.Real) + z.Imaginary;
        if (s >= 15.0) return t * 0.5641896 / (0.5 + t * t);
        if (s >= 5.5)
        {
            var u = t * t;
            return t * (1.410474 + u * 0.5641896) / (0.75 + u * (3.0 + u));
        }
        if (z.Imaginary >= 0.195 * Math.Abs(z.Real) - 0.176)
            return (16.4955 + t * (20.20933 + t * (11.96482 + t * (3.778987 + t * 0.5642236))))
                   / (16.4955 + t * (38.82363 + t * (39.27121 + t * (21.69274 + t * (6.699398 + t)))));
        var v = t * t;
        var numerator = t * (36183.31 - v * (3321.9905 - v * (1540.787 - v * (219.0313 - v * (35.76683 - v * (1.320522 - v * 0.56419))))));
        var denominator = 32066.6 - v * (24322.84 - v * (9022.228 - v * (2186.181 - v * (364.2191 - v * (61.57037 - v * (1.841439 - v))))));
        return Complex.Exp(v) - numerator / denominator;
    }

    /// <summary>
    /// Turbulent coherence between the direct and ground paths (Clifford and Lataitis 1983): Tc = exp(-σ² (1 - Csp)), with
    /// a moderate &lt;μ²&gt; of 5e-6 and an outer scale of 1.1 m (Rietdijk, Chalmers 2017, eqs. 2.55 to 2.58).
    /// <paramref name="rho"/> is the paths' separation, the harmonic mean of the two heights.
    /// </summary>
    public static float Coherence(float hz, float distance, float rho, float speedOfSound)
    {
        const float Mu2 = 5e-6f, OuterScale = 1.1f;
        float k = 2f * MathF.PI * hz / speedOfSound;
        float sigma2 = MathF.Sqrt(MathF.PI) * Mu2 * k * k * distance * OuterScale;
        float x = MathF.Max(1e-4f, rho / OuterScale);
        float csp = MathF.Sqrt(MathF.PI) * 0.5f * Erf(x) / x;
        return MathF.Exp(-sigma2 * MathF.Max(0f, 1f - csp));
    }

    private static float Erf(float x)
    {
        // Abramowitz and Stegun 7.1.26, good to 1.5e-7.
        float t = 1f / (1f + 0.3275911f * MathF.Abs(x));
        float y = 1f - (((((1.061405429f * t - 1.453152027f) * t) + 1.421413741f) * t - 0.284496736f) * t + 0.254829592f) * t * MathF.Exp(-x * x);
        return x < 0 ? -y : y;
    }
}

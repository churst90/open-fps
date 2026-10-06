using System;

namespace OpenFPS.Common;

/// <summary>What a drop does when it lands on something: the five kinds of surface rain sounds
/// different on.</summary>
public enum RainSurfaceKind
{
    /// <summary>Nothing to land on, or nothing that makes a sound (a material with no substance).</summary>
    None,
    /// <summary>Open water and puddles: the drop's click and, for some sizes, a bubble that rings.</summary>
    Pool,
    /// <summary>Hard ground, or a slab too thick to move: a wet click and a splash, and puddles in
    /// the low places.</summary>
    Hard,
    /// <summary>A thin sheet that the drop sets ringing: a metal roof, a car, a pane of glass.</summary>
    Plate,
    /// <summary>Grass, soil, gravel: something that yields under the drop, so its click is soft and low.</summary>
    Soft,
    /// <summary>Leaves: each drop strikes a light, damped leaf, and what the leaves catch drips off
    /// them as big drops onto whatever is below.</summary>
    Canopy,
}

/// <summary>
/// How rain sounds on each kind of surface, from what the surface is made of.
///
/// The material registry already says what a surface IS — how dense, how stiff, how lossy, whether
/// it is porous — and that decides the kind: a skin thinner than <see cref="PlateMaxSkinMetres"/> of
/// anything stiff is a plate the drops ring, anything stiff and thicker is ground the drops splash
/// on, anything soft and porous yields. Two materials are named because no property tells them apart
/// from their neighbours: Water (a pool, which is a fluid, not a stiff solid) and Foliage (leaves in
/// the air, which a lawn's grass blades are not).
/// </summary>
public static class RainSurfaces
{
    /// <summary>A skin thinner than this is a plate the drops set ringing, m. Sheet steel, a car's
    /// panels, glazing, polycarbonate and a timber board are all under it; a concrete slab, a road or
    /// a brick wall is far over it.</summary>
    public const float PlateMaxSkinMetres = 0.02f;

    /// <summary>Under this Young's modulus a material yields under a drop rather than stopping it, GPa:
    /// grass, soil, foam, rubber.</summary>
    public const float SoftBelowGPa = 1f;

    /// <summary>The kind of surface a material of this skin thickness is to rain.</summary>
    public static RainSurfaceKind KindOf(string material, float skinMetres)
    {
        if (string.Equals(material, "Water", StringComparison.OrdinalIgnoreCase)) return RainSurfaceKind.Pool;
        if (string.Equals(material, "Foliage", StringComparison.OrdinalIgnoreCase)) return RainSurfaceKind.Canopy;
        // An unknown name would fall back to Generic with a warning per call; Generic it is, quietly,
        // since a surveyor asks about every box round the listener.
        var p = AcousticRegistry.GetProperties(AcousticRegistry.IsKnown(material) ? material : "Generic");
        if (p.DensityKgM3 <= 0f) return RainSurfaceKind.None;
        if (p.YoungsModulusGPa < SoftBelowGPa) return RainSurfaceKind.Soft;
        if (skinMetres > 0f && skinMetres <= PlateMaxSkinMetres) return RainSurfaceKind.Plate;
        return RainSurfaceKind.Hard;
    }

    // ── Hard ground ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The share of a hard surface standing in puddles, for a rain rate.
    ///
    /// A road sheds its water sideways as a film far thinner than a drop — the kinematic-wave depth
    /// on a 2 % crossfall five metres wide is about a third of a millimetre at 10 mm/h, inside the
    /// texture of the surfacing — and a drop on a film that thin splashes without making a bubble
    /// (a drop needs a pool about as deep as itself to open a crater that closes on air). What does
    /// hold water is the low places: ruts, dips, the gutter, a cracked flag. How much of a street
    /// that is, is not in the map, so this is an ASSUMPTION, written as one: a few per cent in light
    /// rain, rising toward <see cref="PuddleMaxShare"/> as the rain gets heavier and the low places
    /// fill. To be judged by ear and replaced by the ground's own data when the map carries it.
    /// </summary>
    public static float PuddleShare(float rate)
        => rate > 0f ? PuddleMaxShare * rate / (rate + PuddleHalfRate) : 0f;

    /// <summary>The most of a hard surface that is ever puddle (see <see cref="PuddleShare"/>).</summary>
    public const float PuddleMaxShare = 0.08f;

    /// <summary>The rate at which the low places are half as full as they get, mm/h.</summary>
    public const float PuddleHalfRate = 5f;

    // ── The splash ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// How long a drop's water takes to go from a falling sphere to a sheet at its widest on the
    /// ground, s: the time over which the splash pushes the air, and so the time scale of the click
    /// the air hears (RainSynth.Click).
    ///
    /// The kinematic estimate of the time to maximum spreading is t = 8/3 · D / v (Pasandideh-Fard,
    /// Qiao, Chandra and Mostaghimi 1996, "Capillary effects during droplet impact on a solid
    /// surface", Phys. Fluids 8, 650-659), which their photographs and later measurements put in the
    /// right range for water drops at raindrop speeds: about 2-3 D / v, against the D / v the drop's
    /// centre takes to stop. On a wet road the sheet spreads into the film and lifts a crown, which
    /// lives about as long (Cossali, Coghe and Marengo 1997, Exp. Fluids 22, 463-472).
    /// </summary>
    public static float SplashSeconds(float diameterMm, float speed) => SpreadFactor * diameterMm * 1e-3f / MathF.Max(0.1f, speed);

    /// <summary>The 8/3 in the spreading time (see <see cref="SplashSeconds"/>).</summary>
    public const float SpreadFactor = 8f / 3f;

    // ── Soft ground ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// How many times longer a drop takes to stop on this material than on something rigid.
    ///
    /// For the drop itself almost everything is rigid: its dynamic pressure, ρ v², is a few tens of
    /// kilopascals, and even turf is a few megapascals stiff. What a lawn or a bed of soil changes is
    /// what the drop meets first — a blade of grass that bends away under it, a crumb of soil that
    /// gives, no film of water to splash on — and that spreads the same momentum over a little more
    /// time, which takes the top off the click. The law used is a quarter of a drop-transit time
    /// longer for each decade the material is softer than <see cref="SoftBelowGPa"/>: grass 1.6, soil
    /// 1.3, gravel 1.1, leaves 1.5. An ASSUMPTION on the size of the effect, flagged as one; the lab
    /// sets the result beside recordings of rain on grass and in woods, and it is for the ear to judge.
    /// </summary>
    public static float ContactStretch(MaterialProperties p)
    {
        float e = MathF.Max(1e-4f, p.YoungsModulusGPa);
        return e >= SoftBelowGPa ? 1f : 1f + SoftStretchPerDecade * MathF.Log10(SoftBelowGPa / e);
    }

    /// <summary>See <see cref="ContactStretch"/>.</summary>
    public const float SoftStretchPerDecade = 0.25f;

    // ── Leaves ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>The share of the rain a crown of this leaf area index catches: Beer-Lambert
    /// extinction through randomly oriented leaves, 1 − exp(−k LAI) with k = 0.5 (Monsi and Saeki
    /// 1953). A tree in leaf (LAI 4-5) catches about nine drops in ten.</summary>
    public static float CanopyCatch(float leafAreaIndex) => 1f - MathF.Exp(-CanopyExtinction * MathF.Max(0f, leafAreaIndex));

    /// <summary>k in the canopy's Beer-Lambert extinction.</summary>
    public const float CanopyExtinction = 0.5f;

    /// <summary>The leaf area index a hedge or a crown with no spec of its own is taken to have.</summary>
    public const float DefaultLeafAreaIndex = 4f;

    /// <summary>
    /// The diameter the water a canopy catches leaves it at, mm: drips off leaf tips. Throughfall
    /// under broadleaf trees carries drops of 4-6 mm that rain itself almost never has (Nanko,
    /// Hotta and Suzuki 2006, J. Hydrology 329, 422-431), and they fall from the crown to the ground at
    /// whatever speed that height gives them — the slow, heavy "plop" under a tree.
    /// </summary>
    public const float DripDiameterMm = 4.5f;

    // ── Vehicles ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>The share of a car's top, seen from above, that is glass: windscreen, rear screen,
    /// and the roof's strip of each. About a third on a saloon.</summary>
    public const float CarGlassShareOfTop = 0.3f;

    /// <summary>A car's glazing, m: a laminated windscreen is two 2.1 mm plies and an interlayer.</summary>
    public const float CarGlassMetres = 0.0045f;

    /// <summary>A pane of glass in a car is held in a bonded rubber frame: its loss factor in place.</summary>
    public const float CarGlassLoss = 0.05f;

    /// <summary>
    /// What a car's headliner — the 5-10 mm of foam and fabric bonded under its roof — takes off the
    /// roof's sound on its way into the cabin, per mixer band as amplitude: nothing in the low band,
    /// a few dB in the middle, more above 4 kHz, the shape a porous lining that thin has (its
    /// absorption is small below a quarter wavelength of its own thickness and rises to most of the
    /// sound by 4-8 kHz). An ASSUMPTION on the figures, flagged as one.
    /// </summary>
    public static readonly (float Low, float Mid, float High) HeadlinerGains = (1f, 0.75f, 0.45f);

    // ── The listener's own body ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Under the open sky the rain lands on you too: on your head a hand's breadth from your ears and
    /// on your shoulders a little further. Those drops are ten times nearer than the nearest ground,
    /// so each is a hundred times the energy at the ear of one at your feet, and they are the drops
    /// that stand out one by one. The figures are a body's, adult and upright: a head about 15 cm
    /// across and 20 cm front to back (π/4 · 0.15 · 0.2 ≈ 0.024 m²), its crown about 12 cm above the
    /// ear canal; shoulders 40 cm across and 25 cm deep less the neck (about 0.08 m²), 15-25 cm out
    /// from the ears and 18 cm below them. The material is <see cref="BodyMaterial"/>: the map knows
    /// no clothing, so hair and a coat are both a soft surface. ASSUMPTION, flagged: no hood, no hat,
    /// no umbrella.
    /// </summary>
    public const float HeadSquareMetres = 0.024f, HeadRadiusMetres = 0.09f, CrownAboveEarMetres = 0.12f;

    /// <summary>The shoulders: area, m²; how far out from between the ears they start and end, m;
    /// how far below the ears they are, m. See <see cref="HeadSquareMetres"/>.</summary>
    public const float ShouldersSquareMetres = 0.08f, ShouldersInnerMetres = 0.12f, ShouldersOuterMetres = 0.24f,
                       ShouldersBelowEarMetres = 0.18f;

    /// <summary>What a drop landing on the listener lands on.</summary>
    public const string BodyMaterial = "Skin";

    // ── Built panels ─────────────────────────────────────────────────────────────────────────────

    /// <summary>The free span of a built panel between its supports, m: purlins under a roof sheet,
    /// the glazing bars of a curtain wall. A box in the map is a whole roof or a whole run of
    /// glazing; what rings is one bay of it.</summary>
    public const float BuiltBayMetres = 1.2f;
}

/// <summary>
/// A thin sheet the rain lands on, as the physics of a plate: what a drop's blow puts into it and
/// what it gives back to the air.
///
/// THE BLOW. A drop landing on a rigid surface does not rebound; it spreads, and hands the surface
/// its momentum m v over about the time it takes to travel its own diameter, τ = D / v. The force
/// rises as the square root of time from first contact (Philippi, Lagrée and Antkowiak 2016, J. Fluid
/// Mech. 795, 96-135), peaks at about 0.8 ρ v² D² at about a fifth of τ (Gordillo, Sun and Cheng 2018,
/// "Dynamics of drop impact on solid surfaces: evolution of impact force and self-similar spreading",
/// J. Fluid Mech. 840, 190-214; Mitchell et al. 2019, J. Fluid Mech. 867, 300-322, measure the same),
/// and dies away over the rest of the spreading. Taken here as exactly that: √t to the peak at 0.2 τ,
/// then an exponential whose length makes the whole pulse carry m v (0.52 τ). The √t onset is what
/// keeps energy in the top octaves (it falls 9 dB an octave above 1/τ, where a smooth pulse would fall
/// off a cliff): for a 2 mm drop at 6.5 m/s τ is 0.3 ms, the blow's energy peaks near a kilohertz,
/// and 8 kHz is 10 dB under it.
///
/// WHAT GOES IN. An infinite plate driven at a point takes power through its point mobility, which is
/// real and flat: Y = 1 / (8 √(B m″)) (Cremer, Heckl and Petersson, Structure-Borne Sound, ch. 5),
/// B the bending stiffness and m″ the mass per square metre. So the energy a blow puts into the plate
/// in a band is Y times the blow's energy spectrum in that band — and a light thin sheet takes in
/// orders of magnitude more than a slab (0.8 mm steel against 150 mm concrete: 40 dB).
///
/// WHAT COMES OUT. Two things. The plate's own field rings on, decaying at its total loss factor —
/// internal, mounting and radiation (<see cref="WallTransmission.TotalLossFactor"/> for a built
/// panel, the body's own figure for a car) — and radiates P = ρ0 c0 S σ ⟨v²⟩, σ the radiation
/// efficiency: a thin steel sheet is far below its coincidence frequency and radiates mostly from its
/// edges, a pane of glass is at coincidence by 2 kHz and radiates well above it (the brighter tick).
/// Below the frequency where its modes stop overlapping a bay rings at its own notes, which is the
/// drumming of a pane or a car roof; above it the modes are a continuum and the field is a band of
/// noise rising and decaying with each blow. And the struck spot itself pushes the air as the blow
/// lands: the volume acceleration of an infinite plate under a point force is F / m″, so the near
/// field is p = ρ0 F(t) / (2π m″ r), a thud with the blow's own shape.
/// </summary>
public readonly record struct RainPlate
{
    /// <summary>Poisson's ratio, for the bending stiffness: 0.3 for metals and glass.</summary>
    public const float Poisson = 0.3f;
    /// <summary>ρ0 c0 of air at 20 °C, Pa·s/m.</summary>
    public const float AirImpedance = WallTransmission.AirDensity * WallTransmission.SoundSpeed;
    /// <summary>Water's density, kg/m³.</summary>
    public const float WaterDensity = 1000f;
    /// <summary>The peak force of a drop's blow over ρ v² D² (Gordillo et al. 2018).</summary>
    public const float PeakForceCoefficient = 0.8f;

    public string Material { get; init; }
    public float SkinMetres { get; init; }
    /// <summary>One bay's sides, m.</summary>
    public float BayA { get; init; }
    public float BayB { get; init; }
    /// <summary>The loss factor in place before radiation; zero takes the material's own plus the
    /// mounting loss (<see cref="PanelAcoustics.MountedLoss"/>), as a built panel has.</summary>
    public float MountedLossFactor { get; init; }

    public RainPlate(string material, float skinMetres, float bayA, float bayB, float mountedLossFactor = 0f)
    {
        Material = material;
        SkinMetres = skinMetres;
        BayA = MathF.Max(0.05f, bayA);
        BayB = MathF.Max(0.05f, bayB);
        MountedLossFactor = mountedLossFactor;
    }

    public MaterialProperties Properties => AcousticRegistry.GetProperties(AcousticRegistry.IsKnown(Material) ? Material : "Generic");

    /// <summary>m″, kg/m².</summary>
    public float SurfaceDensity => MathF.Max(0.01f, Properties.DensityKgM3 * SkinMetres);

    /// <summary>B = E h³ / 12 (1 − ν²), N·m.</summary>
    public float Bending
    {
        get
        {
            float h = SkinMetres;
            return MathF.Max(1e-6f, Properties.YoungsModulusGPa * 1e9f * h * h * h / (12f * (1f - Poisson * Poisson)));
        }
    }

    /// <summary>The coincidence frequency, Hz: c² / 2π · √(m″ / B).</summary>
    public float CriticalHz => WallTransmission.SoundSpeed * WallTransmission.SoundSpeed / (2f * MathF.PI)
                               * MathF.Sqrt(SurfaceDensity / Bending);

    /// <summary>The point mobility of the infinite plate, m/(N·s).</summary>
    public float Mobility => 1f / (8f * MathF.Sqrt(Bending * SurfaceDensity));

    public float BayArea => BayA * BayB;

    /// <summary>A bay's modes per hertz: (S / 2) √(m″ / B), the same at every frequency.</summary>
    public float ModesPerHz => 0.5f * BayArea * MathF.Sqrt(SurfaceDensity / Bending);

    /// <summary>The loss factor in place before radiation.</summary>
    public float StructuralLoss
    {
        get
        {
            if (MountedLossFactor > 0f) return MountedLossFactor;
            return Properties.LossFactor + PanelAcoustics.MountedLoss;
        }
    }

    /// <summary>
    /// The radiation efficiency of one bay, at a frequency.
    ///
    /// Below coincidence a finite plate radiates from a strip along its edges (Maidanik 1962, J. Acoust.
    /// Soc. Am. 34, 809; the edge-mode term as Bies and Hansen's Engineering Noise Control gives it):
    /// σ = U c / (π² S fc) · √(f / fc), U the perimeter. Above it, every mode radiates:
    /// σ = 1 / √(1 − fc / f), held under <see cref="MaxRadiationEfficiency"/> where that runs away at
    /// coincidence itself. Accurate to a few decibels, which is the model's grain anyway.
    /// </summary>
    public float RadiationEfficiency(float hz)
    {
        float fc = CriticalHz;
        float below = 2f * (BayA + BayB) * WallTransmission.SoundSpeed / (MathF.PI * MathF.PI * BayArea * fc)
                      * MathF.Sqrt(MathF.Min(1f, hz / fc));
        if (hz < fc) return MathF.Min(MaxRadiationEfficiency, below);
        float above = 1f / MathF.Sqrt(MathF.Max(1e-3f, 1f - fc / hz));
        return MathF.Min(MaxRadiationEfficiency, MathF.Max(below, above));
    }

    /// <summary>σ is held under this at coincidence, where the infinite-plate law runs to infinity.</summary>
    public const float MaxRadiationEfficiency = 2f;

    /// <summary>The total loss factor at a frequency: in place, plus what radiating from both faces
    /// takes, 2 ρ0 c0 σ / (ω m″).</summary>
    public float Loss(float hz)
        => StructuralLoss + 2f * AirImpedance * RadiationEfficiency(hz) / (2f * MathF.PI * hz * SurfaceDensity);

    /// <summary>Where a bay's modes start to overlap, Hz: n f η = 1. Below it the bay rings at its own
    /// notes; above it the field is a continuum.</summary>
    public float OverlapHz
    {
        get
        {
            // η changes slowly with f; two passes find the crossing.
            float f = 1f / MathF.Max(1e-6f, ModesPerHz * StructuralLoss);
            f = 1f / MathF.Max(1e-6f, ModesPerHz * Loss(MathF.Max(20f, f)));
            return f;
        }
    }

    /// <summary>A simply supported bay's (m, n) mode, Hz: (π/2) √(B/m″) ((m/a)² + (n/b)²).</summary>
    public float ModeHz(int m, int n)
        => 0.5f * MathF.PI * MathF.Sqrt(Bending / SurfaceDensity) * (m * m / (BayA * BayA) + n * n / (BayB * BayB));

    // ── The blow ────────────────────────────────────────────────────────────────────────────────

    /// <summary>A drop's peak force on a rigid surface, N, for its diameter (mm) and speed (m/s).</summary>
    public static float PeakForce(float diameterMm, float speed)
    {
        float d = diameterMm * 1e-3f;
        return PeakForceCoefficient * WaterDensity * speed * speed * d * d;
    }

    /// <summary>The momentum a drop hands over, N·s.</summary>
    public static float Impulse(float diameterMm, float speed)
    {
        float d = diameterMm * 1e-3f;
        return WaterDensity * MathF.PI / 6f * d * d * d * speed;
    }

    /// <summary>τ = D / v, the time a drop takes to travel its own diameter, s: the blow's time scale.</summary>
    public static float BlowSeconds(float diameterMm, float speed) => diameterMm * 1e-3f / MathF.Max(0.1f, speed);

    /// <summary>Where the blow peaks, and how long its fall takes, in units of τ (see the summary).</summary>
    public const float BlowPeakAt = 0.2f;
    public static readonly float BlowFall = MathF.PI / 6f / PeakForceCoefficient - 2f / 3f * BlowPeakAt;

    /// <summary>The blow's shape, F / F_peak, at t / τ.</summary>
    public static float BlowShape(float x)
        => x <= 0f ? 0f : x < BlowPeakAt ? MathF.Sqrt(x / BlowPeakAt) : MathF.Exp(-(x - BlowPeakAt) / BlowFall);

    /// <summary>∫ shape d(t/τ): the blow's momentum in units of F_peak τ.</summary>
    public static readonly float BlowShapeArea = 2f / 3f * BlowPeakAt + BlowFall;

    /// <summary>∫ shape² d(t/τ): the blow's energy in units of F_peak² τ.</summary>
    public static readonly float BlowShapeEnergy = BlowPeakAt / 2f + BlowFall / 2f;

    // 2 ∫₀ᵘ |ĝ(u')|² du' on a log grid of u = f τ, from the shape's transform: the √ rise numerically,
    // the exponential fall in closed form.
    private const int SpectrumPoints = 240;
    private const float SpectrumLoU = 1e-4f, SpectrumHiU = 1e3f;
    private static readonly float[] SpectrumMagnitude = new float[SpectrumPoints];
    private static readonly float[] SpectrumCumulative = BuildSpectrum(SpectrumMagnitude);

    private static float[] BuildSpectrum(float[] magnitude)
    {
        var cum = new float[SpectrumPoints];
        float atRest = MathF.PI / 6f / PeakForceCoefficient;     // ĝ(0) = ∫ shape = the momentum
        double prevU = 0, prevG2 = 0, sum = 0;
        float peak = BlowPeakAt, fall = BlowFall;
        for (int i = 0; i < SpectrumPoints; i++)
        {
            double u = SpectrumLoU * Math.Pow(SpectrumHiU / SpectrumLoU, i / (double)(SpectrumPoints - 1));
            double w = 2 * Math.PI * u;
            double re = 0, im = 0;
            const int n = 200;
            for (int k = 0; k < n; k++)
            {
                double x = (k + 0.5) / n * peak;
                double g = Math.Sqrt(x / peak) * peak / n;
                re += g * Math.Cos(w * x); im -= g * Math.Sin(w * x);
            }
            // ∫ e^{-(x-p)/d} e^{-iwx} dx from p = e^{-iwp} d / (1 + i w d)
            double denRe = 1, denIm = w * fall, den = denRe * denRe + denIm * denIm;
            double qRe = fall * denRe / den, qIm = -fall * denIm / den;
            double cRe = Math.Cos(w * peak), cIm = -Math.Sin(w * peak);
            re += cRe * qRe - cIm * qIm; im += cRe * qIm + cIm * qRe;
            double g2 = re * re + im * im;
            magnitude[i] = (float)(Math.Sqrt(g2) / atRest);
            sum += i == 0 ? 2 * g2 * u : (g2 + prevG2) * (u - prevU);
            cum[i] = (float)sum;
            prevU = u; prevG2 = g2;
        }
        return cum;
    }

    /// <summary>2 ∫₀ᵘ |ĝ|² at u = f τ, interpolated in log u.</summary>
    private static float SpectrumBelow(float u)
    {
        if (u <= SpectrumLoU) return SpectrumCumulative[0] * u / SpectrumLoU;
        if (u >= SpectrumHiU) return SpectrumCumulative[^1];
        float at = MathF.Log(u / SpectrumLoU) / MathF.Log(SpectrumHiU / SpectrumLoU) * (SpectrumPoints - 1);
        int i = Math.Min(SpectrumPoints - 2, (int)at);
        float frac = at - i;
        return SpectrumCumulative[i] + (SpectrumCumulative[i + 1] - SpectrumCumulative[i]) * frac;
    }

    /// <summary>|F̂(f)| over |F̂(0)| = the impulse: how much of the blow's momentum reaches a mode at
    /// f, for its τ.</summary>
    public static float BlowMagnitude(float tau, float hz)
    {
        float u = hz * tau;
        if (u <= SpectrumLoU) return 1f;
        if (u >= SpectrumHiU) return SpectrumMagnitude[^1];
        float at = MathF.Log(u / SpectrumLoU) / MathF.Log(SpectrumHiU / SpectrumLoU) * (SpectrumPoints - 1);
        int i = Math.Min(SpectrumPoints - 2, (int)at);
        float frac = at - i;
        return SpectrumMagnitude[i] + (SpectrumMagnitude[i + 1] - SpectrumMagnitude[i]) * frac;
    }

    /// <summary>The share of a blow's energy between two frequencies, 0..1, for its τ.</summary>
    public static float BlowShare(float tau, float loHz, float hiHz)
        => MathF.Max(0f, SpectrumBelow(hiHz * tau) - SpectrumBelow(loHz * tau)) / BlowShapeEnergy;

    /// <summary>∫F² dt of the blow, N²·s.</summary>
    public static float BlowEnergy(float diameterMm, float speed)
    {
        float f = PeakForce(diameterMm, speed);
        return f * f * BlowSeconds(diameterMm, speed) * BlowShapeEnergy;
    }

    /// <summary>The energy one blow puts into the plate between two frequencies, J.</summary>
    public float EnergyIn(float diameterMm, float speed, float loHz, float hiHz)
        => Mobility * BlowEnergy(diameterMm, speed) * BlowShare(BlowSeconds(diameterMm, speed), loHz, hiHz);

    /// <summary>The near-field thud's peak at r from the struck spot, Pa: ρ0 F / (2π m″ r).</summary>
    public float ForcedPeak(float diameterMm, float speed, float r)
        => WallTransmission.AirDensity * PeakForce(diameterMm, speed) / (2f * MathF.PI * SurfaceDensity * MathF.Max(0.05f, r));

    /// <summary>
    /// The steady mean-square pressure, Pa², that rain at this rate makes on one face of a large
    /// plate of this kind, at a listener whose view of the plate is <paramref name="viewFactor"/>
    /// (∫ dA / r² over the plate, the same factor every surface source uses): the ringing field in
    /// octave bands from 31.5 Hz to 16 kHz, plus the near-field thuds. Analytic — the synthesiser
    /// renders the same physics event by event, and the tests hold the two together.
    /// </summary>
    public float MeanSquarePressure(float rate, float viewFactor, bool includeForced = true)
    {
        if (!(rate > 0f) || viewFactor <= 0f) return 0f;
        double total = 0;
        float closure = Rainfall.Closure(rate), lambda = Rainfall.Lambda(rate);
        const int steps = 120;
        float dD = (Rainfall.LargestDropMm - Rainfall.SmallestDropMm) / steps;
        for (int k = 0; k < steps; k++)
        {
            float d = Rainfall.SmallestDropMm + (k + 0.5f) * dD;
            float v = Rainfall.TerminalSpeed(d);
            float flux = closure * Rainfall.MarshallPalmerN0 * MathF.Exp(-lambda * d) * v * dD;  // per m² s
            float tau = BlowSeconds(d, v), blow = BlowEnergy(d, v);
            double ring = 0;
            for (float f = 31.5f; f < 20000f; f *= 2f)
            {
                float lo = f / MathF.Sqrt(2f), hi = f * MathF.Sqrt(2f);
                double eIn = Mobility * blow * BlowShare(tau, lo, hi);       // J per blow
                double w = 2f * MathF.PI * f * Loss(f);                           // 1/s
                // Energy per m² of plate in steady state is flux · eIn / w; it radiates ρc σ E / m″
                // per m², which spreads to the ear as ρc G / 2π.
                ring += eIn / w * AirImpedance * RadiationEfficiency(f) / SurfaceDensity;
            }
            double p2 = flux * ring * AirImpedance * viewFactor / (2.0 * Math.PI);
            if (includeForced)
            {
                // ρ0² ∫F² dt / (4π² m″²) per blow at a metre, times the flux over the view.
                double forced = WallTransmission.AirDensity * WallTransmission.AirDensity * blow
                              / (4.0 * Math.PI * Math.PI * SurfaceDensity * SurfaceDensity);
                p2 += flux * forced * viewFactor;
            }
            total += p2;
        }
        return (float)total;
    }
}

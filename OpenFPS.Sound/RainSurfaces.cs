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
/// How rain sounds on each kind of surface, from the material's properties: a thin stiff skin is a plate
/// the drops ring, thicker is ground they splash on, soft yields. Water and Foliage are named because no
/// property tells them from their neighbours (a fluid; leaves in the air, which grass blades are not).
/// </summary>
public static class RainSurfaces
{
    /// <summary>A skin thinner than this is a plate the drops set ringing, m: sheet steel, glazing, a
    /// timber board are under it; a slab, a road or a brick wall far over it.</summary>
    public const float PlateMaxSkinMetres = 0.02f;

    /// <summary>Under this Young's modulus a material yields under a drop rather than stopping it, GPa:
    /// grass, soil, foam, rubber.</summary>
    public const float SoftBelowGPa = 1f;

    /// <summary>The kind of surface a material of this skin thickness is to rain.</summary>
    public static RainSurfaceKind KindOf(string material, float skinMetres)
    {
        if (string.Equals(material, "Water", StringComparison.OrdinalIgnoreCase)) return RainSurfaceKind.Pool;
        if (string.Equals(material, "Foliage", StringComparison.OrdinalIgnoreCase)) return RainSurfaceKind.Canopy;
        // Generic quietly: the registry's fallback warns per call, and a surveyor asks about every box.
        var p = AcousticRegistry.GetProperties(AcousticRegistry.IsKnown(material) ? material : "Generic");
        if (p.DensityKgM3 <= 0f) return RainSurfaceKind.None;
        if (p.YoungsModulusGPa < SoftBelowGPa) return RainSurfaceKind.Soft;
        if (skinMetres > 0f && skinMetres <= PlateMaxSkinMetres) return RainSurfaceKind.Plate;
        return RainSurfaceKind.Hard;
    }

    // ── Hard ground ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The share of a hard surface standing in puddles, for a rain rate. A road's film is far thinner
    /// than a drop (about a third of a millimetre at 10 mm/h on a 2 % crossfall five metres wide), and a
    /// drop makes a bubble only in a pool about as deep as itself; so only the low places count. How much
    /// of a street that is is not in the map: an ASSUMPTION, a few per cent in light rain rising toward
    /// <see cref="PuddleMaxShare"/>, to be replaced by the ground's own data when the map carries it.
    /// </summary>
    public static float PuddleShare(float rate)
        => rate > 0f ? PuddleMaxShare * rate / (rate + PuddleHalfRate) : 0f;

    /// <summary>The most of a hard surface that is ever puddle (see <see cref="PuddleShare"/>).</summary>
    public const float PuddleMaxShare = 0.08f;

    /// <summary>The rate at which the low places are half as full as they get, mm/h.</summary>
    public const float PuddleHalfRate = 5f;

    // ── The splash ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// How long a drop takes to spread to its widest, s: the time scale of the click (RainSynth.Click).
    /// t = 8/3 · D / v (Pasandideh-Fard, Qiao, Chandra and Mostaghimi 1996, Phys. Fluids 8, 650-659),
    /// about 2-3 D / v measured at raindrop speeds. On a wet road the crown the sheet lifts lives about as
    /// long (Cossali, Coghe and Marengo 1997, Exp. Fluids 22, 463-472).
    /// </summary>
    public static float SplashSeconds(float diameterMm, float speed) => SpreadFactor * diameterMm * 1e-3f / MathF.Max(0.1f, speed);

    /// <summary>The 8/3 in the spreading time (see <see cref="SplashSeconds"/>).</summary>
    public const float SpreadFactor = 8f / 3f;

    // ── Soft ground ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// How many times longer a drop takes to stop on this material than on something rigid. To a drop
    /// (ρ v² a few tens of kPa) even turf is rigid; what changes is what it meets first, a blade that
    /// bends or a crumb that gives, spreading the momentum and taking the top off the click. A quarter of
    /// a transit time longer per decade softer than <see cref="SoftBelowGPa"/>: grass 1.6, soil 1.3,
    /// gravel 1.1, leaves 1.5. An ASSUMPTION on the size, for the ear to judge against recordings.
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
    /// The diameter the water a canopy catches drips off it at, mm. Throughfall under broadleaf trees
    /// carries 4-6 mm drops that rain almost never has (Nanko, Hotta and Suzuki 2006, J. Hydrology 329,
    /// 422-431): the slow, heavy plop under a tree.
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
    /// What a car's headliner (5-10 mm of foam and fabric) takes off the roof's sound into the cabin, per
    /// mixer band as amplitude: the shape of a thin porous lining, little below a quarter wavelength of
    /// its thickness, most of the sound by 4-8 kHz. An ASSUMPTION on the figures.
    /// </summary>
    public static readonly (float Low, float Mid, float High) HeadlinerGains = (1f, 0.75f, 0.45f);

    // ── The listener's own body ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// The rain on your own head and shoulders: ten times nearer than the ground, so each drop is a
    /// hundred times the energy at the ear and stands out one by one. An adult's head is about 15 by
    /// 20 cm (π/4 · 0.15 · 0.2 ≈ 0.024 m²), its crown 12 cm above the ear canal; shoulders 40 by 25 cm
    /// less the neck (about 0.08 m²), 15-25 cm out and 18 cm below the ears. Hair and a coat are both
    /// <see cref="BodyMaterial"/>. ASSUMPTION: no hood, no hat, no umbrella.
    /// </summary>
    public const float HeadSquareMetres = 0.024f, HeadRadiusMetres = 0.09f, CrownAboveEarMetres = 0.12f;

    /// <summary>The shoulders: area, m²; how far out from between the ears they start and end, m;
    /// how far below the ears they are, m. See <see cref="HeadSquareMetres"/>.</summary>
    public const float ShouldersSquareMetres = 0.08f, ShouldersInnerMetres = 0.12f, ShouldersOuterMetres = 0.24f,
                       ShouldersBelowEarMetres = 0.18f;

    /// <summary>What a drop landing on the listener lands on.</summary>
    public const string BodyMaterial = "Skin";

    // ── Built panels ─────────────────────────────────────────────────────────────────────────────

    /// <summary>The free span of a built panel between its supports (purlins, glazing bars), m: a map's
    /// box is a whole roof, but what rings is one bay of it.</summary>
    public const float BuiltBayMetres = 1.2f;
}

/// <summary>
/// A thin sheet the rain lands on, as the physics of a plate: what a drop's blow puts into it and
/// what it gives back to the air.
///
/// The blow: a drop spreads rather than rebounds, handing over m v over about τ = D / v. The force rises
/// as √t (Philippi, Lagrée and Antkowiak 2016, J. Fluid Mech. 795, 96-135) to about 0.8 ρ v² D² at a
/// fifth of τ (Gordillo, Sun and Cheng 2018, J. Fluid Mech. 840, 190-214; Mitchell et al. 2019, J. Fluid
/// Mech. 867, 300-322), then an exponential whose length makes the pulse carry m v (0.52 τ). The √t
/// onset keeps the top octaves (9 dB an octave above 1/τ): a 2 mm drop at 6.5 m/s has τ 0.3 ms, its
/// energy peaks near a kilohertz and 8 kHz is 10 dB under it.
///
/// What goes in: the infinite plate's point mobility, real and flat, Y = 1 / (8 √(B m″)) (Cremer, Heckl
/// and Petersson, Structure-Borne Sound, ch. 5), times the blow's energy in the band. A light sheet takes
/// orders of magnitude more than a slab (0.8 mm steel against 150 mm concrete: 40 dB).
///
/// What comes out: the plate's field decays at its total loss factor
/// (<see cref="WallTransmission.TotalLossFactor"/> for a built panel) and radiates P = ρ0 c0 S σ ⟨v²⟩. Thin
/// steel is far below coincidence and radiates from its edges; glass is at coincidence by 2 kHz (the
/// brighter tick). Below the modal overlap a bay rings at its own notes (the drumming of a pane or a car
/// roof), above it the field is noise. And the struck spot's near field is p = ρ0 F(t) / (2π m″ r), a
/// thud with the blow's own shape.
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

    public MaterialProperties Properties => Custom ?? AcousticRegistry.GetProperties(AcousticRegistry.IsKnown(Material) ? Material : "Generic");

    /// <summary>The stuff itself, for a plate of something the registry has no name for (a boat's
    /// aluminium or fibreglass hull, ShoreSynth). Null: <see cref="Material"/>'s registry entry.</summary>
    public MaterialProperties? Custom { get; init; }

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
    /// The radiation efficiency of one bay. Below coincidence a plate radiates from its edges (Maidanik
    /// 1962, J. Acoust. Soc. Am. 34, 809; the edge-mode term as in Bies and Hansen's Engineering Noise
    /// Control): σ = U c / (π² S fc) · √(f / fc), U the perimeter. Above it σ = 1 / √(1 − fc / f), held
    /// under <see cref="MaxRadiationEfficiency"/>. Accurate to a few decibels, the model's grain.
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

    /// <summary>
    /// The steady mean-square pressure, Pa², of rain at this rate on one face of a large plate, for a
    /// listener whose view of it is <paramref name="viewFactor"/> (∫ dA / r²): the ringing field in
    /// octaves from 31.5 Hz to 16 kHz plus the near-field thuds. The synthesiser renders the same physics
    /// event by event, and the tests hold the two together.
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

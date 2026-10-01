using System;
using System.Numerics;

namespace OpenFPS.Common;

/// <summary>
/// The three bands a path's gains are applied in, and what each band's single figure means.
///
/// The mixer filters every voice with FMOD's THREE_EQ, which splits at <see cref="LowCrossoverHz"/> and
/// <see cref="HighCrossoverHz"/> (its defaults, set explicitly where the DSP is made so the two cannot
/// drift apart). A wall's loss changes by tens of decibels across each of those bands, so a band's figure
/// is not the loss at one frequency in it: it is the transmitted ENERGY averaged over the band's
/// one-third octaves (ISO 266 centres), with equal weight per third octave — a pink spectrum, the
/// reference spectrum of ISO 717-1's spectrum adaptation terms. The low band starts at 50 Hz, the bottom
/// of ISO 717-1's extended range; the high band stops at 12.5 kHz.
///
/// Steam Audio's direct simulation does not filter anything itself here: it multiplies the transmission
/// figures of the faces its rays cross and the engine reads the products back
/// (SteamAudioSimulator.GetResult), so the triple handed to a Steam Audio material IS these three bands,
/// whatever Steam Audio's own band centres are. Its reflection simulation never reads transmission, so
/// absorption stays at Steam Audio's centres (SteamAudioScene.SteamAudioBandsHz).
/// </summary>
public static class AcousticBands
{
    /// <summary>The THREE_EQ low/mid crossover, Hz (FMOD's default).</summary>
    public const float LowCrossoverHz = 400f;
    /// <summary>The THREE_EQ mid/high crossover, Hz (FMOD's default).</summary>
    public const float HighCrossoverHz = 4000f;

    /// <summary>The one-third octave centres (ISO 266) each band's figure is averaged over.</summary>
    public static readonly float[] LowThirdsHz = { 50f, 63f, 80f, 100f, 125f, 160f, 200f, 250f, 315f };
    /// <inheritdoc cref="LowThirdsHz"/>
    public static readonly float[] MidThirdsHz = { 400f, 500f, 630f, 800f, 1000f, 1250f, 1600f, 2000f, 2500f, 3150f };
    /// <inheritdoc cref="LowThirdsHz"/>
    public static readonly float[] HighThirdsHz = { 4000f, 5000f, 6300f, 8000f, 10000f, 12500f };

    /// <summary>The geometric centre of each band's range, Hz: where a single-frequency figure for the
    /// band would be read (about 140 Hz, 1.3 kHz and 7 kHz).</summary>
    public static readonly (float Low, float Mid, float High) GeometricCentresHz =
        (Centre(LowThirdsHz), Centre(MidThirdsHz), Centre(HighThirdsHz));

    private static float Centre(float[] thirds)
    {
        // A third-octave band runs from its centre / 2^(1/6) to its centre * 2^(1/6).
        float lo = thirds[0] / MathF.Pow(2f, 1f / 6f), hi = thirds[^1] * MathF.Pow(2f, 1f / 6f);
        return MathF.Sqrt(lo * hi);
    }
}

/// <summary>
/// How a wall is built, beyond what it is made of: two leaves of the material with a cavity between, or
/// (the default) one solid panel the full thickness of the box.
/// </summary>
/// <param name="LeafMetres">Thickness of each of the two leaves, metres; 0 for a solid panel. A box too
/// thin to hold two leaves and <see cref="WallTransmission.MinCavityMetres"/> of cavity is one solid
/// sheet of its material.</param>
/// <param name="StudSpacingMetres">Centres of the studs both leaves are fixed to, metres; 0 when the
/// leaves meet only at the edges of the panel (a door's skins at its frame, a glazed unit's spacer).</param>
public readonly record struct WallBuild(float LeafMetres, float StudSpacingMetres)
{
    public static readonly WallBuild Solid = default;
}

/// <summary>
/// What gets through a wall, per band, from what the wall is made of, how thick it is and how it is
/// built. Used by the Steam Audio scene's materials and by the hand-rolled tracer, so the two answer the
/// same question with one model.
///
/// SINGLE PANEL — Sharp, "Prediction methods for the sound transmission of building elements", Noise
/// Control Engineering 11 (1978) 53-63; also Bies &amp; Hansen, Engineering Noise Control, ch. 7:
///   below fc/2: the field-incidence mass law, R = 20 log10(m f) - 47 dB (m in kg/m²);
///   at and above fc: R = 20 log10(m f) - 47 + 10 log10(2 η f / (π fc)), the coincidence region;
///   between: a straight line in log frequency.
/// The critical frequency is fc = c² / (1.8 cL t) with cL = sqrt(E/ρ) (Poisson's ratio neglected; under
/// 5 % on fc for ν ≤ 0.3). η is the TOTAL loss factor, EN 12354-1:2000 Annex C: the material's own
/// (<see cref="MaterialProperties.LossFactor"/>), plus radiation, ρ0 c / (π f m), plus what the edges
/// carry away — the larger of the coupling to the structure a heavy element is built into, m/(485 √f)
/// (EN 12354-1 eq. C.4), and the mounting loss of a light panel fixed in a frame
/// (<see cref="PanelAcoustics.MountedLoss"/>).
///
/// TWO LEAVES — Sharp 1978 again, the double-leaf model (also Fahy, Sound and Structural Vibration,
/// ch. 4): below the mass-air-mass resonance f0 = (1/2π) sqrt(ρ0 c² (m1 + m2) / (d m1 m2)) the wall is
/// the mass law of its whole weight; above it R1 + R2 + 20 log10(f d) - 29 (18 dB per octave) up to
/// fl = c / (2π d), and R1 + R2 + 6 (12 dB per octave) beyond. Studs bridge the leaves, and above the
/// bridge frequency the wall can do no better than R_M + ΔR_B, with Sharp's line-connection
/// ΔR_B = 10 log10(b fc) + 20 log10(m1 / (m1 + m2)) - 18 (b the stud spacing). Each leaf's own
/// coincidence dip comes through R1, R2 and R_M.
///
/// FLANKING — EN 12354-1:2000 eq. 25: each of the separating element's four edges also passes sound by
/// the element next to it, R_ij = (R_i + R_j)/2 + K_ij + 10 log10(S / (l0 l_k)). The adjoining elements
/// are not in the data, so they are taken to be of the same construction (R_i = R_j = R), joined at
/// rigid cross junctions of equal mass (EN 12354-1 Annex E, E.3 with M = 0): K = 8.7 dB straight on
/// (Ff) and 5.7 dB round the corner (Fd, Df). S and the edge lengths are the box's own faces. Flanking
/// then adds a share of the direct transmission that is the same at every frequency (about 2 dB on a
/// storey-high wall), so the wall's figure keeps rising with frequency as the direct path's does —
/// there is no flat ceiling on what a wall takes.
///
/// Gaps, vents and the leaks round a door are not in the data and are not modelled.
/// </summary>
public static class WallTransmission
{
    /// <summary>Speed of sound, m/s, at 20 °C.</summary>
    public const float SoundSpeed = 343f;
    /// <summary>Density of air, kg/m³, at 20 °C.</summary>
    public const float AirDensity = 1.21f;

    /// <summary>The thinnest cavity two leaves are built with: the 6 mm gap of the narrowest sealed
    /// glazing unit (4-6-4, EN 1279). A box thinner than two leaves and this is one solid sheet.</summary>
    public const float MinCavityMetres = 0.006f;

    /// <summary>EN 12354-1 Annex E (E.3), rigid cross junction, M = 0: the straight-on flanking path, dB.</summary>
    public const float KStraightDb = 8.7f;
    /// <summary>EN 12354-1 Annex E (E.3), rigid cross junction, M = 0: the round-the-corner paths, dB.</summary>
    public const float KCornerDb = 5.7f;

    /// <summary>The critical (coincidence) frequency of a plate, Hz: fc = c² / (1.8 cL t).</summary>
    public static float CriticalHz(float youngsPa, float density, float thickness)
    {
        if (youngsPa <= 0f || density <= 0f || thickness <= 0f) return float.PositiveInfinity;
        float cL = MathF.Sqrt(youngsPa / density);
        return SoundSpeed * SoundSpeed / (1.8f * cL * thickness);
    }

    /// <summary>The total loss factor of a panel in a building (EN 12354-1 Annex C): internal, radiation
    /// and edges, the edges being the larger of the structural coupling and the mounting loss.</summary>
    public static float TotalLossFactor(float internalLoss, float surfaceDensity, float hz)
    {
        float radiation = AirDensity * SoundSpeed / (MathF.PI * hz * MathF.Max(0.1f, surfaceDensity));
        float edges = MathF.Max(surfaceDensity / (485f * MathF.Sqrt(hz)), PanelAcoustics.MountedLoss);
        return internalLoss + radiation + edges;
    }

    /// <summary>The field-incidence mass law, dB.</summary>
    public static float MassLawDb(float surfaceDensity, float hz) => 20f * MathF.Log10(surfaceDensity * hz) - 47f;

    /// <summary>A single panel's transmission loss at one frequency, dB (Sharp 1978).</summary>
    public static float SinglePanelDb(float surfaceDensity, float criticalHz, float internalLoss, float hz)
    {
        if (surfaceDensity <= 0f || hz <= 0f) return 0f;
        float r;
        if (!(hz >= 0.5f * criticalHz)) r = MassLawDb(surfaceDensity, hz);
        else if (hz >= criticalHz) r = Coincident(surfaceDensity, criticalHz, internalLoss, hz);
        else
        {
            float lo = MassLawDb(surfaceDensity, 0.5f * criticalHz);
            float hi = Coincident(surfaceDensity, criticalHz, internalLoss, criticalHz);
            float x = MathF.Log(hz / (0.5f * criticalHz)) / MathF.Log(2f);
            r = lo + (hi - lo) * x;
        }
        return MathF.Max(0f, r);
    }

    private static float Coincident(float m, float fc, float eta, float hz)
        => MassLawDb(m, hz) + 10f * MathF.Log10(2f * TotalLossFactor(eta, m, hz) * hz / (MathF.PI * fc));

    /// <summary>The mass-air-mass resonance of two leaves over a cavity, Hz.</summary>
    public static float MassAirMassHz(float m1, float m2, float cavity)
        => MathF.Sqrt(AirDensity * SoundSpeed * SoundSpeed * (m1 + m2) / (cavity * m1 * m2)) / (2f * MathF.PI);

    /// <summary>
    /// Two equal leaves of one material over a cavity, dB at one frequency (Sharp 1978). Bridged by
    /// studs at <paramref name="bridgeSpacing"/> metres; the leaves always meet somewhere, so a spacing
    /// is always given (the panel's own width when nothing else joins them).
    /// </summary>
    public static float DoubleLeafDb(float leafDensity, float criticalHz, float internalLoss, float cavity,
                                     float bridgeSpacing, float hz)
    {
        float m = leafDensity;
        float rM = SinglePanelDb(2f * m, criticalHz, internalLoss, hz);
        float f0 = MassAirMassHz(m, m, cavity);
        if (hz < f0) return rM;
        float r1 = SinglePanelDb(m, criticalHz, internalLoss, hz);
        float fl = SoundSpeed / (2f * MathF.PI * cavity);
        float ideal = hz < fl ? 2f * r1 + 20f * MathF.Log10(hz * cavity) - 29f : 2f * r1 + 6f;
        // The resonance itself is not modelled below the mass law of the whole: a cavity with anything
        // in it damps it.
        ideal = MathF.Max(ideal, rM);
        float bridge = MathF.Max(0f, 10f * MathF.Log10(bridgeSpacing * criticalHz) + 20f * MathF.Log10(0.5f) - 18f);
        return MathF.Max(rM, MathF.Min(ideal, rM + bridge));
    }

    /// <summary>
    /// How much of the direct transmission flanking adds, as a fraction of it (EN 12354-1 eq. 25, same
    /// construction all round): the perimeter over the area, times the three paths at each edge.
    /// </summary>
    public static float FlankingShare(float faceA, float faceB)
    {
        if (faceA <= 0f || faceB <= 0f) return 0f;
        float perimeterOverArea = 2f * (faceA + faceB) / (faceA * faceB);   // l0 = 1 m
        float paths = MathF.Pow(10f, -KStraightDb / 10f) + 2f * MathF.Pow(10f, -KCornerDb / 10f);
        return perimeterOverArea * paths;
    }

    /// <summary>The transmission loss of a box of this material, size and build at one frequency, dB,
    /// flanking included. The box's smallest dimension is its thickness; the other two are its face.</summary>
    public static float BoxLossDb(MaterialProperties p, Vector3 size, WallBuild build, float hz)
    {
        Faces(size, out float t, out float a, out float b);
        float tau = DirectTau(p, t, a, b, build, hz) * (1f + FlankingShare(a, b));
        return -10f * MathF.Log10(MathF.Max(1e-30f, tau));
    }

    /// <summary>
    /// What a box of this material, size and build lets through, per mixer band, as amplitude gains 0..1
    /// (the Transmission convention): the band-averaged transmitted energy (<see cref="AcousticBands"/>),
    /// flanking included, square-rooted. A porous material (<see cref="MaterialProperties.Porous"/>), or one
    /// with no density, lets sound through its holes, and keeps its table figures.
    /// </summary>
    public static (float Low, float Mid, float High) BandGains(string material, Vector3 size, WallBuild build)
        => BandGains(AcousticRegistry.GetProperties(material), size, build);

    /// <inheritdoc cref="BandGains(string, Vector3, WallBuild)"/>
    public static (float Low, float Mid, float High) BandGains(MaterialProperties p, Vector3 size, WallBuild build)
    {
        Faces(size, out float t, out float a, out float b);
        if (p.Porous || p.DensityKgM3 <= 0f || t <= 0f)
            return (p.TransmissionLow, p.TransmissionMid, p.TransmissionHigh);
        float flank = 1f + FlankingShare(a, b);
        float Band(float[] thirds)
        {
            double sum = 0;
            foreach (float hz in thirds) sum += DirectTau(p, t, a, b, build, hz);
            return MathF.Sqrt((float)Math.Min(1.0, sum / thirds.Length * flank));
        }
        return (Band(AcousticBands.LowThirdsHz), Band(AcousticBands.MidThirdsHz), Band(AcousticBands.HighThirdsHz));
    }

    /// <summary>The direct path's transmitted energy fraction at one frequency.</summary>
    private static float DirectTau(MaterialProperties p, float t, float faceA, float faceB, WallBuild build, float hz)
    {
        float e = p.YoungsModulusGPa * 1e9f, rho = p.DensityKgM3, eta = p.LossFactor;
        float leaf = build.LeafMetres;
        float r;
        if (leaf > 0f && t >= 2f * leaf + MinCavityMetres)
        {
            float cavity = t - 2f * leaf;
            // Studs where there are studs; otherwise the leaves meet at the panel's edges, and the
            // nearest two edges are its narrower span apart.
            float bridge = build.StudSpacingMetres > 0f ? build.StudSpacingMetres : MathF.Min(faceA, faceB);
            r = DoubleLeafDb(rho * leaf, CriticalHz(e, rho, leaf), eta, cavity, bridge, hz);
        }
        else r = SinglePanelDb(rho * t, CriticalHz(e, rho, t), eta, hz);
        return MathF.Pow(10f, -r / 10f);
    }

    /// <summary>A box's thickness (its smallest dimension) and the two sides of its face.</summary>
    private static void Faces(Vector3 size, out float thickness, out float a, out float b)
    {
        float x = MathF.Abs(size.X), y = MathF.Abs(size.Y), z = MathF.Abs(size.Z);
        if (x <= y && x <= z) { thickness = x; a = y; b = z; }
        else if (y <= x && y <= z) { thickness = y; a = x; b = z; }
        else { thickness = z; a = x; b = y; }
    }
}

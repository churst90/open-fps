using System.Globalization;
using OpenFPS.Common.Editing;

namespace OpenFPS.Common;

// A domestic gas hob (docs/GAS_HOB.md): a knob on a plug valve, an injector and a mixing tube under each
// burner, a ring of ports under a cap, one spark module for every electrode, and a thermocouple that holds
// the gas on once a flame has heated it. Every sound is one of three things: the pressure of heat released
// unsteadily (the spark, the light-up, the flame, the pop at the end, all one law), a gas jet's mixing
// noise (Lighthill), or a part of the mechanism being struck (a switch, a detent, a stop, the cap).

/// <summary>Which gas the hob burns, which decides its injectors, its pressure, how fast a flame runs
/// through it and how long an unlit cloud of it lingers round the burner.</summary>
public enum HobGas
{
    /// <summary>Natural gas, G20 (methane) at 20 mbar.</summary>
    Natural,
    /// <summary>Propane, G31, at 37 mbar: heavier than air, so an unlit cloud pools.</summary>
    Propane,
}

/// <summary>
/// A fuel gas's properties at 15 °C and one atmosphere. Sources are tagged as in docs/FIRE.md: [ft] read in
/// full, [sec] read in a secondary source, [recalled] general knowledge not checked here.
/// </summary>
public sealed record FuelGas
{
    public required string Name { get; init; }
    /// <summary>Density, kg/m³: EN 437 relative densities 0.555 (G20) and 1.550 (G31) times air's 1.225 [recalled].</summary>
    public required float DensityKgM3 { get; init; }
    /// <summary>Net (lower) heating value, J/m³: EN 437, 34.02 MJ/m³ for G20 and 88.00 for G31 [recalled].
    /// What the flame actually releases.</summary>
    public required float NetHeatJm3 { get; init; }
    /// <summary>Gross heating value, J/m³: 37.78 and 101.74 MJ/m³ [recalled]. Hob ratings are quoted on it
    /// (the Whirlpool AKT 300 manual's 286 l/h for 3.00 kW is 37.8 MJ/m³ [ft]).</summary>
    public required float GrossHeatJm3 { get; init; }
    /// <summary>Air for complete combustion, m³ per m³ of gas: 9.52 for methane, 23.8 for propane.</summary>
    public required float StoichiometricAir { get; init; }
    /// <summary>Flammable limits in air, volume fraction: methane 5-15 % [sec: Mitu 2021 review], propane
    /// 2.1-9.5 % [recalled].</summary>
    public required float LowerLimit { get; init; }
    public required float UpperLimit { get; init; }
    /// <summary>The fastest a laminar flame runs into still mixture, m/s: 0.353-0.375 for stoichiometric
    /// methane [sec: Mitu et al. 2022], 0.43 for propane [recalled].</summary>
    public required float MaxBurningVelocity { get; init; }
    /// <summary>Gülder's (1984) correlation of burning velocity against equivalence ratio: S = W φ^η
    /// exp(-ξ (φ - 1.075)²), scaled here so its peak is <see cref="MaxBurningVelocity"/> [recalled].</summary>
    public required float GulderEta { get; init; }
    public required float GulderXi { get; init; }
    /// <summary>The least spark energy that lights the most ignitable mixture, mJ: 0.29-0.33 for methane
    /// [sec: combustion handbook table, 29-33 × 10⁻⁵ J], 0.25 for propane [sec].</summary>
    public required float MinIgnitionEnergyMj { get; init; }
    /// <summary>The equivalence ratio at which that least energy is found: a little lean for methane.</summary>
    public required float EasiestEquivalenceRatio { get; init; }

    public static FuelGas Methane { get; } = new()
    {
        Name = "natural gas (G20)", DensityKgM3 = 0.680f, NetHeatJm3 = 34.02e6f, GrossHeatJm3 = 37.78e6f,
        StoichiometricAir = 9.52f, LowerLimit = 0.050f, UpperLimit = 0.150f, MaxBurningVelocity = 0.37f,
        GulderEta = 0.15f, GulderXi = 5.18f, MinIgnitionEnergyMj = 0.29f, EasiestEquivalenceRatio = 0.9f,
    };

    public static FuelGas Propane { get; } = new()
    {
        Name = "propane (G31)", DensityKgM3 = 1.90f, NetHeatJm3 = 88.00e6f, GrossHeatJm3 = 101.74e6f,
        StoichiometricAir = 23.8f, LowerLimit = 0.021f, UpperLimit = 0.095f, MaxBurningVelocity = 0.43f,
        GulderEta = 0.12f, GulderXi = 4.95f, MinIgnitionEnergyMj = 0.25f, EasiestEquivalenceRatio = 1.0f,
    };

    public static FuelGas Of(HobGas gas) => gas == HobGas.Propane ? Propane : Methane;

    /// <summary>The gas's volume fraction in a mixture of this equivalence ratio.</summary>
    public float FractionAt(float phi) => phi / (phi + StoichiometricAir);

    /// <summary>The equivalence ratio of a mixture holding this volume fraction of gas.</summary>
    public float EquivalenceRatio(float fraction)
        => fraction <= 0f ? 0f : fraction >= 1f ? 100f : StoichiometricAir * fraction / (1f - fraction);

    /// <summary>Laminar burning velocity, m/s, at this volume fraction: zero outside the flammable limits.</summary>
    public float BurningVelocity(float fraction)
    {
        if (fraction < LowerLimit || fraction > UpperLimit) return 0f;
        float phi = EquivalenceRatio(fraction);
        float s = MathF.Pow(phi, GulderEta) * MathF.Exp(-GulderXi * (phi - 1.075f) * (phi - 1.075f));
        float peak = MathF.Pow(1.075f, GulderEta);
        return MaxBurningVelocity * s / peak;
    }

    /// <summary>
    /// How much the burnt gas has expanded, the factor by which an unconfined flame front outruns its burning
    /// velocity: about the adiabatic flame temperature over the room's, 7.4 near stoichiometric (2230 K
    /// over 298) and falling to about 5 at the lean limit [recalled].
    /// </summary>
    public float ExpansionRatio(float fraction)
    {
        float phi = EquivalenceRatio(fraction);
        return phi <= 1f ? 1f + 6.4f * MathF.Max(0.3f, phi) : MathF.Max(4f, 7.4f - 2f * (phi - 1f));
    }

    /// <summary>
    /// The spark energy a mixture of this fraction needs, mJ. A spark crosses the mixing layer at the edge
    /// of a port's jet, which holds every fraction between nothing and the richest it reaches, so a channel
    /// that reaches past the easiest mixture finds it; short of it, the energy rises steeply toward the
    /// lean limit: fifty times the least at half stoichiometric for methane [estimate, the shape of the
    /// published curves], so a 15 mJ spark lights down to about the lean limit and no further.
    /// </summary>
    public float IgnitionEnergyMj(float richestFraction)
    {
        if (richestFraction < LowerLimit * 0.9f) return float.PositiveInfinity;
        float phi = MathF.Min(EquivalenceRatio(richestFraction), EasiestEquivalenceRatio);
        float x = MathF.Log(MathF.Max(1e-3f, phi) / EasiestEquivalenceRatio);
        return MinIgnitionEnergyMj * MathF.Exp(11.3f * x * x);
    }
}

/// <summary>One burner: its injector, its head and ports, its cap, and where it sits on the hob.</summary>
public sealed record GasBurnerSpec
{
    [Tunable("", 0, 0, "What the burner is called when it is lit: front left, back right.")]
    public string Name { get; init; } = "";
    /// <summary>The injector's bore, mm. With the supply pressure and a discharge coefficient of 0.80 it gives
    /// the burner's gas: 1.28 mm passes 286 l/h of G20 at 20 mbar (3.00 kW gross, Whirlpool AKT 300 manual
    /// [ft]), 0.95 mm 157 l/h (1.65 kW, the same table), 0.72 mm about 90 l/h (1.0 kW, IKEA HGA4K [ft]).</summary>
    [Tunable("mm", 0.3, 3, "The bore of the gas injector under the burner. It decides how much gas flows, and the pitch of its hiss.", Label = "injector bore", Step = 0.01)]
    public required float InjectorMm { get; init; }
    /// <summary>The gas at the knob's low end as a share of full: 0.60/3.00, 0.35/1.65 and 0.30/1.00 kW in the
    /// same manuals [ft], set by the bypass screw in the tap.</summary>
    [Tunable("", 0.05, 0.6, "The gas at the knob's lowest setting as a share of full: the simmer.", Label = "simmer share", Step = 0.01)]
    public float ReducedShare { get; init; } = 0.2f;
    /// <summary>The ring of ports, mm across: 45, 65 and 85 mm for small, middle and large burners [estimate].</summary>
    [Tunable("mm", 20, 200, "Diameter of the ring of flame ports. A light-up runs round it.", Label = "crown diameter", Step = 1)]
    public required float CrownMm { get; init; }
    /// <summary>The ports' total area, mm²: a port loading near 9 W/mm² of gross input [estimate, the usual
    /// design figure for natural gas]. Sets how fast the mixture leaves.</summary>
    [Tunable("mm²", 20, 2000, "Total area of the flame ports. Smaller ports throw the mixture faster.", Label = "port area", Step = 5)]
    public required float PortAreaMm2 { get; init; }
    /// <summary>One port's bore, mm: smaller than methane's 2 mm quenching distance, so the flame cannot run
    /// back into the head [recalled].</summary>
    [Tunable("mm", 0.5, 4, "The bore of one flame port.", Label = "port bore", Step = 0.1)]
    public float PortMm { get; init; } = 1.4f;
    /// <summary>The mixing tube and the head, cm³: what the first gas has to sweep out [estimate].</summary>
    [Tunable("cm³", 2, 200, "Volume of the mixing tube and burner head. The first gas has to sweep it out before it reaches the ports.", Label = "head volume", Step = 1)]
    public required float HeadVolumeCm3 { get; init; }
    /// <summary>The loose cap over the crown: an enamelled steel disc [estimate]. The spark hits its edge and
    /// rings it.</summary>
    [Tunable("mm", 20, 200, "Diameter of the loose cap over the burner. The spark strikes its edge.", Label = "cap diameter", Step = 1)]
    public required float CapMm { get; init; }
    [Tunable("mm", 1, 10, "Thickness of the cap.", Label = "cap thickness", Step = 0.1)]
    public float CapThicknessMm { get; init; } = 3f;
    /// <summary>Across the hob from its middle, m (+x to the right as you face it), and back from the middle
    /// (+z away from you).</summary>
    [Tunable("m", -0.5, 0.5, "How far right of the hob's middle the burner is.", Label = "position right", Step = 0.01)]
    public float RightMetres { get; init; }
    [Tunable("m", -0.5, 0.5, "How far back from the hob's middle the burner is.", Label = "position back", Step = 0.01)]
    public float BackMetres { get; init; }
}

/// <summary>
/// A domestic gas hob (docs/GAS_HOB.md). <see cref="SourceLevelDb"/> and <see cref="PeakHeadroomDb"/> are
/// measured (AudioLab --stove levels); everything else is the hob's own physics or a cited figure.
/// </summary>
public sealed record GasHobSpec
{
    [Tunable("", 0, 0, "What this hob is called.")]
    public string Name { get; init; } = "";
    [Tunable("", 0, 0, "The gas it burns: natural gas or propane.")]
    public HobGas Gas { get; init; } = HobGas.Natural;
    /// <summary>At the appliance, kPa: EN 437's normal pressures, 2.0 for G20 and 3.7 for G31 [recalled].</summary>
    [Tunable("kPa", 0.5, 6, "Gas pressure at the hob. Natural gas is 2.0 kPa in Europe, propane 3.7.", Label = "supply pressure", Step = 0.1)]
    public float SupplyKPa { get; init; } = 2.0f;
    /// <summary>The injectors' discharge coefficient: 0.80 reproduces both manual flows in <see cref="GasBurnerSpec.InjectorMm"/>.</summary>
    [Tunable("", 0.5, 1, "How much of an injector's bore the flow uses. 0.80 matches the manufacturers' tables.", Label = "discharge coefficient", Step = 0.01)]
    public float DischargeCoefficient { get; init; } = 0.80f;
    /// <summary>Air drawn in by the jet as a share of what burning needs: 40-70 % for a cooker burner
    /// [recalled]. The rest the flame takes from the room.</summary>
    [Tunable("", 0.2, 1, "Air the gas jet draws into the mixing tube, as a share of what burning needs.", Label = "primary air", Step = 0.05)]
    public float PrimaryAeration { get; init; } = 0.5f;
    public required GasBurnerSpec[] Burners { get; init; }

    // ── The spark module ─────────────────────────────────────────────────────────────────────────

    /// <summary>Sparks a second while a knob is held in: three to five on a working hob [sec: cookerspareparts];
    /// 3.2-5.6 in fifteen recordings measured here. The module counts mains cycles, so the rate is the
    /// mains frequency over a whole number.</summary>
    [Tunable("/s", 1, 10, "How many sparks a second the module makes while a knob is held in.", Label = "spark rate", Step = 0.1)]
    public float SparkRateHz { get; init; } = 4.2f;
    [Tunable("Hz", 50, 60, "The mains frequency the spark module charges from.", Label = "mains", Step = 10)]
    public float MainsHz { get; init; } = 50f;
    /// <summary>The energy each spark brings to its burner, mJ: "typically greater than 15 mJ" on a
    /// Robertshaw re-ignition module [sec]. Decides what mixture it can light.</summary>
    [Tunable("mJ", 0.5, 100, "Energy of each spark at the burner. It decides how lean a mixture the spark can light.", Label = "spark energy", Step = 0.5)]
    public float SparkEnergyMj { get; init; } = 15f;
    /// <summary>
    /// What of it heats the air in the gap at once, mJ. A spark's sound is that heating: the hot channel's
    /// volume jumps by (γ-1)E/(γp), a monopole, p = (γ-1)/(4πrc²) dQ/dt, the law the flame obeys. A piezo
    /// lighter's 3 mm spark, 250 Pa at 12 cm with a 2.2 µs half-duration (Scheuer and DeCorby 2024 [ft]),
    /// carries 0.36 mJ of it by that law; a mains module about four times that [estimate].
    /// </summary>
    [Tunable("mJ", 0.05, 20, "The part of each spark's energy that heats the air in the gap at once: the crack.", Label = "spark heat", Step = 0.05)]
    public float SparkHeatMj { get; init; } = 1.5f;
    /// <summary>How long the heat goes in, µs: fitted to the third-octave peak of the ticks in fifteen
    /// recordings, 6.3-10 kHz.</summary>
    [Tunable("µs", 2, 200, "How long the spark takes to heat the gap. Longer is a duller tick.", Label = "spark duration", Step = 1)]
    public float SparkMicroseconds { get; init; } = 30f;
    /// <summary>The electrode's tip above the hob top, mm: the hob's steel answers every spark from its
    /// image, a hair later.</summary>
    [Tunable("mm", 2, 80, "How high the spark is above the hob's steel top.", Label = "spark height", Step = 1)]
    public float SparkHeightMm { get; init; } = 18f;
    /// <summary>The module's own tick (its transformer and switch), dB peak at a metre, inside the hob [estimate].</summary>
    [Tunable("dB", 20, 90, "Peak level at a metre of the spark module's own tick inside the hob.", Label = "module tick level", Step = 1)]
    public float ModuleTickDb { get; init; } = 52f;
    /// <summary>What share of a spark's crack the cap rings back, dB [estimate, the tails of the recorded ticks].</summary>
    [Tunable("dB", -60, 0, "How loud the cap rings when the spark strikes it, against the crack.", Label = "cap ring", Step = 1)]
    public float CapRingDb { get; init; } = -24f;

    // ── The gas round the burner before it lights ───────────────────────────────────────────────

    /// <summary>
    /// How long unlit gas lingers round a burner, s: it rises out from under the cap and pan supports (methane)
    /// or pools in the burner's well (propane, half as heavy again as air) [estimate]. This sets how big the
    /// light-up is after the sparks have failed for a while.
    /// </summary>
    [Tunable("s", 0.1, 20, "How long unlit gas lingers round the burner before it drifts away.", Label = "gas linger time", Step = 0.1)]
    public float CloudSeconds { get; init; } = 1.5f;
    /// <summary>The port velocity at which the jets carry their mixture as far as the electrode, m/s
    /// [estimate]: well under full flow, well over the simmer's, which is why a hob is lit on high.</summary>
    [Tunable("m/s", 0.1, 3, "How fast the mixture must leave the ports to reach the spark.", Label = "reach velocity", Step = 0.05)]
    public float ReachVelocity { get; init; } = 0.6f;

    // ── The flame ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The flames' acoustic power over their heat release at a port velocity of 1.5 m/s, rising as its
    /// square. Laminar flamelets wrinkled only by the mixing tube's turbulence: 1.1e-10, a hundred times
    /// under Shivashankara's (1973) small turbulent premixed burners (1e-8 to 1e-7 [sec: docs/FIRE.md]).
    /// Fitted: it puts a large burner's roar on high 40 dB under its sparks' peaks, the median of the
    /// recordings measured here.
    /// </summary>
    [Tunable("", 1e-12, 1e-6, "Share of the flames' heat that comes out as sound, at full flow. Fitted to recordings.", Label = "acoustic efficiency", Step = 1e-11)]
    public float FlameEfficiency { get; init; } = 1.1e-10f;
    /// <summary>The roar's slope above its peak, as a power of frequency: 2.1-3.4 measured on turbulent
    /// flames (docs/FIRE.md 1.3); 5/2 from Kolmogorov turbulence (Clavin and Siggia 1991). 2.2 fits the
    /// median of thirteen recorded hob flames, -3.5 dB an octave from 250 Hz to 2 kHz.</summary>
    [Tunable("", 1, 4, "How steeply the flame's roar falls above its peak.", Label = "roar slope", Step = 0.1)]
    public float FlameSlope { get; init; } = 2.2f;
    /// <summary>How tall the flames stand on full, mm: the blue cones and the mantle round them [estimate]. A
    /// diffusion flame's height goes with its flow, so the time the mixture takes through it, which sets
    /// the roar's peak, does not change with the knob.</summary>
    [Tunable("mm", 3, 80, "How tall the flames stand on full. Sets the pitch of the roar's peak.", Label = "flame height", Step = 1)]
    public float FlameHeightMm { get; init; } = 12f;
    /// <summary>How much the flames' loudness wavers with the air in the room, as a share [estimate].</summary>
    [Tunable("", 0, 0.6, "How much the flames waver in the room's air.", Label = "flicker", Step = 0.01)]
    public float Flicker { get; init; } = 0.15f;
    /// <summary>The light-up's acoustic power over its heat release. Its front runs through a patchy, moving
    /// cloud, a turbulent premixed flame: 1e-8 to 1e-7 for small open premixed burners (Shivashankara 1973
    /// [sec: docs/FIRE.md 1.4]). At 1e-8 the first 150 ms of a light-up stand 15-20 dB over the steady flame,
    /// as the median of nine recorded light-ups.</summary>
    [Tunable("", 1e-11, 1e-5, "Share of the light-up's heat that comes out as sound: a turbulent flame front.", Label = "light-up efficiency", Step = 1e-9)]
    public float LightUpEfficiency { get; init; } = 1e-8f;
    /// <summary>Where the injectors' hiss sits against Lighthill's law with K = 1e-4, dB, as the aircraft's
    /// and the air brakes' jets do. Zero is the textbook.</summary>
    [Tunable("dB", -30, 20, "Where the gas jets' hiss sits against Lighthill's law. Zero is the textbook.", Label = "hiss trim", Step = 0.5)]
    public float JetTrimDb { get; init; }

    // ── The flame failure device ─────────────────────────────────────────────────────────────────

    /// <summary>A thermocouple in the flame drives a magnet that holds the gas valve open once the knob is
    /// let go; it takes a few seconds to heat (EN 30-1-1 allows up to 10 s on a hob [recalled]) and tens of
    /// seconds to cool, when the armature drops with a click.</summary>
    [Tunable("s", 0.5, 20, "How long the flame safety thermocouple takes to heat.", Label = "thermocouple heating", Step = 0.1)]
    public float ThermocoupleHeatSeconds { get; init; } = 2.5f;
    [Tunable("s", 2, 120, "How long it takes to cool once the flame is out. The safety valve clicks shut at the end of it.", Label = "thermocouple cooling", Step = 1)]
    public float ThermocoupleCoolSeconds { get; init; } = 15f;
    /// <summary>The share of full voltage that holds the magnet, and the share it lets go under.</summary>
    [Tunable("", 0.1, 0.9, "The share of its full voltage the thermocouple needs to hold the gas on.", Label = "hold share", Step = 0.05)]
    public float HoldShare { get; init; } = 0.55f;
    [Tunable("", 0.05, 0.8, "The share under which the safety magnet lets go.", Label = "drop share", Step = 0.05)]
    public float DropShare { get; init; } = 0.35f;
    /// <summary>How long the cook keeps the knob in after the flame catches, s: manuals ask for a few seconds.</summary>
    [Tunable("s", 0, 20, "How long the cook keeps the knob held in after the flame catches.", Label = "hold after lighting", Step = 0.5)]
    public float HoldAfterLightSeconds { get; init; } = 3.5f;

    // ── The knob's parts, dB peak at a metre [estimate: small switches and plastic on steel] ─────

    [Tunable("dB", 20, 90, "Peak at a metre of the ignition switch behind the knob snapping over.", Label = "switch click level", Step = 1)]
    public float SwitchClickDb { get; init; } = 56f;
    [Tunable("dB", 20, 90, "Peak at a metre of the knob dropping into its detent at full.", Label = "detent level", Step = 1)]
    public float DetentClickDb { get; init; } = 52f;
    [Tunable("dB", 20, 90, "Peak at a metre of the knob reaching its stop.", Label = "stop level", Step = 1)]
    public float StopClickDb { get; init; } = 58f;
    [Tunable("dB", 20, 90, "Peak at a metre of the safety magnet's armature dropping.", Label = "safety valve click level", Step = 1)]
    public float MagnetClickDb { get; init; } = 46f;

    // ── Level ────────────────────────────────────────────────────────────────────────────────────

    [Tunable("dB", 10, 120, "Overall level at one metre with every burner on full. Measured with --stove levels; change it only after measuring again.", Label = "level at one metre", Step = 0.5, Source = "MEASURED with --stove levels")]
    public required float SourceLevelDb { get; init; }
    /// <summary>The sparks' peaks over <see cref="SourceLevelDb"/>, dB, measured.</summary>
    public float PeakHeadroomDb { get; init; } = 50f;
    [Tunable("m", 0.05, 3, "How big the hob is as a source: inside it the level is flat.", Label = "extent", Step = 0.05)]
    public float ExtentMetres { get; init; } = 0.6f;

    public FuelGas Fuel => FuelGas.Of(Gas);

    // ── The knob and its valve ───────────────────────────────────────────────────────────────────
    //
    // A plug cock: off at 0°, full at 90° anticlockwise (the big flame mark, where it is lit), and on round
    // to the small flame mark at 240°, where only the bypass screw passes gas.

    public const float FullDegrees = 90f;
    public const float LowDegrees = 240f;
    public const float MediumDegrees = 165f;

    /// <summary>The knob's angle for a setting: 0 off, 1 low, 2 medium, 3 high.</summary>
    public static float AngleFor(int setting) => setting switch
    {
        <= 0 => 0f,
        1 => LowDegrees,
        2 => MediumDegrees,
        _ => FullDegrees,
    };

    /// <summary>
    /// The gas the valve passes at this knob angle, as a share of full. The port in the plug opens between
    /// 20° and 80°; past the full mark a tapered groove closes it to the bypass at the low mark.
    /// </summary>
    public static float FlowShare(float degrees, float reducedShare)
    {
        if (degrees <= 20f) return 0f;
        if (degrees < 80f) { float x = (degrees - 20f) / 60f; return x * x * (3f - 2f * x); }
        if (degrees <= 95f) return 1f;
        float s = Math.Clamp((degrees - 95f) / (LowDegrees - 95f), 0f, 1f);
        return reducedShare + (1f - reducedShare) * MathF.Pow(1f - s, 1.5f);
    }

    /// <summary>The injector's exit velocity at full, m/s: Cd √(2ΔP/ρ). At a share F of full flow the
    /// injector takes F² of the pressure and the valve the rest, so its jet is F times as fast.</summary>
    public float InjectorVelocity() => DischargeCoefficient * MathF.Sqrt(2f * SupplyKPa * 1000f / Fuel.DensityKgM3);

    /// <summary>Gas through a burner at full, m³/s.</summary>
    public float FullFlow(GasBurnerSpec b)
        => MathF.PI * 0.25f * (b.InjectorMm * 1e-3f) * (b.InjectorMm * 1e-3f) * InjectorVelocity();

    /// <summary>A burner's input as a hob's plate would quote it, kW gross.</summary>
    public float RatedKw(GasBurnerSpec b) => FullFlow(b) * Fuel.GrossHeatJm3 / 1000f;

    /// <summary>The heat a burner's flame releases at full, W (net heating value).</summary>
    public float FullHeatWatts(GasBurnerSpec b) => FullFlow(b) * Fuel.NetHeatJm3;

    /// <summary>The share of gas in the mixture leaving the ports: the gas and the air its jet draws in.</summary>
    public float PortFraction => 1f / (1f + PrimaryAeration * Fuel.StoichiometricAir);

    /// <summary>The mixture leaving a burner's ports at full flow, cold, m/s.</summary>
    public float PortVelocity(GasBurnerSpec b)
        => FullFlow(b) / PortFraction / (b.PortAreaMm2 * 1e-6f);

    /// <summary>The roar's peak, Hz: the time the mixture takes through the flames, port velocity over flame
    /// height, about 110 Hz on full and the same at any setting [derived].</summary>
    public float FlamePeakHz(GasBurnerSpec b) => PortVelocity(b) / (FlameHeightMm * 1e-3f);

    /// <summary>Where the mixture round a burner is gathered before it lights, m³: a layer three centimetres
    /// deep over the crown and a hand's width round it [estimate].</summary>
    public static float CloudVolume(GasBurnerSpec b)
    {
        float r = 0.5f * b.CrownMm * 1e-3f + 0.03f;
        return MathF.PI * r * r * 0.03f;
    }

    /// <summary>The pressure, Pa at a metre, per watt a second of change in heat release: (γ-1)/(4π c²), the
    /// monopole law of an unsteady flame (Dowling and Mahmoudi 2015 [ft, docs/FIRE.md 1.1]).</summary>
    public const float MonopolePerWattPerSecond = 0.4f / (4f * MathF.PI * 343f * 343f);

    // ── Presets ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>The burners of a 60 cm four-burner hob: a large one front left, two middle ones at the back,
    /// a small one front right, injectors for G20.</summary>
    private static GasBurnerSpec[] FourBurners(float injectorScale) => new[]
    {
        new GasBurnerSpec
        {
            Name = "front left", InjectorMm = 1.28f * injectorScale, ReducedShare = 0.20f, CrownMm = 85f, PortAreaMm2 = 333f,
            HeadVolumeCm3 = 35f, CapMm = 85f, CapThicknessMm = 3.5f, RightMetres = -0.15f, BackMetres = -0.11f,
        },
        new GasBurnerSpec
        {
            Name = "front right", InjectorMm = 0.72f * injectorScale, ReducedShare = 0.30f, CrownMm = 45f, PortAreaMm2 = 111f,
            HeadVolumeCm3 = 12f, CapMm = 45f, CapThicknessMm = 2.5f, RightMetres = 0.15f, BackMetres = -0.11f,
        },
        new GasBurnerSpec
        {
            Name = "back left", InjectorMm = 0.95f * injectorScale, ReducedShare = 0.21f, CrownMm = 65f, PortAreaMm2 = 194f,
            HeadVolumeCm3 = 20f, CapMm = 65f, CapThicknessMm = 3f, RightMetres = -0.15f, BackMetres = 0.11f,
        },
        new GasBurnerSpec
        {
            Name = "back right", InjectorMm = 0.95f * injectorScale, ReducedShare = 0.21f, CrownMm = 65f, PortAreaMm2 = 194f,
            HeadVolumeCm3 = 20f, CapMm = 65f, CapThicknessMm = 3f, RightMetres = 0.15f, BackMetres = 0.11f,
        },
    };

    /// <summary>A 60 cm four-burner hob on natural gas, with flame failure devices and one spark module.</summary>
    public static GasHobSpec FourBurnerNatural => new()
    {
        Name = "Four-burner gas hob, natural gas",
        Gas = HobGas.Natural,
        SupplyKPa = 2.0f,
        Burners = FourBurners(1f),
        // MEASURED with `--stove levels` 2026-10-10: every burner on full, Leq 47.0 dB (41.4 dB(A)) at a metre; the
        // sparks' peaks reach 89.7 dB, 42.8 dB over it. The large burner alone on full 43.3 dB, on low 22.4.
        SourceLevelDb = 47f,
        PeakHeadroomDb = 45f,
        ExtentMetres = 0.6f,
    };

    /// <summary>The same hob on propane at 37 mbar: injectors about two thirds the bore (CDA HCG301's set,
    /// 0.65-0.95 mm against 0.97-1.35 [ft]), and an unlit cloud that pools rather than rising away.</summary>
    public static GasHobSpec FourBurnerPropane => FourBurnerNatural with
    {
        Name = "Four-burner gas hob, propane",
        Gas = HobGas.Propane,
        SupplyKPa = 3.7f,
        Burners = FourBurners(0.68f),
        CloudSeconds = 4f,
        // MEASURED with `--stove levels` 2026-10-10: every burner on full, Leq 45.3 dB (39.0 dB(A)); sparks 45.6 dB over.
        SourceLevelDb = 45.5f,
        PeakHeadroomDb = 47f,
    };

    /// <summary>One middle-sized burner on its own (a domino hob), natural gas.</summary>
    public static GasHobSpec SingleBurner => FourBurnerNatural with
    {
        Name = "Single gas burner, natural gas",
        Burners = new[] { FourBurners(1f)[2] with { Name = "burner", RightMetres = 0f, BackMetres = 0f } },
        // MEASURED with `--stove levels` 2026-10-10: on full, Leq 40.1 dB (34.7 dB(A)); sparks 43.5 dB over.
        SourceLevelDb = 40f,
        PeakHeadroomDb = 45f,
        ExtentMetres = 0.3f,
    };

    public static IReadOnlyDictionary<string, Func<GasHobSpec>> Presets { get; } =
        new Dictionary<string, Func<GasHobSpec>>(StringComparer.OrdinalIgnoreCase)
        {
            ["hob4"] = () => FourBurnerNatural,
            ["hob4_propane"] = () => FourBurnerPropane,
            ["hob1"] = () => SingleBurner,
        };

    /// <summary>A preset by name or by key, through the <see cref="ModelLibrary"/> so a map's own wins.</summary>
    public static GasHobSpec ByName(string key)
    {
        HobKey.TryParse(key, out var k);
        return ModelLibrary.GasHob(k.Preset.Length > 0 ? k.Preset : key);
    }
}

/// <summary>
/// A hob's state on the wire, in its sound key: "stove:&lt;preset&gt;/&lt;from&gt;&gt;&lt;to&gt;@&lt;seconds&gt;", one
/// digit a burner for its knob (0 off, 1 low, 2 medium, 3 high), what it was and what it was turned to, and
/// when on the shared clock (<see cref="WindField.Now"/>). Every client then hears the same change at the
/// same point, and a client arriving later hears the burners as they are. A bare "stove:&lt;preset&gt;" is
/// every burner off, untouched.
/// </summary>
public readonly record struct HobKey(string Preset, string From, string To, double At)
{
    public const string Prefix = "stove:";

    /// <summary>The burners' settings as digits, every burner off.</summary>
    public static string Off(int burners) => new('0', burners);

    public string Format()
        => string.Create(CultureInfo.InvariantCulture, $"{Prefix}{Preset}/{From}>{To}@{At:F2}");

    public static bool IsKey(string? soundId) => soundId != null && soundId.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Takes a key apart. A bare preset is every burner off since long ago (From and To empty).</summary>
    public static bool TryParse(string? soundId, out HobKey key)
    {
        key = default;
        if (string.IsNullOrEmpty(soundId)) return false;
        string s = soundId.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) ? soundId[Prefix.Length..] : soundId;
        int slash = s.IndexOf('/');
        if (slash < 0) { key = new HobKey(s, "", "", double.NegativeInfinity); return s.Length > 0; }
        string preset = s[..slash], rest = s[(slash + 1)..];
        int arrow = rest.IndexOf('>'), at = rest.IndexOf('@');
        if (arrow < 0 || at < arrow) return false;
        string from = rest[..arrow], to = rest[(arrow + 1)..at];
        if (from.Length != to.Length || !Digits(from) || !Digits(to)) return false;
        if (!double.TryParse(rest[(at + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out double t)) return false;
        key = new HobKey(preset, from, to, t);
        return preset.Length > 0;
    }

    private static bool Digits(string s)
    {
        foreach (char c in s) if (c < '0' || c > '3') return false;
        return true;
    }

    /// <summary>A burner's setting after the change (0 off), or 0 for a burner the key does not name.</summary>
    public int Setting(int burner) => burner >= 0 && burner < To.Length ? To[burner] - '0' : 0;

    /// <summary>A burner's setting before the change.</summary>
    public int Before(int burner) => burner >= 0 && burner < From.Length ? From[burner] - '0' : 0;

    /// <summary>Whether any burner is on after the change.</summary>
    public bool AnyOn
    {
        get
        {
            foreach (char c in To ?? "") if (c != '0') return true;
            return false;
        }
    }

    /// <summary>How long after the last change anything can still be heard from a hob turned off, s: the
    /// flame going out, then the safety valve's click as its thermocouple cools.</summary>
    public const double QuietAfterSeconds = 40.0;
}

/// <summary>
/// What the interact key does at a hob. With a burner off, it lights the next one, front left first, on
/// full, as a hob is lit; with every burner lit, it turns them all off. The cook's hand on the knobs is the
/// client's to play (GasHobSynth): the server keeps only the settings and when they changed.
/// </summary>
public static class HobControls
{
    /// <summary>The key after a press of the interact key, and what to tell the player.</summary>
    public static string Press(string soundId, double now, out string newKey)
    {
        newKey = soundId;
        if (!HobKey.TryParse(soundId, out var key)) return "";
        var spec = GasHobSpec.ByName(key.Preset);
        int n = spec.Burners.Length;
        string current = key.To.Length == n ? key.To : HobKey.Off(n);
        int next = current.IndexOf('0');
        string after;
        string line;
        if (next >= 0)
        {
            var chars = current.ToCharArray();
            chars[next] = '3';
            after = new string(chars);
            line = n == 1 ? "You light the burner." : $"You light the {spec.Burners[next].Name} burner.";
        }
        else
        {
            after = HobKey.Off(n);
            line = n == 1 ? "You turn the burner off." : "You turn every burner off.";
        }
        newKey = new HobKey(key.Preset, current, after, now).Format();
        return line;
    }
}

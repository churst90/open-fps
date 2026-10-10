using System.Collections.Generic;
using System.Text.Json.Serialization;
using OpenFPS.Common.Editing;

namespace OpenFPS.Common;

// A loudspeaker is a chain: the program (a recording, which is what a public-address system plays), an
// amplifier that clips at its rail, a driver with a resonance, a travel it cannot exceed and a voice coil
// that heats, and a horn that will not radiate below its mouth's cutoff and beams as the frequency rises.
// The chain is rendered in Client.Core (LoudspeakerChain); its directivity is Radiator, below, applied
// live per listener. The numbers are a datasheet's where one exists; the rest say "design value".

/// <summary>How the radiating surface is mounted, which decides what is heard behind it.</summary>
public enum LoudspeakerMount
{
    /// <summary>A horn mouth or a cone in free air (a pole horn, a megaphone): all round while the mouth
    /// is small against the wavelength, beaming as it grows; behind it, the low mids and the housing.</summary>
    Horn,
    /// <summary>A cone in a baffle (a ceiling or wall speaker): a hemisphere at low frequencies, nothing
    /// behind the baffle.</summary>
    Baffled,
}

/// <summary>
/// One loudspeaker: amplifier, driver, horn and mounting. Presets are real products' published figures
/// (cited on each); a new kind (a column, a ceiling speaker) is a new record with its own datasheet,
/// authored as a "loudspeaker" model (ModelLibrary) or added to <see cref="Presets"/>.
/// </summary>
public sealed record LoudspeakerSpec
{
    /// <summary>The speed of sound the horn's cutoff and beam are worked out at, m/s (20 C).</summary>
    public const float SpeedOfSound = 343f;

    [Tunable("", 0, 0, "The loudspeaker's name as it is said.")]
    public required string Name { get; init; }

    // ── Amplifier ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Continuous power the speaker is rated for, watts: the voice coil's heating and the
    /// excursion headroom are stated at it.</summary>
    [Tunable("W", 0.5, 500, "Continuous power the speaker is rated for. The coil's heating and the driver's travel are stated at this power.", Label = "rated power", Step = 0.5)]
    public float RatedWatts { get; init; } = 15f;

    /// <summary>The amplifier's clip point as a sine's power into the load, watts: the rail.</summary>
    [Tunable("W", 0.5, 1000, "The most the amplifier can put into the speaker, as a sine, before it clips at its rail.", Label = "amplifier power", Step = 0.5)]
    public float MaxWatts { get; init; } = 15f;

    /// <summary>The voice coil's nominal impedance, ohms.</summary>
    [Tunable("ohm", 1, 64, "The voice coil's nominal impedance.", Label = "impedance", Step = 0.5)]
    public float LoadOhms { get; init; } = 8f;

    /// <summary>The supply's own resistance, ohms: a battery's sags under load (zero for the mains).</summary>
    [Tunable("ohm", 0, 10, "The supply's internal resistance. A battery's rail sags while the amplifier works hard; the mains does not.", Label = "supply resistance", Step = 0.05)]
    public float SupplyOhms { get; init; }

    /// <summary>Where the amplifier starts to round off, as a fraction of its rail: 1 is a hard clip.</summary>
    [Tunable("", 0.5, 1, "Where the amplifier starts to round the wave off, as a fraction of its rail. 1 clips hard.", Label = "clip knee", Step = 0.01)]
    public float ClipKnee { get; init; } = 0.9f;

    // ── Program and dynamics ──────────────────────────────────────────────────────────────────
    //
    // In the order the signal meets them: the low cut, the voice compressor (an AGC on the microphone
    // input, with its make-up gain, so the loudest syllables come out where they went in and the quiet
    // ones higher), then the volume control, which sets where those peaks land against the power
    // amplifier's clip.

    /// <summary>How far the compressed program's peaks are driven past the clip point, dB: the volume
    /// control as whoever set it up left it (an installer just at the clip, a megaphone's knob turned
    /// up so speech is into it on every syllable, someone shouting into it far over).</summary>
    [Tunable("dB", -20, 30, "How far the loudest moments of the program are driven past the amplifier's clip point: the volume control. An installer sets it near zero; a megaphone at full volume is well over.", Label = "drive", Step = 0.5)]
    public float PeakOverClipDb { get; init; }

    /// <summary>The amplifier's protection high-pass, Hz; zero for none.</summary>
    [Tunable("Hz", 0, 1000, "The amplifier's low cut, which keeps bass the horn cannot use out of the driver. Zero for none.", Label = "low cut", Step = 10)]
    public float LowCutHz { get; init; }

    /// <summary>The voice compressor's threshold against the program's peaks, dB.</summary>
    [Tunable("dB", -40, 0, "Where the voice compressor starts to work, against the loudest moments of the program. Everything above is squeezed toward it and the whole made up again, so quiet syllables come up.", Label = "compressor threshold", Step = 0.5)]
    public float CompressorThresholdDb { get; init; } = -12f;

    /// <summary>The compressor's ratio; 1 is none.</summary>
    [Tunable("", 1, 50, "How hard the compressor squeezes: 1 is off, 3 a voice AGC, 10 or more a limiter.", Label = "compressor ratio", Step = 0.5)]
    public float CompressorRatio { get; init; } = 1f;

    [Tunable("ms", 0.1, 100, "How fast the compressor turns a syllable down.", Label = "compressor attack", Step = 0.5)]
    public float CompressorAttackMs { get; init; } = 5f;

    [Tunable("ms", 10, 3000, "How fast the compressor lets go. A slow release pumps on speech.", Label = "compressor release", Step = 10)]
    public float CompressorReleaseMs { get; init; } = 300f;

    // ── Driver ────────────────────────────────────────────────────────────────────────────────

    /// <summary>On-axis SPL for one watt at one metre, dB, averaged over the band below.</summary>
    [Tunable("dB", 80, 125, "Sound pressure on the axis at one metre for one watt, averaged over the datasheet's band.", Label = "sensitivity", Step = 0.5)]
    public float SensitivityDb { get; init; } = 106f;

    [Tunable("Hz", 100, 4000, "Bottom of the band the sensitivity is averaged over.", Label = "sensitivity band bottom", Step = 10)]
    public float SensitivityLowHz { get; init; } = 500f;

    [Tunable("Hz", 1000, 16000, "Top of the band the sensitivity is averaged over.", Label = "sensitivity band top", Step = 50)]
    public float SensitivityHighHz { get; init; } = 6000f;

    /// <summary>
    /// The driver's passband under its horn's load, Hz: the diaphragm's velocity is flat between these and
    /// falls outside, one mass-spring resonance at their geometric mean with Q = f0 / (high - low).
    /// </summary>
    [Tunable("Hz", 100, 4000, "Bottom of the driver's own passband under the horn's load: its resonance and suspension.", Label = "driver low", Step = 10)]
    public float DriverLowHz { get; init; } = 550f;

    [Tunable("Hz", 1000, 20000, "Top of the driver's own passband: where the diaphragm's mass takes over.", Label = "driver high", Step = 50)]
    public float DriverHighHz { get; init; } = 5500f;

    /// <summary>The voice coil's inductance pole, Hz.</summary>
    [Tunable("Hz", 1000, 20000, "Where the voice coil's inductance starts to cut the current.", Label = "coil top", Step = 50)]
    public float CoilTopHz { get; init; } = 12000f;

    /// <summary>A second pole from the diaphragm and phase plug, Hz; zero for none.</summary>
    [Tunable("Hz", 0, 20000, "A second roll-off from the diaphragm breaking up. Zero for none.", Label = "diaphragm top", Step = 50)]
    public float DiaphragmTopHz { get; init; }

    /// <summary>How far inside its travel the diaphragm stays at rated power, at the horn's cutoff, dB.</summary>
    [Tunable("dB", -12, 30, "How far inside its travel the diaphragm stays at rated power, at the horn's cutoff. Zero reaches the limit; below zero bottoms out.", Label = "excursion headroom", Step = 0.5)]
    public float ExcursionHeadroomDb { get; init; } = 6f;

    /// <summary>The suspension's stiffening at the travel limit: k(x) = k0 (1 + h x²), x in limits.</summary>
    [Tunable("", 0, 8, "How much stiffer the suspension is at the limit of travel than at rest, as a multiple.", Label = "suspension hardening", Step = 0.1)]
    public float SuspensionHardening { get; init; } = 1f;

    /// <summary>How much of the force factor is lost at the limit of travel, 0..0.9: the coil leaving the gap.</summary>
    [Tunable("", 0, 0.9, "How much of the motor's force is lost at the limit of travel, as the coil leaves the gap.", Label = "force factor loss", Step = 0.01)]
    public float ForceFactorLoss { get; init; } = 0.3f;

    /// <summary>The voice coil's temperature rise at rated continuous power, kelvin.</summary>
    [Tunable("K", 0, 250, "How far the voice coil heats above the air at rated continuous power. Its resistance rises with it and the output falls: power compression.", Label = "coil temperature rise", Step = 1)]
    public float CoilRiseAtRatedK { get; init; } = 80f;

    [Tunable("s", 0.2, 60, "How long the voice coil takes to heat and cool.", Label = "coil time constant", Step = 0.1)]
    public float CoilSeconds { get; init; } = 4f;

    // ── Horn ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>The mouth's diameter, metres: its cutoff and its beam.</summary>
    [Tunable("m", 0.03, 1.5, "Diameter of the horn's mouth or the cone. It sets where the bass stops and how hard the top beams forward.", Label = "mouth diameter", Step = 0.005)]
    public float MouthDiameterMetres { get; init; } = 0.2f;

    /// <summary>Throat to mouth along the folded path, metres: the spacing of the horn's ripple.</summary>
    [Tunable("m", 0, 3, "Throat to mouth along the horn's folded path. Sound reflected at the mouth runs back down it: the horn's ripple.", Label = "horn path length", Step = 0.01)]
    public float PathMetres { get; init; } = 0.55f;

    /// <summary>The share of the pressure the mouth sends back down the horn near its cutoff, 0..0.8.</summary>
    [Tunable("", 0, 0.8, "How much of the sound the mouth reflects back down the horn near its cutoff. More is a honkier horn.", Label = "mouth reflection", Step = 0.01)]
    public float MouthReflection { get; init; } = 0.4f;

    /// <summary>The re-entrant fold's own resonance, Hz; zero for none.</summary>
    [Tunable("Hz", 0, 5000, "The folded section's own resonance: the cupped colour of a re-entrant horn. Zero for none.", Label = "fold resonance", Step = 10)]
    public float FoldResonanceHz { get; init; }

    [Tunable("dB", 0, 12, "How far the fold's resonance stands up.", Label = "fold resonance level", Step = 0.5)]
    public float FoldResonanceDb { get; init; }

    [Tunable("", 0.5, 12, "How narrow the fold's resonance is.", Label = "fold resonance Q", Step = 0.1)]
    public float FoldResonanceQ { get; init; } = 3f;

    // ── Radiation ─────────────────────────────────────────────────────────────────────────────

    [Tunable("", 0, 0, "A horn or a cone in free air, or a cone in a baffle (a ceiling speaker).", Label = "mounting")]
    public LoudspeakerMount Mount { get; init; } = LoudspeakerMount.Horn;

    /// <summary>What the housing and the driver's back radiate all round, against the on-axis level, dB.</summary>
    [Tunable("dB", -60, 0, "What the housing and the driver's back give off all round, against the level on the axis. Behind a horn this is most of what is heard.", Label = "housing level", Step = 0.5)]
    public float HousingDb { get; init; } = -30f;

    [Tunable("Hz", 100, 8000, "Above this the housing's own radiation falls away.", Label = "housing top", Step = 10)]
    public float HousingTopHz { get; init; } = 1000f;

    // ── Derived ───────────────────────────────────────────────────────────────────────────────

    /// <summary>The mouth's cutoff, Hz: f = c / (π D), where the mouth's circumference reaches the
    /// wavelength (ka = 1). Below it the mouth stops coupling to the air. As SirenSpec.FlareCutoffHz.</summary>
    [JsonIgnore]
    public float FlareCutoffHz => LoudspeakerSpec.SpeedOfSound / (MathF.PI * MathF.Max(0.02f, MouthDiameterMetres));

    /// <summary>The driver's resonance, Hz: the geometric mean of its passband.</summary>
    [JsonIgnore]
    public float DriverResonanceHz => MathF.Sqrt(MathF.Max(1f, DriverLowHz) * MathF.Max(DriverLowHz + 1f, DriverHighHz));

    /// <summary>The driver's total Q under the horn's load: f0 over its bandwidth.</summary>
    [JsonIgnore]
    public float DriverQ => DriverResonanceHz / MathF.Max(1f, DriverHighHz - DriverLowHz);

    /// <summary>The on-axis SPL at one metre for a continuous sine at rated power, averaged over the
    /// sensitivity band, dB: what a datasheet calls the rated output.</summary>
    [JsonIgnore]
    public float RatedSplDb => SensitivityDb + 10f * MathF.Log10(MathF.Max(1e-3f, RatedWatts));

    // ── Presets ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A pole-mounted re-entrant paging horn, 15 W: the Atlas Sound AP-15T. Datasheet (AtlasIED
    /// ATS003852A): 15 W continuous; 106 dB at 1 W/1 m and 116 dB at 15 W/1 m, averaged 500-6000 Hz;
    /// response 500-6000 Hz ±5 dB, 400-14,000 Hz nominal; 70 degrees coverage at -6 dB in the 2 kHz
    /// octave; 7-7/8 in wide, 9-5/16 in deep (a 200 mm mouth, cutoff 546 Hz). The coverage is not
    /// fitted: a 200 mm mouth (Radiator) gives 76 degrees at -6 dB in the 2 kHz octave against the
    /// datasheet's 70, and the response is inside its ±5 dB but for 500 Hz, 6.6 dB under the band's
    /// mean, under the mouth's cutoff (LoudspeakerTests). Design values (no datasheet): the drive (an
    /// installer sets the peaks at the clip), the 300 Hz low cut and the 3:1 voice compressor of a paging
    /// amplifier, the driver's passband, the fold, and the coil's heating (Button, "Heat dissipation and
    /// power compression in loudspeakers", JAES 40, 1992: tens of kelvin to well over a hundred at
    /// rated power).
    /// </summary>
    public static LoudspeakerSpec PaHorn => new()
    {
        Name = "Paging horn, 15 W re-entrant",
        RatedWatts = 15f, MaxWatts = 15f, LoadOhms = 8f, SupplyOhms = 0f, ClipKnee = 0.9f,
        PeakOverClipDb = 0f, LowCutHz = 300f,
        CompressorThresholdDb = -15f, CompressorRatio = 3f, CompressorAttackMs = 5f, CompressorReleaseMs = 300f,
        SensitivityDb = 106f, SensitivityLowHz = 500f, SensitivityHighHz = 6000f,
        DriverLowHz = 450f, DriverHighHz = 5500f, CoilTopHz = 12000f, DiaphragmTopHz = 0f,
        ExcursionHeadroomDb = 6f, SuspensionHardening = 1f, ForceFactorLoss = 0.3f,
        CoilRiseAtRatedK = 80f, CoilSeconds = 4f,
        MouthDiameterMetres = 0.20f, PathMetres = 0.55f, MouthReflection = 0.4f,
        FoldResonanceHz = 1600f, FoldResonanceDb = 2f, FoldResonanceQ = 3f,
        Mount = LoudspeakerMount.Horn, HousingDb = -30f, HousingTopHz = 1000f,
    };

    /// <summary>
    /// A handheld megaphone, 6 W rated and 10 W at most: the TOA ER-1206 (TOA megaphone brochure: six
    /// AA cells, 9 V; 6 W rated, 10 W maximum; 450-6,000 Hz at -20 dB; 154 mm wide, so a 150 mm mouth,
    /// cutoff 728 Hz; voice range about 250 m, JEITA). A 9 V rail clips 10 W into 4 ohms. TOA publish no
    /// sensitivity; 110 dB at 1 W/1 m is the Monacor TM-17M's, the one handheld whose figure is
    /// published. The cells' internal resistance, 0.15-0.3 ohm each (Energizer E91 datasheet), sags the
    /// rail. Design values: a voice AGC (3:1 from 15 dB under the program's peaks), the volume knob
    /// turned up so those peaks are 6 dB into the clip, a driver that reaches its travel at rated power,
    /// a lightly damped plastic fold, and an ABS shell that gives off -20 dB all round.
    /// </summary>
    public static LoudspeakerSpec Megaphone => new()
    {
        Name = "Handheld megaphone, 10 W",
        RatedWatts = 6f, MaxWatts = 10f, LoadOhms = 4f, SupplyOhms = 1.2f, ClipKnee = 0.75f,
        PeakOverClipDb = 6f, LowCutHz = 0f,
        CompressorThresholdDb = -15f, CompressorRatio = 3f, CompressorAttackMs = 5f, CompressorReleaseMs = 400f,
        SensitivityDb = 110f, SensitivityLowHz = 500f, SensitivityHighHz = 6000f,
        DriverLowHz = 1000f, DriverHighHz = 3800f, CoilTopHz = 5500f, DiaphragmTopHz = 6000f,
        ExcursionHeadroomDb = 0f, SuspensionHardening = 2f, ForceFactorLoss = 0.5f,
        CoilRiseAtRatedK = 120f, CoilSeconds = 2f,
        MouthDiameterMetres = 0.15f, PathMetres = 0.40f, MouthReflection = 0.5f,
        FoldResonanceHz = 1400f, FoldResonanceDb = 5f, FoldResonanceQ = 4f,
        Mount = LoudspeakerMount.Horn, HousingDb = -20f, HousingTopHz = 1500f,
    };

    /// <summary>The same megaphone with someone shouting into it: the peaks 16 dB into the clip.</summary>
    public static LoudspeakerSpec MegaphoneShouted => Megaphone with
    {
        Name = "Handheld megaphone, shouted into",
        PeakOverClipDb = 16f,
    };

    public static IReadOnlyDictionary<string, Func<LoudspeakerSpec>> Presets { get; } =
        new Dictionary<string, Func<LoudspeakerSpec>>(StringComparer.OrdinalIgnoreCase)
        {
            ["pa_horn"] = () => PaHorn,
            ["megaphone"] = () => Megaphone,
            ["megaphone_shouted"] = () => MegaphoneShouted,
        };

    public static LoudspeakerSpec ByName(string key)
        => Presets.TryGetValue(key, out var make) ? make()
         : throw new ArgumentException($"No loudspeaker preset '{key}'. Known: {string.Join(", ", Presets.Keys)}");
}

/// <summary>Naming the buffer a loudspeaker plays a program through.</summary>
public static class Loudspeakers
{
    public const string KeyPrefix = "loudspeaker:";

    /// <summary>The sound id of a program played through a loudspeaker: "loudspeaker:pa_horn/ANNOUNCE/x".</summary>
    public static string Key(string speaker, string program) => KeyPrefix + speaker + "/" + program;

    /// <summary>The speaker a placed emitter names, from the model library (an authored one first).</summary>
    public static bool TryGet(string? id, out LoudspeakerSpec spec)
    {
        spec = null!;
        if (string.IsNullOrEmpty(id) || !ModelLibrary.Knows(ModelLibrary.Kinds.Loudspeaker, id)) return false;
        spec = ModelLibrary.Loudspeaker(id);
        return true;
    }
}

/// <summary>
/// Which way a loudspeaker throws its sound, per band: the mouth as a piston of its own diameter, with
/// the unflanged pipe's all-round share at low ka (Levine and Schwinger, as ExhaustRadiation), and the
/// housing's own radiation under both.
///
/// In front the pattern is a rigid circular piston's, 2 J1(x) / x with x = ka sin θ (Kinsler and Frey,
/// Fundamentals of Acoustics, 4th ed., 7.4), past its first null the envelope of its side lobes,
/// 1.6 / x^1.5, since a real mouth's phase is not uniform enough for the nulls. A horn in free air keeps
/// a share 1 / (1 + (ka)²) all round, which behind it is what is heard: the low mids, the housing and
/// none of the top. A baffled cone radiates nothing behind its baffle.
///
/// Six octave bands, 250 Hz to 8 kHz, each averaged in power over its thirds. On the axis every band is
/// unity: the render is the on-axis pressure.
/// </summary>
public sealed class Radiator
{
    /// <summary>The bands' centres, Hz.</summary>
    public static readonly float[] BandHz = { 250f, 500f, 1000f, 2000f, 4000f, 8000f };
    /// <summary>The crossovers between them, Hz: the geometric mean of neighbouring centres.</summary>
    public static readonly float[] CrossoverHz = { 353.6f, 707.1f, 1414.2f, 2828.4f, 5656.9f };
    public const int Bands = 6;
    private const int Steps = 36;            // 5 degrees
    private readonly float[] _table = new float[(Steps + 1) * Bands];
    private readonly float[] _power = new float[Bands];

    public float MouthDiameterMetres { get; }
    public LoudspeakerMount Mount { get; }

    public Radiator(float mouthDiameterMetres, LoudspeakerMount mount, float housingDb = -60f, float housingTopHz = 1000f)
    {
        MouthDiameterMetres = MathF.Max(0.01f, mouthDiameterMetres);
        Mount = mount;
        for (int b = 0; b < Bands; b++)
        {
            float on = PowerAt(b, 0f, housingDb, housingTopHz);
            for (int s = 0; s <= Steps; s++)
            {
                float theta = MathF.PI * s / Steps;
                _table[s * Bands + b] = MathF.Sqrt(PowerAt(b, theta, housingDb, housingTopHz) / on);
            }
            // The mean over the sphere, 1/2 ∫ g² sin θ dθ, in one-degree steps.
            double sum = 0;
            for (int d = 0; d < 180; d++)
            {
                float theta = (d + 0.5f) * MathF.PI / 180f;
                sum += PowerAt(b, theta, housingDb, housingTopHz) / on * MathF.Sin(theta) * (MathF.PI / 180f);
            }
            _power[b] = (float)(0.5 * sum);
        }
    }

    public static Radiator For(LoudspeakerSpec s) => new(s.MouthDiameterMetres, s.Mount, s.HousingDb, s.HousingTopHz);

    /// <summary>The mean of the power gain over the whole sphere, per band: 1 / Q, the share of the
    /// on-axis level a room is fed with.</summary>
    public float RadiatedPower(int band) => _power[band];

    /// <summary>The directivity index of a band, dB.</summary>
    public float DirectivityIndexDb(int band) => -10f * MathF.Log10(MathF.Max(1e-9f, _power[band]));

    /// <summary>The pressure gain in each band toward a listener at this cosine off the axis. No
    /// allocation: a mixer-side caller passes its own span.</summary>
    public void Gains(float cosOffAxis, Span<float> gains)
    {
        float theta = MathF.Acos(Math.Clamp(cosOffAxis, -1f, 1f));
        float at = theta / MathF.PI * Steps;
        int s = Math.Min(Steps - 1, (int)at);
        float f = at - s;
        for (int b = 0; b < Bands && b < gains.Length; b++)
            gains[b] = _table[s * Bands + b] * (1f - f) + _table[(s + 1) * Bands + b] * f;
    }

    /// <summary>The pressure gain of the power radiated all round, per band: √(1/Q), what a room is fed
    /// with against the on-axis level.</summary>
    public void PowerGains(Span<float> gains)
    {
        for (int b = 0; b < Bands && b < gains.Length; b++) gains[b] = MathF.Sqrt(_power[b]);
    }

    /// <summary>The beam toward a listener over the power radiated all round, per band: what follows
    /// <see cref="PowerGains"/> in a chain that feeds its room between the two. Over one on the axis.</summary>
    public void BeamOverPower(float cosOffAxis, Span<float> gains)
    {
        Gains(cosOffAxis, gains);
        for (int b = 0; b < Bands && b < gains.Length; b++) gains[b] /= MathF.Max(1e-4f, MathF.Sqrt(_power[b]));
    }

    /// <summary>One band's pressure gain at an angle off the axis, radians.</summary>
    public float Gain(int band, float thetaRadians)
    {
        Span<float> g = stackalloc float[Bands];
        Gains(MathF.Cos(thetaRadians), g);
        return g[band];
    }

    /// <summary>
    /// The six bands folded onto the mixer's three (AcousticBands: 400 Hz and 4 kHz), weighted by how
    /// much of the program is in each (<paramref name="bandEnergy"/>, any units): for a copy off a wall,
    /// whose own EQ has three bands. The 4 kHz octave straddles the high crossover and goes half to each.
    /// </summary>
    public (float Low, float Mid, float High) ThreeBand(float cosOffAxis, ReadOnlySpan<float> bandEnergy)
    {
        Span<float> g = stackalloc float[Bands];
        Gains(cosOffAxis, g);
        return Fold(g, bandEnergy);
    }

    /// <summary>Six band gains folded onto three, weighted by the program's energy in each.</summary>
    public static (float Low, float Mid, float High) Fold(ReadOnlySpan<float> g, ReadOnlySpan<float> bandEnergy)
    {
        Span<float> share = stackalloc float[] { 1f, 0f, 0f,  0f, 1f, 0f,  0f, 1f, 0f,  0f, 1f, 0f,  0f, 0.5f, 0.5f,  0f, 0f, 1f };
        float lN = 0, lD = 0, mN = 0, mD = 0, hN = 0, hD = 0;
        for (int b = 0; b < Bands; b++)
        {
            float e = b < bandEnergy.Length ? MathF.Max(1e-12f, bandEnergy[b]) : 1f;
            float p = g[b] * g[b];
            lN += share[b * 3] * e * p; lD += share[b * 3] * e;
            mN += share[b * 3 + 1] * e * p; mD += share[b * 3 + 1] * e;
            hN += share[b * 3 + 2] * e * p; hD += share[b * 3 + 2] * e;
        }
        return (MathF.Sqrt(lN / MathF.Max(1e-12f, lD)), MathF.Sqrt(mN / MathF.Max(1e-12f, mD)), MathF.Sqrt(hN / MathF.Max(1e-12f, hD)));
    }

    /// <summary>The whole program's gain toward this angle, weighted by its band energies.</summary>
    public float Broadband(float cosOffAxis, ReadOnlySpan<float> bandEnergy)
    {
        Span<float> g = stackalloc float[Bands];
        Gains(cosOffAxis, g);
        float n = 0, d = 0;
        for (int b = 0; b < Bands; b++)
        {
            float e = b < bandEnergy.Length ? MathF.Max(1e-12f, bandEnergy[b]) : 1f;
            n += e * g[b] * g[b]; d += e;
        }
        return MathF.Sqrt(n / MathF.Max(1e-12f, d));
    }

    /// <summary>The share of the on-axis level that reaches the room, as a pressure gain, weighted by
    /// the program's band energies.</summary>
    public float RadiatedGain(ReadOnlySpan<float> bandEnergy)
    {
        float n = 0, d = 0;
        for (int b = 0; b < Bands; b++)
        {
            float e = b < bandEnergy.Length ? MathF.Max(1e-12f, bandEnergy[b]) : 1f;
            n += e * _power[b]; d += e;
        }
        return MathF.Sqrt(n / MathF.Max(1e-12f, d));
    }

    // The power gain, unnormalised, of one band at an angle: the band's three thirds averaged.
    private float PowerAt(int band, float theta, float housingDb, float housingTopHz)
    {
        float sum = 0f;
        for (int k = -1; k <= 1; k++)
        {
            float f = BandHz[band] * MathF.Pow(2f, k / 3f);
            float ka = MathF.PI * f * MouthDiameterMetres / LoudspeakerSpec.SpeedOfSound;
            float front = theta <= MathF.PI / 2f
                ? Piston(ka * MathF.Sin(theta))
                : Piston(ka) * MathF.Cos(theta - MathF.PI / 2f);
            // In free air, the unflanged pipe's split, as ExhaustRadiation has it: an all-round share
            // 1 / (1 + (ka)²) and the beam for the rest, added in power (the two are not in step across a
            // mouth). Behind, the all-round share is all there is: against the axis, -0.2 dB at 250 Hz
            // from a 200 mm mouth, -11 at 1 kHz, -22 at 2 kHz.
            float w = ka * ka / (1f + ka * ka);
            float g2 = Mount == LoudspeakerMount.Baffled
                ? (theta <= MathF.PI / 2f ? front * front : 0f)
                : (1f - w) * (1f - w) + w * w * front * front;
            float h = MathF.Pow(10f, housingDb / 20f) * (f <= housingTopHz ? 1f : (housingTopHz / f) * (housingTopHz / f));
            sum += g2 + h * h;
        }
        return sum / 3f;
    }

    /// <summary>A rigid piston's far-field pressure, 2 J1(x) / x, its side lobes as their envelope.</summary>
    internal static float Piston(float x)
    {
        x = MathF.Abs(x);
        if (x < 1e-4f) return 1f;
        if (x >= 2.5f) return MathF.Min(1f, 1.6f / (x * MathF.Sqrt(x)));
        // J1 by its series, exact enough to x = 2.5 in eight terms.
        double h = x / 2.0, term = h, j1 = 0;
        for (int m = 0; m < 10; m++)
        {
            j1 += term;
            term *= -(h * h) / ((m + 1) * (m + 2));
        }
        return (float)(2.0 * j1 / x);
    }
}

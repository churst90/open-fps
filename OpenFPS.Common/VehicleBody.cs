using System.Collections.Generic;
using OpenFPS.Common.Editing;

namespace OpenFPS.Common;

/// <summary>One resonance of a body: its note, how long it rings, and how strongly it is driven.</summary>
public readonly record struct BodyMode(float Hz, float DecaySeconds, float Weight);

/// <summary>
/// The car itself, as a thing the engine's sound has to get out through: panels, floorpan and the box
/// of air in the cabin, driven by the engine and radiating on their own.
///
/// The body is linear and time-invariant, so it is an impulse response; the gas path is not (exhaust
/// gas at 700-900 C puts every pipe resonance about 75 % higher than cold, and it moves with load), so
/// the pipes stay in the waveguide model. The response runs as a bank of two-pole resonators, which is
/// the same thing as convolving with it at six multiply-adds per mode instead of 2,600 taps per sample.
/// The modes come from <see cref="PanelAcoustics"/>, the plate law a door leaf and a window pane use,
/// and the cabin's from the box's dimensions.
/// </summary>
public sealed record VehicleBody
{
    /// <summary>What the panels are made of: steel ("Metal") for almost everything.</summary>
    [Tunable("", 0, 0, "What the panels are made of. Steel is Metal.", Label = "panel material", Choices = "materials")]
    public string PanelMaterial { get; init; } = "Metal";

    /// <summary>Panel thickness, metres. Car body skin is 0.7 to 1.0 mm.</summary>
    [Tunable("m", 0.0003, 0.006, "Panel thickness. Car body skin is 0.0007 to 0.001 m. Thicker panels ring higher.", Label = "panel thickness", Step = 0.0001)]
    public float PanelThicknessM { get; init; } = 0.0008f;

    /// <summary>
    /// The free spans in the bodywork, metres: the distance between stiffeners (150 to 400 mm on a
    /// production car), not the size of the pressing. Swages, curvature and spot welds divide a panel
    /// into sub-panels; with a roof's whole dimensions here the first render put 97 % of its energy
    /// below 200 Hz. A van's sides are stiffened more sparsely, which is why they boom.
    /// </summary>
    public float[] PanelSpansM { get; init; } = new[] { 0.35f, 0.28f, 0.22f, 0.18f, 0.14f, 0.11f };

    /// <summary>
    /// Total loss factor of the panels as fitted: the "expensive car" knob. Bare steel's own loss
    /// (about 2e-4) would ring for most of a minute; welding (<see cref="PanelAcoustics.MountedLoss"/>)
    /// and bonded deadening pads stop it. A van and a luxury saloon differ here by a factor of five.
    /// </summary>
    [Tunable("", 0.001, 0.5, "Loss factor of the panels as fitted, with mounting and deadening pads. Higher stops them ringing sooner.", Label = "panel loss factor", Step = 0.005)]
    public float PanelLoss { get; init; } = 0.12f;

    /// <summary>How much of the engine's pressure gets into the structure, 0 to 1: a tailpipe under a
    /// floorpan drives it hard, an open-wheeler's touches almost nothing. The number most worth fitting
    /// against a real recording.</summary>
    public float Coupling { get; init; } = 0.25f;

    /// <summary>Inside dimensions of the cabin, metres; all three zero for no cabin (a motorcycle, a
    /// formula car).</summary>
    [Tunable("m", 0, 15, "Inside length of the cabin. Zero for no cabin.", Label = "cabin length", Step = 0.05)]
    public float CabinLengthM { get; init; } = 2.4f;
    [Tunable("m", 0, 4, "Inside width of the cabin. Zero for no cabin.", Label = "cabin width", Step = 0.05)]
    public float CabinWidthM { get; init; } = 1.45f;
    [Tunable("m", 0, 4, "Inside height of the cabin. Zero for no cabin.", Label = "cabin height", Step = 0.05)]
    public float CabinHeightM { get; init; } = 1.15f;

    /// <summary>
    /// Whether these panels are the walls of a sealed volume rather than free plates: a free plate is a
    /// dipole whose low modes cancel round its edge (<see cref="RadiationFactor"/>'s (ka)²), a sealed
    /// box's walls radiate as sources down to where they stop moving. False by default: a car's skin has
    /// the underbody and cavities connected round behind much of it.
    /// </summary>
    [Tunable("", 0, 1, "Whether the panels are the walls of a sealed volume, so their low modes radiate instead of cancelling round the edges.")]
    public bool SealedBox { get; init; }

    /// <summary>Average absorption of the cabin's surfaces.</summary>
    [Tunable("", 0.02, 0.95, "Average absorption of the cabin's surfaces. Seats and carpet are soft; a stripped race car is not.", Step = 0.01)]
    public float CabinAbsorption { get; init; } = 0.35f;

    /// <summary>
    /// How much of the cabin's resonance reaches a listener outside the car, 0 to 1. A cabin boom is
    /// what the driver hears; it reaches the street only through the bodywork and the seals. At full
    /// weight four fifths of a saloon's response measured below 200 Hz. A leak rather than a deletion,
    /// because an interior mix wants the cabin at full weight.
    /// </summary>
    [Tunable("", 0, 1, "Share of the cabin's resonance that reaches a listener outside the car.", Label = "cabin leak to outside", Step = 0.01)]
    public float CabinLeak { get; init; } = 0.15f;

    /// <summary>
    /// What gets in round the doors, vents and glass, as a fraction of the pressure outside: the
    /// broadband path the panels' mass law does not cover (by 2 kHz a steel door is 40 dB down). 0.03,
    /// thirty decibels, is a well-sealed saloon; a bus with folding doors leaks more.
    /// </summary>
    [Tunable("", 0, 1, "Share of the outside pressure that gets in round the doors, vents and glass. 0.03 is a well-sealed saloon.", Label = "seal leak", Step = 0.01)]
    public float SealLeak { get; init; } = 0.03f;

    /// <summary>
    /// How far down the starter is in the cabin, dB, against its level at a metre in the open. It is
    /// structure-borne (bellhousing, mounts, floor), not airborne through the firewall's mass law
    /// (thirty-odd dB at its whine): a normal start is 60-70 dB from the seat, an 82 dB starter about
    /// eighteen down.
    /// </summary>
    [Tunable("dB", 0, 50, "How far down the starter is in the cabin against its level at a metre in the open.", Label = "starter path loss", Step = 1)]
    public float StarterPathLossDb { get; init; } = 18f;

    /// <summary>
    /// Wind noise inside at 110 km/h, dB SPL: the aero noise's one anchor. Turbulence over mirrors,
    /// pillars and seals, a dipole whose power goes as the sixth power of speed. 64 dB is a quiet modern
    /// saloon.
    /// </summary>
    [Tunable("dB", 40, 95, "Wind noise inside at 110 km/h. A quiet modern saloon is 64; a boxy van or a bus is louder.", Label = "wind noise at 110 km/h", Step = 1)]
    public float WindNoiseDbAt110 { get; init; } = 64f;

    /// <summary>How many modes to run, loudest first. A real budget: modes cost per sample per voice,
    /// with thirty cars on a track; above a couple of kilohertz they read as colouration anyway.</summary>
    public int MaxModes { get; init; } = 16;

    /// <summary>Speed of sound in the cabin's air, m/s: cabin temperature, not the exhaust's.</summary>
    private const float CabinSpeedOfSound = 343f;

    /// <summary>
    /// Every resonance this body has, strongest first, capped at <see cref="MaxModes"/>: the panels by
    /// the plate law (weights from <see cref="PanelAcoustics.Modes"/>), and the cabin's axial modes.
    /// </summary>
    public IReadOnlyList<BodyMode> Modes()
    {
        var material = AcousticRegistry.GetProperties(PanelMaterial);
        var modes = new List<BodyMode>();

        foreach (float span in PanelSpansM ?? Array.Empty<float>())
        {
            if (span <= 0.05f) continue;
            // The other span two thirds of the first, so every panel does not land on the same note.
            float a = span, b = span * 0.66f;
            float radius = 0.5f * MathF.Max(a, b);
            foreach (var mode in PanelAcoustics.Modes(material, a, b, PanelThicknessM))
            {
                float decay = PanelAcoustics.RingSeconds(material, mode.Hz, PanelLoss);
                float radiation = SealedBox ? 1f : RadiationFactor(mode.Hz, radius);
                modes.Add(new BodyMode(mode.Hz, decay, mode.Weight * radiation));
            }
        }

        AddCabinModes(modes);

        modes.Sort((x, y) => y.Weight.CompareTo(x.Weight));
        if (modes.Count > MaxModes) modes.RemoveRange(MaxModes, modes.Count - MaxModes);
        return modes;
    }

    /// <summary>
    /// How much of a mode's motion becomes sound, 0 to 1: (ka)² until the panel is about a wavelength
    /// across, the same law OpenEnd.Radiate applies at a pipe's end. Without it the budget, ranked by
    /// drive alone, kept every panel's lowest mode and the first render had 97 % of its energy below
    /// 200 Hz: low modes are driven hardest and radiate worst.
    /// </summary>
    private static float RadiationFactor(float hz, float radiusMetres)
    {
        float ka = 2f * MathF.PI * hz * MathF.Max(0.01f, radiusMetres) / CabinSpeedOfSound;
        return MathF.Min(1f, ka * ka);
    }

    private void AddCabinModes(List<BodyMode> into)
    {
        if (CabinLengthM <= 0.1f || CabinWidthM <= 0.1f || CabinHeightM <= 0.1f) return;

        float volume = CabinLengthM * CabinWidthM * CabinHeightM;
        float surface = 2f * (CabinLengthM * CabinWidthM + CabinLengthM * CabinHeightM + CabinWidthM * CabinHeightM);
        float absorption = Math.Clamp(CabinAbsorption, 0.02f, 0.95f);

        // Sabine: about a tenth of a second, enough to colour and too short to read as a room.
        float t60 = Math.Clamp(0.161f * volume / (surface * absorption), 0.02f, 0.5f);

        // Axial modes only: the tangential and oblique ones are dense and weak, and a car's boom is axial.
        Span<float> spans = stackalloc float[] { CabinLengthM, CabinWidthM, CabinHeightM };
        foreach (float length in spans)
        {
            float hz = CabinSpeedOfSound / (2f * length);
            if (hz < PanelAcoustics.MinimumRingHz) continue;
            // No radiation factor (a cabin does not radiate by flexing); only CabinLeak gets outside.
            into.Add(new BodyMode(hz, t60, Math.Clamp(CabinLeak, 0f, 1f)));
        }
    }

    /// <summary>The body's impulse response through <see cref="BodyResonator"/>, for auditioning and
    /// measuring against a recording.</summary>
    public float[] ImpulseResponse(int sampleRate, float seconds = 0.25f)
    {
        int n = Math.Max(1, (int)(sampleRate * seconds));
        var bank = new BodyResonator(this, sampleRate);
        var ir = new float[n];
        ir[0] = bank.Process(1f);
        for (int i = 1; i < n; i++) ir[i] = bank.Process(0f);
        return ir;
    }

    /// <summary>Where a response puts its energy, as shares of the total that sum to one.</summary>
    public readonly record struct BandBalance(float Low, float Mid, float High);

    /// <summary>
    /// Which third of the spectrum a response lives in, as shares (an impulse response has no absolute
    /// level). Log-spaced probes, so an octave at the top counts as one at the bottom. Shared by the
    /// diagnostic and the test, so the number a test guards is the number a person reads.
    /// </summary>
    public static BandBalance Bands(float[] response, int sampleRate)
    {
        double total = 0, low = 0, mid = 0, high = 0;
        for (double hz = 40; hz < 8000; hz *= 1.05)
        {
            double mag = Goertzel(response, hz, sampleRate);
            double e = mag * mag;
            total += e;
            if (hz < 200) low += e;
            else if (hz < 1500) mid += e;
            else high += e;
        }
        if (total <= 0) return new BandBalance(0f, 0f, 0f);
        return new BandBalance((float)(low / total), (float)(mid / total), (float)(high / total));
    }

    /// <summary>How long a response takes to fall 60 dB below its own peak, milliseconds.</summary>
    public static float DecayMs(float[] response, int sampleRate)
    {
        float peak = 0f;
        foreach (float v in response) peak = MathF.Max(peak, MathF.Abs(v));
        if (peak <= 0f) return 0f;

        float floor = peak * 0.001f;
        for (int i = response.Length - 1; i >= 0; i--)
            if (MathF.Abs(response[i]) > floor) return 1000f * i / sampleRate;
        return 0f;
    }

    /// <summary>One bin of a DFT, without dragging an FFT in for a diagnostic.</summary>
    private static double Goertzel(float[] x, double hz, int sampleRate)
    {
        double w = 2 * Math.PI * hz / sampleRate;
        double coeff = 2 * Math.Cos(w);
        double s1 = 0, s2 = 0;
        for (int i = 0; i < x.Length; i++)
        {
            double s = x[i] + coeff * s1 - s2;
            s2 = s1; s1 = s;
        }
        return Math.Sqrt(s1 * s1 + s2 * s2 - coeff * s1 * s2);
    }

    /// <summary>A saloon: steel, deadened, a cabin you could sit in. The default car.</summary>
    public static VehicleBody Saloon => new();

    /// <summary>A van: bigger, thicker, flatter panels and almost no deadening, so it booms and
    /// rattles.</summary>
    public static VehicleBody Van => new()
    {
        SealLeak = 0.05f, WindNoiseDbAt110 = 68f,
        PanelThicknessM = 0.0010f,
        // Long flat sides with very few beads in them.
        PanelSpansM = new[] { 0.40f, 0.33f, 0.26f, 0.20f, 0.15f },
        PanelLoss = 0.04f,
        Coupling = 0.35f,
        CabinLengthM = 3.2f, CabinWidthM = 1.7f, CabinHeightM = 1.8f,
        CabinAbsorption = 0.12f,
    };

    /// <summary>
    /// A school bus: the van's gauge with ribs further apart, and a panel's note goes as the inverse
    /// square of its span, so 0.62 m against the van's 0.40 m is less than half the frequency: a bus
    /// booms where a van rattles. Its eleven-metre cabin's lowest axial mode is under 16 Hz.
    /// </summary>
    public static VehicleBody SchoolBus => new()
    {
        SealLeak = 0.08f, WindNoiseDbAt110 = 70f,
        PanelThicknessM = 0.0011f,
        // Ribs about two feet apart over a very long flat flank.
        PanelSpansM = new[] { 0.62f, 0.52f, 0.44f, 0.34f, 0.24f },
        PanelLoss = 0.035f,
        Coupling = 0.40f,
        CabinLengthM = 11.0f, CabinWidthM = 2.4f, CabinHeightM = 2.0f,
        CabinAbsorption = 0.16f,
    };

    /// <summary>
    /// A supercar: small panels, few of them, and an aluminium shell modelled as thicker steel. The plate
    /// law goes as t·sqrt(E/rho), and steel and aluminium have nearly the same sqrt(E/rho) (5050 m/s
    /// against 5055); aluminium is built thicker for the same stiffness, so the difference is a thickness.
    /// </summary>
    public static VehicleBody Supercar => new()
    {
        PanelThicknessM = 0.0011f,
        // Small, heavily stiffened, and thicker: everything lands higher and tighter.
        PanelSpansM = new[] { 0.25f, 0.20f, 0.16f, 0.12f },
        PanelLoss = 0.08f,
        Coupling = 0.18f,
        CabinLengthM = 1.6f, CabinWidthM = 1.35f, CabinHeightM = 1.0f,
        CabinAbsorption = 0.30f,
    };

    /// <summary>A stock car: steel shell, stripped, no deadening, no trim, and the exhaust exits at
    /// the side right against it. Loud, and audibly hollow.</summary>
    public static VehicleBody RaceSaloon => new()
    {
        SealLeak = 0.2f, WindNoiseDbAt110 = 74f,
        PanelThicknessM = 0.0009f,
        // Replacement panels with less pressed into them than the road car's had.
        PanelSpansM = new[] { 0.40f, 0.32f, 0.25f, 0.19f },
        PanelLoss = 0.03f,
        Coupling = 0.40f,
        CabinLengthM = 2.1f, CabinWidthM = 1.5f, CabinHeightM = 1.2f,
        CabinAbsorption = 0.08f,
        // No trim, no seals worth the name, and often no glass in the openings.
        CabinLeak = 0.30f,
    };

    /// <summary>An open-wheeler: no body to speak of and no cabin; the exhaust radiates into free
    /// air.</summary>
    public static VehicleBody OpenWheeler => new()
    {
        PanelMaterial = "Plastic",
        PanelThicknessM = 0.0025f,
        PanelSpansM = new[] { 0.30f, 0.20f },
        PanelLoss = 0.20f,
        Coupling = 0.06f,
        CabinLengthM = 0f, CabinWidthM = 0f, CabinHeightM = 0f,
        MaxModes = 6,
    };

    /// <summary>
    /// A muffler case: a small bare steel box, and where "metallic" comes from. The same type as a car
    /// body on purpose (one plate law, <see cref="PanelAcoustics"/>); the difference is in the numbers.
    /// A 40-series case (about 360 by 230 by 100 mm, welded to its baffles) has free spans of 100-230 mm
    /// against a car panel's 250-350, so its modes land in the high hundreds and low kilohertz; it has
    /// no deadening at all; and it is driven from inside by the full pressure in the chambers. A packed
    /// muffler's glasspack damps its shell too (<see cref="PackedMufflerCase"/>).
    /// How the spans, the loss and "not sealed" were settled by ear: docs/ENGINE_SYNTHESIS.md, "The body
    /// and the muffler case".
    /// </summary>
    public static VehicleBody MufflerCase => new()
    {
        PanelThicknessM = 0.0012f,
        // The small spans (end caps, seams, baffle strips), not the big face: against no can they lift
        // 5.6 kHz by 12 dB, 2.4 kHz by 9.5 and 1.8 kHz by 5.6, no band a decibel above its neighbour.
        // Span is the note and the loss is the Q, two separate levers. The lowest mode is at 194 Hz.
        PanelSpansM = new[] { 0.221f, 0.182f, 0.150f, 0.124f, 0.104f, 0.085f, 0.072f },
        MaxModes = 20,
        // T60 about 220 ms at 330 Hz falling to 25 ms at 3 kHz: the clank with a tail that read as "a
        // Flowmaster". Safe only because nothing is below 330 Hz: a lightly damped low mode (0.04 at
        // 133 Hz rings half a second) integrates the firing harmonics across a rev into one gong.
        PanelLoss = 0.030f,
        // Not sealed: as a sealed box the deep modes gained about 16 dB and one took 97 % of the
        // spectrum, a single droning note. The (ka)² rolloff holds the low modes in proportion.
        // No cabin: the gas volume inside is the chambers, modelled already.
        CabinLengthM = 0f, CabinWidthM = 0f, CabinHeightM = 0f,
        Coupling = 1f,      // scaled by MufflerSpec.ShellLevel where it is used
    };

    /// <summary>
    /// The same can heard as its big face, the long flat side ringing near 73 Hz. Rejected by ear:
    /// weight at 75-180 Hz reads as a muffled car once the render normalises by peak (docs/
    /// ENGINE_SYNTHESIS.md, "The body and the muffler case").
    /// </summary>
    public static VehicleBody DeepMufflerCase => MufflerCase with
    {
        PanelSpansM = new[] { 0.36f, 0.33f, 0.30f, 0.27f, 0.24f, 0.21f, 0.17f, 0.13f, 0.10f },
        MaxModes = 22,
    };

    /// <summary>A packed muffler's case: the packing against the shell damps it, a loss factor and not
    /// a different model.</summary>
    public static VehicleBody PackedMufflerCase => MufflerCase with { PanelLoss = 0.30f };

    /// <summary>No body at all, for A/B.</summary>
    public static VehicleBody None => new() { Coupling = 0f, MaxModes = 0, CabinLengthM = 0f };

    /// <summary>Every shell by key, so a machine's parts list can name one (see MachinePart).</summary>
    public static System.Collections.Generic.IReadOnlyDictionary<string, System.Func<VehicleBody>> Presets { get; } =
        new System.Collections.Generic.Dictionary<string, System.Func<VehicleBody>>(System.StringComparer.OrdinalIgnoreCase)
        {
            ["saloon"] = () => Saloon,
            ["van"] = () => Van,
            ["school_bus"] = () => SchoolBus,
            ["supercar"] = () => Supercar,
            ["race_saloon"] = () => RaceSaloon,
            ["open_wheeler"] = () => OpenWheeler,
            ["none"] = () => None,
        };

    public static VehicleBody ByName(string key)
        => Presets.TryGetValue(key, out var make) ? make()
         : throw new System.ArgumentException($"No body preset '{key}'. Known: {string.Join(", ", Presets.Keys)}");
}

/// <summary>
/// A parallel bank of two-pole resonators: a body's impulse response, applied. Each mode is normalised
/// exactly to unit gain at its own note, so its weight means what it says.
/// </summary>
public sealed class BodyResonator
{
    private readonly float[] _a1, _a2, _b0, _y1, _y2;
    private readonly float _coupling;
    private float _x1, _x2;

    public int ModeCount => _b0.Length;

    public BodyResonator(VehicleBody body, float sampleRate)
    {
        _coupling = Math.Clamp(body.Coupling, 0f, 1f);
        var modes = body.Modes();

        int n = modes.Count;
        _a1 = new float[n]; _a2 = new float[n]; _b0 = new float[n];
        _y1 = new float[n]; _y2 = new float[n];

        // Normalised against the strongest mode, not the sum: by the sum, sixteen modes left the loudest
        // about thirty decibels down, and a body with more panels came out quieter at each of them.
        float total = 0f;
        for (int i = 0; i < n; i++) total = MathF.Max(total, modes[i].Weight);
        if (total <= 0f) total = 1f;

        for (int i = 0; i < n; i++)
        {
            var mode = modes[i];
            float w = 2f * MathF.PI * mode.Hz / sampleRate;
            if (w >= MathF.PI) { _b0[i] = 0f; continue; }   // above Nyquist: not a resonance, an alias

            // Pole radius from the decay: r^(T60 * fs) = 10^-3.
            float r = MathF.Pow(10f, -3f / MathF.Max(1f, mode.DecaySeconds * sampleRate));
            r = Math.Clamp(r, 0f, 0.99999f);

            float a1 = 2f * r * MathF.Cos(w);
            float a2 = -r * r;

            // |H| = b0 |1 - z^-2| / |1 - a1 z^-1 - a2 z^-2| at z = e^{jw}; choose b0 so the magnitude
            // at the resonance is exactly one.
            float re = 1f - a1 * MathF.Cos(w) - a2 * MathF.Cos(2f * w);
            float im = a1 * MathF.Sin(w) + a2 * MathF.Sin(2f * w);
            float den = MathF.Sqrt(re * re + im * im);
            float num = MathF.Sqrt(2f - 2f * MathF.Cos(2f * w));   // |1 - e^{-2jw}|

            _a1[i] = a1;
            _a2[i] = a2;
            _b0[i] = num > 1e-6f ? den / num * (mode.Weight / total) : 0f;
        }
    }

    /// <summary>The body's own contribution for one input sample, which the caller adds to the direct
    /// sound.</summary>
    public float Process(float x)
    {
        if (_coupling <= 0f) return 0f;

        // Zeros at DC and Nyquist: an all-pole resonator has real gain far below its note, and driven
        // by a muffler's 40-200 Hz firing fundamental the can measured 5 to 8 dB below 200 Hz, under
        // every mode it has. A panel is a mechanical high-pass.
        float d = x - _x2;
        _x2 = _x1;
        _x1 = x;

        float sum = 0f;
        for (int i = 0; i < _b0.Length; i++)
        {
            float y = _b0[i] * d + _a1[i] * _y1[i] + _a2[i] * _y2[i];
            _y2[i] = _y1[i];
            _y1[i] = y;
            sum += y;
        }
        return sum * _coupling;
    }

    /// <summary>Forgets the ringing, for a voice being reused for a different car.</summary>
    public void Reset()
    {
        Array.Clear(_y1);
        Array.Clear(_y2);
        _x1 = _x2 = 0f;
    }
}

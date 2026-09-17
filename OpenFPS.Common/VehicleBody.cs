using System;
using System.Collections.Generic;

namespace OpenFPS.Common;

/// <summary>One resonance of a body: its note, how long it rings, and how strongly it is driven.</summary>
public readonly record struct BodyMode(float Hz, float DecaySeconds, float Weight);

/// <summary>
/// The car itself, as a thing the engine's sound has to get out through.
///
/// The physical model makes the source — the pressure wave leaving the tailpipe — and it makes it
/// well. What it has never had is the CAR: a couple of square metres of thin steel, a floorpan the
/// exhaust runs the length of, and a box of air in the middle with people sitting in it. All of that
/// is driven by the engine and radiates on its own account, and it is most of what separates one car
/// from another when the engines under them are similar. It is also, unlike the exhaust, a LINEAR
/// TIME-INVARIANT system, which is what makes it the honest home for an impulse response.
///
/// That distinction matters more than it looks. The gas path is not time-invariant: exhaust gas
/// leaves the head at 700-900 C and the speed of sound goes as the square root of absolute
/// temperature, so every pipe resonance sits about 75 % higher hot than cold and MOVES with load.
/// An impulse response fitted at one engine speed is wrong at every other, which is why the pipes
/// must stay in the physical model. A steel roof panel does not care how hot the gas is. So:
///
///   gas path  -> the waveguide model, with its own temperature scaling
///   body      -> this, an impulse response, fixed, and swappable to make a different car
///
/// WHY A RESONATOR BANK AND NOT A CONVOLUTION. A modal impulse response IS a sum of decaying
/// sinusoids — that is what a mode is — so running the signal through a parallel bank of two-pole
/// resonators tuned to those modes computes exactly the same thing as convolving with the rendered
/// response. The difference is only cost: a 60 ms response at 44.1 kHz is 2,600 taps per sample,
/// against six multiply-adds per mode. <see cref="ImpulseResponse"/> renders the response itself
/// when you want to look at it, or audition it, or measure it against a recording.
///
/// Nothing here was recorded and nothing needs to be. The modes come from
/// <see cref="PanelAcoustics"/> — the same plate law a door leaf and a window pane already use — and
/// the cabin's from the dimensions of the box.
/// </summary>
public sealed record VehicleBody
{
    /// <summary>What the panels are made of. Steel for almost everything; aluminium for a supercar,
    /// which is why one sounds tighter and higher than the same engine in a saloon.</summary>
    public string PanelMaterial { get; init; } = "Metal";

    /// <summary>Panel thickness, metres. Car body skin is 0.7 to 1.0 mm; a van's is thicker, a race
    /// car's composite shell is thinner and made of something else entirely.</summary>
    public float PanelThicknessM { get; init; } = 0.0008f;

    /// <summary>
    /// The FREE SPANS in the bodywork, metres — and this is not the size of the pressing.
    ///
    /// Measuring the first render caught the mistake immediately: with the roof's actual dimensions
    /// in here, 97 per cent of the body's energy came out below 200 Hz, which is not a car, it is a
    /// bass boost. A door skin 1.2 m across, taken as a flat plate, has a fundamental down around
    /// four hertz, and a real one plainly does not drum at four hertz.
    ///
    /// The reason is that a car panel is not a free flat plate. It is pressed with swages and beads,
    /// curved in two directions, and spot-welded to a structure every few inches — and every one of
    /// those divides it into sub-panels that vibrate on their own. What sets the note is the distance
    /// between the STIFFENERS, not the size of the sheet, and on a production car that is 150 to 400
    /// millimetres. Put those numbers in and the panels land where panels are heard, 50 Hz to a few
    /// kHz.
    ///
    /// It is also why the numbers differ between a van and a saloon in the direction they do: a van's
    /// flat sides are stiffened much more sparsely, which is exactly why they boom.
    /// </summary>
    public float[] PanelSpansM { get; init; } = new[] { 0.35f, 0.28f, 0.22f, 0.18f, 0.14f, 0.11f };

    /// <summary>
    /// Total loss factor of the panels as fitted — and the "expensive car" knob.
    ///
    /// Bare steel's own loss is about two ten-thousandths, which would have a car body ringing for
    /// most of a minute. It does not, for two reasons: it is welded to a structure, which is
    /// <see cref="PanelAcoustics.MountedLoss"/>, and almost every production car has bitumen or
    /// butyl deadening pads bonded to the large panels specifically to stop this happening. That
    /// second one is a CHOICE a manufacturer makes and it is audible: a van and a luxury saloon
    /// differ here by a factor of five, and that is a large part of why one sounds cheap.
    /// </summary>
    public float PanelLoss { get; init; } = 0.12f;

    /// <summary>How much of the engine's pressure gets into the structure at all, 0 to 1. A tailpipe
    /// slung under a floorpan drives it hard; an open-wheeler's exhaust radiates into free air and
    /// touches almost nothing. The one number here most worth fitting against a real recording.</summary>
    public float Coupling { get; init; } = 0.25f;

    /// <summary>Inside dimensions of the cabin, metres. All three zero for something with no cabin —
    /// a motorcycle, a formula car — which then has only its panels.</summary>
    public float CabinLengthM { get; init; } = 2.4f;
    public float CabinWidthM { get; init; } = 1.45f;
    public float CabinHeightM { get; init; } = 1.15f;

    /// <summary>
    /// Whether these panels are the walls of a SEALED volume rather than free plates.
    ///
    /// It decides whether the low modes radiate at all, and it is the difference between an
    /// open-baffle loudspeaker and a sealed cabinet. A free plate is a dipole: as one face pushes,
    /// the other pulls, and below the frequency where the plate is about a wavelength across the air
    /// simply flows round the edge from one to the other and nothing is compressed. That
    /// short-circuit is what <see cref="RadiationFactor"/>'s (ka)² describes, and it is why a bare
    /// panel is a poor low-frequency radiator.
    ///
    /// A muffler case has no such path. It is a welded steel box with gas sealed inside it, so its
    /// walls have air on one side only and radiate as sources rather than as dipoles, right down to
    /// where they stop moving. The deep modes that a free plate throws away, a sealed can keeps —
    /// which is most of why a chambered muffler has a body to its sound at all.
    ///
    /// Default false, because it is the conservative reading and because a car's outer skin is a
    /// large plate with the underbody and cavities connected round behind much of it.
    /// </summary>
    public bool SealedBox { get; init; }

    /// <summary>Average absorption of the cabin's surfaces. Seats, carpet and headlining are soft;
    /// a stripped race car is not, which is why it booms.</summary>
    public float CabinAbsorption { get; init; } = 0.35f;

    /// <summary>
    /// How much of the cabin's resonance reaches a listener OUTSIDE the car, 0 to 1.
    ///
    /// Almost none of it, and measuring made that obvious. A cabin boom is a resonance of the air
    /// inside the car: it is the thing the DRIVER hears, and it reaches the street only by driving
    /// the bodywork from the inside and being radiated again, or by leaking through the seals. At
    /// full weight it swamped everything — the first measurement put four fifths of a saloon's whole
    /// response below 200 Hz, which is a boom rather than a body.
    ///
    /// It is deliberately a leak rather than a deletion, because the same cabin at FULL weight is
    /// exactly what an interior mix needs, and a listener can already sit in one of these cars. When
    /// interior audio arrives this is the number it turns up, not a model it has to invent.
    /// </summary>
    public float CabinLeak { get; init; } = 0.15f;

    /// <summary>
    /// How many modes to actually run, loudest first.
    ///
    /// A real body has thousands. The ones that carry the character are the low, strongly-driven
    /// ones, and everything above a couple of kilohertz is dense enough to read as colouration
    /// rather than as pitches. This is a REAL budget — modes cost per sample, per voice, on a track
    /// with thirty cars on it — and it is spent on the strongest, which is the honest way to spend it.
    /// </summary>
    public int MaxModes { get; init; } = 16;

    /// <summary>Speed of sound in the cabin's air, m/s. Not the exhaust's — the cabin is at cabin
    /// temperature, which is the whole point of separating the two.</summary>
    private const float CabinSpeedOfSound = 343f;

    /// <summary>
    /// Every resonance this body has, strongest first, capped at <see cref="MaxModes"/>.
    ///
    /// Two families. The PANELS bend, by the plate law; their weights come out of the selection rule
    /// in <see cref="PanelAcoustics.Modes"/>, where half the modes turn out not to be driven at all.
    /// The CABIN is a box of air, and a box's axial modes go as (c/2)·sqrt((nx/Lx)² + ...) — the same
    /// shape of law, for the same reason, one dimension further up.
    /// </summary>
    public IReadOnlyList<BodyMode> Modes()
    {
        var material = AcousticRegistry.GetProperties(PanelMaterial);
        var modes = new List<BodyMode>();

        foreach (float span in PanelSpansM ?? Array.Empty<float>())
        {
            if (span <= 0.05f) continue;
            // A panel is not square: taking the other span as two thirds of the first keeps the two
            // spans of the plate law genuinely different, which is what stops every panel on the car
            // landing on the same note.
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

        // Strongest first, and then the budget. Spending it on the loudest is the only ranking that
        // does not amount to picking favourites.
        modes.Sort((x, y) => y.Weight.CompareTo(x.Weight));
        if (modes.Count > MaxModes) modes.RemoveRange(MaxModes, modes.Count - MaxModes);
        return modes;
    }

    /// <summary>
    /// How much of a mode's motion actually becomes sound, 0 to 1.
    ///
    /// A panel that is small compared with the wavelength it is moving at barely radiates: its two
    /// halves push and pull against each other and the air simply moves round the edge rather than
    /// being compressed. Power goes as (ka)² until the panel is about a wavelength across, and then
    /// stops rising. This is the SAME law, for the same reason, that OpenEnd.Radiate applies at the
    /// end of an exhaust pipe — where leaving it out was what made every high-revving engine come out
    /// ten decibels too loud and top-heavy.
    ///
    /// Here it does the opposite and is just as necessary. Without it the mode budget, ranked purely
    /// by how hard each mode is DRIVEN, kept the lowest mode of every panel and threw the rest away:
    /// the first render measured 97 per cent of its energy below 200 Hz, which is a bass boost rather
    /// than a car. A panel's low modes are driven hardest and radiate worst, and only both facts
    /// together give the balance a real body has.
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

        // Sabine. A car cabin is small and soft, so this comes out around a tenth of a second — long
        // enough to colour, far too short to read as a room, which is exactly right.
        float t60 = Math.Clamp(0.161f * volume / (surface * absorption), 0.02f, 0.5f);

        // The three axial modes. The tangential and oblique ones above them are dense and weak, and
        // a car's boom is axial: it is the length of the cabin you hear.
        Span<float> spans = stackalloc float[] { CabinLengthM, CabinWidthM, CabinHeightM };
        foreach (float length in spans)
        {
            float hz = CabinSpeedOfSound / (2f * length);
            if (hz < PanelAcoustics.MinimumRingHz) continue;
            // No radiation factor — a cabin does not radiate by flexing, and its openings are large
            // compared with the wavelengths it resonates at — but only CabinLeak of it gets out to
            // somebody standing in the road.
            into.Add(new BodyMode(hz, t60, Math.Clamp(CabinLeak, 0f, 1f)));
        }
    }

    /// <summary>
    /// The body's impulse response, rendered — for auditioning, for writing to a WAV, and for
    /// measuring against a recording of a real car.
    ///
    /// This is the same thing <see cref="BodyResonator"/> applies; it is simply the bank's answer to
    /// an impulse rather than to an engine.
    /// </summary>
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
    /// Which third of the spectrum a response lives in.
    ///
    /// Shares of the total rather than decibels, because an impulse response has no absolute level —
    /// it is a ratio by construction — and the only question worth asking of a body is where it puts
    /// what passes through it. Log-spaced probes, so an octave at the top counts for what an octave
    /// at the bottom does; measuring this linearly would call every panel a subwoofer.
    ///
    /// One implementation, shared by the diagnostic that prints it and the test that asserts on it,
    /// so the number a test guards is the number a person reads.
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

    /// <summary>A van. Bigger, thicker, flatter panels and almost no deadening, which is why the
    /// back of one booms and rattles at anything.</summary>
    public static VehicleBody Van => new()
    {
        PanelThicknessM = 0.0010f,
        // Long flat sides with very few beads in them — the widest free spans on the road.
        PanelSpansM = new[] { 0.40f, 0.33f, 0.26f, 0.20f, 0.15f },
        PanelLoss = 0.04f,
        Coupling = 0.35f,
        CabinLengthM = 3.2f, CabinWidthM = 1.7f, CabinHeightM = 1.8f,
        CabinAbsorption = 0.12f,
    };

    /// <summary>
    /// A supercar: small panels, few of them, and an aluminium shell — modelled as thicker metal,
    /// which is not a cheat.
    ///
    /// The plate law goes as t·sqrt(E/rho), and steel and aluminium have very nearly the SAME
    /// sqrt(E/rho): 5050 m/s against 5055. An aluminium panel of the same thickness therefore rings
    /// at essentially the same note as a steel one, and what actually differs is that aluminium is
    /// built thicker for the same stiffness. So the difference between a steel body and an aluminium
    /// one is a thickness, which is what is written here — and adding an "Aluminium" material would
    /// change nothing about the frequencies.
    /// </summary>
    /// <summary>
    /// A school bus: the biggest, thinnest, flattest steel box on the road.
    ///
    /// It differs from a van in the direction you would expect and by more than you would guess. The
    /// side skin is the same gauge but the RIBS are further apart, and a panel's note goes as the
    /// inverse SQUARE of its free span — so going from a van's 0.40 m bay to a bus's 0.62 m does not
    /// drop the panel a little, it drops it to less than half the frequency. That is why a bus
    /// booms where a van rattles.
    ///
    /// And the cabin behind it is enormous: eleven metres of hard flat surfaces with a few soft seats
    /// in it, whose lowest axial mode is under sixteen hertz. Everything the engine does gets poured
    /// into that and comes back slower.
    /// </summary>
    public static VehicleBody SchoolBus => new()
    {
        PanelThicknessM = 0.0011f,
        // Ribs about two feet apart over a very long flat flank.
        PanelSpansM = new[] { 0.62f, 0.52f, 0.44f, 0.34f, 0.24f },
        PanelLoss = 0.035f,
        Coupling = 0.40f,
        CabinLengthM = 11.0f, CabinWidthM = 2.4f, CabinHeightM = 2.0f,
        CabinAbsorption = 0.16f,
    };

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

    /// <summary>An open-wheeler has no body to speak of and no cabin at all: the exhaust radiates
    /// into free air. Almost nothing here, which is itself the character.</summary>
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
    /// A muffler case: a small bare steel box, and where "metallic" comes from.
    ///
    /// The same type as a car body on purpose. A door leaf, a window pane, a car's wing and the side
    /// of a muffler are all a flat piece of stuff that rings, by the same law — that is the whole
    /// argument of <see cref="PanelAcoustics"/>, and a second implementation for exhausts would be
    /// the same law written twice and free to drift.
    ///
    /// Everything that makes it sound different from a car's bodywork is in the NUMBERS, and every
    /// one of them points the same way:
    ///
    ///   SMALL SPANS put the modes high. A 40-series case is about 360 by 230 by 100 mm and is welded
    ///   to its own baffles, so its free spans are 100-230 mm against a car panel's 250-350. The plate
    ///   law goes as 1/span², so the modes land in the high hundreds and low kilohertz instead of at
    ///   40-600 Hz. That is where a listener hears "metallic".
    ///
    ///   NO DAMPING AT ALL. A car panel has bitumen pads bonded to it because a manufacturer paid to
    ///   stop exactly this. A chambered muffler has nothing: no packing, no deadening, bare steel. So
    ///   it rings where a car panel thuds.
    ///
    ///   AND IT IS DRIVEN FROM THE INSIDE, by the full pressure wave in the chambers rather than by
    ///   what leaks through a structure. That is the part that makes it loud enough to matter.
    ///
    /// It also explains a packed muffler for nothing: the glasspack that absorbs the gas is pressed
    /// against the case and damps it too, so an absorptive muffler's shell has a high loss and does
    /// not ring. Same model, opposite result, no special case anywhere.
    /// </summary>
    public static VehicleBody MufflerCase => new()
    {
        PanelThicknessM = 0.0012f,
        // THE SPANS THAT SHIPPED, chosen by ear after the big face was tried and rejected.
        //
        // A case is not one panel. Its big flat face spans the whole length and rings near 73 Hz;
        // its end caps, seams, baffle-welded strips and pinched edges are small and ring from a few
        // hundred hertz to several kilohertz. Emphasising the big face was tried first and made the
        // exhaust MUFFLED rather than fuller — energy piled into 75-180 Hz pulls everything above
        // 750 Hz down with it once the render normalises by peak, which is audible as a duller car
        // and measures as 4 to 13 dB off the top. What was actually wanted was ring ACROSS the
        // sound, and that comes from the small spans. Measured against no can at all, these lift
        // 5.6 kHz by 12 dB, 2.4 kHz by 9.5 and 1.8 kHz by 5.6, with no band more than a decibel
        // above its neighbour.
        // Widened 30 % over the spans first tried, which drops every mode about five semitones
        // together. Span is the tube's NOTE and the loss factor is its Q — two independent levers,
        // and confusing them cost several rounds: making the ring longer was heard as "a longer
        // tube" when what was wanted was a wider one. The lowest mode sits at 194 Hz.
        PanelSpansM = new[] { 0.221f, 0.182f, 0.150f, 0.124f, 0.104f, 0.085f, 0.072f },
        MaxModes = 20,
        // The free spans across the case, largest first.
        //
        // These went UP after a listening test, and the reason is worth keeping. With the largest
        // span at 0.23 m the case's lowest mode sat at 179 Hz — right inside the band the exhaust
        // already dominates — so the can added energy where there was plenty and changed nothing
        // anybody could hear. Measured, the rendered exhaust peaks at 237-562 Hz and is 12-20 dB
        // thinner between 75 and 180, which is exactly the "body" of the sound.
        //
        // A 40-series case is about 360 by 250 mm and its big flat face is not heavily braced, so a
        // free span of 300 mm is the honest reading of it rather than a convenient one — and it puts
        // the lowest mode near 105 Hz, underneath the exhaust rather than on top of it.
        // How long the case rings, and the last thing that was settled by ear.
        //
        // At 0.030 the small spans have T60s of about 220 ms at 330 Hz falling to 25 ms at 3 kHz —
        // a clank with a tail on it, which is what finally read as "a Flowmaster" rather than as a
        // coloured engine. Doubling the ring at the same LEVEL was the change that did it; six more
        // decibels of the same shape was not. Those are different knobs and it is worth knowing
        // which one "more aggressive" means.
        //
        // It is safe here in a way it was not for the big face. Light damping is dangerous only for
        // LOW modes: at 0.04 a mode down at 133 Hz has a half-second T60, and a mode that rings that
        // long stops being a resonance and becomes an INTEGRATOR — across a rev the firing harmonics
        // sweep through it repeatedly, each pass adding to what has not yet decayed, until one mode
        // carries the whole spectrum with the rest 12 to 18 dB beneath it. That is a gong, and it is
        // what the big-face case did. Nothing here is below 330 Hz, so nothing has time to integrate.
        PanelLoss = 0.030f,
        // NOT sealed, and this was tried the other way first.
        //
        // A welded can with gas inside it looks like a sealed box, and treating it as one removes the
        // (ka)² edge short-circuit from its low modes. Physically that is arguable. Acoustically it
        // was a disaster: the deep modes gained about 16 dB, one of them took 97 per cent of the
        // whole spectrum, and at every level the car became a single droning note. The (ka)² rolloff
        // turns out to be the only thing holding the low modes in proportion to each other, and a
        // model that needs it is telling you something — a muffler hung under a car is not a sealed
        // cabinet in free air; it is a box strapped to a large structure with its panels loaded on
        // both sides and plenty of paths round them.
        // No cabin: the gas volume inside a muffler is already modelled, as the chambers themselves.
        CabinLengthM = 0f, CabinWidthM = 0f, CabinHeightM = 0f,
        Coupling = 1f,      // scaled by MufflerSpec.ShellLevel where it is used
    };

    /// <summary>
    /// The same can heard as its BIG FACE — the long flat side, ringing near 73 Hz.
    ///
    /// Kept because it is a real part of a real case and because the reasoning is worth not
    /// repeating. It shipped for about an hour and was rejected by ear: weight at 75-180 Hz reads as
    /// a MUFFLED car rather than a full one, because once the render normalises by peak everything
    /// above 750 Hz comes down with it. Its other failure mode is worse and is in the notes — with
    /// the low modes lightly damped, one of them integrates the firing harmonics across a rev until
    /// it carries the whole spectrum, and the car becomes a single droning note.
    /// </summary>
    public static VehicleBody DeepMufflerCase => MufflerCase with
    {
        PanelSpansM = new[] { 0.36f, 0.33f, 0.30f, 0.27f, 0.24f, 0.21f, 0.17f, 0.13f, 0.10f },
        MaxModes = 22,
    };

    /// <summary>A packed muffler's case. The packing that absorbs the gas is against the shell and
    /// damps it, so it does not ring — the difference is a loss factor, not a different model.</summary>
    public static VehicleBody PackedMufflerCase => MufflerCase with { PanelLoss = 0.30f };

    /// <summary>No body at all — the model as it was before this existed. For A/B.</summary>
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
/// A parallel bank of two-pole resonators: a body's impulse response, applied.
///
/// Each mode is one resonator, normalised to unit gain AT ITS OWN NOTE so that a mode's weight means
/// what it says rather than being multiplied by whatever gain the pole placement happened to give
/// it. The normalisation is computed exactly — evaluate the transfer function at the resonance and
/// divide — rather than by one of the usual approximations, because it is done once per vehicle and
/// there is no reason to be approximate about something free.
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

        // Normalised against the STRONGEST mode, not against the sum of them.
        //
        // Dividing by the sum was the first thing written here and it made the whole layer inaudible:
        // sixteen modes normalised to sum to one leaves the loudest of them around a tenth, and a
        // tenth of a coupling of 0.25 is two and a half per cent — thirty decibels down, which is
        // nothing. It also had the wrong shape, because it made a body with more resonances QUIETER
        // at each of them, and a car with more panels is not a quieter car.
        //
        // Against the peak, the strongest resonance contributes Coupling of the drive at its own
        // note and the rest fall in behind it. Coupling then means what it says — how much of the
        // engine gets into the structure — instead of being divided by however many modes the budget
        // happened to keep.
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

    /// <summary>
    /// The body's own contribution for one input sample, in the same units as the input.
    ///
    /// It is what the caller ADDS to the direct sound, not a replacement for it: the tailpipe still
    /// radiates straight at the listener, and the car rings as well.
    /// </summary>
    public float Process(float x)
    {
        if (_coupling <= 0f) return 0f;

        // The zeros at DC and at Nyquist are what make this a RESONANCE rather than a filter that
        // happens to peak.
        //
        // Without them each mode is all-pole, and an all-pole resonator has real gain a long way
        // below its own note. That does not matter when the drive is broadband and it matters
        // enormously when the drive is an exhaust: the pressure inside a muffler is dominated by the
        // firing fundamental at 40-200 Hz, which is well under the lowest panel mode, and a small
        // off-resonance gain applied to an enormous low-frequency drive is a lot of output. Measured,
        // the can was adding 5 to 8 dB BELOW 200 Hz — beneath every mode it has — and taking the
        // whole render's headroom with it.
        //
        // A panel is a mechanical high-pass: below its fundamental it barely moves, and what little
        // it does move radiates as (ka)² of nothing. One zero at DC says that, and the matching zero
        // at Nyquist keeps the pair symmetric and the peak gain exact.
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

using System.Collections.Generic;
using System.Globalization;

namespace OpenFPS.Common;

/// <summary>
/// What is on the foot. The sole is a material in the registry, and its stiffness is nearly the whole
/// difference between shoes: rubber is four orders of magnitude softer than leather board, so its
/// contact lasts longer and one thuds where the other clicks.
/// </summary>
public sealed record Shoe
{
    public required string Name { get; init; }

    /// <summary>The sole, by name in the <see cref="AcousticRegistry"/>.</summary>
    public required string SoleMaterial { get; init; }

    /// <summary>Radius of curvature of the part of the heel in contact at the strike (the rounded back
    /// edge), metres: how concentrated the contact is.</summary>
    public float HeelRadiusM { get; init; } = 0.02f;

    /// <summary>How much of the ground's roughness the sole flows into rather than rattling over, 0 to 1.
    /// A floor under <see cref="Footsteps.Conformity"/>, which derives it from the sole's modulus, for a
    /// sole with tread deep enough to grip regardless.</summary>
    public float TreadGrip { get; init; } = 0.15f;

    /// <summary>Mass of the shoe itself, kg. It is part of what arrives.</summary>
    public float MassKg { get; init; } = 0.4f;

    /// <summary>
    /// The radius of a disc with the sole's area, metres: what pushes air (about 280 by 90 mm for an
    /// adult's shoe). Not the Hertzian contact patch, which is where the force is applied: confusing
    /// the two cost fifty decibels.
    /// </summary>
    public float SoleRadiusM { get; init; } = 0.09f;

    /// <summary>
    /// How thick the sole is, metres, and so the note the struck sole makes (the panel law: thickness
    /// times the root of stiffness over density). A trainer's midsole lands near 90 Hz, damped by the
    /// foam; a leather board sole near 250 Hz, the "clop". Before this a recording showed a 28 dB hole
    /// at 125-250 Hz.
    /// </summary>
    public float SoleThicknessM { get; init; } = 0.012f;

    /// <summary>
    /// How big the lumps of the sole are, metres: a tread block or the edge of a heel. The middle of a
    /// footstep's three contact sizes (heel about 3 cm, a thump near 40 Hz; grit about 0.5 mm, a hiss
    /// near 3 kHz); about a centimetre lands at a couple of hundred hertz, where a real footstep keeps
    /// its body. Without it the measurement showed a 30 dB hole there.
    /// </summary>
    public float TreadBlockM { get; init; } = 0.010f;

    /// <summary>A trainer: thick soft rubber, a big rounded heel, and it conforms to everything.</summary>
    public static Shoe Sneaker => new()
    {
        Name = "sneaker", SoleMaterial = "Rubber",
        HeelRadiusM = 0.035f, TreadGrip = 0.35f, MassKg = 0.35f, SoleRadiusM = 0.095f, SoleThicknessM = 0.022f, TreadBlockM = 0.008f,
    };

    /// <summary>A leather-soled dress shoe. Thin, hard, and a squared-off heel that lands on an edge.</summary>
    public static Shoe DressShoe => new()
    {
        Name = "dress shoe", SoleMaterial = "Leather",
        HeelRadiusM = 0.008f, TreadGrip = 0.02f, MassKg = 0.45f, SoleRadiusM = 0.085f, SoleThicknessM = 0.006f, TreadBlockM = 0.006f,
    };

    /// <summary>A work boot: hard rubber, heavy, and a wide heel that lands flat and hard.</summary>
    public static Shoe Boot => new()
    {
        Name = "boot", SoleMaterial = "BootRubber",
        HeelRadiusM = 0.022f, TreadGrip = 0.30f, MassKg = 0.9f, SoleRadiusM = 0.105f, SoleThicknessM = 0.016f, TreadBlockM = 0.020f,
    };

    /// <summary>A bare foot. Soft, wide, quiet, and it slaps rather than clicks.</summary>
    public static Shoe Bare => new()
    {
        Name = "bare foot", SoleMaterial = "Skin",
        HeelRadiusM = 0.045f, TreadGrip = 0.45f, MassKg = 0f, SoleRadiusM = 0.080f, SoleThicknessM = 0.020f, TreadBlockM = 0.025f,
    };

    public static IReadOnlyDictionary<string, Func<Shoe>> Presets { get; } =
        new Dictionary<string, Func<Shoe>>(StringComparer.OrdinalIgnoreCase)
        {
            ["sneaker"] = () => Sneaker,
            ["dress"] = () => DressShoe,
            ["boot"] = () => Boot,
            ["bare"] = () => Bare,
        };

    public static Shoe ByName(string key)
        => Presets.TryGetValue(key, out var make) ? make()
         : throw new ArgumentException($"No shoe '{key}'. Known: {string.Join(", ", Presets.Keys)}");
}

/// <summary>One footstep, as the thing that happened rather than as a file name.</summary>
public sealed record Footstep
{
    /// <summary>What is being walked on: a name in the <see cref="AcousticRegistry"/>.</summary>
    public required string Surface { get; init; }
    public required Shoe Shoe { get; init; }

    /// <summary>The walker, kg. A heavy person on a wooden floor is a different sound, not a louder
    /// one: more energy goes into the panel.</summary>
    public float BodyMassKg { get; init; } = 78f;

    /// <summary>How fast they are going, m/s. Walking is about 1.4; a run is 3.5 and up.</summary>
    public float SpeedMps { get; init; } = 1.4f;

    /// <summary>Which of the two feet, so a pair of steps is not two copies of one sound.</summary>
    public int Seed { get; init; }
}

/// <summary>
/// A footstep from the mechanism, in four parts:
/// <list type="number">
/// <item>The heel lands: a Hertzian impact whose length is set by the softer of sole and ground, and
/// nothing much above one over it is heard. A rubber sole squashes for 15 ms and cannot click; a
/// leather heel is in and out in 4 ms.</item>
/// <item>The sole drags over the roughness it does not conform to: a scatter of tiny impacts, the rest
/// of the difference between shoes and the whole difference between a smooth floor and a rough one.</item>
/// <item>The ground answers: a slab does not ring; a wooden floor is a panel between joists and rings
/// at its own modes (<see cref="PanelAcoustics"/>, as a door or a car's wing).</item>
/// <item>Loose material moves: gravel is a heap of stones, and the crunch is tens of tiny impacts
/// together.</item>
/// </list>
/// It happens three times, heel, roll and toe-off, and the gaps shorten with speed until at a run the
/// whole foot lands at once.
/// </summary>
public static class Footsteps
{
    // Not wired into the game: the client plays the footstep bank, and this is reachable only from
    // AudioLab --footsteps. Its renders measured about 20 dB too bright against real steps (session 10,
    // "gravel sounds like walking on broken glass"): docs/FOOTSTEP_SYNTHESIS_RESEARCH.md, "Session 10".

    public const int SampleRate = 48000;   // the rate the game mixes at (RenderRate.Default)

    /// <summary>
    /// The fraction of a walker's mass moving downward when the heel lands: the leg and the share of
    /// the trunk it carries. Gait measurement puts the initial impact peak at about a sixth of body
    /// mass; the rest arrives over a tenth of a second, and slow force is not sound.
    /// </summary>
    public const float EffectiveMassFraction = 0.17f;

    /// <summary>How fast the heel is going down when it lands, relative to how fast the walker is
    /// going forward.</summary>
    public const float HeelVelocityRatio = 0.28f;

    /// <summary>
    /// How much of the impact reaches the air through the ground rather than through the sole. Fitted,
    /// not derived: measured against a recording of somebody walking on concrete (`--footsteps
    /// compare=`, reference made by `tools/split_footsteps.py`). Like <see cref="VehicleBody.Coupling"/>,
    /// re-fit it, do not nudge it, if the model around it changes.
    /// </summary>
    public const float GroundCoupling = 0.16f;

    /// <summary>
    /// How the impact's energy divides between the whole-heel contact and the tread blocks. The blocks
    /// are the contact and the 20 ms bulk contact only their envelope: a full-strength bulk thump on top
    /// counted the force twice and measured as 18 dB of rubble at 30 Hz against a recording. Fitted
    /// against a recording (`--footsteps compare=`), the model's other fitted pair.
    /// </summary>
    public const float ThumpShare = 0.12f;
    public const float TreadShare = 3.0f;

    /// <summary>How hard the impact drives the sole's own panel modes. See Shoe.SoleThicknessM.</summary>
    public const float ShoeRingShare = 6.0f;

    /// <summary>How much of the ground's roughness a sole flows into: skin and soft rubber follow the
    /// grit almost exactly, a leather board sole sits on its high points.</summary>
    public static float Conformity(MaterialProperties sole)
    {
        float e = MathF.Max(1e-4f, sole.YoungsModulusGPa);
        // 1 at a thousandth of a GPa (skin, foam), falling to nearly nothing by a GPa (board, wood).
        float decades = MathF.Log10(e / 0.001f);
        return Math.Clamp(1f - decades / 3.5f, 0.02f, 1f);
    }

    /// <summary>
    /// Effective contact modulus of two things meeting, Pa: 1/E* = 1/E1 + 1/E2, so the softer wins. A
    /// rubber sole on granite or chipboard is the same contact, and the ground matters far more to a hard
    /// shoe than to a soft one.
    /// </summary>
    public static float ContactModulus(MaterialProperties sole, MaterialProperties ground)
    {
        float a = MathF.Max(1e5f, sole.YoungsModulusGPa * 1e9f);
        float b = MathF.Max(1e5f, ground.YoungsModulusGPa * 1e9f);
        return 1f / (1f / a + 1f / b);
    }

    /// <summary>
    /// How long the heel is in contact, seconds: Hertz's 2.87 (m² / (R E*² v))^(1/5). Doubling the
    /// walker's weight changes it by fifteen per cent; four decades of modulus between rubber and
    /// leather is a factor of four, two octaves of brightness. The impact has little above 1/τ.
    /// </summary>
    public static float ContactSeconds(float effectiveMassKg, float radiusM, float modulusPa, float velocityMps)
    {
        float m = MathF.Max(0.1f, effectiveMassKg);
        float r = MathF.Max(0.002f, radiusM);
        float e = MathF.Max(1e5f, modulusPa);
        float v = MathF.Max(0.05f, velocityMps);
        float tau = 2.87f * MathF.Pow(m * m / (r * e * e * v), 0.2f);
        // A contact longer than a tenth of a second is not an impact, it is standing on something.
        return Math.Clamp(tau, 0.0002f, 0.1f);
    }

    /// <summary>The corner above which an impact of this length has nothing left to say, Hz.</summary>
    public static float ImpactCornerHz(float contactSeconds)
        => Math.Clamp(1f / MathF.Max(1e-4f, contactSeconds), 12f, 16000f);

    /// <summary>
    /// How well a source this small radiates at this frequency, 0 to 1: (ka)², as on
    /// <see cref="VehicleBody"/>'s panels. A 2 cm patch is 36 dB down at 40 Hz (ka = 0.015) and 8 dB
    /// down at 1 kHz (ka = 0.37); without it the model measured 18 dB too bass-heavy against a recording.
    /// </summary>
    public static float RadiationEfficiency(float hz, float patchRadiusM)
    {
        float ka = 2f * MathF.PI * MathF.Max(1f, hz) * MathF.Max(0.002f, patchRadiusM) / 343f;
        return MathF.Min(1f, ka * ka);
    }

    // ── The floor as a panel ────────────────────────────────────────────────────────────────────

    /// <summary>How far a floor of this stuff spans between its supports, metres: the stiffeners set the
    /// note, not the size of the sheet. A slab bears on the ground everywhere and has none.</summary>
    public static float FreeSpanM(string surface) => surface.ToLowerInvariant() switch
    {
        "wood" => 0.40f,          // boards on joists at 16 inches
        "metal" => 0.60f,         // a steel deck or a walkway plate
        "glass" => 0.80f,
        _ => 0f,                  // bearing on the ground: no span, no note
    };

    /// <summary>How thick that deck is, metres.</summary>
    public static float DeckThicknessM(string surface) => surface.ToLowerInvariant() switch
    {
        "wood" => 0.021f,
        "metal" => 0.005f,
        "glass" => 0.012f,
        _ => 0f,
    };

    // ── The ground at the scale of a grain of grit ──────────────────────────────────────────────

    /// <summary>
    /// How coarse the ground is where the foot touches it, millimetres, and how much of it is covered.
    /// Not <see cref="MaterialProperties.Scattering"/>, which is roughness against a wavelength: polished
    /// concrete is acoustically specular and still gritty, and carpet scatters sound and is smooth
    /// underfoot.
    /// </summary>
    public static (float GrainMm, float Coverage) ContactTexture(string surface) => surface.ToLowerInvariant() switch
    {
        // A cast or floated slab: sand and exposed aggregate, and enough of it to hear.
        "concrete" => (0.45f, 0.80f),
        // Polished stone. Nearly nothing there, which is why a marble hall is all click and no scrape.
        "marble" => (0.03f, 0.25f),
        "glass" => (0.02f, 0.15f),
        // Sawn and sealed boards: a fine grain, and much less of it than concrete.
        "wood" => (0.12f, 0.45f),
        // Plate or chequer: smooth between the pattern, sharp on it.
        "metal" => (0.08f, 0.35f),
        // The sole sinks into soft things: nothing to rattle over.
        "carpet" => (0.05f, 0.20f),
        "grass" => (0.30f, 0.35f),
        "dirt" => (0.60f, 0.70f),
        "gravel" => (2.00f, 0.95f),
        _ => (0.25f, 0.50f),
    };

    /// <summary>
    /// How long one grain of grit (or tread block) is in contact, seconds. With mass going as the cube of
    /// size, Hertz's τ is proportional to size: a 0.3 mm grain against a 35 mm heel is a hundred times
    /// shorter, a few kilohertz. That is the click on top of a soft sole's 20 ms (40 Hz) thump.
    /// </summary>
    public static float GrainContactSeconds(float bulkContactSeconds, float heelRadiusM, float grainMm)
    {
        float grainM = MathF.Max(1e-5f, grainMm * 0.001f);
        float ratio = Math.Clamp(grainM / MathF.Max(0.002f, heelRadiusM), 1e-4f, 1f);
        return MathF.Max(2e-5f, bulkContactSeconds * ratio);
    }

    // ── The ground as a heap of loose pieces ────────────────────────────────────────────────────

    /// <summary>How many loose pieces a foot disturbs on a heap (gravel, dirt, grass), and how big they
    /// are, mm; zero for anything solid. Each pair that slips is its own tiny impact.</summary>
    public static (int Count, float GrainMm) LooseMaterial(string surface) => surface.ToLowerInvariant() switch
    {
        "gravel" => (70, 12f),
        "dirt" => (22, 4f),
        "grass" => (30, 1.5f),    // blades, not stones: many, tiny, and very quiet
        _ => (0, 0f),
    };

    // ── Rendering ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The whole step: heel, roll, toe-off, and whatever the ground does about it. Deterministic for a
    /// seed, so a step can be cached, and the seed varies per step.
    /// </summary>
    public static float[] Render(Footstep step, int sampleRate = SampleRate)
    {
        AcousticRegistry.EnsureInitialized();
        var ground = AcousticRegistry.GetProperties(step.Surface);
        var sole = AcousticRegistry.GetProperties(step.Shoe.SoleMaterial);
        var rng = new Random(step.Seed * 7919 + 13);

        bool running = step.SpeedMps > 2.6f;
        // A run drops the body onto the leg from a height; a walk places it.
        float heelV = MathF.Max(0.15f, step.SpeedMps * HeelVelocityRatio * (running ? 1.9f : 1f));
        float mEff = step.BodyMassKg * EffectiveMassFraction + step.Shoe.MassKg;

        // A walking foot rolls from heel to toe over about a fifth of a second; a running one lands
        // nearly flat and the three contacts collapse into one heavier impact.
        float roll = running ? 0.035f : 0.115f + (float)rng.NextDouble() * 0.03f;
        float toeAt = running ? 0.075f : 0.225f + (float)rng.NextDouble() * 0.04f;

        float total = toeAt + 1.2f;
        int n = (int)(sampleRate * total);
        var buf = new float[n];

        // The heel: the loudest by a long way, and the character.
        Contact(buf, 0f, mEff, heelV, step, ground, sole, rng, sampleRate, weight: 1f);

        // The roll: mostly the sole sliding a few millimetres over the roughness.
        Contact(buf, roll, mEff * 0.35f, heelV * 0.35f, step, ground, sole, rng, sampleRate,
                weight: running ? 0.55f : 0.30f, scuffBias: 2.2f);

        // The toe-off: quieter, and brighter than the roll, the sole peeled off a small patch.
        Contact(buf, toeAt, mEff * 0.22f, heelV * 0.5f, step, ground, sole, rng, sampleRate,
                weight: running ? 0.45f : 0.22f, scuffBias: 1.4f);

        return buf;
    }

    /// <summary>One contact of the foot with the ground: the four mechanisms, at one instant.</summary>
    private static void Contact(float[] buf, float atSeconds, float massKg, float velocity,
                                Footstep step, MaterialProperties ground, MaterialProperties sole,
                                Random rng, int sampleRate, float weight, float scuffBias = 1f)
    {
        int at = (int)(atSeconds * sampleRate);
        if (at >= buf.Length) return;

        float joules = 0.5f * massKg * velocity * velocity;
        float modulus = ContactModulus(sole, ground);
        float tau = ContactSeconds(massKg, step.Shoe.HeelRadiusM, modulus, velocity);
        float corner = ImpactCornerHz(tau);

        // A slab returns nearly everything as sound; turf swallows it.
        float returned = 1f - Math.Clamp(ground.Absorption, 0f, 0.95f);
        float levelDb = PanelAcoustics.ImpactDb(joules) + 10f * MathF.Log10(MathF.Max(0.02f, returned));
        float amp = weight * MathF.Pow(10f, (levelDb - 94f) / 20f);

        // Everything at the contact radiates from the small patch of sole and pays its (ka)² penalty
        // together; the floor's ring is a large radiator and is added afterwards, outside this.
        int span = Math.Min(buf.Length - at, (int)(sampleRate * 0.6f));
        if (span <= 0) return;
        var near = new float[span];

        // 1. The bulk contact: weak on purpose, the envelope of the tread contacts (ThumpShare).
        Thump(near, 0, amp * ThumpShare, corner, tau, ground, rng, sampleRate);

        // 2. The grit the sole rattles over. The grain sets the force's spectrum; the sole's patch
        // decides how much of it gets out.
        float conform = MathF.Max(Conformity(sole), step.Shoe.TreadGrip);
        var (grainMm, coverage) = ContactTexture(step.Surface);
        float grainTau = GrainContactSeconds(tau, step.Shoe.HeelRadiusM, grainMm);
        float scuffLevel = amp * coverage * (1f - conform) * 1.9f * scuffBias;
        if (scuffLevel > 1e-5f)
            Scuff(near, 0, scuffLevel, ImpactCornerHz(grainTau), tau, rng, sampleRate);

        // 3. The sole's tread blocks: a couple of hundred hertz, the body of the sound, on any ground,
        // because it is the sole that is lumpy.
        float treadTau = GrainContactSeconds(tau, step.Shoe.HeelRadiusM, step.Shoe.TreadBlockM * 1000f);
        Scuff(near, 0, amp * TreadShare * scuffBias, ImpactCornerHz(treadTau), tau * 1.6f, rng, sampleRate);

        // 4. The sole itself, a small struck panel: rubber's loss factor is two orders above steel's,
        // so a short "clop", not a ring. Two and a half times as long as wide, damped by the foot.
        float soleWidth = step.Shoe.SoleRadiusM * 1.15f;
        Ring(near, 0, amp * ShoeRingShare, sole, soleWidth, soleWidth * 2.5f, step.Shoe.SoleThicknessM,
             rng, sampleRate, mounting: 0.25f);

        // 5. Loose pieces, if the ground is a heap of them.
        var (count, stoneMm) = LooseMaterial(step.Surface);
        if (count > 0)
            Crunch(near, 0, amp, count, stoneMm, weight, rng, sampleRate);

        // Two paths out; modelling only the sole's left the first attempt 30 dB short below 500 Hz.
        // Through the sole, a 9 cm disc: (ka)², 12 dB an octave down below about 600 Hz. Through the
        // ground: a point load spreads over about a bending wavelength (metres of concrete at 100 Hz),
        // so it radiates broadband, loud on a slab and nearly absent on turf, which does not move.
        float groundShare = GroundCoupling * (1f - Math.Clamp(ground.Absorption, 0f, 0.95f));
        for (int i = 0; i < span; i++) buf[at + i] += near[i] * groundShare;

        Radiate(near, step.Shoe.SoleRadiusM, sampleRate);
        for (int i = 0; i < span; i++) buf[at + i] += near[i];

        // 6. The floor, if it is a panel: after the radiation filter, because a deck is a square metre
        // of moving surface and radiates well at the frequencies it rings at.
        float freeSpan = FreeSpanM(step.Surface), thick = DeckThicknessM(step.Surface);
        if (freeSpan > 0f && thick > 0f)
            Ring(buf, at, amp, ground, freeSpan, freeSpan * 2.4f, thick, rng, sampleRate, mounting: 0.09f);
    }

    /// <summary>Applies (ka)²: two one-pole high-passes at the frequency where ka reaches one, twelve dB
    /// an octave below it and flat above. The shape only; the level is PanelAcoustics.ImpactDb's.</summary>
    private static void Radiate(float[] x, float patchRadiusM, int sampleRate)
    {
        float kaOne = Math.Clamp(343f / (2f * MathF.PI * MathF.Max(0.002f, patchRadiusM)), 200f, 12000f);
        float k = MathF.Exp(-2f * MathF.PI * kaOne / sampleRate);
        float lp1 = 0f, lp2 = 0f;
        for (int i = 0; i < x.Length; i++)
        {
            float v = x[i];
            lp1 += (v - lp1) * (1f - k);
            float hp1 = v - lp1;
            lp2 += (hp1 - lp2) * (1f - k);
            x[i] = hp1 - lp2;
        }
    }

    /// <summary>
    /// The bulk impact: a force that takes <paramref name="tau"/> to happen, so nothing above 1/tau. Noise
    /// through a two-pole low-pass rather than a half-sine, because a real contact is never clean.
    /// </summary>
    private static void Thump(float[] buf, int at, float amp, float corner, float tau,
                              MaterialProperties ground, Random rng, int sampleRate)
    {
        // Soft ground lets the thump outlast the contact: the earth under the foot moves with it.
        float decay = tau * (2.5f + 6f * Math.Clamp(ground.Absorption, 0f, 1f));
        int len = Math.Min(buf.Length - at, (int)(sampleRate * decay * 6f) + 8);
        if (len <= 0) return;

        float k = MathF.Exp(-2f * MathF.PI * corner / sampleRate);
        float lp1 = 0f, lp2 = 0f;
        for (int i = 0; i < len; i++)
        {
            float t = i / (float)sampleRate;
            // The rise over the first tenth of the contact keeps it from sounding like a click.
            float env = (1f - MathF.Exp(-t / (0.12f * tau))) * MathF.Exp(-t / decay);
            float x = (float)(rng.NextDouble() * 2.0 - 1.0) * env;
            lp1 += (x - lp1) * (1f - k);
            lp2 += (lp1 - lp2) * (1f - k);
            buf[at + i] += lp2 * amp * 3.2f;
        }
    }

    /// <summary>The sole crossing the roughness: a burst of very short, small impacts, above what the
    /// bulk impact can reach. The click a hard shoe makes on a pavement.</summary>
    private static void Scuff(float[] buf, int at, float amp, float grainCornerHz, float tau,
                              Random rng, int sampleRate)
    {
        // Longer than the bulk impact: the foot still travels a few millimetres as it settles.
        float dur = tau * 2.2f;

        // A fixed energy spread over a longer contact is quieter: without this a step on grass came
        // out louder than one on concrete (the tests caught it).
        amp *= MathF.Sqrt(0.012f / MathF.Max(1e-4f, dur));
        int len = Math.Min(buf.Length - at, (int)(sampleRate * dur * 3.5f) + 8);
        if (len <= 0) return;

        float hi = Math.Clamp(grainCornerHz, 60f, sampleRate * 0.45f);
        // Half a decade wide: grains or lugs are one size, so their energy sits round one corner. At a
        // decade, a lug whose contact said 200 Hz put energy at 20 Hz, a mound of rubble where a real
        // footstep is quietest.
        float lo = hi * 0.45f;
        float kHi = MathF.Exp(-2f * MathF.PI * hi / sampleRate);
        float kLo = MathF.Exp(-2f * MathF.PI * lo / sampleRate);
        float lpHi = 0f, lpLo = 0f;

        for (int i = 0; i < len; i++)
        {
            float t = i / (float)sampleRate;
            // Irregular, as grains are crossed: the difference between grit and hiss.
            float env = MathF.Exp(-t / dur) * (0.35f + 0.65f * (float)rng.NextDouble());
            float x = (float)(rng.NextDouble() * 2.0 - 1.0) * env;
            lpHi += (x - lpHi) * (1f - kHi);          // everything below the grain corner
            lpLo += (lpHi - lpLo) * (1f - kLo);       // ...minus everything below the band
            buf[at + i] += (lpHi - lpLo) * amp * 2.6f;
        }
    }

    /// <summary>A panel (a floor between its joists, or a sole) ringing at its own modes through
    /// <see cref="PanelAcoustics"/>: the "hollow" of a wooden floor.</summary>
    private static void Ring(float[] buf, int at, float amp, MaterialProperties material,
                             float width, float height, float thickness, Random rng, int sampleRate,
                             float mounting = 0.09f)
    {
        var modes = PanelAcoustics.Modes(material, width, height, thickness, maxHz: 3000f, order: 5);
        if (modes.Count == 0) return;

        // Mounted, not free: a floor screwed to joists and a sole round a foot are far more damped.
        int taken = 0;
        foreach (var mode in modes)
        {
            if (taken++ >= 6) break;
            float t60 = PanelAcoustics.RingSeconds(material, mode.Hz, mounting);
            int len = Math.Min(buf.Length - at, (int)(sampleRate * t60 * 0.7f));
            if (len <= 0) continue;

            float w = 2f * MathF.PI * mode.Hz / sampleRate;
            float decay = MathF.Exp(-6.91f / MathF.Max(1f, t60 * sampleRate));
            float phase = (float)(rng.NextDouble() * Math.PI * 2.0);
            // The jitter is where on the board the foot landed.
            float g = amp * mode.Weight * 0.5f * (0.6f + 0.8f * (float)rng.NextDouble());
            float env = 1f;
            for (int i = 0; i < len; i++)
            {
                buf[at + i] += MathF.Sin(w * i + phase) * env * g;
                env *= decay;
            }
        }
    }

    /// <summary>
    /// The crunch: tens of tiny impacts as loose pieces are pushed past each other, most in the first
    /// thirty milliseconds and then a few stragglers, which is what makes gravel crackle rather than
    /// hiss. A piece's frequency goes as one over its size.
    /// </summary>
    private static void Crunch(float[] buf, int at, float amp, int count, float grainMm, float weight,
                               Random rng, int sampleRate)
    {
        int pieces = Math.Max(1, (int)(count * Math.Clamp(weight, 0.05f, 1f)));

        // Mass, and so energy, goes as size cubed: a 1.5 mm blade of grass carries a thousandth of a
        // 12 mm stone. Without it a step on grass came out louder than one on concrete.
        float piece = MathF.Pow(Math.Clamp(grainMm / 12f, 0.02f, 4f), 1.5f);
        // A centimetre of rock is a few kilohertz: grit hisses and ballast rattles.
        float baseHz = Math.Clamp(38000f / MathF.Max(0.5f, grainMm), 200f, 12000f);

        for (int p = 0; p < pieces; p++)
        {
            // Clustered in time: most of them in the first few tens of milliseconds.
            double u = rng.NextDouble();
            float when = (float)(-0.028 * Math.Log(1.0 - u * 0.985));
            int start = at + (int)(when * sampleRate);
            if (start >= buf.Length - 4) continue;

            // Most of the pieces that move barely move; a few take a real knock.
            float energy = MathF.Exp(-2.2f * (float)rng.NextDouble() * (float)rng.NextDouble() * 3f);
            float hz = baseHz * (0.45f + 1.6f * (float)rng.NextDouble());

            // A stone is not a bell: a sine per piece made a tinkle ("gravel sounds like walking on
            // broken glass"). Dense, mistuned, heavily damped modes give a click with a centre: a
            // couple of milliseconds of noise round the frequency its size implies.
            float ring = 0.0012f + 0.004f * (float)rng.NextDouble();
            int len = Math.Min(buf.Length - start, (int)(sampleRate * ring * 4f) + 4);

            float k = MathF.Exp(-2f * MathF.PI * MathF.Min(hz, sampleRate * 0.45f) / sampleRate);
            float decay = MathF.Exp(-1f / MathF.Max(1f, ring * sampleRate));
            float g = amp * energy * piece * 0.5f;
            float env = 1f, lp = 0f, band = 0f;
            for (int i = 0; i < len; i++)
            {
                float noise = (float)(rng.NextDouble() * 2.0 - 1.0) * env;
                lp += (noise - lp) * (1f - k);        // below the stone's own note
                band += ((noise - lp) - band) * 0.5f; // ...and a little smoothing above it
                buf[start + i] += band * g;
                env *= decay;
            }
        }
    }

    // ── Identity, so a step can be named, cached and sent ───────────────────────────────────────

    /// <summary>
    /// The key a <see cref="TransientSound.SynthKey"/> carries. Quantised, because the render is cached
    /// by it: mass to 10 kg, speed to half a metre a second, and eight seeds per surface.
    /// </summary>
    public static string Key(Footstep step)
        => string.Format(CultureInfo.InvariantCulture, "footstep:{0}:{1}:{2}:{3}:{4}",
            step.Surface.ToLowerInvariant(), step.Shoe.Name.Replace(' ', '_'),
            (int)MathF.Round(step.BodyMassKg / 10f) * 10,
            // To the half metre a second: a walk at 1.4 is "1.5", not a run.
            MathF.Round(step.SpeedMps * 2f) / 2f,
            step.Seed & 7);

    /// <summary>Reads one back. False for anything that is not a footstep key.</summary>
    public static bool TryParseKey(string? key, out Footstep step)
    {
        step = null!;
        if (string.IsNullOrEmpty(key)) return false;
        if (!key.StartsWith("footstep:", StringComparison.OrdinalIgnoreCase)) return false;

        var parts = key.Split(':');
        if (parts.Length < 6) return false;
        string shoeName = parts[2].Replace('_', ' ');
        // The shoe presets are keyed by their short name; a step names the shoe it was wearing.
        Shoe? shoe = null;
        foreach (var kv in Shoe.Presets)
            if (string.Equals(kv.Value().Name, shoeName, StringComparison.OrdinalIgnoreCase)) { shoe = kv.Value(); break; }
        if (shoe == null && Shoe.Presets.ContainsKey(parts[2])) shoe = Shoe.ByName(parts[2]);
        if (shoe == null) return false;

        if (!float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float mass)) return false;
        if (!float.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out float speed)) return false;
        if (!int.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out int seed)) return false;

        step = new Footstep { Surface = parts[1], Shoe = shoe, BodyMassKg = mass, SpeedMps = speed, Seed = seed };
        return true;
    }

    /// <summary>What this step measures at one metre, dB SPL: measured from the render, not declared, so
    /// the level cannot drift from the model.</summary>
    public static float MeasuredLevelDb(Footstep step, int sampleRate = SampleRate)
    {
        var buf = Render(step, sampleRate);
        float peak = 0f;
        foreach (float x in buf) { float a = MathF.Abs(x); if (a > peak) peak = a; }
        // The render is in the same units PanelAcoustics.ImpactDb works in, referred to 94 dB = 1.0.
        return 94f + 20f * MathF.Log10(MathF.Max(1e-6f, peak));
    }
}

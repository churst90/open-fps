using System;
using System.Collections.Generic;
using System.Globalization;

namespace OpenFPS.Common;

/// <summary>
/// What is on the foot. A sole is a MATERIAL, and that is nearly the whole difference between shoes.
///
/// A sneaker and a dress shoe striking the same slab differ almost entirely in how stiff the thing
/// doing the striking is, and stiffness is not a property of "footwear", it is a number in the
/// material registry. Rubber is four orders of magnitude softer than leather board, which makes the
/// contact last longer, which rolls the spectrum down — that single chain is why one thuds and the
/// other clicks, and nothing has to be authored per shoe to get it.
/// </summary>
public sealed record Shoe
{
    public required string Name { get; init; }

    /// <summary>The sole, by name in the <see cref="AcousticRegistry"/>.</summary>
    public required string SoleMaterial { get; init; }

    /// <summary>
    /// Radius of curvature where the heel meets the ground, metres.
    ///
    /// Not the size of the heel — the size of the part of it that is actually in contact at the
    /// instant of the strike, which for a shoe landing heel-first is the rounded back edge. It sets
    /// how concentrated the contact is, and so how hard the surface is pressed for a given force.
    /// </summary>
    public float HeelRadiusM { get; init; } = 0.02f;

    /// <summary>
    /// How much of the ground's roughness the sole flows into rather than rattling over, 0 to 1.
    ///
    /// The other half of what a sole does, and the half that is not stiffness. A soft rubber sole
    /// CONFORMS to the grit on a pavement; a hard leather one bridges it and lands on the high points,
    /// which is a scatter of tiny impacts and is most of where a hard shoe's brightness comes from.
    /// Derived from the sole's own modulus rather than authored — see <see cref="Footsteps.Conformity"/>
    /// — and this is only the floor under it, for a sole with tread deep enough to grip regardless.
    /// </summary>
    public float TreadGrip { get; init; } = 0.15f;

    /// <summary>Mass of the shoe itself, kg. It is part of what arrives.</summary>
    public float MassKg { get; init; } = 0.4f;

    /// <summary>A trainer: thick soft rubber, a big rounded heel, and it conforms to everything.</summary>
    public static Shoe Sneaker => new()
    {
        Name = "sneaker", SoleMaterial = "Rubber",
        HeelRadiusM = 0.035f, TreadGrip = 0.35f, MassKg = 0.35f,
    };

    /// <summary>A leather-soled dress shoe. Thin, hard, and a squared-off heel that lands on an edge.</summary>
    public static Shoe DressShoe => new()
    {
        Name = "dress shoe", SoleMaterial = "Leather",
        HeelRadiusM = 0.008f, TreadGrip = 0.02f, MassKg = 0.45f,
    };

    /// <summary>A work boot: hard rubber, heavy, and a wide heel that lands flat and hard.</summary>
    public static Shoe Boot => new()
    {
        Name = "boot", SoleMaterial = "BootRubber",
        HeelRadiusM = 0.022f, TreadGrip = 0.30f, MassKg = 0.9f,
    };

    /// <summary>A bare foot. Soft, wide, quiet, and it slaps rather than clicks.</summary>
    public static Shoe Bare => new()
    {
        Name = "bare foot", SoleMaterial = "Skin",
        HeelRadiusM = 0.045f, TreadGrip = 0.45f, MassKg = 0f,
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
    /// <summary>What is being walked on — a name in the <see cref="AcousticRegistry"/>.</summary>
    public required string Surface { get; init; }
    public required Shoe Shoe { get; init; }

    /// <summary>The walker, kg. A heavy person on a wooden floor is a different sound, not a louder
    /// one: more energy goes into the panel and the panel is what you hear.</summary>
    public float BodyMassKg { get; init; } = 78f;

    /// <summary>How fast they are going, m/s. Walking is about 1.4; a run is 3.5 and up.</summary>
    public float SpeedMps { get; init; } = 1.4f;

    /// <summary>Which of the two feet, so a pair of steps is not two copies of one sound.</summary>
    public int Seed { get; init; }
}

/// <summary>
/// A footstep, from the mechanism.
///
/// It is not one impact and it is not one sample. Four things happen, and a recording can only ever
/// be one instance of all four at once, on one surface, in one shoe, at one weight, at one speed —
/// which is why a game that uses recordings needs dozens of them and still sounds like it is playing
/// a list.
///
/// <list type="number">
/// <item><b>The heel lands.</b> A Hertzian impact between the sole and the ground. How long the
/// contact lasts is set by how stiff the SOFTER of the two is, and the sound cannot contain
/// frequencies much above one over that time. A rubber sole squashes for fifteen milliseconds and so
/// cannot make a click; a leather heel is in and out in four and therefore can. One number, from the
/// materials, and it is most of the difference between shoes.</item>
///
/// <item><b>The sole drags over the roughness.</b> Whatever does not conform to the grit rattles
/// across it — a scatter of tiny impacts through the contact, broadband and brief. A hard sole on a
/// coarse pavement does a great deal of this and a soft one on polished concrete does almost none,
/// which is the rest of the difference between shoes and the whole difference between a smooth floor
/// and a rough one. <see cref="MaterialProperties.Scattering"/> IS the roughness; nothing new had to
/// be invented for it.</item>
///
/// <item><b>The ground answers.</b> A concrete slab does not — it is too massive and too lossy to
/// ring, and what you hear is the room. A wooden floor is a PANEL between joists and it rings at its
/// own modes, which is what "hollow" means when people say a wooden floor sounds hollow;
/// <see cref="PanelAcoustics"/> already knows how to work that out and is the same code that rings a
/// door and a car's wing.</item>
///
/// <item><b>Loose material moves.</b> Gravel is not a surface, it is a heap of stones, and a foot
/// landing in it displaces tens of them. Each is a tiny impact of its own and the crunch is all of
/// them together — the same reasoning as a crowd, where a thousand claps are one texture rather than
/// a thousand voices. Grass and dirt do a quieter version of it.</item>
/// </list>
///
/// Then it happens three times: heel, the weight rolling forward, and the toe pushing off. The gaps
/// between those are what make a step sound like a step rather than a knock, and they shorten as the
/// walker speeds up until at a run the whole foot lands at once.
/// </summary>
public static class Footsteps
{
    // ─────────────────────────────────────────────────────────────────────────────────────────────
    //  THIS DOES NOT SOUND LIKE FOOTSTEPS YET, AND THE REASON IS MEASURED. Session 10.
    //
    //  Played to the owner, the verdict was "none of them pass" and "gravel sounds like walking on
    //  broken glass". A band analysis of the renders says exactly why, and it is not the physics
    //  above — it is the rendering below:
    //
    //      concrete, trainer        30-60 Hz  -24 dB  ...  8-16 kHz   -3 dB
    //      concrete, dress shoe     30-60 Hz  -19 dB  ...  8-16 kHz   -2 dB
    //      gravel, boot             30-60 Hz  -18 dB  ...  8-16 kHz   -3 dB
    //
    //  EVERY ONE OF THEM RISES TO 16 kHz AND PEAKS THERE. A real footstep does the opposite: most of
    //  its energy is between about a hundred and six hundred hertz and it rolls off hard above one or
    //  two kilohertz. These are tilted about twenty decibels the wrong way, which is why they read as
    //  thin, glassy and nothing like a foot — and why gravel in particular reads as broken glass,
    //  since glass is precisely the material that rings at those frequencies.
    //
    //  Two concrete faults, both in the renderers and neither in the model:
    //
    //  1. ONE-POLE FILTERS ARE 6 dB PER OCTAVE and they are being asked to define BANDS. Scuff's
    //     "band" between lo and hi is a difference of two one-poles, which at three octaves above the
    //     corner is still only about eighteen decibels down — so what is meant to be a band of grit
    //     around three kilohertz is in practice bright noise all the way to Nyquist. Bands need
    //     something steeper: a biquad, or several poles.
    //
    //  2. CRUNCH INJECTS RAW WHITE NOISE (`noise * 0.55f`) with no filter on it at all, so every
    //     stone contributes full-band hiss up to 22 kHz. A stone is a small, heavily damped,
    //     IRREGULAR lump — it clicks, it does not ring, and it certainly does not hiss. Giving each
    //     grain a sine resonance was the second half of that mistake: a sine at a few kilohertz is a
    //     bell, and a heap of little bells is a tinkle, which is the "broken glass".
    //
    //  The mechanisms — contact time from the softer material, the grain being two decades smaller
    //  than the heel, the floor as a panel, the heap as many small events — measure sensibly and are
    //  worth keeping. What is between them and a sound is a competent filter bank.
    //
    //  NOT WIRED INTO THE GAME. The client still plays sampled footsteps; this is reachable only from
    //  `--footsteps` in the AudioLab. Nothing has to be reverted to keep using samples.
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    public const int SampleRate = 44100;

    /// <summary>
    /// The fraction of a walker's mass that is actually moving downward when the heel lands.
    ///
    /// Not all of it: the body's centre of mass barely falls during a step, and what strikes the
    /// ground is the leg plus whatever share of the trunk the leg is carrying at that instant. The
    /// figure from gait measurement is that the initial impact peak corresponds to about a sixth of
    /// body mass; the rest of the weight arrives slowly, over a tenth of a second, and slow force is
    /// not sound.
    /// </summary>
    public const float EffectiveMassFraction = 0.17f;

    /// <summary>How fast the heel is going down when it lands, relative to how fast the walker is
    /// going forward. A walk plants the foot; a run drops onto it from a height.</summary>
    public const float HeelVelocityRatio = 0.28f;

    /// <summary>
    /// How much of the ground's roughness a sole flows into rather than rattling over.
    ///
    /// Soft conforms, hard bridges. A decade of modulus is worth a long way here, which is why the
    /// spread across real footwear is so wide: skin and soft rubber follow the grit almost exactly,
    /// and a leather board sole sits on the high points of it.
    /// </summary>
    public static float Conformity(MaterialProperties sole)
    {
        float e = MathF.Max(1e-4f, sole.YoungsModulusGPa);
        // 1 at a thousandth of a GPa (skin, foam), falling to nearly nothing by a GPa (board, wood).
        float decades = MathF.Log10(e / 0.001f);
        return Math.Clamp(1f - decades / 3.5f, 0.02f, 1f);
    }

    /// <summary>
    /// Effective contact modulus of two things meeting, Pa — and the softer one wins.
    ///
    /// The series form, 1/E* = 1/E1 + 1/E2, which is why a rubber sole on granite and the same sole
    /// on chipboard have almost the same contact: the rubber is doing all of the deforming in both.
    /// It is also why a bare foot sounds much the same on everything hard, and why the material of
    /// the GROUND matters far more to a hard shoe than to a soft one.
    /// </summary>
    public static float ContactModulus(MaterialProperties sole, MaterialProperties ground)
    {
        float a = MathF.Max(1e5f, sole.YoungsModulusGPa * 1e9f);
        float b = MathF.Max(1e5f, ground.YoungsModulusGPa * 1e9f);
        return 1f / (1f / a + 1f / b);
    }

    /// <summary>
    /// How long the heel is in contact, seconds — the number the whole timbre hangs off.
    ///
    /// Hertz's solution for a curved body striking a flat one: the contact lasts
    /// 2.87 (m² / (R E*² v))^(1/5). The fifth root is why it is so stable — doubling the walker's
    /// weight changes it by fifteen per cent — and why the material is what moves it: E* appears
    /// squared, so four decades of modulus between rubber and leather is a factor of four in contact
    /// time and therefore two octaves of brightness.
    ///
    /// The sound of the impact cannot contain much above 1/τ, because a force that takes τ to happen
    /// has no components faster than that. That is the entire reason a soft shoe cannot click no
    /// matter how hard you stamp.
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

    // ── The floor as a panel ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// How far a floor of this stuff spans between whatever is holding it up, metres.
    ///
    /// The same insight as a car's body panels: what sets the note is the distance between the
    /// STIFFENERS, not the size of the sheet. A wooden floor is boards on joists at about four
    /// hundred millimetres, and that is why it has a note at all; a concrete slab is bearing on the
    /// ground along its whole length and has no free span, which is why it has none.
    /// </summary>
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
    /// How coarse the ground is where the foot actually touches it, in millimetres.
    ///
    /// NOT <see cref="MaterialProperties.Scattering"/>, and the difference is worth being careful
    /// about because the two are so easily confused. Scattering is an ACOUSTIC coefficient: how
    /// much of a sound wave a surface returns diffusely rather than as a mirror, which is a question
    /// about roughness compared to a WAVELENGTH — centimetres to metres. This is roughness compared
    /// to a CONTACT PATCH, which is a fraction of a millimetre. A polished concrete floor is
    /// acoustically specular and still has grit on it; a thick carpet is acoustically the most
    /// scattering thing in a room and is perfectly smooth to walk on.
    ///
    /// Reusing the acoustic number for this is the kind of shortcut that looks like reuse and is
    /// actually a different quantity wearing the same name, so it is written out.
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
        // Soft things do not have asperities in any useful sense — the sole sinks into them and
        // there is nothing to rattle over.
        "carpet" => (0.05f, 0.20f),
        "grass" => (0.30f, 0.35f),
        "dirt" => (0.60f, 0.70f),
        "gravel" => (2.00f, 0.95f),
        _ => (0.25f, 0.50f),
    };

    /// <summary>
    /// How long ONE grain of grit is in contact, seconds.
    ///
    /// The bulk impact cannot contain anything above 1/tau, and for a soft sole tau is twenty
    /// milliseconds — forty hertz, which you feel rather than hear. So if that were the whole story a
    /// trainer on a pavement would be silent, and it plainly is not. What you hear is the sole
    /// crossing the individual grains, and a grain is a far smaller contact.
    ///
    /// The scaling is geometric and needs no new constants. Hertz gives tau proportional to
    /// (m² / (R E*² v))^(1/5), and for two contacts of the same shape at different sizes the mass
    /// goes as the cube of the radius — so tau comes out simply PROPORTIONAL TO SIZE. A grain three
    /// tenths of a millimetre across against a heel thirty-five millimetres across is a contact a
    /// hundred times shorter, which is two decades higher in the spectrum: the same impact, at the
    /// scale of the grit, is a few kilohertz.
    ///
    /// That is the whole of why a footstep has a click on top of a thump, and why the click belongs
    /// to the GROUND's texture while the thump belongs to the SHOE's stiffness.
    /// </summary>
    public static float GrainContactSeconds(float bulkContactSeconds, float heelRadiusM, float grainMm)
    {
        float grainM = MathF.Max(1e-5f, grainMm * 0.001f);
        float ratio = Math.Clamp(grainM / MathF.Max(0.002f, heelRadiusM), 1e-4f, 1f);
        return MathF.Max(2e-5f, bulkContactSeconds * ratio);
    }

    // ── The ground as a heap of loose pieces ────────────────────────────────────────────────────

    /// <summary>
    /// How many loose pieces a foot disturbs, and how big they are.
    ///
    /// A surface that is really a heap — gravel, grit, dry leaves, snow — does not make one sound
    /// when it is trodden on. It makes several dozen, because a foot landing on a hundred stones
    /// pushes them past each other and each pair that slips is its own tiny impact. That is what a
    /// crunch IS, and it is the same argument as a crowd: the texture is the count.
    ///
    /// Zero for anything solid, which is most things.
    /// </summary>
    public static (int Count, float GrainMm) LooseMaterial(string surface) => surface.ToLowerInvariant() switch
    {
        "gravel" => (70, 12f),
        "dirt" => (22, 4f),
        "grass" => (30, 1.5f),    // blades, not stones: many, tiny, and very quiet
        _ => (0, 0f),
    };

    // ── Rendering ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The whole step: heel, roll, toe-off, and whatever the ground does about it.
    ///
    /// Deterministic given the seed, so two renders of the same step are the same buffer and the
    /// engine can cache one — but the seed varies per step, so a corridor is not one sound played
    /// forty times, which is what people mean when they say footsteps sound like a list.
    /// </summary>
    public static float[] Render(Footstep step, int sampleRate = SampleRate)
    {
        AcousticRegistry.EnsureInitialized();
        var ground = AcousticRegistry.GetProperties(step.Surface);
        var sole = AcousticRegistry.GetProperties(step.Shoe.SoleMaterial);
        var rng = new Random(step.Seed * 7919 + 13);

        bool running = step.SpeedMps > 2.6f;
        // How hard the foot arrives. A run drops the body onto the leg from a height; a walk places
        // it. Both scale with how fast the walker is going, because a faster gait has less time to
        // lower the foot.
        float heelV = MathF.Max(0.15f, step.SpeedMps * HeelVelocityRatio * (running ? 1.9f : 1f));
        float mEff = step.BodyMassKg * EffectiveMassFraction + step.Shoe.MassKg;

        // The three contacts, and how far apart they are. A walking foot rolls from heel to toe over
        // about a fifth of a second; a running one lands nearly flat, so the three collapse together
        // and it reads as one heavier impact. That collapse is the sound of somebody breaking into a
        // run, and it falls out rather than being a second set of samples.
        float roll = running ? 0.035f : 0.115f + (float)rng.NextDouble() * 0.03f;
        float toeAt = running ? 0.075f : 0.225f + (float)rng.NextDouble() * 0.04f;

        float total = toeAt + 1.2f;
        int n = (int)(sampleRate * total);
        var buf = new float[n];

        // 1. THE HEEL. The loudest of the three by a long way, and the one that carries the character.
        Contact(buf, 0f, mEff, heelV, step, ground, sole, rng, sampleRate, weight: 1f);

        // 2. THE ROLL. The foot flattening: much less energy, spread over a longer contact, and it is
        // mostly the sole sliding a few millimetres over the roughness rather than striking anything.
        Contact(buf, roll, mEff * 0.35f, heelV * 0.35f, step, ground, sole, rng, sampleRate,
                weight: running ? 0.55f : 0.30f, scuffBias: 2.2f);

        // 3. THE TOE-OFF. Pushing away, quieter still, and brighter than the roll because the contact
        // patch is small and the sole is being peeled off rather than set down.
        Contact(buf, toeAt, mEff * 0.22f, heelV * 0.5f, step, ground, sole, rng, sampleRate,
                weight: running ? 0.45f : 0.22f, scuffBias: 1.4f);

        return buf;
    }

    /// <summary>One contact of the foot with the ground — the four mechanisms, at one instant.</summary>
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

        // What the ground keeps. A slab returns nearly everything as sound; turf swallows it, which
        // is why walking on grass is nearly silent and is not a mixing decision.
        float returned = 1f - Math.Clamp(ground.Absorption, 0f, 0.95f);
        float levelDb = PanelAcoustics.ImpactDb(joules) + 10f * MathF.Log10(MathF.Max(0.02f, returned));
        float amp = weight * MathF.Pow(10f, (levelDb - 94f) / 20f);

        // ── 1. The bulk contact: a half-sine of force, which is a thump with nothing above 1/tau.
        Thump(buf, at, amp, corner, tau, ground, rng, sampleRate);

        // ── 2. The asperities: whatever the sole does not flow into, it rattles over.
        //
        // This is the part of a footstep you can actually HEAR on a hard floor, because the bulk
        // impact above is at forty hertz for a soft shoe and nobody hears forty hertz from a foot.
        // It lives two decades higher because a grain of grit is two decades smaller than a heel.
        float conform = MathF.Max(Conformity(sole), step.Shoe.TreadGrip);
        var (grainMm, coverage) = ContactTexture(step.Surface);
        float grainTau = GrainContactSeconds(tau, step.Shoe.HeelRadiusM, grainMm);
        float scuffLevel = amp * coverage * (1f - conform) * 1.9f * scuffBias;
        if (scuffLevel > 1e-5f)
            Scuff(buf, at, scuffLevel, ImpactCornerHz(grainTau), tau, rng, sampleRate);

        // ── 3. The floor, if it is a panel rather than the ground.
        float span = FreeSpanM(step.Surface), thick = DeckThicknessM(step.Surface);
        if (span > 0f && thick > 0f)
            Ring(buf, at, amp, ground, span, thick, rng, sampleRate);

        // ── 4. Loose pieces, if the ground is a heap of them.
        var (count, stoneMm) = LooseMaterial(step.Surface);
        if (count > 0)
            Crunch(buf, at, amp, count, stoneMm, weight, ground, rng, sampleRate);
    }

    /// <summary>
    /// The bulk impact: a force that takes <paramref name="tau"/> to happen, so a spectrum that
    /// stops at one over it.
    ///
    /// Rendered as noise through a two-pole low-pass at the corner rather than as a literal half-sine,
    /// because a real contact is never clean — the sole is textured, the ground is not flat, and the
    /// foot is not rigid. What matters and is preserved is that the energy is bounded above by 1/tau
    /// and that it is over in about tau.
    /// </summary>
    private static void Thump(float[] buf, int at, float amp, float corner, float tau,
                              MaterialProperties ground, Random rng, int sampleRate)
    {
        // A soft ground lets the thump breathe longer than the contact itself: the mass of earth or
        // turf under the foot moves with it and takes a moment to stop.
        float decay = tau * (2.5f + 6f * Math.Clamp(ground.Absorption, 0f, 1f));
        int len = Math.Min(buf.Length - at, (int)(sampleRate * decay * 6f) + 8);
        if (len <= 0) return;

        float k = MathF.Exp(-2f * MathF.PI * corner / sampleRate);
        float lp1 = 0f, lp2 = 0f;
        for (int i = 0; i < len; i++)
        {
            float t = i / (float)sampleRate;
            // Rises over the first tenth of the contact and falls exponentially: a foot does not
            // arrive instantaneously, and the rise is what keeps it from sounding like a click.
            float env = (1f - MathF.Exp(-t / (0.12f * tau))) * MathF.Exp(-t / decay);
            float x = (float)(rng.NextDouble() * 2.0 - 1.0) * env;
            lp1 += (x - lp1) * (1f - k);
            lp2 += (lp1 - lp2) * (1f - k);
            buf[at + i] += lp2 * amp * 3.2f;
        }
    }

    /// <summary>
    /// The sole crossing the roughness: a burst of very short, very small impacts through the
    /// contact.
    ///
    /// High-passed at the contact corner on purpose — this is the part of a footstep that lives ABOVE
    /// what the bulk impact can reach, and it is why a hard shoe on a pavement has a click at all
    /// when the impact itself has nothing above a couple of hundred hertz.
    /// </summary>
    private static void Scuff(float[] buf, int at, float amp, float grainCornerHz, float tau,
                              Random rng, int sampleRate)
    {
        // It lasts as long as the sole is moving across the ground, which is longer than the bulk
        // impact: the foot is still travelling forward a few millimetres as it settles.
        float dur = tau * 2.2f;
        int len = Math.Min(buf.Length - at, (int)(sampleRate * dur * 3.5f) + 8);
        if (len <= 0) return;

        // Noise rolled off above the grain's own corner and below a decade under it. The band is
        // wide on purpose — grains are not all one size, and a single resonance would be a whistle.
        float hi = Math.Clamp(grainCornerHz, 200f, sampleRate * 0.45f);
        float lo = hi * 0.12f;
        float kHi = MathF.Exp(-2f * MathF.PI * hi / sampleRate);
        float kLo = MathF.Exp(-2f * MathF.PI * lo / sampleRate);
        float lpHi = 0f, lpLo = 0f;

        for (int i = 0; i < len; i++)
        {
            float t = i / (float)sampleRate;
            // Not a smooth envelope: the sole crosses grains at irregular intervals, and that
            // irregularity is the difference between grit and hiss. Two random walks, one fast and
            // one slow, standing in for how many grains are in contact at once.
            float env = MathF.Exp(-t / dur) * (0.35f + 0.65f * (float)rng.NextDouble());
            float x = (float)(rng.NextDouble() * 2.0 - 1.0) * env;
            lpHi += (x - lpHi) * (1f - kHi);          // everything below the grain corner
            lpLo += (lpHi - lpLo) * (1f - kLo);       // ...minus everything below the band
            buf[at + i] += (lpHi - lpLo) * amp * 2.6f;
        }
    }

    /// <summary>
    /// The floor answering at its own modes — what "a hollow wooden floor" means.
    ///
    /// The same <see cref="PanelAcoustics"/> that rings a door and a car's wing: a floorboard deck is
    /// a plate spanning its joists, and the note is set by that span and the board's thickness, not
    /// by the size of the room. It is the one part of a footstep that is genuinely a TONE, and it is
    /// why a wooden floor is recognisable through a wall.
    /// </summary>
    private static void Ring(float[] buf, int at, float amp, MaterialProperties material,
                             float span, float thickness, Random rng, int sampleRate)
    {
        var modes = PanelAcoustics.Modes(material, span, span * 2.4f, thickness, maxHz: 3000f, order: 5);
        if (modes.Count == 0) return;

        // A floor is walked on, screwed down and sitting on a joist: it is far more damped than a
        // panel hanging in the air, and a floor that rang like a drum would be a fault in the floor.
        const float mounting = 0.09f;
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
            // Struck from one place, so the modes are not all driven equally; the weight from the
            // modal series already carries that and the jitter is where on the board the foot landed.
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
    /// A heap of loose pieces being pushed past each other — the crunch.
    ///
    /// Tens of tiny impacts, scattered through the moment the foot is landing, each one a stone
    /// meeting another stone. Their timing is clustered rather than even: a foot arriving displaces
    /// most of what it is going to displace in the first thirty milliseconds and then a few
    /// stragglers settle, which is what makes gravel crackle rather than hiss.
    ///
    /// The note of one piece comes from its size the same way everything else here does — a small
    /// stone is stiff for its mass and rings high, a big one lower — so grit and ballast are the same
    /// model with a different number, and so is a pile of dry leaves.
    /// </summary>
    private static void Crunch(float[] buf, int at, float amp, int count, float grainMm, float weight,
                               MaterialProperties ground, Random rng, int sampleRate)
    {
        int pieces = Math.Max(1, (int)(count * Math.Clamp(weight, 0.05f, 1f)));
        // A stone's ring: a lump of rock a centimetre across is a few kilohertz, and it goes as
        // 1/size, which is why grit hisses and ballast rattles.
        float baseHz = Math.Clamp(38000f / MathF.Max(0.5f, grainMm), 200f, 12000f);

        for (int p = 0; p < pieces; p++)
        {
            // Clustered in time: most of them in the first few tens of milliseconds.
            double u = rng.NextDouble();
            float when = (float)(-0.028 * Math.Log(1.0 - u * 0.985));
            int start = at + (int)(when * sampleRate);
            if (start >= buf.Length - 4) continue;

            // Most of the pieces that move barely move; a few take a real knock. A log-normal spread
            // is what makes a crunch sound like stones and not like static.
            float energy = MathF.Exp(-2.2f * (float)rng.NextDouble() * (float)rng.NextDouble() * 3f);
            float hz = baseHz * (0.45f + 1.6f * (float)rng.NextDouble());
            float ring = 0.004f + 0.02f * (float)rng.NextDouble();
            int len = Math.Min(buf.Length - start, (int)(sampleRate * ring * 3f) + 4);

            float w = 2f * MathF.PI * hz / sampleRate;
            float decay = MathF.Exp(-1f / MathF.Max(1f, ring * sampleRate));
            float phase = (float)(rng.NextDouble() * Math.PI * 2.0);
            float g = amp * energy * 0.42f;
            float env = 1f;
            for (int i = 0; i < len; i++)
            {
                // Part tone, part noise: a stone is not a bell, and the noisier it is the smaller and
                // more irregular it is.
                float tone = MathF.Sin(w * i + phase);
                float noise = (float)(rng.NextDouble() * 2.0 - 1.0);
                buf[start + i] += (tone * 0.45f + noise * 0.55f) * env * g;
                env *= decay;
            }
        }
    }

    // ── Identity, so a step can be named, cached and sent ───────────────────────────────────────

    /// <summary>
    /// The key a <see cref="TransientSound.SynthKey"/> carries, so the client can render this exact
    /// step without anything being shipped.
    ///
    /// Quantised, because two steps a gram apart are not two sounds: the buffer is cached by this
    /// string, and a key that carried full precision would render one buffer per step and cache
    /// nothing. The seed is deliberately coarse — a handful of variations per surface, which is what
    /// stops a corridor being one sound repeated without becoming a render per footfall.
    /// </summary>
    public static string Key(Footstep step)
        => string.Format(CultureInfo.InvariantCulture, "footstep:{0}:{1}:{2}:{3}:{4}",
            step.Surface.ToLowerInvariant(), step.Shoe.Name.Replace(' ', '_'),
            (int)MathF.Round(step.BodyMassKg / 10f) * 10,
            (int)MathF.Round(step.SpeedMps * 2f) / 2f * 2,
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

    /// <summary>
    /// What this step measures at one metre, dB SPL — the number the emitter is placed at.
    ///
    /// Measured from the render rather than declared, for the same reason an engine's is: a level
    /// that is asserted and a level that is produced drift apart the moment anything in the model
    /// changes, and then the whole mix is wrong in a way nobody can point at.
    /// </summary>
    public static float MeasuredLevelDb(Footstep step, int sampleRate = SampleRate)
    {
        var buf = Render(step, sampleRate);
        float peak = 0f;
        foreach (float x in buf) { float a = MathF.Abs(x); if (a > peak) peak = a; }
        // The render is in the same units PanelAcoustics.ImpactDb works in, referred to 94 dB = 1.0.
        return 94f + 20f * MathF.Log10(MathF.Max(1e-6f, peak));
    }
}

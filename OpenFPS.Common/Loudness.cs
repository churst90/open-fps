using System;

namespace OpenFPS.Common;

/// <summary>
/// How loud a thing is at its source, in decibels, and what that becomes in the mix.
///
/// The engine did not have this concept, and its absence was audible. Every asset ships normalised to
/// the same peak, and the only per-sound control was a 0-1 <c>Volume</c> multiplier that can make a
/// sound quieter but never louder than anything else. Distance attenuation is correct — 1/r on the
/// Steam Audio path — but it starts every source from the same place. The arithmetic that falls out:
/// a gunshot ten metres away rendered FOURTEEN DECIBELS QUIETER than a footstep at one metre. In air
/// those two differ by about eighty-five decibels in the other direction.
///
/// So a sound now says how loud it is where it is made, and the engine works out the rest.
///
/// ── The honest part ────────────────────────────────────────────────────────────────────────────
///
/// The real dynamic range here is about 115 dB, from a muzzle blast at 165 down to a quiet ambience
/// around 48. Sixteen-bit audio has 96 dB and a listener in a real room has rather less; render it
/// literally and every footstep in the game is below the noise floor of the headphones. So the range
/// is COMPRESSED — <see cref="DynamicRangeCompression"/> — and that is a deliberate departure from
/// physics, not an accident of tuning. What compression preserves is the ORDER and the ROUGH SPACING:
/// a gunshot still dwarfs a footstep, a shotgun still outweighs a pistol, and a thing twice as far
/// away is still half as loud. What it gives up is the literal ratio, because the literal ratio is
/// unlistenable.
///
/// The numbers below are real measured source levels where such things are published (firearms are
/// well documented, because hearing damage is). They are a starting point for tuning by ear, not a
/// result of it — see todo.md.
/// </summary>
public static class Loudness
{
    // ── Source levels, dB SPL at one metre ──────────────────────────────────────────────────────
    //
    // Firearms are quoted at the shooter's position, which is where they are measured and where the
    // hearing-protection figures come from.

    /// <summary>5.56x45 from a 16-inch barrel. The loudest thing in the game and the reference.</summary>
    public const float Rifle556Db = 165f;
    /// <summary>7.62x39. Slightly quieter than the 5.56 but with far more low frequency, which is why
    /// it carries further through walls even though it measures lower.</summary>
    public const float Rifle762Db = 159f;
    public const float Pistol9mmDb = 160f;
    public const float Pistol45Db = 157f;
    public const float Shotgun12GaugeDb = 160f;

    /// <summary>The N-wave of a round passing at a metre. Enormous, brief, and almost all of it high
    /// frequency — which is why it localizes so well and why it does not carry.</summary>
    public const float SupersonicCrackDb = 150f;

    /// <summary>A round striking concrete a few metres away.</summary>
    public const float BulletImpactDb = 120f;
    /// <summary>A pane failing. Loud, but nothing like a gunshot.</summary>
    public const float GlassShatterDb = 105f;
    /// <summary>Fragments landing.</summary>
    public const float GlassFragmentDb = 72f;
    /// <summary>A case bouncing on concrete. Quiet, close, and very informative.</summary>
    public const float CasingDb = 75f;
    /// <summary>Working a bolt, a magazine, a selector. Quiet enough that hearing it means someone is
    /// CLOSE, which is most of why it matters.</summary>
    public const float WeaponHandlingDb = 78f;
    public const float SpeechDb = 60f;
    public const float FootstepDb = 55f;
    public const float AmbienceBedDb = 48f;

    /// <summary>
    /// The sound pressure level that renders at full scale, dB SPL.
    ///
    /// Not the level of the loudest SOURCE — the level at the LISTENER that uses all the headroom.
    /// 130 dB is about where sound stops being loud and starts being pain, which is a defensible place
    /// to put the top of a mix. A rifle at thirty metres arrives at 129 dB, so it renders at full
    /// scale; the same rifle at ten metres arrives at 139 and is simply clipped, which is what happens
    /// to your ears as well.
    ///
    /// Setting this to the source level instead — 165, the rifle's level at one metre — was the
    /// mistake in the first version, and it is worth naming because it looks so reasonable. It makes
    /// a gunshot full-scale only when you are standing AT the muzzle, and at any real distance the
    /// inverse-square law has already taken 30 dB off it before the mix sees it. Guns are very loud;
    /// they have to be loud at the ranges people actually shoot from.
    /// </summary>
    // Lowered from 130. At 130 the only thing that ever reached full scale was gunfire: a door slam
    // at 88 dB rendered at -28 dBFS, a pane of glass across a street at -41, and three separate
    // listening tests in a row reported doors, glass and tyres as "weak", "quiet" and "dull" while
    // every measurement said the physics was right. It was — the ANCHOR was wrong. Everyday sounds
    // are 60 to 95 dB and they are what the game is mostly made of, so that is the range the mix
    // should spend itself on. Gunfire now runs into the ceiling and clips, which is what a gunshot
    // does to an ear and to a microphone.
    public const float RenderCeilingDb = 112f;

    /// <summary>
    /// How much of the real decibel difference survives into the mix. 1.0 is literal physics and
    /// unlistenable; 0 would make everything the same loudness. At 0.45 the 110 dB between a rifle and
    /// an ambience bed becomes about 50 dB of rendered range, which fits in a mix and still leaves a
    /// gunshot startling next to a footstep.
    ///
    /// This compresses SOURCE levels only. Distance is deliberately left literal — the engine's 1/r —
    /// because compressing that would flatten the range cues the whole game is built on.
    /// </summary>
    public const float DynamicRangeCompression = 0.45f;

    /// <summary>Below this the engine should not bother playing the voice at all.</summary>
    public const float SilenceGain = 0.0002f;   // about -74 dBFS

    /// <summary>Largest reference distance we will hand out. Past this a source stops being a point
    /// and the 1/r model stops meaning much anyway.</summary>
    public const float MaxReferenceDistance = 40f;
    // Raised from half a metre. The reference distance is where a sound stops getting louder as you
    // approach, and beyond it everything falls away as 1/d — so a floor of 0.5 m meant a quiet source
    // was already losing six decibels by the time you were a metre from it. Nothing in this game is
    // heard from closer than about a metre anyway; a door at arm's length was being attenuated as
    // though the listener's ear were pressed to the latch.
    public const float MinReferenceDistance = 1.2f;

    /// <summary>
    /// Where to put a sound of a given source level: the gain it plays at, and the reference distance
    /// inside which it does not get any louder.
    ///
    /// These two have to be decided together, and that is the part the first version got wrong by
    /// deciding them separately. The engine attenuates by <c>MinDistance / distance</c>, so the
    /// reference distance is not a detail of the rolloff — it is what decides how loud the sound still
    /// is at the range it is actually heard from. With a fixed 2 m reference a muzzle blast is down to
    /// a fifteenth of its level by thirty metres, and no amount of source gain rescues it because the
    /// gain is already clamped at full scale.
    ///
    /// So the reference distance is derived: it is the distance at which this source's SPL falls to
    /// <see cref="RenderCeilingDb"/>. A 159 dB rifle reaches 130 dB at about twenty-eight metres, so
    /// it plays at full scale out to twenty-eight metres and only then begins to fall. A 55 dB
    /// footstep reaches 130 dB nowhere at all, so it clamps to half a metre and takes its quietness
    /// from the gain instead.
    /// </summary>
    public static (float Gain, float ReferenceDistance) Place(float sourceLevelDb)
    {
        float ideal = MathF.Pow(10f, (sourceLevelDb - RenderCeilingDb) / 20f);
        float reference = Math.Clamp(ideal, MinReferenceDistance, MaxReferenceDistance);

        // What is left over after the clamp, compressed. For a loud source the clamp does nothing and
        // this comes out at unity; for a quiet one it is the whole of its quietness.
        float atReference = sourceLevelDb - 20f * MathF.Log10(MathF.Max(0.01f, reference));
        float renderedDb = (atReference - RenderCeilingDb) * DynamicRangeCompression;
        return (MathF.Min(1f, MathF.Pow(10f, renderedDb / 20f)), reference);
    }

    /// <summary>
    /// The same placement, for a source that is not a point.
    ///
    /// A crowd, a waterfall or a motorway has a SIZE, and inside it the inverse law does not hold:
    /// stepping a metre towards one clapper steps you a metre away from another, so the level is flat
    /// across the patch and only starts falling once the whole of it is in front of you. So the
    /// reference distance is the thing's own radius.
    ///
    /// The gain comes down to pay for it, and that is the half that is easy to get wrong. Beyond the
    /// patch a distributed source and a point source of the same total power sound IDENTICAL — that is
    /// what makes the point model usable at all — so widening the reference without touching the gain
    /// would not model a crowd, it would just make one louder than physics allows at every distance
    /// that matters. What the inverse law carries is the PRODUCT of gain and reference distance, so
    /// that product is held and only the near field changes.
    /// </summary>
    public static (float Gain, float ReferenceDistance) Place(float sourceLevelDb, float extentMetres)
        => Widen(Place(sourceLevelDb), extentMetres);

    /// <summary>
    /// The same widening, for a caller that has a gain and a reference distance but no source level.
    ///
    /// An authored emitter — a fountain, a ventilation grille, a waterfall — carries a volume rather
    /// than a level in decibels, and it is just as much a thing with a SIZE. The rule is the one that
    /// matters and the one that is easy to get wrong in the other direction: what the inverse law
    /// carries is the PRODUCT of gain and reference distance, so widening the reference without
    /// paying the gain down does not model an extended source, it makes a louder one at every
    /// distance that matters.
    ///
    /// That mistake was in the engine: a vehicle's reference was widened to three metres with nothing
    /// paid back, which handed every quiet vehicle up to eight decibels it had not earned.
    /// </summary>
    public static (float Gain, float ReferenceDistance) Widen((float Gain, float ReferenceDistance) placed, float extentMetres)
        => extentMetres > placed.ReferenceDistance
         ? (placed.Gain * (placed.ReferenceDistance / extentMetres), extentMetres)
         : placed;

    /// <summary>The same, spelled out for a caller holding two loose floats.</summary>
    public static (float Gain, float ReferenceDistance) Widen(float gain, float referenceDistance, float extentMetres)
        => Widen((gain, referenceDistance), extentMetres);

    /// <summary>Just the gain, for a caller that is setting the reference distance itself.</summary>
    public static float GainFor(float sourceLevelDb) => Place(sourceLevelDb).Gain;

    /// <summary>
    /// What distance does to a voice: the law the mixer actually applies, in one place.
    ///
    /// A natural inverse-distance rolloff from the reference distance out — a source is fully itself
    /// up close and falls away as 1/d — and then a fade over the last quarter of its range so it
    /// reaches nothing at the range rather than stopping on a step. That fade is the part worth
    /// knowing about: a range set shorter than a sound can actually be heard does not save anything,
    /// it silences the sound early. See <see cref="AudibleRange"/>, which is what a range should be.
    ///
    /// It lives here rather than inside the FMOD provider so that a test can ask what the mixer will
    /// do without a sound card, which is the only way the balance between two sources can be checked
    /// at all.
    /// </summary>
    public static float RenderedGain(float gain, float referenceDistance, float range, float distance)
    {
        float min = MathF.Max(0.1f, referenceDistance);
        float span = MathF.Max(0.01f, range - min);
        float inv = Math.Clamp(min / MathF.Max(distance, min), 0f, 1f);
        float edgeFade = Math.Clamp((range - distance) / (0.25f * span), 0f, 1f);
        return gain * inv * edgeFade;
    }

    /// <summary>The muzzle blast level for a weapon, from its cartridge. Falls back to the 7.62
    /// figure — audible and plausible — rather than to silence or to the ceiling.</summary>
    public static float MuzzleBlastDb(WeaponDefinition w) => w.Cartridge switch
    {
        "5.56x45mm" => Rifle556Db,
        "7.62x39mm" => Rifle762Db,
        "9x19mm" => Pistol9mmDb,
        ".45 ACP" => Pistol45Db,
        "12 gauge 00 buck" => Shotgun12GaugeDb,
        _ => Rifle762Db,
    };

    /// <summary>
    /// The distance at which a source of this level drops to the quietest thing worth rendering —
    /// what <c>SpatialEmitter.Range</c> should be. A gunshot carries across a map and a footstep does
    /// not, and that difference should fall out of how loud each one is rather than being a separate
    /// decision made by feel.
    /// </summary>
    public static float AudibleRange(float sourceLevelDb)
    {
        var (gain, reference) = Place(sourceLevelDb);
        if (gain <= SilenceGain) return reference;
        return MathF.Min(3000f, reference * gain / SilenceGain);
    }

    /// <summary>What a source of this level actually sounds like at a distance, in dB SPL — the
    /// physical quantity, before any of the mix's compression. For reporting and for tests.</summary>
    public static float SplAt(float sourceLevelDb, float distanceMetres) =>
        sourceLevelDb - 20f * MathF.Log10(MathF.Max(1f, distanceMetres));
}

using System;
using System.Collections.Generic;
using System.Numerics;

namespace OpenFPS.Common;

/// <summary>
/// Two things meeting, and what that sounds like.
///
/// Deliberately knows nothing about cars, doors, balls or bullets. It is handed two materials, a size,
/// a closing speed and two masses, and it answers in the same four characters everything else in the
/// game answers in. A car meeting a wall, a ball meeting a floor, a crate dropped off a lorry and a
/// round striking concrete are one function called four times, which is the whole reason for having
/// it rather than a collision sound per kind of thing.
///
/// What a listener actually gets out of an impact, and therefore what this has to get right:
///
///   HOW HARD, from the energy — and the energy uses the REDUCED mass, because a lorry hitting a
///   drink can and a drink can hitting a lorry are the same collision and both are governed by the
///   lighter of the two.
///
///   WHAT OF, from both materials. The blow itself takes the character of the SOFTER of the pair
///   (hitting a carpeted wall is a dull thump whatever you hit it with), while the ring afterwards
///   belongs to whichever of them actually rings — which is why a hammer on a bell is a bell.
///
///   HOW BIG, from the size of what was struck. The same blow on a wing mirror and on a garage door
///   are different sounds because the panels are different sizes, and neither needed authoring.
/// </summary>
public static class ImpactAcoustics
{
    /// <summary>Below this a collision is a scuff, not an event. A car creeping into a kerb at
    /// walking pace should not announce itself like a crash.</summary>
    public const float MinimumSpeed = 0.4f;

    /// <summary>
    /// The sound of one thing hitting another.
    ///
    /// <paramref name="struckWidth"/> and <paramref name="struckHeight"/> are the face that was hit,
    /// which is what decides the note; <paramref name="struckThickness"/> is how deep it is.
    /// </summary>
    public static List<TransientSound> Between(MaterialProperties hitter, MaterialProperties struck,
                                               Vector3 where, float closingSpeed,
                                               float hitterMassKg, float struckMassKg,
                                               float struckWidth, float struckHeight, float struckThickness,
                                               bool struckIsFixed = true)
    {
        var sounds = new List<TransientSound>(2);
        if (closingSpeed < MinimumSpeed) return sounds;

        float joules = PanelAcoustics.ImpactJoules(hitterMassKg, struckMassKg, closingSpeed);
        float db = PanelAcoustics.ImpactDb(joules);

        // The blow takes the character of the softer of the two. Hitting a carpeted wall with a
        // steel bar is a dull thump; hitting a steel bar with a carpet is the same dull thump.
        var softer = hitter.YoungsModulusGPa <= struck.YoungsModulusGPa ? hitter : struck;
        float softness = 1f / (1f + MathF.Max(0f, softer.YoungsModulusGPa));
        sounds.Add(new TransientSound
        {
            Character = SoundCharacter.Knock,
            DelaySeconds = 0f,
            Position = where,
            LevelDb = db,
            // A soft contact spreads the blow over more time, which is the same as saying it is lower.
            Hz = Math.Clamp(1400f * (1f - softness) + 70f, 60f, 2500f),
            DecaySeconds = Math.Clamp(0.02f + softness * 0.25f, 0.02f, 0.3f),
            Noisiness = 0.85f,
        });

        // ...and afterwards, whichever of the two actually rings does so. A hammer on a bell is a
        // bell, not a hammer.
        var ringer = hitter.LossFactor <= struck.LossFactor ? hitter : struck;
        bool ringerIsTheStruck = !ReferenceEquals(ringer, hitter) || hitter.LossFactor == struck.LossFactor;
        float hz = PanelAcoustics.RingHz(ringerIsTheStruck ? struck : hitter,
                                         struckWidth, struckHeight, struckThickness);
        if (hz > 0f)
        {
            float mounting = struckIsFixed ? PanelAcoustics.MountedLoss : 0f;
            var ringerMaterial = ringerIsTheStruck ? struck : hitter;
            float seconds = PanelAcoustics.RingSeconds(ringerMaterial, hz, mounting);
            // A carpet has modes too, and very obviously does not ring. What tells a bell from a bag
            // of sand is not whether it has a note but whether the note outlasts the blow.
            if (PanelAcoustics.RingsAudibly(ringerMaterial, hz, mounting))
                sounds.Add(new TransientSound
                {
                    Character = SoundCharacter.Ring,
                    DelaySeconds = 0.003f,
                    Position = where,
                    LevelDb = db - 5f,
                    Hz = hz,
                    DecaySeconds = seconds,
                    Noisiness = 0.12f,
                });
        }
        return sounds;
    }
}

/// <summary>
/// Turning what the glass model already decided into something that can be heard.
///
/// <see cref="GlassBreak"/> has been written and tested for a while and has never made a sound,
/// because until the world audio channel existed there was nothing for it to speak through. This is
/// the whole of the connection, and it is a mapping rather than a model: the physics — which events
/// happen, when, where, how loud — was decided long ago and none of it is second-guessed here.
/// </summary>
public static class GlassSound
{
    /// <summary>
    /// What each glass event IS, physically.
    ///
    /// The shatter is a hiss rather than a knock, and that is the interesting one: a pane letting go
    /// is not one impact, it is some thousands of tiny ones inside a tenth of a second, and a crowd
    /// of impacts that dense stops being heard as impacts at all. A single shard landing IS one
    /// impact, so it is a knock — which is why a pane coming down sounds nothing like the pieces of
    /// it arriving, and why the gap between the two tells you which floor it fell from.
    /// </summary>
    /// <summary>A house window: 1.2 by 1.6 metres of 6 mm glass. Everything is measured against it.</summary>
    private const float ReferencePaneVolume = 1.2f * 1.6f * 0.006f;

    public static List<TransientSound> From(IEnumerable<GlassEvent> events, GlassType type, Vector2 paneSize,
                                            float thicknessMetres = 0.006f)
    {
        var material = AcousticRegistry.GetProperties("Glass");
        var sounds = new List<TransientSound>();

        // HOW MUCH GLASS THERE WAS decides how loud it is, and it has to: a shop front going in is
        // not a wing mirror going in. The energy a pane releases is the elastic strain energy stored
        // in it, which scales with its VOLUME — so a pane twice the area and twice the thickness is
        // four times the glass and six decibels more of it. A car window comes out about nine
        // decibels under a house window and a shop front about eight over, which is roughly the
        // spread anybody would expect.
        float thickness = Math.Clamp(thicknessMetres, 0.002f, 0.030f);
        float volume = MathF.Max(1e-5f, paneSize.X * paneSize.Y * thickness);
        float sizeDb = 10f * MathF.Log10(volume / ReferencePaneVolume);

        // ...and how THICK it was decides the note. Thick glass breaks into bigger pieces, and a
        // bigger piece of a stiff plate rings lower. Six-millimetre glass is the reference; four
        // gives a brighter tinkle and ten a duller one.
        float shardPitch = MathF.Sqrt(0.006f / thickness);

        // How BIG it was has to reach the character and not only the level, or a shop front is just a
        // loud teacup — which is what the first listening test heard: "sounded like a cup breaking".
        // A crack crosses a bigger sheet over a longer time and releases bigger fragments, so the
        // event lasts longer and sits lower. Area rather than volume, because this is about how far
        // the failure has to travel.
        float area = MathF.Max(0.01f, paneSize.X * paneSize.Y);
        float span = MathF.Sqrt(area / (1.2f * 1.6f));
        float sizePitch = Math.Clamp(1f / MathF.Sqrt(span), 0.45f, 2.2f);
        float sizeLength = Math.Clamp(span, 0.5f, 2.5f);

        foreach (var e in events)
        {
            // A pane letting go is ninety-odd decibels at a metre — it is one of the loudest things a
            // building does. Sixty was the level of a conversation, which is why it could not be
            // heard at all from across a street.
            float reference = e.Kind switch
            {
                GlassEventKind.Shatter => 96f,
                GlassEventKind.Puncture => 82f,
                GlassEventKind.Shard => 90f,
                _ => 92f,       // a piece arriving on pavement — the half that tells you the height,
                                // and the half a listening test could not hear at all
            };
            float baseDb = reference + sizeDb + 20f * MathF.Log10(MathF.Max(0.01f, e.Volume));
            switch (e.Kind)
            {
                case GlassEventKind.Puncture:
                    sounds.Add(new TransientSound
                    {
                        Character = SoundCharacter.Knock, DelaySeconds = e.DelaySeconds, Position = e.Position,
                        LevelDb = baseDb, Hz = 3200f * e.Pitch, DecaySeconds = 0.03f, Noisiness = 0.7f,
                    });
                    break;

                case GlassEventKind.Shatter:
                    // Thousands of small releases at once: dense, bright, and over quickly. Laminated
                    // glass is the exception the model already knows about — it keeps the pane, so
                    // what little it makes is duller and shorter.
                    sounds.Add(new TransientSound
                    {
                        Character = SoundCharacter.Hiss, DelaySeconds = e.DelaySeconds, Position = e.Position,
                        LevelDb = baseDb,
                        // A jar going is brighter than a door and duller than the tinkle that follows
                        // it: the measured break carries a sixth of its energy in the 0.8-2.5 kHz
                        // band where the shards carry almost none, and is down 20 dB inside 110 ms.
                        // It has a body, briefly, because until the instant it fails it is still a pane.
                        Hz = (type == GlassType.Laminated ? 900f : 2800f) * sizePitch,
                        DecaySeconds = (type == GlassType.Laminated ? 0.12f : 0.28f) * sizeLength,
                        Noisiness = 1f,
                    });
                    // A pane big enough to have a note gives one up as it goes — but only annealed
                    // glass, which fails from a crack outward and is briefly still a pane. Tempered
                    // glass is gone all at once and never rings.
                    if (type == GlassType.Annealed)
                    {
                        float hz = PanelAcoustics.RingHz(material, paneSize.X, paneSize.Y, thickness);
                        if (hz > 0f)
                            sounds.Add(new TransientSound
                            {
                                Character = SoundCharacter.Ring, DelaySeconds = e.DelaySeconds, Position = e.Position,
                                LevelDb = baseDb - 10f, Hz = hz,
                                DecaySeconds = PanelAcoustics.RingSeconds(material, hz) * 0.3f,
                                Noisiness = 0.3f,
                            });
                    }
                    break;

                case GlassEventKind.Shard:
                    // A piece of glass in the air is a TINKLE — bright and pitched, because a fragment
                    // is a small stiff plate and rings like one. Rendering it as a broadband hiss made
                    // a dozen of them into one wash of white noise, which a shower of glass
                    // emphatically is not.
                    //
                    // The numbers are measured rather than guessed, from two recordings in inbox/.
                    // Glass is pure treble: a continuous tinkle texture puts nine tenths of one per
                    // cent of its energy below 800 Hz, and shards dropped on cement put THIRTY-EIGHT
                    // PER CENT ABOVE 4 kHz with only two per cent below 1.5 kHz — and each piece is
                    // done in about 66 milliseconds.
                    //
                    // At 4.2 kHz and 210 ms this was an octave low and three times too long, which
                    // is not a shard of glass, it is a bottle cap — which is what the listening test
                    // called it.
                    sounds.Add(new TransientSound
                    {
                        Character = SoundCharacter.Ring, DelaySeconds = e.DelaySeconds, Position = e.Position,
                        LevelDb = baseDb, Hz = 6400f * e.Pitch * shardPitch * sizePitch,
                        DecaySeconds = 0.07f, Noisiness = 0.18f,
                    });
                    break;

                default:    // Landing: one piece, one impact, at the foot of the wall.
                    sounds.Add(new TransientSound
                    {
                        Character = SoundCharacter.Knock, DelaySeconds = e.DelaySeconds, Position = e.Position,
                        LevelDb = baseDb, Hz = 5200f * e.Pitch, DecaySeconds = 0.06f, Noisiness = 0.3f,
                    });
                    break;
            }
        }
        return sounds;
    }
}

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
    public static List<TransientSound> From(IEnumerable<GlassEvent> events, GlassType type, Vector2 paneSize)
    {
        var material = AcousticRegistry.GetProperties("Glass");
        var sounds = new List<TransientSound>();

        foreach (var e in events)
        {
            // A pane letting go is ninety-odd decibels at a metre — it is one of the loudest things a
            // building does. Sixty was the level of a conversation, which is why it could not be
            // heard at all from across a street.
            float reference = e.Kind switch
            {
                GlassEventKind.Shatter => 96f,
                GlassEventKind.Puncture => 82f,
                GlassEventKind.Shard => 80f,
                _ => 84f,       // a piece arriving on pavement
            };
            float baseDb = reference + 20f * MathF.Log10(MathF.Max(0.01f, e.Volume));
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
                        Hz = type == GlassType.Laminated ? 900f : 4200f,
                        DecaySeconds = type == GlassType.Laminated ? 0.12f : 0.45f,
                        Noisiness = 1f,
                    });
                    // A pane big enough to have a note gives one up as it goes — but only annealed
                    // glass, which fails from a crack outward and is briefly still a pane. Tempered
                    // glass is gone all at once and never rings.
                    if (type == GlassType.Annealed)
                    {
                        float hz = PanelAcoustics.RingHz(material, paneSize.X, paneSize.Y, 0.004f);
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
                    // A piece of glass in the air is a TINKLE — short, bright, and pitched, because a
                    // fragment is a small stiff plate and rings like one. Rendering it as a long
                    // broadband hiss made a dozen of them into one wash of white noise, which is
                    // what a shower of glass is emphatically not.
                    sounds.Add(new TransientSound
                    {
                        Character = SoundCharacter.Ring, DelaySeconds = e.DelaySeconds, Position = e.Position,
                        LevelDb = baseDb, Hz = 3400f * e.Pitch, DecaySeconds = 0.12f, Noisiness = 0.35f,
                    });
                    break;

                default:    // Landing: one piece, one impact, at the foot of the wall.
                    sounds.Add(new TransientSound
                    {
                        Character = SoundCharacter.Knock, DelaySeconds = e.DelaySeconds, Position = e.Position,
                        LevelDb = baseDb, Hz = 2600f * e.Pitch, DecaySeconds = 0.09f, Noisiness = 0.6f,
                    });
                    break;
            }
        }
        return sounds;
    }
}

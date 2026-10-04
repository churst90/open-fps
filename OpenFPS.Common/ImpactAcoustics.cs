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
///   is the struck panel's — which is why a hammer on a bell is a bell.
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

        // ...and HOW BIG the things are decides the pitch, which this had no notion of at all: a
        // latch bolt and a fifteen-hundred-kilo car were being given the same frequency law and
        // coming out within an octave of each other. They are nothing like each other.
        //
        // Contact time grows with mass — two heavy things stay in contact for tens of milliseconds
        // while two light ones are done in microseconds — and a longer contact is a lower sound. A
        // car meeting a wall came out at 1.4 kHz for 28 ms, which is a tap; it should be a low crunch
        // and now is, because the mass is in the formula. One kilogram is the neutral point.
        float reduced = PanelAcoustics.ImpactJoules(hitterMassKg, struckMassKg, 1f) * 2f;
        float sizeScale = 1f / MathF.Pow(MathF.Max(0.02f, reduced), 0.28f);
        float lengthScale = MathF.Pow(MathF.Max(0.02f, reduced), 0.18f);

        sounds.Add(new TransientSound
        {
            Character = SoundCharacter.Knock,
            DelaySeconds = 0f,
            Position = where,
            LevelDb = db,
            // A soft contact spreads the blow over more time, which is the same as saying it is lower.
            Hz = Math.Clamp((1400f * (1f - softness) + 70f) * sizeScale, 45f, 3000f),
            DecaySeconds = Math.Clamp((0.02f + softness * 0.25f) * lengthScale, 0.02f, 0.6f),
            Noisiness = 0.85f,
        });

        // ...and afterwards the struck panel rings, at the note its own size gives it: a hammer on
        // a bell is a bell. Only the struck body's dimensions are known here, so a caller that
        // wants the hitter to ring passes it as the struck one.
        float hz = PanelAcoustics.RingHz(struck, struckWidth, struckHeight, struckThickness);
        if (hz > 0f)
        {
            float mounting = struckIsFixed ? PanelAcoustics.MountedLoss : 0f;
            var ringerMaterial = struck;
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
/// Turning what the glass model decided into something that can be heard.
///
/// <see cref="GlassBreak"/> decides what happens, when and where: the pane fails at the window, the pieces
/// arrive at the foot of the wall sqrt(2h/g) later. Each of those is now one sound named by its key and
/// rendered on the client by <see cref="GlassFracture"/>, which simulates the glass itself (Cody,
/// 2026-10-04: the hiss-and-tinkle mapping that was here "sounds fake and not realistic").
///
/// Three sounds at most: the BREAK at the pane (the round's strike, the cracks or the dicing, the pieces
/// grinding out, knocking each other and the sill, ringing as they go); the LANDING at the foot of the wall,
/// delayed by the bottom edge's fall time (every piece arriving, bouncing, breaking, skittering, the pile
/// building); or, for a pane that stays up, the HOLE.
/// </summary>
public static class GlassSound
{
    /// <summary>The largest single pane a glazed box stands for, metres. A shop front or a terminal's glass
    /// wall is many panes in its frame; a round breaks one of them.</summary>
    public const float MaxPaneWidth = 3f, MaxPaneHeight = 4f;

    /// <summary>
    /// The sounds of one round striking one pane. <paramref name="speedAtPane"/> is the round's speed when it
    /// got there (zero: the weapon's muzzle velocity); <paramref name="ground"/> the material under the
    /// window; <paramref name="seed"/> picks one of <see cref="GlassFracture.Variants"/> breaks.
    /// </summary>
    public static List<TransientSound> From(IEnumerable<GlassEvent> events, GlassPane pane, WeaponDefinition weapon,
                                            float thicknessMetres, string ground, int seed, float speedAtPane = 0f)
    {
        var sounds = new List<TransientSound>(2);
        var (kg, pellets) = GlassFracture.BulletOf(weapon);
        float speed = speedAtPane > 1f ? speedAtPane : weapon.MuzzleVelocity;
        float width = Math.Clamp(pane.Size.X, 0.1f, MaxPaneWidth), height = Math.Clamp(pane.Size.Y, 0.1f, MaxPaneHeight);
        float thickness = Math.Clamp(thicknessMetres, 0.003f, 0.025f);
        GlassFracture.Spec Spec(GlassFracture.Part part, float drop) => new(part, pane.Type, width, height, thickness,
                                                                           kg, speed, pellets, drop, ground, seed);

        Vector3 landingAt = Vector3.Zero;
        int landings = 0;
        bool shatter = false;
        Vector3 hole = pane.Centre;
        bool punctured = false;
        foreach (var e in events)
        {
            if (e.Kind == GlassEventKind.Shatter) shatter = true;
            else if (e.Kind == GlassEventKind.Landing) { landingAt += e.Position; landings++; }
            else if (e.Kind == GlassEventKind.Puncture) { punctured = true; hole = e.Position; }
        }

        if (punctured && !shatter)
        {
            var spec = Spec(GlassFracture.Part.Hole, 0f);
            sounds.Add(new TransientSound
            {
                Character = SoundCharacter.Knock, Position = hole, LevelDb = GlassFracture.DeclaredDb(spec),
                Hz = 2000f, DecaySeconds = 0.5f, Noisiness = 0.6f, SynthKey = GlassFracture.Key(spec),
            });
            return sounds;
        }
        if (!shatter) return sounds;

        var breakSpec = Spec(GlassFracture.Part.Break, pane.HeightAboveGround);
        sounds.Add(new TransientSound
        {
            Character = SoundCharacter.Knock, Position = pane.Centre, LevelDb = GlassFracture.DeclaredDb(breakSpec),
            Hz = 4000f, DecaySeconds = 1f, Noisiness = 0.8f, SynthKey = GlassFracture.Key(breakSpec),
        });
        if (pane.Type == GlassType.Laminated) return sounds;

        // The landing: at the foot of the wall, thrown out a little (GlassBreak's own landing places, or
        // the foot of the pane when it stands on the ground and GlassBreak has no fall to time), a tenth of a
        // second before the bottom edge's free fall (a piece thrown down arrives first). Its render starts at
        // that same moment, so the first piece still says the floor.
        float drop = GlassFracture.QuantiseDrop(pane.HeightAboveGround);
        Vector3 at = landings > 0 ? landingAt / landings
                   : new Vector3(pane.Centre.X, pane.Centre.Y - pane.HeightAboveGround - pane.Size.Y * 0.5f, pane.Centre.Z) + pane.Normal * 0.5f;
        var landSpec = Spec(GlassFracture.Part.Land, drop);
        sounds.Add(new TransientSound
        {
            // A rain of a few hundred to some tens of thousands of arrivals: a sustained sound, not an impulse,
            // so it is not given the street's flutter of copies.
            Character = SoundCharacter.Hiss, DelaySeconds = GlassFracture.LandingStart(drop), Position = at,
            LevelDb = GlassFracture.DeclaredDb(landSpec), Hz = 6000f, DecaySeconds = 1.5f, Noisiness = 1f,
            SynthKey = GlassFracture.Key(landSpec),
        });
        return sounds;
    }
}

using System.Collections.Generic;
using System.Numerics;

namespace OpenFPS.Common;

/// <summary>
/// Two things meeting, from two materials, a size, a closing speed and two masses, whatever the things
/// are: how hard from the energy (reduced mass), what of from the softer material for the blow and the
/// struck panel for the ring (a hammer on a bell is a bell), how big from the struck face's size.
/// </summary>
public static class ImpactAcoustics
{
    /// <summary>Below this a collision is a scuff, not an event: a car creeping into a kerb.</summary>
    public const float MinimumSpeed = 0.4f;

    /// <summary>The sound of one thing hitting another. <paramref name="struckWidth"/> and
    /// <paramref name="struckHeight"/> are the face that was hit, which decides the note.</summary>
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

        var softer = hitter.YoungsModulusGPa <= struck.YoungsModulusGPa ? hitter : struck;
        float softness = 1f / (1f + MathF.Max(0f, softer.YoungsModulusGPa));

        // Contact time grows with mass, and a longer contact is a lower sound: without it a car
        // meeting a wall came out at 1.4 kHz for 28 ms, a tap. One kilogram is the neutral point.
        float reduced = PanelAcoustics.ImpactJoules(hitterMassKg, struckMassKg, 1f) * 2f;
        float sizeScale = 1f / MathF.Pow(MathF.Max(0.02f, reduced), 0.28f);
        float lengthScale = MathF.Pow(MathF.Max(0.02f, reduced), 0.18f);

        sounds.Add(new TransientSound
        {
            Character = SoundCharacter.Knock,
            DelaySeconds = 0f,
            Position = where,
            LevelDb = db,
            Hz = Math.Clamp((1400f * (1f - softness) + 70f) * sizeScale, 45f, 3000f),
            DecaySeconds = Math.Clamp((0.02f + softness * 0.25f) * lengthScale, 0.02f, 0.6f),
            Noisiness = 0.85f,
        });

        // Then the struck panel rings. Only its dimensions are known here: a caller that wants the
        // hitter to ring passes it as the struck one.
        float hz = PanelAcoustics.RingHz(struck, struckWidth, struckHeight, struckThickness);
        if (hz > 0f)
        {
            float mounting = struckIsFixed ? PanelAcoustics.MountedLoss : 0f;
            var ringerMaterial = struck;
            float seconds = PanelAcoustics.RingSeconds(ringerMaterial, hz, mounting);
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
/// What <see cref="GlassBreak"/> decided, as sounds keyed for <see cref="GlassFracture"/> to simulate on
/// the client: the break at the pane, the landing at the foot of the wall a fall time later, or for a
/// pane that stays up the hole. (Cody, 2026-10-04, of the hiss-and-tinkle mapping it replaced: "sounds
/// fake and not realistic".)
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

        // The landing, at GlassBreak's landing places (or the pane's foot when it stands on the ground), a
        // tenth of a second before the bottom edge's free fall: a piece thrown down arrives first.
        float drop = GlassFracture.QuantiseDrop(pane.HeightAboveGround);
        Vector3 at = landings > 0 ? landingAt / landings
                   : new Vector3(pane.Centre.X, pane.Centre.Y - pane.HeightAboveGround - pane.Size.Y * 0.5f, pane.Centre.Z) + pane.Normal * 0.5f;
        var landSpec = Spec(GlassFracture.Part.Land, drop);
        sounds.Add(new TransientSound
        {
            // Hundreds to tens of thousands of arrivals: sustained, so no street flutter of copies.
            Character = SoundCharacter.Hiss, DelaySeconds = GlassFracture.LandingStart(drop), Position = at,
            LevelDb = GlassFracture.DeclaredDb(landSpec), Hz = 6000f, DecaySeconds = 1.5f, Noisiness = 1f,
            SynthKey = GlassFracture.Key(landSpec),
        });
        return sounds;
    }
}

using System;
using System.Collections.Generic;
using System.Numerics;

namespace OpenFPS.Common;

/// <summary>One of the sounds a door makes, and they are not one sound.</summary>
public enum DoorSoundKind
{
    /// <summary>The bolt clearing the strike plate as the handle is worked. Small, sharp, metallic,
    /// and the FIRST thing you hear — before anything has moved.</summary>
    Latch,
    /// <summary>The hinges, while the leaf is travelling. Only if they are dry.</summary>
    Hinge,
    /// <summary>The leaf meeting its frame. The impact itself: broadband, brief, and loud in
    /// proportion to how fast it was travelling.</summary>
    Impact,
    /// <summary>Air being driven out of a seal as the leaf closes the last centimetre. A short
    /// pressure whoomp, low and soft, and most of why an expensive car sounds expensive.</summary>
    Seal,
    /// <summary>The leaf ringing at its own modes after the impact. What tells a listener whether
    /// they just heard steel, wood, glass or canvas.</summary>
    Panel,
}

/// <summary>
/// One sound, when it happens relative to the event, and what it is made of.
///
/// Deliberately parameters rather than a file name. A door is a family of sounds, not a sound: the
/// same leaf shut gently and slammed are different events, and a steel door and a plywood one are
/// different again. A sample library would need the cross product and would still be wrong for the
/// fifth material somebody invents.
/// </summary>
public readonly record struct DoorSound(
    DoorSoundKind Kind,
    /// <summary>What it is PHYSICALLY, as opposed to which part of a door it is. A latch and a bullet
    /// striking concrete are both knocks; a panel and a bell are both rings. The audio engine only
    /// ever needs to know this much, which is what lets it handle the thing nobody has invented yet.</summary>
    SoundCharacter Character,
    /// <summary>Seconds after the event this one starts.</summary>
    float DelaySeconds,
    /// <summary>Where it comes from. The latch is at the latch edge, not the middle of the leaf.</summary>
    Vector3 Position,
    /// <summary>Peak level at one metre, dB SPL.</summary>
    float LevelDb,
    /// <summary>Centre frequency, Hz — the note for a ring, the tilt for a noise burst.</summary>
    float Hz,
    /// <summary>How long it takes to fall 60 dB, seconds.</summary>
    float DecaySeconds,
    /// <summary>0 is a pure tone, 1 is pure noise. A latch is mostly noise; a panel is mostly not.</summary>
    float Noisiness)
{
    /// <summary>The same sound in the vocabulary every other source in the game uses.</summary>
    public TransientSound ToTransient() => new()
    {
        Character = Character,
        DelaySeconds = DelaySeconds,
        Position = Position,
        LevelDb = LevelDb,
        Hz = Hz,
        DecaySeconds = DecaySeconds,
        Noisiness = Noisiness,
    };
}

/// <summary>
/// What a door sounds like, from what it is made of and how hard it was moved.
///
/// The whole of it comes out of four things that are already known about the leaf — its material,
/// its size, its thickness and how fast it is travelling — and nothing here is a per-door setting.
/// Give the same doorway a steel leaf and it rings; give it plywood and it knocks; give it a heavy
/// car door with a rubber seal and the seal dominates and the ring almost disappears. That is the
/// same bargain the engine, the tyres and the glass already make.
///
/// The physics that matters:
///
///   A PANEL RINGS at its own bending modes, and the fundamental of a flat plate goes as
///   (t / L^2) * sqrt(E / rho). So the note is set by how stiff and how heavy the stuff is, and by
///   the shape — a big thin panel booms, a small thick one knocks. Steel and glass are both stiff,
///   but glass is lighter for its stiffness and rings higher.
///
///   HOW LONG IT RINGS is the material's internal damping, which is a completely different property
///   from how much airborne sound it absorbs. A steel door and a carpeted one absorb about the same
///   from the air and differ by orders of magnitude in how long they ring, which is why one clangs
///   and the other thuds.
///
///   THE IMPACT is the kinetic energy arriving: half m v squared, and the level goes as the log of
///   it. Shut a door twice as fast and it is about six decibels louder, which is what makes a slam
///   recognisable as a slam rather than as a louder close.
///
///   A SEAL, where there is one, absorbs the last of that energy into compressing rubber and pushing
///   air out. So a sealed door is QUIETER and LOWER than the same leaf unsealed, and it robs the
///   panel of the energy that would have made it ring — which is exactly what a car door sounds
///   like against a garden gate.
/// </summary>
public static class DoorAcoustics
{
    /// <summary>The bolt and strike plate are steel however wooden the door is, so the latch is the
    /// one part of a door that sounds much the same on all of them.</summary>
    private const float LatchHz = 2600f;

    /// <summary>A door is a panel hung in a frame; the damping that comes with being hung lives with
    /// every other panel's.</summary>
    public const float HungPanelLoss = PanelAcoustics.MountedLoss;

    /// <summary>
    /// The note a flat panel rings at, Hz.
    ///
    /// The plate-bending fundamental: roughly (t / L^2) * sqrt(E / rho), with the constants folded
    /// into one factor. A material with no stiffness worth speaking of returns nothing, because a
    /// carpet does not have a note.
    /// </summary>
    public static float PanelHz(MaterialProperties material, float width, float height, float thickness)
        => PanelAcoustics.RingHz(material, width, height, thickness);

    /// <summary>
    /// How long that ring takes to fall 60 dB.
    ///
    /// T60 = 2.2 / (loss factor * frequency) — the standard relation, and it is why a steel door
    /// with a loss factor of two ten-thousandths rings for seconds while a plastic one is over
    /// before you notice it started.
    /// </summary>
    public static float RingSeconds(MaterialProperties material, float hz, float mountingLoss = HungPanelLoss)
        => PanelAcoustics.RingSeconds(material, hz, mountingLoss);

    /// <summary>
    /// Everything a door does when it shuts, in order.
    ///
    /// <paramref name="closingSpeed"/> is the LEAF'S EDGE speed in metres per second — a gentle push
    /// is under half a metre a second and a slam is three or four. <paramref name="hasSeal"/> says
    /// whether the frame has a compliant seal in it: a car, a fridge and a fire door do, a garden
    /// gate and a saloon door do not.
    /// </summary>
    public static List<DoorSound> Closing(MaterialProperties material, Vector3 latchEdge, Vector3 centre,
                                          float width, float height, float thickness, float massKg,
                                          float closingSpeed, bool hasSeal)
    {
        var sounds = new List<DoorSound>(4);
        float speed = MathF.Max(0.05f, closingSpeed);
        float energy = 0.5f * MathF.Max(0.5f, massKg) * speed * speed;   // joules arriving

        // A seal takes the sting out of the impact: the last of the travel goes into squeezing
        // rubber instead of into the frame. It is the difference between a thunk and a bang.
        float sealAbsorbed = hasSeal ? 0.55f : 0f;
        float impactEnergy = energy * (1f - sealAbsorbed);

        float impactDb = PanelAcoustics.ImpactDb(impactEnergy);

        // 1. The latch, first, and before the leaf has met anything: the bolt rides up the strike
        //    plate and drops. Almost independent of the door, because the mechanism is steel whatever
        //    the leaf is made of.
        sounds.Add(new DoorSound(DoorSoundKind.Latch, SoundCharacter.Knock, 0.02f, latchEdge,
                                 impactDb - 8f, LatchHz, 0.05f, 0.75f));

        // 2. The seal, if there is one, overlapping the end of the travel — air being pushed out of
        //    a closing gap, so it is low, soft and brief.
        if (hasSeal)
            sounds.Add(new DoorSound(DoorSoundKind.Seal, SoundCharacter.Hiss, 0f, centre,
                                     impactDb - 4f, 90f, 0.12f, 0.95f));

        // 3. The impact itself.
        sounds.Add(new DoorSound(DoorSoundKind.Impact, SoundCharacter.Knock, 0f, latchEdge,
                                 impactDb, 160f, 0.06f, 0.85f));

        // 4. The panel ringing on afterwards, with whatever energy the seal did not take.
        float hz = PanelHz(material, width, height, thickness);
        if (hz > 0f)
        {
            float ring = RingSeconds(material, hz);
            // Having a note is not the same as ringing. A carpet has modes like everything else and
            // very obviously does not ring; what tells them apart is whether the note outlasts the
            // blow that caused it.
            if (PanelAcoustics.RingsAudibly(material, hz, HungPanelLoss))
                sounds.Add(new DoorSound(DoorSoundKind.Panel, SoundCharacter.Ring, 0.004f, centre,
                                         impactDb - 6f - 12f * sealAbsorbed, hz, ring, 0.15f));
        }
        return sounds;
    }

    /// <summary>
    /// Opening, which is a quieter event and a different one.
    ///
    /// The latch is worked and the leaf comes AWAY from the frame, so there is no impact and no
    /// seal — but there is the seal PEELING, which is the small suck a well-sealed door makes when
    /// you pull it, and there are the hinges for as long as it is moving.
    /// </summary>
    public static List<DoorSound> Opening(MaterialProperties material, Vector3 latchEdge, Vector3 hinge,
                                          float width, float height, float thickness,
                                          float swingSeconds, float hingeDryness, bool hasSeal)
    {
        var sounds = new List<DoorSound>(3)
        {
            new(DoorSoundKind.Latch, SoundCharacter.Knock, 0f, latchEdge, 58f, LatchHz, 0.04f, 0.8f),
        };

        if (hasSeal)
            sounds.Add(new DoorSound(DoorSoundKind.Seal, SoundCharacter.Hiss, 0.03f, latchEdge, 50f, 220f, 0.09f, 0.9f));

        // Hinges are a stick-slip relaxation oscillation: rubber on road, brake on disc, a dry pin in
        // a dry knuckle. The same process as a tyre at its limit, and it sings for the same reason.
        if (hingeDryness > 0.05f)
        {
            // A bigger, heavier pin groans lower — so the note comes off the leaf, not off a table.
            float hz = Math.Clamp(900f / MathF.Max(0.3f, width * height), 120f, 2200f);
            sounds.Add(new DoorSound(DoorSoundKind.Hinge, SoundCharacter.Scrape, 0.05f, hinge,
                                     44f + 16f * hingeDryness, hz,
                                     MathF.Max(0.1f, swingSeconds * 0.8f), 0.35f));
        }
        return sounds;
    }

    /// <summary>
    /// How fast the latch edge of a door is travelling, given how long its whole swing takes.
    ///
    /// The edge travels an arc of the leaf's width through the swing angle, so a wide door shuts
    /// faster at the edge than a narrow one taking the same time — which is why a gate bangs and a
    /// cupboard clicks.
    /// </summary>
    public static float EdgeSpeed(float width, float swingRadians, float swingSeconds)
        => swingSeconds <= 0f ? 0f : MathF.Abs(width * swingRadians) / swingSeconds;
}

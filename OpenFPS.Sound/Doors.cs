using System.Collections.Generic;
using System.Numerics;

namespace OpenFPS.Common;

/// <summary>
/// What hardware a door has, which decides how it is opened, how it moves and what it sounds like.
/// Stored as an int in <see cref="OpenFPS.Common.Components.DoorComponent.Kind"/>; 0 is the default.
/// </summary>
public enum DoorKind
{
    /// <summary>A hinged door with a knob or lever: houses and flats.</summary>
    Hinged = 0,
    /// <summary>A steel door with a push bar (panic bar) and a closer: fire and stair doors, service doors.</summary>
    PushBar = 1,
    /// <summary>An aluminium-and-glass front door: push bar inside, key cylinder outside, a closer.</summary>
    GlassPushBar = 2,
    /// <summary>A glass door opened by pulling its handle, with a closer: some shops.</summary>
    GlassPull = 3,
    /// <summary>An automatic sliding door, one leaf of a pair or on its own: opens for anyone who comes up to it.</summary>
    AutoSliding = 4,
    /// <summary>A patio door: a glass leaf slid open and shut by hand.</summary>
    PatioSliding = 5,
    /// <summary>A lift's doors: bi-parting, moved by the lift's motor.</summary>
    Elevator = 6,
}

/// <summary>
/// The names of the mechanical events a door makes, sent as the label of each door sound:
/// "door:KIND:EVENT". Every key, and when it fires, is in docs/DOOR_TYPES_EVENTS.md.
/// </summary>
public static class DoorEvents
{
    public const string LatchRetract = "latch-retract";
    public const string Bar = "bar";
    /// <summary>The leaf set moving by a hand pushing it, from its push side (<c>DoorComponent.PushSide</c>).</summary>
    public const string Push = "push";
    /// <summary>The leaf set moving by a hand pulling it, from the other side.</summary>
    public const string Pull = "pull";
    /// <summary>The key going into the cylinder, from the keyed side of a shut door.</summary>
    public const string KeyInsert = "key-insert";
    /// <summary>The key turning in the cylinder.</summary>
    public const string KeyTurn = "key-turn";
    /// <summary>The turned key drawing the latch or bolt back: the door is unlocked.</summary>
    public const string Unlock = "unlock";
    /// <summary>Kept for code written before the key had three events: the key turning.</summary>
    public const string Key = KeyTurn;
    public const string Swing = "swing";
    public const string Closer = "closer";
    public const string Latch = "latch";
    public const string MotorStart = "motor-start";
    public const string Rollers = "rollers";
    public const string Stop = "stop";
    public const string Shut = "shut";
    public const string Reopen = "reopen";

    /// <summary>The kind's name in event keys and in prefabs ("DoorKind").</summary>
    public static string Slug(DoorKind kind) => kind switch
    {
        DoorKind.PushBar => "pushbar",
        DoorKind.GlassPushBar => "glass-pushbar",
        DoorKind.GlassPull => "glass-pull",
        DoorKind.AutoSliding => "auto-slide",
        DoorKind.PatioSliding => "patio-slide",
        DoorKind.Elevator => "elevator",
        _ => "knob",
    };

    /// <summary>A kind from its slug; false for a name that is not one.</summary>
    public static bool TryParseKind(string? slug, out DoorKind kind)
    {
        foreach (DoorKind k in Enum.GetValues<DoorKind>())
            if (string.Equals(Slug(k), slug?.Trim(), StringComparison.OrdinalIgnoreCase)) { kind = k; return true; }
        kind = DoorKind.Hinged;
        return false;
    }

    /// <summary>The event key: "door:pushbar:bar".</summary>
    public static string Of(DoorKind kind, string ev) => $"door:{Slug(kind)}:{ev}";

    /// <summary>Splits an event key; false for anything that is not one.</summary>
    public static bool TryParse(string? key, out DoorKind kind, out string ev)
    {
        kind = DoorKind.Hinged; ev = "";
        if (key == null || !key.StartsWith("door:", StringComparison.Ordinal)) return false;
        int colon = key.IndexOf(':', 5);
        if (colon < 0 || !TryParseKind(key[5..colon], out kind)) return false;
        ev = key[(colon + 1)..];
        return ev.Length > 0;
    }

    /// <summary>Kinds whose leaf slides rather than swings.</summary>
    public static bool SlidesByDefault(DoorKind kind)
        => kind is DoorKind.AutoSliding or DoorKind.PatioSliding or DoorKind.Elevator;
}

/// <summary>One of the sounds a door makes, and they are not one sound.</summary>
public enum DoorSoundKind
{
    /// <summary>The bolt clearing the strike plate as the handle is worked: the first thing heard,
    /// before anything has moved.</summary>
    Latch,
    /// <summary>The hinges while the leaf travels, only if they are dry.</summary>
    Hinge,
    /// <summary>The leaf meeting its frame: broadband, brief, loud as it was fast.</summary>
    Impact,
    /// <summary>Air driven out of a seal in the last centimetre: a low, soft whoomp.</summary>
    Seal,
    /// <summary>The leaf ringing at its own modes after the impact: what says steel, wood, glass or
    /// canvas.</summary>
    Panel,
}

/// <summary>
/// One sound, when it happens relative to the event, and what it is made of. Parameters rather than a
/// file name: a door is a family of sounds (gentle and slammed, steel and plywood), and a sample library
/// would need the cross product.
/// </summary>
/// <param name="Character">What it is physically, not which part of the door: a latch and a bullet on
/// concrete are both knocks, a panel and a bell both rings.</param>
/// <param name="DelaySeconds">Seconds after the event this one starts.</param>
/// <param name="Position">Where it comes from: the latch is at the latch edge, not the middle of the leaf.</param>
/// <param name="LevelDb">Peak level at one metre, dB SPL.</param>
/// <param name="Hz">Centre frequency: the note for a ring, the tilt for a noise burst.</param>
/// <param name="DecaySeconds">How long it takes to fall 60 dB.</param>
/// <param name="Noisiness">0 is a pure tone, 1 pure noise.</param>
public readonly record struct DoorSound(
    DoorSoundKind Kind,
    SoundCharacter Character,
    float DelaySeconds,
    Vector3 Position,
    float LevelDb,
    float Hz,
    float DecaySeconds,
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
/// What a door sounds like, from the leaf's material, size, thickness and speed; nothing here is a
/// per-door setting. A panel rings at its bending modes, its fundamental going as (t / L^2) sqrt(E / rho).
/// How long it rings is the material's internal damping, not its airborne absorption: steel and carpet
/// absorb about the same and differ by orders of magnitude in ring. The impact is the kinetic energy
/// arriving, so twice as fast is about 6 dB louder. A seal takes the last of that energy, so a sealed
/// door is quieter and lower and its panel rings less.
/// </summary>
public static class DoorAcoustics
{
    /// <summary>The bolt and strike plate are steel however wooden the door is, so the latch sounds
    /// much the same on all of them.</summary>
    private const float LatchHz = 2600f;

    public const float HungPanelLoss = PanelAcoustics.MountedLoss;

    /// <summary>The note a flat panel rings at, Hz, from the plate-bending fundamental; nothing for a
    /// material with no stiffness worth speaking of (a carpet has no note).</summary>
    public static float PanelHz(MaterialProperties material, float width, float height, float thickness)
        => PanelAcoustics.RingHz(material, width, height, thickness);

    /// <summary>How long that ring takes to fall 60 dB: T60 = 2.2 / (loss factor * frequency).</summary>
    public static float RingSeconds(MaterialProperties material, float hz, float mountingLoss = HungPanelLoss)
        => PanelAcoustics.RingSeconds(material, hz, mountingLoss);

    /// <summary>
    /// Everything a door does when it shuts, in order. <paramref name="closingSpeed"/> is the leaf's
    /// edge speed, m/s: a gentle push is under 0.5, a slam three or four. <paramref name="hasSeal"/>:
    /// a car, a fridge and a fire door have one, a garden gate does not.
    /// </summary>
    public static List<DoorSound> Closing(MaterialProperties material, Vector3 latchEdge, Vector3 centre,
                                          float width, float height, float thickness, float massKg,
                                          float closingSpeed, bool hasSeal)
    {
        var sounds = new List<DoorSound>(4);
        float speed = MathF.Max(0.05f, closingSpeed);
        float energy = 0.5f * MathF.Max(0.5f, massKg) * speed * speed;   // joules arriving

        // A seal takes the last of the travel into squeezing rubber instead of the frame.
        float sealAbsorbed = hasSeal ? 0.55f : 0f;
        float impactEnergy = energy * (1f - sealAbsorbed);

        float impactDb = PanelAcoustics.ImpactDb(impactEnergy);

        // The latch comes after the leaf has seated, about 90 ms: at 20 ms forward masking from the thump
        // hid it ("I didn't hear the knob or latch or anything, just a thump").
        // Three parts: the ramp tick, the click into the keeper, and the leaf answering both. A recorded
        // latch has 42 % of its energy below 200 Hz; as a pure click (2 %) it was "a puff of white noise".
        sounds.Add(new DoorSound(DoorSoundKind.Latch, SoundCharacter.Knock, 0.072f, latchEdge,
                                 impactDb - 21f, LatchHz * 1.2f, 0.015f, 0.3f));
        sounds.Add(new DoorSound(DoorSoundKind.Latch, SoundCharacter.Knock, 0.098f, latchEdge,
                                 impactDb - 15f, LatchHz, 0.03f, 0.25f));
        sounds.Add(new DoorSound(DoorSoundKind.Latch, SoundCharacter.Knock, 0.099f, latchEdge,
                                 impactDb - 2f, 140f, 0.09f, 0.5f));

        // The seal: air squeezed out of a narrowing gap, broadband, brief and quiet. As a 90 Hz resonance
        // it was "someone popping a cork".
        if (hasSeal)
            sounds.Add(new DoorSound(DoorSoundKind.Seal, SoundCharacter.Hiss, 0f, centre,
                                     impactDb - 24f, 520f, 0.03f, 1f));

        // The impact, centred high: a recorded close carries nearly two fifths of its energy between
        // 200 Hz and 1.5 kHz (frame, stop and hardware); all bottom and no middle is a boom, not a thud.
        sounds.Add(new DoorSound(DoorSoundKind.Impact, SoundCharacter.Knock, 0f, latchEdge,
                                 impactDb, 430f, 0.06f, 0.85f));

        // The panel ringing on with whatever energy the seal did not take.
        float hz = PanelHz(material, width, height, thickness);
        if (hz > 0f)
        {
            // Most of a closing door's energy goes into the frame it hit: without this a wooden door held
            // 129 Hz for a third of a second, "a cork being popped". A fact about doors, not panels, so a
            // factor here rather than more damping in PanelAcoustics.
            const float intoTheFrame = 0.45f;
            float ring = RingSeconds(material, hz) * intoTheFrame;
            // Having a note is not ringing: a carpet has modes too. Below the blow, heard after the thud.
            if (PanelAcoustics.RingsAudibly(material, hz, HungPanelLoss))
                sounds.Add(new DoorSound(DoorSoundKind.Panel, SoundCharacter.Ring, 0.004f, centre,
                                         impactDb - 9f - 12f * sealAbsorbed, hz, ring, 0.15f));
        }
        return sounds;
    }

    /// <summary>
    /// Opening: no impact and no squeeze, but the latch, the seal peeling and the hinges while it moves.
    /// The latch spring gives every leaf about the same kick, so the leaf's answer goes as one over its
    /// mass, and it rings a little by the same rule as on shutting.
    /// </summary>
    public static List<DoorSound> Opening(MaterialProperties material, Vector3 latchEdge, Vector3 hinge,
                                          float width, float height, float thickness, float massKg,
                                          float swingSeconds, float hingeDryness, bool hasSeal)
    {
        float leaf = MathF.Max(0.5f, massKg) / ReferenceLeafKg;
        // At conversation level the first listening test heard none of this at two metres.
        const float clickDb = 74f;
        float bodyDb = clickDb - Math.Clamp(10f * MathF.Log10(leaf), -12f, 12f);
        var sounds = new List<DoorSound>(5)
        {
            new(DoorSoundKind.Latch, SoundCharacter.Knock, 0f, latchEdge, clickDb, LatchHz, 0.03f, 0.2f),
            new(DoorSoundKind.Latch, SoundCharacter.Knock, 0.001f, latchEdge, bodyDb, 140f, 0.07f, 0.5f),
        };

        float hz = PanelHz(material, width, height, thickness);
        if (hz > 0f && PanelAcoustics.RingsAudibly(material, hz, HungPanelLoss))
            // Still in its frame, which takes most of the ring, as on closing.
            sounds.Add(new DoorSound(DoorSoundKind.Panel, SoundCharacter.Ring, 0.004f, latchEdge,
                                     bodyDb - 7f, hz, RingSeconds(material, hz) * 0.45f, 0.15f));

        if (hasSeal)
            sounds.Add(new DoorSound(DoorSoundKind.Seal, SoundCharacter.Hiss, 0.03f, latchEdge, 66f, 220f, 0.09f, 0.9f));

        // A dry hinge is stick-slip, as a tyre at its limit.
        if (hingeDryness > 0.05f)
        {
            // The pin is the spring and the leaf the mass: 476 Hz for the reference door, as one over
            // the root of the mass.
            float hingeHz = Math.Clamp(476f / MathF.Sqrt(leaf), 120f, 2200f);
            sounds.Add(new DoorSound(DoorSoundKind.Hinge, SoundCharacter.Scrape, 0.05f, hinge,
                                     60f + 16f * hingeDryness, hingeHz,
                                     MathF.Max(0.1f, swingSeconds * 0.8f), 0.35f));
        }
        return sounds;
    }

    /// <summary>The door the opening sounds are levelled on: solid wood, 0.9 by 2.1 m, 40 mm thick.</summary>
    private const float ReferenceLeafKg = 0.9f * 2.1f * 0.04f * 650f;

    /// <summary>How fast the latch edge travels, given how long the whole swing takes: a wide door
    /// shuts faster at the edge than a narrow one in the same time, so a gate bangs and a cupboard
    /// clicks.</summary>
    public static float EdgeSpeed(float width, float swingRadians, float swingSeconds)
        => swingSeconds <= 0f ? 0f : MathF.Abs(width * swingRadians) / swingSeconds;
}

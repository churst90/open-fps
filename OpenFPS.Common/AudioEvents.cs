using System.Collections.Generic;
using System.Numerics;
using MemoryPack;


namespace OpenFPS.Common
{
/// <summary>
/// What a short sound is physically, not what made it: a latch and a bullet strike are both knocks, a
/// panel and a bell both ring. Named by physics so the synthesiser needs no case per new thing.
/// </summary>
public enum SoundCharacter
{
    /// <summary>A brief broadband impact. Energy arriving all at once and mostly leaving again.</summary>
    Knock,
    /// <summary>Something continuing to vibrate at its own note after being struck.</summary>
    Ring,
    /// <summary>Filtered noise with a soft edge — air moving, rather than something being hit.</summary>
    Hiss,
    /// <summary>Stick-slip: a rough, modulated tone that lasts as long as the rubbing does.</summary>
    Scrape,
}

/// <summary>
/// One short sound, described by its physics rather than by a file name: the same handful of numbers
/// for every source, so anything that can say them is heard without the audio engine knowing what it was.
/// </summary>
[MemoryPackable]
public partial struct TransientSound
{
    public SoundCharacter Character { get; set; }

    /// <summary>Seconds after the event this one starts. A latch precedes its own impact; a shard
    /// lands a second and a half after the pane broke.</summary>
    public float DelaySeconds { get; set; }

    /// <summary>Where it comes from, not necessarily where its cause is: a door's latch is at its latch
    /// edge, and glass lands at the foot of the wall.</summary>
    public Vector3 Position { get; set; }

    /// <summary>Peak level at one metre, dB SPL.</summary>
    public float LevelDb { get; set; }

    /// <summary>Centre frequency, Hz: the note of a ring, the tilt of a noise burst.</summary>
    public float Hz { get; set; }

    /// <summary>Seconds to fall 60 dB.</summary>
    public float DecaySeconds { get; set; }

    /// <summary>0 is a pure tone, 1 is pure noise.</summary>
    public float Noisiness { get; set; }

    /// <summary>
    /// A richer model to render this with ("weapon:akm"), or empty for the four characters: a gunshot is
    /// more than one knock. The level, position, delay and acoustic path are still the channel's; only
    /// the waveform comes from the model.
    /// </summary>
    public string SynthKey { get; set; } = "";

    /// <summary>
    /// How big the thing making it is, metres; zero, a point, for almost everything. A grandstand of
    /// people is not a point: the size changes the near field (four hundred people are not a firework at
    /// your ear) and must not change the far field (<see cref="Loudness.Place(float, float)"/>).
    /// </summary>
    public float ExtentMetres { get; set; }

    /// <summary>A sound made on a body (a clap): placed by the listener on that body as it is NOW,
    /// at <see cref="BodyOffset"/>, not at <see cref="Position"/>, which is where the server had the body
    /// when it was made. Walking, the two are a step apart, and your own clap came from behind you.</summary>
    public bool OnBody { get; set; }
    /// <summary>Where on the body, metres, in the body's frame: x right, y up from the feet, z forward.</summary>
    public Vector3 BodyOffset { get; set; }

    /// <summary>
    /// Where the sound has got to by the end of <see cref="MoveSeconds"/>, moved evenly from
    /// <see cref="Position"/>: a sliding door's handle crosses the doorway while its run plays.
    /// </summary>
    public Vector3 MovesTo { get; set; }
    /// <summary>How long it takes to get to <see cref="MovesTo"/>, seconds. Zero: it stays where it is.</summary>
    public float MoveSeconds { get; set; }

    /// <summary>
    /// For a panel that radiates from both faces (a door leaf), its normal, as long as the distance the
    /// sound is placed off it; zero otherwise. Each listener hears it off the face on their side: placed
    /// on the panel it was heard through the leaf and jamb, a front door in plain view at three metres
    /// −46 dB mid-band.
    /// </summary>
    public Vector3 FaceNormal { get; set; }

    public TransientSound() { }

    /// <summary>Where a <see cref="FaceNormal"/> sound is for a listener: off the panel on their side.
    /// Anything else stays where it is.</summary>
    public static Vector3 FacingListener(Vector3 onPanel, Vector3 faceNormal, Vector3 listener)
    {
        if (faceNormal == Vector3.Zero) return onPanel;
        return Vector3.Dot(listener - onPanel, faceNormal) >= 0f ? onPanel + faceNormal : onPanel - faceNormal;
    }

    /// <summary>Where a moving sound is, <paramref name="seconds"/> after it started.</summary>
    public static Vector3 Along(Vector3 from, Vector3 to, float moveSeconds, float seconds)
        => moveSeconds <= 0f ? from : Vector3.Lerp(from, to, Math.Clamp(seconds / moveSeconds, 0f, 1f));
}

}

namespace OpenFPS.Common.Networking
{

/// <summary>
/// Something happened somewhere and made a noise: the one channel for every short sound the world
/// produces. No sound id: the parameters make a door of a material somebody invented audible the first
/// time it shuts, with no recording made in advance.
/// </summary>
[MemoryPackable]
public partial class WorldAudioEvent : IMessage
{
    /// <summary>What made it, or -1: so the sound is not occluded by its own maker, and so a player can
    /// be told what they heard.</summary>
    public int SourceEntityId { get; set; } = -1;

    /// <summary>What it was, in words: "door", "glass", "impact". For speech and for logs.</summary>
    public string Label { get; set; } = "";

    /// <summary>The sounds themselves, usually several.</summary>
    public List<TransientSound> Sounds { get; set; } = new();

    /// <summary>
    /// Makes the rendering vary between events (twenty identical rounds read as a recording) and repeat
    /// between listeners, so two players side by side hear the same event.
    /// </summary>
    public int Seed { get; set; }

    public WorldAudioEvent() { }
}
}

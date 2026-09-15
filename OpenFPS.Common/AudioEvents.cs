using System;
using System.Collections.Generic;
using System.Numerics;
using MemoryPack;
using OpenFPS.Common;


namespace OpenFPS.Common
{
/// <summary>
/// What a short sound IS, physically — not what made it.
///
/// Four of these cover everything in the game that happens and stops. A door latch and a bullet
/// striking a wall are both knocks; a struck panel and a bell are both rings; air leaving a door seal
/// and a tyre letting go are a hiss and a scrape. Naming them by their physics rather than by their
/// source is the whole point: a synthesiser that knows about "doors" needs a new case for every new
/// thing in the world, and one that knows about knocks and rings already handles the ball somebody
/// has not invented yet.
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
/// One short sound, described by its physics rather than by a file name.
///
/// This is deliberately the same handful of numbers for every source in the game. A door's panel
/// ring, a pane of glass landing, a round hitting concrete and two cars meeting all reduce to: what
/// kind of thing it is, when, where, how loud, what note, how long, and how noisy. Anything that can
/// say that can be heard, without the audio engine being told what it was.
/// </summary>
[MemoryPackable]
public partial struct TransientSound
{
    public SoundCharacter Character { get; set; }

    /// <summary>Seconds after the event this one starts. A latch precedes its own impact; a shard
    /// lands a second and a half after the pane broke.</summary>
    public float DelaySeconds { get; set; }

    /// <summary>Where it comes from. Not necessarily where the thing that caused it is — a door's
    /// latch is at its latch edge, and glass lands at the foot of the wall.</summary>
    public Vector3 Position { get; set; }

    /// <summary>Peak level at one metre, dB SPL.</summary>
    public float LevelDb { get; set; }

    /// <summary>Centre frequency, Hz: the note of a ring, the tilt of a noise burst.</summary>
    public float Hz { get; set; }

    /// <summary>Seconds to fall 60 dB.</summary>
    public float DecaySeconds { get; set; }

    /// <summary>0 is a pure tone, 1 is pure noise.</summary>
    public float Noisiness { get; set; }

    public TransientSound() { }
}

}

namespace OpenFPS.Common.Networking
{

/// <summary>
/// Something happened somewhere, and it made a noise.
///
/// The one channel for every short sound the world produces. Before this there was no way at all for
/// the server to say "that just happened" — an entity could carry a looping emitter, and that was the
/// whole vocabulary, which is why glass breakage, gunfire and collisions are all written, tested and
/// completely silent.
///
/// It carries no sound id, and that is the point. A file name would mean every new thing in the world
/// needs a recording of itself, made in advance, at one size and one material and one force. The
/// parameters mean a door somebody builds out of a material somebody else invented is audible the
/// first time it shuts.
/// </summary>
[MemoryPackable]
public partial class WorldAudioEvent : IMessage
{
    /// <summary>
    /// What made it, or -1.
    ///
    /// Used for two things and neither is playback: the sound must not be occluded by the very object
    /// that made it, and a player asking what they just heard deserves an answer better than "a
    /// noise".
    /// </summary>
    public int SourceEntityId { get; set; } = -1;

    /// <summary>What it was, in words: "door", "glass", "impact". For speech and for logs.</summary>
    public string Label { get; set; } = "";

    /// <summary>The sounds themselves, which are usually several. One event, several noises, is the
    /// normal case rather than the exception — almost nothing in the world makes exactly one.</summary>
    public List<TransientSound> Sounds { get; set; } = new();

    /// <summary>
    /// Makes the rendering repeatable, and makes it VARY.
    ///
    /// Two of the same event should not be bit-identical — twenty rounds from one rifle that are the
    /// same twenty samples read as a recording, which is the thing this engine exists not to do. The
    /// seed travels so that everyone hears the same variation of the same event, which matters the
    /// moment two players are standing next to each other.
    /// </summary>
    public int Seed { get; set; }

    public WorldAudioEvent() { }
}
}

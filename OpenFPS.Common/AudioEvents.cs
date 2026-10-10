using System.Collections.Generic;
using MemoryPack;

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

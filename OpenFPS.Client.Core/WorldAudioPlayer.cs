using OpenFPS.Common.Networking;

namespace OpenFPS.Client.Core;

/// <summary>
/// The one-off sounds the server reports (WorldAudioEvent), decoded for the audio world's <see cref="OneOffSounds"/>
/// (docs/SOUND_LIBRARY_BOUNDARY.md, stage 7): the event off the wire is the game's, the playing is the library's.
/// The host's own features that hear every event before it is played hear it here (the world's birds hear its sounds).
/// </summary>
public sealed class WorldAudioPlayer
{
    /// <param name="sounds">The audio world's one-off sounds (AudioWorld.OneOffs), or one of its own for a test.</param>
    public WorldAudioPlayer(OneOffSounds sounds) => Sounds = sounds;

    /// <summary>The audio world's one-off sounds, which play what this decodes.</summary>
    public OneOffSounds Sounds { get; }

    /// <summary>Everything the world reports, before it is played, for the host's own features.</summary>
    public Action<WorldAudioEvent>? Received { get; set; }

    /// <summary>Takes an event off the wire: everything new is rendered, and every sound queued for its moment.</summary>
    public void Receive(WorldAudioEvent message, double now)
    {
        if (message.Sounds == null) return;
        Received?.Invoke(message);
        Sounds.Receive(message.Sounds, message.SourceEntityId, message.Seed, message.Label, now);
    }

    /// <summary>Forgets everything queued, for a map change (OneOffSounds.Clear).</summary>
    public void Clear() => Sounds.Clear();

    /// <summary>Every render the map's doors can ask for, made as the map arrives (OneOffSounds.PrewarmDoors).</summary>
    public void PrewarmDoors(IEnumerable<string> keys) => Sounds.PrewarmDoors(keys);

    /// <summary>A horn sounded on a vehicle, as the world hears one (OneOffSounds.HornReceived).</summary>
    public Action<int, string, float[]>? HornReceived => Sounds.HornReceived;

    /// <summary>A train's horn or whistle and bell (OneOffSounds.TrainSignalReceived).</summary>
    public Action<string, float[], float>? TrainSignalReceived => Sounds.TrainSignalReceived;
}

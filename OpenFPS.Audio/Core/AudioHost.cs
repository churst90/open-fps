namespace OpenFPS.Client.AudioEngine.Core;

/// <summary>
/// The host's recordings (open-fps: the sound bank by material and action, SoundMappingService): which file a sound
/// id plays, chosen among a category's takes, and which category a footfall or a landing on a material is.
/// </summary>
public interface IRecordings
{
    /// <summary>A file for <paramref name="id"/>: one of a category's takes, or the id itself.</summary>
    string ResolvePath(string id);

    /// <summary>The category of a foot or a thing striking <paramref name="material"/> this hard.</summary>
    string GetImpactSoundId(string material, float force);

    /// <summary>The category of a body landing on <paramref name="material"/>.</summary>
    string GetLandingSoundId(string material);
}

/// <summary>
/// What an audio world asks of its host (docs/SOUND_LIBRARY_BOUNDARY.md 7.2, stage 7): where the listener is now,
/// two answers only the host's world has, and the host's own parts of each frame, called where they ran when the
/// audio system was one class, since they place voices through the same registry and the frame's order is heard.
/// </summary>
public interface IAudioHost
{
    /// <summary>The listener now: asked whenever the world places something, so a step taken between frames is
    /// placed from where the listener is then.</summary>
    OpenFPS.Client.AudioEngine.Data.ListenerState Listener { get; }

    /// <summary>The floor under <paramref name="at"/> as the host's physics has it, not counting
    /// <paramref name="leaveOut"/> (an aeroplane on its wheels).</summary>
    float GroundUnder(System.Numerics.Vector3 at, int leaveOut);

    /// <summary>One-off sounds the host has waiting to be heard, for the census.</summary>
    int OneOffsPending { get; }

    /// <summary>The listener is placed: the host's features that follow the ears (open-fps: the one-off sounds,
    /// the beacons, the driving aids).</summary>
    void ListenerPlaced(System.Numerics.Vector3 ear, float earSpeed);

    /// <summary>The rooms are set (<paramref name="newRooms"/>: a new map of them this frame): rooms that moved, the
    /// place's name, the one-off sounds.</summary>
    void RoomsSet(System.Numerics.Vector3 ear, int listenerRegion, bool newRooms);

    /// <summary>The world's sources are placed: what the host places after them (open-fps: the birds).</summary>
    void SourcesPlaced(System.Numerics.Vector3 ear, float earSpeed);

    /// <summary>The world is being left: whatever the host still has to be heard in it goes.</summary>
    void WorldLeft();
}

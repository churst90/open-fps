namespace OpenFPS.Client.Core;

/// <summary>
/// Where the interface sounds go: the client's mixer in the game (<c>UiSounds</c>), which stays in the client
/// until the audio engine is a package of its own (library stage 6). The lists name a cue; the client plays it.
/// </summary>
public interface IUiCues
{
    /// <summary>Plays <paramref name="cue"/>, unless interface sounds are off.</summary>
    void Play(UiCue cue);
}

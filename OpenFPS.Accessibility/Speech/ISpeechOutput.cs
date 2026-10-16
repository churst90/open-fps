namespace OpenFPS.Client.Core.Platform;

/// <summary>
/// Speech and screen-reader output: everything the game says to the player goes through here. Each
/// head brings its own (NvdaSpeechOutput on Windows, LinuxSpeechOutput over Orca or speech-dispatcher).
/// </summary>
public interface ISpeechOutput : IDisposable
{
    /// <summary>Connects to the speech backend. Returns false if none is available.</summary>
    bool Initialize();

    /// <summary>Speaks <paramref name="text"/>. When <paramref name="interrupt"/> is true, cancels current speech first.</summary>
    void Speak(string text, bool interrupt = true);

    void Interrupt();

    /// <summary>The backend or screen reader found, for the log.</summary>
    string BackendName { get; }
}

namespace OpenFPS.Client.Core.Platform;

/// <summary>
/// Platform-agnostic speech / screen-reader output — the single chokepoint for everything the
/// game says to the player. This is the seam that lets each platform use its best native option:
///   Windows -> TolkSpeechOutput        (Tolk: NVDA / JAWS / SAPI / ...)
///   Linux   -> SpeechDispatcherOutput   (speech-dispatcher: Orca / espeak-ng / ...)
/// </summary>
public interface ISpeechOutput : IDisposable
{
    /// <summary>Connects to the speech backend. Returns false if none is available.</summary>
    bool Initialize();

    /// <summary>Speaks <paramref name="text"/>. When <paramref name="interrupt"/> is true, cancels current speech first.</summary>
    void Speak(string text, bool interrupt = true);

    /// <summary>Immediately stops any in-progress speech.</summary>
    void Interrupt();

    /// <summary>Detected backend / screen-reader name, for diagnostics.</summary>
    string BackendName { get; }
}

namespace OpenFPS.Client.Core.Platform;

/// <summary>
/// The session's speech, passed through, remembering the last line. The turn narration cuts off only
/// its own line when you turn again; anything said since is left alone.
/// </summary>
public sealed class SpeechLog : ISpeechOutput
{
    private readonly ISpeechOutput _inner;
    public SpeechLog(ISpeechOutput inner) => _inner = inner;

    /// <summary>The last line spoken through this, or null.</summary>
    public string? LastText { get; private set; }

    public bool Initialize() => _inner.Initialize();

    public void Speak(string text, bool interrupt = true)
    {
        LastText = text;
        _inner.Speak(text, interrupt);
    }

    public void Interrupt() => _inner.Interrupt();

    public string BackendName => _inner.BackendName;

    public void Dispose() => _inner.Dispose();
}

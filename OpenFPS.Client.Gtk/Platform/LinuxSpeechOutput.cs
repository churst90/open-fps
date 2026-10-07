using OpenFPS.Client.Core.Platform;

namespace OpenFPS.Client.Gtk.Platform;

/// <summary>
/// Orca when it is running, speech-dispatcher when it is not, decided per line: a screen reader is
/// turned on and off mid-session. Both backends stay open, so a handover never waits on a connection.
/// </summary>
public sealed class LinuxSpeechOutput : ISpeechOutput
{
    private readonly OrcaSpeechOutput _orca = new();
    private readonly SpeechDispatcherOutput _dispatcher = new();
    private bool _orcaReady;
    private bool _dispatcherReady;
    private bool _lastWasOrca;

    public string BackendName =>
        _orca.IsAvailable ? _orca.BackendName
        : _dispatcherReady ? _dispatcher.BackendName
        : "none";

    public bool Initialize()
    {
        _orcaReady = _orca.Initialize();
        _dispatcherReady = _dispatcher.Initialize();
        _lastWasOrca = _orca.IsAvailable;
        return _orcaReady || _dispatcherReady;
    }

    /// <summary>
    /// The backend for this line. The one being left is silenced once, on the change: neither knows the
    /// other is talking.
    /// </summary>
    private ISpeechOutput Current()
    {
        bool useOrca = _orcaReady && _orca.IsAvailable;
        if (useOrca != _lastWasOrca)
        {
            if (useOrca) { if (_dispatcherReady) _dispatcher.Interrupt(); }
            else _orca.Interrupt();
            _lastWasOrca = useOrca;
            Serilog.Log.Information("Speech now goes through {Backend}.", useOrca ? _orca.BackendName : _dispatcher.BackendName);
        }
        return useOrca ? _orca : _dispatcher;
    }

    public void Speak(string text, bool interrupt = true)
    {
        // Every spoken line in the log, so "what said that?" has an answer after a session.
        Serilog.Log.Information("[SAY] {Text}", text);
        Current().Speak(text, interrupt);
    }

    public void Interrupt() => Current().Interrupt();

    public void Dispose()
    {
        _orca.Dispose();
        _dispatcher.Dispose();
    }
}

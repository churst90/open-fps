using System;
using OpenFPS.Client.Core.Platform;

namespace OpenFPS.Client.Gtk.Platform;

/// <summary>
/// Whichever of the two is right at this moment: Orca when it is running, speech-dispatcher when it
/// is not.
///
/// Decided per utterance rather than once at startup, because a screen reader is not a property of
/// the installation, it is a thing a person turns on and off. Someone who launches the game, plays
/// for an hour and then starts Orca should be heard by it from the next line, and someone who quits
/// Orca mid-session should not go silent.
///
/// Both backends are held open the whole time. Keeping the speech-dispatcher connection alive while
/// Orca has the floor costs one idle socket and removes the only thing that could make the handover
/// audible — a connection being established at the moment something needs saying.
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
    /// The handover has to be CLEAN, not just correct.
    ///
    /// Switching backends mid-sentence leaves the old one still talking — speech-dispatcher does not
    /// know that Orca has taken over, and Orca does not know there is a sentence in the air. So the
    /// one being left is silenced on the way out, once, on the change rather than on every line.
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

    public void Speak(string text, bool interrupt = true) => Current().Speak(text, interrupt);

    public void Interrupt() => Current().Interrupt();

    public void Dispose()
    {
        _orca.Dispose();
        _dispatcher.Dispose();
    }
}

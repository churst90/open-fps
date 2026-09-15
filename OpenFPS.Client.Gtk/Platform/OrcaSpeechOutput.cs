using System;
using System.Threading;
using System.Threading.Tasks;
using OpenFPS.Client.Core.Platform;

namespace OpenFPS.Client.Gtk.Platform;

/// <summary>
/// Speech through Orca itself, rather than past it.
///
/// speech-dispatcher is the right layer for a game that is the only thing talking. It is the wrong
/// one when a screen reader is running, because then there are TWO clients on one synthesiser with
/// no shared idea of what is being said: the game's line and Orca's line arrive as separate requests,
/// each cancels the other's, and which one survives depends on timing. What a listener gets is half a
/// sentence from one and half from the other. Nothing is misconfigured; there is simply nobody
/// arbitrating.
///
/// Orca 47 and later expose a session-bus service that makes it the arbiter. A message sent to
/// <c>PresentMessage</c> goes through Orca's own queue, in Orca's voice, at Orca's rate, with Orca's
/// punctuation settings, and to Orca's braille display — so the game sounds like the rest of the
/// desktop and interrupts itself the way everything else does.
///
/// D-Bus comes from the GIO that GTK has already loaded, so this costs no new dependency.
/// </summary>
public sealed class OrcaSpeechOutput : ISpeechOutput
{
    private const string BusName = "org.gnome.Orca1.Service";
    private const string ServicePath = "/org/gnome/Orca1/Service";
    private const string ServiceIface = "org.gnome.Orca1.Service";
    private const string SpeechPath = "/org/gnome/Orca1/Service/SpeechManager";
    private const string SpeechIface = "org.gnome.Orca1.SpeechManager";

    /// <summary>How long a probe may take before Orca counts as absent, milliseconds. Generous —
    /// Orca is a Python process and can be busy speaking — but bounded, because this is asked from a
    /// background timer that must not wedge on a screen reader that has hung.</summary>
    private const int ProbeTimeoutMs = 1500;

    /// <summary>How often availability is re-checked. Orca can be started or stopped at any moment
    /// and the game has to follow it without being restarted, so this is polled rather than decided
    /// once at startup.</summary>
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(2);

    private Gio.DBusConnection? _bus;
    private volatile bool _available;
    private Timer? _probe;
    private volatile string _version = "";
    private bool _reportedFailure;

    /// <summary>True when Orca is answering on the session bus RIGHT NOW.</summary>
    public bool IsAvailable => _available;

    public string BackendName => _available ? $"Orca {_version}".TrimEnd() : "Orca (not running)";

    public bool Initialize()
    {
        try
        {
            // GirCore installs the native library resolver for a namespace in its Module.Initialize,
            // and until that has run every P/Invoke into Gio fails with "Unable to load shared
            // library 'Gio'". Speech is set up before the GTK application exists — deliberately, so
            // that anything going wrong during startup can still be spoken — so this cannot wait for
            // Gtk.Module.Initialize to do it. It is idempotent.
            Gio.Module.Initialize();
            _bus = Gio.Functions.BusGetSync(Gio.BusType.Session, null);
        }
        catch (Exception ex)
        {
            Serilog.Log.Information("Orca speech: no session bus ({Message}); speech-dispatcher will be used.", ex.Message);
            return false;
        }

        Probe(null);
        // Kept running whatever the first probe said: "Orca is not running" is a fact about this
        // second, not about the session, and a player who turns their screen reader on mid-game
        // should not have to restart to be heard by it.
        _probe = new Timer(Probe, null, ProbeInterval, ProbeInterval);
        return _available;
    }

    private void Probe(object? _)
    {
        if (_bus == null) return;
        try
        {
            var reply = _bus.CallSync(BusName, ServicePath, ServiceIface, "GetVersion",
                                      null, null, Gio.DBusCallFlags.NoAutoStart, ProbeTimeoutMs, null);
            string v = reply?.GetChildValue(0).GetString(out nuint _) ?? "";
            if (!_available) Serilog.Log.Information("Orca {Version} is on the session bus; speech will go through it.", v);
            _version = v;
            _available = true;
        }
        catch (Exception ex)
        {
            // Orca not running is a perfectly ordinary state and not worth a line every two seconds.
            // But "we could not reach Orca" and "Orca is not there" look identical from here, and only
            // one of them is a bug — so the FIRST failure says what it actually was, once. A
            // degradation nobody is told about is a bug that never gets fixed.
            if (_available) Serilog.Log.Information("Orca has left the session bus; speech falls back to speech-dispatcher.");
            else if (!_reportedFailure)
            {
                _reportedFailure = true;
                Serilog.Log.Information("Orca is not answering on the session bus ({Message}); speech-dispatcher will be used "
                                      + "until it does.", ex.Message);
            }
            _available = false;
        }
    }

    public void Speak(string text, bool interrupt = true)
    {
        if (!_available || _bus == null || string.IsNullOrWhiteSpace(text)) return;

        // Interrupt first, then present. Orca's own presentation rules decide the rest — which is the
        // point of routing through it: the game stops being a second opinion about what to say next.
        if (interrupt) Interrupt();
        Fire(ServicePath, ServiceIface, "PresentMessage",
             GLib.Variant.NewTuple(new[] { GLib.Variant.NewString(text) }));
    }

    public void Interrupt()
    {
        if (!_available || _bus == null) return;
        Fire(SpeechPath, SpeechIface, "InterruptSpeech",
             GLib.Variant.NewTuple(new[] { GLib.Variant.NewBoolean(false) }));
    }

    /// <summary>
    /// Sends and does not wait.
    ///
    /// A blocking call here would put a round trip to another process on whatever thread the game
    /// happens to say something from — including the one placing sounds. Speech that arrives a
    /// millisecond late is not a problem; a frame that stalls waiting for a screen reader is.
    /// </summary>
    private void Fire(string path, string iface, string method, GLib.Variant args)
    {
        try
        {
            _ = _bus!.CallAsync(BusName, path, iface, method, args)
                     .ContinueWith(t =>
                     {
                         if (t.IsFaulted) _available = false;   // the next probe decides whether it comes back
                     }, TaskContinuationOptions.ExecuteSynchronously);
        }
        catch (Exception)
        {
            _available = false;
        }
    }

    public void Dispose()
    {
        _probe?.Dispose();
        _probe = null;
        _bus = null;
        _available = false;
    }
}

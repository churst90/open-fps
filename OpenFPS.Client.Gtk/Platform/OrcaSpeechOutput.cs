using OpenFPS.Client.Core.Platform;

namespace OpenFPS.Client.Gtk.Platform;

/// <summary>
/// Speech through Orca's own queue (its session-bus <c>PresentMessage</c>, Orca 47 and later), in its
/// voice and settings and to its braille display. Beside a running screen reader, speech-dispatcher is
/// a second client on one synthesiser, and the two cancel each other's lines. D-Bus comes from the GIO
/// GTK has already loaded.
/// </summary>
public sealed class OrcaSpeechOutput : ISpeechOutput
{
    private const string BusName = "org.gnome.Orca1.Service";
    private const string ServicePath = "/org/gnome/Orca1/Service";
    private const string ServiceIface = "org.gnome.Orca1.Service";
    private const string SpeechPath = "/org/gnome/Orca1/Service/SpeechManager";
    private const string SpeechIface = "org.gnome.Orca1.SpeechManager";

    /// <summary>How long a probe may take before Orca counts as absent, milliseconds: generous (Orca can
    /// be busy speaking) but bounded, so the timer cannot wedge on a hung screen reader.</summary>
    private const int ProbeTimeoutMs = 1500;

    /// <summary>How often availability is checked again: Orca can be started or stopped at any moment.</summary>
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(2);

    private Gio.DBusConnection? _bus;
    private volatile bool _available;
    private Timer? _probe;
    private volatile string _version = "";
    private bool _reportedFailure;

    /// <summary>True when Orca is answering on the session bus now.</summary>
    public bool IsAvailable => _available;

    public string BackendName => _available ? $"Orca {_version}".TrimEnd() : "Orca (not running)";

    public bool Initialize()
    {
        try
        {
            // Speech starts before GTK, so Gio's library resolver is installed here: without it every
            // P/Invoke into Gio fails with "Unable to load shared library 'Gio'". Idempotent.
            Gio.Module.Initialize();
            _bus = Gio.Functions.BusGetSync(Gio.BusType.Session, null);
        }
        catch (Exception ex)
        {
            Serilog.Log.Information("Orca speech: no session bus ({Message}); speech-dispatcher will be used.", ex.Message);
            return false;
        }

        Probe(null);
        // Kept running whatever the first probe said: Orca can be turned on mid-game.
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
            // The first failure is logged with its reason, once: "could not reach Orca" and "Orca is
            // not there" look the same from here, and only one is a bug.
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

    /// <summary>Sends and does not wait: speech is said from any thread, the one placing sounds
    /// included, and must not stall it on a round trip to another process.</summary>
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

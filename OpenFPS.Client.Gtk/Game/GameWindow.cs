using Gtk;
using OpenFPS.Client.Core.Input;

namespace OpenFPS.Client.Gtk.Game;

/// <summary>
/// The in-game GTK window. It holds keyboard focus and forwards raw GTK key events into the shared
/// <see cref="InputStateBuffer"/> (mapped to neutral <c>GameKey</c>s by <see cref="GtkKeyMap"/>).
/// It deliberately renders no game visuals — feedback is entirely audio + speech — so the window is
/// just a focus target that exposes itself to Orca via AT-SPI.
/// </summary>
internal sealed class GameWindow
{
    private readonly InputStateBuffer _input;
    private readonly System.Action _onClose;
    private ApplicationWindow? _window;
    private Widget? _focusTarget;

    /// <summary>True while this window is the active one — the GTK head's answer to
    /// <c>IClientShell.IsGameInputActive</c>.</summary>
    public bool IsActive { get; private set; }

    public GameWindow(InputStateBuffer input, System.Action onClose)
    {
        _input = input;
        _onClose = onClose;
    }

    private volatile int _numLock = -1;   // -1 unknown, 0 off, 1 on

    /// <summary>Whether Num Lock was on at the last key press in this window, or null before any.</summary>
    public bool? NumLockOn => _numLock < 0 ? null : _numLock == 1;

    /// <summary>
    /// Reads Num Lock on the UI thread, where GDK may be asked, at every key press: the keyboard
    /// device's own lock state, and failing that what the keypad key itself says (a keypad digit is
    /// only reported with Num Lock on, a keypad Home or Up only with it off).
    /// </summary>
    private void NoteNumLock(uint keyval)
    {
        try
        {
            var keyboard = Gdk.Display.GetDefault()?.GetDefaultSeat()?.GetKeyboard();
            if (keyboard != null) { _numLock = keyboard.GetNumLockState() ? 1 : 0; return; }
        }
        catch (System.Exception) { /* fall back to the keyval below */ }
        int byKey = GtkKeyMap.NumLockFromKeyval(keyval);
        if (byKey >= 0) _numLock = byKey;
    }

    /// <summary>
    /// What a physical key types with nothing held, in the first layout, or 0 if GDK cannot say: so
    /// Shift and comma is the comma KEY with Shift, whatever character the layout puts on it.
    /// </summary>
    private static uint Unshifted(uint keycode)
    {
        if (_noTranslate) return 0;
        try
        {
            var display = gdk_display_get_default();
            if (display != System.IntPtr.Zero
                && gdk_display_translate_key(display, keycode, 0, 0, out uint keyval, out _, out _, out _))
                return keyval;
        }
        catch (System.Exception ex) when (ex is System.DllNotFoundException or System.EntryPointNotFoundException)
        {
            _noTranslate = true;   // asked once; the reported character is used from then on
        }
        return 0;
    }

    private static bool _noTranslate;

    // gir.core 0.6 does not expose gdk_display_translate_key, so it is called directly. GTK 4 is
    // already loaded by the time a key arrives.
    [System.Runtime.InteropServices.DllImport("libgtk-4.so.1")]
    private static extern System.IntPtr gdk_display_get_default();

    [System.Runtime.InteropServices.DllImport("libgtk-4.so.1")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool gdk_display_translate_key(System.IntPtr display, uint keycode, uint state, int group,
                                                         out uint keyval, out int effectiveGroup, out int level, out uint consumed);

    /// <summary>
    /// Brings the in-game window up, building it the first time only. Built on every call, it left a
    /// window behind per spawn, and the server answers every <c>/tp</c> with a spawn.
    /// </summary>
    public void Present(Application app)
    {
        if (_window != null)
        {
            _window.Present();
            IsActive = true;
            _focusTarget?.GrabFocus();
            return;
        }

        var window = ApplicationWindow.New(app);
        _window = window;
        _window.Title = "OpenFPS — In Game";
        _window.SetDefaultSize(480, 320);
        Scene.Apply(_window);

        var label = Label.New(OpenFPS.Client.Core.Session.ClientGameSession.KeyHelp);
        label.SetWrap(true);
        // A focusable child gives the toplevel a focus target, so key events keep being delivered —
        // notably after alt-tabbing away and back, when GTK would otherwise leave no widget focused
        // and stop routing keys to the window's controller.
        label.SetFocusable(true);
        _window.SetChild(label);
        _focusTarget = label;

        var keys = EventControllerKey.New();
        // Capture phase: key events at the window, whichever child has focus.
        keys.SetPropagationPhase(PropagationPhase.Capture);
        keys.OnKeyPressed += (_, e) =>
        {
            NoteNumLock(e.Keyval);
            _input.SetKey(GtkKeyMap.Map(e.Keyval, Unshifted(e.Keycode)), true);
            return false; // don't consume — keep AT-SPI / default handling alive
        };
        keys.OnKeyReleased += (_, e) =>
        {
            _input.SetKey(GtkKeyMap.Map(e.Keyval, Unshifted(e.Keycode)), false);
        };
        _window.AddController(keys);

        // Drop any held keys if focus leaves the window so movement doesn't "stick" down; re-grab the
        // focus target when the window regains focus (alt-tab back) so keys are routed again.
        var focus = EventControllerFocus.New();
        focus.OnLeave += (_, _) => { IsActive = false; _input.Clear(); };
        focus.OnEnter += (_, _) => { IsActive = true; _input.Clear(); _focusTarget?.GrabFocus(); };
        _window.AddController(focus);

        // Alt-tab is reported by the window's state flags (BACKDROP), not EventControllerFocus. On
        // reactivation the held keys must be cleared: Alt+Tab's Alt goes up while the window is away,
        // leaves Alt held, and a held modifier stops all movement.
        _window.OnStateFlagsChanged += (_, _) =>
        {
            IsActive = !window.GetStateFlags().HasFlag(StateFlags.Backdrop);
            if (IsActive)
            {
                _input.Clear();
                _focusTarget?.GrabFocus();
            }
        };

        // Closing it quits the app (the hidden menu window would keep the process alive), and says so in
        // the log, so a closed window can be told from a killed process.
        _window.OnCloseRequest += (_, _) =>
        {
            Serilog.Log.Information("Game window received a close request.");
            _onClose();
            return false;
        };

        _window.Present();
        IsActive = true;
        _focusTarget?.GrabFocus();
    }

    /// <summary>Hides the window for a return to the main menu. <see cref="Present"/> brings it back.</summary>
    public void Hide()
    {
        IsActive = false;
        _input.Clear();
        _window?.SetVisible(false);
    }

    /// <summary>The toplevel, so dialogs can be made transient for it.</summary>
    public Window? Toplevel => _window;
}

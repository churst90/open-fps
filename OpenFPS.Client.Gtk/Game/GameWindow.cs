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
    /// Brings the in-game window up, building it the first time and only the first time.
    ///
    /// The shell keeps ONE GameWindow for the life of the session, but that was only half of it:
    /// this method built a fresh <see cref="ApplicationWindow"/> on every call and dropped the old
    /// one into the field, so the previous toplevel stayed mapped and stayed owned by the
    /// Application — a window per spawn, and the server answers every <c>/tp</c> with one. Found by
    /// alt-tab: a stack of "OpenFPS — In Game" windows behind the live one.
    ///
    /// Made once; afterwards Present only raises it and puts the focus back on the label.
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

        var label = Label.New(OpenFPS.Client.Core.Session.ClientGameSession.KeyHelp);
        label.SetWrap(true);
        // A focusable child gives the toplevel a focus target, so key events keep being delivered —
        // notably after alt-tabbing away and back, when GTK would otherwise leave no widget focused
        // and stop routing keys to the window's controller.
        label.SetFocusable(true);
        _window.SetChild(label);
        _focusTarget = label;

        var keys = EventControllerKey.New();
        // Capture phase: receive key events at the window level regardless of which (if any) child
        // widget has focus.
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

        // EventControllerFocus only tracks focus moving WITHIN the app; alt-tab is a window-manager
        // activation that GTK reports via the window's state flags (BACKDROP clears when active).
        // On reactivation: CLEAR the held-key buffer and re-grab focus. The clear is critical — the
        // Alt of an Alt+Tab chord registers key-down while focused but its key-up arrives while the
        // window is unfocused, leaving Alt stuck "held". Movement is suppressed whenever a modifier is
        // held, so without this only the non-movement tap keys would respond.
        _window.OnStateFlagsChanged += (_, _) =>
        {
            IsActive = !window.GetStateFlags().HasFlag(StateFlags.Backdrop);
            if (IsActive)
            {
                _input.Clear();
                _focusTarget?.GrabFocus();
            }
        };

        // Closing the in-game window quits the whole app (the menu window is only hidden, so it
        // would otherwise keep the process — and its audio thread — alive).
        // Logged, because a window closing is the one thing that ends the process without a crash,
        // and nothing said when it happened. "It stopped" needs to distinguish this from a signal.
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

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
    private ApplicationWindow _window = null!;
    private Widget? _focusTarget;

    /// <summary>True while this window is the active one — the GTK head's answer to
    /// <c>IClientShell.IsGameInputActive</c>.</summary>
    public bool IsActive { get; private set; }

    public GameWindow(InputStateBuffer input, System.Action onClose)
    {
        _input = input;
        _onClose = onClose;
    }

    public void Present(Application app)
    {
        _window = ApplicationWindow.New(app);
        _window.Title = "OpenFPS — In Game";
        _window.SetDefaultSize(480, 320);

        var label = Label.New(
            "In game. W A S D to move, J / L turn, K / O look up/down, Space jump.\n" +
            "C coordinates, F facing, H health, Z area, comma look ahead, E interact, P scan, I inventory.\n" +
            "V voice, F5 players, brackets to read chat, slash for the command console, Escape to quit.");
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
            _input.SetKey(GtkKeyMap.Map(e.Keyval), true);
            return false; // don't consume — keep AT-SPI / default handling alive
        };
        keys.OnKeyReleased += (_, e) =>
        {
            _input.SetKey(GtkKeyMap.Map(e.Keyval), false);
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
            IsActive = !_window.GetStateFlags().HasFlag(StateFlags.Backdrop);
            if (IsActive)
            {
                _input.Clear();
                _focusTarget?.GrabFocus();
            }
        };

        // Closing the in-game window quits the whole app (the menu window is only hidden, so it
        // would otherwise keep the process — and its audio thread — alive).
        _window.OnCloseRequest += (_, _) => { _onClose(); return false; };

        _window.Present();
        IsActive = true;
        _focusTarget?.GrabFocus();
    }

    /// <summary>The toplevel, so dialogs can be made transient for it.</summary>
    public Window? Toplevel => _window;
}

using Gtk;
using OpenFPS.Client.Core.Platform;

namespace OpenFPS.Client.Gtk.Game;

/// <summary>
/// The in-game GTK window. It holds keyboard focus and forwards raw GTK key events into the
/// <see cref="GameSession"/>'s thread-safe input buffer (mapped to neutral <see cref="GameKey"/>s).
/// It deliberately renders no game visuals — feedback is entirely audio + speech — so the window is
/// just a focus target that exposes itself to Orca via AT-SPI.
/// </summary>
internal sealed class GameWindow
{
    private readonly GameSession _session;
    private readonly ISpeechOutput _speech;
    private readonly System.Action _onClose;
    private ApplicationWindow _window = null!;
    private Widget? _focusTarget;

    public GameWindow(GameSession session, ISpeechOutput speech, System.Action onClose)
    {
        _session = session;
        _speech = speech;
        _onClose = onClose;
    }

    public void Present(Application app)
    {
        _window = ApplicationWindow.New(app);
        _window.Title = "OpenFPS — In Game";
        _window.SetDefaultSize(480, 320);

        var label = Label.New(
            "In game. W A S D to move, J / L turn, K / O look up/down, Space jump.\n" +
            "C coordinates, F facing, H health, Z area, comma look ahead, E interact.");
        label.SetWrap(true);
        // A focusable child gives the toplevel a focus target, so key events keep being delivered —
        // notably after alt-tabbing away and back, when GTK would otherwise leave no widget focused
        // and stop routing keys to the window's controller.
        label.SetFocusable(true);
        _window.SetChild(label);
        _focusTarget = label;

        var keys = EventControllerKey.New();
        // Capture phase: receive key events at the window level regardless of which (if any) child
        // widget has focus — the window holds only a non-focusable label, so a bubble-phase
        // controller would never see keys.
        keys.SetPropagationPhase(PropagationPhase.Capture);
        keys.OnKeyPressed += (_, e) =>
        {
            _session.Input.SetKey(GtkKeyMap.Map(e.Keyval), true);
            return false; // don't consume — keep AT-SPI / default handling alive
        };
        keys.OnKeyReleased += (_, e) =>
        {
            _session.Input.SetKey(GtkKeyMap.Map(e.Keyval), false);
        };
        _window.AddController(keys);

        // Drop any held keys if focus leaves the window so movement doesn't "stick" down; re-grab the
        // focus target when the window regains focus (alt-tab back) so keys are routed again.
        var focus = EventControllerFocus.New();
        focus.OnLeave += (_, _) => _session.Input.Clear();
        focus.OnEnter += (_, _) => { _session.Input.Clear(); _focusTarget?.GrabFocus(); };
        _window.AddController(focus);

        // EventControllerFocus only tracks focus moving WITHIN the app; alt-tab is a window-manager
        // activation that GTK reports via the window's state flags (BACKDROP clears when active).
        // On reactivation: CLEAR the held-key buffer and re-grab focus. The clear is critical — the
        // Alt of an Alt+Tab chord registers key-down while focused but its key-up arrives while the
        // window is unfocused, leaving Alt stuck "held". GatherInput suppresses movement whenever a
        // modifier is held, so without this only the non-movement tap keys (C/F/P) would respond.
        _window.OnStateFlagsChanged += (_, _) =>
        {
            if (!_window.GetStateFlags().HasFlag(StateFlags.Backdrop))
            {
                _session.Input.Clear();
                _focusTarget?.GrabFocus();
            }
        };

        // Closing the in-game window quits the whole app (the menu window is only hidden, so it
        // would otherwise keep the process — and its audio thread — alive).
        _window.OnCloseRequest += (_, _) => { _onClose(); return false; };

        _window.Present();
        _focusTarget?.GrabFocus();
    }
}

using Gtk;
using OpenFPS.Client.Core.Session;

namespace OpenFPS.Client.Gtk.Game;

/// <summary>
/// The connecting window: while a connect is under way it holds focus over whatever window started it,
/// so the menu's arrows and Tab move nothing (Cody, 2026-10-11), and Escape stops the connect.
/// </summary>
internal sealed partial class GtkClientShell
{
    private Window? _hold;
    private Label? _holdLabel;
    private Window? _holdParent;
    private Action? _holdCancel;
    // Escape is down: a held key repeats, and a repeat landing on the window behind once the hold has
    // gone would close a connect form with the password typed in it. So the hold stays until the key is up.
    private bool _holdEscapeDown;
    private bool _holdEndPending;
    // Ended by a login: the loading screen or the main menu takes focus first, then the hold goes, so
    // the menu behind does not get focus for a moment and say its item.
    private bool _holdCloseOnNextWindow;
    private bool _holdClosingOurselves;

    /// <summary>Set by the program: the next menu item to take focus is said after what was said, not
    /// over it. Raised as the hold gives focus back.</summary>
    public Action? FocusReturning { get; set; }

    public void ShowConnecting(string status, Action cancel) => OnUi(() =>
    {
        _holdCancel = cancel;
        _holdEndPending = false;
        _holdCloseOnNextWindow = false;
        if (_hold != null) { _holdLabel?.SetText(status); return; }

        _holdParent = _app?.GetActiveWindow() ?? _menuWindow;
        var window = Window.New();
        window.Title = "Connecting";
        window.SetModal(true);
        window.SetDefaultSize(420, 120);
        if (_holdParent != null) window.SetTransientFor(_holdParent);

        var box = Box.New(Orientation.Vertical, 8);
        box.MarginTop = box.MarginBottom = box.MarginStart = box.MarginEnd = 16;
        // Focusable so Orca lands on it and reads it, as on the loading screen. The session has said it.
        _holdLabel = Label.New(status);
        _holdLabel.SetWrap(true);
        _holdLabel.SetFocusable(true);
        box.Append(_holdLabel);
        window.SetChild(box);

        var keys = EventControllerKey.New();
        keys.SetPropagationPhase(PropagationPhase.Capture);
        keys.OnKeyPressed += (_, e) =>
        {
            // Shift+Tab arrives as ISO_Left_Tab, which the game does not map: it is Tab all the same.
            if (e.Keyval == GdkIsoLeftTab) return true;
            switch (ConnectHold.Decide(GtkKeyMap.Map(e.Keyval)))
            {
                case HoldKey.Cancel:
                    if (!_holdEscapeDown) { _holdEscapeDown = true; _holdCancel?.Invoke(); }
                    return true;
                case HoldKey.Hold:
                    return true;
                default:
                    return false;
            }
        };
        keys.OnKeyReleased += (_, e) =>
        {
            if (e.Keyval != GdkEscape) return;
            _holdEscapeDown = false;
            if (_holdEndPending) CloseHold(returnFocus: true);
        };
        window.AddController(keys);
        // Closed by the window manager: the same as Escape.
        window.OnCloseRequest += (_, _) =>
        {
            if (!_holdClosingOurselves && ReferenceEquals(window, _hold))
            {
                _hold = null;
                _holdCancel?.Invoke();
            }
            return false;
        };

        _hold = window;
        _holdEscapeDown = false;
        window.Present();
        _holdLabel.GrabFocus();
    });

    public void EndConnecting(bool returnFocus) => OnUi(() =>
    {
        if (_hold == null) return;
        if (!returnFocus) { _holdCloseOnNextWindow = true; return; }
        if (_holdEscapeDown)
        {
            _holdEndPending = true;
            // A release that never arrives (focus taken by another program) must not leave it up.
            Task.Delay(1500).ContinueWith(_ => OnUi(() => { if (_holdEndPending) CloseHold(returnFocus: true); }));
            return;
        }
        CloseHold(returnFocus: true);
    });

    /// <summary>The hold goes, if a login ended it, once the window that follows has been put up.</summary>
    private void CloseHoldAfterNextWindow()
    {
        if (_holdCloseOnNextWindow) CloseHold(returnFocus: false);
    }

    private void CloseHold(bool returnFocus)
    {
        var window = _hold;
        var parent = _holdParent;
        _hold = null;
        _holdLabel = null;
        _holdParent = null;
        _holdEndPending = false;
        _holdCloseOnNextWindow = false;
        _holdEscapeDown = false;
        if (window == null) return;
        _holdClosingOurselves = true;
        try { window.Close(); }
        finally { _holdClosingOurselves = false; }
        if (!returnFocus || parent == null || !parent.GetVisible()) return;
        FocusReturning?.Invoke();
        parent.Present();
    }
}

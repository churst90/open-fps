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
    private ApplicationWindow _window = null!;

    public GameWindow(GameSession session, ISpeechOutput speech)
    {
        _session = session;
        _speech = speech;
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
        _window.SetChild(label);

        var keys = EventControllerKey.New();
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

        // Drop any held keys if focus leaves the window so movement doesn't "stick" down.
        var focus = EventControllerFocus.New();
        focus.OnLeave += (_, _) => _session.Input.Clear();
        _window.AddController(focus);

        _window.Present();
    }
}

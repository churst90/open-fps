using System;
using System.Threading;
using Gtk;
using OpenFPS.Client.Core.Input;
using OpenFPS.Client.Core.Platform;

namespace OpenFPS.Client.Gtk.Game;

/// <summary>
/// The Linux half of <see cref="IClientShell"/>: GTK windows for the loading screen, the in-game
/// focus target, the command console, and the quit confirmation.
///
/// The shared session calls every one of these from the game-loop thread, so each one marshals onto
/// the GTK main thread through the captured <see cref="SynchronizationContext"/> before touching a
/// widget. GTK windows expose themselves to Orca over AT-SPI, which is why the console and the quit
/// dialog are real windows rather than spoken prompts.
/// </summary>
internal sealed class GtkClientShell : IClientShell
{
    private readonly ISpeechOutput _speech;
    private readonly Action _onQuit;

    // The session creates the input buffer and needs the shell to construct, so the buffer arrives a
    // moment later via SetInput. The shell only ever uses it to clear held keys around modal dialogs,
    // which cannot happen before the player is in the world.
    private InputStateBuffer _input = new();

    private Application? _app;
    private SynchronizationContext? _ui;
    private Window? _menuWindow;

    private Window? _loadingWindow;
    private Label? _loadingLabel;
    private GameWindow? _gameWindow;
    private bool _consoleOpen;
    private int _lastSpokenDecile = -1;

    public event Action<string>? CommandEntered;

    public GtkClientShell(ISpeechOutput speech, Action onQuit)
    {
        _speech = speech;
        _onQuit = onQuit;
    }

    /// <summary>Hands the shell the session's input buffer, immediately after the session is built.</summary>
    public void SetInput(InputStateBuffer input) => _input = input;

    /// <summary>Called from <c>OnActivate</c>, once GTK has a main loop and a synchronization context.</summary>
    public void AttachToApplication(Application app, SynchronizationContext? uiContext, Window menuWindow)
    {
        _app = app;
        _ui = uiContext;
        _menuWindow = menuWindow;
    }

    /// <summary>Gameplay keys are live only while the in-game window is the active one and no modal
    /// text entry is open — otherwise the player would walk while typing.</summary>
    public bool IsGameInputActive => _gameWindow is { IsActive: true } && !_consoleOpen;

    // ── IClientShell ────────────────────────────────────────────────────────────

    public void ShowLoading(string status) => OnUi(() =>
    {
        _lastSpokenDecile = -1;
        if (_loadingWindow == null)
        {
            _loadingWindow = Window.New();
            _loadingWindow.Title = "OpenFPS — Loading";
            _loadingWindow.SetDefaultSize(420, 140);
            if (_menuWindow != null) _loadingWindow.SetTransientFor(_menuWindow);

            var box = Box.New(Orientation.Vertical, 8);
            box.MarginTop = box.MarginBottom = box.MarginStart = box.MarginEnd = 16;
            _loadingLabel = Label.New(status);
            _loadingLabel.SetWrap(true);
            // Focusable so Orca lands on it and reads the status as it changes.
            _loadingLabel.SetFocusable(true);
            box.Append(_loadingLabel);
            _loadingWindow.SetChild(box);
        }
        else
        {
            _loadingLabel?.SetText(status);
        }

        _loadingWindow.Present();
        _speech.Speak(status, interrupt: true);
    });

    public void UpdateLoadingStatus(string text, int percent) => OnUi(() =>
    {
        string line = percent > 0 ? $"{text} {percent} percent." : text;
        _loadingLabel?.SetText(line);

        // Speak at each 25% step rather than on every update: a map with a thousand entities produces a
        // thousand of these, and a screen reader reading all of them says nothing at all.
        int decile = percent / 25;
        if (decile != _lastSpokenDecile || percent >= 100)
        {
            _lastSpokenDecile = decile;
            _speech.Speak(line, interrupt: false);
        }
    });

    public void EnterGame() => OnUi(() =>
    {
        if (_app == null) return;

        // Hide (don't close) the menu: closing it disrupts the new window's keyboard focus so the game
        // window stops receiving key events. The game window quits the whole app on close, so the
        // hidden menu won't keep the process alive.
        _gameWindow = new GameWindow(_input, _onQuit);
        _gameWindow.Present(_app);

        _loadingWindow?.SetVisible(false);
        _menuWindow?.SetVisible(false);
    });

    public void OpenCommandConsole() => OnUi(() =>
    {
        if (_consoleOpen) return;
        _consoleOpen = true;
        // Clear held keys: the slash that opened this is still down, and its key-up will be delivered
        // to the dialog, not the game window.
        _input.Clear();

        var dialog = Window.New();
        dialog.Title = "Command";
        dialog.SetModal(true);
        dialog.SetDefaultSize(420, 140);
        if (_gameWindow?.Toplevel != null) dialog.SetTransientFor(_gameWindow.Toplevel);

        var box = Box.New(Orientation.Vertical, 8);
        box.MarginTop = box.MarginBottom = box.MarginStart = box.MarginEnd = 16;
        box.Append(Label.New("Type a command (starting with /) or a chat message, then press Enter."));

        var entry = Entry.New();
        entry.SetActivatesDefault(false);
        box.Append(entry);

        void Commit()
        {
            string text = entry.GetText();
            dialog.Close();
            if (!string.IsNullOrWhiteSpace(text)) CommandEntered?.Invoke(text);
        }

        entry.OnActivate += (_, _) => Commit();

        var send = Button.NewWithLabel("Send");
        send.OnClicked += (_, _) => Commit();
        box.Append(send);

        var cancel = Button.NewWithLabel("Cancel");
        cancel.OnClicked += (_, _) => dialog.Close();
        box.Append(cancel);

        dialog.OnCloseRequest += (_, _) =>
        {
            _consoleOpen = false;
            _input.Clear();
            return false;
        };

        dialog.SetChild(box);
        dialog.Present();
        entry.GrabFocus();
        _speech.Speak("Command entry. Type a command or message, then press Enter.", interrupt: true);
    });

    public void RequestQuit() => OnUi(() =>
    {
        if (_consoleOpen) return;
        _consoleOpen = true; // reuse the modal guard: gameplay keys pause while the prompt is up
        _input.Clear();

        var dialog = Window.New();
        dialog.Title = "Quit";
        dialog.SetModal(true);
        dialog.SetDefaultSize(320, 120);
        if (_gameWindow?.Toplevel != null) dialog.SetTransientFor(_gameWindow.Toplevel);

        var box = Box.New(Orientation.Vertical, 8);
        box.MarginTop = box.MarginBottom = box.MarginStart = box.MarginEnd = 16;
        box.Append(Label.New("Quit OpenFPS?"));

        var yes = Button.NewWithLabel("Quit");
        yes.OnClicked += (_, _) => { dialog.Close(); _onQuit(); };
        box.Append(yes);

        var no = Button.NewWithLabel("Keep playing");
        no.OnClicked += (_, _) => dialog.Close();
        box.Append(no);

        dialog.OnCloseRequest += (_, _) => { _consoleOpen = false; _input.Clear(); return false; };

        dialog.SetChild(box);
        dialog.Present();
        no.GrabFocus();
        _speech.Speak("Quit OpenFPS? Tab to choose, Enter to confirm.", interrupt: true);
    });

    // ── Thread marshaling ───────────────────────────────────────────────────────

    private void OnUi(Action action)
    {
        if (_ui != null) _ui.Post(_ => Safe(action), null);
        else Safe(action);
    }

    private static void Safe(Action action)
    {
        try { action(); }
        catch (Exception ex) { Serilog.Log.Error(ex, "GTK shell action failed."); }
    }
}

using Gtk;
using OpenFPS.Client.Core;
using OpenFPS.Client.Core.Input;
using OpenFPS.Client.Core.Platform;

namespace OpenFPS.Client.Gtk.Game;

/// <summary>
/// The Linux half of <see cref="IClientShell"/>: GTK windows for the loading screen, the in-game
/// focus target, the command console, and the game menu.
///
/// The session calls these from the game-loop thread, so each marshals onto the GTK main thread before
/// touching a widget. The console and the quit dialog are real windows so Orca can read them.
/// </summary>
internal sealed class GtkClientShell : IClientShell
{
    private readonly ISpeechOutput _speech;
    private readonly Action _onQuit;
    private readonly Action<UiCue> _cue;

    // Set by SetInput just after the session is built; used only to clear held keys around modal
    // dialogs, which cannot open before then.
    private InputStateBuffer _input = new();

    private Application? _app;
    private SynchronizationContext? _ui;
    private Window? _menuWindow;

    private Window? _loadingWindow;
    private Label? _loadingLabel;
    private ProgressBar? _loadingBar;
    private GameWindow? _gameWindow;
    private bool _consoleOpen;
    // Whatever modal is up over the game window (console or game menu), so leaving can close it.
    private Window? _modal;

    public event Action<string>? CommandEntered;

    public GtkClientShell(ISpeechOutput speech, Action onQuit, Action<UiCue> onCue)
    {
        _speech = speech;
        _onQuit = onQuit;
        _cue = onCue;
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

    public bool? NumLockOn => _gameWindow?.NumLockOn;

    // ── IClientShell ────────────────────────────────────────────────────────────

    public void ShowLoading(string status, bool speak = true) => OnUi(() =>
    {
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
            // The same progress the Windows head shows; the loading tone says it without looking.
            _loadingBar = ProgressBar.New();
            _loadingBar.SetShowText(true);
            box.Append(_loadingBar);
            _loadingWindow.SetChild(box);
        }
        else
        {
            _loadingLabel?.SetText(status);
        }
        _loadingBar?.SetFraction(0);

        _loadingWindow.Present();
        if (speak) _speech.Speak(status, interrupt: true);
    });

    public void UpdateLoadingStatus(string text, int percent) => OnUi(() =>
    {
        string line = percent > 0 ? $"{text} {percent} percent." : text;
        _loadingLabel?.SetText(line);
        _loadingBar?.SetFraction(Math.Clamp(percent, 0, 100) / 100.0);

        // Shown, not spoken: preloading this, receiving that. A player needs to hear that they are in
        // and where (ClientGameSession, on arriving), not the loading steps.
    });

    public void EnterGame() => OnUi(() =>
    {
        if (_app == null) return;

        // Hide, not close, the menu: closing it took keyboard focus from the game window. One game
        // window for the session: this is called on every spawn, every /tp included.
        if (_gameWindow == null) _gameWindow = new GameWindow(_input, _onQuit);
        _gameWindow.Present(_app);

        _loadingWindow?.SetVisible(false);
        _menuWindow?.SetVisible(false);
    });

    public void OpenCommandConsole() => OpenCommandConsole("");

    public void OpenCommandConsole(string initialText) => OnUi(() =>
    {
        if (_consoleOpen) return;
        _consoleOpen = true;
        // Clear held keys: the slash that opened this is still down, and its key-up will be delivered
        // to the dialog, not the game window.
        _input.Clear();

        var dialog = Window.New();
        _modal = dialog;
        dialog.Title = "Command";
        dialog.SetModal(true);
        dialog.SetDefaultSize(420, 140);
        if (_gameWindow?.Toplevel != null) dialog.SetTransientFor(_gameWindow.Toplevel);

        var box = Box.New(Orientation.Vertical, 8);
        box.MarginTop = box.MarginBottom = box.MarginStart = box.MarginEnd = 16;
        box.Append(Label.New("Type a command (starting with /) or a chat message, then press Enter."));

        var entry = Entry.New();
        entry.SetActivatesDefault(false);
        if (initialText.Length > 0) entry.SetText(initialText);
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
            _modal = null;
            _input.Clear();
            return false;
        };

        // Escape cancels, without tabbing to the Cancel button.
        var keys = EventControllerKey.New();
        keys.SetPropagationPhase(PropagationPhase.Capture);
        keys.OnKeyPressed += (_, e) =>
        {
            if (e.Keyval != 0xff1b) return false;   // GDK_Escape
            dialog.Close();
            _speech.Speak("Cancelled.", interrupt: true);
            return true;
        };
        dialog.AddController(keys);

        dialog.SetChild(box);
        dialog.Present();
        entry.GrabFocus();
        if (initialText.Length > 0)
        {
            entry.SetPosition(-1);   // cursor after what is already there
            _speech.Speak($"{initialText.Trim()}. Type the rest, then press Enter.", interrupt: true);
        }
        else _speech.Speak("Command entry. Type a command or message, then press Enter.", interrupt: true);
    });

    public void AskForValue(EditorValuePrompt prompt, Func<string, string?> submit) => OnUi(() =>
    {
        if (_consoleOpen) return;
        _consoleOpen = true;
        // The Enter that chose the item is still down; its key-up goes to the dialog.
        _input.Clear();

        var dialog = Window.New();
        _modal = dialog;
        dialog.Title = prompt.Title;
        dialog.SetModal(true);
        dialog.SetDefaultSize(420, 160);
        if (_gameWindow?.Toplevel != null) dialog.SetTransientFor(_gameWindow.Toplevel);

        var box = Box.New(Orientation.Vertical, 8);
        box.MarginTop = box.MarginBottom = box.MarginStart = box.MarginEnd = 16;

        // The mnemonic widget makes the label the entry's accessible name (labelled-by); the tooltip is
        // its accessible description, which Orca reads after the name.
        var label = Label.New(prompt.Label);
        label.SetXalign(0);
        box.Append(label);
        var entry = Entry.New();
        entry.SetText(prompt.Initial);
        if (prompt.Description.Length > 0) entry.SetTooltipText(prompt.Description);
        label.SetMnemonicWidget(entry);
        box.Append(entry);
        if (prompt.Description.Length > 0)
        {
            var about = Label.New(prompt.Description);
            about.SetWrap(true);
            about.SetXalign(0);
            box.Append(about);
        }
        // Why a value was refused, shown as well as said.
        var refusal = Label.New("");
        refusal.SetWrap(true);
        refusal.SetXalign(0);
        box.Append(refusal);

        void Apply()
        {
            string? why = submit(entry.GetText());
            if (why == null) { dialog.Close(); return; }
            // Focus stays in the box with the text kept, so the reason is heard and not read over.
            refusal.SetText(why);
            _cue(UiCue.MenuEdge);
            _speech.Speak(why, interrupt: true);
        }
        entry.OnActivate += (_, _) => Apply();

        var apply = Button.NewWithLabel("Apply");
        apply.OnClicked += (_, _) => Apply();
        box.Append(apply);
        var cancel = Button.NewWithLabel("Cancel");
        cancel.OnClicked += (_, _) => Cancel();
        box.Append(cancel);

        void Cancel()
        {
            dialog.Close();
            _cue(UiCue.MenuBack);
            _speech.Speak("Cancelled.", interrupt: true);
        }

        dialog.OnCloseRequest += (_, _) =>
        {
            _consoleOpen = false;
            _modal = null;
            _input.Clear();
            return false;
        };

        var keys = EventControllerKey.New();
        keys.SetPropagationPhase(PropagationPhase.Capture);
        keys.OnKeyPressed += (_, e) =>
        {
            if (e.Keyval != 0xff1b) return false;   // GDK_Escape
            Cancel();
            return true;
        };
        dialog.AddController(keys);

        dialog.SetChild(box);
        dialog.Present();
        entry.GrabFocus();
        entry.SelectRegion(0, -1);   // typing replaces the value
        _speech.Speak(prompt.Spoken, interrupt: true);
    });

    public void ShowGameMenu(Action<GameMenuChoice> chosen) => OnUi(() =>
    {
        if (_consoleOpen) return;
        _consoleOpen = true; // the modal guard: gameplay keys pause while the menu is up
        _input.Clear();

        var dialog = Window.New();
        _modal = dialog;
        dialog.Title = "Game menu";
        dialog.SetModal(true);
        dialog.SetDefaultSize(320, 160);
        if (_gameWindow?.Toplevel != null) dialog.SetTransientFor(_gameWindow.Toplevel);

        var box = Box.New(Orientation.Vertical, 8);
        box.MarginTop = box.MarginBottom = box.MarginStart = box.MarginEnd = 16;

        // Answered once: closing the window after a choice must not also count as Keep playing.
        bool answered = false;
        void Choose(GameMenuChoice choice)
        {
            if (answered) return;
            answered = true;
            dialog.Close();
            chosen(choice);
        }

        Button Item(string label, GameMenuChoice choice)
        {
            var b = Button.NewWithLabel(label);
            b.OnClicked += (_, _) => { if (choice != GameMenuChoice.KeepPlaying) _cue(UiCue.MenuSelect); Choose(choice); };
            var focus = EventControllerFocus.New();
            focus.OnEnter += (_, _) => { _cue(UiCue.MenuMove); _speech.Speak(label, interrupt: true); };
            b.AddController(focus);
            box.Append(b);
            return b;
        }
        var keep = Item("Keep playing", GameMenuChoice.KeepPlaying);
        Item("Main menu", GameMenuChoice.MainMenu);
        Item("Quit", GameMenuChoice.Quit);

        // Escape, or closing the window, is Keep playing.
        var keys = EventControllerKey.New();
        keys.SetPropagationPhase(PropagationPhase.Capture);
        keys.OnKeyPressed += (_, e) =>
        {
            if (e.Keyval != 0xff1b) return false;   // GDK_Escape
            Choose(GameMenuChoice.KeepPlaying);
            return true;
        };
        dialog.AddController(keys);
        dialog.OnCloseRequest += (_, _) =>
        {
            _consoleOpen = false;
            _modal = null;
            _input.Clear();
            if (!answered) { answered = true; chosen(GameMenuChoice.KeepPlaying); }
            return false;
        };

        dialog.SetChild(box);
        dialog.Present();
        // The focus announcement is replaced by one line that says where you are and what is focused.
        keep.GrabFocus();
        _speech.Speak("Game menu. Keep playing. Tab or arrows to choose, Enter to confirm, Escape to go back.", interrupt: true);
    });

    public void ReturnToMenu() => OnUi(() =>
    {
        _modal?.Close();
        _modal = null;
        _consoleOpen = false;
        _input.Clear();
        _gameWindow?.Hide();
        _loadingWindow?.SetVisible(false);
        if (_menuWindow == null) return;
        _menuWindow.SetVisible(true);
        _menuWindow.Present();
        _speech.Speak("Main menu.", interrupt: false);
    });

    public void Quit() => OnUi(_onQuit);

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

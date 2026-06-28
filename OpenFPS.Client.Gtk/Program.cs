using System;
using Gtk;
using OpenFPS.Client.Core.Platform;

// OpenFPS GTK (Linux) client — Phase B, milestone 1: a standing, accessible GTK4 main menu wired
// to the cross-platform speech-dispatcher output. Each control speaks when focused (the core
// non-visual navigation pattern), independent of whether a screen reader is running.
internal static class GtkClientProgram
{
    private static ISpeechOutput _speech = null!;

    public static int Main(string[] args)
    {
        _speech = new SpeechDispatcherOutput();
        _speech.Initialize();

        var app = Application.New("org.openfps.client", Gio.ApplicationFlags.FlagsNone);
        app.OnActivate += (sender, _) => BuildMainMenu((Application)sender);

        int rc = app.RunWithSynchronizationContext(null);
        _speech.Dispose();
        return rc;
    }

    private static void BuildMainMenu(Application app)
    {
        var window = ApplicationWindow.New(app);
        window.Title = "OpenFPS";
        window.SetDefaultSize(480, 320);

        var box = Box.New(Orientation.Vertical, 8);
        box.MarginTop = 24;
        box.MarginBottom = 24;
        box.MarginStart = 24;
        box.MarginEnd = 24;

        box.Append(Label.New("OpenFPS — Main Menu"));
        box.Append(MenuButton("Connect to Server", () => _speech.Speak("Connect to server. Not yet implemented.")));
        box.Append(MenuButton("Settings", () => _speech.Speak("Settings. Not yet implemented.")));
        box.Append(MenuButton("Quit", () => { _speech.Speak("Goodbye."); window.Close(); }));

        window.SetChild(box);
        window.Present();
        _speech.Speak("Open F P S main menu. Tab or arrow keys to move, Enter to select.", true);
    }

    private static Button MenuButton(string label, Action onActivate)
    {
        var btn = Button.NewWithLabel(label);
        btn.OnClicked += (_, _) => onActivate();

        // Speak the control when it receives focus — works whether or not Orca is active.
        var focus = EventControllerFocus.New();
        focus.OnEnter += (_, _) => _speech.Speak(label, true);
        btn.AddController(focus);
        return btn;
    }
}

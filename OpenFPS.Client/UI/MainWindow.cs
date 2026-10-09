using System.Runtime.InteropServices;
using OpenFPS.Client.Core;
using OpenFPS.Client.Core.Input;
using OpenFPS.Client.Core.Platform;
using OpenFPS.Client.Services;

namespace OpenFPS.Client.UI;

/// <summary>
/// The in-game window. It draws nothing — the game is heard — and exists to hold keyboard focus and
/// hand key transitions to the shared <see cref="InputStateBuffer"/>.
///
/// Keys come from this window's own messages, not a system-wide hook: a hook saw the screen reader's
/// keys too, and a key released in another window (Alt+Tab's Alt) stayed held.
/// </summary>
public sealed partial class MainWindow : Form
{
    private readonly InputStateBuffer _input;
    private readonly NvdaSpeechOutput _speech;
    private readonly Action<UiCue> _cue;

    private volatile bool _active;
    private volatile bool _modalOpen;
    // The console or game menu while one is up, so leaving the game can close it.
    private Form? _openDialog;

    /// <summary>True while this window is the foreground window.</summary>
    public bool IsWindowActive => _active;

    /// <summary>True while the command console or the game menu is up; gameplay keys wait.</summary>
    public bool IsModalOpen => _modalOpen;

    public event Action<string>? OnCommandEntered;

    public MainWindow(InputStateBuffer input, NvdaSpeechOutput speech, Action<UiCue> cue)
    {
        _input = input;
        _speech = speech;
        _cue = cue;

        Text = "OpenFPS — In Game";
        ClientSize = new Size(640, 360);
        StartPosition = FormStartPosition.CenterScreen;

        // Not focusable: with no focusable child the form itself holds focus, so every key reaches it
        // and nothing inside can consume arrows or Tab for its own navigation.
        Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            Text = OpenFPS.Client.Core.Session.ClientGameSession.KeyHelp,
        });

        Activated += (_, _) => { _active = true; _input.Clear(); };
        Deactivate += (_, _) => { _active = false; _input.Clear(); };
    }

    // Every key is input here: none of them should move focus or press a button.
    protected override bool IsInputKey(Keys keyData) => true;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        OnKey(e.KeyCode, down: true);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        OnKey(e.KeyCode, down: false);
    }

    private volatile int _numLock = -1;   // -1 unknown, 0 off, 1 on

    /// <summary>Whether Num Lock was on at the last key in this window, or null before any. Read on
    /// the UI thread, where the keyboard's toggle state is current.</summary>
    public bool? NumLockOn => _numLock < 0 ? null : _numLock == 1;

    private void OnKey(Keys code, bool down)
    {
        if (down) _numLock = Control.IsKeyLocked(Keys.NumLock) ? 1 : 0;
        // Windows reports a modifier as the generic ShiftKey / ControlKey / Menu, not which side. Ask
        // the keyboard state for both sides instead, so right shift is right shift as it is on Linux,
        // and a release is never credited to the wrong side.
        if (code is Keys.ShiftKey or Keys.ControlKey or Keys.Menu
                 or Keys.LShiftKey or Keys.RShiftKey or Keys.LControlKey or Keys.RControlKey or Keys.LMenu or Keys.RMenu)
        {
            SyncModifiers();
            return;
        }
        // The keypad with Num Lock off is the screen reader's, and arrives as Up, Delete and the rest.
        _input.SetKey(KeypadKeys.IsNumLockOffKeypad((int)code, _extendedKey) ? GameKey.None : WinFormsKeyMap.Map(code), down);
    }

    // The extended-key flag of the key message being handled, which KeyEventArgs does not carry.
    private bool _extendedKey;

    private void SyncModifiers()
    {
        _input.SetKey(GameKey.ShiftLeft, IsDown(VK_LSHIFT));
        _input.SetKey(GameKey.ShiftRight, IsDown(VK_RSHIFT));
        _input.SetKey(GameKey.ControlLeft, IsDown(VK_LCONTROL));
        _input.SetKey(GameKey.ControlRight, IsDown(VK_RCONTROL));
        _input.SetKey(GameKey.AltLeft, IsDown(VK_LMENU));
        _input.SetKey(GameKey.AltRight, IsDown(VK_RMENU));
    }

    private static bool IsDown(int vk) => (GetKeyState(vk) & 0x8000) != 0;

    private const int VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1, VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3, VK_LMENU = 0xA4, VK_RMENU = 0xA5;

    [DllImport("user32.dll")] private static extern short GetKeyState(int nVirtKey);

    private const int WM_SYSCOMMAND = 0x0112;
    private const int SC_KEYMENU = 0xF100;
    private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;

    /// <summary>
    /// Alt pressed and released on its own, or F10, puts a window into menu mode: the next keys go to
    /// the system menu instead of the game, and NVDA says so. A blind player presses Alt as
    /// punctuation dozens of times a minute, so that is swallowed here. Alt+Space (the system menu
    /// asked for by name) and Alt+F4 (a different command) still work.
    /// </summary>
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_SYSCOMMAND && ((int)m.WParam & 0xFFF0) == SC_KEYMENU && m.LParam == IntPtr.Zero)
            return;
        if (m.Msg is WM_KEYDOWN or WM_KEYUP or WM_SYSKEYDOWN or WM_SYSKEYUP)
            _extendedKey = KeypadKeys.IsExtended(m.LParam.ToInt64());
        base.WndProc(ref m);
    }

    /// <summary>The command console: a line of text sent as a slash command or as chat. Opened with
    /// <paramref name="initialText"/> already typed ("/pm sean01 ") and the cursor at its end.</summary>
    public void OpenCommandWindow(string initialText)
    {
        if (_modalOpen) return;
        _modalOpen = true;
        // The key that opened this is still down, and its release goes to the dialog, not here.
        _input.Clear();

        using var dialog = new Form
        {
            Text = "Command",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(460, 130),
        };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Padding = new Padding(8) };
        var prompt = new Label
        {
            Text = "Type a command (starting with /) or a chat message, then press Enter.",
            AutoSize = true,
        };
        var entry = new TextBox { Dock = DockStyle.Fill, Text = initialText, AccessibleName = "Command or chat message" };
        var send = new Button { Text = "Send", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        layout.Controls.Add(prompt, 0, 0);
        layout.SetColumnSpan(prompt, 2);
        layout.Controls.Add(entry, 0, 1);
        layout.SetColumnSpan(entry, 2);
        layout.Controls.Add(send, 0, 2);
        layout.Controls.Add(cancel, 1, 2);
        dialog.Controls.Add(layout);
        dialog.AcceptButton = send;
        dialog.CancelButton = cancel;
        dialog.Shown += (_, _) =>
        {
            entry.Focus();
            entry.SelectionStart = entry.TextLength;
            entry.SelectionLength = 0;
            // NVDA reads the dialog and the field as they take focus; speaking over it would cut it off.
            if (!_speech.ScreenReaderRunning)
                _speech.Speak(initialText.Length > 0
                    ? $"{initialText.Trim()}. Type the rest, then press Enter."
                    : "Command entry. Type a command or message, then press Enter.", interrupt: true);
        };

        DialogResult result;
        _openDialog = dialog;
        try { result = dialog.ShowDialog(this); }
        finally { _modalOpen = false; _openDialog = null; _input.Clear(); }

        if (result == DialogResult.OK && !string.IsNullOrWhiteSpace(entry.Text))
            OnCommandEntered?.Invoke(entry.Text);
        else if (result == DialogResult.Cancel)
        {
            _cue(UiCue.MenuBack);
            _speech.Speak("Cancelled.", interrupt: true);
        }
    }

    /// <summary>
    /// One labelled text box for a value the world editor asks for. Enter or Apply hands the text to
    /// <paramref name="submit"/>: null closes the dialog, a reason is said and shown and the dialog
    /// stays open with the text kept. Escape or Cancel cancels.
    /// </summary>
    public void AskForValue(EditorValuePrompt prompt, Func<string, string?> submit)
    {
        if (_modalOpen) return;
        _modalOpen = true;
        // The Enter that chose the item is still down, and its release goes to the dialog.
        _input.Clear();

        using var dialog = new Form
        {
            Text = prompt.Title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(460, 220),
        };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 5, Padding = new Padding(8) };
        // UseMnemonic off: a label such as "Rock & roll" must not lose its ampersand.
        var label = new Label { Text = prompt.Label, AutoSize = true, UseMnemonic = false };
        // NVDA reads the name, the value (selected) and then the description.
        var entry = new TextBox
        {
            Dock = DockStyle.Fill,
            Text = prompt.Initial,
            AccessibleName = prompt.Label,
            AccessibleDescription = prompt.Description,
        };
        var about = new Label { Text = prompt.Description, AutoSize = true, MaximumSize = new Size(440, 0), UseMnemonic = false };
        var refusal = new Label { Text = "", AutoSize = true, MaximumSize = new Size(440, 0), UseMnemonic = false };
        var apply = new Button { Text = "Apply", AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        layout.Controls.Add(label, 0, 0);
        layout.SetColumnSpan(label, 2);
        layout.Controls.Add(entry, 0, 1);
        layout.SetColumnSpan(entry, 2);
        layout.Controls.Add(about, 0, 2);
        layout.SetColumnSpan(about, 2);
        layout.Controls.Add(refusal, 0, 3);
        layout.SetColumnSpan(refusal, 2);
        layout.Controls.Add(apply, 0, 4);
        layout.Controls.Add(cancel, 1, 4);
        dialog.Controls.Add(layout);
        dialog.AcceptButton = apply;
        dialog.CancelButton = cancel;

        // Apply is not a DialogResult button: a refused value must leave the dialog open.
        apply.Click += (_, _) =>
        {
            string? why = submit(entry.Text);
            if (why == null) { dialog.DialogResult = DialogResult.OK; return; }
            refusal.Text = why;
            _cue(UiCue.MenuEdge);
            // Focus stays in the box, so NVDA has nothing of its own to say over the reason.
            _speech.Speak(why, interrupt: true);
            entry.Focus();
        };
        dialog.Shown += (_, _) =>
        {
            entry.Focus();
            entry.SelectAll();
            // NVDA reads the dialog and the field as they take focus; speaking over it would cut it off.
            if (!_speech.ScreenReaderRunning) _speech.Speak(prompt.Spoken, interrupt: true);
        };

        DialogResult result;
        _openDialog = dialog;
        try { result = dialog.ShowDialog(this); }
        finally { _modalOpen = false; _openDialog = null; _input.Clear(); }

        if (result == DialogResult.Cancel)
        {
            _cue(UiCue.MenuBack);
            _speech.Speak("Cancelled.", interrupt: true);
        }
    }

    /// <summary>
    /// The game menu: Keep playing, Main menu, Quit. Keep playing has the focus and is also what Enter
    /// and Escape do on it, so a stray key does not end the game. NVDA reads the dialog and the focused
    /// button itself; the game speaks only without it.
    /// </summary>
    public void ShowGameMenu(Action<GameMenuChoice> chosen)
    {
        if (_modalOpen) return;
        _modalOpen = true;
        _input.Clear();

        var choice = GameMenuChoice.KeepPlaying;
        using var dialog = new Form
        {
            Text = "Game menu",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(320, 160),
        };
        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(12) };
        Button Item(string text, GameMenuChoice value)
        {
            var b = new Button { Text = text, Width = 280, Height = 32, FlatStyle = FlatStyle.System };
            b.Click += (_, _) =>
            {
                choice = value;
                if (value != GameMenuChoice.KeepPlaying) _cue(UiCue.MenuSelect);
                dialog.Close();
            };
            b.Enter += (_, _) =>
            {
                _cue(UiCue.MenuMove);
                if (!_speech.ScreenReaderRunning) _speech.Speak(text, interrupt: true);
            };
            layout.Controls.Add(b);
            return b;
        }
        var keep = Item("Keep playing", GameMenuChoice.KeepPlaying);
        Item("Main menu", GameMenuChoice.MainMenu);
        Item("Quit", GameMenuChoice.Quit);
        dialog.Controls.Add(layout);
        dialog.CancelButton = keep;
        dialog.Shown += (_, _) =>
        {
            keep.Focus();
            if (!_speech.ScreenReaderRunning)
                _speech.Speak("Game menu. Keep playing. Tab or arrows to choose, Enter to confirm, Escape to go back.", interrupt: true);
        };

        _openDialog = dialog;
        try { dialog.ShowDialog(this); }
        finally { _modalOpen = false; _openDialog = null; _input.Clear(); }
        chosen(choice);
    }

    /// <summary>Closes the console or game menu if one is open, for leaving the game.</summary>
    public void CloseModals()
    {
        var open = _openDialog;
        if (open is { IsDisposed: false }) open.Close();
    }
}

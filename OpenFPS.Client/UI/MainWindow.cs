using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using OpenFPS.Client.Core;
using OpenFPS.Client.Core.Input;
using OpenFPS.Client.Core.Platform;
using OpenFPS.Client.Services;

namespace OpenFPS.Client.UI;

/// <summary>
/// The in-game window. It draws nothing — the game is heard — and exists to hold keyboard focus and
/// hand key transitions to the shared <see cref="InputStateBuffer"/>.
///
/// Keys come from this window's own messages, not a system-wide hook. The hook this replaced saw every
/// key pressed anywhere on the machine, including the screen reader's, and nothing cleared a key whose
/// release happened in another window — the stuck-Alt of an Alt+Tab. The GTK head reads keys the same
/// way, and clears held keys on every focus change for the same reason.
/// </summary>
public sealed class MainWindow : Form
{
    private readonly InputStateBuffer _input;
    private readonly NvdaSpeechOutput _speech;
    private readonly Action<UiCue> _cue;

    private volatile bool _active;
    private volatile bool _modalOpen;

    /// <summary>True while this window is the foreground window.</summary>
    public bool IsWindowActive => _active;

    /// <summary>True while the command console or the quit prompt is up; gameplay keys wait.</summary>
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
            Text = "In game. W A S D to move, J / L turn, O / K look up and down, Space jump.\r\n" +
                   "C coordinates, F facing, H health, Z area, comma look ahead, E interact, P scan, I inventory.\r\n" +
                   "V voice, F5 players, brackets to read chat, slash for the command console, Escape to quit.",
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

    private void OnKey(Keys code, bool down)
    {
        // Windows reports a modifier as the generic ShiftKey / ControlKey / Menu, not which side. Ask
        // the keyboard state for both sides instead, so right shift is right shift as it is on Linux,
        // and a release is never credited to the wrong side.
        if (code is Keys.ShiftKey or Keys.ControlKey or Keys.Menu
                 or Keys.LShiftKey or Keys.RShiftKey or Keys.LControlKey or Keys.RControlKey or Keys.LMenu or Keys.RMenu)
        {
            SyncModifiers();
            return;
        }
        _input.SetKey(WinFormsKeyMap.Map(code), down);
    }

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
                    ? $"Command entry: {initialText.Trim()}. Type the rest, then press Enter."
                    : "Command entry. Type a command or message, then press Enter.", interrupt: true);
        };

        DialogResult result;
        try { result = dialog.ShowDialog(this); }
        finally { _modalOpen = false; _input.Clear(); }

        if (result == DialogResult.OK && !string.IsNullOrWhiteSpace(entry.Text))
            OnCommandEntered?.Invoke(entry.Text);
        else if (result == DialogResult.Cancel)
        {
            _cue(UiCue.MenuBack);
            _speech.Speak("Cancelled.", interrupt: true);
        }
    }

    /// <summary>Asks before quitting. "Keep playing" is the default, so a stray Enter does not end the game.</summary>
    public void ConfirmQuit(Action onQuit)
    {
        if (_modalOpen) return;
        _modalOpen = true;
        _input.Clear();
        DialogResult result;
        try
        {
            result = MessageBox.Show(this, "Quit OpenFPS?", "Quit", MessageBoxButtons.YesNo,
                                     MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        }
        finally { _modalOpen = false; _input.Clear(); }
        if (result == DialogResult.Yes) onQuit();
        else _cue(UiCue.MenuBack);
    }
}

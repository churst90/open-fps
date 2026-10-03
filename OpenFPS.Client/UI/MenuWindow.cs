using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using OpenFPS.Client.Core;
using OpenFPS.Client.Services;

namespace OpenFPS.Client.UI;

/// <summary>
/// The main menu and its windows — Connect, Saved Servers, Settings — over the same
/// <see cref="ClientSettings"/> file the Linux head uses, so a friend's saved servers and devices
/// behave the same on both.
///
/// Every control is a native Win32 control with an accessible name, so NVDA announces each one as it
/// takes focus. The game adds only the interface cue — a tick on focus, a rise on select, a fall on
/// Escape — and speaks names itself only when no screen reader is running, since speaking over NVDA
/// cuts it off (and NVDA's own focus announcement cuts the game off).
/// </summary>
public sealed class MenuWindow : Form
{
    private readonly NvdaSpeechOutput _speech;
    private readonly ClientSettings _settings;
    private readonly MenuServices _services;

    // The connect form stays up until the server has answered. Closing it on submit dropped focus back
    // onto the menu, and that announcement cut off the rejection — a wrong password sounded like silence.
    private Form? _loginForm;
    private TextBox? _loginStatus;
    private TextBox? _loginUser;
    private PendingLogin? _pending;

    private sealed record PendingLogin(string Address, string User, string Pass, bool Remember);

    public MenuWindow(NvdaSpeechOutput speech, ClientSettings settings, MenuServices services)
    {
        _speech = speech;
        _settings = settings;
        _services = services;

        Text = "OpenFPS";
        ClientSize = new Size(420, 320);
        StartPosition = FormStartPosition.CenterScreen;

        var layout = Column();
        layout.Controls.Add(new Label { Text = "OpenFPS — Main Menu", AutoSize = true });
        layout.Controls.Add(MenuButton("Connect", ConnectPreferred));
        layout.Controls.Add(MenuButton("Create account", CreateAccountPreferred));
        layout.Controls.Add(MenuButton("Saved Servers", ShowServers));
        layout.Controls.Add(MenuButton("Settings", ShowSettings));
        layout.Controls.Add(MenuButton("Quit", () => { _speech.Speak("Goodbye."); Close(); }));
        Controls.Add(layout);

        Shown += (_, _) =>
        {
            // NVDA reads the window and the first button; the hint is the part it cannot know.
            _speech.Speak("Open F P S main menu. Tab or arrow keys to move, Enter to select.", interrupt: false);
            if (_services.MissingAudioReport.Length > 0) _speech.Speak("Warning. " + _services.MissingAudioReport, interrupt: false);
        };
    }

    // ── Connect ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Connect goes to the preferred server: straight in with a remembered password, or asking
    /// only for the password. With none saved yet it opens the list to add one.</summary>
    private void ConnectPreferred()
    {
        var server = _settings.Preferred;
        if (server == null)
        {
            _speech.Speak("No preferred server yet. Add one in Saved Servers.", true);
            ShowServers();
            return;
        }
        ConnectTo(server);
    }

    /// <summary>Create account makes a new account on the preferred server: the form for that server, blank,
    /// with Create account first. The saved account there is neither used nor touched; a new account is
    /// saved as its own entry once it is made. With none saved yet it opens the list to add one.</summary>
    private void CreateAccountPreferred()
    {
        var server = _settings.Preferred;
        if (server == null)
        {
            _speech.Speak("No preferred server yet. Add one in Saved Servers.", true);
            ShowServers();
            return;
        }
        ShowLoginForm(server, register: true);
    }

    private void ConnectTo(SavedServer server)
    {
        if (server.RememberPassword && server.Password.Length > 0 && server.Username.Length > 0)
        {
            _pending = null;   // already saved: nothing to remember afterwards
            _services.Connect($"{server.Host}:{server.Port}", server.Username, server.Password, false);
            return;
        }
        ShowLoginForm(server);
    }

    /// <summary>The Connect form; with <paramref name="register"/> it is the same form for making a new
    /// account, with Create account first (and the default) and focus on the username.</summary>
    private void ShowLoginForm(SavedServer? saved, bool register = false)
    {
        if (_loginForm is { IsDisposed: false }) { _loginForm.Activate(); return; }

        var form = Dialog(register ? "Create Account" : "Connect to Server", 440, 400);
        var layout = Column();

        // The last outcome, in a field that can be focused and re-read rather than speech gone by.
        var status = Field(layout, "Status", "No messages.", readOnly: true);
        var server = Field(layout, "Server address", saved != null ? $"{saved.Host}:{saved.Port}" : "127.0.0.1:33288");
        // A new account starts blank: the saved account's name is not the one being made.
        var user = Field(layout, "Username", register ? "" : saved?.Username ?? "");
        var pass = Field(layout, "Password", "", password: true);
        var remember = Check(layout, "Remember password", saved?.RememberPassword ?? false);

        void Submit(bool register)
        {
            _pending = new PendingLogin(server.Text.Trim(), user.Text.Trim(), pass.Text, remember.Checked);
            status.Text = register ? "Creating the account..." : "Connecting...";
            _services.Connect(_pending.Address, _pending.User, _pending.Pass, register);
        }
        var connect = MenuButton("Connect", () => Submit(register: false));
        var create = MenuButton("Create account", () => Submit(register: true));
        if (register) { layout.Controls.Add(create); layout.Controls.Add(connect); }
        else { layout.Controls.Add(connect); layout.Controls.Add(create); }
        var cancel = MenuButton("Cancel", () => form.Close());
        layout.Controls.Add(cancel);
        form.AcceptButton = register ? create : connect;
        form.CancelButton = cancel;
        form.Controls.Add(layout);

        _loginForm = form; _loginStatus = status; _loginUser = user;
        form.FormClosed += (_, _) => { _loginForm = null; _loginStatus = null; _loginUser = null; };
        form.Shown += (_, _) =>
        {
            if (register && saved != null)
            {
                user.Focus();
                if (!_speech.ScreenReaderRunning)
                    _speech.Speak($"Create an account on {(saved.Name.Length > 0 ? saved.Name : saved.Host)}. Username.", true);
            }
            else if (saved != null && saved.Username.Length > 0)
            {
                pass.Focus();
                if (!_speech.ScreenReaderRunning) _speech.Speak($"Connect to {saved.Name} as {saved.Username}. Password.", true);
            }
            else
            {
                server.Focus();
                if (!_speech.ScreenReaderRunning) _speech.Speak("Connect dialog. Server address, username, and password fields.", true);
            }
        };
        form.Show(this);
    }

    /// <summary>
    /// A connect or login outcome. The reason has already been spoken; on a failure focus goes to the
    /// status field, so NVDA reads the reason again from the form and it stays there to re-read.
    /// </summary>
    public void ReportLoginOutcome(string message, bool success)
    {
        if (success)
        {
            if (_pending is { } p && _loginForm != null) RememberServer(p);
            _pending = null;
            if (_loginForm != null) { _loginForm.DialogResult = DialogResult.OK; _loginForm.Close(); }
            return;
        }
        if (_loginForm == null || _loginStatus == null) return;
        _loginStatus.Text = message;
        if (_speech.ScreenReaderRunning) _loginStatus.Focus();
        else _loginUser?.Focus();
    }

    /// <summary>A server logged in to by hand is remembered, so Connect can go straight back.</summary>
    private void RememberServer(PendingLogin login) => _settings.Remember(login.Address, login.User, login.Pass, login.Remember);

    // ── Saved servers ──────────────────────────────────────────────────────────────────────────

    private void ShowServers()
    {
        var form = Dialog("Saved Servers", 520, 440);
        var layout = Column();
        layout.Controls.Add(new Label { Text = "Servers", AutoSize = true });
        var list = new ListBox { Width = 480, Height = 160, AccessibleName = "Servers", IntegralHeight = false };
        layout.Controls.Add(list);

        void Refill()
        {
            int at = list.SelectedIndex;
            list.Items.Clear();
            foreach (var s in _settings.Servers) list.Items.Add(s.ToString());
            if (_settings.Servers.Count == 0) list.Items.Add("No servers saved. Use Add.");
            list.SelectedIndex = Math.Clamp(at, 0, list.Items.Count - 1);
        }
        SavedServer? Selected()
        {
            int i = list.SelectedIndex;
            return i >= 0 && i < _settings.Servers.Count ? _settings.Servers[i] : null;
        }
        void Need(Action<SavedServer> act)
        {
            if (Selected() is { } s) act(s);
            else _speech.Speak("Choose a server first.", true);
        }
        Refill();

        var connect = MenuButton("Connect", () => Need(s => { form.DialogResult = DialogResult.OK; form.Close(); ConnectTo(s); }));
        layout.Controls.Add(connect);
        layout.Controls.Add(MenuButton("Set as preferred", () => Need(s =>
        {
            _settings.SetPreferred(s);
            _settings.Save();
            Refill();
            _speech.Speak($"{s.Name} is now your preferred server.", true);
        })));
        layout.Controls.Add(MenuButton("Add", () => EditServer(form, null, Refill)));
        layout.Controls.Add(MenuButton("Edit", () => Need(s => EditServer(form, s, Refill))));
        layout.Controls.Add(MenuButton("Remove", () => Need(s =>
        {
            bool wasPreferred = s.Preferred;
            _settings.Servers.Remove(s);
            if (wasPreferred && _settings.Servers.Count > 0) _settings.Servers[0].Preferred = true;
            _settings.Save();
            Refill();
            _speech.Speak($"Removed {s.Name}.", true);
        })));
        var close = MenuButton("Close", () => form.Close());
        layout.Controls.Add(close);
        // Enter on the list connects to the server under the cursor.
        form.AcceptButton = connect;
        form.CancelButton = close;
        form.Controls.Add(layout);
        form.Shown += (_, _) =>
        {
            list.Focus();
            _speech.Speak($"Saved servers. {_settings.Servers.Count} saved. Arrow keys to choose, Enter to connect.", false);
        };
        form.ShowDialog(this);
    }

    private void EditServer(Form owner, SavedServer? existing, Action changed)
    {
        var form = Dialog(existing == null ? "Add Server" : "Edit Server", 440, 400);
        var layout = Column();
        var name = Field(layout, "Name", existing?.Name ?? "");
        var address = Field(layout, "Server address", existing != null ? $"{existing.Host}:{existing.Port}" : "127.0.0.1:33288");
        var user = Field(layout, "Username", existing?.Username ?? "");
        var pass = Field(layout, "Password", existing?.Password ?? "", password: true);
        var remember = Check(layout, "Remember password", existing?.RememberPassword ?? false);

        var save = MenuButton("Save", () =>
        {
            var s = existing ?? new SavedServer();
            ServerAddress.Parse(address.Text, out string host, out int port);
            s.Host = host; s.Port = port;
            s.Name = name.Text.Trim().Length > 0 ? name.Text.Trim() : host;
            s.Username = user.Text.Trim();
            s.RememberPassword = remember.Checked;
            s.Password = s.RememberPassword ? pass.Text : "";
            if (existing == null)
            {
                _settings.Servers.Add(s);
                if (_settings.Servers.Count == 1) s.Preferred = true;
            }
            _settings.Save();
            changed();
            form.DialogResult = DialogResult.OK;
            form.Close();
            _speech.Speak($"Saved {s.Name}.", true);
        });
        layout.Controls.Add(save);
        var cancel = MenuButton("Cancel", () => form.Close());
        layout.Controls.Add(cancel);
        form.AcceptButton = save;
        form.CancelButton = cancel;
        form.Controls.Add(layout);
        form.Shown += (_, _) => name.Focus();
        form.ShowDialog(owner);
    }

    // ── Settings ───────────────────────────────────────────────────────────────────────────────

    private void ShowSettings()
    {
        var form = Dialog("Settings", 460, 430);
        var layout = Column();

        var outputs = new List<string> { "System default" };
        outputs.AddRange(_services.OutputDevices());
        var inputs = new List<string> { "System default" };
        inputs.AddRange(_services.InputDevices());

        var output = Choice(layout, "Output device", outputs, _settings.OutputDevice);
        var input = Choice(layout, "Input device, for voice chat", inputs, _settings.InputDevice);
        var uiSounds = Check(layout, "Interface sounds", _settings.UiSounds);

        layout.Controls.Add(new Label { Text = "Interface sound volume, percent", AutoSize = true });
        var volume = new NumericUpDown
        {
            Minimum = 0, Maximum = 100, Increment = 10, Width = 120,
            Value = (decimal)Math.Clamp(Math.Round(_settings.UiVolume * 100), 0, 100),
            AccessibleName = "Interface sound volume, percent",
        };
        volume.Enter += (_, _) => Cue(UiCue.MenuMove);
        layout.Controls.Add(volume);

        layout.Controls.Add(MenuButton("Open log folder", OpenLogFolder));

        var save = MenuButton("Save", () =>
        {
            _settings.OutputDevice = output.SelectedIndex <= 0 ? "" : outputs[output.SelectedIndex];
            _settings.InputDevice = input.SelectedIndex <= 0 ? "" : inputs[input.SelectedIndex];
            _settings.UiSounds = uiSounds.Checked;
            _settings.UiVolume = (float)volume.Value / 100f;
            _settings.Save();
            _services.ApplySettings();
            form.DialogResult = DialogResult.OK;
            form.Close();
            _speech.Speak("Settings saved.", true);
        });
        layout.Controls.Add(save);
        var cancel = MenuButton("Cancel", () => form.Close());
        layout.Controls.Add(cancel);
        form.AcceptButton = save;
        form.CancelButton = cancel;
        form.Controls.Add(layout);
        form.Shown += (_, _) => output.Focus();
        form.ShowDialog(this);
    }

    private void OpenLogFolder()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{Program.LogDirectory}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Could not open the log folder.");
            _speech.Speak($"Could not open the log folder. It is {Program.LogDirectory}", true);
        }
    }

    // ── Building blocks ────────────────────────────────────────────────────────────────────────

    private void Cue(UiCue cue) => _services.Cue(cue);

    private Form Dialog(string title, int width, int height)
    {
        var form = new Form
        {
            Text = title,
            ClientSize = new Size(width, height),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
        };
        // Escape (the CancelButton) and the close box both leave with the falling cue.
        form.FormClosed += (_, _) => { if (form.DialogResult != DialogResult.OK) Cue(UiCue.MenuBack); };
        return form;
    }

    private static FlowLayoutPanel Column() => new()
    {
        Dock = DockStyle.Fill,
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        AutoScroll = true,
        Padding = new Padding(12),
    };

    private Button MenuButton(string text, Action onClick)
    {
        var button = new Button { Text = text, Width = 300, Height = 32, FlatStyle = FlatStyle.System };
        button.Click += (_, _) => { Cue(UiCue.MenuSelect); onClick(); };
        button.Enter += (_, _) => OnControlFocused(text);
        return button;
    }

    private TextBox Field(Control parent, string label, string initial, bool password = false, bool readOnly = false)
    {
        parent.Controls.Add(new Label { Text = label, AutoSize = true });
        var box = new TextBox { Text = initial, Width = 380, AccessibleName = label, UseSystemPasswordChar = password, ReadOnly = readOnly };
        box.Enter += (_, _) => OnControlFocused(password || box.Text.Length == 0 ? label : $"{label}, {box.Text}");
        parent.Controls.Add(box);
        return box;
    }

    private CheckBox Check(Control parent, string label, bool initial)
    {
        var box = new CheckBox { Text = label, Checked = initial, AutoSize = true };
        box.Enter += (_, _) => OnControlFocused($"{label}, {(box.Checked ? "checked" : "not checked")}");
        box.CheckedChanged += (_, _) => { if (!_speech.ScreenReaderRunning) _speech.Speak(box.Checked ? "Checked" : "Not checked", true); };
        parent.Controls.Add(box);
        return box;
    }

    private ComboBox Choice(Control parent, string label, List<string> options, string current)
    {
        parent.Controls.Add(new Label { Text = label, AutoSize = true });
        var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 380, AccessibleName = label };
        combo.Items.AddRange(options.Cast<object>().ToArray());
        combo.SelectedIndex = current.Length == 0 ? 0 : Math.Max(0, options.IndexOf(current));
        combo.Enter += (_, _) => OnControlFocused($"{label}, {combo.SelectedItem}");
        combo.SelectedIndexChanged += (_, _) => { if (!_speech.ScreenReaderRunning && combo.Focused) _speech.Speak($"{combo.SelectedItem}", true); };
        parent.Controls.Add(combo);
        return combo;
    }

    /// <summary>A control took focus: the tick always, and its name only when no screen reader will say it.</summary>
    private void OnControlFocused(string name)
    {
        Cue(UiCue.MenuMove);
        if (!_speech.ScreenReaderRunning) _speech.Speak(name, true);
    }
}

/// <summary>What the menu needs from the rest of the client, so it does not reach into the session.</summary>
public sealed class MenuServices
{
    public required Action<string, string, string, bool> Connect { get; init; }
    public required Action<UiCue> Cue { get; init; }
    public required Func<IReadOnlyList<string>> OutputDevices { get; init; }
    public required Func<IReadOnlyList<string>> InputDevices { get; init; }
    public required Action ApplySettings { get; init; }
    public string MissingAudioReport { get; init; } = "";
}

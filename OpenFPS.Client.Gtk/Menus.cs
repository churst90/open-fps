using System;
using System.Collections.Generic;
using System.Linq;
using Gtk;
using OpenFPS.Client.Core;



/// <summary>
/// The main menu's windows: Saved Servers and Settings, and the sounds every menu makes.
///
/// Every button ticks as focus lands on it and confirms when pressed, and every window closes on
/// Escape with a falling cue — so a menu can be learned by ear as well as by what the screen reader
/// says. The data behind them (<see cref="ClientSettings"/>) lives in the shared client core, so the
/// Windows and Mac heads get the same servers and settings from the same file.
/// </summary>
internal static partial class GtkClientProgram
{
    private static ClientSettings _settings = new();

    /// <summary>Applies what the settings file says once the audio engine is running.</summary>
    private static void ApplyAudioSettings()
    {
        _session.Ui.Enabled = _settings.UiSounds;
        _session.Ui.Volume = _settings.UiVolume;
        if (_settings.OutputDevice.Length > 0 && !_session.Audio.SetOutputDevice(_settings.OutputDevice))
            _speech.Speak($"The saved output device, {_settings.OutputDevice}, is not connected. Using the default.", false);
    }

    private static void Cue(UiCue cue) => _session?.Ui.Play(cue);

    /// <summary>Escape closes the window, with the falling "back" cue.</summary>
    private static void CloseOnEscape(Window window)
    {
        var keys = EventControllerKey.New();
        keys.SetPropagationPhase(PropagationPhase.Capture);
        keys.OnKeyPressed += (_, e) =>
        {
            if (e.Keyval != 0xff1b) return false;   // GDK_Escape
            Cue(UiCue.MenuBack);
            window.Close();
            return true;
        };
        window.AddController(keys);
    }

    private static Window Dialog(string title, int width, int height)
    {
        var w = Window.New();
        w.Title = title;
        w.SetTransientFor(_mainWindow);
        w.SetModal(true);
        w.SetDefaultSize(width, height);
        CloseOnEscape(w);
        return w;
    }

    // ── Connect ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Connect goes to the preferred server. With its password remembered it logs straight in; without
    /// one it asks only for the password. With no server saved yet it opens the list to add one.
    /// </summary>
    private static void ConnectPreferred()
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

    private static void ConnectTo(SavedServer server)
    {
        if (server.RememberPassword && server.Password.Length > 0 && server.Username.Length > 0)
        {
            _pendingUser = server.Username;
            _pendingPass = server.Password;
            DoConnect($"{server.Host}:{server.Port}");
            return;
        }
        ShowLoginDialog(server);
    }

    /// <summary>After a successful login from the Connect dialog, the server is remembered.</summary>
    private static void RememberServer(string address, string user, string pass, bool rememberPassword)
    {
        ParseAddress(address, out string host, out int port);
        var s = _settings.Servers.FirstOrDefault(x => x.Host == host && x.Port == port && x.Username == user);
        if (s == null)
        {
            s = new SavedServer { Name = host, Host = host, Port = port, Username = user };
            _settings.Servers.Add(s);
            if (_settings.Servers.Count == 1) s.Preferred = true;
        }
        s.RememberPassword = rememberPassword;
        s.Password = rememberPassword ? pass : "";
        _settings.Save();
    }

    private static void ParseAddress(string addr, out string host, out int port)
    {
        host = "127.0.0.1"; port = 33288;
        var parts = addr.Trim().Split(':');
        if (parts.Length >= 1 && parts[0].Length > 0) host = parts[0];
        if (parts.Length >= 2 && int.TryParse(parts[1], out int p)) port = p;
    }

    // ── Saved servers ──────────────────────────────────────────────────────────────────────────

    private static void ShowServers()
    {
        var window = Dialog("Saved Servers", 520, 420);
        var box = VBox(16);
        var list = ListBox.New();
        list.SetSelectionMode(SelectionMode.Single);

        void Refill()
        {
            while (list.GetFirstChild() is { } child) list.Remove(child);
            foreach (var s in _settings.Servers)
            {
                var row = ListBoxRow.New();
                row.SetChild(Label.New(s.ToString()));
                var captured = s;
                SpeakOnFocus(row, () => captured.ToString());
                list.Append(row);
            }
            if (_settings.Servers.Count == 0)
            {
                var row = ListBoxRow.New();
                row.SetChild(Label.New("No servers saved. Use Add."));
                SpeakOnFocus(row, "No servers saved. Use Add.");
                list.Append(row);
            }
        }
        SavedServer? Selected()
        {
            var row = list.GetSelectedRow();
            if (row == null) return null;
            int i = row.GetIndex();
            return i >= 0 && i < _settings.Servers.Count ? _settings.Servers[i] : null;
        }
        Refill();
        // Enter on a row connects to it.
        list.OnRowActivated += (_, e) =>
        {
            int i = e.Row.GetIndex();
            if (i < 0 || i >= _settings.Servers.Count) return;
            Cue(UiCue.MenuSelect);
            window.Close();
            ConnectTo(_settings.Servers[i]);
        };
        box.Append(list);

        box.Append(MenuButton("Connect", () =>
        {
            if (Selected() is not { } s) { _speech.Speak("Choose a server first.", true); return; }
            window.Close();
            ConnectTo(s);
        }));
        box.Append(MenuButton("Set as preferred", () =>
        {
            if (Selected() is not { } s) { _speech.Speak("Choose a server first.", true); return; }
            _settings.SetPreferred(s);
            _settings.Save();
            Refill();
            _speech.Speak($"{s.Name} is now your preferred server.", true);
        }));
        box.Append(MenuButton("Add", () => EditServer(null, Refill)));
        box.Append(MenuButton("Edit", () =>
        {
            if (Selected() is not { } s) { _speech.Speak("Choose a server first.", true); return; }
            EditServer(s, Refill);
        }));
        box.Append(MenuButton("Remove", () =>
        {
            if (Selected() is not { } s) { _speech.Speak("Choose a server first.", true); return; }
            bool wasPreferred = s.Preferred;
            _settings.Servers.Remove(s);
            if (wasPreferred && _settings.Servers.Count > 0) _settings.Servers[0].Preferred = true;
            _settings.Save();
            Refill();
            _speech.Speak($"Removed {s.Name}.", true);
        }));
        box.Append(MenuButton("Close", () => { Cue(UiCue.MenuBack); window.Close(); }));

        window.SetChild(box);
        window.Present();
        list.GrabFocus();
        _speech.Speak($"Saved servers. {_settings.Servers.Count} saved. Arrow keys to choose, Enter to connect.", true);
    }

    private static void EditServer(SavedServer? existing, Action changed)
    {
        var window = Dialog(existing == null ? "Add Server" : "Edit Server", 420, 380);
        var box = VBox(16);
        var name = LabeledEntry(box, "Name", existing?.Name ?? "", false);
        var address = LabeledEntry(box, "Server address", existing != null ? $"{existing.Host}:{existing.Port}" : "127.0.0.1:33288", false);
        var user = LabeledEntry(box, "Username", existing?.Username ?? "", false);
        var pass = LabeledEntry(box, "Password", existing?.Password ?? "", true);
        var remember = CheckButton.NewWithLabel("Remember password");
        remember.SetActive(existing?.RememberPassword ?? false);
        SpeakOnFocus(remember, () => $"Remember password, {(remember.GetActive() ? "checked" : "not checked")}");
        box.Append(remember);

        box.Append(MenuButton("Save", () =>
        {
            var s = existing ?? new SavedServer();
            ParseAddress(address.GetText(), out string host, out int port);
            s.Host = host; s.Port = port;
            s.Name = name.GetText().Trim().Length > 0 ? name.GetText().Trim() : host;
            s.Username = user.GetText().Trim();
            s.RememberPassword = remember.GetActive();
            s.Password = s.RememberPassword ? pass.GetText() : "";
            if (existing == null)
            {
                _settings.Servers.Add(s);
                if (_settings.Servers.Count == 1) s.Preferred = true;
            }
            _settings.Save();
            changed();
            window.Close();
            _speech.Speak($"Saved {s.Name}.", true);
        }));
        box.Append(MenuButton("Cancel", () => { Cue(UiCue.MenuBack); window.Close(); }));
        window.SetChild(box);
        window.Present();
        name.GrabFocus();
    }

    // ── Settings ───────────────────────────────────────────────────────────────────────────────

    private static void ShowSettings()
    {
        var window = Dialog("Settings", 460, 400);
        var box = VBox(16);

        var outputs = new List<string> { "System default" };
        outputs.AddRange(_session.Audio.OutputDevices());
        var inputs = new List<string> { "System default" };
        inputs.AddRange(_session.Audio.InputDevices());

        var output = Choice(box, "Output device", outputs, _settings.OutputDevice);
        var input = Choice(box, "Input device, for voice chat", inputs, _settings.InputDevice);

        var uiSounds = CheckButton.NewWithLabel("Interface sounds");
        uiSounds.SetActive(_settings.UiSounds);
        SpeakOnFocus(uiSounds, () => $"Interface sounds, {(uiSounds.GetActive() ? "on" : "off")}");
        uiSounds.OnToggled += (_, _) => _speech.Speak(uiSounds.GetActive() ? "On" : "Off", true);
        box.Append(uiSounds);

        box.Append(Label.New("Interface sound volume, percent"));
        var volume = SpinButton.NewWithRange(0, 100, 10);
        volume.SetValue(Math.Round(_settings.UiVolume * 100));
        SpeakOnFocus(volume, () => $"Interface sound volume, {volume.GetValue():F0} percent");
        box.Append(volume);

        box.Append(MenuButton("Save", () =>
        {
            _settings.OutputDevice = output.GetSelected() == 0 ? "" : outputs[(int)output.GetSelected()];
            _settings.InputDevice = input.GetSelected() == 0 ? "" : inputs[(int)input.GetSelected()];
            _settings.UiSounds = uiSounds.GetActive();
            _settings.UiVolume = (float)(volume.GetValue() / 100.0);
            _settings.Save();
            _session.Audio.SetOutputDevice(_settings.OutputDevice);
            ApplyAudioSettings();
            Cue(UiCue.MenuSelect);
            window.Close();
            _speech.Speak("Settings saved.", true);
        }));
        box.Append(MenuButton("Cancel", () => { Cue(UiCue.MenuBack); window.Close(); }));

        window.SetChild(box);
        window.Present();
        output.GrabFocus();
        _speech.Speak("Settings.", true);
    }

    /// <summary>A labelled drop-down that says its value as it gains focus and as it changes.</summary>
    private static DropDown Choice(Box parent, string label, List<string> options, string current)
    {
        parent.Append(Label.New(label));
        var dd = DropDown.NewFromStrings(options.ToArray());
        int at = current.Length == 0 ? 0 : Math.Max(0, options.IndexOf(current));
        dd.SetSelected((uint)at);
        SpeakOnFocus(dd, () => $"{label}, {options[(int)Math.Min(dd.GetSelected(), (uint)(options.Count - 1))]}");
        dd.OnNotify += (_, e) =>
        {
            if (e.Pspec.GetName() == "selected")
                _speech.Speak(options[(int)Math.Min(dd.GetSelected(), (uint)(options.Count - 1))], true);
        };
        parent.Append(dd);
        return dd;
    }
}

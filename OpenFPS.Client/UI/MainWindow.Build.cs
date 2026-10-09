using OpenFPS.Client.Core;
using OpenFPS.Client.Core.Input;
using OpenFPS.Client.Core.Platform;
using OpenFPS.Common.Editing;

namespace OpenFPS.Client.UI;

/// <summary>The build dialog (Control+B), drawn from the shared <see cref="BuildForm"/>.</summary>
public sealed partial class MainWindow
{
    /// <summary>
    /// The build dialog: What, then the fields of that kind, Place and Cancel. Place (or Enter) sends and
    /// keeps the dialog open with its values, focus back on What; Escape, Cancel or Control+B close it.
    /// NVDA reads each control by its AccessibleName and AccessibleDescription.
    /// </summary>
    public void ShowBuildDialog(BuildDialog build)
    {
        if (_modalOpen) { build.Closed(); return; }
        _modalOpen = true;
        // The B of Control+B is still down, and its release goes to the dialog.
        _input.Clear();
        var form = build.Form;

        using var dialog = new Form
        {
            Text = "Build",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(480, 640),
            KeyPreview = true,
        };
        var outer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(8) };
        int tab = 0;
        // UseMnemonic off: a label such as "Rock & roll" must not lose its ampersand.
        var whatLabel = new Label { Text = "What", AutoSize = true, UseMnemonic = false, TabIndex = tab++ };
        var what = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList, Width = 420, AccessibleName = "What",
            AccessibleDescription = "Floor, wall, roof, door, window, or a prefab from the library.", TabIndex = tab++,
        };
        what.Items.AddRange(form.Kinds.Select(k => (object)k.Label).ToArray());
        what.SelectedIndex = Math.Max(0, form.Kinds.ToList().FindIndex(k => k.Word == form.Kind));
        var fields = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, TabIndex = tab++ };
        var refusal = new Label { Text = "", AutoSize = true, MaximumSize = new Size(440, 0), UseMnemonic = false, TabIndex = tab++ };
        var place = new Button { Text = "Place", AutoSize = true, TabIndex = tab++ };
        var cancel = new Button { Text = "Cancel", AutoSize = true, TabIndex = tab++ };
        outer.Controls.AddRange(new Control[] { whatLabel, what, fields, refusal, place, cancel });
        dialog.Controls.Add(outer);
        dialog.AcceptButton = place;
        dialog.CancelButton = cancel;

        var controls = new List<(string Word, Control Control)>();
        var shown = new Dictionary<string, List<string>>();
        bool syncing = false;

        void Pull()
        {
            foreach (var (word, control) in controls)
                if (control is TextBox t) form.Set(word, t.Text);
        }

        void Refresh()
        {
            syncing = true;
            try
            {
                foreach (var (word, control) in controls)
                {
                    control.Enabled = form.IsEnabled(word);
                    switch (control)
                    {
                        case TextBox t when t.Text != form.Text(word):
                            t.Text = form.Text(word);
                            break;
                        case ComboBox c:
                        {
                            var options = form.Options(word);
                            var values = options.Select(o => o.Value).ToList();
                            if (!shown.TryGetValue(word, out var was) || !was.SequenceEqual(values))
                            {
                                c.Items.Clear();
                                c.Items.AddRange(options.Select(o => (object)o.Label).ToArray());
                                shown[word] = values;
                            }
                            int at = form.Selected(word);
                            if (at >= 0 && c.SelectedIndex != at) c.SelectedIndex = at;
                            break;
                        }
                        case CheckBox k when k.Checked != (form.Text(word) == "true"):
                            k.Checked = form.Text(word) == "true";
                            break;
                    }
                }
            }
            finally { syncing = false; }
        }

        void Rebuild()
        {
            fields.SuspendLayout();
            foreach (Control c in fields.Controls.Cast<Control>().ToList()) c.Dispose();
            fields.Controls.Clear();
            controls.Clear();
            shown.Clear();
            int inner = 0;
            foreach (var view in form.Fields)
            {
                string word = view.Word;
                switch (view.Type)
                {
                    case FieldType.Bool:
                    {
                        var check = new CheckBox { Text = view.Label, AutoSize = true, AccessibleDescription = view.Description, TabIndex = inner++, UseMnemonic = false };
                        check.CheckedChanged += (_, _) =>
                        {
                            if (syncing) return;
                            Pull();
                            form.Set(word, check.Checked ? "true" : "false");
                            Refresh();
                        };
                        fields.Controls.Add(check);
                        controls.Add((word, check));
                        break;
                    }
                    case FieldType.Choice:
                    {
                        fields.Controls.Add(new Label { Text = view.Label, AutoSize = true, UseMnemonic = false, TabIndex = inner++ });
                        var options = form.Options(word);
                        var combo = new ComboBox
                        {
                            DropDownStyle = ComboBoxStyle.DropDownList, Width = 420, AccessibleName = view.Label,
                            AccessibleDescription = view.Description, TabIndex = inner++,
                        };
                        combo.Items.AddRange(options.Select(o => (object)o.Label).ToArray());
                        shown[word] = options.Select(o => o.Value).ToList();
                        combo.SelectedIndexChanged += (_, _) =>
                        {
                            if (syncing) return;
                            var values = shown[word];
                            if (combo.SelectedIndex < 0 || combo.SelectedIndex >= values.Count) return;
                            Pull();
                            form.Set(word, values[combo.SelectedIndex]);
                            Refresh();
                        };
                        fields.Controls.Add(combo);
                        controls.Add((word, combo));
                        break;
                    }
                    default:
                    {
                        fields.Controls.Add(new Label { Text = view.Label, AutoSize = true, UseMnemonic = false, TabIndex = inner++ });
                        var box = new TextBox
                        {
                            Width = 420, Text = form.Text(word), AccessibleName = view.Label,
                            AccessibleDescription = view.Description, TabIndex = inner++,
                        };
                        fields.Controls.Add(box);
                        controls.Add((word, box));
                        break;
                    }
                }
            }
            Refresh();
            fields.ResumeLayout();
        }

        what.SelectedIndexChanged += (_, _) =>
        {
            int at = what.SelectedIndex;
            if (at < 0 || at >= form.Kinds.Count || form.Kinds[at].Word == form.Kind) return;
            Pull();
            form.SetKind(form.Kinds[at].Word);
            refusal.Text = "";
            Rebuild();
        };

        void Say(string why)
        {
            refusal.Text = why;
            _cue(UiCue.MenuEdge);
            // Focus stays where it is, so NVDA has nothing of its own to say over the reason.
            _speech.Speak(why, interrupt: true);
        }

        // Not a DialogResult button: placing keeps the dialog open for the next one.
        place.Click += (_, _) =>
        {
            Pull();
            if (build.Place() is { } why) Say(why);
            else refusal.Text = "";
        };
        bool closed = false;
        void Close()
        {
            if (closed) return;
            closed = true;
            build.Closed();
            dialog.Close();
        }
        cancel.Click += (_, _) => Close();
        dialog.KeyDown += (_, e) =>
        {
            var modifiers = (e.Control ? KeyModifiers.Control : KeyModifiers.None) | (e.Shift ? KeyModifiers.Shift : KeyModifiers.None)
                          | (e.Alt ? KeyModifiers.Alt : KeyModifiers.None);
            if (build.IsCloseKey(WinFormsKeyMap.Map(e.KeyCode), modifiers) && !(e.KeyCode == Keys.Escape && dialog.ActiveControl is ComboBox { DroppedDown: true }))
            {
                e.SuppressKeyPress = true;
                Close();
            }
        };

        // Answers arrive on the game's thread.
        void OnDialog(Action a) { if (!closed && dialog.IsHandleCreated) dialog.BeginInvoke(a); }
        build.Placed += () => OnDialog(() => { refusal.Text = ""; what.Focus(); });
        build.Refused += why => OnDialog(() => Say(why));
        build.CloseRequested += () => OnDialog(Close);

        dialog.Shown += (_, _) =>
        {
            what.Focus();
            // NVDA reads the dialog and the focused control; speaking over it would cut it off.
            if (!_speech.ScreenReaderRunning) _speech.Speak(form.Spoken, interrupt: true);
        };
        Rebuild();

        _openDialog = dialog;
        try { dialog.ShowDialog(this); }
        finally
        {
            closed = true;
            build.Closed();
            _modalOpen = false;
            _openDialog = null;
            _input.Clear();
        }
        _cue(UiCue.MenuBack);
        _speech.Speak("Build closed.", interrupt: true);
    }
}

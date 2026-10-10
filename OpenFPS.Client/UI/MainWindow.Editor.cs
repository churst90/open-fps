using OpenFPS.Client.Core;
using OpenFPS.Client.Core.Input;
using OpenFPS.Client.Core.Platform;

namespace OpenFPS.Client.UI;

/// <summary>The F12 editor dialog, drawn from the shared <see cref="EditorDialog"/>.</summary>
public sealed partial class MainWindow
{
    /// <summary>One drawn section: its group box, the panel its controls are in, and the shape it was drawn for.</summary>
    private sealed record DrawnSection(string Id, Control Frame, FlowLayoutPanel Panel, string Shape);

    private const int EditorWidth = 560;

    /// <summary>
    /// The editor dialog: a TabControl with a page for each tab (NVDA names the property page as the focus
    /// moves into it), each section a group box, and Undo, Redo and Close under the tabs. Placing and
    /// changing keep it open; F12, Escape and Close shut it. With NVDA running the game says nothing NVDA reads.
    /// </summary>
    public void ShowEditorDialog(EditorDialog editor)
    {
        if (_modalOpen) { editor.Closed(); return; }
        _modalOpen = true;
        // The F12 that opened it is still down, and its release goes to the dialog.
        _input.Clear();

        using var dialog = new Form
        {
            Text = editor.Title,
            FormBorderStyle = FormBorderStyle.Sizable,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(640, 720),
            KeyPreview = true,
        };
        var tabs = new TabControl { Dock = DockStyle.Fill, TabIndex = 0 };
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, TabIndex = 1 };
        var status = new Label { Text = "", AutoSize = true, MaximumSize = new Size(600, 0), UseMnemonic = false };
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        var undo = new Button { Text = "Undo", AutoSize = true, TabIndex = 0, UseMnemonic = false };
        var redo = new Button { Text = "Redo", AutoSize = true, TabIndex = 1, UseMnemonic = false };
        var close = new Button { Text = "Close", AutoSize = true, TabIndex = 2 };
        buttons.Controls.AddRange(new Control[] { undo, redo, close });
        bottom.Controls.Add(status);
        bottom.Controls.Add(buttons);
        dialog.Controls.Add(tabs);
        dialog.Controls.Add(bottom);
        // Docked last, so the tabs fill what the buttons leave.
        tabs.BringToFront();

        var pages = new FlowLayoutPanel[editor.Tabs.Count];
        var drawn = new List<DrawnSection>[editor.Tabs.Count];
        var widgets = new Dictionary<string, Control>();
        var models = new Dictionary<Control, DialogControl>();
        bool syncing = false;
        bool closed = false;

        for (int i = 0; i < editor.Tabs.Count; i++)
        {
            var tab = editor.Tabs[i];
            var page = new TabPage(tab.Name) { AccessibleName = tab.Name, UseVisualStyleBackColor = true };
            var content = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(6) };
            page.Controls.Add(content);
            tabs.TabPages.Add(page);
            pages[i] = content;
            drawn[i] = new List<DrawnSection>();
        }

        DialogControl? ModelOf(Control? c)
        {
            for (var at = c; at != null; at = at.Parent)
                if (models.TryGetValue(at, out var m)) return m;
            return null;
        }

        Control? Focused()
        {
            Control? at = dialog.ActiveControl;
            while (at is ContainerControl container && container.ActiveControl != null) at = container.ActiveControl;
            return at;
        }

        void Focus(DialogControl? c)
        {
            if (c != null && widgets.TryGetValue(c.Id, out var w) && w.CanFocus) w.Focus();
        }

        Control Draw(DialogControl c, FlowLayoutPanel into, ref int tab)
        {
            Control focus;
            switch (c.Kind)
            {
                case DialogControlKind.Text:
                    focus = new Label { Text = c.Label, AutoSize = true, MaximumSize = new Size(EditorWidth, 0), UseMnemonic = false, TabIndex = tab++ };
                    into.Controls.Add(focus);
                    break;
                case DialogControlKind.Entry:
                {
                    into.Controls.Add(new Label { Text = c.Label, AutoSize = true, UseMnemonic = false, TabIndex = tab++ });
                    var box = new TextBox
                    {
                        Width = EditorWidth, Text = c.Text, ReadOnly = c.ReadOnly, AccessibleName = c.Label,
                        AccessibleDescription = c.Description, TabIndex = tab++,
                    };
                    box.TextChanged += (_, _) =>
                    {
                        if (syncing) return;
                        c.Text = box.Text;
                        editor.Changed(c);
                    };
                    into.Controls.Add(box);
                    focus = box;
                    break;
                }
                case DialogControlKind.Choice:
                {
                    into.Controls.Add(new Label { Text = c.Label, AutoSize = true, UseMnemonic = false, TabIndex = tab++ });
                    var combo = new ComboBox
                    {
                        DropDownStyle = ComboBoxStyle.DropDownList, Width = EditorWidth, AccessibleName = c.Label,
                        AccessibleDescription = c.Description, TabIndex = tab++,
                    };
                    combo.Items.AddRange(c.Items.Select(o => (object)o.Label).ToArray());
                    if (c.Selected >= 0 && c.Selected < combo.Items.Count) combo.SelectedIndex = c.Selected;
                    combo.SelectedIndexChanged += (_, _) =>
                    {
                        if (syncing || combo.SelectedIndex < 0) return;
                        c.Selected = combo.SelectedIndex;
                        editor.Changed(c);
                    };
                    into.Controls.Add(combo);
                    focus = combo;
                    break;
                }
                case DialogControlKind.Check:
                {
                    var check = new CheckBox { Text = c.Label, AutoSize = true, Checked = c.Checked, AccessibleDescription = c.Description, UseMnemonic = false, TabIndex = tab++ };
                    check.CheckedChanged += (_, _) =>
                    {
                        if (syncing) return;
                        c.Checked = check.Checked;
                        editor.Changed(c);
                    };
                    into.Controls.Add(check);
                    focus = check;
                    break;
                }
                case DialogControlKind.List:
                {
                    into.Controls.Add(new Label { Text = c.Label, AutoSize = true, UseMnemonic = false, TabIndex = tab++ });
                    // A list that ticks is a CheckedListBox: NVDA says "checked" and Space ticks, as anywhere in Windows.
                    ListBox list = c.Checkable ? new CheckedListBox { CheckOnClick = false } : new ListBox();
                    list.Width = EditorWidth;
                    list.Height = 160;
                    list.IntegralHeight = false;
                    list.AccessibleName = c.Label;
                    list.AccessibleDescription = c.Description;
                    list.TabIndex = tab++;
                    Fill(list, c);
                    list.SelectedIndexChanged += (_, _) =>
                    {
                        if (syncing || list.SelectedIndex < 0) return;
                        c.Selected = list.SelectedIndex;
                        editor.Changed(c);
                    };
                    if (list is CheckedListBox ticks)
                        ticks.ItemCheck += (_, e) =>
                        {
                            if (syncing) return;
                            // The server holds the tick: the box changes when it says so.
                            e.NewValue = e.CurrentValue;
                            editor.Toggle(c, e.Index);
                        };
                    into.Controls.Add(list);
                    focus = list;
                    break;
                }
                default:
                {
                    var button = new Button { Text = c.Label, AutoSize = true, AccessibleDescription = c.Description, UseMnemonic = false, TabIndex = tab++ };
                    button.Click += (_, _) => editor.Press(c.Id);
                    into.Controls.Add(button);
                    focus = button;
                    break;
                }
            }
            focus.Enabled = c.Enabled;
            widgets[c.Id] = focus;
            models[focus] = c;
            return focus;
        }

        void Fill(ListBox list, DialogControl c)
        {
            list.BeginUpdate();
            list.Items.Clear();
            foreach (var item in c.Items)
            {
                if (list is CheckedListBox ticks) ticks.Items.Add(item.Label, item.Ticked);
                else list.Items.Add(item.Label);
            }
            if (c.Selected >= 0 && c.Selected < list.Items.Count) list.SelectedIndex = c.Selected;
            list.EndUpdate();
        }

        void Update(DialogControl c)
        {
            if (!widgets.TryGetValue(c.Id, out var w)) return;
            models.Remove(w);
            models[w] = c;
            w.Enabled = c.Enabled;
            switch (w)
            {
                case TextBox t:
                    if (t.Text != c.Text) t.Text = c.Text;
                    t.ReadOnly = c.ReadOnly;
                    t.AccessibleDescription = c.Description;
                    break;
                case ComboBox combo:
                {
                    var labels = c.Items.Select(o => o.Label).ToList();
                    if (!combo.Items.Cast<object>().Select(o => o.ToString()).SequenceEqual(labels))
                    {
                        combo.Items.Clear();
                        combo.Items.AddRange(labels.Cast<object>().ToArray());
                    }
                    if (c.Selected >= 0 && c.Selected < combo.Items.Count && combo.SelectedIndex != c.Selected) combo.SelectedIndex = c.Selected;
                    combo.AccessibleDescription = c.Description;
                    break;
                }
                case CheckBox k:
                    if (k.Checked != c.Checked) k.Checked = c.Checked;
                    break;
                case ListBox list:
                {
                    if (list.Items.Count == c.Items.Count)
                    {
                        // Same rows: their words and ticks change where they stand, so the focus stays on its row.
                        for (int i = 0; i < c.Items.Count; i++)
                        {
                            if (list.Items[i]?.ToString() != c.Items[i].Label) list.Items[i] = c.Items[i].Label;
                            if (list is CheckedListBox ticks && ticks.GetItemChecked(i) != c.Items[i].Ticked) ticks.SetItemChecked(i, c.Items[i].Ticked);
                        }
                        if (c.Selected >= 0 && list.SelectedIndex != c.Selected) list.SelectedIndex = c.Selected;
                    }
                    else Fill(list, c);
                    break;
                }
                case Button b:
                    if (b.Text != c.Label) b.Text = c.Label;
                    b.AccessibleDescription = c.Description;
                    break;
                case Label t:
                    // A line of words (what is being laid): its words change where it stands.
                    if (t.Text != c.Label) t.Text = c.Label;
                    break;
            }
        }

        void Forget(DrawnSection section)
        {
            foreach (var part in section.Shape.Split('|', StringSplitOptions.RemoveEmptyEntries))
            {
                string id = part[..part.LastIndexOf(':')];
                if (widgets.Remove(id, out var w)) models.Remove(w);
            }
        }

        bool Holds(DrawnSection section, string? id)
            => id != null && section.Shape.Split('|').Any(p => p.StartsWith(id + ":", StringComparison.Ordinal));

        void Render(int index)
        {
            var tab = editor.Tabs[index];
            var content = pages[index];
            string? focusedId = ModelOf(Focused())?.Id;
            bool focusLost = false;
            syncing = true;
            content.SuspendLayout();
            try
            {
                var old = drawn[index].ToDictionary(d => d.Id);
                var keep = tab.Sections.Select(s => s.Id).ToHashSet();
                foreach (var gone in old.Values.Where(d => !keep.Contains(d.Id)).ToList())
                {
                    focusLost |= Holds(gone, focusedId);
                    Forget(gone);
                    content.Controls.Remove(gone.Frame);
                    gone.Frame.Dispose();
                    old.Remove(gone.Id);
                }
                var now = new List<DrawnSection>();
                for (int s = 0; s < tab.Sections.Count; s++)
                {
                    var section = tab.Sections[s];
                    if (old.TryGetValue(section.Id, out var was) && was.Shape == section.Shape)
                    {
                        if (was.Frame is GroupBox g && g.Text != section.Title) { g.Text = section.Title; g.AccessibleName = section.Title; }
                        foreach (var c in section.Controls) Update(c);
                        content.Controls.SetChildIndex(was.Frame, s);
                        was.Frame.TabIndex = s;
                        now.Add(was);
                        continue;
                    }
                    if (was != null)
                    {
                        focusLost |= Holds(was, focusedId);
                        Forget(was);
                        content.Controls.Remove(was.Frame);
                        was.Frame.Dispose();
                    }
                    var panel = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Dock = DockStyle.Fill };
                    Control frame = panel;
                    if (section.Title.Length > 0)
                    {
                        // A group box: NVDA names the part the focus has moved into.
                        var group = new GroupBox { Text = section.Title, AccessibleName = section.Title, AutoSize = true, Width = EditorWidth + 30, Padding = new Padding(6) };
                        group.Controls.Add(panel);
                        frame = group;
                    }
                    int inner = 0;
                    foreach (var c in section.Controls) Draw(c, panel, ref inner);
                    content.Controls.Add(frame);
                    content.Controls.SetChildIndex(frame, s);
                    frame.TabIndex = s;
                    now.Add(new DrawnSection(section.Id, frame, panel, section.Shape));
                }
                drawn[index] = now;
            }
            finally
            {
                content.ResumeLayout();
                syncing = false;
            }
            if (focusLost && index == editor.Current) Focus(tab.Find(focusedId!) ?? tab.First);
        }

        void RenderFooter()
        {
            var f = editor.Footer.Controls;
            undo.Text = f[0].Label;
            undo.Enabled = f[0].Enabled;
            redo.Text = f[1].Label;
            redo.Enabled = f[1].Enabled;
        }
        undo.Click += (_, _) => editor.Press("foot.undo");
        redo.Click += (_, _) => editor.Press("foot.redo");

        void Say(string why)
        {
            status.Text = why;
            _cue(UiCue.MenuEdge);
            // Said through NVDA when it runs: the focus has not moved, so NVDA has nothing of its own to say.
            _speech.Speak(why, interrupt: true);
        }

        void Close()
        {
            if (closed) return;
            closed = true;
            editor.Closed();
            dialog.Close();
        }
        close.Click += (_, _) => Close();

        void Show(int index)
        {
            syncing = true;
            try { tabs.SelectedIndex = index; }
            finally { syncing = false; }
            status.Text = "";
            Focus(editor.Tabs[index].First);
            // NVDA names the property page and the control as the focus lands; without it the game says the tab.
            if (!_speech.ScreenReaderRunning) _speech.Speak($"{editor.Tabs[index].Name} tab.", interrupt: true);
        }

        // Control+Tab, Control+Shift+Tab and Control+Page Down and Up are the TabControl's own: the dialog
        // is told, and the focus goes to the first control of the page.
        tabs.SelectedIndexChanged += (_, _) =>
        {
            if (syncing || closed || tabs.SelectedIndex < 0 || tabs.SelectedIndex == editor.Current) return;
            editor.ShowTab(tabs.SelectedIndex);
        };

        // Everything the dialog says arrives here on the UI thread, after what raised it has finished.
        void OnDialog(Action a) { if (!closed && dialog.IsHandleCreated) dialog.BeginInvoke(() => { if (!closed) a(); }); }
        editor.UpdateArrived += () => OnDialog(editor.ApplyPending);
        editor.TabUpdated += tab => OnDialog(() => Render(editor.Tabs.ToList().IndexOf(tab)));
        editor.FooterUpdated += () => OnDialog(RenderFooter);
        editor.TabShown += i => OnDialog(() => Show(i));
        editor.Said += why => OnDialog(() => Say(why));
        editor.FocusAsked += id => OnDialog(() => Focus(editor.CurrentTab.Find(id)));
        editor.ConfirmAsked += (question, yes) => OnDialog(() =>
        {
            // No is the default, so a stray Enter deletes nothing.
            if (MessageBox.Show(dialog, question, "World editor", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
                yes();
        });
        editor.CloseRequested += () => OnDialog(Close);

        dialog.KeyDown += (_, e) =>
        {
            var focus = Focused();
            bool dropped = focus is ComboBox { DroppedDown: true };
            var modifiers = (e.Control ? KeyModifiers.Control : KeyModifiers.None) | (e.Shift ? KeyModifiers.Shift : KeyModifiers.None)
                          | (e.Alt ? KeyModifiers.Alt : KeyModifiers.None);
            if (!dropped && editor.IsCloseKey(WinFormsKeyMap.Map(e.KeyCode), modifiers))
            {
                e.SuppressKeyPress = true;
                Close();
                return;
            }
            // Under the tabs (Undo, Redo, Close) the TabControl does not see Control+Tab, so it is done here.
            if (e.Control && !tabs.ContainsFocus && e.KeyCode is Keys.Tab or Keys.PageDown or Keys.PageUp)
            {
                e.SuppressKeyPress = true;
                editor.NextTab(e.KeyCode == Keys.PageUp || (e.KeyCode == Keys.Tab && e.Shift) ? -1 : 1);
                return;
            }
            // Delete on a list that has a Remove presses it for the row chosen (it asks first).
            if (e.KeyCode == Keys.Delete && e.Modifiers == Keys.None && !dropped && ModelOf(focus) is { Delete: { } remove } removing)
            {
                e.SuppressKeyPress = true;
                if (focus is ListBox chosenList) removing.Selected = chosenList.SelectedIndex;
                editor.Press(remove);
                return;
            }
            if (e.KeyCode != Keys.Enter || dropped || focus is Button) return;
            // Enter presses the button the control names: Place from the prefab list, Apply from a field.
            if (ModelOf(focus) is { Enter: { } press } c)
            {
                e.SuppressKeyPress = true;
                if (focus is ListBox list) c.Selected = list.SelectedIndex;
                editor.Press(press);
            }
        };

        dialog.Shown += (_, _) =>
        {
            Show(editor.Current);
            if (!_speech.ScreenReaderRunning)
                _speech.Speak($"{editor.Title}. {editor.CurrentTab.Name} tab. Control Tab changes tab, Escape or F12 closes.", interrupt: true);
        };
        for (int i = 0; i < editor.Tabs.Count; i++) Render(i);
        RenderFooter();
        syncing = true;
        tabs.SelectedIndex = editor.Current;
        syncing = false;

        _openDialog = dialog;
        try { dialog.ShowDialog(this); }
        finally
        {
            closed = true;
            editor.Closed();
            _modalOpen = false;
            _openDialog = null;
            _input.Clear();
        }
        _cue(UiCue.MenuBack);
        _speech.Speak("Editor closed.", interrupt: true);
    }
}

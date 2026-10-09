using Gtk;
using OpenFPS.Client.Core;
using OpenFPS.Client.Core.Input;
using OpenFPS.Client.Core.Platform;

namespace OpenFPS.Client.Gtk.Game;

/// <summary>The F12 editor dialog, drawn from the shared <see cref="EditorDialog"/>.</summary>
internal sealed partial class GtkClientShell
{
    private const uint GdkTab = 0xff09, GdkIsoLeftTab = 0xfe20, GdkPageUp = 0xff55, GdkPageDown = 0xff56, GdkSpace = 0x20;

    /// <summary>One drawn section: its frame, the box its controls are in, and the shape it was drawn for.</summary>
    private sealed record DrawnSection(string Id, Widget Frame, Box Box, string Shape);

    public void ShowEditorDialog(EditorDialog editor) => OnUi(() =>
    {
        if (_consoleOpen) { editor.Closed(); return; }
        _consoleOpen = true;
        // The F12 that opened it is still down; its key-up goes to the dialog.
        _input.Clear();

        var dialog = Window.New();
        _modal = dialog;
        dialog.Title = editor.Title;
        dialog.SetModal(true);
        dialog.SetDefaultSize(620, 720);
        if (_gameWindow?.Toplevel != null) dialog.SetTransientFor(_gameWindow.Toplevel);

        var outer = Box.New(Orientation.Vertical, 6);
        outer.MarginTop = outer.MarginBottom = outer.MarginStart = outer.MarginEnd = 12;
        var notebook = Notebook.New();
        notebook.SetVexpand(true);
        outer.Append(notebook);
        // Why something was refused, shown as well as said.
        var status = Label.New("");
        status.SetWrap(true);
        status.SetXalign(0);
        outer.Append(status);
        var footer = Box.New(Orientation.Horizontal, 6);
        outer.Append(footer);

        // Each control's widget and its model, by id, so a control drawn again gets the focus back.
        var widgets = new Dictionary<string, Widget>();
        var models = new Dictionary<Widget, DialogControl>();
        var drawn = new List<DrawnSection>[editor.Tabs.Count];
        var pages = new Box[editor.Tabs.Count];
        bool syncing = false;
        bool closed = false;

        for (int i = 0; i < editor.Tabs.Count; i++)
        {
            var tab = editor.Tabs[i];
            var content = Box.New(Orientation.Vertical, 8);
            content.MarginTop = content.MarginBottom = content.MarginStart = content.MarginEnd = 8;
            var scroll = ScrolledWindow.New();
            scroll.SetChild(content);
            scroll.SetVexpand(true);
            // A frame named for the tab: Orca says its name as the focus moves into the page.
            var frame = Frame.New(tab.Name);
            frame.SetChild(scroll);
            notebook.AppendPage(frame, Label.New(tab.Name));
            pages[i] = content;
            drawn[i] = new List<DrawnSection>();
        }

        DialogControl? ModelOf(Widget? w)
        {
            for (var at = w; at != null; at = at.GetParent())
                if (models.TryGetValue(at, out var c)) return c;
            return null;
        }

        void Focus(DialogControl? c)
        {
            if (c != null && widgets.TryGetValue(c.Id, out var w)) w.GrabFocus();
        }

        // The control after (or before) one in its tab that can take the focus.
        DialogControl? Neighbour(DialogControl c, int step)
        {
            var all = editor.CurrentTab.Controls.Where(x => x.Kind != DialogControlKind.Text && x.Enabled).ToList();
            int at = all.FindIndex(x => x.Id == c.Id);
            int next = at + step;
            if (at >= 0 && next >= 0 && next < all.Count) return all[next];
            return null;
        }

        Widget Draw(DialogControl c, Box into)
        {
            Widget focus;
            switch (c.Kind)
            {
                case DialogControlKind.Text:
                {
                    var label = Label.New(c.Label);
                    label.SetWrap(true);
                    label.SetXalign(0);
                    into.Append(label);
                    focus = label;
                    break;
                }
                case DialogControlKind.Entry:
                {
                    // The mnemonic widget makes the label the entry's accessible name; the tooltip is its description.
                    var label = Label.New(c.Label);
                    label.SetXalign(0);
                    var entry = Entry.New();
                    entry.SetText(c.Text);
                    if (c.Description.Length > 0) entry.SetTooltipText(c.Description);
                    entry.SetEditable(!c.ReadOnly);
                    label.SetMnemonicWidget(entry);
                    entry.OnNotify += (_, e) =>
                    {
                        if (syncing || e.Pspec.GetName() != "text") return;
                        c.Text = entry.GetText();
                        editor.Changed(c);
                    };
                    into.Append(label);
                    into.Append(entry);
                    focus = entry;
                    break;
                }
                case DialogControlKind.Choice:
                {
                    var label = Label.New(c.Label);
                    label.SetXalign(0);
                    var drop = DropDown.NewFromStrings(c.Items.Select(o => o.Label).ToArray());
                    if (c.Selected >= 0) drop.SetSelected((uint)c.Selected);
                    if (c.Description.Length > 0) drop.SetTooltipText(c.Description);
                    label.SetMnemonicWidget(drop);
                    drop.OnNotify += (_, e) =>
                    {
                        if (syncing || e.Pspec.GetName() != "selected") return;
                        int at = (int)drop.GetSelected();
                        if (at < 0 || at >= c.Items.Count) return;
                        c.Selected = at;
                        editor.Changed(c);
                    };
                    into.Append(label);
                    into.Append(drop);
                    focus = drop;
                    break;
                }
                case DialogControlKind.Check:
                {
                    var check = CheckButton.NewWithLabel(c.Label);
                    check.SetActive(c.Checked);
                    if (c.Description.Length > 0) check.SetTooltipText(c.Description);
                    check.OnToggled += (_, _) =>
                    {
                        if (syncing) return;
                        c.Checked = check.GetActive();
                        editor.Changed(c);
                    };
                    into.Append(check);
                    focus = check;
                    break;
                }
                case DialogControlKind.List:
                {
                    var label = Label.New(c.Label);
                    label.SetXalign(0);
                    var list = ListBox.New();
                    list.SetSelectionMode(SelectionMode.Single);
                    if (c.Description.Length > 0) list.SetTooltipText(c.Description);
                    label.SetMnemonicWidget(list);
                    Fill(list, c);
                    list.OnRowSelected += (_, e) =>
                    {
                        if (syncing || e.Row == null) return;
                        c.Selected = e.Row.GetIndex();
                        editor.Changed(c);
                    };
                    list.OnRowActivated += (_, e) =>
                    {
                        c.Selected = e.Row.GetIndex();
                        if (c.Enter != null) editor.Press(c.Enter);
                    };
                    var scroll = ScrolledWindow.New();
                    scroll.SetChild(list);
                    scroll.SetMinContentHeight(120);
                    scroll.SetMaxContentHeight(260);
                    scroll.SetPropagateNaturalHeight(true);
                    into.Append(label);
                    into.Append(scroll);
                    focus = list;
                    break;
                }
                default:
                {
                    var button = Button.NewWithLabel(c.Label);
                    if (c.Description.Length > 0) button.SetTooltipText(c.Description);
                    button.OnClicked += (_, _) => editor.Press(c.Id);
                    into.Append(button);
                    focus = button;
                    break;
                }
            }
            focus.SetSensitive(c.Enabled);
            widgets[c.Id] = focus;
            models[focus] = c;
            return focus;
        }

        void Fill(ListBox list, DialogControl c)
        {
            while (list.GetFirstChild() is { } child) list.Remove(child);
            foreach (var item in c.Items)
            {
                var row = ListBoxRow.New();
                var text = Label.New(item.Said);
                text.SetXalign(0);
                text.SetWrap(true);
                row.SetChild(text);
                list.Append(row);
            }
            if (c.Selected >= 0 && list.GetRowAtIndex(c.Selected) is { } chosen) list.SelectRow(chosen);
        }

        // A control drawn before, given the model's values now, where it stands.
        void Update(DialogControl c)
        {
            if (!widgets.TryGetValue(c.Id, out var w)) return;
            models.Remove(w);
            models[w] = c;
            w.SetSensitive(c.Enabled);
            switch (w)
            {
                case Entry e:
                    if (e.GetText() != c.Text) e.SetText(c.Text);
                    e.SetTooltipText(c.Description);
                    e.SetEditable(!c.ReadOnly);
                    break;
                case DropDown d:
                {
                    var model = d.GetModel() as StringList;
                    bool same = model != null && model.GetNItems() == c.Items.Count
                                && c.Items.Select((o, i) => model.GetString((uint)i) == o.Label).All(x => x);
                    if (!same) d.SetModel(StringList.New(c.Items.Select(o => o.Label).ToArray()));
                    if (c.Selected >= 0 && d.GetSelected() != (uint)c.Selected) d.SetSelected((uint)c.Selected);
                    d.SetTooltipText(c.Description);
                    break;
                }
                case CheckButton k:
                    if (k.GetActive() != c.Checked) k.SetActive(c.Checked);
                    k.SetLabel(c.Label);
                    break;
                case ListBox l:
                {
                    bool focused = ModelOf(dialog.GetFocus()) == c;
                    int count = 0;
                    for (var r = l.GetFirstChild(); r != null; r = r.GetNextSibling()) count++;
                    if (count == c.Items.Count)
                    {
                        // Same rows: their words change where they stand, so the focus stays on its row.
                        int i = 0;
                        for (var r = l.GetFirstChild(); r != null; r = r.GetNextSibling(), i++)
                            if (r is ListBoxRow row && row.GetChild() is Label t && t.GetText() != c.Items[i].Said) t.SetText(c.Items[i].Said);
                        var selected = l.GetSelectedRow();
                        if (c.Selected >= 0 && selected?.GetIndex() != c.Selected && l.GetRowAtIndex(c.Selected) is { } want) l.SelectRow(want);
                        if (c.Selected < 0 && selected != null) l.SelectRow(null);
                    }
                    else
                    {
                        Fill(l, c);
                        if (focused) { if (l.GetSelectedRow() is { } row) row.GrabFocus(); else l.GrabFocus(); }
                    }
                    break;
                }
                case Button b:
                    if (b.GetLabel() != c.Label) b.SetLabel(c.Label);
                    b.SetTooltipText(c.Description);
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

        void Render(int index)
        {
            var tab = editor.Tabs[index];
            var content = pages[index];
            var focusedId = ModelOf(dialog.GetFocus())?.Id;
            bool focusLost = false;
            syncing = true;
            try
            {
                var old = drawn[index].ToDictionary(d => d.Id);
                var now = new List<DrawnSection>();
                Widget? previous = null;
                var keep = tab.Sections.Select(s => s.Id).ToHashSet();
                foreach (var gone in old.Values.Where(d => !keep.Contains(d.Id)).ToList())
                {
                    if (focusedId != null && gone.Shape.Split('|').Any(p => p.StartsWith(focusedId + ":", StringComparison.Ordinal))) focusLost = true;
                    Forget(gone);
                    content.Remove(gone.Frame);
                    old.Remove(gone.Id);
                }
                foreach (var section in tab.Sections)
                {
                    if (old.TryGetValue(section.Id, out var was) && was.Shape == section.Shape)
                    {
                        old.Remove(section.Id);
                        if (was.Frame is Frame f && f.GetLabel() != section.Title && section.Title.Length > 0) f.SetLabel(section.Title);
                        foreach (var c in section.Controls) Update(c);
                        content.ReorderChildAfter(was.Frame, previous);
                        now.Add(was);
                        previous = was.Frame;
                        continue;
                    }
                    if (was != null)
                    {
                        old.Remove(section.Id);
                        if (focusedId != null && was.Shape.Split('|').Any(p => p.StartsWith(focusedId + ":", StringComparison.Ordinal))) focusLost = true;
                        Forget(was);
                        content.Remove(was.Frame);
                    }
                    var box = Box.New(Orientation.Vertical, 4);
                    Widget frame = box;
                    if (section.Title.Length > 0)
                    {
                        // A titled section is a frame, so Orca names the part the focus has moved into.
                        var titled = Frame.New(section.Title);
                        box.MarginTop = box.MarginBottom = box.MarginStart = box.MarginEnd = 6;
                        titled.SetChild(box);
                        frame = titled;
                    }
                    foreach (var c in section.Controls) Draw(c, box);
                    content.InsertChildAfter(frame, previous);
                    var drawnNow = new DrawnSection(section.Id, frame, box, section.Shape);
                    now.Add(drawnNow);
                    previous = frame;
                }
                drawn[index] = now;
            }
            finally { syncing = false; }
            // A section drawn again took the focused control with it: the new one has it back.
            if (focusLost && index == editor.Current) Focus(tab.Find(focusedId!) ?? tab.First);
        }

        var undo = Button.NewWithLabel("Undo");
        var redo = Button.NewWithLabel("Redo");
        var close = Button.NewWithLabel("Close");
        footer.Append(undo);
        footer.Append(redo);
        footer.Append(close);
        void RenderFooter()
        {
            var f = editor.Footer.Controls;
            undo.SetLabel(f[0].Label);
            undo.SetSensitive(f[0].Enabled);
            redo.SetLabel(f[1].Label);
            redo.SetSensitive(f[1].Enabled);
        }
        undo.OnClicked += (_, _) => editor.Press("foot.undo");
        redo.OnClicked += (_, _) => editor.Press("foot.redo");

        void Say(string why)
        {
            status.SetText(why);
            _cue(UiCue.MenuEdge);
            _speech.Speak(why, interrupt: true);
        }

        void Close()
        {
            if (closed) return;
            closed = true;
            editor.Closed();
            dialog.Close();
            _cue(UiCue.MenuBack);
            _speech.Speak("Editor closed.", interrupt: true);
        }
        close.OnClicked += (_, _) => Close();

        void Show(int index)
        {
            syncing = true;
            try { notebook.SetCurrentPage(index); }
            finally { syncing = false; }
            status.SetText("");
            Focus(editor.Tabs[index].First);
        }

        notebook.OnSwitchPage += (_, e) =>
        {
            // A tab chosen with the mouse or GTK's own keys: the dialog is told, as Control+Tab tells it.
            if (syncing || closed || (int)e.PageNum == editor.Current) return;
            editor.ShowTab((int)e.PageNum);
        };

        editor.UpdateArrived += () => OnUi(() => { if (!closed) editor.ApplyPending(); });
        editor.TabUpdated += tab => OnUi(() => { if (!closed) Render(editor.Tabs.ToList().IndexOf(tab)); });
        editor.FooterUpdated += () => OnUi(() => { if (!closed) RenderFooter(); });
        editor.TabShown += i => OnUi(() => { if (!closed) Show(i); });
        editor.Said += why => OnUi(() => { if (!closed) Say(why); });
        editor.FocusAsked += id => OnUi(() => { if (!closed) Focus(editor.CurrentTab.Find(id)); });
        editor.ConfirmAsked += (question, yes) => OnUi(() => { if (!closed) Confirm(dialog, question, yes); });
        editor.CloseRequested += () => OnUi(Close);

        dialog.OnCloseRequest += (_, _) =>
        {
            closed = true;
            editor.Closed();
            _consoleOpen = false;
            _modal = null;
            _input.Clear();
            return false;
        };

        var keys = EventControllerKey.New();
        keys.SetPropagationPhase(PropagationPhase.Capture);
        keys.OnKeyPressed += (_, e) =>
        {
            var focus = dialog.GetFocus();
            // An open drop-down list has its own Escape, Enter and arrows.
            if (Ancestor<Popover>(focus) != null) return false;
            bool control = (e.State & Gdk.ModifierType.ControlMask) != 0;
            bool shift = (e.State & Gdk.ModifierType.ShiftMask) != 0;
            var modifiers = (control ? KeyModifiers.Control : KeyModifiers.None) | (shift ? KeyModifiers.Shift : KeyModifiers.None);
            if ((e.State & Gdk.ModifierType.AltMask) != 0) modifiers |= KeyModifiers.Alt;
            var key = e.Keyval == GdkEscape ? GameKey.Escape : GtkKeyMap.Map(e.Keyval);
            if (editor.IsCloseKey(key, modifiers)) { Close(); return true; }

            if (control && e.Keyval is GdkTab or GdkIsoLeftTab) { editor.NextTab(shift || e.Keyval == GdkIsoLeftTab ? -1 : 1); return true; }
            if (control && e.Keyval is GdkPageDown or GdkPageUp) { editor.NextTab(e.Keyval == GdkPageDown ? 1 : -1); return true; }

            var c = ModelOf(focus);
            if (c == null) return false;
            var drop = Ancestor<DropDown>(focus);
            if (e.Keyval is GdkReturn or GdkKpEnter)
            {
                // A button does its own thing; elsewhere Enter presses the button the control names.
                if (focus is Button || c.Enter == null) return false;
                if (c.Kind == DialogControlKind.List && Ancestor<ListBoxRow>(focus) is { } row) c.Selected = row.GetIndex();
                editor.Press(c.Enter);
                return true;
            }
            // Up and Down change a closed drop-down's choice, as on Windows; the game says the new one.
            if (drop != null && modifiers == KeyModifiers.None && e.Keyval is GdkUp or GdkDown)
            {
                uint count = drop.GetModel()?.GetNItems() ?? 0;
                if (count == 0) return true;
                long next = (long)drop.GetSelected() + (e.Keyval == GdkDown ? 1 : -1);
                if (next < 0 || next >= count) { _cue(UiCue.MenuEdge); return true; }
                drop.SetSelected((uint)next);
                if (drop.GetModel() is StringList list && list.GetString((uint)next) is { } said) _speech.Speak(said, interrupt: true);
                return true;
            }
            if (c.Kind == DialogControlKind.List)
            {
                // Space ticks a row; Tab leaves the list rather than stepping through its rows.
                // Space on a list that does not tick does nothing: GTK would activate the row, and place it.
                if (e.Keyval == GdkSpace && modifiers == KeyModifiers.None)
                {
                    if (c.Checkable && Ancestor<ListBoxRow>(focus) is { } ticked) editor.Toggle(c, ticked.GetIndex());
                    return true;
                }
                if (e.Keyval is GdkTab or GdkIsoLeftTab && !control)
                {
                    bool back = shift || e.Keyval == GdkIsoLeftTab;
                    var next = Neighbour(c, back ? -1 : 1);
                    if (next != null) Focus(next);
                    else if (back) return false;
                    else (undo.GetSensitive() ? undo : close).GrabFocus();
                    return true;
                }
            }
            return false;
        };
        dialog.AddController(keys);

        for (int i = 0; i < editor.Tabs.Count; i++) Render(i);
        RenderFooter();
        dialog.SetChild(outer);
        dialog.Present();
        Show(editor.Current);
    });

    /// <summary>Yes or No, over the editor; the focus starts on No, so a stray Enter deletes nothing.</summary>
    private void Confirm(Window over, string question, Action yes)
    {
        var ask = Window.New();
        ask.Title = question;
        ask.SetModal(true);
        ask.SetTransientFor(over);
        ask.SetDefaultSize(320, 120);
        var box = Box.New(Orientation.Vertical, 8);
        box.MarginTop = box.MarginBottom = box.MarginStart = box.MarginEnd = 16;
        box.Append(Label.New(question));
        var buttons = Box.New(Orientation.Horizontal, 8);
        var yesButton = Button.NewWithLabel("Yes");
        var noButton = Button.NewWithLabel("No");
        buttons.Append(yesButton);
        buttons.Append(noButton);
        box.Append(buttons);
        yesButton.OnClicked += (_, _) => { ask.Close(); yes(); };
        noButton.OnClicked += (_, _) => ask.Close();
        var keys = EventControllerKey.New();
        keys.SetPropagationPhase(PropagationPhase.Capture);
        keys.OnKeyPressed += (_, e) =>
        {
            if (e.Keyval != GdkEscape) return false;
            ask.Close();
            return true;
        };
        ask.AddController(keys);
        ask.SetChild(box);
        ask.Present();
        noButton.GrabFocus();
    }
}

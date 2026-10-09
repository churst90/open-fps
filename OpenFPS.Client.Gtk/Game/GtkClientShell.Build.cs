using Gtk;
using OpenFPS.Client.Core;
using OpenFPS.Client.Core.Input;
using OpenFPS.Client.Core.Platform;
using OpenFPS.Common.Editing;

namespace OpenFPS.Client.Gtk.Game;

/// <summary>The build dialog (Control+B), drawn from the shared <see cref="BuildForm"/>.</summary>
internal sealed partial class GtkClientShell
{
    private const uint GdkEscape = 0xff1b, GdkReturn = 0xff0d, GdkKpEnter = 0xff8d, GdkUp = 0xff52, GdkDown = 0xff54;

    public void ShowBuildDialog(BuildDialog build) => OnUi(() =>
    {
        if (_consoleOpen) { build.Closed(); return; }
        _consoleOpen = true;
        // The B of Control+B is still down; its key-up goes to the dialog.
        _input.Clear();
        var form = build.Form;

        var dialog = Window.New();
        _modal = dialog;
        dialog.Title = "Build";
        dialog.SetModal(true);
        dialog.SetDefaultSize(460, 640);
        if (_gameWindow?.Toplevel != null) dialog.SetTransientFor(_gameWindow.Toplevel);

        var box = Box.New(Orientation.Vertical, 6);
        box.MarginTop = box.MarginBottom = box.MarginStart = box.MarginEnd = 16;

        // The mnemonic widget makes each label its control's accessible name; the tooltip is the description.
        var whatLabel = Label.New("What");
        whatLabel.SetXalign(0);
        var what = DropDown.NewFromStrings(form.Kinds.Select(k => k.Label).ToArray());
        what.SetSelected((uint)Math.Max(0, form.Kinds.ToList().FindIndex(k => k.Word == form.Kind)));
        what.SetTooltipText("Floor, wall, roof, door, window, or a prefab from the library.");
        whatLabel.SetMnemonicWidget(what);
        box.Append(whatLabel);
        box.Append(what);

        var fields = Box.New(Orientation.Vertical, 6);
        box.Append(fields);
        // Why a placing was refused, shown as well as said.
        var refusal = Label.New("");
        refusal.SetWrap(true);
        refusal.SetXalign(0);
        box.Append(refusal);
        var place = Button.NewWithLabel("Place");
        box.Append(place);
        var cancel = Button.NewWithLabel("Cancel");
        box.Append(cancel);

        var controls = new List<(string Word, Widget Widget)>();
        var shown = new Dictionary<string, List<string>>();
        bool syncing = false;

        // What is typed is read back before anything else changes the form.
        void Pull()
        {
            foreach (var (word, widget) in controls)
                if (widget is Entry e) form.Set(word, e.GetText());
        }

        void Refresh()
        {
            syncing = true;
            try
            {
                foreach (var (word, widget) in controls)
                {
                    widget.SetSensitive(form.IsEnabled(word));
                    switch (widget)
                    {
                        case Entry e when e.GetText() != form.Text(word):
                            e.SetText(form.Text(word));
                            break;
                        case DropDown d:
                        {
                            var options = form.Options(word);
                            var values = options.Select(o => o.Value).ToList();
                            if (!shown.TryGetValue(word, out var was) || !was.SequenceEqual(values))
                            {
                                d.SetModel(StringList.New(options.Select(o => o.Label).ToArray()));
                                shown[word] = values;
                            }
                            int at = form.Selected(word);
                            if (at >= 0 && d.GetSelected() != (uint)at) d.SetSelected((uint)at);
                            break;
                        }
                        case CheckButton c when c.GetActive() != (form.Text(word) == "true"):
                            c.SetActive(form.Text(word) == "true");
                            break;
                    }
                }
            }
            finally { syncing = false; }
        }

        void Rebuild()
        {
            while (fields.GetFirstChild() is { } child) fields.Remove(child);
            controls.Clear();
            shown.Clear();
            foreach (var view in form.Fields)
            {
                string word = view.Word;
                switch (view.Type)
                {
                    case FieldType.Bool:
                    {
                        var check = CheckButton.NewWithLabel(view.Label);
                        check.SetTooltipText(view.Description);
                        check.OnToggled += (_, _) =>
                        {
                            if (syncing) return;
                            Pull();
                            form.Set(word, check.GetActive() ? "true" : "false");
                            Refresh();
                        };
                        fields.Append(check);
                        controls.Add((word, check));
                        break;
                    }
                    case FieldType.Choice:
                    {
                        var label = Label.New(view.Label);
                        label.SetXalign(0);
                        var options = form.Options(word);
                        var drop = DropDown.NewFromStrings(options.Select(o => o.Label).ToArray());
                        shown[word] = options.Select(o => o.Value).ToList();
                        drop.SetTooltipText(view.Description);
                        label.SetMnemonicWidget(drop);
                        drop.OnNotify += (_, e) =>
                        {
                            if (syncing || e.Pspec.GetName() != "selected") return;
                            var values = shown[word];
                            int at = (int)drop.GetSelected();
                            if (at < 0 || at >= values.Count) return;
                            Pull();
                            form.Set(word, values[at]);
                            Refresh();
                        };
                        fields.Append(label);
                        fields.Append(drop);
                        controls.Add((word, drop));
                        break;
                    }
                    default:
                    {
                        var label = Label.New(view.Label);
                        label.SetXalign(0);
                        var entry = Entry.New();
                        entry.SetText(form.Text(word));
                        entry.SetTooltipText(view.Description);
                        label.SetMnemonicWidget(entry);
                        fields.Append(label);
                        fields.Append(entry);
                        controls.Add((word, entry));
                        break;
                    }
                }
            }
            Refresh();
        }

        what.OnNotify += (_, e) =>
        {
            if (e.Pspec.GetName() != "selected") return;
            int at = (int)what.GetSelected();
            if (at < 0 || at >= form.Kinds.Count || form.Kinds[at].Word == form.Kind) return;
            Pull();
            form.SetKind(form.Kinds[at].Word);
            refusal.SetText("");
            Rebuild();
        };

        void Say(string why)
        {
            refusal.SetText(why);
            _cue(UiCue.MenuEdge);
            _speech.Speak(why, interrupt: true);
        }

        void Place()
        {
            Pull();
            if (build.Place() is { } why) Say(why);
            else refusal.SetText("");
        }
        place.OnClicked += (_, _) => Place();

        bool closed = false;
        void Close()
        {
            if (closed) return;
            closed = true;
            build.Closed();
            dialog.Close();
            _cue(UiCue.MenuBack);
            _speech.Speak("Build closed.", interrupt: true);
        }
        cancel.OnClicked += (_, _) => Close();

        // Placed: the window stays, values kept, focus back on What for the next one. The session says what was placed.
        build.Placed += () => OnUi(() => { if (closed) return; refusal.SetText(""); what.GrabFocus(); });
        build.Refused += why => OnUi(() => { if (!closed) Say(why); });
        build.CloseRequested += () => OnUi(Close);

        dialog.OnCloseRequest += (_, _) =>
        {
            closed = true;
            build.Closed();
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
            var modifiers = (e.State & Gdk.ModifierType.ControlMask) != 0 ? KeyModifiers.Control : KeyModifiers.None;
            if ((e.State & Gdk.ModifierType.ShiftMask) != 0) modifiers |= KeyModifiers.Shift;
            if ((e.State & Gdk.ModifierType.AltMask) != 0) modifiers |= KeyModifiers.Alt;
            var key = e.Keyval == GdkEscape ? GameKey.Escape : GtkKeyMap.Map(e.Keyval);
            if (build.IsCloseKey(key, modifiers)) { Close(); return true; }

            var drop = Ancestor<DropDown>(focus);
            if (e.Keyval is GdkReturn or GdkKpEnter)
            {
                // Enter places from anywhere but a button, which does its own thing (Cancel cancels).
                if (drop == null && focus is Button) return false;
                Place();
                return true;
            }
            // Up and Down change a closed drop-down's choice, as they do on Windows; Space opens it.
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
            return false;
        };
        dialog.AddController(keys);

        Rebuild();
        // A roof has ten fields: scrolled, so Place and Cancel are never off the bottom.
        var scroll = ScrolledWindow.New();
        scroll.SetChild(box);
        dialog.SetChild(scroll);
        dialog.Present();
        what.GrabFocus();
        _speech.Speak(form.Spoken, interrupt: true);
    });

    private static T? Ancestor<T>(Widget? widget) where T : Widget
    {
        for (var w = widget; w != null; w = w.GetParent())
            if (w is T found) return found;
        return null;
    }
}

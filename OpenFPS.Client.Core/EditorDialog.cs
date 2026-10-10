using System.Globalization;
using OpenFPS.Common.Editing;
using OpenFPS.Common.Networking;

namespace OpenFPS.Client.Core;

/// <summary>The kinds of control the editor dialog is made of; each head draws them with its own.</summary>
public enum DialogControlKind { Text, Entry, Choice, Check, List, Button }

/// <summary>One row of a list or one choice of a drop-down: what is shown and what it stands for.</summary>
public sealed record DialogItem(string Label, string Value, bool Ticked = false)
{
    /// <summary>What a toolkit without a ticking list shows: the label, and ", ticked".</summary>
    public string Said => Ticked ? $"{Label}, ticked" : Label;
}

/// <summary>
/// One control of the editor dialog. The head draws it and writes back what the player typed, chose or
/// ticked (<see cref="Text"/>, <see cref="Selected"/>, <see cref="Checked"/>), then tells the dialog
/// (<see cref="EditorDialog.Changed"/>). Ids are stable, so a control drawn again keeps its focus.
/// </summary>
public sealed class DialogControl
{
    public DialogControl(string id, DialogControlKind kind, string label, string description = "")
    {
        Id = id;
        Kind = kind;
        Label = label;
        Description = description;
    }

    public string Id { get; }
    public DialogControlKind Kind { get; }
    /// <summary>Its accessible name: a box's label, a button's text.</summary>
    public string Label { get; set; }
    /// <summary>Its accessible description: range, help, the value now.</summary>
    public string Description { get; set; }
    public bool Enabled { get; set; } = true;
    /// <summary>A box shown but not changed: a model field that needs edit-models.</summary>
    public bool ReadOnly { get; set; }

    /// <summary>A box's text.</summary>
    public string Text { get; set; } = "";
    /// <summary>What the server says the value is; a box, choice or tick that differs has been changed.</summary>
    public string Initial { get; set; } = "";
    /// <summary>A list's rows or a drop-down's choices.</summary>
    public IReadOnlyList<DialogItem> Items { get; set; } = Array.Empty<DialogItem>();
    /// <summary>The row or choice chosen, or -1.</summary>
    public int Selected { get; set; } = -1;
    public bool Checked { get; set; }
    /// <summary>A list whose rows Space ticks.</summary>
    public bool Checkable { get; set; }
    /// <summary>The button Enter presses here, if any.</summary>
    public string? Enter { get; set; }
    /// <summary>The button the Delete key presses here, if any (a list's Remove).</summary>
    public string? Delete { get; set; }
    /// <summary>The player's own state (a search, a choice of where): kept as it is when the tab is made again.</summary>
    public bool Local { get; set; }

    internal EditorMenuItem? Source;
    internal EditorValuePrompt? Prompt;

    public string? SelectedValue => Selected >= 0 && Selected < Items.Count ? Items[Selected].Value : null;

    /// <summary>The value as the player has it now, in the form sent: a box's text, a choice's value, "true" or "false".</summary>
    public string Value => Kind switch
    {
        DialogControlKind.Choice => SelectedValue ?? "",
        DialogControlKind.Check => Checked ? "true" : "false",
        _ => Text.Trim(),
    };

    /// <summary>Changed from what the server says.</summary>
    public bool Dirty => !ReadOnly && Kind is DialogControlKind.Entry or DialogControlKind.Choice or DialogControlKind.Check
                         && Value != Initial.Trim();

    public void Select(string? value)
    {
        Selected = -1;
        for (int i = 0; i < Items.Count; i++) if (Items[i].Value == value) { Selected = i; break; }
    }
}

/// <summary>A titled part of a tab (a GTK frame, a Windows group box).</summary>
public sealed class DialogSection
{
    public DialogSection(string id, string title, IEnumerable<DialogControl> controls)
    {
        Id = id;
        Title = title;
        Controls = controls.ToList();
    }

    public string Id { get; }
    public string Title { get; }
    public IReadOnlyList<DialogControl> Controls { get; }

    /// <summary>Which controls it has, in order: the same shape is updated where it stands, another is drawn again.</summary>
    public string Shape => string.Join("|", Controls.Select(c => $"{c.Id}:{c.Kind}"));
}

/// <summary>One tab: its name, as the tab is labelled and announced, and its sections.</summary>
public sealed class DialogTab
{
    public DialogTab(string id, string name)
    {
        Id = id;
        Name = name;
    }

    public string Id { get; }
    public string Name { get; }
    public IReadOnlyList<DialogSection> Sections { get; internal set; } = Array.Empty<DialogSection>();

    public IEnumerable<DialogControl> Controls => Sections.SelectMany(s => s.Controls);

    /// <summary>Where the focus goes when the tab is shown.</summary>
    public DialogControl? First => Controls.FirstOrDefault(c => c.Kind != DialogControlKind.Text && c.Enabled);

    public DialogControl? Find(string id) => Controls.FirstOrDefault(c => c.Id == id);
}

/// <summary>What the dialog keeps between openings, for the session.</summary>
public sealed class EditorDialogMemory
{
    public string Tab { get; set; } = EditorDialog.TabIds[0];
    public int Where { get; set; }
    public string? Category { get; set; }
    public string? Kind { get; set; }
}

/// <summary>
/// The F12 world editor dialog (docs/WORLD_EDITOR.md section 16): tabs Place, Edit, Build and World,
/// made from the server's "dialog" menus and drawn by each head with standard controls. It holds what
/// the player has typed and chosen, checks values as the value dialog does (EditorValuePrompt), and
/// sends plain /edit commands. Placing and changing keep it open; F12, Escape and Close shut it.
/// </summary>
public sealed class EditorDialog : ModalDialog
{
    public static readonly GameKeyChord Key = new(Platform.GameKey.F12, Input.KeyModifiers.None);
    public static readonly string[] TabIds = { "place", "edit", "build", "world" };
    private static readonly string[] TabNames = { "Place", "Edit", "Build", "World" };

    public const string AllCategories = "All categories";
    public const string LibraryNote = "Changes apply to every map that uses it, so duplicate first to try things.";
    private static readonly string[] WhereLabels =
    {
        "At your feet, or just in front of you if it is solid", "At the build cursor", "Preview only: play it to you, place nothing",
    };

    private readonly Action<string> _send;
    private readonly EditorDialogMemory _memory;
    private readonly DialogTab[] _tabs = TabNames.Select((n, i) => new DialogTab(TabIds[i], n)).ToArray();
    private readonly Dictionary<string, List<EditorMenuItem>> _data = new();
    private List<EditorMenuItem> _foot = new();
    private readonly Queue<Action> _pending = new();

    private readonly BuildForm? _pieceForm;
    private readonly BuildDialog? _piece;

    // The thing and the model the player last asked for, so a late answer does not move the list back.
    private string? _askedThing, _serverThing, _askedModel, _serverModel;

    public string Title { get; private set; }
    public IReadOnlyList<DialogTab> Tabs => _tabs;
    public int Current { get; private set; }
    public DialogTab CurrentTab => _tabs[Current];
    /// <summary>Undo, Redo and Close, under every tab.</summary>
    public DialogSection Footer { get; private set; } = new("foot", "", Array.Empty<DialogControl>());

    /// <summary>A tab's controls changed: the head updates them where they stand, or draws a changed section again.</summary>
    public event Action<DialogTab>? TabUpdated;
    public event Action? FooterUpdated;
    /// <summary>A tab is to be shown, with the focus on its first control.</summary>
    public event Action<int>? TabShown;
    /// <summary>A reason to show and say: a value refused, nothing chosen.</summary>
    public event Action<string>? Said;
    /// <summary>A question with Yes and No; the action runs on Yes.</summary>
    public event Action<string, Action>? ConfirmAsked;
    /// <summary>The focus is to go to a control (the box a refusal is about).</summary>
    public event Action<string>? FocusAsked;
    /// <summary>The server answered: the head calls <see cref="ApplyPending"/> on its own thread.</summary>
    public event Action? UpdateArrived;

    public EditorDialog(EditorMenu whole, EditorDialogMemory memory, BuildMemory buildMemory, Action<string> send) : base(Key)
    {
        _send = send;
        _memory = memory;
        Title = whole.Title;
        Current = Math.Max(0, Array.IndexOf(TabIds, memory.Tab));
        Take(whole.Items);

        var piece = Items("piece").ToArray();
        if (piece.Length > 0)
        {
            _pieceForm = new BuildForm(BuildCatalog.From(new EditorMenu { Items = piece }), buildMemory);
            // The prefab list above places prefabs; this part builds the plain pieces.
            if (_pieceForm.Kind == "prefab" && PieceKinds.FirstOrDefault() is { } first) _pieceForm.SetKind(first.Word);
            _piece = new BuildDialog(_pieceForm, send);
            _piece.Refused += why => Said?.Invoke(why);
        }
        foreach (var tab in _tabs) Rebuild(tab);
        RebuildFooter();
    }

    private IEnumerable<(string Word, string Label)> PieceKinds => _pieceForm?.Kinds.Where(k => k.Word != "prefab") ?? Enumerable.Empty<(string, string)>();

    // ── What the server sends ───────────────────────────────────────────────────────────────────

    private void Take(IEnumerable<EditorMenuItem> items)
    {
        foreach (var group in items.GroupBy(i => i.Section ?? ""))
        {
            if (group.Key.StartsWith("foot", StringComparison.Ordinal)) continue;
            _data[group.Key] = group.ToList();
        }
        var foot = items.Where(i => (i.Section ?? "").StartsWith("foot", StringComparison.Ordinal)).ToList();
        if (foot.Count > 0) _foot = foot;
    }

    private IEnumerable<EditorMenuItem> Items(string section) => _data.TryGetValue(section, out var l) ? l : Enumerable.Empty<EditorMenuItem>();

    /// <summary>A tab brought up to date by the server ("dialog.edit"). Safe from any thread: applied on
    /// the head's thread through <see cref="ApplyPending"/>, or at once when no head listens.</summary>
    public void Update(EditorMenu menu) => Enqueue(() =>
    {
        string tab = menu.Path.StartsWith("dialog.", StringComparison.Ordinal) ? menu.Path["dialog.".Length..] : "";
        int index = Array.IndexOf(TabIds, tab);
        if (index < 0) return;
        // A tab's sections are replaced whole: what the server no longer sends has gone.
        foreach (var key in _data.Keys.Where(k => k.StartsWith(tab + ".", StringComparison.Ordinal)).ToList()) _data.Remove(key);
        Title = menu.Title.Length > 0 ? menu.Title : Title;
        Take(menu.Items);
        Rebuild(_tabs[index]);
        TabUpdated?.Invoke(_tabs[index]);
        RebuildFooter();
        FooterUpdated?.Invoke();
    });

    /// <summary>The server's answer to a piece built from the Place tab.</summary>
    public void PieceAnswer(bool placed, string text) => Enqueue(() => _piece?.Answer(placed, text));

    private void Enqueue(Action action)
    {
        lock (_pending) _pending.Enqueue(action);
        if (UpdateArrived == null) ApplyPending();
        else UpdateArrived.Invoke();
    }

    /// <summary>Applies what the server sent. The head calls it on its own thread.</summary>
    public void ApplyPending()
    {
        while (true)
        {
            Action? next;
            lock (_pending) { if (!_pending.TryDequeue(out next)) return; }
            next();
        }
    }

    protected override void OnClosed() => _send("/edit dialog close");

    // ── Tabs ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Shows a tab: the focus goes to its first control, and the server sends it fresh.</summary>
    public void ShowTab(int index)
    {
        index = ((index % _tabs.Length) + _tabs.Length) % _tabs.Length;
        Current = index;
        _memory.Tab = TabIds[index];
        TabShown?.Invoke(index);
        _send($"/edit dialog tab {TabIds[index]}");
    }

    /// <summary>Control+Tab is +1, Control+Shift+Tab -1; both wrap round.</summary>
    public void NextTab(int delta) => ShowTab(Current + delta);

    private void Rebuild(DialogTab tab)
    {
        var old = tab.Controls.ToDictionary(c => c.Id);
        var sections = tab.Id switch
        {
            "place" => PlaceSections(),
            "edit" => EditSections(),
            "build" => BuildSections(),
            _ => WorldSections(),
        };
        foreach (var c in sections.SelectMany(s => s.Controls))
            if (old.TryGetValue(c.Id, out var was)) Carry(was, c);
        tab.Sections = sections;
        _previous = old;
        Follow(tab);
        Derive(tab);
        _previous = null;
    }

    /// <summary>
    /// The server chose something the player did not ask for (a copy, a thing just placed, a room from the
    /// World tab): its list follows. An answer to what the player asked for leaves the list alone, so a
    /// late answer never moves the choice back while they arrow on.
    /// </summary>
    private void Follow(DialogTab tab)
    {
        if (tab.Id == "edit" && tab.Find("edit.things") is { } things)
        {
            string? server = Items("edit.chosen").FirstOrDefault().Value is { Length: > 0 } v ? v : null;
            if (server != _serverThing && server != _askedThing) things.Select(server);
            else if (things.Selected < 0 && server != null) things.Select(server);
            _serverThing = server;
        }
        if (tab.Id == "world" && tab.Find("world.routes") is { } routes && routes.Selected < 0 && _routeNext != null)
        {
            // A road taken up: the one after it is chosen.
            routes.Select(_routeNext);
            _routeNext = null;
        }
        if (tab.Id == "edit" && tab.Find("edit.changed") is { } changed)
        {
            // As the placed list: a row put back has gone, so the one after it is chosen.
            if (changed.Selected < 0 && _changedNext != null)
            {
                changed.Select(_changedNext);
                _changedNext = null;
            }
            if (changed.Selected < 0 && _changedIndex >= 0 && changed.Items.Count > 0) changed.Selected = Math.Min(_changedIndex, changed.Items.Count - 1);
            _changedIndex = changed.Selected;
        }
        if (tab.Id == "edit" && tab.Find("edit.placed") is { } placed)
        {
            // The chosen row was removed: the next one is chosen, so the focus stays in the list where it was.
            if (placed.Selected < 0 && _placedNext != null)
            {
                placed.Select(_placedNext);
                _placedNext = null;
            }
            if (placed.Selected < 0 && _placedIndex >= 0 && placed.Items.Count > 0) placed.Selected = Math.Min(_placedIndex, placed.Items.Count - 1);
            _placedIndex = placed.Selected;
        }
        else if (tab.Id == "build" && tab.Find("build.kind") is { } kind && tab.Find("build.category") is { } category)
        {
            string? server = Items("build.chosen").FirstOrDefault().Value is { Length: > 0 } v ? v : null;
            if (server != null && server != _serverModel && server != _askedModel)
            {
                var parts = server.Split(' ', 2);
                kind.Select(parts[0]);
                _memory.Kind = parts[0];
                category.Selected = 0;
                _pendingModel = parts.Length > 1 ? parts[1] : null;
            }
            _serverModel = server;
        }
    }

    /// <summary>What the player had typed or chosen stays when the server's values come again.</summary>
    private static void Carry(DialogControl was, DialogControl now)
    {
        if (was.Kind != now.Kind) return;
        if (now.Local)
        {
            now.Text = was.Text;
            now.Checked = was.Checked;
            if (was.SelectedValue is { } v) now.Select(v);
            if (now.Selected < 0 && now.Kind == DialogControlKind.Choice && now.Items.Count > 0) now.Selected = 0;
            return;
        }
        if (!was.Dirty || was.Initial != now.Initial) return;
        now.Text = was.Text;
        now.Checked = was.Checked;
        now.Selected = was.Selected;
    }

    /// <summary>Controls that follow others: the prefab list follows the search and the category.</summary>
    private void Derive(DialogTab tab)
    {
        switch (tab.Id)
        {
            case "place": DerivePlace(tab); DeriveRoute(tab); break;
            case "build": DeriveBuild(tab); break;
        }
    }

    private void RebuildFooter()
    {
        DialogControl FootButton(string id, string section, string none)
        {
            var item = _foot.FirstOrDefault(i => i.Section == section);
            var b = new DialogControl(id, DialogControlKind.Button, item.Label is { Length: > 0 } l ? l : none);
            b.Enabled = item.Kind == EditorItemKind.Action;
            b.Source = item;
            return b;
        }
        Footer = new DialogSection("foot", "", new[]
        {
            FootButton("foot.undo", "foot.undo", "Nothing to undo"),
            FootButton("foot.redo", "foot.redo", "Nothing to redo"),
            new DialogControl("foot.close", DialogControlKind.Button, "Close"),
        });
    }

    // ── Controls ────────────────────────────────────────────────────────────────────────────────

    private static DialogControl Button(string id, string label, bool enabled = true, string description = "")
        => new(id, DialogControlKind.Button, label, description) { Enabled = enabled };

    private static DialogControl LocalBox(string id, string label, string description, string text = "", string? enter = null)
        => new(id, DialogControlKind.Entry, label, description) { Local = true, Text = text, Enter = enter };

    private static DialogControl LocalChoice(string id, string label, string description, IEnumerable<DialogItem> items, string? enter = null)
    {
        var c = new DialogControl(id, DialogControlKind.Choice, label, description) { Local = true, Items = items.ToList(), Enter = enter };
        c.Selected = c.Items.Count > 0 ? 0 : -1;
        return c;
    }

    private static DialogControl LocalList(string id, string label, string description, IEnumerable<DialogItem> items, string? enter = null)
        => new(id, DialogControlKind.List, label, description) { Local = true, Items = items.ToList(), Enter = enter };

    /// <summary>A field the server sent, as a box, a drop-down or a tick, holding its value now.</summary>
    private DialogControl Field(string prefix, EditorMenuItem item, string choicesSection, string? enter)
    {
        string id = prefix + item.Command;
        string label = Capital((item.Prompt ?? "").Length > 0 ? item.Prompt! : item.Label ?? "");
        string help = (item.Help ?? "").Trim();
        DialogControl c;
        switch (item.ValueType)
        {
            case FieldType.Choice:
                c = new DialogControl(id, DialogControlKind.Choice, label)
                {
                    Items = Items(choicesSection).Where(o => o.Command == item.Command).Select(o => new DialogItem(o.Label, o.Value)).ToList(),
                };
                c.Initial = item.Value ?? "";
                // A value the choices do not hold (an old material) is still shown as the one it has.
                if (c.Initial.Length > 0 && !c.Items.Any(o => o.Value == c.Initial)) c.Items = c.Items.Prepend(new DialogItem(c.Initial, c.Initial)).ToList();
                c.Select(c.Initial);
                c.Description = Join($"Now {c.Items.ElementAtOrDefault(Math.Max(0, c.Selected))?.Label ?? c.Initial}.", help);
                break;
            case FieldType.Bool:
                c = new DialogControl(id, DialogControlKind.Check, label, help) { Initial = item.Value ?? "false" };
                c.Checked = c.Initial == "true";
                break;
            default:
                var prompt = new EditorValuePrompt(item);
                c = new DialogControl(id, DialogControlKind.Entry, prompt.Label, prompt.Description) { Initial = prompt.Initial, Text = prompt.Initial };
                c.Prompt = prompt;
                break;
        }
        c.ReadOnly = item.Kind == EditorItemKind.Info;
        c.Source = item;
        c.Enter = c.ReadOnly ? null : enter;
        return c;
    }

    /// <summary>The command a changed field sends, or why it cannot be sent.</summary>
    private static bool TryFieldCommand(DialogControl c, out string command, out string error)
    {
        command = "";
        error = "";
        string start = c.Source?.Command ?? "";
        switch (c.Kind)
        {
            case DialogControlKind.Entry:
                return c.Prompt!.TryCommand(c.Text, out command, out error);
            case DialogControlKind.Choice:
                if (c.SelectedValue is not { } v) { error = $"Choose a {c.Label.ToLowerInvariant()}."; return false; }
                command = start + v;
                return true;
            default:
                command = start + (c.Checked ? "true" : "false");
                return true;
        }
    }

    /// <summary>Sends every changed field of a section; a value refused stops it all and is said.</summary>
    private void ApplyFields(IEnumerable<DialogControl> fields, IEnumerable<string>? before = null)
    {
        var changed = fields.Where(c => c.Source != null && c.Dirty).ToList();
        if (changed.Count == 0) { Said?.Invoke("Nothing has changed."); return; }
        var commands = new List<string>();
        foreach (var c in changed)
        {
            if (!TryFieldCommand(c, out var command, out var error))
            {
                Said?.Invoke(error);
                FocusAsked?.Invoke(c.Id);
                return;
            }
            commands.Add(command);
        }
        foreach (var b in before ?? Enumerable.Empty<string>()) _send(b);
        foreach (var command in commands) _send(command);
        // Sent: what the server says next is the value, not a change still to make.
        foreach (var c in changed) c.Initial = c.Value;
    }

    // ── Place ───────────────────────────────────────────────────────────────────────────────────

    private List<DialogSection> PlaceSections()
    {
        var prefabs = Items("place.prefab").ToList();
        var groups = Items("place.group").ToList();
        var categories = prefabs.Select(p => p.Prompt).Concat(groups.Select(g => g.Prompt)).Where(c => c.Length > 0).Distinct().ToList();
        var category = LocalChoice("place.category", "Category", "Which prefabs the list shows.",
            categories.Prepend(AllCategories).Select(c => new DialogItem(c, c)), enter: "place.place");
        category.Select(_memory.Category ?? AllCategories);
        if (category.Selected < 0) category.Selected = 0;
        var where = LocalChoice("place.where", "Where it goes", "Where Place puts it. While the dialog is open you do not move, so this is from where you stand.",
            WhereLabels.Select((w, i) => new DialogItem(w, i.ToString(CultureInfo.InvariantCulture))), enter: "place.place");
        where.Selected = Math.Clamp(_memory.Where, 0, WhereLabels.Length - 1);
        var again = Items("place.again").FirstOrDefault();

        var controls = new List<DialogControl>
        {
            LocalBox("place.search", "Search", "Words in a prefab's name or category: the list shows the prefabs with every word.", enter: "place.place"),
            category,
            LocalList("place.list", "Prefabs", "Each with its size and what it is. Enter places the one chosen.", Array.Empty<DialogItem>(), enter: "place.place"),
            where,
            Button("place.place", "Place"),
            Button("place.preview", "Preview", description: "Plays it to you alone for a few seconds; nothing is placed."),
            Button("place.again", again.Label is { Length: > 0 } l ? $"Place again: {l}" : "Place again", again.Kind == EditorItemKind.Action,
                   "The last thing placed, again, where you stand now."),
        };
        var sections = new List<DialogSection> { new("prefabs", "Place a prefab", controls) };
        if (_pieceForm != null) sections.Add(new DialogSection("piece", "Build a piece", PieceControls()));
        sections.Add(RouteSection());
        return sections;
    }

    // ── Laying a road, path or railway ──────────────────────────────────────────────────────────

    private static readonly (string Value, string Label)[] RouteKindItems = { ("road", "Road"), ("path", "Path"), ("railway", "Railway") };
    private static readonly (string Value, string Label)[] RouteLevelItems =
    {
        ("ground", "On the ground"), ("raised", "Raised on pillars"), ("underground", "Underground, in a tunnel of its own"),
    };
    public const string UsualSurface = "The usual for it";

    /// <summary>The Place tab's route form: what to lay, how, and the buttons that walk it, type it and lay it.</summary>
    private DialogSection RouteSection()
    {
        var info = Items("place.routeinfo").FirstOrDefault();
        string laying = info.Value ?? "";
        var kind = LocalChoice("place.routekind", "What", "A road for traffic, a path people walk, or a railway a train runs round. A railway is a loop: its last point joins its first.",
                               RouteKindItems.Select(k => new DialogItem(k.Label, k.Value)), enter: "place.routestart");
        var surface = LocalChoice("place.routesurface", "Surface", "What it is made of underfoot. The usual is asphalt for a road, concrete for a path and gravel for a railway's bed.",
                                  Items("place.routesurface").Select(s => new DialogItem(s.Label, s.Value)).Prepend(new DialogItem(UsualSurface, "")), enter: "place.routestart");
        var level = LocalChoice("place.routelevel", "A railway runs", "On the ground on a gravel bed; raised on a deck on pillars; or underground, in a tunnel of its own with no digging.",
                                RouteLevelItems.Select(l => new DialogItem(l.Label, l.Value)), enter: "place.routestart");
        var train = LocalChoice("place.routetrain", "Train on it", "The train that runs the railway once it is laid, or none.",
                                Items("place.routetrain").Select(t => new DialogItem(t.Label, t.Value)).Prepend(new DialogItem("None", "none")), enter: "place.routestart");
        var controls = new List<DialogControl>
        {
            kind,
            LocalBox("place.routename", "Name", "What it is called, such as High Street or Loop line. Empty for a number.", enter: "place.routestart"),
            LocalBox("place.routewidth", "Width in metres", "Empty for the usual: 7 for a road, 2 for a path, 4.2 for a railway. 0.5 to 60.", enter: "place.routestart"),
            surface, level,
            LocalBox("place.routeheight", "Height or depth in metres",
                     "How high a raised railway's deck is over the ground, or how deep an underground one runs: 3 to 60. Empty for 6 raised and 8 underground.", enter: "place.routestart"),
            train,
            new DialogControl("place.routestatus", DialogControlKind.Text, info.Label is { Length: > 0 } l ? l : "Nothing is being laid"),
            Button("place.routestart", "Start here, then walk it", laying.Length == 0,
                   "The first point is where you stand. Close the editor and walk its way: a point is dropped every metre, and you are told how far it has come. Open the editor again to lay it."),
            LocalBox("place.routepoints", "Points, typed: east and north in metres, apart by semicolons",
                     "Such as 0 0; 50 0; 50 40, as F1 says where you are. A third number is a height; without it the point is on the ground there.", enter: "place.routeadd"),
            Button("place.routeadd", "Add the points"),
            Button("place.routepoint", "Drop a point where you stand", laying.Length > 0),
            Button("place.routestation", "Add a station where you stand", laying == "railway", "Trains stop at the point of the line nearest you, at a platform beside it."),
            Button("place.routecrossing", "Add a level crossing where you stand", laying == "railway", "Where a road crosses the line, at the point of it nearest you: bells and gates."),
            Button("place.routeback", "Take back the last point", laying.Length > 0),
            Button("place.routefinish", "Lay it", laying.Length > 0, "Lays it with what is chosen above, its straight stretches joined up. One undo takes it up."),
            Button("place.routecancel", "Cancel: lay nothing", laying.Length > 0),
        };
        return new DialogSection("route", "Lay a road, path or railway", controls);
    }

    /// <summary>The route form's controls that follow its choices: what a railway has, and what is laid now.</summary>
    private void DeriveRoute(DialogTab tab)
    {
        string laying = Items("place.routeinfo").FirstOrDefault().Value ?? "";
        if (tab.Find("place.routekind") is not { } kind) return;
        // While a route is being laid it is the kind it is.
        if (laying.Length > 0) kind.Select(laying);
        kind.Enabled = laying.Length == 0;
        bool rail = kind.SelectedValue == "railway";
        if (tab.Find("place.routelevel") is { } level)
        {
            level.Enabled = rail;
            if (tab.Find("place.routeheight") is { } height) height.Enabled = rail && level.SelectedValue != "ground";
        }
        if (tab.Find("place.routetrain") is { } train) train.Enabled = rail;
    }

    /// <summary>What the route form says, as /edit route words: width, surface, level, height, train, name.</summary>
    private string RouteFields(DialogTab tab)
    {
        var words = new List<string>();
        string Text(string id) => tab.Find(id)?.Text.Trim() ?? "";
        bool rail = tab.Find("place.routekind")?.SelectedValue == "railway";
        if (Text("place.routewidth") is { Length: > 0 } width) words.Add($"width {width}");
        if (tab.Find("place.routesurface")?.SelectedValue is { Length: > 0 } surface) words.Add($"surface {surface}");
        if (rail)
        {
            string level = tab.Find("place.routelevel")?.SelectedValue ?? "ground";
            words.Add($"level {level}");
            if (level != "ground" && Text("place.routeheight") is { Length: > 0 } height) words.Add($"{(level == "raised" ? "height" : "depth")} {height}");
            words.Add($"train {tab.Find("place.routetrain")?.SelectedValue ?? "none"}");
        }
        if (Text("place.routename") is { Length: > 0 } name) words.Add($"name {name}");
        return string.Join(" ", words);
    }

    private static string Command(params string[] parts) => string.Join(" ", parts.Where(p => p.Length > 0));

    private void DerivePlace(DialogTab tab)
    {
        if (tab.Find("place.list") is not { } list) return;
        string search = tab.Find("place.search")?.Text ?? "";
        string category = tab.Find("place.category")?.SelectedValue ?? AllCategories;
        var words = search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool Has(EditorMenuItem i, string w) => i.Label.Contains(w, StringComparison.OrdinalIgnoreCase) || i.Value.Contains(w, StringComparison.OrdinalIgnoreCase)
                                                || i.Prompt.Contains(w, StringComparison.OrdinalIgnoreCase);
        var rows = Items("place.prefab").Select(p => (Item: p, Value: p.Value))
            .Concat(Items("place.group").Select(g => (Item: g, Value: "group:" + g.Value)))
            .Where(r => category == AllCategories || r.Item.Prompt == category)
            .Where(r => words.All(w => Has(r.Item, w)))
            .Select(r => new DialogItem(r.Item.Help is { Length: > 0 } h ? $"{r.Item.Label}: {h}" : r.Item.Label, r.Value)).ToList();
        string? chosen = list.SelectedValue;
        list.Items = rows;
        list.Select(chosen);
        var item = ChosenPrefab(tab);
        if (tab.Find("place.place") is { } place) place.Enabled = list.SelectedValue != null;
        if (tab.Find("place.preview") is { } preview) preview.Enabled = item is { Count: 1 } && !(list.SelectedValue ?? "").StartsWith("group:", StringComparison.Ordinal);
    }

    private EditorMenuItem? ChosenPrefab(DialogTab tab)
    {
        string? v = tab.Find("place.list")?.SelectedValue;
        if (v == null) return null;
        return v.StartsWith("group:", StringComparison.Ordinal)
            ? Items("place.group").FirstOrDefault(g => g.Value == v[6..])
            : Items("place.prefab").FirstOrDefault(p => p.Value == v);
    }

    private List<DialogControl> PieceControls()
    {
        var form = _pieceForm!;
        var kinds = PieceKinds.Select(k => new DialogItem(k.Label, k.Word)).ToList();
        var what = new DialogControl("piece.kind", DialogControlKind.Choice, "What", "Floor, wall, roof, door or window, at the size typed.")
        {
            Items = kinds, Enter = "piece.place",
        };
        what.Select(form.Kind);
        var controls = new List<DialogControl> { what };
        foreach (var view in form.Fields)
        {
            string id = "piece." + view.Word;
            DialogControl c = view.Type switch
            {
                FieldType.Bool => new DialogControl(id, DialogControlKind.Check, view.Label, view.Description) { Checked = form.Text(view.Word) == "true" },
                FieldType.Choice => new DialogControl(id, DialogControlKind.Choice, view.Label, view.Description)
                {
                    Items = form.Options(view.Word).Select(o => new DialogItem(o.Label, o.Value)).ToList(), Selected = form.Selected(view.Word),
                },
                _ => new DialogControl(id, DialogControlKind.Entry, view.Label, view.Description) { Text = form.Text(view.Word), Local = true },
            };
            c.Enabled = form.IsEnabled(view.Word);
            c.Enter = "piece.place";
            controls.Add(c);
        }
        controls.Add(Button("piece.place", "Place the piece"));
        return controls;
    }

    /// <summary>Puts what is typed in the piece's boxes into its form, before a choice changes the rest.</summary>
    private void PullPiece()
    {
        if (_pieceForm == null) return;
        foreach (var c in _tabs[0].Controls.Where(c => c.Id.StartsWith("piece.", StringComparison.Ordinal) && c.Kind == DialogControlKind.Entry))
            _pieceForm.Set(c.Id["piece.".Length..], c.Text);
    }

    private void RefreshPiece()
    {
        var tab = _tabs[0];
        var sections = tab.Sections.ToList();
        int at = sections.FindIndex(s => s.Id == "piece");
        if (at < 0) return;
        sections[at] = new DialogSection("piece", "Build a piece", PieceControls());
        tab.Sections = sections;
        TabUpdated?.Invoke(tab);
    }

    // ── Edit ────────────────────────────────────────────────────────────────────────────────────

    private List<DialogSection> EditSections()
    {
        var things = Items("edit.thing").ToList();
        var chosen = Items("edit.chosen").FirstOrDefault();
        string? serverThing = chosen.Value is { Length: > 0 } v ? v : null;

        var list = LocalList("edit.things", "Things near you",
            "Nearest first, what you stand on or in at the top. Arrow to choose one; its fields are below. Space ticks it, to move, turn or group several together.",
            things.Select(t => new DialogItem(t.Label, t.Value, t.Checked)));
        list.Checkable = true;
        var sections = new List<DialogSection>
        {
            new("things", "Things near you", new[]
            {
                list,
                LocalBox("edit.find", "Find by name or number", "A name, or # and a number such as #1002. The nearest match is chosen and put in the list.", enter: "edit.findgo"),
                Button("edit.findgo", "Find"),
            }),
            PlacedSection(),
            ChangedSection(),
        };

        bool has = chosen.Label is { Length: > 0 };
        var controls = new List<DialogControl>();
        // The thing's number is in each field's id, so a value typed for one is never carried to another.
        if (has)
            foreach (var field in Items("edit.field"))
                controls.Add(Field($"edit.f.{serverThing}.", field, "edit.choice", "edit.apply"));
        controls.Add(Button("edit.apply", "Apply changes", has, "Sends every field changed above."));
        controls.Add(Button("edit.bring", "Bring to me", has));
        controls.Add(Button("edit.duplicate", "Duplicate", has, "A copy, one of its own widths along the way you face. The copy is chosen."));
        controls.Add(LocalBox("edit.rowtext", "Row of copies: how many, and spacing in metres",
            $"Up to 50 copies the way you face. 4 puts them a width apart; 4 2.5 puts them 2.5 metres apart.", "3", "edit.row"));
        controls.Add(Button("edit.row", "Row of copies", has));
        controls.Add(Button("edit.delete", "Delete", has, "Asks first. Undo puts it back."));
        if (has && controls.Count > 0) controls[0].Description = Join(chosen.Help, controls[0].Description);
        sections.Add(new DialogSection("chosen", has ? $"Chosen: {chosen.Label}" : "Nothing chosen", controls));

        int ticked = things.Count(t => t.Checked);
        if (ticked > 0)
            sections.Add(new DialogSection("ticked", ticked == 1 ? "1 ticked" : $"{ticked} ticked", new[]
            {
                LocalBox("edit.heldmove", "Move them together: metres east, north and up", "Three numbers, such as 1 0 0 for a metre east. Negative goes west, south or down.", enter: "edit.heldmovego"),
                Button("edit.heldmovego", "Move them"),
                LocalBox("edit.heldturn", "Turn them together, in degrees", "Positive turns them clockwise about their middle, negative anticlockwise.", enter: "edit.heldturngo"),
                Button("edit.heldturngo", "Turn them"),
                LocalBox("edit.groupname", "Group name", "The ticked things become a group, or a building, with this name, to place again from Place.", enter: "edit.group"),
                Button("edit.group", "Group them"),
                Button("edit.building", "Save as a building", description: "The ticked things become a building with the group name above, listed in Place under Buildings."),
                Button("edit.deleteticked", "Delete the ticked things", description: "Wherever they are. Asks first. One undo puts them all back."),
                Button("edit.untick", "Untick all"),
            }));
        return sections;
    }

    /// <summary>Everything placed on the map with the editor, wherever it is: filtered, removed, gone to.</summary>
    private DialogSection PlacedSection()
    {
        var info = Items("edit.placedinfo").FirstOrDefault();
        var rows = Items("edit.placed").ToList();
        bool any = rows.Count > 0;
        bool mayGo = (info.Prompt ?? "").Split(' ').Contains("goto");
        string summary = info.Label is { Length: > 0 } l ? l : "Nothing has been placed on this map with the editor";
        var list = LocalList("edit.placed", "Placed on this map",
            Join(summary + ".", "Each with where it is from you and who placed it. Space ticks one; Delete removes the chosen one, asking first."),
            any ? rows.Select(r => new DialogItem(r.Label, r.Value, r.Checked)) : new[] { new DialogItem(summary, "") }, enter: "edit.placedchoose");
        // Always a ticking list: a head draws the kind of list once, before any rows may have come.
        list.Checkable = true;
        list.Delete = "edit.placedremove";
        return new DialogSection("placed", "Placed on this map", new[]
        {
            LocalBox("edit.placedfilter", "Filter placed things",
                "Words in a name, a kind or who placed it; within 20 keeps those within 20 metres. Enter filters; nothing typed shows them all.",
                info.Value ?? "", "edit.placedfiltergo"),
            Button("edit.placedfiltergo", "Filter"),
            list,
            Button("edit.placedremove", "Remove it", any, "Removes the one chosen in the list, wherever it is. Asks first. Undo puts it back."),
            Button("edit.placedgoto", "Go to it", any && mayGo, mayGo ? "Takes you to stand beside it, facing it." : "Needs the move permission on this map."),
            Button("edit.placedchoose", "Edit it", any, "Chooses it: its fields are under Chosen."),
            Button("edit.placedtickall", "Tick all shown", any, "Ticks every thing the list shows, to move, group, save as a building or delete together."),
        });
    }

    /// <summary>Things from the map file the editor changed or removed, wherever they are: filtered, put
    /// back as the map has them, gone to.</summary>
    private DialogSection ChangedSection()
    {
        var info = Items("edit.changedinfo").FirstOrDefault();
        var rows = Items("edit.changed").ToList();
        bool any = rows.Count > 0;
        bool mayGo = (info.Prompt ?? "").Split(' ').Contains("goto");
        string summary = info.Label is { Length: > 0 } l ? l : "Nothing from the map file has been changed or removed with the editor";
        var list = LocalList("edit.changed", "Changed on this map",
            Join(summary + ".", "Things from the map file that were moved, turned, resized, renamed or removed, each with what was done and where it is from you."),
            any ? rows.Select(r => new DialogItem(r.Label, r.Value)) : new[] { new DialogItem(summary, "") }, enter: "edit.changedchoose");
        return new DialogSection("changed", "Changed on this map", new[]
        {
            LocalBox("edit.changedfilter", "Filter changed things",
                "Words in a name or a kind, or what was done: moved, turned, removed. within 20 keeps those within 20 metres. Enter filters; nothing typed shows them all.",
                info.Value ?? "", "edit.changedfiltergo"),
            Button("edit.changedfiltergo", "Filter"),
            list,
            Button("edit.changedputback", "Put it back as the map has it", any,
                   "Puts the one chosen back where and as the map file has it, or brings back one that was removed. Asks first. Undo changes it again."),
            Button("edit.changedgoto", "Go to it", any && mayGo, mayGo ? "Takes you to stand beside it, or where it was, facing it." : "Needs the move permission on this map."),
            Button("edit.changedchoose", "Edit it", any, "Chooses it: its fields are under Chosen. A removed thing must be put back first."),
        });
    }

    private DialogControl? Chosen(string tab, string list) => _tabs[Array.IndexOf(TabIds, tab)].Find(list);

    // ── Build ───────────────────────────────────────────────────────────────────────────────────

    private List<DialogSection> BuildSections()
    {
        var chosen = Items("build.chosen").FirstOrDefault();
        string? serverModel = chosen.Value is { Length: > 0 } v ? v : null;
        var may = (chosen.Prompt ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();

        var kind = LocalChoice("build.kind", "Kind", LibraryNote, Items("build.kind").Select(k => new DialogItem(k.Label, k.Value)));
        kind.Select(_memory.Kind ?? Items("build.kind").FirstOrDefault().Value);
        if (kind.Selected < 0 && kind.Items.Count > 0) kind.Selected = 0;
        var category = LocalChoice("build.category", "Category", "Which prefabs the list shows.",
            Items("build.model").Where(m => m.Help.Length > 0).Select(m => m.Help).Distinct().Prepend(AllCategories).Select(c => new DialogItem(c, c)));
        var models = LocalList("build.models", "Models", "Arrow to choose one: what it is, its fields, versions and where it is used are below.", Array.Empty<DialogItem>());

        var sections = new List<DialogSection>
        {
            new("library", "Library", new[] { new DialogControl("build.note", DialogControlKind.Text, LibraryNote), kind, category, models }),
        };
        if (serverModel == null) return sections;

        string modelId = serverModel.Split(' ', 2).ElementAtOrDefault(1) ?? "";
        sections.Add(new DialogSection("entry", chosen.Label, new[]
        {
            new DialogControl("build.summary", DialogControlKind.Text, chosen.Help ?? ""),
            LocalBox("build.newid", "Id for the copy", "Letters, digits, _ and -, up to 64. The copy is version 1 and becomes the one shown here.",
                     Suggest(modelId), "build.duplicate"),
            Button("build.duplicate", "Duplicate", may.Contains("copy"), may.Contains("copy") ? "" : "Changing the library needs edit-models."),
        }));

        var fields = Items("build.field").ToList();
        var fieldList = LocalList("build.fields", "Fields", "Arrow to one; its value is in the box below it.",
            fields.Select(f => new DialogItem(FieldRow(f), f.Command)));
        sections.Add(new DialogSection("fields", "Fields", new[] { fieldList }));
        sections.Add(new DialogSection("value", "Value", new[]
        {
            Button("build.set", "Set", false),
        }));

        var versions = Items("build.version").ToList();
        sections.Add(new DialogSection("versions", "Versions", new[]
        {
            LocalList("build.versions", "Versions", "Newest first.", versions.Select(x => new DialogItem(x.Label, x.Value))),
            Button("build.use", "Use on every map", may.Contains("use")),
            Button("build.pin", "Pin on this map", may.Contains("pin")),
            Button("build.unpin", "Lift this map's pin", may.Contains("pin") && versions.Any(x => (x.Prompt ?? "").Contains("pinned"))),
        }));
        sections.Add(new DialogSection("where", "Where it is used", new[]
        {
            LocalList("build.where", "Where it is used", "Loaded maps only.", Items("build.where").Select(w => new DialogItem(w.Label, w.Label))),
        }));
        if (may.Contains("replace"))
            sections.Add(new DialogSection("replace", "Replace", new[]
            {
                LocalChoice("build.replacewith", "Replace it with", "Every thing using this model uses the one chosen instead. One undo puts them all back.",
                            Items("build.replace").Select(r => new DialogItem(r.Label, r.Value))),
                Button("build.replacehere", "Replace on this map"),
                Button("build.replaceall", "Replace everywhere", may.Contains("everywhere")),
            }));
        return sections;
    }

    private string? _pendingModel;
    private Dictionary<string, DialogControl>? _previous;
    /// <summary>The placed row to choose once the one being removed has gone, and where the choice was.</summary>
    private string? _placedNext;
    private int _placedIndex = -1;
    /// <summary>The same for the list of things changed from the map file.</summary>
    private string? _changedNext;
    private int _changedIndex = -1;
    /// <summary>The laid road to choose once the one being taken up has gone.</summary>
    private string? _routeNext;

    private static string Suggest(string id)
    {
        string root = id.EndsWith("_copy", StringComparison.Ordinal) ? id : id + "_copy";
        return root.Length > 64 ? root[..64] : root;
    }

    private static string FieldRow(EditorMenuItem f)
    {
        var field = new FieldDescriptor { Path = "", Label = f.Label, Type = f.ValueType, Unit = f.Unit ?? "" };
        return $"{f.Label}, {field.Say(f.Value ?? "")}{(f.Kind == EditorItemKind.Info ? ", read only" : "")}";
    }

    private void DeriveBuild(DialogTab tab)
    {
        var kind = tab.Find("build.kind");
        var category = tab.Find("build.category");
        var models = tab.Find("build.models");
        if (kind == null || category == null || models == null) return;
        string k = kind.SelectedValue ?? "";
        bool prefabs = k == "prefab";
        category.Enabled = prefabs;
        string cat = prefabs ? category.SelectedValue ?? AllCategories : AllCategories;
        string? chosen = _pendingModel ?? models.SelectedValue;
        _pendingModel = null;
        models.Items = Items("build.model").Where(m => m.Prompt == k && (cat == AllCategories || m.Help == cat))
                                           .Select(m => new DialogItem(m.Label, m.Value)).ToList();
        models.Select(chosen);
        // A model chosen elsewhere (the copy) stays chosen when the list is drawn again.
        if (models.Selected < 0 && _serverModel is { } sm && sm.StartsWith(k + " ", StringComparison.Ordinal)) models.Select(sm[(k.Length + 1)..]);
        DeriveValue(tab);
    }

    /// <summary>The value box follows the field chosen in the list: a box, a drop-down or a tick.</summary>
    private void DeriveValue(DialogTab tab)
    {
        var sections = tab.Sections.ToList();
        int at = sections.FindIndex(s => s.Id == "value");
        if (at < 0) return;
        string? command = tab.Find("build.fields")?.SelectedValue;
        var item = Items("build.field").FirstOrDefault(f => f.Command == command);
        var controls = new List<DialogControl>();
        if (command != null && item.Command == command)
        {
            var value = Field("build.value.", item, "build.choice", "build.set");
            // After the server's refresh the old box is in what the tab had before; otherwise it is still here.
            var prior = _previous?.GetValueOrDefault(value.Id) ?? sections[at].Controls.FirstOrDefault(c => c.Id == value.Id);
            if (prior != null) Carry(prior, value);
            controls.Add(value);
            controls.Add(Button("build.set", "Set", !value.ReadOnly));
        }
        else controls.Add(Button("build.set", "Set", false));
        sections[at] = new DialogSection("value", "Value", controls);
        tab.Sections = sections;
    }

    // ── World ───────────────────────────────────────────────────────────────────────────────────

    private List<DialogSection> WorldSections()
    {
        var can = (Items("world.can").FirstOrDefault().Value ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var settings = Items("world.field").Select(f => Field("world.f.", f, "world.choice", "world.apply")).ToList();
        settings.Add(Button("world.apply", "Apply changes", description: "Sends the weather, time and ground changed above."));
        settings.Add(Button("world.spawn", "Set spawn here", description: "Where you stand, facing your way."));
        var sections = new List<DialogSection> { new("settings", "Weather, time and ground", settings) };

        if (Items("world.size").FirstOrDefault() is { Command.Length: > 0 } size)
            sections.Add(new DialogSection("size", "Map size", new[] { Field("world.f.", size, "", "world.resize"), Button("world.resize", "Change the size") }));

        sections.Add(VersionsSection());

        var laid = Items("world.route").ToList();
        sections.Add(new DialogSection("routes", "Roads, paths and railways", new[]
        {
            LocalList("world.routes", "Roads, paths and railways laid",
                      "Each with what it is, where its nearest point is from you, and who laid it. Laid from the Place tab.",
                      laid.Count > 0 ? laid.Select(r => new DialogItem(r.Label, r.Value)) : new[] { new DialogItem("None laid with the editor", "") }, "world.routegoto"),
            Button("world.routegoto", "Go to it", laid.Count > 0, "Takes you to stand beside its nearest point."),
            Button("world.routeremove", "Take it up", laid.Count > 0, "Takes up its pieces and its data, and its train. Asks first. Undo lays it again."),
        }));

        var rooms = Items("world.room").ToList();
        sections.Add(new DialogSection("rooms", "Rooms and areas", new[]
        {
            LocalList("world.rooms", "Rooms and areas", "Named places and rooms, nearest first. Edit it chooses one on the Edit tab, with its name and materials.",
                      rooms.Select(r => new DialogItem(r.Label, r.Value)), "world.editroom"),
            Button("world.editroom", "Edit it", rooms.Count > 0),
        }));

        var beacons = Items("world.beacon").Select(f => Field("world.b.", f, "world.choice", "world.beaconapply")).ToList();
        beacons.Add(Button("world.beaconapply", "Apply beacon rules"));
        sections.Add(new DialogSection("beacons", "Beacon rules", beacons));

        var editors = Items("world.editor").ToList();
        var editorControls = new List<DialogControl>
        {
            LocalList("world.editors", "Editors", "Who may edit this map besides its owner.",
                      editors.Count > 0 ? editors.Select(e => new DialogItem(e.Label, e.Value)) : new[] { new DialogItem("None besides the owner", "") }),
        };
        if (can.Contains("editors"))
        {
            editorControls.Add(LocalBox("world.editorname", "Player name", "The player to add as an editor.", enter: "world.addeditor"));
            editorControls.Add(Button("world.addeditor", "Add editor"));
            editorControls.Add(Button("world.removeeditor", "Remove the chosen editor", editors.Count > 0));
        }
        sections.Add(new DialogSection("editors", "Editors", editorControls));

        var pins = Items("world.pin").ToList();
        sections.Add(new DialogSection("pins", "Model versions pinned to this map", new[]
        {
            LocalList("world.pins", "Pinned models", "A pinned model plays the version named here on this map.",
                      pins.Count > 0 ? pins.Select(p => new DialogItem(p.Label, p.Value)) : new[] { new DialogItem("No model is pinned here", "") }),
            Button("world.unpin", "Lift the chosen pin", pins.Count > 0),
        }));
        sections.Add(new DialogSection("info", "Map information", new[]
        {
            LocalList("world.info", "Map information", "", Items("world.info").Select(i => new DialogItem(i.Label, i.Label))),
            Button("world.scan", "What is around me"),
        }));
        return sections;
    }

    /// <summary>Versions of the map's edits: saved by name, restored, and written into the map file.</summary>
    private DialogSection VersionsSection()
    {
        var info = Items("world.versioninfo").FirstOrDefault();
        var rows = Items("world.version").ToList();
        bool bake = info.Value == "bake";
        string summary = info.Label is { Length: > 0 } l ? l : "No versions saved yet";
        return new DialogSection("versions", "Versions of this map", new[]
        {
            LocalBox("world.versionname", "Name for a new version", "A few words to know it by, such as before the market. Save keeps the map's edits as they are now.",
                     enter: "world.versionsave"),
            Button("world.versionsave", "Save a version"),
            LocalList("world.versions", "Versions of this map",
                      Join(summary + ".", "Each with who saved it and when, and what it holds. Restore asks first."),
                      rows.Count > 0 ? rows.Select(r => new DialogItem(r.Label, r.Value)) : new[] { new DialogItem(summary, "") }, "world.versionrestore"),
            Button("world.versionrestore", "Restore the chosen version", rows.Count > 0,
                   "The map's edits become what the version has, as one step undo takes back. What the map has now is saved as a version first."),
            Button("world.bake", "Write the edits into the map file", bake, info.Help ?? ""),
        });
    }

    // ── What the player does ────────────────────────────────────────────────────────────────────

    /// <summary>The head wrote what the player typed, chose or ticked into a control.</summary>
    public void Changed(DialogControl c)
    {
        switch (c.Id)
        {
            case "place.search":
            case "place.category":
            case "place.list":
                if (c.Id == "place.category") _memory.Category = c.SelectedValue;
                DerivePlace(_tabs[0]);
                TabUpdated?.Invoke(_tabs[0]);
                return;
            case "place.where":
                _memory.Where = Math.Max(0, c.Selected);
                return;
            case "place.routekind":
            case "place.routelevel":
                DeriveRoute(_tabs[0]);
                TabUpdated?.Invoke(_tabs[0]);
                return;
            case "piece.kind":
                PullPiece();
                if (c.SelectedValue is { } kind && kind != _pieceForm?.Kind) _pieceForm?.SetKind(kind);
                RefreshPiece();
                return;
            case "edit.placed":
                _placedIndex = c.Selected;
                return;
            case "edit.changed":
                _changedIndex = c.Selected;
                return;
            case "edit.things":
                if (c.SelectedValue is { } thing && thing != _serverThing)
                {
                    _askedThing = thing;
                    _send($"/edit select #{thing} dialog");
                }
                return;
            case "build.kind":
            case "build.category":
                if (c.Id == "build.kind") _memory.Kind = c.SelectedValue;
                DeriveBuild(_tabs[2]);
                TabUpdated?.Invoke(_tabs[2]);
                return;
            case "build.models":
                if (c.SelectedValue is { } model && Chosen("build", "build.kind")?.SelectedValue is { } k && $"{k} {model}" != _serverModel)
                {
                    _askedModel = $"{k} {model}";
                    _send($"/edit dialog model {k} {model}");
                }
                return;
            case "build.fields":
                DeriveValue(_tabs[2]);
                TabUpdated?.Invoke(_tabs[2]);
                return;
        }
        if (c.Id.StartsWith("piece.", StringComparison.Ordinal) && c.Kind != DialogControlKind.Entry && _pieceForm != null)
        {
            PullPiece();
            _pieceForm.Set(c.Id["piece.".Length..], c.Kind == DialogControlKind.Check ? (c.Checked ? "true" : "false") : c.SelectedValue ?? "");
            RefreshPiece();
        }
    }

    /// <summary>Space on a row of a list that ticks: the thing is held with the others, or let go.</summary>
    public void Toggle(DialogControl list, int index)
    {
        if (!list.Checkable || index < 0 || index >= list.Items.Count) return;
        var row = list.Items[index];
        if (row.Value.Length == 0) return;
        // The server holds the ticks, so a thing ticked in one list is ticked in the other.
        _send(row.Ticked ? $"/edit select drop #{row.Value} dialog" : $"/edit select add #{row.Value} dialog");
    }

    /// <summary>A button, or Enter on a control that presses one.</summary>
    public void Press(string id)
    {
        var tab = CurrentTab;
        DialogControl? Get(string cid) => tab.Find(cid);
        string Text(string cid) => Get(cid)?.Text.Trim() ?? "";
        switch (id)
        {
            case "foot.undo": _send("/edit undo"); return;
            case "foot.redo": _send("/edit redo"); return;
            case "foot.close": Close(); return;

            case "place.place":
            {
                string? v = Get("place.list")?.SelectedValue;
                if (v == null) { Said?.Invoke("Choose a prefab first."); FocusAsked?.Invoke("place.list"); return; }
                if (v.StartsWith("group:", StringComparison.Ordinal)) { _send($"/edit place group {v[6..]}"); return; }
                _send((Get("place.where")?.Selected ?? 0) switch
                {
                    1 => $"/edit place {v} at cursor",
                    2 => $"/edit preview {v}",
                    _ => $"/edit place {v}",
                });
                return;
            }
            case "place.preview":
                if (Get("place.list")?.SelectedValue is { } p && !p.StartsWith("group:", StringComparison.Ordinal)) _send($"/edit preview {p}");
                else Said?.Invoke("Choose a prefab with a sound of its own first.");
                return;
            case "place.again": _send("/edit again"); return;
            case "place.routestart":
                _send(Command("/edit route start", Get("place.routekind")?.SelectedValue ?? "road", RouteFields(tab)));
                return;
            case "place.routeadd":
            {
                if (Text("place.routepoints") is not { Length: > 0 } points) { Said?.Invoke("Type the points: east and north in metres, apart by semicolons, such as 0 0; 50 0; 50 40."); FocusAsked?.Invoke("place.routepoints"); return; }
                // Nothing laid yet: begun from the form, with no point where you stand.
                if ((Items("place.routeinfo").FirstOrDefault().Value ?? "").Length == 0)
                    _send(Command("/edit route new", Get("place.routekind")?.SelectedValue ?? "road", RouteFields(tab)));
                _send($"/edit route points {points}");
                return;
            }
            case "place.routepoint": _send("/edit route point"); return;
            case "place.routestation": _send("/edit route station"); return;
            case "place.routecrossing": _send("/edit route crossing"); return;
            case "place.routeback": _send("/edit route back"); return;
            case "place.routefinish": _send(Command("/edit route finish", RouteFields(tab))); return;
            case "place.routecancel": _send("/edit route cancel"); return;
            case "piece.place":
                PullPiece();
                if (_piece?.Place() is { } why) Said?.Invoke(why);
                return;

            case "edit.findgo":
                if (Text("edit.find") is not { Length: > 0 } find) { Said?.Invoke("Type a name, or # and a number."); FocusAsked?.Invoke("edit.find"); return; }
                _askedThing = null;
                _send($"/edit select {find}");
                return;
            case "edit.apply":
                ApplyFields(tab.Controls.Where(c => c.Id.StartsWith("edit.f.", StringComparison.Ordinal)));
                return;
            case "edit.bring": _send("/edit bring"); return;
            case "edit.duplicate": _send("/edit duplicate"); return;
            case "edit.row":
            {
                var words = Text("edit.rowtext").Replace(',', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (words.Length is < 1 or > 2 || !int.TryParse(words[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 1 || n > 50
                    || (words.Length == 2 && (!double.TryParse(words[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double sp) || sp <= 0 || sp > 100)))
                { Said?.Invoke("Row of copies is how many, 1 to 50, and the spacing in metres if not its own width: 4, or 4 2.5."); FocusAsked?.Invoke("edit.rowtext"); return; }
                _send("/edit row " + string.Join(" ", words));
                return;
            }
            case "edit.delete":
            {
                string name = Items("edit.chosen").FirstOrDefault().Label ?? "it";
                ConfirmAsked?.Invoke($"Delete {name}?", () => _send("/edit delete"));
                return;
            }
            case "edit.heldmovego": Typed("edit.heldmove", HeldMove); return;
            case "edit.heldturngo": Typed("edit.heldturn", HeldTurn); return;
            case "edit.group":
                if (Text("edit.groupname") is not { Length: > 0 } group) { Said?.Invoke("Type a name for the group."); FocusAsked?.Invoke("edit.groupname"); return; }
                _send($"/edit group {group}");
                return;
            case "edit.untick": _send("/edit select clear dialog"); return;
            case "edit.building":
                if (Text("edit.groupname") is not { Length: > 0 } building) { Said?.Invoke("Type a name for the building in Group name."); FocusAsked?.Invoke("edit.groupname"); return; }
                _send($"/edit building {building}");
                return;
            case "edit.deleteticked":
            {
                int ticked = Items("edit.thing").Count(t => t.Checked);
                ConfirmAsked?.Invoke(ticked == 1 ? "Delete the ticked thing?" : $"Delete the {ticked} ticked things?", () => _send("/edit remove held dialog"));
                return;
            }

            case "edit.placedfiltergo":
                _send(string.Join(" ", new[] { "/edit placed", Text("edit.placedfilter"), "dialog" }.Where(w => w.Length > 0)));
                return;
            case "edit.placedremove":
            {
                var list = Get("edit.placed");
                if (list?.SelectedValue is not { Length: > 0 } removeId) { Said?.Invoke("Choose a thing in the list first."); FocusAsked?.Invoke("edit.placed"); return; }
                int at = list.Selected;
                // After it has gone, the one after it is chosen, or the one before if it was the last.
                _placedNext = list.Items.ElementAtOrDefault(at + 1)?.Value ?? list.Items.ElementAtOrDefault(at - 1)?.Value;
                string name = Items("edit.placed").FirstOrDefault(r => r.Value == removeId).Help is { Length: > 0 } n ? n : "it";
                ConfirmAsked?.Invoke($"Remove {name}?", () => _send($"/edit remove #{removeId} dialog"));
                return;
            }
            case "edit.placedgoto":
                if (Get("edit.placed")?.SelectedValue is not { Length: > 0 } goId) { Said?.Invoke("Choose a thing in the list first."); FocusAsked?.Invoke("edit.placed"); return; }
                _send($"/edit goto #{goId}");
                return;
            case "edit.placedchoose":
                if (Get("edit.placed")?.SelectedValue is not { Length: > 0 } chooseId) { Said?.Invoke("Choose a thing in the list first."); FocusAsked?.Invoke("edit.placed"); return; }
                _askedThing = null;
                _send($"/edit select #{chooseId}");
                return;
            case "edit.placedtickall": _send("/edit select add placed dialog"); return;

            case "edit.changedfiltergo":
                _send(string.Join(" ", new[] { "/edit changed", Text("edit.changedfilter"), "dialog" }.Where(w => w.Length > 0)));
                return;
            case "edit.changedputback":
            {
                var list = Get("edit.changed");
                if (list?.SelectedValue is not { Length: > 0 } backId) { Said?.Invoke("Choose a thing in the list first."); FocusAsked?.Invoke("edit.changed"); return; }
                int at = list.Selected;
                _changedNext = list.Items.ElementAtOrDefault(at + 1)?.Value ?? list.Items.ElementAtOrDefault(at - 1)?.Value;
                var row = Items("edit.changed").FirstOrDefault(r => r.Value == backId);
                string name = row.Help is { Length: > 0 } n ? n : "it";
                ConfirmAsked?.Invoke(row.Prompt == "removed" ? $"Bring back {name} as the map has it?" : $"Put {name} back as the map has it?",
                                     () => _send($"/edit putback #{backId} dialog"));
                return;
            }
            case "edit.changedgoto":
                if (Get("edit.changed")?.SelectedValue is not { Length: > 0 } toId) { Said?.Invoke("Choose a thing in the list first."); FocusAsked?.Invoke("edit.changed"); return; }
                _send($"/edit goto #{toId}");
                return;
            case "edit.changedchoose":
            {
                if (Get("edit.changed")?.SelectedValue is not { Length: > 0 } pickId) { Said?.Invoke("Choose a thing in the list first."); FocusAsked?.Invoke("edit.changed"); return; }
                var row = Items("edit.changed").FirstOrDefault(r => r.Value == pickId);
                if (row.Prompt == "removed") { Said?.Invoke($"{(row.Help is { Length: > 0 } n ? n : "It")} has been removed. Put it back first to change it."); return; }
                _askedThing = null;
                _send($"/edit select #{pickId}");
                return;
            }

            case "build.duplicate":
            {
                if (_serverModel?.Split(' ', 2) is not [var k, var m]) return;
                if (Text("build.newid") is not { Length: > 0 } newId) { Said?.Invoke("Type an id for the copy."); FocusAsked?.Invoke("build.newid"); return; }
                _send($"/edit model copy {k} {m} {newId}");
                return;
            }
            case "build.set":
                ApplyFields(tab.Controls.Where(c => c.Id.StartsWith("build.value.", StringComparison.Ordinal)));
                return;
            case "build.use":
            case "build.pin":
            {
                if (_serverModel?.Split(' ', 2) is not [var k, var m]) return;
                if (Get("build.versions")?.SelectedValue is not { } version) { Said?.Invoke("Choose a version first."); FocusAsked?.Invoke("build.versions"); return; }
                _send($"/edit model {(id == "build.use" ? "use" : "pin")} {k} {m} {version}");
                return;
            }
            case "build.unpin":
                if (_serverModel?.Split(' ', 2) is [var uk, var um]) _send($"/edit model unpin {uk} {um}");
                return;
            case "build.replacehere":
            case "build.replaceall":
            {
                if (_serverModel?.Split(' ', 2) is not [var k, var m]) return;
                if (Get("build.replacewith")?.SelectedValue is not { } other) { Said?.Invoke("Choose what to replace it with."); return; }
                _send($"/edit model replace {k} {m} with {other} {(id == "build.replaceall" ? "everywhere" : "here")}");
                return;
            }

            case "world.apply":
                ApplyFields(tab.Controls.Where(c => c.Id.StartsWith("world.f.", StringComparison.Ordinal) && c.Source?.Section == "world.field"));
                return;
            case "world.spawn": _send("/edit spawn here"); return;
            case "world.resize":
                ApplyFields(tab.Controls.Where(c => c.Source?.Section == "world.size"));
                return;
            case "world.editroom":
                if (Get("world.rooms")?.SelectedValue is not { Length: > 0 } room) { Said?.Invoke("Choose a room or area first."); FocusAsked?.Invoke("world.rooms"); return; }
                _askedThing = null;
                _send($"/edit select #{room} dialog");
                ShowTab(1);
                return;
            case "world.beaconapply":
                ApplyFields(tab.Controls.Where(c => c.Id.StartsWith("world.b.", StringComparison.Ordinal)));
                return;
            case "world.addeditor":
                if (Text("world.editorname") is not { Length: > 0 } add) { Said?.Invoke("Type the player's name."); FocusAsked?.Invoke("world.editorname"); return; }
                _send($"/map editor add {add}");
                _send("/edit dialog tab world");
                return;
            case "world.removeeditor":
                if (Get("world.editors")?.SelectedValue is not { Length: > 0 } remove) { Said?.Invoke("Choose an editor first."); FocusAsked?.Invoke("world.editors"); return; }
                _send($"/map editor remove {remove}");
                _send("/edit dialog tab world");
                return;
            case "world.unpin":
                if (Get("world.pins")?.SelectedValue is not { Length: > 0 } pin) { Said?.Invoke("Choose a pin first."); FocusAsked?.Invoke("world.pins"); return; }
                _send($"/edit model unpin {pin}");
                _send("/edit dialog tab world");
                return;
            case "world.scan": _send("/scan"); return;
            case "world.versionsave":
                if (Text("world.versionname") is not { Length: > 0 } versionName) { Said?.Invoke("Type a name for the version."); FocusAsked?.Invoke("world.versionname"); return; }
                _send($"/edit map save {versionName}");
                return;
            case "world.versionrestore":
            {
                if (Get("world.versions")?.SelectedValue is not { Length: > 0 } number) { Said?.Invoke("Choose a version first."); FocusAsked?.Invoke("world.versions"); return; }
                string name = Items("world.version").FirstOrDefault(v => v.Value == number).Prompt is { Length: > 0 } vname ? $", {vname}" : "";
                ConfirmAsked?.Invoke($"Restore version {number}{name}? What the map has now is saved as a version first.", () => _send($"/edit map restore {number}"));
                return;
            }
            case "world.routegoto":
                if (Get("world.routes")?.SelectedValue is not { Length: > 0 } goRoute) { Said?.Invoke("Choose one in the list first."); FocusAsked?.Invoke("world.routes"); return; }
                _send($"/edit route goto {goRoute}");
                return;
            case "world.routeremove":
            {
                var list = Get("world.routes");
                if (list?.SelectedValue is not { Length: > 0 } upRoute) { Said?.Invoke("Choose one in the list first."); FocusAsked?.Invoke("world.routes"); return; }
                string name = Items("world.route").FirstOrDefault(r => r.Value == upRoute).Prompt is { Length: > 0 } rn ? rn : "it";
                _routeNext = list.Items.ElementAtOrDefault(list.Selected + 1)?.Value ?? list.Items.ElementAtOrDefault(list.Selected - 1)?.Value;
                ConfirmAsked?.Invoke($"Take up {name}?", () => _send($"/edit route remove {upRoute}"));
                return;
            }
            case "world.bake":
                ConfirmAsked?.Invoke("Write this map's edits into its file? Undo cannot take it back; the file as it was is kept beside it.",
                                     () => _send("/edit map bake now"));
                return;
        }
    }

    private static readonly EditorMenuItem HeldMove = new()
    {
        Kind = EditorItemKind.Input, Command = "/edit held move ", Prompt = "move them together: metres east, north and up",
        ValueType = FieldType.Number, Min = -1000, Max = 1000, Count = 3,
    };

    private static readonly EditorMenuItem HeldTurn = new()
    {
        Kind = EditorItemKind.Input, Command = "/edit held turn ", Prompt = "turn them together, in degrees", ValueType = FieldType.Number, Min = -360, Max = 360,
    };

    /// <summary>A box checked as the value dialog checks one, then sent.</summary>
    private void Typed(string id, EditorMenuItem item)
    {
        var prompt = new EditorValuePrompt(item);
        if (!prompt.TryCommand(CurrentTab.Find(id)?.Text ?? "", out var command, out var error))
        {
            Said?.Invoke(error);
            FocusAsked?.Invoke(id);
            return;
        }
        _send(command);
    }

    private static string Join(params string?[] parts) => string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim()));

    private static string Capital(string s) => s.Length == 0 ? s : char.ToUpper(s[0], CultureInfo.InvariantCulture) + s[1..];
}

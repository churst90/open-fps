using System.Numerics;
using MemoryPack;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.Core;
using OpenFPS.Client.Core.Input;
using OpenFPS.Client.Core.Platform;
using OpenFPS.Client.Core.Session;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Editing;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Editor;
using OpenFPS.Server.Repositories;
using Rig = OpenFPS.Tests.WorldEditorTests.Rig;

namespace OpenFPS.Tests;

/// <summary>
/// The F12 editor dialog (docs/WORLD_EDITOR.md section 16): the server sends its tabs only to a player
/// who may edit the map, keeps it up to date after each change, and the shared dialog model turns what
/// the player does into plain /edit commands.
/// </summary>
public class EditorDialogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "openfps-dialog-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static Rig Mine(string dir, UserRole role = UserRole.Player, bool models = false)
    {
        var rig = new Rig(dir, role, models: models);
        rig.On("mine");
        return rig;
    }

    private static IEnumerable<EditorMenuItem> In(EditorMenu menu, string section) => menu.Items.Where(i => i.Section == section);

    private static EditorMenu LastDialog(Rig rig, string path)
        => rig.Replies.OfType<EditorMenu>().Last(m => m.Path == path);

    // ── The server ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void F12IsAnsweredOnlyOnAMapYouMayEdit()
    {
        var rig = new Rig(_dir, UserRole.Player);
        rig.On("theirs");
        // Nothing at all: no dialog, no words.
        Assert.Equal("", rig.Run("edit", "dialog", "open", "place"));
        Assert.Null(rig.LastMenu);
        Assert.Empty(rig.Replies);

        rig.On("mine");
        Assert.Equal("", rig.Run("edit", "dialog", "open", "place"));
        var whole = Assert.IsType<EditorMenu>(rig.LastMenu);
        Assert.Equal("dialog", whole.Path);
        Assert.StartsWith("World editor, ", whole.Title);
        Assert.NotEmpty(In(whole, "place.prefab"));
        Assert.NotEmpty(In(whole, "piece"));
        Assert.NotEmpty(In(whole, "build.kind"));
        Assert.Equal(3, In(whole, "world.field").Count());
        Assert.Single(In(whole, "foot.undo"));
        // The owner may change the size of a map that is not shipped.
        Assert.Single(In(whole, "world.size"));
    }

    [Fact]
    public void WhatWouldOpenAMenuBringsTheDialogsTabUpToDate()
    {
        var rig = Mine(_dir);
        rig.Run("edit", "dialog", "open", "edit");
        Assert.StartsWith("Placed: Concrete Wall, ", rig.Run("edit", "place", "concrete_wall"));
        int id = rig.Selected;
        var edit = LastDialog(rig, "dialog.edit");
        Assert.True(edit.Refresh);
        Assert.Contains(In(edit, "edit.thing"), t => t.Value == id.ToString());
        Assert.Equal(id.ToString(), Assert.Single(In(edit, "edit.chosen")).Value);
        var fields = In(edit, "edit.field").ToList();
        Assert.Contains(fields, f => f.Command == "/edit move to " && f.Count == 3);
        Assert.Contains(fields, f => f.Command == "/edit face ");
        Assert.Contains(fields, f => f.Command.StartsWith("/edit set "));
        Assert.StartsWith("Undo: ", Assert.Single(In(edit, "foot.undo")).Label);
        // No list menu is sent while the dialog is open.
        Assert.DoesNotContain(rig.Replies.OfType<EditorMenu>(), m => !m.Path.StartsWith("dialog"));

        // Choosing from the list is quiet: the screen reader reads the row.
        rig.Replies.Clear();
        Assert.Equal("", rig.Run("edit", "select", $"#{id}", "dialog"));
        Assert.Single(rig.Replies.OfType<EditorMenu>(), m => m.Path == "dialog.edit");

        // Closed, the menus come back.
        rig.Run("edit", "dialog", "close");
        rig.Run("edit", "select", $"#{id}");
        Assert.Equal("selected", rig.LastMenu?.Path);
    }

    [Fact]
    public void ThePositionAndFacingBoxesMoveAndTurnToWhatIsTyped()
    {
        var rig = Mine(_dir);
        rig.Run("edit", "place", "concrete_wall");
        int id = rig.Selected;
        Assert.StartsWith("Moved Concrete Wall to 10.0, 12.0, 1.5", rig.Run("edit", "move", "to", "10", "12", "1.5"));
        Assert.True(Vector3.Distance(new Vector3(10, 1.5f, 12), rig.PoseOf(id).Position) < 1e-3f);
        Assert.Contains("faces 90 degrees, east", rig.Run("edit", "face", "90"));
        Assert.Equal("east", WorldEditor.CompassOf(WorldEditor.YawOf(rig.PoseOf(id).Rotation)));
        // Compass words still work, and a turn past a full circle is refused.
        Assert.Contains("faces north", rig.Run("edit", "face", "north"));
        Assert.StartsWith("Facing is in degrees", rig.Run("edit", "face", "400"));
        // One undo each.
        rig.Run("edit", "undo");
        Assert.Equal("east", WorldEditor.CompassOf(WorldEditor.YawOf(rig.PoseOf(id).Rotation)));
    }

    [Fact]
    public void TicksAreHeldThingsAndADropLetsOneGo()
    {
        var rig = Mine(_dir);
        rig.Run("edit", "dialog", "open", "edit");
        rig.Run("edit", "place", "concrete_wall");
        int a = rig.Selected;
        rig.Run("edit", "place", "concrete_wall");
        int b = rig.Selected;
        Assert.Equal("Ticked Concrete Wall: 1 ticked.", rig.Run("edit", "select", "add", $"#{a}", "dialog"));
        Assert.Equal("Ticked Concrete Wall: 2 ticked.", rig.Run("edit", "select", "add", $"#{b}", "dialog"));
        var edit = LastDialog(rig, "dialog.edit");
        Assert.True(In(edit, "edit.thing").Single(t => t.Value == a.ToString()).Checked);
        Assert.Equal("Unticked Concrete Wall: 1 ticked.", rig.Run("edit", "select", "drop", $"#{a}", "dialog"));
        edit = LastDialog(rig, "dialog.edit");
        Assert.False(In(edit, "edit.thing").Single(t => t.Value == a.ToString()).Checked);
        Assert.True(In(edit, "edit.thing").Single(t => t.Value == b.ToString()).Checked);
        // A deleted thing is not held any more.
        rig.Run("edit", "select", $"#{b}");
        rig.Run("edit", "delete");
        Assert.DoesNotContain(In(LastDialog(rig, "dialog.edit"), "edit.thing"), t => t.Checked);
    }

    [Fact]
    public void APrefabDuplicatesAndTheCopyIsTheOneShown()
    {
        var rig = Mine(_dir, UserRole.Dev, models: true);
        rig.Run("edit", "dialog", "open", "build");
        rig.Run("edit", "dialog", "model", "prefab", "concrete_wall");
        var build = LastDialog(rig, "dialog.build");
        var chosen = Assert.Single(In(build, "build.chosen"));
        Assert.Equal("prefab concrete_wall", chosen.Value);
        Assert.Contains("copy", chosen.Prompt.Split(' '));
        Assert.NotEmpty(In(build, "build.field"));
        Assert.NotEmpty(In(build, "build.version"));

        Assert.StartsWith("Made the prefab wall_b, copied from concrete_wall", rig.Run("edit", "model", "copy", "prefab", "concrete_wall", "wall_b"));
        build = LastDialog(rig, "dialog.build");
        Assert.Equal("prefab wall_b", Assert.Single(In(build, "build.chosen")).Value);
        Assert.Contains(In(build, "build.model"), m => m.Value == "wall_b" && m.Prompt == "prefab");
        // The copy is a prefab like any other: it can be placed, and changed without touching the original.
        Assert.StartsWith("Placed: ", rig.Run("edit", "place", "wall_b"));
        rig.Run("edit", "model", "set", "prefab", "wall_b", "Name", "Garden Wall");
        Assert.Equal("Concrete Wall", rig.Maps.Prefabs["concrete_wall"].Name);
        Assert.Equal("Garden Wall", rig.Maps.Prefabs["wall_b"].Name);
    }

    [Fact]
    public void TheDialogsMembersSurviveTheWire()
    {
        var menu = new EditorMenu
        {
            Path = "dialog.edit", Refresh = true,
            Items = new[] { new EditorMenuItem { Label = "Fountain, 3 metres ahead", Section = "edit.thing", Value = "1002", Checked = true } },
        };
        var back = (EditorMenu)MemoryPackSerializer.Deserialize<IMessage>(MemoryPackSerializer.Serialize<IMessage>(menu))!;
        var item = Assert.Single(back.Items);
        Assert.Equal("edit.thing", item.Section);
        Assert.True(item.Checked);
        Assert.Equal("1002", item.Value);
        // A menu's items have no section.
        Assert.Equal("", new EditorMenuItem().Section);
    }

    // ── The client ──────────────────────────────────────────────────────────────────────────────

    private sealed class Speech : ISpeechOutput
    {
        public readonly List<string> Spoken = new();
        public string BackendName => "test";
        public bool Initialize() => true;
        public void Speak(string text, bool interrupt = true) => Spoken.Add(text);
        public void Interrupt() { }
        public void Dispose() { }
    }

    private sealed class Shell : IClientShell
    {
        public EditorDialog? Editor;
        public bool IsGameInputActive => true;
        public event Action<string>? CommandEntered { add { } remove { } }
        public void ShowLoading(string status, bool speak = true) { }
        public void UpdateLoadingStatus(string text, int percent) { }
        public void EnterGame() { }
        public void OpenCommandConsole() { }
        public void ShowEditorDialog(EditorDialog dialog) => Editor = dialog;
        public void ShowGameMenu(Action<GameMenuChoice> chosen) { }
        public void ReturnToMenu() { }
        public void Quit() { }
    }

    private static (ClientGameSession Session, Shell Shell, Speech Speech, List<IMessage> Sent) Client()
    {
        AcousticRegistry.Initialize();
        var network = new ClientNetworkService();
        var sent = new List<IMessage>();
        network.Sending = sent.Add;
        var speech = new Speech();
        var shell = new Shell();
        var session = new ClientGameSession(network, speech, shell, new AudioEngineFacade(),
            microphone: new NullMicrophoneCapture("none"), enableAudio: false);
        return (session, shell, speech, sent);
    }

    private static string Typed(IMessage m)
    {
        var c = Assert.IsType<TextCommand>(m);
        return "/" + string.Join(" ", c.Args.Prepend(c.Command));
    }

    [Fact]
    public void F12AsksOpensOnlyOnAnAnswerAndClosesAgain()
    {
        var (session, shell, speech, sent) = Client();
        Assert.True(session.Press(GameKey.F12, KeyModifiers.None));
        Assert.Equal("/edit dialog open place", Typed(Assert.Single(sent)));
        // No answer (no permission): nothing opens and nothing is said.
        Assert.Null(shell.Editor);
        Assert.Empty(speech.Spoken);

        var rig = Mine(_dir);
        rig.Run("edit", "dialog", "open", "place");
        session.HandleMessage(rig.LastMenu!);
        var editor = Assert.IsType<EditorDialog>(shell.Editor);
        Assert.True(editor.IsOpen);
        Assert.True(editor.IsCloseKey(GameKey.F12, KeyModifiers.None));
        Assert.True(editor.IsCloseKey(GameKey.Escape, KeyModifiers.None));
        Assert.False(editor.IsCloseKey(GameKey.F12, KeyModifiers.Shift));

        // F12 again closes it, and the server is told.
        bool closed = false;
        editor.CloseRequested += () => closed = true;
        sent.Clear();
        session.Press(GameKey.F12, KeyModifiers.None);
        Assert.True(closed);
        Assert.False(editor.IsOpen);
        Assert.Equal("/edit dialog close", Typed(Assert.Single(sent)));
    }

    private (EditorDialog Editor, Rig Rig, List<string> Sent, List<string> Said) Dialog(UserRole role = UserRole.Player, bool models = false, string tab = "place")
    {
        var rig = Mine(_dir, role, models);
        rig.Run("edit", "place", "concrete_wall");
        rig.Run("edit", "dialog", "open", tab);
        var sent = new List<string>();
        var said = new List<string>();
        var editor = new EditorDialog(rig.LastMenu!, new EditorDialogMemory { Tab = tab }, new BuildMemory(), sent.Add);
        editor.Said += said.Add;
        return (editor, rig, sent, said);
    }

    [Fact]
    public void TheTabsAreInOrderAndControlTabWalksThem()
    {
        var (editor, _, sent, _) = Dialog();
        Assert.Equal(new[] { "Place", "Edit", "Build", "World" }, editor.Tabs.Select(t => t.Name));
        Assert.Equal(0, editor.Current);
        Assert.Equal("place.search", editor.Tabs[0].First?.Id);
        Assert.Equal("edit.things", editor.Tabs[1].First?.Id);
        Assert.Equal("build.kind", editor.Tabs[2].First?.Id);
        Assert.Equal(DialogControlKind.Choice, editor.Tabs[3].First?.Kind);

        int shown = -1;
        editor.TabShown += i => shown = i;
        editor.NextTab(1);
        Assert.Equal(1, shown);
        Assert.Equal("/edit dialog tab edit", sent[^1]);
        editor.NextTab(-1);
        editor.NextTab(-1);
        Assert.Equal(3, editor.Current);
        Assert.Equal("/edit dialog tab world", sent[^1]);
        editor.NextTab(1);
        Assert.Equal(0, editor.Current);
    }

    [Fact]
    public void EachTabShowsWhatAnEditorWorksWith()
    {
        var (editor, _, _, _) = Dialog();
        var place = editor.Tabs[0];
        Assert.Contains(place.Find("place.list")!.Items, i => i.Value == "concrete_wall");
        Assert.Equal(EditorDialog.AllCategories, place.Find("place.category")!.SelectedValue);
        Assert.NotNull(place.Find("piece.kind"));
        Assert.DoesNotContain(place.Find("piece.kind")!.Items, k => k.Value == "prefab");
        Assert.Equal("Place again: Concrete Wall", place.Find("place.again")!.Label);

        var edit = editor.Tabs[1];
        Assert.NotEmpty(edit.Find("edit.things")!.Items);
        Assert.Contains(edit.Controls, c => c.Kind == DialogControlKind.Entry && c.Label.StartsWith("Position"));
        Assert.True(edit.Find("edit.delete")!.Enabled);

        var build = editor.Tabs[2];
        Assert.Equal("prefab", build.Find("build.kind")!.SelectedValue);
        Assert.Contains(build.Find("build.models")!.Items, m => m.Value == "concrete_wall");
        Assert.Contains(build.Controls, c => c.Label == EditorDialog.LibraryNote);

        var world = editor.Tabs[3];
        Assert.Equal(new[] { "Weather", "Time of day", "Natural ground" }, world.Sections[0].Controls.Take(3).Select(c => c.Label.Split(',')[0]));
        Assert.Contains(world.Sections, s => s.Title == "Rooms and areas");
        Assert.Contains(world.Sections, s => s.Title == "Beacon rules");
        Assert.Contains(world.Sections, s => s.Title == "Map size");
        Assert.Contains(world.Sections, s => s.Title == "Map information");
    }

    [Fact]
    public void SearchAndCategoryNarrowThePrefabs()
    {
        var (editor, _, _, _) = Dialog();
        var tab = editor.Tabs[0];
        var search = tab.Find("place.search")!;
        search.Text = "concrete wall";
        editor.Changed(search);
        var list = tab.Find("place.list")!;
        Assert.NotEmpty(list.Items);
        Assert.All(list.Items, i => Assert.Contains("concrete", (i.Label + i.Value).ToLowerInvariant()));
        // Nothing chosen yet: Place is not in use.
        Assert.False(tab.Find("place.place")!.Enabled);
    }

    [Fact]
    public void PlacingSendsWhatWhereSaysAndKeepsTheDialogOpen()
    {
        var (editor, _, sent, said) = Dialog();
        var tab = editor.Tabs[0];
        editor.Press("place.place");
        Assert.Equal("Choose a prefab first.", said[^1]);
        Assert.Empty(sent);

        var list = tab.Find("place.list")!;
        list.Select("concrete_wall");
        editor.Changed(list);
        Assert.True(tab.Find("place.place")!.Enabled);
        editor.Press("place.place");
        Assert.Equal("/edit place concrete_wall", sent[^1]);
        var where = tab.Find("place.where")!;
        where.Selected = 1;
        editor.Changed(where);
        editor.Press("place.place");
        Assert.Equal("/edit place concrete_wall at cursor", sent[^1]);
        editor.Press("place.again");
        Assert.Equal("/edit again", sent[^1]);
        Assert.True(editor.IsOpen);
    }

    [Fact]
    public void APlacedPieceIsSaidAndTheDialogStaysOpen()
    {
        var (session, shell, speech, sent) = Client();
        var rig = Mine(_dir);
        rig.Run("edit", "dialog", "open", "place");
        session.HandleMessage(rig.LastMenu!);
        var editor = shell.Editor!;
        sent.Clear();
        editor.Press("piece.place");
        Assert.EndsWith(" dialog", Typed(Assert.Single(sent)));
        session.HandleMessage(new EditorMenu { Path = "build.placed", Title = "Placed: floor, 4 by 4 metres, wood, at your feet." });
        Assert.True(editor.IsOpen);
        Assert.Contains("Placed: floor, 4 by 4 metres, wood, at your feet.", speech.Spoken);
        // A list menu sent while the dialog is open is not shown under it.
        session.HandleMessage(new EditorMenu { Path = "root", Title = "World editor", Items = new[] { new EditorMenuItem { Label = "Place" } } });
        Assert.True(editor.IsOpen);
    }

    [Fact]
    public void FieldsAreCheckedAsTheValueDialogChecksThem()
    {
        var (editor, rig, sent, said) = Dialog(tab: "edit");
        var tab = editor.Tabs[1];
        var position = tab.Controls.First(c => c.Label.StartsWith("Position"));
        Assert.Equal(3, position.Text.Split(' ').Length);

        editor.Press("edit.apply");
        Assert.Equal("Nothing has changed.", said[^1]);

        string? focused = null;
        editor.FocusAsked += id => focused = id;
        position.Text = "1 2";
        editor.Press("edit.apply");
        Assert.Contains("is 3 numbers", said[^1]);
        Assert.Equal(position.Id, focused);
        Assert.Empty(sent);

        position.Text = "6 7 1.5";
        var width = tab.Controls.First(c => c.Source?.Command == "/edit set Width ");
        width.Text = "900";
        editor.Press("edit.apply");
        Assert.Contains("500", said[^1]);
        Assert.Empty(sent);

        width.Text = "3";
        editor.Press("edit.apply");
        Assert.Equal(new[] { "/edit move to 6 7 1.5", "/edit set Width 3" }, sent);
        // What was sent is not a change any more; the server's answer is the value.
        Assert.False(position.Dirty);

        // Sent through the server, the dialog's commands do what they say.
        foreach (var command in sent) rig.Run("edit", command.Split(' ')[1..]);
        Assert.True(Vector3.Distance(new Vector3(6, 1.5f, 7), rig.PoseOf(rig.Selected).Position) < 1e-3f);
    }

    [Fact]
    public void ARefreshKeepsWhatThePlayerHasTyped()
    {
        var (editor, rig, _, _) = Dialog(tab: "edit");
        var tab = editor.Tabs[1];
        var find = tab.Find("edit.find")!;
        find.Text = "wall";
        var width = tab.Controls.First(c => c.Source?.Command == "/edit set Width ");
        width.Text = "4";

        rig.Run("edit", "dialog", "tab", "edit");
        editor.Update(LastDialog(rig, "dialog.edit"));
        Assert.Equal("wall", tab.Find("edit.find")!.Text);
        Assert.Equal("4", tab.Controls.First(c => c.Source?.Command == "/edit set Width ").Text);
    }

    [Fact]
    public void DeleteAsksFirstAndTicksGoThroughTheServer()
    {
        var (editor, rig, sent, _) = Dialog(tab: "edit");
        string? question = null;
        Action? yes = null;
        editor.ConfirmAsked += (q, y) => { question = q; yes = y; };
        editor.Press("edit.delete");
        Assert.Equal("Delete Concrete Wall?", question);
        Assert.Empty(sent);
        yes!();
        Assert.Equal("/edit delete", sent[^1]);

        var things = editor.Tabs[1].Find("edit.things")!;
        Assert.True(things.Checkable);
        int row = things.Items.ToList().FindIndex(i => i.Value == rig.Selected.ToString());
        editor.Toggle(things, row);
        Assert.Equal($"/edit select add #{rig.Selected} dialog", sent[^1]);
    }

    [Fact]
    public void BuildTabFollowsTheCopyJustMade()
    {
        var (editor, rig, sent, _) = Dialog(UserRole.Dev, models: true, tab: "build");
        var tab = editor.Tabs[2];
        var models = tab.Find("build.models")!;
        models.Select("concrete_wall");
        editor.Changed(models);
        Assert.Equal("/edit dialog model prefab concrete_wall", sent[^1]);
        rig.Run("edit", "dialog", "model", "prefab", "concrete_wall");
        editor.Update(LastDialog(rig, "dialog.build"));
        Assert.Equal("concrete_wall_copy", tab.Find("build.newid")!.Text);
        Assert.True(tab.Find("build.duplicate")!.Enabled);

        editor.Press("build.duplicate");
        Assert.Equal("/edit model copy prefab concrete_wall concrete_wall_copy", sent[^1]);
        rig.Run("edit", "model", "copy", "prefab", "concrete_wall", "concrete_wall_copy");
        editor.Update(LastDialog(rig, "dialog.build"));
        Assert.Equal("concrete_wall_copy", tab.Find("build.models")!.SelectedValue);

        // A field chosen from the list gets a box of its own; Set sends the model's command.
        var fields = tab.Find("build.fields")!;
        int name = fields.Items.ToList().FindIndex(i => i.Label.StartsWith("Name"));
        Assert.True(name >= 0);
        fields.Selected = name;
        editor.Changed(fields);
        var value = tab.Controls.Single(c => c.Id.StartsWith("build.value.", StringComparison.Ordinal));
        value.Text = "Garden Wall";
        editor.Press("build.set");
        Assert.Equal("/edit model set prefab concrete_wall_copy Name Garden Wall", sent[^1]);
    }
}

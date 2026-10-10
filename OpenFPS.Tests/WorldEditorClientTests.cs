using System.Numerics;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.Core;
using OpenFPS.Client.Core.Platform;
using OpenFPS.Client.Core.Session;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;

namespace OpenFPS.Tests;

/// <summary>
/// The world editor on the client: F12 asks the server, its menus are shown on the same lists as F5, F6
/// and F8 and say what they are, choosing sends what the item says to send, a changed menu replaces
/// itself without a word, and a changed model is heard at once.
/// </summary>
public class WorldEditorClientTests
{
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
        public string? Console;
        public bool IsGameInputActive { get; set; } = true;
        public event Action<string>? CommandEntered { add { } remove { } }
        public void ShowLoading(string status, bool speak = true) { }
        public void UpdateLoadingStatus(string text, int percent) { }
        public void EnterGame() { }
        public void OpenCommandConsole() => Console = "";
        public void OpenCommandConsole(string initialText) => Console = initialText;
        public void ShowGameMenu(Action<GameMenuChoice> chosen) { }
        public void ReturnToMenu() { }
        public void Quit() { }
    }

    private static (ClientGameSession Session, List<IMessage> Sent, Speech Speech, Shell Shell) NewClient()
    {
        AcousticRegistry.Initialize();
        var network = new ClientNetworkService();
        var sent = new List<IMessage>();
        network.Sending = sent.Add;
        var speech = new Speech();
        var shell = new Shell();
        var session = new ClientGameSession(network, speech, shell, new AudioEngineFacade(),
            microphone: new NullMicrophoneCapture("none"), enableAudio: false);
        return (session, sent, speech, shell);
    }

    private static EditorMenuItem Item(string label, EditorItemKind kind, string command = "", bool stay = false)
        => new() { Label = label, Kind = kind, Command = command, Stay = stay };

    private static EditorMenu Root() => new()
    {
        Path = "root", Title = "World editor, mine",
        Items = new[] { Item("Map", EditorItemKind.Menu, "map"), Item("Place", EditorItemKind.Menu, "place"), Item("Nothing to undo", EditorItemKind.Info) },
    };

    private static EditorMenu Nudge(string step = "0.5") => new()
    {
        Path = "nudge", Title = "Nudge",
        Items = new[]
        {
            Item($"Step, {step} metres, typed", EditorItemKind.Input, "/edit step "),
            Item("North", EditorItemKind.Action, "edit nudge north", stay: true),
            Item("South", EditorItemKind.Action, "edit nudge south", stay: true),
        },
    };

    [Fact]
    public void F12AsksForTheDialogAndATypedEditMenuIsSpokenAndWalked()
    {
        var (session, sent, speech, _) = NewClient();
        Assert.True(session.Press(GameKey.F12));
        var asked = Assert.IsType<TextCommand>(sent[^1]);
        Assert.Equal(("edit", "dialog open place"), (asked.Command, string.Join(" ", asked.Args)));

        // /edit typed on its own still answers with the menus, shown as lists.
        session.HandleMessage(Root());
        Assert.Equal("World editor, mine, 3 items. Map", speech.Spoken[^1]);
        session.Menus.HandleKey(GameKey.Down);
        Assert.Equal("Place", speech.Spoken[^1]);
        session.Menus.HandleKey(GameKey.Enter);                       // a menu: asked for, the lists stay open
        Assert.Equal("edit menu place", Typed(sent[^1]));
        Assert.True(session.Menus.IsOpen);

        // The answer opens on top; Escape comes back.
        session.HandleMessage(Nudge());
        Assert.Equal("Nudge, 3 items. Step, 0.5 metres, typed", speech.Spoken[^1]);
        session.Menus.HandleKey(GameKey.N);                           // a letter jumps
        Assert.Equal("North", speech.Spoken[^1]);
        session.Menus.HandleKey(GameKey.Enter);                       // an action that stays
        Assert.Equal("edit nudge north", Typed(sent[^1]));
        Assert.Equal("Nudge", session.Menus.Current!.Title);
        session.Menus.HandleKey(GameKey.Escape);
        Assert.Equal("World editor, mine", session.Menus.Current!.Title);
        Assert.Equal("World editor, mine. Place", speech.Spoken[^1]);
    }

    [Fact]
    public void ARefreshReplacesTheOpenMenuSilentlyAndKeepsTheCursor()
    {
        var (session, _, speech, _) = NewClient();
        session.HandleMessage(Root());
        session.HandleMessage(Nudge());
        session.Menus.HandleKey(GameKey.Down);
        int said = speech.Spoken.Count;

        var fresh = Nudge("0.25");
        fresh.Refresh = true;
        session.HandleMessage(fresh);
        Assert.Equal(said, speech.Spoken.Count);
        Assert.Equal(1, session.Menus.Current!.At);
        Assert.Equal("Step, 0.25 metres, typed", session.Menus.Current.Items[0].Label);

        // A refresh of a menu that is not the one open changes nothing.
        var other = Root();
        other.Refresh = true;
        session.HandleMessage(other);
        Assert.Equal("Nudge", session.Menus.Current!.Title);
    }

    [Fact]
    public void AnInputItemStartsTheCommandLineAndF12AgainGoesBackToTheTop()
    {
        var (session, _, _, shell) = NewClient();
        session.HandleMessage(Root());
        session.HandleMessage(Nudge());
        session.Menus.HandleKey(GameKey.Enter);
        Assert.Equal("/edit step ", shell.Console);
        Assert.True(session.Menus.IsOpen);

        session.HandleMessage(Root());
        Assert.Equal("World editor, mine", session.Menus.Current!.Title);
        session.Menus.HandleKey(GameKey.Escape);
        Assert.False(session.Menus.IsOpen);
    }

    [Fact]
    public void ADeleteThatDoesNotStayClosesTheEditor()
    {
        var (session, sent, _, _) = NewClient();
        session.HandleMessage(new EditorMenu
        {
            Path = "delete", Title = "Delete Fire?",
            Items = new[] { Item("Yes, delete Fire", EditorItemKind.Action, "edit delete") },
        });
        session.Menus.HandleKey(GameKey.Enter);
        Assert.Equal("edit delete", Typed(sent[^1]));
        Assert.False(session.Menus.IsOpen);
    }

    [Fact]
    public void AChangedModelGoesIntoTheLibrary()
    {
        var (session, _, _, _) = NewClient();
        string id = "editor_client_" + Guid.NewGuid().ToString("N")[..8];
        ModelLibrary.Add(ModelLibrary.Kinds.SmallMachine, id, SmallMachineSpec.AirConditionerCondenser);
        var changed = SmallMachineSpec.AirConditionerCondenser with
        {
            Compressor = SmallMachineSpec.AirConditionerCondenser.Compressor! with { HumDb = 70f },
        };
        session.HandleMessage(new ModelUpdate { Kind = ModelLibrary.Kinds.SmallMachine, Id = id, Version = 2, SpecJson = ModelLibrary.SpecJson(changed) });
        Assert.Equal(70f, ModelLibrary.SmallMachine(id).Compressor!.HumDb);
        // One that does not read is refused and the old one kept.
        session.HandleMessage(new ModelUpdate { Kind = ModelLibrary.Kinds.SmallMachine, Id = id, Version = 3, SpecJson = "{ not json" });
        Assert.Equal(70f, ModelLibrary.SmallMachine(id).Compressor!.HumDb);
    }

    [Fact]
    public void AMachineIsVoicedAgainFromItsChangedModel()
    {
        string id = "editor_voice_" + Guid.NewGuid().ToString("N")[..8];
        ModelLibrary.Add(ModelLibrary.Kinds.SmallMachine, id, SmallMachineSpec.AirConditionerCondenser);
        var h = new ClientAudioHarness();
        h.StandAt(new Vector3(0, 0, 0));
        const int unit = 4242;
        var def = new EntityDefinition
        {
            EntityId = unit,
            Type = EntityType.StaticObject,
            Transform = new Transform { Position = new Vector3(3, 0.45f, 0), Rotation = Quaternion.Identity, Scale = Vector3.One },
            Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(0.95f, 0.9f, 0.95f), IsSolid = true },
            Material = new MaterialComponent { Material = "Metal" },
        };
        def.SoundEmitter.IsSynth = true;
        def.SoundEmitter.SoundId = "machine:" + id;
        def.SoundEmitter.Mode = PlaybackMode.LoopOne;
        def.SoundEmitter.Volume = 1f;
        def.SoundEmitter.Range = 160f;
        def.SoundEmitter.MinDistance = 1.8f;
        h.World.RegisterDefinition(def);

        Assert.True(h.TickUntil(() => h.Mixer.WasStarted(unit)), "the machine was never voiced");
        int starts = h.Mixer.Started.Count(e => e.EntityId == unit);

        var restarted = h.Audio.ModelChanged(ModelLibrary.Kinds.SmallMachine, id, h.World.GetSnapshot());
        Assert.Equal(new[] { unit }, restarted);
        h.Facade.PumpForTest();
        Assert.Contains(unit, h.Mixer.Stopped);
        Assert.True(h.TickUntil(() => h.Mixer.Started.Count(e => e.EntityId == unit) > starts), "the machine was not voiced again");
        // Another model's change leaves it alone.
        Assert.Empty(h.Audio.ModelChanged(ModelLibrary.Kinds.SmallMachine, "ac_window", h.World.GetSnapshot()));
    }

    [Fact]
    public void TheDirectKeysAreOffByDefaultShiftOnlyAndSaid()
    {
        Assert.False(new ClientSettings().EditorDirectKeys);
        bool was = EditorKeys.Enabled;
        try
        {
            EditorKeys.Enabled = false;
            Assert.StartsWith("The editor's direct keys are off.", ClientGameSession.EditorKeysCommand(Array.Empty<string>(), () => { }));
            int saved = 0;
            Assert.StartsWith("Editor direct keys on", ClientGameSession.EditorKeysCommand(new[] { "on" }, () => saved++));
            Assert.True(EditorKeys.Enabled);
            Assert.Equal(1, saved);
            // Nothing a screen reader owns: no modifier but Shift is read, and the keypad, Insert,
            // Control and Alt send nothing.
            foreach (var key in new[] { GameKey.ControlLeft, GameKey.AltLeft, GameKey.Numpad5, GameKey.Numpad8, GameKey.Enter, GameKey.Escape })
                Assert.Null(EditorKeys.CommandFor(key));
            Assert.Equal("edit nudge forward", EditorKeys.CommandFor(GameKey.Up));
            Assert.Equal("edit menu delete", EditorKeys.CommandFor(GameKey.Delete));
        }
        finally { EditorKeys.Enabled = was; }
    }

    [Fact]
    public void AVehiclesSoundNamesTheVehicleModelAndAShoreItsShore()
    {
        Assert.True(OpenFPS.Common.Editing.ModelKinds.TryModelOfSound("engine:school_bus", out var kind, out var id));
        Assert.Equal((ModelLibrary.Kinds.Vehicle, "school_bus"), (kind, id));
        Assert.True(OpenFPS.Common.Editing.ModelKinds.TryModelOfSound("shore:lake_rock", out kind, out _));
        Assert.Equal(ModelLibrary.Kinds.Shore, kind);
        Assert.Equal("water:pond/elm_park/0", OpenFPS.Common.Editing.ModelKinds.WithModel("water:park_fountain/elm_park/0", "pond"));
    }

    private static string Typed(IMessage m)
    {
        var c = Assert.IsType<TextCommand>(m);
        return string.Join(" ", new[] { c.Command }.Concat(c.Args));
    }
}

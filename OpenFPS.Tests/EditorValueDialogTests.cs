using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.Core;
using OpenFPS.Client.Core.Platform;
using OpenFPS.Client.Core.Session;
using OpenFPS.Common;
using OpenFPS.Common.Editing;
using OpenFPS.Common.Networking;

namespace OpenFPS.Tests;

/// <summary>
/// The world editor's typed values (docs/WORLD_EDITOR.md section 14): an Input item opens a dialog with
/// one labelled text box, what was typed is checked while the box is open, and only a good value is sent.
/// </summary>
public class EditorValueDialogTests
{
    private static EditorMenuItem Hum(string value = "63") => new()
    {
        Label = "Type a value", Kind = EditorItemKind.Input, Command = "/edit model set small_machine ac_condenser Compressor.HumDb ",
        Prompt = "compressor hum level", Value = value, ValueType = FieldType.Number, Unit = "dB", Min = 30, Max = 90,
        Help = "The magnetic hum at one metre.",
    };

    [Fact]
    public void TheBoxIsLabelledAndDescribedByItsField()
    {
        var p = new EditorValuePrompt(Hum());
        Assert.Equal("Compressor hum level", p.Title);
        Assert.Equal("Compressor hum level, in dB", p.Label);
        Assert.Equal("63", p.Initial);
        Assert.Equal("Now 63 dB. From 30 to 90 dB. The magnetic hum at one metre.", p.Description);
        Assert.Equal("Compressor hum level, in dB: 63. From 30 to 90 dB. Type, then Enter. Escape cancels.", p.Spoken);
        Assert.Equal("/edit model set small_machine ac_condenser Compressor.HumDb ", p.Command);
    }

    [Fact]
    public void ANumberIsCheckedAgainstItsRangeAndBecomesTheCommand()
    {
        var p = new EditorValuePrompt(Hum());
        Assert.True(p.TryCommand(" 66 ", out var command, out _));
        Assert.Equal("/edit model set small_machine ac_condenser Compressor.HumDb 66", command);
        Assert.True(p.TryCommand("65,5", out command, out _));   // a decimal comma
        Assert.EndsWith(" 65.5", command);

        Assert.False(p.TryCommand("120", out _, out var why));
        Assert.Equal("Compressor hum level must be 30 to 90 dB; 120 is outside it.", why);
        Assert.False(p.TryCommand("loud", out _, out why));
        Assert.Equal("Compressor hum level needs a number, 30 to 90 dB.", why);
        Assert.False(p.TryCommand("  ", out _, out why));
        Assert.StartsWith("Compressor hum level is empty.", why);
        Assert.False(p.TryCommand("60 70", out _, out why));
        Assert.Equal("Compressor hum level is one number.", why);
    }

    [Fact]
    public void AWholeNumberRefusesAFraction()
    {
        var p = new EditorValuePrompt(new EditorMenuItem
        {
            Kind = EditorItemKind.Input, Command = "/edit select #", Prompt = "number of the thing to select",
            ValueType = FieldType.Integer, Min = 0, Max = int.MaxValue,
        });
        Assert.True(p.TryCommand("1002", out var command, out _));
        Assert.Equal("/edit select #1002", command);
        Assert.False(p.TryCommand("10.5", out _, out var why));
        Assert.StartsWith("Number of the thing to select needs a whole number", why);
    }

    [Fact]
    public void SeveralNumbersAreApartBySpacesOrCommas()
    {
        var p = new EditorValuePrompt(new EditorMenuItem
        {
            Kind = EditorItemKind.Input, Command = "/edit move ", Prompt = "metres east, north and up",
            ValueType = FieldType.Number, Min = -1000, Max = 1000, Count = 3,
        });
        Assert.True(p.TryCommand("1 0 -2.5", out var command, out _));
        Assert.Equal("/edit move 1 0 -2.5", command);
        Assert.True(p.TryCommand("1, 0, 2", out command, out _));
        Assert.Equal("/edit move 1 0 2", command);
        Assert.True(p.TryCommand("1,0,2", out command, out _));
        Assert.Equal("/edit move 1 0 2", command);

        Assert.False(p.TryCommand("1 0", out _, out var why));
        Assert.Equal("Metres east, north and up is 3 numbers, apart by spaces, such as 1 0 0.", why);
        Assert.False(p.TryCommand("1 0 2000", out _, out why));
        Assert.Contains("2000 is outside it", why);
    }

    [Fact]
    public void WordsAreSentAsTyped()
    {
        var p = new EditorValuePrompt(new EditorMenuItem
        {
            Kind = EditorItemKind.Input, Command = "/edit find ", Prompt = "words to search for", Help = "Prefabs whose name has every word.",
        });
        Assert.Equal("Words to search for", p.Label);
        Assert.Equal("Prefabs whose name has every word.", p.Description);
        Assert.True(p.TryCommand("concrete wall", out var command, out _));
        Assert.Equal("/edit find concrete wall", command);
        Assert.False(p.TryCommand("", out _, out _));
    }

    [Fact]
    public void AnItemWithoutAPromptIsLabelledByItsOwnLabel()
    {
        var p = new EditorValuePrompt(new EditorMenuItem { Label = "Step, 0.5 metres, typed", Kind = EditorItemKind.Input, Command = "/edit step " });
        Assert.Equal("Step, 0.5 metres, typed", p.Label);
        Assert.True(p.TryCommand("2", out var command, out _));
        Assert.Equal("/edit step 2", command);
    }

    // ── Through the session ─────────────────────────────────────────────────────────────────────

    private sealed class Speech : ISpeechOutput
    {
        public readonly List<string> Spoken = new();
        public string BackendName => "test";
        public bool Initialize() => true;
        public void Speak(string text, bool interrupt = true) => Spoken.Add(text);
        public void Interrupt() { }
        public void Dispose() { }
    }

    /// <summary>A head with the dialog: it keeps what it was asked, and types into it on request.</summary>
    private sealed class Shell : IClientShell
    {
        public EditorValuePrompt? Asked;
        public Func<string, string?>? Submit;
        public string? Console;
        public bool IsGameInputActive => true;
        public event Action<string>? CommandEntered { add { } remove { } }
        public void ShowLoading(string status, bool speak = true) { }
        public void UpdateLoadingStatus(string text, int percent) { }
        public void EnterGame() { }
        public void OpenCommandConsole() => Console = "";
        public void OpenCommandConsole(string initialText) => Console = initialText;
        public void AskForValue(EditorValuePrompt prompt, Func<string, string?> submit) { Asked = prompt; Submit = submit; }
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

    [Fact]
    public void ChoosingATypedItemOpensTheDialogAndOnlyAGoodValueIsSent()
    {
        var (session, sent, speech, shell) = NewClient();
        session.HandleMessage(new EditorMenu { Path = "root", Title = "World editor, mine", Items = new[] { Hum() } });
        session.Menus.HandleKey(GameKey.Enter);

        Assert.NotNull(shell.Asked);
        Assert.Null(shell.Console);
        Assert.True(session.Menus.IsOpen);   // the lists wait under the dialog
        Assert.Equal("Compressor hum level, in dB", shell.Asked!.Label);

        // Refused: the reason comes back for the dialog to say, and nothing is sent.
        sent.Clear();
        Assert.Equal("Compressor hum level must be 30 to 90 dB; 95 is outside it.", shell.Submit!("95"));
        Assert.Empty(sent);

        // The value it opened with: nothing to send, said so, and the dialog closes.
        Assert.Null(shell.Submit!("63"));
        Assert.Empty(sent);
        Assert.Equal("Unchanged.", speech.Spoken.Last());

        Assert.Null(shell.Submit!("66"));
        var command = Assert.IsType<TextCommand>(Assert.Single(sent));
        Assert.Equal("edit", command.Command);
        Assert.Equal(new[] { "model", "set", "small_machine", "ac_condenser", "Compressor.HumDb", "66" }, command.Args);
    }
}

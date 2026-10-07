using OpenFPS.Client.Core;
using OpenFPS.Client.Core.Input;
using OpenFPS.Client.Core.Platform;
using OpenFPS.Client.Core.Session;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Common;

namespace OpenFPS.Tests;

/// <summary>
/// No gameplay action may be bound to a key a screen reader owns: Control silences speech and Alt is the
/// window manager's, pressed dozens of times a minute, so an action there fires by itself. Control was
/// once the trigger: five sessions chased "random banging" that was the player's own rifle shots
/// (docs/CLIENT_NOTES.md, "Screen reader keys are not game keys").
/// </summary>
public class ScreenReaderKeyTests
{
    private sealed class Speech : ISpeechOutput
    {
        public string BackendName => "test";
        public bool Initialize() => true;
        public void Speak(string text, bool interrupt = true) { }
        public void Interrupt() { }
        public void Dispose() { }
    }

    private sealed class Shell : IClientShell
    {
        public bool IsGameInputActive { get; set; } = true;
        public event Action<string>? CommandEntered;
        public void ShowLoading(string status, bool speak = true) { }
        public void UpdateLoadingStatus(string text, int percent) { }
        public void EnterGame() { }
        public void OpenCommandConsole() { }
        public void ShowGameMenu(Action<GameMenuChoice> chosen) { }
        public void ReturnToMenu() { }
        public void Quit() { }
    }

    private static ClientGameSession NewSession()
    {
        AcousticRegistry.Initialize();
        return new ClientGameSession(
            new ClientNetworkService(), new Speech(), new Shell(), new AudioEngineFacade(),
            microphone: new NullMicrophoneCapture("none"), enableAudio: false);
    }

    [Fact]
    public void NothingInGameplayIsBoundToAScreenReadersKey()
    {
        var session = NewSession();

        foreach (var key in ClientGameSession.ScreenReaderKeys)
        {
            Assert.False(session.IsBound(InputContext.Gameplay, key),
                $"{key} is a screen reader's key. A blind player presses it to silence speech, not to "
              + "act — binding a gameplay action to it means the action fires by itself.");
            Assert.False(session.IsBound(InputContext.Global, key),
                $"{key} is a screen reader's key and must not carry a global action either.");
        }
    }

    /// <summary>The trigger exists and is on a key a blind player can find by touch.</summary>
    [Fact]
    public void TheTriggerIsReachableAndIsNotAModifier()
    {
        var session = NewSession();

        Assert.True(session.IsBound(InputContext.Gameplay, GameKey.Enter), "there is no trigger bound");
        Assert.DoesNotContain(GameKey.Enter, ClientGameSession.ScreenReaderKeys);
    }

    /// <summary>
    /// The keypad with Num Lock off is NVDA's review keys. Windows reports keypad 8 as Up and keypad
    /// period as Delete; only the extended flag tells them from the arrows and Delete, which are the
    /// game's. GTK names them apart, and the Windows head drops them the same way.
    /// </summary>
    [Theory]
    [InlineData(0x26, false, true)]    // keypad 8: Up, not extended
    [InlineData(0x26, true, false)]    // the Up arrow
    [InlineData(0x2E, false, true)]    // keypad period: Delete, not extended
    [InlineData(0x2E, true, false)]    // Delete
    [InlineData(0x2D, false, true)]    // keypad 0: Insert, NVDA's own key
    [InlineData(0x0C, false, true)]    // keypad 5: Clear
    [InlineData(0x68, false, false)]   // keypad 8 with Num Lock on: the scope's
    [InlineData(0x0D, true, false)]    // the keypad's Enter is Enter
    [InlineData(0x55, false, false)]   // U
    public void TheWindowsKeypadWithNumLockOffIsNotTheGames(int virtualKey, bool extended, bool keypad)
        => Assert.Equal(keypad, OpenFPS.Client.Core.KeypadKeys.IsNumLockOffKeypad(virtualKey, extended));

    [Fact]
    public void TheExtendedFlagIsBit24OfTheKeyMessage()
    {
        Assert.True(OpenFPS.Client.Core.KeypadKeys.IsExtended(0x01480001));    // Up arrow, scan code 0x48, extended
        Assert.False(OpenFPS.Client.Core.KeypadKeys.IsExtended(0x00480001));   // keypad 8, Num Lock off
        Assert.True(OpenFPS.Client.Core.KeypadKeys.IsExtended(unchecked((int)0xC1480001)));   // its key-up
    }
}

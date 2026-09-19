using System;
using System.Linq;
using OpenFPS.Client.Core;
using OpenFPS.Client.Core.Input;
using OpenFPS.Client.Core.Platform;
using OpenFPS.Client.Core.Session;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Common;
using OpenFPS.Common.Networking;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// No gameplay action may be bound to a key a screen reader owns.
///
/// This is not a preference. CONTROL is how a screen reader user silences speech — every reader
/// there is stops talking when it is pressed — and ALT belongs to the window manager. A blind player
/// presses both dozens of times a minute as punctuation, without ever thinking of them as input to
/// the game. A game action on one of those keys is not a key that is awkward to use; it is a key
/// that fires by itself.
///
/// Control was the trigger. The cost is on record: five sessions chasing a report of *"random
/// banging... bang, wait a few seconds, bang, like someone closing a cabinet, I have no clue what the
/// noise is"*, through the reverb model, the room equation, the movement engine and the reflection
/// machinery — and the answer was twenty-six rifle shots at 159 dB that the player had fired himself
/// by shutting his screen reader up. It was found in an audio trace, not by reading the bindings.
///
/// So the bindings are asserted instead.
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
        public void ShowLoading(string status) { }
        public void UpdateLoadingStatus(string text, int percent) { }
        public void EnterGame() { }
        public void OpenCommandConsole() { }
        public void RequestQuit() { }
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

    /// <summary>
    /// ...and the trigger in particular is on a key a blind player can find by touch. Named because
    /// it is the one that went wrong, and because "there is a trigger at all" is worth asserting.
    /// </summary>
    [Fact]
    public void TheTriggerIsReachableAndIsNotAModifier()
    {
        var session = NewSession();

        Assert.True(session.IsBound(InputContext.Gameplay, GameKey.Enter), "there is no trigger bound");
        Assert.DoesNotContain(GameKey.Enter, ClientGameSession.ScreenReaderKeys);
    }
}

using System.Collections.Concurrent;
using System.Numerics;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.Core;
using OpenFPS.Client.Core.Platform;
using OpenFPS.Client.Core.Session;
using OpenFPS.Common;
using OpenFPS.Common.Networking;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// The probable bugs the Client.Core housekeeping pass reported (changes.md 2026-10-07), each confirmed
/// here by a test that failed before its fix.
/// </summary>
public class ProbableBugsTests
{
    private readonly ITestOutputHelper _o;
    public ProbableBugsTests(ITestOutputHelper o) => _o = o;

    // ── 1. A map change forgets the old map's one-offs ─────────────────────────────────────────────

    private static WorldAudioEvent LateKnock() => new()
    {
        SourceEntityId = 9, Label = "door", Seed = 1,
        Sounds = { new TransientSound { Character = SoundCharacter.Knock, LevelDb = 80f, Hz = 400f,
                                        DecaySeconds = 0.2f, DelaySeconds = 10f, Position = new Vector3(3, 1, 0) } },
    };

    /// <summary>
    /// A sound still waiting for its moment when /join takes you to another map belongs to the map you
    /// left: it was placed there, and the generation that drops thunder rendered for the old map only
    /// moved in a Clear nothing called.
    /// </summary>
    [Fact]
    public void TravelToAnotherMapForgetsTheOneOffsStillQueued()
    {
        AcousticRegistry.Initialize();
        var session = new ClientGameSession(new ClientNetworkService(), new QuietSpeech(), new QuietShell(),
            new AudioEngineFacade(), microphone: new NullMicrophoneCapture("no microphone here"), enableAudio: false);
        session.HandleMessage(new MapManifest { MapName = "first", ExpectedEntityCount = 0, WorldSize = new Vector3(100, 20, 100) });
        session.HandleMessage(new PlayerSpawned { EntityId = 5 });
        Assert.True(session.IsInGame);

        var world = session.AudioSystemForTest.WorldAudio;
        session.HandleMessage(LateKnock());
        Assert.True(world.Outstanding > 0, "the sound was never queued");
        int generation = world.MapGeneration;

        session.HandleMessage(new MapManifest { MapName = "second", ExpectedEntityCount = 0, WorldSize = new Vector3(100, 20, 100) });

        Assert.Equal(0, world.Outstanding);
        Assert.True(world.MapGeneration > generation, "thunder rendered for the old map would still play");
    }

    /// <summary>Leaving the world (a dropped connection, the main menu) forgets them as well.</summary>
    [Fact]
    public void LeavingTheWorldForgetsTheOneOffsStillQueued()
    {
        var h = new ClientAudioHarness();
        h.StandAt(Vector3.Zero);
        h.Audio.WorldAudio.Receive(LateKnock(), AudioClock.Now);
        Assert.True(h.Audio.WorldAudio.Outstanding > 0);

        h.Audio.LeaveWorld(Array.Empty<int>());

        Assert.Equal(0, h.Audio.WorldAudio.Outstanding);
    }

    // ── Harness ─────────────────────────────────────────────────────────────────────────────────

    private sealed class QuietSpeech : ISpeechOutput
    {
        public readonly ConcurrentQueue<string> Spoken = new();
        public string BackendName => "fake";
        public bool Initialize() => true;
        public void Speak(string text, bool interrupt = true) => Spoken.Enqueue(text);
        public void Interrupt() { }
        public void Dispose() { }
    }

    private sealed class QuietShell : IClientShell
    {
        public bool IsGameInputActive { get; set; } = true;
        public event Action<string>? CommandEntered { add { } remove { } }
        public void ShowLoading(string status, bool speak = true) { }
        public void UpdateLoadingStatus(string text, int percent) { }
        public void EnterGame() { }
        public void OpenCommandConsole() { }
        public void ShowGameMenu(Action<GameMenuChoice> chosen) { }
        public void ReturnToMenu() { }
        public void Quit() { }
    }
}

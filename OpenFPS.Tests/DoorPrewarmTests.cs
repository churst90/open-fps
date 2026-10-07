using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Arch.Core;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Client.Core;
using OpenFPS.Client.Core.Platform;
using OpenFPS.Client.Core.Session;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using OpenFPS.Server.Systems;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// Every door on the city is heard the first time it is opened. A door model's render takes seconds and a
/// first hearing waits a tenth of one, so a sound the client has not rendered before is silent: Cody,
/// 2026-10-05, "it doesn't play a sound for the first few times I open it". The client renders a list at
/// start (WorldAudioPlayer.PrewarmKeys) and keeps each on disk (DoorRenderCache); these tests hold the
/// list to what the city's doors ask for, and the cache to giving back what it was given.
/// </summary>
public class DoorPrewarmTests
{
    public DoorPrewarmTests() { AcousticRegistry.Initialize(); }

    /// <summary>The keys DoorSystem sends for a door, built as it builds them (KnobDoorSound, PushBarSound,
    /// GlassDoorSound): every opening from either side, and every close.</summary>
    private static IEnumerable<string> KeysFor(World world, Entity e)
    {
        var door = world.Get<DoorComponent>(e);
        var size = world.Get<ColliderComponent>(e).Size;
        int id = e.Id;
        switch ((DoorKind)door.Kind)
        {
            case DoorKind.Hinged:
                yield return KnobDoor.Key(false, KnobDoor.Construction.HollowCore, id, door.SwingSeconds, KnobDoor.Shut.Normal, size.X, size.Y);
                yield return KnobDoor.Key(false, KnobDoor.Construction.HollowCore, id, door.SwingSeconds, KnobDoor.Shut.Normal, size.X, size.Y, push: true);
                foreach (var how in new[] { KnobDoor.Shut.Gentle, KnobDoor.Shut.Normal, KnobDoor.Shut.Hard })
                    yield return KnobDoor.Key(true, KnobDoor.Construction.HollowCore, id, door.SwingSeconds, how, size.X, size.Y);
                break;
            case DoorKind.PushBar:
                yield return PushBarDoor.Key(false, id, door.SwingSeconds, size.X, size.Y);
                yield return PushBarDoor.Key(false, id, door.SwingSeconds, size.X, size.Y, pull: true);
                yield return PushBarDoor.Key(true, id, door.SwingSeconds, size.X, size.Y);
                break;
            case DoorKind.GlassPushBar:
                foreach (var how in new[] { GlassDoor.Opening.Key, GlassDoor.Opening.Push })
                    yield return GlassDoor.Key(GlassDoor.Kind.PushBar, false, how, GlassDoor.Glazing.Tempered, id, door.SwingSeconds, size.X, size.Y);
                yield return GlassDoor.Key(GlassDoor.Kind.PushBar, true, GlassDoor.Opening.Pull, GlassDoor.Glazing.Tempered, id, door.SwingSeconds, size.X, size.Y);
                break;
        }
    }

    [Fact]
    public void EveryHingedDoorOnTheCityIsRenderedAtStart()
    {
        var maps = new MapManager(new MapRepository(Path.Combine(AppContext.BaseDirectory, "maps")),
                                  new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs")));
        maps.Initialize();
        Assert.True(maps.TryGetMap("city", out var world, out _, out _, out _));

        var prewarmed = WorldAudioPlayer.PrewarmKeys().ToHashSet();
        var missing = new SortedSet<string>();
        int doors = 0;
        world.Query(new QueryDescription().WithAll<DoorComponent, ColliderComponent>(), (Entity e) =>
        {
            doors++;
            foreach (string key in KeysFor(world, e))
                if (!prewarmed.Contains(key)) missing.Add(key);
        });
        Assert.True(doors > 400, $"{doors} doors on the city");
        Assert.True(missing.Count == 0, "not rendered at start: " + string.Join(", ", missing.Take(12)));
    }

    [Fact]
    public void AKeptRenderComesBackAsItWent()
    {
        string dir = Path.Combine(Path.GetTempPath(), "openfps-rendercache-" + Guid.NewGuid().ToString("N"));
        string? was = DoorRenderCache.Folder;
        try
        {
            DoorRenderCache.Folder = dir;
            string key = KnobDoor.Key(false, KnobDoor.Construction.HollowCore, 1, 0.9f, KnobDoor.Shut.Normal, 1.1f, 2.1f);
            Assert.False(DoorRenderCache.TryLoad(key, out _, out _));
            var pcm = new[] { 0f, 0.5f, -1f, 0.25f };
            DoorRenderCache.Store(key, pcm, 97.5f);
            Assert.True(DoorRenderCache.TryLoad(key, out var back, out float db));
            Assert.Equal(pcm.Length, back.Length);
            for (int i = 0; i < pcm.Length; i++) Assert.InRange(back[i], pcm[i] - 1e-4f, pcm[i] + 1e-4f);
            // Over full scale (a car window's render is not peak-normalised) comes back whole, not clipped.
            DoorRenderCache.Store(key, new[] { 1.5f, -0.75f }, 0f);
            Assert.True(DoorRenderCache.TryLoad(key, out var loud, out _));
            Assert.InRange(loud[0], 1.4999f, 1.5001f);
            DoorRenderCache.Store(key, pcm, 97.5f);
            Assert.True(DoorRenderCache.TryLoad(key, out back, out db));
            Assert.Equal(97.5f, db);
            // Another key is not this one's render.
            Assert.False(DoorRenderCache.TryLoad(key + ":push", out _, out _));
            // A pane breaking has a key of its own each time: never kept.
            Assert.False(DoorRenderCache.Keeps(GlassFracture.KeyPrefix + "break:x"));
        }
        finally
        {
            DoorRenderCache.Folder = was;
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>A session with no sound (every test that builds one) renders no doors. Each one rendered the
    /// whole list on the thread pool, minutes of every core, and on a 4-core CI runner the next test's
    /// password check waited behind them past the 30-minute hang timeout (2026-10-07).</summary>
    [Fact]
    public void ASessionWithoutSoundRendersNoDoors()
    {
        var session = new ClientGameSession(new ClientNetworkService(), new Silent(), new NoShell(), new AudioEngineFacade(),
            microphone: new NullMicrophoneCapture("none"), enableAudio: false);
        Assert.Equal(0, session.AudioSystemForTest.WorldAudio.RendersOutstanding);
    }

    private sealed class Silent : ISpeechOutput
    {
        public string BackendName => "test";
        public bool Initialize() => true;
        public void Speak(string text, bool interrupt = true) { }
        public void Interrupt() { }
        public void Dispose() { }
    }

    private sealed class NoShell : IClientShell
    {
        public bool IsGameInputActive { get; set; } = true;
        public event Action<string>? CommandEntered { add { } remove { } }
        public void ShowLoading(string status, bool speak = true) { }
        public void UpdateLoadingStatus(string text, int percent) { }
        public void EnterGame() { }
        public void OpenCommandConsole() { }
        public void OpenCommandConsole(string initialText) { }
        public void ShowGameMenu(Action<GameMenuChoice> chosen) { }
        public void ReturnToMenu() { }
        public void Quit() { }
    }
}

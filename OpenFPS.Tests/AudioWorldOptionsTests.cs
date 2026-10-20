using OpenFPS.Audio.Acoustics;
using OpenFPS.Audio.Core;
using OpenFPS.Audio.Fmod;
using System.Numerics;
using OpenFPS.Sound;
using OpenFPS.Sound.Core.Engine;
using OpenFPS.Sound.Core.Nature;
using OpenFPS.Client.Core;
using OpenFPS.Models;

namespace OpenFPS.Tests;

/// <summary>
/// One audio world's options and data paths (docs/SOUND_LIBRARY_BOUNDARY.md, 7.4, 7.5 and 19): the wide sources'
/// and the cabins' levers were statics every world shared, and the door render cache's folders were statics the
/// library worked out for itself. Each is now a world's, so a lever set in one world never reaches another.
/// </summary>
public class AudioWorldOptionsTests
{
    [Fact]
    public void AWorldsWideSourcesAreItsOwn()
    {
        var shipped = new WideSources();
        var oldRule = new WideSources { Rule = ExtendedSources.PlaceRule.Middle };
        var narrow = new WideSources { Enabled = false };

        // The creek is a line cut at the ear under the shipped rule, a layout of places under the old one.
        Assert.Null(shipped.Layout("flow:creek", ModelLibrary.Presets));
        Assert.Equal(RunningWaterSpec.Creek.LengthMetres, shipped.LineLength("flow:creek", ModelLibrary.Presets));
        Assert.NotNull(oldRule.Layout("flow:creek", ModelLibrary.Presets));
        Assert.Equal(0f, oldRule.LineLength("flow:creek", ModelLibrary.Presets));
        Assert.Equal(ExtendedSources.MaxPlaces, shipped.FlowPlaces("flow:creek", RunningWaterSpec.Creek, ModelLibrary.Presets));
        Assert.Equal(RunningWaterSpec.Creek.Places, oldRule.FlowPlaces("flow:creek", RunningWaterSpec.Creek, ModelLibrary.Presets));

        // A world that does not spread hears every one from one point, and the others still spread.
        Assert.Null(narrow.Layout("foliage:park_tree", ModelLibrary.Presets));
        Assert.Equal(0f, narrow.LineLength("flow:creek", ModelLibrary.Presets));
        Assert.NotNull(shipped.Layout("foliage:park_tree", ModelLibrary.Presets));
        Assert.NotNull(oldRule.Layout("foliage:park_tree", ModelLibrary.Presets));
    }

    [Fact]
    public void ALeverChangedIsHeardByTheNextLayout()
    {
        var wide = new WideSources();
        // A tree's crown is laid out in places; a fire is cut at the ear and has no layout.
        var spread = wide.Layout("foliage:park_tree", ModelLibrary.Presets)!;
        Assert.Contains(spread.Skip(1), p => p.Length() > 0.1f);

        // The lab collapses the places onto the middle: the layout asked for next is made under the new scale.
        wide.LayoutScale = 0f;
        var collapsed = wide.Layout("foliage:park_tree", ModelLibrary.Presets)!;
        Assert.Equal(spread.Length, collapsed.Length);
        Assert.All(collapsed, p => Assert.Equal(Vector3.Zero, p));
        // The one handed out before keeps its places, and another world's are untouched.
        Assert.Contains(spread.Skip(1), p => p.Length() > 0.1f);
        Assert.Contains(new WideSources().Layout("foliage:park_tree", ModelLibrary.Presets)!.Skip(1), p => p.Length() > 0.1f);

        // The forced spread is the world's too.
        Assert.Equal(0.25f, new WideSources { ForceSpread = 0.25f }.SpreadFor(3f, 200f));
        Assert.Equal(ExtendedSources.SpreadFor(3f, 200f), wide.SpreadFor(3f, 200f));
    }

    [Fact]
    public void TheEnvironmentChoosesAsTheStaticsDid()
    {
        var shipped = AudioWorldOptions.From(_ => null);
        Assert.True(shipped.WideSources.Enabled);
        Assert.Equal(ExtendedSources.PlaceRule.Pieces, shipped.WideSources.Rule);
        Assert.True(shipped.CabinPathsOn);

        var vars = new Dictionary<string, string>
        {
            ["OPENFPS_WIDE_SOURCES"] = "0", ["OPENFPS_EXTENT_RULE"] = "middle", ["OPENFPS_CABIN_PATHS"] = "0",
        };
        var set = AudioWorldOptions.From(vars.GetValueOrDefault);
        Assert.False(set.WideSources.Enabled);
        Assert.Equal(ExtendedSources.PlaceRule.Middle, set.WideSources.Rule);
        Assert.False(set.CabinPathsOn);
    }

    [Fact]
    public void TheBudgetsComeFromTheEnvironmentAsTheStaticsDid()
    {
        var shipped = AudioWorldOptions.From(_ => null).Budgets;
        Assert.Equal(new VoiceBudgets(), shipped);
        Assert.Equal((32, EngineReflections.MaxEchoesPerEngine, 6, 36, 24),
                     (shipped.Engines, shipped.Echoes, shipped.Front, shipped.Places, shipped.Machines));

        var vars = new Dictionary<string, string>
        {
            ["OPENFPS_ENGINE_VOICES"] = "5", ["OPENFPS_ENGINE_ECHOES"] = "9", ["OPENFPS_FRONT_VOICES"] = "0",
            ["OPENFPS_PLACE_VOICES"] = "12", ["OPENFPS_MACHINE_VOICES"] = "0", ["OPENFPS_AUDIO_DEBUG"] = "1",
        };
        var set = AudioWorldOptions.From(vars.GetValueOrDefault);
        // Echoes no more than an engine has; no engines at all is not a budget, no machines is.
        Assert.Equal((5, EngineReflections.MaxEchoesPerEngine, 0, 12, 0),
                     (set.Budgets.Engines, set.Budgets.Echoes, set.Budgets.Front, set.Budgets.Places, set.Budgets.Machines));
        Assert.True(set.Trace);
        Assert.False(AudioWorldOptions.From(_ => null).Trace);
        Assert.Equal(32, AudioWorldOptions.From(new Dictionary<string, string> { ["OPENFPS_ENGINE_VOICES"] = "0" }.GetValueOrDefault).Budgets.Engines);
    }

    [Fact]
    public void AWorldsEngineBudgetIsItsOwn()
    {
        static int Engines(AudioWorldOptions? options)
        {
            var h = new ClientAudioHarness(options: options);
            h.StandAt(Vector3.Zero);
            for (int i = 0; i < 5; i++)
                h.AddCar(500 + i, "i4_economy", new Vector3(15f * MathF.Cos(i), 0f, 15f * MathF.Sin(i)));
            h.Tick(30);
            return h.Mixer.Started.Where(e => !string.IsNullOrEmpty(e.EngineKey)).Select(e => e.EntityId).Distinct().Count();
        }
        Assert.Equal(5, Engines(null));
        Assert.Equal(3, Engines(new AudioWorldOptions { Budgets = new VoiceBudgets { Engines = 3 } }));
    }

    [Fact]
    public void TheCabinsPathsAreTheWorlds()
    {
        var v = MachineRegistry.VehicleFor("i4_economy");
        var split = new AudioWorldOptions();
        var onePoint = new AudioWorldOptions { CabinPathsOn = false };
        var lab = new AudioWorldOptions { CabinOnePlace = true };
        var layout = split.CabinLayout(v, ModelLibrary.Presets);
        Assert.NotNull(layout);
        Assert.Null(onePoint.CabinLayout(v, ModelLibrary.Presets));
        var ear = new Vector3(-0.36f, 1.25f, 0.28f);
        Assert.Equal(CabinPaths.Offset(layout!, 1, ear), split.CabinOffset(layout!, 1, ear));
        Assert.Equal(CabinPaths.OnePlace, lab.CabinOffset(layout!, 1, ear));
        Assert.NotEqual(CabinPaths.OnePlace, split.CabinOffset(layout!, 1, ear));

        // An engine made for a world that plays its cabin from one point has no paths.
        Assert.NotNull(new EngineVoiceState(v, 48000, 7).CabinLayout);
        Assert.Null(new EngineVoiceState(v, 48000, 7, cabinPaths: false).CabinLayout);
    }

    [Fact]
    public void TheHostReachesTheMixersOptions()
    {
        var options = new AudioWorldOptions { CabinPathsOn = false };
        var facade = new AudioEngineFacade(new FmodAudioProvider { Options = options });
        Assert.Same(options, facade.Options);
        // A mixer that is not FMOD's: the world's given, or the shipped one.
        Assert.Same(options, new AudioEngineFacade(new VoiceLifecycleTests.RecordingProvider(), options: options).Options);
        Assert.True(new AudioEngineFacade(new VoiceLifecycleTests.RecordingProvider()).Options.CabinPathsOn);
    }

    [Fact]
    public void DoorRendersAreKeptWhereTheHostSays()
    {
        string a = Path.Combine(Path.GetTempPath(), "openfps-doors-a-" + Guid.NewGuid().ToString("N"));
        string b = Path.Combine(Path.GetTempPath(), "openfps-doors-b-" + Guid.NewGuid().ToString("N"));
        try
        {
            string key = KnobDoor.Key(false, KnobDoor.Construction.HollowCore, 1, 0.9f, KnobDoor.Shut.Normal, 1.1f, 2.1f);
            var first = new DoorRenderCache(a, shippedFolder: null);
            var second = new DoorRenderCache(b, shippedFolder: a);
            first.Store(key, new[] { 0f, 0.5f, -1f }, 90f);
            Assert.True(first.TryLoad(key, out _, out _));
            // Another world's folder holds none of it; one that ships the first's folder reads it from there.
            Assert.False(new DoorRenderCache(b, shippedFolder: null).TryLoad(key, out _, out _));
            Assert.True(second.TryLoad(key, out var shipped, out float db));
            Assert.Equal(3, shipped.Length);
            Assert.Equal(90f, db);
        }
        finally
        {
            foreach (var dir in new[] { a, b })
                try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }

        // A world made without paths reads what ships beside the program and keeps nothing; the client's are the
        // player's, which OPENFPS_RENDER_CACHE moves (the tests' scratch folder, TestConfigIsolation).
        var bare = new AudioEngineFacade(new VoiceLifecycleTests.RecordingProvider());
        Assert.Null(bare.DoorRenders.Folder);
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "ASSETS", "SOUNDS"), bare.Paths.Sounds);
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "ASSETS", "rendercache", DoorRenderCache.Name), bare.DoorRenders.ShippedFolder);
        var player = ClientAudioPaths.ForPlayer();
        Assert.Equal(System.Environment.GetEnvironmentVariable("OPENFPS_RENDER_CACHE"), player.RenderCache);
        Assert.Equal(player.RenderCache, new AudioEngineFacade(new VoiceLifecycleTests.RecordingProvider(), paths: player).DoorRenders.Folder);
    }
}

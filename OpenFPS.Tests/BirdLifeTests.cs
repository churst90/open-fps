using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Arch.Core;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.Core;
using OpenFPS.Common;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using Xunit;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// The birds on the city: found from its foliage and its roofs rather than placed, a hedge of
/// sparrows heard as sparrows in a hedge, and a bang that shuts them up.
/// </summary>
public class BirdLifeTests
{
    private readonly ITestOutputHelper _o;
    public BirdLifeTests(ITestOutputHelper o) => _o = o;

    private static string Sounds()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "OpenFPS.Client", "ASSETS", "SOUNDS"))) dir = dir.Parent;
        // The build output carries its own copy when the repository is not above it.
        return dir != null ? Path.Combine(dir.FullName, "OpenFPS.Client", "ASSETS", "SOUNDS")
                           : "/home/cody/external-rescue/Github/open-fps/OpenFPS.Client/ASSETS/SOUNDS";
    }

    private static (WorldSnapshot World, BirdLife Birds, VoiceLifecycleTests.RecordingProvider Mixer, AudioEngineFacade Audio) City()
    {
        var prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
        var maps = new MapManager(new MapRepository(Path.Combine(AppContext.BaseDirectory, "maps")), prefabs);
        maps.Initialize();
        Assert.True(maps.TryGetMap("city", out World world, out _, out _, out _));
        Assert.True(maps.TryGetMapData("city", out var data));
        var client = new ClientWorldState();
        client.Clear(data.Size, data.MinBound, data.MaxBound);
        foreach (var def in EntityDefinitionFactory.StaticDefinitions(world)) client.RegisterDefinition(def);
        var mixer = new VoiceLifecycleTests.RecordingProvider();
        var audio = new AudioEngineFacade(mixer);
        if (!Directory.Exists("/home/cody/external-rescue/Github/open-fps/OpenFPS.Client/ASSETS/SOUNDS/BIRDS")
            && !Directory.Exists(Path.Combine(Sounds(), "BIRDS")))
            throw new InvalidOperationException("no bird samples");
        audio.InitializeForTest(Sounds());
        var birds = new BirdLife(audio, new OpenFPS.Client.AudioEngine.Acoustics.SpatialAcoustics(new SpatialService()));
        return (client.GetSnapshot(), birds, mixer, audio);
    }

    /// <summary>Calls made within a few metres of <paramref name="near"/> over the run.</summary>
    private static int Run(WorldSnapshot world, BirdLife birds, VoiceLifecycleTests.RecordingProvider mixer, AudioEngineFacade audio,
                           Vector3 ear, double from, double seconds, string folder, Vector3? near = null)
    {
        mixer.PlayedSounds.Clear();
        int here = 0;
        birds.OnCall = (sp, at) => { if (near is { } n && MathF.Abs(at.X - n.X) < 1.5f && MathF.Abs(at.Z - n.Z) < 8f) here++; };
        audio.UpdateListener(ear, Quaternion.Identity, Vector3.Zero, -1);
        for (double t = from; t < from + seconds; t += 0.05)
        {
            birds.Update(world, ear, t);
            audio.PumpForTest();
        }
        return near != null ? here : mixer.PlayedSounds.Count(s => s.StartsWith("BIRDS/" + folder, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheCitysHedgesAndRoofsHaveBirdsWithoutAnyBeingPlaced()
    {
        var (world, birds, mixer, audio) = City();
        birds.Update(world, Vector3.Zero, 0);
        var census = birds.Census;
        _o.WriteLine(string.Join(", ", census.Select(kv => $"{kv.Value} {kv.Key}")));
        Assert.True(census.GetValueOrDefault("house sparrow") > 100);
        Assert.True(census.GetValueOrDefault("pigeon") > 0);
        Assert.True(census.GetValueOrDefault("dove") > 0);
    }

    [Fact]
    public void AHedgeOfSparrowsChattersAndABangShutsItUp()
    {
        var (world, birds, mixer, audio) = City();
        birds.Update(world, Vector3.Zero, 0);
        var hedge = birds.Groups.Where(g => g.Species == "house sparrow").OrderByDescending(g => g.Birds).First();
        var ear = hedge.Centre + new Vector3(0f, 0.7f, 0f) + Vector3.Normalize(new Vector3(1f, 0f, 1f)) * 5f;

        int chatter = Run(world, birds, mixer, audio, ear, 1, 60, "SPARROW", hedge.Centre);
        _o.WriteLine($"{hedge.Birds} sparrows at {hedge.Centre}: {chatter} chirps in a minute from 5 m "
                   + $"({chatter / 60.0 / hedge.Birds:F2} a second each)");
        // Bouts of chirps with rests between: somewhere between a tenth and a half of a call a
        // second per bird. A hedge that never rests is a machine; one that barely calls is empty.
        Assert.InRange(chatter / 60.0 / hedge.Birds, 0.1, 0.5);

        // A shot in the street beside them.
        birds.Heard(new WorldAudioEvent
        {
            Label = "gunshot",
            Sounds = new List<TransientSound> { new() { Position = hedge.Centre + new Vector3(6f, 0f, 0f), LevelDb = 150f } },
        }, 61);
        int after = Run(world, birds, mixer, audio, ear, 61, 7.5, "SPARROW", hedge.Centre);
        _o.WriteLine($"in the 7.5 s after a shot: {after}");
        Assert.True(after <= 1, "the sparrows carried on through a gunshot");

        int later = Run(world, birds, mixer, audio, ear, 68.5, 90, "SPARROW", hedge.Centre);
        _o.WriteLine($"in the 90 s after that: {later}");
        Assert.True(later > 3, "they never came back");
    }

    [Fact]
    public void WalkRightUpToAHedgeAndItGoesQuiet()
    {
        var (world, birds, mixer, audio) = City();
        birds.Update(world, Vector3.Zero, 0);
        var hedge = birds.Groups.Where(g => g.Species == "house sparrow").OrderByDescending(g => g.Birds).First();
        // Warm up at a distance, then stand in it.
        Run(world, birds, mixer, audio, hedge.Centre + new Vector3(8f, 1f, 0f), 1, 30, "SPARROW", hedge.Centre);
        int inside = Run(world, birds, mixer, audio, hedge.Centre + new Vector3(0f, 0.5f, 0f), 31, 20, "SPARROW", hedge.Centre);
        _o.WriteLine($"standing in the hedge for 20 s: {inside} chirps (others' hedges may still be heard)");
        Assert.True(inside <= 2);
    }
}

using System.Numerics;
using OpenFPS.Client.AudioEngine.Acoustics;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Client.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;

namespace OpenFPS.Tests;

/// <summary>
/// The floor between two flats and what it lets through (2026-10-10, Cody: "I can't hear anything on the
/// game level ... it's fuzzy and the sound cuts out"). One structural slab between storeys, as a block of
/// flats is built, not a slab per storey's floor and another per its ceiling; and a voice heard through it
/// starts on its path, keeps its units until the mixer has finished its last block, and does not take a
/// room's dry sends through a bypassed reverb stage.
/// </summary>
public class FloorsBetweenFlatsTests
{
    private static float Db(float gain) => -20f * MathF.Log10(MathF.Max(1e-12f, gain));

    /// <summary>Selby House, flat 11F (floor 1) and 21F over it: where --floor-render listens.</summary>
    private static readonly Vector3 EarBelow = new(14f, 3.29f + 1.6f, 176f), MouthAbove = new(14.5f, 6.29f + 1.6f, 176.5f);

    private static WorldSnapshot SelbyHouse()
    {
        AcousticRegistry.Initialize();
        var prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
        var maps = new MapManager(new MapRepository(Path.Combine(AppContext.BaseDirectory, "maps")), prefabs);
        maps.Initialize();
        Assert.True(maps.TryGetMap("city", out var ecs, out _, out _, out _));
        var world = new ClientWorldState();
        world.Clear(new Vector3(4000, 400, 4000));
        foreach (var def in EntityDefinitionFactory.StaticDefinitions(ecs))
            if (Vector3.Distance(def.Transform.Position, EarBelow) < 40f || def.Collider.Size.Length() > 40f)
                world.RegisterDefinition(def);
        return world.GetSnapshot();
    }

    private static List<(string Material, float Bottom, float Top)> SolidsOnTheVertical(WorldSnapshot world, float x, float z, float y0, float y1)
    {
        var found = new List<(string, float, float)>();
        foreach (var e in world.Entities.Values)
        {
            var d = e.Definition;
            if (d.Type != EntityType.StaticObject || !d.Collider.IsSolid || d.Collider.Shape != ColliderShape.Box) continue;
            Vector3 c = d.Transform.Position, h = d.Collider.Size / 2f;
            if (MathF.Abs(x - c.X) > h.X || MathF.Abs(z - c.Z) > h.Z) continue;
            if (c.Y + h.Y <= y0 || c.Y - h.Y >= y1) continue;
            found.Add((d.Material.Material, c.Y - h.Y, c.Y + h.Y));
        }
        return found.OrderBy(f => f.Item2).ToList();
    }

    [Fact]
    public void BetweenTwoFlatsThereIsOneSlab()
    {
        var world = SelbyHouse();
        var between = SolidsOnTheVertical(world, 14f, 176f, EarBelow.Y, MouthAbove.Y);
        var concrete = between.Where(s => s.Material == "Concrete").ToList();
        Assert.True(concrete.Count == 1, "between flat 11F and 21F: " + string.Join(", ", between.Select(s => $"{s.Material} {s.Bottom:F2}-{s.Top:F2}")));
        Assert.InRange(concrete[0].Top - concrete[0].Bottom, 0.149f, 0.151f);
        // The soffit under it and the carpet on it touch it: one construction (Constructions).
        Assert.Contains(between, s => s.Material == "Plaster" && MathF.Abs(s.Top - concrete[0].Bottom) < 1e-3f);
        Assert.Contains(between, s => s.Material == "Carpet" && MathF.Abs(s.Bottom - concrete[0].Top) < 1e-3f);
    }

    [Fact]
    public void TheFloorLosesWhatALabTestedSlabLosesTheSameBothWays()
    {
        var world = SelbyHouse();
        var tracer = new SpatialService();
        tracer.GetOcclusionData(world, EarBelow, MouthAbove, out _, out _, out float ul, out float um, out float uh);
        tracer.GetOcclusionData(world, MouthAbove, EarBelow, out _, out _, out float dl, out float dm, out float dh);
        // A 152 mm slab with tile, RAL-TL15-332 (STC 54), over the same bands: 40 / 54 / 83 dB.
        Assert.InRange(Db(ul), 35f, 43f);
        Assert.InRange(Db(um), 50f, 58f);
        Assert.True(Db(uh) > 75f, $"high band {Db(uh):F1} dB");
        Assert.True(MathF.Abs(Db(ul) - Db(dl)) < 0.1f && MathF.Abs(Db(um) - Db(dm)) < 0.1f && MathF.Abs(Db(uh) - Db(dh)) < 0.1f,
                    $"up {Db(ul):F1}/{Db(um):F1}/{Db(uh):F1}, down {Db(dl):F1}/{Db(dm):F1}/{Db(dh):F1}");
    }

    [Fact]
    public void ARoomsBusIsSilentWhileItsTracedStageIsBypassed()
    {
        // A bypassed stage passes its input through, and a room's sends are taken before the walls.
        Assert.Equal(0f, FmodAudioProvider.BusVolumeFor(0.0008f, hasStage: true, stageRunning: false));
        Assert.Equal(0.5f, FmodAudioProvider.BusVolumeFor(0.5f, hasStage: true, stageRunning: true));
        // No Steam Audio, no stage: the bus is what it always was.
        Assert.Equal(0.0008f, FmodAudioProvider.BusVolumeFor(0.0008f, hasStage: false, stageRunning: false));
        Assert.False(FmodAudioProvider.StageRuns(FmodAudioProvider.StageAudibleVolume));
        Assert.True(FmodAudioProvider.StageRuns(FmodAudioProvider.StageAudibleVolume * 1.01f));
    }

    [Fact]
    public void ANewVoiceIsPlacedOnItsPathBeforeItIsHeard()
    {
        // FMOD needs a sound card to construct, so the order is read off the source: the first attribute
        // pass runs before the channel is unpaused, or the first block plays at the emitter's bare volume
        // through whatever EQ gains the pooled unit kept.
        string source = File.ReadAllText(Source(new[] { "OpenFPS.Client.Core", "AudioEngine", "Fmod", "FmodAudioProvider.cs" }));
        int play = source.IndexOf("public void PlaySpatialSound(", StringComparison.Ordinal);
        Assert.True(play > 0);
        int start = source.IndexOf("StartOnPath(activeSound)", play, StringComparison.Ordinal);
        int unpause = source.IndexOf("channel.setPaused(false)", play, StringComparison.Ordinal);
        Assert.True(start > 0 && unpause > 0 && start < unpause, "StartOnPath must come before the channel is unpaused");
    }

    [Fact]
    public void AVoiceKeepsItsUnitsUntilTheMixerIsDoneWithIt()
    {
        // At least two mixer blocks of 1024 at 48 kHz after FMOD says it stopped; well under a step apart.
        Assert.InRange(FmodAudioProvider.ReleaseAfterEndSeconds, 2 * 1024 / 48000.0, 0.15);
        Assert.False(FmodAudioProvider.MayRelease(10.0, 10.0 + FmodAudioProvider.ReleaseAfterEndSeconds * 0.5));
        Assert.True(FmodAudioProvider.MayRelease(10.0, 10.0 + FmodAudioProvider.ReleaseAfterEndSeconds * 1.01));
    }

    [Fact]
    public void AnEndedOrStoppedVoiceIsRetiredNotReleasedOnTheSpot()
    {
        // FMOD needs a sound card to construct, so this is read off the source: the reaper and StopSound
        // hand a voice to the retiring list; only ReleaseRetired (and Dispose) take its units off.
        string source = File.ReadAllText(Source(new[] { "OpenFPS.Client.Core", "AudioEngine", "Fmod", "FmodAudioProvider.cs" }));
        int reap = source.IndexOf("if (!isPlaying) {", StringComparison.Ordinal);
        Assert.True(reap > 0);
        string branch = source.Substring(reap, source.IndexOf('}', reap) - reap);
        Assert.Contains("_retiring.Add(", branch);
        Assert.DoesNotContain("ReleaseActiveSoundResources", branch);
        int stop = source.IndexOf("public void StopSound(int entityId)", StringComparison.Ordinal);
        string stopBody = source.Substring(stop, source.IndexOf("public bool IsPlaying(", stop, StringComparison.Ordinal) - stop);
        Assert.Contains("_retiring.Add(", stopBody);
        Assert.DoesNotContain("ReleaseActiveSoundResources", stopBody);
    }

    private static string Source(string[] parts, [System.Runtime.CompilerServices.CallerFilePath] string here = "")
        => Path.Combine(new[] { Path.GetDirectoryName(here)!, ".." }.Concat(parts).ToArray());
}

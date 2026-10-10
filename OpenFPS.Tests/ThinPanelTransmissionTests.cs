using System.Numerics;
using OpenFPS.Client.Core.AudioEngine.SteamAudio;
using OpenFPS.Common;
using Box = OpenFPS.Client.Core.AudioEngine.SteamAudio.SteamAudioScene.Box;

namespace OpenFPS.Tests;

/// <summary>
/// Steam Audio's direct stage must let through one panel what the panel's construction does
/// (WallTransmission), however thin it is. Its transmission rays step about 2 cm past every hit, so a
/// panel thinner than that was crossed as two faces where a thicker one is three, and lost a third of its
/// decibels: a 12 mm glass front door passed -12/-19/-30 dB against its -18/-28/-45 (AudioLab
/// --thin-panel, 2026-10-09). Both ways the scene is built: the whole scene from boxes, and the tile
/// scenes from the triangle store. Needs Steam Audio with Embree (SteamAudioForTests).
/// </summary>
public class ThinPanelTransmissionTests
{
    private static float Db(float g) => 20f * MathF.Log10(MathF.Max(1e-6f, g));

    /// <summary>A glass leaf 1.9 by 2.1 m, as glass_front_door is, of the given thickness, across the
    /// line from an ear 2 m one side to a source 10 m the other.</summary>
    private static List<Box> Panel(float thickness) => new()
    {
        new(new Vector3(0, 1.05f, 0), new Vector3(1.9f, 2.1f, thickness), Quaternion.Identity, "Glass"),
    };

    private static readonly Vector3 Ear = new(0, 1.0f, -2f), Source = new(0, 1.0f, 10f);

    private static SteamAudioSimulator.DirectResult Trace(IntPtr ctx, SteamAudioScene scene)
    {
        using var sim = new SteamAudioSimulator(ctx, maxSources: 1);
        sim.SetScene(scene);
        IntPtr src = sim.AcquireSource();
        sim.SetListener(Ear);
        sim.SetSourceInputs(src, Source, 0.01f);
        sim.Run();
        return sim.GetResult(src);
    }

    private static void AssertAsBuilt(SteamAudioSimulator.DirectResult r, float thickness, string how)
    {
        var (l, m, h) = WallTransmission.BandGains("Glass", new Vector3(1.9f, 2.1f, thickness), default);
        string at = $"{thickness * 1000f:F0} mm glass, {how}: Steam Audio {Db(r.TransLow):F1}/{Db(r.TransMid):F1}/{Db(r.TransHigh):F1} dB, "
                  + $"the panel {Db(l):F1}/{Db(m):F1}/{Db(h):F1}";
        Assert.True(r.Visibility < 0.1f, at + $"; visibility {r.Visibility:F2}");
        Assert.True(MathF.Abs(Db(r.TransLow) - Db(l)) < 1f, at);
        Assert.True(MathF.Abs(Db(r.TransMid) - Db(m)) < 1f, at);
        Assert.True(MathF.Abs(Db(r.TransHigh) - Db(h)) < 1f, at);
    }

    [SteamAudioEmbreeFact]
    public void A_thin_panel_passes_its_own_transmission_in_the_whole_scene()
    {
        IntPtr ctx = SteamAudioForTests.EmbreeContext;
        AcousticRegistry.Initialize();
        foreach (float t in new[] { 0.006f, 0.012f, 0.05f })
        {
            using var scene = new SteamAudioScene(ctx);
            scene.Build(Panel(t));
            AssertAsBuilt(Trace(ctx, scene), t, "whole scene");
        }
    }

    [SteamAudioEmbreeFact]
    public void A_thin_panel_passes_its_own_transmission_in_the_tile_scenes()
    {
        IntPtr ctx = SteamAudioForTests.EmbreeContext;
        AcousticRegistry.Initialize();
        foreach (float t in new[] { 0.006f, 0.012f, 0.05f })
        {
            using var set = new TileSceneSet(ctx, 20f);
            set.Update(Panel(t));
            var (full, _) = set.Assemble();
            AssertAsBuilt(Trace(ctx, full), t, "tile scenes");
        }
    }
}

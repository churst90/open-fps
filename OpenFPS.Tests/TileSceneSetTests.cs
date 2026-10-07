using System.Numerics;
using OpenFPS.Client.Core.AudioEngine.SteamAudio;
using OpenFPS.Common;
using Box = OpenFPS.Client.Core.AudioEngine.SteamAudio.SteamAudioScene.Box;

namespace OpenFPS.Tests;

/// <summary>
/// A Steam Audio context with Embree for the few tests that trace real scenes, made once per test run, or
/// none. Opt-in, by OPENFPS_STEAMAUDIO_TESTS=1 (Steam Audio from the nearest lib/ above the tests, which
/// is per-developer and not in git) or =DIR (from DIR). The library is never copied next to the tests:
/// every other test that starts the occlusion worker expects it absent (DegradationTests) and would run
/// Steam Audio if it could load it, so these run on their own:
///
///   OPENFPS_STEAMAUDIO_TESTS=1 dotnet test OpenFPS.Tests --filter "FullyQualifiedName~TileSceneSetTests"
///
/// Without it (the CI runners, a whole-suite run) they are skipped.
/// </summary>
internal static class SteamAudioForTests
{
    private static readonly Lazy<IntPtr> Context = new(() =>
    {
        string? asked = Environment.GetEnvironmentVariable("OPENFPS_STEAMAUDIO_TESTS");
        if (string.IsNullOrEmpty(asked) || asked == "0") return IntPtr.Zero;
        string? file = Find(asked);
        if (file == null) return IntPtr.Zero;
        try
        {
            System.Runtime.InteropServices.NativeLibrary.SetDllImportResolver(typeof(Phonon).Assembly,
                (name, _, _) => name == "phonon" ? System.Runtime.InteropServices.NativeLibrary.Load(file) : IntPtr.Zero);
            var cs = Phonon.DefaultContextSettings();
            if (Phonon.iplContextCreate(ref cs, out IntPtr ctx) != Phonon.IPL_STATUS_SUCCESS) return IntPtr.Zero;
            if (!SteamAudioScene.UseEmbree(ctx)) { Phonon.iplContextRelease(ref ctx); return IntPtr.Zero; }
            return ctx;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or InvalidOperationException)
        { return IntPtr.Zero; }
    });

    /// <summary>The library: in the directory given, or in the nearest lib/ above the tests.</summary>
    private static string? Find(string asked)
    {
        string name = OpenFPS.Client.Core.Platform.NativeAudioLibraries.PhononFileName;
        if (asked != "1") return File.Exists(Path.Combine(asked, name)) ? Path.Combine(asked, name) : null;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string at = Path.Combine(dir.FullName, "lib", name);
            if (File.Exists(at)) return at;
        }
        return null;
    }

    /// <summary>The context (its scenes are Embree scenes), or zero.</summary>
    public static IntPtr EmbreeContext => Context.Value;
}

/// <summary>A test that needs Steam Audio with Embree: skipped unless asked for (see SteamAudioForTests).</summary>
public sealed class SteamAudioEmbreeFactAttribute : FactAttribute
{
    public SteamAudioEmbreeFactAttribute()
    {
        if (SteamAudioForTests.EmbreeContext == IntPtr.Zero)
            Skip = "Needs Steam Audio with Embree: run on its own with OPENFPS_STEAMAUDIO_TESTS=1 (see SteamAudioForTests).";
    }
}

/// <summary>
/// The tile scenes (TileSceneSet: a sub-scene per tile, instanced into two pairs of top scenes used in
/// turn) must trace as the whole scene built from the same boxes does, before and after every change.
/// On 2026-10-06 the first door that swung handed over a pair that traced as empty: every voice lost its
/// occlusion and the traced reverb its walls. Two things in Steam Audio's Embree scenes did it (see
/// TileSceneSet's remarks): an instance made for a pair and not added kept that pair's next commit from
/// building anything, and an instance released gave its geometry id back without leaving the scene, so
/// the next tile given that id was never in it.
/// </summary>
public class TileSceneSetTests
{
    private const float Tile = 20f;
    private static readonly Quaternion Q = Quaternion.Identity;

    /// <summary>
    /// A small map over four tiles: ground wider than a tile (a piece of its own), a concrete wall along
    /// x = 15 with a doorway at z 19.5-20.5 and a wooden leaf in it (shut, or swung a quarter turn about
    /// its hinge), a brick wall in the next tile east, and a wall in a far tile that comes and goes as a
    /// streamed tile does.
    /// </summary>
    private static List<Box> World(bool doorOpen, bool farTile)
    {
        var boxes = new List<Box>
        {
            new(new Vector3(30, -0.1f, 30), new Vector3(80, 0.2f, 80), Q, "Asphalt"),
            new(new Vector3(15, 1.5f, 9.75f), new Vector3(0.3f, 3f, 19.5f), Q, "Concrete"),
            new(new Vector3(15, 1.5f, 30.25f), new Vector3(0.3f, 3f, 19.5f), Q, "Concrete"),
            doorOpen
                ? new(new Vector3(15.5f, 1.5f, 20.5f), new Vector3(0.05f, 3f, 1f), Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f), "Wood", EntityId: 900)
                : new(new Vector3(15, 1.5f, 20f), new Vector3(0.05f, 3f, 1f), Q, "Wood", EntityId: 900),
            new(new Vector3(30, 1.5f, 10), new Vector3(0.3f, 3f, 10f), Q, "Brick"),
        };
        if (farTile) boxes.Add(new(new Vector3(50, 1.5f, 50), new Vector3(0.3f, 3f, 8f), Q, "Concrete"));
        return boxes;
    }

    /// <summary>Ears and sources: through the doorway, through the wall, through the brick wall in the
    /// next tile, through the far tile's wall, and a clear line.</summary>
    private static readonly (Vector3 Ear, Vector3 Source, string What)[] Pairs =
    {
        (new(12, 1.5f, 20), new(18, 1.5f, 20), "through the doorway"),
        (new(12, 1.5f, 8), new(18, 1.5f, 8), "through the concrete wall"),
        (new(27, 1.5f, 10), new(33, 1.5f, 10), "through the brick wall in the next tile"),
        (new(47, 1.5f, 50), new(53, 1.5f, 50), "through the far tile's wall"),
        (new(5, 1.5f, 35), new(10, 1.5f, 35), "in the clear"),
    };

    private static SteamAudioSimulator.DirectResult[] Trace(SteamAudioSimulator sim, IntPtr[] sources)
    {
        var r = new SteamAudioSimulator.DirectResult[Pairs.Length];
        for (int i = 0; i < Pairs.Length; i++)
        {
            sim.SetListener(Pairs[i].Ear);
            sim.SetSourceInputs(sources[i], Pairs[i].Source, 0.1f);
            sim.Run();
            r[i] = sim.GetResult(sources[i]);
        }
        return r;
    }

    private static float Db(float g) => 20f * MathF.Log10(MathF.Max(1e-5f, g));

    /// <summary>
    /// Ten changes in a row, each handed over as the worker hands one over (the door swinging every time,
    /// the far tile coming and going every third), each pair of top scenes traced against the whole scene
    /// built from the same boxes, the full scene and the listener's both. <paramref name="recycleShare"/>
    /// 0 makes a pair afresh whenever it holds a replaced tile, which every pair then does.
    /// </summary>
    private static void SwingAndStream(double recycleShare)
    {
        IntPtr ctx = SteamAudioForTests.EmbreeContext;
        AcousticRegistry.Initialize();
        double share = TileSceneSet.RecycleShare;
        TileSceneSet.RecycleShare = recycleShare;
        var set = new TileSceneSet(ctx, Tile);
        var tiles = new SteamAudioSimulator(ctx, maxSources: 8);
        var tilesListener = new SteamAudioSimulator(ctx, maxSources: 8);
        var whole = new SteamAudioSimulator(ctx, maxSources: 8);
        var wholeListener = new SteamAudioSimulator(ctx, maxSources: 8);
        var retired = new List<SteamAudioScene>();
        try
        {
            Assert.True(set.IsValid);
            IntPtr[]? ts = null, tls = null, ws = null, wls = null;
            for (int step = 0; step < 10; step++)
            {
                bool open = step % 2 == 1, far = step / 3 % 2 == 0;
                var boxes = World(open, far);
                set.Update(boxes);
                var (full, listener) = set.Assemble();
                Assert.Equal(step % 2, set.ActivePair);
                var wFull = new SteamAudioScene(ctx); wFull.Build(boxes);
                var wListener = new SteamAudioScene(ctx); wListener.Build(SteamAudioScene.WithoutOpenGround(boxes));
                retired.Add(wFull); retired.Add(wListener);
                tiles.SetScene(full); tilesListener.SetScene(listener);
                whole.SetScene(wFull); wholeListener.SetScene(wListener);
                ts ??= Pairs.Select(_ => tiles.AcquireSource()).ToArray();
                tls ??= Pairs.Select(_ => tilesListener.AcquireSource()).ToArray();
                ws ??= Pairs.Select(_ => whole.AcquireSource()).ToArray();
                wls ??= Pairs.Select(_ => wholeListener.AcquireSource()).ToArray();

                foreach (var (got, want, scene) in new[] { (Trace(tiles, ts), Trace(whole, ws), "full"), (Trace(tilesListener, tls), Trace(wholeListener, wls), "listener's") })
                    for (int i = 0; i < Pairs.Length; i++)
                    {
                        string at = $"step {step} (door {(open ? "open" : "shut")}, far tile {(far ? "in" : "out")}, pair {set.ActivePair}, "
                                  + $"{set.Recycles} made afresh), {scene} scene, {Pairs[i].What}";
                        Assert.True(MathF.Abs(got[i].Visibility - want[i].Visibility) < 0.05f,
                            $"{at}: occlusion {got[i].Visibility:F2} from the tiles, {want[i].Visibility:F2} from the whole scene");
                        Assert.True(MathF.Abs(Db(got[i].TransMid) - Db(want[i].TransMid)) < 1f,
                            $"{at}: transmission {Db(got[i].TransMid):F1} dB from the tiles, {Db(want[i].TransMid):F1} dB from the whole scene");
                    }
                // And the answers are the walls', not an empty scene's that happens to agree with another.
                var now = Trace(tiles, ts);
                Assert.True(now[1].Visibility < 0.2f, $"step {step}: the concrete wall stopped occluding ({now[1].Visibility:F2})");
                Assert.True(now[2].Visibility < 0.2f, $"step {step}: the brick wall in the next tile stopped occluding ({now[2].Visibility:F2})");
                Assert.True(now[4].Visibility > 0.9f, $"step {step}: the clear line is occluded ({now[4].Visibility:F2})");
                Assert.Equal(far, now[3].Visibility < 0.2f);
                Assert.Equal(open, Db(now[0].TransMid) > -1f && now[0].Visibility > 0.9f);
            }
            if (recycleShare == 0) Assert.True(set.Recycles >= 4, $"only {set.Recycles} pairs made afresh");
        }
        finally
        {
            TileSceneSet.RecycleShare = share;
            // Nothing traces the scenes once the simulators have gone.
            tiles.Dispose(); tilesListener.Dispose(); whole.Dispose(); wholeListener.Dispose();
            set.Dispose();
            foreach (var s in retired) s.Dispose();
        }
    }

    [SteamAudioEmbreeFact]
    public void Tile_scenes_trace_as_the_whole_scene_through_swings_and_tile_changes() => SwingAndStream(TileSceneSet.RecycleShare);

    [SteamAudioEmbreeFact]
    public void Tile_scenes_trace_as_the_whole_scene_when_every_pair_is_made_afresh() => SwingAndStream(0);
}

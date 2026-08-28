using System.Numerics;
using System.Runtime.InteropServices;
using OpenFPS.Client.AudioEngine.Acoustics;
using OpenFPS.Client.Core;
using OpenFPS.Client.Core.AudioEngine.SteamAudio;
using OpenFPS.Client.Core.Platform;
using OpenFPS.Common;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;

namespace OpenFPS.Tests;

/// <summary>
/// Cover for step 3 of the engineering audit — "make every degradation loud".
///
/// The defects these pin down all shared one shape: a component that could not do its job answered with
/// something that looked like success. Steam Audio failing a tick reported *zero occlusion for every
/// source* — every wall in the level vanishing, silently. A CPU without AVX2 was handed an AVX2 context.
/// A missing phonon library left the game running with the spatial audio it exists for quietly switched
/// off. None of those threw; none of them logged.
/// </summary>
public class DegradationTests
{
    // ── The Steam Audio fallback ────────────────────────────────────────────────────────────────
    // These run with no phonon library next to the test assembly, so the worker takes exactly the path a
    // client takes when Steam Audio is unavailable: the hand-rolled ray-tracer. The contract under test is
    // that this path produces a REAL acoustic answer, not a clear one.

    // Two rooms in maps/default.json, joined by an authored portal — the wall between them is what an
    // occlusion result has to notice. (Same landmarks PortalPipelineTests uses.)
    private static readonly Vector3 RoomACentre = new(-7, 2, 15);
    private static readonly Vector3 RoomBCentre = new(7, 2, 15);

    private static readonly Lazy<ClientWorldState> LoadedWorld = new(BuildWorldFromShippedMap, isThreadSafe: true);

    private static ClientWorldState BuildWorldFromShippedMap()
    {
        var prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
        var maps = new MapRepository(Path.Combine(AppContext.BaseDirectory, "maps"));
        var manager = new MapManager(maps, prefabs);
        manager.Initialize();

        Assert.True(manager.TryGetMap("default", out var world, out Vector3 size, out _, out _),
            "maps/default.json failed to load — check it was copied to the test output.");
        var data = manager.GetAllMaps().First(kv => kv.Key == "default").Value.data;

        AcousticRegistry.Initialize();

        var state = new ClientWorldState();
        state.Clear(size, data.MinBound, data.MinBound + size);
        foreach (var def in EntityDefinitionFactory.StaticDefinitions(world))
            state.RegisterDefinition(def);
        return state;
    }

    /// <summary>Drives the worker for one source and returns the acoustic paths it publishes.</summary>
    private static List<OpenFPS.Client.AudioEngine.Data.AcousticPathData> ResolvePaths(
        Vector3 listener, Vector3 source, int entityId)
    {
        var snapshot = LoadedWorld.Value.GetSnapshot();
        using var worker = new AsyncAcousticWorker(new SpatialAcoustics());
        worker.UpdateWorld(snapshot);
        worker.Start();

        for (int attempt = 0; attempt < 200; attempt++)
        {
            worker.EnqueueRequest(new AcousticRequest
            {
                EntityId = entityId, ListenerPos = listener, SourcePos = source, IsImportant = true,
            });
            if (worker.TryGetResult(entityId, out var paths)) return paths;
            Thread.Sleep(10);
        }

        Assert.Fail("The acoustic worker published no result at all — a source that gets no answer is " +
                    "exactly the silent failure this step exists to remove.");
        return null!;
    }

    [Fact]
    public void OccludedSource_GetsATracedPath_NotAClearOne()
    {
        // A source in the next room, behind a wall. Reporting Occlusion 0 here is the precise bug: it tells
        // the player, by ear, that nothing is between them and the sound.
        var paths = ResolvePaths(RoomACentre, RoomBCentre, entityId: 4242);

        Assert.NotEmpty(paths);
        var direct = paths.First(p => !p.IsReflection);
        Assert.True(direct.Occlusion > 0f,
            $"A source behind a wall came back with Occlusion {direct.Occlusion:F3}. " +
            "Falling back to 'clear' is worse than any wrong-but-real number.");
    }

    [Fact]
    public void UnoccludedSource_IsNotReportedAsBlocked()
    {
        // The complement: the fallback must not be a blanket "everything is occluded" either. A source a
        // couple of metres away in open space, with clear line of sight.
        var paths = ResolvePaths(RoomACentre, RoomACentre + new Vector3(1.5f, 0, 0), entityId: 4243);

        var direct = paths.First(p => !p.IsReflection);
        Assert.True(direct.Occlusion < AcousticConstants.OcclusionCap,
            $"A source in clear line of sight came back with Occlusion {direct.Occlusion:F3}.");
    }

    [Fact]
    public void EveryRequestedSource_GetsAResult()
    {
        // Before the fix, a source the simulator had no answer for (exhausted source pool, failed tick)
        // was handed DirectResult.Clear. Now every requested source is answered by *something real*.
        var snapshot = LoadedWorld.Value.GetSnapshot();
        using var worker = new AsyncAcousticWorker(new SpatialAcoustics());
        worker.UpdateWorld(snapshot);
        worker.Start();

        const int sources = 12;
        for (int attempt = 0; attempt < 200; attempt++)
        {
            for (int i = 0; i < sources; i++)
                worker.EnqueueRequest(new AcousticRequest
                {
                    EntityId = 5000 + i,
                    ListenerPos = RoomACentre,
                    SourcePos = RoomBCentre + new Vector3(i * 0.25f, 0, 0),
                    IsImportant = false,
                });

            if (Enumerable.Range(0, sources).All(i => worker.TryGetResult(5000 + i, out _)))
            {
                // "Degraded" means Steam Audio is running but not covering every source. With no phonon
                // library present it never started, so there is nothing to report as degraded — a health
                // flag that cried wolf on every machine without the library would be useless.
                Assert.False(worker.IsDegraded);
                Assert.False(worker.SteamAudioActive);
                return;
            }
            Thread.Sleep(10);
        }

        var missing = Enumerable.Range(0, sources).Where(i => !worker.TryGetResult(5000 + i, out _)).ToList();
        Assert.Fail($"{missing.Count} of {sources} sources were never answered: {string.Join(", ", missing)}.");
    }

    // ── SIMD level ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void DetectedSimdLevel_NeverExceedsWhatThisCpuSupports()
    {
        int level = Phonon.DetectSimdLevel();

        Assert.InRange(level, Phonon.IPL_SIMDLEVEL_SSE2, Phonon.IPL_SIMDLEVEL_AVX512);

        if (level >= Phonon.IPL_SIMDLEVEL_AVX512)
            Assert.True(System.Runtime.Intrinsics.X86.Avx512F.IsSupported, "asked for AVX-512 without AVX-512");
        if (level >= Phonon.IPL_SIMDLEVEL_AVX2)
            Assert.True(System.Runtime.Intrinsics.X86.Avx2.IsSupported, "asked for AVX2 without AVX2");
        if (level >= Phonon.IPL_SIMDLEVEL_AVX)
            Assert.True(System.Runtime.Intrinsics.X86.Avx.IsSupported, "asked for AVX without AVX");
        if (level >= Phonon.IPL_SIMDLEVEL_SSE4)
            Assert.True(System.Runtime.Intrinsics.X86.Sse42.IsSupported, "asked for SSE4.2 without SSE4.2");
    }

    [Fact]
    public void DefaultContextSettings_CarryTheDetectedLevelAndTheBoundVersion()
    {
        var cs = Phonon.DefaultContextSettings();

        Assert.Equal(Phonon.DetectSimdLevel(), cs.simdLevel);
        Assert.Equal(Phonon.STEAMAUDIO_VERSION, cs.version);
        Assert.False(string.IsNullOrWhiteSpace(Phonon.SimdLevelName(cs.simdLevel)));
    }

    // ── Native library reporting ────────────────────────────────────────────────────────────────

    [Fact]
    public void SteamAudioLibrary_IsRequired_NotOptional()
    {
        var phonon = NativeAudioLibraries.Expected.SingleOrDefault(l => l.FileName == NativeAudioLibraries.PhononFileName);

        Assert.False(string.IsNullOrEmpty(phonon.FileName),
            "phonon is missing from the required-native check; a client with no HRTF would start and say nothing.");
        Assert.True(phonon.Required);
        Assert.Contains("HRTF", phonon.Degradation);
    }

    [Fact]
    public void ExpectedLibraries_UseThisPlatformsFileNames()
    {
        bool windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        string expectedSuffix = windows ? ".dll" : ".so";

        Assert.All(NativeAudioLibraries.Expected, lib => Assert.EndsWith(expectedSuffix, lib.FileName));
        Assert.Equal(windows ? "phonon.dll" : "libphonon.so", NativeAudioLibraries.PhononFileName);
        Assert.Equal(windows ? "fmod.dll" : "libfmod.so", NativeAudioLibraries.FmodFileName);
    }

    [Fact]
    public void DescribeMissing_NamesEachLibraryAndWhatItCosts()
    {
        var missing = NativeAudioLibraries.Expected.ToList();
        string report = NativeAudioLibraries.DescribeMissing(missing);

        Assert.All(missing, lib =>
        {
            Assert.Contains(lib.FileName, report);
            Assert.Contains(lib.Degradation, report);
        });
        Assert.Contains(NativeAudioLibraries.BaseDirectory, report);
    }

    [Fact]
    public void DescribeMissing_IsSilentWhenNothingIsMissing()
    {
        Assert.Equal(string.Empty, NativeAudioLibraries.DescribeMissing(Array.Empty<NativeAudioLibraries.NativeLib>()));
    }

    // ── Connection / protocol failures ──────────────────────────────────────────────────────────

    [Fact]
    public void FailedConnect_IsReportedAsSpeakableText()
    {
        // Port 1 on the loopback has nothing listening, so LiteNetLib exhausts its connect attempts and
        // reports ConnectionFailed. Before this step that arrived as an empty handler body: the client
        // simply sat on the menu forever with nothing said.
        var net = new ClientNetworkService();
        string? reported = null;
        net.OnConnectionFailed += r => reported ??= r;

        net.Start();
        net.Connect("127.0.0.1", 1);

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (reported == null && DateTime.UtcNow < deadline)
        {
            net.Poll();
            Thread.Sleep(20);
        }

        Assert.NotNull(reported);
        Assert.Contains("127.0.0.1", reported);
        Assert.EndsWith(".", reported!.Trim());   // a finished sentence, ready to be spoken
    }

    [Fact]
    public void SendWithoutAConnection_DoesNotThrow()
    {
        // It logs the drop rather than pretending the message went out — and, crucially, never takes the
        // client down mid-session.
        var net = new ClientNetworkService();
        net.Start();
        net.Send(new TextCommand { Command = "ready" });
    }
}

using System.Numerics;
using OpenFPS.Audio.Core;
using OpenFPS.Audio.Fmod;
using OpenFPS.Audio.SteamAudio;
using Xunit.Abstractions;
using OpenFPS.Native.SteamAudio;
using Box = OpenFPS.Audio.SteamAudio.SteamAudioScene.Box;

namespace OpenFPS.Tests;

/// <summary>
/// The traced reverb is made off the threads that place sounds (PlacementThread), and never under the lock
/// the audio thread reads every tick. Getting into a car and walking into a room held every source still
/// for 130 to 640 ms (AudioLab --driving and --room-change, 2026-10-12): a room's stage calibrated its ears
/// on a second of noise where its bus came in, and the cabin's trace and a room's were made under the
/// set's lock. The Steam Audio cases need the library (SteamAudioEmbreeFact); the rule for what plays in
/// the meantime does not.
/// </summary>
public class TracedPlacementTests
{
    private readonly ITestOutputHelper _o;
    public TracedPlacementTests(ITestOutputHelper o) => _o = o;

    private const int Rate = 48000, Sub = TracedReverb.TracedFrame;
    private static readonly Quaternion Q = Quaternion.Identity;
    /// <summary>The 1.6 hatchback, and the driver's ear in its frame.</summary>
    private const string Car = "i4_economy";
    private static readonly Vector3 Seat = new(-0.36f, 1.25f, 0.28f);

    /// <summary>A plastered room six metres by eight, three high.</summary>
    private static SteamAudioScene Room(IntPtr ctx)
    {
        var scene = new SteamAudioScene(ctx);
        scene.Build(new List<Box>
        {
            new(new Vector3(0, -0.1f, 0), new Vector3(6, 0.2f, 8), Q, "Concrete"),
            new(new Vector3(0, 3.1f, 0), new Vector3(6, 0.2f, 8), Q, "Plaster"),
            new(new Vector3(-3.1f, 1.5f, 0), new Vector3(0.2f, 3, 8), Q, "Plaster"),
            new(new Vector3(3.1f, 1.5f, 0), new Vector3(0.2f, 3, 8), Q, "Plaster"),
            new(new Vector3(0, 1.5f, -4.1f), new Vector3(6, 3, 0.2f), Q, "Plaster"),
            new(new Vector3(0, 1.5f, 4.1f), new Vector3(6, 3, 0.2f), Q, "Plaster"),
        });
        return scene;
    }

    [SteamAudioEmbreeFact]
    public void ACabinAndARoomAreTracedOffThePlacementThread()
    {
        IntPtr ctx = SteamAudioForTests.EmbreeContext;
        using var scene = Room(ctx);
        var set = new TracedReverbSet();
        set.Configure(ctx, scene);
        try
        {
            var (before, _) = PlacementThread.HeavyOnThisThread;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            using (PlacementThread.Enter())
            {
                set.RideIn(Car, Seat);
                set.ForRoom(5, new Vector3(0f, 1.6f, 0f));
            }
            _o.WriteLine($"asked for a cabin and a room in {clock.Elapsed.TotalMilliseconds:F1} ms");
            var (after, last) = PlacementThread.HeavyOnThisThread;
            Assert.True(after == before, $"{after - before} trace(s) made where sounds are placed, the last: {last}");

            // Made elsewhere, and there once made: the cabin you sit in, the room asked for.
            set.WaitForMaking();
            Assert.NotNull(set.Cabin);
            Assert.NotNull(set.ForRoom(5, new Vector3(0f, 1.6f, 0f)));
            Assert.Equal(0, set.MadeUnderLock);
        }
        finally { set.Release(); }
    }

    /// <summary>
    /// While a cabin is being made, everything the audio thread asks of the set answers at once. Made under
    /// the set's lock, the audio thread's every tick (Listener, Cabin, LateField, Echoes) waited for it.
    /// </summary>
    [SteamAudioEmbreeFact]
    public void TheSetsLockIsFreeWhileATraceIsMade()
    {
        IntPtr ctx = SteamAudioForTests.EmbreeContext;
        using var scene = Room(ctx);
        var set = new TracedReverbSet();
        set.Configure(ctx, scene);
        using var hold = new ManualResetEventSlim(false);
        using var started = new ManualResetEventSlim(false);
        // Each trace being made is held up here until the asking below is done.
        set.MakingForTests = _ => { started.Set(); hold.Wait(5000); };
        try
        {
            var ride = Task.Run(() => set.RideIn(Car, Seat));
            Assert.True(started.Wait(5000), "the cabin was never started");
            var asked = Task.Run(() =>
            {
                _ = set.Listener; _ = set.Cabin; _ = set.LateField; _ = set.Echoes;
                set.SetListener(new Vector3(0f, 1.6f, 1f));
                set.RideIn(Car, Seat);
                _ = set.ForRoom(6, new Vector3(1f, 1.6f, 0f));
                _ = set.Stats();
            });
            bool answered = asked.Wait(2000);
            hold.Set();
            Assert.True(answered, "the audio thread's questions waited for a trace being made");
            Assert.True(ride.Wait(5000));
            set.WaitForMaking();
            Assert.Equal(0, set.MadeUnderLock);
            Assert.NotNull(set.Cabin);
        }
        finally { hold.Set(); set.Release(); }
    }

    /// <summary>
    /// A reverb bus's stage is taken made (TracedStages), never made where sounds are placed, and a stage
    /// made on its own thread calibrates and renders exactly as one made in place: the same diffuse-field
    /// gain to the bit, the same samples. Its renders are taken while the next stage is being made, as the
    /// mixer plays a stage while the builder calibrates another: through one HRTF at once from two
    /// threads, Steam Audio gave both garbage (the calibration read +17 to +30 dB, alone -0.64).
    /// </summary>
    [SteamAudioEmbreeFact]
    public void AStageIsMadeOffThePlacementThreadAndIsTheStageMadeInPlace()
    {
        IntPtr ctx = SteamAudioForTests.EmbreeContext;
        var stages = new TracedStages(ctx, 1024);
        int irSize = (int)(TracedReverb.DurationSeconds * Rate);
        var shape = new TracedStages.Shape(ctx, Rate, irSize, irSize / Sub + 1, Sub);
        TracedReverbState? st = null;
        DiffuseTail? inPlace = null;
        try
        {
            var (before, _) = PlacementThread.HeavyOnThisThread;
            using (PlacementThread.Enter()) Assert.Null(stages.Take(shape));     // none made yet: they are started
            stages.WaitForMaking();
            using (PlacementThread.Enter()) st = stages.Take(shape);             // and the next one started
            var (after, last) = PlacementThread.HeavyOnThisThread;
            Assert.True(after == before, $"{after - before} stage(s) made where sounds are placed, the last: {last}");
            Assert.NotNull(st);
            Assert.NotNull(st!.Diffuse);
            Assert.NotEqual(IntPtr.Zero, st.Effect);

            // Played while the builder makes the next: the mixer's part.
            const int Blocks = 400;
            var r = new Random(5);
            var inputs = new float[Blocks][];
            var played = new float[Blocks][];
            for (int block = 0; block < Blocks; block++)
            {
                var ambi = inputs[block] = new float[Sub * TracedReverb.Channels];
                for (int k = 0; k < Sub; k++) ambi[k * TracedReverb.Channels] = (float)(r.NextDouble() * 2 - 1) * 0.2f;
                st.Diffuse!.SetListenerRotation(Quaternion.CreateFromYawPitchRoll(block * 0.07f, 0f, 0f));
                st.Diffuse.RenderBinaural(ambi, Sub, TracedReverb.Channels);
                played[block] = new float[3 * Sub];
                st.Diffuse.Stereo.CopyTo(played[block], 0);
                st.Diffuse.Low.CopyTo(played[block], 2 * Sub);
            }
            stages.WaitForMaking();
            Assert.Equal(TracedStages.Spare, stages.Ready);

            // The same stage made here, alone, through the HRTF the stage plays.
            inPlace = DiffuseTail.Create(ctx, Sub, TracedReverb.Channels, st.Hrtf, Rate)!;
            _o.WriteLine($"diffuse-field gain {st.Diffuse!.DiffuseFieldGainDb:F4} dB made ahead, {inPlace.DiffuseFieldGainDb:F4} dB in place");
            Assert.Equal(inPlace.DiffuseFieldGainDb, st.Diffuse.DiffuseFieldGainDb);
            bool any = false;
            for (int block = 0; block < Blocks; block++)
            {
                inPlace.SetListenerRotation(Quaternion.CreateFromYawPitchRoll(block * 0.07f, 0f, 0f));
                inPlace.RenderBinaural(inputs[block], Sub, TracedReverb.Channels);
                Assert.True(played[block].AsSpan(0, 2 * Sub).SequenceEqual(inPlace.Stereo), $"block {block}: the stage made ahead renders otherwise");
                Assert.True(played[block].AsSpan(2 * Sub).SequenceEqual(inPlace.Low), $"block {block}: its low end differs");
                any |= Array.Exists(inPlace.Stereo, x => x != 0f);
            }
            Assert.True(any);
        }
        finally
        {
            inPlace?.Release();
            if (st != null) TracedStages.Free(st);
            stages.Release();
        }
    }

    /// <summary>
    /// The tripwires themselves: a stage's tail, a cabin path's HRTF bands and a trace made where sounds are
    /// placed are counted. Without them the guards above would pass whatever the provider did.
    /// </summary>
    [SteamAudioEmbreeFact]
    public void HeavyTracedWorkOnThePlacementThreadIsCounted()
    {
        IntPtr ctx = SteamAudioForTests.EmbreeContext;
        var au = new Phonon.IPLAudioSettings { samplingRate = Rate, frameSize = Sub };
        var hs = new Phonon.IPLHRTFSettings { type = Phonon.IPL_HRTFTYPE_DEFAULT, volume = 1f, normType = Phonon.IPL_HRTFNORMTYPE_NONE };
        Assert.Equal(Phonon.IPL_STATUS_SUCCESS, Phonon.iplHRTFCreate(ctx, ref au, ref hs, out IntPtr hrtf));
        try
        {
            var (before, _) = PlacementThread.HeavyOnThisThread;
            DiffuseTail? tail;
            using (PlacementThread.Enter())
            {
                tail = DiffuseTail.Create(ctx, Sub, TracedReverb.Channels, hrtf, Rate);
                _ = HrtfBands.Measure(ctx, hrtf, Rate, 1024, new Vector3(0f, 0f, -1f));
            }
            var (after, last) = PlacementThread.HeavyOnThisThread;
            tail?.Release();
            Assert.Equal(before + 2, after);
            Assert.Equal("An HRTF's bands for a cabin path", last);
        }
        finally { Phonon.iplHRTFRelease(ref hrtf); }
    }

    /// <summary>
    /// What a heard stage plays while the trace it should play has not answered it: the one it played,
    /// not the silence a stage reads from a trace that has not traced its reader yet, for at most a
    /// couple of seconds. An unheard stage moves at once.
    /// </summary>
    [Fact]
    public void AHeardStageKeepsItsTraceUntilTheNewOneAnswers()
    {
        Assert.True(FmodAudioProvider.KeepsTheOldTrace(answered: false, heard: true, heldSeconds: 0.3));
        Assert.False(FmodAudioProvider.KeepsTheOldTrace(answered: true, heard: true, heldSeconds: 0.3));
        Assert.False(FmodAudioProvider.KeepsTheOldTrace(answered: false, heard: false, heldSeconds: 0.3));
        Assert.False(FmodAudioProvider.KeepsTheOldTrace(answered: false, heard: true, heldSeconds: FmodAudioProvider.HandOverLimitSeconds));
    }
}

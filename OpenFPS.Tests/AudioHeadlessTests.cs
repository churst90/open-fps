using System.Numerics;
using OpenFPS.Audio.Core;
using OpenFPS.Audio.Fmod;
using OpenFPS.Client.Core;
using OpenFPS.Client.Services;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Models;
using OpenFPS.Sound;

namespace OpenFPS.Tests;

/// <summary>
/// The headless mode a host runs the library in with no sound card: FMOD's NOSOUND output (FmodAudioProvider.NoSound,
/// or OPENFPS_FMOD_OUTPUT=nosound), the facade stepped by hand (InitializeSteppedByHand, StepByHand) and the mix read
/// back (MixPeakDb). Run with <c>-e OPENFPS_FMOD_TESTS=1 -e LD_LIBRARY_PATH=&lt;repo&gt;/lib</c>.
/// </summary>
public class AudioHeadlessTests
{
    [Fact]
    public void Without_a_mixer_that_can_say_the_peak_is_not_a_number()
    {
        var facade = new AudioEngineFacade(new RecordingMixer());
        Assert.True(float.IsNaN(facade.MixPeakDb));
        facade.InitializeSteppedByHand();
        Assert.True(float.IsNaN(facade.MixPeakDb));
        facade.Dispose();
    }

    [FmodFact]
    public void A_headless_mixer_plays_a_car_into_a_mix_that_is_not_silent_and_disposes_cleanly()
    {
        var before = Shutdown.Alive().ToHashSet();
        var facade = new AudioEngineFacade(new FmodAudioProvider { NoSound = true });
        facade.InitializeSteppedByHand();
        Assert.True(facade.IsInitialized);

        var world = new ClientWorldState();
        world.Clear(new Vector3(4000, 400, 4000));
        var player = new LocalPlayerState();
        world.Clock = facade.Clock;
        world.Hearing = facade.Hearing;
        var sounds = new SoundMappingService(player);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var audio = new ClientAudioSystem(facade, sounds, player, () => clock.Elapsed.TotalSeconds, prewarm: false);

        var def = new EntityDefinition
        {
            EntityId = 1, Type = EntityType.NPC, Moves = true,
            Transform = new Transform { Position = new Vector3(8, 0, 2), Rotation = Quaternion.Identity },
        };
        def.SoundEmitter.IsSynth = true;
        def.SoundEmitter.SoundId = "engine:i4_economy";
        def.SoundEmitter.Mode = PlaybackMode.LoopOne;
        def.SoundEmitter.Volume = 1f;
        def.SoundEmitter.Range = facade.Hearing.AudibleRange(MachineRegistry.VehicleFor("i4_economy").SourceLevelDb);
        def.SoundEmitter.MinDistance = 3f;
        world.RegisterDefinition(def);
        world.SyncState(new[] { new EntityState { EntityId = 1, Transform = QuantizedTransform.FromTransform(def.Transform) } });

        // The mixer thread runs in real time, so the frames are spread over real time.
        float peak = float.NegativeInfinity;
        var until = System.Diagnostics.Stopwatch.StartNew();
        while (until.Elapsed.TotalSeconds < 3.0)
        {
            audio.Update(world.GetSnapshot());
            facade.StepByHand();
            float now = facade.MixPeakDb;
            if (float.IsFinite(now)) peak = Math.Max(peak, now);
            Thread.Sleep(16);
        }
        Assert.True(peak > -80f, $"the headless mix stayed silent: peak {peak} dBFS");

        facade.Dispose();
        facade.Dispose();
        Assert.False(facade.IsInitialized);
        var clockOut = System.Diagnostics.Stopwatch.StartNew();
        while (Shutdown.Alive().Any(t => !before.Contains(t)) && clockOut.ElapsedMilliseconds < 2000) Thread.Sleep(10);
        Assert.Empty(Shutdown.Alive().Where(t => !before.Contains(t)).Select(t => t.Name));
        Assert.Equal(0, audio.World.BackgroundRunning);
    }
}

using System.Numerics;
using OpenFPS.Audio.Data;
using OpenFPS.Audio.Fmod;
using OpenFPS.Environment;
using OpenFPS.Physics;
using OpenFPS.Sound;
using ActiveSound = OpenFPS.Audio.Fmod.FmodAudioProvider.ActiveSound;

namespace OpenFPS.Tests;

/// <summary>
/// A voice that reads another's synth (a machine's front outlet, a cabin path, an engine's echo, a fire's or a wood's
/// outer place) stopped and started again under its id, or the voice it reads restarted under its own (Resonance 3.2,
/// AudioLab --resonance-maybes outlet). The old tap was let go up to 250 ms after the new one took its share, and its
/// hand-back cleared a flag the new one had set: the engine's own voice carried the front again beside the new tap,
/// 4.3 dB. A reader left on a released voice went on reading it: a front outlet off a car whose play position had
/// stopped, a fire's outer places on a synth that burnt on after the fire was out.
/// </summary>
public class TapHandBackTests
{
    private const int Rate = 44100, Block = 1024;

    private static EngineVoiceState Engine(int seed, float speed, bool split = false)
    {
        var v = VehicleProfile.ByName("v8_muscle");
        var e = new EngineVoiceState(v, Rate, seed) { TargetSpeed = speed, SplitVoices = split };
        e.PlaceAtSpeed(speed);
        return e;
    }

    /// <summary>
    /// A front tap started under its id before the old one is gone, and the engine's own voice still leaves the front
    /// to the new tap, sample for sample the voice of an engine whose front is played elsewhere throughout. Faded, the
    /// old one hands back as its fade begins and again when it is released (once is all that counts); stopped
    /// outright (a voice not yet sounding), only when it is released, after the new one took the front (the count).
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_tap_let_go_after_its_successor_started_leaves_the_front_with_the_successor(bool faded)
    {
        const float speed = 20f;
        var engine = Engine(7, speed);
        var reference = Engine(7, speed, split: true);
        var old = new EngineTapState(engine);
        old.Take();

        var a = new float[Block];
        var b = new float[Block];
        double worst = 0, energy = 0;
        int blocks = Rate * 2 / Block;
        for (int k = 0; k < blocks; k++)
        {
            if (k == 10)
            {
                // The swap in one tick: stopped (its fade begins, Leave, or outright) and started again under the id.
                if (faded) old.HandBack();
                new EngineTapState(engine).Take();
            }
            // Released a few blocks on, 230 ms after its stop: its second hand-back.
            if (k == 20) old.HandBack();
            engine.Produce();
            reference.Produce();
            engine.Consume(a);
            reference.Consume(b);
            if (k < Rate / 10 / Block) continue;
            for (int i = 0; i < Block; i++)
            {
                worst = Math.Max(worst, Math.Abs(a[i] - b[i]));
                energy += b[i] * (double)b[i];
            }
        }

        Assert.Equal(1, engine.FrontTaps);
        Assert.True(engine.SplitVoices);
        Assert.True(energy > 1e-3, $"nothing rendered to compare (energy {energy:G3})");
        Assert.True(worst < 1e-4, $"the engine's own voice took the front back beside the new tap: worst sample difference {worst:G4}");
    }

    /// <summary>The same for a path into the cabin: the old path's tap handed back at its stop and its release, or
    /// only at its release; the new one keeps the path.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_cabin_path_let_go_after_its_successor_started_stays_with_the_successor(bool faded)
    {
        var v = OpenFPS.Models.MachineRegistry.VehicleFor("i4_economy");
        var engine = new EngineVoiceState(v, Rate, 7) { TargetSpeed = 15f, CompensateLevel = false, Interior = true };
        Assert.NotNull(engine.CabinLayout);
        var old = new EngineTapState(engine, 1);
        old.Take();
        Assert.True(engine.CabinTapped(1));
        if (faded) old.HandBack();
        var successor = new EngineTapState(engine, 1);
        successor.Take();
        old.HandBack();
        Assert.True(engine.CabinTapped(1), "the old tap's second hand-back gave the path back to the engine's own voice");
        successor.HandBack();
        Assert.False(engine.CabinTapped(1));
        // A second hand-back of a tap holding nothing gives back nothing.
        successor.HandBack();
        new EngineTapState(engine, 1).Take();
        Assert.True(engine.CabinTapped(1));
    }

    /// <summary>
    /// A tap faded out by the registry and wanted back before its fade ended (VoiceRegistry.Unretire, the provider's
    /// ReviveEngine) takes its share again, once however often it is revived. Left handed back, it played nothing for
    /// the rest of its life while the engine's voice carried its part from the tailpipe.
    /// </summary>
    [Fact]
    public void A_tap_wanted_back_mid_fade_takes_its_share_again()
    {
        var engine = Engine(7, 10f);
        var mixer = new FmodAudioProvider();
        var tap = new EngineTapState(engine);
        tap.Take();
        mixer.AddActive(new ActiveSound { EntityId = 5, TapState = tap });

        mixer.FadeOutEngine(5);
        mixer.FadeOutEngine(5);
        Assert.Equal(0f, tap.TargetGain);
        Assert.Equal(0, engine.FrontTaps);

        mixer.ReviveEngine(5);
        mixer.ReviveEngine(5);
        Assert.Equal(1f, tap.TargetGain);
        Assert.Equal(1, engine.FrontTaps);
        Assert.True(engine.SplitVoices);
    }

    /// <summary>
    /// The engine restarted under its id in one tick: the front tap moves to the live voice with its share, fading out
    /// of the old one (no step) and, while the new one is not yet primed, playing silence; then the new car.
    /// Left on the old one it read a ring whose play position had stopped, over and over.
    /// </summary>
    [Fact]
    public void A_tap_follows_its_engine_restarted_under_its_id()
    {
        const float speed = 20f;
        var released = Engine(7, speed);
        var live = Engine(11, speed);
        var tap = new EngineTapState(released);
        tap.Take();
        var mixer = new FmodAudioProvider();
        var engineVoice = new ActiveSound { EntityId = 900, EngineState = live };
        var tapVoice = new ActiveSound { EntityId = 902, TapState = tap };
        mixer.AddActive(engineVoice);
        mixer.AddActive(tapVoice);

        var block = new float[Block];
        var rear = new float[Block];
        double before = 0;
        for (int k = 0; k < 20; k++)
        {
            released.Produce();
            tap.Render(block);
            released.Consume(rear);
            if (k >= 15) before += Energy(block);
        }
        before /= 5;
        Assert.True(before > 1e-6, $"the front read nothing before the restart ({before:G3})");

        mixer.FollowLiveSource(tapVoice, new SpatialEmitter { EntityId = 902, IsSynth = true, IntakeOfEntity = 900 });
        Assert.Same(live, tap.Source);
        Assert.Equal(0, released.FrontTaps);
        Assert.Equal(1, live.FrontTaps);

        // The old voice is released: nothing produces or plays it any more.
        tap.Render(block);
        Assert.True(Energy(block) > 0.1 * before, $"cut, not faded: the first block after the rebind is {Energy(block) / before:P0} of before");
        for (int k = 0; k < Rate / 10 / Block; k++) tap.Render(block);
        tap.Render(block);
        Assert.True(Energy(block) == 0, "still reading the released engine with the live one not yet started");

        double after = 0;
        for (int k = 0; k < 30; k++)
        {
            live.Produce();
            tap.Render(block);
            live.Consume(rear);
            if (k >= 25) after += Energy(block);
        }
        Assert.True(after / 5 > 0.1 * before, $"the live engine's front is not heard: {after / 5:G3} against {before:G3}");
    }

    /// <summary>An engine's echo or borrowed voice follows the live voice the same way: out of the released one,
    /// which here goes on reading silence from the live one not yet started.</summary>
    [Fact]
    public void An_echo_follows_its_engine_restarted_under_its_id()
    {
        const float speed = 20f;
        var released = Engine(7, speed);
        var live = Engine(11, speed);
        var echo = new EngineEchoState(released) { TargetDelaySeconds = 0.05f, TargetGain = 1f, OwnCursor = true };
        var mixer = new FmodAudioProvider();
        var echoVoice = new ActiveSound { EntityId = 903, EchoState = echo };
        mixer.AddActive(new ActiveSound { EntityId = 900, EngineState = live });
        mixer.AddActive(echoVoice);

        var block = new float[Block];
        var rear = new float[Block];
        for (int k = 0; k < 20; k++)
        {
            released.Produce();
            echo.Render(block);
            released.Consume(rear);
        }
        Assert.True(Energy(block) > 1e-6);

        mixer.FollowLiveSource(echoVoice, new SpatialEmitter { EntityId = 903, IsSynth = true, EchoOfEntity = 900 });
        Assert.Same(live, echo.Source);
        for (int k = 0; k < Rate / 2 / Block; k++) echo.Render(block);
        Assert.True(Energy(block) == 0, "still reading the released engine");
    }

    /// <summary>
    /// A fire's middle restarted under its id makes a new synth; its outer places move to it (crossfaded) and play
    /// exactly what a place made on it now plays. Left on the old synth, they burnt on after the fire was put out:
    /// only the middle carries Running.
    /// </summary>
    [Fact]
    public void A_fires_outer_place_follows_its_middle_restarted_under_its_id()
    {
        var weather = new WorldWeather { Wind = WindWeather.Steady(3f, 270f, 0f) };
        var at = new Vector3(10f, 0f, 10f);
        var spec = OpenFPS.Models.ModelLibrary.Presets.Fire("fire_pit");
        const int Places = 4;
        PlacedNatureVoice Synth(int seed) => new("fire:fire_pit", spec, Places, Rate, seed, at, weather) { TargetSpread = 1f };
        var first = Synth(41);
        var outer = new NaturePlaceState(first, 1, Rate, at);
        var block = new float[Block];
        for (int k = 0; k < 10; k++) outer.Render(block);

        var second = Synth(83);
        var mixer = new FmodAudioProvider();
        var middleVoice = new ActiveSound { EntityId = 950, MachineState = new NaturePlaceState(second, 0, Rate, at) };
        var outerVoice = new ActiveSound { EntityId = 951, MachineState = outer };
        mixer.AddActive(middleVoice);
        mixer.AddActive(outerVoice);
        mixer.FollowLiveSource(outerVoice, new SpatialEmitter { EntityId = 951, IsSynth = true, PlaceOfEntity = 950, Place = 1 });
        Assert.Same(second, outer.Shared);
        Assert.Equal(0, first.Voices);
        Assert.Equal(2, second.Voices);

        // A place made on the new synth at the same moment: once the crossfade is over, the two are one stream.
        var fresh = new NaturePlaceState(second, 1, Rate, at);
        var made = new float[Block];
        double worst = 0, energy = 0;
        for (int k = 0; k < 20; k++)
        {
            outer.Render(block);
            fresh.Render(made);
            if (k < 4) continue;
            for (int i = 0; i < Block; i++)
            {
                worst = Math.Max(worst, Math.Abs(block[i] - made[i]));
                energy += made[i] * (double)made[i];
            }
        }
        Assert.True(energy > 1e-8, $"the new synth's place rendered nothing ({energy:G3})");
        Assert.True(worst < 1e-6, $"the outer place does not play the live middle's synth: worst sample difference {worst:G4}");
    }

    private static double Energy(float[] block)
    {
        double e = 0;
        foreach (float x in block) e += x * (double)x;
        return e / block.Length;
    }
}

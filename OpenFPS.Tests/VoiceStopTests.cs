using System.Numerics;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Client.Core;
using OpenFPS.Common.Components;
using ActiveSound = OpenFPS.Client.AudioEngine.Fmod.FmodAudioProvider.ActiveSound;
using StopWay = OpenFPS.Client.AudioEngine.Fmod.FmodAudioProvider.StopWay;

namespace OpenFPS.Tests;

/// <summary>
/// No voice stops with a click (docs/CLIENT_NOTES.md, "A voice is never cut off"). Every way a voice is let go reaches
/// the mixer's StopSound while the voice sounds, and StopSound draws the voice's fade from where it is to silence as one
/// line (fade points) and has the mixer stop it two blocks after. Stopped outright, a 440 Hz tone ended in a step the
/// size of the tone itself (AudioLab --stop-clicks: 0 to 1 dB under its level); faded so, 72 to 76 dB under it.
/// </summary>
public class VoiceStopTests
{
    private const int Rate = 48000;
    private const ulong Block = 1024;

    /// <summary>A voice at full, part way down the budget's fade, or inside a one-shot's onset ramp, at clock 2000.</summary>
    private static ActiveSound Voice(string where) => where switch
    {
        "full" => new ActiveSound(),
        "budget" => new ActiveSound { FadeFromClock = 0, FadeFromGain = 1f, FadeToClock = 3840, FadeToGain = 0f, FadeTarget = 0f },
        "onset" => new ActiveSound { FadeFromClock = 1900, FadeFromGain = 0f, FadeToClock = 2188, FadeToGain = 1f },
        _ => throw new ArgumentException(where),
    };

    /// <summary>
    /// The line a voice leaves by, from wherever its fade is: it starts where the fade was (no jump), falls no faster
    /// than the budget's 80 ms from full, reaches silence, and the mixer stops the voice two blocks after that, so
    /// nothing fed ahead of its fader once an update is still sounding.
    /// </summary>
    [Theory]
    [InlineData("full")]
    [InlineData("budget")]
    [InlineData("onset")]
    public void A_voice_stopped_is_silent_two_blocks_before_its_channel_stops(string where)
    {
        var a = Voice(where);
        const ulong now = 2000;
        float before = FmodAudioProvider.FadeAt(a, now);
        var (from, end) = FmodAudioProvider.Line(a, now, 0f, Rate);
        Assert.Equal(before, from, 5);
        // As FadeTo draws it.
        (a.FadeFromClock, a.FadeFromGain, a.FadeToClock, a.FadeToGain) = (now, from, end, 0f);
        ulong stop = FmodAudioProvider.StopClock(end, Rate);

        float steepest = 1f / (Rate * FmodAudioProvider.VoiceFadeSeconds);
        float last = from;
        for (ulong t = now; t <= stop; t += 16)
        {
            float g = FmodAudioProvider.FadeAt(a, t);
            Assert.True(g <= last + 1e-6f, $"rose at {t}");
            Assert.True(last - g <= 16 * steepest + 1e-5f, $"fell {last - g} in 16 samples at {t}");
            last = g;
        }
        Assert.Equal(0f, FmodAudioProvider.FadeAt(a, stop));
        Assert.True(stop - end >= 2 * Block, $"stopped {stop - end} samples after silence");
        Assert.True(end - now + 1 >= (ulong)(from * Rate * FmodAudioProvider.VoiceFadeSeconds), $"fell from {from} in {end - now} samples");
    }

    /// <summary>A voice sounding is faded out; one making no sound yet (held back for its flight time, or ended) is
    /// stopped at once; with the world at its floor it is stopped under it; the lab's lever stops every one outright.</summary>
    [Fact]
    public void A_voice_sounding_is_faded_out_unless_the_world_is_at_its_floor()
    {
        Assert.Equal(StopWay.Fade, FmodAudioProvider.HowItStops(sounding: true, worldFade: 1f, hardStops: false));
        Assert.Equal(StopWay.Fade, FmodAudioProvider.HowItStops(sounding: true, worldFade: 0.01f, hardStops: false));
        Assert.Equal(StopWay.Now, FmodAudioProvider.HowItStops(sounding: false, worldFade: 1f, hardStops: false));
        Assert.Equal(StopWay.UnderFloor, FmodAudioProvider.HowItStops(sounding: true, FmodAudioProvider.WorldFadeFloor, hardStops: false));
        Assert.Equal(StopWay.Now, FmodAudioProvider.HowItStops(sounding: true, worldFade: 1f, hardStops: true));
        // Cut under the floor, a voice is 80 dB under its own level: at 60 it was at the edge of hearing.
        Assert.True(FmodAudioProvider.WorldFadeFloor <= 1e-4f);
    }

    private static readonly Vector3 Ear = Vector3.Zero;

    private static SpatialEmitter Tone(int id, bool oneShot, float volume = 0.5f, float metres = 2f) => new()
    {
        EntityId = id, SoundId = "lab:tone", Type = EmitterType.WorldLocked,
        Mode = oneShot ? PlaybackMode.Single : PlaybackMode.LoopOne, IsEvent = oneShot,
        Position = Ear + new Vector3(metres, 0f, 0f), Volume = volume, MinDistance = 1f, Range = 200f, Pitch = 1f,
        EqLow = 1f, EqMid = 1f, EqHigh = 1f, ApertureFactor = 1f,
    };

    private static void Pump(AudioEngineFacade facade, VoiceRegistry? registry = null)
    {
        for (int i = 0; i < 4; i++) { registry?.Fades(); facade.PumpForTest(); }
    }

    /// <summary>
    /// Every way a voice is let go reaches the mixer's StopSound while the voice is still sounding, once: the budget
    /// dropping a one-shot that lost its slot and giving up a continuous voice that lost its, an owner stopping a budget
    /// voice or a direct one, the registry's Stop, StopNow and both Retires, and leaving the world. That is where the
    /// fade is drawn, so none of them can cut a voice off.
    /// </summary>
    [Theory]
    [InlineData("one-shot losing its slot")]
    [InlineData("continuous voice losing its slot")]
    [InlineData("budget voice stopped by its owner")]
    [InlineData("direct voice stopped")]
    [InlineData("registry Stop")]
    [InlineData("registry StopNow")]
    [InlineData("registry Retire, voice fade")]
    [InlineData("registry Retire, engine fade")]
    [InlineData("leaving the world")]
    public void Every_way_a_voice_is_let_go_stops_it_at_the_mixer_while_it_sounds(string path)
    {
        var provider = new VoiceLifecycleTests.RecordingProvider();
        var facade = new AudioEngineFacade(provider);
        facade.InitializeForTest();
        facade.UpdateListener(Ear, Quaternion.Identity, Vector3.Zero, -1);
        var registry = new VoiceRegistry { Mixer = facade };
        const int id = 7101, louder = 7601;
        bool budget = path.Contains("slot") || path.StartsWith("budget", StringComparison.Ordinal);
        bool oneShot = path.StartsWith("one-shot", StringComparison.Ordinal);
        if (path.Contains("slot")) facade.BudgetForLab = 1;
        if (budget) facade.Submit(Tone(id, oneShot));
        else if (path.StartsWith("direct", StringComparison.Ordinal)) facade.PlayPhysicalSoundDirect(Tone(id, false));
        else registry.Sound(Tone(id, false));
        Pump(facade, registry);
        Assert.True(provider.IsPlaying(id), "the voice never started");

        switch (path)
        {
            case "one-shot losing its slot": facade.Submit(Tone(louder, true, volume: 1f, metres: 1f)); break;
            case "continuous voice losing its slot": facade.Submit(Tone(louder, false, volume: 1f, metres: 1f)); break;
            case "budget voice stopped by its owner": facade.StopSound(id); break;
            case "direct voice stopped": facade.StopSound(id); break;
            case "registry Stop": registry.Stop(id); break;
            case "registry StopNow": registry.StopNow(id); break;
            case "registry Retire, voice fade": registry.Retire(id, VoiceFade.Voice); break;
            case "registry Retire, engine fade": registry.Retire(id, VoiceFade.Engine); break;
            case "leaving the world": facade.StopAllWorldSounds(); break;
        }
        Pump(facade, registry);

        Assert.Equal(1, provider.Stopped.Count(s => s == id));
        Assert.False(provider.IsPlaying(id));
    }
}

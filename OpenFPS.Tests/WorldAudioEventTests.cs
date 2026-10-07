using System.Numerics;
using MemoryPack;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Common;
using OpenFPS.Common.Networking;

namespace OpenFPS.Tests;

/// <summary>
/// The channel for every short sound the world makes. It is generic: it carries knocks, rings, hisses
/// and scrapes, not doors, so a bouncing ball or a round striking concrete needs no new audio code.
/// </summary>
public class WorldAudioEventTests
{
    public WorldAudioEventTests() => AcousticRegistry.Initialize();

    // ── The wire ────────────────────────────────────────────────────────────────────────────────

    /// <summary>It survives the wire, parameters and all: the client renders from the physics sent, so
    /// numbers that arrive changed are a different sound.</summary>
    [Fact]
    public void AnEventSurvivesTheWire()
    {
        var sent = new WorldAudioEvent
        {
            SourceEntityId = 42,
            Label = "door",
            Seed = 7,
            Sounds = new List<TransientSound>
            {
                new() { Character = SoundCharacter.Knock, DelaySeconds = 0.02f, Position = new Vector3(1, 2, 3),
                        LevelDb = 71f, Hz = 2600f, DecaySeconds = 0.05f, Noisiness = 0.75f },
                new() { Character = SoundCharacter.Ring, DelaySeconds = 0.004f, Position = new Vector3(4, 5, 6),
                        LevelDb = 65f, Hz = 140f, DecaySeconds = 0.5f, Noisiness = 0.15f },
            },
        };

        var bytes = MemoryPackSerializer.Serialize<IMessage>(sent);
        var back = Assert.IsType<WorldAudioEvent>(MemoryPackSerializer.Deserialize<IMessage>(bytes));

        Assert.Equal(sent.SourceEntityId, back.SourceEntityId);
        Assert.Equal(sent.Label, back.Label);
        Assert.Equal(sent.Seed, back.Seed);
        Assert.Equal(2, back.Sounds.Count);
        for (int i = 0; i < 2; i++)
        {
            Assert.Equal(sent.Sounds[i].Character, back.Sounds[i].Character);
            Assert.Equal(sent.Sounds[i].Position, back.Sounds[i].Position);
            Assert.Equal(sent.Sounds[i].Hz, back.Sounds[i].Hz, 3);
            Assert.Equal(sent.Sounds[i].LevelDb, back.Sounds[i].LevelDb, 3);
            Assert.Equal(sent.Sounds[i].DecaySeconds, back.Sounds[i].DecaySeconds, 4);
            Assert.Equal(sent.Sounds[i].Noisiness, back.Sounds[i].Noisiness, 3);
            Assert.Equal(sent.Sounds[i].DelaySeconds, back.Sounds[i].DelaySeconds, 4);
        }
    }

    /// <summary>A door's model speaks the same vocabulary as everything else, with nothing lost in
    /// the translation.</summary>
    [Fact]
    public void ADoorsSoundsBecomeOrdinaryTransients()
    {
        var sounds = DoorAcoustics.Closing(AcousticRegistry.GetProperties("Metal"),
                                           new Vector3(1, 1, 0), Vector3.Zero,
                                           0.9f, 2.1f, 0.04f, 40f, 2f, hasSeal: true);

        foreach (var sound in sounds)
        {
            var transient = sound.ToTransient();
            Assert.Equal(sound.Character, transient.Character);
            Assert.Equal(sound.Position, transient.Position);
            Assert.Equal(sound.Hz, transient.Hz);
            Assert.Equal(sound.LevelDb, transient.LevelDb);
            Assert.Equal(sound.DecaySeconds, transient.DecaySeconds);
        }

        // The door's parts map onto physical characters, so the synthesiser need not know what a door is.
        Assert.Equal(SoundCharacter.Knock, sounds.First(s => s.Kind == DoorSoundKind.Latch).Character);
        Assert.Equal(SoundCharacter.Ring, sounds.First(s => s.Kind == DoorSoundKind.Panel).Character);
        Assert.Equal(SoundCharacter.Hiss, sounds.First(s => s.Kind == DoorSoundKind.Seal).Character);
    }

    // ── The renderer ────────────────────────────────────────────────────────────────────────────

    /// <summary>Every character renders something audible, of about the length it claimed, and
    /// nothing renders silence or a click of nothing.</summary>
    [Theory]
    [InlineData(SoundCharacter.Knock)]
    [InlineData(SoundCharacter.Ring)]
    [InlineData(SoundCharacter.Hiss)]
    [InlineData(SoundCharacter.Scrape)]
    public void EveryCharacterRendersSomethingAudible(SoundCharacter character)
    {
        var sound = new TransientSound
        {
            Character = character, Hz = 220f, LevelDb = 70f, DecaySeconds = 0.4f, Noisiness = 0.4f,
        };
        var pcm = TransientSynth.Render(sound, seed: 3);

        int expected = (int)(0.4f * TransientSynth.SampleRate);
        Assert.InRange(pcm.Length, expected - 100, expected + 100);
        Assert.True(pcm.Max(MathF.Abs) > 0.5f, $"{character} came out nearly silent");
        Assert.All(pcm, v => Assert.InRange(v, -1.001f, 1.001f));
    }

    /// <summary>A ring holds on and a knock does not, in the samples: it is how steel and wooden doors are
    /// told apart.</summary>
    [Fact]
    public void ARingOutlastsAKnock()
    {
        float ringTail = TailEnergy(SoundCharacter.Ring);
        float knockTail = TailEnergy(SoundCharacter.Knock);
        Assert.True(ringTail > knockTail * 2f,
                    $"the ring's last quarter held {ringTail:F4} and the knock's {knockTail:F4}");
    }

    /// <summary>Energy in the last quarter of the buffer, as a fraction of the whole.</summary>
    private static float TailEnergy(SoundCharacter character)
    {
        var pcm = TransientSynth.Render(new TransientSound
        {
            Character = character, Hz = 200f, DecaySeconds = 0.5f, Noisiness = 0.2f, LevelDb = 70f,
        }, seed: 5);

        float all = pcm.Sum(v => v * v);
        float tail = pcm.Skip(pcm.Length * 3 / 4).Sum(v => v * v);
        return all <= 0f ? 0f : tail / all;
    }

    /// <summary>The seed varies the render (twenty identical rounds read as a replayed recording), and the
    /// same seed gives the same render, so two players together hear the same door.</summary>
    [Fact]
    public void TheSeedVariesItAndRepeatsIt()
    {
        var sound = new TransientSound
        {
            Character = SoundCharacter.Knock, Hz = 300f, DecaySeconds = 0.2f, Noisiness = 0.6f, LevelDb = 70f,
        };

        var a = TransientSynth.Render(sound, seed: 1);
        var b = TransientSynth.Render(sound, seed: 2);
        var againA = TransientSynth.Render(sound, seed: 1);

        Assert.Equal(a, againA);
        Assert.NotEqual(a, b);
    }

    /// <summary>A ring keeps its note within a small variation: a panel asked for 200 Hz is not an octave off.</summary>
    [Fact]
    public void ARingComesOutAtAboutTheNoteItWasAskedFor()
    {
        const float asked = 200f;
        var pcm = TransientSynth.Render(new TransientSound
        {
            Character = SoundCharacter.Ring, Hz = asked, DecaySeconds = 0.5f, Noisiness = 0f, LevelDb = 70f,
        }, seed: 11);

        // Count zero crossings over the first tenth of a second, while the fundamental dominates.
        int window = TransientSynth.SampleRate / 10;
        int crossings = 0;
        for (int i = 1; i < Math.Min(window, pcm.Length); i++)
            if ((pcm[i - 1] < 0f) != (pcm[i] < 0f)) crossings++;

        float measured = crossings / 2f * 10f;
        Assert.InRange(measured, asked * 0.7f, asked * 1.45f);
    }

    /// <summary>Something that decays in five milliseconds is not allowed to allocate three seconds
    /// of silence, and something claiming to ring for a minute is not allowed to allocate a minute.</summary>
    [Fact]
    public void NothingRendersARidiculousBuffer()
    {
        var brief = TransientSynth.Render(new TransientSound
        { Character = SoundCharacter.Knock, Hz = 3000f, DecaySeconds = 0.004f, LevelDb = 60f }, 1);
        var forever = TransientSynth.Render(new TransientSound
        { Character = SoundCharacter.Ring, Hz = 100f, DecaySeconds = 90f, LevelDb = 60f }, 1);

        Assert.InRange(brief.Length, 16, TransientSynth.SampleRate / 50);
        Assert.InRange(forever.Length, 1, TransientSynth.SampleRate * 3);
    }

    // ── 16-bit ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ItConvertsToSixteenBitWithoutWrappingRound()
    {
        var pcm = new[] { 0f, 0.5f, -0.5f, 1f, -1f, 4f, -4f };
        var bytes = TransientSynth.ToPcm16(pcm);
        Assert.Equal(pcm.Length * 2, bytes.Length);

        short At(int i) => (short)(bytes[i * 2] | (bytes[i * 2 + 1] << 8));
        Assert.Equal(0, At(0));
        Assert.InRange(At(1), 16000, 16500);
        Assert.InRange(At(2), -16500, -16000);
        // Past full scale clamps rather than wraps: a wrap is a loud click.
        Assert.Equal(short.MaxValue, At(5));
        Assert.Equal(short.MinValue, At(6));
    }
}

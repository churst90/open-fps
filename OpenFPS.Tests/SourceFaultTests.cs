using System.Numerics;
using OpenFPS.Audio.Core;
using OpenFPS.Audio.Data;
using OpenFPS.Client.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Geometry;
using OpenFPS.Sound;

namespace OpenFPS.Tests;

/// <summary>
/// One bad source must not touch the mix (docs/LIBRARY_CHANGES.md): a value that would poison a shared stage is caught
/// where the source is made, and that source alone is silent. The real mix is measured in AudioLab (--game-levels
/// set=source-faults); these are the checks that run without FMOD.
/// </summary>
public class SourceFaultTests
{
    private const int Good = 1, Bad = 2;

    private static ClientAudioHarness WithGoodCar()
    {
        var h = new ClientAudioHarness();
        h.StandAt(Vector3.Zero);
        h.AddCar(Good, "i4_economy", new Vector3(8f, 0f, 0f));
        return h;
    }

    private static void AddEmitter(ClientAudioHarness h, string soundId, Func<SoundEmitterComponent, SoundEmitterComponent> edit)
    {
        var def = new EntityDefinition
        {
            EntityId = Bad, Type = EntityType.StaticObject,
            Transform = new Transform { Position = new Vector3(0f, 0.6f, 5f), Rotation = Quaternion.Identity, Scale = Vector3.One },
        };
        var em = new SoundEmitterComponent { IsSynth = true, SoundId = soundId, Mode = PlaybackMode.LoopOne, Volume = 1f, Range = 60f, MinDistance = 1.2f };
        def.SoundEmitter = edit(em);
        h.World.RegisterDefinition(def);
    }

    private static void AssertOnlyTheBadOneIsSilent(ClientAudioHarness h, string badSoundId)
    {
        Assert.True(h.TickUntil(() => h.Mixer.Live.Contains(Good), 120), "the good car never sounded");
        h.Wait(3.0);
        h.Tick(30);
        Assert.Contains(Good, h.Mixer.Live);
        // The bad source is never given a voice of its own, whatever voice id it would have had.
        Assert.DoesNotContain(h.Mixer.Started, e => e.SoundId == badSoundId);
        foreach (var e in h.Mixer.Started)
        {
            Assert.True(float.IsFinite(e.Volume) && float.IsFinite(e.Range) && float.IsFinite(e.MinDistance) && float.IsFinite(e.ConeOutside),
                        $"voice {e.EntityId} reached the mixer with a value that is not finite");
        }
    }

    [Fact]
    public void ASpecDefaultsToAnEmitterThatSoundsEverywhere()
    {
        var emitter = new SoundEmitterComponent();
        foreach (var spec in new[] { new SourceSpec(), SnapshotSources.SpecOf(null), new AudioSource(7).Spec })
        {
            Assert.Equal(emitter.ConeInsideAngle, spec.ConeInsideAngle);
            Assert.Equal(emitter.ConeOutsideAngle, spec.ConeOutsideAngle);
            Assert.Equal(emitter.ConeOutsideVolume, spec.ConeOutsideVolume);
            Assert.Equal(emitter.SynthRunning, spec.SynthRunning);
        }
        Assert.Equal(360f, new SourceSpec().ConeOutsideAngle);
        Assert.Equal(1f, new SourceSpec().ConeOutsideVolume);
        Assert.True(new SourceSpec().SynthRunning);
    }

    [Theory]
    [InlineData("engine:no_such_vehicle")]
    [InlineData("fire:no_such_fire")]
    [InlineData("water:no_such_pond")]
    public void ASourceNamingAModelThatIsNotThereIsSilentAndTheRestSounds(string soundId)
    {
        var h = WithGoodCar();
        // Synth fields as an authored plain oscillator would carry them: the old path played these loud.
        AddEmitter(h, soundId, em => { em.SynthFrequency = 220f; em.SynthWave = 0; return em; });
        AssertOnlyTheBadOneIsSilent(h, soundId);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void ASourceWithAValueThatIsNotFiniteIsSilentAndTheRestSounds(float value)
    {
        var h = WithGoodCar();
        AddEmitter(h, "water:park_fountain", em => { em.Volume = value; em.ConeOutsideVolume = value; return em; });
        AssertOnlyTheBadOneIsSilent(h, "water:park_fountain");
    }

    [Fact]
    public void AGoodCarAloneSounds()
    {
        var h = WithGoodCar();
        Assert.True(h.TickUntil(() => h.Mixer.Live.Contains(Good), 120), "the good car never sounded");
    }

    [Fact]
    public void TheCheckNamesTheFieldThatIsBad()
    {
        var e = new SpatialEmitter { Volume = 1f, Range = 10f, MinDistance = 1f, ConeInside = 360f, ConeOutside = 360f, ConeOutsideVolume = 1f, Direction = Vector3.UnitZ };
        Assert.Null(AudioWorld.BadValueIn(e));
        e.ConeOutsideVolume = float.NaN;
        Assert.Contains("cone", AudioWorld.BadValueIn(e));
        e.ConeOutsideVolume = 1f; e.Range = float.NaN;
        Assert.Contains("finite", AudioWorld.BadValueIn(e));
    }
}

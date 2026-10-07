using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;

namespace OpenFPS.Tests;

/// <summary>
/// Which sustained sources the ground answers inside their voice (ClientAudioSystem.ProcessAudioEmitter).
/// An engine and a physical model do (GroundReflection). A recording that goes on, such as a PA
/// speaker's speech, does not: speech with a ground reflection flanged (docs/CLIENT_NOTES.md, "Speech
/// has no ground reflection"), and a recording made near the ground has its own. The test asked
/// `physicalKey != null` of a string that starts as "", so every emitter got one (probable bug 2 of
/// 2026-10-07).
/// </summary>
public class SustainedGroundTests
{
    private static ClientAudioHarness OnAsphalt()
    {
        var h = new ClientAudioHarness();
        var def = new EntityDefinition
        {
            EntityId = 1,
            Type = EntityType.StaticObject,
            Transform = new Transform { Position = new Vector3(0f, -0.25f, 0f), Rotation = Quaternion.Identity },
        };
        def.Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(200f, 0.5f, 200f), IsSolid = true };
        def.Material.Material = "Asphalt";
        h.World.RegisterDefinition(def);
        h.StandAt(Vector3.Zero);
        h.Tick();
        return h;
    }

    private static void Add(ClientAudioHarness h, int id, string sound, bool synth, Vector3 at)
    {
        var def = new EntityDefinition
        {
            EntityId = id, Type = EntityType.StaticObject,
            Transform = new Transform { Position = at, Rotation = Quaternion.Identity, Scale = Vector3.One },
        };
        def.SoundEmitter = new SoundEmitterComponent();
        def.SoundEmitter.IsSynth = synth;
        def.SoundEmitter.SoundId = sound;
        def.SoundEmitter.Mode = PlaybackMode.LoopOne;
        def.SoundEmitter.Volume = 1f;
        def.SoundEmitter.Range = 400f;
        def.SoundEmitter.MinDistance = 12f;
        h.World.RegisterDefinition(def);
    }

    [Fact]
    public void APublicAddressSpeakersSpeechHasNoGroundReflection()
    {
        var h = OnAsphalt();
        Add(h, 50, "ANNOUNCE/st_louis_welcome", synth: false, new Vector3(0f, 4f, 10f));
        Assert.True(h.TickUntil(() => h.Mixer.Latest.ContainsKey(50), 60), "the speaker was never played");
        h.Tick(5);
        var e = h.Mixer.Latest[50];
        Assert.Equal(0f, e.GroundLowGain);
        Assert.Equal(0f, e.GroundHighGain);
    }

    [Fact]
    public void AMachineStillHearsTheRoadUnderIt()
    {
        var h = OnAsphalt();
        Add(h, 51, "machine:ac_condenser", synth: true, new Vector3(0f, 0.6f, 6f));
        Assert.True(h.TickUntil(() => h.Mixer.Latest.ContainsKey(51), 120), "the machine was never voiced");
        h.Tick(5);
        Assert.True(h.Mixer.Latest[51].GroundLowGain > 0f, "a machine on the ground lost its ground reflection");
    }
}

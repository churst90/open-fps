using System.Numerics;
using OpenFPS.Client.AudioEngine.Acoustics;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;

namespace OpenFPS.Tests;

/// <summary>
/// Recorded sounds (a door, a shot) hear the ground as synthesised voices do. Speech does not (see
/// WorldAudioPlayer.HearsTheGround); the geometry below is still the one a voice would get.
/// </summary>
public class RecordedGroundTests
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

    [Fact]
    public void A_mouth_three_metres_off_is_answered_by_the_pavement_a_few_milliseconds_later()
    {
        var h = OnAsphalt();
        var mouth = new Vector3(3f, Speech.MouthHeight, 0f);
        var e = new SpatialEmitter { EntityId = -5_000_001, Position = mouth };
        h.Audio.ApplyRecordedGround(ref e, h.World.GetSnapshot());

        var ear = new Vector3(0f, 1.7f, 0f);
        var image = new Vector3(mouth.X, -mouth.Y, mouth.Z);
        float expected = (Vector3.Distance(image, ear) - Vector3.Distance(mouth, ear)) / AudioPhysics.SpeedOfSound;
        Assert.InRange(e.GroundDelaySeconds, expected * 0.9f, expected * 1.1f);
        Assert.InRange(e.GroundDelaySeconds, 0.003f, 0.005f);
        Assert.True(e.GroundLowGain > 0.5f, $"low {e.GroundLowGain}");
        Assert.True(e.GroundHighGain > 0.3f, $"high {e.GroundHighGain}");
    }

    [Fact]
    public void A_sound_made_at_the_ground_already_has_it_in_the_recording()
    {
        var h = OnAsphalt();
        var e = new SpatialEmitter { EntityId = -5_000_002, Position = new Vector3(2f, 0.05f, 1f) };
        h.Audio.ApplyRecordedGround(ref e, h.World.GetSnapshot());
        Assert.Equal(0f, e.GroundLowGain);
        Assert.Equal(0f, e.GroundHighGain);
    }

    /// <summary>A pooled binaural stage handed to a new sound starts at the new sound's ground, not
    /// gliding up from nothing (which would lose it under a sharp onset) and not with the last sound's.</summary>
    [Fact]
    public void A_reset_stage_takes_its_first_ground_at_once()
    {
        const float sr = 48000f;
        var g = new GroundReflection(sr);
        g.Set(0.002f, 0.2f, 0.2f);
        for (int i = 0; i < 5000; i++) g.Process(0.3f);
        g.Reset();
        g.Set(0.004f, 1f, 1f);
        int delay = (int)(0.004f * sr);
        float echo = 0f;
        for (int i = 0; i <= delay + 2; i++)
        {
            float y = g.Process(i == 0 ? 1f : 0f);
            if (i >= 1 && i < delay - 1) Assert.True(MathF.Abs(y) < 1e-6f, $"sample {i} carries the last sound: {y}");
            if (i >= delay - 1) echo = MathF.Max(echo, MathF.Abs(y));
        }
        Assert.True(echo > 0.7f, $"first echo {echo}");
    }
}

/// <summary>Lines are matched by loudness, the way the ear weighs them, not by RMS.</summary>
public class SpeechLoudnessTests
{
    private static float[] Sine(float hz, float rmsDb, int n = 48000)
    {
        float a = MathF.Pow(10f, rmsDb / 20f) * MathF.Sqrt(2f);
        var x = new float[n];
        for (int i = 0; i < n; i++) x[i] = a * MathF.Sin(2f * MathF.PI * hz * i / 48000f);
        return x;
    }

    /// <summary>BS.1770's calibration: a 1 kHz tone reads its own RMS level.</summary>
    [Fact]
    public void A_kilohertz_tone_reads_its_own_level()
        => Assert.InRange(Speech.LoudnessLufs(Sine(1000f, -20f)), -20.15, -19.85);

    /// <summary>The K-weighting: the ear's shelf lifts the top, and deep bass counts for less.</summary>
    [Fact]
    public void Bass_counts_for_less_and_presence_for_more()
    {
        Assert.True(Speech.LoudnessLufs(Sine(40f, -20f)) < -21.0);
        Assert.InRange(Speech.LoudnessLufs(Sine(4000f, -20f)), -17.0, -16.0);   // the shelf is +3 to +4 dB by 4 kHz
    }
}

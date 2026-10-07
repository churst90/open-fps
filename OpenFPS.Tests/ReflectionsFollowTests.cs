using System.Numerics;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Client.Core;
using OpenFPS.Client.Core.Platform;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;

namespace OpenFPS.Tests;

/// <summary>
/// Reflections of your steps, your voice and another talker follow you both up a building: on the
/// upper of two identical storeys every copy is where it was below, one storey up. Copies once went to
/// the occlusion worker as sources at their images, and a talker had none
/// (docs/TEST_NOTES.md, "Reflections that did not follow").
/// </summary>
public class ReflectionsFollowTests
{
    private const float Storey = 3.2f, Slab = 0.2f;
    private const float HalfX = 3f, HalfZ = 4f, Wall = 0.3f;
    private const int Floor0Room = 9000, Floor1Room = 9001;

    /// <summary>Two concrete rooms, one on top of the other, each 6 by 8 m and 3 m high.</summary>
    private static void BuildTwoStoreys(ClientAudioHarness h)
    {
        float top = 2 * Storey;
        for (int k = 0; k <= 2; k++)
            h.AddWall(8000 + k, new Vector3(0f, k * Storey - Slab / 2f, 0f), new Vector3(2 * HalfX + 2 * Wall, Slab, 2 * HalfZ + 2 * Wall), "Concrete");
        float height = top + Slab, midY = (top - Slab) / 2f;
        h.AddWall(8010, new Vector3(-HalfX - Wall / 2f, midY, 0f), new Vector3(Wall, height, 2 * HalfZ + 2 * Wall), "Concrete");
        h.AddWall(8011, new Vector3(HalfX + Wall / 2f, midY, 0f), new Vector3(Wall, height, 2 * HalfZ + 2 * Wall), "Concrete");
        h.AddWall(8012, new Vector3(0f, midY, -HalfZ - Wall / 2f), new Vector3(2 * HalfX, height, Wall), "Concrete");
        h.AddWall(8013, new Vector3(0f, midY, HalfZ + Wall / 2f), new Vector3(2 * HalfX, height, Wall), "Concrete");

        var map = new AcousticMap();
        for (int k = 0; k < 2; k++)
        {
            int id = k == 0 ? Floor0Room : Floor1Room;
            map.Regions[id] = new RegionComponent
            {
                FriendlyName = $"stair, floor {k}", IsIndoor = true,
                RoomSize = new Vector3(2 * HalfX, Storey - Slab, 2 * HalfZ),
            };
            map.RegionPositions[id] = new Vector3(0f, k * Storey + (Storey - Slab) / 2f, 0f);
        }
        h.World.SetAcousticMap(map);
    }

    private static readonly Vector3 Up = new(0f, Storey, 0f);

    // ── Your own steps ──────────────────────────────────────────────────────────────────────

    private static List<SpatialEmitter> StepEchoes(ClientAudioHarness h, int from)
        => h.Mixer.Started.Skip(from).Where(e => e.IsReflection && !e.FollowsListener).ToList();

    [Fact]
    public void Your_own_steps_copies_keep_their_own_path_and_are_not_traced_through_their_wall()
    {
        var h = new ClientAudioHarness(Sounds());
        BuildTwoStoreys(h);
        var feet = new Vector3(0.6f, Storey, 1.1f);
        h.StandAt(feet);
        h.Tick();

        int from = h.Mixer.Started.Count;
        h.Audio.OnOwnFootstep(feet + new Vector3(0f, 0f, 0.2f), "Concrete", "0");
        h.Tick(2);
        var echoes = StepEchoes(h, from);
        Assert.NotEmpty(echoes);
        int step = h.Mixer.Started.Skip(from).First(e => e.FollowsListener).EntityId;

        // The step itself is answered, and long enough after for the copies to have been too.
        Assert.True(h.TickUntil(() => h.Mixer.HasPath(step), 300), "the worker never answered for the step itself");
        for (int i = 0; i < 30; i++) { h.Tick(); System.Threading.Thread.Sleep(2); }

        foreach (var e in echoes)
            Assert.False(h.Mixer.HasPath(e.EntityId),
                $"the copy at ({e.Position.X:F1}, {e.Position.Y:F1}, {e.Position.Z:F1}) was given a path traced from its image: "
                + (h.Mixer.HasPath(e.EntityId) ? $"occlusion {h.Mixer.LastPath(e.EntityId).Occlusion:F2}, mid {h.Mixer.LastPath(e.EntityId).EqMid:F3}" : ""));
    }

    [Fact]
    public void Your_own_steps_are_answered_by_the_floor_you_are_on()
    {
        List<Vector3> CopiesOn(int floor)
        {
            var h = new ClientAudioHarness(Sounds());
            BuildTwoStoreys(h);
            var feet = new Vector3(0.6f, floor * Storey, 1.1f);
            h.StandAt(feet);
            h.Tick();
            int from = h.Mixer.Started.Count;
            h.Audio.OnOwnFootstep(feet + new Vector3(0f, 0f, 0.2f), "Concrete", "0");
            h.Tick(2);
            return StepEchoes(h, from).Select(e => e.Position).ToList();
        }

        var below = CopiesOn(0);
        var above = CopiesOn(1);
        Assert.NotEmpty(below);
        AssertSameUpAStorey(below, above, "your step's copies");
    }

    // ── Your own voice ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Your_own_voice_is_answered_by_the_floor_you_are_on()
    {
        var h = new ClientAudioHarness();
        BuildTwoStoreys(h);
        h.Audio.OwnVoiceLive = true;
        try
        {
            h.StandAt(new Vector3(0.6f, 0f, 1.1f));
            h.Tick(3);
            var below = OwnVoiceCopies(h);
            Assert.NotEmpty(below);

            h.StandAt(new Vector3(0.6f, Storey, 1.1f));
            System.Threading.Thread.Sleep(150);   // past the 0.1 s between searches, on the audio clock
            h.Tick(3);
            AssertSameUpAStorey(below, OwnVoiceCopies(h), "your voice's copies");
        }
        finally { h.Audio.OwnVoiceLive = false; }
    }

    private static List<Vector3> OwnVoiceCopies(ClientAudioHarness h)
    {
        var at = new List<Vector3>();
        for (int k = 1; k <= 8; k++)
            if (h.Mixer.Live.Contains(ClientAudioSystem.OwnVoiceBase - k)) at.Add(h.Mixer.Latest[ClientAudioSystem.OwnVoiceBase - k].Position);
        return at;
    }

    // ── Somebody else talking ─────────────────────────────────────────────────────────────────

    private const int Sean = 42;

    private static void AddPlayer(ClientAudioHarness h, int id, Vector3 feet)
    {
        var def = new EntityDefinition
        {
            EntityId = id,
            Type = EntityType.Player,
            Moves = true,
            Transform = new Transform { Position = feet, Rotation = Quaternion.Identity, Scale = Vector3.One },
        };
        h.World.RegisterDefinition(def);
        MovePlayer(h, id, feet);
    }

    private static void MovePlayer(ClientAudioHarness h, int id, Vector3 feet)
        => h.World.SyncState(new[] { new EntityState
        {
            EntityId = id,
            Transform = QuantizedTransform.FromTransform(new Transform { Position = feet, Rotation = Quaternion.Identity, Scale = Vector3.One }),
        } });

    private static ushort _seq = 1;

    /// <summary>A fifth of a second of Sean talking, arriving now.</summary>
    private static void SeanSays()
    {
        var enc = new VoiceFrameEncoder();
        var frame = new float[VoiceCodec.FrameSamples];
        var stream = Talkers.For(Sean);
        for (int f = 0; f < 10; f++)
        {
            for (int i = 0; i < frame.Length; i++) frame[i] = 0.3f * MathF.Sin(2f * MathF.PI * 180f * i / VoiceCodec.Rate);
            enc.Push(frame, p => stream.Receive(_seq++, p, AudioClock.Now));
        }
    }

    private static List<SpatialEmitter> SeansCopies(ClientAudioHarness h)
        => h.Mixer.Latest.Values.Where(e => h.Mixer.Live.Contains(e.EntityId)
                                         && e.PhysicalKey.StartsWith(Talkers.CopyKeyPrefix, StringComparison.Ordinal)).ToList();

    [Fact]
    public void Somebody_talking_is_answered_by_the_room_they_are_in_and_its_surfaces_follow_them()
    {
        Talkers.Clear();
        var h = new ClientAudioHarness();
        BuildTwoStoreys(h);
        try
        {
            h.Audio.OwnEntityId = 7;
            h.StandAt(new Vector3(-1.5f, 0f, -2f));
            AddPlayer(h, Sean, new Vector3(0.6f, 0f, 1.1f));
            h.Tick();
            SeanSays();
            int voice = ClientAudioSystem.TalkerVoiceBase - Sean;
            Assert.True(h.TickUntil(() => h.Mixer.Live.Contains(voice) && SeansCopies(h).Count > 0, 120), "Sean's voice and its copies never started");

            // His voice reverberates in the room his mouth is in...
            Assert.True(h.TickUntil(() => h.Mixer.Latest[voice].TargetRegionId == Floor0Room, 120),
                $"Sean's voice went to region {h.Mixer.Latest[voice].TargetRegionId}, not the room he is standing in");
            // ...and its surfaces answer: copies of this talker's voice at their images, each its own path
            // and never a reverb send of its own.
            var below = SeansCopies(h);
            Assert.All(below, c =>
            {
                Assert.True(c.IsReflection);
                Assert.True(Talkers.TryParseCopyKey(c.PhysicalKey, out int of) && of == Sean, c.PhysicalKey);
                Assert.InRange(c.EchoDelaySeconds, 0f, 0.1f);
            });
            Assert.Contains(below, c => c.Position.Y > Storey - Slab);   // the ceiling over him answers

            // Both of you go up a floor: everything that answered him downstairs answers him there.
            h.StandAt(new Vector3(-1.5f, Storey, -2f));
            MovePlayer(h, Sean, new Vector3(0.6f, Storey, 1.1f));
            System.Threading.Thread.Sleep(150);
            SeanSays();
            Assert.True(h.TickUntil(() => h.Mixer.Latest[voice].TargetRegionId == Floor1Room, 240),
                $"Sean's voice stayed in region {h.Mixer.Latest[voice].TargetRegionId} after he went up a floor");
            System.Threading.Thread.Sleep(150);
            SeanSays();
            h.Tick(3);
            AssertSameUpAStorey(below.Select(c => c.Position).ToList(), SeansCopies(h).Select(c => c.Position).ToList(),
                                "Sean's voice's copies");
        }
        finally { Talkers.Clear(); }
    }

    [Fact]
    public void A_talkers_copy_key_names_the_talker_and_is_not_their_voice()
    {
        Assert.True(Talkers.TryParseCopyKey(Talkers.CopyKey(42), out int of) && of == 42);
        Assert.False(Talkers.TryParseKey(Talkers.CopyKey(42), out _));
        Assert.False(Talkers.TryParseCopyKey(Talkers.Key(42), out _));
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────

    private static void AssertSameUpAStorey(List<Vector3> below, List<Vector3> above, string what)
    {
        static string Show(IEnumerable<Vector3> v) => string.Join(" ", v.Select(p => $"({p.X:F2},{p.Y:F2},{p.Z:F2})"));
        var expected = below.Select(p => p + Up).OrderBy(p => p.X).ThenBy(p => p.Y).ThenBy(p => p.Z).ToList();
        var actual = above.OrderBy(p => p.X).ThenBy(p => p.Y).ThenBy(p => p.Z).ToList();
        Assert.True(expected.Count == actual.Count && expected.Zip(actual).All(t => Vector3.Distance(t.First, t.Second) < 0.05f),
            $"{what} on the floor above are not the floor below's, one storey up.\n  below+storey: {Show(expected)}\n  above:        {Show(actual)}");
    }

    private static string Sounds()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "OpenFPS.Client", "ASSETS", "SOUNDS"))) dir = dir.Parent;
        return dir != null ? Path.Combine(dir.FullName, "OpenFPS.Client", "ASSETS", "SOUNDS")
                           : "/home/cody/external-rescue/Github/open-fps/OpenFPS.Client/ASSETS/SOUNDS";
    }
}

using System.Numerics;
using OpenFPS.Client.AudioEngine.Core.Rail;
using OpenFPS.Client.Core;
using OpenFPS.Common;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// The voices of moving things (docs/SPECIAL_CASES_AUDIT.md, P3a): every one is an entry in the registry
/// and is handed its owner's path in its own emitter, by one pass, every frame. "Shouldn't all mobile sound
/// sources like vehicles, trains, sirens, etc use the same path so there's consistency?" (Cody, 2026-10-11),
/// after a train horn played through a brick wall on the path its voice started with.
/// </summary>
public class VoiceRegistryTests
{
    private readonly ITestOutputHelper _o;
    public VoiceRegistryTests(ITestOutputHelper o) => _o = o;

    /// <summary>
    /// Two seconds in front of a brick building with one of every voice of a moving thing about: a car near
    /// enough for its intake to be a voice of its own, honking; a patrol car behind the building, its driver
    /// on the siren and the horn; three cars beyond the engine budget, borrowing; a train's source. Each
    /// frame's entries are collected.
    /// </summary>
    private static (Replay Run, Dictionary<int, VoiceKind> Seen) EveryKind()
    {
        var seen = new Dictionary<int, VoiceKind>();
        var run = Replay.RunScript(r =>
        {
            r.Wall(9501, new Vector3(0f, 4f, -6f), new Vector3(40f, 8f, 0.3f), "Brick");
            r.ShedEnginesTo(2);
            r.Car(501, "v8_muscle", new Vector3(4f, 0.5f, 7f), Vector3.Zero);
            var patrolAt = new Vector3(-20f, 0.5f, -14f);
            var patrolV = new Vector3(10f, 0f, 0f);
            r.Car(502, "police_interceptor", patrolAt, patrolV);
            var far = new (int Id, Vector3 At, Vector3 V)[]
            {
                (503, new Vector3(-30f, 0.5f, 70f), new Vector3(11f, 0f, 0f)),
                (504, new Vector3(-20f, 0.5f, -40f), new Vector3(10f, 0f, 0f)),
                (505, new Vector3(150f, 0.5f, 10f), new Vector3(0f, 0f, 12f)),
            };
            foreach (var c in far) r.Car(c.Id, "v8_muscle", c.At, c.V);
            var layout = TrainLayout.Sources(TrainProfile.ByName("light_rail"));
            for (int i = 0; i < layout.Count; i++)
                r.RailSource(601 + i, $"rail:light_rail/t1/{i}", layout[i].LevelDb,
                             new Vector3(-10f - layout[i].AlongMetres, layout[i].HeightMetres, -25f), Vector3.Zero);
            for (int f = 0; f < 120; f++)
            {
                if (f == 10) r.Audio.WorldAudio.HornReceived!(501, "electric:disc_pair", new[] { 1.5f });
                r.Listener(new Vector3(0f, 0f, 4f), Vector3.Zero, 180f);
                r.Move(501, new Vector3(4f, 0.5f, 7f), Vector3.Zero);
                r.Move(502, patrolAt, patrolV, Replay.Driven(horn: true, siren: SirenMode.Wail));
                for (int i = 0; i < far.Length; i++)
                {
                    r.Move(far[i].Id, far[i].At, far[i].V);
                    far[i].At += far[i].V / (float)ClientAudioSystem.UpdateHz;
                }
                r.Tick();
                patrolAt += patrolV / (float)ClientAudioSystem.UpdateHz;
                foreach (var e in r.Audio.Voices.Entries) seen[e.VoiceId] = e.Kind;
            }
        });
        return (run, seen);
    }

    [Fact]
    public void Every_voice_of_a_moving_thing_is_an_entry_with_its_one_writer()
    {
        var (run, seen) = EveryKind();
        foreach (var kind in new[] { VoiceKind.Engine, VoiceKind.Intake, VoiceKind.Borrowed, VoiceKind.Horn,
                                     VoiceKind.HeldHorn, VoiceKind.Siren, VoiceKind.TrainField })
            Assert.Contains(kind, seen.Values);
        _o.WriteLine(string.Join("\n", seen.OrderBy(s => s.Value).ThenBy(s => s.Key).Select(s => $"{s.Value,-12} {s.Key}")));

        // No registered voice is ever handed a path by a second call: it carries its own, every frame.
        var pathed = new HashSet<int>();
        var carried = new HashSet<int>();
        foreach (var line in run.Mixer.Lines)
        {
            var parts = line.Split(' ', 4);
            if (parts.Length < 3 || !int.TryParse(parts[2], out int id)) continue;
            if (parts[1] == "Path") pathed.Add(id);
            else if ((parts[1] == "Place" || parts[1] == "Play") && parts.Length == 4 && parts[3].Contains("CarriesPath=true")) carried.Add(id);
        }
        Assert.DoesNotContain(seen.Keys, pathed.Contains);
        // Inside nothing here, so every one of them is told its path by its own emitter.
        Assert.All(seen.Keys, id => Assert.Contains(id, carried));
    }

    /// <summary>A borrowed car behind a building is heard behind it: its voice was re-placed every frame with
    /// nothing in the way and no distance whatever its path said (S3).</summary>
    [Fact]
    public void A_borrowed_car_behind_a_building_is_occluded_and_at_its_distance()
    {
        var (run, seen) = EveryKind();
        int voice = ClientAudioSystem.DistantVoiceBase - 504;
        Assert.Equal(VoiceKind.Borrowed, seen[voice]);
        var expanded = EmitterStreamReplayTests.Expand(run.Mixer.Lines);
        string last = expanded.Last(l => l.Contains($" Place {voice} "));
        _o.WriteLine(last.Length > 400 ? last[..400] : last);
        float occlusion = Field(last, "Occlusion"), distance = Field(last, "EffectiveDistance");
        Assert.True(occlusion > 0.5f, $"occlusion {occlusion}");
        Assert.InRange(distance, 30f, 80f);
    }

    private static float Field(string line, string name)
    {
        int at = line.IndexOf(" " + name + "=", StringComparison.Ordinal) + name.Length + 2;
        int end = line.IndexOf(' ', at);
        return float.Parse(line[at..(end < 0 ? line.Length : end)], System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The lane rumble under your own car is placed by the budget alone, from the emitter it holds, which the
    /// aid renews: re-placed past it each frame, the budget then put back the pitch and place it started with,
    /// so the strikes kept the rate of the speed the strip was first touched at (S11).
    /// </summary>
    [Fact]
    public void The_lane_rumble_follows_the_speed()
    {
        const string rumble = "-964003";
        var expanded = EmitterStreamReplayTests.Expand(Replay.Run("talker_and_aids"));
        var lastOfFrame = new SortedDictionary<int, float>();
        foreach (var line in expanded)
        {
            var parts = line.Split(' ', 4);
            if (parts.Length < 4 || parts[2] != rumble || parts[1] is not ("Play" or "Place")) continue;
            lastOfFrame[int.Parse(parts[0])] = Field(line, "Pitch");
        }
        Assert.True(lastOfFrame.Count > 60, $"the rumble was placed in {lastOfFrame.Count} frames");
        float first = lastOfFrame.First().Value, last = lastOfFrame.Last().Value;
        _o.WriteLine($"rumble pitch {first:F3} at frame {lastOfFrame.First().Key}, {last:F3} at frame {lastOfFrame.Last().Key}");
        // From 4 m/s to 6: half as fast again.
        Assert.InRange(last / first, 1.35f, 1.65f);
        float prev = 0f;
        foreach (var (_, pitch) in lastOfFrame)
        {
            Assert.True(pitch >= prev, "the rumble's rate went back");
            prev = pitch;
        }
    }

    [Fact]
    public void How_each_kind_is_heard_from()
    {
        foreach (var kind in new[] { VoiceKind.Engine, VoiceKind.Aircraft, VoiceKind.Intake, VoiceKind.Borrowed })
            Assert.Equal(HeardFrom.Point, VoiceRegistry.HeardFromOf(kind));
        foreach (var kind in new[] { VoiceKind.Horn, VoiceKind.HeldHorn, VoiceKind.Siren, VoiceKind.TrainField, VoiceKind.TrainSignal })
            Assert.Equal(HeardFrom.TowardEdge, VoiceRegistry.HeardFromOf(kind));
        Assert.Equal(HeardFrom.Inside, VoiceRegistry.HeardFromOf(VoiceKind.Cabin));
    }

    [Fact]
    public void An_entry_lasts_while_it_is_declared_and_never_into_the_next_world()
    {
        var reg = new VoiceRegistry();
        var rot = Quaternion.CreateFromYawPitchRoll(MathF.PI / 2, 0f, 0f);
        var mount = VoiceRegistry.MountOf(new Vector3(10f, 0f, 0f), rot, new Vector3(12f, 0.6f, 0f));
        Assert.Equal(0f, mount.X, 4);
        Assert.Equal(0.6f, mount.Y, 4);
        Assert.Equal(2f, mount.Z, 4);   // two metres ahead of a car facing east

        reg.Declare(-1200007, 7, VoiceKind.Horn, mount, 110f, frame: 1);
        reg.Declare(-900008, 8, VoiceKind.Siren, Vector3.Zero, 130f, frame: 1);
        Assert.True(reg.TryGet(-1200007, out var horn));
        Assert.Equal(7, horn.OwnerId);
        Assert.Equal(new[] { 7, 8 }, reg.Owners.OrderBy(i => i));

        reg.Declare(-1200007, 7, VoiceKind.Horn, mount, 110f, frame: 2);
        reg.Sweep(2);
        Assert.True(reg.IsRegistered(-1200007));
        Assert.False(reg.IsRegistered(-900008));

        reg.LeaveWorld();
        Assert.False(reg.IsRegistered(-1200007));
        Assert.Empty(reg.Owners);
        reg.Declare(-1200007, 7, VoiceKind.Horn, mount, 110f, frame: 3);
        Assert.True(reg.TryGet(-1200007, out var again));
        Assert.Equal(reg.World, again.World);
    }
}

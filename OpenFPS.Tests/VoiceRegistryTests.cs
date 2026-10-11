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

    public static IEnumerable<object[]> Scenarios() => EmitterStreamReplayTests.Scenarios();

    /// <summary>
    /// Every voice written in a frame of every stored scenario is an entry at the end of it, and no voice is
    /// ever handed a path by a second call (docs/SPECIAL_CASES_AUDIT.md, P3b): what a voice is comes from its
    /// entry, not from where its id falls.
    /// </summary>
    [Theory]
    [MemberData(nameof(Scenarios))]
    public void Every_voice_written_is_an_entry_and_none_has_a_second_writer(string scenario)
    {
        var kinds = new Dictionary<VoiceKind, int>();
        var unregistered = new SortedSet<string>();
        var before = new HashSet<int>();
        int read = 0;
        var run = Replay.RunScript(r =>
        {
            r.AfterTick = x =>
            {
                var lines = x.Mixer.Lines;
                var written = new List<(int Id, string Line)>();
                var stopped = new HashSet<int>();
                for (; read < lines.Count; read++)
                {
                    var parts = lines[read].Split(' ', 4);
                    if (parts.Length < 3) continue;
                    if (parts[1] == "Stop") stopped.Add(int.Parse(parts[2]));
                    if (parts[1] is "Play" or "Place" or "Path") written.Add((int.Parse(parts[2]), $"{parts[1]} {parts[2]} (frame {parts[0]})"));
                }
                foreach (var (id, line) in written)
                {
                    if (x.Audio.Voices.TryGet(id, out var e)) kinds[e.Kind] = kinds.GetValueOrDefault(e.Kind) + 1;
                    // Let go this frame, its owner gone, and placed once more on the way out by the budget's
                    // held emitter before the stop goes through (P3d: one way to stop).
                    else if (!(stopped.Contains(id) && before.Contains(id))) unregistered.Add(line);
                }
                before.Clear();
                foreach (var e in x.Audio.Voices.Entries) before.Add(e.VoiceId);
            };
            Replay.Scenarios[scenario](r);
        });
        _o.WriteLine(string.Join(", ", kinds.OrderBy(k => k.Key).Select(k => $"{k.Key} {k.Value}")));
        Assert.True(unregistered.Count == 0, "written and no entry: " + string.Join("; ", unregistered.Take(20)));
        Assert.DoesNotContain(run.Mixer.Lines, l => l.Split(' ', 3) is [_, "Path", _]);
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

    /// <summary>
    /// A blocked engine is heard from the edge its sound bends round, as its horn is, and at its own distance,
    /// so a turn from the car to the edge goes round the head and not through it; clear, it is heard from the
    /// car (Cody, 2026-10-11). The replay's tracer answers every source from its own point, so the rule is
    /// shown on an answer here.
    /// </summary>
    [Fact]
    public void A_blocked_engine_is_heard_round_the_edge_at_its_own_distance()
    {
        foreach (var kind in new[] { VoiceKind.Engine, VoiceKind.Aircraft, VoiceKind.Intake, VoiceKind.Borrowed })
            Assert.Equal(VoiceRegistry.HeardFromOf(VoiceKind.Horn), VoiceRegistry.HeardFromOf(kind));
        var ear = new Vector3(0f, 1.7f, 0f);
        var car = new Vector3(9f, 0.8f, 8f);
        // Round a corner: the path arrives from the doorway, straight ahead.
        var blocked = new OpenFPS.Client.AudioEngine.Data.AcousticPathData(0.95f, new Vector3(0f, 1.2f, 3f), 12f)
        {
            SourcePosition = car,
        };
        var heard = ClientAudioSystem.SirenApparent(blocked, car, ear);
        Assert.Equal(Vector3.Distance(ear, car), Vector3.Distance(ear, heard), 3);
        Assert.Equal(0f, Vector3.Normalize(heard - ear).X, 3);
        // In the open, or with no answer yet: from the car.
        var open = new OpenFPS.Client.AudioEngine.Data.AcousticPathData(0f, car, 12f) { SourcePosition = car };
        Assert.Equal(car, ClientAudioSystem.SirenApparent(open, car, ear));
        Assert.Equal(car, ClientAudioSystem.SirenApparent(default, car, ear));
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
        // A blocked source comes from the edge it bent over, an engine as its horn does (Cody, 2026-10-11).
        foreach (var kind in new[] { VoiceKind.Engine, VoiceKind.Aircraft, VoiceKind.Intake, VoiceKind.Borrowed,
                                     VoiceKind.Horn, VoiceKind.HeldHorn, VoiceKind.Siren, VoiceKind.TrainField, VoiceKind.TrainSignal })
            Assert.Equal(HeardFrom.TowardEdge, VoiceRegistry.HeardFromOf(kind));
        Assert.Equal(HeardFrom.Inside, VoiceRegistry.HeardFromOf(VoiceKind.Cabin));
        foreach (var kind in new[] { VoiceKind.Machine, VoiceKind.Wood })
            Assert.Equal(HeardFrom.Point, VoiceRegistry.HeardFromOf(kind));
        Assert.Equal(HeardFrom.Edge, VoiceRegistry.HeardFromOf(VoiceKind.Recording));
        Assert.Equal(HeardFrom.Spread, VoiceRegistry.HeardFromOf(VoiceKind.Place));
        foreach (var kind in new[] { VoiceKind.OwnVoice, VoiceKind.OwnStep, VoiceKind.Aid, VoiceKind.Reference })
            Assert.Equal(HeardFrom.Inside, VoiceRegistry.HeardFromOf(kind));
        foreach (var kind in new[] { VoiceKind.Reflection, VoiceKind.EngineEcho, VoiceKind.Talker, VoiceKind.TalkerCopy,
                                     VoiceKind.OwnVoiceCopy, VoiceKind.Footstep, VoiceKind.StepEcho, VoiceKind.Rain,
                                     VoiceKind.RainDrop, VoiceKind.Bird, VoiceKind.Beacon, VoiceKind.Transient })
            Assert.Equal(HeardFrom.Own, VoiceRegistry.HeardFromOf(kind));
        // Only a voice heard by its owner's path has its owner asked about.
        foreach (var kind in Enum.GetValues<VoiceKind>())
            Assert.Equal(VoiceRegistry.HeardFromOf(kind) is HeardFrom.Point or HeardFrom.TowardEdge or HeardFrom.Edge or HeardFrom.Spread,
                         VoiceRegistry.TakesOwnersPath(kind));
    }

    [Fact]
    public void A_one_off_is_an_entry_until_it_has_stopped_and_its_owner_is_not_asked_about()
    {
        var reg = new VoiceRegistry { Frame = 1 };
        bool sounding = false;
        reg.DeclareEvent(-1800003, 9, VoiceKind.StepEcho, Vector3.Zero, 68f);
        reg.Declare(-1400009, 9, VoiceKind.Talker, Vector3.Zero, 60f);
        // A talker's own path is its own: the player is not asked about for it.
        Assert.Empty(reg.Owners);

        // Waiting to start, then sounding: kept with no declaration.
        for (int f = 2; f <= VoiceRegistry.EventStartFrames + 1; f++) reg.Sweep(f, _ => sounding);
        Assert.True(reg.IsRegistered(-1800003));
        Assert.False(reg.IsRegistered(-1400009));
        sounding = true;
        reg.Sweep(VoiceRegistry.EventStartFrames + 40, _ => sounding);
        Assert.True(reg.IsRegistered(-1800003));
        sounding = false;
        reg.Sweep(VoiceRegistry.EventStartFrames + 41, _ => sounding);
        Assert.False(reg.IsRegistered(-1800003));

        // Kept between its owner's searches, as it was declared.
        reg.Frame = 100;
        reg.Declare(-1700000, 9, VoiceKind.TalkerCopy, new Vector3(1f, 2f, 3f));
        reg.Frame = 101;
        reg.Keep(-1700000);
        reg.Sweep(101);
        Assert.True(reg.TryGet(-1700000, out var copy));
        Assert.Equal(new Vector3(1f, 2f, 3f), copy.Mount);
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

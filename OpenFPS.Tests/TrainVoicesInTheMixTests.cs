using System.Numerics;
using OpenFPS.Client.AudioEngine.Core.Rail;
using OpenFPS.Client.Core;
using OpenFPS.Common;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// A whole ClientAudioSystem (the emitter-stream replay's harness) with a fifty-wagon freight going past:
/// what the mixer is asked to play. In Cody's session of 2026-10-07 the freight was started 913 times in
/// nine minutes, held 130 voices at once and emptied the binaural pool.
/// </summary>
public class TrainVoicesInTheMixTests
{
    private readonly ITestOutputHelper _o;
    public TrainVoicesInTheMixTests(ITestOutputHelper o) => _o = o;

    private const int FirstEntity = 50_000;

    /// <summary>A train's own voice starting, by what it plays (TrainVoiceState.SlotKey), not where its id falls.</summary>
    private static bool IsTrainVoice(string playLine) => playLine.Contains("SoundId=\"rail\"", StringComparison.Ordinal);

    /// <summary>Where a point <paramref name="s"/> metres along the track is: a straight line down x, or a
    /// circle of 150 m round the origin (the listener 12 m inside it), which makes the groups merge and
    /// split as the train goes round.</summary>
    private static (Vector3 At, Vector3 Velocity) Track(bool curved, float s, float height, float speed)
    {
        if (!curved) return (new Vector3(s, height, 0f), new Vector3(speed, 0f, 0f));
        float a = s / 150f;
        return (new Vector3(150f * MathF.Sin(a), height, 150f - 150f * MathF.Cos(a)),
                new Vector3(MathF.Cos(a), 0f, MathF.Sin(a)) * speed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_fifty_wagon_freight_passing_is_a_handful_of_voices_and_none_comes_and_goes(bool curved)
    {
        var layout = TrainLayout.Sources(TrainProfile.ByName("freight"));
        const string train = "freight_test";
        const float speed = 18f;
        var ear = new Vector3(0f, 0f, 12f);
        double seconds = curved ? 25.0 : 15.0;
        var r = Replay.RunScript(r =>
        {
            float head = 120f;
            for (int i = 0; i < layout.Count; i++)
            {
                var (at, v) = Track(curved, head - layout[i].AlongMetres, layout[i].HeightMetres, speed);
                r.RailSource(FirstEntity + i, $"rail:freight/{train}/{i}", layout[i].LevelDb, at, v);
            }
            int frames = (int)(seconds * ClientAudioSystem.UpdateHz);
            for (int f = 0; f < frames; f++)
            {
                head += speed / (float)ClientAudioSystem.UpdateHz;
                for (int i = 0; i < layout.Count; i++)
                {
                    var (at, v) = Track(curved, head - layout[i].AlongMetres, layout[i].HeightMetres, speed);
                    r.Move(FirstEntity + i, at, v);
                }
                // Half way through, the horn and bell for a crossing.
                if (f == frames / 2) r.Audio.WorldAudio.TrainSignalReceived!("freight/" + train, new[] { 3f, 1f, 3f, 1f, 1f, 1f, 8f }, 18f);
                r.Listener(ear, Vector3.Zero, 0f);
                r.Tick();
            }
        });

        if (Environment.GetEnvironmentVariable("OPENFPS_TRAIN_MIX_DUMP") is { Length: > 0 } dump)
            File.WriteAllLines(dump, r.Mixer.Lines.Where(l => l.Contains("PlanTrain") || IsTrainVoice(l)));
        var playing = new HashSet<int>();
        int most = 0, trainStarts = 0, sourceVoices = 0, signalSeen = 0;
        foreach (var line in r.Mixer.Lines)
        {
            var parts = line.Split(' ', 4);
            if (parts.Length < 3 || !int.TryParse(parts[2], out int id)) continue;
            if (parts[1] == "Play")
            {
                if (id >= FirstEntity && id < FirstEntity + layout.Count) sourceVoices++;
                if (!IsTrainVoice(line)) continue;
                trainStarts++;
                playing.Add(id);
                most = Math.Max(most, playing.Count);
                if (line.Contains($"/@{TrainVoicing.WarningSlot}\"")) signalSeen++;
            }
            else if (parts[1] == "Stop") playing.Remove(id);
        }
        _o.WriteLine($"{trainStarts} train voice start(s) in {seconds:F0} s, at most {most} at once, {sourceVoices} per-source voice(s); horn voice started {signalSeen} time(s)");
        Assert.Equal(0, sourceVoices);
        Assert.True(most <= TrainVoicing.Slots, $"{most} voices for one train");
        Assert.True(most >= 2, "the freight beside you is more than one voice");
        Assert.True(trainStarts <= TrainVoicing.Slots + 3, $"{trainStarts} starts in {seconds:F0} s: voices are coming and going");
        Assert.Equal(1, signalSeen);
    }

    // ── A train's voice takes the path of where it is now ─────────────────────────────────────────

    private const int Source = 77, Wall = 78;

    /// <summary>A train's source as the server puts one, 12 m north of the listener at the origin.</summary>
    private static ClientAudioHarness ATrainInView()
    {
        var h = new ClientAudioHarness();
        h.StandAt(Vector3.Zero);
        var def = new OpenFPS.Common.Networking.EntityDefinition
        {
            EntityId = Source,
            Type = OpenFPS.Common.Components.EntityType.StaticObject,
            Transform = new OpenFPS.Common.Components.Transform { Position = new Vector3(0f, 1f, 12f), Rotation = Quaternion.Identity, Scale = Vector3.One },
        };
        def.SoundEmitter.IsSynth = true;
        def.SoundEmitter.SoundId = "rail:" + TrainProfile.Presets.Keys.First() + "/t1/0";
        def.SoundEmitter.Mode = OpenFPS.Common.Components.PlaybackMode.LoopOne;
        def.SoundEmitter.Volume = 1f;
        def.SoundEmitter.MinDistance = 1f;
        def.SoundEmitter.Range = 200f;
        h.World.RegisterDefinition(def);
        return h;
    }

    private static int VoiceOf(ClientAudioHarness h)
        => h.Mixer.Latest.Values.First(e => e.PhysicalKey.StartsWith("rail:", StringComparison.Ordinal)).EntityId;

    private static float Db(float g) => 20f * MathF.Log10(MathF.Max(1e-6f, g));

    /// <summary>
    /// A wall put up between a playing train voice and the ear reaches the voice. Its id is no entity's, and
    /// the path pass skipped it, and a re-submission without its path leaves a voice's tone alone: until 2026-10-10 a train
    /// kept the band gains it started with, and a horn begun on an outdoor answer played flat through
    /// Marlow Tower's brick (74 dB in the floor 3 corridor, Cody's log).
    /// </summary>
    [Fact]
    public void A_playing_train_voice_is_handed_the_path_of_where_it_is_now()
    {
        var h = ATrainInView();
        Assert.True(h.TickUntil(() => h.Mixer.Latest.Values.Any(e => e.PhysicalKey.StartsWith("rail:", StringComparison.Ordinal)), 60),
                    "the train was never voiced");
        int voice = VoiceOf(h);
        Assert.True(h.TickUntil(() => h.Mixer.HasCarried(voice), 60), "the train's voice was never handed a path");
        float open = h.Mixer.LastCarried(voice).EqMid;
        h.AddWall(Wall, new Vector3(0f, 3f, 6f), new Vector3(40f, 8f, 0.35f));
        bool walled = h.TickUntil(() => h.Mixer.LastCarried(voice).EqMid < 0.1f * open, 300, TimeSpan.FromSeconds(5));
        _o.WriteLine($"voice {voice}: mid {Db(open):F1} dB in view, {Db(h.Mixer.LastCarried(voice).EqMid):F1} dB behind 35 cm of brick");
        Assert.False(h.Mixer.HasPath(voice), "the train's voice had a second writer");
        Assert.True(walled, "the wall never reached the playing voice");
    }
}

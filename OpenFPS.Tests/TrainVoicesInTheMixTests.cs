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

    private static bool IsTrainVoice(int id) => id <= ClientAudioSystem.TrainVoiceBase && id > ClientAudioSystem.TrainVoiceBase - 10_000;

    [Fact]
    public void A_fifty_wagon_freight_passing_is_a_handful_of_voices_and_none_comes_and_goes()
    {
        var layout = TrainLayout.Sources(TrainProfile.ByName("freight"));
        const string train = "freight_test";
        var velocity = new Vector3(18f, 0f, 0f);
        var ear = new Vector3(0f, 0f, 12f);
        double seconds = 15.0;
        var r = Replay.RunScript(r =>
        {
            float head = 120f;
            for (int i = 0; i < layout.Count; i++)
                r.RailSource(FirstEntity + i, $"rail:freight/{train}/{i}", layout[i].LevelDb,
                             new Vector3(head - layout[i].AlongMetres, layout[i].HeightMetres, 0f), velocity);
            int frames = (int)(seconds * ClientAudioSystem.UpdateHz);
            for (int f = 0; f < frames; f++)
            {
                head += velocity.X / (float)ClientAudioSystem.UpdateHz;
                for (int i = 0; i < layout.Count; i++)
                    r.Move(FirstEntity + i, new Vector3(head - layout[i].AlongMetres, layout[i].HeightMetres, 0f), velocity);
                // Half way through, the horn and bell for a crossing.
                if (f == frames / 2) r.Audio.WorldAudio.TrainSignalReceived!("freight/" + train, new[] { 3f, 1f, 3f, 1f, 1f, 1f, 8f }, 18f);
                r.Listener(ear, Vector3.Zero, 0f);
                r.Tick();
            }
        });

        if (Environment.GetEnvironmentVariable("OPENFPS_TRAIN_MIX_DUMP") is { Length: > 0 } dump)
            File.WriteAllLines(dump, r.Mixer.Lines.Where(l => l.Contains("PlanTrain") || l.Contains(" -23000")));
        var playing = new HashSet<int>();
        int most = 0, trainStarts = 0, sourceVoices = 0, signalSeen = 0;
        foreach (var line in r.Mixer.Lines)
        {
            var parts = line.Split(' ', 4);
            if (parts.Length < 3 || !int.TryParse(parts[2], out int id)) continue;
            if (parts[1] == "Play")
            {
                if (id >= FirstEntity && id < FirstEntity + layout.Count) sourceVoices++;
                if (!IsTrainVoice(id)) continue;
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
        Assert.True(trainStarts <= TrainVoicing.Slots + 4, $"{trainStarts} starts in {seconds:F0} s: voices are coming and going");
        Assert.Equal(1, signalSeen);
    }
}

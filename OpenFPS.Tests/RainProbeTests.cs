using System.Globalization;
using System.Numerics;
using OpenFPS.Audio;
using OpenFPS.Audio.Core;
using OpenFPS.Audio.Fmod;
using OpenFPS.Environment;
using OpenFPS.Common.Networking;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// Rain behind walls is heard by the acoustic worker's answer (docs/SPECIAL_CASES_AUDIT.md, P6 and S14): the
/// loudest surface of each ring patch is asked of the worker every frame under a probe of the patch's own, as a
/// car, a beacon or a perched bird is, and the patch takes that answer. The hand-rolled tracer placed every
/// patch alone, on terms of its own: it and Steam Audio have disagreed by 48 dB on one car.
/// </summary>
public class RainProbeTests
{
    private readonly ITestOutputHelper _o;
    public RainProbeTests(ITestOutputHelper o) => _o = o;

    private const float Floor = 9f;

    /// <summary>A brick room with a concrete roof on the third floor of a block, in 8 mm/h of rain: nothing of
    /// the street is in plain view, so every patch is behind the walls.</summary>
    private static void ARoomInTheRain(Replay r)
    {
        r.Wall(9001, new Vector3(0f, -0.25f, 0f), new Vector3(200f, 0.5f, 200f), "Asphalt");
        r.Wall(9002, new Vector3(0f, Floor - 0.15f, 0f), new Vector3(6.6f, 0.3f, 6.6f), "Concrete");
        r.Wall(9003, new Vector3(0f, Floor + 3.15f, 0f), new Vector3(6.6f, 0.3f, 6.6f), "Concrete");
        r.Wall(9004, new Vector3(-3.15f, Floor + 1.5f, 0f), new Vector3(0.3f, 3f, 6.6f), "Brick");
        r.Wall(9005, new Vector3(3.15f, Floor + 1.5f, 0f), new Vector3(0.3f, 3f, 6.6f), "Brick");
        r.Wall(9006, new Vector3(0f, Floor + 1.5f, -3.15f), new Vector3(6.6f, 3f, 0.3f), "Brick");
        r.Wall(9007, new Vector3(0f, Floor + 1.5f, 3.15f), new Vector3(6.6f, 3f, 0.3f), "Brick");
        r.Wall(9008, new Vector3(0f, (Floor - 0.3f) / 2f, 0f), new Vector3(6.6f, Floor - 0.3f, 6.6f), "Brick");
        r.World.UpdateAtmosphere(new WorldStateUpdate
        {
            Temperature = 12f, Humidity = 0.9f, AirPressure = 1008f, AirAbsorptionMultiplier = 1f,
            PrecipitationIntensity = 0.5f, RainRateMmPerHour = 8f, PrecipitationKind = (int)PrecipitationKind.Rain,
            RainMedianDropMm = 1.4f,
        });
    }

    private static readonly Vector3 Feet = new(0f, Floor, 0f);

    /// <summary>The ring patches behind walls in the rain's last survey.</summary>
    private static List<int> BehindWalls(Replay r)
    {
        var slots = new List<int>();
        if (r.Audio.World.Rain.LastSurvey is not { } survey) return slots;
        for (int s = 0; s < RainFeeds.Slots; s++)
            if (s != RainSurvey.OverheadSlot && survey.Patches[s] != null && !survey.Direct[s]) slots.Add(s);
        return slots;
    }

    private static int Probe(Replay r, int slot)
        => r.Audio.World.Voices.TryIdOf(VoiceKind.Probe, VoiceRegistry.NoEntity, VoiceRegistry.RainProbe(slot), out int id) ? id : 0;

    /// <summary>
    /// Each patch behind the walls has a probe of its own from the frame it is surveyed, no voice sounds under
    /// the probe, and from the next frame on the worker's answer for it is the one taken, every frame.
    /// </summary>
    [Fact]
    public void A_patch_behind_walls_is_asked_of_the_worker_under_its_own_probe_and_takes_its_answer()
    {
        var problems = new List<string>();
        int surveyedFrames = 0, slotFrames = 0, answered = 0;
        var seen = new SortedSet<int>();
        var probes = new HashSet<int>();
        Replay.RunScript(r =>
        {
            ARoomInTheRain(r);
            for (int f = 0; f < 90; f++)
            {
                r.Listener(Feet, Vector3.Zero, 0f);
                int before = r.Audio.World.Rain.AnsweredByWorker;
                var asked = BehindWalls(r);          // surveyed before this frame: asked in it
                r.Tick();
                if (asked.Count == 0) continue;
                surveyedFrames++;
                slotFrames += asked.Count;
                foreach (int s in asked)
                {
                    seen.Add(s);
                    int probe = Probe(r, s);
                    if (probe == 0) { problems.Add($"frame {r.Frame}: {RainSurvey.SlotName(s)} has no probe"); continue; }
                    probes.Add(probe);
                    if (r.Mixer.IsPlaying(probe)) problems.Add($"frame {r.Frame}: a voice sounds under {RainSurvey.SlotName(s)}'s probe {probe}");
                }
                // Asked in one frame, answered at its end (the replay's worker), taken in the next.
                if (surveyedFrames > 1 && r.Audio.World.Rain.AnsweredByWorker - before != asked.Count)
                    problems.Add($"frame {r.Frame}: {r.Audio.World.Rain.AnsweredByWorker - before} of {asked.Count} patches took the worker's answer");
            }
            answered = r.Audio.World.Rain.AnsweredByWorker;
        });
        _o.WriteLine($"{seen.Count} patches behind the walls ({string.Join(", ", seen.Select(RainSurvey.SlotName))}), {probes.Count} probes; "
                   + $"{answered} answers taken over {slotFrames} patch-frames in {surveyedFrames} frames");
        Assert.True(problems.Count == 0, string.Join("\n", problems.Take(12)));
        Assert.True(seen.Count >= 4, $"only {seen.Count} patches were behind the walls: little was checked");
        // A probe each, none shared.
        Assert.Equal(seen.Count, probes.Count);
        Assert.True(answered >= slotFrames - seen.Count, $"{answered} answers for {slotFrames} patch-frames");
    }

    /// <summary>
    /// The stored rain scenario (traffic_rain: a wall and a porch at a crossing) does not show any of this, and
    /// its stream does not change: every patch there is in plain view, so none asks and no probe is given.
    /// </summary>
    [Fact]
    public void In_the_stored_rain_scenario_every_patch_is_in_plain_view_and_none_asks()
    {
        int patchFrames = 0, behind = 0, answered = -1;
        Replay.RunScript(r =>
        {
            r.AfterTick = x =>
            {
                if (x.Audio.World.Rain.LastSurvey is not { } survey) return;
                patchFrames += survey.Patches.Count(p => p != null);
                behind += BehindWalls(x).Count;
            };
            Replay.Scenarios["traffic_rain"](r);
            answered = r.Audio.World.Rain.AnsweredByWorker;
            for (int s = 0; s < RainFeeds.Slots; s++) Assert.Equal(0, Probe(r, s));
        });
        _o.WriteLine($"traffic_rain: {patchFrames} patch-frames, {behind} behind walls, {answered} answers taken");
        Assert.True(patchFrames > 0, "it did not rain");
        Assert.Equal(0, behind);
        Assert.Equal(0, answered);
    }

    /// <summary>
    /// The patch is placed by what was answered: behind 30 cm of brick, well blocked, and the same with the
    /// worker as with the tracer alone here, since without Steam Audio the worker's answer is the tracer's. So
    /// the tracer stays a true stand-in for the frames before an answer comes.
    /// </summary>
    [Fact]
    public void The_patch_is_as_blocked_by_the_workers_answer_as_by_the_tracers_here()
    {
        Dictionary<int, float> Occlusions(bool worker)
        {
            var voices = new Dictionary<int, int>();
            var run = Replay.RunScript(r =>
            {
                ARoomInTheRain(r);
                if (!worker) r.Audio.World.Rain.Worker = null;
                for (int f = 0; f < 60; f++)
                {
                    r.Listener(Feet, Vector3.Zero, 0f);
                    r.Tick();
                }
                foreach (int s in BehindWalls(r)) voices[s] = r.Audio.World.Voices.IdFor(VoiceKind.Rain, s, 0);
                Assert.Equal(worker, r.Audio.World.Rain.AnsweredByWorker > 0);
            });
            var expanded = EmitterStreamReplayTests.Expand(run.Mixer.Lines);
            var heard = new Dictionary<int, float>();
            foreach (var (slot, voice) in voices)
            {
                string? last = expanded.LastOrDefault(l => l.Contains($" Place {voice} ") || l.Contains($" Play {voice} "));
                if (last == null) continue;
                int at = last.IndexOf(" Occlusion=", StringComparison.Ordinal) + " Occlusion=".Length;
                int end = last.IndexOf(' ', at);
                heard[slot] = float.Parse(last[at..(end < 0 ? last.Length : end)], CultureInfo.InvariantCulture);
            }
            return heard;
        }
        var withWorker = Occlusions(worker: true);
        var tracerAlone = Occlusions(worker: false);
        _o.WriteLine("blocked, with the worker: " + string.Join(", ", withWorker.Select(kv => $"{RainSurvey.SlotName(kv.Key)} {kv.Value:F3}")));
        Assert.True(withWorker.Count >= 4, $"only {withWorker.Count} patches sounded");
        Assert.Equal(tracerAlone.Keys.OrderBy(k => k), withWorker.Keys.OrderBy(k => k));
        foreach (var (slot, occlusion) in withWorker)
        {
            Assert.True(occlusion > 0.5f, $"{RainSurvey.SlotName(slot)} is barely behind the wall ({occlusion:F2})");
            Assert.Equal(tracerAlone[slot], occlusion, 4);
        }
    }
}

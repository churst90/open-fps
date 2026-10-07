using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Server.Systems;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>People in the street talking: the catalogue, which lines fit when, and who says them.</summary>
public class PedestrianSpeechTests
{
    private static readonly SpeechConditions Noon = new(12.5f, 20f, 0f, WeatherType.Clear);
    private static readonly SpeechConditions Night = new(21f, 15f, 0f, WeatherType.Clear);

    /// <summary>Always rolls the lowest number, so every "sometimes" happens.</summary>
    private sealed class Yes : Random
    {
        public override double NextDouble() => 0.0;
        public override int Next(int maxValue) => 0;
    }

    /// <summary>Always rolls the highest, so no "sometimes" happens.</summary>
    private sealed class No : Random
    {
        public override double NextDouble() => 0.999;
        public override int Next(int maxValue) => maxValue - 1;
    }

    [Fact]
    public void The_catalogue_ships_the_approved_voices_and_the_clones_chosen()
    {
        // Cody's set on 2026-09-27, and glenn, louis, steve and two children on 2026-09-28: twenty-three
        // grown-ups who walk the streets, two children for the schools to come, and five angry drivers.
        // And alex on 2026-10-06: one homeless man, who is a character and not one of the crowd.
        Assert.Equal(31, Speech.Voices.Count);
        Assert.Equal(new[] { "alec", "alex", "ben", "camel", "ethan", "fluke", "glenn", "jimdale", "joeb", "joel", "louis", "presidents_kid", "seanterry", "steve", "tim", "tyler" },
                     Speech.Takes.Where(t => t.Kind == "cloned").Select(t => t.Voice).Distinct().OrderBy(v => v));
        Assert.Equal(24, Speech.VoicesWith("greet").Count);
        Assert.DoesNotContain("alex", PedestrianSpeech.WalkerVoices);
        Assert.DoesNotContain("ethan", PedestrianSpeech.WalkerVoices);
        Assert.Contains("angry_vito", Speech.VoicesWith("yell"));
        Assert.DoesNotContain("angry_vito", PedestrianSpeech.WalkerVoices);
    }

    [Fact]
    public void Every_line_the_street_can_ask_for_exists_in_every_voice()
    {
        // Every list, under every condition that changes one, so a misspelt line name fails here and
        // not as a person who silently says nothing.
        var lines = new HashSet<string>();
        foreach (float hour in new[] { 3f, 9f, 14f, 19f })
            foreach (float temp in new[] { 0f, 20f, 32f })
                foreach (var weather in Enum.GetValues<WeatherType>())
                {
                    var c = new SpeechConditions(hour, temp, 0.1f, weather);
                    lines.UnionWith(StreetLines.Greetings(c));
                    lines.UnionWith(StreetLines.Partings(c));
                    lines.UnionWith(StreetLines.PhoneTalk(c));
                    for (int i = 0; i < 40; i++)
                    {
                        var (a, b) = StreetLines.Exchange(c, new Random(i));
                        lines.UnionWith(a.Lines); lines.UnionWith(b.Lines);
                    }
                }
        lines.UnionWith(StreetLines.Sorry);
        lines.UnionWith(StreetLines.Annoyed);
        lines.UnionWith(StreetLines.Help);
        lines.UnionWith(StreetLines.Answer);
        lines.UnionWith(StreetLines.Listening);
        lines.UnionWith(StreetLines.RingOff);

        lines.UnionWith(StreetLines.Startled);
        lines.UnionWith(StreetLines.Impatient);
        lines.UnionWith(StreetLines.Waiting);
        lines.UnionWith(StreetLines.AtSomebodyInTheRoad);
        lines.UnionWith(StreetLines.AtAParker);

        // Every line is somebody's. Not everybody has every line (the three voices from the first set
        // have 86, the rest 126, and the drivers only yell), and a person only picks what they have.
        foreach (var line in lines)
            Assert.True(Speech.Voices.Any(v => Speech.Find(v, line) != null), $"nobody has {line}");
        // ...but everybody on foot can greet, apologise, say goodbye and answer the phone: from the
        // named lines, or, for a voice recorded later, from its own lines in the same category.
        foreach (var voice in PedestrianSpeech.WalkerVoices.Distinct())
            foreach (var (list, cats) in new (IReadOnlyList<string>, string[])[]
            {
                (StreetLines.Greetings(Noon), new[] { "greet" }), (StreetLines.Sorry, new[] { "polite" }),
                (StreetLines.Partings(Noon), new[] { "bye" }), (StreetLines.Answer, new[] { "phone_answer" }),
            })
                Assert.True(StreetLines.Candidates(voice, list, cats).Count > 0, $"{voice} has nothing for {cats[0]}");
    }

    [Fact]
    public void Lines_filled_in_from_a_category_name_no_time_or_weather()
    {
        Assert.True(StreetLines.AnyTime("Hey, how's it going?"));
        Assert.False(StreetLines.AnyTime("Good morning."));
        Assert.False(StreetLines.AnyTime("Looks like rain."));
        foreach (var voice in PedestrianSpeech.WalkerVoices.Distinct())
            foreach (var line in StreetLines.Candidates(voice, Array.Empty<string>(), new[] { "greet", "bye" }))
                Assert.True(StreetLines.AnyTime(Speech.Find(voice, line)!.Text), $"{voice}: {line}");
    }

    [Fact]
    public void Every_scripted_call_and_conversation_is_recorded_line_for_line()
    {
        Assert.True(Speech.Calls.Count >= 30, $"{Speech.Calls.Count} calls");
        foreach (var call in Speech.Calls)
            foreach (var (voice, turns) in call.Voices)
                foreach (var t in turns.Where(t => t.Line != null))
                    Assert.True(Speech.Find(voice, t.Line!) != null, $"{call.Name}: {voice} has no {t.Line}");
        Assert.True(Speech.Conversations.Count >= 40, $"{Speech.Conversations.Count} conversations");
        foreach (var c in Speech.Conversations)
            foreach (var t in c.Turns.Where(t => t.Line != null))
                Assert.True(Speech.Find(t.Who == "A" ? c.A : c.B, t.Line!) != null, $"{c.Name}: {t.Who} has no {t.Line}");
    }

    [Fact]
    public void Every_take_in_the_catalogue_has_its_recording()
    {
        string root = SoundsRoot();
        foreach (var t in Speech.Takes)
            Assert.True(File.Exists(Path.Combine(root, "VOICES", t.Voice, t.Line + ".ogg")), $"{t.Voice}/{t.Line}.ogg");
    }

    /// <summary>
    /// "Have a good day." was said at night: Partings leaves it out after dark, but Candidates added
    /// every "bye" line AnyTime let through, and AnyTime knew "nice day" and "beautiful day" but not
    /// "good day". "How's your day going?" came back into the greetings the same way.
    /// </summary>
    [Fact]
    public void Nobody_wishes_you_a_good_day_at_night()
    {
        var day = new System.Text.RegularExpressions.Regex(@"\bday\b|\bweather\b",
                                                           System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        foreach (var voice in PedestrianSpeech.WalkerVoices.Distinct())
        {
            var said = StreetLines.Candidates(voice, StreetLines.Partings(Night), new[] { "bye" })
                .Concat(StreetLines.Candidates(voice, StreetLines.Greetings(Night), new[] { "greet" }))
                .Concat(StreetLines.Candidates(voice, Array.Empty<string>(), new[] { "smalltalk_open" }))
                .Select(line => Speech.Find(voice, line)!.Text);
            Assert.DoesNotContain(said, text => day.IsMatch(text));
        }
        Assert.False(StreetLines.AnyTime("Have a good day."));
        Assert.False(StreetLines.AnyTime("How's your day going?"));
        Assert.False(StreetLines.AnyTime("Crazy weather lately, huh?"));
        Assert.True(StreetLines.AnyTime("Long time no see!"));
    }

    [Fact]
    public void Lines_that_need_something_true_are_only_offered_when_it_is()
    {
        Assert.DoesNotContain("greet_good_morning", StreetLines.Greetings(Night));
        Assert.Contains("greet_good_evening", StreetLines.Greetings(Night));
        Assert.Contains("greet_good_afternoon", StreetLines.Greetings(Noon));
        Assert.Contains("greet_beautiful_day_huh", StreetLines.Greetings(Noon));
        Assert.DoesNotContain("greet_beautiful_day_huh", StreetLines.Greetings(Noon with { Weather = WeatherType.Rain }));
        Assert.DoesNotContain("greet_cold_out_here_today", StreetLines.Greetings(Noon));
        Assert.Contains("greet_cold_out_here_today", StreetLines.Greetings(Noon with { TemperatureC = 2f }));
        Assert.Contains("greet_looks_like_rain", StreetLines.Greetings(Noon with { Weather = WeatherType.Rain, Precipitation = 0.1f }));
        Assert.DoesNotContain("greet_looks_like_rain", StreetLines.Greetings(Noon with { Weather = WeatherType.Rain, Precipitation = 0.8f }));
        Assert.Contains("bye_have_a_good_night", StreetLines.Partings(Night));
        Assert.DoesNotContain("bye_have_a_good_night", StreetLines.Partings(Noon));
    }

    [Fact]
    public void Speech_is_placed_at_a_talkers_level_from_the_mouth()
    {
        Assert.Equal(90.35f, Speech.LevelDb(Speech.NormalDb), 2);
        Assert.True(Speech.TryParseKey(Speech.Key("maria", "greet_hi"), out var id));
        Assert.Equal("VOICES/maria/greet_hi", id);
        Assert.False(Speech.TryParseKey("weapon:akm", out _));
    }

    // ── Behaviour ───────────────────────────────────────────────────────────────────────────────

    private static (World World, Entity Walker) Street(Vector3 walkerAt, float heading)
    {
        var world = World.Create();
        var walker = world.Create(
            new Transform { Position = walkerAt, Rotation = Quaternion.CreateFromYawPitchRoll(heading, 0f, 0f) },
            new Velocity { Linear = Vector3.Transform(Vector3.UnitZ, Quaternion.CreateFromYawPitchRoll(heading, 0f, 0f)) * 1.3f },
            new Pedestrian { Voice = "linda" });
        return (world, walker);
    }

    private static Entity Player(World world, Vector3 at)
        => world.Create(new Transform { Position = at }, new Velocity(), new PlayerComponent { Username = "p" });

    private static List<(int Id, string Label, TransientSound Sound)> Run(PedestrianSpeech speech, World world,
        double from, double to, SpeechConditions c, Action<double>? each = null)
    {
        var said = new List<(int, string, TransientSound)>();
        for (double t = from; t < to; t += 0.05)
        {
            each?.Invoke(t);
            speech.Update("test", world, t, c, (id, label, s) => said.Add((id, label, s)));
        }
        return said;
    }

    [Fact]
    public void A_walker_says_hello_to_a_player_they_are_walking_towards()
    {
        var (world, walker) = Street(Vector3.Zero, 0f);
        Player(world, new Vector3(0.8f, 0f, 3f));
        var said = Run(new PedestrianSpeech(new Yes()), world, 0, 0.5, Noon);

        var (id, label, s) = Assert.Single(said);
        Assert.Equal(walker.Id, id);
        Assert.StartsWith("voice:linda/greet_", s.SynthKey);
        Assert.StartsWith("speech: ", label);
        Assert.Equal(Speech.MouthHeight, s.Position.Y, 1);
        Assert.Equal(Speech.LevelDb(Speech.NormalDb), s.LevelDb, 2);
    }

    [Fact]
    public void Nobody_greets_the_back_of_someone_they_cannot_see()
    {
        var (world, _) = Street(Vector3.Zero, 0f);
        Player(world, new Vector3(0.5f, 0f, -3f));
        Assert.Empty(Run(new PedestrianSpeech(new Yes()), world, 0, 2, Noon));
    }

    [Fact]
    public void A_hello_is_said_once_per_meeting_not_every_tick()
    {
        var (world, _) = Street(Vector3.Zero, 0f);
        Player(world, new Vector3(0.8f, 0f, 3f));
        // Under five seconds: with every roll coming up "yes" a phone call starts at five, and answering
        // it is a "hello" too.
        var said = Run(new PedestrianSpeech(new Yes()), world, 0, 4.9, Noon);
        Assert.Single(said, x => x.Sound.SynthKey.Contains("/greet_"));
    }

    [Fact]
    public void Some_people_say_nothing()
    {
        var (world, _) = Street(Vector3.Zero, 0f);
        Player(world, new Vector3(0.8f, 0f, 3f));
        Assert.Empty(Run(new PedestrianSpeech(new No()), world, 0, 5, Noon));
    }

    [Fact]
    public void Walking_into_someone_gets_an_apology_and_no_hello_after_it()
    {
        var (world, _) = Street(Vector3.Zero, 0f);
        Player(world, new Vector3(0.3f, 0f, 0.3f));
        var said = Run(new PedestrianSpeech(new Yes()), world, 0, 5, Noon);
        Assert.Contains(said, x => x.Sound.SynthKey.Contains("/polite_"));
        Assert.DoesNotContain(said, x => x.Sound.SynthKey.Contains("/greet_"));
    }

    [Fact]
    public void A_goodbye_comes_as_you_part_not_while_you_approach()
    {
        var (world, walker) = Street(Vector3.Zero, 0f);
        var player = Player(world, new Vector3(0.8f, 0f, 3.5f));
        // The player walks south past the walker, who stands still.
        world.Get<Velocity>(walker).Linear = Vector3.Zero;
        var said = Run(new PedestrianSpeech(new Yes()), world, 0, 8, Noon, t =>
            world.Get<Transform>(player).Position = new Vector3(0.8f, 0f, 3.5f - 1.3f * (float)t));

        int hello = said.FindIndex(x => x.Sound.SynthKey.Contains("/greet_"));
        int bye = said.FindIndex(x => x.Sound.SynthKey.Contains("/bye_"));
        Assert.True(hello >= 0 && bye > hello, string.Join(", ", said.Select(x => x.Sound.SynthKey)));
    }

    [Fact]
    public void Two_strangers_passing_can_greet_each_other_one_after_the_other()
    {
        var world = World.Create();
        var a = world.Create(new Transform { Position = Vector3.Zero, Rotation = Quaternion.Identity },
                             new Velocity(), new Pedestrian { Voice = "linda" });
        var b = world.Create(new Transform { Position = new Vector3(0.5f, 0f, 3f), Rotation = Quaternion.CreateFromYawPitchRoll(MathF.PI, 0f, 0f) },
                             new Velocity(), new Pedestrian { Voice = "frank" });
        var said = Run(new PedestrianSpeech(new No()), world, 0, 0.2, Noon);
        Assert.Empty(said);   // "No" never takes the chance

        world = World.Create();
        a = world.Create(new Transform { Position = Vector3.Zero, Rotation = Quaternion.Identity },
                         new Velocity(), new Pedestrian { Voice = "linda" });
        b = world.Create(new Transform { Position = new Vector3(0.5f, 0f, 3f), Rotation = Quaternion.CreateFromYawPitchRoll(MathF.PI, 0f, 0f) },
                         new Velocity(), new Pedestrian { Voice = "frank" });
        // Nobody within earshot: nothing is said.
        Player(world, new Vector3(0f, 0f, 300f));
        Assert.Empty(Run(new PedestrianSpeech(new Yes()), world, 0, 0.2, Noon));

        // Somebody across the street, too far to be greeted themselves.
        Player(world, new Vector3(20f, 0f, 1f));
        said = Run(new PedestrianSpeech(new Yes()), world, 0, 0.2, Noon);
        Assert.Equal(2, said.Count);
        Assert.NotEqual(said[0].Id, said[1].Id);
        Assert.True(said[1].Sound.DelaySeconds > said[0].Sound.DelaySeconds);
    }

    [Fact]
    public void Somebody_with_stories_tells_one_on_the_phone_now_and_then()
    {
        var world = World.Create();
        world.Create(new Transform { Position = Vector3.Zero, Rotation = Quaternion.Identity },
                     new Velocity(), new Pedestrian { Voice = "joel" });
        Player(world, new Vector3(0f, 0f, -20f));     // in earshot, behind them, so no hello
        // Half the calls are recorded calls played through, which have no story in them: an hour.
        var said = Run(new PedestrianSpeech(new Random(7)), world, 0, 3600, Noon);
        Assert.Contains(said, x => x.Sound.SynthKey.Contains("/story_"));
        Assert.All(said.Where(x => x.Sound.SynthKey.Contains("/story_")), x => Assert.True(x.Sound.DecaySeconds > 15f));
    }

    /// <summary>A story that happened "this morning" is not told on the phone at night.</summary>
    [Fact]
    public void A_phone_story_from_this_morning_is_not_told_at_night()
    {
        var world = World.Create();
        world.Create(new Transform { Position = Vector3.Zero, Rotation = Quaternion.Identity },
                     new Velocity(), new Pedestrian { Voice = "joel" });
        Player(world, new Vector3(0f, 0f, -20f));
        var said = Run(new PedestrianSpeech(new Random(7)), world, 0, 4 * 3600, Night)
            .Where(x => x.Sound.SynthKey.Contains("/story_"))
            .Select(x => Speech.Find("joel", x.Sound.SynthKey[(x.Sound.SynthKey.IndexOf('/') + 1)..])!.Text)
            .ToList();
        Assert.NotEmpty(said);
        Assert.DoesNotContain(said, text => text.Contains("this morning", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Somebody muttering to themselves in the morning does not think about dinner.</summary>
    [Fact]
    public void A_muttered_remark_fits_the_hour()
    {
        var morning = new SpeechConditions(9f, 20f, 0f, WeatherType.Clear);
        var world = World.Create();
        world.Create(new Transform { Position = Vector3.Zero, Rotation = Quaternion.Identity },
                     new Velocity(), new Pedestrian { Voice = "joel" });
        Player(world, new Vector3(0f, 0f, -10f));     // within earshot of a mutter, behind them
        var said = Run(new PedestrianSpeech(new Random(3)), world, 0, 6 * 3600, morning)
            .Select(x => x.Sound.SynthKey[(x.Sound.SynthKey.IndexOf('/') + 1)..])
            .Where(line => line.StartsWith("mutter_") || line.StartsWith("think_aloud_") || line.StartsWith("read_text_"))
            .ToList();
        Assert.True(said.Count >= 20, $"only {said.Count} remarks in six hours");
        Assert.DoesNotContain(said, line => !HomelessLines.TrueNow(Speech.Find("joel", line)!.Text, morning));
    }

    /// <summary>
    /// Every call ends with a goodbye, and the next one waits. The ring-off used to come only if a
    /// line happened to fall due in the last six seconds of the call; the gap to the next line is up
    /// to ten seconds and more, so about three calls in ten ran past their end and stopped in
    /// silence, and skipped the wait before the next call as well.
    /// </summary>
    [Fact]
    public void Every_phone_call_ends_with_a_goodbye_and_the_next_one_waits()
    {
        // Linda has no recorded calls, no voicemail and no stories: every call she takes is an
        // ordinary one, picked up with a hello and rung off with a goodbye.
        var world = World.Create();
        world.Create(new Transform { Position = Vector3.Zero, Rotation = Quaternion.Identity },
                     new Velocity(), new Pedestrian { Voice = "linda" });
        Player(world, new Vector3(0f, 0f, -20f));     // in earshot, behind them, so no hello to the player
        Assert.Empty(Speech.CallsFor("linda"));

        var ringOff = new HashSet<string>(StreetLines.RingOff);
        var speech = new PedestrianSpeech(new Random(11));
        var said = new List<(double At, string Line)>();
        for (double t = 0; t < 6 * 3600; t += 0.05)
            speech.Update("test", world, t, Noon, (_, _, s) =>
                said.Add((t + s.DelaySeconds, s.SynthKey[(s.SynthKey.IndexOf('/') + 1)..])));

        var answers = Enumerable.Range(0, said.Count).Where(i => said[i].Line.StartsWith("greet_")).ToList();
        Assert.True(answers.Count >= 40, $"only {answers.Count} calls in six hours");
        int silent = 0, early = 0;
        foreach (int i in answers.Skip(1))
        {
            var before = said[i - 1];
            if (!ringOff.Contains(before.Line)) silent++;
            else if (said[i].At - before.At < 60) early++;
        }
        Assert.True(silent == 0, $"{silent} of {answers.Count - 1} calls ended without a goodbye");
        Assert.True(early == 0, $"{early} calls started again within a minute of ringing off");
    }

    [Fact]
    public void A_talker_is_duller_and_quieter_behind_than_in_front()
    {
        var front = Speech.Directivity(Vector3.UnitZ, new Vector3(0f, 0f, 5f));
        var side = Speech.Directivity(Vector3.UnitZ, new Vector3(5f, 0f, 0f));
        var behind = Speech.Directivity(Vector3.UnitZ, new Vector3(0f, 0f, -5f));
        Assert.Equal(1f, front.High, 3);
        Assert.Equal(Speech.BehindHighDb, 20f * MathF.Log10(behind.High), 1);
        Assert.Equal(Speech.BehindLowDb, 20f * MathF.Log10(behind.Low), 1);
        Assert.True(side.High < front.High && side.High > behind.High);
        Assert.True(behind.High < behind.Mid && behind.Mid < behind.Low);
    }

    [Fact]
    public void A_street_of_people_gets_different_voices()
    {
        int n = PedestrianSpeech.WalkerVoices.Count;
        var voices = Enumerable.Range(0, n).Select(_ => PedestrianSpeech.NextVoice("voices-test")).ToList();
        Assert.Equal(23, voices.Distinct().Count());
        // Cody's favourites come round twice as often.
        Assert.Equal(2, voices.Count(v => v == "joel"));
        Assert.Equal(1, voices.Count(v => v == "linda"));
    }

    private static string SoundsRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "OpenFPS.Client", "ASSETS", "SOUNDS"))) dir = dir.Parent;
        return dir != null ? Path.Combine(dir.FullName, "OpenFPS.Client", "ASSETS", "SOUNDS")
                           : "/home/cody/external-rescue/Github/open-fps/OpenFPS.Client/ASSETS/SOUNDS";
    }

    /// <summary>
    /// No pedestrian walks through anything solid. The map generator once read a prefab with no IsSolid
    /// as not solid, where the server reads it as solid, and routed the Main Street walks through the
    /// tunnel's concrete sides: a player outside the tunnel heard people walking inside the wall.
    /// Checked at knee and chest height, against the server's own entities.
    /// </summary>
    [Fact]
    public void NoPedestrianWalksThroughAnythingSolid()
    {
        var prefabs = new OpenFPS.Server.Repositories.PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
        var maps = new OpenFPS.Server.Core.MapManager(new OpenFPS.Server.Repositories.MapRepository(Path.Combine(AppContext.BaseDirectory, "maps")), prefabs);
        maps.Initialize();
        Assert.True(maps.TryGetMap("city", out World world, out _, out _, out _));
        Assert.True(maps.TryGetMapData("city", out var data));
        var solids = OpenFPS.Server.Core.EntityDefinitionFactory.StaticDefinitions(world)
            .Where(d => d.Collider.IsSolid && d.Collider.Shape == ColliderShape.Box && !d.Moves)
            .ToList();

        var walks = data.Vehicles!.Where(v => v.Preset == "walker").ToList();
        Assert.True(walks.Count > 200);
        var through = new List<string>();
        foreach (var w in walks)
        {
            float length = Vector3.Distance(w.RoadStart, w.RoadEnd);
            for (float d = 0f; d <= length; d += 0.5f)
            {
                var at = Vector3.Lerp(w.RoadStart, w.RoadEnd, length > 0f ? d / length : 0f);
                foreach (float h in new[] { 0.5f, 1.3f })
                {
                    var p = new Vector3(at.X, h, at.Z);
                    var hit = solids.FirstOrDefault(s => GeometryUtils.IsPointInOBB(p, s.Transform.Position, s.Collider.Size, s.Transform.Rotation));
                    if (hit != null) { through.Add($"{w.Name} at ({p.X:F1}, {p.Z:F1}) inside {hit.Identity.Name} {hit.Material.Material}"); goto nextWalk; }
                }
            }
            nextWalk:;
        }
        Assert.True(through.Count == 0, string.Join("\n", through.Take(10)));
    }
}

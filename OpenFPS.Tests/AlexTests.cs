using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using OpenFPS.Server.Systems;

namespace OpenFPS.Tests;

/// <summary>Alex, the homeless man on the city: his voice, his day, his places and what he says when.</summary>
public class AlexTests
{
    private static readonly SpeechConditions Noon = new(12.5f, 20f, 0f, WeatherType.Clear);
    private static readonly SpeechConditions Night = new(23f, 15f, 0f, WeatherType.Clear);
    private static readonly SpeechConditions ColdNoon = new(12.5f, 2f, 0f, WeatherType.Clear);

    private sealed class Yes : Random
    {
        public override double NextDouble() => 0.0;
        public override int Next(int maxValue) => 0;
    }

    // ── The voice ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Alex_is_a_character_never_a_walker_a_driver_or_a_caller()
    {
        Assert.Contains("alex", Speech.Voices);
        Assert.True(PedestrianSpeech.IsCharacterVoice("alex"));
        Assert.DoesNotContain("alex", PedestrianSpeech.WalkerVoices);
        for (int i = 0; i < 200; i++)
        {
            Assert.NotEqual("alex", PedestrianSpeech.NextVoice("alex-test"));
            Assert.NotEqual("alex", PedestrianSpeech.NextDriverVoice("alex-test"));
        }
        // No phone calls, no yelling from a car, no texts read out.
        Assert.DoesNotContain(Speech.Takes, t => t.Voice == "alex" && (t.Category.StartsWith("phone_") || t.Category == "yell" || t.Category == "read_text"));
        Assert.Empty(Speech.CallsFor("alex"));
        Assert.DoesNotContain(Speech.ConversationPairs, p => p.A == "alex" || p.B == "alex");
        // Nobody else is a character.
        Assert.Equal(new[] { "alex" }, Speech.Voices.Where(PedestrianSpeech.IsCharacterVoice));
    }

    [Fact]
    public void Alex_has_every_one_of_his_categories_and_four_stories()
    {
        foreach (var cat in new[] { "money", "smokes", "weed", "drugs", "food", "ride", "shelter", "thanks", "refused",
                                     "angry", "mutter", "cops", "cars" })
            Assert.True(Speech.LinesOf("alex", "homeless_" + cat).Count >= 10, cat);
        Assert.Equal(4, Speech.LinesOf("alex", "story").Count);
    }

    [Fact]
    public void His_lines_are_true_of_the_hour_and_the_weather()
    {
        Assert.False(HomelessLines.TrueNow("You know a place I can stay tonight?", Noon));
        Assert.True(HomelessLines.TrueNow("You know a place I can stay tonight?", Night));
        Assert.False(HomelessLines.TrueNow("It's cold. It's so fucking cold.", Noon));
        Assert.True(HomelessLines.TrueNow("It's cold. It's so fucking cold.", ColdNoon));
        Assert.False(HomelessLines.TrueNow("You got a water? It's hot as hell out here.", Noon));
        // Not about now: true at any hour.
        Assert.True(HomelessLines.TrueNow("Nope. Not today.", Night));
        Assert.True(HomelessLines.TrueNow("Buy me a hot dog, man.", Noon));
        Assert.True(HomelessLines.TrueNow("Wow. You're cold, man.", Noon));
        Assert.True(HomelessLines.TrueNow("And one morning I'm making coffee", Night));
        Assert.True(HomelessLines.TrueNow("Every night. Every night you guys do this.", Noon));
        // "This morning" is a story told later the same day, not at two in the morning.
        Assert.False(HomelessLines.TrueNow("This lady comes up to me this morning", new(2f, 15f, 0f, WeatherType.Clear)));
        Assert.True(HomelessLines.TrueNow("This lady comes up to me this morning", Noon));
        // Every hour of the day leaves him something to ask and something to mutter.
        for (float h = 0; h < 24; h += 1)
        {
            var c = new SpeechConditions(h, 15f, 0f, WeatherType.Clear);
            Assert.Contains(Speech.LinesOf("alex", "homeless_money"), l => HomelessLines.TrueNow(Speech.Find("alex", l)!.Text, c));
            Assert.Contains(Speech.LinesOf("alex", "homeless_mutter"), l => HomelessLines.TrueNow(Speech.Find("alex", l)!.Text, c));
        }
    }

    [Fact]
    public void He_asks_mostly_for_money_and_for_a_bed_only_when_it_is_night_or_cold()
    {
        var day = Enumerable.Range(0, 2000).Select(i => HomelessLines.AskCategory(Noon, new Random(i))).ToList();
        var night = Enumerable.Range(0, 2000).Select(i => HomelessLines.AskCategory(Night, new Random(i))).ToList();
        Assert.DoesNotContain("homeless_shelter", day);
        Assert.Contains("homeless_shelter", night);
        Assert.Equal("homeless_money", day.GroupBy(c => c).OrderByDescending(g => g.Count()).First().Key);
        Assert.True(day.Count(c => c == "homeless_drugs") < day.Count(c => c == "homeless_weed"));
    }

    // ── His day ─────────────────────────────────────────────────────────────────────────────────

    private static Haunt H(string name, HauntKind kind, float x, float z) => new() { Name = name, Kind = kind, Stand = new Vector3(x, 0f, z) };

    private static readonly List<Haunt> Places = new()
    {
        H("stop", HauntKind.BusStop, 0, 0), H("door", HauntKind.Doorway, 10, 0),
        H("lobby", HauntKind.Lobby, 20, 0), H("square", HauntKind.Square, 30, 0),
    };

    [Fact]
    public void At_night_and_in_the_cold_he_goes_to_a_lobby_and_by_day_to_the_street()
    {
        double Share(SpeechConditions c, HauntKind k)
            => Enumerable.Range(0, 2000).Count(i => CharacterSystem.Choose(Places, null, null, c, new Random(i)).Kind == k) / 2000.0;
        Assert.True(Share(Night, HauntKind.Lobby) > 0.55);
        Assert.True(Share(ColdNoon, HauntKind.Lobby) > 0.55);
        Assert.True(Share(new(12f, 20f, 0.6f, WeatherType.Rain), HauntKind.Lobby) > 0.55);
        Assert.True(Share(Noon, HauntKind.Lobby) < 0.1);
        Assert.True(Share(Noon, HauntKind.BusStop) + Share(Noon, HauntKind.Doorway) > 0.6);
    }

    [Fact]
    public void He_never_chooses_where_he_already_is_and_prefers_somewhere_near()
    {
        for (int i = 0; i < 500; i++)
            Assert.NotSame(Places[1], CharacterSystem.Choose(Places, Places[1], Vector2.Zero, Noon, new Random(i)));
        var far = new List<Haunt> { H("near", HauntKind.Doorway, 10, 0), H("far", HauntKind.Doorway, 900, 0) };
        int near = Enumerable.Range(0, 1000).Count(i => CharacterSystem.Choose(far, null, Vector2.Zero, Noon, new Random(i)).Name == "near");
        Assert.True(near > 750);
    }

    [Fact]
    public void He_stays_minutes_and_longest_in_a_lobby_on_a_cold_night()
    {
        for (int i = 0; i < 200; i++)
            foreach (HauntKind k in Enum.GetValues<HauntKind>())
            {
                double s = CharacterSystem.LingerSeconds(k, Noon, new Random(i));
                Assert.InRange(s, 120, 600);
            }
        double lobbyNight = Enumerable.Range(0, 200).Average(i => CharacterSystem.LingerSeconds(HauntKind.Lobby, Night, new Random(i)));
        double lobbyDay = Enumerable.Range(0, 200).Average(i => CharacterSystem.LingerSeconds(HauntKind.Lobby, Noon, new Random(i)));
        Assert.True(lobbyNight > 2 * lobbyDay);
    }

    [Fact]
    public void His_day_is_the_same_on_the_same_server_and_another_day_differs()
    {
        Assert.Equal(CharacterSystem.Seed(1, "city", "Alex", 40, 3), CharacterSystem.Seed(1, "city", "Alex", 40, 3));
        Assert.NotEqual(CharacterSystem.Seed(1, "city", "Alex", 40, 3), CharacterSystem.Seed(1, "city", "Alex", 41, 3));
        Assert.NotEqual(CharacterSystem.Seed(1, "city", "Alex", 40, 3), CharacterSystem.Seed(1, "city", "Alex", 40, 4));
        Assert.NotEqual(CharacterSystem.Seed(1, "city", "Alex", 40, 3), CharacterSystem.Seed(2, "city", "Alex", 40, 3));
    }

    // ── The pavements ───────────────────────────────────────────────────────────────────────────

    /// <summary>A crossroads: two pavements either side of a north-south road and two of an east-west one.</summary>
    private static Pavements Crossroads() => Pavements.Build(new[]
    {
        new Pavements.Strip(new(-8, -100), new(-8, 100), 1.75f, "west"),
        new Pavements.Strip(new(8, -100), new(8, 100), 1.75f, "east"),
        new Pavements.Strip(new(-100, -8), new(100, -8), 1.75f, "south"),
        new Pavements.Strip(new(-100, 8), new(100, 8), 1.75f, "north"),
    });

    [Fact]
    public void A_walk_keeps_to_the_pavements_and_crosses_at_the_corner()
    {
        var net = Crossroads();
        var route = net.Route(new Vector2(-8, -60), new Vector2(8, 60));
        // Along the west pavement, across at the junction, up the east one: every point is on a pavement line.
        foreach (var p in route)
            Assert.True(MathF.Abs(MathF.Abs(p.X) - 8) < 0.01f || MathF.Abs(MathF.Abs(p.Y) - 8) < 0.01f, $"{p} is off the pavements");
        Assert.InRange(Pavements.Length(route), 135f, 137f);   // 52 + 16 + 68: no short cut across the road
    }

    [Fact]
    public void A_walk_steps_round_what_stands_on_the_pavement()
    {
        var net = Crossroads();
        var shelter = new Pavements.Solid(new Vector3(-8, 1.2f, 30), Quaternion.Identity, new Vector3(1.6f, 1.2f, 0.05f));
        var solids = new List<Pavements.Solid> { shelter };
        var route = Pavements.Clear(net.Route(new Vector2(-8, 0), new Vector2(-8, 60)), solids);
        for (int i = 1; i < route.Count; i++)
            Assert.False(Pavements.LineBlocked(solids, route[i - 1], route[i]), $"{route[i - 1]} to {route[i]} walks through it");
        Assert.Equal(new Vector2(-8, 60), route[^1]);
    }

    // ── On the city ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void On_the_city_he_finds_the_bus_stops_the_doorways_the_lobbies_and_the_square()
    {
        var prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
        var maps = new MapManager(new MapRepository(Path.Combine(AppContext.BaseDirectory, "maps")), prefabs);
        maps.Initialize();
        Assert.True(maps.TryGetMap("city", out World world, out _, out var grid, out _));
        Assert.True(maps.TryGetMapData("city", out var data));
        var alex = Assert.Single(data.Characters!);
        Assert.Equal(("Alex", "alex", "homeless"), (alex.Name, alex.Voice, alex.Kind));

        var net = Pavements.Build(Pavements.StripsOf(world));
        var solids = Pavements.SolidsOf(world);
        var haunts = HauntFinder.Find(world, grid, data, net, solids);
        Assert.Equal(2, haunts.Count(h => h.Kind == HauntKind.BusStop));
        foreach (var tower in new[] { "Marlow Tower", "Kestrel House", "Union Building", "Brandt Court", "Selby House" })
        {
            Assert.Contains(haunts, h => h.Kind == HauntKind.Doorway && h.Name.Contains(tower));
            Assert.Contains(haunts, h => h.Kind == HauntKind.Lobby && h.Name.Contains(tower));
        }
        Assert.Contains(haunts, h => h.Kind == HauntKind.Square && h.Name == "Market Square");
        foreach (var h in haunts)
            Assert.False(Pavements.Blocked(solids, new Vector2(h.Stand.X, h.Stand.Z), h.Stand.Y), $"{h.Name} stands him in a wall");

        // From one shelter to the other is a walk of the pavements, crossing Main Street once.
        var a = haunts.First(h => h.Kind == HauntKind.BusStop);
        var b = haunts.Last(h => h.Kind == HauntKind.BusStop);
        var route = Pavements.Clear(net.Route(new Vector2(a.Stand.X, a.Stand.Z), new Vector2(b.Stand.X, b.Stand.Z)), solids);
        float straight = Vector2.Distance(new Vector2(a.Stand.X, a.Stand.Z), new Vector2(b.Stand.X, b.Stand.Z));
        Assert.InRange(Pavements.Length(route), straight, straight * 1.4f + 30f);
        for (int i = 1; i < route.Count - 1; i++)
            Assert.False(Pavements.LineBlocked(solids, route[i - 1], route[i]), $"{route[i - 1]} to {route[i]} walks through something");
    }

    // ── What he says ────────────────────────────────────────────────────────────────────────────

    private static (World World, Entity Alex) Him(Vector3 at)
    {
        var world = World.Create();
        var alex = world.Create(new Transform { Position = at, Rotation = Quaternion.Identity }, new Velocity(),
                                new Pedestrian { Voice = "alex", Pair = "", Character = "Alex" });
        return (world, alex);
    }

    private static Entity Player(World world, Vector3 at)
        => world.Create(new Transform { Position = at }, new Velocity(), new PlayerComponent { Username = "p" });

    private static List<(double T, Speech.Take Take)> Run(PedestrianSpeech speech, World world, double from, double to,
                                                          SpeechConditions c, Action<double>? each = null)
    {
        var said = new List<(double, Speech.Take)>();
        for (double t = from; t < to; t += 0.05)
        {
            each?.Invoke(t);
            double now = t;
            speech.Update("test", world, t, c, (id, label, s) =>
            {
                Assert.True(Speech.TryParseKey(s.SynthKey, out var sound));
                var parts = sound["VOICES/".Length..].Split('/');
                said.Add((now + s.DelaySeconds, Speech.Find(parts[0], parts[1])!));
            });
        }
        return said;
    }

    private static readonly string[] Asks =
        { "homeless_money", "homeless_smokes", "homeless_weed", "homeless_drugs", "homeless_food", "homeless_shelter" };

    [Fact]
    public void He_asks_a_player_who_comes_near_once_and_not_every_second()
    {
        var (world, alex) = Him(Vector3.Zero);
        Player(world, new Vector3(2f, 0f, 1f));
        var said = Run(new PedestrianSpeech(new Random(7)), world, 0, 100, Noon);
        var mine = said.Where(s => s.Take.Voice == "alex").ToList();
        Assert.Equal(1, mine.Count(s => Asks.Contains(s.Take.Category)));
        Assert.True(Asks.Contains(mine[0].Take.Category), mine[0].Take.Category);
        Assert.True(mine[0].T < 1.0);
    }

    [Fact]
    public void He_never_says_two_things_at_once_and_takes_a_breath_between()
    {
        var (world, alex) = Him(Vector3.Zero);
        var player = Player(world, new Vector3(2f, 0f, 1f));
        // A player who comes and goes and stands about for ten minutes, and passers-by.
        for (int k = 0; k < 6; k++)
            world.Create(new Transform { Position = new Vector3(3f, 0f, -20f + 7 * k), Rotation = Quaternion.Identity },
                         new Velocity { Linear = new Vector3(0f, 0f, 1.3f) }, new Pedestrian { Voice = "ben", Pair = "", Character = "" });
        var said = Run(new PedestrianSpeech(new Random(3)), world, 0, 600, Noon, t =>
        {
            world.Get<Transform>(player).Position = (int)(t / 60) % 2 == 0 ? new Vector3(2f, 0f, 1f) : new Vector3(30f, 0f, 1f);
            world.Query(new QueryDescription().WithAll<Transform, Pedestrian>(), (Entity e, ref Transform tr, ref Pedestrian p) =>
            {
                if (p.Character == "") tr.Position = new Vector3(3f, 0f, -20f + (float)((t * 1.3 + e.Id * 7) % 40));
            });
        });
        var mine = said.Where(s => s.Take.Voice == "alex").OrderBy(s => s.T).ToList();
        Assert.True(mine.Count >= 3, $"only {mine.Count} lines");
        for (int i = 1; i < mine.Count; i++)
            Assert.True(mine[i].T >= mine[i - 1].T + mine[i - 1].Take.Seconds - 1e-6,
                        $"\"{mine[i].Take.Text}\" at {mine[i].T:F1} s starts while \"{mine[i - 1].Take.Text}\" is still being said");
        // A presence, not a loop: on average no more than a line every fifteen seconds.
        Assert.True(mine.Count < 600 / 15, $"{mine.Count} lines in ten minutes");
    }

    [Fact]
    public void He_thanks_whoever_hands_him_something()
    {
        var (world, alex) = Him(Vector3.Zero);
        var player = Player(world, new Vector3(1f, 0f, 0f));
        var speech = new PedestrianSpeech(new Random(1));
        Run(speech, world, 0, 20, Noon);
        speech.Given("test", alex.Id, player.Id);
        var said = Run(speech, world, 20, 30, Noon);
        Assert.Contains(said, s => s.Take.Voice == "alex" && s.Take.Category == "homeless_thanks");
    }

    [Fact]
    public void Walking_past_his_ask_gets_something_bitter_said_after_you()
    {
        var (world, alex) = Him(Vector3.Zero);
        var player = Player(world, new Vector3(2f, 0f, 0f));
        var said = Run(new PedestrianSpeech(new Yes()), world, 0, 30, Noon, t =>
        {
            if (t > 5) world.Get<Transform>(player).Position = new Vector3(2f + (float)(t - 5) * 1.4f, 0f, 0f);
        });
        var mine = said.Where(s => s.Take.Voice == "alex").ToList();
        Assert.True(Asks.Contains(mine[0].Take.Category));
        Assert.Contains(mine, s => s.Take.Category == "homeless_refused");
    }

    [Fact]
    public void A_passer_by_he_asks_may_tell_him_no()
    {
        var (world, alex) = Him(Vector3.Zero);
        Player(world, new Vector3(20f, 0f, 0f));            // somebody to hear it, out of asking range
        world.Create(new Transform { Position = new Vector3(2f, 0f, 0f), Rotation = Quaternion.Identity },
                     new Velocity(), new Pedestrian { Voice = "ben", Pair = "", Character = "" });
        var said = Run(new PedestrianSpeech(new Yes()), world, 0, 8, Noon);
        Assert.Contains(said, s => s.Take.Voice == "alex" && Asks.Contains(s.Take.Category));
        Assert.Contains(said, s => s.Take.Voice == "ben" && s.Take.Category == "refuse");
    }

    [Fact]
    public void He_says_nothing_where_no_player_could_hear_it()
    {
        var (world, alex) = Him(Vector3.Zero);
        Player(world, new Vector3(200f, 0f, 0f));
        world.Create(new Transform { Position = new Vector3(2f, 0f, 0f), Rotation = Quaternion.Identity },
                     new Velocity(), new Pedestrian { Voice = "ben", Pair = "", Character = "" });
        Assert.DoesNotContain(Run(new PedestrianSpeech(new Yes()), world, 0, 300, Noon), s => s.Take.Voice == "alex");
    }

    [Fact]
    public void He_argues_with_a_police_car_and_yells_at_a_car_that_passes_close()
    {
        var (world, alex) = Him(Vector3.Zero);
        Player(world, new Vector3(12f, 0f, 0f));
        var police = world.Create(new Transform { Position = new Vector3(-20f, 0f, 0f) }, new Velocity(),
                                  new VehicleComponent { VehicleType = "police_interceptor" });
        var said = Run(new PedestrianSpeech(new Yes()), world, 0, 5, Noon);
        Assert.Contains(said, s => s.Take.Category == "homeless_cops");

        (world, alex) = Him(Vector3.Zero);
        Player(world, new Vector3(12f, 0f, 0f));
        world.Create(new Transform { Position = new Vector3(-3f, 0f, 0f) }, new Velocity { Linear = new Vector3(0f, 0f, 12f) },
                     new VehicleComponent { VehicleType = "i4_economy" });
        said = Run(new PedestrianSpeech(new Yes()), world, 0, 5, Noon);
        Assert.Contains(said, s => s.Take.Category == "homeless_cars");
    }

    [Fact]
    public void A_car_stopped_beside_him_with_a_player_at_it_gets_asked_for_a_ride()
    {
        var (world, alex) = Him(Vector3.Zero);
        world.Create(new Transform { Position = new Vector3(5f, 0f, 0f) }, new Velocity(), new VehicleComponent { VehicleType = "i4_economy" });
        Player(world, new Vector3(6f, 0f, 1.5f));
        var said = Run(new PedestrianSpeech(new Yes()), world, 0, 5, Noon);
        Assert.Equal("homeless_ride", said.First(s => s.Take.Voice == "alex").Take.Category);
    }

    [Fact]
    public void Somebody_who_stays_with_him_hears_a_story()
    {
        var (world, alex) = Him(Vector3.Zero);
        Player(world, new Vector3(1.5f, 0f, 0f));
        var said = Run(new PedestrianSpeech(new Random(5)), world, 0, 60, Noon);
        Assert.Contains(said, s => s.Take.Voice == "alex" && s.Take.Category == "story");
    }
}

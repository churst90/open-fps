using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;

namespace OpenFPS.Server.Systems;

/// <summary>
/// Marks a body as a person with a voice. Server-only: the client never needs it, because what a
/// person says reaches it as a world sound that names the voice.
/// </summary>
public struct Pedestrian
{
    public string Voice;
}

/// <summary>What is true about the world right now that a person might remark on.</summary>
public readonly record struct SpeechConditions(float Hour, float TemperatureC, float Precipitation, WeatherType Weather)
{
    public static SpeechConditions From(in WorldEnvironmentComponent env, WeatherType weather)
        => new(env.GameTime, env.Temperature, env.PrecipitationIntensity, weather);
}

/// <summary>
/// People in the street saying things: hello to someone they pass, sorry to someone they bump,
/// goodbye as they part, and their half of a phone call.
///
/// Every line is said BY a body, from its mouth, as a world sound, so another player standing next to
/// you hears the same greeting from the same place. Which lines are allowed comes from what is true:
/// "Morning" only in the morning by the game clock, "Looks like rain" only when a wet front is coming
/// in, "Cold out here today" only when it is. A line the world cannot make true is not used at all.
/// </summary>
public sealed class PedestrianSpeech
{
    /// <summary>Close enough that a hello is meant for you: a pavement's width and a step.</summary>
    public const float GreetMetres = 4f;
    /// <summary>Shoulders touching. People are not solid to each other here, so this is how close
    /// somebody walked into them.</summary>
    public const float BumpMetres = 0.75f;
    /// <summary>How much of a straight-ahead cone counts as "in front": 80 degrees either side.</summary>
    public const float FacingCos = 0.17f;
    /// <summary>Most people say something to someone they pass; not everybody.</summary>
    public const float GreetChance = 0.7f;
    /// <summary>Of those who said hello, how many add a goodbye as you part.</summary>
    public const float PartChance = 0.35f;
    /// <summary>A person greets the same passer-by once, then not again for this long.</summary>
    public const double MeetAgainSeconds = 90;
    public const double BumpAgainSeconds = 6;
    /// <summary>Standing in front of somebody who has stopped, for this long, gets asked what you want.</summary>
    public const double LingerSeconds = 2.5;
    public const double HelpAgainSeconds = 60;
    /// <summary>Two strangers passing, how often one says hello to the other.</summary>
    public const float StrangerGreetChance = 0.3f;
    /// <summary>
    /// How far a phone call is worth saying out loud, metres: where a normal voice has fallen to about
    /// 30 dB SPL (62 dB at a metre, less 32 for forty times the distance), which is under the
    /// background of any street. The mixer's audible range runs to hundreds of metres, because it is a
    /// cost bound; this is whether anybody could hear it at all.
    /// </summary>
    public const float HeardMetres = 40f;
    /// <summary>How often a phone line, for somebody who has stories, is one of them.</summary>
    public const double StoryChance = 0.2;
    /// <summary>A beat between one person finishing and the other answering.</summary>
    public const float ReplyGapSeconds = 0.3f;

    private sealed class Person
    {
        public required string Voice;
        public double BusyUntil;
        public string LastLine = "";
        public double CallUntil = double.NegativeInfinity;
        public double NextCallCheck;
        public double NextPhoneLine;
        public bool ToldStory;
        public Vector3 LastPosition;
    }

    private sealed class Encounter
    {
        public double LastMet = double.NegativeInfinity;
        public double LastBump = double.NegativeInfinity;
        public bool WillPart;
        public float Closest = float.MaxValue;
        public double LingerSince = double.NaN;
        public double LastHelp = double.NegativeInfinity;
    }

    private readonly Dictionary<(string Map, int Id), Person> _people = new();
    private readonly Dictionary<(string Map, int Person, int Other), Encounter> _encounters = new();
    private readonly Random _rng;
    private readonly HashSet<(string, int)> _seen = new();

    public PedestrianSpeech(Random? rng = null) { _rng = rng ?? new Random(); }

    // ── Voices ──────────────────────────────────────────────────────────────────────────────────

    private static readonly Dictionary<string, int> _nextVoice = new();

    /// <summary>The voices Cody picked out (2026-09-27): each is handed out twice as often, so the city
    /// hears them more and the older voices less.</summary>
    public static readonly IReadOnlyList<string> Favourites =
        new[] { "joel", "seanterry", "joeb", "ben", "alec", "fluke", "camel" };

    private static IReadOnlyList<string>? _walkerVoices;

    /// <summary>Everybody who can greet you, with the favourites in twice.</summary>
    public static IReadOnlyList<string> WalkerVoices => _walkerVoices ??=
        Speech.VoicesWith("greet").Concat(Speech.VoicesWith("greet").Where(Favourites.Contains)).ToArray();

    /// <summary>
    /// The next voice for a new person on a map. Handed out in turn, so a street of a dozen people is a
    /// dozen different voices rather than a hash that gives three of them the same one.
    /// </summary>
    public static string NextVoice(string mapId) => Next("walk:" + mapId, WalkerVoices);

    /// <summary>The next driver's voice: somebody with something to yell.</summary>
    public static string NextDriverVoice(string mapId) => Next("drive:" + mapId, Speech.VoicesWith("yell"));

    private static string Next(string key, IReadOnlyList<string> voices)
    {
        if (voices.Count == 0) return "";
        lock (_nextVoice)
        {
            int n = _nextVoice.GetValueOrDefault(key);
            _nextVoice[key] = n + 1;
            return voices[n % voices.Count];
        }
    }

    // ── Each tick ───────────────────────────────────────────────────────────────────────────────

    public void Update(string mapId, World world, double now, SpeechConditions cond,
                       Action<int, string, TransientSound> say)
    {
        var people = new List<(int Id, Vector3 At, Vector3 Forward, float Speed, string Voice)>();
        world.Query(new QueryDescription().WithAll<Transform, Velocity, Pedestrian>(),
            (Entity e, ref Transform t, ref Velocity v, ref Pedestrian p) =>
            {
                if (string.IsNullOrEmpty(p.Voice)) return;
                var fwd = Vector3.Transform(Vector3.UnitZ, t.Rotation);
                fwd.Y = 0f;
                fwd = fwd.LengthSquared() > 1e-6f ? Vector3.Normalize(fwd) : Vector3.UnitZ;
                people.Add((e.Id, t.Position, fwd, new Vector2(v.Linear.X, v.Linear.Z).Length(), p.Voice));
            });
        PruneGone(mapId, people);
        if (people.Count == 0) return;

        var players = new List<(int Id, Vector3 At, float Speed)>();
        world.Query(new QueryDescription().WithAll<Transform, PlayerComponent>(),
            (Entity e, ref Transform t, ref PlayerComponent pc) =>
            {
                if (pc.IsInVehicle) return;
                float speed = world.Has<Velocity>(e) ? new Vector2(world.Get<Velocity>(e).Linear.X, world.Get<Velocity>(e).Linear.Z).Length() : 0f;
                players.Add((e.Id, t.Position, speed));
            });

        foreach (var p in people)
        {
            var me = Get(mapId, p.Id, p.Voice, now);
            me.LastPosition = p.At;
            foreach (var q in players)
                Meet(mapId, p, me, q, now, cond, say);
            Phone(mapId, p, me, players, now, cond, say);
        }

        // Two strangers passing each other, where somebody could hear it. Everywhere else it would be
        // a hello sent to every client on the map from three hundred metres away.
        for (int i = 0; i < people.Count; i++)
        {
            var a = people[i];
            if (!players.Any(q => Vector3.Distance(q.At, a.At) < HeardMetres)) continue;
            for (int j = i + 1; j < people.Count; j++)
                Pass(mapId, a, people[j], now, cond, say);
        }
    }

    private Person Get(string mapId, int id, string voice, double now)
    {
        if (!_people.TryGetValue((mapId, id), out var me) || me.Voice != voice)
        {
            me = new Person { Voice = voice, NextCallCheck = now + Uniform(5, 60) };
            _people[(mapId, id)] = me;
        }
        return me;
    }

    private void PruneGone(string mapId, List<(int Id, Vector3 At, Vector3 Forward, float Speed, string Voice)> people)
    {
        _seen.Clear();
        foreach (var p in people) _seen.Add((mapId, p.Id));
        foreach (var key in _people.Keys.Where(k => k.Map == mapId && !_seen.Contains(k)).ToList())
        {
            _people.Remove(key);
            foreach (var enc in _encounters.Keys.Where(k => k.Map == mapId && (k.Person == key.Id || k.Other == key.Id)).ToList())
                _encounters.Remove(enc);
        }
    }

    private Encounter EncounterFor(string mapId, int person, int other)
    {
        if (!_encounters.TryGetValue((mapId, person, other), out var enc))
            _encounters[(mapId, person, other)] = enc = new Encounter();
        return enc;
    }

    /// <summary>Horizontal distance, or infinity for somebody on another floor.</summary>
    private static float Apart(Vector3 a, Vector3 b)
        => MathF.Abs(a.Y - b.Y) > 2.5f ? float.PositiveInfinity : new Vector2(a.X - b.X, a.Z - b.Z).Length();

    private static bool InFront(Vector3 at, Vector3 forward, Vector3 other)
    {
        var to = new Vector3(other.X - at.X, 0f, other.Z - at.Z);
        float len = to.Length();
        return len < 1e-3f || Vector3.Dot(forward, to / len) >= FacingCos;
    }

    /// <summary>A person and a player: bumped, greeted, parted from, or stood in front of.</summary>
    private void Meet(string mapId, (int Id, Vector3 At, Vector3 Forward, float Speed, string Voice) p, Person me,
                      (int Id, Vector3 At, float Speed) q, double now, SpeechConditions cond,
                      Action<int, string, TransientSound> say)
    {
        float d = Apart(p.At, q.At);
        if (d > 8f) return;
        var enc = EncounterFor(mapId, p.Id, q.Id);
        bool free = now >= me.BusyUntil;
        bool onPhone = now < me.CallUntil;

        if (d < BumpMetres && now - enc.LastBump > BumpAgainSeconds && free)
        {
            enc.LastBump = now;
            enc.LastMet = now;            // walked into you: that is the meeting, no hello after it
            enc.WillPart = false;
            bool annoyed = _rng.NextDouble() < 0.15;
            string line = Pick(me, annoyed ? StreetLines.Annoyed : StreetLines.Sorry);
            Say(p, me, line, line == "polite_watch_it" ? Speech.RaisedDb : Speech.NormalDb, 0f, now, say);
            return;
        }

        if (d < GreetMetres && now - enc.LastMet > MeetAgainSeconds && free && InFront(p.At, p.Forward, q.At))
        {
            enc.LastMet = now;
            enc.Closest = d;
            enc.WillPart = false;
            if (!onPhone && _rng.NextDouble() < GreetChance)
            {
                Say(p, me, Pick(me, StreetLines.Greetings(cond)), Speech.NormalDb, 0f, now, say);
                enc.WillPart = _rng.NextDouble() < PartChance;
            }
            return;
        }

        if (enc.WillPart)
        {
            enc.Closest = MathF.Min(enc.Closest, d);
            if (d > 7f) enc.WillPart = false;
            else if (free && d > enc.Closest + 1.5f && d > 2f)
            {
                enc.WillPart = false;
                Say(p, me, Pick(me, StreetLines.Partings(cond)), Speech.NormalDb, 0f, now, say);
            }
        }

        // Somebody stopped in front of a person who has also stopped.
        bool standing = p.Speed < 0.2f && q.Speed < 0.3f && d < 2f && InFront(p.At, p.Forward, q.At);
        if (!standing) { enc.LingerSince = double.NaN; return; }
        if (double.IsNaN(enc.LingerSince)) enc.LingerSince = now;
        if (now - enc.LingerSince >= LingerSeconds && now - enc.LastHelp > HelpAgainSeconds && free && !onPhone)
        {
            enc.LastHelp = now;
            enc.LastMet = now;
            Say(p, me, Pick(me, StreetLines.Help), Speech.NormalDb, 0f, now, say);
        }
    }

    /// <summary>
    /// A phone call: now and then a person takes one, answers it, says their half of it with long
    /// gaps where the other end is talking, and rings off. Only said where somebody could hear it.
    /// </summary>
    private void Phone((int Id, Vector3 At, Vector3 Forward, float Speed, string Voice) p, Person me, double now,
                       SpeechConditions cond, Action<int, string, TransientSound> say, bool heard)
    {
        if (now < me.CallUntil)
        {
            if (now < me.NextPhoneLine || now < me.BusyUntil) return;
            bool last = now + 6 > me.CallUntil;
            // Somebody with a story tells it, once a call at most: half a minute of their side.
            var stories = Speech.LinesOf(me.Voice, "story");
            if (!last && !me.ToldStory && stories.Count > 0 && _rng.NextDouble() < StoryChance)
            {
                me.ToldStory = true;
                string story = Pick(me, stories);
                if (heard) Say(p, me, story, Speech.NormalDb, 0f, now, say);
                me.CallUntil = Math.Max(me.CallUntil, now + LengthOf(me.Voice, story) + 10);
                me.NextPhoneLine = now + LengthOf(me.Voice, story) + Uniform(2, 5);
                return;
            }
            string line = Pick(me, last ? StreetLines.RingOff
                                        : _rng.NextDouble() < 0.55 ? StreetLines.Listening : StreetLines.PhoneTalk(cond));
            if (heard) Say(p, me, line, Speech.NormalDb, 0f, now, say);
            if (last) { me.CallUntil = now; me.NextCallCheck = now + Uniform(60, 180); return; }
            me.NextPhoneLine = now + LengthOf(me.Voice, line) + Uniform(3, 10);
            return;
        }
        if (now < me.NextCallCheck || now < me.BusyUntil) return;
        me.NextCallCheck = now + Uniform(45, 150);
        if (_rng.NextDouble() >= 0.35) return;
        me.CallUntil = now + Uniform(30, 90);
        me.ToldStory = false;
        string hello = Pick(me, StreetLines.Answer);
        if (heard) Say(p, me, hello, Speech.NormalDb, 0f, now, say);
        me.NextPhoneLine = now + LengthOf(me.Voice, hello) + Uniform(2, 5);
    }

    private void Phone(string mapId, (int Id, Vector3 At, Vector3 Forward, float Speed, string Voice) p, Person me,
                       List<(int Id, Vector3 At, float Speed)> players, double now, SpeechConditions cond,
                       Action<int, string, TransientSound> say)
    {
        bool heard = players.Any(q => Vector3.Distance(q.At, p.At) < HeardMetres);
        Phone(p, me, now, cond, say, heard);
    }

    /// <summary>Two people passing on a pavement; one may say hello and the other answer.</summary>
    private void Pass(string mapId, (int Id, Vector3 At, Vector3 Forward, float Speed, string Voice) a,
                      (int Id, Vector3 At, Vector3 Forward, float Speed, string Voice) b, double now,
                      SpeechConditions cond, Action<int, string, TransientSound> say)
    {
        float d = Apart(a.At, b.At);
        if (d > GreetMetres) return;
        var enc = EncounterFor(mapId, Math.Min(a.Id, b.Id), Math.Max(a.Id, b.Id));
        if (now - enc.LastMet <= MeetAgainSeconds) return;
        var pa = _people[(mapId, a.Id)];
        var pb = _people[(mapId, b.Id)];
        if (now < pa.BusyUntil || now < pb.BusyUntil) return;
        if (!InFront(a.At, a.Forward, b.At) || !InFront(b.At, b.Forward, a.At)) return;
        enc.LastMet = now;
        if (now < pa.CallUntil || now < pb.CallUntil || _rng.NextDouble() >= StrangerGreetChance) return;

        var (first, second) = StreetLines.Exchange(cond, _rng);
        string l1 = Pick(pa, first);
        string l2 = Pick(pb, second);
        float gap = LengthOf(pa.Voice, l1) + ReplyGapSeconds;
        Say(a, pa, l1, Speech.NormalDb, 0f, now, say);
        Say(b, pb, l2, Speech.NormalDb, gap, now, say);
    }

    private void Say((int Id, Vector3 At, Vector3 Forward, float Speed, string Voice) p, Person me, string line,
                     float effortDb, float delay, double now, Action<int, string, TransientSound> say)
    {
        var take = Speech.Find(me.Voice, line);
        if (take == null) return;
        me.LastLine = line;
        me.BusyUntil = Math.Max(me.BusyUntil, now + delay + take.Seconds);
        Serilog.Log.Information("SPEECH e{Id} {Voice} at {At}: \"{Text}\"", p.Id, me.Voice, p.At, take.Text);
        say(p.Id, "speech: " + take.Text, new TransientSound
        {
            // Air, shaped: the nearest of the four characters. The recording named by the key is
            // what is actually played; the character only keeps it out of the impulse paths.
            Character = SoundCharacter.Hiss,
            DelaySeconds = delay,
            Position = p.At + new Vector3(0f, Speech.MouthHeight, 0f) + p.Forward * 0.1f,
            LevelDb = Speech.LevelDb(effortDb),
            DecaySeconds = take.Seconds,
            Noisiness = 0.5f,
            SynthKey = Speech.Key(me.Voice, line),
        });
    }

    private static float LengthOf(string voice, string line) => Speech.Find(voice, line)?.Seconds ?? 1f;

    /// <summary>A line from the list that this voice recorded, not the one this person said last if
    /// there is any other.</summary>
    private string Pick(Person me, IReadOnlyList<string> offered)
    {
        var lines = offered.Where(l => Speech.Find(me.Voice, l) != null).ToList();
        if (lines.Count == 0) return "";
        int last = -1;
        for (int i = 0; i < lines.Count; i++) if (lines[i] == me.LastLine) { last = i; break; }
        if (last < 0 || lines.Count == 1) return lines[_rng.Next(lines.Count)];
        int k = _rng.Next(lines.Count - 1);
        return lines[k >= last ? k + 1 : k];
    }

    private double Uniform(double lo, double hi) => lo + (hi - lo) * _rng.NextDouble();
}

/// <summary>
/// Which recorded lines fit which moment. Each list is line names from the catalogue; a line that
/// needs something to be true (the time of day, the weather) is only in a list when it is.
/// </summary>
public static class StreetLines
{
    public static bool IsMorning(float h) => h >= 5f && h < 12f;
    public static bool IsAfternoon(float h) => h >= 12f && h < 17f;
    public static bool IsEvening(float h) => h >= 17f && h < 23f;
    public static bool IsDaytime(float h) => h >= 5f && h < 17f;
    public static bool IsNight(float h) => h >= 18f || h < 5f;

    /// <summary>A clear, mild day in daylight: the one "beautiful day" is true of.</summary>
    public static bool IsFine(SpeechConditions c)
        => c.Weather == WeatherType.Clear && c.Precipitation < 0.05f && c.Hour >= 8f && c.Hour < 19f
           && c.TemperatureC >= 12f && c.TemperatureC <= 30f;

    /// <summary>A wet front coming in that has not properly arrived yet.</summary>
    public static bool RainComing(SpeechConditions c)
        => (c.Weather == WeatherType.Rain || c.Weather == WeatherType.Storm) && c.Precipitation < 0.3f;

    public static IReadOnlyList<string> Greetings(SpeechConditions c)
    {
        var l = new List<string>
        {
            "greet_hey_how_s_it_going", "greet_yo_what_s_up", "greet_hey_there", "greet_hi", "greet_hello",
            "greet_hey", "greet_how_are_you_doing", "greet_how_you_doing", "greet_what_s_going_on", "greet_hey_man",
        };
        if (IsMorning(c.Hour)) l.AddRange(new[] { "greet_hey_good_morning", "greet_morning", "greet_good_morning" });
        if (IsAfternoon(c.Hour)) l.AddRange(new[] { "greet_afternoon", "greet_good_afternoon" });
        if (IsEvening(c.Hour)) l.AddRange(new[] { "greet_evening", "greet_good_evening" });
        if (IsFine(c)) l.AddRange(new[] { "greet_beautiful_day_huh", "greet_nice_day_for_a_walk" });
        if (c.TemperatureC >= 28f) l.Add("greet_hot_one_today_isn_t_it");
        if (c.TemperatureC <= 8f) l.Add("greet_cold_out_here_today");
        if (RainComing(c)) l.Add("greet_looks_like_rain");
        return l;
    }

    public static IReadOnlyList<string> Partings(SpeechConditions c)
    {
        var l = new List<string>
        {
            "bye_have_a_good_one", "bye_take_care", "bye_see_you_later", "bye_see_ya", "bye_take_it_easy",
            "bye_bye_now", "bye_later", "bye_stay_safe_out_there",
        };
        if (IsDaytime(c.Hour)) l.AddRange(new[] { "bye_have_a_good_day", "bye_have_a_nice_day" });
        if (IsNight(c.Hour)) l.Add("bye_have_a_good_night");
        return l;
    }

    public static readonly IReadOnlyList<string> Sorry =
        new[] { "polite_excuse_me", "polite_pardon_me", "polite_sorry_excuse_me", "polite_oh_sorry" };

    public static readonly IReadOnlyList<string> Annoyed = new[] { "polite_watch_it", "polite_careful_there" };

    public static readonly IReadOnlyList<string> Help = new[] { "greet_can_i_help_you", "greet_need_something" };

    /// <summary>Picking up.</summary>
    public static readonly IReadOnlyList<string> Answer = new[] { "greet_hello", "greet_hey", "greet_hi" };

    /// <summary>Most of a phone call is listening to the other end.</summary>
    public static readonly IReadOnlyList<string> Listening = new[]
    {
        "chatter_mm_hmm", "chatter_uh_huh", "chatter_right_right", "chatter_yeah_yeah_totally",
        "chatter_yeah_i_know_right", "chatter_ha_yeah", "chatter_that_s_what_i_m_saying",
        "chatter_oh_my_god_that_s_hilarious", "chatter_okay_sounds_good",
        "chatter_yeah_that_s_what_i_heard", "chatter_exactly", "chatter_yeah_no_i_get_it",
        "chatter_okay_okay_i_hear_you", "chatter_for_real", "chatter_that_s_crazy", "chatter_can_you_believe_that",
        "chatter_hold_on_a_second",
    };

    public static IReadOnlyList<string> PhoneTalk(SpeechConditions c)
    {
        var l = new List<string>
        {
            "chatter_no_way_seriously", "chatter_i_told_him_that_already", "chatter_where_are_we_eating_tonight",
            "chatter_i_don_t_know_what_do_you_want", "chatter_did_you_see_the_game_last_night",
            "chatter_we_should_grab_coffee_sometime", "chatter_how_much_was_it", "chatter_that_s_way_too_expensive",
            "chatter_i_ll_meet_you_there", "chatter_did_you_get_my_text", "chatter_my_phone_s_about_to_die",
            "chatter_i_m_so_tired_today", "chatter_honestly_i_have_no_idea", "chatter_anyway_like_i_was_saying",
            "chatter_nah_i_m_good_thanks", "chatter_i_think_we_parked_on_the_other_side",
            "chatter_i_m_not_sure_honestly", "chatter_we_ll_see_how_it_goes", "chatter_did_you_call_your_mom_back",
            "chatter_i_ve_got_to_pick_up_the_kids_at_three", "chatter_it_s_been_a_long_week",
            "chatter_i_ll_tell_you_about_it_later", "chatter_oh_that_reminds_me", "chatter_i_need_a_vacation",
            "chatter_let_me_know_when_you_re_ready", "chatter_where_d_you_park", "chatter_what_time_is_it",
            "chatter_i_m_starving_you_want_to_grab_lunch", "chatter_they_re_opening_a_new_place_down_the_str",
            "chatter_i_haven_t_seen_him_in_forever", "chatter_she_said_she_d_be_here_by_now",
            "chatter_no_the_other_one", "chatter_i_ll_think_about_it", "chatter_i_swear_every_single_time",
            "chatter_whatever_it_s_fine", "chatter_it_s_not_a_big_deal", "chatter_i_was_just_about_to_say_that",
            "chatter_rent_went_up_again", "chatter_my_car_s_in_the_shop", "chatter_the_bus_is_late_again",
            "chatter_i_don_t_even_know_anymore", "chatter_it_is_what_it_is",
        };
        if (IsMorning(c.Hour)) l.Add("chatter_traffic_was_insane_this_morning");
        if (c.Weather == WeatherType.Storm && c.Precipitation < 0.3f) l.Add("chatter_it_s_supposed_to_storm_later");
        return l;
    }

    public static readonly IReadOnlyList<string> RingOff = new[]
    {
        "chatter_hang_on_let_me_call_you_back", "chatter_i_m_running_late_i_gotta_go",
        "chatter_okay_sounds_good", "bye_bye_now", "bye_later", "bye_see_ya",
    };

    // ── Drivers ─────────────────────────────────────────────────────────────────────────────────
    //
    // The city has no traffic lights, so "It's green! Go!" and "the light's green" are not used: there
    // is nothing for them to be true about.

    /// <summary>Somebody else did something: a car across their bows, a stop they had to make.</summary>
    public static readonly IReadOnlyList<string> Startled = new[]
    {
        "yell_watch_where_you_re_going", "yell_are_you_kidding_me", "yell_what_are_you_doing", "yell_you_cut_me_off",
        "yell_learn_how_to_drive", "yell_who_taught_you_how_to_drive", "yell_oh_you_ve_got_to_be_kidding_me",
        "yell_get_off_your_phone", "yell_what_s_wrong_with_you", "yell_hey_watch_it_buddy", "yell_use_your_blinker",
        "yell_pick_a_lane", "yell_stay_in_your_lane", "yell_you_almost_hit_me", "yell_unbelievable",
    };

    /// <summary>Held up: waiting, or somebody in front not going.</summary>
    public static readonly IReadOnlyList<string> Impatient = new[]
    {
        "yell_move_it", "yell_come_on_come_on_let_s_go", "yell_the_gas_is_on_the_right_asshole", "yell_move_your_car",
        "yell_go_around_idiot", "yell_are_you_kidding_me", "yell_unbelievable",
    };

    /// <summary>Held at a level crossing while a train takes its time.</summary>
    public static readonly IReadOnlyList<string> Waiting = new[]
    {
        "yell_come_on_come_on_let_s_go", "yell_unbelievable", "yell_oh_you_ve_got_to_be_kidding_me", "yell_are_you_kidding_me",
    };

    /// <summary>Somebody standing in the road in front of them.</summary>
    public static readonly IReadOnlyList<string> AtSomebodyInTheRoad = new[]
    {
        "yell_get_out_of_the_way", "yell_get_off_the_road", "yell_move_it", "yell_hey_watch_it_buddy",
        "yell_watch_where_you_re_going", "yell_what_are_you_doing", "yell_are_you_kidding_me", "yell_get_off_your_phone",
    };

    /// <summary>Somebody pulling in to the kerb in front of them.</summary>
    public static readonly IReadOnlyList<string> AtAParker = new[] { "yell_nice_parking_job_genius", "yell_use_your_blinker", "yell_move_your_car" };

    /// <summary>What two strangers say passing each other: a hello and a hello back, mostly.</summary>
    public static (IReadOnlyList<string> First, IReadOnlyList<string> Second) Exchange(SpeechConditions c, Random rng)
    {
        if (rng.NextDouble() < 0.06)
            return (new[] { "chatter_which_way_is_the_train_station" },
                    new[] { "chatter_it_s_just_up_the_street_on_the_left", "chatter_it_s_right_around_the_corner" });
        var hello = new List<string> { "greet_hi", "greet_hey", "greet_hello", "greet_hey_there", "greet_how_you_doing" };
        if (IsMorning(c.Hour)) hello.AddRange(new[] { "greet_morning", "greet_good_morning" });
        if (IsAfternoon(c.Hour)) hello.Add("greet_afternoon");
        if (IsEvening(c.Hour)) hello.Add("greet_evening");
        return (hello, hello);
    }
}

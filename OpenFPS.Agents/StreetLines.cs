using OpenFPS.Models;
using OpenFPS.Environment;

namespace OpenFPS.Server.Systems;

/// <summary>What is true about the world right now that a person might remark on (the host fills it from its
/// world: PedestrianSpeech.Conditions).</summary>
public readonly record struct SpeechConditions(float Hour, float TemperatureC, float Precipitation, WeatherType Weather);

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
    // No traffic-light lines ("It's green! Go!"): the city has no lights for them to be true about.

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

    /// <summary>Named lines, and categories whose any-time lines join them.</summary>
    public readonly record struct Offer(IReadOnlyList<string> Lines, string[] Categories);

    /// <summary>What two strangers say passing each other: a hello and a hello back, mostly; now and
    /// then a remark and an answer, or the way somewhere.</summary>
    public static (Offer First, Offer Second) Exchange(SpeechConditions c, Random rng)
    {
        double r = rng.NextDouble();
        if (r < 0.06)
            return (new Offer(new[] { "chatter_which_way_is_the_train_station" }, new[] { "ask_directions" }),
                    new Offer(new[] { "chatter_it_s_just_up_the_street_on_the_left", "chatter_it_s_right_around_the_corner" }, new[] { "give_directions" }));
        if (r < 0.16)
            return (new Offer(Array.Empty<string>(), new[] { "smalltalk_open" }), new Offer(Array.Empty<string>(), new[] { "smalltalk_reply" }));
        var hello = new List<string> { "greet_hi", "greet_hey", "greet_hello", "greet_hey_there", "greet_how_you_doing" };
        if (IsMorning(c.Hour)) hello.AddRange(new[] { "greet_morning", "greet_good_morning" });
        if (IsAfternoon(c.Hour)) hello.Add("greet_afternoon");
        if (IsEvening(c.Hour)) hello.Add("greet_evening");
        return (new Offer(hello, new[] { "greet" }), new Offer(hello, new[] { "greet", "greet_reply" }));
    }

    /// <summary>
    /// Whether a line names no time, season or weather, so it can be said whenever. "Day" alone is in
    /// the list because "Have a good day." came back after dark when only "nice day" was.
    /// </summary>
    public static bool AnyTime(string text)
        => !System.Text.RegularExpressions.Regex.IsMatch(text.ToLowerInvariant(),
            @"\b(morning|afternoon|evening|tonight|night|noon|lunch|breakfast|dinner|day|days|today|tomorrow|yesterday|weekend|monday|tuesday|wednesday|thursday|friday|saturday|sunday|spring|summer|autumn|winter|weather|rain|raining|snow|snowing|cold|freezing|hot|heat|sun|sunny|storm|wind|windy|fog|foggy)\b");

    /// <summary>The lines a voice can say from a list: the named ones it recorded, and its any-time
    /// lines in the given categories.</summary>
    public static List<string> Candidates(string voice, IReadOnlyList<string> offered, IReadOnlyList<string> categories)
    {
        var lines = offered.Where(l => Speech.Find(voice, l) != null).ToList();
        foreach (var cat in categories)
            foreach (var line in Speech.LinesOf(voice, cat))
                if (!lines.Contains(line) && Speech.Find(voice, line) is { } t && AnyTime(t.Text)) lines.Add(line);
        return lines;
    }

    /// <summary>The categories of a remark to nobody: muttering, a thought, a text read out, and the
    /// weather or the hour when true.</summary>
    public static string[] Remarks(SpeechConditions c)
    {
        var l = new List<string> { "mutter", "think_aloud", "read_text" };
        bool wet = c.Weather is WeatherType.Rain or WeatherType.Storm && c.Precipitation >= 0.2f;
        if (wet) l.Add("weather_rain");
        if (c.Weather == WeatherType.Storm) l.Add("weather_storm");
        if (c.Weather == WeatherType.Snow) l.Add("weather_snow");
        if (c.TemperatureC <= 5f) l.Add("weather_cold");
        if (c.TemperatureC >= 28f) l.Add("weather_hot");
        if (IsFine(c)) l.Add("weather_fine");
        if (c.Hour >= 5f && c.Hour < 11f) l.Add("time_morning");
        else if (c.Hour >= 11f && c.Hour < 14f) l.Add("time_noon");
        else if (c.Hour >= 17f && c.Hour < 22f) l.Add("time_evening");
        else if (c.Hour >= 22f || c.Hour < 5f) l.Add("time_night");
        return l.ToArray();
    }
}

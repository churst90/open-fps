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
    /// <summary>Two people walking together share this ("" for somebody on their own).</summary>
    public string Pair;
    /// <summary>The name of somebody in particular (CharacterSystem: "Alex"), or "" for a passer-by.
    /// What a character says is their own (PedestrianSpeech.Homeless), not the crowd's.</summary>
    public string Character;
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
/// Every line is a world sound from a body's mouth, so everyone near hears it from the same place.
/// A line is only used when the world makes it true: "Morning" by the game clock, "Looks like rain"
/// when a wet front is coming in.
/// </summary>
public sealed partial class PedestrianSpeech
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
    /// 30 dB SPL (62 dB at a metre, less 32), under any street's background. The mixer's range is a cost
    /// bound of hundreds of metres; this is whether anybody could hear it.
    /// </summary>
    public const float HeardMetres = 40f;
    /// <summary>How often a phone line, for somebody who has stories, is one of them.</summary>
    public const double StoryChance = 0.2;
    /// <summary>A beat between one person finishing and the other answering.</summary>
    public const float ReplyGapSeconds = 0.3f;
    /// <summary>Of calls made, how many go to voicemail.</summary>
    public const double VoicemailChance = 0.12;
    /// <summary>Of calls made, how many are one of the recorded calls played through.</summary>
    public const double ScriptedCallChance = 0.5;
    /// <summary>Two people walking together start a conversation when somebody is near, then not
    /// another for this long, seconds.</summary>
    public const double TalkAgainSeconds = 90;
    /// <summary>A gap between one person finishing and the other answering, seconds: in speech it is
    /// usually a fifth of a second or so (Stivers et al. 2009).</summary>
    public const float TurnGapSeconds = 0.25f;
    /// <summary>Somebody on their own says something to nobody (mutters, reads a text out, remarks on
    /// the weather) about this often, while somebody is near enough to hear, seconds.</summary>
    public const double RemarkEverySeconds = 150;
    /// <summary>Muttering is quieter than talking to someone: about 8 dB under a normal voice.</summary>
    public const float MutterDb = Speech.NormalDb - 8f;
    /// <summary>A remark to nobody is only made with somebody within this, metres.</summary>
    public const float RemarkHeardMetres = 15f;

    private sealed class Person
    {
        public required string Voice;
        public double BusyUntil;
        public string LastLine = "";
        public double CallUntil = double.NegativeInfinity;
        /// <summary>An ordinary call is open until its goodbye is said: by the clock alone, a call
        /// whose next line fell due after CallUntil stopped in silence.</summary>
        public bool OnCall;
        public double NextCallCheck;
        public double NextPhoneLine;
        public bool ToldStory;
        public Vector3 LastPosition;
        public double NextRemark;
        /// <summary>A scripted call in progress, and how far through it.</summary>
        public IReadOnlyList<Speech.CallTurn>? Script;
        public int ScriptAt;
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

    /// <summary>Everybody who can greet you, with the favourites in twice. Not a character's voice:
    /// there is one Alex, and a passer-by in his voice would be him twice.</summary>
    public static IReadOnlyList<string> WalkerVoices => _walkerVoices ??=
        PasserByVoices().Concat(PasserByVoices().Where(Favourites.Contains)).ToArray();

    private static IEnumerable<string> PasserByVoices() => Speech.VoicesWith("greet").Where(v => !IsCharacterVoice(v));

    /// <summary>The categories that make a voice somebody in particular (homeless_money, ...): a voice
    /// that recorded them is a character's (Alex), never handed to a walker, a driver or a pair.</summary>
    public const string CharacterCategoryPrefix = "homeless_";

    /// <summary>Whether a voice belongs to a character rather than to the crowd.</summary>
    public static bool IsCharacterVoice(string voice)
        => Speech.Takes.Any(t => t.Voice == voice && t.Category.StartsWith(CharacterCategoryPrefix, StringComparison.Ordinal));

    /// <summary>The next voice for a new person on a map, in turn, so a dozen people are a dozen voices.</summary>
    public static string NextVoice(string mapId) => Next("walk:" + mapId, WalkerVoices);

    private static readonly Dictionary<(string Map, string Pair), (string A, string B)> _pairVoices = new();

    /// <summary>
    /// The voice for one of two people walking together: the two voices of a recorded conversation,
    /// handed out in turn, so each pair has something to say to each other.
    /// </summary>
    public static string PairVoice(string mapId, string pair, bool second)
    {
        lock (_pairVoices)
        {
            if (!_pairVoices.TryGetValue((mapId, pair), out var v))
            {
                var pairs = Speech.ConversationPairs;
                if (pairs.Count == 0) return NextVoice(mapId);
                int n = _nextVoice.GetValueOrDefault("pair:" + mapId); _nextVoice["pair:" + mapId] = n + 1;
                _pairVoices[(mapId, pair)] = v = pairs[n % pairs.Count];
            }
            return second ? v.B : v.A;
        }
    }

    /// <summary>The next driver's voice: somebody with something to yell.</summary>
    public static string NextDriverVoice(string mapId)
        => Next("drive:" + mapId, Speech.VoicesWith("yell").Where(v => !IsCharacterVoice(v)).ToArray());

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
        var characters = new List<(int Id, Vector3 At, Vector3 Forward, float Speed, string Voice)>();
        var pairs = new Dictionary<string, List<int>>();
        world.Query(new QueryDescription().WithAll<Transform, Velocity, Pedestrian>().WithNone<DeadComponent>(),
            (Entity e, ref Transform t, ref Velocity v, ref Pedestrian p) =>
            {
                if (string.IsNullOrEmpty(p.Voice)) return;
                if (!string.IsNullOrEmpty(p.Character))
                {
                    // Somebody in particular: what they say is their own (Homeless), not the crowd's.
                    var cf = Vector3.Transform(Vector3.UnitZ, t.Rotation);
                    cf.Y = 0f;
                    cf = cf.LengthSquared() > 1e-6f ? Vector3.Normalize(cf) : Vector3.UnitZ;
                    characters.Add((e.Id, t.Position, cf, new Vector2(v.Linear.X, v.Linear.Z).Length(), p.Voice));
                    return;
                }
                if (!string.IsNullOrEmpty(p.Pair))
                {
                    if (!pairs.TryGetValue(p.Pair, out var members)) pairs[p.Pair] = members = new List<int>();
                    members.Add(people.Count);
                }
                var fwd = Vector3.Transform(Vector3.UnitZ, t.Rotation);
                fwd.Y = 0f;
                fwd = fwd.LengthSquared() > 1e-6f ? Vector3.Normalize(fwd) : Vector3.UnitZ;
                people.Add((e.Id, t.Position, fwd, new Vector2(v.Linear.X, v.Linear.Z).Length(), p.Voice));
            });
        PruneGone(mapId, people.Concat(characters).ToList());
        if (people.Count == 0 && characters.Count == 0) return;

        var players = new List<(int Id, Vector3 At, float Speed)>();
        world.Query(new QueryDescription().WithAll<Transform, PlayerComponent>().WithNone<DeadComponent>(),
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
            Phone(p, me, players, now, cond, say);
            Remark(p, me, players, now, cond, say);
        }
        foreach (var ch in characters)
            Panhandle(mapId, world, ch, people, players, now, cond, say);
        // A character startles at a shot like anybody, and is asked nothing by the crowd.
        React(mapId, people.Concat(characters).ToList(), players, now, say);
        foreach (var (pair, members) in pairs)
            if (members.Count == 2) Converse(mapId, pair, people[members[0]], people[members[1]], players, now, say);

        // Strangers passing only where a player could hear: elsewhere it is a hello sent to every
        // client on the map from three hundred metres away.
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
            me = new Person { Voice = voice, NextCallCheck = now + Uniform(5, 60), NextRemark = now + Uniform(20, 2 * RemarkEverySeconds) };
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
            lock (_panhandlers) _panhandlers.Remove(key);
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
        bool onPhone = OnPhone(me, now);

        if (d < BumpMetres && now - enc.LastBump > BumpAgainSeconds && free)
        {
            enc.LastBump = now;
            enc.LastMet = now;            // walked into you: that is the meeting, no hello after it
            enc.WillPart = false;
            bool annoyed = _rng.NextDouble() < 0.15;
            string line = annoyed ? Pick(me, StreetLines.Annoyed, "annoyed") : Pick(me, StreetLines.Sorry, "polite");
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
                Say(p, me, Pick(me, StreetLines.Greetings(cond), "greet"), Speech.NormalDb, 0f, now, say);
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
                Say(p, me, Pick(me, StreetLines.Partings(cond), "bye"), Speech.NormalDb, 0f, now, say);
            }
        }

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
    /// A phone call: a scripted one played through, or an ordinary one (answer, their half with gaps
    /// for the other end, ring off). Kept going unheard; only said aloud when <paramref name="heard"/>.
    /// </summary>
    private void Phone((int Id, Vector3 At, Vector3 Forward, float Speed, string Voice) p, Person me, double now,
                       SpeechConditions cond, Action<int, string, TransientSound> say, bool heard)
    {
        if (now < me.CallUntil && me.Script != null)
        {
            if (now < me.NextPhoneLine || now < me.BusyUntil) return;
            if (me.ScriptAt >= me.Script.Count) { me.Script = null; me.CallUntil = now; me.NextCallCheck = now + Uniform(60, 180); return; }
            var turn = me.Script[me.ScriptAt++];
            if (turn.Line == null)
            {
                // The far end talking.
                me.NextPhoneLine = now + turn.Pause;
                if (turn.Pause >= 3f && _rng.NextDouble() < 0.3 && heard)
                {
                    string mm = Pick(me, StreetLines.Listening, "phone_listen");
                    if (mm.Length > 0) Say(p, me, mm, Speech.NormalDb, (float)Uniform(0.8, turn.Pause - 1.5), now, say);
                }
                return;
            }
            if (heard) Say(p, me, turn.Line, Speech.NormalDb, 0f, now, say);
            me.NextPhoneLine = now + LengthOf(me.Voice, turn.Line) + Uniform(0.2, 0.6);
            return;
        }
        if (me.OnCall)
        {
            if (now < me.NextPhoneLine || now < me.BusyUntil) return;
            bool last = now + 6 > me.CallUntil;
            // A story at most once a call.
            var stories = Speech.LinesOf(me.Voice, "story")
                .Where(l => Speech.Find(me.Voice, l) is { } t && HomelessLines.StoryTrueNow(t.Text, cond)).ToList();
            if (!last && !me.ToldStory && stories.Count > 0 && _rng.NextDouble() < StoryChance)
            {
                me.ToldStory = true;
                string story = Pick(me, stories);
                if (heard) Say(p, me, story, Speech.NormalDb, 0f, now, say);
                me.CallUntil = Math.Max(me.CallUntil, now + LengthOf(me.Voice, story) + 10);
                me.NextPhoneLine = now + LengthOf(me.Voice, story) + Uniform(2, 5);
                return;
            }
            string line = last ? Pick(me, StreetLines.RingOff, "phone_end")
                        : _rng.NextDouble() < 0.06 ? Pick(me, Array.Empty<string>(), "phone_signal")
                        : _rng.NextDouble() < 0.55 ? Pick(me, StreetLines.Listening, "phone_listen")
                        : Pick(me, StreetLines.PhoneTalk(cond), "chatter");
            if (heard) Say(p, me, line, Speech.NormalDb, 0f, now, say);
            if (last) { me.OnCall = false; me.CallUntil = now; me.NextCallCheck = now + Uniform(60, 180); return; }
            me.NextPhoneLine = now + LengthOf(me.Voice, line) + Uniform(3, 10);
            // A line due after the call was meant to end is its goodbye, and the call lasts until then,
            // so everybody else still sees somebody on the phone.
            me.CallUntil = Math.Max(me.CallUntil, me.NextPhoneLine);
            return;
        }
        if (now < me.NextCallCheck || now < me.BusyUntil) return;
        me.NextCallCheck = now + Uniform(45, 150);
        if (_rng.NextDouble() >= 0.35) return;
        me.CallUntil = now + Uniform(30, 90);
        me.ToldStory = false;
        if (_rng.NextDouble() < VoicemailChance && Speech.LinesOf(me.Voice, "phone_voicemail").Count > 0)
        {
            string message = Pick(me, Array.Empty<string>(), "phone_voicemail");
            if (heard) Say(p, me, message, Speech.NormalDb, 0f, now, say);
            me.CallUntil = now; me.NextCallCheck = now + Uniform(60, 180);
            return;
        }
        var scripts = Speech.CallsFor(me.Voice);
        if (scripts.Count > 0 && _rng.NextDouble() < ScriptedCallChance)
        {
            me.Script = scripts[_rng.Next(scripts.Count)];
            me.ScriptAt = 0;
            me.CallUntil = now + 600;           // until the script ends
            me.NextPhoneLine = now;
            return;
        }
        me.Script = null;
        me.OnCall = true;
        string hello = Pick(me, StreetLines.Answer, "phone_answer");
        if (heard) Say(p, me, hello, Speech.NormalDb, 0f, now, say);
        me.NextPhoneLine = now + LengthOf(me.Voice, hello) + Uniform(2, 5);
    }

    private void Phone((int Id, Vector3 At, Vector3 Forward, float Speed, string Voice) p, Person me,
                       List<(int Id, Vector3 At, float Speed)> players, double now, SpeechConditions cond,
                       Action<int, string, TransientSound> say)
    {
        bool heard = players.Any(q => Vector3.Distance(q.At, p.At) < HeardMetres);
        Phone(p, me, now, cond, say, heard);
    }

    private readonly Dictionary<(string Map, string Pair), double> _nextTalk = new();

    /// <summary>
    /// Two people walking together play one of their recorded conversations, turn by turn, when a
    /// player is near enough to hear and neither is busy.
    /// </summary>
    private void Converse(string mapId, string pair, (int Id, Vector3 At, Vector3 Forward, float Speed, string Voice) x,
                          (int Id, Vector3 At, Vector3 Forward, float Speed, string Voice) y,
                          List<(int Id, Vector3 At, float Speed)> players, double now, Action<int, string, TransientSound> say)
    {
        if (now < _nextTalk.GetValueOrDefault((mapId, pair))) return;
        if (Apart(x.At, y.At) > 3f || !players.Any(q => Apart(q.At, x.At) < HeardMetres)) return;
        var px = _people[(mapId, x.Id)]; var py = _people[(mapId, y.Id)];
        if (now < px.BusyUntil || now < py.BusyUntil || OnPhone(px, now) || OnPhone(py, now)) return;
        var talks = Speech.ConversationsBetween(x.Voice, y.Voice);
        if (talks.Count == 0) { _nextTalk[(mapId, pair)] = now + 600; return; }
        var talk = talks[_rng.Next(talks.Count)];
        float t = 0f;
        foreach (var turn in talk.Turns)
        {
            bool isX = (turn.Who == "A") == (talk.A == x.Voice);
            var (who, me) = isX ? (x, px) : (y, py);
            if (turn.Line == null) { t += 0.8f; continue; }          // a laugh, a sigh: not recorded yet
            if (turn.Cut) t = MathF.Max(0f, t - 0.3f);                  // cuts in before the other has quite finished
            Say(who, me, turn.Line, Speech.NormalDb, t, now, say);
            t += LengthOf(me.Voice, turn.Line) + TurnGapSeconds * (float)Uniform(0.6, 1.6);
        }
        px.BusyUntil = py.BusyUntil = now + t;
        _nextTalk[(mapId, pair)] = now + t + TalkAgainSeconds * (0.7 + 0.6 * _rng.NextDouble());
    }

    /// <summary>Somebody on their own says something to nobody: muttered, so only when somebody is
    /// close enough to catch it.</summary>
    private void Remark((int Id, Vector3 At, Vector3 Forward, float Speed, string Voice) p, Person me,
                        List<(int Id, Vector3 At, float Speed)> players, double now, SpeechConditions cond,
                        Action<int, string, TransientSound> say)
    {
        if (now < me.NextRemark || now < me.BusyUntil || OnPhone(me, now)) return;
        me.NextRemark = now + RemarkEverySeconds * (0.5 + _rng.NextDouble());
        if (!players.Any(q => Apart(q.At, p.At) < RemarkHeardMetres)) return;
        var cats = StreetLines.Remarks(cond).Where(c => Speech.LinesOf(me.Voice, c).Count > 0).ToList();
        if (p.Speed < 0.2f && Speech.LinesOf(me.Voice, "waiting").Count > 0) cats.Add("waiting");
        if (cats.Count == 0) return;
        string cat = cats[_rng.Next(cats.Count)];
        // The weather and hour categories are only offered when true; the rest can still name an hour.
        bool offeredWhenTrue = cat.StartsWith("weather_", StringComparison.Ordinal) || cat.StartsWith("time_", StringComparison.Ordinal);
        var lines = Speech.LinesOf(me.Voice, cat)
            .Where(l => l != me.LastLine && (offeredWhenTrue || Speech.Find(me.Voice, l) is { } t && HomelessLines.TrueNow(t.Text, cond)))
            .ToList();
        if (lines.Count == 0) return;
        float db = cat is "mutter" or "think_aloud" ? MutterDb : Speech.NormalDb;
        Say(p, me, lines[_rng.Next(lines.Count)], db, 0f, now, say);
    }

    // ── Reactions ──────────────────────────────────────────────────────────────────────────────
    //
    // A shot or a horn near people, and one or two say something. Only sounds the server makes are heard.

    private readonly List<(string Map, string Kind, Vector3 At, double When)> _happened = new();

    /// <summary>Every sound event the server sends (GameServer.EmitWorldAudio); shots and horns are kept.</summary>
    public void Heard(string mapId, string label, IReadOnlyList<TransientSound> sounds, double now)
    {
        if (sounds.Count == 0) return;
        string? kind = sounds.Any(s => s.SynthKey.StartsWith("weapon:", StringComparison.OrdinalIgnoreCase)) ? "shot"
                     : string.Equals(label, "horn", StringComparison.OrdinalIgnoreCase) ? "horn" : null;
        if (kind == null) return;
        lock (_happened) _happened.Add((mapId, kind, sounds[0].Position, now));
        if (kind == "horn") HeardHorn(mapId, sounds[0].Position, now);
    }

    /// <summary>A shot this close is "near": people duck and swear. Further, up to the far range, it is
    /// somebody asking whether that was a gunshot.</summary>
    public const float ShotNearMetres = 40f, ShotFarMetres = 400f, HornMetres = 20f;

    private void React(string mapId, List<(int Id, Vector3 At, Vector3 Forward, float Speed, string Voice)> people,
                       List<(int Id, Vector3 At, float Speed)> players, double now, Action<int, string, TransientSound> say)
    {
        List<(string Map, string Kind, Vector3 At, double When)> mine;
        lock (_happened)
        {
            mine = _happened.Where(h => h.Map == mapId).ToList();
            _happened.RemoveAll(h => h.Map == mapId || now - h.When > 5);
        }
        foreach (var h in mine)
        {
            float reach = h.Kind == "shot" ? ShotFarMetres : HornMetres;
            var near = people.Where(p => Apart(p.At, h.At) < reach
                                         && players.Any(q => Apart(q.At, p.At) < HeardMetres)
                                         && _people.TryGetValue((mapId, p.Id), out var pp) && now >= pp.BusyUntil
                                         && MayReact(mapId, p.Id, h.Kind, Apart(p.At, h.At), now))
                             .OrderBy(p => Apart(p.At, h.At)).Take(h.Kind == "shot" ? 3 : 1).ToList();
            int said = 0;
            foreach (var p in near)
            {
                var me = _people[(mapId, p.Id)];
                bool close = Apart(p.At, h.At) < ShotNearMetres;
                string cat = h.Kind == "horn" ? "react_horn" : close ? "react_gunshot_near" : "react_gunshot_far";
                double chance = h.Kind == "horn" ? 0.25 : close ? 0.8 : 0.35;
                if (_rng.NextDouble() >= chance || Speech.LinesOf(me.Voice, cat).Count == 0) continue;
                var lines = Speech.LinesOf(me.Voice, cat);
                float effort = h.Kind == "shot" && close ? Speech.RaisedDb : Speech.NormalDb;
                // Speech after a startle comes a beat later, and not all at once.
                Say(p, me, lines[_rng.Next(lines.Count)], effort, (float)Uniform(0.3, 1.2) + said * 0.8f, now, say);
                said++;
            }
        }
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
        if (OnPhone(pa, now) || OnPhone(pb, now) || _rng.NextDouble() >= StrangerGreetChance) return;

        var (first, second) = StreetLines.Exchange(cond, _rng);
        string l1 = Pick(pa, first.Lines, first.Categories);
        string l2 = Pick(pb, second.Lines, second.Categories);
        if (l1.Length == 0 || l2.Length == 0) return;
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
            // The recording named by the key is what plays; Hiss only keeps it out of the impulse paths.
            Character = SoundCharacter.Hiss,
            DelaySeconds = delay,
            Position = p.At + new Vector3(0f, Speech.MouthHeight, 0f) + p.Forward * 0.1f,
            // At their mouth as they are when heard, walking on with them (see the driver's yell).
            OnBody = true,
            BodyOffset = new Vector3(0f, Speech.MouthHeight, 0.1f),
            LevelDb = Speech.LevelDb(effortDb),
            DecaySeconds = take.Seconds,
            Noisiness = 0.5f,
            SynthKey = Speech.Key(me.Voice, line),
        });
    }

    private static bool OnPhone(Person me, double now) => me.OnCall || now < me.CallUntil;

    private static float LengthOf(string voice, string line) => Speech.Find(voice, line)?.Seconds ?? 1f;

    /// <summary>A line this voice recorded, not the one it said last if there is another. The voice's
    /// any-time lines in <paramref name="categories"/> join the list (StreetLines.AnyTime), so a voice
    /// with none of the named lines still has something to say.</summary>
    private string Pick(Person me, IReadOnlyList<string> offered, params string[] categories)
    {
        var lines = StreetLines.Candidates(me.Voice, offered, categories);
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

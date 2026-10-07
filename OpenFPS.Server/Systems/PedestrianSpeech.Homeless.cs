using System.Numerics;
using System.Text.RegularExpressions;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;

namespace OpenFPS.Server.Systems;

/// <summary>
/// A homeless man's lines (Alex, ALEX-2026-10-06.md): what he says, and what makes him say it.
///
/// He asks the people who come near him for money, a cigarette, weed, something stronger, food, or at
/// night somewhere to sleep; thanks whoever gives him something; is bitter at whoever walks past his
/// ask; flares up when he is bumped or crowded, and now and then at nothing; asks for a ride when a car
/// stops beside him or somebody gets into one; argues with the police when a police car is near; yells
/// at cars that pass close or honk; mutters to himself when nobody is talking to him; and tells one of
/// his stories to somebody who stands with him a while.
///
/// He is a presence, not a loop: never two lines at once, a few seconds' breath after every line,
/// each person asked once in a while and not every time they pass, and the muttering a minute or two
/// apart. Everything is said only where a player could hear it.
/// </summary>
public sealed partial class PedestrianSpeech
{
    /// <summary>What a character is doing (CharacterSystem.ViewOf), by map and entity. Set by the server.</summary>
    public Func<string, int, CharacterSystem.View?>? CharacterView { get; set; }
    /// <summary>Turns a character to face somebody they are talking to, for some seconds. Set by the server.</summary>
    public Action<string, int, Vector3, double>? FaceCharacter { get; set; }

    // ── How near, how often ────────────────────────────────────────────────────────────────────

    /// <summary>Somebody this near gets asked: a pavement's width and a step.</summary>
    public const float AskMetres = 4.5f;
    /// <summary>A player is asked again after this long, a passer-by after <see cref="WalkerAskAgainSeconds"/>.</summary>
    public const double PlayerAskAgainSeconds = 120, WalkerAskAgainSeconds = 300;
    /// <summary>Between asking one passer-by and the next, seconds, at least: a street of people walking
    /// past is not a queue to be worked through.</summary>
    public const double WalkerAskGapSeconds = 30;
    /// <summary>A breath after every line of his before he starts another of his own accord, seconds.</summary>
    public const double AfterLineSeconds = 3;
    /// <summary>Somebody asked who goes further than this without giving has walked past the ask.</summary>
    public const float WalkedPastMetres = 7f;
    /// <summary>An ask nobody answered is forgotten after this long, seconds.</summary>
    public const double AskForgottenSeconds = 40;
    /// <summary>Of those who walk past his ask, how many he says something bitter after: a player, a passer-by.</summary>
    public const double PlayerRefusedChance = 0.6, WalkerRefusedChance = 0.35;
    /// <summary>Of passers-by he asks, how many answer no out loud.</summary>
    public const double WalkerAnswersChance = 0.5;
    /// <summary>Bumped: a player inside this, a passer-by inside the smaller one.</summary>
    public const float PlayerBumpMetres = 0.75f, WalkerBumpMetres = 0.5f;
    public const double AngryBumpAgainSeconds = 45;
    /// <summary>This many people within <see cref="CrowdMetres"/> is crowding him.</summary>
    public const int CrowdCount = 3;
    public const float CrowdMetres = 2.2f;
    public const double CrowdAgainSeconds = 90;
    /// <summary>A police car this near and he argues with it.</summary>
    public const float CopsMetres = 30f;
    public const double CopsAgainSeconds = 45;
    /// <summary>A car passing this near and this fast, or a horn this near, and he yells at it.</summary>
    public const float CarsMetres = 5f, CarsSpeed = 6f, CarHornMetres = 25f;
    public const double CarsAgainSeconds = 30, CarsChance = 0.35;
    /// <summary>A car standing this near, with somebody at it or in it, and he asks for a ride.</summary>
    public const float RideMetres = 8f;
    public const double RideAgainSeconds = 120;
    /// <summary>A player who stands this near for this long gets a story, once in a while.</summary>
    public const float StoryMetres = 3f;
    public const double StoryAfterSeconds = 10, StoryAgainSeconds = 600;
    /// <summary>Talking to himself: about this often while somebody is near enough to hear it.</summary>
    public const double IdleMinSeconds = 50, IdleMaxSeconds = 120;
    /// <summary>Walking, a longer breath after each line, seconds.</summary>
    public const double WalkingQuietSeconds = 12;
    /// <summary>Of his idle lines, how many are an outburst at nobody.</summary>
    public const double OutburstChance = 0.1;

    private sealed class Panhandler
    {
        public double NextLine;
        public double NextIdle = double.NaN;
        public double NextWalkerAsk;
        public readonly Dictionary<int, double> AskedAt = new();
        public readonly Dictionary<int, double> RideAskedAt = new();
        public readonly Dictionary<int, double> StoryAt = new();
        public readonly Dictionary<int, double> StandingSince = new();
        public readonly HashSet<(int, string)> StoriesTold = new();
        public int AskTarget = -1;
        public bool AskTargetIsPlayer;
        public double AskedTime;
        public double AskEnds;
        public double LastBump = double.NegativeInfinity, LastCrowd = double.NegativeInfinity;
        public double LastCops = double.NegativeInfinity, LastCars = double.NegativeInfinity;
        public readonly Queue<int> Gifts = new();
        public readonly List<string> Recent = new();
        public bool Walking;
    }

    private readonly Dictionary<(string Map, int Id), Panhandler> _panhandlers = new();
    private readonly List<(string Map, Vector3 At, double When)> _horns = new();

    /// <summary>Somebody handed a character something (/hand). They thank them when they can.</summary>
    public void Given(string mapId, int characterId, int giverId)
    {
        lock (_panhandlers)
        {
            if (!_panhandlers.TryGetValue((mapId, characterId), out var ph))
                _panhandlers[(mapId, characterId)] = ph = new Panhandler();
            ph.Gifts.Enqueue(giverId);
        }
    }

    /// <summary>
    /// Whether somebody may say something about a shot or a horn (React). Anybody may; a character only
    /// about a shot close by, and only when they are not still getting their breath back from their last
    /// line. A horn is his to yell at in his own words (homeless_cars), and a far shot every half minute
    /// was half of everything he said.
    /// </summary>
    private bool MayReact(string mapId, int id, string kind, float metres, double now)
    {
        Panhandler? ph;
        lock (_panhandlers) _panhandlers.TryGetValue((mapId, id), out ph);
        if (ph == null) return true;
        if (kind != "shot" || metres >= ShotNearMetres || now < ph.NextLine) return false;
        ph.NextLine = now + 8;
        return true;
    }

    /// <summary>A horn sounded: a character near it may yell at it.</summary>
    private void HeardHorn(string mapId, Vector3 at, double now)
    {
        lock (_horns)
        {
            _horns.RemoveAll(h => now - h.When > 5);
            _horns.Add((mapId, at, now));
        }
    }

    private void Panhandle(string mapId, World world, (int Id, Vector3 At, Vector3 Forward, float Speed, string Voice) p,
                           List<(int Id, Vector3 At, Vector3 Forward, float Speed, string Voice)> people,
                           List<(int Id, Vector3 At, float Speed)> players, double now, SpeechConditions cond,
                           Action<int, string, TransientSound> say)
    {
        var me = Get(mapId, p.Id, p.Voice, now);
        me.LastPosition = p.At;
        Panhandler ph;
        lock (_panhandlers)
            if (!_panhandlers.TryGetValue((mapId, p.Id), out ph!))
                _panhandlers[(mapId, p.Id)] = ph = new Panhandler();
        if (double.IsNaN(ph.NextIdle)) ph.NextIdle = now + Uniform(10, IdleMaxSeconds);
        var view = CharacterView?.Invoke(mapId, p.Id);
        bool indoors = view?.Indoors ?? false;
        bool riding = view?.Doing == CharacterSystem.Doing.Riding;
        bool walking = view?.Doing is CharacterSystem.Doing.Walking or CharacterSystem.Doing.Boarding;
        ph.Walking = walking;

        // Everybody who could hear him, in a vehicle or not: a player on the bus with him too.
        var hearers = new List<Vector3>();
        world.Query(new QueryDescription().WithAll<Transform, PlayerComponent>().WithNone<DeadComponent>(),
            (ref Transform t) => hearers.Add(t.Position));
        if (!hearers.Any(q => Vector3.Distance(q, p.At) < HeardMetres))
        {
            ph.AskTarget = -1;
            ph.Gifts.Clear();
            return;
        }
        bool free = now >= me.BusyUntil;

        // ── Somebody gave him something ─────────────────────────────────────────────────────────
        if (ph.Gifts.Count > 0)
        {
            if (!free) return;
            int giver = ph.Gifts.Dequeue();
            ph.Gifts.Clear();
            ph.AskTarget = -1;
            if (Line(ph, me, "homeless_thanks", cond) is { Length: > 0 } thanks)
            {
                Face(mapId, p, Where(giver, people, players), thanks);
                SayAs(p, me, ph, thanks, Speech.NormalDb, 0.4f, now, say);
            }
            return;
        }

        // ── An ask, and whoever it was put to ───────────────────────────────────────────────────
        if (ph.AskTarget >= 0)
        {
            var at = Where(ph.AskTarget, people, players);
            if (now - ph.AskedTime > AskForgottenSeconds) ph.AskTarget = -1;
            else if (now >= ph.AskEnds && (at == null || Apart(at.Value, p.At) > WalkedPastMetres))
            {
                bool player = ph.AskTargetIsPlayer;
                ph.AskTarget = -1;
                if (free && _rng.NextDouble() < (player ? PlayerRefusedChance : WalkerRefusedChance)
                    && Line(ph, me, "homeless_refused", cond) is { Length: > 0 } bitter)
                {
                    SayAs(p, me, ph, bitter, Speech.RaisedDb, (float)Uniform(0.2, 0.9), now, say);
                    return;
                }
            }
        }
        if (!free) return;

        // ── Bumped, or crowded ──────────────────────────────────────────────────────────────────
        if (!riding && now - ph.LastBump > AngryBumpAgainSeconds)
        {
            // A passer-by brushing past him while he walks is a pavement, not a bump.
            var bumper = players.Where(q => Apart(q.At, p.At) < PlayerBumpMetres).Select(q => (Vector3?)q.At).FirstOrDefault()
                      ?? (walking ? null : people.Where(o => o.Id != p.Id && Apart(o.At, p.At) < WalkerBumpMetres).Select(o => (Vector3?)o.At).FirstOrDefault());
            if (bumper is { } b)
            {
                ph.LastBump = now;
                string cat = _rng.NextDouble() < 0.65 ? "homeless_angry" : "annoyed";
                if (Line(ph, me, cat, cond) is { Length: > 0 } line)
                {
                    Face(mapId, p, b, line);
                    SayAs(p, me, ph, line, cat == "homeless_angry" ? Speech.LoudDb : Speech.RaisedDb, 0.2f, now, say);
                    return;
                }
            }
        }
        if (now < ph.NextLine) return;
        int near = players.Count(q => Apart(q.At, p.At) < CrowdMetres) + people.Count(o => o.Id != p.Id && Apart(o.At, p.At) < CrowdMetres);
        if (!riding && near >= CrowdCount && now - ph.LastCrowd > CrowdAgainSeconds)
        {
            ph.LastCrowd = now;
            if (Line(ph, me, "homeless_angry", cond) is { Length: > 0 } line)
            { SayAs(p, me, ph, line, Speech.LoudDb, 0f, now, say); return; }
        }

        // ── The street: police, cars, a ride ────────────────────────────────────────────────────
        if (!indoors && !riding)
        {
            var (police, passing, standing) = Traffic(world, p.At);
            if (police is { } cop && now - ph.LastCops > CopsAgainSeconds)
            {
                ph.LastCops = now;
                if (Line(ph, me, "homeless_cops", cond) is { Length: > 0 } line)
                { Face(mapId, p, cop, line); SayAs(p, me, ph, line, Speech.RaisedDb, 0f, now, say); return; }
            }
            Vector3? horn = null;
            lock (_horns)
                foreach (var h in _horns)
                    if (h.Map == mapId && now - h.When < 3 && Apart(h.At, p.At) < CarHornMetres) { horn = h.At; break; }
            // One roll per car going by, not one a tick: a miss waits for the next car.
            bool yell = horn != null || passing != null && _rng.NextDouble() < CarsChance;
            if (passing != null && !yell && now - ph.LastCars > CarsAgainSeconds) ph.LastCars = now - CarsAgainSeconds + 6;
            if (now - ph.LastCars > CarsAgainSeconds && yell)
            {
                ph.LastCars = now;
                lock (_horns) _horns.RemoveAll(h => h.Map == mapId && Apart(h.At, p.At) < CarHornMetres);
                if (Line(ph, me, "homeless_cars", cond) is { Length: > 0 } line)
                { Face(mapId, p, (horn ?? passing)!.Value, line); SayAs(p, me, ph, line, Speech.LoudDb, 0.3f, now, say); return; }
            }
            // A car standing beside him with somebody getting in or out of it, or a player in it.
            foreach (var (car, carId) in standing)
            {
                int who = players.Where(q => Apart(q.At, car) < 3f).Select(q => q.Id).FirstOrDefault(-1);
                if (who < 0) who = PlayerIn(world, carId);
                if (who < 0) who = people.Where(o => o.Id != p.Id && Apart(o.At, car) < 3f).Select(o => o.Id).FirstOrDefault(-1);
                if (who < 0 || now - ph.RideAskedAt.GetValueOrDefault(who, double.NegativeInfinity) < RideAgainSeconds) continue;
                ph.RideAskedAt[who] = now;
                if (Line(ph, me, "homeless_ride", cond) is { Length: > 0 } line)
                { Face(mapId, p, car, line); SayAs(p, me, ph, line, Speech.RaisedDb, 0f, now, say); return; }
            }
        }

        // ── Somebody near: ask them ─────────────────────────────────────────────────────────────
        if (ph.AskTarget < 0)
        {
            foreach (var q in players.OrderBy(q => Apart(q.At, p.At)))
            {
                if (Apart(q.At, p.At) > AskMetres) break;
                if (now - ph.AskedAt.GetValueOrDefault(q.Id, double.NegativeInfinity) < PlayerAskAgainSeconds) continue;
                if (walking && _rng.NextDouble() > 0.5) { ph.AskedAt[q.Id] = now; continue; }
                if (Ask(mapId, p, me, ph, q.Id, q.At, true, now, cond, say)) return;
            }
            if (!riding && now >= ph.NextWalkerAsk)
                foreach (var o in people.Where(o => o.Id != p.Id).OrderBy(o => Apart(o.At, p.At)))
                {
                    if (Apart(o.At, p.At) > AskMetres) break;
                    if (now - ph.AskedAt.GetValueOrDefault(o.Id, double.NegativeInfinity) < WalkerAskAgainSeconds) continue;
                    if (walking && _rng.NextDouble() > 0.35) { ph.AskedAt[o.Id] = now; continue; }
                    if (Ask(mapId, p, me, ph, o.Id, o.At, false, now, cond, say)) return;
                }
        }

        // ── A player who stays: a story ─────────────────────────────────────────────────────────
        foreach (var q in players)
        {
            bool staying = Apart(q.At, p.At) < StoryMetres && q.Speed < 0.3f && !walking;
            if (!staying) { ph.StandingSince.Remove(q.Id); continue; }
            if (!ph.StandingSince.TryGetValue(q.Id, out double since)) { ph.StandingSince[q.Id] = now; continue; }
            if (now - since < StoryAfterSeconds || ph.AskTarget == q.Id) continue;
            if (now - ph.StoryAt.GetValueOrDefault(q.Id, double.NegativeInfinity) < StoryAgainSeconds) continue;
            var untold = Speech.LinesOf(me.Voice, "story")
                .Where(l => !ph.StoriesTold.Contains((q.Id, l)) && Speech.Find(me.Voice, l) is { } t && HomelessLines.TrueNow(t.Text, cond))
                .ToList();
            ph.StoryAt[q.Id] = now;
            if (untold.Count == 0) continue;
            string story = untold[_rng.Next(untold.Count)];
            ph.StoriesTold.Add((q.Id, story));
            Face(mapId, p, q.At, story);
            SayAs(p, me, ph, story, Speech.NormalDb, 0.5f, now, say);
            return;
        }

        // ── Nobody talking to him: to himself ───────────────────────────────────────────────────
        if (now < ph.NextIdle) return;
        ph.NextIdle = now + Uniform(IdleMinSeconds, IdleMaxSeconds);
        if (!hearers.Any(q => Apart(q, p.At) < RemarkHeardMetres)) return;
        var (category, db) = HomelessLines.Idle(cond, view?.Kind == HauntKind.BusStop, _rng);
        string said = Line(ph, me, category, cond);
        if (said.Length == 0) said = Line(ph, me, "homeless_mutter", cond);
        if (said.Length > 0) SayAs(p, me, ph, said, db, 0f, now, say);
    }

    /// <summary>Asks somebody for something. True if anything was said.</summary>
    private bool Ask(string mapId, (int Id, Vector3 At, Vector3 Forward, float Speed, string Voice) p, Person me, Panhandler ph,
                     int target, Vector3 at, bool player, double now, SpeechConditions cond, Action<int, string, TransientSound> say)
    {
        ph.AskedAt[target] = now;
        string cat = HomelessLines.AskCategory(cond, _rng);
        string line = Line(ph, me, cat, cond);
        if (line.Length == 0) line = Line(ph, me, "homeless_money", cond);
        if (line.Length == 0) return false;
        Face(mapId, p, at, line);
        float db = Apart(at, p.At) > 2.5f ? Speech.RaisedDb : Speech.NormalDb;
        SayAs(p, me, ph, line, db, 0f, now, say);
        ph.AskTarget = target;
        ph.AskTargetIsPlayer = player;
        ph.AskedTime = now;
        ph.AskEnds = now + LengthOf(me.Voice, line);
        if (!player)
        {
            ph.NextWalkerAsk = now + WalkerAskGapSeconds * (1 + _rng.NextDouble());
            // The passer-by may say no, out loud, as they go by.
            if (_people.TryGetValue((mapId, target), out var them) && now >= them.BusyUntil && !OnPhone(them, now)
                && _rng.NextDouble() < WalkerAnswersChance)
            {
                var no = StreetLines.Candidates(them.Voice, Speech.LinesOf(them.Voice, "refuse"), Array.Empty<string>());
                if (no.Count > 0)
                {
                    var toHim = new Vector3(p.At.X - at.X, 0f, p.At.Z - at.Z);
                    var facing = toHim.LengthSquared() > 1e-6f ? Vector3.Normalize(toHim) : Vector3.UnitZ;
                    var person = (target, at, facing, 1f, them.Voice);
                    Say(person, them, no[_rng.Next(no.Count)], Speech.NormalDb,
                        LengthOf(me.Voice, line) + ReplyGapSeconds + (float)Uniform(0f, 0.4f), now, say);
                }
            }
        }
        return true;
    }

    /// <summary>A line of his in a category that is true now, not one he said lately.</summary>
    private string Line(Panhandler ph, Person me, string category, SpeechConditions cond)
    {
        var lines = Speech.LinesOf(me.Voice, category)
            .Where(l => Speech.Find(me.Voice, l) is { } t && HomelessLines.TrueNow(t.Text, cond)).ToList();
        if (lines.Count == 0) return "";
        var fresh = lines.Where(l => !ph.Recent.Contains(l)).ToList();
        var from = fresh.Count > 0 ? fresh : lines;
        return from[_rng.Next(from.Count)];
    }

    private void SayAs((int Id, Vector3 At, Vector3 Forward, float Speed, string Voice) p, Person me, Panhandler ph,
                       string line, float db, float delay, double now, Action<int, string, TransientSound> say)
    {
        Say(p, me, line, db, delay, now, say);
        ph.Recent.Add(line);
        if (ph.Recent.Count > 16) ph.Recent.RemoveAt(0);
        // On the move he has less breath for it, and is past most people before he could say much.
        ph.NextLine = now + delay + LengthOf(me.Voice, line) + AfterLineSeconds + (ph.Walking ? WalkingQuietSeconds : 0);
    }

    private void Face(string mapId, (int Id, Vector3 At, Vector3 Forward, float Speed, string Voice) p, Vector3? at, string line)
    {
        if (at is { } a) FaceCharacter?.Invoke(mapId, p.Id, a, LengthOf(p.Voice, line) + 2.0);
    }

    private static Vector3? Where(int id, List<(int Id, Vector3 At, Vector3 Forward, float Speed, string Voice)> people,
                                  List<(int Id, Vector3 At, float Speed)> players)
    {
        foreach (var q in players) if (q.Id == id) return q.At;
        foreach (var o in people) if (o.Id == id) return o.At;
        return null;
    }

    /// <summary>The traffic round a point: a police car near, a car passing close, and cars standing near.</summary>
    private static (Vector3? Police, Vector3? Passing, List<(Vector3 At, int Id)> Standing) Traffic(World world, Vector3 at)
    {
        Vector3? police = null, passing = null;
        var standing = new List<(Vector3, int)>();
        world.Query(new QueryDescription().WithAll<Transform, Velocity>().WithAny<VehicleComponent, DriveComponent>(),
            (Entity e, ref Transform t, ref Velocity v) =>
            {
                float d = Apart(t.Position, at);
                if (d > CopsMetres) return;
                float speed = new Vector2(v.Linear.X, v.Linear.Z).Length();
                string type = world.Has<VehicleComponent>(e) ? world.Get<VehicleComponent>(e).VehicleType ?? "" : "";
                if (type.Contains("police", StringComparison.OrdinalIgnoreCase)) police ??= t.Position;
                if (d < CarsMetres && speed > CarsSpeed) passing ??= t.Position;
                // A bus at its stop is not a car somebody might give him a lift in.
                if (d < RideMetres && speed < 0.5f && !type.Contains("bus", StringComparison.OrdinalIgnoreCase))
                    standing.Add((t.Position, e.Id));
            });
        return (police, passing, standing);
    }

    /// <summary>A player sitting in a vehicle, or -1.</summary>
    private static int PlayerIn(World world, int vehicleId)
    {
        int found = -1;
        world.Query(new QueryDescription().WithAll<OccupantComponent, PlayerComponent>(), (Entity e, ref OccupantComponent o) =>
        {
            if (found < 0 && o.RootEntityId == vehicleId) found = e.Id;
        });
        return found;
    }
}

/// <summary>Which of a homeless man's lines fit when.</summary>
public static class HomelessLines
{
    /// <summary>
    /// What he asks for, by the draw at this hour and weather: mostly money, then a cigarette, food,
    /// weed, and rarely something stronger; at night or in the cold and wet, often somewhere to sleep.
    /// </summary>
    public static string AskCategory(SpeechConditions c, Random rng)
    {
        bool shelter = CharacterSystem.Sheltering(c);
        var weights = new (string Cat, double W)[]
        {
            ("homeless_money", 45), ("homeless_smokes", 20), ("homeless_food", 15),
            ("homeless_weed", 10), ("homeless_drugs", 4), ("homeless_shelter", shelter ? 20 : 0),
        };
        double pick = rng.NextDouble() * weights.Sum(w => w.W);
        foreach (var (cat, w) in weights)
        {
            pick -= w;
            if (w > 0 && pick <= 0) return cat;
        }
        return "homeless_money";
    }

    /// <summary>What he says to nobody: muttering mostly, a word about where he needs to sleep when it is
    /// night or cold, the bus when he is waiting for one, and now and then an outburst.</summary>
    public static (string Category, float Db) Idle(SpeechConditions c, bool atBusStop, Random rng)
    {
        double r = rng.NextDouble();
        if (r < PedestrianSpeech.OutburstChance) return ("homeless_angry", Speech.RaisedDb);
        if (CharacterSystem.Sheltering(c) && r < 0.4) return ("homeless_shelter", Speech.NormalDb - 3f);
        if (atBusStop && r < 0.5) return ("waiting", PedestrianSpeech.MutterDb);
        if (r < 0.8) return ("homeless_mutter", PedestrianSpeech.MutterDb);
        return (r < 0.9 ? "mutter" : "think_aloud", PedestrianSpeech.MutterDb);
    }

    private static readonly Regex Always = new(
        @"\b(one|every|every single|last|worst|other) (day|days|night|morning|week)\b|\btwo days\b|\bhot dog\b|\byou're cold\b|\bnot today\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Words = new(
        @"\b(this morning|cold tonight|morning|afternoon|evening|tonight|night|noon|lunch|breakfast|dinner|day|today|days|tomorrow|yesterday|weekend|monday|tuesday|wednesday|thursday|friday|saturday|sunday|spring|summer|autumn|winter|weather|rain|raining|snow|snowing|cold|freezing|hot|heat|sun|sunny|storm|wind|windy|fog|foggy)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Today = new(@"\b(this morning|cold tonight|tonight|today)\b",
                                              RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Whether a story is true now. Only what it says about today is checked ("this morning",
    /// "tonight"); the rest of a story is in the past, so "two in the morning" or "Sunday dinner" is
    /// told at any hour.</summary>
    public static bool StoryTrueNow(string text, SpeechConditions c) => TrueNow(text, c, Today);

    /// <summary>
    /// Whether a line is true now: every word in it about the hour or the weather is. "Somewhere to sleep
    /// tonight" from the afternoon on; "It's so fucking cold" when it is; "Not today", "one morning",
    /// "every night" and "a hot dog" at any time, because they are not about now.
    /// </summary>
    public static bool TrueNow(string text, SpeechConditions c) => TrueNow(text, c, Words);

    private static bool TrueNow(string text, SpeechConditions c, Regex words)
    {
        string rest = Always.Replace(text, " ");
        foreach (Match m in words.Matches(rest))
        {
            float h = c.Hour;
            bool ok = m.Value.ToLowerInvariant() switch
            {
                "this morning" => h >= 7f && h < 20f,
                "cold tonight" => h >= 12f && c.TemperatureC <= 15f,
                "morning" => StreetLines.IsMorning(h),
                "afternoon" => h >= 12f && h < 18f,
                "evening" => h >= 16f && h < 23f,
                "tonight" => h >= 14f || h < 3f,
                "night" => h >= 18f || h < 5f,
                "noon" or "lunch" => h >= 11f && h < 15f,
                "breakfast" => h >= 5f && h < 11f,
                "dinner" => h >= 16f && h < 23f,
                "day" or "today" => h >= 6f && h < 22f,
                "days" or "tomorrow" or "yesterday" or "weekend" or "weather" => true,
                "rain" or "raining" => c.Weather is WeatherType.Rain or WeatherType.Storm,
                "snow" or "snowing" => c.Weather == WeatherType.Snow,
                "storm" => c.Weather == WeatherType.Storm,
                "cold" or "freezing" => c.TemperatureC <= 12f,
                "hot" or "heat" => c.TemperatureC >= 26f,
                "sun" or "sunny" => StreetLines.IsFine(c),
                _ => false,
            };
            if (!ok) return false;
        }
        return true;
    }
}
